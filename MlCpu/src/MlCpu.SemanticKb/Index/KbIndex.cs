using System.Text.Json;

namespace MlCpu.SemanticKb.Index;

public sealed class KbManifest
{
    public string Schema { get; set; } = "mlcpu.kb-index.v1";
    public string Project { get; set; } = "";
    public string BuiltAt { get; set; } = "";
    public string Chunker { get; set; } = "";
    public string Model { get; set; } = "";
    public string Revision { get; set; } = "";
    public string Variant { get; set; } = "";
    public int Dimensions { get; set; }
    public int Chunks { get; set; }
    public long Tokens { get; set; }
    public SortedDictionary<string, int> Kinds { get; set; } = [];
    public int Embedded { get; set; }
    public int CacheHits { get; set; }
    public double HarvestSeconds { get; set; }
    public double EmbedSeconds { get; set; }
    public double ClassifySeconds { get; set; }
    public string ChunksSha { get; set; } = "";
    public SortedDictionary<string, string> Sources { get; set; } = [];
}

/// <summary>
/// One project's index in projects/&lt;id&gt;/knowledge/: manifest.json, chunks.jsonl (with final labels) and vectors.f32
/// (row i is chunk i: count × dimensions little-endian float32, L2-normalized).
/// </summary>
public sealed class KbIndex
{
    public KbManifest Manifest { get; }
    public IReadOnlyList<KbChunk> Chunks { get; }
    /// <summary>Row-major vectors, <see cref="KbManifest.Dimensions"/> per chunk.</summary>
    public float[] Vectors { get; }
    public string Dir { get; }

    public KbIndex(string dir, KbManifest manifest, IReadOnlyList<KbChunk> chunks, float[] vectors)
    {
        Dir = dir; Manifest = manifest; Chunks = chunks; Vectors = vectors;
    }

    public ReadOnlySpan<float> Vector(int row) => Vectors.AsSpan(row * Manifest.Dimensions, Manifest.Dimensions);

    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, "manifest.json")) && File.Exists(Path.Combine(dir, "vectors.f32"));

    public static KbIndex Load(string dir)
    {
        var manifest = KbJson.Read<KbManifest>(Path.Combine(dir, "manifest.json"));
        var chunks = File.ReadLines(Path.Combine(dir, "chunks.jsonl")).Where(l => l.Length > 0)
            .Select(l => JsonSerializer.Deserialize<KbChunk>(l, KbJson.Options)!).ToList();
        var bytes = File.ReadAllBytes(Path.Combine(dir, "vectors.f32"));
        var vectors = new float[bytes.Length / 4];
        Buffer.BlockCopy(bytes, 0, vectors, 0, bytes.Length);
        if (vectors.Length != chunks.Count * manifest.Dimensions)
            throw new InvalidDataException($"{dir}: {chunks.Count} chunks but {vectors.Length / Math.Max(1, manifest.Dimensions)} vectors");
        return new KbIndex(dir, manifest, chunks, vectors);
    }

    public static void Save(string dir, KbManifest manifest, IReadOnlyList<KbChunk> chunks, IReadOnlyList<float[]> vectors)
    {
        Directory.CreateDirectory(dir);
        var lines = chunks.Select(c => JsonSerializer.Serialize(c, KbJson.Options)).ToList();
        manifest.ChunksSha = KbJson.Sha256(string.Join('\n', lines));
        File.WriteAllLines(Path.Combine(dir, "chunks.jsonl"), lines);
        using (var w = new BinaryWriter(File.Create(Path.Combine(dir, "vectors.f32"))))
            foreach (var v in vectors) foreach (var x in v) w.Write(x);
        KbJson.Write(Path.Combine(dir, "manifest.json"), manifest);
    }
}
