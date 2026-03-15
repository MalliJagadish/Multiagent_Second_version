using Microsoft.Extensions.AI;
using MultiAgent.Agents;
using MultiAgent.Models;
using MultiAgent.Services;
using MultiAgent.Tools;
using MultiAgent.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel;

namespace MultiAgent.Workflows;

/// <summary>
/// MultiAgent Workflow — fully in-memory, no local disk.
///
/// Files are stored in PipelineState.Files (Dictionary&lt;string, string&gt;)
/// and committed directly to GitHub via the Git Data API.
/// Works like GitHub Copilot's coding agent — no local folder needed.
///
/// PIPELINE ORDER:
///   Phase 1 (Sequential): CoderAgent → UnitTestAgent → PlaywrightAgent
///   Phase 2 (Parallel):   ReviewAgent + SecurityAgent (both post to PR)
/// </summary>
public class MultiAgentWorkflow
{
    private readonly IConfiguration _config;
    private readonly GitHubService _github;
    private readonly PipelineHistoryService _history;
    private readonly IHubContext<PipelineHub> _hub;
    private readonly ILogger<MultiAgentWorkflow> _logger;

    public MultiAgentWorkflow(
        IConfiguration config,
        GitHubService github,
        PipelineHistoryService history,
        IHubContext<PipelineHub> hub,
        ILogger<MultiAgentWorkflow> logger)
    {
        _config = config;
        _github = github;
        _history = history;
        _hub = hub;
        _logger = logger;
    }

    // ═══════════════════════════════════════════════════════════════
    // MAIN ENTRY POINT
    // ═══════════════════════════════════════════════════════════════
    public async Task<PipelineState> RunAsync(PipelineRequest request)
    {
        var state = new PipelineState
        {
            FeatureDescription = request.FeatureDescription,
            BranchName = BuildBranchName(request.FeatureDescription),
            GitHubIssueNumber = request.GitHubIssueNumber,
            GitHubRepoFullName = request.GitHubRepoFullName
        };

        _history.Add(new PipelineRun
        {
            PipelineId = state.PipelineId,
            FeatureDescription = state.FeatureDescription,
            Status = "running",
            GitHubIssueNumber = request.GitHubIssueNumber
        });

        await Log(state, "Orchestrator", $"🚀 Pipeline started: {request.FeatureDescription}");
        await Log(state, "Orchestrator", "📦 Mode: In-memory (no local disk)");

        try
        {
            await CreateBranch(state);

            if (state.GitHubIssueNumber != null)
                await PostIssueComment(state.GitHubIssueNumber,
                    $"🤖 **DevPipeline started!**\nBranch: `{state.BranchName}`\nMode: In-memory (no local disk)");

            // ── PHASE 1: Sequential ───────────────────────────────
            await Log(state, "Orchestrator", "▶️ Phase 1: Coder → UnitTest → Playwright");

            await RunCoderAgent(state);
            VerifyFilesCreated(state);
            await Task.Delay(20000); // Small delay to ensure all tool results are logged before next agent starts
            await RunUnitTestAgent(state);
            await RunPlaywrightAgent(state);

            // ── COMMIT DIRECTLY TO GITHUB (no local disk) ─────────
            await CommitAndPr(state);

            // ── PHASE 2: Parallel ─────────────────────────────────
            await Log(state, "Orchestrator", "▶️ Phase 2: Review + Security (parallel)");

            await Task.WhenAll(
                RunReviewAgent(state),
                RunSecurityAgent(state));

            return await Finalize(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline {Id} crashed", state.PipelineId);
            await Log(state, "Orchestrator", $"💥 Crashed: {ex.Message}", "error");
            return await Finalize(state, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // AGENT RUNNERS — all use in-memory file tools
    // ═══════════════════════════════════════════════════════════════

    private async Task RunCoderAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("File path relative to repo root, e.g. Controllers/TodoController.cs")]
                 string relativePath,
                 [Description("Complete file content")]
                 string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteFile",
                "Write a complete source file. It will be committed to GitHub directly."),

            AIFunctionFactory.Create(
                () => InMemoryFileTools.ListFiles(state.Files),
                "ListFiles",
                "List all files currently staged for commit.")
        };

        var output = await RunAgentLoop(
            state,
            CreateGroqClient(),
            BuildCoderPrompt(state),
            $"Implement this feature for a .NET 10 Web API: {state.FeatureDescription}",
            "CoderAgent",
            tools,
            maxIterations: 15);

        state.GeneratedCode = output;
    }

