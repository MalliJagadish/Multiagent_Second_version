# MultiAgent — AI-Driven Developer Pipeline

> ⚠️ **Prototype**
> This repository is an early-stage prototype. It is **not production-ready**.

---

## Table of Contents

- [What Is This Project?](#what-is-this-project)
- [Problem It Solves](#problem-it-solves)
- [How It Works](#how-it-works)
  - [Architecture Overview](#architecture-overview)
  - [Agent Pipeline Flow](#agent-pipeline-flow)
- [Tech Stack](#tech-stack)
- [Repository Structure](#repository-structure)
- [Getting Started](#getting-started)
  - [Prerequisites](#prerequisites)
  - [1. Clone the Repository](#1-clone-the-repository)
  - [2. Configure the Backend](#2-configure-the-backend)
  - [3. Run the Backend](#3-run-the-backend)
  - [4. Run the Frontend](#4-run-the-frontend)
- [Configuration Reference](#configuration-reference)
  - [appsettings.json Structure](#appsettingsjson-structure)
  - [GitHub Classic Token Setup](#github-classic-token-setup)
  - [What You Must Change for Your Own Environment](#what-you-must-change-for-your-own-environment)
- [Using the Dashboard](#using-the-dashboard)
- [Limitations & Known Issues](#limitations--known-issues)
- [Roadmap / Future Exploration](#roadmap--future-exploration)
- [License](#license)

---

## What Is This Project?

**MultiAgent** is a prototype AI-driven developer pipeline that uses a team of cooperating software agents to automatically implement small feature requests and persist results to GitHub.

It consists of:
- A **C# / ASP.NET Core backend** that orchestrates multiple specialised AI agents working in a defined sequence, backed by multiple AI providers (Gemini, Groq, and GitHub Models)
- A **Vue 3 frontend dashboard** that lets you trigger pipelines, observe agents working in real time via live logs, and review pipeline history

The system breaks a development task into well-defined phases — coding, review, security, testing — each handled by a dedicated agent, with the entire review-and-fix loop completing internally before a pull request is ever opened.

---

## How It Works

### Architecture Overview

```
┌──────────────────────────────────────────────────────────────┐
│                      Vue 3 Frontend                          │
│           (Dashboard · Live Logs · Pipeline History)         │
└───────────────────────┬──────────────────────────────────────┘
                        │  SignalR (WebSocket)  +  REST /api
┌───────────────────────▼──────────────────────────────────────┐
│                   ASP.NET Core Backend                       │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  Workflows/MultiAgentWorkflow.cs                     │    │
│  │  Orchestrates all agents in sequence / parallel      │    │
│  │                                                      │    │
│  │  CoderAgent → [ReviewAgent ‖ SecurityAgent]          │    │
│  │            → CoderAgent (fix/defend)                 │    │
│  │            → [ReReviewAgent ‖ SecReReviewAgent]      │    │
│  │            → (loop if unresolved criticals)          │    │
│  │            → [UnitTestAgent ‖ PlaywrightAgent]       │    │
│  │            → Commit + Draft PR                       │    │
│  │            → Post inline comments → Done             │    │
│  └──────────────────────────────────────────────────────┘    │
│                                                              │
│  ┌──────────────┐  ┌────────────────┐  ┌─────────────────┐  │
│  │ Agents/      │  │ Services/      │  │ Tools/          │  │
│  │ GeminiChat   │  │ GitHubService  │  │ InMemoryFile    │  │
│  │ Client.cs    │  │ GeminiThrottler│  │ Tools.cs        │  │
│  │ GroqChat     │  │ PipelineHistory│  │ PipelineTools   │  │
│  │ Client.cs    │  └────────────────┘  └─────────────────┘  │
│  │ GitHubModels │                                            │
│  │ ChatClient   │                                            │
│  │ Throttled    │                                            │
│  │ ChatClient   │                                            │
│  └──────────────┘                                            │
│                                                              │
│  ┌──────────────────────────────────────────────────────┐    │
│  │  Hubs/PipelineHub.cs (SignalR)                       │    │
│  │  Streams live agent status + logs to the frontend    │    │
│  └──────────────────────────────────────────────────────┘    │
└───────────────────────┬──────────────────────────────────────┘
                        │
                ┌───────▼────────┐
                │   GitHub API   │
                │  Issues ·      │
                │  Commits · PRs │
                └────────────────┘
```

### Agent Pipeline Flow

The pipeline runs agents in a structured sequence with parallel stages and an iterative fix loop:

```
        ┌─────────────────────┐
        │     Coder agent     │
        │  Generates source   │
        │       files         │
        └──────────┬──────────┘
                   │
           ┌───────┴ parallel ──────┐
           ▼                        ▼
  ┌────────────────┐      ┌──────────────────┐
  │  Review agent  │      │  Security agent  │
  │  Code quality  │      │  Vulnerability   │
  │    findings    │      │    findings      │
  └───────┬────────┘      └────────┬─────────┘
           └──────── merge ────────┘
                        │
           ┌────────────▼────────────┐
           │      Coder agent        │
           │  Fix or defend findings │
           └────────────┬────────────┘
                        │
           ┌────────────┴ parallel ──────────┐
           ▼                                  ▼
  ┌──────────────────┐            ┌───────────────────────┐
  │  Re-review agent │            │   Sec re-review agent │
  │  Accept or reject│            │   Verify vulnerability│
  │      fixes       │            │         fixes         │
  └────────┬─────────┘            └──────────┬────────────┘
           └──────────── merge ──────────────┘
                              │
                   ┌──────────▼───────────┐
                   │   Unresolved         │
                   │   criticals?         │
                   └──────────┬───────────┘
        yes (next round,          ◄────┘────► no
        max 2 rounds)                        │
        (loop back to                 │
         Coder agent)      ┌──────────┴ parallel ──────────┐
                           ▼                                ▼
                  ┌─────────────────┐           ┌───────────────────┐
                  │ Unit test agent │           │  Playwright agent │
                  │   NUnit tests   │           │  E2E / API tests  │
                  └────────┬────────┘           └─────────┬─────────┘
                           └──────────── merge ───────────┘
                                              │
                                 ┌────────────▼────────────┐
                                 │    Commit + draft PR    │
                                 │   Push files to branch  │
                                 └────────────┬────────────┘
                                              │
                                 ┌────────────▼────────────┐
                                 │  Post inline comments   │
                                 │  Resolved + unresolved  │
                                 └────────────┬────────────┘
                                              │
                                 ┌────────────▼────────────┐
                                 │          Done           │
                                 │ Human reviews open      │
                                 │       threads           │
                                 └─────────────────────────┘
```

**Key characteristics of this pipeline:**
- **Parallel execution** — Review and Security agents run simultaneously; test agents run simultaneously
- **Iterative fix loop** — After fixes are applied, re-review agents check whether criticals are resolved. The loop runs up to **2 rounds** and exits early if all criticals are resolved before that.
- **Multiple AI models across agents** — Each agent uses the model best suited to its role:
  - **Groq** (`llama-3.3-70b-versatile`) — CoderAgent and CoderFixAgent (fast code generation and fixing)
  - **Gemini** (`gemini-2.0-flash`) — SecurityAgent, SecReReviewAgent, PlaywrightAgent
  - **GitHub Models** (`openai/gpt-4.1-nano`) — ReviewAgent and ReReviewAgent
- **GitHub integration** — Output is committed to a branch, a draft PR is opened, and inline review comments are posted automatically
- **Human-in-the-loop at the end** — The pipeline hands off to a human reviewer who sees all open threads from the agents

---

## Tech Stack

| Layer      | Technology                                           |
|------------|------------------------------------------------------|
| Backend    | C# · ASP.NET Core 10 · SignalR                          |
| AI Models  | Groq `llama-3.3-70b-versatile` · Gemini `gemini-2.0-flash` · GitHub Models `openai/gpt-4.1-nano` |
| Frontend   | Vue 3 · TypeScript · Vite                            |
| Realtime   | SignalR (WebSocket)                                  |
| Testing    | NUnit (unit tests) · Playwright (E2E / API tests)    |
| VCS        | GitHub REST API (issues, commits, branches, PRs)     |

---

## Repository Structure

```
Multiagent_Second_version/
│
├── MultiAgent/                                # Root solution (MultiAgent.sln)
│   │
│   ├── MultiAgent/                            # ASP.NET Core backend project
│   │   ├── Program.cs                         # App entry, DI registration, middleware, CORS
│   │   ├── appsettings.json                   # Configuration: GitHub, Gemini, Groq keys
│   │   ├── appsettings.Development.json       # Local dev overrides (DO NOT commit secrets)
│   │   ├── MultiAgent.http                    # HTTP request scratch file for manual testing
│   │   ├── WeatherForecast.cs                 # ASP.NET template leftover (unused)
│   │   │
│   │   ├── Agents/                            # AI provider chat client wrappers
│   │   │   ├── GeminiChatClient.cs            # Gemini API chat client
│   │   │   ├── GitHubModelsChatClient.cs      # GitHub Models chat client
│   │   │   ├── GroqChatClient.cs              # Groq API chat client
│   │   │   └── ThrottledChatClient.cs         # Throttle wrapper for any chat client
│   │   │
│   │   ├── Controllers/
│   │   │   ├── PipelineController.cs          # REST endpoints to trigger / query pipelines
│   │   │   └── WeatherForecastController.cs   # ASP.NET template leftover (unused)
│   │   │
│   │   ├── Hubs/
│   │   │   └── PipelineHub.cs                 # SignalR hub — streams live logs to frontend
│   │   │
│   │   ├── Models/
│   │   │   ├── PipelineModels.cs              # Request / response models for pipeline API
│   │   │   └── ReviewModels.cs                # Models for agent review findings
│   │   │
│   │   ├── Services/
│   │   │   ├── GeminiThrottler.cs             # Rate-limits Gemini API calls across agents
│   │   │   ├── GitHubService.cs               # GitHub API: issues, commits, PRs, comments
│   │   │   └── PipelineHistoryService.cs      # In-memory pipeline run history
│   │   │
│   │   ├── Tools/
│   │   │   ├── InMemoryFileTools.cs           # In-memory virtual file system for agents
│   │   │   └── PipelineTools.cs               # Shared tool utilities for the pipeline
│   │   │
│   │   └── Workflows/
│   │       └── MultiAgentWorkflow.cs          # Core orchestrator — sequences all agents
│   │
│   ├── MultiAgent.AppHost/                    # App host bootstrap for distributed scenarios
│   └── MultiAgent.ServiceDefaults/            # Shared service configuration defaults
│
├── MultiAgent Frontend/                       # Vue 3 + TypeScript + Vite frontend
│   ├── index.html
│   ├── vite.config.ts                         # Dev server + proxy to backend (:7247)
│   └── src/
│       ├── main.ts
│       └── App.vue                            # Dashboard: run pipelines, live logs, history
│
└── README.md
```

---

## Getting Started

### Prerequisites

| Tool | Version |
|------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.0 |
| [Node.js](https://nodejs.org/) | 22.x or later |
| npm or pnpm | Latest |
| A GitHub account | — |
| A [Google Gemini API key](https://aistudio.google.com/app/apikey) | — |
| A [Groq API key](https://console.groq.com/keys) | — |

---

### 1. Clone the Repository

```bash
git clone https://github.com/MalliJagadish/Multiagent_Second_version.git
cd Multiagent_Second_version
```

---

### 2. Configure the Backend

Open `MultiAgent/MultiAgent/appsettings.json` and replace all placeholder values:

```json
{
  "Gemini": {
    "ApiKey": "YOUR_GEMINI_API_KEY_HERE"
  },
  "Groq": {
    "ApiKey": "YOUR_GROQ_API_KEY_HERE"
  },
  "GitHub": {
    "Token": "YOUR_GITHUB_PAT_HERE",
    "RepoOwner": "YOUR_GITHUB_USERNAME",
    "RepoName": "YOUR_REPO_NAME",
    "WebhookSecret": "any-random-string-you-make-up"
  },
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.AspNetCore": "Warning"
    }
  },
  "AllowedHosts": "*"
}
```

> 🔒 **Never commit real tokens or API keys.** Use `appsettings.Development.json` (ensure it is in `.gitignore`) to store secrets locally.

---

### 3. Run the Backend

```bash
cd MultiAgent/MultiAgent
dotnet restore
dotnet build
dotnet run
```

The backend starts at `https://localhost:7247`.
The SignalR hub is available at `https://localhost:7247/pipelinehub`.

---

### 4. Run the Frontend

Open a second terminal:

```bash
cd "MultiAgent Frontend"
npm install
npm run dev
```

The frontend runs at **http://localhost:5173** and proxies all `/api` requests to the backend via `vite.config.ts`.

---

## Configuration Reference

### appsettings.json Structure

| Key | Description |
|-----|-------------|
| `Gemini:ApiKey` | Google Gemini API key — get one from [Google AI Studio](https://aistudio.google.com/app/apikey) |
| `Groq:ApiKey` | Groq API key — get one from [Groq Console](https://console.groq.com/keys) |
| `GitHub:Token` | GitHub Classic Personal Access Token (see setup below) |
| `GitHub:RepoOwner` | GitHub username of the target repository owner |
| `GitHub:RepoName` | Name of the repository the pipeline reads issues from and pushes output to |
| `GitHub:WebhookSecret` | Any random string you choose — used to verify GitHub webhook payloads |

---

### GitHub Classic Token Setup

This project uses **GitHub Classic Personal Access Tokens** (not fine-grained tokens).

1. Go to **GitHub → Settings → Developer Settings → Personal Access Tokens → Tokens (classic)**
2. Click **"Generate new token (classic)"**
3. Give it a name (e.g. `multiagent-pipeline`) and select the following scopes:

| Scope | Why it's needed |
|-------|-----------------|
| `repo` | Full repository access — read issues, push commits, create branches, open PRs, post comments |
| `read:user` | Read your GitHub profile (for author attribution on commits) |

4. Click **"Generate token"** and copy it immediately — it will not be shown again
5. Paste it into `appsettings.json` as `GitHub:Token`

---

### What You Must Change for Your Own Environment

If you clone this repo, ensure **all** of the following are configured before running:

- [ ] `Gemini:ApiKey` → Your own key from [Google AI Studio](https://aistudio.google.com/app/apikey)
- [ ] `Groq:ApiKey` → Your own key from [Groq Console](https://console.groq.com/keys)
- [ ] `GitHub:Token` → Your own GitHub Classic PAT (see above)
- [ ] `GitHub:RepoOwner` → Your GitHub username
- [ ] `GitHub:RepoName` → The repository the pipeline will operate on
- [ ] `GitHub:WebhookSecret` → Any random string of your choice
- [ ] **CORS origins** in `Program.cs` → If your frontend runs on a different port than `5173`, add it to the allowed origins list
- [ ] **Vite proxy target** in `vite.config.ts` → If your backend runs on a different port than `7247`, update the proxy target URL

---

## Using the Dashboard

Once both backend and frontend are running:

1. Open **http://localhost:5173** in your browser
2. Use the **"Run Pipeline"** panel to submit a feature task or issue description
3. Watch the **live log panel** as each agent works through its stage in real time via SignalR
4. The pipeline will iterate through fix loops automatically until no critical issues remain
5. Once complete, a **draft PR** will be opened on the configured GitHub repository with inline comments from the agents
6. Review open threads manually — the human reviewer is the final step in the pipeline

---

## Limitations & Known Issues

> This is an early-stage prototype. Please be aware of the following:

- **In-memory history only** — Pipeline history is stored in memory and lost when the backend restarts. There is no database persistence yet.
- **In-process orchestration** — All agents run inside a single ASP.NET process. There is no distributed agent infrastructure yet.
- **No authentication** — The dashboard has no login layer. Do not expose it publicly without adding authentication.
- **API rate limits** — `GeminiThrottler` and `ThrottledChatClient` provide basic throttling, but heavy usage may still hit Gemini or Groq API quotas.
- **No actual test execution** — Test agents generate NUnit and Playwright test files but do not execute them against a real runtime environment in this prototype.
- **No test coverage on the pipeline itself** — The codebase does not yet have automated tests.
- **Minimal error handling** — A failing agent may halt the pipeline without graceful recovery or retry.
- **Single pipeline at a time** — Concurrent pipeline runs are not supported in this prototype.
- **`WeatherForecast.cs` / `WeatherForecastController.cs`** — Leftover files from the ASP.NET project template. They are unused and can be safely deleted.

---

## Roadmap / Future Exploration

I am still exploring this space. The following ideas represent possible directions this prototype could evolve toward:

### Feature / Agent Enhancements
- **Planning agent** — Break large tasks into smaller, well-scoped steps before handing off to the Coder agent.
- **Dynamic model assignment** — Assign the most suitable AI model to each agent role based on task complexity.
- **Supervisor agent** — Monitor agent execution, detect failures, and handle recovery automatically.
- **Agent-to-Agent (A2A) communication** — Enable scalable multi-agent orchestration using frameworks like Microsoft Agent Framework (MAF).
- **RAG integration** — Align generated code with existing project conventions by grounding agents in a retrieval index of the codebase.
- **Runtime test execution** — Connect to an actual runtime environment so test agents can run tests and feed real results back into the pipeline.
- **Security scanning orchestration** — Integrate tools like Snyk with AI interpretation to prioritize and contextualize vulnerability fixes.

### Additional Improvements
- Persistent storage for pipeline history (e.g., SQLite or PostgreSQL)
- Authentication
- Improved error recovery — retry logic and partial pipeline resumption
- Streaming agent output token-by-token to the frontend

---
