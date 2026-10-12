using System.Text.Json;
using System.Text.Json.Serialization;
using MlCpu.Cobol.Analysis;

namespace MlCpu.CobolKb.Knowledge;

public sealed record ReplayLeaf(string Name, int Offset, int Length, string Usage, string? Pic, int Scale, bool Signed,
    IReadOnlyDictionary<string, IReadOnlyList<string>>? Roles);

public sealed record ReplayLayout(string Record, string Role, int Length, IReadOnlyList<ReplayLeaf> Fields);

public sealed record ReplayFile(string Fd, string Dd, string Direction, string Owner, int Lrecl, bool Dummy, string? Dsn, string? Generation,
    string Status, IReadOnlyList<ReplayLayout> Layouts);

public sealed record ReplayOutput(string Dd, IReadOnlyList<ReplayLayout> Layouts, IReadOnlyList<string> VolatileFields, string VolatileFieldsBasis,
    IReadOnlyDictionary<string, IReadOnlyList<string>> Writers);

public sealed record ReplayProgram(string Id, string Class, string Path);

public sealed record ReplayParm(string? Text, string? Source);

public sealed record ReplayStep(string Step, string? Program, string StepCitation, ReplayParm Parm, string? Linkage, IReadOnlyList<string> CallClosure,
    IReadOnlyList<ReplayProgram> Programs, IReadOnlyList<ReplayFile> Files, IReadOnlyList<ReplayOutput> Outputs);

public sealed record ReplayGeneratedFrom(string DdBindingPlan, string Project);

public sealed record ReplayPlan(string Schema, string Job, string AsOf, string Encoding, ReplayGeneratedFrom GeneratedFrom,
    IReadOnlyList<ReplayStep> Steps, IReadOnlyList<string> Frontiers);

/// <summary>
/// Comparison data layer (§5.6.4, §7.6): per step, the files with their leaf layouts, and per output DD the record layouts
/// by role, the fields whose bytes may derive from the clock (volatile, excluded from comparison), and their writers.
/// </summary>
public static class ReplayPlanBuilder
{
    public const string Schema = "mlcpu.replay-plan.v1";

    public const string VolatileBasis =
        "flow-insensitive byte-range taint from FUNCTION CURRENT-DATE / WHEN-COMPILED and ACCEPT FROM DATE|TIME|DAY, " +
        "propagated through MOVE (byte-mapped for alphanumeric and group moves), arithmetic, STRING, UNSTRING and WRITE/REWRITE FROM";

    static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>Writes DdBindingPlan.json and replay/ReplayPlan.json into the project root; returns both texts.</summary>
    public static (string DdBindingPlanJson, string ReplayPlanJson) EmitDataLayer(JclJob job, IReadOnlyDictionary<string, string> programSources,
        string javaProjectRoot, string asOf, string encoding = "CP037")
    {
        var plan = DdBindingPlanner.Plan(job, programSources, asOf, out var models);
        var dd = DdBindingPlanner.ToJson(plan);
        var replay = BuildJson(plan, models, Path.GetFileName(javaProjectRoot.TrimEnd('/', '\\')), encoding);
        File.WriteAllText(Path.Combine(javaProjectRoot, "DdBindingPlan.json"), dd);
        Directory.CreateDirectory(Path.Combine(javaProjectRoot, "replay"));
        File.WriteAllText(Path.Combine(javaProjectRoot, "replay", "ReplayPlan.json"), replay);
        return (dd, replay);
    }

    public static string BuildJson(DdBindingPlan plan, IReadOnlyDictionary<string, CobolProgram> models, string project, string encoding = "CP037") =>
        JsonSerializer.Serialize(Build(plan, models, project, encoding), Json) + "\n";

