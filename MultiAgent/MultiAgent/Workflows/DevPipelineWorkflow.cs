//using Microsoft.Agents.AI;
//using Microsoft.Agents.AI.Workflows;
//using Microsoft.Extensions.AI;
//using DevPipeline.Agents;
//using DevPipeline.Models;
//using DevPipeline.Services;
//using DevPipeline.Tools;
//using DevPipeline.Hubs;
//using Microsoft.AspNetCore.SignalR;

//namespace DevPipeline.Workflows;

///// <summary>
///// A2A Pipeline — Microsoft Agent Framework rc4
/////
///// ══════════════════════════════════════════════════════════════
///// CONFIRMED API — verified from IntelliSense screenshots + official docs
///// ══════════════════════════════════════════════════════════════
/////
///// 1. AGENT CREATION — instructions/tools are CONSTRUCTOR PARAMS, not options:
/////
/////    var agent = new ChatClientAgent(
/////        chatClient,                     ← IChatClient
/////        instructions: "...",            ← constructor param (NOT in ChatClientAgentOptions)
/////        name: "MyAgent",                ← constructor param (read-only property after)
/////        tools: new List&lt;AITool&gt; { ... } ← constructor param
/////    );
/////
/////    ChatClientAgentOptions actual properties (from IntelliSense):
/////      Name, Description, Id, ChatOptions,
/////      ChatHistoryProvider, AIContextProviders,
/////      ClearOnChatHistoryProviderConflict,
/////      ThrowOnChatHistoryProviderConflict,
/////      WarnOnChatHistoryProviderConflict,
/////      UseProvidedChatClientAsIs
/////    ❌ Instructions does NOT exist in ChatClientAgentOptions
/////    ❌ ChatOptions does NOT exist on ChatClientAgent (only in ChatClientAgentOptions)
/////
///// 2. WORKFLOW:
/////    Workflow workflow = AgentWorkflowBuilder.BuildSequential(a, b, c, d, e);
/////
///// 3. STREAMING EXECUTION (confirmed from Microsoft Learn + RC blog):
/////    await using StreamingRun run = await InProcessExecution.StreamAsync(workflow, messages);
/////    await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
/////    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
/////    {
/////        if (evt is AgentResponseUpdateEvent e)
/////        {
/////            string agentId = e.ExecutorId;   ← which agent is running
/////            string token   = e.Update.Text;  ← the text token
/////        }
/////        else if (evt is WorkflowOutputEvent) { break; }
/////    }
///// </summary>
//public class DevPipelineWorkflow
//{
//    private readonly IConfiguration _config;
//    private readonly GitHubService _github;
//    private readonly PipelineHistoryService _history;
//    private readonly IHubContext<PipelineHub> _hub;
//    private readonly ILogger<DevPipelineWorkflow> _logger;

//    public DevPipelineWorkflow(
//        IConfiguration config,
//        GitHubService github,
//        PipelineHistoryService history,
//        IHubContext<PipelineHub> hub,
//        ILogger<DevPipelineWorkflow> logger)
//    {
//        _config = config;
//        _github = github;
//        _history = history;
//        _hub = hub;
//        _logger = logger;
//    }

//    public async Task<PipelineState> RunAsync(PipelineRequest request)
//    {
//        var state = new PipelineState
//        {
//            FeatureDescription = request.FeatureDescription,
//            RepoPath = request.RepoPath,
//            BranchName = BuildBranchName(request.FeatureDescription),
//            GitHubIssueNumber = request.GitHubIssueNumber,
//            GitHubRepoFullName = request.GitHubRepoFullName
//        };

//        _history.Add(new PipelineRun
//        {
//            PipelineId = state.PipelineId,
//            FeatureDescription = state.FeatureDescription,
//            Status = "running",
//            GitHubIssueNumber = request.GitHubIssueNumber
//        });

//        await Log(state, "Orchestrator", $"🚀 Pipeline started: {request.FeatureDescription}");

//        try
//        {
//            await CreateBranch(state);

//            if (state.GitHubIssueNumber != null)
//                await PostIssueComment(state.GitHubIssueNumber,
//                    $"🤖 **DevPipeline started!**\nBranch: `{state.BranchName}`\n\n" +
//                    "Agents: Coder (Claude) → Tests (GPT-4o) → E2E (Gemini) → Review (GPT-4o) → Security (Gemini)");

//            // ══════════════════════════════════════════════════
//            // BUILD AGENTS
//            // Confirmed constructor: new ChatClientAgent(client, instructions, name, tools)
//            // tools param type: IEnumerable<AITool> or List<AITool>
//            // AIFunctionFactory.Create() returns AIFunction which extends AITool ✅
//            // ══════════════════════════════════════════════════

