using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace MultiAgent.Services;

/// <summary>
/// All GitHub API operations for the pipeline.
/// Protocol: HTTPS REST — api.github.com
/// Auth: Personal Access Token (PAT) via Bearer header
/// </summary>
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

    /// <summary>Creates a new branch from main for this pipeline run.</summary>
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

    //// <summary>Commits all changed files in repoPath to the branch on GitHub.</summary>
    //public async Task CommitFilesAsync(string repoPath, string branchName, string commitMessage)
    //{
    //    // Ensure the repo path exists — create it if the agent somehow missed it
    //    Directory.CreateDirectory(repoPath);

    //    var files = Directory.GetFiles(repoPath, "*.*", SearchOption.AllDirectories)
    //        .Where(f => !f.Contains("\\bin\\") && !f.Contains("\\obj\\")
    //                 && !f.Contains("\\.git\\"))
    //        .ToList();

    //    if (!files.Any())
    //    {
    //        _logger.LogWarning("No files found in {RepoPath} — nothing to commit", repoPath);
    //        throw new Exception(
    //            $"No files were written to {repoPath}. " +
    //            $"The Coder agent may have failed silently. Check live logs for WriteFile calls.");
    //    }

    //    var treeItems = new List<object>();
    //    foreach (var file in files)
    //    {
    //        var content = await File.ReadAllTextAsync(file);
    //        var relativePath = Path.GetRelativePath(repoPath, file).Replace("\\", "/");

    //        var blobResp = await PostAsync($"repos/{_repoOwner}/{_repoName}/git/blobs", new
    //        {
    //            content,
    //            encoding = "utf-8"
    //        });
    //        var blobSha = blobResp.GetProperty("sha").GetString()!;

    //        treeItems.Add(new
    //        {
    //            path = relativePath,
    //            mode = "100644",
    //            type = "blob",
    //            sha = blobSha
    //        });
    //    }

    //    var branchInfo = await GetAsync($"repos/{_repoOwner}/{_repoName}/git/ref/heads/{branchName}");
    //    var commitSha = branchInfo.GetProperty("object").GetProperty("sha").GetString()!;
    //    var commitInfo = await GetAsync($"repos/{_repoOwner}/{_repoName}/git/commits/{commitSha}");
    //    var treeSha = commitInfo.GetProperty("tree").GetProperty("sha").GetString()!;

    //    var newTree = await PostAsync($"repos/{_repoOwner}/{_repoName}/git/trees", new
    //    {
    //        base_tree = treeSha,
    //        tree = treeItems
    //    });
    //    var newTreeSha = newTree.GetProperty("sha").GetString()!;

    //    var newCommit = await PostAsync($"repos/{_repoOwner}/{_repoName}/git/commits", new
    //    {
    //        message = commitMessage,
    //        tree = newTreeSha,
    //        parents = new[] { commitSha }
    //    });
    //    var newCommitSha = newCommit.GetProperty("sha").GetString()!;

    //    await PatchAsync($"repos/{_repoOwner}/{_repoName}/git/refs/heads/{branchName}", new
    //    {
    //        sha = newCommitSha
    //    });

    //    _logger.LogInformation("Committed {Count} files to {Branch}", files.Count, branchName);
    //}


    /// <summary>
    /// Commits files directly to GitHub from an in-memory dictionary.
    /// No local disk involved — creates blobs, tree, and commit via the Git Data API.
    /// </summary>
    public async Task CommitFromMemoryAsync(
        Dictionary<string, string> files, string branchName, string commitMessage)
    {
        if (files.Count == 0)
            throw new Exception("No files to commit. The Coder agent produced no output.");

        // 1. Create a blob for each file
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

        // 2. Get current branch HEAD
        var branchInfo = await GetAsync(
            $"repos/{_repoOwner}/{_repoName}/git/ref/heads/{branchName}");
        var headCommitSha = branchInfo.GetProperty("object").GetProperty("sha").GetString()!;

        // 3. Get the tree SHA of the current HEAD commit
        var commitInfo = await GetAsync(
            $"repos/{_repoOwner}/{_repoName}/git/commits/{headCommitSha}");
        var baseTreeSha = commitInfo.GetProperty("tree").GetProperty("sha").GetString()!;

        // 4. Create new tree (inherits existing files from base_tree)
        var newTree = await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/git/trees",
            new { base_tree = baseTreeSha, tree = treeItems });
        var newTreeSha = newTree.GetProperty("sha").GetString()!;

        // 5. Create commit pointing to new tree
        var newCommit = await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/git/commits",
            new
            {
                message = commitMessage,
                tree = newTreeSha,
                parents = new[] { headCommitSha }
            });
        var newCommitSha = newCommit.GetProperty("sha").GetString()!;

        // 6. Update branch ref to point to new commit
        await PatchAsync(
            $"repos/{_repoOwner}/{_repoName}/git/refs/heads/{branchName}",
            new { sha = newCommitSha });

        _logger.LogInformation(
            "Committed {Count} files to {Branch} (in-memory, no local disk)",
            files.Count, branchName);
    }

    /// <summary>Opens a Draft PR from the feature branch to main.</summary>
    public async Task<string> CreateDraftPrAsync(string branchName, string title, string body)
    {
        var pr = await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls", new
        {
            title,
            body,
            head = branchName,
            base_ = "main",
            draft = true
        });

        var prUrl = pr.GetProperty("html_url").GetString()!;
        _logger.LogInformation("Draft PR created: {Url}", prUrl);
        return prUrl;
    }

    /// <summary>
    /// Opens a Draft PR and returns both the URL and PR number.
    /// ALWAYS creates as draft=true — pipeline never auto-merges.
    /// Human must manually review inline comments and merge.
    /// </summary>
    public async Task<(string url, int number)> CreateDraftPrWithNumberAsync(
        string branchName, string title, string body)
    {
        var pr = await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls", new
        {
            title,
            body,
            head = branchName,
            base_ = "main",
            draft = true          // always draft — human reviews before merge
        });

        var url = pr.GetProperty("html_url").GetString()!;
        var number = pr.GetProperty("number").GetInt32();
        _logger.LogInformation("Draft PR #{Number} created: {Url}", number, url);
        return (url, number);
    }

    /// <summary>
    /// Posts an inline review comment on a specific file+line in a PR.
    /// commitSha must be the HEAD commit of the PR branch.
    /// </summary>
    //public async Task PostPRInlineCommentAsync(
    //    int prNumber, string commitSha, string filePath, int line, string comment)
    //{
    //    await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/comments", new
    //    {
    //        body = comment,
    //        commit_id = commitSha,
    //        path = filePath,
    //        line,
    //        side = "RIGHT"
    //    });
    //    _logger.LogInformation("Inline comment posted on PR #{PR} {File}:{Line}", prNumber, filePath, line);
    //}
    public async Task PostPRInlineCommentAsync(
    int prNumber, string commitSha, string filePath, int line, string comment)
    {
        try
        {
            // Try posting as a line-level comment first
            await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/comments", new
            {
                body = comment,
                commit_id = commitSha,
                path = filePath.TrimStart('/'),
                line,
                side = "RIGHT"
            });
            _logger.LogInformation("Inline comment on PR #{PR} {File}:{Line}", prNumber, filePath, line);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("Line comment failed ({Msg}), posting as file-level comment", ex.Message);
            try
            {
                // Fallback: post as a file-level comment (appears on the file, not a specific line)
                await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/comments", new
                {
                    body = $"**Line {line}:** {comment}",
                    commit_id = commitSha,
                    path = filePath.TrimStart('/'),
                    subject_type = "file"
                });
            }
            catch (Exception ex2)
            {
                _logger.LogWarning("File comment also failed: {Msg}, posting as general PR comment", ex2.Message);
                // Last fallback: post as a regular PR review comment
                await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/reviews", new
                {
                    body = $"**{filePath}:{line}** — {comment}",
                    @event = "COMMENT"
                });
            }
        }
    }

    /// <summary>
    /// Posts a top-level PR review (summary comment + APPROVE / REQUEST_CHANGES / COMMENT).
    /// event_: "APPROVE", "REQUEST_CHANGES", or "COMMENT"
    /// </summary>
    public async Task PostPRReviewAsync(int prNumber, string body, string reviewEvent = "COMMENT")
    {
        await PostAsync($"repos/{_repoOwner}/{_repoName}/pulls/{prNumber}/reviews", new
        {
            body,
            @event = reviewEvent   // "APPROVE" | "REQUEST_CHANGES" | "COMMENT"
        });
        _logger.LogInformation("PR review posted on #{PR}: {Event}", prNumber, reviewEvent);
    }

    /// <summary>Gets the latest commit SHA on a branch — needed for inline comments.</summary>
    public async Task<string> GetLatestCommitShaAsync(string branchName)
    {
        var branchInfo = await GetAsync($"repos/{_repoOwner}/{_repoName}/git/ref/heads/{branchName}");
        return branchInfo.GetProperty("object").GetProperty("sha").GetString()!;
    }

    /// <summary>Posts a comment to a GitHub Issue.</summary>
    public async Task PostIssueCommentAsync(string issueNumber, string markdown)
        => await PostAsync(
            $"repos/{_repoOwner}/{_repoName}/issues/{issueNumber}/comments",
            new { body = markdown });

    /// <summary>Validates GitHub webhook HMAC-SHA256 signature.</summary>
    public bool ValidateWebhookSignature(string payload, string signatureHeader, string secret)
    {
        if (string.IsNullOrEmpty(signatureHeader)) return false;

        using var hmac = new System.Security.Cryptography.HMACSHA256(
            Encoding.UTF8.GetBytes(secret));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(payload));
        var expected = "sha256=" + Convert.ToHexString(hash).ToLower();

        return signatureHeader.ToLower() == expected;
    }

    /// <summary>Builds the markdown body for the Draft PR.</summary>
    public string BuildPrBody(string feature, string tests, string review,
        string security, string? issueNumber)
    {
        var safe = (string s) => s[..Math.Min(600, s.Length)];
        return $"""
            ## 🤖 AI Pipeline — Auto-generated PR

            **Feature:** {feature}
            {(issueNumber != null ? $"**Closes:** #{issueNumber}" : "")}

            ---

            ## Pipeline Results

            | Agent | Model | Status |
            |---|---|---|
            | 🧑‍💻 Coder | Groq Llama 3.3 70B | ✅ Done |
            | 🧪 Unit Tests | Groq Llama 3.3 70B | ✅ Done |
            | 🎭 Playwright | Gemini 2.0 Flash | ✅ Done |
            | 👁️ Code Review | GitHub GPT-4.1 | ✅ Done |
            | 🔒 Security | Gemini 2.0 Flash | ✅ Done |

            ---

            ## 🧪 Unit Test Results
            ```
            {safe(tests)}
            ```

            ## 👁️ Code Review
            {safe(review)}

            ## 🔒 Security Report
            {safe(security)}

            ---

            > ⚠️ AI-generated. Human review required before merging.
            """;
    }

    // ── HTTP helpers ──────────────────────────────────────────────

    private async Task<JsonElement> GetAsync(string path)
    {
        var resp = await _http.GetAsync($"https://api.github.com/{path}");
        var json = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
            throw new Exception($"GitHub GET {path} failed: {json}");
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
            throw new Exception($"GitHub POST {path} failed: {respJson}");
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
}