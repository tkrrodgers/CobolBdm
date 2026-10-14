using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Pipeline v2 feedback from a probe run: what differs, by record and by COBOL field name with both values decoded, and
/// where the logic parts: the faithful statement and rule that wrote the first differing record, the inputs it had just read,
/// and which statement would have written what the model wrote instead. Layouts come from normalization/&lt;PID&gt;/record-layouts.json
/// (without them, records are compared as raw bytes); statements from the faithful source's step tags; rules from the packet.
/// </summary>
public sealed class FeedbackRenderer
{
    // ------------------------------------------------------------------ inputs

    public sealed record Field(string Name, int Offset, int Length, string Kind, string? Pic, int Digits, int Scale, bool Signed,
        bool SignSeparate, bool SignLeading, bool Group, IReadOnlyList<(int Count, int Stride)> Occurs, string? Accessor);

    /// <param name="Constants">VALUE text in place when the area is new; Named marks a named item's VALUE, which the program may overwrite.</param>
    public sealed record Layout(string Name, string Program, int Length, string? Class, IReadOnlyList<Field> Fields,
        IReadOnlyList<(int Offset, string Text, bool Named)> Constants, string Role = "");

    public sealed record Dataset(string Dsn, string Dd, string File, string Format, IReadOnlyList<string> Layouts, int? KeyOffset, int? KeyLength);

    public sealed record Writer(string File, string Verb, string StepId, string? Paragraph, string Cobol, string? Layout);

    public sealed record Rule(string Id, string Paragraph, IReadOnlyList<string> When, IReadOnlyList<(string StepId, string Cobol)> Statements);

    readonly Dictionary<string, Layout> layouts = new(StringComparer.Ordinal);
    readonly List<Dataset> datasets = [];
    readonly List<Writer> writers = [];
    readonly List<string> linkage = [];
    readonly List<Rule> rules = [];
    readonly string? sourceRoot;
    readonly Dictionary<string, List<(int Line, string Tag, string Text)>> tags = new(StringComparer.Ordinal);
    public bool HasLayouts => layouts.Count > 0;

