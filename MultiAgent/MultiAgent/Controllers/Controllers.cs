using DevPipeline.Models;
using DevPipeline.Services;
using DevPipeline.Workflows;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace DevPipeline.Controllers;

// ── Manual trigger from Vue dashboard ────────────────────────────
[ApiController]
[Route("api/[controller]")]
public class PipelineController : ControllerBase
{
    private readonly DevPipelineWorkflow _workflow;
    private readonly PipelineHistoryService _history;

    public PipelineController(DevPipelineWorkflow workflow, PipelineHistoryService history)
    {
        _workflow = workflow;
        _history = history;
    }

    /// <summary>
    /// Starts a pipeline run. Returns immediately — work happens in background.
    /// Client watches progress via SignalR hub at /pipelinehub.
    /// </summary>
    [HttpPost("run")]
    public IActionResult Run([FromBody] PipelineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureDescription))
            return BadRequest(new { error = "Feature description is required." });

        if (string.IsNullOrWhiteSpace(request.RepoPath))
            return BadRequest(new { error = "Repo path is required." });

        if (!Directory.Exists(request.RepoPath))
            return BadRequest(new { error = $"Repo path not found: {request.RepoPath}" });

        // Fire and forget — client watches via SignalR
        _ = Task.Run(async () => await _workflow.RunAsync(request));

        return Ok(new { message = "Pipeline started! Watch live via SignalR at /pipelinehub." });
    }

    [HttpGet("history")]
    public IActionResult History() => Ok(_history.GetAll());

    [HttpGet("health")]
    public IActionResult Health() => Ok(new
    {
        status = "ok",
        framework = "Microsoft Agent Framework rc4",
        agents = new[] { "CoderAgent (Claude)", "UnitTestAgent (GPT-4o)",
                            "PlaywrightAgent (Gemini)", "ReviewAgent (GPT-4o)",
                            "SecurityAgent (Gemini)" },
        time = DateTime.UtcNow
    });
}

// ── GitHub webhook — auto-trigger from issue labels ───────────────
[ApiController]
[Route("api/webhook")]
public class GitHubWebhookController : ControllerBase
{
    private readonly DevPipelineWorkflow _workflow;
    private readonly GitHubService _github;
    private readonly IConfiguration _config;
    private readonly ILogger<GitHubWebhookController> _logger;
    private const string TriggerLabel = "ai-pipeline";

    public GitHubWebhookController(
        DevPipelineWorkflow workflow,
        GitHubService github,
        IConfiguration config,
        ILogger<GitHubWebhookController> logger)
    {
        _workflow = workflow;
        _github = github;
        _config = config;
        _logger = logger;
    }

    /// <summary>
    /// Receives GitHub webhook events.
    /// Triggers pipeline when label "ai-pipeline" is added to any issue.
    /// Validates HMAC-SHA256 signature from GitHub.
    /// </summary>
    [HttpPost("github")]
    public async Task<IActionResult> Handle()
    {
        using var reader = new StreamReader(Request.Body);
        var rawBody = await reader.ReadToEndAsync();

        // Validate HMAC-SHA256 signature — prevents random triggering
        var secret = _config["GitHub:WebhookSecret"];
        if (!string.IsNullOrEmpty(secret))
        {
            var sig = Request.Headers["X-Hub-Signature-256"].ToString();
            if (!_github.ValidateWebhookSignature(rawBody, sig, secret))
                return Unauthorized(new { error = "Invalid webhook signature." });
        }

        var eventType = Request.Headers["X-GitHub-Event"].ToString();
        if (eventType != "issues")
            return Ok(new { message = $"Event '{eventType}' ignored." });

        using var doc = JsonDocument.Parse(rawBody);
        var action = doc.RootElement.GetProperty("action").GetString();
        if (action != "labeled")
            return Ok(new { message = $"Action '{action}' ignored." });

        var label = doc.RootElement.GetProperty("label").GetProperty("name").GetString();
        if (label != TriggerLabel)
            return Ok(new { message = $"Label '{label}' is not the trigger label." });

        var issue = doc.RootElement.GetProperty("issue");
        var issueNumber = issue.GetProperty("number").GetInt32().ToString();
        var title = issue.GetProperty("title").GetString() ?? "";
        var body = issue.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
        var repoPath = _config["Pipeline:LocalRepoPath"]
                          ?? throw new Exception("Missing Pipeline:LocalRepoPath in appsettings.json");

        _logger.LogInformation("Pipeline triggered by issue #{N}: {T}", issueNumber, title);

        _ = Task.Run(async () => await _workflow.RunAsync(new PipelineRequest
        {
            FeatureDescription = string.IsNullOrWhiteSpace(body)
                ? title
                : $"{title}\n\n{body}",
            RepoPath = repoPath,
            GitHubIssueNumber = issueNumber
        }));

        return Ok(new { message = $"Pipeline triggered for issue #{issueNumber}." });
    }

    [HttpGet("ping")]
    public IActionResult Ping() => Ok(new
    {
        status = "DevPipeline webhook is alive! 🚀",
        trigger = $"Add label '{TriggerLabel}' to any GitHub issue to start the pipeline.",
        time = DateTime.UtcNow
    });
}