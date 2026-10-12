using System.Text;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;
using MlCpu.Kit;

namespace MlCpu.CobolKb.Knowledge.Typed;

/// <param name="Package">The capability's package; the record kit is Package.records.</param>
/// <param name="LayoutsJson">The capability's record-layouts.json (the record kit's layouts, with class and accessor names).</param>
/// <param name="KitSources">The record kit's Java sources by class name (for the accessors' Java types).</param>
/// <param name="Callees">Per CALLed program with verified groups: its equivalences.json and record-layouts.json.</param>
/// <param name="Embeddable">Programs whose own typed reference passed their acceptance test (by PID): a CALL of one without
/// verified operations runs that reference, generated again over this capability's record kit (bottom-up composition).</param>
public sealed record TypedRequest(string ProgramId, string Package, string EntryClass, CobolProgram Program, string LayoutsJson,
    IReadOnlyDictionary<string, string> KitSources, IReadOnlyDictionary<string, (string EquivalencesJson, string LayoutsJson)> Callees,
    IReadOnlyDictionary<string, CobolProgram>? Embeddable = null)
{
    /// <summary>Layouts given directly (an embedded callee's: its LINKAGE named as it names it, over the caller's kit classes).</summary>
    public KitLayouts? Layouts { get; init; }
    public int Depth { get; init; }
    /// <summary>A program's own record kit (its layouts and kit sources): an embedded callee's CALL areas the caller's kit lacks come from it.</summary>
    public Func<string, (KitLayouts Layouts, IReadOnlyDictionary<string, string> Sources)?>? KitOf { get; init; }
}

/// <param name="Files">Java sources (file name → source), in the capability's package.</param>
/// <param name="Gaps">What the projection could not render (each is generated as an exception, never guessed).</param>
/// <param name="Starting">The files that are the model's starting point (the entry class and the logic); the others are given.</param>
/// <param name="Prompt">The prompt section (typed-reference.md): the starting files in full, the given classes' API.</param>
public sealed record TypedReference(string ProgramId, IReadOnlyList<(string Name, string Source)> Files, IReadOnlyList<string> Gaps,
    IReadOnlyList<string> Starting, string Prompt);

