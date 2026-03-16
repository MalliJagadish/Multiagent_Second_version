using MultiAgent.Models;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Services;

public class GitHubService
{
    private readonly HttpClient _http;
    private readonly string _token;
    private readonly string _repoOwner;
    private readonly string _repoName;
    private readonly ILogger<GitHubService> _logger;

    public GitHubService(IConfiguration config, ILogger<GitHubService> logger)
    {
        _logger = logger;
        _token = config["GitHub:Token"] ?? throw new Exception("Missing GitHub:Token");
        _repoOwner = config["GitHub:RepoOwner"] ?? throw new Exception("Missing GitHub:RepoOwner");
        _repoName = config["GitHub:RepoName"] ?? throw new Exception("Missing GitHub:RepoName");

        _http = new HttpClient();
        _http.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _token);
        _http.DefaultRequestHeaders.Add("User-Agent", "DevPipeline-Bot");
        _http.DefaultRequestHeaders.Add("Accept", "application/vnd.github+json");
        _http.DefaultRequestHeaders.Add("X-GitHub-Api-Version", "2022-11-28");
    }

    // ═══════════════════════════════════════════════════════════════
    // BRANCH
    // ═══════════════════════════════════════════════════════════════

    public async Task<string> CreateBranchAsync(string branchName)
    {
        var mainRef = await GetAsync($"repos/{_repoOwner}/{_repoName}/git/ref/heads/main");
        var sha = mainRef.GetProperty("object").GetProperty("sha").GetString()!;

        await PostAsync($"repos/{_repoOwner}/{_repoName}/git/refs", new
        {
            @ref = $"refs/heads/{branchName}",
            sha
        });

        _logger.LogInformation("Created branch: {Branch}", branchName);
        return branchName;
    }

    // ═══════════════════════════════════════════════════════════════
    // COMMIT FROM MEMORY (no local disk)
    // ═══════════════════════════════════════════════════════════════

    public async Task CommitFromMemoryAsync(
        Dictionary<string, string> files, string branchName, string commitMessage)
    {
        if (files.Count == 0)
            throw new Exception("No files to commit.");

        var treeItems = new List<object>();
        foreach (var (relativePath, content) in files)
        {
            var blobResp = await PostAsync(
                $"repos/{_repoOwner}/{_repoName}/git/blobs",
                new { content, encoding = "utf-8" });
            var blobSha = blobResp.GetProperty("sha").GetString()!;

            treeItems.Add(new
            {
                path = relativePath.TrimStart('/').Replace("\\", "/"),
                mode = "100644",
                type = "blob",
                sha = blobSha
            });
        }

        var branchInfo = await GetAsync(
            $"repos/{_repoOwner}/{_repoName}/git/ref/heads/{branchName}");
        var headSha = branchInfo.GetProperty("object").GetProperty("sha").GetString()!;

        var commitInfo = await GetAsync(
            $"repos/{_repoOwner}/{_repoName}/git/commits/{headSha}");
        var baseTreeSha = commitInfo.GetProperty("tree").GetProperty("sha").GetString()!;

        var newTree = await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/git/trees",
            new { base_tree = baseTreeSha, tree = treeItems });
        var newTreeSha = newTree.GetProperty("sha").GetString()!;

        var newCommit = await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/git/commits",
            new { message = commitMessage, tree = newTreeSha, parents = new[] { headSha } });
        var newCommitSha = newCommit.GetProperty("sha").GetString()!;

        await PatchAsync(
            $"repos/{_repoOwner}/{_repoName}/git/refs/heads/{branchName}",
            new { sha = newCommitSha });

        _logger.LogInformation("Committed {Count} files to {Branch} (in-memory)", files.Count, branchName);
    }

    // ═══════════════════════════════════════════════════════════════
    // PULL REQUESTS
    // ═══════════════════════════════════════════════════════════════

    public async Task<(string url, int number)> CreateDraftPrWithNumberAsync(
        string branchName, string title, string body)
    {
        var pr = await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls", new
        {
            title,
            body,
            head = branchName,
            base_ = "main",
            draft = true
        });

        var url = pr.GetProperty("html_url").GetString()!;
        var number = pr.GetProperty("number").GetInt32();
        _logger.LogInformation("Draft PR #{Number}: {Url}", number, url);
        return (url, number);
    }

    public async Task<string> GetLatestCommitShaAsync(string branchName)
    {
        var info = await GetAsync(
            $"repos/{_repoOwner}/{_repoName}/git/ref/heads/{branchName}");
        return info.GetProperty("object").GetProperty("sha").GetString()!;
    }

    // ═══════════════════════════════════════════════════════════════
    // PR COMMENTS — post inline and return the node_id for resolving
    // ═══════════════════════════════════════════════════════════════

    /// <summary>
    /// Posts an inline review comment and returns (commentId, nodeId).
    /// nodeId is needed by the GraphQL API to resolve the thread.
    /// </summary>
    public async Task<(int commentId, string nodeId)> PostInlineCommentWithNodeIdAsync(
        int prNumber, string commitSha, string filePath, int line, string comment)
    {
        var result = await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/comments",
            new
            {
                body = comment,
                commit_id = commitSha,
                path = filePath,
                line,
                side = "RIGHT"
            });

        var commentId = result.GetProperty("id").GetInt32();
        var nodeId = result.GetProperty("node_id").GetString()!;
        _logger.LogInformation("Comment {Id} on PR #{PR} {File}:{Line}", commentId, prNumber, filePath, line);
        return (commentId, nodeId);
    }

    /// <summary>
    /// Fallback: post as a file-level comment when the line doesn't exist in the diff.
    /// </summary>
    public async Task<(int commentId, string nodeId)> PostFileCommentWithNodeIdAsync(
        int prNumber, string commitSha, string filePath, string comment)
    {
        var result = await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/comments",
            new
            {
                body = comment,
                commit_id = commitSha,
                path = filePath,
                subject_type = "file"
            });

        var commentId = result.GetProperty("id").GetInt32();
        var nodeId = result.GetProperty("node_id").GetString()!;
        return (commentId, nodeId);
    }

    /// <summary>
    /// Resolves a PR review thread using the GitHub GraphQL API.
    ///
    /// The REST API doesn't support resolving threads — only GraphQL can do it.
    /// We need the thread's node_id. A review comment's node_id points to the
    /// PullRequestReviewComment, but we need the PullRequestReviewThread.
    ///
    /// Strategy: query the comment's node_id to get its parent thread, then resolve.
    /// </summary>
    public async Task ResolveThreadByCommentNodeIdAsync(string commentNodeId)
    {
        // Step 1: Find the thread node_id from the comment node_id
        var findThreadQuery = new
        {
            query = @"
                query($commentId: ID!) {
                    node(id: $commentId) {
                        ... on PullRequestReviewComment {
                            pullRequestReview {
                                pullRequest {
                                    reviewThreads(last: 100) {
                                        nodes {
                                            id
                                            isResolved
                                            comments(first: 1) {
                                                nodes { id }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }",
            variables = new { commentId = commentNodeId }
        };

        var findResult = await PostGraphqlAsync(findThreadQuery);

        // Parse to find the thread that contains our comment
        string? threadNodeId = null;
        try
        {
            var threads = findResult
                .GetProperty("data")
                .GetProperty("node")
                .GetProperty("pullRequestReview")
                .GetProperty("pullRequest")
                .GetProperty("reviewThreads")
                .GetProperty("nodes");

            foreach (var thread in threads.EnumerateArray())
            {
                var comments = thread.GetProperty("comments").GetProperty("nodes");
                foreach (var c in comments.EnumerateArray())
                {
                    if (c.GetProperty("id").GetString() == commentNodeId)
                    {
                        threadNodeId = thread.GetProperty("id").GetString();
                        break;
                    }
                }
                if (threadNodeId != null) break;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Could not find thread for comment {NodeId}: {Msg}",
                commentNodeId, ex.Message);
        }

        // Fallback: if we can't find via nested query, try direct thread resolution
        // by posting the comment as part of a review and resolving that thread
        if (threadNodeId == null)
        {
            _logger.LogWarning(
                "Thread not found for {NodeId} — trying alternative resolution", commentNodeId);
            await ResolveThreadAlternativeAsync(commentNodeId);
            return;
        }

        // Step 2: Resolve the thread
        var resolveQuery = new
        {
            query = @"
                mutation($threadId: ID!) {
                    resolveReviewThread(input: { threadId: $threadId }) {
                        thread { isResolved }
                    }
                }",
            variables = new { threadId = threadNodeId }
        };

        await PostGraphqlAsync(resolveQuery);
        _logger.LogInformation("Thread {ThreadId} resolved", threadNodeId);
    }

    /// <summary>
    /// Alternative approach: list all threads on the PR and match by file+line.
    /// Used when the nested GraphQL query doesn't find the thread.
    /// </summary>
    public async Task ResolveThreadForCommentAsync(int prNumber, string filePath, int line)
    {
        var prNodeQuery = new
        {
            query = @"
                query($owner: String!, $repo: String!, $prNumber: Int!) {
                    repository(owner: $owner, name: $repo) {
                        pullRequest(number: $prNumber) {
                            reviewThreads(last: 100) {
                                nodes {
                                    id
                                    isResolved
                                    path
                                    line
                                }
                            }
                        }
                    }
                }",
            variables = new { owner = _repoOwner, repo = _repoName, prNumber }
        };

        var result = await PostGraphqlAsync(prNodeQuery);
        try
        {
            var threads = result
                .GetProperty("data")
                .GetProperty("repository")
                .GetProperty("pullRequest")
                .GetProperty("reviewThreads")
                .GetProperty("nodes");

            foreach (var thread in threads.EnumerateArray())
            {
                var threadPath = thread.GetProperty("path").GetString() ?? "";
                var threadLine = thread.TryGetProperty("line", out var l) ? l.GetInt32() : 0;
                var isResolved = thread.GetProperty("isResolved").GetBoolean();

                if (threadPath == filePath && threadLine == line && !isResolved)
                {
                    var threadId = thread.GetProperty("id").GetString()!;
                    var resolveQuery = new
                    {
                        query = @"
                            mutation($threadId: ID!) {
                                resolveReviewThread(input: { threadId: $threadId }) {
                                    thread { isResolved }
                                }
                            }",
                        variables = new { threadId }
                    };
                    await PostGraphqlAsync(resolveQuery);
                    _logger.LogInformation("Resolved thread on {File}:{Line}", filePath, line);
                    return;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Failed to resolve thread for {File}:{Line}: {Msg}",
                filePath, line, ex.Message);
        }
    }

    private async Task ResolveThreadAlternativeAsync(string commentNodeId)
    {
        // Directly try to resolve using the comment's node_id as if it were a thread
        // This works in some GitHub API versions
        var resolveQuery = new
        {
            query = @"
                mutation($threadId: ID!) {
                    resolveReviewThread(input: { threadId: $threadId }) {
                        thread { isResolved }
                    }
                }",
            variables = new { threadId = commentNodeId }
        };

        try
        {
            await PostGraphqlAsync(resolveQuery);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Alternative resolve failed for {Id}: {Msg}",
                commentNodeId, ex.Message);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // PR REVIEWS
    // ═══════════════════════════════════════════════════════════════

    //public async Task PostPRReviewAsync(int prNumber, string body, string reviewEvent = "COMMENT")
    //{
    //    await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/reviews", new
    //    {
    //        body,
    //        @event = reviewEvent
    //    });
    //    _logger.LogInformation("PR review on #{PR}: {Event}", prNumber, reviewEvent);
    //}

    public async Task PostPRReviewAsync(int prNumber, string body, string reviewEvent = "COMMENT")
    {
        if (reviewEvent == "REQUEST_CHANGES" || reviewEvent == "APPROVE")
            reviewEvent = "COMMENT";

        await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/reviews", new
        {
            body,
            @event = reviewEvent
        });
        _logger.LogInformation("PR review on #{PR}: {Event}", prNumber, reviewEvent);
    }

    // ═══════════════════════════════════════════════════════════════
    // ISSUES
    // ═══════════════════════════════════════════════════════════════

    public async Task PostIssueCommentAsync(string issueNumber, string markdown)
        => await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/issues/{issueNumber}/comments",
            new { body = markdown });

    // ═══════════════════════════════════════════════════════════════
    // PR BODY BUILDER
    // ═══════════════════════════════════════════════════════════════

    public string BuildPrBody(string feature, int resolved, int unresolved,
        List<ReviewLogEntry> reviewLog, string? issueNumber)
    {
        var logTable = new StringBuilder();
        logTable.AppendLine("| Status | Round | Agent | File | Severity | Detail |");
        logTable.AppendLine("|---|---|---|---|---|---|");
        foreach (var e in reviewLog)
        {
            var icon = e.FinalStatus == "resolved" ? "✅" : "⚠️";
            var detail = e.FinalStatus == "resolved"
                ? $"Resolved: {Truncate(e.CoderResponse, 80)}"
                : $"Needs human review";
            logTable.AppendLine(
                $"| {icon} | {e.Round} | {e.Finding.Source} | " +
                $"`{e.Finding.FilePath}:{e.Finding.Line}` | " +
                $"{e.Finding.Severity} | {detail} |");
        }

        return $"""
            ## 🤖 AI Pipeline — Auto-generated PR

            **Feature:** {feature}
            {(issueNumber != null ? $"**Closes:** #{issueNumber}" : "")}

            ---

            ## Internal Review Summary

            | Metric | Count |
            |---|---|
            | ✅ Resolved internally | {resolved} |
            | ⚠️ Needs human review | {unresolved} |

            {(unresolved == 0
                ? "> ✅ All findings were resolved during internal AI review. Resolved comments are collapsed in Files Changed."
                : "> ⚠️ Unresolved items are open as inline comments. Resolved items are collapsed.")}

            ---

            <details>
            <summary>📋 Full internal review log ({resolved + unresolved} findings)</summary>

            {logTable}

            </details>

            ---

            > 🤖 AI-generated. Internal review completed. Human review required before merging.
            """;
    }

    // ═══════════════════════════════════════════════════════════════
    // WEBHOOK SIGNATURE
    // ═══════════════════════════════════════════════════════════════

    public bool ValidateWebhookSignature(string payload, string signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;
        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = "sha256=" + Convert.ToHexString(hash).ToLower();
        return signatureHeader.ToLower() == expected;
    }

    // ═══════════════════════════════════════════════════════════════
    // HTTP HELPERS
    // ═══════════════════════════════════════════════════════════════

    private async Task<JsonElement> GetAsync(string path)
    {
        var resp = await _http.GetAsync($"https://api.github.com/{path}");
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub GET {path}: {json}");
        return JsonDocument.Parse(json).RootElement;
    }

    private async Task<JsonElement> PostAsync(string path, object body)
    {
        var json = JsonSerializer.Serialize(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase })
            .Replace("\"base_\"", "\"base\"");

        var resp = await _http.PostAsync(
            $"https://api.github.com/{path}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var respJson = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub POST {path}: {respJson}");
        return JsonDocument.Parse(respJson).RootElement;
    }

    private async Task PatchAsync(string path, object body)
    {
        var json = JsonSerializer.Serialize(body,
            new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
        var resp = await _http.PatchAsync(
            $"https://api.github.com/{path}",
            new StringContent(json, Encoding.UTF8, "application/json"));
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub PATCH {path} failed");
    }

    private async Task<JsonElement> PostGraphqlAsync(object body)
    {
        var json = JsonSerializer.Serialize(body);
        var resp = await _http.PostAsync(
            "https://api.github.com/graphql",
            new StringContent(json, Encoding.UTF8, "application/json"));
        var respJson = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub GraphQL: {respJson}");
        return JsonDocument.Parse(respJson).RootElement;
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max] + "...";
}