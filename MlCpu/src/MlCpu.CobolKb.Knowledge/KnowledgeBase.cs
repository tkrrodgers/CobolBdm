using System.Text.Json;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis;
using MlCpu.Core;

namespace MlCpu.CobolKb.Knowledge;

public sealed record ProgramFile(string Name, string Assign, string Organization, string Direction);

public sealed record ProgramCard(string Id, string File, int Loc, IReadOnlyList<string> Calls, IReadOnlyList<string> Copybooks,
    IReadOnlyList<ProgramFile> Files, IReadOnlyList<string> Jobs, bool ExecSql, bool ExecCics, int Frontiers,
    IReadOnlyList<string> Upstream, IReadOnlyList<string> Downstream);

/// <summary>Knowledge base used by job resolution (§5.5.3): kb/jobs.jsonl, kb/programs.jsonl, kb/meta.json. Deterministic, no clock fields.</summary>
public static class KnowledgeBase
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public sealed record Result(IReadOnlyList<JclJob> Jobs, IReadOnlyList<ProgramCard> Programs, string KbDir);

    public static Result Build(string codeRoot, IEnumerable<string> jclRoots, IEnumerable<string> procRoots, string siteDir, Action<string>? log = null)
    {
        var jobs = JclHarvester.Harvest(jclRoots, procRoots);
        var stepsByProgram = jobs.SelectMany(j => j.Steps.Where(s => s.Program is not null).Select(s => (s.Program!, j.Name)))
            .GroupBy(x => x.Item1, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(x => x.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(), StringComparer.OrdinalIgnoreCase);

        var cards = new List<ProgramCard>();
        foreach (var path in CobolFiles.Programs(codeRoot))
        {
            try
            {
                var model = CobolModelBuilder.BuildGroundedFull(path);
                var graph = ProgramGraphBuilder.Build(model);
                var files = model.Files.Select(f => new ProgramFile(f.Name, f.Assign, f.Organization, Direction(model, f.Name))).ToList();
                cards.Add(new ProgramCard(model.ProgramId, Path.GetFileName(path), File.ReadLines(path).Count(),
                    graph.Calls.SelectMany(c => c.ResolvedTargets.Count > 0 ? c.ResolvedTargets : [c.Target]).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList(),
                    model.Copybooks, files, stepsByProgram.GetValueOrDefault(model.ProgramId) ?? [], model.ExecSql, model.ExecCics,
                    graph.Frontiers.Count + model.Diagnostics.Count, [], []));
            }
            catch (Exception e)
            {
                log?.Invoke($"{Path.GetFileName(path)}: {e.Message}");
            }
        }
        // Lineage by dataset: producer steps feed consumer steps of programs that share a DSN (hubs > 15 programs are dropped).
        var producers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var consumers = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in jobs.SelectMany(j => j.Expanded).Where(s => s.Program is not null))
            foreach (var d in s.Datasets.Where(d => d.Dsn is not null && !d.Dummy && !d.Temporary))
                (d.Direction == "producer" ? producers : consumers).TryAdd(d.Dsn!, []);
        foreach (var s in jobs.SelectMany(j => j.Expanded).Where(s => s.Program is not null))
            foreach (var d in s.Datasets.Where(d => d.Dsn is not null && !d.Dummy && !d.Temporary))
                (d.Direction == "producer" ? producers : consumers)[d.Dsn!].Add(s.Program!);
        cards = cards.Select(c =>
        {
            var up = new SortedSet<string>(StringComparer.Ordinal);
            var down = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var (dsn, cons) in consumers)
                if (cons.Contains(c.Id) && producers.TryGetValue(dsn, out var pr) && pr.Count + cons.Count <= 15) up.UnionWith(pr.Where(x => x != c.Id));
            foreach (var (dsn, pr) in producers)
                if (pr.Contains(c.Id) && consumers.TryGetValue(dsn, out var cons) && pr.Count + cons.Count <= 15) down.UnionWith(cons.Where(x => x != c.Id));
            return c with { Upstream = up.ToList(), Downstream = down.ToList() };
        }).OrderBy(c => c.Id, StringComparer.Ordinal).ToList();

        var kb = Path.Combine(siteDir, "kb");
        Directory.CreateDirectory(kb);
        File.WriteAllLines(Path.Combine(kb, "jobs.jsonl"), jobs.Select(j => JsonSerializer.Serialize(j, Json)));
        File.WriteAllLines(Path.Combine(kb, "programs.jsonl"), cards.Select(c => JsonSerializer.Serialize(c, Json)));
        File.WriteAllText(Path.Combine(kb, "meta.json"), JsonSerializer.Serialize(new { schema = "mlcpu.kb.v1", programs = cards.Count, jobs = jobs.Count, steps = jobs.Sum(j => j.Steps.Count) }, Json));
        return new Result(jobs, cards, kb);
    }

    /// <summary>File direction from the program's OPEN modes.</summary>
    static string Direction(CobolProgram model, string file)
    {
        var modes = new SortedSet<string>(StringComparer.Ordinal);
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                if (s.Verb == "OPEN")
                    foreach (var f in s.L?.GetValueOrDefault("files") ?? [])
                        if (string.Equals(f.V, file, StringComparison.OrdinalIgnoreCase)) modes.Add(f.Op!);
                if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Walk(b);
            }
        }
        foreach (var p in model.Paragraphs) Walk(p.Statements);
        return modes.Count == 0 ? "unopened" : string.Join("+", modes);
    }

    public static IReadOnlyList<JclJob> LoadJobs(string kbDir) =>
        File.Exists(Path.Combine(kbDir, "jobs.jsonl"))
            ? File.ReadLines(Path.Combine(kbDir, "jobs.jsonl")).Select(l => JsonSerializer.Deserialize<JclJob>(l, Json)!).ToList()
            : [];
}
