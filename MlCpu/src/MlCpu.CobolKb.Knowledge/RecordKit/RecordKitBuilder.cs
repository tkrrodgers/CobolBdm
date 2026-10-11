using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;

namespace MlCpu.CobolKb.Knowledge;

/// <param name="Program">The capability's program: the step's main program, or the CALLed subprogram.</param>
/// <param name="Host">The program the job step runs (the capability's program for a step; its caller for a subprogram).</param>
/// <param name="HostBindings">The step's DD bindings: the datasets the step reads and writes.</param>
/// <param name="FaithfulClass">The faithful translation's entry class (com.mlcpu.core.Prog_&lt;PID&gt;), for the kit's conformance test.</param>
public sealed record RecordKitRequest(string ProgramId, string Package, string Kind, CobolProgram Program, CobolProgram Host,
    IReadOnlyList<DdBinding> HostBindings, string FaithfulClass);

/// <param name="Files">Project files: Java sources under src/main/java and the conformance test under src/test/java.</param>
/// <param name="Summary">The kit as the implementation prompt shows it (record-kit.md).</param>
/// <param name="LayoutsJson">Every record layout of the step's datasets, with field names, for field-level feedback (record-layouts.json).</param>
public sealed record RecordKit(string Package, IReadOnlyList<(string Path, string Content)> Files, string Summary, string LayoutsJson,
    IReadOnlyList<string> Classes, IReadOnlyList<string> Notes);

/// <summary>
/// The record kit of a capability (pipeline v2): the parts of a modernized program that follow from the COBOL data division
/// alone, generated instead of being left to a model. For every record area the program does I/O with (FD records;
/// WORKING-STORAGE records used by READ INTO, WRITE/REWRITE FROM, a MOVE to or from an FD record, CALL USING; LINKAGE
/// records) a class over the record's bytes with the VALUE clauses applied and a typed accessor per item, storing with COBOL
/// MOVE rules (padding, truncation, sign, zoned / packed / binary, editing). Report lines are such records, so a line is
/// VALUE-initialised and formats its own edited fields. A class per FD carries the DD, lengths, keys and the WRITE … FROM
/// image; <c>Pic</c> carries the rules (a copy of RecordKit/Pic.java). <c>RecordKitTest</c> proves every class against
/// the faithful program's own storage: initial images, every setter and getter.
/// </summary>
public static class RecordKitBuilder
{
    public const string SubPackage = "records";
    const string Schema = "mlcpu.record-layouts.v1";

