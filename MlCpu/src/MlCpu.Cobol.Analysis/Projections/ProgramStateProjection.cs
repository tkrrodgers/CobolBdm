using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.Cobol.Analysis.Projections;

/// <summary>
/// Projection 3 — <c>Prog_&lt;PID&gt;</c> → <c>Prog_&lt;PID&gt; extends State_&lt;PID&gt;</c> (§8, Q13.3): storage areas,
/// field views, files, <c>_init</c>, <c>_linkage</c>, <c>_fields</c> and the literal constants move to the abstract state
/// class; moved privates become protected. Inheritance keeps every field reference valid, so procedure bodies are untouched.
/// </summary>
public static partial class ProgramStateProjection
{
    public const string Pass = "ProgramStateProjection";

    [GeneratedRegex(@"/core/Prog_([A-Za-z0-9_]+)\.java$")] private static partial Regex PathRx();
    [GeneratedRegex(@"^public final class Prog_(?<p>[A-Za-z0-9_]+) extends CobolBase \{[ \t]*\r?$", RegexOptions.Multiline)] private static partial Regex ClassRx();
    [GeneratedRegex(@"^    private ", RegexOptions.Multiline)] private static partial Regex PrivateRx();
    [GeneratedRegex(@"(\r?\n)[ \t]*(\r?\n)([ \t]*\r?\n)+")] private static partial Regex BlankRuns();
    [GeneratedRegex(@"^    private static final [^(\r\n]*=[^\r\n]*;[^\r\n]*\r?\n", RegexOptions.Multiline)] private static partial Regex ConstantRx();
    [GeneratedRegex(@"^import [^\r\n]+;\r?$", RegexOptions.Multiline)] private static partial Regex ImportRx();
    [GeneratedRegex(@"^    private void _(?:init|fields)\d+\(", RegexOptions.Multiline)] private static partial Regex HelperRx();

    static readonly string[] MovedMembers = ["protected void _init() {", "protected Area[] _linkage() {", "protected java.util.Map<String, CobField> _fields() {"];

    public static ProjectionLedger Split(ProjectFiles files)
    {
        var ledger = new ProjectionLedger(Pass);
        int lines = 0;
        foreach (var path in files.Paths("src/main/java/com/mlcpu/core/"))
        {
            var pm = PathRx().Match(path);
            if (!pm.Success) continue;
            var program = pm.Groups[1].Value;
            var text = files.Get(path)!;
            var (state, prog, reason) = SplitOne(program, text);
            if (reason is not null) { ledger.Skip(program, reason); continue; }
            files.Set(path, prog!);
            files.Set($"src/main/java/com/mlcpu/core/State_{program}.java", state!);
            lines += state!.Count(c => c == '\n');
            ledger.Applied.Add(program);
        }
        ledger.Facts["programsSplit"] = ledger.Applied.Count;
        ledger.Facts["linesMoved"] = lines;
        return ledger;
    }

