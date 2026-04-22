using Microsoft.Extensions.AI;
using MultiAgent.Agents;
using MultiAgent.Models;
using MultiAgent.Services;
using MultiAgent.Tools;
using MultiAgent.Hubs;
using Microsoft.AspNetCore.SignalR;
using System.ComponentModel;
using System.Text;
using System.Text.RegularExpressions;

namespace MultiAgent.Workflows;

/// <summary>
/// MultiAgent Workflow — GitHub Models, two vendors (OpenAI + Mistral).
///
/// AGENTS AND MODELS:
///   CoderAgent      → GitHub Models: openai/gpt-4.1-mini          (OpenAI — code generation)
///   UnitTestAgent   → GitHub Models: openai/gpt-4.1-mini          (OpenAI — test generation)
///   PlaywrightAgent → GitHub Models: mistral-ai/mistral-small-2503  (Mistral — E2E test generation)
///   ReviewAgent     → GitHub Models: mistral-ai/mistral-small-2503  (Mistral — code review)
///   SecurityAgent   → GitHub Models: mistral-ai/mistral-small-2503  (Mistral — security analysis)
///
/// FLOW:
///   Phase 1: Coder generates code             (OpenAI gpt-4.1-mini)
///   Phase 2: Review + Security in parallel    (Mistral Small 3.1 × 2)
///   Phase 3: Coder fixes or defends findings  (OpenAI gpt-4.1-mini)
///   Phase 4: Re-review in parallel            (Mistral Small 3.1 × 2)
///   Phase 5: Unit tests + Playwright parallel (gpt-4.1-mini + Mistral Small 3.1)
///   Phase 6: Commit → Create PR → Post all inline comments
/// </summary>
public class MultiAgentWorkflow
{
    private readonly IConfiguration _config;
    private readonly GitHubService _github;
    private readonly PipelineHistoryService _history;
    private readonly IHubContext<PipelineHub> _hub;
    private readonly ILogger<MultiAgentWorkflow> _logger;

    private const int MaxReviewRounds = 2;

