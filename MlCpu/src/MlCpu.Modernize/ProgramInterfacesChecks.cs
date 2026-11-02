using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

/// <summary>
/// R5 program-interfaces self checks (MlCpu.Modernize r5-selftest [project scratch-dir]...): the decision cases on synthetic
/// units (widened: interface-only uses, null checks, string conversion, flows into accepting declarations; kept: members
/// outside the interface or bound differently, outbound overloads, JDK and unknown call arguments, lambda captures, casts,
/// instanceof, reference comparison, ambiguous or missing interfaces, every keep obligation, flows into a declaration that
/// stays the class, to a fixpoint; JDK collections: List/Map/Set/SortedMap with implementation-specific members kept), the
/// binding rules of Overload.java (work/phase-m/r5/fixtures/overload), invariance under permutation and renaming of every
/// identity and under poisoned display, site and source ids; and, per estate, the same decisions and sources from a run whose
/// class, package, method, field and variable names, FRAME signatures and parameter type strings are random tokens and whose
/// programs carry planted J3S-style comments, sources that differ from the rule-off run only in widened declaration types (and
/// their imports), and only R5 running.
/// </summary>
public static class ProgramInterfacesChecks
{
    public static int Run(string[] args)
    {
        int failed = 0, passed = 0;
        void Check(string name, bool ok, string detail = "")
        {
            if (ok) passed++; else failed++;
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")} {name}{(detail.Length > 0 ? ": " + detail : "")}");
        }
        foreach (var (name, ok, detail) in Cases()) Check(name, ok, detail);
        for (int i = 0; i + 1 < args.Length; i += 2)
            foreach (var (name, ok, detail) in Estate(args[i], args[i + 1])) Check(name, ok, detail);
        Console.WriteLine($"r5-selftest: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    // ------------------------------------------------------------------ synthetic units

    static ProgramInterfaces.Use Recv(string member, params string[] covers) => new() { Site = "S", Kind = ProgramInterfaces.UseKind.Receiver, Member = member, Covers = [.. covers], Why = covers.Length == 0 ? "a member the interface does not declare" : null };
    static ProgramInterfaces.Use Esc(string why) => new() { Site = "S", Kind = ProgramInterfaces.UseKind.Escape, Why = why };
    static ProgramInterfaces.Use Plain(ProgramInterfaces.UseKind k) => new() { Site = "S", Kind = k };
    static ProgramInterfaces.Use FlowTo(string? target, params string[] accepts) => new() { Site = "S", Kind = ProgramInterfaces.UseKind.Flow, Target = target, Accepts = [.. accepts] };
    static ProgramInterfaces.Use ArgTo(string? target, string? obstacle, params string[] accepts) => new() { Site = "S", Kind = ProgramInterfaces.UseKind.Argument, Target = target, Accepts = [.. accepts], Why = obstacle };

    static ProgramInterfaces.Unit U(string id, string[] options, ProgramInterfaces.Use[] uses, string[]? keep = null, string kind = "field", string from = "T001") => new()
    {
        Id = id, Kind = kind, From = from, Options = [.. options], Uses = [.. uses], Keep = [.. keep ?? []], Display = "display " + id, FromDisplay = from,
    };

    static List<ProgramInterfaces.Unit> Set() =>
    [
        U("F001", ["I01"], [Recv("fn:A/M1", "I01"), Plain(ProgramInterfaces.UseKind.Discard)]),
        U("F002", ["I01"], [Recv("fn:A/M2")]),
        U("F003", ["I01"], [Recv("fn:A/M3", "I01"), ArgTo(null, "outbound overload: the called method has a sibling with the same name and arity", "I01")]),
        U("F004", ["I01"], [Esc("an argument of a JDK method (java.util.List.add)")]),
        U("F005", ["I01"], [Esc("a lambda capture")]),
        U("F006", ["I01"], [Esc("instanceof")]),
        U("F007", ["I01"], [Esc("a cast")]),
        U("F008", ["I01"], [Esc("a reference comparison")]),
        U("F009", ["I01"], [Plain(ProgramInterfaces.UseKind.NullCompare), Plain(ProgramInterfaces.UseKind.StringConversion), Plain(ProgramInterfaces.UseKind.Discard)]),
        U("F010", ["I01", "I02"], [Recv("fn:A/M1", "I01", "I02")]),
        U("F011", [], [Recv("fn:A/M1")]),
        U("F012", ["I01"], [Recv("fn:A/M1", "I01")], ["override: it overrides a superclass method"]),
        U("P001", ["I01"], [Recv("fn:A/M1", "I01")], ["external or unknown caller: CALL from the job dispatch"], "parameter"),
        U("F013", ["I01"], [Recv("fn:A/M1", "I01")], ["accessor: a generated getter prints the field's type"]),
        U("F014", ["I01"], [Recv("fn:A/M1", "I01")], ["reflection/provider registration: the class is loaded by name"]),
        U("P002", ["I01"], [Recv("fn:A/M1", "I01")], ["type variable: the parameter is generic"], "parameter"),
        U("F015", ["I01"], [FlowTo(null, "I01")]),
        U("F016", ["I01"], [FlowTo(null)]),
        // a chain: L001 flows into L002, L002 into a declaration that stays the class: both kept (fixpoint)
        U("L001", ["I01"], [FlowTo("L002")], kind: "local"),
        U("L002", ["I01"], [FlowTo(null)], kind: "local"),
        // a cycle of two widened units: both widened
        U("L003", ["I01"], [FlowTo("L004"), Recv("fn:A/M1", "I01")], kind: "local"),
        U("L004", ["I01"], [FlowTo("L003")], kind: "local"),
        // R003 flows into a unit chosen for another interface: kept
        U("R001", ["I01", "I02"], [Recv("fn:A/M1", "I01"), FlowTo("R002")], kind: "return"),
        U("R002", ["I01", "I02"], [Recv("fn:A/M9", "I02")], kind: "return"),
        // JDK collections
        U("L005", ["java.util.List"], [Recv("add", "java.util.List"), Recv("get", "java.util.List"), Recv("size", "java.util.List")], kind: "local", from: "java.util.ArrayList"),
        U("L006", ["java.util.List"], [Recv("add", "java.util.List"), new() { Site = "S", Kind = ProgramInterfaces.UseKind.Receiver, Member = "ensureCapacity", Why = "implementation-specific member ensureCapacity" }], kind: "local", from: "java.util.ArrayList"),
        U("L007", ["java.util.SortedMap"], [Recv("put", "java.util.SortedMap"), Recv("firstKey", "java.util.SortedMap")], kind: "local", from: "java.util.TreeMap"),
        U("L008", ["java.util.SortedMap"], [new() { Site = "S", Kind = ProgramInterfaces.UseKind.Receiver, Member = "descendingMap", Why = "implementation-specific member descendingMap" }], kind: "local", from: "java.util.TreeMap"),
        U("L009", ["java.util.List", "java.util.Deque"], [Recv("size", "java.util.List", "java.util.Deque")], kind: "local", from: "java.util.LinkedList"),
        U("L010", [], [], ["implementation contract: identity keys: Map's equals-based contract does not describe it"], "local", "java.util.IdentityHashMap"),
    ];

    static readonly Dictionary<string, (string Outcome, string ReasonStart, string? To)> Expected = new(StringComparer.Ordinal)
    {
        ["F001"] = ("applied", "every use", "I01"), ["F002"] = ("kept", "member", null), ["F003"] = ("kept", "outbound binding", null),
        ["F004"] = ("kept", "escape", null), ["F005"] = ("kept", "escape", null), ["F006"] = ("kept", "escape", null), ["F007"] = ("kept", "escape", null),
        ["F008"] = ("kept", "escape", null), ["F009"] = ("applied", "every use", "I01"), ["F010"] = ("kept", "ambiguous", null), ["F011"] = ("skipped", "no interface", null),
        ["F012"] = ("kept", "keep obligation", null), ["P001"] = ("kept", "keep obligation", null), ["F013"] = ("kept", "keep obligation", null),
        ["F014"] = ("kept", "keep obligation", null), ["P002"] = ("kept", "keep obligation", null), ["F015"] = ("applied", "every use", "I01"),
        ["F016"] = ("kept", "flow: into", null), ["L001"] = ("kept", "flow: into", null), ["L002"] = ("kept", "flow: into", null),
        ["L003"] = ("applied", "every use", "I01"), ["L004"] = ("applied", "every use", "I01"), ["R001"] = ("kept", "flow: into", null), ["R002"] = ("applied", "every use", "I02"),
        ["L005"] = ("applied", "every use", "java.util.List"), ["L006"] = ("kept", "member", null), ["L007"] = ("applied", "every use", "java.util.SortedMap"),
        ["L008"] = ("kept", "member", null), ["L009"] = ("kept", "ambiguous", null), ["L010"] = ("skipped", "no interface", null),
    };

    static IEnumerable<(string, bool, string)> Cases()
    {
        var set = Set();
        var exp = Expected;
        var d = ProgramInterfaces.Decide(set).ToDictionary(x => x.U.Id);
        foreach (var (id, (outcome, reason, to)) in exp.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var x = d[id];
            yield return ($"decision {id} ({x.U.Kind}): {outcome}", x.Outcome == outcome && x.Reason.StartsWith(reason, StringComparison.Ordinal) && x.To == to, $"{x.Outcome}: {x.Reason}" + (x.To is null ? "" : " -> " + x.To));
        }
        yield return ("every unit decided once", d.Count == set.Count && exp.Count == set.Count, $"{d.Count} decisions");

        // the binding rules (Overload.java): outbound overloads and receiver rebinding are detected
        yield return ("binding: f(ArrayList) and f(List) are overloads (case 1)", ProgramInterfaces.Overloaded(["java.util.ArrayList<String>", "java.util.List<String>"]), "");
        yield return ("binding: g(Square) and g(Shape) are overloads (case 2)", ProgramInterfaces.Overloaded(["T001", "I01"]), "");
        yield return ("binding: one target signature is not an overload (overrides share it)", !ProgramInterfaces.Overloaded(["T001", "T001"]), "");
        yield return ("binding: m(String)+m(Object) on the class vs m(Object) on the interface rebinds (case 3)",
            !ProgramInterfaces.SameBinding(["java.lang.String->java.lang.String throws ", "java.lang.Object->java.lang.String throws "], ["java.lang.Object->java.lang.String throws "]), "");
        yield return ("binding: one equal signature on both binds the same", ProgramInterfaces.SameBinding(["T009->int throws ", "T009->int throws "], ["T009->int throws "]), "");
        yield return ("binding: a different result or a checked exception rebinds", !ProgramInterfaces.SameBinding(["T009->long throws "], ["T009->int throws "])
            && !ProgramInterfaces.SameBinding(["T009->int throws java.io.IOException"], ["T009->int throws "]), "");
        // the JDK contracts
        yield return ("jdk: List declares add/get/size/subList, not ensureCapacity/trimToSize/clone",
            ProgramInterfaces.JdkDeclares("java.util.List", "add", 1) && ProgramInterfaces.JdkDeclares("java.util.List", "subList", 2) && ProgramInterfaces.JdkDeclares("java.util.List", "size", 0)
            && !ProgramInterfaces.JdkDeclares("java.util.List", "ensureCapacity", 1) && !ProgramInterfaces.JdkDeclares("java.util.List", "trimToSize", 0) && !ProgramInterfaces.JdkDeclares("java.util.List", "clone", 0), "");
        yield return ("jdk: SortedMap declares headMap(K), not headMap(K, boolean) nor descendingMap/ceilingKey",
            ProgramInterfaces.JdkDeclares("java.util.SortedMap", "headMap", 1) && !ProgramInterfaces.JdkDeclares("java.util.SortedMap", "headMap", 2)
            && !ProgramInterfaces.JdkDeclares("java.util.SortedMap", "descendingMap", 0) && !ProgramInterfaces.JdkDeclares("java.util.SortedMap", "ceilingKey", 1), "");
        yield return ("jdk: options ArrayList->List, HashMap/LinkedHashMap->Map, TreeMap->SortedMap, HashSet->Set, TreeSet->SortedSet; IdentityHashMap kept",
            ProgramInterfaces.JdkOptions["java.util.ArrayList"].SequenceEqual(["java.util.List"]) && ProgramInterfaces.JdkOptions["java.util.HashMap"].SequenceEqual(["java.util.Map"])
            && ProgramInterfaces.JdkOptions["java.util.LinkedHashMap"].SequenceEqual(["java.util.Map"]) && ProgramInterfaces.JdkOptions["java.util.TreeMap"].SequenceEqual(["java.util.SortedMap"])
            && ProgramInterfaces.JdkOptions["java.util.HashSet"].SequenceEqual(["java.util.Set"]) && ProgramInterfaces.JdkOptions["java.util.TreeSet"].SequenceEqual(["java.util.SortedSet"])
            && !ProgramInterfaces.JdkOptions.ContainsKey("java.util.IdentityHashMap") && ProgramInterfaces.JdkKeep.ContainsKey("java.util.IdentityHashMap"), "");

        // permutation and renaming of every identity: the same decisions
        var map = set.Select((u, i) => (u.Id, New: "Z" + (set.Count - i).ToString("D3"))).ToDictionary(x => x.Id, x => x.New);
        var optMap = new Dictionary<string, string>(StringComparer.Ordinal) { ["I01"] = "I77", ["I02"] = "I05" };
        string O(string o) => optMap.GetValueOrDefault(o, o);
        var renamed = set.AsEnumerable().Reverse().Select(u => new ProgramInterfaces.Unit
        {
            Id = map[u.Id], Kind = u.Kind, From = u.From == "T001" ? "T999" : u.From, Options = u.Options.Select(O).Reverse().ToList(), Keep = u.Keep,
            Uses = u.Uses.AsEnumerable().Reverse().Select(x => new ProgramInterfaces.Use { Site = "Q", Kind = x.Kind, Member = x.Member, Covers = x.Covers.Select(O).ToList(), Target = x.Target is { } t ? map[t] : null, Accepts = x.Accepts.Select(O).ToList(), Why = x.Why }).ToList(),
        }).ToList();
        var dr = ProgramInterfaces.Decide(renamed).ToDictionary(x => x.U.Id);
        var perm = set.Where(u => dr[map[u.Id]].Outcome != d[u.Id].Outcome || (dr[map[u.Id]].To ?? "") != (d[u.Id].To is { } t0 ? O(t0) : "")).Select(u => u.Id).ToList();
        yield return ("identity permutation and renaming", perm.Count == 0, perm.Count == 0 ? $"{set.Count} equal" : "differ: " + string.Join(", ", perm));
        // poisoned display, site and source ids quoting Java types, CLASS/FRAME/PEC: the same decisions
        var poisoned = Set().Select(u => new ProgramInterfaces.Unit
        {
            Id = u.Id, Kind = u.Kind, From = u.From, Options = u.Options, Keep = u.Keep,
            Display = "CLASS public final java.util.ArrayList FRAME OF M0001 public List<X> f(ArrayList a) legacy PEC:9",
            FromDisplay = "java.util.HashMap", SourceIds = ["FRAME List", "PEC:1"],
            Uses = u.Uses.Select(x => new ProgramInterfaces.Use { Site = "CLASS org.example.Shape implements Comparable PEC:2", Kind = x.Kind, Member = x.Member, Covers = x.Covers, Target = x.Target, Accepts = x.Accepts, Why = x.Why }).ToList(),
        }).ToList();
        var dp = ProgramInterfaces.Decide(poisoned).ToDictionary(x => x.U.Id);
        var pdiff = set.Where(u => dp[u.Id].Outcome != d[u.Id].Outcome || dp[u.Id].To != d[u.Id].To || dp[u.Id].Reason != d[u.Id].Reason).Select(u => u.Id).ToList();
        yield return ("poisoned display names, sites and source ids", pdiff.Count == 0, pdiff.Count == 0 ? $"{set.Count} equal" : "differ: " + string.Join(", ", pdiff));
        // the ledger lists every unit with its outcome, reason, uses and options
        using var ledger = JsonDocument.Parse(ProgramInterfaces.Ledger(ProgramInterfaces.Decide(set), new { }, [], []));
        var ids = ledger.RootElement.GetProperty("units").EnumerateArray().Select(x => x.GetProperty("id").GetString()).ToList();
        yield return ("ledger completeness", ids.Count == set.Count && ids.Distinct().Count() == set.Count && ledger.RootElement.GetProperty("summary").GetProperty("applied").GetInt32() == d.Values.Count(x => x.Applied),
            $"{ids.Count} units");
    }

    // ------------------------------------------------------------------ estates

    /// <summary>
    /// The estate check (the project's own files are only read): normal and poisoned R5 runs decide the same and print the same
    /// sources; the R5 sources differ from the rule-off run only in widened declaration types (and the imports of those types);
    /// only R5 ran; the ledger's applied units are the declarations that changed.
    /// </summary>
    static IEnumerable<(string, bool, string)> Estate(string project, string scratch)
    {
        var a = Path.Combine(scratch, "normal");
        var b = Path.Combine(scratch, "poisoned");
        var off = Path.Combine(scratch, "off");
        foreach (var dir in new[] { a, b, off }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var manifestBefore = File.ReadAllBytes(Path.Combine(project, "project.json"));
        var quiet = (string _) => { };
        Modernizer.Run(project, a, quiet, null, [ProgramInterfaces.RuleName]);
        Modernizer.Run(project, b, quiet, null, [ProgramInterfaces.RuleName], r5Poison: true);
        Modernizer.Run(project, off, quiet, null, []);
        static List<(string Id, string Outcome, string Reason, string? To, string Display, string From, string? ToType)> Load(string dir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "modernize", ProgramInterfaces.RuleName + ".json")));
            return doc.RootElement.GetProperty("units").EnumerateArray().Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("outcome").GetString()!,
                x.GetProperty("reason").GetString()!, x.TryGetProperty("to", out var t) ? t.GetString() : null, x.GetProperty("declaration").GetString()!,
                x.TryGetProperty("fromType", out var f) ? f.GetString() ?? "" : "", x.TryGetProperty("toType", out var tt) ? tt.GetString() : null)).ToList();
        }
        var da = Load(a);
        var db = Load(b).ToDictionary(x => x.Id);
        var diff = da.Where(x => !db.TryGetValue(x.Id, out var y) || x.Outcome != y.Outcome || x.Reason != y.Reason || x.To != y.To).Select(x => x.Id).ToList();
        if (db.Count != da.Count) diff.Add($"count {da.Count} vs {db.Count}");
        var name = Path.GetFileName(project);
        yield return ($"estate {name}: poisoned names, comments, signatures and parameter types, permuted inputs", diff.Count == 0,
            diff.Count == 0 ? $"{da.Count} decisions equal ({da.Count(x => x.Outcome == "applied")} widened, {da.Count(x => x.Outcome == "kept")} kept, {da.Count(x => x.Outcome == "skipped")} skipped)" : $"{diff.Count} differ: " + string.Join(", ", diff.Take(10)));
        bool namesPoisoned = da.Count == 0 || db.Values.All(y => !da.Any(x => x.Display == y.Display));
        yield return ($"estate {name}: the poisoned run saw no real name", namesPoisoned, namesPoisoned ? "every ledger declaration display differs" : "a real name reached the poisoned run");
        var srcA = Path.Combine(a, "src", "main", "java");
        var srcB = Path.Combine(b, "src", "main", "java");
        var srcO = Path.Combine(off, "src", "main", "java");
        var fa = Directory.GetFiles(srcA, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(srcA, f)).Order(StringComparer.Ordinal).ToList();
        var differ = fa.Where(f => !File.Exists(Path.Combine(srcB, f)) || File.ReadAllText(Path.Combine(srcA, f)) != File.ReadAllText(Path.Combine(srcB, f))).ToList();
        yield return ($"estate {name}: identical sources from the poisoned run", differ.Count == 0, differ.Count == 0 ? $"{fa.Count} files" : string.Join(", ", differ.Take(5)));
        // against the rule-off run: the same files; a differing line is a declaration whose type name became the interface's
        static string Outer(string t) { var x = t.IndexOf('<') is var k && k > 0 ? t[..k] : t; return x[(x.LastIndexOf('.') + 1)..]; }
        var pairs = da.Where(x => x.Outcome == "applied" && x.ToType is not null).Select(x => (Old: Outer(x.From), New: Outer(x.ToType!), OldFull: x.From, NewFull: x.ToType!)).Distinct().ToList();
        var fo = Directory.GetFiles(srcO, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(srcO, f)).Order(StringComparer.Ordinal).ToList();
        var bad = new List<string>();
        if (!fo.SequenceEqual(fa)) bad.Add("file lists differ");
        int changedLines = 0, changedFiles = 0;
        foreach (var f in fa.Intersect(fo))
        {
            var la = File.ReadAllLines(Path.Combine(srcA, f));
            var lo = File.ReadAllLines(Path.Combine(srcO, f));
            if (la.SequenceEqual(lo)) continue;
            changedFiles++;
            // imports: only the interfaces' added, or the classes' that no declaration names any more removed
            var ia = la.Where(l => l.StartsWith("import ", StringComparison.Ordinal)).ToHashSet();
            var io = lo.Where(l => l.StartsWith("import ", StringComparison.Ordinal)).ToHashSet();
            foreach (var x in ia.Except(io)) if (!pairs.Any(p => x == "import " + p.NewFull + ";")) bad.Add($"{f}: import added {x}");
            foreach (var x in io.Except(ia)) if (!pairs.Any(p => x == "import " + p.OldFull + ";")) bad.Add($"{f}: import removed {x}");
            var ba = la.Where(l => !l.StartsWith("import ", StringComparison.Ordinal)).ToList();
            var bo = lo.Where(l => !l.StartsWith("import ", StringComparison.Ordinal)).ToList();
            // an import block that appears or disappears changes one blank line after the package line
            if (ba.Count != bo.Count)
            {
                ba = ba.Where((l, i) => !(l.Length == 0 && i > 0 && ba[i - 1].StartsWith("package ", StringComparison.Ordinal))).ToList();
                bo = bo.Where((l, i) => !(l.Length == 0 && i > 0 && bo[i - 1].StartsWith("package ", StringComparison.Ordinal))).ToList();
            }
            if (ba.Count != bo.Count) { bad.Add($"{f}: {bo.Count} -> {ba.Count} lines"); continue; }
            for (int i = 0; i < ba.Count; i++)
            {
                if (ba[i] == bo[i]) continue;
                changedLines++;
                // the new line is the old with one or more declared type names replaced by an applied interface's
                var cand = bo[i];
                foreach (var p in pairs) cand = Regex.Replace(cand, @"(?<![\w.])" + Regex.Escape(p.Old) + @"(?=(<[^=;()]*>)?\s+\w+\s*([;=,)(:]|$))", p.New);
                if (cand != ba[i]) bad.Add($"{f}:{i + 1}: {bo[i].Trim()} -> {ba[i].Trim()}");
            }
        }
        yield return ($"estate {name}: only declaration types (and their imports) differ from the rule-off run", bad.Count == 0,
            bad.Count == 0 ? $"{changedLines} declaration lines in {changedFiles} files, {da.Count(x => x.Outcome == "applied")} widened" : string.Join("; ", bad.Take(6)));
        bool none = da.Any(x => x.Outcome == "applied") || changedLines == 0;
        yield return ($"estate {name}: nothing widened means nothing changed", none, $"{changedLines} lines");
        var metaA = Path.Combine(a, "modernize");
        foreach (var f in Directory.GetFiles(Path.Combine(off, "modernize")).Select(Path.GetFileName))
        {
            bool same = File.Exists(Path.Combine(metaA, f!)) && File.ReadAllBytes(Path.Combine(metaA, f!)).SequenceEqual(File.ReadAllBytes(Path.Combine(off, "modernize", f!)));
            // the source map moves with the header (imports): declonly.py checks the shift
            if (!same && f != "sourcemap.tsv") bad.Add("modernize/" + f);
        }
        yield return ($"estate {name}: only R5 ran (no api-visibility or final-classes ledger), other artifacts equal",
            !File.Exists(Path.Combine(metaA, ApiVisibility.RuleName + ".json")) && !File.Exists(Path.Combine(metaA, FinalClasses.RuleName + ".json"))
            && !bad.Any(x => x.StartsWith("modernize/", StringComparison.Ordinal)),
            string.Join(", ", bad.Where(x => x.StartsWith("modernize/", StringComparison.Ordinal))));
        yield return ($"estate {name}: the project's manifest is unchanged", manifestBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(project, "project.json"))), "project.json bytes equal");
    }
}
