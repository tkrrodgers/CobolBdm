namespace MlCpu.SemanticKb.Embedding;

/// <summary>
/// Embeddings by content: SHA-256 of (variant, prompted text) → vector, in one binary file per variant
/// (knowledge/embed-cache.&lt;variant&gt;.bin: "KBEC" v1, dimensions, count, then 32-byte key + float32 vector per entry).
/// A rebuild embeds only chunks whose text changed. Entries no chunk uses are dropped on save.
/// </summary>
public sealed class EmbeddingCache
{
    const uint Magic = 0x4345424B; // "KBEC"
    readonly Dictionary<string, float[]> _entries = new(StringComparer.Ordinal);
    readonly HashSet<string> _used = new(StringComparer.Ordinal);
    readonly int _dims;

    public string Path { get; }
    public int Hits { get; private set; }
    public int Count => _entries.Count;

    public EmbeddingCache(string path, int dims = EmbeddingGemma.Dimensions)
    {
        Path = path;
        _dims = dims;
        if (!File.Exists(path)) return;
        try
        {
            using var r = new BinaryReader(File.OpenRead(path));
            if (r.ReadUInt32() != Magic || r.ReadInt32() != 1 || r.ReadInt32() != dims) return;
            int n = r.ReadInt32();
            for (int i = 0; i < n; i++)
            {
                var key = Convert.ToHexStringLower(r.ReadBytes(32));
                var v = new float[dims];
                for (int k = 0; k < dims; k++) v[k] = r.ReadSingle();
                _entries[key] = v;
            }
        }
        catch (EndOfStreamException) { _entries.Clear(); }
    }

    public static string Key(string variant, string promptedText) => KbJson.Sha256(variant + "\n" + promptedText);

    public bool TryGet(string key, out float[] vector)
    {
        _used.Add(key);
        if (_entries.TryGetValue(key, out vector!)) { Hits++; return true; }
        return false;
    }

    public void Put(string key, float[] vector)
    {
        lock (_entries) { _entries[key] = vector; _used.Add(key); }
    }

    public void Save()
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        var tmp = Path + ".tmp";
        using (var w = new BinaryWriter(File.Create(tmp)))
        {
            var keep = _entries.Where(kv => _used.Contains(kv.Key)).OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList();
            w.Write(Magic); w.Write(1); w.Write(_dims); w.Write(keep.Count);
            foreach (var (k, v) in keep)
            {
                w.Write(Convert.FromHexString(k));
                foreach (var x in v) w.Write(x);
            }
        }
        File.Move(tmp, Path, overwrite: true);
    }
}
