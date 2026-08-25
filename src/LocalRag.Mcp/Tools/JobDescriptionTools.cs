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
/// Exposes structured job-description extraction as an MCP tool. Wraps
/// <see cref="JdAnalysisQuery"/> — extraction only, no fit judgment here.
/// </summary>
[McpServerToolType]
public sealed class JobDescriptionTools
{
    private readonly IMediator _mediator;
    private readonly ILogger<JobDescriptionTools> _logger;

    public JobDescriptionTools(IMediator mediator, ILogger<JobDescriptionTools> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [McpServerTool(Name = "analyze_job_description", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Extracts structured requirements from a free-text job description: title, " +
        "required skills, preferred skills, minimum years of experience, " +
        "responsibilities, technologies, domain knowledge, and certifications. " +
        "This tool ONLY extracts and structures what the job description states — " +
        "it does not judge whether any candidate is qualified. To assess fit, call " +
        "search_my_cv separately for each requirement and compare the evidence yourself.")]
    public async Task<string> AnalyzeJobDescriptionAsync(
        [Description("The full job description text, pasted as-is.")]
        string jobDescription,
        CancellationToken ct = default)
    {
        jobDescription = ToolInput.RequireNonEmpty(jobDescription, nameof(jobDescription));
        ToolAudit.LogCall(_logger, "analyze_job_description", jobDescription);

        try
        {
            var analysis = await _mediator.Send(new JdAnalysisQuery(jobDescription), ct);

            var json = JsonSerializer.Serialize(analysis, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            });

            ToolAudit.LogResult(_logger, "analyze_job_description", json.Length);
            return json;
        }
        catch (Exception ex)
        {
            ToolAudit.LogError(_logger, "analyze_job_description", ex);
            throw;
        }
    }
}