    public MultiAgentWorkflow(
        IConfiguration config, GitHubService github,
        PipelineHistoryService history, IHubContext<PipelineHub> hub,
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
        await Log(state, "Orchestrator", "🤖 Multi-vendor GitHub Models: OpenAI gpt-4.1-mini (Coder) + Mistral Small 3.1 (Reviewer)");

        try
        {
            await CreateBranch(state);

            // ── PHASE 1: Generate code ─────────────────────────────
            await Log(state, "Orchestrator", "▶️ Phase 1: CoderAgent [gpt-4.1-mini] generates code");
            await RunCoderAgent(state);
            VerifyFilesCreated(state);

            // ── PHASE 2–4: Internal review loop ───────────────────
            await Log(state, "Orchestrator", "▶️ Phase 2–4: Internal review loop");

            for (int round = 1; round <= MaxReviewRounds; round++)
            {
                await Log(state, "Orchestrator", $"🔄 Review round {round}/{MaxReviewRounds}");

                // Phase 2: Review + Security in parallel (both Mistral Small 3.1)
                var reviewTask = RunInternalReviewAgent(state);
                var securityTask = RunInternalSecurityAgent(state);
                await Task.WhenAll(reviewTask, securityTask);

                var allFindings = reviewTask.Result.Concat(securityTask.Result).ToList();
                var criticalCount = allFindings.Count(f =>
                    f.Severity is FindingSeverity.Critical or FindingSeverity.High);

                await Log(state, "Orchestrator",
                    $"📋 {allFindings.Count} finding(s), {criticalCount} critical/high",
                    criticalCount > 0 ? "warning" : "info");

                if (allFindings.Count == 0)
                {
                    await Log(state, "Orchestrator", "✅ No issues found", "success");
                    break;
                }

                // Phase 3: Coder fixes or defends ALL findings (gpt-4.1-mini)
                await Log(state, "Orchestrator", "▶️ CoderAgent [gpt-4.1-mini] addressing all findings...");
                var coderResponses = await RunCoderFixAgent(state, allFindings);

                // Phase 4: Re-review (both Mistral Small 3.1 in parallel)
                await Log(state, "Orchestrator", "▶️ Re-evaluating responses [Mistral Small 3.1 × 2 parallel]...");

                var reviewFindings = allFindings.Where(f => f.Source == "ReviewAgent").ToList();
                var securityFindings = allFindings.Where(f => f.Source == "SecurityAgent").ToList();

                var reReviewTask = reviewFindings.Count > 0
                    ? RunReReviewAgent(state, reviewFindings, coderResponses)
                    : Task.FromResult(new List<ReReviewResult>());

                var secReReviewTask = securityFindings.Count > 0
                    ? RunSecurityReReviewAgent(state, securityFindings, coderResponses)
                    : Task.FromResult(new List<ReReviewResult>());

                await Task.WhenAll(reReviewTask, secReReviewTask);
                var reReviewResults = reReviewTask.Result.Concat(secReReviewTask.Result).ToList();

                foreach (var f in allFindings)
                {
                    var cr = coderResponses.FirstOrDefault(r => r.FindingId == f.Id);
                    var rr = reReviewResults.FirstOrDefault(r => r.FindingId == f.Id);

                    state.InternalReviewLog.Add(new ReviewLogEntry
                    {
                        Round = round,
                        Finding = f,
                        CoderResponse = cr?.Response ?? "(no response)",
                        FinalStatus = rr?.Accepted == true ? "resolved" : "unresolved"
                    });
                }

                var unresolvedCritical = reReviewResults
                    .Count(r => !r.Accepted &&
                        (r.Severity is FindingSeverity.Critical or FindingSeverity.High));

                if (unresolvedCritical == 0)
                {
                    await Log(state, "Orchestrator", "✅ All critical issues resolved", "success");
                    break;
                }

                if (round < MaxReviewRounds)
                    await Log(state, "Orchestrator",
                        $"⚠️ {unresolvedCritical} unresolved critical — another round", "warning");
            }

            // ── PHASE 5: Tests on FINAL stable code (parallel) ────
            await Log(state, "Orchestrator",
                "▶️ Phase 5: Tests [OpenAI gpt-4.1-mini + Mistral Small 3.1 parallel]");

            await Task.WhenAll(
                RunUnitTestAgent(state),
                RunPlaywrightAgent(state));

            // ── PHASE 6: Commit + PR + Post all comments ──────────
            await Log(state, "Orchestrator", "▶️ Phase 6: Commit → PR → Comments");
            await CommitAndPr(state);

            if (state.PullRequestNumber != null)
                await PostAllFindingsToPR(state);

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
    // PHASE 1: CODER — openai/gpt-4.1-mini
    // ═══════════════════════════════════════════════════════════════

    private async Task RunCoderAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                ([Description("File path relative to repo root")] string relativePath,
                 [Description("Complete file content")] string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteFile", "Write a source file."),
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ListFiles(state.Files),
                "ListFiles", "List staged files.")
        };

        state.GeneratedCode = await RunAgentLoop(state, CreateCoderClient(),
            BuildCoderPrompt(state),
            $"Implement: {state.FeatureDescription}", "CoderAgent", tools, 15);
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 5: TESTS (after review loop)
    // ═══════════════════════════════════════════════════════════════

    private async Task RunUnitTestAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs"),
                "ReadSourceCode", "Read C# source files."),
            AIFunctionFactory.Create(
                ([Description("Test file path")] string relativePath,
                 [Description("NUnit test content")] string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteTestFile", "Write a test file."),
        };

        state.UnitTestResults = await RunAgentLoop(state, CreateCoderClient(),
            BuildUnitTestPrompt(state),
            $"Write NUnit tests for: {state.FeatureDescription}", "UnitTestAgent", tools, 12);
    }

