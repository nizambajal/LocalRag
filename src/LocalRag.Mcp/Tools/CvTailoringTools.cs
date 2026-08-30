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
/// Exposes CV tailoring as an MCP tool. Deliberately
/// NOT marked ReadOnly — unlike the other tools, this one produces a
/// deliverable the candidate would act on, so TrueForge's default
/// approval policy (require_approval_for_tools: ["@write", "@destructive"])
/// pauses for human approval before returning results.
/// see scripts/setup-trueforge.sh for how the agent is configured.
/// </summary>
[McpServerToolType]
public sealed class CvTailoringTools
{
    private readonly IMediator _mediator;
    private readonly ILogger<CvTailoringTools> _logger;

    public CvTailoringTools(IMediator mediator, ILogger<CvTailoringTools> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [McpServerTool(Name = "generate_tailored_cv", ReadOnly = false, Destructive = false, Idempotent = false, OpenWorld = false)]
    [Description(
        "Generates a tailored CV for a specific job description, organized into " +
        "sections with bullet points. Every bullet is labeled 'Existing Experience' " +
        "or 'Suggested Wording' and cites the CV evidence it's drawn from — nothing " +
        "is invented. Skills the CV doesn't support are listed separately as " +
        "missingSkills and never appear in the tailored sections. This produces a " +
        "deliverable the candidate would act on, so present it for their review " +
        "before treating it as final.")]
    public async Task<string> GenerateTailoredCvAsync(
        [Description("The full job description text, pasted as-is.")]
        string jobDescription,
        CancellationToken ct = default)
    {
        jobDescription = ToolInput.RequireNonEmpty(jobDescription, nameof(jobDescription));
        ToolAudit.LogCall(_logger, "generate_tailored_cv", jobDescription);

        try
        {
            var result = await _mediator.Send(new TailorCvQuery(jobDescription), ct);

            var json = JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            ToolAudit.LogResult(_logger, "generate_tailored_cv", json);
            return json;
        }
        catch (Exception ex)
        {
            ToolAudit.LogError(_logger, "generate_tailored_cv", ex);
            throw;
        }
    }
}