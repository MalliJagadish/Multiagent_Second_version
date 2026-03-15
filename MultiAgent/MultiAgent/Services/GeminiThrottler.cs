namespace MultiAgent.Services;

/// <summary>
/// Ensures Gemini API calls stay within 15 RPM free tier limit.
/// Shared across all agents in a pipeline run.
/// </summary>
public class GeminiThrottler
{
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly Queue<DateTime> _callTimes = new();
    private const int MaxRequestsPerMinute = 12; // stay under 15 RPM with buffer
    private const int WindowSeconds = 60;

    public async Task WaitIfNeededAsync(CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            var now = DateTime.UtcNow;

            // Remove calls older than 60 seconds
            while (_callTimes.Count > 0 &&
                   (now - _callTimes.Peek()).TotalSeconds > WindowSeconds)
                _callTimes.Dequeue();

            // If at limit, wait until oldest call falls out of window
            if (_callTimes.Count >= MaxRequestsPerMinute)
            {
                var oldest = _callTimes.Peek();
                var waitMs = (int)(WindowSeconds * 1000 - (now - oldest).TotalMilliseconds) + 500;
                if (waitMs > 0)
                    await Task.Delay(waitMs, ct);

                // Re-clean after wait
                now = DateTime.UtcNow;
                while (_callTimes.Count > 0 &&
                       (now - _callTimes.Peek()).TotalSeconds > WindowSeconds)
                    _callTimes.Dequeue();
            }

            _callTimes.Enqueue(DateTime.UtcNow);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}