using System.Text;
using MlCpu.SemanticKb.Labels;

namespace MlCpu.SemanticKb.Catalog;

public sealed record CatalogShare(string Id, string Name, int Chunks, double Share);

public sealed record CatalogRule(string Id, string Paragraph, string? Function, string? FunctionSource, string? RuleKind, string? Role,
    string Path, int StartLine, int EndLine, string? Design, string ChunkId);

public sealed record CatalogQuirk(string Id, string Kind, string Paragraph, string Text);

public sealed record CatalogProgram(string Id, string Kind, string HeaderFunction, string? Function, string? FunctionSource,
    IReadOnlyList<CatalogShare> Functions, IReadOnlyDictionary<string, int> Roles, IReadOnlyDictionary<string, int> RuleKinds,
    IReadOnlyList<CatalogRule> Rules, bool RulesFromPacket, IReadOnlyList<CatalogQuirk> Quirks, int Paragraphs, int JavaMembers,
    IReadOnlyDictionary<string, int> LabelSources);

public sealed record ProjectCatalog(string Schema, string Project, string BuiltAt, IReadOnlyList<CatalogProgram> Programs,
    IReadOnlyDictionary<string, int> FunctionsByPrograms);

/// <summary>
/// The classification deliverable per project (catalog.json / catalog.md): for every program its business functions
/// ranked by share of chunks, its paragraph roles and rule kinds, and its business rules with function, kind, role,
/// citation and Gemma 4's description. Every label shows its source: pipeline, rule, glossary or model.
/// </summary>
public static class CatalogBuilder
{
    static readonly HashSet<string> BusinessKinds = ["calculation", "validation", "decision", "lookup", "rounding", "date-time", "aggregation"];

    public static ProjectCatalog Build(string project, IReadOnlyList<KbChunk> chunks, Taxonomy taxonomy)
    {
        var programs = new List<CatalogProgram>();
        foreach (var g in chunks.GroupBy(c => c.Program).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var summary = g.FirstOrDefault(c => c.Kind == "program");
            var labeled = g.Where(c => c.Kind is "paragraph" or "java" or "rule" or "data" or "design" && c.Label(Families.Function) is not null).ToList();
            var functions = labeled.GroupBy(c => c.Label(Families.Function)!)
                .Select(f => new CatalogShare(f.Key, taxonomy.Name(f.Key), f.Count(), Math.Round((double)f.Count() / Math.Max(1, labeled.Count), 3)))
                .OrderByDescending(f => f.Chunks).ThenBy(f => f.Id, StringComparer.Ordinal).ToList();
            var code = g.Where(c => c.Kind == "paragraph").ToList();
            var packetRules = g.Where(c => c.Kind == "rule").OrderBy(c => c.StartLine).ThenBy(c => c.Id, StringComparer.Ordinal).ToList();
            var rules = packetRules.Count > 0
                ? packetRules.Select(Rule).ToList()
                : code.Where(c => BusinessKinds.Contains(c.Label(Families.RuleKind) ?? "")).OrderBy(c => c.StartLine).Select(Rule).ToList();
            programs.Add(new CatalogProgram(
                g.Key,
                summary?.Fact("programKind") ?? "",
                summary?.Fact("function") ?? "",
                summary?.Label(Families.Function) ?? functions.FirstOrDefault()?.Id,
                summary?.Labels.GetValueOrDefault(Families.Function)?.Source ?? (functions.Count > 0 ? "share" : null),
                functions,
                Count(code, Families.Role),
                Count(code, Families.RuleKind),
                rules,
                packetRules.Count > 0,
                g.Where(c => c.Kind == "quirk").Select(c => new CatalogQuirk(c.Id.Split(':').Last(), c.Fact("quirk"), c.Fact("paragraph"), c.Text)).ToList(),
                code.Select(c => c.Fact("paragraph")).Distinct().Count(),
                g.Count(c => c.Kind == "java"),
                g.SelectMany(c => c.Labels.Values).GroupBy(l => l.Source).OrderBy(x => x.Key).ToDictionary(x => x.Key, x => x.Count())));
        }
        var byPrograms = programs.Where(p => p.Function is not null).GroupBy(p => p.Function!).OrderByDescending(x => x.Count()).ToDictionary(x => x.Key, x => x.Count());
        return new ProjectCatalog("mlcpu.kb-catalog.v1", project, DateTime.Now.ToString("s"), programs, byPrograms);
    }

