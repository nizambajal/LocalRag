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
┌───────────────────────────────────────────┐
│                 Frontend                  │
│   TrueForge's bundled chat UI (primary) — │
│   existing Angular app optional (§ below) │
└───────────────────┬───────────────────────┘
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
  per the master prompt's own instruction not to add MCP "purely because
  the hackathon mentions it."
- **Subagents**: TrueForge's `dynamic_sub_agents.enabled` defaults to
  `true` (verified against the live v0.1.4 API — see `RuntimeConfig` in
  `GET /api/v1/openapi.json`). The main agent can delegate sub-tasks (e.g.
  a focused "analyze this JD" pass) to a dynamically spun-up subagent
  without any code on our side. **Caveat**: we verified the *config
  default* live, but did not run a full end-to-end delegation trace in
  this environment (that needs a live Ollama + LocalRag.Mcp + browser
  session) — worth confirming once you're running it locally.
- **Sandbox**: used for the CV quality-check flow (§12) — see "Sandbox"
  under Limitations below for the real constraint we found (Daytona
  account required; no free local fallback reachable via the public API).
- **Human approval**: `require_approval_for_tools` defaults to
  `["@write", "@destructive"]`. Every read-only tool (`search_my_cv`,
  `analyze_job_description`, `compare_skills`, `prepare_interview`,
  `get_full_cv_text`) is explicitly annotated `ReadOnly = true` so it
  never pauses. `generate_tailored_cv` is deliberately left `ReadOnly =
  false`, so it's the one tool that triggers TrueForge's approval UI —
  matching §11's requirement with zero custom approval code.
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
| `DAYTONA_API_KEY` | `scripts/setup-trueforge.sh` | Optional — enables the sandboxed CV quality-check tool (§12). See Limitations. |

## Running

Five processes, in order, each in its own terminal:

```bash
# Terminal 1
ollama serve

# Terminal 2 — builds/refreshes the CV index
dotnet run --project src/LocalRag.API
# (or LocalRag.Worker, if you only want ingestion without the old API surface)

# Terminal 3 — exposes the RAG + agent tools over MCP
dotnet run --project src/LocalRag.Mcp

# Terminal 4 — the agent harness
npx @truefoundry/trueforge

# Terminal 5 — one-time per fresh TrueForge instance
chmod +x scripts/setup-trueforge.sh
./scripts/setup-trueforge.sh
```

Then open the URL TrueForge prints (default `http://localhost:8790`) and
chat with `cv-career-assistant`.

**Frontend note**: TrueForge ships its own chat UI with tool-call
visibility built in, which already satisfies §17/§18's observability and
"simple professional UI" requirements out of the box. The existing Angular
app is not wired to TrueForge yet — doing that well means a thin ASP.NET
Core proxy (per the §19 architecture diagram: Angular → ASP.NET Core →
TrueForge) so the browser isn't calling TrueForge directly, plus a real
decision about auth. That proxy was deliberately **not** built blind in
this session — TrueForge's session/turn API wasn't verified in enough
depth to write real endpoint code without guessing, which would violate
§22's "do not invent APIs" rule. Worth doing as a follow-up once you
confirm the schema against a running instance's `/api/v1/openapi.json`.

## Demo

Matching §21/§28 exactly, in one continuous TrueForge chat session:

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

Guardrails implemented, mapped to §16:

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
   (§12) runs exclusively in TrueForge's sandbox (Daytona), never in this
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

- **Not run end-to-end.** Every piece here was built and, where possible,
  verified against a *live* TrueForge v0.1.4 instance (real API calls,
  real OpenAPI schema, a real rejected Daytona key) — but never against a
  live LocalRag.Mcp + real Ollama + real CV documents together, because
  this sandbox can't reach NuGet.org to restore the .NET packages. You are
  the first to actually run the full stack together. Expect to fix small
  things.
- **Sandbox requires a paid/free-tier Daytona account.** We tested this
  live: `PUT /settings/sandbox-providers` only accepts `type: "daytona"`
  in this version, and it validates the key against Daytona's real API
  immediately. A "local sandbox fallback" log line appears on TrueForge
  startup but isn't reachable through the public settings API — if you
  want §12's sandboxed quality check working, you need a Daytona key.
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
- **Observability is TrueForge's default UI, not custom-built.** §17
  asked for concise operational traces without hidden chain-of-thought —
  TrueForge's chat UI already does this per its own docs, so nothing
  custom was added; this wasn't independently re-verified live.