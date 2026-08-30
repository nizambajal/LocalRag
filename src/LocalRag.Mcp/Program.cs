using LocalRag.Application;
using LocalRag.Infrastructure;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Mcp.Audit;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
// Two sinks: console (as before) and a rolling daily file under logs/, so
// tool call request/response audit trail (ToolAudit) survives after the
// terminal scrolls or closes — useful when debugging agent behavior across
// a long TrueForge session.
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.MinimumLevel.Debug()
       .WriteTo.Console(outputTemplate:
           "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
       .WriteTo.File(
           path: "logs/localrag-mcp-.log",
           rollingInterval: RollingInterval.Day,
           retainedFileCountLimit: 14,
           outputTemplate:
               "[{Timestamp:yyyy-MM-dd HH:mm:ss.fff} {Level:u3}] {Message:lj}{NewLine}{Exception}")
       .ReadFrom.Configuration(ctx.Configuration));

// ── Settings ──────────────────────────────────────────────────────────────────
builder.Services.Configure<RagOptions>(
    builder.Configuration.GetSection(RagOptions.SectionName));

// Verbose tool logging (full request/response, including CV text) is off by
// default — see RagOptions.VerboseToolLogging and ToolAudit for the privacy
// rationale. Read it once at startup since it's a debug-session toggle, not
// something that needs to change per-request.
ToolAudit.VerboseEnabled = builder.Configuration
    .GetValue<bool>($"{RagOptions.SectionName}:{nameof(RagOptions.VerboseToolLogging)}");

// ── Reuse the existing Application + Infrastructure layers ────────────────────
// Same MediatR handlers, same BM25/vector search, same ONNX embeddings, same
// ingestion services as LocalRag.API — nothing here is re-implemented.
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ── MCP server (HTTP transport) ────────────────────────────────────────────────
builder.Services
    .AddMcpServer(options =>
    {
        options.ServerInfo = new()
        {
            Name = "LocalRag.Mcp",
            Version = "0.1.0"
        };
    })
    .WithHttpTransport(options =>
    {
        // Stateless is recommended when the server doesn't need to push
        // server-initiated requests (sampling/elicitation) back to the client.
        options.Stateless = true;
    })
    .WithToolsFromAssembly();

var app = builder.Build();

// Logs EVERY HTTP request that reaches this server — one structured line
// per request (method, path, status code, elapsed ms) — regardless of
// whether it ever reaches a tool method. This is what catches requests
// that fail before/outside our own ToolAudit calls (e.g. a malformed or
// unknown MCP tool-call request), which ToolAudit alone can never see.
app.UseSerilogRequestLogging();

// ── Load vector store on startup (same as LocalRag.API) ───────────────────────
// The MCP server is read-only against the existing index — ingestion still
// happens via LocalRag.Worker, which must be running (e.g. hosted by the API).
using (var scope = app.Services.CreateScope())
{
    var vectorStore = scope.ServiceProvider
        .GetRequiredService<LocalRag.Application.Contracts.IVectorStore>();
    await vectorStore.LoadAsync();
    Log.Information("MCP server: vector store ready. Vectors: {Count}", vectorStore.Count);
    if (ToolAudit.VerboseEnabled)
        Log.Warning("VerboseToolLogging is ON — full tool request/response content " +
                    "(including CV text) will be written to logs/. Remember to turn " +
                    "this back off when done debugging.");
}

// ── MCP endpoints ─────────────────────────────────────────────────────────────
app.MapMcp("/mcp");

app.Run();