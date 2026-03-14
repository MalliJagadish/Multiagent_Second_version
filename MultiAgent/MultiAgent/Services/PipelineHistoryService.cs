using DevPipeline.Models;

namespace DevPipeline.Services;

/// <summary>
/// In-memory store of all pipeline runs this session.
/// Thread-safe — multiple pipelines can run concurrently.
/// Future: replace with SQLite or PostgreSQL for persistence.
/// </summary>
public class PipelineHistoryService
{
    private readonly List<PipelineRun> _runs = new();
    private readonly object _lock = new();

    public void Add(PipelineRun run)
    {
        lock (_lock) _runs.Add(run);
    }

    public void Update(string pipelineId, Action<PipelineRun> update)
    {
        lock (_lock)
        {
            var run = _runs.FirstOrDefault(r => r.PipelineId == pipelineId);
            if (run != null) update(run);
        }
    }

    public List<PipelineRun> GetAll()
    {
        lock (_lock) return _runs.OrderByDescending(r => r.StartedAt).ToList();
    }

    public PipelineRun? Get(string pipelineId)
    {
        lock (_lock) return _runs.FirstOrDefault(r => r.PipelineId == pipelineId);
    }
}