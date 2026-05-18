using LocalRag.Application.Contracts;
using LocalRag.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace LocalRag.Infrastructure.Services;

/// <summary>
/// Generates dense text embeddings using a locally stored ONNX model.
///
/// Compatible models (download separately — see models/onnx/README.md):
///   • all-MiniLM-L6-v2  → 384 dimensions  (fast, good quality)
///   • BGE-small-en-v1.5 → 384 dimensions  (slightly higher quality)
///   • BGE-base-en-v1.5  → 768 dimensions  (higher quality, slower)
///
/// The service is registered as a singleton — the ONNX session and tokenizer
/// are expensive to initialise and are fully thread-safe once created.
/// </summary>
public sealed class OnnxEmbeddingService : IEmbeddingService, IDisposable
{
    private readonly RagOptions _opts;
    private readonly ILogger<OnnxEmbeddingService> _logger;

    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialised;

    private const string InputIdsName = "input_ids";
    private const string AttentionMaskName = "attention_mask";
    private const string TokenTypeIdsName = "token_type_ids";
    private const string OutputName = "last_hidden_state";

    public OnnxEmbeddingService(IOptions<RagOptions> opts, ILogger<OnnxEmbeddingService> logger)
    {
        _opts = opts.Value;
        _logger = logger;
    }

    public int Dimensions => _opts.EmbeddingDimensions;

    public async Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var results = await EmbedBatchAsync([text], ct);
        return results[0];
    }

    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(
        IReadOnlyList<string> texts,
        CancellationToken ct = default)
    {
        await EnsureInitialisedAsync(ct);
        if (texts.Count == 0) return [];

        var encoded = _tokenizer!.EncodeBatch(texts);
        int batchSize = texts.Count;
        int seqLen = encoded[0].InputIds.Length;

        var inputIdsTensor = new DenseTensor<long>(new[] { batchSize, seqLen });
        var attentionMaskTensor = new DenseTensor<long>(new[] { batchSize, seqLen });
        var tokenTypeIdsTensor = new DenseTensor<long>(new[] { batchSize, seqLen });

        for (int i = 0; i < batchSize; i++)
            for (int j = 0; j < seqLen; j++)
            {
                inputIdsTensor[i, j] = encoded[i].InputIds[j];
                attentionMaskTensor[i, j] = encoded[i].AttentionMask[j];
                tokenTypeIdsTensor[i, j] = encoded[i].TokenTypeIds[j];
            }

        var inputs = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(InputIdsName,      inputIdsTensor),
            NamedOnnxValue.CreateFromTensor(AttentionMaskName, attentionMaskTensor),
            NamedOnnxValue.CreateFromTensor(TokenTypeIdsName,  tokenTypeIdsTensor),
        };

        using var outputs = _session!.Run(inputs);
        var lastHiddenState = outputs
            .First(o => o.Name == OutputName)
            .AsEnumerable<float>()
            .ToArray();

        int hiddenSize = lastHiddenState.Length / (batchSize * seqLen);
        var embeddings = new float[batchSize][];

        for (int i = 0; i < batchSize; i++)
        {
            var pooled = MeanPool(lastHiddenState, encoded[i].AttentionMask,
                                   batchIndex: i, seqLen: seqLen, hiddenSize: hiddenSize);
            embeddings[i] = L2Normalise(pooled);
        }

        return embeddings;
    }

    private async Task EnsureInitialisedAsync(CancellationToken ct)
    {
        if (_initialised) return;
        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialised) return;

            if (!File.Exists(_opts.ModelPath))
                throw new FileNotFoundException(
                    $"ONNX model not found: {_opts.ModelPath}\n" +
                    "Download from: https://huggingface.co/sentence-transformers/all-MiniLM-L6-v2");

            var sessionOptions = new SessionOptions
            {
                ExecutionMode = ExecutionMode.ORT_PARALLEL,
                InterOpNumThreads = Environment.ProcessorCount,
                IntraOpNumThreads = Environment.ProcessorCount,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            _session = new InferenceSession(_opts.ModelPath, sessionOptions);
            _tokenizer = await BertTokenizer.LoadAsync(
                _opts.TokenizerPath,
                Microsoft.Extensions.Logging.Abstractions.NullLogger<BertTokenizer>.Instance,
                ct: ct);

            _initialised = true;
            _logger.LogInformation("ONNX model loaded. Dimensions: {Dim}", Dimensions);
        }
        finally
        {
            _initLock.Release();
        }
    }

    private static float[] MeanPool(
        float[] lastHiddenState, long[] attentionMask,
        int batchIndex, int seqLen, int hiddenSize)
    {
        var pooled = new float[hiddenSize];
        float maskSum = 0f;

        for (int t = 0; t < seqLen; t++)
        {
            float maskVal = attentionMask[t];
            if (maskVal == 0f) continue;
            maskSum++;
            int baseOffset = (batchIndex * seqLen + t) * hiddenSize;
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] += lastHiddenState[baseOffset + h] * maskVal;
        }

        if (maskSum > 0f)
            for (int h = 0; h < hiddenSize; h++)
                pooled[h] /= maskSum;

        return pooled;
    }

    private static float[] L2Normalise(float[] v)
    {
        float norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm < 1e-10f) return v;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    public void Dispose()
    {
        _session?.Dispose();
        _initLock.Dispose();
    }
}