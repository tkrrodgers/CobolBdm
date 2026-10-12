using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.CobolKb.Knowledge;

public enum DdBindingStatus { Bound, NoDdInStep, UnresolvedSymbolic, NoLayout, MissingCopybook, LengthMismatch, Dummy }

public sealed record DdLayout(string? Record, int RecordLength, string? RecordingMode, IReadOnlyList<string> Copybooks,
    IReadOnlyList<string> MissingCopybooks, string? Citation);

public sealed record DdBinding(string OwnerProgram, string LogicalFile, string Dd, string Direction, string Status, string? RawDsn,
    string? ResolvedDsn, string? Generation, string? Member, string? Disp, string? JclRecfm, string? JclLrecl, bool IsDummy,
    string? DdCitation, string? Note, DdLayout? Layout);

public sealed record UnclaimedDd(string Dd, string Kind, string? Dsn, string? Generation, string? Member, string? Disp, string? Recfm, string? Lrecl);

public sealed record DdBindingStep(string Step, string? Program, int Order, string? Parm, string StepCitation, IReadOnlyList<string> CallClosure,
    IReadOnlyDictionary<string, string> Symbolics, IReadOnlyList<DdBinding> Bindings, IReadOnlyList<UnclaimedDd> UnclaimedDds);

public sealed record DdBindingPlan(string Schema, string Job, string AsOf, IReadOnlyDictionary<string, int> Summary,
    IReadOnlyList<DdBindingStep> Steps, IReadOnlyList<string> Unbound, IReadOnlyList<string> Diagnostics);

/// <summary>
/// DD binding (§5.5.2): per unconditional step, the program's static CALL closure, the union of its SELECT/ASSIGN files,
/// matched to the step's DDs, with FD layouts, OPEN-derived direction, and every unresolved join recorded.
/// </summary>
public static partial class DdBindingPlanner
{
    public const string Schema = "ddBindingPlan/v1";

    /// <summary>Utilities the job engine runs without a program source; their DDs are unclaimed, not frontiers.</summary>
    public static readonly HashSet<string> Utilities = new(StringComparer.OrdinalIgnoreCase)
        { "SORT", "ICEMAN", "DFSORT", "SYNCSORT", "IDCAMS", "IEBGENER", "ICEGENER", "IEFBR14", "IKJEFT01", "IKJEFT1B", "DFHCSDUP" };

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    [GeneratedRegex(@"&[A-Z#@$]")] private static partial Regex Symbolic();

