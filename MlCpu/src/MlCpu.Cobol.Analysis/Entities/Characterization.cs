using System.Globalization;
using System.Numerics;
using System.Text;

namespace MlCpu.Cobol.Analysis.Entities;

public sealed record CharacterizationVector(string Paragraph, string Seed, IReadOnlyList<(string Cobol, string Value)> Inputs);

/// <summary>
/// Characterization seeds (§8.12). Targets are reachable paragraphs whose PERFORM closure is free of I/O, SQL, CICS and
/// CALL. Inputs are the closure's upward-exposed reads that the ProgramState boundary can seed (elementary, uniquely
/// named, not in a table). Values come from the PICTURE; up to 20 reach seeds set an input to a literal it is compared
/// with, so both branches are driven. A path it cannot drive is simply not seeded — never guessed.
/// An input used as a subscript is seeded inside its table's OCCURS bound (1 to n): a PIC-derived 0 or 9999 would only
/// test storage outside the table, which COBOL leaves undefined.
/// </summary>
public static class CharacterizationSeedBuilder
{
    public const int MaxReachSeeds = 20;
    static readonly string[] PicSeeds = ["zeros", "ones", "boundary-high", "boundary-low", "smallest-unit", "distinct-markers"];

    public static List<CharacterizationVector> Build(CobolProgram program, ProgramGraph graph, PartitionPlan plan)
    {
        var seedable = Seedable(program);
        var bounds = SubscriptBounds(program, seedable);
        var paras = program.Paragraphs.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var result = new List<CharacterizationVector>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        void Add(CharacterizationVector v)
        {
            if (seen.Add(v.Paragraph + "\u0001" + string.Join("\u0001", v.Inputs.Select(i => i.Cobol + "=" + i.Value)))) result.Add(v);
        }
        foreach (var pp in plan.Paragraphs.Where(p => p.Reachable && p.PureClosure && p.Effect != nameof(ParagraphEffect.Empty) && p.Statements > 0))
        {
            var closure = Closure(pp.Paragraph, plan);
            var inputs = UpwardExposed(closure.Select(n => paras[n]), program)
                .Where(seedable.ContainsKey).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            int marker = 0;
            foreach (var seed in PicSeeds)
                Add(new CharacterizationVector(pp.Paragraph, seed, inputs.Select(f => (f, Value(seedable[f], seed, ++marker, bounds.GetValueOrDefault(f)))).ToList()));
            int reach = 0;
            foreach (var (field, literal) in Comparisons(closure.Select(n => paras[n]), program).Where(c => seedable.ContainsKey(c.Field)))
            {
                if (reach >= MaxReachSeeds) break;
                var it = seedable[field];
                var values = new List<string> { literal };
                if (it.Category == Category.Numeric && decimal.TryParse(literal, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    values.Add((d + Unit(it)).ToString(CultureInfo.InvariantCulture));
                foreach (var v in values)
                {
                    if (reach++ >= MaxReachSeeds) break;
                    var vec = inputs.Select(f => (f, f == field ? v : Value(seedable[f], "zeros", 0, bounds.GetValueOrDefault(f)))).ToList();
                    if (!inputs.Contains(field)) vec.Add((field, v));
                    Add(new CharacterizationVector(pp.Paragraph, $"reach-{field}={v}", vec));
                }
            }
        }
        return result;
    }

    static Dictionary<string, DataItem> Seedable(CobolProgram p)
    {
        var counts = p.Items.Where(i => i.Level != 88).GroupBy(i => i.Name).ToDictionary(g => g.Key, g => g.Count());
        return p.Items.Where(i => i.Level != 88 && !i.IsFiller && counts[i.Name] == 1 && i.Dims.Count == 0 && i.Children.All(c => c.Level == 88)
                && i.Category is Category.Numeric or Category.Alphanumeric or Category.Alphabetic && i.Usage is not (Usage.Comp1 or Usage.Comp2)
                && i.Section != DataSection.Linkage)
            .ToDictionary(i => i.Name, StringComparer.OrdinalIgnoreCase);
    }

    static List<string> Closure(string start, PartitionPlan plan)
    {
        var byName = plan.Paragraphs.ToDictionary(p => p.Paragraph, StringComparer.OrdinalIgnoreCase);
        var seen = new List<string>();
        var work = new Stack<string>([start]);
        while (work.Count > 0)
        {
            var x = work.Pop();
            if (seen.Contains(x, StringComparer.OrdinalIgnoreCase) || !byName.TryGetValue(x, out var pp)) continue;
            seen.Add(pp.Paragraph);
            foreach (var t in pp.Performs) work.Push(t);
        }
        return seen;
    }

    /// <summary>Fields read before any write in source order (an over-approximation is harmless: extra inputs are just seeded).</summary>
    static IEnumerable<string> UpwardExposed(IEnumerable<Paragraph> paragraphs, CobolProgram p)
    {
        var items = p.Items.ToDictionary(i => i.Id);
        var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var exposed = new List<string>();
        string[] defKeys = ["dst", "giving", "into", "rem", "var"];
        void Reads(Node? n)
        {
            if (n is null) return;
            if (n.K is "ref" or "c88" && n.Id is { } id && items.TryGetValue(id, out var it))
            {
                var name = it.Level == 88 && it.Parent is not null ? it.Parent.Name : it.Name;
                if (!written.Contains(name)) exposed.Add(name);
                foreach (var c in Leaves(it.Level == 88 && it.Parent is not null ? it.Parent : it)) if (!written.Contains(c)) exposed.Add(c);
            }
            Reads(n.L); Reads(n.R); Reads(n.Start); Reads(n.Len);
            foreach (var a in n.Args ?? []) Reads(a);
        }
        void Writes(Node? n)
        {
            if (n?.Id is { } id && items.TryGetValue(id, out var it) && n.Args is null && n.Start is null) { written.Add(it.Name); foreach (var c in Leaves(it)) written.Add(c); }
        }
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                foreach (var (k, v) in s.N ?? []) if (!defKeys.Contains(k)) Reads(v);
                foreach (var (k, v) in s.L ?? []) if (!defKeys.Contains(k) || s.Verb is "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE") foreach (var x in v) Reads(x);
                if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Walk(b);
                foreach (var (k, v) in s.N ?? []) if (defKeys.Contains(k)) Writes(v);
                foreach (var (k, v) in s.L ?? []) if (defKeys.Contains(k)) foreach (var x in v) Writes(x);
            }
        }
        foreach (var para in paragraphs) Walk(para.Statements);
        return exposed;
    }