    public static ReplayPlan Build(DdBindingPlan plan, IReadOnlyDictionary<string, CobolProgram> models, string project, string encoding = "CP037")
    {
        var frontiers = new List<string>(plan.Unbound.Select(u => "unbound " + u));
        var steps = new List<ReplayStep>();
        var taints = new Dictionary<string, Taint>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in plan.Steps)
        {
            var programs = s.CallClosure.Select(p => new ReplayProgram(p, "com.mlcpu.core." + ClassName(p), $"src/main/java/com/mlcpu/core/{ClassName(p)}.java")).ToList();
            var files = new List<ReplayFile>();
            var outputs = new List<ReplayOutput>();
            foreach (var b in s.Bindings.Where(b => b.LogicalFile != "ACCEPT"))
            {
                if (!models.TryGetValue(b.OwnerProgram, out var m)) continue;
                var fd = m.Files.First(f => f.Name == b.LogicalFile);
                files.Add(new ReplayFile(fd.Name, b.Dd, b.Direction, b.OwnerProgram, b.Layout?.RecordLength ?? 0, b.IsDummy, b.ResolvedDsn, b.Generation, b.Status,
                    fd.Records.Select(r => Layout(r, "DTL")).ToList()));
                if (!(b.Direction.Contains("output") || b.Direction.Contains("extend") || b.Direction.Contains("i-o"))) continue;
                if (!taints.TryGetValue(m.ProgramId, out var taint)) taints[m.ProgramId] = taint = Taint.Of(m);
                var written = WrittenShapes(m, fd);
                var layouts = written.Select(w => Layout(w, Role(w, written.Count))).ToList();
                var volatileFields = new SortedSet<string>(StringComparer.Ordinal);
                var writers = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
                foreach (var w in written)
                {
                    foreach (var leaf in Leaves(w))
                    {
                        if (taint.Overlaps(leaf)) volatileFields.Add($"{w.Name}.{leaf.Name}");
                        var ws = taint.WritersOf(leaf);
                        if (ws.Count > 0) writers[$"{w.Name}.{leaf.Name}"] = ws;
                    }
                }
                outputs.Add(new ReplayOutput(b.Dd, layouts, volatileFields.ToList(), VolatileBasis, writers));
            }
            var entry = s.Program is not null && models.TryGetValue(s.Program, out var em) ? em : null;
            var linkage = entry is null || entry.Using.Count == 0 ? null
                : string.Join(",", entry.Using.Select(u => entry.Items.FirstOrDefault(i => i.Name == u && i.Level is 1 or 77) is { } it ? $"{u}({it.Length})" : u));
            steps.Add(new ReplayStep(s.Step, s.Program, s.StepCitation, new ReplayParm(s.Parm, s.Parm is null ? null : "jcl"), linkage, s.CallClosure,
                programs, files, outputs));
        }
        return new ReplayPlan(Schema, plan.Job, plan.AsOf, encoding, new ReplayGeneratedFrom(plan.Schema, project), steps, frontiers);
    }

    static string ClassName(string pid) => "Prog_" + new string(pid.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    /// <summary>Record shapes written to a file: the FROM operand of WRITE/REWRITE when present, otherwise the FD record.</summary>
    static List<DataItem> WrittenShapes(CobolProgram m, FileDecl fd)
    {
        var byId = m.Items.ToDictionary(i => i.Id);
        var fdRecords = new HashSet<int>(fd.Records.Select(r => r.Id));
        var shapes = new List<DataItem>();
        foreach (var s in DdBindingPlanner.Walk(m).Where(s => s.Verb is "WRITE" or "REWRITE"))
        {
            if (s.Get("record")?.Id is not { } rid || !byId.TryGetValue(rid, out var rec) || !fdRecords.Contains(rec.Record.Id)) continue;
            var shape = s.Get("from") is { K: "ref", Id: { } fid } && byId.TryGetValue(fid, out var from) ? from : rec;
            if (!shapes.Contains(shape)) shapes.Add(shape);
        }
        return shapes.Count > 0 ? shapes : fd.Records.ToList();
    }

    /// <summary>HDR/TLR by the written record's name, otherwise DTL. Single-shape files are DTL.</summary>
    static string Role(DataItem shape, int shapes)
    {
        if (shapes == 1) return "DTL";
        var n = shape.Name.ToUpperInvariant();
        if (n.Contains("HEADER") || n.Contains("HDR") || n.Contains("HEADING") || n.Contains("TITLE")) return "HDR";
        if (n.Contains("TOTAL") || n.Contains("TRAILER") || n.Contains("TLR") || n.Contains("FOOTER")) return "TLR";
        return "DTL";
    }

    static ReplayLayout Layout(DataItem shape, string role) =>
        new(shape.Name, role, shape.Length, Leaves(shape).Select(l => new ReplayLeaf(l.Name, l.Offset, l.Length, l.Usage, l.Pic, l.Scale, l.Signed, l.Roles)).ToList());

    internal sealed record Leaf(string Name, int Offset, int Length, string Usage, string? Pic, int Scale, bool Signed,
        IReadOnlyDictionary<string, IReadOnlyList<string>>? Roles, DataItem Item, int AbsOffset);

    /// <summary>A record's elementary fields (OCCURS expanded, offsets relative to the record), for decoding record images.</summary>
    public sealed record LayoutField(string Name, int Offset, int Length, string Usage, string? Pic, int Scale, bool Signed, bool Numeric);

    public static IReadOnlyList<LayoutField> Layout(DataItem record) =>
        Leaves(record).Select(l => new LayoutField(l.Name, l.Offset, l.Length, l.Usage, l.Pic, l.Scale, l.Signed, l.Item.Category == Category.Numeric)).ToList();

    /// <summary>Elementary items under <paramref name="shape"/> with offsets relative to it (AbsOffset: within the storage area); OCCURS expanded (up to 2,000 leaves).</summary>
    internal static List<Leaf> Leaves(DataItem shape)
    {
        var leaves = new List<Leaf>();
        var areaStart = Taint.RangeOf(shape).Start;
        void Visit(DataItem it, int off, string suffix)
        {
            if (it.Level == 88 || leaves.Count > 2000) return;
            int count = Math.Max(1, it.Occurs);
            for (int k = 0; k < count; k++)
            {
                var at = off + k * it.Length;
                var sfx = it.Occurs > 0 ? $"{suffix}({k + 1})" : suffix;
                if (it.IsLeaf)
                {
                    var name = (it.Name == "FILLER" ? $"FILLER@{at}" : it.Name) + sfx;
                    var roles = it.Children.Where(c => c.Level == 88).ToDictionary(c => c.Name,
                        c => (IReadOnlyList<string>)c.Values.Select(v => v.High is null ? v.Low.Text : $"{v.Low.Text} THRU {v.High.Text}").ToList());
                    leaves.Add(new Leaf(name, at, it.Length, UsageOf(it), it.Pic, it.Scale, it.Signed, roles.Count == 0 ? null : roles, it, areaStart + at));
                }
                else
                    foreach (var c in it.Children.Where(c => c.Level != 88)) Visit(c, at + (c.Offset - it.Offset), sfx);
            }
        }
        if (shape.IsLeaf) Visit(shape, 0, "");
        else foreach (var c in shape.Children.Where(c => c.Level != 88)) Visit(c, c.Offset - shape.Offset, "");
        return leaves;
    }

    internal static string UsageOf(DataItem it) => it.Category is Category.NumericEdited or Category.AlphanumericEdited ? "EDITED" : it.Usage switch
    {
        Usage.Comp3 => "PACKED",
        Usage.Binary or Usage.Comp5 => "BINARY",
        Usage.Comp1 or Usage.Comp2 => "FLOAT",
        _ => "DISPLAY"
    };

    /// <summary>Byte-range taint over storage areas (same area model as the PDG: FD records share the file area, REDEFINES share the root).</summary>
    internal sealed class Taint
    {
        readonly Dictionary<string, List<(int S, int E)>> _ranges = [];
        readonly List<(string Area, int S, int E, string StepId, string Text)> _defs = [];
        readonly Dictionary<int, DataItem> _items;

        Taint(CobolProgram m) => _items = m.Items.ToDictionary(i => i.Id);

        public static Taint Of(CobolProgram m)
        {
            var t = new Taint(m);
            var stmts = DdBindingPlanner.Walk(m).ToList();
            foreach (var s in stmts) t.RecordDefs(s);
            for (int pass = 0; pass < 20 && stmts.Aggregate(false, (changed, s) => t.Apply(s) | changed); pass++) { }
            return t;
        }

        public static (string Area, int Start, int End) RangeOf(DataItem it)
        {
            var root = it.Record ?? it;
            while (root.RedefinesItem is not null && root.RedefinesItem != root) root = root.RedefinesItem.Record ?? root.RedefinesItem;
            var area = root.FileName is not null ? "F:" + root.FileName : "R:" + root.Id;
            var start = it.Offset;
            var len = it.Length * Math.Max(1, it.Occurs);
            if (it.Dims.Count > 0 && it.Occurs == 0)
            {
                var t = it;
                while (t.Parent is not null && t.Parent.Dims.Count > 0) t = t.Parent;
                start = t.Offset; len = t.Length * Math.Max(1, t.Occurs);
            }
            return (area, start, start + Math.Max(1, len));
        }

        public bool Overlaps(Leaf leaf)
        {
            var a = RangeOf(leaf.Item).Area;
            return _ranges.TryGetValue(a, out var rs) && rs.Any(r => r.S < leaf.AbsOffset + leaf.Length && leaf.AbsOffset < r.E);
        }

        public IReadOnlyList<string> WritersOf(Leaf leaf)
        {
            var a = RangeOf(leaf.Item).Area;
            return _defs.Where(d => d.Area == a && d.S < leaf.AbsOffset + leaf.Length && leaf.AbsOffset < d.E)
                .Select(d => $"{d.StepId} {d.Text}").Distinct().Take(8).ToList();
        }

        void RecordDefs(Stmt s)
        {
            foreach (var d in Targets(s))
                if (Item(d) is { } it)
                {
                    var (a, st, e) = RangeOf(it);
                    var text = s.Text.Length > 90 ? s.Text[..90] + "..." : s.Text;
                    _defs.Add((a, st, e, s.StepId, text));
                }
        }

        static IEnumerable<Node> Targets(Stmt s)
        {
            IEnumerable<Node> L(string k) => s.L?.GetValueOrDefault(k) ?? [];
            switch (s.Verb)
            {
                case "MOVE" or "COMPUTE" or "INITIALIZE" or "SET": return L("dst");
                case "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE": return L("dst").Concat(L("giving"));
                case "ACCEPT": return s.Get("dst") is { } d ? [d] : [];
                case "STRING": return s.Get("into") is { } i ? [i] : [];
                case "UNSTRING": return L("targets").Select(t => t.L).OfType<Node>();
                case "READ": return s.Get("into") is { } r ? [r] : [];
                case "WRITE" or "REWRITE": return s.Get("from") is not null && s.Get("record") is { } rec ? [rec] : [];
                default: return [];
            }
        }

        DataItem? Item(Node? n)
        {
            if (n is not { K: "ref" or "c88", Id: { } id } || !_items.TryGetValue(id, out var it)) return null;
            return it.Level == 88 && it.Parent is not null ? it.Parent : it;
        }

        static bool Clock(Node? n) => n is not null && (n.K == "fn" && n.V is "CURRENT-DATE" or "WHEN-COMPILED"
            || Clock(n.L) || Clock(n.R) || Clock(n.Start) || Clock(n.Len) || (n.Args?.Any(Clock) ?? false));

        bool Tainted(Node? n)
        {
            if (n is null) return false;
            if (Clock(n)) return true;
            if (Item(n) is { } it)
            {
                var (a, s, e) = RangeOf(it);
                if (_ranges.TryGetValue(a, out var rs) && rs.Any(r => r.S < e && s < r.E)) return true;
            }
            return Tainted(n.L) || Tainted(n.R) || (n.Args?.Any(Tainted) ?? false);
        }

        bool Add(string area, int s, int e)
        {
            if (e <= s) return false;
            if (!_ranges.TryGetValue(area, out var rs)) _ranges[area] = rs = [];
            if (rs.Any(r => r.S <= s && e <= r.E)) return false;
            rs.Add((s, e));
            return true;
        }

        bool Whole(Node? n) => Item(n) is { } it && Add(RangeOf(it).Area, RangeOf(it).Start, RangeOf(it).End);

        /// <summary>Byte copy src → dst for alphanumeric/group moves: tainted sub-ranges keep their relative position, clipped to dst.</summary>
        bool Copy(Node src, Node dst)
        {
            if (Clock(src)) return Whole(dst);
            if (Item(src) is not { } si || Item(dst) is not { } di) return Tainted(src) && Whole(dst);
            bool bytewise = si.Category is not Category.Numeric && di.Category is Category.Group or Category.Alphanumeric or Category.Alphabetic
                && src.Start is null && dst.Start is null && si.Dims.Count == 0 && di.Dims.Count == 0;
            var (sa, ss, se) = RangeOf(si);
            if (!_ranges.TryGetValue(sa, out var rs)) return false;
            var hits = rs.Where(r => r.S < se && ss < r.E).ToList();
            if (hits.Count == 0) return false;
            if (!bytewise) return Whole(dst);
            var (da, ds, de) = RangeOf(di);
            bool changed = false;
            foreach (var r in hits)
                changed |= Add(da, ds + Math.Max(0, r.S - ss), Math.Min(de, ds + (Math.Min(r.E, se) - ss)));
            return changed;
        }

        bool Apply(Stmt s)
        {
            IEnumerable<Node> L(string k) => s.L?.GetValueOrDefault(k) ?? [];
            bool changed = false;
            switch (s.Verb)
            {
                case "MOVE" when s.Get("src") is { } src:
                    foreach (var d in L("dst")) changed |= Copy(src, d);
                    break;
                case "COMPUTE" when Tainted(s.Get("expr")):
                    foreach (var d in L("dst")) changed |= Whole(d);
                    break;
                case "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE" when L("ops").Any(Tainted) || Tainted(s.Get("by")) || Tainted(s.Get("minuend")) || L("dst").Any(Tainted):
                    foreach (var d in L("dst").Concat(L("giving"))) changed |= Whole(d);
                    break;
                case "ACCEPT" when s.Str("from") is { } f && (f.StartsWith("DATE") || f.StartsWith("TIME") || f.StartsWith("DAY")):
                    changed |= Whole(s.Get("dst"));
                    break;
                case "STRING" when L("pieces").Any(p => Tainted(p.L)):
                    changed |= Whole(s.Get("into"));
                    break;
                case "UNSTRING" when Tainted(s.Get("src")):
                    foreach (var t in L("targets")) changed |= Whole(t.L);
                    break;
                case "WRITE" or "REWRITE" when s.Get("from") is { } from && s.Get("record") is { } rec:
                    changed |= Copy(from, rec);
                    break;
            }
            return changed;
        }
    }
}
