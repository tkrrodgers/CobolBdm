namespace MlCpu.SemanticKb.Eval;

public sealed record LabelEvalResult(string Family, int Items, int Found, int Correct, double Accuracy,
    IReadOnlyDictionary<string, (int Items, int Correct)> BySource, IReadOnlyList<(string ChunkId, string Accepted, string? Got, string? Source)> Misses);

/// <summary>Final labels against an SME sample (knowledge/gold/function-labels.tsv: chunk id, accepted classes separated
/// by '|', note), overall and by the label's source (glossary weak label or model prediction).</summary>
public static class LabelEval
{
    public static LabelEvalResult Run(IEnumerable<KbChunk> chunks, string tsvPath, string family = Labels.Families.Function)
    {
        var byId = chunks.ToDictionary(c => c.Id);
        var items = File.ReadLines(tsvPath).Where(l => l.Length > 0 && !l.StartsWith('#')).Select(l => l.Split('\t')).Where(f => f.Length >= 2).ToList();
        int found = 0, correct = 0;
        var bySource = new Dictionary<string, (int Items, int Correct)>();
        var misses = new List<(string, string, string?, string?)>();
        foreach (var f in items)
        {
            if (!byId.TryGetValue(f[0], out var c)) { misses.Add((f[0], f[1], null, "missing")); continue; }
            found++;
            var label = c.Labels.GetValueOrDefault(family);
            bool ok = label is not null && f[1].Split('|').Contains(label.Value);
            if (ok) correct++; else misses.Add((f[0], f[1], label?.Value, label?.Source));
            var src = label?.Source ?? "none";
            var s = bySource.GetValueOrDefault(src);
            bySource[src] = (s.Items + 1, s.Correct + (ok ? 1 : 0));
        }
        return new LabelEvalResult(family, items.Count, found, correct, found == 0 ? 0 : Math.Round((double)correct / found, 3), bySource, misses);
    }
}
