using System.Text;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;
using MlCpu.Kit;

namespace MlCpu.CobolKb.Knowledge.Typed;

/// <summary>
/// Typed views of the record kit's record classes (typed-interface projection, step 1): one Java interface per record area,
/// with names that drop the COBOL prefixes, Java types (BigDecimal, long, String), enums for fields whose 88-level names
/// are single distinct values, testers for the other 88-level names, and one nested view per OCCURS table occurrence
/// (<c>model.rp(k).ip(j).outstBal()</c>). The byte-backed implementation underneath is the kit class (its MOVE rules,
/// encodings and VALUE clauses), so a view stores exactly what the COBOL program stores.
/// </summary>
sealed class TypedRecords
{
    /// <summary>One member of a view: how the logic reads and writes an item.</summary>
    public sealed record Member(string Name, string Type, KitField Kit, string KitAccessor, View Owner, EnumInfo? Enum, bool Group);

    public sealed record EnumInfo(string Type, IReadOnlyList<(string Constant, string Value, string Condition)> Values, bool Text);

    /// <summary>A record or one occurrence of a table in it.</summary>
    public sealed class View
    {
        public required string Interface;
        public required string Cobol;
        public required int Depth;
        public View? Parent;
        public string? Accessor;            // on the parent: rp(int k)
        public IReadOnlyList<(int Count, int Stride)> Dims = [];
        /// <summary>The occurrence's own group item in the kit (its bytes as text: a group MOVE of a whole occurrence).</summary>
        public KitField? Owner;
        public readonly List<Member> Members = [];
        public readonly List<View> Children = [];
        public readonly List<(string Name, string Condition, string Test, string Set, KitField Field)> Testers = [];
    }

    public sealed class Record
    {
        public required KitLayout Layout;
        public required string Interface;
        public required string KitClass;
        public required View Root;
        public required string Comment;
    }

    readonly Dictionary<string, string> kitTypes;      // "Class.accessor" → Java type of the getter
    readonly CobolProgram program;
    public readonly Dictionary<string, Record> ByLayout = new(StringComparer.Ordinal);
    /// <summary>Elementary or group item name (in a record view) → its member.</summary>
    public readonly Dictionary<(string Record, string Item), Member> Members = [];
    /// <summary>A table's occurrence group item → its view.</summary>
    public readonly Dictionary<(string Record, string Item), View> Occurrences = [];

    /// <summary>
    /// The member at an item's place in a record (offset and length from the record's start, table dimensions): an item named
    /// otherwise than the kit names it (the same copybook under another prefix, COPY … REPLACING) is still the same bytes.
    /// </summary>
    public Member? ByPlace(string record, int offset, int length, int dims) =>
        Members.Where(kv => kv.Key.Record == record).Select(kv => kv.Value)
            .FirstOrDefault(m => m.Kit.Offset == offset && m.Kit.Length == length && m.Kit.Occurs.Count == dims);

    public View? OccurrenceByPlace(string record, int offset, int length, int dims) =>
        Occurrences.Where(kv => kv.Key.Record == record).Select(kv => kv.Value)
            .FirstOrDefault(v => v.Owner is { } o && o.Offset == offset && o.Length == length && o.Occurs.Count == dims);
    /// <summary>88-level name → (member of its field, enum constant or tester).</summary>
    public readonly Dictionary<string, (Member Field, string? Constant, (string Name, string Condition, string Test, string Set, KitField Field)? Tester)> Conditions =
        new(StringComparer.Ordinal);

    public TypedRecords(CobolProgram program, IReadOnlyDictionary<string, string> kitSources)
    {
        this.program = program;
        kitTypes = new(StringComparer.Ordinal);
        foreach (var (cls, src) in kitSources)
            foreach (Match mt in Regex.Matches(src, @"public (long|BigDecimal|String|boolean) (\w+)\((?:int \w+(?:, int \w+)*)?\) \{"))
                kitTypes.TryAdd(cls + "." + mt.Groups[2].Value, mt.Groups[1].Value);
    }

