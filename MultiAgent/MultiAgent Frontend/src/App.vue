<template>
  <div class="app">

    <!-- Header -->
    <header>
      <div class="logo">⚡ DevPipeline</div>
      <div class="tagline">A2A Dev Pipeline · MAF rc4</div>
      <div class="badges">
        <span class="badge" :class="connected ? 'live' : 'offline'">
          {{ connected ? '● Live' : '○ Offline' }}
        </span>
      </div>
    </header>

    <!-- Tabs -->
    <div class="tabs">
      <button :class="{ active: tab === 'run' }"     @click="tab = 'run'">▶ Run Pipeline</button>
      <button :class="{ active: tab === 'history' }" @click="tab = 'history'; loadHistory()">📋 History</button>
      <button :class="{ active: tab === 'setup' }"   @click="tab = 'setup'">⚙️ Setup</button>
    </div>

    <!-- ── TAB: Run ─────────────────────────────────────────── -->
    <div v-if="tab === 'run'">

      <div class="card">
        <div class="field">
          <label>Feature Description</label>
          <textarea v-model="featureDescription" :disabled="running" rows="3"
            placeholder="e.g. Add JWT authentication to the login endpoint with refresh token support" />
        </div>
        <div class="row">
          <div class="field flex1">
            <label>Local Repo Path</label>
            <input v-model="repoPath" :disabled="running"
              placeholder="C:\\Jagadish Kumar\\ai-generated-code" />
          </div>
          <div class="field" style="width:200px">
            <label>GitHub Issue # (optional)</label>
            <input v-model="issueNumber" :disabled="running" placeholder="e.g. 42" />
          </div>
          <button class="btn-primary" @click="runPipeline"
            :disabled="running || !featureDescription || !repoPath">
            <span v-if="!running">🚀 Run Pipeline</span>
            <span v-else class="pulse">⏳ Running...</span>
          </button>
        </div>
      </div>

      <!-- GitHub trigger tip -->
      <div class="info-banner" v-if="!running && !hasStarted">
        <strong>💡 Trigger from GitHub:</strong>
        Open any issue → Add label <code>ai-pipeline</code> → Pipeline starts automatically!
      </div>

      <!-- Agent status row -->
      <div class="agents" v-if="hasStarted">
        <div v-for="a in agents" :key="a.name" class="agent" :class="a.status">
          <div class="agent-icon">{{ a.icon }}</div>
          <div class="agent-name">{{ a.label }}</div>
          <div class="agent-model">{{ a.model }}</div>
          <div class="agent-status-text">
            <span v-if="a.status === 'waiting'" class="dim">⬜ Waiting</span>
            <span v-else-if="a.status === 'running'" class="pulse">⏳ Running</span>
            <span v-else-if="a.status === 'done'" class="green">✅ Done</span>
            <span v-else-if="a.status === 'failed'" class="red">❌ Failed</span>
          </div>
        </div>
      </div>

      <!-- Live logs -->
      <div class="card" v-if="hasStarted">
        <div class="log-header">
          <span>📋 Live Logs</span>
          <button class="btn-ghost" @click="logs = []">Clear</button>
        </div>
        <div class="logs" ref="logsEl">
          <div v-if="!logs.length" class="dim" style="padding:20px;text-align:center">
            Waiting for pipeline logs...
          </div>
          <div v-for="(l, i) in logs" :key="i" class="log" :class="l.level">
            <span class="t">{{ l.time }}</span>
            <span class="a">[{{ l.agent }}]</span>
            <span class="m">{{ l.message }}</span>
          </div>
        </div>
      </div>

      <!-- Final report -->
      <div v-if="finalReport" class="report"
        :class="finalReport.hasErrors ? 'report-warn' : 'report-ok'">
        <h3>{{ finalReport.hasErrors ? '⚠️ Complete with issues' : '🎉 Pipeline Complete!' }}</h3>
        <p style="margin-top:8px;font-size:14px;color:#94a3b8">{{ finalReport.summary }}</p>
        <a v-if="finalReport.prUrl" :href="finalReport.prUrl" target="_blank" class="pr-link">
          🔀 Open Draft PR →
        </a>
      </div>
    </div>

    <!-- ── TAB: History ─────────────────────────────────────── -->
    <div v-if="tab === 'history'">
      <div class="card">
        <div v-if="!history.length" class="dim" style="padding:20px;text-align:center">
          No pipeline runs yet this session.
        </div>
        <table v-else class="history-table">
          <thead>
            <tr>
              <th>Feature</th>
              <th>Status</th>
              <th>Issue</th>
              <th>Started</th>
              <th>Duration</th>
              <th>PR</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="r in history" :key="r.pipelineId">
              <td>{{ r.featureDescription }}</td>
              <td>
                <span :class="'badge-' + r.status">
                  {{ r.status === 'complete' ? '✅ Complete'
                   : r.status === 'running'  ? '⏳ Running'
                   : '❌ Failed' }}
                </span>
              </td>
              <td>{{ r.gitHubIssueNumber ? '#' + r.gitHubIssueNumber : '—' }}</td>
              <td>{{ formatTime(r.startedAt) }}</td>
              <td>{{ duration(r.startedAt, r.finishedAt) }}</td>
              <td>
                <a v-if="r.pullRequestUrl" :href="r.pullRequestUrl" target="_blank">View PR →</a>
                <span v-else class="dim">—</span>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- ── TAB: Setup ───────────────────────────────────────── -->
    <div v-if="tab === 'setup'">
      <div class="card setup">
        <h3>🔧 Setup Checklist</h3>

        <div class="step">
          <div class="step-num">1</div>
          <div class="step-body">
            <strong>Get API Keys</strong>
            <ul>
              <li>Gemini: <a href="https://aistudio.google.com" target="_blank">aistudio.google.com</a> → Get API Key → FREE, no card</li>
              <li>Groq: <a href="https://console.groq.com" target="_blank">console.groq.com</a> → API Keys → FREE, no card</li>
              <li>GitHub Models: <a href="https://github.com/settings/tokens" target="_blank">github.com/settings/tokens</a> → your existing PAT works!</li>
            </ul>
          </div>
        </div>

        <div class="step">
          <div class="step-num">2</div>
          <div class="step-body">
            <strong>Fill in appsettings.json</strong>
            <pre>Gemini:ApiKey, Groq:ApiKey
