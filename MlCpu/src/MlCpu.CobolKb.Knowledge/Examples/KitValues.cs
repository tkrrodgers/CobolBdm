using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;

namespace MlCpu.Kit;

// Shared by MlCpu.CobolKb.Knowledge (the kit: worked examples at publish) and MlCpu.DesignModel (CALL-level shadow feedback,
// the kit upgrade and the prompt measurements), linked into both as source so that the model reads values in one form.

/// <summary>An item of a record layout as normalization/&lt;PID&gt;/record-layouts.json (the record kit's layouts) gives it.</summary>
public sealed record KitField(string Name, int Offset, int Length, string Kind, string? Pic, int Digits, int Scale, bool Signed,
    bool SignSeparate, bool SignLeading, bool Group, IReadOnlyList<(int Count, int Stride)> Occurs, string? Accessor)
{
    public bool Numeric => Kind is "packed" or "binary" or "zoned";
}

public sealed record KitLayout(string Name, string Program, string Role, int Length, string? Class, IReadOnlyList<KitField> Fields);

/// <summary>The layouts of record-layouts.json, and the LINKAGE records in USING order (a subprogram's CALL arguments).</summary>
public sealed class KitLayouts
{
    public IReadOnlyList<KitLayout> All { get; }
    public IReadOnlyList<string> LinkageNames { get; }

    KitLayouts(List<KitLayout> all, List<string> linkage) { All = all; LinkageNames = linkage; }

    public static KitLayouts Of(IEnumerable<KitLayout> layouts, IEnumerable<string> linkage) => new(layouts.ToList(), linkage.ToList());

    public static KitLayouts Parse(string json)
    {
        var j = JsonNode.Parse(json)!;
        var all = new List<KitLayout>();
        foreach (var l in j["layouts"]?.AsArray().OfType<JsonObject>() ?? [])
        {
            var fields = l["fields"]!.AsArray().OfType<JsonObject>().Select(f => new KitField(
                f["name"]!.GetValue<string>(), f["offset"]!.GetValue<int>(), f["length"]!.GetValue<int>(), f["kind"]!.GetValue<string>(),
                f["pic"]?.GetValue<string>(), f["digits"]?.GetValue<int>() ?? 0, f["scale"]?.GetValue<int>() ?? 0, f["signed"]?.GetValue<bool>() ?? false,
                f["signSeparate"]?.GetValue<bool>() ?? false, f["signLeading"]?.GetValue<bool>() ?? false, f["group"]?.GetValue<bool>() ?? false,
                f["occurs"]?.AsArray().Select(o => (o![0]!.GetValue<int>(), o[1]!.GetValue<int>())).ToList() ?? [], f["accessor"]?.GetValue<string>())).ToList();
            all.Add(new KitLayout(l["name"]!.GetValue<string>(), l["program"]?.GetValue<string>() ?? "", l["role"]?.GetValue<string>() ?? "",
                l["length"]!.GetValue<int>(), l["class"]?.GetValue<string>(), fields));
        }
        return new KitLayouts(all, j["linkage"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? []);
    }

    /// <summary>The layout of the i-th USING argument: the LINKAGE record of that name (a CALL USING view of the same name otherwise).</summary>
    public KitLayout? Argument(int i, int length)
    {
        if (i >= LinkageNames.Count) return null;
        var name = LinkageNames[i];
        return All.FirstOrDefault(l => l.Name == name && l.Role.Contains("LINKAGE", StringComparison.Ordinal))
               ?? All.FirstOrDefault(l => l.Name == name && l.Length == length) ?? All.FirstOrDefault(l => l.Name == name);
    }
}

/// <summary>Field values as the program sees them: numbers decoded with their scale, text without trailing spaces, raw bytes only where needed.</summary>
public static class KitValues
{
    /// <summary>The conventions, for a prompt's legend.</summary>
    public const string Legend =
        "Values are shown as the program reads them: numbers (packed COMP-3, binary COMP/COMP-5, zoned) decoded with their PICTURE's scale " +
        "(1000.0000 is PIC S9(13)V9(4) holding one thousand); text in quotes without its trailing spaces ('USD' in a PIC X(10) is \"USD\" and " +
        "seven spaces); (spaces) for a text item that is all spaces; x'00' for a text, packed or zoned item whose bytes are all x'00' " +
        "(storage nobody has written: LOW-VALUES, which is not a valid packed number; a binary item of x'00' bytes is simply 0); raw bytes between « and » only for text with bytes that are not printable " +
        "(\\x00 is byte 0) and for a number whose bytes are not valid in its USAGE. Items still all spaces or all x'00' are left out of an " +
        "argument's listing. An OCCURS table is shown as rows, only the occurrences in use (rows not listed are blank), with the columns " +
        "that vary; a column with one value in every row listed is given once.";

