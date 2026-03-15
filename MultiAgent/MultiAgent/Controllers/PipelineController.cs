using Microsoft.AspNetCore.Mvc;
using MultiAgent.Models;
using MultiAgent.Services;
using MultiAgent.Workflows;

namespace MultiAgent.Controllers;

[ApiController]
[Route("api/[controller]")]
public class PipelineController : ControllerBase
{
    private readonly MultiAgentWorkflow _workflow;
    private readonly PipelineHistoryService _history;

    public PipelineController(MultiAgentWorkflow workflow, PipelineHistoryService history)
    {
        _workflow = workflow;
        _history = history;
    }

    [HttpPost("run")]
    public IActionResult Run([FromBody] PipelineRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.FeatureDescription))
            return BadRequest(new { error = "FeatureDescription is required." });

        // Fire-and-forget — progress via SignalR WebSocket
        _ = Task.Run(() => _workflow.RunAsync(request));

        return Accepted(new
        {
            message = "Pipeline started. Watch /pipelinehub for live updates.",
            mode = "in-memory (no local disk)"
        });
    }

    [HttpGet("history")]
    public IActionResult History() => Ok(_history.GetAll());

    [HttpGet("{id}")]
    public IActionResult Get(string id)
    {
        var run = _history.Get(id);
        return run != null ? Ok(run) : NotFound();
    }
}