    static readonly HashSet<string> JavaReserved = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double",
        "else", "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface",
        "long", "native", "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp", "super", "switch",
        "synchronized", "this", "throw", "throws", "transient", "try", "void", "volatile", "while", "record", "var", "yield", "true", "false", "null"
    };

    /// <summary>Member names every record class already has.</summary>
    static readonly string[] ClassMembers = ["bytes", "copy", "moveFrom", "readFrom", "fill", "initialize", "wrap", "values", "toString", "equals",
        "hashCode", "getClass", "notify", "notifyAll", "wait", "clone", "finalize", "b"];

    // ------------------------------------------------------------------ model

    sealed record Root(DataItem Item, string Role, CobolProgram Program);

    sealed class Field
    {
        public required DataItem Item;
        public required string Kind;
        public int Offset;
        public List<(int Count, int Stride)> Dims = [];
        public string Cobol = "";
        public string Name = "";
        public string Java = "";
        public string? JavaType;
    }

    sealed class Shape
    {
        public required List<Root> Roots;
        public required List<Field> Fields;
        public string Class = "";
        public string Prefix = "";
        public bool Values;
        public bool Line;
        public Root Primary => Roots[0];
        public int Length => Primary.Item.Length;
    }

    sealed record FileClass(FileDecl Fd, string Class, CobolProgram Program, bool Print);

    // ------------------------------------------------------------------ entry point

    public static RecordKit Build(RecordKitRequest r)
    {
        var notes = new List<string>();
        var pkg = r.Package + "." + SubPackage;
        var shapes = Shapes(Roots(r.Program, r.Kind == "subprogram"), notes);
        var files = r.Program.Files.Where(f => !f.IsSort && f.Records.Count > 0).ToList();
        var used = new HashSet<string>(StringComparer.Ordinal) { "Pic", "RecordKitTest" };
        var fileClasses = new List<FileClass>();
        foreach (var f in files)
        {
            var name = Pascal(f.Name);
            if (!name.EndsWith("File", StringComparison.Ordinal)) name += "File";
            fileClasses.Add(new FileClass(f, Unique(name, used), r.Program, IsPrintFile(r.Program, f)));
        }
        foreach (var s in shapes) s.Class = Unique(Pascal(s.Primary.Item.Name), used);
        foreach (var s in shapes) s.Line = IsLine(r.Program, s, fileClasses);

        var dir = "src/main/java/" + pkg.Replace('.', '/');
        var outFiles = new List<(string, string)> { ($"{dir}/Pic.java", PicSource(pkg)) };
        foreach (var s in shapes) outFiles.Add(($"{dir}/{s.Class}.java", RecordSource(pkg, r.ProgramId, s, notes)));
        foreach (var f in fileClasses) outFiles.Add(($"{dir}/{f.Class}.java", FileSource(pkg, r.ProgramId, f, shapes)));
        outFiles.Add(($"src/test/java/{pkg.Replace('.', '/')}/RecordKitTest.java", TestSource(pkg, r, shapes)));
        var summary = Summary(pkg, r, shapes, fileClasses, notes);
        var layouts = Layouts(r, pkg, shapes);
        return new RecordKit(pkg, outFiles, summary, layouts, shapes.Select(s => s.Class).Concat(fileClasses.Select(f => f.Class)).ToList(), notes);
    }

    // ------------------------------------------------------------------ which record areas

    /// <summary>Record areas the program does I/O with: FD records, LINKAGE (USING) records, and the INTO/FROM/MOVE/CALL operands that are records.</summary>
    static List<Root> Roots(CobolProgram m, bool subprogram)
    {
        var roots = new List<Root>();
        var byId = m.Items.ToDictionary(i => i.Id);
        void Add(DataItem? it, string role)
        {
            if (it is null || it.Level == 88 || it.Category is Category.Condition) return;
            if (it.Level is not (1 or 77) && it.IsElementary) return;
            int own = it.Occurs > 0 ? 1 : 0;
            if (it.Dims.Count > own || it.Occurs > 0) return;
            if (roots.Any(x => x.Item == it)) return;
            roots.Add(new Root(it, role, m));
        }
        DataItem? Item(Node? n) => n is { K: "ref", Id: { } id } && n.Args is null && n.Start is null && byId.TryGetValue(id, out var it) ? it : null;
        bool FdRecord(DataItem? it) => it is not null && it.Section == DataSection.File && it.Level == 1;

        foreach (var f in m.Files.Where(f => !f.IsSort))
            foreach (var rec in f.Records) Add(rec, $"FD {f.Name}");
        foreach (var u in m.Using)
            Add(m.Items.FirstOrDefault(i => i.Section == DataSection.Linkage && i.Level is 1 or 77 && i.Name.Equals(u, StringComparison.OrdinalIgnoreCase)),
                subprogram ? "CALL argument (LINKAGE)" : "PARM (LINKAGE)");
        foreach (var s in DdBindingPlanner.Walk(m))
        {
            switch (s.Verb)
            {
                case "READ": Add(Item(s.Get("into")), "READ INTO"); break;
                case "RETURN": Add(Item(s.Get("into")), "RETURN INTO"); break;
                case "WRITE": case "REWRITE": case "RELEASE": Add(Item(s.Get("from")), s.Verb + " FROM"); break;
                case "MOVE":
                {
                    var src = Item(s.Get("src"));
                    var dsts = s.L?.GetValueOrDefault("dst")?.Select(Item).ToList() ?? [];
                    if (src is { Level: 1 or 77 } && dsts.Any(FdRecord)) Add(src, "MOVE to an FD record");
                    if (FdRecord(src)) foreach (var d in dsts.Where(d => d is { Level: 1 or 77 } && !FdRecord(d))) Add(d, "MOVE from an FD record");
                    break;
                }
                case "CALL":
                    // Language Environment services (CALL 'CEE3ABD' USING ABCODE TIMING) are the abend convention, not data the logic exchanges.
                    if (s.Get("target") is { K: "str" } t && t.V is { } tv && tv.StartsWith("CEE", StringComparison.OrdinalIgnoreCase)) break;
                    foreach (var a in s.L?.GetValueOrDefault("args") ?? [])
                        if (a.K == "arg" && Item(a.L) is { Level: 1 or 77 } it) Add(it, "CALL USING");
                    break;
            }
        }
        return roots;
    }

    static string Kind(DataItem it) => it.Category switch
    {
        Category.Group => "group",
        Category.Alphanumeric or Category.Alphabetic => "text",
        Category.AlphanumericEdited => "alnumEdited",
        Category.NumericEdited => "edited",
        Category.Numeric => it.Usage switch
        {
            Usage.Display => "zoned",
            Usage.Comp3 => "packed",
            Usage.Binary or Usage.Comp5 => "binary",
            _ => "unsupported"
        },
        _ => "unsupported"
    };

    static bool Numeric(string kind) => kind is "zoned" or "packed" or "binary" or "edited";

    static IEnumerable<DataItem> Tree(DataItem root)
    {
        yield return root;
        foreach (var c in root.Children.Where(c => c.Level != 88))
            foreach (var x in Tree(c)) yield return x;
    }

    static List<Field> FieldsOf(DataItem root)
    {
        int rootDims = root.Dims.Count;
        var list = new List<Field>();
        foreach (var it in Tree(root))
        {
            if (it.IsFiller) continue;
            if (it == root && root.Children.Count(c => c.Level != 88) > 0) continue;
            list.Add(new Field
            {
                Item = it, Kind = Kind(it), Offset = it.Offset - root.Offset, Dims = it.Dims.Skip(rootDims).ToList(), Cobol = it.Name
            });
        }
        return list;
    }

    /// <summary>Longest hyphen-terminated prefix shared by the root and every named item under it ("WS-CUST-" for WS-CUST-REC).</summary>
    static string Prefix(DataItem root)
    {
        var names = Tree(root).Where(i => !i.IsFiller).Select(i => i.Name).ToList();
        var first = root.Name;
        var best = "";
        for (int i = first.IndexOf('-'); i >= 0; i = first.IndexOf('-', i + 1))
        {
            var p = first[..(i + 1)];
            if (names.All(n => n.StartsWith(p, StringComparison.Ordinal) && n.Length > p.Length)) best = p;
            else break;
        }
        return best;
    }

    static bool HasValues(DataItem root) =>
        root.Section is DataSection.WorkingStorage or DataSection.LocalStorage && Tree(root).Any(i => i.Values.Count > 0 && !UnderRedefines(i, root));

    static string Signature(DataItem root, string prefix)
    {
        var sb = new StringBuilder($"{root.Length}|{HasValues(root)}");
        foreach (var it in Tree(root))
        {
            var name = it.IsFiller ? "FILLER" : it.Name.StartsWith(prefix, StringComparison.Ordinal) ? it.Name[prefix.Length..] : it.Name;
            sb.Append($";{name}:{it.Offset - root.Offset}:{it.Length}:{it.Category}:{it.Usage}:{it.Digits}:{it.Scale}:{it.Signed}:{it.SignSeparate}:{it.SignLeading}")
              .Append($":{it.Justified}:{it.BlankWhenZero}:{it.Pic}:{it.Occurs}:{it.Redefines is not null}:{it.ValueAll}:")
              .Append(string.Join(",", it.Values.Select(v => (v.Low.Quoted ? "'" : "") + v.Low.Text + (v.High is null ? "" : "~" + v.High.Text))));
            foreach (var c in it.Children.Where(c => c.Level == 88))
            {
                var cn = c.Name.StartsWith(prefix, StringComparison.Ordinal) ? c.Name[prefix.Length..] : c.Name;
                sb.Append($";88 {cn}:").Append(string.Join(",", c.Values.Select(v => (v.Low.Quoted ? "'" : "") + v.Low.Text + (v.High is null ? "" : "~" + v.High.Text))));
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// One class per distinct layout. Areas declared from the same copybook under different prefixes (COPY … REPLACING: CUST-REC,
    /// CSTOUT-REC, WS-CUST-REC) share a class whose accessors drop the prefix; any other layout keeps the COBOL names as declared.
    /// </summary>
    static List<Shape> Shapes(List<Root> roots, List<string> notes)
    {
        var shapes = new List<Shape>();
        foreach (var group in roots.GroupBy(r => Signature(r.Item, Prefix(r.Item))))
        {
            var members = group.ToList();
            var primary = members[0].Item;
            bool strip = members.Select(m => Signature(m.Item, "")).Distinct().Count() > 1;
            var prefix = strip ? Prefix(primary) : "";
            var fields = FieldsOf(primary);
            var used = new HashSet<string>(ClassMembers, StringComparer.Ordinal);
            foreach (var f in fields)
            {
                f.Name = prefix.Length > 0 && f.Cobol.StartsWith(prefix, StringComparison.Ordinal) ? f.Cobol[prefix.Length..] : f.Cobol;
                var java = Camel(f.Name);
                if (!used.Add(java))
                {
                    var parent = f.Item.Parent is { IsFiller: false } p ? Camel(p.Name.StartsWith(prefix, StringComparison.Ordinal) ? p.Name[prefix.Length..] : p.Name) : null;
                    java = parent is not null && used.Add(java + "Of" + char.ToUpperInvariant(parent[0]) + parent[1..]) ? java + "Of" + char.ToUpperInvariant(parent[0]) + parent[1..] : Unique(java, used);
                }
                f.Java = java;
                if (f.Kind == "unsupported") notes.Add($"{primary.Name}: {f.Cobol} ({f.Item.Usage}) has no accessor (USAGE not supported by the kit)");
            }
            shapes.Add(new Shape { Roots = members, Fields = fields, Prefix = prefix, Values = HasValues(primary) });
        }
        return shapes;
    }

    static bool IsPrintFile(CobolProgram m, FileDecl f) =>
        f.Organization.Contains("LINE") || DdBindingPlanner.Walk(m).Any(s => s.Verb == "WRITE" && s.Str("advWhen") is not null && RecordFile(m, s.Get("record")) == f.Name);

    static string? RecordFile(CobolProgram m, Node? n)
    {
        if (n?.Id is not { } id) return null;
        var it = m.Items.FirstOrDefault(i => i.Id == id);
        return it?.FileName ?? it?.Record?.FileName;
    }

    /// <summary>A report or message line: a WORKING-STORAGE record written to (or moved into) a file whose record is one text item.</summary>
    static bool IsLine(CobolProgram m, Shape s, List<FileClass> files)
    {
        if (s.Primary.Item.Section is not (DataSection.WorkingStorage or DataSection.LocalStorage)) return false;
        var lineFiles = files.Where(f => f.Print || f.Fd.Records.All(r => r.IsElementary && r.Category == Category.Alphanumeric)).Select(f => f.Fd.Name).ToHashSet();
        var ids = s.Roots.Select(r => r.Item.Id).ToHashSet();
        foreach (var st in DdBindingPlanner.Walk(m))
        {
            if (st.Verb == "WRITE" && st.Get("from") is { Id: { } fid } && ids.Contains(fid) && RecordFile(m, st.Get("record")) is { } wf && lineFiles.Contains(wf)) return true;
            if (st.Verb == "MOVE" && st.Get("src") is { Id: { } sid } && ids.Contains(sid)
                && (st.L?.GetValueOrDefault("dst") ?? []).Any(d => RecordFile(m, d) is { } mf && lineFiles.Contains(mf))) return true;
        }
        return false;
    }

    // ------------------------------------------------------------------ names

    static string Camel(string cobol)
    {
        var parts = cobol.ToLowerInvariant().Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var sb = new StringBuilder(parts.Length == 0 ? "item" : parts[0]);
        foreach (var p in parts.Skip(1)) sb.Append(char.ToUpperInvariant(p[0])).Append(p[1..]);
        var s = sb.ToString();
        if (char.IsDigit(s[0])) s = "f" + s;
        return JavaReserved.Contains(s) ? s + "_" : s;
    }

    static string Pascal(string cobol)
    {
        var c = Camel(cobol).TrimEnd('_');
        return char.ToUpperInvariant(c[0]) + c[1..];
    }

    static string Unique(string name, ISet<string> used)
    {
        var n = name;
        for (int k = 2; !used.Add(n); k++) n = name + k;
        return n;
    }

    static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c < 0x20 || c is >= (char)0x7F and <= (char)0xFF) sb.Append('\\').Append(Convert.ToString(c, 8).PadLeft(3, '0'));
            else if (c > 0xFF) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>Text safe inside a // comment: no line terminators and no backslash (the Java lexer decodes \u escapes even in comments).</summary>
    static string CommentSafe(string s) => new(s.Select(c => c < 0x20 || c > 0x7E || c == '\\' ? '?' : c).ToArray());

    static string B(bool b) => b ? "true" : "false";

    static string PicText(DataItem it)
    {
        var pic = it.Pic is null ? (it.Category == Category.Group ? "group" : "") : "PIC " + it.Pic;
        var usage = it.Usage switch { Usage.Comp3 => " COMP-3", Usage.Binary => " COMP", Usage.Comp5 => " COMP-5", Usage.Comp1 => " COMP-1", Usage.Comp2 => " COMP-2", _ => "" };
        var sign = it.SignSeparate ? (it.SignLeading ? " SIGN LEADING SEPARATE" : " SIGN TRAILING SEPARATE") : it.SignLeading ? " SIGN LEADING" : "";
        return pic + usage + sign + (it.Justified ? " JUSTIFIED RIGHT" : "") + (it.BlankWhenZero ? " BLANK WHEN ZERO" : "");
    }

    static string Occurs(Field f) => f.Dims.Count == 0 ? "" : " OCCURS " + string.Join(" x ", f.Dims.Select(d => d.Count));

    // ------------------------------------------------------------------ Java: the record classes

    static string PicSource(string pkg)
    {
        using var s = typeof(RecordKitBuilder).Assembly.GetManifestResourceStream("MlCpu.CobolKb.Knowledge.RecordKit.Pic.java")
                      ?? throw new InvalidOperationException("RecordKit/Pic.java is not embedded");
        using var reader = new StreamReader(s, Encoding.UTF8);
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace("package __PACKAGE__;", $"package {pkg};");
    }

    static readonly string[] Subs = ["i", "j", "k", "l", "m", "n", "p"];

    static string Params(Field f) => string.Join(", ", f.Dims.Select((_, k) => "int " + Subs[k]));

    static string Args(Field f) => string.Join(", ", f.Dims.Select((_, k) => Subs[k]));

    static string OffsetExpr(Field f) =>
        f.Dims.Count == 0 ? f.Offset.ToString() : f.Offset + string.Concat(f.Dims.Select((d, k) => $" + ({Subs[k]} - 1) * {d.Stride}"));

    /// <summary>Java type of a numeric getter: long for integers that fit, BigDecimal otherwise.</summary>
    static string NumericType(DataItem it, string kind) =>
        it.Scale == 0 && it.Digits <= 18 && !(kind == "binary" && it.Length == 8 && !it.Signed) ? "long" : "BigDecimal";

    static string Get(Field f, string off)
    {
        var it = f.Item;
        return f.Kind switch
        {
            "zoned" => $"Pic.getZoned(b, {off}, {it.Length}, {it.Scale}, {B(it.Signed)}, {B(it.SignSeparate)}, {B(it.SignLeading)})",
            "packed" => $"Pic.getPacked(b, {off}, {it.Length}, {it.Scale})",
            "binary" => $"Pic.getBinary(b, {off}, {it.Length}, {it.Scale}, {B(it.Signed)})",
            _ => $"Pic.getText(b, {off}, {it.Length})"
        };
    }

    static string Put(Field f, string off, string value)
    {
        var it = f.Item;
        return f.Kind switch
        {
            "zoned" => $"Pic.putZoned(b, {off}, {it.Length}, {it.Digits}, {it.Scale}, {B(it.Signed)}, {B(it.SignSeparate)}, {B(it.SignLeading)}, {value})",
            "packed" => $"Pic.putPacked(b, {off}, {it.Length}, {it.Digits}, {it.Scale}, {B(it.Signed)}, {value})",
            "binary" => $"Pic.putBinary(b, {off}, {it.Length}, {it.Scale}, {B(it.Signed)}, {value})",
            "edited" => $"Pic.putEdited(b, {off}, {it.Length}, {Lit(DataDivisionParser.ExpandPic(it.Pic!))}, {it.Scale}, {B(it.BlankWhenZero)}, {value})",
            "alnumEdited" => $"Pic.putAlnumEdited(b, {off}, {it.Length}, {Lit(DataDivisionParser.ExpandPic(it.Pic!))}, {value})",
            _ => $"Pic.putText(b, {off}, {it.Length}, {value}, {B(it.Justified && it.Category != Category.Group)})"
        };
    }

    static string Describe(Shape s) => string.Join(", ", s.Roots.Select(r => $"{r.Item.Name} ({r.Role})"));

    static string RecordSource(string pkg, string pid, Shape s, List<string> notes)
    {
        var root = s.Primary.Item;
        var cls = s.Class;
        var sb = new StringBuilder();
        sb.Append($"package {pkg};\n\nimport java.math.BigDecimal;\n\n");
        sb.Append($"/**\n * {pid}: {(s.Line ? "report line" : "record")} {Describe(s)}{(root.Origin.Length > 0 && root.Origin != "main" ? $", copybook {root.Origin}" : "")}; {s.Length} bytes.\n");
        if (s.Prefix.Length > 0) sb.Append($" * Accessors are named without the prefix {string.Join(" / ", s.Roots.Select(r => Prefix(r.Item)).Distinct())}.\n");
        sb.Append(" * A setter stores with COBOL MOVE rules and changes only its own bytes. Generated by the MlCpu record kit; do not edit.\n */\n");
        sb.Append($"public final class {cls} {{\n");
        sb.Append($"    /** Bytes in the record area. */\n    public static final int LENGTH = {s.Length};\n\n    private final byte[] b;\n\n");
        sb.Append($"    private {cls}(byte[] b) {{\n        this.b = b;\n    }}\n\n");
        sb.Append($"    /** A new record area: every byte the storage default ({(s.Values ? "ctx.storageDefault()" : "ctx.storageDefault(), as COBOL leaves FILE SECTION and LINKAGE storage")}){(s.Values ? ", then the VALUE clauses" : "")}. */\n");
        sb.Append($"    public {cls}(byte storageDefault) {{\n        this(Pic.filled(LENGTH, storageDefault));\n{(s.Values ? "        values();\n" : "")}    }}\n\n");
        sb.Append($"    /** A view over {{@code bytes}} (no copy): reads and writes go to the array (a record just read, a CALL argument). */\n");
        sb.Append($"    public static {cls} wrap(byte[] bytes) {{\n        return new {cls}(bytes);\n    }}\n\n");
        sb.Append("    /** The record area itself (no copy): what WRITE, REWRITE or ctx.call(...) gets. */\n    public byte[] bytes() {\n        return b;\n    }\n\n");
        sb.Append($"    /** An independent copy of this area. */\n    public {cls} copy() {{\n        return new {cls}(b.clone());\n    }}\n\n");
        sb.Append($"    /** MOVE a group or record TO this record: {{@code from}} copied, then space padded or truncated to LENGTH. */\n");
        sb.Append($"    public {cls} moveFrom(byte[] from) {{\n        System.arraycopy(Pic.moved(from, LENGTH), 0, b, 0, LENGTH);\n        return this;\n    }}\n\n");
        sb.Append($"    /** READ into this area: the bytes of the record read are copied; bytes after them keep what they held. */\n");
        sb.Append($"    public {cls} readFrom(byte[] record) {{\n        System.arraycopy(record, 0, b, 0, Math.min(record.length, LENGTH));\n        return this;\n    }}\n\n");
        sb.Append($"    /** MOVE SPACES / ZEROS / LOW-VALUES / HIGH-VALUES TO this record: every byte (' ', '0', 0x00, 0xFF). */\n");
        sb.Append($"    public {cls} fill(char c) {{\n        Pic.fill(b, 0, LENGTH, (byte) c);\n        return this;\n    }}\n\n");
        sb.Append($"    /** INITIALIZE this record: alphanumeric items to spaces, numeric items to zero; FILLER and REDEFINES items keep their bytes. */\n");
        sb.Append($"    public {cls} initialize() {{\n");
        foreach (var line in InitializeLines(root)) sb.Append("        ").Append(line).Append('\n');
        sb.Append("        return this;\n    }\n");
        if (s.Values)
        {
            sb.Append("\n    /** The VALUE clauses, in declaration order (as the program initializes its WORKING-STORAGE). */\n    private void values() {\n");
            foreach (var line in ValueLines(root, notes)) sb.Append("        ").Append(line).Append('\n');
            sb.Append("    }\n");
        }
        foreach (var f in s.Fields.Where(f => f.Kind != "unsupported")) AppendAccessors(sb, cls, f);
        sb.Append("}\n");
        return sb.ToString();
    }

    static void AppendAccessors(StringBuilder sb, string cls, Field f)
    {
        var it = f.Item;
        var off = OffsetExpr(f);
        var p = Params(f);
        var a = Args(f);
        var comma = p.Length > 0 ? ", " : "";
        var where = f.Dims.Count == 0 ? $"bytes {f.Offset}..{f.Offset + it.Length - 1}"
            : $"first occurrence at bytes {f.Offset}..{f.Offset + it.Length - 1}; subscripts from 1, not checked against OCCURS (one past the end addresses the bytes after the table, as in COBOL)";
        var doc = $"{f.Cobol} {PicText(it)}{Occurs(f)}, {where}";
        sb.Append('\n');
        if (Numeric(f.Kind) && f.Kind != "edited")
        {
            var type = NumericType(it, f.Kind);
            f.JavaType = type;
            sb.Append($"    /** {doc}. */\n    public {type} {f.Java}({p}) {{\n        return {Get(f, off)}{(type == "long" ? ".longValue()" : "")};\n    }}\n\n");
            sb.Append($"    /** MOVE / COMPUTE to {f.Cobol}: fraction digits beyond {it.Scale} cut, integer digits beyond {Math.Max(0, it.Digits - Math.Max(0, it.Scale))} lost on the left{(it.Signed ? "" : ", sign dropped")}. */\n");
            sb.Append($"    public {cls} {f.Java}({p}{comma}BigDecimal v) {{\n        {Put(f, off, "v")};\n        return this;\n    }}\n\n");
            sb.Append($"    public {cls} {f.Java}({p}{comma}long v) {{\n        return {f.Java}({a}{(a.Length > 0 ? ", " : "")}BigDecimal.valueOf(v));\n    }}\n");
        }
        else if (f.Kind == "edited")
        {
            f.JavaType = "String";
            sb.Append($"    /** {doc}: the edited text. */\n    public String {f.Java}({p}) {{\n        return {Get(f, off)};\n    }}\n\n");
            sb.Append($"    /** MOVE a number to {f.Cobol}: edited by its PICTURE (high-order digits beyond the PICTURE lost). */\n");
            sb.Append($"    public {cls} {f.Java}({p}{comma}BigDecimal v) {{\n        {Put(f, off, "v")};\n        return this;\n    }}\n\n");
            sb.Append($"    public {cls} {f.Java}({p}{comma}long v) {{\n        return {f.Java}({a}{(a.Length > 0 ? ", " : "")}BigDecimal.valueOf(v));\n    }}\n");
        }
        else
        {
            f.JavaType = "String";
            var rule = it.Category == Category.Group ? "group move: left-justified, space padded, truncated"
                : f.Kind == "alnumEdited" ? "B, 0 and / inserted by the PICTURE" : it.Justified ? "right-justified, space padded on the left, truncated on the left" : "left-justified, space padded, truncated on the right";
            sb.Append($"    /** {doc}: its bytes as text (trailing spaces kept). */\n    public String {f.Java}({p}) {{\n        return {Get(f, off)};\n    }}\n\n");
            sb.Append($"    /** MOVE text to {f.Cobol}: {rule}. */\n    public {cls} {f.Java}({p}{comma}String v) {{\n        {Put(f, off, "v")};\n        return this;\n    }}\n");
        }
        if (f.Dims.Count == 0) AppendConditions(sb, cls, f);
    }

    /// <summary>Level-88 condition names on an elementary item: isX() tests it, setX() is SET X TO TRUE (moves the first value).</summary>
    static void AppendConditions(StringBuilder sb, string cls, Field f)
    {
        var it = f.Item;
        if (!it.IsElementary) return;
        foreach (var c in it.Children.Where(c => c.Level == 88 && c.Values.Count > 0))
        {
            var tests = new List<string>();
            string? first = null;
            bool numeric = Numeric(f.Kind) && f.Kind != "edited";
            foreach (var (low, high) in c.Values)
            {
                var l = ConditionValue(low, it, numeric);
                if (l is null) { tests.Clear(); break; }
                first ??= l;
                if (high is null) tests.Add(numeric ? $"{f.Java}Value().compareTo({l}) == 0" : $"{f.Java}().equals({l})");
                else
                {
                    var h = ConditionValue(high, it, numeric);
                    if (h is null) { tests.Clear(); break; }
                    tests.Add(numeric ? $"({f.Java}Value().compareTo({l}) >= 0 && {f.Java}Value().compareTo({h}) <= 0)"
                        : $"({f.Java}().compareTo({l}) >= 0 && {f.Java}().compareTo({h}) <= 0)");
                }
            }
            if (tests.Count == 0 || first is null) continue;
            var name = Pascal(c.Name);
            sb.Append($"\n    /** 88 {c.Name}. */\n    public boolean is{name}() {{\n        return {string.Join(" || ", tests)};\n    }}\n\n");
            sb.Append($"    /** SET {c.Name} TO TRUE. */\n    public {cls} set{name}() {{\n        {Put(f, f.Offset.ToString(), first)};\n        return this;\n    }}\n");
            if (numeric && !sb.ToString().Contains($"BigDecimal {f.Java}Value()"))
                sb.Append($"\n    private BigDecimal {f.Java}Value() {{\n        return {Get(f, f.Offset.ToString())};\n    }}\n");
        }
    }

    static string? ConditionValue(RawWord w, DataItem parent, bool numeric)
    {
        var fig = Figurative(w);
        if (numeric)
        {
            if (w.Quoted) return null;
            if (fig == "ZERO") return "BigDecimal.ZERO";
            return NumericLiteral(w.Text) is { } n ? $"new BigDecimal({Lit(n)})" : null;
        }
        if (w.Quoted) return Lit(PadText(w.Text, parent.Length));
        if (fig is not null)
        {
            char c = fig switch { "SPACE" => ' ', "ZERO" => '0', "HIGH" => 'ÿ', "LOW" => '\0', "QUOTE" => '"', _ => '\0' };
            return Lit(new string(c, parent.Length));
        }
        return NumericLiteral(w.Text) is { } d ? Lit(PadText(d.TrimStart('+', '-').Replace(".", ""), parent.Length)) : null;
    }

    static string PadText(string s, int n) => s.Length >= n ? s[..n] : s + new string(' ', n - s.Length);

    static string? Figurative(RawWord w) => w.Quoted ? null : w.Text.ToUpperInvariant() switch
    {
        "SPACE" or "SPACES" => "SPACE",
        "ZERO" or "ZEROS" or "ZEROES" => "ZERO",
        "HIGH-VALUE" or "HIGH-VALUES" => "HIGH",
        "LOW-VALUE" or "LOW-VALUES" => "LOW",
        "QUOTE" or "QUOTES" => "QUOTE",
        "NULL" or "NULLS" => "NULL",
        _ => null
    };

    /// <summary>A numeric literal as BigDecimal text ("+0" → "0", ".5" → "0.5"), or null.</summary>
    static string? NumericLiteral(string t)
    {
        var s = t.Trim();
        if (s.StartsWith('+')) s = s[1..];
        if (s.EndsWith('.')) s = s[..^1];
        if (s.StartsWith('.')) s = "0" + s;
        if (s.StartsWith("-.")) s = "-0" + s[1..];
        return System.Text.RegularExpressions.Regex.IsMatch(s, @"^-?\d+(\.\d+)?$") ? s : null;
    }

    /// <summary>Faithful _init skips items under a subordinate REDEFINES (the root's own clause does not count).</summary>
    static bool UnderRedefines(DataItem it, DataItem root)
    {
        for (var x = it; x is not null && x != root; x = x.Parent)
            if (x.Redefines is not null && x.Parent is not null) return true;
        return root.Redefines is not null && root.Parent is not null;
    }

    static IEnumerable<string> Loop(List<(int Count, int Stride)> dims, string body)
    {
        if (dims.Count == 0) { yield return body; yield break; }
        var head = string.Concat(dims.Select((d, k) => $"for (int {Subs[k]} = 1; {Subs[k]} <= {d.Count}; {Subs[k]}++) "));
        yield return head + body;
    }

    static List<string> ValueLines(DataItem root, List<string> notes)
    {
        var lines = new List<string>();
        int rootDims = root.Dims.Count;
        foreach (var it in Tree(root))
        {
            if (it.Values.Count == 0 || UnderRedefines(it, root)) continue;
            var f = new Field { Item = it, Kind = Kind(it), Offset = it.Offset - root.Offset, Dims = it.Dims.Skip(rootDims).ToList(), Cobol = it.Name };
            var off = OffsetExpr(f);
            var w = it.Values[0].Low;
            var fig = Figurative(w);
            string? stmt = null;
            if (w.Quoted || it.ValueAll && fig is null)
            {
                if (f.Kind is "text" or "group")
                    stmt = it.ValueAll ? $"Pic.putAll(b, {off}, {it.Length}, {Lit(w.Text)});" : Put(f, off, Lit(w.Text)) + ";";
                else if (f.Kind == "alnumEdited") stmt = Put(f, off, Lit(it.ValueAll ? Repeat(w.Text, it.Length) : w.Text)) + ";";
            }
            else if (fig is not null)
            {
                if (fig == "ZERO" && Numeric(f.Kind)) stmt = Put(f, off, "BigDecimal.ZERO") + ";";
                else
                {
                    var b = fig switch { "SPACE" => "(byte) ' '", "ZERO" => "(byte) '0'", "HIGH" => "(byte) 0xFF", "QUOTE" => "(byte) '\"'", _ => "(byte) 0" };
                    stmt = $"Pic.fill(b, {off}, {it.Length}, {b});";
                }
            }
            else if (NumericLiteral(w.Text) is { } n)
            {
                if (Numeric(f.Kind)) stmt = Put(f, off, $"new BigDecimal({Lit(n)})") + ";";
                else if (f.Kind is "text" or "group") stmt = Put(f, off, Lit(n.TrimStart('-').Replace(".", ""))) + ";";
                else if (f.Kind == "alnumEdited") stmt = Put(f, off, Lit(n.TrimStart('-').Replace(".", ""))) + ";";
            }
            if (stmt is null) { notes.Add($"{root.Name}: VALUE {w.Text} of {it.Name} is not reproduced by the kit"); continue; }
            lines.Add(CommentSafe($"// {it.Name} VALUE {(it.ValueAll ? "ALL " : "")}{(w.Quoted ? "'" + w.Text + "'" : w.Text)}"));
            lines.AddRange(Loop(f.Dims, stmt));
        }
        return lines;
    }

    static string Repeat(string s, int n)
    {
        var sb = new StringBuilder();
        while (sb.Length < n) sb.Append(s);
        return sb.ToString()[..n];
    }

    /// <summary>The faithful INITIALIZE of a whole record: elementary items under it, skipping FILLER, 88s and REDEFINES subtrees.</summary>
    static List<string> InitializeLines(DataItem root)
    {
        var lines = new List<string>();
        int rootDims = root.Dims.Count;
        void Visit(DataItem g)
        {
            foreach (var c in g.Children)
            {
                if (c.Level == 88 || c.Redefines is not null) continue;
                if (c.Children.Any(x => x.Level != 88)) { Visit(c); continue; }
                if (c.IsFiller) continue;
                var f = new Field { Item = c, Kind = Kind(c), Offset = c.Offset - root.Offset, Dims = c.Dims.Skip(rootDims).ToList(), Cobol = c.Name };
                var off = OffsetExpr(f);
                string? stmt = c.Category switch
                {
                    Category.Index or Category.Pointer => null,
                    _ when Numeric(f.Kind) => Put(f, off, "BigDecimal.ZERO") + ";",
                    _ when f.Kind == "unsupported" => null,
                    _ => $"Pic.fill(b, {off}, {c.Length}, (byte) ' ');"
                };
                if (stmt is not null) lines.AddRange(Loop(f.Dims, stmt));
            }
        }
        if (root.Children.Any(x => x.Level != 88)) Visit(root);
        else if (!root.IsFiller)
        {
            var f = new Field { Item = root, Kind = Kind(root), Offset = 0, Cobol = root.Name };
            if (Numeric(f.Kind)) lines.Add(Put(f, "0", "BigDecimal.ZERO") + ";");
            else if (f.Kind != "unsupported") lines.Add($"Pic.fill(b, 0, {root.Length}, (byte) ' ');");
        }
        return lines;
    }

    // ------------------------------------------------------------------ Java: one class per FD

    static string FileSource(string pkg, string pid, FileClass fc, List<Shape> shapes)
    {
        var f = fc.Fd;
        bool variable = string.Equals(f.RecordingMode, "V", StringComparison.OrdinalIgnoreCase) || f.Records.Select(r => r.Length).Distinct().Count() > 1
                        || (f.MinRecord > 0 && f.MinRecord != f.MaxRecord);
        bool indexed = f.Organization.Contains("INDEXED");
        var format = fc.Print ? "print file written with ADVANCING (PrintFile)" : f.Organization.Contains("LINE") ? "line sequential"
            : indexed ? "indexed (KSDS image: fixed-length records in key order)" : variable ? "variable-length sequential (4-byte record descriptor per record)" : "fixed-length sequential";
        var records = string.Join(", ", f.Records.Select(r => $"{r.Name} ({r.Length} bytes{(shapes.FirstOrDefault(s => s.Roots.Any(x => x.Item == r)) is { } sh ? ", " + sh.Class : "")})"));
        var sb = new StringBuilder($"package {pkg};\n\n");
        sb.Append($"/**\n * {pid}: {f.Name} (DD {f.Assign}), {format}; record{(f.Records.Count > 1 ? "s" : "")} {records}.\n");
        sb.Append(" * Generated by the MlCpu record kit; do not edit.\n */\n");
        sb.Append($"public final class {fc.Class} {{\n    private {fc.Class}() {{}}\n\n");
        sb.Append($"    /** The DD name: ctx.dataset(DD). */\n    public static final String DD = {Lit(f.Assign)};\n");
        sb.Append($"    /** The longest record; every record of a fixed-length file. */\n    public static final int RECORD_LENGTH = {Math.Max(f.MaxRecord, f.Records.Max(r => r.Length))};\n");
        if (variable) sb.Append($"    public static final int MIN_RECORD_LENGTH = {(f.MinRecord > 0 ? f.MinRecord : f.Records.Min(r => r.Length))};\n    public static final boolean VARIABLE = true;\n");
        if (indexed && f.RecordKey is { } key && f.Records.SelectMany(Tree).FirstOrDefault(i => i.Name == key) is { } k)
            sb.Append($"    /** RECORD KEY {key}: offset and length in the record. */\n    public static final int KEY_OFFSET = {k.Offset - k.Record.Offset}, KEY_LENGTH = {k.Length};\n");
        foreach (var alt in f.AlternateKeys)
            if (f.Records.SelectMany(Tree).FirstOrDefault(i => i.Name == alt.Name) is { } ak)
                sb.Append($"    /** ALTERNATE RECORD KEY {alt.Name}{(alt.Duplicates ? " WITH DUPLICATES" : "")}. */\n    public static final int {Constant(alt.Name)}_OFFSET = {ak.Offset - ak.Record.Offset}, {Constant(alt.Name)}_LENGTH = {ak.Length};\n");
        foreach (var r in f.Records)
        {
            var name = Camel(r.Name);
            sb.Append($"\n    /** WRITE {r.Name} FROM x (or MOVE x TO {r.Name}): x copied into the {r.Length}-byte record, space padded or truncated on the right. */\n");
            sb.Append($"    public static byte[] {name}(byte[] from) {{\n        return Pic.moved(from, {r.Length});\n    }}\n");
        }
        sb.Append("}\n");
        return sb.ToString();
    }

    static string Constant(string cobol) => string.Concat(cobol.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_'));

    // ------------------------------------------------------------------ the conformance test

    static string TestSource(string pkg, RecordKitRequest r, List<Shape> shapes)
    {
        var sb = new StringBuilder();
        sb.Append($$"""
            package {{pkg}};

            import static org.junit.jupiter.api.Assertions.assertArrayEquals;
            import static org.junit.jupiter.api.Assertions.assertEquals;

            import com.mlcpu.rt.Area;
            import com.mlcpu.rt.CobConfig;
            import com.mlcpu.rt.CobField;
            import com.mlcpu.rt.CobRt;
            import java.lang.reflect.Method;
            import java.math.BigDecimal;
            import java.util.ArrayList;
            import java.util.List;
            import java.util.Map;
            import java.util.function.BiConsumer;
            import java.util.function.Function;
            import org.junit.jupiter.api.AfterEach;
            import org.junit.jupiter.api.Test;

            /**
             * The record kit of {{r.ProgramId}} against the faithful program's own storage ({{r.FaithfulClass}}): every class's initial
             * image under both storage defaults, and every accessor's MOVE and decode over the same bytes. Generated; do not edit.
             */
            class RecordKitTest {
                static final String[] TEXTS = {"", "A", "Zz 9", "a longer text value than most fields hold, 0123456789 0123456789 0123456789"};
                static final String[] NUMBERS = {"0", "1", "-1", "7.5", "-12.34", "99", "-100.005", "123456789.987654321", "-98765432109876543.21", "1000000000000000000"};
                final byte savedDefault = CobConfig.DEFAULT_BYTE;
                final boolean savedTruncate = CobConfig.BINARY_TRUNCATE;

                @AfterEach
                void restore() {
                    CobConfig.DEFAULT_BYTE = savedDefault;
                    CobConfig.BINARY_TRUNCATE = savedTruncate;
                }

                /** The faithful program, storage initialised, its linkage bound to fresh areas; COBOL name to field (every item it declares). */
                static Map<String, List<CobField>> fields(byte storageDefault) throws Exception {
                    CobConfig.DEFAULT_BYTE = storageDefault;
                    CobConfig.BINARY_TRUNCATE = false;
                    Class<?> c = Class.forName({{Lit(r.FaithfulClass)}});
                    Object p = c.getDeclaredConstructor().newInstance();
                    c.getMethod("ensureInit").invoke(p);
                    Area[] linkage = (Area[]) find(c, "_linkage").invoke(p);
                    for (Area a : linkage) a.bind(new byte[a.size], 0);
                    Map<String, List<CobField>> m = new java.util.HashMap<>();
                    for (Class<?> k = c; k != null; k = k.getSuperclass())
                        for (java.lang.reflect.Field f : k.getDeclaredFields())
                            if (f.getType() == CobField.class && !java.lang.reflect.Modifier.isStatic(f.getModifiers())) {
                                f.setAccessible(true);
                                CobField v = (CobField) f.get(p);
                                if (v != null) m.computeIfAbsent(v.name, x -> new ArrayList<>()).add(v);
                            }
                    return m;
                }

                static Method find(Class<?> c, String name) throws NoSuchMethodException {
                    for (Class<?> k = c; k != null; k = k.getSuperclass())
                        for (Method m : k.getDeclaredMethods())
                            if (m.getName().equals(name) && m.getParameterCount() == 0) { m.setAccessible(true); return m; }
                    throw new NoSuchMethodException(name);
                }

                static CobField field(Map<String, List<CobField>> m, String name) {
                    List<CobField> f = m.get(name);
                    if (f == null) throw new AssertionError(name + " is not a field of the faithful program");
                    return f.get(0);
                }

                /** The item named {@code name} inside {@code record} (a qualified name: the same name can be declared in several records). */
                static CobField field(Map<String, List<CobField>> m, String name, CobField record) {
                    for (CobField f : m.getOrDefault(name, List.of()))
                        if (f.area == record.area && f.off >= record.off && f.off + f.len <= record.off + record.len) return f;
                    throw new AssertionError(name + " is not a field of " + record.name + " in the faithful program");
                }

                /** One accessor checked: the same MOVE through the faithful field and through the kit, over the same record bytes. */
                static <T> void check(List<String> failures, String what, CobField record, CobField target, Function<byte[], T> wrap,
                                      BiConsumer<T, Object> kitMove, Object value, Function<T, Object> kitGet) {
                    byte[] before = record.bytes();
                    T kit = wrap.apply(before.clone());
                    CobRt.move(value, target);
                    try {
                        kitMove.accept(kit, value);
                    } catch (RuntimeException e) {
                        failures.add(what + " <- " + value + ": " + e);
                        record.setBytes(before);
                        return;
                    }
                    byte[] expected = record.bytes();
                    byte[] actual = (byte[]) bytesOf(kit);
                    if (!java.util.Arrays.equals(expected, actual)) failures.add(what + " <- " + value + ": bytes differ");
                    else if (kitGet != null) {
                        Object got = kitGet.apply(kit);
                        Object want = target.isNumeric() ? (got instanceof Long ? (Object) target.dec().longValue() : target.dec()) : target.str();
                        if (got instanceof BigDecimal g && want instanceof BigDecimal w ? g.compareTo(w) != 0 : !got.equals(want))
                            failures.add(what + " <- " + value + ": get " + got + ", faithful " + want);
                    }
                    record.setBytes(before);
                }

                static Object bytesOf(Object kit) {
                    try {
                        return kit.getClass().getMethod("bytes").invoke(kit);
                    } catch (ReflectiveOperationException e) {
                        throw new IllegalStateException(e);
                    }
                }

                static void report(List<String> failures) {
                    if (!failures.isEmpty()) throw new AssertionError(failures.size() + " difference(s):\n" + String.join("\n", failures.subList(0, Math.min(40, failures.size()))));
                }

            """);
        // Initial images: WORKING-STORAGE roots only (FD and LINKAGE areas have no VALUE semantics: their image is the storage default).
        sb.Append("    @Test\n    void initialImagesMatchTheProgram() throws Exception {\n        for (byte d : new byte[]{0, ' '}) {\n            Map<String, List<CobField>> m = fields(d);\n");
        foreach (var s in shapes)
            foreach (var root in s.Roots.Where(x => x.Item.Section is DataSection.WorkingStorage or DataSection.LocalStorage or DataSection.File && x.Program == r.Program))
                sb.Append($"            assertArrayEquals(field(m, {Lit(root.Item.Name)}).bytes(), new {s.Class}(d).bytes(), \"{root.Item.Name} with storage default \" + d);\n");
        sb.Append("        }\n    }\n\n");

        // Accessors: per class, every field (first and last occurrence), through the primary root's faithful fields.
        sb.Append("    @Test\n    void accessorsStoreAndReadLikeTheProgram() throws Exception {\n        Map<String, List<CobField>> m = fields((byte) 0);\n        List<String> failures = new ArrayList<>();\n");
        foreach (var s in shapes)
        {
            var root = s.Roots.FirstOrDefault(x => x.Program == r.Program);
            if (root is null) continue;
            var rootPrefix = Prefix(root.Item);
            sb.Append($"        {{\n            CobField rec = field(m, {Lit(root.Item.Name)});\n");
            foreach (var f in s.Fields.Where(f => f.Kind != "unsupported"))
            {
                var cobol = s.Prefix.Length > 0 ? rootPrefix + f.Name : f.Cobol;
                var samples = Numeric(f.Kind) ? "NUMBERS" : "TEXTS";
                var subsets = f.Dims.Count == 0 ? new List<int[]> { Array.Empty<int>() }
                    : new List<int[]> { f.Dims.Select(_ => 1).ToArray(), f.Dims.Select(d => d.Count).ToArray() };
                foreach (var subs in subsets.DistinctBy(x => string.Join(",", x)))
                {
                    var at = subs.Length == 0 ? "" : ".at(" + string.Join(", ", subs) + ")";
                    var args = subs.Length == 0 ? "" : string.Join(", ", subs) + ", ";
                    var argsOnly = subs.Length == 0 ? "" : string.Join(", ", subs);
                    var cast = Numeric(f.Kind) ? "(BigDecimal) x" : "(String) x";
                    var moveValue = Numeric(f.Kind) ? "new BigDecimal(v)" : "v";
                    var getter = f.Kind == "edited" ? $"k -> k.{f.Java}({argsOnly})" : $"k -> (Object) k.{f.Java}({argsOnly})";
                    sb.Append($"            for (String v : {samples})\n");
                    sb.Append($"                check(failures, {Lit(cobol + (subs.Length == 0 ? "" : "(" + string.Join(",", subs) + ")"))}, rec, field(m, {Lit(cobol)}, rec){at}, {s.Class}::wrap,\n");
                    sb.Append($"                    (k, x) -> k.{f.Java}({args}{cast}), {moveValue}, {getter});\n");
                }
            }
            sb.Append("        }\n");
        }
        sb.Append("        report(failures);\n    }\n}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ the prompt's view of the kit

    static string Summary(string pkg, RecordKitRequest r, List<Shape> shapes, List<FileClass> files, List<string> notes)
    {
        var sb = new StringBuilder();
        sb.Append($"# Generated record kit: package `{pkg}` (use it; do not re-implement it)\n\n");
        sb.Append("Generated from the COBOL data division and tested byte for byte against the original program's storage (RecordKitTest). Use these ");
        sb.Append("classes for every record you read, write, rewrite or pass to a CALL, and for every report or message line, instead of computing ");
        sb.Append("offsets or formats yourself. Import them with `import " + pkg + ".*;`.\n\n");
        sb.Append("- Every setter stores with COBOL MOVE rules: text is space padded or truncated on the right; numbers are cut to the PICTURE (fraction ");
        sb.Append("digits beyond the scale dropped, integer digits beyond the PICTURE lost on the left, the sign dropped for unsigned items) and encoded ");
        sb.Append("in the item's USAGE (zoned with the sign overpunched, packed, binary); numeric-edited items are formatted by their PICTURE. ");
        sb.Append("A setter changes only its own bytes, so a record keeps every byte the program does not assign. Setters return the record (chainable).\n");
        sb.Append("- Subscripts are 1-based and not checked against OCCURS: like COBOL, one past the end addresses the bytes after the table.\n");
        sb.Append("- Every record class has: `new X(ctx.storageDefault())` (a new area: storage default, then its VALUE clauses; keep one instance per ");
        sb.Append("COBOL area for the whole run), `X.wrap(byte[])` (a view, no copy: a record just read, a CALL argument), `bytes()` (the area itself, ");
        sb.Append("for WRITE, REWRITE or ctx.call), `copy()`, `moveFrom(byte[])` (MOVE a group TO this record: copied, then space padded or ");
        sb.Append("truncated), `readFrom(byte[])` (READ into this area: the record's bytes copied, the rest keeps its bytes), `fill(char)` (MOVE ");
        sb.Append("SPACES / ZEROS / LOW-VALUES TO the record), `initialize()` (INITIALIZE: alphanumeric items to spaces, numeric to zero, FILLER kept).\n");
        sb.Append("- `Pic` applies the same rules to values you keep in Java fields: `Pic.fit(v, \"S9(9)V99\")` (MOVE/COMPUTE without ROUNDED), ");
        sb.Append("`Pic.fitRounded(v, pic)`, `Pic.sizeError(v, pic)` (ON SIZE ERROR), `Pic.edit(v, \"-ZZZ,ZZZ,ZZZ.ZZ\")`, ");
        sb.Append("`Pic.display(v, \"S9(9)\", \"COMP-3\", ctx.displayNumeric())` (what DISPLAY prints for a numeric item), `Pic.text(s, n)`, `Pic.digits(n, len)`.\n\n");

        void Class(Shape s)
        {
            var root = s.Primary.Item;
            var copybook = root.Origin.Length > 0 && root.Origin != "main" ? $"; copybook {root.Origin}" : "";
            sb.Append($"### {s.Class}: {s.Length} bytes; {Describe(s)}{copybook}{(s.Values ? "; VALUE clauses applied by the constructor" : "")}\n");
            if (s.Prefix.Length > 0) sb.Append($"Accessors drop the prefix ({string.Join(" / ", s.Roots.Select(x => Prefix(x.Item)).Distinct())}).\n");
            if (s.Line)
            {
                foreach (var (record, length, fileClass) in Targets(r.Program, s, files))
                    sb.Append($"Written into {record} ({length} bytes{(length < s.Length ? $", so the last {s.Length - length} byte(s) are cut" : length > s.Length ? ", space padded" : "")}): `{fileClass}.{Camel(record)}(line.bytes())`.\n");
                sb.Append("Image (VALUE text; _ marks the variable items; trailing spaces not shown):\n```\n").Append(Image(s)).Append("\n```\n");
            }
            if (s.Fields.All(f => f.Kind == "unsupported")) { sb.Append("No variable items: a constant line.\n\n"); return; }
            sb.Append("```\n");
            int w = Math.Max(12, s.Fields.Where(f => f.Kind != "unsupported").Select(f => Signature(f).Length).DefaultIfEmpty(0).Max());
            foreach (var f in s.Fields.Where(f => f.Kind != "unsupported"))
                sb.Append(Signature(f).PadRight(w)).Append("  ").Append(f.Cobol).Append(' ').Append(PicText(f.Item)).Append(Occurs(f))
                  .Append("  @").Append(f.Offset).Append('+').Append(f.Item.Length).Append('\n');
            foreach (var f in s.Fields.Where(f => f.Dims.Count == 0 && f.Item.IsElementary))
                foreach (var c in f.Item.Children.Where(c => c.Level == 88 && c.Values.Count > 0))
                    sb.Append($"is{Pascal(c.Name)}() / set{Pascal(c.Name)}()".PadRight(w)).Append("  88 ").Append(c.Name).Append(" VALUE ")
                      .Append(string.Join(", ", c.Values.Select(v => (v.Low.Quoted ? "'" + v.Low.Text + "'" : v.Low.Text) + (v.High is null ? "" : " THRU " + v.High.Text)))).Append('\n');
            sb.Append("```\n\n");
        }

        // Accessor types are decided while the source is rendered; render them first so the summary shows them.
        foreach (var s in shapes) foreach (var f in s.Fields.Where(f => f.Kind != "unsupported")) f.JavaType ??= TypeOf(f);
        var records = shapes.Where(s => !s.Line).ToList();
        var lines = shapes.Where(s => s.Line).ToList();
        if (records.Count > 0)
        {
            sb.Append("## Record areas\n\n");
            foreach (var s in records) Class(s);
        }
        if (lines.Count > 0)
        {
            sb.Append("## Report and message lines (VALUE-initialised: set the variable items, then write `bytes()`)\n\n");
            foreach (var s in lines) Class(s);
        }
        if (files.Count > 0)
        {
            sb.Append("## Files (constants and the WRITE … FROM image)\n\n```\n");
            foreach (var fc in files)
            {
                var f = fc.Fd;
                bool variable = string.Equals(f.RecordingMode, "V", StringComparison.OrdinalIgnoreCase) || f.Records.Select(x => x.Length).Distinct().Count() > 1;
                var kind = fc.Print ? "print (PrintFile, ADVANCING)" : f.Organization.Contains("INDEXED") ? "indexed" : variable ? "variable (VariableDataset)" : "fixed";
                var key = f.Organization.Contains("INDEXED") && f.RecordKey is { } k ? $", KEY_OFFSET/KEY_LENGTH ({k})" : "";
                sb.Append($"{fc.Class}: {f.Name}, DD {f.Assign}, {kind}, RECORD_LENGTH {Math.Max(f.MaxRecord, f.Records.Max(x => x.Length))}{key}; ");
                sb.Append(string.Join(", ", f.Records.Select(x => $"{Camel(x.Name)}(byte[] from) -> {x.Length} bytes"))).Append('\n');
            }
            sb.Append("```\n\n");
        }
        if (notes.Count > 0) sb.Append("Not covered by the kit: ").Append(string.Join("; ", notes.Distinct())).Append(".\n\n");
        return sb.ToString();
    }

    static string TypeOf(Field f) => Numeric(f.Kind) && f.Kind != "edited" ? NumericType(f.Item, f.Kind) : "String";

    /// <summary>A line's image as the reader of the report sees it: VALUE text, '_' over variable items.</summary>
    static string Image(Shape s)
    {
        var root = s.Primary.Item;
        var img = new char[s.Length];
        Array.Fill(img, ' ');
        foreach (var f in s.Fields.Where(f => f.Item.IsElementary))
            for (int k = 0; k < f.Item.Length * Math.Max(1, f.Dims.Count == 0 ? 1 : f.Dims[0].Count) && f.Offset + k < img.Length; k++) img[f.Offset + k] = '_';
        foreach (var it in Tree(root).Where(i => i.Values.Count > 0 && i.Dims.Count == root.Dims.Count && !UnderRedefines(i, root)))
            if (ConstantText(it) is { } text)
                for (int k = 0; k < text.Length && it.Offset - root.Offset + k < img.Length; k++) img[it.Offset - root.Offset + k] = text[k];
        return CommentSafe(new string(img).TrimEnd());
    }

    /// <summary>The FD records a line is written into: WRITE fd-record FROM line, or MOVE line TO fd-record.</summary>
    static List<(string Record, int Length, string FileClass)> Targets(CobolProgram m, Shape s, List<FileClass> files)
    {
        var ids = s.Roots.Select(x => x.Item.Id).ToHashSet();
        var list = new List<(string, int, string)>();
        foreach (var st in DdBindingPlanner.Walk(m))
        {
            IEnumerable<Node> recs = st.Verb == "WRITE" && st.Get("from") is { Id: { } fid } && ids.Contains(fid) && st.Get("record") is { } rec ? [rec]
                : st.Verb == "MOVE" && st.Get("src") is { Id: { } sid } && ids.Contains(sid) ? st.L?.GetValueOrDefault("dst") ?? [] : [];
            foreach (var n in recs)
                if (n.Id is { } id && m.Items.FirstOrDefault(i => i.Id == id) is { Level: 1, Section: DataSection.File } fdRec
                    && files.FirstOrDefault(f => f.Fd.Name == fdRec.FileName) is { } fc && !list.Any(x => x.Item1 == fdRec.Name))
                    list.Add((fdRec.Name, fdRec.Length, fc.Class));
        }
        return list;
    }

    static string Signature(Field f)
    {
        var p = string.Join(", ", f.Dims.Select((_, k) => "int " + Subs[k]));
        var type = f.JavaType ?? TypeOf(f);
        var set = Numeric(f.Kind) ? (f.Kind == "edited" ? "BigDecimal|long" : type == "long" ? "long|BigDecimal" : "BigDecimal|long") : "String";
        return $"{type} {f.Java}({p}) / {f.Java}({(p.Length > 0 ? p + ", " : "")}{set})";
    }

    // ------------------------------------------------------------------ layouts for the feedback

    /// <summary>
    /// mlcpu.record-layouts.v1: every layout a dataset of the step can hold (FD records, and the areas the host program writes
    /// FROM or moves into them), with field names, kinds and constant bytes (to recognise report lines), the statements that
    /// write each file, and the capability's own classes.
    /// </summary>
    static string Layouts(RecordKitRequest r, string pkg, List<Shape> shapes)
    {
        var host = r.Host;
        var hostRoots = Roots(host, false);
        var layouts = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string? ClassOf(DataItem it) => shapes.FirstOrDefault(s => s.Roots.Any(x => x.Item.Name == it.Name && x.Item.Length == it.Length))?.Class;
        void AddLayout(DataItem root, string role, string program)
        {
            if (!seen.Add(program + ":" + root.Name)) return;
            var fields = new JsonArray();
            foreach (var f in FieldsOf(root))
            {
                var o = new JsonObject
                {
                    ["name"] = f.Cobol, ["offset"] = f.Offset, ["length"] = f.Item.Length, ["kind"] = f.Kind, ["pic"] = f.Item.Pic,
                    ["digits"] = f.Item.Digits, ["scale"] = f.Item.Scale, ["signed"] = f.Item.Signed
                };
                if (f.Item.SignSeparate) o["signSeparate"] = true;
                if (f.Item.SignLeading) o["signLeading"] = true;
                if (f.Item.Category == Category.Group) o["group"] = true;
                if (f.Dims.Count > 0) o["occurs"] = new JsonArray(f.Dims.Select(d => (JsonNode)new JsonArray(d.Count, d.Stride)).ToArray());
                var accessor = shapes.FirstOrDefault(s => s.Roots.Any(x => x.Item == root))?.Fields.FirstOrDefault(x => x.Item == f.Item)?.Java;
                if (accessor is not null) o["accessor"] = accessor;
                fields.Add(o);
            }
            var constants = new JsonArray();
            if (root.Section is DataSection.WorkingStorage or DataSection.LocalStorage)
                foreach (var it in Tree(root).Where(i => i.Values.Count > 0 && i.Dims.Count == root.Dims.Count && !UnderRedefines(i, root)))
                    if (ConstantText(it) is { } text && (text.Trim().Length > 0 || it.IsFiller))
                        constants.Add(new JsonObject { ["offset"] = it.Offset - root.Offset, ["text"] = text, ["named"] = !it.IsFiller });
            layouts.Add(new JsonObject
            {
                ["name"] = root.Name, ["program"] = program, ["role"] = role, ["length"] = root.Length, ["class"] = ClassOf(root),
                ["fields"] = fields, ["constants"] = constants
            });
        }
        foreach (var s in shapes) foreach (var root in s.Roots) AddLayout(root.Item, root.Role, root.Program.ProgramId);
        foreach (var root in hostRoots) AddLayout(root.Item, root.Role, host.ProgramId);

        // Datasets of the step: each binding of the host program's files, with the layouts its records can have.
        var datasets = new JsonArray();
        var writers = new JsonArray();
        var byFile = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var paragraphOf = new Dictionary<Stmt, string>();
        foreach (var p in host.Paragraphs) foreach (var s in Flatten(p.Statements)) paragraphOf[s] = p.Name;
        foreach (var s in DdBindingPlanner.Walk(host))
        {
            if (s.Verb is not ("WRITE" or "REWRITE" or "MOVE")) continue;
            if (s.Verb == "MOVE")
            {
                var src = s.Get("src") is { Id: { } sid } ? host.Items.FirstOrDefault(i => i.Id == sid) : null;
                foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                    if (src is { Level: 1 or 77 } && RecordFile(host, d) is { } mf && host.Items.FirstOrDefault(i => i.Id == d.Id) is { Level: 1 })
                    {
                        (byFile.TryGetValue(mf, out var set) ? set : byFile[mf] = []).Add(src.Name);
                        writers.Add(new JsonObject { ["file"] = mf, ["verb"] = "MOVE", ["stepId"] = s.StepId, ["paragraph"] = paragraphOf.GetValueOrDefault(s), ["cobol"] = s.Text, ["layout"] = src.Name });
                    }
                continue;
            }
            var file = RecordFile(host, s.Get("record"));
            if (file is null) continue;
            var rec = s.Get("record") is { Id: { } rid } ? host.Items.FirstOrDefault(i => i.Id == rid) : null;
            var from = s.Get("from") is { Id: { } fid } ? host.Items.FirstOrDefault(i => i.Id == fid) : null;
            var layout = (from ?? rec)?.Name;
            if (layout is not null) (byFile.TryGetValue(file, out var set) ? set : byFile[file] = []).Add(layout);
            var adv = s.Str("advWhen") is { } when ? $"{when} {(s.Str("advPage") is not null ? "PAGE" : s.Get("advLines")?.V ?? "")}".Trim() : null;
            writers.Add(new JsonObject
            {
                ["file"] = file, ["verb"] = s.Verb, ["stepId"] = s.StepId, ["paragraph"] = paragraphOf.GetValueOrDefault(s), ["cobol"] = s.Text,
                ["record"] = rec?.Name, ["layout"] = layout, ["advancing"] = adv
            });
        }
        foreach (var b in r.HostBindings.Where(b => b.ResolvedDsn is not null && !b.IsDummy).DistinctBy(b => b.ResolvedDsn))
        {
            var owner = b.OwnerProgram.Equals(host.ProgramId, StringComparison.OrdinalIgnoreCase) ? host
                : b.OwnerProgram.Equals(r.Program.ProgramId, StringComparison.OrdinalIgnoreCase) ? r.Program : null;
            var fd = owner?.Files.FirstOrDefault(f => f.Name == b.LogicalFile);
            if (fd is null) continue;
            bool variable = !fd.Organization.Contains("INDEXED") && (string.Equals(fd.RecordingMode, "V", StringComparison.OrdinalIgnoreCase)
                            || fd.Records.Select(x => x.Length).Distinct().Count() > 1 || (fd.MinRecord > 0 && fd.MinRecord != fd.MaxRecord));
            int lrecl = Math.Max(fd.MaxRecord, fd.Records.Select(x => x.Length).DefaultIfEmpty(0).Max());
            var format = fd.Organization.Contains("LINE") ? "line" : variable ? "variable" : (IsPrintFile(owner!, fd) ? "print:" : "fixed:") + lrecl;
            var candidates = fd.Records.Select(x => x.Name).Concat(byFile.GetValueOrDefault(fd.Name) ?? []).Distinct().ToList();
            foreach (var c in candidates)
                if (owner!.Items.FirstOrDefault(i => i.Name == c && i.Level is 1 or 77) is { } it) AddLayout(it, "dataset record", owner.ProgramId);
            var d = new JsonObject
            {
                ["dsn"] = b.ResolvedDsn, ["dd"] = b.Dd, ["file"] = fd.Name, ["program"] = owner!.ProgramId, ["direction"] = b.Direction, ["format"] = format,
                ["layouts"] = new JsonArray(candidates.Select(c => (JsonNode)c).ToArray())
            };
            if (fd.Organization.Contains("INDEXED") && fd.RecordKey is { } key && fd.Records.SelectMany(Tree).FirstOrDefault(i => i.Name == key) is { } k)
            {
                d["keyOffset"] = k.Offset - k.Record.Offset;
                d["keyLength"] = k.Length;
            }
            datasets.Add(d);
        }
        var linkage = r.Kind == "subprogram"
            ? new JsonArray(r.Program.Using.Select(u => (JsonNode)u.ToUpperInvariant()).ToArray()) : new JsonArray();
        return new JsonObject
        {
            ["schema"] = Schema, ["programId"] = r.ProgramId, ["kind"] = r.Kind, ["host"] = host.ProgramId, ["package"] = pkg,
            ["classes"] = new JsonArray(shapes.Select(s => (JsonNode)new JsonObject
            {
                ["class"] = s.Class, ["roots"] = new JsonArray(s.Roots.Select(x => (JsonNode)x.Item.Name).ToArray()), ["line"] = s.Line
            }).ToArray()),
            ["linkage"] = linkage, ["datasets"] = datasets, ["layouts"] = layouts, ["writers"] = writers
        }.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
    }

    static IEnumerable<Stmt> Flatten(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null)
                foreach (var b in s.Blocks.Values)
                    foreach (var x in Flatten(b)) yield return x;
        }
    }

    /// <summary>The text a VALUE clause puts in an alphanumeric item (quoted literal, ALL literal, or a figurative), or null.</summary>
    static string? ConstantText(DataItem it)
    {
        if (Kind(it) is not ("text" or "group")) return null;
        var w = it.Values[0].Low;
        if (w.Quoted) return it.ValueAll ? Repeat(w.Text, it.Length) : PadText(w.Text, it.Length);
        return Figurative(w) switch { "SPACE" => new string(' ', it.Length), "ZERO" => new string('0', it.Length), _ => null };
    }
}
