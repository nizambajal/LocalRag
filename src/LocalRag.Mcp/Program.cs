using LocalRag.Application;
using LocalRag.Infrastructure;
using LocalRag.Infrastructure.Configuration;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
builder.Host.UseSerilog((ctx, cfg) =>
    cfg.MinimumLevel.Debug()
       .WriteTo.Console(outputTemplate:
           "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
       .ReadFrom.Configuration(ctx.Configuration));

// ── Settings ──────────────────────────────────────────────────────────────────
builder.Services.Configure<RagOptions>(
    builder.Configuration.GetSection(RagOptions.SectionName));

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

// ── Load vector store on startup (same as LocalRag.API) ───────────────────────
// The MCP server is read-only against the existing index — ingestion still
// happens via LocalRag.Worker, which must be running (e.g. hosted by the API).
using (var scope = app.Services.CreateScope())
{
    var vectorStore = scope.ServiceProvider
        .GetRequiredService<LocalRag.Application.Contracts.IVectorStore>();
    await vectorStore.LoadAsync();
    Log.Information("MCP server: vector store ready. Vectors: {Count}", vectorStore.Count);
}

// ── MCP endpoints ─────────────────────────────────────────────────────────────
app.MapMcp("/mcp");

app.Run();