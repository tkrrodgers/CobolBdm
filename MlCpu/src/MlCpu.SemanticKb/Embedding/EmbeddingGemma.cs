using System.Collections.Concurrent;
using Microsoft.ML;
using Microsoft.ML.Data;
using Microsoft.ML.Transforms.Onnx;

namespace MlCpu.SemanticKb.Embedding;

/// <summary>
/// EmbeddingGemma 300M (onnx-community/embeddinggemma-300m-ONNX) through ML.NET: the ONNX graph runs in ML.NET's ONNX
/// transformer (<c>ApplyOnnxModel</c>) on ONNX Runtime, CPU only. Inputs are variable-length rows of token ids and their
/// attention mask (batch 1, no padding); the output <c>sentence_embedding</c> is 768-d and L2-normalized by the model.
/// Thread-safe: each worker thread gets its own ML.NET prediction engine over the one shared transformer and session.
/// </summary>
public sealed class EmbeddingGemma : IDisposable
{
    public const int Dimensions = 768;
    public const string Repository = "onnx-community/embeddinggemma-300m-ONNX";
    public const string Revision = "5090578d9565bb06545b4552f76e6bc2c93e4a66";

    /// <summary>Prompts from the EmbeddingGemma model card.</summary>
    public static string Document(string title, string text) => $"title: {(title.Length == 0 ? "none" : title)} | text: {text}";
    public static string Query(string q, string task = "search result") => $"task: {task} | query: {q}";

    readonly MLContext _ml = new(seed: 1);
    readonly ITransformer _model;
    readonly ThreadLocal<PredictionEngine<TokenRow, EmbeddingRow>> _engine;
    readonly ConcurrentBag<PredictionEngine<TokenRow, EmbeddingRow>> _engines = [];

    public GemmaTokenizer Tokenizer { get; }
    public string Variant { get; }
    public string ModelFile { get; }
    public int MaxTokens { get; }

    public EmbeddingGemma(string modelDir, string variant = "q8", int intraOpThreads = 1, int maxTokens = GemmaTokenizer.MaxSequence)
    {
        Variant = variant;
        MaxTokens = maxTokens;
        ModelFile = Path.Combine(modelDir, "onnx", ModelFileName(variant));
        if (!File.Exists(ModelFile)) throw new FileNotFoundException($"EmbeddingGemma {variant} not found; run `kb model --variant {variant}`", ModelFile);
        Tokenizer = new GemmaTokenizer(Path.Combine(modelDir, "tokenizer.model"));
        var pipeline = _ml.Transforms.ApplyOnnxModel(new OnnxOptions
        {
            ModelFile = ModelFile,
            InputColumns = ["input_ids", "attention_mask"],
            OutputColumns = ["sentence_embedding"],
            GpuDeviceId = null,
            FallbackToCpu = true,
            IntraOpNumThreads = intraOpThreads,
            InterOpNumThreads = 1
        });
        _model = pipeline.Fit(_ml.Data.LoadFromEnumerable(new List<TokenRow>()));
        _engine = new ThreadLocal<PredictionEngine<TokenRow, EmbeddingRow>>(() =>
        {
            var e = _ml.Model.CreatePredictionEngine<TokenRow, EmbeddingRow>(_model);
            _engines.Add(e);
            return e;
        });
    }

    public static string ModelFileName(string variant) => variant switch
    {
        "q8" => "model_quantized.onnx",
        "fp32" => "model.onnx",
        "q4" => "model_q4.onnx",
        _ => throw new ArgumentException($"unknown EmbeddingGemma variant '{variant}' (q8, fp32, q4; fp16 is not supported by EmbeddingGemma's activations)")
    };

    /// <summary>The embedding of an already-prompted text.</summary>
    public float[] Embed(string promptedText)
    {
        var ids = Tokenizer.Encode(promptedText, MaxTokens);
        var mask = new long[ids.Length];
        Array.Fill(mask, 1L);
        var v = _engine.Value!.Predict(new TokenRow { InputIds = ids, AttentionMask = mask }).Embedding;
        return Normalize(v);
    }

    public float[] EmbedQuery(string query, string task = "search result") => Embed(Query(query, task));

    /// <summary>Embeds many texts on <paramref name="workers"/> dedicated below-normal-priority threads (the machine is
    /// shared with the model runs), reporting each finished index.</summary>
    public float[][] EmbedMany(IReadOnlyList<string> promptedTexts, int workers, Action<int>? done = null, CancellationToken ct = default)
    {
        var result = new float[promptedTexts.Count][];
        int next = -1;
        Exception? failure = null;
        // Longest first, so the slowest inputs do not finish last on one thread.
        var order = Enumerable.Range(0, promptedTexts.Count).OrderByDescending(i => promptedTexts[i].Length).ToArray();
        var threads = Enumerable.Range(0, Math.Max(1, Math.Min(workers, promptedTexts.Count))).Select(_ => new Thread(() =>
        {
            try
            {
                int k;
                while ((k = Interlocked.Increment(ref next)) < order.Length && failure is null)
                {
                    ct.ThrowIfCancellationRequested();
                    var i = order[k];
                    result[i] = Embed(promptedTexts[i]);
                    done?.Invoke(i);
                }
            }
            catch (Exception e) { failure ??= e; }
        }) { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "embeddinggemma" }).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        if (failure is OperationCanceledException oce) throw oce;
        if (failure is not null) throw new InvalidOperationException("embedding failed: " + failure.Message, failure);
        return result;
    }

    /// <summary>L2-normalizes in place (the model's output already is; a Matryoshka truncation would not be).</summary>
    public static float[] Normalize(float[] v)
    {
        double s = 0;
        foreach (var x in v) s += (double)x * x;
        var n = (float)Math.Sqrt(s);
        if (n > 0) for (int i = 0; i < v.Length; i++) v[i] /= n;
        return v;
    }

    public void Dispose()
    {
        foreach (var e in _engines) e.Dispose();
        _engine.Dispose();
        (_model as IDisposable)?.Dispose();
    }

    sealed class TokenRow
    {
        [ColumnName("input_ids"), VectorType] public long[] InputIds { get; set; } = [];
        [ColumnName("attention_mask"), VectorType] public long[] AttentionMask { get; set; } = [];
    }

    sealed class EmbeddingRow
    {
        [ColumnName("sentence_embedding"), VectorType(Dimensions)] public float[] Embedding { get; set; } = [];
    }
}
