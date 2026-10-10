using System.Text;

namespace MlCpu.Cobol.Analysis.Entities;

public enum ParagraphEffect { Domain, Orchestration, Io, Sql, Cics, ExternalCall, Console, Time, Empty, Unclassified }

public sealed record ParagraphPlan(string Paragraph, string Effect, bool Reachable, int Statements, IReadOnlyList<string> Writes, IReadOnlyList<string> Reads,
    IReadOnlyList<string> Performs, bool PureClosure);

public sealed record DomainCluster(string Id, IReadOnlyList<string> Paragraphs, IReadOnlyList<string> OwnedFields, IReadOnlyList<string> SharedFields, int Statements);

public sealed record PartitionPlan(string Schema, string ProgramId, IReadOnlyList<ParagraphPlan> Paragraphs, IReadOnlyList<DomainCluster> Clusters,
    IReadOnlyList<string> OwnedFields, IReadOnlyList<string> SharedFields);

/// <summary>
/// Paragraph effects and domain clusters (Answers Q12). Impurity takes precedence (SQL &gt; CICS &gt; I/O &gt; CALL &gt; console &gt;
/// time &gt; orchestration &gt; domain). Domain paragraphs that write the same alias group (a record with REDEFINES or OCCURS
/// overlays, otherwise the item) are unioned; the union-find root is the ordinally smaller member.
/// </summary>
public static class BulkPartitionPlanner
{
    public const string Schema = "mlcpu.partition-plan.v1";

