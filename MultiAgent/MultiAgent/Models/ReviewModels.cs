namespace MultiAgent.Models;

public enum FindingSeverity { Critical, High, Medium, Low }
public enum ResponseAction { Fix, Defend }

public class ReviewFinding
{
    public string Id { get; set; } = "";
    public string Source { get; set; } = "";
    public string FilePath { get; set; } = "";
    public int Line { get; set; }
    public string Comment { get; set; } = "";
    public FindingSeverity Severity { get; set; }
}

public class CoderResponse
{
    public string FindingId { get; set; } = "";
    public ResponseAction Action { get; set; }
    public string Response { get; set; } = "";
}

public class ReReviewResult
{
    public string FindingId { get; set; } = "";
    public bool Accepted { get; set; }
    public string Reason { get; set; } = "";
    public FindingSeverity Severity { get; set; }
}

public class ReviewLogEntry
{
    public int Round { get; set; }
    public ReviewFinding Finding { get; set; } = new();
    public string CoderResponse { get; set; } = "";
    public string FinalStatus { get; set; } = "unresolved";
}