    private async Task RunPlaywrightAgent(PipelineState state)
    {
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs"),
                "ReadBackendCode", "Read backend code."),
            AIFunctionFactory.Create(
                ([Description("E2E test path")] string relativePath,
                 [Description("Playwright TS content")] string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteE2ETest", "Write a Playwright test."),
        };

        state.PlaywrightResults = await RunAgentLoop(state, CreateReviewClient(),
            BuildPlaywrightPrompt(state),
            $"Write E2E tests for: {state.FeatureDescription}", "PlaywrightAgent", tools, 10);
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 2: INTERNAL REVIEW + SECURITY (parallel) — Mistral Small 3.1-Instruct
    // ═══════════════════════════════════════════════════════════════

    private async Task<List<ReviewFinding>> RunInternalReviewAgent(PipelineState state)
    {
        var findings = new List<ReviewFinding>();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts,.vue"),
                "ReadAllCode", "Read all code."),
            AIFunctionFactory.Create(
                ([Description("File path")] string filePath,
                 [Description("Line number")] int line,
                 [Description("Issue + suggested fix")] string comment,
                 [Description("CRITICAL, HIGH, MEDIUM, or LOW")] string severity) =>
                {
                    var f = new ReviewFinding
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Source = "ReviewAgent",
                        FilePath = filePath.TrimStart('/'),
                        Line = line,
                        Comment = comment,
                        Severity = ParseSeverity(severity)
                    };
                    findings.Add(f);
                    return $"✅ Finding #{f.Id}: [{severity}] {filePath}:{line}";
                },
                "ReportFinding", "Report a review finding with severity."),
        };

        await RunAgentLoop(state, CreateReviewClient(),
            BuildInternalReviewPrompt(state),
            $"Internal review for: {state.FeatureDescription}",
            "ReviewAgent", tools, 10);
        return findings;
    }

    private async Task<List<ReviewFinding>> RunInternalSecurityAgent(PipelineState state)
    {
        var findings = new List<ReviewFinding>();
        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts,.json"),
                "ReadCodeForScan", "Read code for security scan."),
            AIFunctionFactory.Create(
                ([Description("Code to scan")] string code) =>
                    SecurityScanTools.ScanForSecrets(code),
                "ScanSecrets", "Scan for hardcoded secrets."),
            AIFunctionFactory.Create(
                ([Description("File path")] string filePath,
                 [Description("Line number")] int line,
                 [Description("Vulnerability + vector + fix")] string vulnerability,
                 [Description("CRITICAL, HIGH, MEDIUM, or LOW")] string severity) =>
                {
                    // "N/A":0 is the convention for a clean scan — don't add as a finding
                    if (filePath.Equals("N/A", StringComparison.OrdinalIgnoreCase) && line == 0)
                        return "✅ Clean — no vulnerabilities found.";

                    var f = new ReviewFinding
                    {
                        Id = Guid.NewGuid().ToString("N")[..8],
                        Source = "SecurityAgent",
                        FilePath = filePath.TrimStart('/'),
                        Line = line,
                        Comment = vulnerability,
                        Severity = ParseSeverity(severity)
                    };
                    findings.Add(f);
                    return $"✅ Finding #{f.Id}: [{severity}] {filePath}:{line}";
                },
                "ReportVulnerability", "Report a security vulnerability."),
        };

        await RunAgentLoop(state, CreateReviewClient(),
            BuildInternalSecurityPrompt(state),
            $"Security scan for: {state.FeatureDescription}",
            "SecurityAgent", tools, 10);
        return findings;
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 3: CODER FIXES OR DEFENDS — gpt-4.1-mini
    // ═══════════════════════════════════════════════════════════════

    private async Task<List<CoderResponse>> RunCoderFixAgent(
        PipelineState state, List<ReviewFinding> findings)
    {
        var responses = new List<CoderResponse>();

        var findingsText = new StringBuilder();
        findingsText.AppendLine("You MUST call RespondToFinding for EVERY ID below:");
        findingsText.AppendLine();
        foreach (var f in findings)
        {
            findingsText.AppendLine($"  ID: \"{f.Id}\" | Severity: {f.Severity} | Source: {f.Source}");
            findingsText.AppendLine($"  File: {f.FilePath}:{f.Line}");
            findingsText.AppendLine($"  Issue: {f.Comment}");
            findingsText.AppendLine($"  -> Call: RespondToFinding(\"{f.Id}\", \"FIX\" or \"DEFEND\", explanation)");
            findingsText.AppendLine();
        }

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts"),
                "ReadCurrentCode", "Read current code."),
            AIFunctionFactory.Create(
                ([Description("File path")] string relativePath,
                 [Description("Updated file content")] string content) =>
                    InMemoryFileTools.WriteFile(state.Files, relativePath, content),
                "WriteFile", "Write updated file."),
            AIFunctionFactory.Create(
                ([Description("Finding ID (e.g. 'a1b2c3d4')")] string findingId,
                 [Description("FIX or DEFEND")] string action,
                 [Description("Explanation of fix or defense reasoning")] string explanation) =>
                {
                    responses.Add(new CoderResponse
                    {
                        FindingId = findingId,
                        Action = action.ToUpper().Contains("FIX")
                            ? ResponseAction.Fix : ResponseAction.Defend,
                        Response = explanation
                    });
                    return $"✅ #{findingId}: {action}";
                },
                "RespondToFinding", "Respond to a finding: FIX or DEFEND."),
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ListFiles(state.Files),
                "ListFiles", "List files.")
        };

        var prompt = $"""
            You are an expert .NET 10 developer responding to review findings.

            FINDINGS (from both ReviewAgent and SecurityAgent):
            {findingsText}

            For EACH finding:
            1. FIX: Call WriteFile with corrected code, then RespondToFinding(id, "FIX", explanation)
            2. DEFEND: Call RespondToFinding(id, "DEFEND", reasoning) if you disagree

            Fix CRITICAL/HIGH unless you have strong reasoning. MEDIUM/LOW can be defended.
            Rewrite COMPLETE files when fixing. Respond to EVERY finding.

            CRITICAL: Use tool functions via proper function calling.
            Do NOT output function calls as XML tags.
            """;

        await RunAgentLoop(state, CreateCoderClient(), prompt,
            "Address all findings.", "CoderAgent", tools, 20);

        foreach (var f in findings.Where(f => !responses.Any(r => r.FindingId == f.Id)))
            responses.Add(new CoderResponse
            {
                FindingId = f.Id,
                Action = ResponseAction.Fix,
                Response = "(No explicit response — assumed addressed)"
            });

        await Log(state, "CoderAgent",
            $"📝 {responses.Count(r => r.Action == ResponseAction.Fix)} fixed, " +
            $"{responses.Count(r => r.Action == ResponseAction.Defend)} defended");
        return responses;
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 4: RE-REVIEW (parallel) — Mistral Small 3.1-Instruct
    // ═══════════════════════════════════════════════════════════════

    private async Task<List<ReReviewResult>> RunReReviewAgent(
        PipelineState state, List<ReviewFinding> findings, List<CoderResponse> coderResponses)
    {
        var results = new List<ReReviewResult>();

        var context = new StringBuilder();
        foreach (var f in findings)
        {
            var cr = coderResponses.FirstOrDefault(r => r.FindingId == f.Id);
            context.AppendLine($"FINDING #{f.Id} [{f.Severity}] ({f.Source})");
            context.AppendLine($"  File: {f.FilePath}:{f.Line}");
            context.AppendLine($"  Issue: {f.Comment}");
            context.AppendLine($"  Coder: {cr?.Action} — {cr?.Response}");
            context.AppendLine();
        }

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts"),
                "ReadUpdatedCode", "Read code after fixes."),
            AIFunctionFactory.Create(
                ([Description("Finding ID")] string findingId,
                 [Description("true=accept, false=unresolved")] bool accepted,
                 [Description("Reason")] string reason,
                 [Description("CRITICAL, HIGH, MEDIUM, LOW")] string severity) =>
                {
                    results.Add(new ReReviewResult
                    {
                        FindingId = findingId,
                        Accepted = accepted,
                        Reason = reason,
                        Severity = ParseSeverity(severity)
                    });
                    return $"✅ #{findingId}: {(accepted ? "ACCEPTED" : "UNRESOLVED")}";
                },
                "EvaluateResponse", "Accept or reject coder's response."),
        };

        var prompt = $"""
            You are the same senior code reviewer who originally found these issues.
            Now verify whether the coder's fixes are correct.

            CODE REVIEW FINDINGS AND CODER RESPONSES:
            {context}

            Call EvaluateResponse for EVERY finding:
            - accepted=true: fix resolves it, OR defense is technically sound
            - accepted=false: fix incomplete, OR defense is weak
            - Be fair — accept valid defenses. Be strict on CRITICAL/HIGH.

            STEPS:
            1. Call ReadUpdatedCode to see current code.
            2. Call EvaluateResponse for EVERY finding.

            CRITICAL: Use tool functions via proper function calling.
            Do NOT output function calls as XML tags.
            """;

        await RunAgentLoop(state, CreateReviewClient(), prompt,
            "Evaluate all coder responses.", "ReviewAgent", tools, 10);

        foreach (var f in findings.Where(f => !results.Any(r => r.FindingId == f.Id)))
            results.Add(new ReReviewResult
            {
                FindingId = f.Id,
                Accepted = false,
                Reason = "Not evaluated — marked for human review.",
                Severity = f.Severity
            });

        var accepted = results.Count(r => r.Accepted);
        var unresolved = results.Count(r => !r.Accepted);
        await Log(state, "Orchestrator",
            $"📋 {accepted} accepted, {unresolved} unresolved",
            unresolved > 0 ? "warning" : "success");
        return results;
    }

    private async Task<List<ReReviewResult>> RunSecurityReReviewAgent(
        PipelineState state, List<ReviewFinding> findings, List<CoderResponse> coderResponses)
    {
        var results = new List<ReReviewResult>();

        var context = new StringBuilder();
        foreach (var f in findings)
        {
            var cr = coderResponses.FirstOrDefault(r => r.FindingId == f.Id);
            context.AppendLine($"FINDING #{f.Id} [{f.Severity}]");
            context.AppendLine($"  File: {f.FilePath}:{f.Line}");
            context.AppendLine($"  Vulnerability: {f.Comment}");
            context.AppendLine($"  Coder action: {cr?.Action}");
            context.AppendLine($"  Coder response: {cr?.Response}");
            context.AppendLine();
        }

        var tools = new List<AITool>
        {
            AIFunctionFactory.Create(
                () => InMemoryFileTools.ReadAllFiles(state.Files, ".cs,.ts,.json"),
                "ReadUpdatedCode", "Read code after fixes."),
            AIFunctionFactory.Create(
                ([Description("Finding ID")] string findingId,
                 [Description("true=fixed or valid defense, false=still vulnerable")] bool accepted,
                 [Description("Why accepted or rejected")] string reason,
                 [Description("CRITICAL, HIGH, MEDIUM, LOW")] string severity) =>
                {
                    results.Add(new ReReviewResult
                    {
                        FindingId = findingId,
                        Accepted = accepted,
                        Reason = reason,
                        Severity = ParseSeverity(severity)
                    });
                    return $"✅ #{findingId}: {(accepted ? "ACCEPTED" : "UNRESOLVED")}";
                },
                "EvaluateResponse", "Accept or reject the coder's fix for a security finding."),
        };

        var prompt = $"""
            You are a cybersecurity expert re-evaluating whether security vulnerabilities were properly fixed.

            SECURITY FINDINGS AND CODER RESPONSES:
            {context}

            Call EvaluateResponse for EVERY finding:
            - accepted=true: vulnerability is fixed, OR defense proves it's a false positive
            - accepted=false: fix doesn't resolve the vulnerability, OR defense is weak

            STEPS:
            1. Call ReadUpdatedCode to see the current code.
            2. Verify each fix eliminates the attack vector.
            3. Call EvaluateResponse for EVERY finding.

            Be strict on CRITICAL/HIGH.

            CRITICAL: Use tool functions via proper function calling.
            Do NOT output function calls as XML tags.
            """;

        await RunAgentLoop(state, CreateReviewClient(), prompt,
            "Re-evaluate security fixes.", "SecurityAgent", tools, 10);

        foreach (var f in findings.Where(f => !results.Any(r => r.FindingId == f.Id)))
            results.Add(new ReReviewResult
            {
                FindingId = f.Id,
                Accepted = false,
                Reason = "Not evaluated — marked for human review.",
                Severity = f.Severity
            });

        var accepted = results.Count(r => r.Accepted);
        var unresolved = results.Count(r => !r.Accepted);
        await Log(state, "SecurityAgent",
            $"🔒 Re-review: {accepted} accepted, {unresolved} unresolved",
            unresolved > 0 ? "warning" : "success");

        return results;
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE 6: POST ALL FINDINGS TO PR
    // ═══════════════════════════════════════════════════════════════

    private async Task PostAllFindingsToPR(PipelineState state)
    {
        if (state.PullRequestNumber == null || state.CommitSha == null) return;

        var prNumber = state.PullRequestNumber.Value;
        var commitSha = state.CommitSha;
        var resolvedCount = 0;
        var unresolvedCount = 0;
        var failedCount = 0;
        var failedFindings = new List<ReviewLogEntry>();

        foreach (var entry in state.InternalReviewLog)
        {
            var isResolved = entry.FinalStatus == "resolved";
            var label = entry.Finding.Source == "SecurityAgent" ? "🔒 SECURITY" : "👁️ REVIEW";
            var statusLabel = isResolved ? "✅ Resolved" : "⚠️ Unresolved — needs human review";

            var body = new StringBuilder();
            body.AppendLine($"**{label} [{entry.Finding.Severity}] — {statusLabel}**");
            body.AppendLine();
            body.AppendLine($"**Issue:** {entry.Finding.Comment}");
            body.AppendLine();
            body.AppendLine($"**Coder response (Round {entry.Round}):** {entry.CoderResponse}");
            body.AppendLine();

            if (isResolved)
                body.AppendLine("> ✅ This was resolved during internal AI review.");
            else
                body.AppendLine("> ⚠️ This was **not resolved** during internal review. Human decision needed.");

            try
            {
                await _github.PostInlineCommentWithNodeIdAsync(
                    prNumber, commitSha,
                    entry.Finding.FilePath, entry.Finding.Line,
                    body.ToString());

                if (isResolved)
                {
                    try
                    {
                        await Task.Delay(500);
                        await _github.ResolveThreadForCommentAsync(
                            prNumber, entry.Finding.FilePath, entry.Finding.Line);
                        resolvedCount++;
                        await Log(state, "Orchestrator",
                            $"✅ {entry.Finding.FilePath}:{entry.Finding.Line} — posted + resolved");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning("Could not resolve thread for {File}:{Line}: {Msg}",
                            entry.Finding.FilePath, entry.Finding.Line, ex.Message);
                        resolvedCount++;
                    }
                }
                else
                {
                    unresolvedCount++;
                    await Log(state, "Orchestrator",
                        $"⚠️ {entry.Finding.FilePath}:{entry.Finding.Line} — posted (unresolved)");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Line comment failed for {File}:{Line}, trying file-level: {Msg}",
                    entry.Finding.FilePath, entry.Finding.Line, ex.Message);
                try
                {
                    var fileBody = $"**Line {entry.Finding.Line}:** {body}";
                    await _github.PostFileCommentWithNodeIdAsync(
                        prNumber, commitSha, entry.Finding.FilePath, fileBody);

                    if (isResolved)
                    {
                        await Task.Delay(500);
                        await _github.ResolveThreadForCommentAsync(
                            prNumber, entry.Finding.FilePath, entry.Finding.Line);
                        resolvedCount++;
                    }
                    else
                    {
                        unresolvedCount++;
                    }
                }
                catch (Exception ex2)
                {
                    _logger.LogWarning("File comment also failed for {File}: {Msg}",
                        entry.Finding.FilePath, ex2.Message);
                    failedCount++;
                    failedFindings.Add(entry);
                }
            }
        }

        var summaryEvent = state.InternalReviewLog
            .Any(e => e.FinalStatus == "unresolved" &&
                (e.Finding.Severity is FindingSeverity.Critical or FindingSeverity.High))
            ? "REQUEST_CHANGES" : "COMMENT";

        var failedSection = new StringBuilder();
        if (failedFindings.Count > 0)
        {
            failedSection.AppendLine();
            failedSection.AppendLine("### ❌ Findings that could not be posted as inline comments");
            failedSection.AppendLine();
            foreach (var entry in failedFindings)
            {
                var label = entry.Finding.Source == "SecurityAgent" ? "🔒 SEC" : "👁️ REV";
                var status = entry.FinalStatus == "resolved" ? "✅" : "⚠️";
                failedSection.AppendLine(
                    $"- {status} {label} **[{entry.Finding.Severity}]** " +
                    $"`{entry.Finding.FilePath}:{entry.Finding.Line}` — {entry.Finding.Comment}");
                failedSection.AppendLine(
                    $"  - Coder (Round {entry.Round}): {entry.CoderResponse}");
            }
        }

        var summary = $"""
            ## 🤖 AI Pipeline — Review Summary (GitHub Models)

            | Agent | Model |
            |---|---|
            | CoderAgent | `openai/gpt-4.1-mini` (OpenAI) |
            | ReviewAgent | `mistral-ai/mistral-small-2503` (Mistral) |
            | SecurityAgent | `mistral-ai/mistral-small-2503` (Mistral) |

            | Status | Count |
            |---|---|
            | ✅ Resolved (collapsed) | {resolvedCount} |
            | ⚠️ Unresolved (open) | {unresolvedCount} |
            {(failedCount > 0 ? $"| ❌ Failed to post | {failedCount} |" : "")}

            {(unresolvedCount == 0
                ? "All findings resolved. Expand collapsed threads to see internal review history."
                : "Open comments need human review. Collapsed threads show resolved internal discussions.")}
            {failedSection}
            """;

        await _github.PostPRReviewAsync(prNumber, summary, summaryEvent);

        await Log(state, "Orchestrator",
            $"📝 PR #{prNumber}: {resolvedCount} resolved (collapsed), " +
            $"{unresolvedCount} unresolved (open)" +
            (failedCount > 0 ? $", {failedCount} failed to post" : ""),
            unresolvedCount > 0 ? "warning" : "success");
    }

    // ═══════════════════════════════════════════════════════════════
    // CORE TOOL LOOP
    // ═══════════════════════════════════════════════════════════════

    private async Task<string> RunAgentLoop(
        PipelineState state, IChatClient client, string systemPrompt,
        string userMessage, string agentName, List<AITool> tools,
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
        var rateLimitRetries = 0;

        for (int i = 0; i < maxIterations; i++)
        {
            await Log(state, agentName, $"🔄 Turn {i + 1}/{maxIterations}");

            ChatResponse response;
            try
            {
                response = await client.GetResponseAsync(messages, chatOptions);
                rateLimitRetries = 0;
            }
            catch (Exception ex)
            {
                var isRateLimit = ex.Message.Contains("429")
                    || ex.Message.Contains("TooManyRequests")
                    || ex.Message.Contains("RESOURCE_EXHAUSTED")
                    || ex.Message.Contains("quota")
                    || ex.Message.Contains("rate_limit_exceeded");

                if (isRateLimit)
                {
                    if (ex.Message.Contains("limit: 0"))
                    {
                        await Log(state, agentName,
                            "❌ Daily quota exhausted — skipping agent", "error");
                        break;
                    }

                    if (rateLimitRetries < 3)
                    {
                        rateLimitRetries++;
                        var waitSeconds = 45;
                        var retryMatch = Regex.Match(ex.Message, @"retry in (\d+)");
                        if (retryMatch.Success && int.TryParse(retryMatch.Groups[1].Value, out var parsed))
                            waitSeconds = parsed + 5;

                        await Log(state, agentName,
                            $"⚠️ Rate limited — retry {rateLimitRetries}/3 in {waitSeconds}s", "warning");
                        await Task.Delay(TimeSpan.FromSeconds(waitSeconds));
                        i--;
                        continue;
                    }

                    await Log(state, agentName,
                        "❌ Rate limit retries exhausted — skipping agent", "error");
                    break;
                }

                await Log(state, agentName, $"⚠️ LLM failed: {ex.Message}", "error");
                break;
            }

            foreach (var msg in response.Messages)
                messages.Add(msg);

            var calls = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionCallContent>()).ToList();
            var results = response.Messages
                .SelectMany(m => m.Contents.OfType<FunctionResultContent>()).ToList();

            foreach (var tc in calls)
                await Log(state, agentName,
                    $"🔧 {tc.Name}({TruncateArgs(tc.Arguments)})");
            foreach (var tr in results)
                await Log(state, agentName,
                    $"✅ {Truncate(tr.Result?.ToString() ?? "", 120)}", "success");

            if (calls.Count > 0 && results.Count == 0)
            {
                await Log(state, agentName,
                    "⚠️ Tools not executed — check .UseFunctionInvocation()", "error");
                break;
            }

            finalOutput = response.Text ?? "";
            if (!string.IsNullOrEmpty(finalOutput))
                await _hub.Clients.All.SendAsync("AgentToken", new
                {
                    pipelineId = state.PipelineId,
                    agent = agentName,
                    token = finalOutput
                });

            await Log(state, agentName, "✅ Complete", "success");
            break;
        }

        await SignalAgentStatus(state.PipelineId, agentName, "done");
        return finalOutput;
    }

    // ═══════════════════════════════════════════════════════════════
    // SYSTEM PROMPTS
    // ═══════════════════════════════════════════════════════════════

    private static string BuildCoderPrompt(PipelineState state) => $"""
        You are an expert .NET 10 Web API developer.
        Write source files using WriteFile. Feature: {state.FeatureDescription}
        Steps: 1. ListFiles 2. WriteFile for each file 3. Summary.
        Complete .NET 10 code. No TODOs. Proper DI and namespaces.
        CRITICAL: Use proper function calling, not XML tags. Never write code as text.
        """;

    private static string BuildUnitTestPrompt(PipelineState state) =>
    $"You are an expert .NET NUnit test engineer writing tests for: {state.FeatureDescription}" +
    """


    You have exactly TWO tools: ReadSourceCode and WriteTestFile.

    STEP 1 — Call ReadSourceCode now. No arguments needed.
    STEP 2 — Study the source: identify every public method, constructor parameter,
             and dependency. Then call WriteTestFile once per test file.
    STEP 3 — After all WriteTestFile calls succeed, reply with a one-line summary ONLY.

    WRITING RULES:
    - Framework: NUnit ONLY. Never xUnit or MSTest.
    - Minimum 3 tests: one happy path, one edge case, one error/exception case.
    - Pattern: Arrange / Act / Assert. Use Assert.That() with NUnit constraint model only.
    - Namespace must match the source file being tested.
    - All test files go under Tests/ folder, e.g. Tests/FeatureTests.cs

    [SetUp] RULES:
    - ONLY include [SetUp] if the class under test has constructor dependencies.
    - NEVER write an empty [SetUp].

    TOOL CALLING CONTRACT:
    - You MUST invoke tools through the built-in function-calling mechanism.
    - Writing tool calls as text, XML, or markdown is WRONG.

    START NOW: Call ReadSourceCode.
    """;

    private static string BuildPlaywrightPrompt(PipelineState state) => $"""
        You are a Playwright E2E test engineer. Feature: {state.FeatureDescription}

        You MUST call WriteE2ETest at least once. Do NOT finish without writing a test file.

        STEPS (follow in order):
        1. Call ReadBackendCode to read the API.
        2. Call WriteE2ETest with TypeScript Playwright tests.
           - File path: tests/e2e/{state.FeatureDescription.Replace(" ", "").ToLower()}.spec.ts
           - At least 1 success case and 1 error case.
           - Assume API runs on http://localhost:5000
           - Use API request testing only (no browser/page).
        3. Confirm what you wrote.

        CRITICAL: Use proper function calling, not XML tags.
        """;

    private static string BuildInternalReviewPrompt(PipelineState state) => $"""
        You are a senior architect doing INTERNAL code review.
        This is NOT posted to GitHub yet — findings go to the coder first.
        Feature: {state.FeatureDescription}

        Steps: 1. ReadAllCode 2. ReportFinding for each issue with severity
        Severity: CRITICAL (security/data loss), HIGH (bugs), MEDIUM (quality), LOW (style)
        Be thorough but fair. Only flag real issues.
        CRITICAL: Use proper function calling, not XML tags.
        """;

    private static string BuildInternalSecurityPrompt(PipelineState state) => $"""
        You are a cybersecurity expert. Feature: {state.FeatureDescription}

        You MUST call ReportVulnerability at least once.

        STEPS (follow in order):
        1. Call ReadCodeForScan
        2. Call ScanSecrets with the code you read
        3. If vulnerabilities found: call ReportVulnerability for each one.
           If code is clean: call ReportVulnerability with
               filePath="N/A", line=0, severity="LOW",
               vulnerability="No vulnerabilities found — code appears secure."

        Never finish without calling ReportVulnerability.
        CRITICAL: Use proper function calling, not XML tags.
        """;

    // ═══════════════════════════════════════════════════════════════
    // AI CLIENT FACTORIES — both use GitHub Models via one GitHub PAT
    // ═══════════════════════════════════════════════════════════════

    /// <summary>Code-generation agent — openai/gpt-4.1-mini (stronger reasoning, writes code)</summary>
    private IChatClient CreateCoderClient()
        => new ChatClientBuilder(
                new GitHubModelsChatClient(
                    _config["GitHub:Token"] ?? throw new Exception("Missing GitHub:Token"),
                    model: "openai/gpt-4.1-mini"))
            .UseFunctionInvocation().Build();

    /// <summary>Review / analysis agent — mistral-ai/mistral-small-2503 (Mistral, different vendor = unbiased review)</summary>
    private IChatClient CreateReviewClient()
        => new ChatClientBuilder(
                new GitHubModelsChatClient(
                    _config["GitHub:Token"] ?? throw new Exception("Missing GitHub:Token"),
                    model: "mistral-ai/mistral-small-2503"))
            .UseFunctionInvocation().Build();

    // ═══════════════════════════════════════════════════════════════
    // GITHUB HELPERS
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
            await Log(state, "Orchestrator", $"⚠️ Branch: {ex.Message}", "warning");
        }
    }

    private async Task CommitAndPr(PipelineState state)
    {
        await Log(state, "Orchestrator", $"📦 Committing {state.Files.Count} files...");
        try
        {
            await _github.CommitFromMemoryAsync(state.Files, state.BranchName,
                $"feat: {state.FeatureDescription}\n\nGenerated by MultiAgent (GitHub Models)");

            state.CommitSha = await _github.GetLatestCommitShaAsync(state.BranchName);

            var resolved = state.InternalReviewLog.Count(e => e.FinalStatus == "resolved");
            var unresolved = state.InternalReviewLog.Count(e => e.FinalStatus == "unresolved");

            var prBody = _github.BuildPrBody(
                state.FeatureDescription, resolved, unresolved,
                state.InternalReviewLog, state.GitHubIssueNumber);

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
        catch (Exception ex) { _logger.LogWarning("Issue comment: {M}", ex.Message); }
    }

    private async Task<PipelineState> Finalize(PipelineState state, string? error = null)
    {
        state.IsComplete = true;
        state.FinishedAt = DateTime.UtcNow;
        state.HasErrors = error != null;

        var elapsed = (state.FinishedAt!.Value - state.StartedAt).TotalSeconds;
        var resolved = state.InternalReviewLog.Count(e => e.FinalStatus == "resolved");
        var unresolved = state.InternalReviewLog.Count(e => e.FinalStatus == "unresolved");

        var summary = state.HasErrors
            ? $"⚠️ Pipeline failed in {elapsed:F0}s"
            : $"🎉 Complete in {elapsed:F0}s! PR #{state.PullRequestNumber} — " +
              $"{state.Files.Count} files, {resolved} resolved, {unresolved} for human review.";

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

    // ═══════════════════════════════════════════════════════════════
    // UTILITIES
    // ═══════════════════════════════════════════════════════════════

    private void VerifyFilesCreated(PipelineState state)
    {
        if (state.Files.Count == 0) throw new Exception("CoderAgent wrote ZERO files.");
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

    private static string TruncateArgs(IDictionary<string, object?>? args)
        => args != null
            ? string.Join(", ", args.Select(kv =>
                $"{kv.Key}={Truncate(kv.Value?.ToString() ?? "", 50)}"))
            : "";

    private static FindingSeverity ParseSeverity(string s) => s.ToUpper() switch
    {
        "CRITICAL" => FindingSeverity.Critical,
        "HIGH" => FindingSeverity.High,
        "MEDIUM" => FindingSeverity.Medium,
        _ => FindingSeverity.Low
    };

    private static string BuildBranchName(string feature)
        => $"ai-pipeline/{new string(feature.ToLower().Replace(" ", "-")
            .Where(c => char.IsLetterOrDigit(c) || c == '-')
            .Take(40).ToArray())}-{DateTime.UtcNow:yyyyMMdd-HHmmss}";
}