    public static (string? State, string? Prog, string? Reason) SplitOne(string program, string text)
    {
        var cm = ClassRx().Match(text);
        if (!cm.Success) return (null, null, $"{program}: class declaration not in expected form");
        if (cm.Groups["p"].Value != program) return (null, null, $"{program}: class name does not match file name");
        if (!text.Contains("    protected void _dispatch(int pc) {", StringComparison.Ordinal)) return (null, null, $"{program}: no _dispatch(int) to split at");

        var spans = new List<(int Start, int End)>();
        var storageStart = text.IndexOf("    // ---- storage areas", StringComparison.Ordinal);
        var init = JavaText.Member(text, MovedMembers[0]);
        if (storageStart < 0 || init is null) return (null, null, $"{program}: unterminated 'storage areas'");
        spans.Add((storageStart, init.Value.Start));
        foreach (var sig in MovedMembers)
        {
            var m = JavaText.Member(text, sig);
            if (m is null) return (null, null, $"{program}: unterminated '{sig}'");
            if (m.Value.End < 0) return (null, null, $"{program}: unbalanced '{sig}'");
            spans.Add((m.Value.Start, m.Value.End));
        }
        // A large program's _init and _fields call helpers (_init0, _fields0, ...: the emitter splits bodies past the JVM's
        // 64 KB per method); they move with the member that calls them.
        foreach (Match h in HelperRx().Matches(text))
        {
            var m = JavaText.Member(text, h.Value.TrimStart());
            if (m is null || m.Value.End < 0) return (null, null, $"{program}: unbalanced '{h.Value.Trim()}'");
            if (spans.Any(s => m.Value.Start >= s.Start && m.Value.Start < s.End)) continue; // already inside a moved span
            spans.Add((m.Value.Start, m.Value.End));
        }
        foreach (Match c in ConstantRx().Matches(text)) spans.Add((c.Index, c.Index + c.Length));
        spans.Sort((a, b) => a.Start.CompareTo(b.Start));
        for (int i = 1; i < spans.Count; i++)
            if (spans[i].Start < spans[i - 1].End) return (null, null, $"{program}: overlapping state members");

        var moved = new StringBuilder();
        foreach (var (s, e) in spans) moved.Append(text, s, e - s).Append('\n');
        var body = PrivateRx().Replace(BlankRuns().Replace(moved.ToString(), "\n\n"), "    protected ").TrimEnd() + "\n";

        var prog = new StringBuilder(text);
        for (int i = spans.Count - 1; i >= 0; i--) prog.Remove(spans[i].Start, spans[i].End - spans[i].Start);
        var progText = ClassRx().Replace(prog.ToString(), $"public final class Prog_{program} extends State_{program} {{", 1);
        progText = BlankRuns().Replace(progText, "\n\n").Replace("{\n\n\n", "{\n\n");

        var imports = string.Join("\n", ImportRx().Matches(text).Select(m => m.Value.TrimEnd('\r')));
        var state = $$"""
            package com.mlcpu.core;

            {{imports}}

            /** Storage of {{program}}: areas, field views, files, VALUE initialization and the ProgramState boundary (projection 3). */
            abstract class State_{{program}} extends CobolBase {

            {{body}}}

            """;
        return (state, progText, null);
    }
}

/// <summary>
/// Projection 4 — identical copybook record layouts across programs become one <c>core/copybook/Storage_*</c> class
/// (§8, Q13.4). The state classes keep the emitter's field names and alias the shared views, so binding keys and
/// procedure bodies are unchanged. Revisions of one copybook that conflict get a SHA-256 suffix.
/// </summary>
public static partial class SharedCopybookProjection
{
    public const string Pass = "SharedCopybookProjection";

    [GeneratedRegex(@"/core/State_([A-Za-z0-9_]+)\.java$")] private static partial Regex PathRx();
    [GeneratedRegex(@"^    final Area (?<v>a\d+) = new Area\((?<n>\d+)\);(?<c>[^\r\n]*)$", RegexOptions.Multiline)] private static partial Regex AreaRx();
    [GeneratedRegex(@"^    final CobField (?<f>\w+) = (?<call>CobField\.\w+\((?<q>""(?:[^""\\]|\\.)*""), (?:CobField\.(?:Use|Cat)\.\w+, )?(?<a>\w+), [^\r\n]*\));(?<c>[^\r\n]*)$", RegexOptions.Multiline)]
    private static partial Regex ItemRx();