GitHub:Token (reused for Models too!), GitHub:RepoOwner
GitHub:RepoName, GitHub:WebhookSecret, Pipeline:LocalRepoPath</pre>
          </div>
        </div>

        <div class="step">
          <div class="step-num">3</div>
          <div class="step-body">
            <strong>GitHub Personal Access Token</strong><br/>
            <a href="https://github.com/settings/tokens" target="_blank">github.com/settings/tokens</a>
            → Generate new token (classic) → check <code>repo</code> scope → copy
          </div>
        </div>

        <div class="step">
          <div class="step-num">4</div>
          <div class="step-body">
            <strong>Create an empty GitHub repo</strong><br/>
            This is where the pipeline commits the generated code and opens the Draft PR.
          </div>
        </div>

        <div class="step">
          <div class="step-num">5</div>
          <div class="step-body">
            <strong>Run the API</strong>
            <pre>Set AppHost as startup project → F5
Aspire Dashboard → http://localhost:18888</pre>
          </div>
        </div>

        <div class="step" style="border:none">
          <div class="step-num">6</div>
          <div class="step-body">
            <strong>Run this dashboard</strong>
            <pre>cd pipeline-ui
npm install
npm run dev   ← opens http://localhost:5173</pre>
          </div>
        </div>

        <div class="cost-box">
          <h4>Estimated cost per full pipeline run</h4>
          <table class="cost-table">
            <tr><td>✨ Gemini 2.0 Flash (Coder, Playwright, Security)</td><td>$0.00 FREE</td></tr>
            <tr><td>⚡ Groq Llama 3.3 70B (UnitTest)</td><td>$0.00 FREE</td></tr>
            <tr><td>🐙 GitHub Models GPT-4o Mini (Review)</td><td>$0.00 FREE</td></tr>
            <tr class="total"><td><strong>Total per pipeline run</strong></td><td><strong>$0.00 FREE</strong></td></tr>
          </table>
        </div>
      </div>
    </div>

  </div>