    static IEnumerable<string> Leaves(DataItem it)
    {
        foreach (var c in it.Children.Where(c => c.Level != 88))
        {
            if (c.Children.Any(x => x.Level != 88)) foreach (var l in Leaves(c)) yield return l;
            else if (!c.IsFiller) yield return c.Name;
        }
    }

    static IEnumerable<(string Field, string Literal)> Comparisons(IEnumerable<Paragraph> paragraphs, CobolProgram p)
    {
        var items = p.Items.ToDictionary(i => i.Id);
        var found = new List<(string, string)>();
        void Cond(Node? n)
        {
            if (n is null) return;
            if (n.K == "rel" && n.L?.K == "ref" && n.R?.K is "num" or "str" && n.L.Args is null && n.L.Start is null && items.TryGetValue(n.L.Id!.Value, out var it))
                found.Add((it.Name, n.R.V!));
            if (n.K == "c88" && n.Id is { } cid && items.TryGetValue(cid, out var c88) && c88.Parent is not null && c88.Values.Count > 0 && c88.Values[0].High is null)
                found.Add((c88.Parent.Name, c88.Values[0].Low.Text));
            Cond(n.L); Cond(n.R);
        }
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                Cond(s.Get("cond")); Cond(s.Get("until"));
                if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Walk(b);
            }
        }
        foreach (var para in paragraphs) Walk(para.Statements);
        return found.Distinct();
    }

    /// <summary>
    /// Items used directly as a subscript, with the smallest OCCURS count of the dimensions they index. Only plain
    /// references count (WS-I, not WS-I + 1); an item that also indexes a larger table keeps the smaller bound.
    /// </summary>
    static Dictionary<string, int> SubscriptBounds(CobolProgram p, Dictionary<string, DataItem> seedable)
    {
        var items = p.Items.ToDictionary(i => i.Id);
        var bounds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void Visit(Node? n)
        {
            if (n is null) return;
            if (n.K == "ref" && n.Args is { Count: > 0 } subs && n.Id is { } id && items.TryGetValue(id, out var table))
                for (int k = 0; k < subs.Count && k < table.Dims.Count; k++)
                    if (subs[k] is { K: "ref", Args: null, Start: null, Id: { } sid } && items.TryGetValue(sid, out var sub)
                        && seedable.ContainsKey(sub.Name) && table.Dims[k].Count > 0)
                        bounds[sub.Name] = bounds.TryGetValue(sub.Name, out var b) ? Math.Min(b, table.Dims[k].Count) : table.Dims[k].Count;
            Visit(n.L); Visit(n.R); Visit(n.Start); Visit(n.Len);
            foreach (var a in n.Args ?? []) Visit(a);
        }
        void Walk(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                foreach (var (_, v) in s.N ?? []) Visit(v);
                foreach (var (_, v) in s.L ?? []) foreach (var x in v) Visit(x);
                if (s.Blocks is not null) foreach (var b in s.Blocks.Values) Walk(b);
            }
        }
        foreach (var para in p.Paragraphs) Walk(para.Statements);
        return bounds;
    }

    static decimal Unit(DataItem it) => it.Scale > 0 ? (decimal)Math.Pow(10, -it.Scale) : 1m;

    static string Value(DataItem it, string seed, int marker, int bound = 0)
    {
        if (it.Category == Category.Numeric && bound > 0)
            return (seed switch
            {
                "boundary-high" => bound,
                "distinct-markers" => marker % bound + 1,
                _ => 1
            }).ToString(CultureInfo.InvariantCulture);
        if (it.Category == Category.Numeric)
        {
            int intDigits = Math.Max(it.Digits - Math.Max(it.Scale, 0), 0);
            string max = (intDigits > 0 ? new string('9', intDigits) : "0") + (it.Scale > 0 ? "." + new string('9', it.Scale) : "");
            return seed switch
            {
                "zeros" => "0",
                "ones" => "1",
                "boundary-high" => max,
                "boundary-low" => it.Signed ? "-" + max : "0",
                "smallest-unit" => Unit(it).ToString(CultureInfo.InvariantCulture),
                _ => (BigInteger.Pow(10, Math.Max(intDigits, 1)) > marker ? marker : marker % 10).ToString(CultureInfo.InvariantCulture)
            };
        }
        int len = Math.Max(it.Length, 1);
        return seed switch
        {
            "zeros" => new string('0', len),
            "ones" => "1",
            "boundary-high" => new string('Z', len),
            "boundary-low" => "",
            "smallest-unit" => "A",
            _ => ("M" + marker).Length > len ? ("M" + marker)[..len] : "M" + marker
        };
    }
}