//            var coderAgent = new ChatClientAgent(
//                CreateClaudeClient(),
//                instructions: BuildCoderPrompt(state),
//                name: "CoderAgent",
//                tools: new List<AITool>
//                {
//                    AIFunctionFactory.Create(
//                        (string relativePath, string content) =>
//                            FileSystemTools.WriteFile(state.RepoPath, relativePath, content),
//                        "WriteFile",
//                        "Write a complete file to the repository. Always write full content."),

//                    AIFunctionFactory.Create(
//                        () => FileSystemTools.ListFiles(state.RepoPath),
//                        "ListFiles",
//                        "List all files currently in the repository."),
//                });

//            var unitTestAgent = new ChatClientAgent(
//                CreateOpenAiClient(),
//                instructions: BuildUnitTestPrompt(state),
//                name: "UnitTestAgent",
//                tools: new List<AITool>
//                {
//                    AIFunctionFactory.Create(
//                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs"),
//                        "ReadSourceCode",
//                        "Read all C# source files from the repo."),

//                    AIFunctionFactory.Create(
//                        (string relativePath, string content) =>
//                            FileSystemTools.WriteFile(state.RepoPath, relativePath, content),
//                        "WriteTestFile",
//                        "Write a unit test file to the repository."),

//                    AIFunctionFactory.Create(
//                        (string command) =>
//                            TerminalTools.RunCommand(command, state.RepoPath),
//                        "RunTests",
//                        "Run dotnet test and return results."),
//                });

//            var playwrightAgent = new ChatClientAgent(
//                CreateGeminiClient(),
//                instructions: BuildPlaywrightPrompt(state),
//                name: "PlaywrightAgent",
//                tools: new List<AITool>
//                {
//                    AIFunctionFactory.Create(
//                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs"),
//                        "ReadBackendCode",
//                        "Read backend C# code to understand API endpoints."),

//                    AIFunctionFactory.Create(
//                        (string relativePath, string content) =>
//                            FileSystemTools.WriteFile(state.RepoPath, relativePath, content),
//                        "WriteE2ETest",
//                        "Write a Playwright TypeScript test file."),

//                    AIFunctionFactory.Create(
//                        (string command) =>
//                            TerminalTools.RunCommand(command, state.RepoPath),
//                        "RunPlaywright",
//                        "Run Playwright tests."),
//                });

//            // ReviewAgent: clean context, no coder bias — no state in prompt
//            var reviewAgent = new ChatClientAgent(
//                CreateOpenAiClient(),
//                instructions: BuildReviewPrompt(),
//                name: "ReviewAgent",
//                tools: new List<AITool>
//                {
//                    AIFunctionFactory.Create(
//                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs,.ts,.vue"),
//                        "ReadAllCode",
//                        "Read all code files in the repository for review."),
//                });

//            var securityAgent = new ChatClientAgent(
//                CreateGeminiClient(),
//                instructions: BuildSecurityPrompt(),
//                name: "SecurityAgent",
//                tools: new List<AITool>
//                {
//                    AIFunctionFactory.Create(
//                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs,.ts,.json"),
//                        "ReadCodeForScan",
//                        "Read all code files for security analysis."),

//                    AIFunctionFactory.Create(
//                        () => SecurityScanTools.RunSemgrep(state.RepoPath),
//                        "RunSemgrep",
//                        "Run Semgrep static security scanner."),

//                    AIFunctionFactory.Create(
//                        (string code) => SecurityScanTools.ScanForSecrets(code),
//                        "ScanSecrets",
//                        "Scan for hardcoded secrets and API keys."),
//                });

//            // ══════════════════════════════════════════════════
//            // BUILD SEQUENTIAL A2A WORKFLOW
//            // Each agent's output becomes the next agent's input context.
//            // ══════════════════════════════════════════════════
//            Workflow workflow = AgentWorkflowBuilder.BuildSequential(
//                coderAgent, unitTestAgent, playwrightAgent, reviewAgent, securityAgent);

//            await Log(state, "Orchestrator",
//                "✅ A2A workflow built: Coder→UnitTest→Playwright→Review→Security", "success");

