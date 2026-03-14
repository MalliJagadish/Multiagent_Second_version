namespace DevPipeline.Models;

// ── Inbound request from dashboard or webhook ─────────────────────
public class PipelineRequest
{
    public string FeatureDescription { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string? GitHubIssueNumber { get; set; }
    public string? GitHubRepoFullName { get; set; }
}

// ── Agent status ──────────────────────────────────────────────────
public enum AgentStatus { Waiting, Running, Done, Failed }

// ── Per-agent result ──────────────────────────────────────────────
public class AgentResult
{
    public string AgentName { get; set; } = "";
    public string AiProvider { get; set; } = "";
    public string AiModel { get; set; } = "";
    public AgentStatus Status { get; set; }
    public string Output { get; set; } = "";
    public List<string> FilesChanged { get; set; } = new();
    public string Error { get; set; } = "";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
}

// ── Live log line streamed to Vue via SignalR ─────────────────────
public class PipelineLog
{
    public string PipelineId { get; set; } = "";
    public string Agent { get; set; } = "";
    public string Message { get; set; } = "";
    public string Level { get; set; } = "info"; // info|success|error|warning
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

// ── Full pipeline state — shared context flowing through agents ───
public class PipelineState
{
    public string PipelineId { get; set; } = Guid.NewGuid().ToString();
    public string FeatureDescription { get; set; } = "";
    public string RepoPath { get; set; } = "";
    public string BranchName { get; set; } = "";
    public string? GitHubIssueNumber { get; set; }
    public string? GitHubRepoFullName { get; set; }

    // Output captured from each agent — passed to next agent as context
    public string GeneratedCode { get; set; } = "";
    public string UnitTestResults { get; set; } = "";
    public string PlaywrightResults { get; set; } = "";
    public string ReviewFeedback { get; set; } = "";
    public string SecurityReport { get; set; } = "";

    public List<AgentResult> AgentResults { get; set; } = new();
    public bool IsComplete { get; set; }
    public bool HasErrors { get; set; }
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string? PullRequestUrl { get; set; }
}

// ── Pipeline run summary for history tab ─────────────────────────
public class PipelineRun
{
    public string PipelineId { get; set; } = "";
    public string FeatureDescription { get; set; } = "";
    public string Status { get; set; } = "running";
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public string? PullRequestUrl { get; set; }
    public bool HasErrors { get; set; }
    public string? GitHubIssueNumber { get; set; }
}