    public static bool Initial(ReadOnlySpan<byte> b)
    {
        if (b.Length == 0) return true;
        bool spaces = true, zeros = true;
        foreach (var x in b) { if (x != (byte)' ') spaces = false; if (x != 0) zeros = false; }
        return spaces || zeros;
    }

    public static ReadOnlySpan<byte> Slice(byte[] r, int off, int len) =>
        off >= r.Length ? ReadOnlySpan<byte>.Empty : r.AsSpan(off, Math.Min(len, r.Length - off));

    /// <summary>The value of an elementary item at <paramref name="off"/> in <paramref name="r"/>, in the prompt's form.</summary>
    public static string Show(KitField f, byte[] r, int off) => Show(f, Slice(r, off, f.Length));

    public static string Show(KitField f, ReadOnlySpan<byte> v)
    {
        if (v.Length == 0) return "(absent)";
        bool zeros = true, spaces = true;
        foreach (var x in v) { if (x != 0) zeros = false; if (x != (byte)' ') spaces = false; }
        // All x'00' is a valid binary zero, but not a valid packed or zoned number nor text anyone wrote.
        if (zeros && f.Kind != "binary") return "x'00'";
        if (f.Numeric)
        {
            if (Decode(f, v) is { } n) return n;
            if (spaces) return "(spaces)";
            return "«" + Escape(v) + "»";
        }
        if (spaces) return "(spaces)";
        if (Printable(v)) return "'" + Encoding.Latin1.GetString(v).TrimEnd(' ') + "'";
        return "«" + Escape(v) + "»";
    }

    /// <summary>True when the value decodes as a number equal to zero.</summary>
    public static bool IsZero(KitField f, ReadOnlySpan<byte> v) => f.Numeric && Decode(f, v) is { } s && s.TrimStart('-').All(c => c is '0' or '.');

    static bool Printable(ReadOnlySpan<byte> v)
    {
        foreach (var x in v) if (x is < 0x20 or >= 0x7F) return false;
        return true;
    }

    public static string Escape(ReadOnlySpan<byte> b)
    {
        var sb = new StringBuilder(b.Length);
        foreach (var x in b)
        {
            if (x == '\n') sb.Append("\\n");
            else if (x == '\f') sb.Append("\\f");
            else if (x is >= 0x20 and < 0x7F) sb.Append((char)x);
            else sb.Append($"\\x{x:x2}");
        }
        return sb.ToString();
    }

    /// <summary>The number a numeric item holds (sign overpunch, packed, binary) with its scale, or null when the bytes are not valid.</summary>
    public static string? Decode(KitField f, ReadOnlySpan<byte> v)
    {
        if (v.Length == 0) return null;
        BigInteger u = 0;
        bool neg = false;
        switch (f.Kind)
        {
            case "zoned":
            {
                int ds = 0, de = v.Length;
                if (f.Signed && f.SignSeparate)
                {
                    var sign = f.SignLeading ? v[0] : v[^1];
                    if (sign is not ((byte)'-' or (byte)'+')) return null;
                    neg = sign == '-';
                    if (f.SignLeading) ds++; else de--;
                }
                for (int i = ds; i < de; i++)
                {
                    int c = v[i], d;
                    bool signPos = f.Signed && !f.SignSeparate && (f.SignLeading ? i == ds : i == de - 1);
                    if (c is >= '0' and <= '9') d = c - '0';
                    else if (signPos && c == '{') d = 0;
                    else if (signPos && c is >= 'A' and <= 'I') d = c - 'A' + 1;
                    else if (signPos && c == '}') { d = 0; neg = true; }
                    else if (signPos && c is >= 'J' and <= 'R') { d = c - 'J' + 1; neg = true; }
                    else return null;
                    u = u * 10 + d;
                }
                break;
            }
            case "packed":
                for (int i = 0; i < v.Length; i++)
                {
                    int hi = v[i] >> 4, lo = v[i] & 0x0F;
                    if (hi > 9) return null;
                    u = u * 10 + hi;
                    if (i < v.Length - 1) { if (lo > 9) return null; u = u * 10 + lo; }
                    else if (lo < 0x0A) return null;
                    else neg = lo is 0x0D or 0x0B;
                }
                break;
            case "binary":
                u = new BigInteger(v, isUnsigned: !f.Signed, isBigEndian: true);
                break;
            default: return null;
        }
        if (neg) u = -u;
        if (f.Scale <= 0) return (u * BigInteger.Pow(10, -f.Scale)).ToString();
        var s = BigInteger.Abs(u).ToString().PadLeft(f.Scale + 1, '0');
        return (u.Sign < 0 ? "-" : "") + s[..^f.Scale] + "." + s[^f.Scale..];
    }