</template>

<script setup lang="ts">
import { ref, nextTick, onMounted, onUnmounted } from 'vue'
import * as signalR from '@microsoft/signalr'

// ── Types ─────────────────────────────────────────────────────
interface AgentState {
  name:   string
  label:  string
  icon:   string
  model:  string
  status: 'waiting' | 'running' | 'done' | 'failed'
}

interface LogEntry {
  agent:   string
  message: string
  level:   string
  time:    string
}

interface PipelineRun {
  pipelineId:         string
  featureDescription: string
  status:             string
  startedAt:          string
  finishedAt?:        string
  pullRequestUrl?:    string
  gitHubIssueNumber?: string
}

interface FinalReport {
  hasErrors: boolean
  prUrl?:    string
  summary:   string
}

// ── State ─────────────────────────────────────────────────────
const tab                = ref<'run' | 'history' | 'setup'>('run')
const connected          = ref(false)
const running            = ref(false)
const hasStarted         = ref(false)
const featureDescription = ref('')
const repoPath           = ref('')
const issueNumber        = ref('')
const logs               = ref<LogEntry[]>([])
const history            = ref<PipelineRun[]>([])
const finalReport        = ref<FinalReport | null>(null)
const logsEl             = ref<HTMLElement | null>(null)

const agents = ref<AgentState[]>([
  { name: 'CoderAgent',      label: 'Coder',      icon: '🧑‍💻', model: 'Gemini 2.0 Flash',  status: 'waiting' },
  { name: 'UnitTestAgent',   label: 'Unit Tests', icon: '🧪', model: 'Groq Llama 3.3',    status: 'waiting' },
  { name: 'PlaywrightAgent', label: 'Playwright', icon: '🎭', model: 'Gemini 2.0 Flash',  status: 'waiting' },
  { name: 'ReviewAgent',     label: 'Review',     icon: '👁️', model: 'GitHub GPT-4o Mini', status: 'waiting' },
  { name: 'SecurityAgent',   label: 'Security',   icon: '🔒', model: 'Gemini 2.0 Flash',  status: 'waiting' },
])

// ── SignalR connection ─────────────────────────────────────────
// Connects to the .NET Hub at /pipelinehub
// Vite proxies /api → https://localhost:5000
// For SignalR we connect directly since proxy doesn't handle WS by default
const API_BASE = 'https://localhost:5000'

let hubConnection: signalR.HubConnection | null = null

function buildConnection(): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl(`${API_BASE}/pipelinehub`, {
      skipNegotiation: false,
      transport: signalR.HttpTransportType.WebSockets
        | signalR.HttpTransportType.ServerSentEvents
        | signalR.HttpTransportType.LongPolling
    })
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build()
}

