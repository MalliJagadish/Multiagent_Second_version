using Microsoft.AspNetCore.SignalR;

namespace DevPipeline.Hubs;

/// <summary>
/// SignalR Hub — real-time WebSocket channel between API and Vue dashboard.
///
/// Events broadcast to Vue:
///   "PipelineLog"      → { pipelineId, agent, message, level, timestamp }
///   "AgentToken"       → { pipelineId, agent, token }   ← individual LLM tokens
///   "AgentStatus"      → { pipelineId, agentName, status }
///   "PipelineComplete" → { pipelineId, hasErrors, prUrl, summary }
///
/// Vue connects via:
///   new signalR.HubConnectionBuilder().withUrl('/pipelinehub').build()
/// Protocol: WebSocket (wss://) — falls back to SSE, then long polling
/// </summary>
public class PipelineHub : Hub
{
    public override async Task OnConnectedAsync()
    {
        await Clients.Caller.SendAsync("Connected",
            new { message = "Connected to DevPipeline hub ⚡" });
        await base.OnConnectedAsync();
    }
}