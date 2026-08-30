using LocalRag.Application.Contracts;
using LocalRag.Application.UseCases;
using LocalRag.Domain.Entities;
using MediatR;
using NSubstitute;
using Xunit;

namespace LocalRag.Tests;

/// <summary>
/// Tests that <see cref="InterviewPrepQueryHandler"/>'s "potential weak areas"
/// list is computed deterministically from the skill-gap report — never left
/// to the interview-question generator (which could omit or misjudge a gap).
/// </summary>
public class InterviewPrepQueryHandlerTests
{
    private static SkillGapReport MakeReport(params SkillAssessment[] assessments) => new()
    {
        JobTitle = "Senior .NET Developer",
        Assessments = assessments.ToList()
    };

    private static SkillAssessment MakeAssessment(
        string skill, string classification, SkillRequirementType type = SkillRequirementType.Required) => new()
        {
            Skill = skill,
            RequirementType = type,
            Classification = classification,
            Evidence = []
        };

    [Fact]
    public async Task WeakAreas_OnlyIncludeRequiredSkillsWithWeakOrMissingEvidence()
    {
        var mediator = Substitute.For<IMediator>();
        var report = MakeReport(
            MakeAssessment("ASP.NET Core", "Strong Match"),
            MakeAssessment("Kubernetes", "Missing"),
            MakeAssessment("AWS", "Weak Evidence"),
            MakeAssessment("Docker", "Partial Match"),
            MakeAssessment("GraphQL", "Missing", SkillRequirementType.Preferred)); // not required — must be excluded

        mediator.Send(Arg.Any<SkillGapQuery>(), Arg.Any<CancellationToken>())
            .Returns(report);

        var generator = Substitute.For<IInterviewQuestionGenerator>();
        generator.GenerateAsync(Arg.Any<SkillGapReport>(), Arg.Any<CancellationToken>())
            .Returns(new List<InterviewQuestion>());

        var handler = new InterviewPrepQueryHandler(mediator, generator);

        var package = await handler.Handle(new InterviewPrepQuery("jd text"), CancellationToken.None);

        Assert.Contains("Kubernetes", package.PotentialWeakAreas);
        Assert.Contains("AWS", package.PotentialWeakAreas);
        Assert.DoesNotContain("ASP.NET Core", package.PotentialWeakAreas); // strong match
        Assert.DoesNotContain("Docker", package.PotentialWeakAreas);       // partial match
        Assert.DoesNotContain("GraphQL", package.PotentialWeakAreas);      // preferred, not required
    }

    [Fact]
    public async Task NoGaps_ProducesEmptyWeakAreasList()
    {
        var mediator = Substitute.For<IMediator>();
        var report = MakeReport(MakeAssessment("ASP.NET Core", "Strong Match"));
        mediator.Send(Arg.Any<SkillGapQuery>(), Arg.Any<CancellationToken>()).Returns(report);

        var generator = Substitute.For<IInterviewQuestionGenerator>();
        generator.GenerateAsync(Arg.Any<SkillGapReport>(), Arg.Any<CancellationToken>())
            .Returns(new List<InterviewQuestion>());

        var handler = new InterviewPrepQueryHandler(mediator, generator);
        var package = await handler.Handle(new InterviewPrepQuery("jd"), CancellationToken.None);

        Assert.Empty(package.PotentialWeakAreas);
    }
}

/// <summary>
/// Tests that <see cref="TailorCvQueryHandler"/>'s "missing skills" list is
/// computed the same deterministic way as interview prep, and stays
/// consistent regardless of what the CV-tailoring generator produces.
/// </summary>
public class TailorCvQueryHandlerTests
{
    private static SkillGapReport MakeReport(params SkillAssessment[] assessments) => new()
    {
        JobTitle = "Senior .NET Developer",
        Assessments = assessments.ToList()
    };

    private static SkillAssessment MakeAssessment(
        string skill, string classification, SkillRequirementType type = SkillRequirementType.Required) => new()
        {
            Skill = skill,
            RequirementType = type,
            Classification = classification,
            Evidence = []
        };

    [Fact]
    public async Task MissingSkills_MatchesRequiredWeakOrMissingOnly()
    {
        var mediator = Substitute.For<IMediator>();
        var report = MakeReport(
            MakeAssessment("ASP.NET Core", "Strong Match"),
            MakeAssessment("Kubernetes", "Missing"));

        mediator.Send(Arg.Any<SkillGapQuery>(), Arg.Any<CancellationToken>()).Returns(report);

        var generator = Substitute.For<ICvTailoringGenerator>();
        generator.GenerateAsync(Arg.Any<SkillGapReport>(), Arg.Any<CancellationToken>())
            .Returns(new List<TailoredCvSection>());

        var handler = new TailorCvQueryHandler(mediator, generator);
        var result = await handler.Handle(new TailorCvQuery("jd"), CancellationToken.None);

        Assert.Contains("Kubernetes", result.MissingSkills);
        Assert.DoesNotContain("ASP.NET Core", result.MissingSkills);
    }
}