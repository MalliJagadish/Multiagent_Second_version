using Microsoft.AspNetCore.Mvc;
using MultiAgent.Models;
using MultiAgent.Services;
using MultiAgent.Workflows;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class WebhookController : ControllerBase
{
    private readonly MultiAgentWorkflow _workflow;
    private readonly GitHubService _github;
    private readonly IConfiguration _config;
    private readonly ILogger<WebhookController> _logger;

    public WebhookController(
        MultiAgentWorkflow workflow,
        GitHubService github,
        IConfiguration config,
        ILogger<WebhookController> logger)
    {
        _workflow = workflow;
        _github = github;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// GitHub webhook endpoint.
    /// Configure in repo Settings -> Webhooks:
    ///   Payload URL : https://your-host/api/webhook/github
    ///   Content type: application/json
    ///   Secret      : value of GitHub:WebhookSecret in appsettings.json
    ///   Events      : Issues
    /// </summary>
    [HttpPost("github")]
    public async Task<IActionResult> GitHub()
    {
        // ── Read raw body (signature validation needs the raw bytes) ──
        using var reader = new StreamReader(Request.Body, Encoding.UTF8);
        var payload = await reader.ReadToEndAsync();

        // ── Validate HMAC-SHA256 signature ────────────────────────────
        var signature = Request.Headers["X-Hub-Signature-256"].FirstOrDefault() ?? "";
        var secret = _config["GitHub:WebhookSecret"] ?? "";

        if (!string.IsNullOrEmpty(secret) && !_github.ValidateWebhookSignature(payload, signature, secret))
        {
            _logger.LogWarning("Webhook signature validation failed.");
            return Unauthorized(new { error = "Invalid webhook signature." });
        }

        // ── Parse event type ──────────────────────────────────────────
        var eventType = Request.Headers["X-GitHub-Event"].FirstOrDefault() ?? "";
        if (eventType != "issues")
            return Ok(new { skipped = true, reason = $"Event '{eventType}' ignored — only 'issues' events are handled." });

        // ── Parse payload ─────────────────────────────────────────────
        JsonElement root;
        try
        {
            root = JsonDocument.Parse(payload).RootElement;
        }
        catch (JsonException ex)
        {
            _logger.LogError(ex, "Failed to parse webhook payload.");
            return BadRequest(new { error = "Invalid JSON payload." });
        }

        var action = root.TryGetProperty("action", out var a) ? a.GetString() : "";

        // ── Approach 1: issue opened with [Agent] or [Pipeline] title ─
        // ── Approach 2: ai-pipeline label added ───────────────────────
        bool shouldTrigger = action switch
        {
            "opened" => IsAgentIssue(root),
            "labeled" => IsAiPipelineLabel(root),
            _ => false
        };

        if (!shouldTrigger)
            return Ok(new { skipped = true, reason = $"Action '{action}' did not match trigger conditions." });

        // ── Extract issue details ─────────────────────────────────────
        var issue = root.GetProperty("issue");
        var issueNumber = issue.GetProperty("number").GetInt32().ToString();
        var issueTitle = issue.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
        var issueBody = issue.TryGetProperty("body", out var b) && b.ValueKind != JsonValueKind.Null
            ? b.GetString() ?? ""
            : "";

        var repoFullName = root.TryGetProperty("repository", out var repo)
            ? repo.TryGetProperty("full_name", out var fn) ? fn.GetString() ?? "" : ""
            : "";

        var featureDescription = string.IsNullOrWhiteSpace(issueBody)
            ? issueTitle
            : $"{issueTitle}\n\n{issueBody}";

        var request = new PipelineRequest
        {
            FeatureDescription = featureDescription,
            GitHubIssueNumber = issueNumber,
            GitHubRepoFullName = repoFullName
        };

        _logger.LogInformation(
            "Webhook triggered pipeline for issue #{Issue}: {Title} (action: {Action})",
            issueNumber, issueTitle, action);

        // ── Fire pipeline asynchronously (respond immediately) ────────
        _ = Task.Run(() => _workflow.RunAsync(request));

        return Accepted(new
        {
            message = "Pipeline started.",
            trigger = action == "opened" ? "Approach 1 — issue opened" : "Approach 2 — ai-pipeline label",
            issue = issueNumber,
            feature = featureDescription[..Math.Min(100, featureDescription.Length)]
        });
    }

    // Approach 1: title starts with [Agent] or [Pipeline]
    private static bool IsAgentIssue(JsonElement root)
    {
        var title = root.TryGetProperty("issue", out var issue)
            && issue.TryGetProperty("title", out var t)
            ? t.GetString() ?? ""
            : "";

        return title.StartsWith("[Agent]", StringComparison.OrdinalIgnoreCase)
            || title.StartsWith("[Pipeline]", StringComparison.OrdinalIgnoreCase);
    }

    // Approach 2: the label that was just added is "ai-pipeline"
    private static bool IsAiPipelineLabel(JsonElement root)
    {
        if (!root.TryGetProperty("label", out var label))
            return false;

        var name = label.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
        return name.Equals("ai-pipeline", StringComparison.OrdinalIgnoreCase);
    }
}