/// <summary>Renders &lt;PID&gt;CharacterizationTest: Baseline_&lt;PID&gt; and Prog_&lt;PID&gt; run from identical seeds and must agree; results are also pinned in a golden file.</summary>
public static class JavaCharacterizationRenderer
{
    public static string RenderTest(string programId, IReadOnlyList<CharacterizationVector> vectors, string clockEpoch)
    {
        var cls = Sanitize(programId);
        var sb = new StringBuilder();
        sb.Append($$"""
            package com.mlcpu.core;

            import static org.junit.jupiter.api.Assertions.assertEquals;

            import com.mlcpu.rt.Characterization;
            import com.mlcpu.rt.CobClock;
            import com.mlcpu.rt.ProgramState;
            import java.util.Map;
            import java.util.stream.Stream;
            import org.junit.jupiter.api.Assumptions;
            import org.junit.jupiter.api.BeforeAll;
            import org.junit.jupiter.params.ParameterizedTest;
            import org.junit.jupiter.params.provider.Arguments;
            import org.junit.jupiter.params.provider.MethodSource;

            /** Generated characterization test: {{vectors.Count}} seeds over {{vectors.Select(v => v.Paragraph).Distinct().Count()}} side-effect-free paragraphs of {{programId}}. */
            class {{cls}}CharacterizationTest {

                @BeforeAll
                static void pinClock() {
                    CobClock.configure("FIXED", "{{clockEpoch}}");
                }


            """);
        // One seeds() method while it stays well under the JVM's 64 KB of bytecode per method (about 14 bytes per
        // Map.entry); beyond that the seeds are split over seeds0(), seeds1(), ... J3S's FSJBDEC, with the big-value
        // store, has 1,371 seeds and 27,380 entries: one method was "code too large", and no test of the estate compiled.
        var chunks = new List<List<CharacterizationVector>> { new() };
        int inChunk = 0;
        foreach (var v in vectors)
        {
            if (inChunk > 0 && inChunk + v.Inputs.Count > SeedEntriesPerMethod)
            {
                chunks.Add([]);
                inChunk = 0;
            }
            chunks[^1].Add(v);
            inChunk += v.Inputs.Count;
        }
        if (chunks.Count == 1) Seeds(sb, "seeds", vectors, false);
        else
        {
            sb.Append("    static Stream<Arguments> seeds() {\n        return Stream.of(")
              .Append(string.Join(", ", chunks.Select((_, k) => $"seeds{k}()"))).Append(").flatMap(s -> s);\n    }\n\n");
            for (int k = 0; k < chunks.Count; k++) Seeds(sb, $"seeds{k}", chunks[k], true);
        }
        sb.Append($$"""
                @ParameterizedTest(name = "{0} [{1}]")
                @MethodSource("seeds")
                void normalizedMatchesBaseline(String paragraph, String seed, Map<String, String> inputs) {
                    ProgramState expected = new Baseline_{{cls}}().runParagraph(paragraph, inputs);
                    ProgramState actual = new Prog_{{cls}}().runParagraph(paragraph, inputs);
                    Assumptions.assumeFalse("LOOP_LIMIT".equals(expected.get("@OUTCOME")), "inconclusive: loop budget exceeded");
                    assertEquals(expected.fields(), actual.fields(), () -> paragraph + " [" + seed + "] differs from Baseline_{{cls}}");
                    Characterization.pin("{{programId}}", paragraph + " [" + seed + "]", actual);
                }
            }

            """);
        return sb.ToString();
    }

    /// <summary>Map entries per seeds method: about 28 KB of bytecode, under the JVM's 64 KB limit with room for the seeds' own code.</summary>
    const int SeedEntriesPerMethod = 2000;

    static void Seeds(StringBuilder sb, string name, IReadOnlyList<CharacterizationVector> vectors, bool helper)
    {
        sb.Append($"    {(helper ? "private " : "")}static Stream<Arguments> {name}() {{\n        return Stream.of(\n");
        for (int i = 0; i < vectors.Count; i++)
        {
            var v = vectors[i];
            var entries = string.Join(", ", v.Inputs.Select(x => $"Map.entry({Lit(x.Cobol)}, {Lit(x.Value)})"));
            sb.Append($"                Arguments.of({Lit(v.Paragraph)}, {Lit(v.Seed)}, Map.<String, String>ofEntries({entries})){(i + 1 < vectors.Count ? "," : "")}\n");
        }
        sb.Append("        );\n    }\n\n");
    }

    static string Sanitize(string id) => new(id.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray());

    static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c < 0x20 || c > 0x7E) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }
}
