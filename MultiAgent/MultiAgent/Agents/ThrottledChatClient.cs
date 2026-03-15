using Microsoft.Extensions.AI;
using MultiAgent.Services;
using System.Runtime.CompilerServices;

namespace MultiAgent.Agents;

/// <summary>
/// Wraps any IChatClient with a throttler that rate-limits calls
/// before they reach the underlying client.
/// </summary>
public class ThrottledChatClient : IChatClient
{
    private readonly IChatClient _inner;
    private readonly GeminiThrottler _throttler;

    public ThrottledChatClient(IChatClient inner, GeminiThrottler throttler)
    {
        _inner = inner;
        _throttler = throttler;
    }

    public async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken ct = default)
    {
        await _throttler.WaitIfNeededAsync(ct);
        return await _inner.GetResponseAsync(messages, options, ct);
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await _throttler.WaitIfNeededAsync(ct);
        await foreach (var update in _inner.GetStreamingResponseAsync(messages, options, ct))
            yield return update;
    }

    public object? GetService(Type serviceType, object? serviceKey = null)
        => _inner.GetService(serviceType, serviceKey);

    public void Dispose() => _inner.Dispose();
}