    private async Task RunUnitTestAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs"),
                "ReadSourceCode",
                "Read all C# source files currently staged."),

            AIFunctionFactory.Create(
                ([Description("Test file path, e.g. Tests/FeatureTests.cs")]
                 string relativePath,
                 [Description("Complete NUnit test file content")]
                 string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteTestFile",
                "Write a unit test file. It will be committed to GitHub directly."),
        };

        var output = await RunAgentLoop(
            state, CreateGroqClient(), BuildUnitTestPrompt(state),
            $"Write unit tests for: {state.FeatureDescription}",
            "UnitTestAgent", tools, maxIterations: 12);

        state.UnitTestResults = output;
    }

    private async Task RunPlaywrightAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs"),
                "ReadBackendCode",
                "Read backend C# code to understand API endpoints."),

            AIFunctionFactory.Create(
                ([Description("E2E test path, e.g. tests/e2e/feature.spec.ts")]
                 string relativePath,
                 [Description("Complete Playwright TS test content")]
                 string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteE2ETest",
                "Write a Playwright TypeScript E2E test file."),
        };

        var output = await RunAgentLoop(
            state, CreateGeminiClient(), BuildPlaywrightPrompt(state),
            $"Write E2E tests for: {state.FeatureDescription}",
            "PlaywrightAgent", tools, maxIterations: 10);

        state.PlaywrightResults = output;
    }

    private async Task RunReviewAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts,.vue"),
                "ReadAllCode",
                "Read all code files staged for this pipeline run."),

            AIFunctionFactory.Create(
                async ([Description("Exact file path, e.g. Controllers/TodoController.cs")]
                       string filePath,
                       [Description("Line number that was CHANGED in the new code")]
                       int line,
                       [Description("Review comment with issue and fix suggestion")]
                       string comment) =>
                {
                    if (state.PullRequestNumber == null || state.CommitSha == null)
                        return "⚠️ No PR yet — comment skipped";
                    await _github.PostPRInlineCommentAsync(
                        state.PullRequestNumber.Value, state.CommitSha,
                        filePath.TrimStart('/'), line, comment);
                    return $"✅ Comment posted on {filePath}:{line}";
                },
                "PostInlineComment",
                "Post an inline code review comment on a specific file and line."),

            AIFunctionFactory.Create(
                async ([Description("Markdown summary of the review")]
                       string summary,
                       [Description("APPROVED, REQUEST_CHANGES, or COMMENT")]
                       string verdict) =>
                {
                    if (state.PullRequestNumber == null) return "⚠️ No PR yet";
                    var evt = verdict.ToUpper().Contains("APPROV") ? "APPROVE"
                            : verdict.ToUpper().Contains("REQUEST") ? "REQUEST_CHANGES"
                            : "COMMENT";
                    await _github.PostPRReviewAsync(state.PullRequestNumber.Value, summary, evt);
                    return $"✅ Review submitted: {evt}";
                },
                "SubmitPRReview",
                "Submit the final PR review with verdict.")
        };

        var output = await RunAgentLoop(
            state, CreateGitHubModelsClient(), BuildReviewPrompt(state),
            $"Review the code for: {state.FeatureDescription}. PR #{state.PullRequestNumber}.",
            "ReviewAgent", tools, maxIterations: 10);

        state.ReviewFeedback = output;
    }

    private async Task RunSecurityAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts,.json"),
                "ReadCodeForScan",
                "Read all code files for security analysis."),

            AIFunctionFactory.Create(
                ([Description("Source code to scan for secrets")]
                 string code) =>
                    SecurityScanTools.ScanForSecrets(code),
                "ScanSecrets",
                "Scan for hardcoded secrets and API keys."),

            AIFunctionFactory.Create(
                async ([Description("File path")] string filePath,
                       [Description("Line number")] int line,
                       [Description("Vulnerability description + fix")] string vulnerability) =>
                {
                    if (state.PullRequestNumber == null || state.CommitSha == null)
                        return "⚠️ No PR yet";
                    await _github.PostPRInlineCommentAsync(
                        state.PullRequestNumber.Value, state.CommitSha,
                        filePath.TrimStart('/'), line,
                        $"🔒 **SECURITY:** {vulnerability}");
                    return $"✅ Security comment on {filePath}:{line}";
                },
                "PostSecurityComment",
                "Post an inline security finding on a specific file and line."),

            AIFunctionFactory.Create(
                async ([Description("Security summary")] string securitySummary,
                       [Description("PASS or FAIL")] string verdict) =>
                {
                    if (state.PullRequestNumber == null) return "⚠️ No PR yet";
                    var evt = verdict.ToUpper().Contains("FAIL") ? "REQUEST_CHANGES" : "COMMENT";
                    await _github.PostPRReviewAsync(state.PullRequestNumber.Value,
                        $"## 🔒 Security Report\n\n{securitySummary}", evt);
                    return $"✅ Security review: {evt}";
                },
                "SubmitSecurityReview",
                "Submit final security review. PASS or FAIL.")
        };

        var output = await RunAgentLoop(
            state, CreateGeminiClient(), BuildSecurityPrompt(state),
            $"Security scan the code for: {state.FeatureDescription}. PR #{state.PullRequestNumber}.",
            "SecurityAgent", tools, maxIterations: 10);

        state.SecurityReport = output;
    }

    // ═══════════════════════════════════════════════════════════════
    // THE CORE TOOL LOOP
    // ═══════════════════════════════════════════════════════════════
    private async Task<string> RunAgentLoop(
        PipelineState state,
        IChatClient client,
        string systemPrompt,
        string userMessage,
        string agentName,
        List<AITool> tools,
        int maxIterations = 15)
    {
        await SignalAgentStatus(state.PipelineId, agentName, "running");
        await Log(state, agentName, "▶️ Starting...");

        var messages = new List<ChatMessage>
        {
            new(ChatRole.System, systemPrompt),
            new(ChatRole.User, userMessage)
        };

        var chatOptions = new ChatOptions { Tools = tools };
        var finalOutput = "";

        for (int i = 0; i < maxIterations; i++)
        {
            await Log(state, agentName, $"🔄 Turn {i + 1}/{maxIterations}");

            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(messages, chatOptions);
            }
            catch (Exception ex)
            {
                await Log(state, agentName, $"⚠️ LLM call failed: {ex.Message}", "error");
                break;
            }

            foreach (var msg in response.Messages)
                messages.Add(msg);

            var allToolCalls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
            var allToolResults = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();

            foreach (var tc in allToolCalls)
            {
                var argsSummary = tc.Arguments != null
                    ? string.Join(", ", tc.Arguments.Select(kv =>
                        $"{kv.Key}={Truncate(kv.Value?.ToString() ?? "", 60)}"))
                    : "";
                await Log(state, agentName, $"🔧 {tc.Name}({argsSummary})");
            }

            foreach (var tr in allToolResults)
                await Log(state, agentName,
                    $"✅ Result: {Truncate(tr.Result?.ToString() ?? "", 120)}", "success");

            if (allToolCalls.Count > 0 && allToolResults.Count > 0)
                continue;

            if (allToolCalls.Count > 0 && allToolResults.Count == 0)
            {
                await Log(state, agentName,
                    "⚠️ Tool calls returned but not executed! Check .UseFunctionInvocation()", "error");
                break;
            }

            finalOutput = response.Text ?? "";
            if (!string.IsNullOrEmpty(finalOutput))
            {
                await _hub.Clients.All.SendAsync("AgentToken", new
                {
                    pipelineId = state.PipelineId,
                    agent = agentName,
                    token = finalOutput
                });
            }

            await Log(state, agentName, "✅ Complete", "success");
            break;
        }

        await SignalAgentStatus(state.PipelineId, agentName, "done");
        return finalOutput;
    }

    // ═══════════════════════════════════════════════════════════════
    // VERIFICATION
    // ═══════════════════════════════════════════════════════════════
    private void VerifyFilesCreated(PipelineState state)
    {
        if (state.Files.Count == 0)
            throw new Exception(
                "CoderAgent wrote ZERO files. The LLM may not be calling tools.");
    }

    // ═══════════════════════════════════════════════════════════════
    // SYSTEM PROMPTS
    // ═══════════════════════════════════════════════════════════════
    private static string BuildCoderPrompt(PipelineState state) => $"""
        You are an expert .NET 10 Web API developer.
        YOUR ONLY JOB: Write real source files using the WriteFile tool.
        Files are stored in-memory and committed directly to GitHub — no local disk.

        FEATURE: {state.FeatureDescription}

        STEPS (strict order):
        1. Call ListFiles to see what exists.
        2. Call WriteFile for EVERY source file needed. You MUST call WriteFile at least once.
           Example: WriteFile("Controllers/TodoController.cs", "using Microsoft...")
        3. After all files written, respond with a plain-text summary of filenames only.

        CRITICAL RULES:
        - ALWAYS use the WriteFile tool. Never output code as text.
        - Complete, compilable .NET 10 code only. No TODOs, no placeholders.
        - Register all services in Program.cs using dependency injection.
        - Each file must have proper namespace, usings, and class structure.

        CRITICAL: You MUST use the provided tool functions via proper function calling.
        Do NOT output function calls as XML tags like <function=name>.
        ONLY use the tool calling mechanism provided to you.
        """;

    private static string BuildUnitTestPrompt(PipelineState state) => $"""
        You are an expert .NET NUnit test engineer.
        YOUR ONLY JOB: Write test files using WriteTestFile.
        Files are stored in-memory and committed directly to GitHub.

        Feature: {state.FeatureDescription}

        STEPS:
        1. Call ReadSourceCode to read existing C# files.
        2. Call WriteTestFile for each test file. At least one call required.

        RULES:
        - Use NUnit framework ONLY (not xUnit, not MSTest).
        - Use [Test], [TestCase], [SetUp], [TestFixture] attributes.
        - Min 3 tests: happy path, edge case, error case.
        - AAA pattern. Use Assert.That() with NUnit constraint model.
        - Use WriteTestFile tool only — never output code as text.

        CRITICAL: You MUST use the provided tool functions via proper function calling.
        Do NOT output function calls as XML tags like <function=name>.
        ONLY use the tool calling mechanism provided to you.
        """;

    private static string BuildPlaywrightPrompt(PipelineState state) => $"""
        You are an expert Playwright E2E test engineer.
        YOUR ONLY JOB: Write Playwright test files using WriteE2ETest.
        Files are stored in-memory and committed directly to GitHub.

        Feature: {state.FeatureDescription}
        API: http://localhost:5000

        STEPS:
        1. Call ReadBackendCode to understand endpoints.
        2. Call WriteE2ETest for each test file.

        RULES: TypeScript. At least one success + one error test case.

        CRITICAL: You MUST use the provided tool functions via proper function calling.
        Do NOT output function calls as XML tags like <function=name>.
        ONLY use the tool calling mechanism provided to you.
        """;

    private static string BuildReviewPrompt(PipelineState state) => $"""
        You are a senior architect doing code review.
        PR #{state.PullRequestNumber} is open on GitHub.
        Feature: {state.FeatureDescription}

        STEPS:
        1. Call ReadAllCode.
        2. Call PostInlineComment for each issue (min 2 comments).
        3. Call SubmitPRReview with summary and verdict.

        IMPORTANT RULES FOR PostInlineComment:
        - filePath must be EXACT relative path (e.g. "Controllers/HealthController.cs")
        - Do NOT add leading slashes
        - line must be a line number that EXISTS in the file
        - Count lines carefully when reading the code

        Review for: null checks, error handling, DI, naming, validation.
        This is a Draft PR — human must review before merge.

        CRITICAL: You MUST use the provided tool functions via proper function calling.
        Do NOT output function calls as XML tags like <function=name>.
        ONLY use the tool calling mechanism provided to you.
        """;

    private static string BuildSecurityPrompt(PipelineState state) => $"""
        You are a cybersecurity expert. Red-team mindset.
        PR #{state.PullRequestNumber} is open.
        Feature: {state.FeatureDescription}

        STEPS:
        1. Call ReadCodeForScan.
        2. Call ScanSecrets with the code content.
        3. Call PostSecurityComment for each vulnerability found.
        4. Call SubmitSecurityReview. FAIL=critical issues. PASS=low/info only.

        CRITICAL: You MUST use the provided tool functions via proper function calling.
        Do NOT output function calls as XML tags like <function=name>.
        ONLY use the tool calling mechanism provided to you.
        """;

    // ═══════════════════════════════════════════════════════════════
    // AI CLIENT FACTORIES
    // ═══════════════════════════════════════════════════════════════
    private IChatClient CreateGroqClient()
        => new ChatClientBuilder(
                new GroqChatClient(
                    _config["Groq:ApiKey"] ?? throw new Exception("Missing Groq:ApiKey")))
            .UseFunctionInvocation()
            .Build();

    private IChatClient CreateGeminiClient()
        => new ChatClientBuilder(
                new GeminiChatClient(
                    _config["Gemini:ApiKey"] ?? throw new Exception("Missing Gemini:ApiKey")))
            .UseFunctionInvocation()
            .Build();

    // Add back the GitHub Models client factory
    private IChatClient CreateGitHubModelsClient()
        => new ChatClientBuilder(
                new GitHubModelsChatClient(
                    _config["GitHub:Token"] ?? throw new Exception("Missing GitHub:Token"),
                    model: "openai/gpt-4.1"))
            .UseFunctionInvocation()
            .Build();

    // ═══════════════════════════════════════════════════════════════
    // GITHUB — now commits from memory, not disk
    // ═══════════════════════════════════════════════════════════════
    private async Task CreateBranch(PipelineState state)
    {
        await Log(state, "Orchestrator", $"🌿 Creating branch: {state.BranchName}");
        try
        {
            await _github.CreateBranchAsync(state.BranchName);
            await Log(state, "Orchestrator", "✅ Branch created", "success");
        }
        catch (Exception ex)
        {
            await Log(state, "Orchestrator", $"⚠️ Branch skipped: {ex.Message}", "warning");
        }
    }

    private async Task CommitAndPr(PipelineState state)
    {
        await Log(state, "Orchestrator",
            $"📦 Committing {state.Files.Count} files directly to GitHub (no local disk)...");
        try
        {
            // Commit from in-memory dictionary — no local folder involved
            await _github.CommitFromMemoryAsync(
                state.Files, state.BranchName,
                $"feat: {state.FeatureDescription}\n\nGenerated by DevPipeline (in-memory)");

            state.CommitSha = await _github.GetLatestCommitShaAsync(state.BranchName);

            var prBody = _github.BuildPrBody(
                state.FeatureDescription, state.UnitTestResults,
                state.ReviewFeedback, state.SecurityReport,
                state.GitHubIssueNumber);

            var (prUrl, prNumber) = await _github.CreateDraftPrWithNumberAsync(
                state.BranchName, $"[AI] {state.FeatureDescription}", prBody);

            state.PullRequestUrl = prUrl;
            state.PullRequestNumber = prNumber;
            await Log(state, "Orchestrator", $"✅ Draft PR #{prNumber}: {prUrl}", "success");
        }
        catch (Exception ex)
        {
            await Log(state, "Orchestrator", $"⚠️ Commit/PR failed: {ex.Message}", "warning");
        }
    }

    private async Task PostIssueComment(string issue, string body)
    {
        try { await _github.PostIssueCommentAsync(issue, body); }
        catch (Exception ex) { _logger.LogWarning("Issue comment failed: {M}", ex.Message); }
    }

    private async Task<PipelineState> Finalize(PipelineState state, string? error = null)
    {
        state.IsComplete = true;
        state.FinishedAt = DateTime.UtcNow;
        state.HasErrors = error != null;

        var elapsed = (state.FinishedAt!.Value - state.StartedAt).TotalSeconds;
        var summary = state.HasErrors
            ? $"⚠️ Pipeline finished with issues in {elapsed:F0}s"
            : $"🎉 Complete in {elapsed:F0}s! Draft PR #{state.PullRequestNumber} ready. " +
              $"{state.Files.Count} files committed directly to GitHub.";

        _history.Update(state.PipelineId, r =>
        {
            r.Status = state.HasErrors ? "failed" : "complete";
            r.FinishedAt = state.FinishedAt;
            r.HasErrors = state.HasErrors;
            r.PullRequestUrl = state.PullRequestUrl;
        });

        await Log(state, "Orchestrator", summary, state.HasErrors ? "warning" : "success");
        await _hub.Clients.All.SendAsync("PipelineComplete", new
        {
            pipelineId = state.PipelineId,
            hasErrors = state.HasErrors,
            prUrl = state.PullRequestUrl,
            summary
        });

        return state;
    }

    private async Task Log(PipelineState state, string agent, string message, string level = "info")
    {
        _logger.LogInformation("[{Agent}] {Message}", agent, message);
        await _hub.Clients.All.SendAsync("PipelineLog", new
        {
            pipelineId = state.PipelineId,
            agent,
            message,
            level,
            timestamp = DateTime.UtcNow
        });
    }

    private async Task SignalAgentStatus(string pipelineId, string agentName, string status)
        => await _hub.Clients.All.SendAsync("AgentStatus", new { pipelineId, agentName, status });

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";

    private static string BuildBranchName(string feature)
        => $"ai-pipeline/{new string(feature.ToLower().Replace(" ", "-")
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(40).ToArray())}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
}