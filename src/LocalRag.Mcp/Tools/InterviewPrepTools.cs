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
/// Exposes interview preparation as an MCP tool. Wraps
/// <see cref="InterviewPrepQuery"/>, which internally reuses the skill-gap
/// workflow so questions and evidence stay consistent with compare_skills.
/// </summary>
[McpServerToolType]
public sealed class InterviewPrepTools
{
    private readonly IMediator _mediator;
    private readonly ILogger<InterviewPrepTools> _logger;

    public InterviewPrepTools(IMediator mediator, ILogger<InterviewPrepTools> logger)
    {
        _mediator = mediator;
        _logger = logger;
    }

    [McpServerTool(Name = "prepare_interview", ReadOnly = true, Destructive = false, Idempotent = true, OpenWorld = false)]
    [Description(
        "Given a job description, generates categorized interview questions " +
        "(Technical, Scenario, Architecture, C#, .NET, SQL, Azure, AI/RAG, " +
        "Behavioral, Candidate-Specific) plus a list of potential weak areas. " +
        "Candidate-Specific questions include a model answer ONLY when the CV has " +
        "strong evidence for that skill — questions about weak/missing skills are " +
        "honest probing questions with no invented model answer. Call this after " +
        "compare_skills if you want the fuller interview-prep package; this tool " +
        "runs the same skill-gap analysis internally, so you don't need to call " +
        "compare_skills first.")]
    public async Task<string> PrepareInterviewAsync(
        [Description("The full job description text, pasted as-is.")]
        string jobDescription,
        CancellationToken ct = default)
    {
        jobDescription = ToolInput.RequireNonEmpty(jobDescription, nameof(jobDescription));
        ToolAudit.LogCall(_logger, "prepare_interview", jobDescription);

        try
        {
            var package = await _mediator.Send(new InterviewPrepQuery(jobDescription), ct);

            var json = JsonSerializer.Serialize(package, new JsonSerializerOptions
            {
                WriteIndented = false,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
            });

            ToolAudit.LogResult(_logger, "prepare_interview", json);
            return json;
        }
        catch (Exception ex)
        {
            ToolAudit.LogError(_logger, "prepare_interview", ex);
            throw;
        }
    }
}