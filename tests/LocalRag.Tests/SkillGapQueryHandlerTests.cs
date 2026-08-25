using LocalRag.Application.Contracts;
using LocalRag.Application.UseCases;
using LocalRag.Domain.Entities;
using NSubstitute;
using Xunit;

namespace LocalRag.Tests;

/// <summary>
/// Tests the hallucination-protection guarantees in <see cref="SkillGapQueryHandler"/>
/// (master prompt §24 "Hallucination Protection" / §16 guardrails #1-2):
/// a skill is classified "Missing" deterministically — without ever calling the
/// LLM classifier — whenever hybrid search returns no evidence.
/// </summary>
public class SkillGapQueryHandlerTests
{
    private static JobDescriptionAnalysis MakeJd(
        List<string>? required = null, List<string>? preferred = null) => new()
        {
            Title = "Senior .NET Developer",
            RequiredSkills = required ?? ["ASP.NET Core"],
            PreferredSkills = preferred ?? []
        };

    private static HybridSearchResult MakeResult(string content = "Built REST APIs with ASP.NET Core.") => new()
    {
        Chunk = new DocumentChunk
        {
            Content = content,
            Vector = [],
            SourceFile = "cv.pdf",
            ChunkIndex = 0,
            PageNumber = 1
        },
        VectorScore = 0.8f,
        Bm25Score = 0.5f,
        CombinedScore = 0.02f,
        Rank = 1
    };

    [Fact]
    public async Task NoEvidence_ClassifiesAsMissing_WithoutCallingClassifier()
    {
        var jdExtractor = Substitute.For<IJobDescriptionExtractor>();
        jdExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeJd());

        var search = Substitute.For<IHybridSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(new List<HybridSearchResult>()); // no evidence

        var classifier = Substitute.For<ISkillClassifier>();

        var handler = new SkillGapQueryHandler(jdExtractor, search, classifier);

        var report = await handler.Handle(new SkillGapQuery("some JD text"), CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal("Missing", assessment.Classification);
        Assert.Empty(assessment.Evidence);

        // The LLM classifier must never be consulted when there's no evidence —
        // "Missing" must be a deterministic fact, not an LLM opinion.
        await classifier.DidNotReceive().ClassifyAsync(
            Arg.Any<string>(), Arg.Any<IReadOnlyList<HybridSearchResult>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task WithEvidence_DelegatesClassificationToClassifier()
    {
        var jdExtractor = Substitute.For<IJobDescriptionExtractor>();
        jdExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeJd());

        var results = new List<HybridSearchResult> { MakeResult() };
        var search = Substitute.For<IHybridSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(results);

        var classifier = Substitute.For<ISkillClassifier>();
        classifier.ClassifyAsync(Arg.Any<string>(), Arg.Any<IReadOnlyList<HybridSearchResult>>(), Arg.Any<CancellationToken>())
            .Returns(("Strong Match", "Directly demonstrates the skill."));

        var handler = new SkillGapQueryHandler(jdExtractor, search, classifier);

        var report = await handler.Handle(new SkillGapQuery("some JD text"), CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal("Strong Match", assessment.Classification);
        Assert.Single(assessment.Evidence);
        Assert.Equal("cv.pdf", assessment.Evidence[0].Source);
    }

    [Fact]
    public async Task DuplicateSkillAcrossRequiredAndPreferred_KeepsRequiredTag()
    {
        var jdExtractor = Substitute.For<IJobDescriptionExtractor>();
        jdExtractor.ExtractAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(MakeJd(
                required: ["Azure"],
                preferred: ["Azure"])); // same skill listed both ways

        var search = Substitute.For<IHybridSearchService>();
        search.SearchAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<float>(), Arg.Any<float>(), Arg.Any<CancellationToken>())
            .Returns(new List<HybridSearchResult>());

        var classifier = Substitute.For<ISkillClassifier>();
        var handler = new SkillGapQueryHandler(jdExtractor, search, classifier);

        var report = await handler.Handle(new SkillGapQuery("jd"), CancellationToken.None);

        var assessment = Assert.Single(report.Assessments);
        Assert.Equal(SkillRequirementType.Required, assessment.RequirementType);
    }
}