    /// <summary>"1..6", "1,3,5", "2..4,9".</summary>
    public static string Ranges(IEnumerable<int> values)
    {
        var v = values.Distinct().Order().ToList();
        var parts = new List<string>();
        for (int i = 0; i < v.Count;)
        {
            int j = i;
            while (j + 1 < v.Count && v[j + 1] == v[j] + 1) j++;
            parts.Add(j == i ? $"{v[i]}" : j == i + 1 ? $"{v[i]},{v[j]}" : $"{v[i]}..{v[j]}");
            i = j + 1;
        }
        return string.Join(",", parts);
    }
}

/// <summary>
/// One record image (a CALL argument) rendered in values: the plain items, then each OCCURS table as rows; and the changes
/// between two images (before → after) item by item.
/// </summary>
public sealed class RecordView
{
    readonly KitLayout layout;
    readonly List<KitField> plain;
    readonly List<Table> tables;

    public sealed record Table(string Name, IReadOnlyList<(int Count, int Stride)> Dims, IReadOnlyList<KitField> Columns, string Prefix);

    public RecordView(KitLayout layout)
    {
        this.layout = layout;
        var leaves = layout.Fields.Where(f => !f.Group).ToList();
        plain = leaves.Where(f => f.Occurs.Count == 0).ToList();
        tables = [];
        foreach (var g in leaves.Where(f => f.Occurs.Count > 0).GroupBy(f => string.Join(";", f.Occurs.Select(o => $"{o.Count}x{o.Stride}"))))
        {
            var cols = g.OrderBy(f => f.Offset).ToList();
            var dims = cols[0].Occurs;
            var owner = layout.Fields.Where(f => f.Group && f.Occurs.Count == dims.Count && f.Occurs.SequenceEqual(dims)).OrderBy(f => f.Offset).FirstOrDefault();
            var name = owner?.Name ?? cols[0].Name;
            tables.Add(new Table(name, dims, cols, CommonPrefix(cols.Select(c => c.Name).ToList())));
        }
    }

    public KitLayout Layout => layout;
    public IReadOnlyList<KitField> Plain => plain;
    public IReadOnlyList<Table> Tables => tables;

    static string CommonPrefix(List<string> names)
    {
        if (names.Count < 2) return "";
        var p = names[0];
        foreach (var n in names.Skip(1))
        {
            int k = 0;
            while (k < p.Length && k < n.Length && p[k] == n[k]) k++;
            p = p[..k];
        }
        int dash = p.LastIndexOf('-');
        return dash > 0 ? p[..(dash + 1)] : "";
    }

    /// <summary>Every index tuple of a table (1-based) with the byte offset of its occurrence from the column's base offset.</summary>
    public static IEnumerable<(int[] Index, int Delta)> Rows(Table t)
    {
        var idx = new int[t.Dims.Count];
        IEnumerable<(int[], int)> Walk(int d, int delta)
        {
            if (d == t.Dims.Count) { yield return ((int[])idx.Clone(), delta); yield break; }
            for (int k = 0; k < t.Dims[d].Count; k++)
            {
                idx[d] = k + 1;
                foreach (var x in Walk(d + 1, delta + k * t.Dims[d].Stride)) yield return x;
            }
        }
        return Walk(0, 0);
    }

    public static string Label(int[] index) => string.Join(",", index);

    /// <summary>Items of one occurrence that are not at their initial value.</summary>
    static bool RowInUse(Table t, byte[] r, int delta) => t.Columns.Any(c => !KitValues.Initial(KitValues.Slice(r, c.Offset + delta, c.Length)));

