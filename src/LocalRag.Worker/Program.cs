using LocalRag.Application;
using LocalRag.Infrastructure;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Worker;
using Serilog;

var builder = Host.CreateApplicationBuilder(args);

// ── Serilog ───────────────────────────────────────────────────────────────────
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Debug()
    .WriteTo.Console(outputTemplate:
        "[{Timestamp:HH:mm:ss} {Level:u3}] {Message:lj}{NewLine}{Exception}")
    .CreateLogger();

builder.Logging.ClearProviders();
builder.Logging.AddSerilog(Log.Logger);

// ── Settings ──────────────────────────────────────────────────────────────────
builder.Services.Configure<RagOptions>(
    builder.Configuration.GetSection(RagOptions.SectionName));

// ── Application + Infrastructure layers ──────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ── Background worker ─────────────────────────────────────────────────────────
builder.Services.AddHostedService<IndexingWorker>();

var host = builder.Build();

// ── Load existing vector store before worker starts ───────────────────────────
var vectorStore = host.Services.GetRequiredService<LocalRag.Application.Contracts.IVectorStore>();
await vectorStore.LoadAsync();
Log.Information("Vector store ready. Vectors: {Count}", vectorStore.Count);

await host.RunAsync();