using System.ComponentModel;
using System.Text.Json;
using LocalRag.Application.UseCases;
using LocalRag.Mcp.Audit;
using LocalRag.Mcp.Validation;
using MediatR;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LocalRag.Mcp.Tools;

/// <summary>
/// Exposes the existing LocalRag hybrid search (BM25 + vector, fused via RRF)
/// as an MCP tool. This wraps <see cref="HybridSearchQuery"/> — no retrieval
/// logic is duplicated here.
/// </summary>
[McpServerToolType]
public sealed class CvSearchTools
{
    private readonly IMediator _mediator;
    private readonly ILogger<CvSearchTools> _logger;

    // Instance method ⇒ the SDK resolves this class (and its constructor
    // dependencies) from the DI container for every tool call.
    public CvSearchTools(IMediator mediator, ILogger<CvSearchTools> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [McpServerTool(Name = "search_my_cv", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Searches the candidate's CV/resume for evidence relevant to a query. " +
        "Returns matching chunks with their source file, page number, and relevance " +
        "score. If no evidence is found for a claim, that must be treated as " +
        "'no evidence found' — never invent experience the CV doesn't contain.")]
    public async Task<string> SearchMyCvAsync(
        [Description("What to search for, e.g. 'experience with Azure AI Foundry' " +
                      "or 'ASP.NET Core REST API development'.")]
        string query,
        [Description("Number of top results to return. Default 5.")]
        int topK = 5,
        CancellationToken ct = default)
    {
        query = ToolInput.RequireNonEmpty(query, nameof(query));
        ToolAudit.LogCall(_logger, "search_my_cv", query);

        try
        {
            var response = await _mediator.Send(
                new HybridSearchQuery(query, topK), ct);

            var payload = new
            {
                query,
                results = response.Results.Select(r => new
                {
                    text = r.Content,
                    source = r.SourceFile,
                    page = r.PageNumber,
                    chunkIndex = r.ChunkIndex,
                    score = r.CombinedScore,
                    vectorScore = r.VectorScore,
                    bm25Score = r.Bm25Score
                })
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            ToolAudit.LogResult(_logger, "search_my_cv", json);
            return json;
        }
        catch (Exception ex)
        {
            ToolAudit.LogError(_logger, "search_my_cv", ex);
            throw;
        }
    }
}