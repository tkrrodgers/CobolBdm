using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// The pattern library (pipeline v2): every program that passes its acceptance test leaves a pattern (what the program is,
/// from its requirements packet; the verified code; the problems the attempts had to fix). A later program gets the most
/// similar pattern of another program in its prompt. Similarity is BM25 over the packet's features (file organizations and
/// operations, report output, quirks, rule kinds, PICTURE usages), or cosine over embeddings when an OpenAI-compatible
/// /v1/embeddings endpoint is given (EmbeddingGemma on llama-server, for instance). Never the same program: a pattern
/// teaches another program, it does not hand back a solution.
/// </summary>
public sealed class PatternLibrary(string dir, string? embeddingsUrl = null)
{
    const int CodeBudget = 9000;

    public sealed record Pattern(string ProgramId, string Model, string Features, IReadOnlyDictionary<string, string> Files, IReadOnlyList<string> Fixes, string Recorded);

    /// <summary>Bag of features of a program, from its requirements packet (words, so BM25 and embeddings both read it).</summary>
    public static string Features(string packetPath)
    {
        var p = JsonNode.Parse(File.ReadAllText(packetPath))!;
        var words = new List<string>();
        foreach (var port in p["ports"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var org = port["organization"]?.GetValue<string>() ?? "";
            var dir = port["direction"]?.GetValue<string>() ?? "";
            words.Add($"file-{org.ToLowerInvariant().Replace(' ', '-')}-{dir.ToLowerInvariant()}");
            // The verb only ("READ INTO TRAN-RECORD" is READ): record names are the program's own and say nothing about similarity.
            foreach (var op in port["operations"]?.AsArray() ?? [])
                words.Add($"op-{op!.GetValue<string>().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.ToLowerInvariant()}-{org.ToLowerInvariant().Replace(' ', '-')}");
        }
        foreach (var q in p["legacyQuirks"]?.AsArray().OfType<JsonObject>() ?? []) words.Add("quirk-" + q["kind"]?.GetValue<string>());
        foreach (var r in p["rules"]?.AsArray().OfType<JsonObject>() ?? [])
            foreach (var s in r["statements"]?.AsArray().OfType<JsonObject>() ?? []) words.Add("verb-" + s["verb"]?.GetValue<string>()?.ToLowerInvariant());
        var text = p["dataContracts"]?.ToJsonString() ?? "";
        foreach (Match m in Regex.Matches(text, "\"usage\":\"(\\w+)\"")) words.Add("usage-" + m.Groups[1].Value.ToLowerInvariant());
        foreach (Match m in Regex.Matches(text, "\"pic\":\"([^\"]*[Z,.+\\-][^\"]*)\"")) words.Add("edited-picture");
        if ((p["observableOutputs"]?.ToJsonString() ?? "").Contains("ADVANCING", StringComparison.OrdinalIgnoreCase)) words.Add("print-report");
        words.Add((p["invocation"]?.AsArray().Count ?? 0) > 0 ? "job-step" : "subprogram");
        return string.Join(" ", words.Where(w => !string.IsNullOrEmpty(w)));
    }

    public void Record(string programId, string model, string packetPath, IDictionary<string, string> files, IEnumerable<string> fixes)
    {
        Directory.CreateDirectory(dir);
        var p = new Pattern(programId, model, Features(packetPath), new SortedDictionary<string, string>(files), fixes.Take(12).ToList(), DateTime.Now.ToString("s"));
        File.WriteAllText(Path.Combine(dir, $"{programId}.{Safe(model)}.json"), JsonSerializer.Serialize(p, Program.Json) + "\n");
    }

    IEnumerable<Pattern> All() => !Directory.Exists(dir) ? []
        : Directory.EnumerateFiles(dir, "*.json").Select(f => JsonSerializer.Deserialize<Pattern>(File.ReadAllText(f))).OfType<Pattern>();

    /// <summary>The most similar pattern of another program, rendered for the implementation prompt; null when there is none.</summary>
    public string? Retrieve(string programId, string packetPath, out string? from)
    {
        from = null;
        var candidates = All().Where(x => !x.ProgramId.Equals(programId, StringComparison.OrdinalIgnoreCase)).ToList();
        if (candidates.Count == 0) return null;
        var query = Features(packetPath);
        var scores = embeddingsUrl is not null ? Cosine(query, candidates) : null;
        scores ??= Bm25(query, candidates);
        var best = candidates.Zip(scores).OrderByDescending(x => x.Second).First();
        if (best.Second <= 0) return null;
        var pattern = best.First;
        from = pattern.ProgramId;
        var sb = new StringBuilder($"# A verified solution of a similar program: {pattern.ProgramId} (passed its acceptance test byte for byte)\n\n");
        sb.Append($"It is another program with other business rules: use it for how the platform layer and the record kit are used ");
        sb.Append($"(opening datasets, the read loop, writing records and report lines, file status and abend handling), not for the rules.\n\n");
        if (pattern.Fixes.Count > 0)
        {
            sb.Append("Problems its attempts had to fix before it passed:\n");
            foreach (var f in pattern.Fixes.Take(5)) sb.Append("- ").Append(f.Replace('\n', ' ')).Append('\n');
            sb.Append('\n');
        }
        int used = 0;
        foreach (var (name, src) in pattern.Files.OrderBy(f => f.Key.EndsWith("Step") || f.Key.EndsWith("Program") ? 0 : 1))
        {
            if (used > CodeBudget) { sb.Append($"(further classes of {pattern.ProgramId} omitted)\n"); break; }
            var text = src.Length + used > CodeBudget ? src[..Math.Max(0, CodeBudget - used)] + "\n// … (clipped)" : src;
            sb.Append("```java\n").Append(text.TrimEnd()).Append("\n```\n");
            used += text.Length;
        }
        return sb.ToString();
    }

    static List<double> Bm25(string query, List<Pattern> docs)
    {
        var tokens = docs.Select(d => d.Features.Split(' ', StringSplitOptions.RemoveEmptyEntries)).ToList();
        double avg = tokens.Average(t => (double)t.Length);
        var q = query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Distinct().ToList();
        var scores = new List<double>();
        foreach (var t in tokens)
        {
            double s = 0;
            foreach (var term in q)
            {
                int df = tokens.Count(x => x.Contains(term));
                if (df == 0) continue;
                double idf = Math.Log(1 + (tokens.Count - df + 0.5) / (df + 0.5));
                int tf = t.Count(x => x == term);
                s += idf * tf * 2.2 / (tf + 1.2 * (0.25 + 0.75 * t.Length / avg));
            }
            scores.Add(s);
        }
        return scores;
    }

    List<double>? Cosine(string query, List<Pattern> docs)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
            var inputs = docs.Select(d => d.Features).Prepend(query).ToArray();
            var response = http.PostAsJsonAsync(embeddingsUrl!.TrimEnd('/') + "/v1/embeddings", new { input = inputs }).GetAwaiter().GetResult();
            response.EnsureSuccessStatusCode();
            var data = JsonNode.Parse(response.Content.ReadAsStringAsync().GetAwaiter().GetResult())!["data"]!.AsArray()
                .Select(x => x!["embedding"]!.AsArray().Select(v => v!.GetValue<double>()).ToArray()).ToList();
            var qv = data[0];
            return data.Skip(1).Select(v => Dot(qv, v) / Math.Sqrt(Dot(qv, qv) * Dot(v, v))).ToList();
        }
        catch (Exception e)
        {
            Program.Log($"  pattern library: embeddings unavailable ({e.Message}); using BM25");
            return null;
        }
    }

    static double Dot(double[] a, double[] b) => a.Zip(b).Sum(p => p.First * p.Second);

    static string Safe(string s) => string.Concat(s.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_'));
}
