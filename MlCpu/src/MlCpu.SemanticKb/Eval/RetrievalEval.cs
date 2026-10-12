using System.Text;
using MlCpu.SemanticKb.Search;

namespace MlCpu.SemanticKb.Eval;

public sealed class GoldExpect
{
    public string Program { get; set; } = "";
    /// <summary>Paragraph (anchor tail); null = any chunk of the program counts.</summary>
    public string? Paragraph { get; set; }
}

public sealed class GoldQuestion
{
    public string Id { get; set; } = "";
    public string Project { get; set; } = "";
    public string Question { get; set; } = "";
    /// <summary>prose (a user's question) or identifier (names code).</summary>
    public string Kind { get; set; } = "prose";
    public List<GoldExpect> Expect { get; set; } = [];
    public string? Note { get; set; }
}

public sealed class GoldFile
{
    public string Schema { get; set; } = "";
    public string Note { get; set; } = "";
    public List<GoldQuestion> Questions { get; set; } = [];
}

public sealed record ModeScore(string Mode, int Questions, double RecallAt1, double RecallAt5, double RecallAt10, double Mrr10,
    IReadOnlyDictionary<string, double> RecallAt5ByProject, IReadOnlyDictionary<string, double> RecallAt5ByKind, IReadOnlyDictionary<string, double> Mrr10ByKind);

public sealed record QuestionResult(string Id, string Mode, int? Rank, string TopHit);

/// <summary>
/// Retrieval quality over the SME gold questions (knowledge/gold/questions.json): a hit matches when it is in the expected
/// program (and paragraph, if one is given). Recall@k = share of questions with a match in the top k; MRR@10 = mean of
/// 1 / rank of the first match. Searched across all projects, so a wrong-project hit counts against the question.
/// </summary>
public static class RetrievalEval
{
    public static (IReadOnlyList<ModeScore> Scores, IReadOnlyList<QuestionResult> Results) Run(KbSearch search, GoldFile gold, IReadOnlyList<string> modes)
    {
        var scores = new List<ModeScore>();
        var results = new List<QuestionResult>();
        foreach (var mode in modes)
        {
            var ranks = new List<(GoldQuestion Q, int? Rank)>();
            foreach (var q in gold.Questions)
            {
                var res = search.Search(new SearchRequest(q.Question, null, 10, mode, MaxPerAnchor: 2));
                int? rank = null;
                for (int i = 0; i < res.Hits.Count && rank is null; i++)
                {
                    var c = res.Hits[i].Chunk;
                    if (c.Project == q.Project && q.Expect.Any(e => c.Program.Equals(e.Program, StringComparison.OrdinalIgnoreCase) &&
                        (e.Paragraph is null || c.Anchor.EndsWith("/" + e.Paragraph, StringComparison.OrdinalIgnoreCase)))) rank = i + 1;
                }
                ranks.Add((q, rank));
                results.Add(new QuestionResult(q.Id, mode, rank, res.Hits.Count == 0 ? "" : res.Hits[0].Chunk.Id));
            }
            double R(int k, IEnumerable<(GoldQuestion Q, int? Rank)> xs) { var l = xs.ToList(); return l.Count == 0 ? 0 : Math.Round((double)l.Count(x => x.Rank <= k) / l.Count, 3); }
            scores.Add(new ModeScore(mode, ranks.Count, R(1, ranks), R(5, ranks), R(10, ranks),
                Math.Round(ranks.Average(x => x.Rank is { } r ? 1.0 / r : 0), 3),
                ranks.GroupBy(x => x.Q.Project).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => R(5, g)),
                ranks.GroupBy(x => x.Q.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => R(5, g)),
                ranks.GroupBy(x => x.Q.Kind).OrderBy(g => g.Key).ToDictionary(g => g.Key, g => Math.Round(g.Average(x => x.Rank is { } r ? 1.0 / r : 0), 3))));
        }
        return (scores, results);
    }

    public static string Markdown(IReadOnlyList<ModeScore> scores, IReadOnlyList<QuestionResult> results, GoldFile gold, string variant)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Retrieval evaluation ({variant}, {DateTime.Now:s})");
        sb.AppendLine();
        sb.AppendLine($"BM25 weight for prose queries: {Search.KbSearch.LexicalWeightForProse} (1.0 for queries that name code).");
        sb.AppendLine();
        sb.AppendLine("| Mode | Questions | Recall@1 | Recall@5 | Recall@10 | MRR@10 | Recall@5 by project | Recall@5 / MRR@10 by kind |");
        sb.AppendLine("|---|---|---|---|---|---|---|---|");
        foreach (var s in scores)
            sb.AppendLine($"| {s.Mode} | {s.Questions} | {s.RecallAt1:N3} | **{s.RecallAt5:N3}** | {s.RecallAt10:N3} | {s.Mrr10:N3} | {string.Join(", ", s.RecallAt5ByProject.Select(kv => $"{kv.Key} {kv.Value:N2}"))} | " +
                          $"{string.Join(", ", s.RecallAt5ByKind.Select(kv => $"{kv.Key} {kv.Value:N2} / {s.Mrr10ByKind[kv.Key]:N2}"))} |");
        sb.AppendLine();
        sb.AppendLine("| Question | " + string.Join(" | ", scores.Select(s => s.Mode + " rank")) + " | Hybrid top hit |");
        sb.AppendLine("|---|" + string.Concat(scores.Select(_ => "---|")) + "---|");
        foreach (var q in gold.Questions)
        {
            var row = scores.Select(s => results.First(r => r.Id == q.Id && r.Mode == s.Mode).Rank?.ToString() ?? "—");
            var top = results.FirstOrDefault(r => r.Id == q.Id && r.Mode == "hybrid")?.TopHit ?? "";
            sb.AppendLine($"| {q.Id}: {q.Question.Replace("|", "\\|")} | {string.Join(" | ", row)} | {top} |");
        }
        return sb.ToString();
    }
}
