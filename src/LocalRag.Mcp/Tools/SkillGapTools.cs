using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using LocalRag.Application.UseCases;
using LocalRag.Mcp.Audit;
using LocalRag.Mcp.Validation;
using MediatR;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace LocalRag.Mcp.Tools;

/// <summary>
/// Exposes the skill-gap comparison workflow as an MCP tool. Wraps
/// <see cref="SkillGapQuery"/> — orchestrates JD extraction, CV search per
/// skill, and evidence-grounded classification. This is the "Analyze JD →
/// Search CV → Classify" pipeline from the master prompt's Phase 1 target.
/// </summary>
[McpServerToolType]
public sealed class SkillGapTools
{
    private readonly IMediator _mediator;
    private readonly ILogger<SkillGapTools> _logger;

    public SkillGapTools(IMediator mediator, ILogger<SkillGapTools> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [McpServerTool(Name = "compare_skills", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Given a job description, extracts its required and preferred skills, " +
        "searches the candidate's CV for evidence of each skill, and classifies " +
        "each as 'Strong Match', 'Partial Match', 'Weak Evidence', or 'Missing'. " +
        "Every classification includes the CV evidence it's based on — a skill is " +
        "only ever 'Missing' when no CV evidence was found. Use this before making " +
        "any overall suitability judgment; the classification of individual skills " +
        "here is evidence-based, but whether the candidate is overall qualified for " +
        "the role is a judgment for you to make after reviewing this report.")]
    public async Task<string> CompareSkillsAsync(
        [Description("The full job description text, pasted as-is.")]
        string jobDescription,
        [Description("How much CV evidence to retrieve per skill. Default 3.")]
        int evidenceTopK = 3,
        CancellationToken ct = default)
    {
        jobDescription = ToolInput.RequireNonEmpty(jobDescription, nameof(jobDescription));
        ToolAudit.LogCall(_logger, "compare_skills", jobDescription);

        try
        {
            var report = await _mediator.Send(
                new SkillGapQuery(jobDescription, evidenceTopK), ct);

            var json = JsonSerializer.Serialize(report, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            });

            ToolAudit.LogResult(_logger, "compare_skills", json);
            return json;
        }
        catch (Exception ex)
        {
            ToolAudit.LogError(_logger, "compare_skills", ex);
            throw;
        }
    }
}