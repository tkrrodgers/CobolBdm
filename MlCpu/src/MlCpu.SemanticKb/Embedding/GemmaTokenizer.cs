using Microsoft.ML.Tokenizers;

namespace MlCpu.SemanticKb.Embedding;

/// <summary>
/// EmbeddingGemma's tokenizer: the Gemma 3 SentencePiece BPE model (tokenizer.model, 262,144 pieces, byte fallback, no
/// dummy prefix) through Microsoft.ML.Tokenizers, with &lt;bos&gt; (2) and &lt;eos&gt; (1) around every input as the Hugging Face
/// post-processor adds them. Verified id for id against the HF tokenizer.json (knowledge/fixtures).
/// </summary>
public sealed class GemmaTokenizer
{
    public const int MaxSequence = 2048;
    readonly SentencePieceTokenizer _tok;

    public GemmaTokenizer(string tokenizerModelPath)
    {
        using var s = File.OpenRead(tokenizerModelPath);
        _tok = SentencePieceTokenizer.Create(s, true, true);
    }

    /// <summary>Ids with BOS and EOS, cut to <paramref name="max"/> (EOS kept last).</summary>
    public long[] Encode(string text, int max = MaxSequence)
    {
        var ids = _tok.EncodeToIds(text);
        int n = Math.Min(ids.Count, max);
        var r = new long[n];
        for (int i = 0; i < n; i++) r[i] = ids[i];
        if (ids.Count > max) r[^1] = ids[^1];
        return r;
    }

    /// <summary>Token count without BOS/EOS.</summary>
    public int Count(string text) => text.Length == 0 ? 0 : _tok.CountTokens(text, false, false, true, true);
}
