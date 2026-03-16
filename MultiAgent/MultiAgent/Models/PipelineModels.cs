namespace MultiAgent.Models;

public class PipelineRequest
{
    public string FeatureDescription { get; set; } = "";
    public string? GitHubIssueNumber { get; set; }
    public string? GitHubRepoFullName { get; set; }
}

public class PipelineState
{
    public string PipelineId { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string FeatureDescription { get; set; } = "";
    public string BranchName { get; set; } = "";
    public string? GitHubIssueNumber { get; set; }
    public string? GitHubRepoFullName { get; set; }

    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public bool IsComplete { get; set; }
    public bool HasErrors { get; set; }

    public string? PullRequestUrl { get; set; }
    public int? PullRequestNumber { get; set; }
    public string? CommitSha { get; set; }

    public string GeneratedCode { get; set; } = "";
    public string UnitTestResults { get; set; } = "";
    public string PlaywrightResults { get; set; } = "";
    public string ReviewFeedback { get; set; } = "";
    public string SecurityReport { get; set; } = "";

    // In-memory file store
    public Dictionary<string, string> Files { get; set; } = new();

    // Internal review loop audit trail
    public List<ReviewLogEntry> InternalReviewLog { get; set; } = new();
}

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