    sealed record Use(string Path, string Program, string AreaVar, string Copybook, string Record, int Length, List<(string Field, string Call, string Comment, string Cobol)> Items, string Key);

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units)
    {
        var ledger = new ProjectionLedger(Pass);
        var uses = new List<Use>();
        foreach (var path in files.Paths("src/main/java/com/mlcpu/core/"))
        {
            var pm = PathRx().Match(path);
            if (!pm.Success) continue;
            var program = pm.Groups[1].Value;
            var unit = units.FirstOrDefault(u => u.Pid == program);
            if (unit is null) { ledger.Skip(program, $"{program}: no analysis model"); continue; }
            var text = files.Get(path)!;
            var items = ItemRx().Matches(text).GroupBy(m => m.Groups["a"].Value).ToDictionary(g => g.Key, g => g.ToList());
            foreach (Match a in AreaRx().Matches(text))
            {
                var v = a.Groups["v"].Value;
                if (!items.TryGetValue(v, out var its) || its.Count == 0) continue;
                var head = its[0].Groups["c"].Value.Trim();
                if (!head.StartsWith("// 01 ", StringComparison.Ordinal)) continue;
                var recName = head[6..].Trim();
                var rec = unit.Model.Items.FirstOrDefault(i => i.Level == 1 && i.Name == recName && i.Origin.Length > 0);
                if (rec is null) continue;
                var list = its.Select(m => (m.Groups["f"].Value, m.Groups["call"].Value, m.Groups["c"].Value, Unquote(m.Groups["q"].Value))).ToList();
                var key = rec.Origin + "|" + a.Groups["n"].Value + "|" + string.Join("\n", list.Select(x => Normal(x.Item2, v) + x.Item3));
                uses.Add(new Use(path, program, v, rec.Origin, recName, int.Parse(a.Groups["n"].Value), list, key));
            }
        }

        var types = new List<object>();
        int removed = 0;
        var rewrites = new Dictionary<string, List<(Use U, string Type, List<string> Canon)>>(StringComparer.Ordinal);
        foreach (var byRecord in uses.GroupBy(u => (u.Copybook, u.Record)))
        {
            var shapes = byRecord.GroupBy(u => u.Key).ToList();
            foreach (var shape in shapes)
            {
                var first = shape.First();
                var programs = shape.Select(u => u.Program).Distinct().OrderBy(p => p, StringComparer.Ordinal).ToList();
                var baseName = $"Storage_{San(first.Copybook)}_{San(first.Record)}";
                var type = shapes.Count > 1 ? baseName + "_" + ProjectionJson.Sha256(shape.Key)[..8] : baseName;
                if (programs.Count < 2) { ledger.Skip($"{first.Copybook}/{first.Record}", $"{programs[0]}/{baseName}: used by one program only"); continue; }
                var used = new HashSet<string>(StringComparer.Ordinal) { "area" };
                var canon = first.Items.Select(i => JavaNames.Unique(i.Cobol == "FILLER" ? "filler" : JavaNames.Member(i.Cobol), used)).ToList();
                var sb = new StringBuilder($$"""
                    package com.mlcpu.core.copybook;

                    import com.mlcpu.rt.*;

                    /** Copybook {{first.Copybook}}, record {{first.Record}} ({{first.Length}} bytes): one layout shared by {{string.Join(", ", programs)}} (projection 4). */
                    public final class {{type}} {
                        public final Area area = new Area({{first.Length}});

                    """);
                for (int i = 0; i < first.Items.Count; i++)
                    sb.Append($"    public final CobField {canon[i]} = {Normal(first.Items[i].Call, first.AreaVar)};{first.Items[i].Comment}\n");
                sb.Append("}\n");
                files.Set($"src/main/java/com/mlcpu/core/copybook/{type}.java", sb.ToString());
                foreach (var u in shape)
                {
                    if (!rewrites.TryGetValue(u.Path, out var l)) rewrites[u.Path] = l = [];
                    l.Add((u, type, canon));
                    removed += u.Items.Count;
                }
                types.Add(new { TypeName = type, BaseName = baseName, Fields = first.Items.Count, Programs = programs });
                ledger.Applied.Add(type);
            }
        }
        foreach (var (path, list) in rewrites)
        {
            var text = files.Get(path)!;
            foreach (var (u, type, canon) in list)
            {
                text = Regex.Replace(text, $@"^    final Area {u.AreaVar} = new Area\(\d+\);(?<c>[^\r\n]*)$",
                    m => $"    final {type} s_{u.AreaVar} = new {type}();\n    final Area {u.AreaVar} = s_{u.AreaVar}.area;{m.Groups["c"].Value}", RegexOptions.Multiline);
                for (int i = 0; i < u.Items.Count; i++)
                {
                    var (field, call, comment, _) = u.Items[i];
                    var line = $"    final CobField {field} = {call};{comment}";
                    var at = text.IndexOf(line, StringComparison.Ordinal);
                    if (at < 0) { ledger.Skip($"{u.Program}/{type}", $"{u.Program}/{type}: declaration not in expected form"); continue; }
                    text = text.Remove(at, line.Length).Insert(at, $"    final CobField {field} = s_{u.AreaVar}.{canon[i]};{comment}");
                }
            }
            if (!text.Contains("import com.mlcpu.core.copybook.*;", StringComparison.Ordinal))
                text = text.Replace("import com.mlcpu.rt.*;", "import com.mlcpu.core.copybook.*;\nimport com.mlcpu.rt.*;");
            files.Set(path, text);
        }
        ledger.Facts["package"] = "com.mlcpu.core.copybook";
        ledger.Facts["sharedTypes"] = types.Count;
        ledger.Facts["declarationsRemoved"] = removed;
        ledger.Facts["types"] = types;
        return ledger;
    }

    /// <summary>The factory call with the area variable replaced by the storage class's own <c>area</c>.</summary>
    static string Normal(string call, string areaVar) => Regex.Replace(call, $@"(?<=[(,] ){Regex.Escape(areaVar)}(?=,)", "area");

    static string Unquote(string q) => q[1..^1].Replace("\\\"", "\"").Replace("\\\\", "\\");

    static string San(string s) => new(s.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());
}