    /// <param name="layoutsPath">normalization/&lt;PID&gt;/record-layouts.json (optional).</param>
    /// <param name="packetPath">The requirements packet (rules by statement).</param>
    /// <param name="sourceRoot">src/main/java of the harness (the faithful classes' statement tags).</param>
    public FeedbackRenderer(string? layoutsPath, string? packetPath, string? sourceRoot)
    {
        this.sourceRoot = sourceRoot;
        if (layoutsPath is not null && File.Exists(layoutsPath))
        {
            var j = JsonNode.Parse(File.ReadAllText(layoutsPath))!;
            foreach (var l in j["layouts"]!.AsArray().OfType<JsonObject>())
            {
                var fields = l["fields"]!.AsArray().OfType<JsonObject>().Select(f => new Field(
                    f["name"]!.GetValue<string>(), f["offset"]!.GetValue<int>(), f["length"]!.GetValue<int>(), f["kind"]!.GetValue<string>(),
                    f["pic"]?.GetValue<string>(), f["digits"]?.GetValue<int>() ?? 0, f["scale"]?.GetValue<int>() ?? 0, f["signed"]?.GetValue<bool>() ?? false,
                    f["signSeparate"]?.GetValue<bool>() ?? false, f["signLeading"]?.GetValue<bool>() ?? false, f["group"]?.GetValue<bool>() ?? false,
                    f["occurs"]?.AsArray().Select(o => (o![0]!.GetValue<int>(), o[1]!.GetValue<int>())).ToList() ?? [], f["accessor"]?.GetValue<string>())).ToList();
                var constants = l["constants"]!.AsArray().OfType<JsonObject>()
                    .Select(c => (c["offset"]!.GetValue<int>(), c["text"]!.GetValue<string>(), c["named"]?.GetValue<bool>() ?? false)).ToList();
                var name = l["name"]!.GetValue<string>();
                layouts.TryAdd(name, new Layout(name, l["program"]!.GetValue<string>(), l["length"]!.GetValue<int>(), l["class"]?.GetValue<string>(), fields, constants,
                    l["role"]?.GetValue<string>() ?? ""));
            }
            foreach (var d in j["datasets"]!.AsArray().OfType<JsonObject>())
                datasets.Add(new Dataset(d["dsn"]!.GetValue<string>(), d["dd"]!.GetValue<string>(), d["file"]!.GetValue<string>(), d["format"]!.GetValue<string>(),
                    d["layouts"]!.AsArray().Select(x => x!.GetValue<string>()).ToList(), d["keyOffset"]?.GetValue<int>(), d["keyLength"]?.GetValue<int>()));
            foreach (var w in j["writers"]!.AsArray().OfType<JsonObject>())
                writers.Add(new Writer(w["file"]!.GetValue<string>(), w["verb"]!.GetValue<string>(), w["stepId"]?.GetValue<string>() ?? "",
                    w["paragraph"]?.GetValue<string>(), w["cobol"]?.GetValue<string>() ?? "", w["layout"]?.GetValue<string>()));
            linkage.AddRange(j["linkage"]?.AsArray().Select(x => x!.GetValue<string>()) ?? []);
        }
        if (packetPath is not null && File.Exists(packetPath))
            foreach (var r in JsonNode.Parse(File.ReadAllText(packetPath))!["rules"]?.AsArray().OfType<JsonObject>() ?? [])
                rules.Add(new Rule(r["id"]!.GetValue<string>(), r["paragraph"]?.GetValue<string>() ?? "",
                    r["when"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [],
                    r["statements"]?.AsArray().OfType<JsonObject>().Select(s => (s["stepId"]!.GetValue<string>(), s["cobol"]?.GetValue<string>() ?? "")).ToList() ?? []));
    }

    // ------------------------------------------------------------------ rendering

    const int Budget = 9000;

    public string Render(IReadOnlyList<ProbeScenario> scenarios)
    {
        var sb = new StringBuilder();
        foreach (var s in scenarios)
        {
            if (sb.Length > Budget) { sb.Append($"(scenario '{s.Name}' not shown: feedback budget reached)\n"); continue; }
            sb.Append(Scenario(s)).Append('\n');
        }
        return sb.ToString().TrimEnd();
    }

    /// <param name="Misfit">Set when your file is not a whole number of records of the dataset's length (records cannot be compared then).</param>
    sealed record Diff(string Output, Dataset? Ds, List<Op> Ops, List<byte[]> Legacy, List<byte[]> Yours, string Format, byte[] RawLegacy, byte[] RawYours,
        string? Misfit = null);

    enum OpKind { Equal, Changed, Missing, Extra }

    /// <summary>One step of the alignment: legacy record L and/or yours Y (indexes, -1 when absent).</summary>
    sealed record Op(OpKind Kind, int L, int Y);

    string Scenario(ProbeScenario s)
    {
        var sb = new StringBuilder($"## Scenario '{s.Name}'\n");
        if (s.Legacy.ReturnCode != s.Yours.ReturnCode)
            sb.Append($"Return code: legacy {s.Legacy.ReturnCode} ({Clip(s.Legacy.Detail, 160)}), yours {s.Yours.ReturnCode} ({Clip(s.Yours.Detail, 300)}).\n");
        else if (s.Yours.Detail.Contains("Exception", StringComparison.Ordinal))
            sb.Append($"Your step ended with: {Clip(s.Yours.Detail, 300)}\n");
        var diffs = new List<Diff>();
        foreach (var name in s.Legacy.Files.Keys.Union(s.Yours.Files.Keys).OrderBy(x => x, StringComparer.Ordinal))
        {
            var a = s.Legacy.Files.GetValueOrDefault(name);
            var b = s.Yours.Files.GetValueOrDefault(name);
            if (a is null || b is null) { sb.Append($"{name}: only {(a is null ? "yours" : "the legacy program")} wrote it.\n"); continue; }
            if (a.AsSpan().SequenceEqual(b)) continue;
            diffs.Add(Compare(name, a, b));
        }
        foreach (var d in diffs) sb.Append(Output(d));
        if (s.LegacyCalls.Count > 0 || s.YourCalls.Count > 0) sb.Append(Calls(s));
        var where = Divergence(s, diffs);
        if (where.Length > 0) sb.Append(where);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ records and alignment

    Diff Compare(string name, byte[] a, byte[] b)
    {
        var ds = name.StartsWith("data/", StringComparison.Ordinal) ? datasets.FirstOrDefault(d => d.Dsn == name[5..]) : null;
        var format = name.StartsWith("sysout/", StringComparison.Ordinal) ? "sysout" : ds?.Format ?? "raw";
        var colon = format.IndexOf(':');
        if (colon > 0 && int.TryParse(format[(colon + 1)..], out var lrecl) && lrecl > 0)
        {
            // Print files: the line bytes without the ADVANCING control bytes must be whole lines.
            bool print = format.StartsWith("print", StringComparison.Ordinal);
            int payload = print ? b.Length - b.Count(x => x is (byte)'\n' or (byte)'\f') : b.Length;
            if (payload % lrecl != 0)
            {
                var guess = Enumerable.Range(lrecl - 3, 7).Where(n => n > 0 && n != lrecl && payload % n == 0).Select(n => $"{n}-byte").ToList();
                var misfit = $"your file holds {payload} bytes of {(print ? "lines" : "records")}, not a whole number of {lrecl}-byte {(print ? "lines" : "records")}"
                             + (guess.Count > 0 ? $" (it divides into {string.Join(" or ", guess)} ones)" : "")
                             + $": every {(print ? "line" : "record")} must be exactly {lrecl} bytes (a longer area written FROM is cut, a shorter one space padded)";
                return new Diff(name, ds, [], [], [], format, a, b, misfit);
            }
        }
        var ra = Records(a, format);
        var rb = Records(b, format);
        List<Op> ops;
        if (ds?.KeyOffset is { } ko && ds.KeyLength is { } kl && format.StartsWith("fixed", StringComparison.Ordinal)) ops = ByKey(ra, rb, ko, kl);
        else ops = Align(ra, rb);
        return new Diff(name, ds, ops, ra, rb, format, a, b);
    }

    static List<byte[]> Records(byte[] f, string format)
    {
        var recs = new List<byte[]>();
        if (format == "sysout" || format == "line")
        {
            var text = Encoding.Latin1.GetString(f).Replace("\r\n", "\n");
            if (text.EndsWith('\n')) text = text[..^1];
            foreach (var l in text.Split('\n')) recs.Add(Encoding.Latin1.GetBytes(l));
            return recs;
        }
        if (format == "variable")
        {
            int at = 0;
            while (at + 4 <= f.Length)
            {
                int len = (f[at] << 8) | f[at + 1];
                recs.Add(f[(at + 4)..Math.Min(f.Length, at + 4 + len)]);
                at += 4 + len;
            }
            return recs;
        }
        var colon = format.IndexOf(':');
        if (colon < 0 || !int.TryParse(format[(colon + 1)..], out var lrecl) || lrecl <= 0) { recs.Add(f); return recs; }
        bool print = format.StartsWith("print", StringComparison.Ordinal);
        int i = 0;
        while (i < f.Length)
        {
            if (print && (f[i] == '\n' || f[i] == '\f')) { i++; continue; }
            int end = Math.Min(f.Length, i + lrecl);
            recs.Add(f[i..end]);
            i = end;
        }
        return recs;
    }

    static List<Op> ByKey(List<byte[]> a, List<byte[]> b, int ko, int kl)
    {
        string Key(byte[] r) => r.Length >= ko + kl ? Encoding.Latin1.GetString(r, ko, kl) : Encoding.Latin1.GetString(r);
        var yb = new Dictionary<string, int>();
        for (int j = 0; j < b.Count; j++) yb.TryAdd(Key(b[j]), j);
        var ops = new List<Op>();
        var used = new HashSet<int>();
        for (int i = 0; i < a.Count; i++)
        {
            if (yb.TryGetValue(Key(a[i]), out var j)) { used.Add(j); ops.Add(new Op(a[i].AsSpan().SequenceEqual(b[j]) ? OpKind.Equal : OpKind.Changed, i, j)); }
            else ops.Add(new Op(OpKind.Missing, i, -1));
        }
        for (int j = 0; j < b.Count; j++) if (!used.Contains(j)) ops.Add(new Op(OpKind.Extra, -1, j));
        return ops;
    }

    /// <summary>Records aligned in order: equal runs, then changed pairs, missing (legacy only) and extra (yours only) records.</summary>
    static List<Op> Align(List<byte[]> a, List<byte[]> b)
    {
        var ops = new List<Op>();
        if (a.Count == b.Count || (long)a.Count * b.Count > 4_000_000)
        {
            for (int i = 0; i < Math.Max(a.Count, b.Count); i++)
                ops.Add(i >= a.Count ? new Op(OpKind.Extra, -1, i) : i >= b.Count ? new Op(OpKind.Missing, i, -1)
                    : new Op(a[i].AsSpan().SequenceEqual(b[i]) ? OpKind.Equal : OpKind.Changed, i, i));
            return ops;
        }
        var ha = a.Select(x => Convert.ToBase64String(x)).ToArray();
        var hb = b.Select(x => Convert.ToBase64String(x)).ToArray();
        var lcs = new int[a.Count + 1, b.Count + 1];
        for (int i = a.Count - 1; i >= 0; i--)
            for (int j = b.Count - 1; j >= 0; j--)
                lcs[i, j] = ha[i] == hb[j] ? lcs[i + 1, j + 1] + 1 : Math.Max(lcs[i + 1, j], lcs[i, j + 1]);
        var raw = new List<Op>();
        int x = 0, y = 0;
        while (x < a.Count && y < b.Count)
        {
            if (ha[x] == hb[y]) { raw.Add(new Op(OpKind.Equal, x++, y++)); continue; }
            if (lcs[x + 1, y] >= lcs[x, y + 1]) raw.Add(new Op(OpKind.Missing, x++, -1));
            else raw.Add(new Op(OpKind.Extra, -1, y++));
        }
        while (x < a.Count) raw.Add(new Op(OpKind.Missing, x++, -1));
        while (y < b.Count) raw.Add(new Op(OpKind.Extra, -1, y++));
        // A run of missing records next to a run of extra ones is a run of changed records (paired in order).
        for (int k = 0; k < raw.Count;)
        {
            if (raw[k].Kind == OpKind.Equal) { ops.Add(raw[k++]); continue; }
            var miss = new List<int>();
            var extra = new List<int>();
            while (k < raw.Count && raw[k].Kind != OpKind.Equal)
            {
                if (raw[k].Kind == OpKind.Missing) miss.Add(raw[k].L); else extra.Add(raw[k].Y);
                k++;
            }
            int n = Math.Min(miss.Count, extra.Count);
            for (int p = 0; p < n; p++) ops.Add(new Op(OpKind.Changed, miss[p], extra[p]));
            foreach (var m in miss.Skip(n)) ops.Add(new Op(OpKind.Missing, m, -1));
            foreach (var e in extra.Skip(n)) ops.Add(new Op(OpKind.Extra, -1, e));
        }
        return ops;
    }

    // ------------------------------------------------------------------ one output

    string Output(Diff d)
    {
        var sb = new StringBuilder();
        if (d.Misfit is not null)
        {
            sb.Append($"{d.Output}: {d.Misfit}. First difference:\n").Append(RawRegions(d.RawLegacy, d.RawYours, "  ", 1));
            return sb.ToString();
        }
        int changed = d.Ops.Count(o => o.Kind == OpKind.Changed), missing = d.Ops.Count(o => o.Kind == OpKind.Missing), extra = d.Ops.Count(o => o.Kind == OpKind.Extra);
        var what = d.Format switch
        {
            "sysout" => "console lines",
            "variable" => "variable-length records",
            _ when d.Format.StartsWith("print") => $"{d.Format[6..]}-byte print lines",
            _ when d.Format.StartsWith("fixed") => $"{d.Format[6..]}-byte records{(d.Ds?.KeyOffset is not null ? ", by key" : "")}",
            _ => "bytes"
        };
        sb.Append($"{d.Output} ({what}; legacy {d.Legacy.Count}, yours {d.Yours.Count}): ");
        sb.Append(string.Join(", ", new[] { changed > 0 ? $"{changed} differ" : null, missing > 0 ? $"{missing} missing in yours" : null, extra > 0 ? $"{extra} extra in yours" : null }.OfType<string>())).Append('\n');
        if (d.Format == "raw") { sb.Append(RawRegions(d.Legacy[0], d.Yours[0])); return sb.ToString(); }

        // Changed records with the same set of differing fields are shown once, with a count.
        var groups = new List<(string Signature, List<Op> Ops)>();
        int shown = 0;
        foreach (var op in d.Ops.Where(o => o.Kind != OpKind.Equal))
        {
            if (op.Kind == OpKind.Changed && d.Format != "sysout")
            {
                var (layout, _) = Match(d, d.Legacy[op.L]);
                var sig = layout is null ? "raw" : layout.Name + ":" + string.Join(",", FieldDiffs(layout, d.Legacy[op.L], d.Yours[op.Y]).Select(x => x.Field.Name));
                var g = groups.FirstOrDefault(x => x.Signature == sig);
                if (g.Ops is not null) { g.Ops.Add(op); continue; }
                groups.Add((sig, [op]));
                continue;
            }
            groups.Add(("#" + groups.Count, [op]));
        }
        foreach (var (sig, ops) in groups)
        {
            if (shown >= 6 || sb.Length > 3500) { sb.Append($"  … {groups.Skip(shown).Sum(g => g.Ops.Count)} more differing record(s)\n"); break; }
            shown++;
            var op = ops[0];
            sb.Append(Record(d, op));
            if (ops.Count > 1)
                sb.Append($"  … and {ops.Count - 1} more record(s) differ in the same field(s) (legacy record{(ops.Count > 2 ? "s" : "")} {string.Join(", ", ops.Skip(1).Take(8).Select(o => o.L + 1))}{(ops.Count > 9 ? ", …" : "")})\n");
        }
        if (d.Format.StartsWith("print") && int.TryParse(d.Format[6..], out var lrecl))
        {
            var (ca, cb) = (Controls(d.RawLegacy, lrecl), Controls(d.RawYours, lrecl));
            int k = 0;
            while (k < Math.Min(ca.Count, cb.Count) && ca[k] == cb[k]) k++;
            if (k < Math.Max(ca.Count, cb.Count))
                sb.Append($"  ADVANCING differs (line feeds \\n / form feeds \\f written before each line): legacy {string.Join(", ", ca.Skip(k).Take(5))}; yours {string.Join(", ", cb.Skip(k).Take(5))}\n");
        }
        return sb.ToString();
    }

    /// <summary>A print file's control bytes per line ("line 3: \n\n" = two line feeds before line 3); the last entry is what CLOSE added.</summary>
    static List<string> Controls(byte[] f, int lrecl)
    {
        var list = new List<string>();
        var ctl = new StringBuilder();
        int i = 0, line = 0;
        while (i < f.Length)
        {
            if (f[i] == '\n' || f[i] == '\f') { ctl.Append(f[i] == '\n' ? "\\n" : "\\f"); i++; continue; }
            line++;
            list.Add($"line {line}: {(ctl.Length == 0 ? "none" : ctl.ToString())}");
            ctl.Clear();
            i = Math.Min(f.Length, i + lrecl);
        }
        list.Add($"after line {line}: {(ctl.Length == 0 ? "none" : ctl.ToString())}");
        return list;
    }

    string Record(Diff d, Op op)
    {
        var sb = new StringBuilder();
        string Name(int i) => d.Format == "sysout" ? $"line {i + 1}" : $"record {i + 1}";
        if (op.Kind == OpKind.Missing)
        {
            var (l, _) = Match(d, d.Legacy[op.L]);
            sb.Append($"  legacy {Name(op.L)}{(l is null ? "" : $" ({l.Name})")} is missing in yours: «{Show(d.Legacy[op.L], 160)}»\n");
            return sb.ToString();
        }
        if (op.Kind == OpKind.Extra)
        {
            var (l, _) = Match(d, d.Yours[op.Y]);
            sb.Append($"  your {Name(op.Y)}{(l is null ? "" : $" ({l.Name})")} has no legacy counterpart: «{Show(d.Yours[op.Y], 160)}»\n");
            return sb.ToString();
        }
        var a = d.Legacy[op.L];
        var b = d.Yours[op.Y];
        if (d.Format == "sysout")
        {
            sb.Append($"  {Name(op.L)}: legacy «{Show(a, 200)}»\n  {new string(' ', Name(op.L).Length)}  yours  «{Show(b, 200)}»\n");
            var la2 = Encoding.Latin1.GetString(a);
            if (Regex.IsMatch(la2, @"\d{4,}[+-](\s|$)") && !Regex.IsMatch(Encoding.Latin1.GetString(b), @"\d{4,}[+-](\s|$)"))
                sb.Append("    (a signed numeric item DISPLAYs as all its digits, zero padded, then its sign: Pic.display(value, \"S9(9)V99\", \"DISPLAY\", ctx.displayNumeric()) with the item's PICTURE and USAGE)\n");
            return sb.ToString();
        }
        var (la, sa) = Match(d, a);
        var (lb, sbScore) = Match(d, b);
        var label = $"{Name(op.L)}{(op.Y != op.L ? $" (your {op.Y + 1})" : "")}";
        if (la is not null && lb is not null && la.Name != lb.Name && sbScore >= 5 && sa >= 5)
        {
            sb.Append($"  {label}: legacy wrote a {la.Name} line «{Show(a, 120)}», yours a {lb.Name} line «{Show(b, 120)}»\n");
            return sb.ToString();
        }
        if (la is null)
        {
            sb.Append($"  {label} ({a.Length} vs {b.Length} bytes):\n").Append(RawRegions(a, b, "    "));
            return sb.ToString();
        }
        var fd = FieldDiffs(la, a, b);
        sb.Append($"  {label} ({la.Name}{(a.Length != b.Length ? $"; legacy {a.Length} bytes, yours {b.Length}" : "")}):");
        if (fd.Count == 0) { sb.Append('\n').Append(RawRegions(a, b, "    ")); return sb.ToString(); }
        sb.Append('\n');
        foreach (var x in fd.Take(8)) sb.Append("    ").Append(x.Text).Append('\n');
        if (fd.Count > 8) sb.Append($"    … {fd.Count - 8} more field(s)\n");
        return sb.ToString();
    }

    /// <summary>The layout a record most likely has: same length (or, for print lines, the most constant text in place).</summary>
    (Layout? Layout, int Score) Match(Diff d, byte[] r)
    {
        if (d.Ds is null) return (null, 0);
        Layout? best = null;
        double bestScore = double.MinValue;
        // A report written without ADVANCING is a fixed file whose record is one text item; its lines are shorter areas moved into it.
        bool lineFile = d.Format.StartsWith("print") || d.Format == "line"
                        || d.Ds.Layouts.Any(n => layouts.TryGetValue(n, out var fd) && fd.Role.StartsWith("FD ", StringComparison.Ordinal)
                                                  && fd.Fields.Count == 1 && fd.Fields[0].Kind == "text" && fd.Fields[0].Length == fd.Length);
        foreach (var name in d.Ds.Layouts)
        {
            if (!layouts.TryGetValue(name, out var l)) continue;
            if (!lineFile && l.Length != r.Length && !(d.Format == "variable" && l.Length >= r.Length)) continue;
            // VALUE text in place counts for the layout, VALUE text contradicted counts against it. Spaces say little (a long FILLER
            // of spaces fits any short line), and a named item's VALUE may have been overwritten by the program.
            double constant = 0;
            foreach (var (off, text, named) in l.Constants)
                for (int k = 0; k < text.Length && off + k < r.Length; k++)
                    constant += text[k] == ' ' ? (r[off + k] == ' ' ? 0.1 : -0.1) : r[off + k] == text[k] ? (named ? 0.7 : 1) : (named ? -0.5 : -1);
            // A number item holding letters is not this layout.
            double plausible = 0;
            foreach (var f in l.Fields.Where(f => f.Occurs.Count == 0 && f.Offset + f.Length <= r.Length))
            {
                var v = Encoding.Latin1.GetString(r, f.Offset, f.Length);
                bool ok = f.Kind switch
                {
                    "zoned" => v.Length == 0 || v[..^1].All(char.IsAsciiDigit) && (char.IsAsciiDigit(v[^1]) || "{}ABCDEFGHIJKLMNOPQR".Contains(v[^1])) || v.Trim().Length == 0,
                    "edited" => v.All(c => " 0123456789,.+-$*/BCRD".Contains(c)),
                    _ => true
                };
                if (!ok) plausible -= 3;
            }
            // A single-item record (the FD's PIC X(n)) describes nothing: prefer any line layout over it. Otherwise prefer the
            // program's own view of the record (the WORKING-STORAGE layout it reads INTO or writes FROM, with its numeric items)
            // over the FD's shell.
            double score = constant + plausible + (lineFile && l.Fields.Count <= 1 && l.Constants.Count == 0 ? -1000 : l.Fields.Count * 0.2)
                           + l.Fields.Count(f => f.Kind is "zoned" or "packed" or "binary") * 0.3 + (l.Role.StartsWith("FD ", StringComparison.Ordinal) ? -1 : 0)
                           + (l.Length == r.Length ? 1 : 0);
            if (score > bestScore) { best = l; bestScore = score; }
        }
        // How sure the match is: the layout's non-space VALUE text found in place, net of the text contradicted. Only this says
        // which line a record is (spaces fit almost any line).
        double evidence = 0;
        if (best is not null)
            foreach (var (off, text, named) in best.Constants)
                for (int k = 0; k < text.Length && off + k < r.Length; k++)
                    if (text[k] != ' ') evidence += r[off + k] == text[k] ? (named ? 0.7 : 1) : (named ? -0.5 : -1);
        return (best, (int)Math.Max(0, evidence));
    }

    sealed record FieldDiff(Field Field, string Text);

    List<FieldDiff> FieldDiffs(Layout l, byte[] a, byte[] b)
    {
        var list = new List<FieldDiff>();
        var covered = new bool[Math.Max(a.Length, b.Length)];
        foreach (var f in l.Fields.Where(f => !f.Group))
            foreach (var (off, suffix) in Occurrences(f))
            {
                for (int k = off; k < off + f.Length && k < covered.Length; k++) covered[k] = true;
                var va = Slice(a, off, f.Length);
                var vb = Slice(b, off, f.Length);
                if (va.AsSpan().SequenceEqual(vb)) continue;
                list.Add(new FieldDiff(f, Describe(f, suffix, off, va, vb)));
            }
        // Bytes no named item covers (FILLER).
        int i = 0;
        while (i < covered.Length)
        {
            if (covered[i] || (i < a.Length && i < b.Length && a[i] == b[i])) { i++; continue; }
            int s = i;
            while (i < covered.Length && !covered[i]) i++;
            var va = Slice(a, s, i - s);
            var vb = Slice(b, s, i - s);
            if (!va.AsSpan().SequenceEqual(vb))
                list.Add(new FieldDiff(new Field("FILLER", s, i - s, "text", null, 0, 0, false, false, false, false, [], null),
                    $"FILLER @{s}+{i - s}: legacy «{Show(va, 80)}», yours «{Show(vb, 80)}»{Hint(va, vb, null)}"));
        }
        return list;
    }

    static IEnumerable<(int Offset, string Suffix)> Occurrences(Field f)
    {
        if (f.Occurs.Count == 0) { yield return (f.Offset, ""); yield break; }
        IEnumerable<(int, string)> Expand(int dim, int off, string sfx)
        {
            if (dim == f.Occurs.Count) { yield return (off, sfx.Length == 0 ? "" : "(" + sfx + ")"); yield break; }
            for (int k = 0; k < f.Occurs[dim].Count; k++)
                foreach (var x in Expand(dim + 1, off + k * f.Occurs[dim].Stride, sfx.Length == 0 ? $"{k + 1}" : $"{sfx},{k + 1}")) yield return x;
        }
        foreach (var x in Expand(0, f.Offset, "")) yield return x;
    }

    static byte[] Slice(byte[] r, int off, int len) => off >= r.Length ? [] : r[off..Math.Min(r.Length, off + len)];

    string Describe(Field f, string suffix, int off, byte[] a, byte[] b)
    {
        var pic = f.Pic is null ? "" : $"PIC {f.Pic}{(f.Kind == "packed" ? " COMP-3" : f.Kind == "binary" ? " COMP" : "")} ";
        var head = $"{f.Name}{suffix} ({pic}@{off}+{f.Length}{(f.Accessor is not null ? $", {f.Accessor}()" : "")}): ";
        if (f.Kind is "zoned" or "packed" or "binary")
        {
            var va = Decode(f, a);
            var vb = Decode(f, b);
            return head + $"legacy {va ?? "?"} «{Raw(f, a)}», yours {vb ?? "?"} «{Raw(f, b)}»{NumericHint(f, va, vb)}";
        }
        return head + $"legacy «{Show(a, 100)}», yours «{Show(b, 100)}»{Hint(a, b, f)}";
    }

    static string Raw(Field f, byte[] v) => f.Kind is "packed" or "binary" ? Convert.ToHexString(v) : Show(v, 60);

    /// <summary>The value a numeric item holds, as the program reads it (sign overpunch, packed, binary), or null when not numeric.</summary>
    static string? Decode(Field f, byte[] v)
    {
        if (v.Length == 0) return null;
        BigInteger u = 0;
        bool neg = false;
        switch (f.Kind)
        {
            case "zoned":
            {
                int ds = 0, de = v.Length;
                if (f.Signed && f.SignSeparate) { neg = (f.SignLeading ? v[0] : v[^1]) == '-'; if (f.SignLeading) ds++; else de--; }
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

    static string NumericHint(Field f, string? a, string? b)
    {
        if (a is null || b is null) return b is null && a is not null ? " (yours is not a valid number in this USAGE)" : "";
        if (!decimal.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
            || !decimal.TryParse(b, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y)) return "";
        if (x == y) return " (same value, different encoding: check the sign convention and USAGE)";
        if (!f.Signed && x == Math.Abs(y) && y < 0) return " (the item is unsigned: the sign is dropped)";
        var unit = f.Scale > 0 ? (decimal)Math.Pow(10, -f.Scale) : 1m;
        if (Math.Abs(x - y) == unit) return " (one unit apart in the last digit: the program truncates, it rounds only where ROUNDED is coded)";
        int intDigits = f.Digits - Math.Max(0, f.Scale);
        if (intDigits > 0 && intDigits < 27)
        {
            var cap = (decimal)Math.Pow(10, intDigits);
            if (Math.Abs(y) >= cap && x == Math.Sign(y) * (Math.Abs(y) % cap)) return $" (integer digits beyond the PICTURE's {intDigits} are dropped on the left)";
            // A value pinned at the PICTURE's maximum: COBOL never clamps, it keeps the low-order digits of the true result.
            if (Regex.IsMatch(b.TrimStart('-').Split('.')[0], $"^9{{{intDigits}}}$"))
                return $" (yours sits at the PICTURE's maximum: COBOL does not clamp; a result with more than {intDigits} integer digits keeps its low-order digits)";
        }
        return "";
    }

    static string Hint(byte[] a, byte[] b, Field? f)
    {
        if (a.Length != b.Length) return "";
        bool spacesVsZero = true, zeroVsSpaces = true;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == b[i]) continue;
            if (!(a[i] == ' ' && b[i] == 0)) spacesVsZero = false;
            if (!(a[i] == 0 && b[i] == ' ')) zeroVsSpaces = false;
        }
        if (spacesVsZero) return " (the program has spaces here, from a VALUE SPACES or a MOVE that pads; yours left the bytes at x'00')";
        if (zeroVsSpaces) return " (the program never assigns these bytes: they keep the storage default, x'00' here; yours wrote spaces)";
        var ta = Encoding.Latin1.GetString(a);
        var tb = Encoding.Latin1.GetString(b);
        if (ta.Trim() == tb.Trim() && ta.Trim().Length > 0) return " (same text, different position or padding)";
        if (f?.Kind == "edited") return " (an edited PICTURE: format the number with it, or use the record kit's setter)";
        return "";
    }

    static string RawRegions(byte[] a, byte[] b, string indent = "  ", int max = 6)
    {
        var sb = new StringBuilder();
        int min = Math.Min(a.Length, b.Length), i = 0, regions = 0;
        while (i < min && regions < max)
        {
            if (a[i] == b[i]) { i++; continue; }
            int s = i, e = i;
            while (e + 1 < min)
            {
                int k = e + 1;
                while (k < min && k <= e + 8 && a[k] == b[k]) k++;
                if (k < min && k <= e + 8) e = k; else break;
            }
            regions++;
            sb.Append($"{indent}bytes {s}-{e}: legacy «{Show(a, s - 10, e + 10)}» yours «{Show(b, s - 10, e + 10)}»\n");
            i = e + 1;
        }
        if (a.Length != b.Length) sb.Append($"{indent}lengths differ: legacy {a.Length}, yours {b.Length}\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------ CALL arguments (subprograms)

    string Calls(ProbeScenario s)
    {
        var sb = new StringBuilder();
        if (s.LegacyCalls.Count != s.YourCalls.Count)
            sb.Append($"CALLs: the caller made {s.LegacyCalls.Count} CALL(s) with the faithful subprogram, {s.YourCalls.Count} with yours.\n");
        for (int c = 0; c < Math.Min(s.LegacyCalls.Count, s.YourCalls.Count); c++)
        {
            var la = s.LegacyCalls[c];
            var ya = s.YourCalls[c];
            bool sameIn = la.Before.Count == ya.Before.Count && la.Before.Zip(ya.Before).All(p => p.First.AsSpan().SequenceEqual(p.Second));
            var differing = Enumerable.Range(0, Math.Min(la.After.Count, ya.After.Count)).Where(i => !la.After[i].AsSpan().SequenceEqual(ya.After[i])).ToList();
            if (differing.Count == 0) continue;
            sb.Append($"CALL {c + 1} (the first whose results differ{(sameIn ? "" : "; the caller's arguments already differed, because an earlier CALL left different results")}):\n");
            for (int i = 0; i < la.Before.Count; i++)
            {
                var name = i < linkage.Count ? linkage[i] : $"argument {i + 1}";
                var l = layouts.GetValueOrDefault(name) ?? layouts.Values.FirstOrDefault(x => x.Length == la.Before[i].Length && x.Program != "");
                sb.Append($"  {name} passed in: «{Show(la.Before[i], 140)}»\n");
            }
            foreach (var i in differing)
            {
                var name = i < linkage.Count ? linkage[i] : $"argument {i + 1}";
                var l = layouts.GetValueOrDefault(name);
                if (l is null) { sb.Append($"  {name} after the CALL:\n").Append(RawRegions(la.After[i], ya.After[i], "    ")); continue; }
                sb.Append($"  {name} after the CALL ({l.Name}):\n");
                foreach (var x in FieldDiffs(l, la.After[i], ya.After[i]).Take(8)) sb.Append("    ").Append(x.Text).Append('\n');
                var changedByLegacy = FieldDiffs(l, la.Before[i], la.After[i]).Select(x => x.Field.Name).Distinct().ToList();
                sb.Append($"    (the faithful {name} changes {(changedByLegacy.Count == 0 ? "nothing in it" : string.Join(", ", changedByLegacy))} on this CALL)\n");
            }
            break;
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ where the logic parts

    /// <summary>
    /// The faithful event that wrote the earliest differing output record (in the order the program ran), with its statement,
    /// PERFORM chain and rules; the input records read just before it; and what the model wrote there instead.
    /// </summary>
    string Divergence(ProbeScenario s, List<Diff> diffs)
    {
        if (s.Trace.Count == 0 || diffs.Count == 0) return "";
        (int Event, Diff Diff, Op Op)? earliest = null;
        foreach (var d in diffs)
        {
            var op = d.Ops.FirstOrDefault(o => o.Kind != OpKind.Equal);
            if (op is null) continue;
            int ev = EventOf(s, d, op);
            if (ev >= 0 && (earliest is null || ev < earliest.Value.Event)) earliest = (ev, d, op);
        }
        if (earliest is null) return "";
        var (index, diff, first) = earliest.Value;
        var e = s.Trace[index];
        var sb = new StringBuilder("Where the runs part");
        sb.Append(s.TraceTotal > s.Trace.Count ? $" (the first {s.Trace.Count} of {s.TraceTotal} traced effects):\n" : ":\n");
        var unit = diff.Format == "sysout" ? "line" : "record";
        int before = diff.Ops.TakeWhile(o => o != first).Count(o => o.L >= 0);
        var recordName = first.Kind == OpKind.Extra
            ? $"{unit} {first.Y + 1} of yours, which comes before the program's {unit} {before + 1}"
            : $"{unit} {first.L + 1}";
        sb.Append($"- The first difference in the order the program runs is {diff.Output} {recordName} ");
        sb.Append(first.Kind == OpKind.Extra ? $"(yours has it, the program does not: «{Show(diff.Yours[first.Y], 120)}»). " : first.Kind == OpKind.Missing ? "(missing in yours). " : "(it differs). ");
        sb.Append(first.Kind == OpKind.Extra
            ? $"The program's next {unit} there is effect {index + 1} of its run:\n"
            : $"The faithful program {(e.Op == "DISPLAY" ? "displayed it" : "wrote it")} as effect {index + 1} of its run:\n");
        var chain = Chain(e);
        if (chain.Count > 0)
        {
            sb.Append($"    {chain[0].Tag} {chain[0].Text}\n");
            foreach (var c in chain.Skip(1).Take(5)) sb.Append($"    performed from {c.Tag} {c.Text}\n");
            var decisive = chain.SelectMany(c => RulesOf(c.Tag)).Where(r => r.When.Count > 0).DistinctBy(r => r.Id).Take(3).ToList();
            foreach (var r in decisive) sb.Append($"    under rule {r.Id} ({r.Paragraph}): {string.Join(" AND ", r.When.Select(w => Clip(w, 120)))}\n");
        }
        // The inputs the program had just read (the most recent READ per file).
        var reads = new List<ProbeEvent>();
        for (int k = index - 1; k >= 0 && reads.Count < 3; k--)
            if (s.Trace[k].Op.StartsWith("READ", StringComparison.Ordinal) && !reads.Any(r => r.Dd == s.Trace[k].Dd)) reads.Add(s.Trace[k]);
        foreach (var r in reads)
            sb.Append(r.Status == 10 ? $"    after {r.Op} {r.Dd}: at end of file\n"
                : r.Status == 23 || r.Data.Length == 0 ? $"    after {r.Op} {r.Dd}: status {r.Status:00}{(r.Status == 23 ? " (no record with that key)" : "")}\n"
                : $"    after {r.Op} {r.Dd}{(r.Status == 0 ? "" : $" (status {r.Status:00})")}: «{Show(r.Data, 110)}»\n");
        // What the model produced at that point.
        if (first.Kind is OpKind.Changed or OpKind.Extra && first.Y >= 0 && first.Y < diff.Yours.Count && diff.Format != "sysout")
        {
            var mine = diff.Yours[first.Y];
            var (l, score) = Match(diff, mine);
            var (ll, _) = first.L >= 0 && first.L < diff.Legacy.Count ? Match(diff, diff.Legacy[first.L]) : (null, 0);
            if (l is not null && score >= 5 && (ll is null || ll.Name != l.Name))
            {
                sb.Append($"- Yours wrote a {l.Name} {(diff.Format.StartsWith("print") ? "line" : "record")} there.");
                var ws = writers.Where(w => w.Layout == l.Name && diff.Ds is not null && w.File == diff.Ds.File).ToList();
                if (ws.Count > 0) sb.Append($" The program writes {l.Name} only in {string.Join("; ", ws.Take(3).Select(w => $"{w.Paragraph} ({w.Cobol})"))}.");
                var why = LiteralRules(l, mine);
                if (why.Count > 0) sb.Append($" Its content comes from {string.Join("; ", why.Take(3).Select(r => $"rule {r.Id} ({r.Paragraph}{(r.When.Count > 0 ? ", when " + string.Join(" AND ", r.When.Select(w => Clip(w, 100))) : "")})"))}: the faithful program did not take that path here.");
                sb.Append('\n');
            }
        }
        else if (first.Kind == OpKind.Missing) sb.Append("- Yours wrote nothing there: the statement above (or the branch around it) did not run in your code.\n");
        return sb.ToString();
    }

    /// <summary>The trace event that wrote a differing record: the n-th write to its DD (by key for an indexed file), the n-th DISPLAY for SYSOUT.</summary>
    static int EventOf(ProbeScenario s, Diff d, Op op)
    {
        int legacyIndex = op.L >= 0 ? op.L : d.Ops.TakeWhile(o => o != op).Count(o => o.L >= 0);
        if (d.Format == "sysout")
        {
            int n = -1;
            for (int k = 0; k < s.Trace.Count; k++)
                if (s.Trace[k].Op == "DISPLAY" && ++n == legacyIndex) return k;
            return -1;
        }
        if (d.Ds is null) return -1;
        if (d.Ds.KeyOffset is { } ko && d.Ds.KeyLength is { } kl && op.L >= 0)
        {
            var key = Encoding.Latin1.GetString(Slice(d.Legacy[op.L], ko, kl));
            for (int k = s.Trace.Count - 1; k >= 0; k--)
                if (s.Trace[k].Dd == d.Ds.Dd && s.Trace[k].Op is "WRITE" or "REWRITE" && Encoding.Latin1.GetString(Slice(s.Trace[k].Data, ko, kl)) == key) return k;
            return -1;
        }
        int w = -1;
        for (int k = 0; k < s.Trace.Count; k++)
            if (s.Trace[k].Dd == d.Ds.Dd && s.Trace[k].Op == "WRITE" && ++w == legacyIndex) return k;
        return -1;
    }

    sealed record Step(string Tag, string Text);

    static readonly Regex TagRx = new(@"^\s*// \[([^\]]+)\] ?(.*)$");

    /// <summary>The statement at each faithful frame, innermost first, from the step tags above the frame's line.</summary>
    List<Step> Chain(ProbeEvent e) => Chain(e.At);

    /// <summary>
    /// The COBOL statements at faithful frames, innermost first, as "step-id statement" (a CALL's place in the faithful run, for
    /// the shadow feedback and the debug prompt).
    /// </summary>
    public List<(string Tag, string Text)> StatementsAt(IEnumerable<ProbeFrame> frames) => Chain(frames).Select(s => (s.Tag, s.Text)).ToList();

    List<Step> Chain(IEnumerable<ProbeFrame> frames)
    {
        var list = new List<Step>();
        foreach (var f in frames)
        {
            var t = Tags(f.Class);
            var tag = t.LastOrDefault(x => x.Line <= f.Line);
            if (tag.Tag is null) continue;
            if (list.Count > 0 && list[^1].Tag == tag.Tag) continue;
            list.Add(new Step(tag.Tag, Clip(tag.Text, 140)));
        }
        return list;
    }

    List<(int Line, string Tag, string Text)> Tags(string cls)
    {
        if (tags.TryGetValue(cls, out var t)) return t;
        t = [];
        if (sourceRoot is not null)
        {
            var path = Path.Combine(sourceRoot, cls.Replace('.', Path.DirectorySeparatorChar) + ".java");
            // The compatibility entry point (com.mlcpu.core.Prog_X) holds no logic; frames name the class that declares the method.
            if (File.Exists(path))
            {
                var lines = File.ReadAllLines(path);
                for (int i = 0; i < lines.Length; i++)
                    if (TagRx.Match(lines[i]) is { Success: true } m) t.Add((i + 1, m.Groups[1].Value, m.Groups[2].Value));
            }
        }
        tags[cls] = t;
        return t;
    }

    /// <summary>
    /// The rules a statement belongs to; a statement in no rule (a PERFORM, typically) gets the rule just before it in its
    /// paragraph, whose conditions are the branch it sits in.
    /// </summary>
    IEnumerable<Rule> RulesOf(string stepId)
    {
        var direct = rules.Where(r => r.Statements.Any(s => s.StepId == stepId)).ToList();
        if (direct.Count > 0) return direct;
        var parts = stepId.Split(':');
        if (parts.Length < 4 || !int.TryParse(parts[^1].TrimStart('L'), out var line)) return [];
        var paragraph = string.Join(":", parts[..^3]);
        static int Line(string id) => int.TryParse(id.Split(':')[^1].TrimStart('L'), out var l) ? l : 0;
        var before = rules.Where(r => r.Paragraph == paragraph && r.Statements.Count > 0 && r.Statements.Max(s => Line(s.StepId)) < line)
            .OrderByDescending(r => r.Statements.Max(s => Line(s.StepId))).FirstOrDefault();
        return before is null ? [] : [before];
    }

    /// <summary>Rules that MOVE a literal into one of the record's items whose content is that literal (where a line's text came from).</summary>
    List<Rule> LiteralRules(Layout l, byte[] r)
    {
        var found = new List<Rule>();
        foreach (var f in l.Fields.Where(f => f.Kind == "text" && f.Occurs.Count == 0 && f.Offset < r.Length))
        {
            var value = Norm(Encoding.Latin1.GetString(Slice(r, f.Offset, f.Length)));
            if (value.Length < 3) continue;
            var rx = new Regex(@"^MOVE\s+'([^']*)'\s+TO\s+(.+)$");
            foreach (var rule in rules)
                foreach (var (_, cobol) in rule.Statements)
                    if (rx.Match(cobol) is { Success: true } m && Norm(m.Groups[1].Value) == value
                        && m.Groups[2].Value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(f.Name) && !found.Contains(rule))
                        found.Add(rule);
        }
        return found;
    }

    static string Norm(string s) => Regex.Replace(s, @"\s+", " ").Trim();

    // ------------------------------------------------------------------ text

    static string Show(byte[] b, int max) => Show(b, 0, Math.Min(b.Length, max) - 1) + (b.Length > max ? "…" : "");

    static string Show(byte[] b, int from, int to)
    {
        var sb = new StringBuilder();
        for (int i = Math.Max(0, from); i <= Math.Min(b.Length - 1, to); i++)
        {
            int c = b[i];
            sb.Append(c == '\n' ? "\\n" : c == '\f' ? "\\f" : c is >= 0x20 and < 0x7F ? ((char)c).ToString() : $"\\x{c:x2}");
        }
        return sb.ToString();
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