    /// <summary>
    /// The argument as the CALL passes it: plain items not at their initial value, then each table's occurrences in use
    /// (up to <paramref name="maxRows"/>; the varying columns as a table, the columns with one value given once).
    /// </summary>
    public string Listing(byte[] r, string indent, int maxRows = 12)
    {
        var sb = new StringBuilder();
        var items = plain.Where(f => !KitValues.Initial(KitValues.Slice(r, f.Offset, f.Length))).Select(f => $"{f.Name} = {KitValues.Show(f, r, f.Offset)}").ToList();
        sb.Append(items.Count == 0 && !tables.Any(t => Rows(t).Any(x => RowInUse(t, r, x.Delta))) ? "(blank)" : string.Join(", ", items));
        foreach (var t in tables)
        {
            var rows = Rows(t).Where(x => RowInUse(t, r, x.Delta)).ToList();
            if (rows.Count == 0) continue;
            sb.Append('\n').Append(TableText(t, rows, r, indent, maxRows));
        }
        return sb.ToString();
    }

    string TableText(Table t, List<(int[] Index, int Delta)> rows, byte[] r, string indent, int maxRows)
    {
        var sb = new StringBuilder();
        var shown = rows.Count <= maxRows ? rows : rows.Take(maxRows - 2).Concat(rows.TakeLast(2)).ToList();
        var label = $"{t.Name}({KitValues.Ranges(rows.Select(x => x.Index[0]))}{(t.Dims.Count > 1 ? ",…" : "")})";
        // A column that is blank in every row in use says nothing; one with a single value is given once.
        var cols = t.Columns.Where(c => rows.Any(x => !KitValues.Initial(KitValues.Slice(r, c.Offset + x.Delta, c.Length)))).ToList();
        var constant = cols.Where(c => shown.Select(x => KitValues.Show(c, r, c.Offset + x.Delta)).Distinct().Count() == 1 && rows.Count > 1).ToList();
        var varying = cols.Except(constant).ToList();
        string Col(KitField c) => t.Prefix.Length > 0 && c.Name.StartsWith(t.Prefix, StringComparison.Ordinal) ? c.Name[t.Prefix.Length..] : c.Name;
        sb.Append(indent).Append($"{label}: {rows.Count} occurrence(s) in use");
        if (t.Prefix.Length > 0) sb.Append($"; columns are {t.Prefix}<name>");
        if (constant.Count > 0)
            sb.Append($"; in every one: {string.Join(", ", constant.Select(c => $"{Col(c)} = {KitValues.Show(c, r, c.Offset + shown[0].Delta)}"))}");
        sb.Append('\n');
        if (varying.Count > 0)
        {
            sb.Append(indent).Append("| # | ").Append(string.Join(" | ", varying.Select(Col))).Append(" |\n");
            sb.Append(indent).Append("|---|").Append(string.Concat(varying.Select(_ => "---|"))).Append('\n');
            for (int i = 0; i < shown.Count; i++)
            {
                if (rows.Count > maxRows && i == maxRows - 2) sb.Append(indent).Append($"| … | ({rows.Count - maxRows} more occurrence(s) in use, not shown) |\n");
                var x = shown[i];
                sb.Append(indent).Append("| ").Append(Label(x.Index)).Append(" | ")
                    .Append(string.Join(" | ", varying.Select(c => KitValues.Show(c, r, c.Offset + x.Delta)))).Append(" |\n");
            }
        }
        return sb.ToString().TrimEnd('\n');
    }

    public sealed record Change(KitField Field, string Name, int Offset, string Before, string After, Table? Table, int[]? Index);

