# MultiAgent — Mult-agent-workflow

Short description

This repository contains "MultiAgent", a prototype AI-driven developer pipeline that runs a set of cooperating agents to implement small feature requests and persist results to GitHub. The solution includes a C# backend that hosts agent workflows and a Vue.js frontend dashboard for running pipelines and viewing live progress/history.

Key features

- Run an in-memory multi-agent pipeline that coordinates multiple agent roles.
- Real-time dashboard using Vue 3 + Vite and SignalR for live logs/status.
- Optional GitHub integration to open/close issues and persist pipeline outputs.

Repository structure

- MultiAgent/            — Main backend solution (C#)
  - MultiAgent/          — ASP.NET project (SignalR hubs, workflows, services)
  - MultiAgent.AppHost/  — App host bootstrap for distributed application scenarios
  - MultiAgent Frontend/ — Vue 3 + TypeScript + Vite frontend (dashboard)

Main components

- Backend (C#)
  - Program.cs: Configures ASP.NET pipeline, registers services: PipelineHistoryService, GitHubService, MultiAgentWorkflow, GeminiThrottler, and SignalR hub mapping (/pipelinehub).
  - Hubs: SignalR hub (PipelineHub) used to push live logs and agent status to the frontend.
  - Services: PipelineHistoryService, GitHubService, MultiAgentWorkflow (coordinates agents), GeminiThrottler (rate-limits AI calls).
  - WeatherForecast.cs: sample template class left from template projects.

- Frontend (Vue 3 + TypeScript + Vite)
  - index.html / src/main.ts / src/App.vue: Small dashboard UI that can start pipelines, show agent status, and display live logs.
  - vite.config.ts: Vite dev server runs on port 5173 and proxies API calls (e.g. /api) to the backend at https://localhost:7247 during development.
  - README.md inside MultiAgent Frontend contains a short starter note for Vue + TypeScript + Vite.

Development setup (local)

Prerequisites

- .NET SDK (7 or later recommended)
- Node.js + npm or pnpm
- Git

Backend

1. Open the solution (MultiAgent.sln) in Visual Studio or use the dotnet CLI.
2. From the repo root, build and run the backend project:

   dotnet restore
   dotnet build
   dotnet run --project MultiAgent/MultiAgent

3. The backend exposes a SignalR hub at /pipelinehub and API endpoints under /api. Ensure HTTPS is enabled for SignalR in development or adjust Vite proxy secure option.

Frontend

1. Change into the frontend folder and install dependencies:

   cd "MultiAgent/MultiAgent Frontend"
   npm install

2. Run the Vite dev server:

   npm run dev

3. The frontend runs at http://localhost:5173 by default and proxies API requests to the backend (see vite.config.ts).

Notes on running

- The frontend UI provides a "Run Pipeline" panel. You can also trigger pipelines via GitHub by adding label `ai-pipeline` to an issue (the UI mentions this behavior).
- CORS: the backend config specifically allows origins such as http://localhost:5173, http://localhost:3000, and https://localhost:5173 in development to permit SignalR connections.
- The Project contains