/// <summary>
/// The typed-interface projection (Phase B of docs/PIPELINE-OUTPUT.md): a deterministic rewrite of a subprogram's faithful
/// behaviour into typed Java a model can start from. Every record the program passes or receives becomes an interface of
/// typed accessors over the record kit's bytes (<see cref="TypedRecords"/>); the program's paragraphs become methods over those
/// views and over WORKING-STORAGE kept in typed Java fields (<see cref="TypedLogic"/>); and every CALL of a program whose CALL
/// groups the equivalence probe verified becomes those JDK operations (<see cref="VerifiedOperations"/>). The result is judged
/// by the capability's own acceptance test, unchanged: it must pass, because it is the faithful behaviour.
/// </summary>
public static class TypedProjection
{
    public static TypedReference Build(TypedRequest r)
    {
        var gaps = new List<string>();
        var layouts = r.Layouts ?? KitLayouts.Parse(r.LayoutsJson);
        var kitPackage = r.Package + ".records";
        var recs = new TypedRecords(r.Program, r.KitSources);
        var kitTypes = KitTypes(r.KitSources);
        // Interface names stay clear of the record kit's classes (imported by the views) and of the JDK types the code uses.
        var used = new HashSet<string>(StringComparer.Ordinal)
        {
            r.EntryClass, "Storage", "MathContext", "RoundingMode", "BigDecimal", "BigInteger", "LocalDate", "ChronoUnit", "String", "Object",
            "Record", "Map", "List", "Set", "Optional", "Math", "System", "Override", "Pic"
        };
        foreach (var k in r.KitSources.Keys) used.Add(k);
        // Views: the LINKAGE records, then the WORKING-STORAGE areas the record kit has (CALL USING areas of this program).
        var mine = layouts.All.Where(l => l.Program.Equals(r.ProgramId, StringComparison.OrdinalIgnoreCase) && l.Class is not null
                                          && (l.Role.Contains("LINKAGE", StringComparison.Ordinal) || l.Role == "CALL USING")).ToList();
        foreach (var l in mine.OrderBy(l => l.Role.Contains("LINKAGE", StringComparison.Ordinal) ? 0 : 1))
        {
            if (recs.ByLayout.ContainsKey(l.Name)) continue;
            var iface = TypedRecords.InterfaceName(l.Name, used);
            var role = l.Role.Contains("LINKAGE", StringComparison.Ordinal) ? $"A CALL argument of {r.ProgramId} (LINKAGE)." : $"A WORKING-STORAGE area {r.ProgramId} passes to a CALL.";
            recs.Add(WithAccessors(l, layouts), iface, role);
        }
        // CALLed programs with verified operations: their arguments at the CALL sites of this program.
        var operations = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var opsSources = new List<(string, string)>();
        foreach (var (callee, facts) in r.Callees)
        {
            var sites = DdBindingPlanner.Walk(r.Program).Where(s => s.Verb == "CALL" && s.Get("target") is { K: "str" } t && t.V!.Equals(callee, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sites.Count == 0) continue;
            var argNames = sites.Select(s => string.Join(",", (s.L?.GetValueOrDefault("args") ?? []).Select(a => a.L?.V))).Distinct().ToList();
            if (argNames.Select(a => string.Join(",", a.Split(',').Select(n => recs.ByLayout.GetValueOrDefault(n)?.KitClass ?? "?" + n))).Distinct().Count() != 1) { gaps.Add($"CALL '{callee}' passes different arguments at different sites"); continue; }
            var argRecords = argNames[0].Split(',').Select(n => recs.ByLayout.GetValueOrDefault(n)).ToList();
            if (argRecords.Any(a => a is null)) { gaps.Add($"CALL '{callee}': an argument is not a record of the kit"); continue; }
            var ops = new VerifiedOperations(callee.ToUpperInvariant(), facts.EquivalencesJson, facts.LayoutsJson,
                argRecords.Select(a => (a!.Layout, a.KitClass)).ToList(), kitTypes, gaps, SelectorCodes(r.Program, facts.EquivalencesJson));
            try
            {
                opsSources.Add((ops.ClassName, ops.Source(r.Package, kitPackage)));
                operations[callee] = ops.ClassName;
            }
            catch (NotSupportedException e) { gaps.Add($"CALL '{callee}': {e.Message}"); }
        }
        // CALLed programs without verified operations whose own typed reference passed: embedded, bottom-up.
        var embedded = new Dictionary<string, (string Wrapper, string Entry)>(StringComparer.OrdinalIgnoreCase);
        var embedSources = new List<(string Name, string Source)>();
        foreach (var callee in DdBindingPlanner.Walk(r.Program).Where(s => s.Verb == "CALL" && s.Get("target") is { K: "str" }).Select(s => s.Get("target")!.V!.ToUpperInvariant()).Distinct())
        {
            if (operations.ContainsKey(callee) || callee.StartsWith("CEE", StringComparison.Ordinal) || r.Embeddable?.GetValueOrDefault(callee) is not { } calleeProgram) continue;
            if (r.Depth >= 8) { gaps.Add($"CALL '{callee}': embedded more than 8 levels deep"); continue; }
            var sites = DdBindingPlanner.Walk(r.Program).Where(s => s.Verb == "CALL" && s.Get("target") is { K: "str" } t && t.V!.Equals(callee, StringComparison.OrdinalIgnoreCase)).ToList();
            var argNames = sites.Select(s => string.Join(",", (s.L?.GetValueOrDefault("args") ?? []).Select(a => a.L?.V))).Distinct().ToList();
            if (argNames.Select(a => string.Join(",", a.Split(',').Select(n => recs.ByLayout.GetValueOrDefault(n)?.KitClass ?? "?" + n))).Distinct().Count() != 1) { gaps.Add($"CALL '{callee}' passes different arguments at different sites"); continue; }
            var argRecords = argNames[0].Split(',').Select(n => recs.ByLayout.GetValueOrDefault(n)).ToList();
            if (argRecords.Any(a => a is null)) { gaps.Add($"CALL '{callee}': an argument is not a record of the kit"); continue; }
            try
            {
                var copies = new Dictionary<string, string>(StringComparer.Ordinal);
                var calleeLayouts = CalleeLayouts(calleeProgram, argRecords.Select(a => a!.Layout).ToList(), layouts, r.KitOf?.Invoke(callee), r.KitSources, copies);
                var sources = new Dictionary<string, string>(r.KitSources, StringComparer.Ordinal);
                foreach (var (k, v) in copies) sources[k] = v;
                var sub = Build(new TypedRequest(callee, r.Package, TypedRecords.Pascal(callee) + "Program", calleeProgram, "", sources, r.Callees, r.Embeddable)
                    { Layouts = calleeLayouts, Depth = r.Depth + 1, KitOf = r.KitOf });
                if (sub.Gaps.Count > 0) { gaps.Add($"CALL '{callee}': its typed reference over this kit has gaps ({string.Join("; ", sub.Gaps.Take(3))})"); continue; }
                var wrapper = TypedRecords.Pascal(callee) + "Reference";
                // The callee's own CALL areas this kit lacks: its kit's classes, nested with the kit's Pic (package-private helpers).
                var nested = copies.Count == 0 ? sub.Files : [.. sub.Files, .. copies.Select(c => (c.Key, c.Value)), ("Pic", r.KitOf!(callee)!.Value.Sources["Pic"])];
                embedSources.Add((wrapper, Wrap(callee, wrapper, nested, r.Package, copies.Keys.ToHashSet(StringComparer.Ordinal))));
                embedded[callee] = (wrapper, TypedRecords.Pascal(callee) + "Program");
            }
            catch (NotSupportedException e) { gaps.Add($"CALL '{callee}': {e.Message}"); }
        }
        var logicClass = TypedRecords.Pascal(r.ProgramId) + "Logic";
        var logic = new TypedLogic(r.Program, recs, logicClass, operations, gaps, embedded);
        logic.Bind();
        var files = new List<(string Name, string Source)>();
        foreach (var rec in recs.ByLayout.Values) files.Add((rec.Interface, recs.Source(rec, r.Package, kitPackage)));
        var logicSource = logic.Source(r.Package, kitPackage, []);
        var entrySource = Entry(r, logic, logicClass);
        files.Add((logicClass, logicSource));
        files.AddRange(opsSources);
        files.AddRange(embedSources);
        files.Add(("Storage", StorageSource(r.Package)));
        files.Add((r.EntryClass, entrySource));
        var prompt = Prompt(r, recs, kitPackage, logicClass, logicSource, entrySource, opsSources, gaps, embedSources.Select(e => e.Name).ToList());
        return new TypedReference(r.ProgramId, files, gaps, [r.EntryClass, logicClass], prompt);
    }

    /// <summary>
    /// An embedded callee's layouts: each LINKAGE record named as the callee names it, over the caller's kit class for the argument
    /// the caller passes there (fields renamed by offset and length to the callee's names); the callee's own WORKING-STORAGE CALL
    /// USING areas are the caller's kit records of the same name and shape, else the callee's own kit records (their classes
    /// go to <paramref name="copies"/> unless the caller's kit has the same class).
    /// </summary>
    static KitLayouts CalleeLayouts(CobolProgram callee, List<KitLayout> args, KitLayouts caller,
        (KitLayouts Layouts, IReadOnlyDictionary<string, string> Sources)? calleeKit, IReadOnlyDictionary<string, string> callerSources, Dictionary<string, string> copies)
    {
        var list = new List<KitLayout>();
        var linkage = new List<string>();
        for (int i = 0; i < callee.Using.Count; i++)
        {
            var l = callee.Items.FirstOrDefault(x => x.Section == DataSection.Linkage && x.Level is 1 or 77 && x.Name.Equals(callee.Using[i], StringComparison.OrdinalIgnoreCase))
                    ?? throw new NotSupportedException($"{callee.ProgramId}: USING {callee.Using[i]} not found");
            if (i >= args.Count || args[i].Length != l.Length) throw new NotSupportedException($"{callee.ProgramId}: argument {i + 1} is not the caller's {(i < args.Count ? args[i].Name : "?")}");
            var items = Descendants(l).ToList();
            var fields = args[i].Fields.Select(f =>
            {
                var at = items.Where(c => c.Offset - l.Offset == f.Offset && c.Length == f.Length && (c.IsLeaf != f.Group) && c.Dims.Count == f.Occurs.Count).ToList();
                var match = at.FirstOrDefault(c => c.Name == f.Name) ?? at.FirstOrDefault(c => c.RedefinesItem is null) ?? at.FirstOrDefault();
                return match is null ? f : f with { Name = match.Name };
            }).ToList();
            list.Add(new KitLayout(l.Name, callee.ProgramId, "CALL argument (LINKAGE)", l.Length, args[i].Class, fields));
            linkage.Add(l.Name);
        }
        foreach (var s in DdBindingPlanner.Walk(callee).Where(s => s.Verb == "CALL"))
            foreach (var a in s.L?.GetValueOrDefault("args") ?? [])
            {
                if (a.L?.V is not { } name || list.Any(x => x.Name == name)
                    || callee.Items.FirstOrDefault(x => x.Name == name && x.Level is 1 or 77) is not { } it) continue;
                if (caller.All.FirstOrDefault(x => x.Name == name && x.Class is not null && x.Role is "CALL USING") is { } same && it.Length == same.Length)
                {
                    list.Add(same with { Program = callee.ProgramId });
                    continue;
                }
                if (calleeKit?.Layouts.All.FirstOrDefault(x => x.Name == name && x.Class is not null && x.Role is "CALL USING"
                        && x.Program.Equals(callee.ProgramId, StringComparison.OrdinalIgnoreCase)) is not { } found || found.Length != it.Length
                    || calleeKit.Value.Sources.GetValueOrDefault(found.Class!) is not { } src) continue;
                var own = WithAccessors(found, calleeKit.Value.Layouts);
                // This kit has the record (the same copybook, under that class or another, with accessors named its way): by place.
                if (caller.All.Where(x => x.Class is not null && callerSources.ContainsKey(x.Class) && x.Fields.Any(f => f.Accessor is not null) && SameShape(x, own))
                        .OrderBy(x => x.Class == own.Class ? 0 : 1).FirstOrDefault() is { } mine)
                {
                    list.Add(Rebind(own, mine) with { Program = callee.ProgramId });
                    continue;
                }
                if (callerSources.TryGetValue(own.Class!, out var have) && KitShape(have) != KitShape(src))
                    throw new NotSupportedException($"{callee.ProgramId}: {name}'s kit class {own.Class} differs from this kit's class of that name");
                if (!callerSources.ContainsKey(own.Class!)) copies[own.Class!] = src;
                list.Add(own);
            }
        return KitLayouts.Of(list, linkage);
    }

    /// <summary>
    /// A record over another record's kit class (a WORKING-STORAGE copy of the same copybook under another prefix, MU-MODEL over
    /// LmModel): the kit names no accessors for its fields, so each takes the accessor of the field at its place in the record
    /// that has them.
    /// </summary>
    static KitLayout WithAccessors(KitLayout l, KitLayouts all)
    {
        if (l.Fields.Any(f => f.Accessor is not null)) return l;
        var owner = all.All.FirstOrDefault(o => o.Class == l.Class && o.Length == l.Length && o.Fields.Any(f => f.Accessor is not null));
        return owner is null ? l : Rebind(l, owner);
    }

    /// <summary>The same record layout: every field at the same place with the same PICTURE and USAGE (names aside).</summary>
    static bool SameShape(KitLayout a, KitLayout b) =>
        a.Length == b.Length && a.Fields.Count == b.Fields.Count && a.Fields.Zip(b.Fields).All(p =>
            p.First.Offset == p.Second.Offset && p.First.Length == p.Second.Length && p.First.Group == p.Second.Group && p.First.Kind == p.Second.Kind
            && p.First.Digits == p.Second.Digits && p.First.Scale == p.Second.Scale && p.First.Signed == p.Second.Signed && p.First.Occurs.SequenceEqual(p.Second.Occurs));

    /// <summary>A record's fields with the accessors of the fields at the same places in <paramref name="owner"/> (the same record).</summary>
    static KitLayout Rebind(KitLayout l, KitLayout owner)
    {
        var fields = l.Fields.Select((f, i) =>
        {
            var o = i < owner.Fields.Count && owner.Fields[i] is { } p && p.Offset == f.Offset && p.Length == f.Length && p.Group == f.Group && p.Occurs.SequenceEqual(f.Occurs)
                ? p : owner.Fields.FirstOrDefault(p => p.Offset == f.Offset && p.Length == f.Length && p.Group == f.Group && p.Occurs.SequenceEqual(f.Occurs));
            return f with { Accessor = o?.Accessor };
        }).ToList();
        return l with { Class = owner.Class, Fields = fields };
    }

    /// <summary>A kit class's code without its package and comments (the same record in two kits).</summary>
    static string KitShape(string src) => string.Join("\n", src.Replace("\r\n", "\n").Split('\n')
        .Where(l => !l.StartsWith("package ", StringComparison.Ordinal) && !Regex.IsMatch(l.TrimStart(), @"^(/\*\*|\*|\*/|//)")));

    static IEnumerable<DataItem> Descendants(DataItem root)
    {
        foreach (var c in root.Children.Where(c => c.Level != 88))
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    /// <summary>A projection's files as one class of nested types (the callee's names cannot collide with the caller's).</summary>
    static string Wrap(string callee, string wrapper, IReadOnlyList<(string Name, string Source)> files, string package, IReadOnlySet<string> nestedKit)
    {
        var imports = new SortedSet<string>(StringComparer.Ordinal);
        var body = new StringBuilder();
        foreach (var (_, src) in files)
        {
            foreach (var line in src.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.StartsWith("package ", StringComparison.Ordinal)) continue;
                if (line.StartsWith("import ", StringComparison.Ordinal))
                {
                    // A kit class nested here (the callee's own CALL area) is not in the record kit's package.
                    if (Regex.Match(line, @"^import [\w.]+\.records\.(\w+);") is { Success: true } km && nestedKit.Contains(km.Groups[1].Value)) continue;
                    imports.Add(line.Trim());
                    continue;
                }
                var l = Regex.Replace(line, @"^public interface ", "interface ");
                l = Regex.Replace(l, @"^(public )?final class ", "static final class ");
                body.Append(l.Length == 0 ? "\n" : "    " + l + "\n");
            }
        }
        var sb = new StringBuilder($"package {package};\n\n");
        foreach (var i in imports) sb.Append(i).Append('\n');
        sb.Append($"\n/**\n * {callee}'s typed reference, which passed {callee}'s own acceptance test, generated again over this capability's record kit:\n");
        sb.Append($" * CALL '{callee}' runs it ({wrapper}.{TypedRecords.Pascal(callee)}Program). Its types are nested here, so its names cannot collide.\n */\n");
        sb.Append($"final class {wrapper} {{\n    private {wrapper}() {{}}\n").Append(Regex.Replace(body.ToString(), @"\n{3,}", "\n\n")).Append("}\n");
        return sb.ToString();
    }

    /// <summary>The implementation prompt's section: what the reference is, what is given, the starting files, the given API.</summary>
    static string Prompt(TypedRequest r, TypedRecords recs, string kitPackage, string logicClass, string logicSource, string entrySource,
        List<(string Name, string Source)> ops, List<string> gaps, List<string> embeddedRefs)
    {
        var views = recs.ByLayout.Values.Select(x => x.Interface).ToList();
        var sb = new StringBuilder($"# Typed reference implementation of {r.ProgramId} (generated from the COBOL; it passes the acceptance test)\n");
        sb.Append($"The pipeline rewrote {r.ProgramId} mechanically onto typed views of its records: each paragraph is a method (its comment names the ");
        sb.Append("paragraph, as the requirements packet's step ids do), each statement a typed assignment that stores with the COBOL rules");
        sb.Append(ops.Count > 0 ? $", and each CALL of {string.Join(", ", ops.Select(o => o.Name.Replace("Operations", "").ToUpperInvariant()))} is the JDK operation the equivalence probe verified" : "");
        sb.Append(". It reproduces the COBOL program exactly, but it is not yet the design.\n");
        sb.Append($"Already in package `{r.Package}` and compiled (use them; do not redefine them): the record views {string.Join(", ", views)} ");
        sb.Append($"(typed accessors over the record kit's bytes){(ops.Count > 0 ? ", " + string.Join(", ", ops.Select(o => o.Name)) + " (the verified operations)" : "")}, ");
        sb.Append("and Storage (COBOL store rules for values kept in Java fields).\n");
        if (embeddedRefs.Count > 0)
            sb.Append($"A CALL of {string.Join(", ", embeddedRefs.Select(e => e.Replace("Reference", "").ToUpperInvariant()))} runs that program's own typed reference, embedded in " +
                      $"{string.Join(", ", embeddedRefs)} (in the package; each passed its program's acceptance test); you may call it as the reference does.\n");
        sb.Append($"Your starting files are {r.EntryClass}.java and {logicClass}.java: returned as they are, they pass. Refactor them into the design ");
        sb.Append("(domain records, enums, services, BigDecimal and LocalDate operations, clear names) and keep every behaviour; edit them with ");
        sb.Append("SEARCH/REPLACE blocks or return them whole. The tests judge the behaviour; the design judges the code.\n");
        if (gaps.Count > 0)
            sb.Append($"Where the projection could not render a statement it throws instead of guessing ({gaps.Count}): {string.Join("; ", gaps.Take(8))}{(gaps.Count > 8 ? "; …" : "")}.\n");
        sb.Append($"\n## {r.EntryClass}.java\n```java\n{entrySource.TrimEnd()}\n```\n");
        sb.Append($"\n## {logicClass}.java\n```java\n{logicSource.TrimEnd()}\n```\n");
        sb.Append("\n## The record views (their API, getter / setter per item; the implementation over the record kit's bytes is in the package)\n");
        sb.Append("Text getters drop trailing spaces and setters pad, numeric setters store with the item's PICTURE and USAGE (the record kit's MOVE rules).\n```java\n");
        foreach (var rec in recs.ByLayout.Values) sb.Append(recs.Summary(rec)).Append('\n');
        sb.Append("```\n");
        foreach (var (name, src) in ops)
            sb.Append($"\n## {name}.java (in the package)\n```java\n{src[src.IndexOf("/**", StringComparison.Ordinal)..].TrimEnd()}\n```\n");
        sb.Append("\n## Storage (its API; in the package)\n```java\n");
        foreach (Match mt in Regex.Matches(StorageSource(r.Package), @"(/\*\*[^\n]*\*/)\n\s*(static [^{]+)\{"))
            sb.Append(mt.Groups[1].Value.Trim()).Append('\n').Append(mt.Groups[2].Value.Trim()).Append(";\n");
        sb.Append("```\n");
        return sb.ToString();
    }

    /// <summary>
    /// The codes the program can put into the callee's function selector: the literals it MOVEs to it and the values of the
    /// 88-level names it SETs. Null (every group) when the selector also receives anything else.
    /// </summary>
    static IReadOnlySet<string>? SelectorCodes(CobolProgram m, string equivalencesJson)
    {
        var selector = System.Text.Json.Nodes.JsonNode.Parse(equivalencesJson)!["functionSelector"]?.GetValue<string>();
        if (selector is null) return null;
        var items = m.Items.Where(i => i.Name == selector).ToList();
        if (items.Count == 0) return null;
        var ids = items.Select(i => i.Id).ToHashSet();
        var conds = items.SelectMany(i => i.Children.Where(c => c.Level == 88)).ToDictionary(c => c.Id);
        // A MOVE or INITIALIZE of a group that holds the selector writes it too: then any code may arrive.
        var groups = items.SelectMany(i => { var l = new List<int>(); for (var p = i.Parent; p is not null; p = p.Parent) l.Add(p.Id); return l; }).ToHashSet();
        if (DdBindingPlanner.Walk(m).Any(s => s.Verb is "MOVE" or "INITIALIZE" && (s.L?.GetValueOrDefault("dst") ?? []).Any(d => d.Id is { } id && groups.Contains(id))))
            return null;
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in DdBindingPlanner.Walk(m))
        {
            var dsts = s.L?.GetValueOrDefault("dst") ?? [];
            if (s.Verb == "MOVE" && dsts.Any(d => d.Id is { } id && ids.Contains(id)))
            {
                if (s.Get("src") is { K: "str" or "num" } lit) codes.Add(lit.V!.TrimEnd(' '));
                else return null;
            }
            else if (s.Verb == "SET")
                foreach (var d in dsts.Where(d => d.Id is { } id && conds.ContainsKey(id)))
                    codes.Add(conds[d.Id!.Value].Values[0].Low.Text.TrimEnd(' '));
            else if (s.Verb is not ("IF" or "EVALUATE" or "PERFORM" or "WHEN" or "CALL") && dsts.Any(d => d.Id is { } id && ids.Contains(id)))
                return null;
        }
        return codes;
    }

    public static Dictionary<string, string> KitTypes(IReadOnlyDictionary<string, string> kitSources)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (cls, src) in kitSources)
            foreach (Match mt in Regex.Matches(src, @"public (long|BigDecimal|String|boolean) (\w+)\((?:int \w+(?:, int \w+)*)?\) \{"))
                map.TryAdd(cls + "." + mt.Groups[2].Value, mt.Groups[1].Value);
        return map;
    }

