using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace MlCpu.SemanticKb.Embedding;

/// <summary>
/// Downloads EmbeddingGemma from Hugging Face at the pinned revision and verifies every LFS file against the SHA-256
/// the Hub reports for that revision. Files already present with the right hash are kept.
/// </summary>
public static class ModelStore
{
    static readonly string[] Common = ["config.json", "special_tokens_map.json", "tokenizer_config.json", "tokenizer.model", "tokenizer.json"];

    public static async Task<IReadOnlyList<string>> EnsureAsync(string modelDir, string variant, Action<string> log, CancellationToken ct = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromHours(1) };
        var hub = $"https://huggingface.co/api/models/{EmbeddingGemma.Repository}/tree/{EmbeddingGemma.Revision}";
        var hashes = new Dictionary<string, (long Size, string? Sha)>(StringComparer.Ordinal);
        foreach (var sub in new[] { "", "/onnx" })
            foreach (var e in JsonNode.Parse(await http.GetStringAsync(hub + sub, ct))!.AsArray())
                if (e?["type"]?.GetValue<string>() == "file")
                    hashes[e["path"]!.GetValue<string>()] = (e["size"]!.GetValue<long>(), e["lfs"]?["oid"]?.GetValue<string>());
        var onnx = "onnx/" + EmbeddingGemma.ModelFileName(variant);
        var wanted = Common.Concat([onnx, onnx + "_data"]).ToList();
        var report = new List<string>();
        foreach (var f in wanted)
        {
            if (!hashes.TryGetValue(f, out var meta)) throw new InvalidDataException($"{f} is not in {EmbeddingGemma.Repository}@{EmbeddingGemma.Revision}");
            var path = Path.Combine(modelDir, f.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(path) && new FileInfo(path).Length == meta.Size && (meta.Sha is null || await Sha256Async(path, ct) == meta.Sha))
            {
                report.Add($"ok       {f} ({meta.Size:N0} bytes{(meta.Sha is null ? "" : ", sha256 " + meta.Sha[..12])})");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            log($"download {f} ({meta.Size:N0} bytes)");
            var url = $"https://huggingface.co/{EmbeddingGemma.Repository}/resolve/{EmbeddingGemma.Revision}/{f}";
            await using (var src = await http.GetStreamAsync(url, ct))
            await using (var dst = File.Create(path + ".part"))
                await src.CopyToAsync(dst, ct);
            File.Move(path + ".part", path, overwrite: true);
            if (meta.Sha is not null && await Sha256Async(path, ct) != meta.Sha) throw new InvalidDataException($"{f}: SHA-256 mismatch after download");
            report.Add($"fetched  {f}");
        }
        foreach (var line in report) log(line);
        return report;
    }

    public static async Task<string> Sha256Async(string path, CancellationToken ct = default)
    {
        await using var s = File.OpenRead(path);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(s, ct));
    }
}