    static readonly HashSet<string> Reserved = new(StringComparer.Ordinal)
    {
        "abstract", "assert", "boolean", "break", "byte", "case", "catch", "char", "class", "const", "continue", "default", "do", "double", "else",
        "enum", "extends", "final", "finally", "float", "for", "goto", "if", "implements", "import", "instanceof", "int", "interface", "long",
        "native", "new", "package", "private", "protected", "public", "return", "short", "static", "strictfp", "super", "switch", "synchronized",
        "this", "throw", "throws", "transient", "try", "void", "volatile", "while", "record", "var", "yield", "true", "false", "null",
        "bytes", "initialize", "over", "create", "toString", "hashCode", "equals", "getClass", "wait", "notify", "notifyAll", "clone", "finalize"
    };

    public static string Camel(string cobol)
    {
        var parts = cobol.Split('-', StringSplitOptions.RemoveEmptyEntries).Select(p => p.ToLowerInvariant()).ToList();
        if (parts.Count == 0) return "x";
        var s = parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
        if (char.IsDigit(s[0])) s = "n" + s;
        return s;
    }

    public static string Pascal(string cobol) { var c = Camel(cobol); return char.ToUpperInvariant(c[0]) + c[1..]; }

    static string CommonPrefix(IEnumerable<string> names)
    {
        var list = names.ToList();
        if (list.Count == 0) return "";
        var p = list[0];
        foreach (var n in list.Skip(1))
        {
            int k = 0;
            while (k < p.Length && k < n.Length && p[k] == n[k]) k++;
            p = p[..k];
        }
        int dash = p.LastIndexOf('-');
        return dash > 0 ? p[..(dash + 1)] : "";
    }

    static string Strip(string name, string prefix) => prefix.Length > 0 && name.StartsWith(prefix, StringComparison.Ordinal) && name.Length > prefix.Length ? name[prefix.Length..] : name;

    /// <summary>The interface name of a record: its name without a short leading qualifier (FN-LOAN-TERMS → LoanTerms) when that stays unique.</summary>
    public static string InterfaceName(string record, ISet<string> used)
    {
        var parts = record.Split('-');
        bool generic = parts.Length == 2 && parts[1] is "PARMS" or "PARM" or "AREA" or "REC" or "RECORD" or "DATA" or "WORK" or "FIELDS" or "REQUEST";
        var shortName = parts.Length >= 2 && parts[0].Length <= 2 && !generic ? Pascal(string.Join("-", parts.Skip(1))) : Pascal(record);
        var name = shortName;
        if (used.Contains(name) && name != Pascal(record) && !used.Contains(name + "Record")) name += "Record";
        if (used.Contains(name)) name = Pascal(record) + "View";
        while (!used.Add(name)) name += "View";
        return name;
    }