    static CatalogRule Rule(KbChunk c)
    {
        var design = c.Text.Split('\n').FirstOrDefault(l => l.StartsWith("Gemma 4 design: "))?["Gemma 4 design: ".Length..];
        return new CatalogRule(c.Kind == "rule" ? c.Fact("ruleId") : c.Fact("paragraph"), c.Fact("paragraph"),
            c.Label(Families.Function), c.Labels.GetValueOrDefault(Families.Function)?.Source,
            c.Label(Families.RuleKind), c.Label(Families.Role), c.Path, c.StartLine, c.EndLine, design, c.Id);
    }

    static Dictionary<string, int> Count(IEnumerable<KbChunk> chunks, string family) =>
        chunks.Where(c => c.Label(family) is not null).GroupBy(c => c.Label(family)!).OrderByDescending(x => x.Count()).ToDictionary(x => x.Key, x => x.Count());

    public static string Markdown(ProjectCatalog cat, Taxonomy taxonomy)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# {cat.Project}: functionality and business rules");
        sb.AppendLine();
        sb.AppendLine($"Generated {cat.BuiltAt} by the knowledge base (knowledgeBaseGroundGemma4.md §8). Label sources: **pipeline** = the CobolBdm requirements packet; " +
                      "**rule** = derived from the COBOL statements; **glossary** = weak label from the financial glossary; **model** = ML.NET classifier over EmbeddingGemma vectors. " +
                      "The taxonomy and glossary are unreviewed SME drafts.");
        sb.AppendLine();
        sb.AppendLine("| Program | Kind | Header function | Business function | Top functions (share of labeled chunks) | Rules | Quirks |");
        sb.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var p in cat.Programs)
            sb.AppendLine($"| {p.Id} | {p.Kind} | {Esc(p.HeaderFunction)} | {(p.Function is null ? "—" : $"{taxonomy.Name(p.Function)} ({p.FunctionSource})")} | " +
                          $"{string.Join(", ", p.Functions.Take(3).Select(f => $"{f.Id} {f.Share:P0}"))} | {p.Rules.Count}{(p.RulesFromPacket ? "" : "*")} | {p.Quirks.Count} |");
        sb.AppendLine();
        sb.AppendLine("\\* no requirements packet: business-rule paragraphs (calculation, validation, decision, lookup, rounding, date-time, aggregation) from the AST outline.");
        foreach (var p in cat.Programs)
        {
            sb.AppendLine();
            sb.AppendLine($"## {p.Id}: {Esc(p.HeaderFunction)}");
            sb.AppendLine();
            sb.AppendLine($"- Kind: {p.Kind}; {p.Paragraphs} paragraphs; {p.JavaMembers} Java chunks; label sources: {string.Join(", ", p.LabelSources.Select(kv => $"{kv.Key} {kv.Value}"))}");
            if (p.Functions.Count > 0) sb.AppendLine("- Functions: " + string.Join(", ", p.Functions.Select(f => $"{f.Name} {f.Share:P0}")));
            if (p.Roles.Count > 0) sb.AppendLine("- Paragraph roles: " + string.Join(", ", p.Roles.Select(kv => $"{kv.Key} {kv.Value}")));
            if (p.RuleKinds.Count > 0) sb.AppendLine("- Rule kinds: " + string.Join(", ", p.RuleKinds.Select(kv => $"{kv.Key} {kv.Value}")));
            if (p.Rules.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine(p.RulesFromPacket ? "| Rule | Paragraph | Function | Kind | Role | Lines | Gemma 4 design |" : "| Paragraph | Function | Kind | Role | Lines |");
                sb.AppendLine(p.RulesFromPacket ? "|---|---|---|---|---|---|---|" : "|---|---|---|---|---|");
                foreach (var r in p.Rules)
                {
                    var f = r.Function is null ? "—" : $"{r.Function} ({r.FunctionSource})";
                    var lines = r.StartLine > 0 ? $"{r.Path}:{r.StartLine}-{r.EndLine}" : r.Path;
                    sb.AppendLine(p.RulesFromPacket
                        ? $"| {r.Id} | {r.Paragraph} | {f} | {r.RuleKind} | {r.Role} | {lines} | {Esc(r.Design ?? "")} |"
                        : $"| {r.Paragraph} | {f} | {r.RuleKind} | {r.Role} | {lines} |");
                }
            }
            if (p.Quirks.Count > 0)
            {
                sb.AppendLine();
                foreach (var q in p.Quirks) sb.AppendLine($"- Quirk {q.Id} ({q.Kind}, {q.Paragraph}): {Esc(q.Text.Split('\n')[0])}");
            }
        }
        return sb.ToString();
    }

    static string Esc(string s) => s.Replace("|", "\\|").Replace("\n", " ");
}
