//var builder = WebApplication.CreateBuilder(args);

//builder.AddServiceDefaults();

//// Add services to the container.

//builder.Services.AddControllers();
//// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi
//builder.Services.AddOpenApi();

//var app = builder.Build();

//app.MapDefaultEndpoints();

//// map root to swagger so visiting "/" won't return 404
//app.MapGet("/", () => Results.Redirect("/swagger"));

//// Configure the HTTP request pipeline.
//if (app.Environment.IsDevelopment())
//{
//    app.MapOpenApi();
//}

//app.UseHttpsRedirection();

//app.UseAuthorization();

//app.MapControllers();

//app.Run();


using DevPipeline.Hubs;
using DevPipeline.Services;
using DevPipeline.Workflows;

var builder = WebApplication.CreateBuilder(args);

// ── Aspire — handles OpenTelemetry, health checks, logging ───────
// This one line replaces all manual OpenTelemetry setup.
// Aspire Dashboard shows every agent call, LLM request, tool invocation.
builder.AddServiceDefaults();

// ── CORS — allow Vue dashboard ────────────────────────────────────
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins("https://localhost:5173", "http://localhost:5173")
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

// ── Controllers + SignalR ─────────────────────────────────────────
// SignalR is built into .NET 10 — no extra NuGet package needed
builder.Services.AddControllers();
builder.Services.AddSignalR();

// ── Services ──────────────────────────────────────────────────────
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddSingleton<PipelineHistoryService>();

// DevPipelineWorkflow is Scoped — each pipeline run gets a fresh instance
// Prevents state bleeding between concurrent runs
builder.Services.AddScoped<DevPipelineWorkflow>();

// ─────────────────────────────────────────────────────────────────
var app = builder.Build();

app.UseCors();
app.UseHttpsRedirection();
app.MapControllers();
app.MapHub<PipelineHub>("/pipelinehub");

// Aspire health endpoints: /health and /alive
app.MapDefaultEndpoints();

Console.WriteLine("""

  ╔══════════════════════════════════════════════════════════╗
  ║    DevPipeline V4 — Microsoft Agent Framework rc4 🚀     ║
  ╠══════════════════════════════════════════════════════════╣
  ║  Aspire Dashboard → http://localhost:18888               ║
  ║  API              → https://localhost:5000               ║
  ║  SignalR Hub      → https://localhost:5000/pipelinehub   ║
  ║  Webhook          → https://localhost:5000/api/webhook/github ║
  ║  Health           → https://localhost:5000/api/pipeline/health ║
  ╠══════════════════════════════════════════════════════════╣
  ║  A2A Pipeline:                                           ║
  ║  Coder (Claude) → UnitTest (GPT-4o) →                   ║
  ║  Playwright (Gemini) → Review (GPT-4o) →                ║
  ║  Security (Gemini)                                       ║
  ╚══════════════════════════════════════════════════════════╝

""");

app.Run();