    /// <summary>Adds the view of a record-kit record (a layout of record-layouts.json with a class).</summary>
    public Record Add(KitLayout layout, string iface, string comment)
    {
        var kitClass = layout.Class ?? throw new InvalidOperationException($"{layout.Name} has no record kit class");
        var root = new View { Interface = iface, Cobol = layout.Name, Depth = 0 };
        var rec = new Record { Layout = layout, Interface = iface, KitClass = kitClass, Root = root, Comment = comment };
        ByLayout[layout.Name] = rec;
        var topPrefix = CommonPrefix(layout.Fields.Select(f => f.Name));
        // Views per OCCURS depth: the group item whose dimensions are exactly those of its columns.
        var views = new Dictionary<string, View> { [""] = root };
        View ViewOf(IReadOnlyList<(int Count, int Stride)> dims)
        {
            var key = string.Join(";", dims.Select(d => $"{d.Count}x{d.Stride}"));
            if (views.TryGetValue(key, out var v)) return v;
            var parent = ViewOf(dims.Take(dims.Count - 1).ToList());
            var owner = layout.Fields.Where(f => f.Group && f.Occurs.Count == dims.Count && f.Occurs.SequenceEqual(dims)).OrderBy(f => f.Offset).FirstOrDefault();
            var ownerName = owner?.Name ?? layout.Fields.First(f => f.Occurs.Count == dims.Count && f.Occurs.SequenceEqual(dims)).Name;
            var vName = Pascal(Strip(ownerName, topPrefix));
            var used = new HashSet<string>(views.Values.Select(x => x.Interface).Append(iface), StringComparer.Ordinal);
            while (used.Contains(vName)) vName += "Entry";
            v = new View
            {
                Interface = vName, Cobol = ownerName, Depth = dims.Count, Parent = parent, Dims = dims, Accessor = Unique(Camel(Strip(ownerName, topPrefix)), parent),
                Owner = owner is { Accessor: not null } ? owner : null
            };
            parent.Children.Add(v);
            views[key] = v;
            Occurrences[(layout.Name, ownerName)] = v;
            return v;
        }
        foreach (var f in layout.Fields.Where(f => f.Accessor is not null).OrderBy(f => f.Offset))
        {
            var view = ViewOf(f.Occurs);
            if (f.Group && f.Occurs.Count > 0 && view.Cobol == f.Name) continue; // the occurrence itself
            var prefix = view.Depth == 0 ? topPrefix
                : CommonPrefix(layout.Fields.Where(x => x.Occurs.Count == view.Depth && x.Occurs.SequenceEqual(view.Dims) && x.Name != view.Cobol).Select(x => x.Name));
            if (prefix.Length == 0) prefix = topPrefix;
            var type = f.Group ? "String" : kitTypes.GetValueOrDefault(kitClass + "." + f.Accessor) ?? (f.Kind == "text" ? "String" : "BigDecimal");
            var item = program.Items.FirstOrDefault(i => i.Name == f.Name && i.Level != 88 && i.Record?.Name == layout.Name)
                       ?? program.Items.FirstOrDefault(i => i.Name == f.Name && i.Level != 88);
            var conds = item?.Children.Where(c => c.Level == 88).ToList() ?? [];
            EnumInfo? enumInfo = null;
            var name = Unique(Camel(Strip(f.Name, prefix)), view);
            if (!f.Group && conds.Count >= 2 && conds.All(c => c.Values.Count == 1 && c.Values[0].High is null)
                && conds.Select(c => c.Values[0].Low.Text).Distinct().Count() == conds.Count)
            {
                var cp = CommonPrefix(conds.Select(c => c.Name));
                var constants = new HashSet<string>(StringComparer.Ordinal);
                var values = conds.Select(c =>
                {
                    var k = Strip(c.Name, cp).Replace('-', '_').ToUpperInvariant();
                    if (k.Length == 0 || char.IsDigit(k[0])) k = "V" + k;
                    while (!constants.Add(k)) k += "_";
                    return (k, c.Values[0].Low.Text, c.Name);
                }).ToList();
                bool text = f.Kind == "text" || conds.All(c => c.Values[0].Low.Quoted);
                if (text == (type == "String"))
                    enumInfo = new EnumInfo(Unique(char.ToUpperInvariant(name[0]) + name[1..], view, true), values, text);
            }
            var member = new Member(name, type, f, f.Accessor!, view, enumInfo, f.Group);
            view.Members.Add(member);
            Members[(layout.Name, f.Name)] = member;
            if (enumInfo is not null)
                foreach (var (k, _, c) in enumInfo.Values) Conditions.TryAdd(c, (member, k, null));
            else
                foreach (var c in conds)
                {
                    var tName = Unique(Pascal(Strip(c.Name, prefix)), view, true);
                    // The test and the SET TO TRUE, on the item's own value ({IDX} stands for the occurrence's subscripts).
                    var get = $"r.{f.Accessor}({{IDX}})";
                    var tests = c.Values.Select(v => TesterTerm(get, type, f, v.Low, v.High)).ToList();
                    var first = c.Values.FirstOrDefault().Low;
                    var set = first is null ? "" : $"r.{f.Accessor}({{IDX,}}{(type == "String" ? Lit(first.Text) : type == "long" ? Num(first.Text) : $"new BigDecimal(\"{first.Text}\")")})";
                    var tester = ("is" + tName, c.Name, string.Join(" || ", tests), set, f);
                    view.Testers.Add(tester);
                    Conditions.TryAdd(c.Name, (member, null, tester));
                }
        }
        return rec;
    }

    static string Unique(string name, View view, bool type = false)
    {
        if (Reserved.Contains(name)) name += type ? "Kind" : "Value";
        var used = view.Members.Select(m => m.Name).Concat(view.Children.Select(c => c.Accessor ?? "")).Concat(view.Testers.Select(t => t.Name))
            .Concat(view.Members.Where(m => m.Enum is not null).Select(m => m.Enum!.Type)).ToHashSet(StringComparer.Ordinal);
        var n = name;
        for (int i = 2; used.Contains(n) || used.Contains(n + "Code"); i++) n = name + i;
        return n;
    }

    // ------------------------------------------------------------------ Java source of a record view

    public string Source(Record rec, string package, string kitPackage, bool apiOnly = false)
    {
        var sb = new StringBuilder();
        sb.Append($"package {package};\n\n");
        sb.Append("import java.math.BigDecimal;\n");
        sb.Append($"import {kitPackage}.{rec.KitClass};\n\n");
        sb.Append($"/**\n * {rec.Comment}\n * Typed view of {rec.Layout.Name} ({rec.Layout.Length} bytes); the bytes underneath are the record kit's {rec.KitClass}, which\n");
        sb.Append(" * stores with the COBOL MOVE rules. Text getters drop trailing spaces; setters pad. Generated (typed-interface projection).\n */\n");
        sb.Append($"public interface {rec.Interface} {{\n");
        if (apiOnly)
        {
            sb.Append($"    /** A view over a CALL argument's bytes (no copy). */\n    static {rec.Interface} over(byte[] bytes);\n\n");
            sb.Append($"    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */\n    static {rec.Interface} create();\n\n");
        }
        else
        {
            sb.Append($"    /** A view over a CALL argument's bytes (no copy). */\n    static {rec.Interface} over(byte[] bytes) {{\n        return new Bytes({rec.KitClass}.wrap(bytes));\n    }}\n\n");
            sb.Append($"    /** A new area as WORKING-STORAGE starts: x'00' bytes, then the VALUE clauses. */\n    static {rec.Interface} create() {{\n        return new Bytes(new {rec.KitClass}((byte) 0));\n    }}\n\n");
        }
        sb.Append("    /** The area itself (what a CALL passes BY REFERENCE). */\n    byte[] bytes();\n\n");
        sb.Append("    /** INITIALIZE: text items to spaces, numeric items to zero. */\n    void initialize();\n");
        Declarations(sb, rec.Root, "    ");
        if (apiOnly)
        {
            foreach (var child in AllViews(rec.Root).Skip(1))
            {
                sb.Append($"\n    /** One occurrence of {child.Cobol}. */\n    interface {child.Interface} {{");
                Declarations(sb, child, "        ");
                sb.Append("    }\n");
            }
            return sb.Append("}\n").ToString();
        }
        sb.Append($"\n    /** The record kit's {rec.KitClass} behind the view. */\n");
        sb.Append($"    final class Bytes implements {rec.Interface} {{\n        private final {rec.KitClass} r;\n\n        Bytes({rec.KitClass} r) {{\n            this.r = r;\n        }}\n\n");
        sb.Append("        public byte[] bytes() {\n            return r.bytes();\n        }\n\n        public void initialize() {\n            r.initialize();\n        }\n");
        Implementations(sb, rec, rec.Root, "        ", "");
        sb.Append("    }\n");
        foreach (var child in rec.Root.Children) OccurrenceClass(sb, rec, child, "    ");
        sb.Append("\n    private static String strip(String s) {\n        int n = s.length();\n        while (n > 0 && s.charAt(n - 1) == ' ') n--;\n        return s.substring(0, n);\n    }\n");
        sb.Append("}\n");
        return sb.ToString();
    }

    /// <summary>A view's API one line per item (getter / setter, the COBOL item), for a prompt: the record kit summary's form.</summary>
    public string Summary(Record rec)
    {
        var sb = new StringBuilder($"interface {rec.Interface}   // {rec.Layout.Name}, {rec.Layout.Length} bytes: {rec.Comment}\n");
        sb.Append($"    static {rec.Interface} over(byte[] bytes) / create(); byte[] bytes(); void initialize()\n");
        foreach (var v in AllViews(rec.Root))
        {
            var ind = v.Depth == 0 ? "    " : "        ";
            if (v.Depth > 0) sb.Append($"    interface {v.Interface}   // one occurrence of {v.Cobol} (OCCURS {v.Dims[^1].Count})\n");
            var lines = new List<(string Api, string Cobol)>();
            if (v.Depth > 0 && v.Owner is not null) lines.Add(("String raw() / raw(String)", "the occurrence's bytes (a MOVE of the whole occurrence)"));
            foreach (var c in v.Children) lines.Add(($"{c.Interface} {c.Accessor}(int {(c.Depth == 1 ? "k" : "j")})", $"{c.Cobol} OCCURS {c.Dims[^1].Count}, 1-based, not checked"));
            foreach (var m in v.Members)
            {
                var num = m.Type == "long" ? "long|BigDecimal" : m.Type == "BigDecimal" && !m.Group ? "BigDecimal|long" : m.Type;
                lines.Add(m.Enum is { } e
                    ? ($"{e.Type} {m.Name}() / {m.Name}({e.Type}); {m.Type} {m.Name}Code() / {m.Name}Code({num})", Comment(m.Kit))
                    : ($"{m.Type} {m.Name}() / {m.Name}({num})", Comment(m.Kit)));
            }
            foreach (var t in v.Testers) lines.Add(($"boolean {t.Name}() / set{t.Name[2..]}()", "88 " + t.Condition));
            int width = Math.Min(70, lines.Select(l => l.Api.Length).DefaultIfEmpty(0).Max() + 2);
            foreach (var (api, cobol) in lines) sb.Append(ind).Append(api.PadRight(width)).Append("// ").Append(cobol).Append('\n');
            foreach (var m in v.Members.Where(m => m.Enum is not null))
                sb.Append(ind).Append($"enum {m.Enum!.Type} {{ {string.Join(", ", m.Enum.Values.Select(x => $"{x.Constant}({(m.Enum.Text ? Lit(x.Value) : x.Value)})"))} }}   // .code, of(code)\n");
            if (v.Depth > 0) sb.Append("    }\n");
        }
        return sb.ToString();
    }

    static IEnumerable<View> AllViews(View v)
    {
        yield return v;
        foreach (var c in v.Children)
            foreach (var x in AllViews(c)) yield return x;
    }

    static string Comment(KitField f) =>
        $"{f.Name}{(f.Group ? " (group)" : f.Pic is null ? "" : " PIC " + f.Pic + (f.Kind switch { "packed" => " COMP-3", "binary" => " COMP", _ => "" }))}";

    void Declarations(StringBuilder sb, View v, string ind)
    {
        foreach (var c in v.Children)
            sb.Append($"\n{ind}/** {c.Cobol} OCCURS {c.Dims[^1].Count}: occurrence {(c.Depth == 1 ? "k" : "j")} (1-based, not checked, as in COBOL). */\n{ind}{c.Interface} {c.Accessor}(int {(c.Depth == 1 ? "k" : "j")});\n");
        foreach (var m in v.Members)
        {
            sb.Append($"\n{ind}/** {Comment(m.Kit)}{(m.Enum is not null ? "; its 88-level names as " + m.Enum.Type : "")} */\n");
            if (m.Enum is { } e)
            {
                sb.Append($"{ind}{e.Type} {m.Name}();\n\n{ind}void {m.Name}({e.Type} v);\n\n");
                sb.Append($"{ind}/** The value itself (any value, also one without an 88-level name). */\n{ind}{m.Type} {m.Name}Code();\n\n{ind}void {m.Name}Code({m.Type} v);\n");
                if (m.Type == "long") sb.Append($"\n{ind}void {m.Name}Code(BigDecimal v);\n");
            }
            else
            {
                sb.Append($"{ind}{m.Type} {m.Name}();\n\n{ind}void {m.Name}({m.Type} v);\n");
                if (m.Type == "long") sb.Append($"\n{ind}void {m.Name}(BigDecimal v);\n");
                if (m.Type == "BigDecimal" && !m.Group) sb.Append($"\n{ind}void {m.Name}(long v);\n");
            }
        }
        foreach (var t in v.Testers)
            sb.Append($"\n{ind}/** 88 {t.Condition} */\n{ind}boolean {t.Name}();\n\n{ind}void set{t.Name[2..]}();\n");
        foreach (var m in v.Members.Where(m => m.Enum is not null))
        {
            var e = m.Enum!;
            sb.Append($"\n{ind}/** The 88-level names of {m.Kit.Name}. */\n{ind}enum {e.Type} {{\n");
            sb.Append(string.Join(",\n", e.Values.Select(x => $"{ind}    /** 88 {x.Condition} */\n{ind}    {x.Constant}({(e.Text ? Lit(x.Value) : Num(x.Value))})"))).Append(";\n\n");
            sb.Append($"{ind}    public final {(e.Text ? "String" : "long")} code;\n\n{ind}    {e.Type}({(e.Text ? "String" : "long")} code) {{\n{ind}        this.code = code;\n{ind}    }}\n\n");
            sb.Append($"{ind}    /** The constant for a value, or null when no 88-level name has it. */\n{ind}    public static {e.Type} of({(e.Text ? "String" : "long")} code) {{\n");
            sb.Append($"{ind}        for ({e.Type} x : values()) if ({(e.Text ? "x.code.equals(code)" : "x.code == code")}) return x;\n{ind}        return null;\n{ind}    }}\n{ind}}}\n");
        }
    }

    static string Lit(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>One VALUE (or VALUE THRU) of an 88-level name tested on the item's raw value (text compared space padded).</summary>
    static string TesterTerm(string get, string type, KitField f, RawWord low, RawWord? high)
    {
        if (type == "String")
        {
            if (high is null) return $"{get}.equals({Lit(low.Text.PadRight(f.Length))})";
            return $"({get}.compareTo({Lit(low.Text.PadRight(f.Length))}) >= 0 && {get}.compareTo({Lit(high.Text.PadRight(f.Length))}) <= 0)";
        }
        if (type == "long")
            return high is null ? $"{get} == {Num(low.Text)}" : $"({get} >= {Num(low.Text)} && {get} <= {Num(high.Text)})";
        return high is null ? $"{get}.compareTo(new BigDecimal(\"{low.Text}\")) == 0"
            : $"({get}.compareTo(new BigDecimal(\"{low.Text}\")) >= 0 && {get}.compareTo(new BigDecimal(\"{high.Text}\")) <= 0)";
    }

    static string Num(string s) => long.TryParse(s.TrimStart('+'), out var l) ? l + "L" : "0L /* " + s + " */";

    void Implementations(StringBuilder sb, Record rec, View v, string ind, string idx)
    {
        foreach (var c in v.Children)
        {
            var p = c.Depth == 1 ? "k" : "j";
            sb.Append($"\n{ind}public {c.Interface} {c.Accessor}(int {p}) {{\n{ind}    return new {c.Interface}Bytes(r{(idx.Length > 0 ? ", " + idx : "")}, {p});\n{ind}}}\n");
        }
        foreach (var m in v.Members)
        {
            var get = $"r.{m.KitAccessor}({idx})";
            string set(string val) => $"r.{m.KitAccessor}({(idx.Length > 0 ? idx + ", " : "")}{val})";
            var getter = m.Type == "String" && !m.Group ? $"strip({get})" : get;
            if (m.Enum is { } e)
            {
                sb.Append($"\n{ind}public {e.Type} {m.Name}() {{\n{ind}    return {e.Type}.of({getter});\n{ind}}}\n");
                sb.Append($"\n{ind}public void {m.Name}({e.Type} v) {{\n{ind}    {set("v.code")};\n{ind}}}\n");
                sb.Append($"\n{ind}public {m.Type} {m.Name}Code() {{\n{ind}    return {getter};\n{ind}}}\n");
                sb.Append($"\n{ind}public void {m.Name}Code({m.Type} v) {{\n{ind}    {set("v")};\n{ind}}}\n");
                if (m.Type == "long") sb.Append($"\n{ind}public void {m.Name}Code(BigDecimal v) {{\n{ind}    {set("v")};\n{ind}}}\n");
                continue;
            }
            sb.Append($"\n{ind}public {m.Type} {m.Name}() {{\n{ind}    return {getter};\n{ind}}}\n");
            sb.Append($"\n{ind}public void {m.Name}({m.Type} v) {{\n{ind}    {set("v")};\n{ind}}}\n");
            if (m.Type == "long") sb.Append($"\n{ind}public void {m.Name}(BigDecimal v) {{\n{ind}    {set("v")};\n{ind}}}\n");
            if (m.Type == "BigDecimal" && !m.Group) sb.Append($"\n{ind}public void {m.Name}(long v) {{\n{ind}    {set("v")};\n{ind}}}\n");
        }
        foreach (var t in v.Testers)
        {
            var test = t.Test.Replace("{IDX}", idx);
            var set = t.Set.Replace("{IDX,}", idx.Length > 0 ? idx + ", " : "");
            var setName = "set" + t.Name[2..];
            sb.Append($"\n{ind}public boolean {t.Name}() {{\n{ind}    return {test};\n{ind}}}\n");
            sb.Append($"\n{ind}public void {setName}() {{\n{ind}    {set};\n{ind}}}\n");
        }
    }

    void OccurrenceClass(StringBuilder sb, Record rec, View v, string ind)
    {
        var idxParams = v.Depth == 1 ? "int k" : "int k, int j";
        var idxArgs = v.Depth == 1 ? "k" : "k, j";
        sb.Append($"\n{ind}/** One occurrence of {v.Cobol}. */\n{ind}interface {v.Interface} {{");
        if (v.Owner is not null)
            sb.Append($"\n{ind}    /** The occurrence's bytes as text (a MOVE of the whole occurrence copies them). */\n{ind}    String raw();\n\n{ind}    void raw(String v);\n");
        Declarations(sb, v, ind + "    ");
        sb.Append($"{ind}}}\n");
        sb.Append($"\n{ind}final class {v.Interface}Bytes implements {v.Interface} {{\n{ind}    private final {rec.KitClass} r;\n");
        sb.Append(v.Depth == 1 ? $"{ind}    private final int k;\n" : $"{ind}    private final int k, j;\n");
        sb.Append($"\n{ind}    {v.Interface}Bytes({rec.KitClass} r, {idxParams}) {{\n{ind}        this.r = r;\n{ind}        this.k = k;\n");
        if (v.Depth > 1) sb.Append($"{ind}        this.j = j;\n");
        sb.Append($"{ind}    }}\n");
        if (v.Owner is not null)
            sb.Append($"\n{ind}    public String raw() {{\n{ind}        return r.{v.Owner.Accessor}({idxArgs});\n{ind}    }}\n\n{ind}    public void raw(String v) {{\n{ind}        r.{v.Owner.Accessor}({idxArgs}, v);\n{ind}    }}\n");
        Implementations(sb, rec, v, ind + "    ", idxArgs);
        sb.Append($"{ind}}}\n");
        foreach (var c in v.Children) OccurrenceClass(sb, rec, c, ind);
    }
}
