using System.ComponentModel;
using System.Text.Json;
using LocalRag.Application.UseCases;
using LocalRag.Mcp.Audit;
using MediatR;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LocalRag.Mcp.Tools;

/// <summary>
/// Exposes full reconstructed CV text as an MCP tool. This is the data
/// source for sandboxed quality-check flow: the agent calls this,
/// writes a check script, and runs it in TrueForge's sandbox (Code Mode) —
/// no quality-check logic lives in this codebase, by design.
/// </summary>
[McpServerToolType]
public sealed class CvDocumentTools
{
    private readonly IMediator _mediator;
    private readonly ILogger<CvDocumentTools> _logger;

    public CvDocumentTools(IMediator mediator, ILogger<CvDocumentTools> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [McpServerTool(Name = "get_full_cv_text", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Returns the full reconstructed text of every indexed CV/resume document, " +
        "keyed by source filename. Unlike search_my_cv (which returns only the most " +
        "relevant snippets for a query), this returns the complete text — use it when " +
        "you need to check the whole document, e.g. for a CV quality check " +
        "(required sections present, duplicate content, formatting issues, keyword " +
        "coverage) that you'll run as a script in your sandbox.")]
    public async Task<string> GetFullCvTextAsync(CancellationToken ct = default)
    {
        // No user-supplied input to validate — this tool takes no parameters.
        ToolAudit.LogCall(_logger, "get_full_cv_text", "(no input)");

        try
        {
            var documents = await _mediator.Send(new GetFullCvTextQuery(), ct);

            var payload = new
            {
                documents = documents.Select(kv => new { sourceFile = kv.Key, text = kv.Value })
            };

            var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
            {
                WriteIndented = false
            });

            // Deliberately log only the byte length, never the CV text itself —
            // guardrail #5: "Never expose private CV data unnecessarily," which
            // extends to logs, not just tool responses.
            ToolAudit.LogResult(_logger, "get_full_cv_text", json);
            return json;
        }
        catch (Exception ex)
        {
            ToolAudit.LogError(_logger, "get_full_cv_text", ex);
            throw;
        }
    }
}