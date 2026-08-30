# LocalRag — AI Career Agent powered by TrueForge

## Project

An AI career agent, built on top of an existing offline CV RAG application
(LocalRag), that uses [TrueForge](https://trueforge.dev) as its agent
execution layer. The RAG system is not the product — it's one tool the
agent calls among several.

## Problem

A normal CV chatbot answers questions about a CV. That's not what a job
search needs. A job search needs something that can **act**: read a job
description, decide which parts of the CV are relevant, compare them
systematically against every requirement, generate interview prep grounded
in what's actually true, and produce a tailored CV — while never claiming
skills or experience that aren't backed by evidence, and never taking a
consequential action without the candidate's sign-off.

A chatbot with a system prompt telling it "be helpful and don't lie" is not
that. It has no structured workflow, no enforced evidence trail, no
approval gate, and no way to run an actual check against the real document
rather than its own paraphrase of it. This project builds that missing
structure using an agent harness (TrueForge) rather than reimplementing
orchestration, approval, and session logic by hand.

## Solution

The existing LocalRag RAG pipeline (PDF ingestion → chunking → BM25 +
vector hybrid search → Ollama-generated answers) is preserved entirely
unchanged. On top of it, a new project (`LocalRag.Mcp`) exposes that
pipeline — plus job-description extraction, skill-gap classification,
interview prep, and CV tailoring — as MCP tools. TrueForge is configured
with those tools and drives the actual multi-step reasoning: deciding
which tool to call, when, delegating to subagents where useful, holding
conversation state across turns, and pausing for human approval before
returning a generated CV.

## Architecture

```
┌─────────────────────────────────────────────┐
│                  Frontend                    │
│  TrueForge's bundled chat UI (primary) —     │
│  existing Angular app optional (see below)   │
└─────────────────────┬─────────────────────────┘
                       │
                     v
┌───────────────────────────────────────────┐
│               TRUEFORGE                   │
│           PRIMARY AGENT HARNESS            │
│                                             │
│  Tool orchestration · Sessions             │
│  Subagents (dynamic) · Human approval      │
│  Sandbox (Daytona) · MCP client            │
└───────────┬─────────────────────┬─────────┘
            │                     │
            v                     v
     LocalRag.Mcp            (model provider:
   (MCP server, HTTP)         Ollama or hosted)
            │
            v
   LocalRag.Application
   (MediatR use-cases: HybridSearchQuery,
    JdAnalysisQuery, SkillGapQuery,
    InterviewPrepQuery, TailorCvQuery,
    GetFullCvTextQuery)
            │
            v
   LocalRag.Infrastructure
   (BM25 [Lucene] + FAISS/Flat vector store,
    ONNX embeddings, Ollama chat/extraction)
            │
            v
        CV Documents (/pdfs)
```

**TrueForge is the agent execution layer. The CV RAG is one of its tools.**
Nothing in `LocalRag.Mcp` decides *when* to search the CV, *when* to check
skills, or *when* to ask for approval — that's TrueForge's job, driven by
the agent's own reasoning over the conversation.

## TrueForge Usage

- **Why TrueForge**: it provides tool orchestration, session state, human
  approval, subagents, and sandboxed code execution as configuration, not
  code we'd otherwise have to build and maintain ourselves.
- **Agent orchestration / tool calling**: the agent (`cv-career-assistant`,
  created by `scripts/setup-trueforge.sh`) decides which of the six MCP
  tools to call and in what order, based on its instructions and the
  conversation so far.
- **MCP**: `LocalRag.Mcp` is registered as a remote MCP server
  (`http://localhost:5014/mcp`). This is the one MCP integration in the
  project, and it's not incidental — it's the entire mechanism by which
  TrueForge reaches the CV data at all. No other MCP server was added,
  on the principle that an integration should exist because it's actually
  needed, not because the technology is available.
- **Subagents**: TrueForge's `dynamic_sub_agents.enabled` defaults to
  `true` (verified against the live v0.1.4 API — see `RuntimeConfig` in
  `GET /api/v1/openapi.json`). The main agent can delegate sub-tasks (e.g.
  a focused "analyze this JD" pass) to a dynamically spun-up subagent
  without any code on our side. **Caveat**: we verified the *config
  default* live, but did not run a full end-to-end delegation trace in
  this environment (that needs a live Ollama + LocalRag.Mcp + browser
  session) — worth confirming once you're running it locally.
- **Sandbox**: used for the CV quality-check flow — see "Sandbox"
  under Limitations below for the real constraint we found (Daytona
  account required; no free local fallback reachable via the public API).
- **Human approval**: `require_approval_for_tools` defaults to
  `["@write", "@destructive"]`. Every read-only tool (`search_my_cv`,
  `analyze_job_description`, `compare_skills`, `prepare_interview`,
  `get_full_cv_text`) is explicitly annotated `ReadOnly = true` so it
  never pauses. `generate_tailored_cv` is deliberately left `ReadOnly =
  false`, so it's the one tool that triggers TrueForge's approval UI —
  giving human-in-the-loop review with zero custom approval code.
- **Sessions**: TrueForge persists conversation state per session
  automatically. "Now prepare me for the interview" after "Analyze this
  job" works because the agent still has the JD in context — no session
  storage was built for this; it's the harness's default behavior.

## RAG

The existing retrieval system — PdfPig extraction → `ChunkingService` →
`OnnxEmbeddingService` (all-MiniLM-L6-v2, 384-dim) into a `FlatVectorStore`,
`LuceneBm25Index` for BM25, fused via Reciprocal Rank Fusion in
`HybridSearchService` — is **unmodified**. It became an agent tool by
adding one new project, `LocalRag.Mcp`, whose tool classes call the
existing MediatR use-cases (`HybridSearchQuery` etc.) and serialize the
result as MCP tool output. No retrieval logic was duplicated or rewritten.

## Setup

```bash
# 1. .NET dependencies (per project)
cd LocalRag
dotnet restore

# 2. Ollama
ollama serve
ollama pull llama3.2:3b

# 3. Python (for pretty-printing setup script output — optional but assumed
#    by scripts/setup-trueforge.sh)
python3 --version   # any Python 3.x

# 4. TrueForge — no install step; npx fetches it
npx --yes @truefoundry/trueforge --version
```

## Configuration

Copy `.env.example` to `.env` and fill in real values. Variables:

| Variable | Used by | Purpose |
|---|---|---|
| `OLLAMA_BASE_URL`, `OLLAMA_MODEL` | `LocalRag.API`, `LocalRag.Mcp` (appsettings `Rag` section — these env vars document the values, actual config is in `appsettings.json`) | Local LLM for RAG answers, JD extraction, skill classification, interview questions, CV tailoring |
| `TRUEFORGE_URL` | `scripts/setup-trueforge.sh` | Where TrueForge is running |
| `LOCALRAG_MCP_URL` | `scripts/setup-trueforge.sh` | Where TrueForge should reach `LocalRag.Mcp` |
| `ANTHROPIC_API_KEY` / `OPENAI_API_KEY` / `GOOGLE_API_KEY` | `scripts/setup-trueforge.sh` (commented block) | Optional hosted model for the agent's own reasoning, instead of/alongside Ollama |
| `DAYTONA_API_KEY` | `scripts/setup-trueforge.sh` | Optional — enables the sandboxed CV quality-check tool. See Limitations. |

## Running

### One-time per machine

`scripts/setup-trueforge.sh` needs `jq` (not Python — see Troubleshooting
for why):
```bash
# Windows/WSL, from PowerShell: winget install jqlang.jq
# Or inside WSL/Ubuntu directly: sudo apt install jq
```

### Every time you want to run the app

Run everything — Ollama, `LocalRag.API`, `LocalRag.Mcp`, and TrueForge —
**inside the same WSL environment**. Splitting them across WSL and
Windows-native processes is what caused most of the networking issues
during initial setup (`localhost` doesn't cross that boundary); keeping
everything in one WSL distro avoids that entirely.

**Terminal 1 — open WSL and go to the repo**
```bash
wsl -d Ubuntu
```
Plain `wsl` opens whatever your *default* distro is — if Docker Desktop is
installed, that default may be a Docker-managed distro, not one you can
develop in normally. Naming it explicitly (`-d Ubuntu`, or whichever
distro you actually installed LocalRag's tooling into) avoids that
ambiguity — use it every time, not just when needed.

Then `cd` into the repo (quote the path if it has spaces, e.g. a
`Documents` folder name with spaces in it):
```bash
cd "/mnt/c/Users/<you>/<your-project-folder>/LocalRag"
```

**Terminal 2 — Ollama + build/refresh the CV index**
```bash
ollama serve &
ollama pull llama3.2:latest    # first time only, or whichever model you're using
dotnet run --project src/LocalRag.API
```
Wait for it to finish ingesting `/pdfs`. The index is just files on disk
after that — stop the process or leave it running, either is fine.

**Terminal 3 — the MCP server**
```bash
dotnet run --project src/LocalRag.Mcp
```
Confirm the startup log shows `Vectors: N` with `N > 0`.

**Terminal 4 — TrueForge**
```bash
mkdir -p logs
npx @truefoundry/trueforge 2>&1 | tee logs/trueforge.log
```
`tee` shows the output live and saves it to a file at the same time — see
Troubleshooting for why that matters. Note the URL it prints (default
`http://localhost:8790`) and open it in your browser.

**Terminal 5 — register everything with TrueForge**
```bash
chmod +x scripts/setup-trueforge.sh   # first time only
./scripts/setup-trueforge.sh
```
This is what actually creates the MCP server registration, the model
provider(s), and the `cv-career-assistant` agent — nothing shows up in the
UI until this has run at least once. Safe to re-run anytime (updates
everything in place rather than duplicating); see Troubleshooting for how
to switch models (`AGENT_MODEL=...`) or wipe everything and start fresh.

### Confirming it worked, in the TrueForge UI

1. Click the gear/**Settings** icon — your registered model providers
   (e.g. `ollama-local`, `groq-free`) should show as **Connected**.
2. Go to **Connectors** — `localrag` should be listed and connected.
   Click it to see all 6 tools: `search_my_cv`, `analyze_job_description`,
   `compare_skills`, `prepare_interview`, `generate_tailored_cv`,
   `get_full_cv_text`.
3. Go to **Agents Library** (left sidebar) — `cv-career-assistant` should
   be listed. Click **Try** to open a chat with it.
4. To adjust it later — switch models, edit instructions, change which
   connectors/tools it can use — open the agent, click **Edit**, make
   changes, then **Update Agent**. Equivalently, re-running
   `scripts/setup-trueforge.sh` with different environment variables
   (`AGENT_MODEL=...`, etc.) updates the same agent in place.
5. Submit your query — see Demo below for a suggested flow.

### Starting completely fresh

To wipe every registration (MCP server, model providers, agent — all of
it) and start over:
```bash
# Stop TrueForge first (Ctrl+C)
rm -rf ~/.local/share/trueforge/db/
# Start TrueForge again, then re-run scripts/setup-trueforge.sh
```

**Frontend note**: TrueForge ships its own chat UI with tool-call
visibility built in, which already satisfies the observability and
"simple professional UI" goals out of the box. The existing Angular app is
not wired to TrueForge yet — doing that well means a thin ASP.NET Core
proxy (per the architecture diagram: Angular → ASP.NET Core → TrueForge)
so the browser isn't calling TrueForge directly, plus a real decision
about auth. That proxy was deliberately **not** built blind — TrueForge's
session/turn API wasn't verified in enough depth to write real endpoint
code without guessing. Worth doing as a follow-up once you confirm the
schema against a running instance's `/api/v1/openapi.json`.

## Troubleshooting

Real problems hit while getting this running, in roughly the order you'll
encounter them:

- **`python3` fails on Windows/Git Bash with a Microsoft Store prompt** —
  that's Windows' "App Execution Alias" stub, not real Python.
  `scripts/setup-trueforge.sh` uses `jq` instead specifically to sidestep
  this — install `jq`, don't fight Windows' Python.
- **Re-running `setup-trueforge.sh` is always safe.** Every step upserts
  by name (`PUT`) rather than creating duplicates — confirmed live by
  running it twice in a row against the same instance and checking the
  final counts.
- **`EHOSTUNREACH` or connection timeouts** to Ollama or `LocalRag.Mcp` —
  almost always a WSL/Windows network-boundary issue. Keep every process
  inside the same WSL environment and use `localhost` throughout, per the
  Running section above.
- **The agent prints fake JSON instead of making a real tool call** (e.g.
  `{"name": "search_my_cv", ...}` shown as plain chat text) — a small
  local model (`llama3.2:3b`) struggling with tool-call formatting, not a
  wiring bug. Verify real function-calling works at all with a direct
  test against Ollama's `/v1/chat/completions` with a `tools` array in
  the request; if that works standalone but the full agent still fails,
  it's a multi-tool-selection capability limit at that model size — try a
  larger local model (`llama3.1:8b`, `qwen2.5:7b`) or a hosted provider.
- **Groq's `gpt-oss-20b` fails with `'messages.2': property
  'reasoning_content' is unsupported`** — a confirmed, known
  incompatibility (seen across multiple different agent harnesses, not
  specific to TrueForge) between Groq's reasoning-model output and how
  harnesses replay conversation history back to Groq's API on the next
  turn. Not fixable from this codebase. Avoid Groq's reasoning models
  (`gpt-oss-*`, `qwen3.6-*`) as the agent's model — a real OpenAI/
  Anthropic key, Gemini, or a local Ollama model don't have this issue.
- **Gemini returns `503: This model is currently experiencing high
  demand`** — this is Google's own server load, unrelated to anything in
  this setup. Just retry; if it's persistent, try a more established
  model tier, or fall back to another provider temporarily.
- **The agent asks you to paste/upload your CV** — it shouldn't; the CV
  is already indexed and searchable via the tools. If it does, its
  `instructions` need to say so explicitly (see `AGENT_INSTRUCTIONS` in
  `scripts/setup-trueforge.sh` — already fixed there, but check if you've
  customized instructions since via the UI's Edit screen).
- **Tool selection is unreliable even for one simple tool** — set
  `mcp_servers[].preload: true` on the agent (already the default in
  `scripts/setup-trueforge.sh`) so the model gets all 6 tool schemas
  upfront, instead of first navigating a multi-step `list_tools`/
  `get_tool_info` discovery flow. Also try a lower
  `model.params.temperature` (already set to `0.1`) for more
  deterministic tool-call formatting.
- **No logs appear even when something fails** — check
  `src/LocalRag.Mcp/logs/localrag-mcp-*.log` (every request that reaches
  `LocalRag.Mcp`, via `UseSerilogRequestLogging` — not just successful
  tool executions) and `logs/trueforge.log` (everything upstream: model
  provider calls, errors that never reach `LocalRag.Mcp` at all — this is
  why Terminal 4 above pipes through `tee`).
- **Need to see full CV content in the logs while debugging** — set
  `Rag:VerboseToolLogging: true` in
  `src/LocalRag.Mcp/appsettings.Development.json`, restart
  `LocalRag.Mcp`, and turn it back off once done (it writes real CV text
  to disk while on — see Security below for why that's off by
  default).

## Demo

In one continuous TrueForge chat session:

1. **"Analyze this job description for me: [paste a Senior .NET Developer JD]"**
   → agent calls `analyze_job_description`, then `compare_skills`; presents
   evidence-backed Strong/Partial/Weak/Missing classifications.
2. **"Now prepare me for the interview."**
   → agent reuses the JD from session context (no re-paste needed), calls
   `prepare_interview`; presents categorized questions, grounded model
   answers only where the CV has strong evidence.
3. **"Tailor my CV for this role."**
   → agent calls `generate_tailored_cv` → **TrueForge pauses for approval**
   (this is the one non-read-only tool) → you approve/reject in the UI →
   result is a section-by-section CV with every bullet labeled Existing
   Experience / Suggested Wording and cited to CV evidence, plus a
   separate Missing Skills list that never leaks into the CV itself.
4. **(If Daytona is configured)** "Check the quality of that CV against
   this job description." → agent calls `get_full_cv_text`, writes a
   short Python check script, runs it in its sandbox, reports results.

## Security

Guardrails implemented:

1. **Never invent CV experience** — every generative tool (JD extraction,
   skill classification, interview questions, CV tailoring) drops any
   LLM output that isn't traceable to a cited evidence line, rather than
   trusting the model's compliance with the prompt.
2. **Never claim a skill without evidence** — `SkillGapQueryHandler`
   classifies "Missing" deterministically in code when hybrid search
   returns zero results; the LLM classifier is never even called in that
   case (see `SkillGapQueryHandlerTests`).
3. **Never submit an application without approval** — `generate_tailored_cv`
   is the only tool not marked `ReadOnly`, so it's the only one that
   triggers TrueForge's approval gate (default `require_approval_for_tools`).
4. **Never execute arbitrary host commands** — the CV quality-check flow
   runs exclusively in TrueForge's sandbox (Daytona), never in this
   codebase or on the host running `LocalRag.Mcp`.
5. **Never expose private CV data unnecessarily** — audit logs
   (`ToolAudit`) log tool name, truncated input, and output *length* only
   — never full CV text or full tool output.
6. **Validate tool inputs** — `ToolInput.RequireNonEmpty` rejects empty or
   oversized (>50,000 char) input on every tool that takes free text.
7. **Limit tool permissions** — the agent is scoped to exactly one MCP
   server (`localrag`); it has no filesystem, shell, or network tools
   beyond what TrueForge's sandbox itself provides.
8. **Log important agent actions** — every tool call logs entry (tool +
   truncated input), success (output length), or failure (error message)
   via standard `ILogger`.
9. **Make tool calls observable** — TrueForge's UI shows each tool call
   and result inline by default; nothing was built to suppress or hide
   this.
10. **Distinguish facts from recommendations** — skill classifications
    and CV bullets always carry their source evidence separately from any
    summary judgment; `MissingSkills` is computed in code, never phrased
    by the LLM as an opinion.

## Limitations

Being direct about what's real vs. not:

- **Confirmed working end-to-end, with real issues found and fixed along
  the way.** Every piece was built and verified against a *live* TrueForge
  v0.1.4 instance during development (real API calls, real OpenAPI
  schema), then actually run against a real Ollama, real CV documents, and
  multiple real model providers (Ollama, Groq, Gemini). What that surfaced
  is captured in Troubleshooting above — a Groq-specific incompatibility
  that isn't fixable from this codebase, a Windows/WSL networking gap, and
  concrete tool-calling reliability limits at small local model sizes.
- **Sandbox requires a paid/free-tier Daytona account.** We tested this
  live: `PUT /settings/sandbox-providers` only accepts `type: "daytona"`
  in this version, and it validates the key against Daytona's real API
  immediately. A "local sandbox fallback" log line appears on TrueForge
  startup but isn't reachable through the public settings API — if you
  want the sandboxed quality check working, you need a Daytona key.
  Everything else works without one.
- **`llama3.2:3b` as the agent's own reasoning model is a real risk.**
  It's adequate for the narrow, single-shot extraction/classification
  calls LocalRag makes internally, but tool-calling reliability as the
  agent's *decision-making* model (which tool, when, with what arguments)
  is unproven at this size. If the demo is unreliable, swap in a larger
  model — see the commented block in `scripts/setup-trueforge.sh`.
- **No automated end-to-end test of subagent delegation.** The
  `dynamic_sub_agents.enabled: true` default was confirmed via the live
  API schema, not via an actual multi-agent conversation trace.
- **Angular frontend is not wired to TrueForge.** TrueForge's own bundled
  UI is the working demo path. Connecting the existing Angular app
  requires a proxy layer that wasn't built (see "Running" above for why).
- **Unit tests were written but not executed in this environment**
  (`SkillGapQueryHandlerTests`, `InterviewAndCvTailoringHandlerTests`) —
  same NuGet restriction as above. Run `dotnet test` locally before
  trusting them.
- **Observability is TrueForge's default UI, not custom-built.**
  TrueForge's chat UI already gives concise operational traces without
  hidden chain-of-thought, per its own docs, so nothing custom was added;
  this wasn't independently re-verified live.