    /// <summary>Every elementary item whose bytes differ between the two images (FILLER bytes as one raw range each).</summary>
    public List<Change> Changes(byte[] a, byte[] b)
    {
        var list = new List<Change>();
        foreach (var f in plain)
            if (!KitValues.Slice(a, f.Offset, f.Length).SequenceEqual(KitValues.Slice(b, f.Offset, f.Length)))
                list.Add(new Change(f, f.Name, f.Offset, KitValues.Show(f, a, f.Offset), KitValues.Show(f, b, f.Offset), null, null));
        foreach (var t in tables)
            foreach (var (index, delta) in Rows(t))
                foreach (var c in t.Columns)
                {
                    int off = c.Offset + delta;
                    if (off >= Math.Max(a.Length, b.Length)) continue;
                    if (KitValues.Slice(a, off, c.Length).SequenceEqual(KitValues.Slice(b, off, c.Length))) continue;
                    list.Add(new Change(c, $"{c.Name}({Label(index)})", off, KitValues.Show(c, a, off), KitValues.Show(c, b, off), t, index));
                }
        // Bytes no named item covers.
        var covered = new bool[Math.Max(a.Length, b.Length)];
        foreach (var f in plain) for (int k = f.Offset; k < f.Offset + f.Length && k < covered.Length; k++) covered[k] = true;
        foreach (var t in tables)
            foreach (var (_, delta) in Rows(t))
                foreach (var c in t.Columns)
                    for (int k = c.Offset + delta; k < c.Offset + delta + c.Length && k < covered.Length; k++) covered[k] = true;
        for (int i = 0; i < covered.Length;)
        {
            bool same = i < a.Length && i < b.Length && a[i] == b[i];
            if (covered[i] || same) { i++; continue; }
            int s = i;
            while (i < covered.Length && !covered[i] && !(i < a.Length && i < b.Length && a[i] == b[i])) i++;
            var raw = new KitField("FILLER", s, i - s, "text", null, 0, 0, false, false, false, false, [], null);
            list.Add(new Change(raw, $"FILLER@{s}+{i - s}", s, KitValues.Show(raw, a, s), KitValues.Show(raw, b, s), null, null));
        }
        return list;
    }

    /// <summary>
    /// Changes as text: plain items "NAME: before → after"; a table column that changes the same way in several occurrences once
    /// ("LM-RP-RF-PLUS1(1..6): 0 → 1.0"); otherwise each occurrence, or a table of the changed occurrences when there are many.
    /// </summary>
    public static string ChangesText(List<Change> changes, string indent, int maxCells = 12)
    {
        var lines = new List<string>();
        foreach (var c in changes.Where(c => c.Table is null)) lines.Add($"{c.Name}: {c.Before} → {c.After}");
        foreach (var tg in changes.Where(c => c.Table is not null).GroupBy(c => c.Table!.Name))
        {
            var t = tg.First().Table!;
            var cells = tg.ToList();
            var byCol = cells.GroupBy(c => c.Field.Name).ToList();
            var uniform = byCol.Where(g => g.Select(c => (c.Before, c.After)).Distinct().Count() == 1).ToList();
            var rest = byCol.Except(uniform).SelectMany(g => g).ToList();
            foreach (var g in uniform)
            {
                var one = g.First();
                var where = g.Count() == 1 ? Label(one.Index!) : (t.Dims.Count == 1 ? KitValues.Ranges(g.Select(c => c.Index![0])) : string.Join("; ", g.Select(c => Label(c.Index!))));
                lines.Add($"{g.Key}({where}): {one.Before} → {one.After}");
            }
            if (rest.Count == 0) continue;
            if (rest.Count <= maxCells)
            {
                foreach (var c in rest) lines.Add($"{c.Name}: {c.Before} → {c.After}");
                continue;
            }
            // A table of the changed occurrences: one column per changed item, "before → after" in each cell.
            var cols = rest.Select(c => c.Field).DistinctBy(f => f.Name).OrderBy(f => f.Offset).ToList();
            string Col(KitField f) => t.Prefix.Length > 0 && f.Name.StartsWith(t.Prefix, StringComparison.Ordinal) ? f.Name[t.Prefix.Length..] : f.Name;
            var rows = rest.GroupBy(c => Label(c.Index!)).ToList();
            var sb = new StringBuilder($"{t.Name}: {rows.Count} occurrence(s) changed{(t.Prefix.Length > 0 ? $"; columns are {t.Prefix}<name>" : "")}; each cell is before → after\n");
            sb.Append(indent).Append("| # | ").Append(string.Join(" | ", cols.Select(Col))).Append(" |\n");
            sb.Append(indent).Append("|---|").Append(string.Concat(cols.Select(_ => "---|"))).Append('\n');
            int shownRows = 0;
            foreach (var row in rows)
            {
                if (shownRows++ == 16) { sb.Append(indent).Append($"| … | ({rows.Count - 16} more changed occurrence(s)) |\n"); break; }
                sb.Append(indent).Append("| ").Append(row.Key).Append(" | ")
                    .Append(string.Join(" | ", cols.Select(f => row.FirstOrDefault(c => c.Field.Name == f.Name) is { } c ? $"{c.Before} → {c.After}" : "="))).Append(" |\n");
            }
            lines.Add(sb.ToString().TrimEnd('\n'));
        }
        return string.Join("\n", lines.Select(l => indent + l));
    }
}
