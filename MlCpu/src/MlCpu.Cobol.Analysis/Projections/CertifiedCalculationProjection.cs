using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MlCpu.Cobol.Analysis.Projections;

public sealed record CalculationField(string Cobol, string DomainName);

public sealed record CalculationTestVector(string Name, IReadOnlyList<string> Inputs);

public enum CalculationRoutingStage { Isolated, ShadowReady }

public sealed record CertifiedCalculationSlice(string ProgramId, string Paragraph, string Package, string Capability,
    IReadOnlyList<CalculationField> Inputs, IReadOnlyList<CalculationField> Outputs, IReadOnlyList<CalculationTestVector> TestVectors,
    string SemanticFingerprint, string Evidence, string ReviewedBy, string ReviewedAt, CalculationRoutingStage RoutingStage = CalculationRoutingStage.Isolated);

/// <summary>
/// Reviewed, in-code data (Q11, ASK-7). CardDemo has no closed calculation paragraph (each COMPUTE paragraph also PERFORMs
/// a write), so the catalog is empty for this estate.
/// </summary>
public static class CertifiedCalculationCatalog
{
    public static readonly IReadOnlyList<CertifiedCalculationSlice> Entries = [];
}

/// <summary>
/// Projection 6 (§8, Q13.6): a certified closed calculation paragraph becomes <c>capabilities.&lt;pkg&gt;.&lt;Cap&gt;Calculator</c>
/// with typed input/result records and its own storage, running the generated paragraph body verbatim (so arithmetic,
/// truncation and rounding are the core's), plus a differential test against Baseline_&lt;PID&gt; over the catalog's vectors.
/// Isolated slices add files only; ShadowReady slices also make the paragraph compare calculator and core at run time
/// (drift throws). Any change to the paragraph's semantics invalidates the fingerprint and the slice is refused.
/// </summary>
public static partial class CertifiedCalculationProjection
{
    public const string Pass = "CertifiedCalculationProjection";

    static readonly HashSet<string> ClosedVerbs = new(StringComparer.Ordinal)
        { "COMPUTE", "ADD", "SUBTRACT", "MULTIPLY", "DIVIDE", "MOVE", "IF", "EVALUATE", "WHEN", "CONTINUE", "INITIALIZE" };

    [GeneratedRegex(@"m\.put\(""(?<c>(?:[^""\\]|\\.)*)"", (?<j>\w+)\);")] private static partial Regex FieldMapRx();
    [GeneratedRegex(@"^\s*(?:private|protected) static final (?<decl>(?:NumLit|Object\[\]\[\]) (?<n>\w+) = [^\r\n]*;)", RegexOptions.Multiline)] private static partial Regex ConstRx();