//            // ══════════════════════════════════════════════════
//            // EXECUTE WITH STREAMING
//            // Confirmed from Microsoft Learn + RC blog + Semantic Kernel blog:
//            //   InProcessExecution.StreamAsync(workflow, messages)
//            //   TurnToken(emitEvents: true) triggers agents to start
//            //   AgentResponseUpdateEvent.ExecutorId  → which agent is running
//            //   AgentResponseUpdateEvent.Update.Text → token text
//            //   WorkflowOutputEvent                  → all done, break
//            // ══════════════════════════════════════════════════
//            var inputMessages = new List<ChatMessage>
//            {
//                new(ChatRole.User,
//                    $"Implement this feature for a .NET 10 Web API: {request.FeatureDescription}")
//            };

//            await Log(state, "Orchestrator", "▶️  Running agents...");
//            await SignalAgentStatus(state.PipelineId, "CoderAgent", "running");

//            var currentAgent = "CoderAgent";
//            var fullOutput = new System.Text.StringBuilder();

//            await using StreamingRun run =
//                await InProcessExecution.RunStreamingAsync(workflow, inputMessages);

//            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

//            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
//            {
//                if (evt is AgentResponseUpdateEvent update)
//                {
//                    var agentId = update.ExecutorId ?? currentAgent;

//                    // Detect agent transition
//                    if (agentId != currentAgent)
//                    {
//                        CaptureAgentOutput(state, currentAgent, fullOutput.ToString());
//                        await Log(state, currentAgent, "✅ Complete", "success");
//                        await SignalAgentStatus(state.PipelineId, currentAgent, "done");

//                        currentAgent = agentId;
//                        fullOutput.Clear();
//                        await Log(state, currentAgent, "▶️  Starting...");
//                        await SignalAgentStatus(state.PipelineId, currentAgent, "running");
//                    }

//                    var token = update.Update?.Text ?? "";
//                    if (!string.IsNullOrEmpty(token))
//                    {
//                        fullOutput.Append(token);
//                        await _hub.Clients.All.SendAsync("AgentToken", new
//                        {
//                            pipelineId = state.PipelineId,
//                            agent = currentAgent,
//                            token
//                        });
//                    }
//                }
//                else if (evt is WorkflowOutputEvent)
//                {
//                    break;
//                }
//            }

//            // Save last agent's output
//            CaptureAgentOutput(state, currentAgent, fullOutput.ToString());
//            await SignalAgentStatus(state.PipelineId, currentAgent, "done");

//            await CommitAndPr(state);
//            return await Finalize(state);
//        }
//        catch (Exception ex)
//        {
//            _logger.LogError(ex, "Pipeline {Id} crashed", state.PipelineId);
//            await Log(state, "Orchestrator", $"💥 Crashed: {ex.Message}", "error");
//            return await Finalize(state, ex.Message);
//        }
//    }

//    // ── Helpers ───────────────────────────────────────────────────

//    private void CaptureAgentOutput(PipelineState state, string agentName, string output)
//    {
//        switch (agentName)
//        {
//            case "CoderAgent": state.GeneratedCode = output; break;
//            case "UnitTestAgent": state.UnitTestResults = output; break;
//            case "PlaywrightAgent": state.PlaywrightResults = output; break;
//            case "ReviewAgent": state.ReviewFeedback = output; break;
//            case "SecurityAgent": state.SecurityReport = output; break;
//        }
//    }

//    private async Task SignalAgentStatus(string pipelineId, string agentName, string status)
//        => await _hub.Clients.All.SendAsync("AgentStatus",
//            new { pipelineId, agentName, status });

//    // ── System Prompts ────────────────────────────────────────────

//    private static string BuildCoderPrompt(PipelineState state) => $"""
//        You are an expert .NET 10 developer. Implement this feature:
//        Feature: {state.FeatureDescription}
//        Repo: {state.RepoPath}

//        1. Call ListFiles to see current project structure
//        2. Write each file using WriteFile — always write COMPLETE file contents
//        3. Use dependency injection, async/await, XML doc comments
//        4. No placeholders or TODOs — write real working code
//        5. Summarize what you created

//        The next agent (UnitTestAgent, GPT-4o) will read and test your code.
//        Write testable code — prefer interfaces over static methods.
//        """;

//    private static string BuildUnitTestPrompt(PipelineState state) => $"""
//        You are an expert .NET test engineer (GPT-4o).
//        You are reviewing code written by a Claude coder agent.
//        Feature: {state.FeatureDescription}

//        1. Call ReadSourceCode to read all C# files
//        2. Write xUnit tests: happy path, edge cases, error cases
//        3. Use [Theory] + [InlineData], Arrange/Act/Assert pattern
//        4. Write to Tests/UnitTests.cs using WriteTestFile
//        5. Run: RunTests("dotnet test") — fix any failures
//        6. Report final test count and results

//        You did NOT write the source code. Review it critically.
//        """;

