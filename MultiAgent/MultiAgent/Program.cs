//using MultiAgent.Hubs;
//using MultiAgent.Services;
//using MultiAgent.Workflows;

//var builder = WebApplication.CreateBuilder(args);

//// ── Services ──────────────────────────────────────────────
//builder.Services.AddControllers();
//builder.Services.AddSignalR();
//builder.Services.AddEndpointsApiExplorer();
//builder.Services.AddSwaggerGen();

//// Register pipeline services
//builder.Services.AddSingleton<PipelineHistoryService>();
//builder.Services.AddSingleton<GitHubService>();
//builder.Services.AddTransient<MultiAgentWorkflow>();

//// CORS — allow Vue dashboard to connect
//builder.Services.AddCors(options =>
//{
//    options.AddDefaultPolicy(policy =>
//        policy.AllowAnyOrigin().AllowAnyMethod().AllowAnyHeader());

//    options.AddPolicy("SignalR", policy =>
//        policy.WithOrigins("http://localhost:5173", "http://localhost:3000")
//              .AllowAnyMethod().AllowAnyHeader().AllowCredentials());
//});

//var app = builder.Build();

//// ── Middleware ─────────────────────────────────────────────
//if (app.Environment.IsDevelopment())
//{
//    app.UseSwagger();
//    app.UseSwaggerUI();
//}

//app.UseCors();
//app.MapControllers();
//app.MapHub<PipelineHub>("/pipelinehub");

//app.Run();


using MultiAgent.Hubs;
using MultiAgent.Services;
using MultiAgent.Workflows;

var builder = WebApplication.CreateBuilder(args);

// ── Services ──────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddSignalR();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register pipeline services
builder.Services.AddSingleton<PipelineHistoryService>();
builder.Services.AddSingleton<GitHubService>();
builder.Services.AddTransient<MultiAgentWorkflow>();
builder.Services.AddSingleton<GeminiThrottler>();

// CORS — SignalR requires credentials mode, which forbids wildcard origin.
// Use explicit origins + AllowCredentials for everything.
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

// ── Middleware ─────────────────────────────────────────────
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.MapControllers();
app.MapHub<PipelineHub>("/pipelinehub").RequireCors();

app.Run();