    /// <summary>SHA-256 over the paragraph's statement semantics (verbs and operand trees; no text, lines or step ids).</summary>
    public static string Fingerprint(Paragraph p)
    {
        static object Shape(Stmt s) => new
        {
            s.Verb,
            N = s.N?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
            L = s.L?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
            S = s.S?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value),
            Blocks = s.Blocks?.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToDictionary(kv => kv.Key, kv => kv.Value.Select(Shape).ToList()),
            s.Frontier
        };
        return ProjectionJson.Sha256(JsonSerializer.Serialize(p.Statements.Select(Shape).ToList(), ProjectionJson.Pascal));
    }

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units, IReadOnlyList<CertifiedCalculationSlice>? catalog = null)
    {
        catalog ??= CertifiedCalculationCatalog.Entries;
        var ledger = new ProjectionLedger(Pass);
        var rejections = new List<object>();
        int hooks = 0, routed = 0;
        foreach (var u in units.Where(u => !catalog.Any(c => c.ProgramId == u.ProgramId)))
            ledger.Skip(u.ProgramId, $"{u.ProgramId}: no certified calculation slices");
        foreach (var slice in catalog)
        {
            var u = units.FirstOrDefault(x => x.ProgramId == slice.ProgramId);
            if (u is null) continue;
            var progPath = $"src/main/java/com/mlcpu/core/{u.ClassName}.java";
            var java = files.Get(progPath);
            if (java is null) { ledger.Skip($"{slice.ProgramId}/{slice.Paragraph}", $"{u.Pid}: generated program not found"); continue; }
            string calculator, test;
            try { (calculator, test) = Render(u, slice, java, files); }
            catch (InvalidOperationException e)
            {
                rejections.Add(new { programId = slice.ProgramId, paragraph = slice.Paragraph, reason = e.Message, disposition = "retained-faithful-core" });
                ledger.Skip($"{slice.ProgramId}/{slice.Paragraph}", $"{slice.ProgramId}/{slice.Paragraph}: certified capability was not rendered; faithful core retained");
                continue;
            }
            var pkg = JavaNames.Package(slice.Package);
            files.Set($"src/main/java/com/mlcpu/capabilities/{pkg}/{slice.Capability}Calculator.java", calculator);
            files.Set($"src/test/java/com/mlcpu/capabilities/{pkg}/{slice.Capability}DifferentialTest.java", test);
            hooks++;
            if (slice.RoutingStage == CalculationRoutingStage.ShadowReady)
            {
                var shadow = Shadow(u, slice, java, files);
                if (shadow is null) { ledger.Skip($"{slice.ProgramId}/{slice.Paragraph}", $"{slice.ProgramId}/{slice.Paragraph}: generated paragraph method not found"); continue; }
                files.Set(progPath, shadow);
                routed++;
            }
            ledger.Applied.Add($"{slice.ProgramId}/{slice.Paragraph}");
        }
        if (rejections.Count > 0) files.Set("calculation-projection-rejections.json", ProjectionJson.Write(rejections));
        ledger.Facts["hooks"] = hooks;
        ledger.Facts["routed"] = routed;
        return ledger;
    }

    sealed record Port(string Cobol, string Domain, string Java, DataItem Item, bool Numeric);

    static (string Calculator, string Test) Render(ProgramUnit u, CertifiedCalculationSlice slice, string java, ProjectFiles files)
    {
        var para = u.Model.Paragraphs.FirstOrDefault(p => string.Equals(p.Name, slice.Paragraph, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Approved calculation paragraph {slice.Paragraph} was not found in the semantic tree");
        var verbs = Flatten(para.Statements).Select(s => s.Verb).Distinct().ToList();
        var open = verbs.Where(v => !ClosedVerbs.Contains(v)).ToList();
        if (open.Count > 0 || Flatten(para.Statements).Any(s => s.Frontier is not null))
            throw new InvalidOperationException($"{slice.ProgramId}/{slice.Paragraph} is no longer a closed calculation ({string.Join(", ", open.DefaultIfEmpty("frontier"))})");
        var fp = Fingerprint(para);
        if (fp != slice.SemanticFingerprint)
            throw new InvalidOperationException($"Approved calculation semantics drifted for {slice.ProgramId}/{slice.Paragraph}: expected {slice.SemanticFingerprint}, actual {fp}");
        var plan = u.Plan.Paragraphs.First(p => string.Equals(p.Paragraph, slice.Paragraph, StringComparison.OrdinalIgnoreCase));
        var outputs = slice.Outputs.Select(o => o.Cobol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inputs = slice.Inputs.Select(i => i.Cobol).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (plan.Writes.Any(w => !outputs.Contains(w)))
            throw new InvalidOperationException($"Guarded calculation {slice.ProgramId}/{slice.Paragraph} may assign only approved outputs (assigns {string.Join(", ", plan.Writes.Where(w => !outputs.Contains(w)))})");
        if (plan.Reads.Any(r => !inputs.Contains(r) && !outputs.Contains(r)))
            throw new InvalidOperationException($"Approved calculation contract drifted for {slice.ProgramId}/{slice.Paragraph} (reads {string.Join(", ", plan.Reads.Where(r => !inputs.Contains(r) && !outputs.Contains(r)))})");

        var stateText = files.Get($"src/main/java/com/mlcpu/core/State_{u.Pid}.java") ?? java;
        var javaOf = FieldMapRx().Matches(stateText).ToDictionary(m => m.Groups["c"].Value, m => m.Groups["j"].Value, StringComparer.OrdinalIgnoreCase);
        Port PortOf(CalculationField f)
        {
            var it = u.Model.Items.FirstOrDefault(i => i.Level != 88 && string.Equals(i.Name, f.Cobol, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Approved calculation contract drifted for {slice.ProgramId}/{slice.Paragraph} ({f.Cobol} not declared)");
            if (!it.IsElementary || it.Dims.Count > 0 || it.Category is not (Category.Numeric or Category.Alphanumeric or Category.Alphabetic) || it.Usage is Usage.Comp1 or Usage.Comp2)
                throw new InvalidOperationException($"Unsupported certified calculation field type {it.Category}/{it.Usage} ({it.Name})");
            if (!javaOf.TryGetValue(it.Name, out var j))
                throw new InvalidOperationException($"Approved calculation contract drifted for {slice.ProgramId}/{slice.Paragraph} ({f.Cobol} has no generated field)");
            return new Port(it.Name, JavaNames.Member(f.DomainName), j, it, it.Category == Category.Numeric);
        }
        var ins = slice.Inputs.Select(PortOf).ToList();
        var outs = slice.Outputs.Select(PortOf).ToList();
        var seeded = ins.Concat(outs.Where(o => !ins.Any(i => i.Cobol == o.Cobol))).ToList();

        var method = DomainClusterAnalyzer.JavaMethod(java, para.Name) ?? throw new InvalidOperationException($"{slice.ProgramId}/{slice.Paragraph}: generated paragraph method not found");
        var m = JavaText.Member(java, $"private void {method}() {{") ?? throw new InvalidOperationException($"{slice.ProgramId}/{slice.Paragraph}: generated paragraph method not found");
        if (m.End < 0) throw new InvalidOperationException($"{slice.ProgramId}/{slice.Paragraph}: generated paragraph method is unbalanced");
        var body = java[(m.BodyOpen + 1)..m.BodyClose];
        foreach (var bad in new[] { "perform(", "_goto", "exitProgram(", "CobRt.frontier(", "CobRt.callProg" })
            if (body.Contains(bad, StringComparison.Ordinal))
                throw new InvalidOperationException($"{slice.ProgramId}/{slice.Paragraph} unsupported control flow: {bad.TrimEnd('(')}");
        var referenced = Regex.Matches(body, @"\b[A-Za-z_]\w*\b").Select(x => x.Value).ToHashSet(StringComparer.Ordinal);
        var allFields = javaOf.Values.ToHashSet(StringComparer.Ordinal);
        var stray = referenced.Where(r => allFields.Contains(r) && !seeded.Any(p => p.Java == r)).ToList();
        if (stray.Count > 0)
            throw new InvalidOperationException($"Approved calculation contract drifted for {slice.ProgramId}/{slice.Paragraph} (uses {string.Join(", ", stray)})");
        var declared = ConstRx().Matches(stateText + "\n" + java).GroupBy(c => c.Groups["n"].Value).ToDictionary(g => g.Key, g => g.First().Groups["decl"].Value);
        var needed = new HashSet<string>(referenced.Where(declared.ContainsKey), StringComparer.Ordinal);
        for (var frontier = needed.ToList(); frontier.Count > 0;)
            frontier = frontier.SelectMany(n => Regex.Matches(declared[n], @"\b[A-Za-z_]\w*\b").Select(x => x.Value))
                .Where(x => declared.ContainsKey(x) && needed.Add(x)).ToList();
        var constants = declared.Where(kv => needed.Contains(kv.Key)).Select(kv => kv.Value).ToList();

        var pkg = JavaNames.Package(slice.Package);
        var cap = slice.Capability;
        var sb = new StringBuilder($$"""
            package com.mlcpu.capabilities.{{pkg}};

            import com.mlcpu.rt.*;
            import java.math.BigDecimal;

            /**
             * Certified calculation {{slice.ProgramId}}/{{slice.Paragraph}} (projection 6, {{slice.RoutingStage}}).
             * Evidence: {{slice.Evidence}}. Reviewed by {{slice.ReviewedBy}} on {{slice.ReviewedAt}}.
             * The body is the generated paragraph, verbatim, over this class's own storage (fingerprint {{slice.SemanticFingerprint[..16]}}).
             */
            public final class {{cap}}Calculator {
                public record Input({{string.Join(", ", seeded.Select(p => $"{(p.Numeric ? "BigDecimal" : "String")} {p.Domain}"))}}) {}

                public record Result({{string.Join(", ", outs.Select(p => $"{(p.Numeric ? "BigDecimal" : "String")} {p.Domain}"))}}) {}


            """);
        foreach (var c in constants) sb.Append($"    private static final {c}\n");
        foreach (var p in seeded) sb.Append($"    private final CobField {p.Java} = {CompositionProjection.Factory(p.Item, 0, $"new Area({p.Item.Length})")};\n");
        sb.Append("\n    public synchronized Result calculate(Input in) {\n");
        foreach (var p in seeded)
            sb.Append(p.Numeric ? $"        CobRt.move(new NumLit(in.{p.Domain}()), {p.Java});\n" : $"        CobRt.move(in.{p.Domain}(), {p.Java});\n");
        sb.Append(body.TrimEnd()).Append('\n');
        sb.Append($"        return new Result({string.Join(", ", outs.Select(p => p.Numeric ? $"{p.Java}.dec()" : $"{p.Java}.str()"))});\n    }}\n}}\n");

        var vectorRows = slice.TestVectors.Select(v =>
        {
            var map = new List<string>();
            for (int k = 0; k < ins.Count && k < v.Inputs.Count; k++) map.Add($"Map.entry({JavaText.Lit(ins[k].Cobol)}, {JavaText.Lit(v.Inputs[k])})");
            foreach (var o in outs.Where(o => !ins.Any(i => i.Cobol == o.Cobol))) map.Add($"Map.entry({JavaText.Lit(o.Cobol)}, {JavaText.Lit(o.Numeric ? "0" : "")})");
            return $"                Arguments.of({JavaText.Lit(v.Name)}, Map.<String, String>ofEntries({string.Join(", ", map)}))";
        });
        var inputArgs = string.Join(", ", seeded.Select(p => p.Numeric ? $"new BigDecimal(seed.get({JavaText.Lit(p.Cobol)}))" : $"seed.get({JavaText.Lit(p.Cobol)})"));
        var asserts = string.Join("\n", outs.Select(p =>
            $"        assertEquals(baseline.get({JavaText.Lit(p.Cobol)}), {(p.Numeric ? $"r.{p.Domain}().toPlainString()" : $"r.{p.Domain}()")}, name + \": {p.Cobol}\");"));
        var test = $$"""
            package com.mlcpu.capabilities.{{pkg}};

            import static org.junit.jupiter.api.Assertions.assertEquals;

            import com.mlcpu.rt.ProgramState;
            import java.math.BigDecimal;
            import java.util.Map;
            import java.util.stream.Stream;
            import org.junit.jupiter.params.ParameterizedTest;
            import org.junit.jupiter.params.provider.Arguments;
            import org.junit.jupiter.params.provider.MethodSource;

            /** {{cap}}Calculator against the frozen Baseline_{{u.Pid}} paragraph {{slice.Paragraph}}, over the certified vectors. */
            class {{cap}}DifferentialTest {
                static Stream<Arguments> vectors() {
                    return Stream.of(
            {{string.Join(",\n", vectorRows)}}
                    );
                }

                @ParameterizedTest(name = "{0}")
                @MethodSource("vectors")
                void calculatorMatchesBaseline(String name, Map<String, String> seed) {
                    ProgramState baseline = new com.mlcpu.core.Baseline_{{u.Pid}}().runParagraph({{JavaText.Lit(slice.Paragraph)}}, seed);
                    assertEquals("COMPLETED", baseline.get("@OUTCOME"), name);
                    {{cap}}Calculator.Result r = new {{cap}}Calculator().calculate(new {{cap}}Calculator.Input({{inputArgs}}));
            {{asserts}}
                }
            }

            """;
        return (sb.ToString(), test);
    }

    /// <summary>ShadowReady: the paragraph runs the calculator, then the faithful body, and throws when they disagree.</summary>
    static string? Shadow(ProgramUnit u, CertifiedCalculationSlice slice, string java, ProjectFiles files)
    {
        var method = DomainClusterAnalyzer.JavaMethod(java, slice.Paragraph);
        if (method is null) return null;
        var m = JavaText.Member(java, $"private void {method}() {{");
        if (m is null || m.Value.End < 0) return null;
        var stateText = files.Get($"src/main/java/com/mlcpu/core/State_{u.Pid}.java") ?? java;
        var javaOf = FieldMapRx().Matches(stateText).ToDictionary(x => x.Groups["c"].Value, x => x.Groups["j"].Value, StringComparer.OrdinalIgnoreCase);
        var numeric = u.Model.Items.Where(i => i.Level != 88).GroupBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First().Category == Category.Numeric, StringComparer.OrdinalIgnoreCase);
        var ins = slice.Inputs.Concat(slice.Outputs.Where(o => !slice.Inputs.Any(i => i.Cobol == o.Cobol))).ToList();
        var calc = $"com.mlcpu.capabilities.{JavaNames.Package(slice.Package)}.{slice.Capability}Calculator";
        var field = "_calc" + slice.Capability;
        string Get(string cobol) => numeric[cobol] ? $"{javaOf[cobol]}.dec()" : $"{javaOf[cobol]}.str()";
        var check = string.Join("\n", slice.Outputs.Select(o => numeric[o.Cobol]
            ? $"        if (_r.{JavaNames.Member(o.DomainName)}().compareTo({javaOf[o.Cobol]}.dec()) != 0) throw new IllegalStateException(\"calculation drift in {slice.Paragraph}: {o.Cobol}\");"
            : $"        if (!_r.{JavaNames.Member(o.DomainName)}().equals({javaOf[o.Cobol]}.str())) throw new IllegalStateException(\"calculation drift in {slice.Paragraph}: {o.Cobol}\");"));
        var body = java[(m.Value.BodyOpen + 1)..m.Value.BodyClose];
        var replacement = $"    private void {method}() {{\n        {calc}.Result _r = {field}.calculate(new {calc}.Input({string.Join(", ", ins.Select(i => Get(i.Cobol)))}));\n" +
                          $"        {method}_core();\n{check}\n    }}\n\n    private void {method}_core() {{{body}}}\n\n    private final {calc} {field} = new {calc}();\n";
        return java[..m.Value.Start] + replacement + java[m.Value.End..];
    }

    static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null) foreach (var b in s.Blocks.Values) foreach (var x in Flatten(b)) yield return x;
        }
    }
}
