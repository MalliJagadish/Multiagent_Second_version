namespace MultiAgent.Models;

/// <summary>
/// Incoming request to start a pipeline run.
/// </summary>
public class PipelineRequest
{
    public string FeatureDescription { get; set; } = "";
    public string? GitHubIssueNumber { get; set; }
    public string? GitHubRepoFullName { get; set; }
}

/// <summary>
/// Mutable state carried through the entire pipeline.
/// </summary>
public class PipelineState
{
    public string PipelineId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FeatureDescription { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string BranchName { get; set; } = "";
    public string? GitHubIssueNumber { get; set; }
    public string? GitHubRepoFullName { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool HasErrors { get; set; }

    // GitHub PR state
    public string? PullRequestUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? CommitSha { get; set; }

    // Agent outputs
    public string GeneratedCode { get; set; } = "";
    public string UnitTestResults { get; set; } = "";
    public string PlaywrightResults { get; set; } = "";
    public string ReviewFeedback { get; set; } = "";
    public string SecurityReport { get; set; } = "";

    // In-memory file store — replaces local disk writes
    public Dictionary<string, string> Files { get; set; } = new();
}

/// <summary>
/// Lightweight record stored in PipelineHistoryService.
/// </summary>

public class PipelineRun
{
    public string PipelineId { get; set; } = "";
    public string FeatureDescription { get; set; } = "";
    public string Status { get; set; } = "pending";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public bool HasErrors { get; set; }
    public string? PullRequestUrl { get; set; }
    public string? GitHubIssueNumber { get; set; }
}