//    private static string BuildPlaywrightPrompt(PipelineState state) => $"""
//        You are an expert Playwright E2E test engineer (Gemini).
//        Feature: {state.FeatureDescription}
//        App base URL: http://localhost:5000

//        1. Call ReadBackendCode to understand API endpoints
//        2. Write Playwright TypeScript tests using page object model
//        3. Write to tests/e2e/feature.spec.ts using WriteE2ETest
//        4. Try RunPlaywright("npx playwright test") — ok if not installed
//        5. Report what you tested and any concerns
//        """;

//    private static string BuildReviewPrompt() => """
//        You are a senior software architect doing a code review (GPT-4o).
//        You have ZERO prior context. Review as if seeing this PR for the first time.

//        1. Call ReadAllCode to read all files
//        2. Review for: SOLID violations, code smells, missing error handling,
//           performance issues, missing input validation, naming conventions,
//           architecture concerns, missing XML docs
//        3. Format:
//           ## Summary
//           ## Issues Found
//           [CRITICAL/MAJOR/MINOR] File: X, Issue: Y, Fix: Z
//           ## Positive Observations
//           ## Verdict: APPROVED / CHANGES_REQUIRED
//        """;

//    private static string BuildSecurityPrompt() => """
//        You are a cybersecurity expert with a red-team adversarial mindset (Gemini).
//        You did NOT write this code. Think like an attacker.

//        1. Call ReadCodeForScan to read all source files
//        2. Call RunSemgrep for static analysis
//        3. Call ScanSecrets for hardcoded credentials
//        4. Review OWASP Top 10:
//           - Broken access control, injection, JWT flaws,
//             CORS wildcards, missing rate limiting, sensitive data in logs
//        5. Each finding: SEVERITY, LOCATION, ATTACK VECTOR, FIX
//        6. End with: SECURITY_VERDICT: PASS or FAIL
//        """;

//    // ── AI Client Factories ───────────────────────────────────────

//    private IChatClient CreateClaudeClient()
//        => new AnthropicChatClient(
//            _config["Claude:ApiKey"] ?? throw new Exception("Missing Claude:ApiKey"));

//    private IChatClient CreateOpenAiClient()
//        => new OpenAI.OpenAIClient(
//            _config["OpenAI:ApiKey"] ?? throw new Exception("Missing OpenAI:ApiKey"))
//            .GetChatClient("gpt-4o")
//            .AsIChatClient();

//    private IChatClient CreateGeminiClient()
//        => new GeminiChatClient(
//            _config["Gemini:ApiKey"] ?? throw new Exception("Missing Gemini:ApiKey"));

//    // ── GitHub helpers ────────────────────────────────────────────

//    private async Task CreateBranch(PipelineState state)
//    {
//        await Log(state, "Orchestrator", $"🌿 Creating branch: {state.BranchName}");
//        try
//        {
//            await _github.CreateBranchAsync(state.BranchName);
//            await Log(state, "Orchestrator", "✅ Branch created", "success");
//        }
//        catch (Exception ex)
//        {
//            await Log(state, "Orchestrator", $"⚠️ Branch skipped: {ex.Message}", "warning");
//        }
//    }

//    private async Task CommitAndPr(PipelineState state)
//    {
//        await Log(state, "Orchestrator", "📦 Committing to GitHub...");
//        try
//        {
//            await _github.CommitFilesAsync(state.RepoPath, state.BranchName,
//                $"feat: {state.FeatureDescription}\n\nGenerated by DevPipeline A2A — MAF rc4");

//            var prBody = _github.BuildPrBody(
//                state.FeatureDescription, state.UnitTestResults,
//                state.ReviewFeedback, state.SecurityReport,
//                state.GitHubIssueNumber);

//            state.PullRequestUrl = await _github.CreateDraftPrAsync(
//                state.BranchName, $"[AI] {state.FeatureDescription}", prBody);

//            await Log(state, "Orchestrator", $"✅ Draft PR: {state.PullRequestUrl}", "success");
//        }
//        catch (Exception ex)
//        {
//            await Log(state, "Orchestrator", $"⚠️ Commit/PR failed: {ex.Message}", "warning");
//        }
//    }

//    private async Task PostIssueComment(string issue, string body)
//    {
//        try { await _github.PostIssueCommentAsync(issue, body); }
//        catch (Exception ex) { _logger.LogWarning("Issue comment failed: {M}", ex.Message); }
//    }

//    private async Task<PipelineState> Finalize(PipelineState state, string? error = null)
//    {
//        state.IsComplete = true;
//        state.FinishedAt = DateTime.UtcNow;
//        state.HasErrors = error != null;

