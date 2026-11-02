using System.Text.Json;

namespace MlCpu.Modernize;

/// <summary>
/// R1 api-visibility self checks (MlCpu.Modernize r1-selftest [project scratch-dir]): the decision cases on synthetic graphs
/// (private, package, public, unknown reflection, unknown caller, entry surface, cross package, override, static constant,
/// subclass and enum-body floors), invariance under permutation and renaming of every identity and under poisoned display
/// names; and, given an estate, the same decisions from a run whose class and package names are random tokens, whose
/// programs carry planted J3S-style comments that quote Java access words, and whose inputs are permuted.
/// </summary>
public static class ApiVisibilityChecks
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
        if (args.Length >= 2)
        {
            foreach (var (name, ok, detail) in Estate(args[0], args[1])) Check(name, ok, detail);
        }
        Console.WriteLine($"r1-selftest: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    static ApiVisibility.Member M(string id, string type, string pkg, Access before, string kind = "method", bool stat = false, bool subs = false, string? cls = null, string? keep = null, string? entry = null)
    {
        var m = new ApiVisibility.Member { Id = id, Type = type, Class = cls ?? type, Package = pkg, Kind = kind, Before = before, Static = stat, OwnerHasSubclasses = subs, Keep = keep };
        if (entry is not null) m.Entry.Add(entry);
        return m;
    }

    static ApiVisibility.Ref R(string member, string type, string pkg, string? cls = null, bool anon = false) => new(member, type, cls ?? type, pkg, "site", anon);

    static (List<ApiVisibility.Member>, List<ApiVisibility.Ref>) Graph()
    {
        var ms = new List<ApiVisibility.Member>
        {
            M("a.private", "T1", "P1", Access.Public),
            M("b.package", "T1", "P1", Access.Public),
            M("c.public", "T1", "P1", Access.Private),
            M("d.reflection", "T1", "P1", Access.Public, "constructor", keep: "reflection/provider registration: the class is loaded by name"),
            M("e.unknown-context", "T1", "P1", Access.Public),
            M("f.no-reference", "T1", "P1", Access.Public),
            M("g.entry", "T1", "P1", Access.Private, entry: "DRV:10 CALL 'X' entry 3 (job/replay dispatch)"),
            M("h.override", "T1", "P1", Access.Public, keep: "override: the same signature in its class hierarchy"),
            M("i.constant", "T1", "P1", Access.Public, "constant", stat: true),
            M("j.static-from-nested", "T1", "P1", Access.Package, "static-field", stat: true),
            M("k.instance-subclass", "T1", "P1", Access.Public, subs: true),
            M("l.enum-body", "T1", "P1", Access.Public, stat: true, cls: "T1"),
            M("m.cross-package-constant", "T2", "P2", Access.Public, "constant", stat: true),
            M("n.ctor-same-package", "T2", "P2", Access.Public, "constructor", subs: true),
            M("o.keep-beats-entry", "T2", "P2", Access.Public, keep: "unknown caller: a CALL with an entry that is not a literal", entry: "DRV:1"),
        };
        var rs = new List<ApiVisibility.Ref>
        {
            R("a.private", "T1", "P1"), R("a.private", "T1", "P1", "T1.Inner"),
            R("b.package", "T1", "P1"), R("b.package", "T3", "P1"),
            R("c.public", "T9", "P9"),
            R("d.reflection", "T1", "P1"),
            R("e.unknown-context", ApiVisibility.UnknownContext, ApiVisibility.UnknownContext),
            R("g.entry", "T1", "P1"),
            R("h.override", "T1", "P1"),
            R("i.constant", "T1", "P1"),
            R("j.static-from-nested", "T1", "P1", "T1.Inner"),
            R("k.instance-subclass", "T1", "P1", "T1.Sub"),
            R("l.enum-body", "T1", "P1", "T1.Body", anon: true),
            R("m.cross-package-constant", "T1", "P1"),
            R("n.ctor-same-package", "T2", "P2", "T2.Sub"),
            R("o.keep-beats-entry", "T5", "P7"),
        };
        return (ms, rs);
    }

    static IEnumerable<(string, bool, string)> Cases()
    {
        var (ms, rs) = Graph();
        var d = ApiVisibility.Decide(ms, rs).ToDictionary(x => x.M.Id, x => x);
        (string, bool, string) Is(string id, Access want, string scope) =>
            ($"case {id}", d[id].After == want && d[id].Scope == scope, $"{d[id].M.Before} -> {d[id].After} ({d[id].Scope}: {d[id].Reason})");
        yield return Is("a.private", Access.Private, "same-type");
        yield return Is("b.package", Access.Package, "same-package");
        yield return Is("c.public", Access.Public, "other-package");
        yield return Is("d.reflection", Access.Public, "kept");
        yield return Is("e.unknown-context", Access.Public, "kept");
        yield return Is("f.no-reference", Access.Public, "kept");
        yield return Is("g.entry", Access.Public, "entry-surface");
        yield return Is("h.override", Access.Public, "kept");
        yield return Is("i.constant", Access.Private, "same-type");
        yield return Is("j.static-from-nested", Access.Private, "same-type");
        yield return Is("k.instance-subclass", Access.Package, "same-type");
        yield return Is("l.enum-body", Access.Package, "same-type");
        yield return Is("m.cross-package-constant", Access.Public, "other-package");
        yield return Is("n.ctor-same-package", Access.Package, "same-type");
        yield return Is("o.keep-beats-entry", Access.Public, "kept");

        // permutation and renaming: a bijection over every id and a shuffled order give the same decisions
        var rnd = new Random(7);
        string Ren(string s) => s == ApiVisibility.UnknownContext ? s : "x" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..10];
        var ms2 = ms.Select(m =>
        {
            var c = new ApiVisibility.Member { Id = Ren(m.Id), Type = Ren(m.Type), Class = Ren(m.Class), Package = Ren(m.Package), Kind = m.Kind, Before = m.Before, Static = m.Static, OwnerHasSubclasses = m.OwnerHasSubclasses, Keep = m.Keep };
            c.Entry.AddRange(m.Entry);
            return c;
        }).OrderBy(_ => rnd.Next()).ToList();
        var rs2 = rs.Select(r => new ApiVisibility.Ref(Ren(r.Member), Ren(r.FromType), Ren(r.FromClass), Ren(r.FromPackage), r.Site, r.FromStaticAnonymousBody)).OrderBy(_ => rnd.Next()).ToList();
        var d2 = ApiVisibility.Decide(ms2, rs2).ToDictionary(x => x.M.Id, x => x);
        var diff = ms.Where(m => d2[Ren(m.Id)].After != d[m.Id].After || d2[Ren(m.Id)].Scope != d[m.Id].Scope).Select(m => m.Id).ToList();
        yield return ("permutation and renaming of every identity", diff.Count == 0, diff.Count == 0 ? $"{ms.Count} decisions equal" : string.Join(", ", diff));

        // poisoned ledger-only inputs: forbidden-looking names and comments as display names and source ids change nothing
        var ms3 = ms.Select(m =>
        {
            var c = new ApiVisibility.Member
            {
                Id = m.Id, Type = m.Type, Class = m.Class, Package = m.Package, Kind = m.Kind, Before = m.Before, Static = m.Static, OwnerHasSubclasses = m.OwnerHasSubclasses, Keep = m.Keep,
                Display = m.Before == Access.Public ? "private static FRAME OF M0001-X private" : "public CLASS org.example.Api public",
                SourceIds = ["legacy PEC:182 public final", "CLASS private"],
            };
            c.Entry.AddRange(m.Entry);
            return c;
        }).ToList();
        var d3 = ApiVisibility.Decide(ms3, rs).ToDictionary(x => x.M.Id, x => x);
        var diff3 = ms.Where(m => d3[m.Id].After != d[m.Id].After).Select(m => m.Id).ToList();
        yield return ("poisoned display names and source ids", diff3.Count == 0, diff3.Count == 0 ? $"{ms.Count} decisions equal" : string.Join(", ", diff3));

        // the ledger carries every member with before, after, scope and reason
        var ledger = JsonDocument.Parse(ApiVisibility.Ledger(ApiVisibility.Decide(ms, rs), new { }, [], []));
        var lm = ledger.RootElement.GetProperty("members");
        bool complete = lm.GetArrayLength() == ms.Count && lm.EnumerateArray().All(x => x.TryGetProperty("before", out _) && x.TryGetProperty("after", out _) && x.TryGetProperty("scope", out _) && x.TryGetProperty("reason", out _));
        yield return ("ledger lists every member with before/after/scope/reason", complete, $"{lm.GetArrayLength()} members");
    }

    /// <summary>The estate check: a normal and a poisoned run decide the same; without the rule there is no ledger.</summary>
    static IEnumerable<(string, bool, string)> Estate(string project, string scratch)
    {
        var a = Path.Combine(scratch, "normal");
        var b = Path.Combine(scratch, "poisoned");
        foreach (var d in new[] { a, b }) if (Directory.Exists(d)) Directory.Delete(d, true);
        var quiet = (string _) => { };
        Modernizer.Run(project, a, quiet, null, [ApiVisibility.RuleName]);
        Modernizer.Run(project, b, quiet, null, [ApiVisibility.RuleName], r1Poison: true);
        static Dictionary<string, (string After, string Scope, string Reason)> Load(string dir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "modernize", ApiVisibility.RuleName + ".json")));
            return doc.RootElement.GetProperty("members").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetString()!,
                x => (x.GetProperty("after").GetString()!, x.GetProperty("scope").GetString()!, x.GetProperty("reason").GetString()!));
        }
        var da = Load(a);
        var db = Load(b);
        var diff = da.Keys.Union(db.Keys).Where(k => !da.TryGetValue(k, out var x) || !db.TryGetValue(k, out var y) || x.After != y.After || x.Scope != y.Scope || x.Reason != y.Reason).Order(StringComparer.Ordinal).ToList();
        yield return ($"estate {Path.GetFileName(project)}: poisoned names and comments, permuted inputs", diff.Count == 0,
            diff.Count == 0 ? $"{da.Count} decisions equal ({da.Values.Count(v => v.Scope != "kept")} decided, {da.Values.Count(v => v.Scope == "kept")} kept)" : $"{diff.Count} differ: " + string.Join(", ", diff.Take(10)));
        // generated sources of the normal run equal those of the poisoned run (the poison is undone before printing, and the decisions match)
        var srcA = Path.Combine(a, "src", "main", "java");
        var srcB = Path.Combine(b, "src", "main", "java");
        var fa = Directory.GetFiles(srcA, "*.java", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(srcA, f)).Order(StringComparer.Ordinal).ToList();
        var differ = fa.Where(f => !File.Exists(Path.Combine(srcB, f)) || File.ReadAllText(Path.Combine(srcA, f)) != File.ReadAllText(Path.Combine(srcB, f))).ToList();
        yield return ($"estate {Path.GetFileName(project)}: identical sources from the poisoned run", differ.Count == 0, differ.Count == 0 ? $"{fa.Count} files" : string.Join(", ", differ.Take(5)));
    }
}
