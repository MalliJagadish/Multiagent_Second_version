var builder = DistributedApplication.CreateBuilder(args);

builder.AddProject<Projects.MultiAgent>("multiagent");

builder.Build().Run();
