#!/usr/bin/env bash
# ─────────────────────────────────────────────────────────────────────────────
# Sets up TrueForge to drive the LocalRag CV agent. SAFE TO RE-RUN - every
# step upserts in place (PUT for mcp-servers/model-providers/sandbox-providers,
# GET-then-PUT-or-POST for the agent) rather than creating duplicates.
#
# Requires: curl, jq (https://jqlang.org - a single small binary, much easier
# than fighting Windows' Python App Execution Alias stubs).
#   Windows (Git Bash / PowerShell):  winget install jqlang.jq
#   macOS:                             brew install jq
#   Ubuntu/WSL:                        sudo apt install jq
#   Or just download the .exe/binary directly: https://github.com/jqlang/jq/releases
#
# Verified live against TrueForge v0.1.4 (2026-08-22):
#   - POST on an existing name returns 409 Conflict (does NOT duplicate, but
#     DOES abort the script under `set -e`).
#   - PUT on mcp-servers / model-providers / sandbox-providers cleanly
#     updates in place by name - no duplicates, confirmed via a live
#     before/after GET.
#   - Agents have no PUT-by-name endpoint - only PUT /agents/{id} - so we
#     GET /api/v1/agents, look up the id by name, and PUT to that id if
#     found, POST only if not found.
# TrueForge's standalone-mode SQLite DB lives under your home directory
# (~/.local/share/trueforge/db/), not per-project-folder, so whatever you
# registered on a previous run is still there even in a fresh terminal.
#
# Prerequisites (run these in separate terminals first):
#   1. Ollama running with a model pulled:      ollama serve
#                                                ollama pull llama3.2:latest
#   2. LocalRag.Mcp running:                    dotnet run --project src/LocalRag.Mcp
#      (this in turn needs LocalRag.API or LocalRag.Worker to have already
#      built an index - see the main README)
#   3. OPTIONAL, for the sandboxed CV quality-check tool (§12) to actually
#      run code: a Daytona account + API key (https://app.daytona.io).
#      "daytona" is the ONLY sandbox provider type exposed via the settings
#      API in this version - no free/local fallback reachable through it.
#      Without DAYTONA_API_KEY set, this script skips sandbox setup and the
#      agent runs fine without it.
#
# WSL / cross-boundary note: if TrueForge runs in one environment (e.g. WSL)
# and LocalRag.Mcp/Ollama run in another (e.g. Windows host), `localhost`
# won't cross that boundary - override LOCALRAG_MCP_URL / OLLAMA_BASE_URL
# with the actual reachable IP (include the port - LocalRag.Mcp defaults to
# :5014, Ollama to :11434).
#
# Model note: if the AGENT (as opposed to LocalRag's own internal RAG calls)
# struggles with tool-calling reliability on a small local model, override
# AGENT_MODEL to point at a larger/hosted provider already registered above
# - e.g. AGENT_MODEL="groq-free/gpt-oss-20b" ./scripts/setup-trueforge.sh
#
# Usage:
#   chmod +x scripts/setup-trueforge.sh
#   ./scripts/setup-trueforge.sh
# ─────────────────────────────────────────────────────────────────────────────
set -euo pipefail

if ! command -v jq >/dev/null 2>&1; then
  echo "ERROR: jq is required but not found on PATH." >&2
  echo "  Windows: winget install jqlang.jq" >&2
  echo "  macOS:   brew install jq" >&2
  echo "  Linux:   sudo apt install jq   (or your distro's package manager)" >&2
  echo "  Or download a binary directly: https://github.com/jqlang/jq/releases" >&2
  exit 1
fi

TRUEFORGE_URL="${TRUEFORGE_URL:-http://localhost:8790}"
MCP_URL="${LOCALRAG_MCP_URL:-http://localhost:5014/mcp}"
OLLAMA_URL="${OLLAMA_BASE_URL:-http://localhost:11434}"
OLLAMA_MODEL="${OLLAMA_MODEL:-qwen3:4b}"
DAYTONA_API_KEY="${DAYTONA_API_KEY:-}"
AGENT_NAME="cv-career-assistant"
AGENT_MODEL="ollama-local/llama3-2-3b"

echo "── 1/4: Upserting LocalRag as an MCP server ─────────────────────────────"
MCP_SERVER_BODY=$(jq -n \
  --arg url "$MCP_URL" \
  '{manifest: {type: "remote", name: "localrag", url: $url,
     description: "Search my CV, analyze job descriptions, and compare my skills against a role"}}')

curl -sf -X PUT "$TRUEFORGE_URL/api/v1/settings/mcp-servers" \
  -H "Content-Type: application/json" \
  -d "$MCP_SERVER_BODY" | jq .

echo ""
echo "── 2/4: Upserting Ollama as a model provider ────────────────────────────"
echo "   NOTE: a 3B local model is fine for the RAG answer/extraction calls"
echo "   LocalRag already makes internally, but tool-calling reliability as"
echo "   the AGENT's own reasoning model may be weak (multi-tool selection"
echo "   with several tools offered at once degrades sharply at this size)."
echo "   If agent turns fail to call tools correctly, override AGENT_MODEL to"
echo "   point at a larger local model or an already-registered hosted"
echo "   provider - see the commented block further down and the note at the"
echo "   top of this file."

MODEL_PROVIDER_BODY=$(jq -n \
  --arg baseUrl "$OLLAMA_URL/v1" \
  --arg modelId "$OLLAMA_MODEL" \
  '{manifest: {type: "custom", name: "ollama-local", base_url: $baseUrl,
     auth: {api_key: "not-needed"},
     models: [{model_id: $modelId, name: "llama3-2-3b", properties: {context_length: 128000}}]}}')