    public static ParagraphEffect Classify(Paragraph p)
    {
        var verbs = new HashSet<string>(StringComparer.Ordinal);
        bool time = false;
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                verbs.Add(s.Verb);
                if (UsesTime(s)) time = true;
                if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Walk(b);
            }
        }
        Walk(p.Statements);
        if (verbs.Contains("EXEC-SQL")) return ParagraphEffect.Sql;
        if (verbs.Contains("EXEC-CICS") || verbs.Contains("EXEC-DLI")) return ParagraphEffect.Cics;
        if (verbs.Overlaps(["OPEN", "CLOSE", "READ", "WRITE", "REWRITE", "DELETE", "START", "SORT", "MERGE", "RELEASE", "RETURN"])) return ParagraphEffect.Io;
        if (verbs.Contains("CALL")) return ParagraphEffect.ExternalCall;
        if (verbs.Contains("DISPLAY")) return ParagraphEffect.Console;
        if (time || verbs.Contains("ACCEPT")) return ParagraphEffect.Time;
        if (verbs.Overlaps(["PERFORM", "GO TO", "GOBACK", "STOP RUN", "EXIT PROGRAM", "ALTER"])) return ParagraphEffect.Orchestration;
        if (verbs.All(v => v is "EXIT" or "CONTINUE" or "EXIT PARAGRAPH")) return ParagraphEffect.Empty;
        if (verbs.Contains("RAW")) return ParagraphEffect.Unclassified;
        return ParagraphEffect.Domain;
    }

    static bool UsesTime(Stmt s)
    {
        bool Fn(Node? n) => n is not null && (n.K == "fn" && n.V == "CURRENT-DATE" || Fn(n.L) || Fn(n.R) || (n.Args?.Any(Fn) ?? false));
        return (s.N?.Values.Any(Fn) ?? false) || (s.L?.Values.SelectMany(x => x).Any(Fn) ?? false);
    }

    public static PartitionPlan Plan(CobolProgram program, ProgramGraph graph)
    {
        var items = program.Items.ToDictionary(i => i.Id);
        var dead = new HashSet<string>(graph.DeadParagraphs, StringComparer.OrdinalIgnoreCase);
        var effects = program.Paragraphs.ToDictionary(p => p.Name, Classify, StringComparer.OrdinalIgnoreCase);
        var performs = graph.ParagraphEdges.Where(e => e.Kind.StartsWith("perform") || e.Kind == "goto")
            .GroupBy(e => e.From, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Select(e => e.To).Distinct().ToList(), StringComparer.OrdinalIgnoreCase);

        bool PureClosure(string name, HashSet<string> seen)
        {
            if (!seen.Add(name)) return true;
            if (!effects.TryGetValue(name, out var e)) return false;
            if (e is ParagraphEffect.Io or ParagraphEffect.Sql or ParagraphEffect.Cics or ParagraphEffect.ExternalCall or ParagraphEffect.Unclassified) return false;
            return performs.GetValueOrDefault(name)?.All(t => PureClosure(t, seen)) ?? true;
        }

        var plans = new List<ParagraphPlan>();
        foreach (var p in program.Paragraphs)
        {
            var (writes, reads, count) = Access(p, items);
            plans.Add(new ParagraphPlan(p.Name, effects[p.Name].ToString(), !dead.Contains(p.Name), count, writes, reads,
                performs.GetValueOrDefault(p.Name) ?? [], PureClosure(p.Name, new HashSet<string>(StringComparer.OrdinalIgnoreCase))));
        }

        // Union domain paragraphs over shared alias groups.
        var parent = plans.Where(x => x.Effect == nameof(ParagraphEffect.Domain)).ToDictionary(x => x.Paragraph, x => x.Paragraph, StringComparer.OrdinalIgnoreCase);
        string Find(string x) => parent[x] == x ? x : parent[x] = Find(parent[x]);
        var groupOwner = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pl in plans.Where(x => parent.ContainsKey(x.Paragraph)))
            foreach (var w in pl.Writes.Select(f => AliasGroup(f, program)))
            {
                if (!groupOwner.TryGetValue(w, out var other)) { groupOwner[w] = pl.Paragraph; continue; }
                var (a, b) = (Find(other), Find(pl.Paragraph));
                if (a == b) continue;
                if (string.CompareOrdinal(a, b) < 0) parent[b] = a; else parent[a] = b;
            }
        var touchedByNonDomain = new HashSet<string>(plans.Where(x => !parent.ContainsKey(x.Paragraph)).SelectMany(x => x.Writes.Concat(x.Reads)), StringComparer.OrdinalIgnoreCase);
        var clusters = new List<DomainCluster>();
        var clusterOfField = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        var groups = parent.Keys.GroupBy(Find, StringComparer.OrdinalIgnoreCase).OrderBy(g => g.Key, StringComparer.Ordinal).ToList();
        foreach (var g in groups)
            foreach (var f in plans.Where(x => g.Contains(x.Paragraph, StringComparer.OrdinalIgnoreCase)).SelectMany(x => x.Writes.Concat(x.Reads)))
                (clusterOfField.TryGetValue(f, out var s) ? s : clusterOfField[f] = new HashSet<string>(StringComparer.OrdinalIgnoreCase)).Add(g.Key);
        int n = 0;
        foreach (var g in groups)
        {
            var members = g.OrderBy(x => x, StringComparer.Ordinal).ToList();
            var fields = plans.Where(x => members.Contains(x.Paragraph, StringComparer.OrdinalIgnoreCase)).SelectMany(x => x.Writes).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            var shared = fields.Where(f => touchedByNonDomain.Contains(f) || clusterOfField[f].Count > 1).OrderBy(x => x, StringComparer.Ordinal).ToList();
            clusters.Add(new DomainCluster($"{program.ProgramId}Domain{++n:00}", members, fields.Except(shared, StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList(),
                shared, plans.Where(x => members.Contains(x.Paragraph, StringComparer.OrdinalIgnoreCase)).Sum(x => x.Statements)));
        }
        var allShared = clusters.SelectMany(c => c.SharedFields).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        var allOwned = clusters.SelectMany(c => c.OwnedFields).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
        return new PartitionPlan(Schema, program.ProgramId, plans, clusters, allOwned, allShared);
    }

    static string AliasGroup(string field, CobolProgram program)
    {
        var it = program.Items.FirstOrDefault(i => i.Name == field && i.Level != 88);
        if (it?.Record is null) return field;
        var rec = it.Record;
        bool overlays = program.Items.Any(i => i.Record == rec && (i.Redefines is not null || i.Occurs > 0)) || rec.RedefinesItem is not null;
        return overlays ? "REC:" + (rec.RedefinesItem?.Name ?? rec.Name) : field;
    }

    static (List<string> Writes, List<string> Reads, int Count) Access(Paragraph p, Dictionary<int, DataItem> items)
    {
        var writes = new SortedSet<string>(StringComparer.Ordinal);
        var reads = new SortedSet<string>(StringComparer.Ordinal);
        int count = 0;
        string[] defKeys = ["dst", "giving", "into", "rem", "pointer", "tallying", "var"];
        void Refs(Node? n, SortedSet<string> into)
        {
            if (n is null) return;
            if (n.K is "ref" or "c88" && n.Id is { } id && items.TryGetValue(id, out var it)) into.Add(it.Level == 88 && it.Parent is not null ? it.Parent.Name : it.Name);
            Refs(n.L, into); Refs(n.R, into); Refs(n.Start, into); Refs(n.Len, into);
            foreach (var a in n.Args ?? []) Refs(a, into);
        }
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                if (s.Verb is not ("EXIT" or "CONTINUE")) count++;
                foreach (var (k, v) in s.N ?? []) Refs(v, defKeys.Contains(k) ? writes : reads);
                foreach (var (k, v) in s.L ?? []) foreach (var x in v) Refs(x, defKeys.Contains(k) ? writes : reads);
                if (s.Verb == "SET" && s.Str("mode") == "TRUE") foreach (var d in s.L?.GetValueOrDefault("dst") ?? []) Refs(d, writes);
                if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Walk(b);
            }
        }
        Walk(p.Statements);
        return (writes.ToList(), reads.ToList(), count);
    }

    public const string SummaryHeader = "Program,Paragraphs,ReachableParagraphs,Statements,Domain,ReachableDomain,DomainStatements,Clusters,ReachableClusters,OwnedFields,SharedFields,Orchestration,Io,Sql,Cics,ExternalCall,Console,Unclassified,Empty";

    public static string SummaryRow(PartitionPlan plan)
    {
        int E(string e) => plan.Paragraphs.Count(p => p.Effect == e);
        var reachable = plan.Paragraphs.Where(p => p.Reachable).Select(p => p.Paragraph).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var domain = plan.Paragraphs.Where(p => p.Effect == nameof(ParagraphEffect.Domain)).ToList();
        return string.Join(",", plan.ProgramId, plan.Paragraphs.Count, reachable.Count, plan.Paragraphs.Sum(p => p.Statements), domain.Count,
            domain.Count(p => p.Reachable), domain.Sum(p => p.Statements), plan.Clusters.Count, plan.Clusters.Count(c => c.Paragraphs.Any(reachable.Contains)),
            plan.OwnedFields.Count, plan.SharedFields.Count, E("Orchestration"), E("Io"), E("Sql"), E("Cics"), E("ExternalCall"), E("Console"), E("Unclassified"), E("Empty"));
    }
}