    public static DdBindingPlan Plan(string jobName, IEnumerable<string> jclRoots, IEnumerable<string> procRoots,
        IReadOnlyDictionary<string, string> programSources, string asOf, Action<string>? log = null)
    {
        var job = JclHarvester.Harvest(jclRoots, procRoots).FirstOrDefault(j => string.Equals(j.Name, jobName, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException($"job {jobName} not found");
        return Plan(job, programSources, asOf, out _, log);
    }

    public static DdBindingPlan Plan(JclJob job, IReadOnlyDictionary<string, string> programSources, string asOf,
        out Dictionary<string, CobolProgram> models, Action<string>? log = null)
    {
        models = new(StringComparer.OrdinalIgnoreCase);
        var diagnostics = new List<string>();
        var steps = new List<DdBindingStep>();
        foreach (var s in job.Expanded)
        {
            var citation = $"{s.Citation.Member}:{s.Citation.Line}";
            var closure = s.Program is not null && programSources.ContainsKey(s.Program)
                ? CallClosure(s.Program, programSources, diagnostics, models) : [];
            if (s.Program is not null && closure.Count == 0 && !Utilities.Contains(s.Program))
                diagnostics.Add($"{s.QualifiedStep}: program {s.Program} has no source in the estate");
            var bindings = new List<DdBinding>();
            var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pid in closure)
            {
                if (!models.TryGetValue(pid, out var m)) continue;
                foreach (var f in m.Files.Where(f => !f.IsSort))
                {
                    var dd = DdName(f.Assign);
                    claimed.Add(dd);
                    bindings.Add(Bind(pid, m, f, dd, Direction(m, f.Name), s.Datasets.FirstOrDefault(d => string.Equals(d.Dd, dd, StringComparison.OrdinalIgnoreCase))));
                }
                if (ReadsSysin(m))
                {
                    claimed.Add("SYSIN");
                    var d = s.Datasets.FirstOrDefault(x => string.Equals(x.Dd, "SYSIN", StringComparison.OrdinalIgnoreCase));
                    bindings.Add(new DdBinding(pid, "ACCEPT", "SYSIN", "input", d is null ? nameof(DdBindingStatus.NoDdInStep) : d.Dummy ? nameof(DdBindingStatus.Dummy) : nameof(DdBindingStatus.Bound),
                        d?.Dsn, d?.Dsn, d?.Generation, d?.Member, d?.Disp, d?.Recfm, d?.Lrecl, d?.Dummy ?? false, d is null ? null : $"{d.Citation.Member}:{d.Citation.Line}",
                        d?.InStream is not null ? "in-stream data" : null, null));
                }
            }
            var unclaimed = s.Datasets.Where(d => !claimed.Contains(d.Dd)).Select(d => new UnclaimedDd(d.Dd, Kind(d), d.Dsn, d.Generation, d.Member, d.Disp, d.Recfm, d.Lrecl)).ToList();
            steps.Add(new DdBindingStep(s.QualifiedStep, s.Program, s.Order, s.Parm, citation, closure,
                new SortedDictionary<string, string>(s.Symbols.ToDictionary(kv => kv.Key, kv => kv.Value), StringComparer.Ordinal), bindings, unclaimed));
        }
        var all = steps.SelectMany(s => s.Bindings.Select(b => (s, b))).ToList();
        var summary = new SortedDictionary<string, int>(StringComparer.Ordinal)
        {
            ["steps"] = steps.Count,
            ["programSteps"] = steps.Count(s => s.CallClosure.Count > 0),
            ["bindings"] = all.Count
        };
        foreach (var g in all.GroupBy(x => x.b.Status)) summary[g.Key] = g.Count();
        var unbound = all.Where(x => x.b.Status is not (nameof(DdBindingStatus.Bound) or nameof(DdBindingStatus.Dummy)))
            .Select(x => $"{x.s.Step}/{x.b.OwnerProgram}/{x.b.LogicalFile}: {x.b.Status}{(x.b.Note is null ? "" : " (" + x.b.Note + ")")}").ToList();
        foreach (var d in diagnostics) log?.Invoke(d);
        return new DdBindingPlan(Schema, job.Name, asOf, summary, steps, unbound, diagnostics.Distinct().ToList());
    }

    public static string ToJson(DdBindingPlan plan) => JsonSerializer.Serialize(plan, Json) + "\n";

    /// <summary>Breadth-first static CALL closure (entry first), over programs with source; models are cached into <paramref name="models"/>.</summary>
    public static List<string> CallClosure(string entry, IReadOnlyDictionary<string, string> programSources, List<string> diagnostics,
        Dictionary<string, CobolProgram> models)
    {
        var order = new List<string>();
        var queue = new Queue<string>([entry.ToUpperInvariant()]);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.Count > 0)
        {
            var pid = queue.Dequeue();
            if (!seen.Add(pid) || !programSources.TryGetValue(pid, out var path)) continue;
            if (!models.TryGetValue(pid, out var m))
            {
                try { m = CobolModelBuilder.BuildGroundedFull(path); models[pid] = m; }
                catch (Exception e) { diagnostics.Add($"{pid}: model build failed: {e.Message}"); continue; }
            }
            order.Add(pid);
            foreach (var c in ProgramGraphBuilder.Calls(m))
                foreach (var t in c.ResolvedTargets.Count > 0 ? c.ResolvedTargets : [c.Target])
                {
                    if (programSources.ContainsKey(t)) queue.Enqueue(t.ToUpperInvariant());
                    else if (!EstateCallGraph.RuntimeServices.Contains(t) && c.Kind == "static") diagnostics.Add($"{pid}: CALL '{t}' has no source in the estate");
                }
        }
        return order;
    }

    /// <summary>ASSIGN TO name: literal quotes removed; device prefixes (UT-S-, S-, DA-...) dropped, since DD names cannot contain '-'.</summary>
    public static string DdName(string assign)
    {
        var a = assign.Trim().Trim('\'', '"').ToUpperInvariant();
        var dash = a.LastIndexOf('-');
        return dash >= 0 ? a[(dash + 1)..] : a;
    }

