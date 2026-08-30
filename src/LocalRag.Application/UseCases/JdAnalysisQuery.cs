using LocalRag.Application.Contracts;
using LocalRag.Domain.Entities;
using MediatR;

namespace LocalRag.Application.UseCases;

// ── Query ─────────────────────────────────────────────────────────────────────

public record JdAnalysisQuery(string JobDescriptionText) : IRequest<JobDescriptionAnalysis>;

// ── Handler ───────────────────────────────────────────────────────────────────

public sealed class JdAnalysisQueryHandler
    : IRequestHandler<JdAnalysisQuery, JobDescriptionAnalysis>
{
    private readonly IJobDescriptionExtractor _extractor;

    public JdAnalysisQueryHandler(IJobDescriptionExtractor extractor)
        => _extractor = extractor;

    public Task<JobDescriptionAnalysis> Handle(JdAnalysisQuery request, CancellationToken ct)
        => _extractor.ExtractAsync(request.JobDescriptionText, ct);
}