async function startConnection(): Promise<void> {
  hubConnection = buildConnection()

  // ── Incoming SignalR events from .NET backend ──────────────

  // Log line: { pipelineId, agent, message, level, timestamp }
  hubConnection.on('PipelineLog', (data: {
    pipelineId: string
    agent:      string
    message:    string
    level:      string
  }) => {
    addLog(data.agent, data.message, data.level)
  })

  // Token-by-token LLM output: { pipelineId, agent, token }
  hubConnection.on('AgentToken', (data: {
    pipelineId: string
    agent:      string
    token:      string
  }) => {
    // Append token to last log line if same agent, else new line
    const last = logs.value[logs.value.length - 1]
    if (last && last.agent === data.agent && last.level === 'token') {
      last.message += data.token
    } else {
      logs.value.push({
        agent:   data.agent,
        message: data.token,
        level:   'token',
        time:    new Date().toTimeString().slice(0, 8)
      })
    }
    scrollLogs()
  })

  // Agent status change: { pipelineId, agentName, status }
  hubConnection.on('AgentStatus', (data: {
    pipelineId: string
    agentName:  string
    status:     'waiting' | 'running' | 'done' | 'failed'
  }) => {
    const agent = agents.value.find(a => a.name === data.agentName)
    if (agent) agent.status = data.status
  })

  // Pipeline finished: { pipelineId, hasErrors, prUrl, summary }
  hubConnection.on('PipelineComplete', (data: {
    pipelineId: string
    hasErrors:  boolean
    prUrl?:     string
    summary:    string
  }) => {
    running.value = false
    finalReport.value = {
      hasErrors: data.hasErrors,
      prUrl:     data.prUrl,
      summary:   data.summary
    }
    addLog('Orchestrator', data.summary, data.hasErrors ? 'warning' : 'success')
  })

  // Connection events
  hubConnection.onreconnecting(() => { connected.value = false })
  hubConnection.onreconnected(()  => { connected.value = true  })
  hubConnection.onclose(()        => { connected.value = false })

  try {
    await hubConnection.start()
    connected.value = true
  } catch (err) {
    console.warn('SignalR connection failed:', err)
    connected.value = false
  }
}

// ── Lifecycle ─────────────────────────────────────────────────
onMounted(async () => {
  await startConnection()
})

onUnmounted(async () => {
  if (hubConnection) await hubConnection.stop()
})

// ── Run pipeline ───────────────────────────────────────────────
async function runPipeline(): Promise<void> {
  running.value      = true
  hasStarted.value   = true
  finalReport.value  = null
  logs.value         = []
  agents.value.forEach(a => a.status = 'waiting')

  try {
    const res = await fetch(`${API_BASE}/api/pipeline/run`, {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        featureDescription: featureDescription.value,
        repoPath:           repoPath.value,
        gitHubIssueNumber:  issueNumber.value || null
      })
    })

    if (!res.ok) {
      const err = await res.text()
      addLog('System', `❌ API error: ${err}`, 'error')
      running.value = false
    }
    // Success: pipeline started in background, logs come via SignalR
  } catch {
    addLog('System', '❌ Cannot reach API on port 5000 — is dotnet running?', 'error')
    running.value = false
  }
}

// ── History ────────────────────────────────────────────────────
async function loadHistory(): Promise<void> {
  try {
    const res = await fetch(`${API_BASE}/api/pipeline/history`)
    history.value = await res.json() as PipelineRun[]
  } catch {
    history.value = []
  }
}

// ── Helpers ────────────────────────────────────────────────────
function addLog(agent: string, message: string, level = 'info'): void {
  logs.value.push({
    agent,
    message,
    level,
    time: new Date().toTimeString().slice(0, 8)
  })
  scrollLogs()
}

async function scrollLogs(): Promise<void> {
  await nextTick()
  if (logsEl.value) logsEl.value.scrollTop = logsEl.value.scrollHeight
}

function formatTime(t?: string): string {
  return t ? new Date(t).toLocaleTimeString() : '—'
}

function duration(start?: string, end?: string): string {
  if (!start || !end) return '—'
  const s = Math.round((new Date(end).getTime() - new Date(start).getTime()) / 1000)
  return s < 60 ? `${s}s` : `${Math.floor(s / 60)}m ${s % 60}s`
}
</script>

<style scoped>
@import url('https://fonts.googleapis.com/css2?family=JetBrains+Mono:wght@400;600&family=Inter:wght@400;500;600;700&display=swap');