curl -sf -X PUT "$TRUEFORGE_URL/api/v1/settings/model-providers" \
  -H "Content-Type: application/json" \
  -d "$MODEL_PROVIDER_BODY" | jq .

# ── Alternative: hosted model provider instead of local Ollama ──────────────
# Uncomment and set ANTHROPIC_API_KEY (never hardcode it) if you'd rather use
# a hosted model as the agent's brain while LocalRag's own RAG pipeline keeps
# using Ollama separately.
#
# ANTHROPIC_BODY=$(jq -n --arg key "$ANTHROPIC_API_KEY" \
#   '{manifest: {type: "anthropic", name: "anthropic-main", auth: {api_key: $key}}}')
# curl -sf -X PUT "$TRUEFORGE_URL/api/v1/settings/model-providers" \
#   -H "Content-Type: application/json" -d "$ANTHROPIC_BODY"

SANDBOX_ENABLED=false
if [ -n "$DAYTONA_API_KEY" ]; then
  echo ""
  echo "── 3/4: Upserting Daytona as the sandbox provider ───────────────────────"
  SANDBOX_BODY=$(jq -n --arg key "$DAYTONA_API_KEY" \
    '{manifest: {type: "daytona", auth: {api_key: $key},
       exec_timeout_ms: 60000, auto_stop_interval_in_minutes: 5,
       auto_archive_interval_in_minutes: 60, auto_delete_interval_in_minutes: 7200}}')

  if curl -sf -X PUT "$TRUEFORGE_URL/api/v1/settings/sandbox-providers" \
    -H "Content-Type: application/json" \
    -d "$SANDBOX_BODY" | jq .; then
    SANDBOX_ENABLED=true
  else
    echo "   Daytona registration failed (bad key?) - continuing without sandbox."
  fi
else
  echo ""
  echo "── 3/4: Skipping sandbox provider (DAYTONA_API_KEY not set) ─────────────"
fi

echo ""
echo "── 4/4: Upserting the $AGENT_NAME agent ──────────────────────────────────"

AGENT_INSTRUCTIONS="You are a CV and job-analysis assistant.
    The candidate's CV is already indexed in the LocalRag MCP server.
    Never ask the user to upload, paste, or provide their CV.
    Use only tools provided by the runtime.
    Never invent tool names.
    Never attempt to discover, list, inspect, or manage tools yourself.
    When the user provides a job description and asks for job-fit analysis:
    1. Call analyze_job_description.
    2. After it returns, call compare_skills.
    3. Do not provide the final fit assessment until compare_skills has returned.
    4. Present the Strong Match, Partial Match, Weak Evidence, and Missing classifications returned by compare_skills.
    5. Include the evidence returned by compare_skills.
    6. Ground every claim about the candidate in CV evidence.
    Do not manually call search_my_cv for every requirement when compare_skills can perform the complete comparison.
    When the user asks for interview preparation, use prepare_interview.
    When the user asks for a tailored CV, use generate_tailored_cv.
    When the user explicitly asks for a complete CV review, use get_full_cv_text.
    Never invent skills, experience, projects, certifications, employers, achievements, or qualifications that are not supported by the CV."

AGENT_MANIFEST=$(jq -n \
  --arg instructions "$AGENT_INSTRUCTIONS" \
  --arg model "$AGENT_MODEL" \
  --argjson sandboxEnabled "$SANDBOX_ENABLED" \
  '{
    model: {name: $model, params: {temperature: 0.1}},
    instructions: $instructions,
    mcp_servers: [{name: "localrag", enable_tools: ["@all"], preload: true}],
    config: {enabled": $sandboxEnabled}}
  }')

# Look up an existing agent by name - agents have no PUT-by-name endpoint,
# only PUT /agents/{id}, so we need the id first.
EXISTING_ID=$(curl -sf "$TRUEFORGE_URL/api/v1/agents" \
  | jq -r --arg name "$AGENT_NAME" '.data[] | select(.name==$name) | .id' | head -n1)

if [ -n "$EXISTING_ID" ]; then
  echo "   Found existing agent (id=$EXISTING_ID) - updating in place."
  curl -sf -X PUT "$TRUEFORGE_URL/api/v1/agents/$EXISTING_ID" \
    -H "Content-Type: application/json" \
    -d "$(jq -n --argjson manifest "$AGENT_MANIFEST" '{manifest: $manifest}')" | jq .
else
  echo "   No existing agent found - creating."
  curl -sf -X POST "$TRUEFORGE_URL/api/v1/agents" \
    -H "Content-Type: application/json" \
    -d "$(jq -n --arg name "$AGENT_NAME" --argjson manifest "$AGENT_MANIFEST" '{name: $name, manifest: $manifest}')" | jq .
fi

echo ""
echo "Done. Open $TRUEFORGE_URL in your browser and chat with $AGENT_NAME."
echo ""
echo "Notes on defaults TrueForge applied automatically (no code needed):"
echo "  - dynamic_sub_agents.enabled = true   → subagents already work"
echo "  - require_approval_for_tools defaults to [\"@write\", \"@destructive\"]"
echo "    → search_my_cv, analyze_job_description, compare_skills, prepare_interview,"
echo "      and get_full_cv_text are all marked ReadOnly and won't pause. Only"
echo "      generate_tailored_cv will trigger the approval UI, since it's the"
echo "      first tool that produces a deliverable."
if [ "$SANDBOX_ENABLED" = "true" ]; then
  echo "  - config.sandbox.enabled = true       → the agent can now write and run"
  echo "    a quality-check script in its Daytona sandbox (see instructions above)."
else
  echo "  - config.sandbox.enabled = false      → set DAYTONA_API_KEY and re-run"
  echo "    this script to enable sandboxed CV quality checks (§12)."
fi