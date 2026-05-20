using LocalRag.Application;
using LocalRag.Infrastructure;
using LocalRag.Infrastructure.Configuration;
using LocalRag.Worker;
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

// ── Layers ────────────────────────────────────────────────────────────────────
builder.Services.AddApplication();
builder.Services.AddInfrastructure(builder.Configuration);

// ── Background worker (runs inside the API process during dev) ────────────────
builder.Services.AddHostedService<IndexingWorker>();

// ── API ───────────────────────────────────────────────────────────────────────
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
    c.SwaggerDoc("v1", new() { Title = "LocalRag API", Version = "v1" }));

// ── CORS for Angular ──────────────────────────────────────────────────────────
var allowedOrigins = builder.Configuration
    .GetSection("Cors:AllowedOrigins")
    .Get<string[]>() ?? ["http://localhost:4200", "http://192.168.0.109:4200"];

//builder.Services.AddCors(opt =>
//    opt.AddDefaultPolicy(p =>
//        p.WithOrigins(allowedOrigins)
//         .AllowAnyMethod()
//         .AllowAnyHeader()));

builder.WebHost.UseUrls(
    "http://0.0.0.0:51413"
);

builder.Services.AddCors(opt =>
    opt.AddDefaultPolicy(p =>
        p.AllowAnyOrigin()
         .AllowAnyMethod()
         .AllowAnyHeader()));

var app = builder.Build();

// ── Load vector store on startup ──────────────────────────────────────────────
using (var scope = app.Services.CreateScope())
{
    var vectorStore = scope.ServiceProvider
        .GetRequiredService<LocalRag.Application.Contracts.IVectorStore>();
    await vectorStore.LoadAsync();
    Log.Information("Vector store ready. Vectors: {Count}", vectorStore.Count);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors();
app.UseAuthorization();
app.MapControllers();

app.Run();