*, *::before, *::after { box-sizing: border-box; margin: 0; padding: 0; }

.app {
  min-height: 100vh;
  background: #0d0f14;
  color: #e2e8f0;
  font-family: 'Inter', sans-serif;
  padding: 20px 24px;
  max-width: 1200px;
  margin: 0 auto;
}

/* Header */
header { display:flex; align-items:center; gap:12px; margin-bottom:20px; padding-bottom:16px; border-bottom:1px solid #1e2535; }
.logo    { font-size:20px; font-weight:700; color:#60a5fa; }
.tagline { color:#475569; font-size:13px; flex:1; }
.badges  { display:flex; gap:8px; }
.badge   { font-size:11px; font-family:'JetBrains Mono',monospace; padding:3px 10px; border-radius:20px; }
.live    { color:#4ade80; background:#052e16; }
.offline { color:#94a3b8; background:#1e2535; }

/* Tabs */
.tabs { display:flex; gap:4px; margin-bottom:16px; }
.tabs button { background:#131720; border:1px solid #1e2535; color:#64748b; padding:8px 18px; border-radius:8px; cursor:pointer; font-size:13px; font-weight:500; transition:all .2s; }
.tabs button.active { background:#1e3a5f; border-color:#3b82f6; color:#93c5fd; }
.tabs button:hover:not(.active) { border-color:#334155; color:#94a3b8; }

/* Card */
.card { background:#131720; border:1px solid #1e2535; border-radius:12px; padding:20px; margin-bottom:16px; }

/* Form */
.field { display:flex; flex-direction:column; gap:6px; }
.field label { font-size:11px; font-weight:600; color:#64748b; text-transform:uppercase; letter-spacing:.8px; }
textarea, input { background:#0d0f14; border:1px solid #1e2535; border-radius:8px; color:#e2e8f0; font-family:'JetBrains Mono',monospace; font-size:13px; padding:10px 12px; width:100%; transition:border-color .2s; }
textarea { resize:vertical; }
textarea:focus, input:focus { outline:none; border-color:#3b82f6; }
.row   { display:flex; gap:12px; align-items:flex-end; margin-top:14px; flex-wrap:wrap; }
.flex1 { flex:1; min-width:200px; }

/* Buttons */
.btn-primary { background:#2563eb; color:#fff; border:none; border-radius:8px; padding:10px 24px; font-size:14px; font-weight:600; cursor:pointer; white-space:nowrap; transition:background .2s; }
.btn-primary:hover:not(:disabled) { background:#1d4ed8; }
.btn-primary:disabled { background:#1e2535; color:#475569; cursor:not-allowed; }
.btn-ghost { background:none; border:1px solid #1e2535; color:#64748b; padding:6px 14px; border-radius:6px; cursor:pointer; font-size:12px; }
.btn-ghost:hover { border-color:#334155; color:#94a3b8; }

/* Info banner */
.info-banner { background:#0f1e35; border:1px solid #1e3a5f; border-radius:8px; padding:12px 16px; font-size:13px; color:#93c5fd; margin-bottom:16px; }
.info-banner code { background:#1e3a5f; padding:1px 6px; border-radius:4px; font-family:'JetBrains Mono',monospace; font-size:12px; }

/* Agents */
.agents { display:flex; gap:10px; margin-bottom:16px; flex-wrap:wrap; }
.agent  { flex:1; min-width:130px; background:#131720; border:1px solid #1e2535; border-radius:10px; padding:14px 12px; text-align:center; transition:all .3s; }
.agent.running { border-color:#3b82f6; background:#0f172a; box-shadow:0 0 12px #1e3a5f; }
.agent.done    { border-color:#22c55e; background:#052e16; }
.agent.failed  { border-color:#ef4444; background:#2d0a0a; }
.agent-icon        { font-size:24px; margin-bottom:6px; }
.agent-name        { font-size:12px; font-weight:600; margin-bottom:2px; }
.agent-model       { font-size:10px; color:#475569; font-family:'JetBrains Mono',monospace; margin-bottom:4px; }
.agent-status-text { font-size:11px; }

/* Logs */
.log-header { display:flex; justify-content:space-between; align-items:center; margin-bottom:10px; font-size:13px; font-weight:600; color:#64748b; }
.logs { background:#0a0c10; border:1px solid #1e2535; border-radius:8px; padding:12px; height:300px; overflow-y:auto; font-family:'JetBrains Mono',monospace; font-size:12px; }
.log { display:flex; gap:10px; padding:3px 0; border-bottom:1px solid #0a0d14; }
.t   { color:#1e3a5f; min-width:65px; flex-shrink:0; }
.a   { color:#3b82f6; min-width:120px; flex-shrink:0; }
.m   { color:#64748b; flex:1; word-break:break-word; white-space:pre-wrap; }
.log.success .m { color:#4ade80; }
.log.error   .m { color:#f87171; }
.log.warning .m { color:#fbbf24; }
.log.token   .m { color:#c4b5fd; }

/* Report */
.report      { border-radius:12px; padding:20px; margin-bottom:16px; }
.report-ok   { background:#052e16; border:1px solid #166534; }
.report-warn { background:#1c1008; border:1px solid #92400e; }
.report h3   { font-size:16px; font-weight:700; }
.pr-link { display:inline-block; margin-top:14px; background:#1d4ed8; color:#fff; padding:8px 18px; border-radius:8px; text-decoration:none; font-size:13px; font-weight:600; }

/* History table */
.history-table { width:100%; border-collapse:collapse; font-size:13px; }
.history-table th { text-align:left; padding:8px 12px; color:#64748b; font-size:11px; text-transform:uppercase; letter-spacing:.6px; border-bottom:1px solid #1e2535; }
.history-table td { padding:10px 12px; border-bottom:1px solid #0f1520; }
.history-table a  { color:#60a5fa; }
.badge-complete { color:#4ade80; }
.badge-running  { color:#60a5fa; }
.badge-failed   { color:#f87171; }

/* Setup */
.setup h3 { font-size:16px; font-weight:700; margin-bottom:18px; }
.step { display:flex; gap:14px; margin-bottom:18px; padding-bottom:18px; border-bottom:1px solid #1e2535; }
.step-num { width:28px; height:28px; min-width:28px; background:#1e3a5f; color:#60a5fa; border-radius:50%; display:flex; align-items:center; justify-content:center; font-weight:700; font-size:13px; }
.step-body { flex:1; font-size:13px; line-height:1.7; color:#94a3b8; }
.step-body strong { color:#e2e8f0; display:block; margin-bottom:4px; }
.step-body pre, .step-body code { font-family:'JetBrains Mono',monospace; background:#0d0f14; padding:4px 10px; border-radius:4px; font-size:12px; color:#60a5fa; display:inline-block; margin-top:4px; }
.step-body ul { margin-left:16px; }
.step-body a { color:#60a5fa; }
.cost-box { background:#0d0f14; border-radius:8px; padding:16px; margin-top:8px; }
.cost-box h4 { font-size:13px; font-weight:600; color:#64748b; margin-bottom:10px; }
.cost-table { width:100%; font-size:13px; border-collapse:collapse; }
.cost-table td { padding:6px 8px; color:#94a3b8; }
.cost-table td:last-child { text-align:right; color:#e2e8f0; }
.cost-table tr.total td { color:#4ade80; border-top:1px solid #1e2535; padding-top:10px; font-weight:600; }

/* Utilities */
.dim   { color:#334155; }
.green { color:#4ade80; }
.red   { color:#f87171; }
.pulse { animation:pulse 1s infinite; }
@keyframes pulse { 0%,100%{opacity:1} 50%{opacity:.3} }
</style>