//        var elapsed = (state.FinishedAt!.Value - state.StartedAt).TotalSeconds;
//        var summary = state.HasErrors
//            ? $"⚠️ Pipeline finished with issues in {elapsed:F0}s"
//            : $"🎉 Complete in {elapsed:F0}s! Draft PR ready.";

//        _history.Update(state.PipelineId, r =>
//        {
//            r.Status = state.HasErrors ? "failed" : "complete";
//            r.FinishedAt = state.FinishedAt;
//            r.HasErrors = state.HasErrors;
//            r.PullRequestUrl = state.PullRequestUrl;
//        });

//        await Log(state, "Orchestrator", summary, state.HasErrors ? "warning" : "success");
//        await _hub.Clients.All.SendAsync("PipelineComplete", new
//        {
//            pipelineId = state.PipelineId,
//            hasErrors = state.HasErrors,
//            prUrl = state.PullRequestUrl,
//            summary
//        });

//        return state;
//    }

//    private async Task Log(PipelineState state, string agent,
//        string message, string level = "info")
//        => await _hub.Clients.All.SendAsync("PipelineLog", new
//        {
//            pipelineId = state.PipelineId,
//            agent,
//            message,
//            level,
//            timestamp = DateTime.UtcNow
//        });

//    private static string BuildBranchName(string feature)
//        => $"ai-pipeline/{new string(feature.ToLower().Replace(" ", "-")
//            .Where(c => char.IsLetterOrDigit(c) || c == '-')
//            .Take(40).ToArray())}-{DateTime.UtcNow:yyyyMMdd-HHmm}";
//}

//end of claude, openai code here

using Microsoft.Agents.AI;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using DevPipeline.Agents;
using DevPipeline.Models;
using DevPipeline.Services;
using DevPipeline.Tools;
using DevPipeline.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace DevPipeline.Workflows;

/// <summary>
/// A2A Pipeline — Microsoft Agent Framework rc4
///
/// ══════════════════════════════════════════════════════════════
/// CONFIRMED API — verified from IntelliSense screenshots + official docs
/// ══════════════════════════════════════════════════════════════
///
/// 1. AGENT CREATION — instructions/tools are CONSTRUCTOR PARAMS, not options:
///
///    var agent = new ChatClientAgent(
///        chatClient,                     ← IChatClient
///        instructions: "...",            ← constructor param (NOT in ChatClientAgentOptions)
///        name: "MyAgent",                ← constructor param (read-only property after)
///        tools: new List&lt;AITool&gt; { ... } ← constructor param
///    );
///
///    ChatClientAgentOptions actual properties (from IntelliSense):
///      Name, Description, Id, ChatOptions,
///      ChatHistoryProvider, AIContextProviders,
///      ClearOnChatHistoryProviderConflict,
///      ThrowOnChatHistoryProviderConflict,
///      WarnOnChatHistoryProviderConflict,
///      UseProvidedChatClientAsIs
///    ❌ Instructions does NOT exist in ChatClientAgentOptions
///    ❌ ChatOptions does NOT exist on ChatClientAgent (only in ChatClientAgentOptions)
///
/// 2. WORKFLOW:
///    Workflow workflow = AgentWorkflowBuilder.BuildSequential(a, b, c, d, e);
///
/// 3. STREAMING EXECUTION (confirmed from Microsoft Learn + RC blog):
///    await using StreamingRun run = await InProcessExecution.StreamAsync(workflow, messages);
///    await run.TrySendMessageAsync(new TurnToken(emitEvents: true));
///    await foreach (WorkflowEvent evt in run.WatchStreamAsync())
///    {
///        if (evt is AgentResponseUpdateEvent e)
///        {
///            string agentId = e.ExecutorId;   ← which agent is running
///            string token   = e.Update.Text;  ← the text token
///        }
///        else if (evt is WorkflowOutputEvent) { break; }
///    }
/// </summary>
public class DevPipelineWorkflow
{
    private readonly IConfiguration _config;
    private readonly GitHubService _github;
    private readonly PipelineHistoryService _history;
    private readonly IHubContext<PipelineHub> _hub;
    private readonly ILogger<DevPipelineWorkflow> _logger;

    public DevPipelineWorkflow(
        IConfiguration config,
        GitHubService github,
        PipelineHistoryService history,
        IHubContext<PipelineHub> hub,
        ILogger<DevPipelineWorkflow> logger)
    {
        _config = config;
        _github = github;
        _history = history;
        _hub = hub;
        _logger = logger;
    }

