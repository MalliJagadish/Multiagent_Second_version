using MultiAgent.Hubs;
using MultiAgent.Services;
using MultiAgent.Workflows;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddSingleton<PipelineHistoryService>();
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddTransient<MultiAgentWorkflow>();

// CORS — SignalR requires credentials mode, which forbids wildcard origin.
builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.WithOrigins("http://localhost:5173", "http://localhost:3000", "https://localhost:5173")
              .AllowAnyMethod()
              .AllowAnyHeader()
              .AllowCredentials());
});

var app = builder.Build();
app.UseHttpsRedirection();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();
app.MapHub<PipelineHub>("/pipelinehub").RequireCors();

app.Run();
