using System.Text;

namespace MlCpu.Cobol.Analysis.Projections;

/// <summary>
/// Projection 2 — composition (§8): typed entities for the records a program reads and writes (READ INTO / WRITE FROM
/// targets, else FD records), byte codecs built from the same CobField views the core uses, and a typed
/// command/result/service boundary per program that runs the faithful core.
/// CompositionGate: a record is mapped only when every leaf is a plain alphanumeric or numeric item (no OCCURS,
/// REDEFINES, edited or floating items). FILLER is not carried; encode writes spaces there.
/// </summary>
public static class CompositionProjection
{
    public const string Pass = "composition";

    public sealed record EntityField(string Cobol, string Java, string JavaType, string Factory, bool Numeric, int Offset, int Length, int Digits = 0, int Scale = 0);

    public sealed record Entity(string Type, string Record, string? Copybook, int Length, IReadOnlyList<EntityField> Fields, IReadOnlyList<string> Programs);

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units)
    {
        var ledger = new ProjectionLedger(Pass);
        var entities = new Dictionary<string, Entity>(StringComparer.Ordinal);
        var rows = new List<object>();
        foreach (var u in units)
        {
            var corePath = $"src/main/java/com/mlcpu/core/{u.ClassName}.java";
            if (!files.Exists(corePath)) { ledger.Skip(u.ProgramId, "generated program not found"); rows.Add(new { program = u.ProgramId, status = "skipped", reason = "generated program not found" }); continue; }
            var mapped = new List<string>();
            foreach (var rec in Candidates(u.Model))
            {
                var reason = Gate(rec);
                if (reason is not null) { ledger.Skip($"{u.ProgramId}/{rec.Name}", reason); continue; }
                var e = Shape(rec);
                var key = e.Type;
                if (entities.TryGetValue(key, out var existing))
                {
                    if (SameShape(existing, e)) { entities[key] = existing with { Programs = existing.Programs.Append(u.ProgramId).Distinct().ToList() }; mapped.Add(key); continue; }
                    key = e.Type + "_" + ProjectionJson.Sha256(string.Join("|", e.Fields.Select(f => f.Factory)))[..8];
                    e = e with { Type = key };
                    if (entities.ContainsKey(key)) { mapped.Add(key); continue; }
                }
                entities[key] = e with { Programs = [u.ProgramId] };
                mapped.Add(key);
            }
            files.Set($"src/main/java/com/mlcpu/usecases/{u.Pid}Command.java", CommandSource(u));
            files.Set($"src/main/java/com/mlcpu/usecases/{u.Pid}Result.java", ResultSource(u));
            files.Set($"src/main/java/com/mlcpu/services/{u.Pid}Service.java", ServiceSource(u));
            ledger.Applied.Add(u.ProgramId);
            rows.Add(new { program = u.ProgramId, status = "composed", reason = $"command/result/service; entities [{string.Join(", ", mapped.Distinct())}]" });
        }
        foreach (var e in entities.Values.OrderBy(e => e.Type, StringComparer.Ordinal))
        {
            files.Set($"src/main/java/com/mlcpu/entities/{e.Type}.java", EntitySource(e));
            files.Set($"src/main/java/com/mlcpu/mapping/{e.Type}Codec.java", CodecSource(e));
            files.Set($"src/test/java/com/mlcpu/mapping/{e.Type}CodecTest.java", CodecTestSource(e));
        }
        ledger.Facts["entities"] = entities.Values.OrderBy(e => e.Type, StringComparer.Ordinal)
            .Select(e => new { type = e.Type, record = e.Record, copybook = e.Copybook, length = e.Length, fields = e.Fields.Count, programs = e.Programs }).ToList();
        files.Set("composition-ledger.json", ProjectionJson.Write(rows));
        return ledger;
    }

    /// <summary>READ INTO targets and WRITE/REWRITE FROM sources; FD records of files used without INTO/FROM.</summary>
    static List<DataItem> Candidates(CobolProgram m)
    {
        var byId = m.Items.ToDictionary(i => i.Id);
        var shapes = new List<DataItem>();
        var viaWs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(DataItem it) { if (!it.IsElementary && !shapes.Contains(it)) shapes.Add(it); }
        foreach (var s in Walk(m.Paragraphs.SelectMany(p => p.Statements)))
        {
            if (s.Verb == "READ" && s.Get("into") is { Id: { } i } && byId.TryGetValue(i, out var into)) { Add(into); viaWs.Add(s.Str("file") ?? ""); }
            if (s.Verb is "WRITE" or "REWRITE" && s.Get("from") is { K: "ref", Id: { } f } && byId.TryGetValue(f, out var from))
            {
                Add(from);
                if (s.Get("record")?.Id is { } r && byId.TryGetValue(r, out var rec) && rec.Record.FileName is { } fn) viaWs.Add(fn);
            }
        }
        foreach (var fd in m.Files.Where(f => !f.IsSort && !viaWs.Contains(f.Name)))
            foreach (var r in fd.Records) Add(r);
        return shapes;
    }

    static IEnumerable<Stmt> Walk(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null) foreach (var b in s.Blocks.Values) foreach (var x in Walk(b)) yield return x;
        }
    }

    static IEnumerable<DataItem> Descendants(DataItem it) => it.Children.Where(c => c.Level != 88).SelectMany(c => new[] { c }.Concat(Descendants(c)));

    public static string? Gate(DataItem rec)
    {
        foreach (var d in Descendants(rec))
        {
            if (d.Occurs > 0) return $"OCCURS table {d.Name} is not mapped to entity components";
            if (d.Redefines is not null) return $"REDEFINES overlay {d.Name} has no single typed shape";
            if (!d.IsElementary) continue;
            if (d.Category is not (Category.Alphanumeric or Category.Alphabetic or Category.Numeric)) return $"leaf {d.Name} is {d.Category}";
            if (d.Usage is Usage.Comp1 or Usage.Comp2 or Usage.Pointer or Usage.Index) return $"leaf {d.Name} has usage {d.Usage}";
        }
        return Descendants(rec).Any(d => d.IsElementary && d.Name != "FILLER") ? null : "record has no named leaves";
    }

    static Entity Shape(DataItem rec)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var fields = new List<EntityField>();
        foreach (var d in Descendants(rec).Where(d => d.IsElementary))
        {
            var off = d.Offset - rec.Offset;
            var factory = Factory(d, off);
            if (d.Name == "FILLER") { fields.Add(new EntityField("FILLER", "", "", factory, false, off, d.Length)); continue; }
            var numeric = d.Category == Category.Numeric;
            var type = !numeric ? "String" : d.Scale > 0 || d.Digits > 18 ? "BigDecimal" : "long";
            fields.Add(new EntityField(d.Name, JavaNames.Unique(JavaNames.Member(d.Name), used), type, factory, numeric, off, d.Length, d.Digits, d.Scale));
        }
        var name = rec.Name.StartsWith("FD-", StringComparison.OrdinalIgnoreCase) ? rec.Name[3..] : rec.Name;
        return new Entity(JavaNames.Type(name), rec.Name, rec.Origin.Length > 0 ? rec.Origin : null, rec.Length, fields, []);
    }

    static bool SameShape(Entity a, Entity b) => a.Length == b.Length && a.Fields.Select(f => f.Factory).SequenceEqual(b.Fields.Select(f => f.Factory));

    /// <summary>The CobField factory call the emitter uses for this leaf, over the codec's own area.</summary>
    public static string Factory(DataItem d, int off, string area = "area")
    {
        if (d.Category == Category.Numeric)
        {
            var use = d.Usage switch { Usage.Comp3 => "PACKED", Usage.Binary => "BINARY", Usage.Comp5 => "COMP5", _ => "DISPLAY" };
            return $"CobField.num({JavaText.Lit(d.Name)}, CobField.Use.{use}, {area}, {off}, {d.Length}, {d.Digits}, {d.Scale}, {B(d.Signed)}, {B(d.SignSeparate)}, {B(d.SignLeading)}, null, null)";
        }
        return $"CobField.alnum({JavaText.Lit(d.Name)}, {area}, {off}, {d.Length}, {B(d.Justified)}, null, null)";
    }

    static string B(bool b) => b ? "true" : "false";

    static string EntitySource(Entity e)
    {
        var comps = e.Fields.Where(f => f.Java.Length > 0).Select(f => $"        {f.JavaType} {f.Java}");
        return $$"""
            package com.mlcpu.entities;

            import java.math.BigDecimal;

            /**
             * {{e.Record}}{{(e.Copybook is null ? "" : $" (copybook {e.Copybook})")}}, {{e.Length}} bytes; used by {{string.Join(", ", e.Programs)}}.
             * Components follow the record's leaf order; FILLER is not carried. Encode and decode with {@code {{e.Type}}Codec}.
             */
            public record {{e.Type}}(
            {{string.Join(",\n", comps)}}) {
            }

            """;
    }

    static string CodecSource(Entity e)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            package com.mlcpu.mapping;

            import com.mlcpu.entities.{{e.Type}};
            import com.mlcpu.rt.Area;
            import com.mlcpu.rt.CobField;
            import com.mlcpu.rt.CobRt;
            import com.mlcpu.rt.NumLit;
            import java.math.BigDecimal;

            /** Byte codec for {{e.Record}} ({{e.Length}} bytes), using the same field views as the converted programs. */
            public final class {{e.Type}}Codec {
                private {{e.Type}}Codec() {}

                public static final int LENGTH = {{e.Length}};

                private static final class View {
                    final Area area = new Area(LENGTH);

            """);
        int k = 0;
        var views = e.Fields.Select(f => (f, v: f.Java.Length > 0 ? f.Java : "filler" + k++)).ToList();
        foreach (var (f, v) in views) sb.Append($"        final CobField {v} = {f.Factory};\n");
        sb.Append($$"""
                }

                public static {{e.Type}} decode(byte[] record) {
                    if (record.length != LENGTH) throw new IllegalArgumentException("{{e.Type}} is " + LENGTH + " bytes, got " + record.length);
                    View v = new View();
                    System.arraycopy(record, 0, v.area.b, 0, LENGTH);
                    return new {{e.Type}}(

            """);
        sb.Append(string.Join(",\n", views.Where(x => x.f.Java.Length > 0).Select(x => "                " + (x.f.JavaType switch
        {
            "String" => $"v.{x.v}.str()",
            "long" => $"v.{x.v}.dec().longValueExact()",
            _ => $"v.{x.v}.dec()"
        }))));
        sb.Append($$"""
            );
                }

                public static byte[] encode({{e.Type}} r) {
                    View v = new View();
                    java.util.Arrays.fill(v.area.b, (byte) ' ');

            """);
        foreach (var (f, v) in views.Where(x => x.f.Java.Length > 0))
            sb.Append(f.JavaType switch
            {
                "String" => $"        CobRt.move(r.{f.Java}() == null ? \"\" : r.{f.Java}(), v.{v});\n",
                "long" => $"        CobRt.move(new NumLit(BigDecimal.valueOf(r.{f.Java}())), v.{v});\n",
                _ => $"        CobRt.move(new NumLit(r.{f.Java}() == null ? BigDecimal.ZERO : r.{f.Java}()), v.{v});\n"
            });
        sb.Append("        return v.area.b.clone();\n    }\n}\n");
        return sb.ToString();
    }

    static string CodecTestSource(Entity e)
    {
        var sample = e.Fields.Where(f => f.Java.Length > 0).Select((f, i) =>
        {
            var whole = f.Digits - f.Scale > 0 ? (i % 9 + 1).ToString() : "0";
            return f.JavaType switch
            {
                "String" => JavaText.Lit(new string((char)('A' + i % 26), Math.Min(f.Length, 3)).PadRight(f.Length)),
                "long" => whole + "L",
                _ => $"new BigDecimal(\"{whole}{(f.Scale > 0 ? "." + new string('0', f.Scale - 1) + "1" : "")}\")"
            };
        });
        return $$"""
            package com.mlcpu.mapping;

            import static org.junit.jupiter.api.Assertions.assertArrayEquals;
            import static org.junit.jupiter.api.Assertions.assertEquals;

            import com.mlcpu.entities.{{e.Type}};
            import java.math.BigDecimal;
            import org.junit.jupiter.api.Test;

            class {{e.Type}}CodecTest {
                @Test
                void encodeThenDecodeKeepsEveryComponent() {
                    {{e.Type}} r = new {{e.Type}}({{string.Join(", ", sample)}});
                    byte[] image = {{e.Type}}Codec.encode(r);
                    assertEquals({{e.Type}}Codec.LENGTH, image.length);
                    {{e.Type}} back = {{e.Type}}Codec.decode(image);
                    assertEquals(r, back);
                    assertArrayEquals(image, {{e.Type}}Codec.encode(back));
                }
            }

            """;
    }

    static string Dds(ProgramUnit u) => string.Join(", ", u.Model.Files.Where(f => !f.IsSort).Select(f => JavaText.Lit(MlCpuDd(f.Assign))));

    static string MlCpuDd(string assign)
    {
        var a = assign.Trim().Trim('\'', '"').ToUpperInvariant();
        var dash = a.LastIndexOf('-');
        return dash >= 0 ? a[(dash + 1)..] : a;
    }

    static string CommandSource(ProgramUnit u) => $$"""
        package com.mlcpu.usecases;

        import java.nio.file.Path;
        import java.util.List;
        import java.util.Map;

        /** Typed input of {{u.ProgramId}}: the datasets bound to its DDs, and the JCL PARM (null when the step has none). */
        public record {{u.Pid}}Command(Map<String, Path> datasets, String parm) {
            /** DD names the program assigns (SELECT ... ASSIGN TO). */
            public static final List<String> DDS = List.of({{Dds(u)}});

            public {{u.Pid}}Command {
                datasets = Map.copyOf(datasets);
            }
        }

        """;

    static string ResultSource(ProgramUnit u) => $$"""
        package com.mlcpu.usecases;

        import java.util.List;

        /** Typed outcome of {{u.ProgramId}}: RETURN-CODE, the DISPLAY lines, and COMPLETED or ABEND:<code>. */
        public record {{u.Pid}}Result(int returnCode, List<String> display, String outcome) {
        }

        """;

    static string ServiceSource(ProgramUnit u) => $$"""
        package com.mlcpu.services;

        import com.mlcpu.core.{{u.ClassName}};
        import com.mlcpu.rt.JobEngine;
        import com.mlcpu.usecases.{{u.Pid}}Command;
        import com.mlcpu.usecases.{{u.Pid}}Result;
        import org.springframework.stereotype.Service;

        /** Runs the faithful {{u.ProgramId}} core behind a typed boundary. Every assigned DD must be bound. */
        @Service
        public class {{u.Pid}}Service {
            public {{u.Pid}}Result execute({{u.Pid}}Command command) {
                for (String dd : {{u.Pid}}Command.DDS)
                    if (!command.datasets().containsKey(dd)) throw new IllegalArgumentException("{{u.ProgramId}}: DD " + dd + " is not bound");
                JobEngine.ProgramRun run = JobEngine.runProgram({{u.ClassName}}::new, command.datasets(), command.parm());
                return new {{u.Pid}}Result(run.returnCode(), run.display(), run.outcome());
            }
        }

        """;
}