    public async Task<PipelineState> RunAsync(PipelineRequest request)
    {
        var state = new PipelineState
        {
            FeatureDescription = request.FeatureDescription,
            RepoPath = request.RepoPath,
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

        try
        {
            await CreateBranch(state);

            if (state.GitHubIssueNumber != null)
                await PostIssueComment(state.GitHubIssueNumber,
                    $"🤖 **DevPipeline started!**\nBranch: `{state.BranchName}`\n\n" +
                    "Agents: Coder (Claude) → Tests (GPT-4o) → E2E (Gemini) → Review (GPT-4o) → Security (Gemini)");

            // ══════════════════════════════════════════════════
            // BUILD AGENTS
            // Confirmed constructor: new ChatClientAgent(client, instructions, name, tools)
            // tools param type: IEnumerable<AITool> or List<AITool>
            // AIFunctionFactory.Create() returns AIFunction which extends AITool ✅
            // ══════════════════════════════════════════════════

            var coderAgent = new ChatClientAgent(
                CreateGeminiClient(),
                instructions: BuildCoderPrompt(state),
                name: "CoderAgent",
                tools: new List<AITool>
                {
                    AIFunctionFactory.Create(
                        (string relativePath, string content) =>
                            FileSystemTools.WriteFile(state.RepoPath, relativePath, content),
                        "WriteFile",
                        "Write a complete file to the repository. Always write full content."),

                    AIFunctionFactory.Create(
                        () => FileSystemTools.ListFiles(state.RepoPath),
                        "ListFiles",
                        "List all files currently in the repository."),
                });

            var unitTestAgent = new ChatClientAgent(
                CreateGroqClient(),
                instructions: BuildUnitTestPrompt(state),
                name: "UnitTestAgent",
                tools: new List<AITool>
                {
                    AIFunctionFactory.Create(
                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs"),
                        "ReadSourceCode",
                        "Read all C# source files from the repo."),

                    AIFunctionFactory.Create(
                        (string relativePath, string content) =>
                            FileSystemTools.WriteFile(state.RepoPath, relativePath, content),
                        "WriteTestFile",
                        "Write a unit test file to the repository."),

                    AIFunctionFactory.Create(
                        (string command) =>
                            TerminalTools.RunCommand(command, state.RepoPath),
                        "RunTests",
                        "Run dotnet test and return results."),
                });

            var playwrightAgent = new ChatClientAgent(
                CreateGeminiClient(),
                instructions: BuildPlaywrightPrompt(state),
                name: "PlaywrightAgent",
                tools: new List<AITool>
                {
                    AIFunctionFactory.Create(
                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs"),
                        "ReadBackendCode",
                        "Read backend C# code to understand API endpoints."),

                    AIFunctionFactory.Create(
                        (string relativePath, string content) =>
                            FileSystemTools.WriteFile(state.RepoPath, relativePath, content),
                        "WriteE2ETest",
                        "Write a Playwright TypeScript test file."),

                    AIFunctionFactory.Create(
                        (string command) =>
                            TerminalTools.RunCommand(command, state.RepoPath),
                        "RunPlaywright",
                        "Run Playwright tests."),
                });

            // ReviewAgent: GitHub Models GPT-4o Mini — reuses existing GitHub PAT, no extra key needed
            var reviewAgent = new ChatClientAgent(
                CreateGitHubModelsClient(),
                instructions: BuildReviewPrompt(),
                name: "ReviewAgent",
                tools: new List<AITool>
                {
                    AIFunctionFactory.Create(
                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs,.ts,.vue"),
                        "ReadAllCode",
                        "Read all code files in the repository for review."),
                });

            var securityAgent = new ChatClientAgent(
                CreateGeminiClient(),
                instructions: BuildSecurityPrompt(),
                name: "SecurityAgent",
                tools: new List<AITool>
                {
                    AIFunctionFactory.Create(
                        () => FileSystemTools.ReadRepoFiles(state.RepoPath, ".cs,.ts,.json"),
                        "ReadCodeForScan",
                        "Read all code files for security analysis."),

                    AIFunctionFactory.Create(
                        () => SecurityScanTools.RunSemgrep(state.RepoPath),
                        "RunSemgrep",
                        "Run Semgrep static security scanner."),

                    AIFunctionFactory.Create(
                        (string code) => SecurityScanTools.ScanForSecrets(code),
                        "ScanSecrets",
                        "Scan for hardcoded secrets and API keys."),
                });

            // ══════════════════════════════════════════════════
            // BUILD SEQUENTIAL A2A WORKFLOW
            // Each agent's output becomes the next agent's input context.
            // ══════════════════════════════════════════════════
            Workflow workflow = AgentWorkflowBuilder.BuildSequential(
                coderAgent, unitTestAgent, playwrightAgent, reviewAgent, securityAgent);

            await Log(state, "Orchestrator",
                "✅ A2A workflow built: Coder→UnitTest→Playwright→Review→Security", "success");

            // ══════════════════════════════════════════════════
            // EXECUTE WITH STREAMING
            // Confirmed from Microsoft Learn + RC blog + Semantic Kernel blog:
            //   InProcessExecution.StreamAsync(workflow, messages)
            //   TurnToken(emitEvents: true) triggers agents to start
            //   AgentResponseUpdateEvent.ExecutorId  → which agent is running
            //   AgentResponseUpdateEvent.Update.Text → token text
            //   WorkflowOutputEvent                  → all done, break
            // ══════════════════════════════════════════════════
            var inputMessages = new List<ChatMessage>
            {
                new(ChatRole.User,
                    $"Implement this feature for a .NET 10 Web API: {request.FeatureDescription}")
            };

            await Log(state, "Orchestrator", "▶️  Running agents...");
            await SignalAgentStatus(state.PipelineId, "CoderAgent", "running");

            var currentAgent = "CoderAgent";
            var fullOutput = new System.Text.StringBuilder();

            await using StreamingRun run =
                await InProcessExecution.RunStreamingAsync(workflow, inputMessages);

            await run.TrySendMessageAsync(new TurnToken(emitEvents: true));

            await foreach (WorkflowEvent evt in run.WatchStreamAsync())
            {
                if (evt is AgentResponseUpdateEvent update)
                {
                    var agentId = update.ExecutorId ?? currentAgent;

                    // Detect agent transition
                    if (agentId != currentAgent)
                    {
                        CaptureAgentOutput(state, currentAgent, fullOutput.ToString());
                        await Log(state, currentAgent, "✅ Complete", "success");
                        await SignalAgentStatus(state.PipelineId, currentAgent, "done");

                        currentAgent = agentId;
                        fullOutput.Clear();
                        await Log(state, currentAgent, "▶️  Starting...");
                        await SignalAgentStatus(state.PipelineId, currentAgent, "running");
                    }

                    var token = update.Update?.Text ?? "";
                    if (!string.IsNullOrEmpty(token))
                    {
                        fullOutput.Append(token);
                        await _hub.Clients.All.SendAsync("AgentToken", new
                        {
                            pipelineId = state.PipelineId,
                            agent = currentAgent,
                            token
                        });
                    }
                }
                else if (evt is WorkflowOutputEvent)
                {
                    break;
                }
            }

            // Save last agent's output
            CaptureAgentOutput(state, currentAgent, fullOutput.ToString());
            await SignalAgentStatus(state.PipelineId, currentAgent, "done");

            await CommitAndPr(state);
            return await Finalize(state);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pipeline {Id} crashed", state.PipelineId);
            await Log(state, "Orchestrator", $"💥 Crashed: {ex.Message}", "error");
            return await Finalize(state, ex.Message);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────

    private void CaptureAgentOutput(PipelineState state, string agentName, string output)
    {
        switch (agentName)
        {
            case "CoderAgent": state.GeneratedCode = output; break;
            case "UnitTestAgent": state.UnitTestResults = output; break;
            case "PlaywrightAgent": state.PlaywrightResults = output; break;
            case "ReviewAgent": state.ReviewFeedback = output; break;
            case "SecurityAgent": state.SecurityReport = output; break;
        }
    }

    private async Task SignalAgentStatus(string pipelineId, string agentName, string status)
        => await _hub.Clients.All.SendAsync("AgentStatus",
            new { pipelineId, agentName, status });

    // ── System Prompts ────────────────────────────────────────────

    private static string BuildCoderPrompt(PipelineState state) => $"""
        You are an expert .NET 10 developer. Implement this feature:
        Feature: {state.FeatureDescription}
        Repo: {state.RepoPath}

        1. Call ListFiles to see current project structure
        2. Write each file using WriteFile — always write COMPLETE file contents
        3. Use dependency injection, async/await, XML doc comments
        4. No placeholders or TODOs — write real working code
        5. Summarize what you created

        The next agent (UnitTestAgent, GPT-4o) will read and test your code.
        Write testable code — prefer interfaces over static methods.
        """;

    private static string BuildUnitTestPrompt(PipelineState state) => $"""
        You are an expert .NET test engineer (GPT-4o).
        You are reviewing code written by a Claude coder agent.
        Feature: {state.FeatureDescription}

        1. Call ReadSourceCode to read all C# files
        2. Write xUnit tests: happy path, edge cases, error cases
        3. Use [Theory] + [InlineData], Arrange/Act/Assert pattern
        4. Write to Tests/UnitTests.cs using WriteTestFile
        5. Run: RunTests("dotnet test") — fix any failures
        6. Report final test count and results

        You did NOT write the source code. Review it critically.
        """;

    private static string BuildPlaywrightPrompt(PipelineState state) => $"""
        You are an expert Playwright E2E test engineer (Gemini).
        Feature: {state.FeatureDescription}
        App base URL: http://localhost:5000

        1. Call ReadBackendCode to understand API endpoints
        2. Write Playwright TypeScript tests using page object model
        3. Write to tests/e2e/feature.spec.ts using WriteE2ETest
        4. Try RunPlaywright("npx playwright test") — ok if not installed
        5. Report what you tested and any concerns
        """;

    private static string BuildReviewPrompt() => """
        You are a senior software architect doing a code review (GPT-4o).
        You have ZERO prior context. Review as if seeing this PR for the first time.

        1. Call ReadAllCode to read all files
        2. Review for: SOLID violations, code smells, missing error handling,
           performance issues, missing input validation, naming conventions,
           architecture concerns, missing XML docs
        3. Format:
           ## Summary
           ## Issues Found
           [CRITICAL/MAJOR/MINOR] File: X, Issue: Y, Fix: Z
           ## Positive Observations
           ## Verdict: APPROVED / CHANGES_REQUIRED
        """;

    private static string BuildSecurityPrompt() => """
        You are a cybersecurity expert with a red-team adversarial mindset (Gemini).
        You did NOT write this code. Think like an attacker.

        1. Call ReadCodeForScan to read all source files
        2. Call RunSemgrep for static analysis
        3. Call ScanSecrets for hardcoded credentials
        4. Review OWASP Top 10:
           - Broken access control, injection, JWT flaws,
             CORS wildcards, missing rate limiting, sensitive data in logs
        5. Each finding: SEVERITY, LOCATION, ATTACK VECTOR, FIX
        6. End with: SECURITY_VERDICT: PASS or FAIL
        """;

    // ── AI Client Factories — 100% FREE, no credit card needed ───
    // CoderAgent      → Gemini 2.0 Flash       (Google AI Studio free tier)
    // UnitTestAgent   → Groq Llama 3.3 70B     (Groq free tier)
    // PlaywrightAgent → Gemini 2.0 Flash       (Google AI Studio free tier)
    // ReviewAgent     → GitHub Models GPT-4o Mini (reuses your existing GitHub PAT!)
    // SecurityAgent   → Gemini 2.0 Flash       (Google AI Studio free tier)

    private IChatClient CreateGroqClient()
        => new GroqChatClient(
            _config["Groq:ApiKey"] ?? throw new Exception("Missing Groq:ApiKey"));

    private IChatClient CreateGeminiClient()
        => new GeminiChatClient(
            _config["Gemini:ApiKey"] ?? throw new Exception("Missing Gemini:ApiKey"));

    private IChatClient CreateGitHubModelsClient()
        => new GitHubModelsChatClient(
            _config["GitHub:Token"] ?? throw new Exception("Missing GitHub:Token"),
            model: "openai/gpt-4o-mini");

    // ── GitHub helpers ────────────────────────────────────────────

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
        await Log(state, "Orchestrator", "📦 Committing to GitHub...");
        try
        {
            await _github.CommitFilesAsync(state.RepoPath, state.BranchName,
                $"feat: {state.FeatureDescription}\n\nGenerated by DevPipeline A2A — MAF rc4");

            var prBody = _github.BuildPrBody(
                state.FeatureDescription, state.UnitTestResults,
                state.ReviewFeedback, state.SecurityReport,
                state.GitHubIssueNumber);

            state.PullRequestUrl = await _github.CreateDraftPrAsync(
                state.BranchName, $"[AI] {state.FeatureDescription}", prBody);

            await Log(state, "Orchestrator", $"✅ Draft PR: {state.PullRequestUrl}", "success");
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
            : $"🎉 Complete in {elapsed:F0}s! Draft PR ready.";

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

    private async Task Log(PipelineState state, string agent,
        string message, string level = "info")
        => await _hub.Clients.All.SendAsync("PipelineLog", new
        {
            pipelineId = state.PipelineId,
            agent,
            message,
            level,
            timestamp = DateTime.UtcNow
        });

    private static string BuildBranchName(string feature)
        => $"ai-pipeline/{new string(feature.ToLower().Replace(" ", "-")
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(40).ToArray())}-{DateTime.UtcNow:yyyyMMdd-HHmm}";
}