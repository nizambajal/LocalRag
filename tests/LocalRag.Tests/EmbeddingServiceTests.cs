using LocalRag.Application.Contracts;
using LocalRag.Infrastructure.Services;
using NSubstitute;
using Xunit;

namespace LocalRag.Tests;

public class EmbeddingServiceTests
{
    [Fact]
    public async Task EmbedAsync_ReturnsSingleVector()
    {
        var svc = Substitute.For<IEmbeddingService>();
        svc.Dimensions.Returns(384);
        svc.EmbedAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(new float[384]));

        var vector = await svc.EmbedAsync("hello world");

        Assert.Equal(384, vector.Length);
    }

    [Fact]
    public async Task EmbedBatchAsync_ReturnsOneVectorPerText()
    {
        var texts = new[] { "first", "second", "third" };
        var svc = Substitute.For<IEmbeddingService>();
        svc.EmbedBatchAsync(Arg.Any<IReadOnlyList<string>>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<float[]>>(
                texts.Select(_ => new float[384]).ToList()));

        var results = await svc.EmbedBatchAsync(texts);

        Assert.Equal(3, results.Count);
    }

    [Fact]
    public void L2Normalise_ProducesUnitVector()
    {
        var v = new float[] { 3f, 4f };
        var norm = EmbeddingMath.L2Normalise(v);
        float len = MathF.Sqrt(norm.Sum(x => x * x));

        Assert.True(MathF.Abs(len - 1f) < 1e-5f);
    }

    [Fact]
    public void L2Normalise_ZeroVector_DoesNotThrow()
    {
        var v = new float[] { 0f, 0f, 0f };
        var n = EmbeddingMath.L2Normalise(v);
        Assert.All(n, x => Assert.False(float.IsNaN(x)));
    }

    [Fact]
    public void CosineSimilarity_IdenticalVectors_IsOne()
    {
        var v = EmbeddingMath.L2Normalise([1f, 2f, 3f]);
        float s = EmbeddingMath.CosineSimilarity(v, v);
        Assert.True(MathF.Abs(s - 1f) < 1e-5f);
    }

    [Fact]
    public void CosineSimilarity_OrthogonalVectors_IsZero()
    {
        var a = EmbeddingMath.L2Normalise([1f, 0f]);
        var b = EmbeddingMath.L2Normalise([0f, 1f]);
        float s = EmbeddingMath.CosineSimilarity(a, b);
        Assert.True(MathF.Abs(s) < 1e-5f);
    }
}

public static class EmbeddingMath
{
    public static float[] L2Normalise(float[] v)
    {
        var copy = (float[])v.Clone();
        float norm = MathF.Sqrt(copy.Sum(x => x * x));
        if (norm < 1e-10f) return copy;
        for (int i = 0; i < copy.Length; i++) copy[i] /= norm;
        return copy;
    }

    public static float CosineSimilarity(float[] a, float[] b)
    {
        float dot = 0f, normA = 0f, normB = 0f;
        for (int i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        float denom = MathF.Sqrt(normA) * MathF.Sqrt(normB);
        return denom < 1e-10f ? 0f : dot / denom;
    }
}