    static string Entry(TypedRequest r, TypedLogic logic, string logicClass)
    {
        var sb = new StringBuilder($"package {r.Package};\n\nimport com.mlcpu.rt.ModernProgram;\n\n");
        sb.Append($"/**\n * {r.ProgramId}: every CALL '{r.ProgramId}' reaches call(parameters); the arguments are viewed as typed records and the\n");
        sb.Append($" * program's logic ({logicClass}) runs on them. One instance serves every CALL of the run, so WORKING-STORAGE persists.\n */\n");
        sb.Append($"public final class {r.EntryClass} implements ModernProgram {{\n    private final {logicClass} logic = new {logicClass}();\n\n");
        sb.Append("    @Override\n    public void call(byte[][] parameters) {\n");
        sb.Append($"        logic.call({string.Join(", ", logic.Arguments.Select(a => $"{a.Rec.Interface}.over(parameters[{a.Arg}])"))});\n    }}\n}}\n");
        return sb.ToString();
    }

    /// <summary>COBOL store rules for WORKING-STORAGE values kept in Java fields (the oracle's: GnuCOBOL -std=ibm, binary-truncate no).</summary>
    static string StorageSource(string package) => $$"""
        package {{package}};

        import java.math.BigDecimal;
        import java.math.BigInteger;
        import java.math.RoundingMode;
        import java.nio.charset.StandardCharsets;

        /**
         * COBOL store rules for values the program keeps in Java fields, as the original program applies them (GnuCOBOL -std=ibm,
         * binary-truncate no): a packed or zoned item keeps its PICTURE's digits (fraction digits beyond the scale cut, integer
         * digits beyond the PICTURE lost on the left, the sign dropped when unsigned); a binary item keeps its byte size (two's
         * complement). Generated by the typed-interface projection.
         */
        final class Storage {
            private Storage() {}

            /** MOVE / COMPUTE without ROUNDED into a packed or zoned item of {@code digits} digits, {@code scale} of them after the point. */
            static BigDecimal decimal(BigDecimal v, int digits, int scale, boolean signed) {
                BigInteger u = v.setScale(scale, RoundingMode.DOWN).unscaledValue();
                if (!signed) u = u.abs();
                BigInteger cap = BigInteger.TEN.pow(digits);
                if (u.abs().compareTo(cap) >= 0) u = u.signum() < 0 ? u.negate().mod(cap).negate() : u.mod(cap);
                return new BigDecimal(u, scale);
            }

            /** ON SIZE ERROR for a packed or zoned item: integer digits beyond the PICTURE. */
            static boolean decimalOverflow(BigDecimal v, int digits, int scale) {
                return v.setScale(scale, RoundingMode.DOWN).unscaledValue().abs().compareTo(BigInteger.TEN.pow(digits)) >= 0;
            }

            /** MOVE / COMPUTE into a binary item of {@code bytes} bytes: the integer part, wrapped to the byte size. */
            static long binary(BigDecimal v, int bytes, boolean signed) {
                BigInteger u = v.setScale(0, RoundingMode.DOWN).unscaledValue();
                if (!signed) u = u.abs();
                return wrap(u, bytes, signed);
            }

            static long binary(long v, int bytes, boolean signed) {
                return binary(BigDecimal.valueOf(v), bytes, signed);
            }

            /** ON SIZE ERROR for a binary item: the integer part beyond the byte size. */
            static boolean binaryOverflow(BigDecimal v, int bytes, boolean signed) {
                BigInteger u = v.setScale(0, RoundingMode.DOWN).unscaledValue();
                if (!signed) u = u.abs();
                return u.abs().compareTo(BigInteger.ONE.shiftLeft(bytes * 8 - (signed ? 1 : 0))) >= 0;
            }

            static boolean binaryOverflow(long v, int bytes, boolean signed) {
                return binaryOverflow(BigDecimal.valueOf(v), bytes, signed);
            }

            static boolean decimalOverflow(long v, int digits, int scale) {
                return decimalOverflow(BigDecimal.valueOf(v), digits, scale);
            }

            private static long wrap(BigInteger u, int bytes, boolean signed) {
                BigInteger mod = BigInteger.ONE.shiftLeft(bytes * 8);
                BigInteger r = u.mod(mod);
                if (signed && r.testBit(bytes * 8 - 1)) r = r.subtract(mod);
                return r.longValue();
            }

            /** MOVE text into a text item of {@code length} characters: space padded or truncated on the right (trailing spaces dropped, as the views show text). */
            static String text(String v, int length) {
                String s = v.length() > length ? v.substring(0, length) : v;
                int n = s.length();
                while (n > 0 && s.charAt(n - 1) == ' ') n--;
                return s.substring(0, n);
            }

            /** FUNCTION INTEGER-OF-DATE: the day number of a yyyymmdd date (day 1 = 1601-01-01); 0 outside its domain. */
            static long integerOfDate(long yyyymmdd) {
                int y = (int) (yyyymmdd / 10000), m = (int) (yyyymmdd / 100 % 100), d = (int) (yyyymmdd % 100);
                if (yyyymmdd < 16010101 || yyyymmdd > 99991231 || m < 1 || m > 12 || d < 1 || d > java.time.YearMonth.of(y, m).lengthOfMonth()) return 0;
                return java.time.temporal.ChronoUnit.DAYS.between(java.time.LocalDate.of(1600, 12, 31), java.time.LocalDate.of(y, m, d));
            }

            /** FUNCTION DATE-OF-INTEGER: the yyyymmdd date of a day number (day 1 = 1601-01-01); 0 outside its domain. */
            static long dateOfInteger(long day) {
                if (day < 1 || day > 3067671) return 0;
                java.time.LocalDate x = java.time.LocalDate.of(1600, 12, 31).plusDays(day);
                return x.getYear() * 10000L + x.getMonthValue() * 100L + x.getDayOfMonth();
            }

            /** COBOL comparison of two texts: the shorter is padded with spaces. */
            static int compareText(String a, String b) {
                int n = Math.max(a.length(), b.length());
                for (int i = 0; i < n; i++) {
                    char x = i < a.length() ? a.charAt(i) : ' ', y = i < b.length() ? b.charAt(i) : ' ';
                    if (x != y) return x < y ? -1 : 1;
                }
                return 0;
            }

            /** MOVE between byte ranges (reference modification): copied, space padded or cut to the receiving length. */
            static void moveBytes(byte[] from, int fromOffset, int fromLength, byte[] to, int toOffset, int toLength) {
                int c = Math.min(fromLength, toLength);
                byte[] copy = java.util.Arrays.copyOfRange(from, fromOffset, fromOffset + c);
                System.arraycopy(copy, 0, to, toOffset, c);
                java.util.Arrays.fill(to, toOffset + c, toOffset + toLength, (byte) ' ');
            }

            /** MOVE a group (its bytes as ISO-8859-1 text) to a record area: copied, space padded or truncated. */
            static void move(String from, byte[] to) {
                byte[] b = from.getBytes(StandardCharsets.ISO_8859_1);
                int c = Math.min(b.length, to.length);
                System.arraycopy(b, 0, to, 0, c);
                java.util.Arrays.fill(to, c, to.length, (byte) ' ');
            }
        }
        """;
}
