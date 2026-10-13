using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace MlCpu.SemanticKb;

/// <summary>
/// One searchable unit of a project's knowledge: a program summary, a COBOL paragraph, a Java method, a requirements rule,
/// a quirk, a data contract, the I/O picture, a Gemma design or a worked example. The views of one COBOL paragraph
/// (paragraph, Java method, rules) share an <see cref="Anchor"/>.
/// </summary>
public sealed class KbChunk
{
    /// <summary>"&lt;project&gt;:&lt;program&gt;:&lt;kind&gt;:&lt;key&gt;", stable across rebuilds.</summary>
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public string Program { get; set; } = "";
    /// <summary>program | paragraph | java | rule | quirk | data | io | design | example</summary>
    public string Kind { get; set; } = "";
    /// <summary>"&lt;project&gt;/&lt;program&gt;/&lt;paragraph or member&gt;".</summary>
    public string Anchor { get; set; } = "";
    public string Title { get; set; } = "";
    /// <summary>What is embedded, indexed and shown.</summary>
    public string Text { get; set; } = "";
    /// <summary>Source file relative to the project directory (forward slashes).</summary>
    public string Path { get; set; } = "";
    public int StartLine { get; set; }
    public int EndLine { get; set; }
    /// <summary>Pipeline facts: role, segment kinds, reads, writes, rule ids, capability, verbs (lists joined by ", ").</summary>
    public Dictionary<string, string> Facts { get; set; } = [];
    /// <summary>Label family (role, ruleKind, function) → label.</summary>
    public Dictionary<string, KbLabel> Labels { get; set; } = [];
    public int Tokens { get; set; }
    /// <summary>SHA-256 of title + text: the embedding cache key (with the model variant and the prompt).</summary>
    public string Sha { get; set; } = "";

    public string Fact(string key) => Facts.GetValueOrDefault(key, "");

    public string? Label(string family) => Labels.TryGetValue(family, out var l) ? l.Value : null;

    public void ComputeSha() => Sha = KbJson.Sha256(Title + "\n" + Text);
}

/// <summary>A label and where it came from: pipeline (ground truth from the CobolBdm pipeline), rule (deterministic, from
/// the statements), glossary (weak, from the financial glossary) or model (an ML.NET classifier's prediction).</summary>
public sealed record KbLabel(string Value, string Source, double Confidence);

public static class KbJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNameCaseInsensitive = true
    };

    public static readonly JsonSerializerOptions Indented = new(Options) { WriteIndented = true };

    public static string Sha256(string s) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(s)));

    public static T Read<T>(string path) => JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"{path}: empty JSON");

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Indented) + "\n");
    }
}