    static DdBinding Bind(string pid, CobolProgram m, FileDecl f, string dd, string direction, JclDataset? d)
    {
        var layout = Layout(m, f);
        DdBindingStatus status;
        string? note = null;
        if (d is null) status = DdBindingStatus.NoDdInStep;
        else if (d.Dummy) status = DdBindingStatus.Dummy;
        else if (d.Dsn is not null && Symbolic().IsMatch(d.Dsn)) { status = DdBindingStatus.UnresolvedSymbolic; note = d.Dsn; }
        else if (layout.Record is null) status = layout.MissingCopybooks.Count > 0 ? DdBindingStatus.MissingCopybook : DdBindingStatus.NoLayout;
        else if (int.TryParse(d.Lrecl, out var lrecl) && ExpectedLrecl(layout, d.Recfm) is var want && lrecl != want)
        {
            status = DdBindingStatus.LengthMismatch;
            note = $"JCL LRECL {lrecl}, FD record {layout.RecordLength}{((d.Recfm ?? "").StartsWith('V') ? " (+4 RDW)" : "")}";
        }
        else status = DdBindingStatus.Bound;
        var raw = d?.Dsn is null ? null : d.Dsn + (d.Generation is not null ? $"({d.Generation})" : d.Member is not null ? $"({d.Member})" : "");
        return new DdBinding(pid, f.Name, dd, direction, status.ToString(), raw, d?.Dsn, d?.Generation, d?.Member, d?.Disp, d?.Recfm, d?.Lrecl,
            d?.Dummy ?? false, d is null ? null : $"{d.Citation.Member}:{d.Citation.Line}", note, layout);
    }

    static int ExpectedLrecl(DdLayout l, string? recfm) => (recfm ?? "").StartsWith('V') ? l.RecordLength + 4 : l.RecordLength;

    static DdLayout Layout(CobolProgram m, FileDecl f)
    {
        var record = f.Records.OrderByDescending(r => r.Length).FirstOrDefault();
        var length = f.MaxRecord > 0 ? f.MaxRecord : f.Records.Select(r => r.Length).DefaultIfEmpty(0).Max();
        var records = new HashSet<DataItem>(f.Records);
        var copybooks = m.Items.Where(i => i.Record is not null && records.Contains(i.Record) && i.Origin.Length > 0)
            .Select(i => i.Origin).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var missing = record is null ? m.MissingCopybooks.Select(x => x.Member).Distinct().ToList() : [];
        return new DdLayout(record?.Name, length, f.RecordingMode, copybooks, missing, $"{m.ProgramId}.cbl:{f.Line}");
    }

    static string Kind(JclDataset d) =>
        d.Sysout ? "sysout" : d.InStream is not null ? "instream" : d.Dummy ? "dummy"
        : d.Dd.ToUpperInvariant() is "STEPLIB" or "JOBLIB" ? "library" : d.Member is not null ? "member" : "dataset";

    /// <summary>File direction from the program's OPEN modes: input, output, i-o, extend (joined by '+'), or unopened.</summary>
    public static string Direction(CobolProgram m, string file)
    {
        var modes = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var s in Walk(m))
            if (s.Verb == "OPEN")
                foreach (var f in s.L?.GetValueOrDefault("files") ?? [])
                    if (string.Equals(f.V, file, StringComparison.OrdinalIgnoreCase)) modes.Add(f.Op!.ToLowerInvariant());
        return modes.Count == 0 ? "unopened" : string.Join("+", modes);
    }

    static bool ReadsSysin(CobolProgram m) => Walk(m).Any(s => s.Verb == "ACCEPT" && s.Str("from") is null);

    public static IEnumerable<Stmt> Walk(CobolProgram m)
    {
        static IEnumerable<Stmt> Rec(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                yield return s;
                if (s.Blocks is not null)
                    foreach (var b in s.Blocks.Values)
                        foreach (var x in Rec(b)) yield return x;
            }
        }
        return m.Paragraphs.SelectMany(p => Rec(p.Statements));
    }
}
