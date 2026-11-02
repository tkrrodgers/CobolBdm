using System.Text.Json;

namespace MlCpu.Modernize;

/// <summary>
/// R3 final-classes self checks (MlCpu.Modernize r3-selftest [project scratch-dir]...): the decision cases on synthetic
/// candidates (closed leaf and nested leaf: final; known subclass, abstract, interface, record, enum, enum constant body,
/// reflection/provider, proxy, model edits, unknown hierarchy: not newly final), invariance under permutation and renaming
/// of every identity and under poisoned display names, source ids and proofs; and, per estate, the same decisions and
/// sources from a run whose class, package, method and field names are random tokens, whose programs carry planted
/// J3S-style comments quoting Java modifiers, whose FRAME signatures are poisoned and whose inputs are permuted.
/// </summary>
public static class FinalClassesChecks
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
        var profileDir = Path.Combine(args.Length >= 2 ? args[1] : Environment.CurrentDirectory, "r3-profile-cases");
        foreach (var (name, ok, detail) in ProfileCases(profileDir)) Check(name, ok, detail);
        if (Directory.Exists(profileDir)) Directory.Delete(profileDir, true);
        for (int i = 0; i + 1 < args.Length; i += 2)
            foreach (var (name, ok, detail) in Estate(args[i], args[i + 1])) Check(name, ok, detail);
        Console.WriteLine($"r3-selftest: {passed} passed, {failed} failed");
        return failed == 0 ? 0 : 1;
    }

    /// <summary>
    /// A fixture project for an estate: its project.json (the original file is only read) with the given deployment
    /// profile section (null: none) and the source directory made absolute, in <paramref name="dir"/>.
    /// </summary>
    public static string Fixture(string project, string dir, string? section)
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(project, "project.json")))!.AsObject();
        var layout = root["layout"]?.AsObject();
        var src = layout?["sourceDir"]?.GetValue<string>() ?? "source";
        if (layout is null) root["layout"] = layout = [];
        layout["sourceDir"] = Path.GetFullPath(Path.Combine(project, src));
        if (section is not null) root[DeploymentProfile.Key] = System.Text.Json.Nodes.JsonNode.Parse(section);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "project.json"), root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return dir;
    }

    /// <summary>The profile's input surface: only an object with one boolean closedWorld; R3 only with closedWorld: true.</summary>
    static IEnumerable<(string, bool, string)> ProfileCases(string dir)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        string P(string name, string json)
        {
            var d = Path.Combine(dir, name);
            Directory.CreateDirectory(d);
            File.WriteAllText(Path.Combine(d, "project.json"), json);
            return d;
        }
        const string Head = "{ \"schema\": \"mlcpu.project.v1\", \"id\": \"fixture\", \"programs\": []";
        string With(string section) => Head + ", \"deploymentProfile\": " + section + " }";
        (bool Ok, string Detail) Try(Func<object?> f)
        {
            try { var r = f(); return (true, r?.ToString() ?? "null"); }
            catch (Exception e) when (e is ArgumentException or InvalidDataException) { return (false, e.GetType().Name + ": " + e.Message); }
        }
        string[] r3 = [FinalClasses.RuleName];
        string[] r1 = [ApiVisibility.RuleName];
        var none = P("absent", Head + " }");
        var noManifest = Path.Combine(dir, "no-manifest");
        Directory.CreateDirectory(noManifest);
        var off = P("false", With("{ \"closedWorld\": false }"));
        var on = P("true", With("{ \"closedWorld\": true }"));
        yield return Try(() => DeploymentProfile.Load(none)) is (true, "null") ? ("profile absent: none", true, "null") : ("profile absent: none", false, "not null");
        yield return Try(() => DeploymentProfile.Load(noManifest)) is (true, "null") ? ("no manifest (a source tree): none", true, "null") : ("no manifest (a source tree): none", false, "not null");
        var lf = Try(() => DeploymentProfile.Load(off)?.ClosedWorld);
        yield return ("profile closedWorld false: loaded, false", lf is (true, "False"), lf.Detail);
        var lt = DeploymentProfile.Load(on);
        yield return ("profile closedWorld true: loaded, true, hashed", lt is { ClosedWorld: true } && lt.SectionSha256.Length == 64 && lt.ManifestSha256.Length == 64 && lt.Estate == "fixture", lt?.ToString() ?? "null");
        foreach (var (name, section) in new[]
        {
            ("string \"true\"", "{ \"closedWorld\": \"true\" }"), ("number 1", "{ \"closedWorld\": 1 }"), ("null", "{ \"closedWorld\": null }"),
            ("missing field", "{ }"), ("unknown field", "{ \"closedWorld\": true, \"openWorld\": false }"), ("array", "[ true ]"), ("bare true", "true"),
        })
        {
            var t = Try(() => DeploymentProfile.Load(P("bad-" + name.Replace(' ', '-').Replace("\"", ""), With(section))));
            yield return ($"profile {name}: fails", !t.Ok && t.Detail.StartsWith("InvalidDataException"), t.Detail);
        }
        var malformed = P("malformed-json", Head + ", \"deploymentProfile\": { \"closedWorld\": tru }");
        var tm = Try(() => DeploymentProfile.Load(malformed));
        yield return ("malformed manifest JSON: fails", !tm.Ok, tm.Detail);
        var a1 = Try(() => Modernizer.Authorize(none, r3));
        yield return ("--rule final-classes without a profile: fails", !a1.Ok && a1.Detail.Contains("not authorized") && a1.Detail.Contains("no deployment profile"), a1.Detail);
        var a1b = Try(() => Modernizer.Authorize(noManifest, r3));
        yield return ("--rule final-classes on a source tree (no manifest): fails", !a1b.Ok && a1b.Detail.Contains("not authorized"), a1b.Detail);
        var a2 = Try(() => Modernizer.Authorize(off, r3));
        yield return ("--rule final-classes with closedWorld false: fails", !a2.Ok && a2.Detail.Contains("closedWorld: false"), a2.Detail);
        var a3 = Try(() => Modernizer.Authorize(on, r3));
        yield return ("--rule final-classes with closedWorld true: authorized", a3.Ok, a3.Detail);
        var a4 = Try(() => Modernizer.Authorize(P("bad-for-any-lift", With("{ \"closedWorld\": \"yes\" }")), []));
        yield return ("a malformed profile fails every lift (no rule requested)", !a4.Ok, a4.Detail);
        var a5 = Try(() => Modernizer.Authorize(on, []));
        yield return ("closedWorld true without --rule: no rule runs (default off)", a5.Ok, a5.Detail);
        var a6 = Try(() => Modernizer.Authorize(off, r1));
        yield return ("closedWorld false does not affect other opt-in rules", a6.Ok, a6.Detail);
        var a7 = Try(() => Modernizer.Authorize(on, ["final-class"]));
        yield return ("an unknown rule fails", !a7.Ok && a7.Detail.Contains("unknown rule"), a7.Detail);
    }

    static FinalClasses.Candidate C(string id, FinalClasses.Form form = FinalClasses.Form.Class, bool nested = false, string[]? subs = null, string[]? unknown = null, string[]? keep = null) => new()
    {
        Id = id, Form = form, Nested = nested, Subclasses = [.. subs ?? []], HierarchyUnknown = [.. unknown ?? []], Obligations = [.. keep ?? []],
    };

    static List<FinalClasses.Candidate> Set() =>
    [
        C("a.closed-leaf"),
        C("b.nested-leaf", nested: true),
        C("c.known-subclass", subs: ["T9"]),
        C("d.abstract", FinalClasses.Form.Abstract, subs: ["T8"]),
        C("e.interface", FinalClasses.Form.Interface),
        C("f.record", FinalClasses.Form.Record),
        C("g.enum", FinalClasses.Form.Enum),
        C("h.enum-body-anonymous", FinalClasses.Form.EnumBody),
        C("i.reflective-provider", keep: ["reflection/provider registration: the class is loaded by name"]),
        C("j.proxy", keep: ["reflection or proxy: the code looks classes up or proxies them at run time (1 sites, targets unknown)"]),
        C("k.model-edits", keep: ["model edits: their Java text may extend any class, outside the evidenced hierarchy"]),
        C("l.unknown-hierarchy-pool", unknown: ["heap pool P01 has 1 class ids no class is bound to"]),
        C("m.unknown-hierarchy-unallocated", unknown: ["instance members but no class id is allocated as this class"]),
        C("n.subclass-and-keep", subs: ["T7"], keep: ["reflection/provider registration: the class is loaded by name"]),
        C("o.nested-subclass", nested: true, subs: ["T6"]),
        C("p.nested-unknown", nested: true, unknown: ["heap pool P02 holds 2 unlinked class hierarchies"]),
    ];

    static IEnumerable<(string, bool, string)> Cases()
    {
        var cs = Set();
        var d = FinalClasses.Decide(cs).ToDictionary(x => x.C.Id, x => x);
        (string, bool, string) Is(string id, string outcome, string reasonClass) =>
            ($"case {id}", d[id].Outcome == outcome && FinalClasses.ReasonClass(d[id].Reason) == reasonClass, $"{d[id].Outcome} ({d[id].Reason})");
        yield return Is("a.closed-leaf", "applied", "final");
        yield return Is("b.nested-leaf", "applied", "final");
        yield return Is("c.known-subclass", "kept", "known subclass");
        yield return Is("d.abstract", "skipped", "abstract");
        yield return Is("e.interface", "skipped", "an interface");
        yield return Is("f.record", "skipped", "implicitly final");
        yield return Is("g.enum", "skipped", "implicitly final");
        yield return Is("h.enum-body-anonymous", "skipped", "an enum constant body");
        yield return Is("i.reflective-provider", "kept", "keep obligation");
        yield return Is("j.proxy", "kept", "keep obligation");
        yield return Is("k.model-edits", "kept", "keep obligation");
        yield return Is("l.unknown-hierarchy-pool", "kept", "unknown hierarchy");
        yield return Is("m.unknown-hierarchy-unallocated", "kept", "unknown hierarchy");
        yield return Is("n.subclass-and-keep", "kept", "known subclass");
        yield return Is("o.nested-subclass", "kept", "known subclass");
        yield return Is("p.nested-unknown", "kept", "unknown hierarchy");
        int finals = d.Values.Count(x => x.Final);
        yield return ("only closed leaf classes are final", finals == 2 && d["a.closed-leaf"].Final && d["b.nested-leaf"].Final, $"{finals} final");

        // permutation and renaming: a bijection over every id (subclass ids included) and a shuffled order give the same decisions
        var rnd = new Random(23);
        string Ren(string s) => "x" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(s)))[..10];
        var cs2 = cs.Select(c => new FinalClasses.Candidate
        {
            Id = Ren(c.Id), Form = c.Form, Nested = c.Nested, Subclasses = c.Subclasses.Select(Ren).ToList(), HierarchyUnknown = [.. c.HierarchyUnknown], Obligations = [.. c.Obligations],
        }).OrderBy(_ => rnd.Next()).ToList();
        var d2 = FinalClasses.Decide(cs2).ToDictionary(x => x.C.Id, x => x);
        var diff = cs.Where(c => d2[Ren(c.Id)].Outcome != d[c.Id].Outcome || FinalClasses.ReasonClass(d2[Ren(c.Id)].Reason) != FinalClasses.ReasonClass(d[c.Id].Reason)).Select(c => c.Id).ToList();
        yield return ("permutation and renaming of every identity", diff.Count == 0, diff.Count == 0 ? $"{cs.Count} decisions equal" : string.Join(", ", diff));

        // poisoned ledger-only inputs: names, comments and quoted modifiers as display, source ids and proof change nothing
        var cs3 = cs.Select(c => new FinalClasses.Candidate
        {
            Id = c.Id, Form = c.Form, Nested = c.Nested, Subclasses = [.. c.Subclasses], HierarchyUnknown = [.. c.HierarchyUnknown], Obligations = [.. c.Obligations],
            Display = c.Form == FinalClasses.Form.Class ? "public abstract class org.example.Base extends Object" : "public final class org.example.Leaf",
            SourceIds = ["CLASS public final org.example.Money", "FRAME OF M0001-X public static", "legacy PEC:182 final", "@FunctionalInterface sealed permits"],
            SourceProof = ["final", "non-sealed", "extends", "abstract"],
        }).ToList();
        var d3 = FinalClasses.Decide(cs3).ToDictionary(x => x.C.Id, x => x);
        var diff3 = cs.Where(c => d3[c.Id].Outcome != d[c.Id].Outcome || d3[c.Id].Reason != d[c.Id].Reason).Select(c => c.Id).ToList();
        yield return ("poisoned display names, source ids and proofs", diff3.Count == 0, diff3.Count == 0 ? $"{cs.Count} decisions equal" : string.Join(", ", diff3));

        // the ledger carries every class with form, outcome, reason and proof
        var ledger = JsonDocument.Parse(FinalClasses.Ledger(FinalClasses.Decide(cs), new { }, [], []));
        var lc = ledger.RootElement.GetProperty("classes");
        bool complete = lc.GetArrayLength() == cs.Count && lc.EnumerateArray().All(x => x.TryGetProperty("form", out _) && x.TryGetProperty("outcome", out _) && x.TryGetProperty("reason", out _) && x.TryGetProperty("sourceProof", out _));
        yield return ("ledger lists every class with form/outcome/reason/proof", complete, $"{lc.GetArrayLength()} classes");
    }

    /// <summary>
    /// The estate check, on a fixture copy of the estate's manifest declaring closedWorld: true (the project's own files are
    /// only read): a normal and a poisoned run decide the same and print the same sources; finals are printed; the ledger
    /// and deployment-profile.json record the profile; without --rule the fixture generates exactly what the project does.
    /// </summary>
    static IEnumerable<(string, bool, string)> Estate(string project, string scratch)
    {
        var a = Path.Combine(scratch, "normal");
        var b = Path.Combine(scratch, "poisoned");
        var offFixture = Path.Combine(scratch, "off-fixture");
        var offProject = Path.Combine(scratch, "off-project");
        foreach (var dir in new[] { a, b, offFixture, offProject }) if (Directory.Exists(dir)) Directory.Delete(dir, true);
        var manifestBefore = File.ReadAllBytes(Path.Combine(project, "project.json"));
        var fixture = Fixture(project, Path.Combine(scratch, "fixture"), "{ \"closedWorld\": true }");
        var quiet = (string _) => { };
        Modernizer.Run(fixture, a, quiet, null, [FinalClasses.RuleName]);
        Modernizer.Run(fixture, b, quiet, null, [FinalClasses.RuleName], r3Poison: true);
        Modernizer.Run(fixture, offFixture, quiet, null, []);
        Modernizer.Run(project, offProject, quiet, null, []);
        static List<(string Id, string Outcome, string Reason, string Form, string Display)> Load(string dir)
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir, "modernize", FinalClasses.RuleName + ".json")));
            return doc.RootElement.GetProperty("classes").EnumerateArray().Select(x => (x.GetProperty("id").GetString()!, x.GetProperty("outcome").GetString()!,
                x.GetProperty("reason").GetString()!, x.GetProperty("form").GetString()!, x.GetProperty("class").GetString()!)).ToList();
        }
        var da = Load(a);
        var db = Load(b).ToDictionary(x => x.Id);
        var diff = da.Where(x => !db.TryGetValue(x.Id, out var y) || x.Outcome != y.Outcome || x.Reason != y.Reason || x.Form != y.Form).Select(x => x.Id).ToList();
        if (db.Count != da.Count) diff.Add($"count {da.Count} vs {db.Count}");
        var name = Path.GetFileName(project);
        yield return ($"estate {name}: poisoned names, comments and signatures, permuted inputs", diff.Count == 0,
            diff.Count == 0 ? $"{da.Count} decisions equal ({da.Count(x => x.Outcome == "applied")} final, {da.Count(x => x.Outcome == "kept")} kept, {da.Count(x => x.Outcome == "skipped")} skipped)" : $"{diff.Count} differ: " + string.Join(", ", diff.Take(10)));
        bool namesPoisoned = da.Count == 0 || db.Values.All(y => !da.Any(x => x.Display == y.Display));
        yield return ($"estate {name}: the poisoned run saw no real class name", namesPoisoned, namesPoisoned ? "every ledger display differs" : "a real name reached the poisoned run");
        var srcA = Path.Combine(a, "src", "main", "java");
        var srcB = Path.Combine(b, "src", "main", "java");
        var fa = Directory.GetFiles(srcA, "*.java", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(srcA, f)).Order(StringComparer.Ordinal).ToList();
        var differ = fa.Where(f => !File.Exists(Path.Combine(srcB, f)) || File.ReadAllText(Path.Combine(srcA, f)) != File.ReadAllText(Path.Combine(srcB, f))).ToList();
        yield return ($"estate {name}: identical sources from the poisoned run", differ.Count == 0, differ.Count == 0 ? $"{fa.Count} files" : string.Join(", ", differ.Take(5)));
        // every applied class is printed final, no other estate class is
        var printed = new List<string>();
        foreach (var x in da)
        {
            var top = x.Display;
            var simple = top[(top.LastIndexOf('.') + 1)..];
            string? file = null;
            for (var t = top; file is null && t.Contains('.'); t = t[..t.LastIndexOf('.')])
                if (File.Exists(Path.Combine(srcA, t.Replace('.', Path.DirectorySeparatorChar) + ".java"))) file = Path.Combine(srcA, t.Replace('.', Path.DirectorySeparatorChar) + ".java");
            bool isFinal = file is not null && File.ReadAllLines(file).Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\s*public (static )?final class " + System.Text.RegularExpressions.Regex.Escape(simple) + @"\b"));
            if (isFinal != (x.Outcome == "applied")) printed.Add($"{x.Id} {x.Outcome} printed {(isFinal ? "final" : "not final")}");
        }
        yield return ($"estate {name}: exactly the applied classes are printed final", printed.Count == 0, printed.Count == 0 ? $"{da.Count(x => x.Outcome == "applied")} final" : string.Join("; ", printed.Take(5)));
        // the authorization recorded: the ledger and modernize/deployment-profile.json name the profile, its hashes and value
        using (var ledger = JsonDocument.Parse(File.ReadAllText(Path.Combine(a, "modernize", FinalClasses.RuleName + ".json"))))
        using (var rec = JsonDocument.Parse(File.ReadAllText(Path.Combine(a, "modernize", "deployment-profile.json"))))
        {
            var au = ledger.RootElement.GetProperty("authorization");
            bool ok = au.GetProperty("closedWorld").GetBoolean() && rec.RootElement.GetProperty("closedWorld").GetBoolean()
                && au.GetProperty("sectionSha256").GetString() == rec.RootElement.GetProperty("sectionSha256").GetString()
                && au.GetProperty("manifestSha256").GetString()!.Length == 64 && au.GetProperty("scopeClaim").GetString()!.StartsWith("closedWorld")
                && rec.RootElement.GetProperty("appliedRules").EnumerateArray().Select(x => x.GetString()).SequenceEqual([FinalClasses.RuleName]);
            yield return ($"estate {name}: the ledger and deployment-profile.json record the closedWorld profile", ok, au.GetProperty("sectionSha256").GetString()!);
        }
        // closedWorld true without --rule: R3 off (no ledger, no final), only the profile record; the same sources as the project
        var srcF = Path.Combine(offFixture, "src", "main", "java");
        var srcP = Path.Combine(offProject, "src", "main", "java");
        var ff = Directory.GetFiles(srcP, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(srcP, f)).Order(StringComparer.Ordinal).ToList();
        var offDiff = ff.Where(f => !File.Exists(Path.Combine(srcF, f)) || !File.ReadAllBytes(Path.Combine(srcP, f)).SequenceEqual(File.ReadAllBytes(Path.Combine(srcF, f)))).ToList();
        offDiff.AddRange(Directory.GetFiles(srcF, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(srcF, f)).Where(f => !ff.Contains(f)));
        bool offOk = offDiff.Count == 0 && !File.Exists(Path.Combine(offFixture, "modernize", FinalClasses.RuleName + ".json"))
            && !File.Exists(Path.Combine(offFixture, "modernize", ApiVisibility.RuleName + ".json")) && !File.Exists(Path.Combine(offProject, "modernize", "deployment-profile.json"));
        string recOff;
        using (var r0 = JsonDocument.Parse(File.ReadAllText(Path.Combine(offFixture, "modernize", "deployment-profile.json"))))
            recOff = r0.RootElement.GetProperty("appliedRules").GetArrayLength() == 0 ? "" : "rules applied without --rule";
        yield return ($"estate {name}: closedWorld true without --rule generates the project's bytes (R3 off)", offOk && recOff.Length == 0, offOk ? $"{ff.Count} files equal" : string.Join(", ", offDiff.Take(5)) + recOff);
        // the R3 run enabled only R3 (no R1 ledger, no visibility change: sources differ from R3 off only by final)
        yield return ($"estate {name}: only R3 ran", !File.Exists(Path.Combine(a, "modernize", ApiVisibility.RuleName + ".json")), "no api-visibility ledger");
        yield return ($"estate {name}: the project's manifest is unchanged", manifestBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(project, "project.json"))), "project.json bytes equal");
    }
}
