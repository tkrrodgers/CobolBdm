using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// Legacy procedure-division control flow structured at the COBOL level, before the classic lift (Loop 2): SECTIONs and
/// PERFORM ... THRU ranges become one paragraph each, GO TO inside a range becomes the structured statement it stands
/// for (a loop, an IF, the end of the range), a GO TO ... DEPENDING ON dispatch through an INSPECT TALLYING code table
/// becomes an EVALUATE of the code, SET condition TO FALSE the MOVE of its WHEN SET TO FALSE value, and paragraphs no
/// path reaches (an ALTERed switch left over) are dropped. Every rewrite is an equivalence of the COBOL control flow on
/// every input; a range it cannot prove is left as it is, so the lift reports its GO TOs as residue (fail closed). An
/// estate with J3S programs (their own GO TO protocol) is never touched.
/// </summary>
public static class LegacyFlow
{
    /// <summary>One rewrite, for the ledger (modernize/legacy-flow.json).</summary>
    public sealed record Entry(string Program, int Line, string Kind, string Detail, List<int>? Lines = null);

    /// <summary>The source lines of statements (nested ones too): a rewrite's region, for the coverage join.</summary>
    static List<int> LinesOf(IEnumerable<Stmt> stmts) => Model.Deep(stmts).Select(s => s.Line).Where(l => l > 0).Distinct().Order().ToList();

    /// <summary>The source lines of the program's statements whose operands use one of the items.</summary>
    static List<int> UsesOf(SourceProgram sp, IEnumerable<DataItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        return sp.Model.Paragraphs.SelectMany(p => Model.Deep(p.Statements)).Where(s => Structurer.RefsOf(s).Any(n => n.Id is int id && ids.Contains(id)))
            .Select(s => s.Line).Distinct().Order().ToList();
    }

    public static void Run(Estate estate)
    {
        if (estate.Programs.Any(p => J3sComments.HeaderClass(p) is not null)) return;
        foreach (var sp in estate.Programs)
        {
            var paras = sp.Model.Paragraphs;
            if (paras.Count == 0 || paras.Any(p => p.Declarative)) continue;
            if (!paras.Any(p => p.IsSection || Model.Deep(p.Statements).Any(s => s.Verb is "GO TO" or "ALTER" or "SEARCH" or "SEARCH ALL" or "STRING" || s.Str("thru") is not null
                    || s.Verb == "SET" && s.Str("mode") is "FALSE" or "TO" or "UP" or "DOWN")))
                continue;
            var ledger = new List<Entry>();
            new Structurer(sp, ledger).Run();
            Verbs(sp, ledger);
            if (ledger.Count == 0) continue;
            sp.ParagraphIndex.Clear();
            for (int i = 0; i < paras.Count; i++) sp.ParagraphIndex.TryAdd(paras[i].Name, i);
            estate.FlowLedger.AddRange(ledger);
        }
        if (estate.FlowLedger.Count == 0) return;
        InlineCopies(estate);
        UnifyLayouts(estate);
        AbendScratch(estate);
    }

    static IEnumerable<DataItem> Descendants(DataItem d)
    {
        foreach (var c in d.Children)
        {
            yield return c;
            foreach (var x in Descendants(c)) yield return x;
        }
    }

    /// <summary>
    /// A record laid out as a copybook's record (the same items at the same offsets, PICTUREs, usages, OCCURS and
    /// condition values, their names the same past their first word) is that copybook's record wherever it is written
    /// out: a copy of the copybook with a replaceable prefix (FNSTATX REPLACING ==:P:== BY ==ST==), or the record typed
    /// into the program. One record class then serves the CALL's caller and callee. The copybook is the first by name.
    /// </summary>
    static void UnifyLayouts(Estate estate)
    {
        static string Tail(string n) => n.IndexOf('-') is var k and > 0 ? n[(k + 1)..] : n;
        // a PICTURE as its symbols: X(2) and XX, 9(09) and 9(9) are one picture
        static string Pic(string? p) => p is null ? "" : Regex.Replace(p.ToUpperInvariant(), @"(.)\((\d+)\)", m => new string(m.Groups[1].Value[0], int.Parse(m.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture)));
        static string Signature(DataItem r) => string.Join("|", Descendants(r).Prepend(r).Select(x =>
            $"{x.Level}:{x.Offset - x.Record.Offset}:{x.Length}:{x.Usage}:{Pic(x.Pic)}:{x.Occurs}:{x.IsFiller}:{Tail(x.Name).ToUpperInvariant()}:{x.RedefinesItem is not null}"
            + (x.Level == 88 ? ":" + string.Join(",", x.Values.Select(v => v.Low.Text + (v.High is { } h ? "-" + h.Text : ""))) : "")));
        var recs = estate.Programs.SelectMany(p => p.Model.Items
            .Where(i => i.Level == 1 && i.Children.Count > 0 && i.Section is DataSection.WorkingStorage or DataSection.Linkage or DataSection.LocalStorage)
            .Select(i => (P: p, I: i))).ToList();
        foreach (var grp in recs.GroupBy(x => Signature(x.I)))
        {
            var origins = grp.Select(x => x.I.Origin).Where(o => o.Length > 0).Distinct().Order(StringComparer.Ordinal).ToList();
            if (origins.Count == 0) continue;
            var canon = origins[0];
            foreach (var (p, i) in grp.Where(x => x.I.Origin != canon))
            {
                estate.FlowLedger.Add(new Entry(p.Id, i.Line, "record-layout", $"{i.Name} ({(i.Origin.Length == 0 ? "written out in the program" : "COPY " + i.Origin)}) is the layout of COPY {canon}: its record class", UsesOf(p, Descendants(i).Prepend(i))));
                foreach (var d in Descendants(i).Prepend(i)) d.Origin = canon;
            }
        }
    }

    /// <summary>
    /// A subprogram's elementary LINKAGE items that only its abend routine uses (the abend code it MOVEs a literal to
    /// right before PERFORMing the routine, the code's display field, CEE3ABD's timing argument): the caller never sees
    /// a value of them (the routine ends the run), so they are the subprogram's own work fields and leave its CALL
    /// interface (and every CALL's USING). Any other use, or a CALL that does not pass every argument, keeps them.
    /// </summary>
    static void AbendScratch(Estate estate)
    {
        var calls = estate.Programs.SelectMany(p => p.Model.Paragraphs.SelectMany(x => Model.Deep(x.Statements)).Where(s => s.Verb == "CALL").Select(s => (P: p, S: s))).ToList();
        if (calls.Any(c => c.S.Get("target") is not { K: "str" })) return;
        foreach (var sp in estate.Programs)
        {
            var paras = sp.Model.Paragraphs;
            var usingNames = sp.Model.Using;
            if (usingNames.Count == 0) continue;
            static bool Cee3abd(Stmt s) => s.Verb == "CALL" && s.Get("target") is { K: "str", V: { } t } && t.Equals("CEE3ABD", StringComparison.OrdinalIgnoreCase);
            var routines = paras.Where(p => p.Statements.Count > 0 && Cee3abd(p.Statements[^1]) && p.Statements.All(s => s.Verb is "MOVE" or "DISPLAY" or "CALL" && s.Blocks is null))
                .Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (routines.Count == 0) continue;
            bool PerformsRoutine(Stmt s) => s.Verb == "PERFORM" && s.Str("inline") != "true" && s.Str("thru") is null && s.N is null && s.L is null && routines.Contains(s.Str("target") ?? "");
            static bool Uses(Stmt s, int id) => Structurer.RefsOf(s).Any(n => n.Id == id);
            static bool MovesLiteral(Stmt s, int id) => s.Verb == "MOVE" && s.Get("src") is { K: "num" or "str" } && s.L?.GetValueOrDefault("dst") is [{ K: "ref", Id: int d, Args: null or { Count: 0 }, Start: null }] && d == id;
            var lists = paras.Select(p => p.Statements).Concat(paras.SelectMany(p => Model.Deep(p.Statements)).SelectMany(s => s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>())).ToList();
            var drop = new List<int>();
            for (int k = 0; k < usingNames.Count; k++)
            {
                if (!sp.ItemsByName.TryGetValue(usingNames[k], out var it) || it.Section != DataSection.Linkage || it.Level is not (1 or 77)
                    || it.Children.Count > 0 || it.RedefinesItem is not null || sp.Model.Items.Any(x => x.RedefinesItem == it)) continue;
                bool ok = true;
                // outside the routine: only MOVE literal TO it, right before a PERFORM of the routine
                foreach (var list in lists)
                    for (int i = 0; i < list.Count && ok; i++)
                        if (Uses(list[i], it.Id) && !routines.Contains(OwnerOf(paras, list) ?? "")
                            && !(MovesLiteral(list[i], it.Id) && i + 1 < list.Count && PerformsRoutine(list[i + 1])))
                            ok = false;
                // inside the routine: read only after the routine writes it, or as CEE3ABD's argument, or set before every PERFORM of it
                foreach (var r in paras.Where(p => routines.Contains(p.Name)))
                {
                    bool written = false;
                    foreach (var s in r.Statements)
                    {
                        if (!Uses(s, it.Id)) continue;
                        bool isDst = s.Verb == "MOVE" && s.L?.GetValueOrDefault("dst") is [{ Id: int d }] && d == it.Id;
                        if (isDst) { written = true; continue; }
                        if (written || Cee3abd(s)) continue;
                        // a read of the caller's value unless every PERFORM of the routine follows MOVE literal TO it
                        foreach (var list in lists)
                            for (int i = 0; i < list.Count; i++)
                                if (PerformsRoutine(list[i]) && Eq(list[i].Str("target")!, r.Name) && !(i > 0 && MovesLiteral(list[i - 1], it.Id))) ok = false;
                    }
                }
                if (ok) drop.Add(k);
            }
            if (drop.Count == 0) continue;
            var sites = calls.Where(c => c.S.Get("target") is { V: { } t } && t.Equals(sp.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (sites.Count == 0 || sites.Any(c => c.S.L?.GetValueOrDefault("args") is not { } a || a.Count != usingNames.Count)) continue;
            foreach (var c in sites)
            {
                var args = c.S.L!["args"];
                foreach (var k in drop.OrderByDescending(x => x)) args.RemoveAt(k);
                estate.FlowLedger.Add(new Entry(c.P.Id, c.S.Line, "abend-scratch-argument", $"CALL '{sp.Id}': {string.Join(", ", drop.Select(k => usingNames[k]))} only its abend routine uses: not passed", [c.S.Line]));
            }
            foreach (var k in drop.OrderByDescending(x => x))
            {
                var it = sp.ItemsByName[usingNames[k]];
                it.Section = DataSection.WorkingStorage;
                estate.FlowLedger.Add(new Entry(sp.Id, it.Line, "abend-scratch", $"LINKAGE {it.Name}: only the abend routine uses it (no value reaches or leaves the CALL): a work field", UsesOf(sp, [it])));
                usingNames.RemoveAt(k);
            }
        }
    }

    static string? OwnerOf(List<Paragraph> paras, List<Stmt> list) =>
        paras.FirstOrDefault(p => ReferenceEquals(p.Statements, list) || Model.Deep(p.Statements).Any(s => s.Blocks?.Values.Any(b => ReferenceEquals(b, list)) == true))?.Name;

    static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    // ================================================================ table and string verbs

    /// <summary>
    /// The table and string verbs the lift takes as their structured equivalents: an index name (INDEXED BY) is the
    /// occurrence number it stands for (SET TO, UP BY, DOWN BY: MOVE, ADD, SUBTRACT); a serial SEARCH is a loop over
    /// the index; SEARCH ALL of a constant table with unique ascending keys is a loop too (the one entry with the key);
    /// STRING ... DELIMITED BY SIZE into a group whose fields its pieces fill is a MOVE per field. Anything else stays.
    /// </summary>
    static void Verbs(SourceProgram sp, List<Entry> ledger)
    {
        var indexes = sp.Model.Items.Where(i => i.Usage == Usage.Index && i.Level == 77).ToDictionary(i => i.Id);
        var converted = new HashSet<DataItem>();
        Stmt Mk(Stmt like, string verb, string text) => new() { Verb = verb, StepId = like.StepId, Line = like.Line, Sentence = like.Sentence, Text = text };
        Stmt Move(Stmt like, Node src, Node dst) => Mk(like, "MOVE", like.Text).Set("src", src).Set("dst", [dst]);
        Stmt AddTo(Stmt like, Node n, Node dst, bool sub) => Mk(like, sub ? "SUBTRACT" : "ADD", like.Text).Set("ops", [n]).Set("dst", [dst]).Set("form", sub ? "FROM" : "TO");
        bool IsIndex(Node? n) => n is { K: "ref", Id: int id } && indexes.ContainsKey(id);
        Node Gt(Node ix, int occ) => new() { K = "rel", Op = "gt", L = ix, R = new Node { K = "num", V = occ.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
        List<Stmt> Loop(Stmt s, Node ix, int occ, Node cond, List<Stmt> atEnd, List<Stmt> found)
        {
            var until = new Node { K = "or", L = Gt(ix, occ), R = cond };
            var perform = Mk(s, "PERFORM", s.Text).Set("inline", "true").Set("until", until).Block("LOOP", [AddTo(s, new Node { K = "num", V = "1" }, ix, false)]);
            if (atEnd.Count == 0 && found.Count == 0) return [perform];
            var test = Mk(s, "IF", s.Text);
            if (atEnd.Count == 0) test.Set("cond", new Node { K = "not", L = Gt(ix, occ) }).Block("TRUE", found);
            else
            {
                test.Set("cond", Gt(ix, occ)).Block("TRUE", atEnd);
                if (found.Count > 0) test.Block("FALSE", found);
            }
            return [perform, test];
        }
        void Rewrite(List<Stmt> list)
        {
            for (int k = 0; k < list.Count; k++)
            {
                var s = list[k];
                foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) Rewrite(b);
                List<Stmt>? with = null;
                string? why = null;
                if (s.Verb == "SET" && s.Str("mode") is "TO" or "UP" or "DOWN" && s.Get("value") is { } val && s.L?.GetValueOrDefault("dst") is [var dst]
                    && (IsIndex(dst) || IsIndex(val)) && dst.K == "ref" && val.K is "ref" or "num" && dst.Start is null)
                {
                    if (s.Str("mode") == "TO") with = [Move(s, val, dst)];
                    else if (IsIndex(dst)) with = [AddTo(s, val, dst, s.Str("mode") == "DOWN")];
                    if (with is not null) why = $"{s.Text}: the index's occurrence number";
                }
                else if (s.Verb == "SEARCH" && s.Get("table") is { Id: int tid } && s.Get("index") is { } ix && IsIndex(ix) && s.Get("varying") is null
                    && sp.ItemsById.TryGetValue(tid, out var table) && table.Occurs > 0 && table.DependingOn is null
                    && s.Blocks?.GetValueOrDefault("WHEN") is [{ } when] && when.Get("cond") is { } cond)
                {
                    with = Loop(s, ix, table.Occurs, cond, s.Blocks.GetValueOrDefault("AT-END") ?? [], when.Blocks?.GetValueOrDefault("TRUE") ?? []);
                    why = $"SEARCH {table.Name} (serial, from the index's occurrence): a loop over {ix.V}";
                }
                else if (s.Verb == "SEARCH ALL" && SearchAll(sp, s, IsIndex) is { } all)
                {
                    var (ix2, occ, cond2, keys) = all;
                    with = [Move(s, new Node { K = "num", V = "1" }, ix2), .. Loop(s, ix2, occ, cond2, s.Blocks!.GetValueOrDefault("AT-END") ?? [], s.Blocks["WHEN"][0].Blocks?.GetValueOrDefault("TRUE") ?? [])];
                    why = $"SEARCH ALL (binary): the constant table's {occ} keys are unique and ascending ({keys}), so the one entry with the key is the first: a loop over {ix2.V}";
                }
                else if (s.Verb == "STRING" && StringFields(sp, s) is { } moves)
                {
                    with = moves.Select(m => Move(s, m.Src, m.Dst)).ToList();
                    why = $"STRING ... DELIMITED BY SIZE INTO {s.Get("into")!.V}: its pieces fill the group's fields, a MOVE each";
                }
                else if (s.Verb == "PERFORM" && s.Str("inline") != "true" && s.L?.GetValueOrDefault("after") is [{ K: "after", Args: [{ K: "ref" } av, var af, var ab, var au] }]
                    && s.Get("var") is { K: "ref" } v1 && s.Get("from") is { } f1 && s.Get("by") is { } b1 && s.Get("until") is { } u1 && s.Str("test") != "AFTER")
                {
                    // PERFORM ... VARYING v1 FROM f1 BY b1 UNTIL u1 AFTER v2 FROM f2 BY b2 UNTIL u2: v1 and v2 set, then while
                    // not u1: (while not u2: the paragraphs, v2 augmented), v1 augmented and v2 set to f2 again
                    var call = Mk(s, "PERFORM", "PERFORM " + s.Str("target"));
                    call.S = new Dictionary<string, string>(s.S!);
                    var inner = Mk(s, "PERFORM", s.Text).Set("inline", "true").Set("until", au).Block("LOOP", [call, AddTo(s, ab, av, false)]);
                    var outer = Mk(s, "PERFORM", s.Text).Set("inline", "true").Set("until", u1).Block("LOOP", [inner, AddTo(s, b1, v1, false), Move(s, af, av)]);
                    with = [Move(s, f1, v1), Move(s, af, av), outer];
                    why = $"PERFORM {s.Str("target")} VARYING {v1.V} ... AFTER {av.V}: the nested loops (the inner variable set again after each outer step)";
                }
                if (with is null) continue;
                foreach (var n in with.SelectMany(w => Model.Deep([w])).SelectMany(Structurer.RefsOf)) if (n.Id is int id && indexes.TryGetValue(id, out var ixItem)) converted.Add(ixItem);
                list.RemoveAt(k);
                list.InsertRange(k, with);
                k += with.Count - 1;
                ledger.Add(new Entry(sp.Id, s.Line, s.Verb.ToLowerInvariant().Replace(' ', '-'), why!, LinesOf([s])));
            }
        }
        foreach (var p in sp.Model.Paragraphs) Rewrite(p.Statements);
        // an index name the rewrites use is a binary occurrence number (when no statement left treats it as an index)
        foreach (var ix in converted)
        {
            if (sp.Model.Paragraphs.SelectMany(p => Model.Deep(p.Statements)).Any(s => s.Verb is "SET" or "SEARCH" or "SEARCH ALL" && Structurer.RefsOf(s).Any(n => n.Id == ix.Id))) continue;
            ix.Usage = Usage.Binary;
            ix.Category = Category.Numeric;
            ix.Pic = "S9(9)";
            ix.Digits = 9;
            ix.Scale = 0;
            ix.Signed = true;
            ix.Length = 4;
            ledger.Add(new Entry(sp.Id, ix.Line, "index-name", $"{ix.Name}: an occurrence number (PIC S9(9) BINARY)", UsesOf(sp, [ix])));
        }
    }

    /// <summary>
    /// SEARCH ALL t AT END ... WHEN key (ix) = value: the table is a constant (its record's VALUEs, no statement writes
    /// it), its keys are unique and ascending, the index is used by this statement only and the value does not depend on
    /// it. Then the binary search finds the one entry with the key, as a serial search from the first does.
    /// </summary>
    static (Node Ix, int Occurs, Node Cond, string Keys)? SearchAll(SourceProgram sp, Stmt s, Func<Node?, bool> isIndex)
    {
        if (s.Get("table") is not { Id: int tid } || s.Get("index") is not { } ix || !isIndex(ix) || !sp.ItemsById.TryGetValue(tid, out var table)) return null;
        if (table.Occurs <= 0 || table.DependingOn is not null || table.Keys.Count != 1 || !table.Keys[0].Ascending) return null;
        if (s.Blocks?.GetValueOrDefault("WHEN") is not [{ } when] || when.Get("cond") is not { K: "rel", Op: "eq", L: { K: "ref", Id: int kid, Args: [{ Id: int aid }], Start: null }, R: { } value } cond) return null;
        if (aid != ix.Id || !sp.ItemsById.TryGetValue(kid, out var key) || !key.Name.Equals(table.Keys[0].Name, StringComparison.OrdinalIgnoreCase) || key.Category == Category.Numeric) return null;
        if (Nodes(value).Any(n => n.Id == ix.Id)) return null;
        // the index: this statement only
        var inside = Model.Deep([s]).ToHashSet(ReferenceEqualityComparer.Instance);
        var all = sp.Model.Paragraphs.SelectMany(p => Model.Deep(p.Statements)).ToList();
        if (all.Any(x => !inside.Contains(x) && Structurer.RefsOf(x).Any(n => n.Id == ix.Id))) return null;
        // the table's record and what it redefines: constants (VALUE), used by no other statement
        var rec = table.Record;
        var image = rec.RedefinesItem ?? rec;
        var recs = new[] { rec, image }.Distinct().ToList();
        if (all.Any(x => !inside.Contains(x) && Structurer.RefsOf(x).Any(n => n.Id is int id && sp.ItemsById.TryGetValue(id, out var d) && recs.Contains(d.Record)))) return null;
        var bytes = new char[image.Length];
        Array.Fill(bytes, ' ');
        foreach (var leaf in Leaves(image))
        {
            if (leaf.Category == Category.Numeric || leaf.Values.Count != 1 || leaf.Values[0].Low is not { Quoted: true } v || leaf.Occurs > 0 || leaf.Dims.Count > 0) return null;
            v.Text.PadRight(leaf.Length)[..leaf.Length].CopyTo(0, bytes, leaf.Offset - image.Offset, leaf.Length);
        }
        int stride = table.Length, keyAt = key.Offset - table.Offset, tableAt = table.Offset - rec.Offset;
        if (key.Dims.Count != 1 || tableAt + stride * table.Occurs > image.Length) return null;
        var keys = Enumerable.Range(0, table.Occurs).Select(i => new string(bytes, tableAt + i * stride + keyAt, key.Length)).ToList();
        for (int i = 1; i < keys.Count; i++) if (string.CompareOrdinal(keys[i - 1], keys[i]) >= 0) return null;
        return (ix, table.Occurs, cond, $"{keys[0].TrimEnd()} .. {keys[^1].TrimEnd()}");
    }

    static IEnumerable<DataItem> Leaves(DataItem d)
    {
        if (d.Children.Count == 0 || d.Children.All(c => c.Level == 88)) { if (d.Level != 88) yield return d; yield break; }
        foreach (var c in d.Children) foreach (var l in Leaves(c)) yield return l;
    }

    static IEnumerable<Node> Nodes(Node n)
    {
        yield return n;
        foreach (var c in new[] { n.L, n.R, n.Start, n.Len }.Concat(n.Args ?? []))
            if (c is not null) foreach (var x in Nodes(c)) yield return x;
    }

    /// <summary>
    /// STRING pieces DELIMITED BY SIZE INTO a group (no POINTER, no OVERFLOW): each piece's characters go to the
    /// group's field at the piece's position. A field a piece fills exactly takes it as a MOVE when that copies the same
    /// characters (text to text, an unsigned integer to an unsigned integer of its digits); a literal over a field of
    /// the same VALUE no statement can change is already there. Characters past the pieces stay (as STRING leaves them).
    /// </summary>
    static List<(Node Src, Node Dst)>? StringFields(SourceProgram sp, Stmt s)
    {
        if (s.Get("into") is not { K: "ref", Id: int gid, Args: null or { Count: 0 }, Start: null } || s.Get("pointer") is not null || s.Blocks is { Count: > 0 }) return null;
        if (!sp.ItemsById.TryGetValue(gid, out var group) || group.Children.Count == 0 || group.Occurs > 0 || group.Dims.Count > 0) return null;
        var pieces = s.L?.GetValueOrDefault("pieces");
        if (pieces is null || pieces.Count == 0 || pieces.Any(p => p is not { K: "piece", R.K: "size" })) return null;
        var fields = Leaves(group).ToList();
        var moves = new List<(Node, Node)>();
        int at = 0;
        // a statement that writes the group, a record that holds it, or the field: a literal's field could change
        var others = sp.Model.Paragraphs.SelectMany(p => Model.Deep(p.Statements)).Where(x => !ReferenceEquals(x, s)).ToList();
        bool Writes(Stmt x, DataItem f) => (x.L?.GetValueOrDefault("dst") ?? []).Concat(x.Get("into") is { } i ? [i] : [])
            .Any(n => n.Id is int id && sp.ItemsById.TryGetValue(id, out var d) && (d == f || Descendant(d, f)));
        foreach (var p in pieces)
        {
            var src = p.L!;
            int len;
            DataItem? si = null;
            if (src.K == "str") len = (src.V ?? "").Length;
            else if (src is { K: "ref", Id: int sid, Args: null or { Count: 0 }, Start: null } && sp.ItemsById.TryGetValue(sid, out si) && si.IsLeaf && si.Usage == Usage.Display && si.Dims.Count == 0) len = si.Length;
            else return null;
            var f = fields.FirstOrDefault(x => x.Offset - group.Offset == at);
            if (f is null || f.Length != len || f.Dims.Count > 0) return null;
            if (si is null)
            {
                if (f.Values is not [{ Low: { Quoted: true } fv }] || fv.Text.PadRight(f.Length)[..f.Length] != src.V || others.Any(x => Writes(x, f))) return null;
            }
            else
            {
                if (f.IsFiller) return null;
                bool text = si.Category != Category.Numeric && f.Category is Category.Alphanumeric or Category.Alphabetic;
                bool digits = si.Category == Category.Numeric && !si.Signed && si.Scale == 0 && f.Category == Category.Numeric && f.Usage == Usage.Display && !f.Signed && f.Scale == 0 && f.Digits == si.Digits;
                if (!text && !digits) return null;
                moves.Add((src, new Node { K = "ref", V = f.Name, Id = f.Id }));
            }
            at += len;
        }
        return at <= group.Length ? moves : null;
    }

    static bool Descendant(DataItem ancestor, DataItem d)
    {
        for (var x = d.Parent; x is not null; x = x.Parent) if (x == ancestor) return true;
        return false;
    }

    // ================================================================ inlined copies of subprograms

    /// <summary>
    /// A subprogram's procedure copied into a caller (FNDTKRN INLINED HERE (WAS A CALL)): the paragraphs a PERFORM of X
    /// runs are, statement for statement, the subprogram's from its entry (its last GOBACK the end of the range), its
    /// items mapped one to one onto the caller's of the same layout and initial values, its LINKAGE record onto one of the
    /// caller's records, and its working storage onto caller records nothing else in the caller uses. Then PERFORM X is
    /// CALL 'K' USING that record (the caller's own instance of K holds what the copy's private records held) and the copy
    /// goes. Any difference of statement, operand, layout or use keeps the copy.
    /// </summary>
    static void InlineCopies(Estate estate)
    {
        var subs = estate.Programs.Where(k => k.Model.Using.Count == 1 && k.Model.Paragraphs.Count > 0
            && !k.Model.Paragraphs.SelectMany(p => Model.Deep(p.Statements)).Any(s => s.Verb is "CALL" or "GO TO" or "ALTER")).ToList();
        foreach (var sp in estate.Programs)
        {
            var paras = sp.Model.Paragraphs;
            var index = paras.Select((p, i) => (p.Name, i)).ToDictionary(x => x.Name, x => x.i, StringComparer.OrdinalIgnoreCase);
            var performed = paras.SelectMany(p => Model.Deep(p.Statements)).Where(s => s.Verb == "PERFORM" && s.Str("inline") != "true" && s.Str("thru") is null)
                .Select(s => s.Str("target")!).Where(index.ContainsKey).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            foreach (var x in performed)
                foreach (var k in subs.Where(k => k != sp))
                {
                    if (!index.ContainsKey(x)) break;
                    var m = new CopyMatch(sp, k);
                    if (!m.Match(x))
                    {
                        if (Environment.GetEnvironmentVariable("LEGACYFLOW_DEBUG") == "1" && x.StartsWith(sp.Id.Length > 0 ? "DTK" : "", StringComparison.OrdinalIgnoreCase)) Console.Error.WriteLine($"copy {sp.Id} {x} ~ {k.Id}: {m.Why}");
                        continue;
                    }
                    var copy = m.Paragraphs.Keys.ToHashSet(StringComparer.OrdinalIgnoreCase);
                    // the PERFORMs of the copy from outside it: of X only, plain
                    var outside = paras.Where(p => !copy.Contains(p.Name)).SelectMany(p => Model.Deep(p.Statements)).ToList();
                    if (outside.Any(s => s.Verb == "PERFORM" && s.Str("inline") != "true" && copy.Contains(s.Str("target") ?? "") && (!Eq(s.Str("target")!, x) || s.N is not null || s.L is not null || s.Str("times") is not null || s.Str("thru") is not null))) { if (Environment.GetEnvironmentVariable("LEGACYFLOW_DEBUG") == "1") Console.Error.WriteLine($"copy {sp.Id} {x} ~ {k.Id}: outside perform"); continue; }
                    if (outside.Any(s => (s.L?.GetValueOrDefault("targets") ?? []).Any(n => copy.Contains(n.V ?? "")))) { if (Environment.GetEnvironmentVariable("LEGACYFLOW_DEBUG") == "1") Console.Error.WriteLine($"copy {sp.Id} {x} ~ {k.Id}: outside goto"); continue; }
                    // the copy's private records: no statement outside the copy uses them
                    var privateRecs = m.Records.Where(r => r.Value != m.KLinkage).Select(r => r.Key).ToHashSet();
                    if (outside.Any(s => Structurer.RefsOf(s).Any(n => n.Id is int id && sp.ItemsById.TryGetValue(id, out var it) && privateRecs.Contains(it.Record)))) { if (Environment.GetEnvironmentVariable("LEGACYFLOW_DEBUG") == "1") Console.Error.WriteLine($"copy {sp.Id} {x} ~ {k.Id}: private used outside"); continue; }
                    var arg = m.Records.First(r => r.Value == m.KLinkage).Key;
                    if (arg.Level != 1) { if (Environment.GetEnvironmentVariable("LEGACYFLOW_DEBUG") == "1") Console.Error.WriteLine($"copy {sp.Id} {x} ~ {k.Id}: arg level"); continue; }
                    // a record that redefines a private record (or that it redefines) is private too: mapped with it
                    if (sp.Model.Items.Any(i => i.Level == 1 && i.RedefinesItem is { } ri && privateRecs.Contains(i) != privateRecs.Contains(ri.Record)
                        && outside.Any(s => Structurer.RefsOf(s).Any(n => n.Id is int id && sp.ItemsById.TryGetValue(id, out var it) && (it.Record == i || it.Record == ri.Record))))) { if (Environment.GetEnvironmentVariable("LEGACYFLOW_DEBUG") == "1") Console.Error.WriteLine($"copy {sp.Id} {x} ~ {k.Id}: redefines partner"); continue; }
                    int line = paras.First(p => Eq(p.Name, x)).Line;
                    var copyLines = LinesOf(paras.Where(p => copy.Contains(p.Name)).SelectMany(p => p.Statements))
                        .Concat(outside.Where(s => s.Verb == "PERFORM" && Eq(s.Str("target") ?? "", x)).Select(s => s.Line)).Distinct().Order().ToList();
                    void Rewrite(List<Stmt> list)
                    {
                        for (int i = 0; i < list.Count; i++)
                        {
                            foreach (var b in list[i].Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) Rewrite(b);
                            if (list[i] is { Verb: "PERFORM" } s && s.Str("inline") != "true" && Eq(s.Str("target") ?? "", x))
                                list[i] = new Stmt { Verb = "CALL", StepId = s.StepId, Line = s.Line, Sentence = s.Sentence, Text = $"CALL '{k.Id}' USING {arg.Name}" }
                                    .Set("target", new Node { K = "str", V = k.Id }).Set("args", [new Node { K = "ref", V = arg.Name, Id = arg.Id }]);
                        }
                    }
                    foreach (var p in paras.Where(p => !copy.Contains(p.Name))) Rewrite(p.Statements);
                    paras.RemoveAll(p => copy.Contains(p.Name));
                    sp.ParagraphIndex.Clear();
                    for (int i = 0; i < paras.Count; i++) sp.ParagraphIndex.TryAdd(paras[i].Name, i);
                    index = paras.Select((p, i) => (p.Name, i)).ToDictionary(t => t.Name, t => t.i, StringComparer.OrdinalIgnoreCase);
                    estate.FlowLedger.Add(new Entry(sp.Id, line,
                        "inlined-subprogram", $"PERFORM {x}: {m.Paragraphs.Count} paragraphs that are {k.Id}'s statement for statement ({m.Items.Count} items mapped, {privateRecs.Count} private records): CALL '{k.Id}' USING {arg.Name}", copyLines));
                    break;
                }
        }
    }

    /// <summary>A statement-for-statement match of a caller's paragraphs with a subprogram's procedure.</summary>
    sealed class CopyMatch(SourceProgram p, SourceProgram k)
    {
        public readonly Dictionary<string, string> Paragraphs = new(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<DataItem, DataItem> Items = [];
        readonly Dictionary<DataItem, DataItem> back = [];
        public readonly Dictionary<DataItem, DataItem> Records = [];
        public DataItem KLinkage => k.ItemsByName[k.Model.Using[0]].Record;
        public string? Why;
        bool No(string why) { Why ??= why; return false; }

        Paragraph? P(string n) => p.Model.Paragraphs.FirstOrDefault(x => Eq(x.Name, n));
        Paragraph? K(string n) => k.Model.Paragraphs.FirstOrDefault(x => Eq(x.Name, n));

        public bool Match(string x)
        {
            var work = new Queue<(string P, string K)>();
            if (!Pair(x, k.Model.Paragraphs[0].Name, work)) return No("entry pair");
            while (work.Count > 0)
            {
                var (pn, kn) = work.Dequeue();
                var pb = P(pn)!.Statements;
                var kb = K(kn)!.Statements;
                // the entry's last GOBACK ends the CALL as the end of the range ends the PERFORM
                if (Eq(kn, k.Model.Paragraphs[0].Name) && kb.Count > 0 && kb[^1].Verb == "GOBACK") kb = kb.Take(kb.Count - 1).ToList();
                if (!List(pb, kb, work)) return No($"paragraph {pn} ~ {kn}: {Why}");
            }
            // the subprogram ends only at its entry's last statement (a GOBACK elsewhere would end the caller)
            int ends = Model.Deep(Paragraphs.Values.Select(n => K(n)!).SelectMany(q => q.Statements)).Count(s => s.Verb is "GOBACK" or "STOP RUN" or "EXIT PROGRAM");
            if (ends != (k.Model.Paragraphs[0].Statements is [.., { Verb: "GOBACK" }] ? 1 : 0)) return No("ends");
            // the items: the same layout and initial values, records onto records of the same layout
            foreach (var (pi, ki) in Items)
            {
                if (!SameItem(pi, ki)) return No($"item {pi.Name} ~ {ki.Name}");
                if (Records.TryGetValue(pi.Record, out var kr) && kr != ki.Record) return No($"record of {pi.Name}");
                Records[pi.Record] = ki.Record;
            }
            if (Records.Values.Distinct().Count() != Records.Count) return false;
            foreach (var (pr, kr) in Records)
            {
                if (pr.Length != kr.Length || !SameLayout(pr, kr)) return No($"layout {pr.Name} ~ {kr.Name}");
                if (pr.Section == DataSection.Linkage && kr != KLinkage) return false;
                if (kr.Section == DataSection.Linkage && kr != KLinkage) return false;
                if (pr.Section is not (DataSection.WorkingStorage or DataSection.Linkage)) return false;
            }
            return Records.ContainsValue(KLinkage);
        }

        bool Pair(string pn, string kn, Queue<(string, string)> work)
        {
            if (P(pn) is null || K(kn) is null) return false;
            if (Paragraphs.TryGetValue(pn, out var had)) return Eq(had, kn);
            if (Paragraphs.Values.Contains(kn, StringComparer.OrdinalIgnoreCase)) return false;
            Paragraphs[pn] = kn;
            work.Enqueue((pn, kn));
            return true;
        }

        bool List(List<Stmt> a, List<Stmt> b, Queue<(string, string)> work)
        {
            if (a.Count != b.Count) return No($"count {a.Count}/{b.Count} at {a.FirstOrDefault()?.Line}");
            for (int i = 0; i < a.Count; i++) if (!Stmt(a[i], b[i], work)) return No($"stmt {a[i].Line} {a[i].Text} ~ {b[i].Line} {b[i].Text}");
            return true;
        }

        bool Stmt(Stmt a, Stmt b, Queue<(string, string)> work)
        {
            if (a.Verb != b.Verb) return false;
            var sa = a.S ?? []; var sb = b.S ?? [];
            if (sa.Count != sb.Count) return false;
            foreach (var (key, va) in sa)
            {
                if (!sb.TryGetValue(key, out var vb)) return false;
                if (key is "target" or "thru" && a.Verb == "PERFORM") { if (!Pair(va, vb, work)) return false; }
                else if (va != vb) return false;
            }
            var na = a.N ?? []; var nb = b.N ?? [];
            if (na.Count != nb.Count || na.Any(kv => !nb.TryGetValue(kv.Key, out var y) || !Node(kv.Value, y))) return false;
            var la = a.L ?? []; var lb = b.L ?? [];
            if (la.Count != lb.Count || la.Any(kv => !lb.TryGetValue(kv.Key, out var y) || kv.Value.Count != y.Count || kv.Value.Zip(y).Any(t => !Node(t.First, t.Second)))) return false;
            var ba = a.Blocks ?? []; var bb = b.Blocks ?? [];
            if (ba.Count != bb.Count) return false;
            foreach (var (key, list) in ba) if (!bb.TryGetValue(key, out var other) || !List(list, other, work)) return false;
            return true;
        }

        bool Node(Node? a, Node? b)
        {
            if (a is null || b is null) return a is null && b is null;
            if (a.K != b.K || a.Op != b.Op || a.Rounded != b.Rounded) return false;
            if (a.Id is not null || b.Id is not null)
            {
                if (a.Id is not int pa || b.Id is not int kb || !p.ItemsById.TryGetValue(pa, out var pi) || !k.ItemsById.TryGetValue(kb, out var ki)) return false;
                if (Items.TryGetValue(pi, out var had) ? had != ki : back.ContainsKey(ki)) return false;
                Items[pi] = ki;
                back[ki] = pi;
            }
            else if (a.K is "para") return false;
            else if (a.V != b.V) return false;
            if (!Node(a.L, b.L) || !Node(a.R, b.R) || !Node(a.Start, b.Start) || !Node(a.Len, b.Len)) return false;
            var aa = a.Args ?? []; var ab = b.Args ?? [];
            return aa.Count == ab.Count && aa.Zip(ab).All(t => Node(t.First, t.Second));
        }

        static bool SameItem(DataItem a, DataItem b) =>
            (a.Level == b.Level || a.Level is 1 or 77 && b.Level is 1 or 77 && a.Children.Count == 0 && b.Children.Count == 0) && a.Usage == b.Usage && a.Category == b.Category && a.Digits == b.Digits && a.Scale == b.Scale && a.Signed == b.Signed
            && a.SignSeparate == b.SignSeparate && a.SignLeading == b.SignLeading && a.Justified == b.Justified && a.Length == b.Length && a.Occurs == b.Occurs
            && a.Offset - a.Record.Offset == b.Offset - b.Record.Offset && a.Dims.SequenceEqual(b.Dims) && a.ValueAll == b.ValueAll
            && a.Values.Select(v => (v.Low.Text, v.Low.Quoted, v.High?.Text)).SequenceEqual(b.Values.Select(v => (v.Low.Text, v.Low.Quoted, v.High?.Text)))
            && a.FalseValue?.Text == b.FalseValue?.Text && (a.Level != 88 || a.Parent is { } ap2 && b.Parent is { } bp2 && ap2.Offset - ap2.Record.Offset == bp2.Offset - bp2.Record.Offset);

        /// <summary>Two records with the same items (layout and initial values) in the same order.</summary>
        static bool SameLayout(DataItem a, DataItem b)
        {
            var da = Descendants(a).Prepend(a).ToList();
            var db = Descendants(b).Prepend(b).ToList();
            return da.Count == db.Count && da.Zip(db).All(t => (t.First == a || SameItem(t.First, t.Second)) && (t.First.RedefinesItem is null) == (t.Second.RedefinesItem is null));
        }
    }

    sealed class Block(string label, List<Stmt> body)
    {
        public string Label { get; } = label;
        public List<Stmt> Body { get; set; } = body;
    }

    sealed class Structurer(SourceProgram sp, List<Entry> ledger)
    {
        readonly List<Paragraph> paras = sp.Model.Paragraphs;

        void Log(int line, string kind, string detail, List<int>? lines = null) => ledger.Add(new Entry(sp.Id, line, kind, detail, lines));

        public void Run()
        {
            SetFalse();
            var index = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < paras.Count; i++) index.TryAdd(paras[i].Name, i);
            int End(int i)
            {
                if (!paras[i].IsSection) return i;
                int j = i;
                while (j + 1 < paras.Count && !paras[j + 1].IsSection && string.Equals(paras[j + 1].Section, paras[i].Name, StringComparison.OrdinalIgnoreCase)) j++;
                return j;
            }
            // the ranges PERFORMed: a section (its paragraphs), a paragraph, a THRU range
            var ranges = new Dictionary<(int From, int To), List<Stmt>>();
            bool unknown = false;
            foreach (var p in paras)
                foreach (var s in Model.Deep(p.Statements).Where(s => s.Verb == "PERFORM" && s.Str("inline") != "true"))
                {
                    if (!index.TryGetValue(s.Str("target") ?? "", out var from)) { unknown = true; continue; }
                    int to = s.Str("thru") is { } thru ? index.TryGetValue(thru, out var t) ? End(t) : -1 : End(from);
                    if (to < from) { unknown = true; continue; }
                    (ranges.TryGetValue((from, to), out var l) ? l : ranges[(from, to)] = []).Add(s);
                }
            if (unknown) return;
            // the entry: from the first paragraph (its section) on, as far as its control falls through
            int entryEnd = End(0);
            List<Stmt>? entry = null;
            while (true)
            {
                entry = Structure(0, entryEnd, true);
                if (entry is not null && !Completes(entry) || entryEnd + 1 >= paras.Count || ranges.Keys.Any(r => r.Item1 == entryEnd + 1)) break;
                entryEnd = End(entryEnd + 1);
            }
            // a paragraph in two ranges (or in the entry and a range) is not one method: those stay as they are
            var owner = new Dictionary<int, (int, int)>();
            var conflicted = new HashSet<(int, int)>();
            foreach (var r in ranges.Keys.Append((0, entryEnd)).Distinct())
                for (int i = r.Item1; i <= r.Item2; i++)
                    if (owner.TryGetValue(i, out var o) && o != r) { conflicted.Add(o); conflicted.Add(r); }
                    else owner[i] = r;
            // a GO TO into a range from outside it: the range is no unit either
            for (int i = 0; i < paras.Count; i++)
                foreach (var s in Model.Deep(paras[i].Statements))
                    foreach (var n in s.L?.GetValueOrDefault("targets") ?? [])
                        if (n.V is { } v && index.TryGetValue(v, out var g) && owner.TryGetValue(g, out var gr) && (!owner.TryGetValue(i, out var ir) || ir != gr))
                            conflicted.Add(gr);
            var done = new Dictionary<(int From, int To), List<Stmt>>();
            if (entry is not null && !Completes(entry) && !conflicted.Contains((0, entryEnd))) done[(0, entryEnd)] = entry;
            foreach (var r in ranges.Keys.Where(r => !conflicted.Contains(r) && r != (0, entryEnd)).OrderBy(r => r.Item1))
                if (Structure(r.From, r.To, false) is { } body) done[r] = body;
            // a range that ends the CALL (GOBACK) performed once from the entry: inlined there, so its GOBACK is the entry's
            var inlined = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var inlinedRanges = new List<(int From, int To, int Site)>();
            if (done.TryGetValue((0, entryEnd), out var entryBody))
                foreach (var (r, body) in done.Where(kv => kv.Key != (0, entryEnd)).ToList())
                    if (Model.Deep(body).Any(s => s.Verb is "GOBACK" or "STOP RUN" or "EXIT PROGRAM") && !Model.Deep(body).Any(s => s.Verb == "EXIT PARAGRAPH")
                        && ranges[r] is [var only] && only.N is null && only.L is null && only.Str("times") is null
                        && Model.Deep(entryBody).FirstOrDefault(s => s.Verb == "PERFORM" && s.StepId == only.StepId && s.Line == only.Line) is { } site
                        && Replace(entryBody, site, body))
                    {
                        done.Remove(r);
                        ranges.Remove(r);
                        inlined.Add(paras[r.From].Name);
                        inlinedRanges.Add((r.Item1, r.Item2, only.Line));
                        Log(only.Line, "inline-goback-range", $"PERFORM {paras[r.From].Name}{(r.To > r.From ? " THRU " + paras[r.To].Name : "")} (its GOBACK ends the CALL) inlined into the entry", LinesOf(body).Append(only.Line).Distinct().Order().ToList());
                    }
            if (done.Count == 0) return;
            // the reachable paragraphs: the entry, then what reachable code PERFORMs or goes to (a structured range: its body)
            var reach = new HashSet<int>();
            var work = new Stack<int>();
            work.Push(0);
            bool entryOk = done.ContainsKey((0, entryEnd));
            while (work.Count > 0)
            {
                var i = work.Pop();
                if (!reach.Add(i)) continue;
                var range = done.Keys.FirstOrDefault(k => k.Item1 <= i && i <= k.Item2, (-1, -1));
                IEnumerable<Stmt> stmts;
                if (range.Item1 >= 0)
                {
                    for (int k = range.Item1; k <= range.Item2; k++) reach.Add(k);
                    stmts = Model.Deep(done[range]);
                }
                else
                {
                    stmts = Model.Deep(paras[i].Statements);
                    // a paragraph's end runs into the next one unless a PERFORM range ends there
                    if (i + 1 < paras.Count && !ranges.Keys.Any(r => r.Item2 == i) && Completes(paras[i].Statements)) work.Push(i + 1);
                }
                foreach (var s in stmts)
                {
                    var targets = new List<int>();
                    if (s.Verb == "PERFORM" && s.Str("inline") != "true" && index.TryGetValue(s.Str("target") ?? "", out var f))
                    {
                        int to = s.Str("thru") is { } th && index.TryGetValue(th, out var t) ? End(t) : End(f);
                        for (int k = f; k <= to; k++) targets.Add(k);
                    }
                    foreach (var n in s.L?.GetValueOrDefault("targets") ?? [])
                        if (n.V is { } v && index.TryGetValue(v, out var g)) targets.Add(g);
                    if (s.Verb == "ALTER") entryOk = false;
                    foreach (var k in targets) work.Push(k);
                }
            }
            // the new paragraph list: a structured range is one paragraph (its first's name); unreachable ones go
            var result = new List<Paragraph>();
            for (int i = 0; i < paras.Count; i++)
            {
                var r = done.Keys.FirstOrDefault(k => k.Item1 == i, (-1, -1));
                if (r.Item1 == i)
                {
                    var p = paras[i];
                    result.Add(new Paragraph { Name = p.Name, Section = p.Section, Line = p.Line, EndLine = paras[r.Item2].EndLine, IsSection = false, Statements = done[r] });
                    if (r.Item2 > i || p.IsSection)
                        Log(p.Line, p.IsSection ? "section" : "thru-range", $"{p.Name}{(r.Item2 > i ? " .. " + paras[r.Item2].Name : "")}: one paragraph ({r.Item2 - i + 1} labels: {string.Join(" ", Enumerable.Range(i, r.Item2 - i + 1).Select(q => paras[q].Name + "@" + paras[q].Line))})",
                            LinesOf(Enumerable.Range(i, r.Item2 - i + 1).SelectMany(q => paras[q].Statements)));
                    i = r.Item2;
                    continue;
                }
                if (inlinedRanges.FirstOrDefault(x => x.From <= i && i <= x.To) is { Site: > 0 } ir && !reach.Contains(i))
                {
                    // not unreachable: its statements run in the entry now, where the PERFORM at the site was
                    Log(paras[i].Line, "inlined-range-source", $"{paras[i].Name}: part of the range PERFORMed once at line {ir.Site}, whose statements the entry holds now (inline-goback-range)",
                        LinesOf(paras[i].Statements));
                    continue;
                }
                if (entryOk && !reach.Contains(i))
                {
                    // the graph evidence: every statement that names the paragraph, and whether its own paragraph is reachable
                    var name = paras[i].Name;
                    var refs = new List<string>();
                    for (int q = 0; q < paras.Count; q++)
                        foreach (var s in Model.Deep(paras[q].Statements))
                            if (Eq(s.Str("target") ?? "", name) || Eq(s.Str("thru") ?? "", name) || (s.L?.GetValueOrDefault("targets") ?? []).Any(n => Eq(n.V ?? "", name))
                                || s.Verb == "ALTER" && Regex.IsMatch(s.Text, @"(^|\s)" + Regex.Escape(name) + @"(\s|$)", RegexOptions.IgnoreCase))
                                refs.Add($"line {s.Line} {s.Verb} in {paras[q].Name} ({(reach.Contains(q) ? "reachable" : "unreachable")}{(s.Verb == "PERFORM" && inlined.Contains(s.Str("target") ?? "") ? "; this PERFORM was replaced by the range's own statements, inline-goback-range" : "")})");
                    string fall = i > 0 ? $"; the paragraph before, {paras[i - 1].Name}, is {(reach.Contains(i - 1) ? "reachable" : "unreachable")}{(reach.Contains(i - 1) ? (ranges.Keys.Any(r => r.Item2 == i - 1) ? " and ends a PERFORM range" : Completes(paras[i - 1].Statements) ? " and COMPLETES" : " and ends with a jump") : "")}" : "";
                    Log(paras[i].Line, "unreachable", $"{name}: no PERFORM, GO TO or fall-through reaches it{(Model.Deep(paras[i].Statements).Any(s => s.Verb == "ALTER") ? " (an ALTER of a switch no path runs)" : "")}; named by: {(refs.Count == 0 ? "no statement" : string.Join("; ", refs))}{fall}",
                        LinesOf(paras[i].Statements));
                    continue;
                }
                result.Add(paras[i]);
            }
            // PERFORM a THRU b of a structured range: PERFORM a
            foreach (var p in result)
                foreach (var s in Model.Deep(p.Statements).Where(s => s.Verb == "PERFORM" && s.Str("thru") is not null))
                    if (index.TryGetValue(s.Str("target")!, out var f) && index.TryGetValue(s.Str("thru")!, out var t) && done.ContainsKey((f, End(t))))
                        s.S!.Remove("thru");
            paras.Clear();
            paras.AddRange(result);
        }

        /// <summary>SET condition TO FALSE: the MOVE of its WHEN SET TO FALSE literal to the conditional variable.</summary>
        void SetFalse()
        {
            foreach (var p in paras) SetFalse(p.Statements);
        }

        void SetFalse(List<Stmt> list)
        {
            for (int k = 0; k < list.Count; k++)
            {
                var s = list[k];
                foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) SetFalse(b);
                if (s.Verb != "SET" || s.Str("mode") != "FALSE" || s.L?.GetValueOrDefault("dst") is not [var d]) continue;
                if (d.K != "c88" || d.Id is not int id || !sp.ItemsById.TryGetValue(id, out var c88) || c88.FalseValue is not { } fv || c88.Parent is not { } parent) continue;
                var lit = fv.Quoted ? new Node { K = "str", V = fv.Text }
                    : new Node { K = fv.Text.StartsWith("ZERO", StringComparison.OrdinalIgnoreCase) || fv.Text.StartsWith("SPACE", StringComparison.OrdinalIgnoreCase) ? "fig" : "num", V = fv.Text };
                list[k] = new Stmt { Verb = "MOVE", StepId = s.StepId, Line = s.Line, Sentence = s.Sentence, Text = s.Text }
                    .Set("src", lit).Set("dst", [new Node { K = "ref", V = parent.Name, Id = parent.Id, Args = d.Args }]);
                Log(s.Line, "set-false", $"SET {c88.Name} TO FALSE: MOVE {(fv.Quoted ? "'" + fv.Text + "'" : fv.Text)} TO {parent.Name}", [s.Line]);
            }
        }

        static bool Replace(List<Stmt> list, Stmt target, List<Stmt> with)
        {
            for (int k = 0; k < list.Count; k++)
            {
                if (ReferenceEquals(list[k], target))
                {
                    list.RemoveAt(k);
                    list.InsertRange(k, with);
                    return true;
                }
                foreach (var b in list[k].Blocks?.Values ?? Enumerable.Empty<List<Stmt>>())
                    if (Replace(b, target, with)) return true;
            }
            return false;
        }

        // ============================================================ one range

        /// <summary>
        /// The structured statements of paragraphs from..to run as one unit (a PERFORM of the range, or the entry), or
        /// null when its GO TOs are not all proven: a jump out of the range, a loop or a jump of no known shape.
        /// </summary>
        List<Stmt>? Structure(int from, int to, bool isEntry)
        {
            var blocks = new List<Block>();
            for (int i = from; i <= to; i++) blocks.Add(new Block(paras[i].Name, Clone(paras[i].Statements)));
            var labels = blocks.Select(b => b.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
            if (!Model.Deep(blocks.SelectMany(b => b.Body)).Any(s => s.Verb is "GO TO" or "ALTER")) return blocks.SelectMany(b => b.Body).Where(s => s.Verb != "EXIT").ToList();
            var log = new List<Entry>();
            foreach (var b in blocks)
            {
                Dispatch(b, log);
                Truncate(b.Body);
            }
            foreach (var s in Model.Deep(blocks.SelectMany(b => b.Body)))
            {
                if (s.Verb == "ALTER") return null;
                if (s.Verb == "GO TO" && (s.L?.GetValueOrDefault("targets") is not [{ V: { } t }] || s.Get("depending") is not null || !labels.Contains(t))) return null;
            }
            for (int guard = 0; guard < 1000; guard++)
            {
                // the range's end: GO TO a label after which nothing runs is the end of the range
                int tail = blocks.Count;
                while (tail > 1 && blocks[tail - 1].Body.All(s => s.Verb is "EXIT" or "CONTINUE")) tail--;
                var ends = blocks.Skip(tail).Select(b => b.Label).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (ends.Count > 0)
                {
                    if (isEntry && blocks.Take(tail).SelectMany(b => Model.Deep(b.Body)).Any(s => Target(s) is { } t && ends.Contains(t))) return null;
                    foreach (var b in blocks.Take(tail)) MapGoTo(b.Body, t => ends.Contains(t) ? ExitRange() : null);
                    blocks.RemoveRange(tail, blocks.Count - tail);
                    continue;
                }
                if (blocks.Count == 1 && !Model.Deep(blocks[0].Body).Any(s => s.Verb == "GO TO")) break;
                if (Step(blocks, log)) continue;
                return null;
            }
            if (blocks.Count != 1 || Model.Deep(blocks[0].Body).Any(s => s.Verb == "GO TO")) return null;
            var body = blocks[0].Body;
            // branches that end the range: what follows them is the other path (no early exits left at the end)
            while (Absorb(body, s => s.Verb == "EXIT PARAGRAPH")) { }
            StripTailExit(body);
            if (isEntry && Model.Deep(body).Any(s => s.Verb == "EXIT PARAGRAPH")) return null;
            ledger.AddRange(log);
            return body;
        }

        /// <summary>One structuring step on the range's labelled blocks; false when none applies.</summary>
        bool Step(List<Block> blocks, List<Entry> log)
        {
            for (int k = 0; k < blocks.Count; k++)
            {
                var b = blocks[k];
                string? next = k + 1 < blocks.Count ? blocks[k + 1].Label : null;
                // GO TO the next label as a block's last statement: its fall-through
                if (b.Body.Count > 0 && Target(b.Body[^1]) is { } lt && next is not null && Eq(lt, next))
                {
                    b.Body.RemoveAt(b.Body.Count - 1);
                    return true;
                }
                // a loop: the block ends with GO TO its own label
                if (b.Body.Count > 0 && Target(b.Body[^1]) is { } self && Eq(self, b.Label) && Loop(b, next, log)) return true;
                // a forward GO TO the next label from a branch: the rest of the block is the other path
                if (next is not null && Absorb(b.Body, s => Target(s) is { } t && Eq(t, next))) return true;
            }
            for (int k = 1; k < blocks.Count; k++)
            {
                var b = blocks[k];
                int refs = blocks.Sum(x => Model.Deep(x.Body).Count(s => Target(s) is { } t && Eq(t, b.Label)));
                bool fallsIn = Completes(blocks[k - 1].Body);
                // a label no GO TO names: the previous block runs into it, or nothing does
                if (refs == 0)
                {
                    if (fallsIn) blocks[k - 1].Body.AddRange(b.Body);
                    blocks.RemoveAt(k);
                    return true;
                }
                // a label one GO TO names and nothing falls into: its block at the GO TO
                if (refs == 1 && !fallsIn && !Model.Deep(b.Body).Any(s => Target(s) is { } t && Eq(t, b.Label)))
                {
                    var body = b.Body;
                    if (Completes(body)) body.Add(k + 1 < blocks.Count ? GoTo(blocks[k + 1].Label, body.LastOrDefault()?.Line ?? 0) : ExitRange());
                    blocks.RemoveAt(k);
                    foreach (var x in blocks)
                        if (MapGoTo(x.Body, t => Eq(t, b.Label) ? body : null)) return true;
                    return false;
                }
            }
            return false;
        }

        /// <summary>
        /// A block that ends with GO TO its own label: PERFORM UNTIL c (the block starts with IF c GO TO the next
        /// label) or PERFORM WITH TEST AFTER UNTIL c (the IF comes right before the jump back). A jump back or out from
        /// anywhere else is not proven (null).
        /// </summary>
        bool Loop(Block b, string? next, List<Entry> log)
        {
            var body = b.Body;
            bool Out(Stmt s) => s.Verb == "EXIT PARAGRAPH" ? next is null : Target(s) is { } t && next is not null && Eq(t, next);
            Node? Exit(Stmt s) => s is { Verb: "IF" } && s.Blocks?.GetValueOrDefault("TRUE") is [var only] && Out(only)
                && (s.Blocks.GetValueOrDefault("FALSE") is null or { Count: 0 }) ? s.Get("cond") : null;
            Node? until = null;
            bool after = false;
            List<Stmt> middle;
            if (body.Count >= 2 && Exit(body[0]) is { } c0) { until = c0; middle = body.GetRange(1, body.Count - 2); }
            else if (body.Count >= 2 && Exit(body[^2]) is { } c1) { until = c1; after = true; middle = body.GetRange(0, body.Count - 2); }
            else return false;
            // no other jump back, out or to the end inside the loop
            if (Model.Deep(middle).Any(s => s.Verb == "GO TO") || Shallow(middle).Any(s => s.Verb == "EXIT PERFORM")) return false;
            var perform = new Stmt { Verb = "PERFORM", StepId = b.Label + ":LOOP", Line = body[after ? ^2 : 0].Line, Text = $"PERFORM {(after ? "WITH TEST AFTER " : "")}UNTIL (GO TO {b.Label})" }
                .Set("inline", "true").Set("until", until!).Block("LOOP", middle);
            if (after) perform.Set("test", "AFTER");
            b.Body = [perform];
            log.Add(new Entry(sp.Id, perform.Line, after ? "loop-test-after" : "loop", $"{b.Label}: GO TO {b.Label} with its exit test: PERFORM {(after ? "WITH TEST AFTER " : "")}UNTIL",
                LinesOf(body).Prepend(perform.Line).Distinct().Order().ToList()));
            return true;
        }

        /// <summary>
        /// An IF or EVALUATE some of whose branches end with GO TO the label after the block (or with the range's end):
        /// those branches skip the statements after it, so the statements go into the one path that runs on to them (the
        /// ELSE, WHEN OTHER, or the branch that completes; a missing ELSE or WHEN OTHER is that path). Statements would be
        /// copied into two paths: not done.
        /// </summary>
        static bool Absorb(List<Stmt> body, Func<Stmt, bool> jump)
        {
            for (int i = 0; i < body.Count; i++)
            {
                var s = body[i];
                if (s.Verb is not ("IF" or "EVALUATE")) continue;
                var paths = Paths(s);
                if (paths is null || !paths.Any(p => p is { Count: > 0 } && jump(p[^1]))) continue;
                var rest = body.GetRange(i + 1, body.Count - i - 1);
                var runs = paths.Where(p => p is null || !(p.Count > 0 && jump(p[^1])) && Completes(p)).ToList();
                if (rest.Count > 0 && runs.Count > 1) continue;
                body.RemoveRange(i + 1, body.Count - i - 1);
                foreach (var p in paths) if (p is { Count: > 0 } && jump(p[^1])) p.RemoveAt(p.Count - 1);
                if (rest.Count > 0 && runs.Count == 1)
                {
                    if (runs[0] is { } run) run.AddRange(rest);
                    else Implicit(s).AddRange(rest);
                }
                Tidy(s);
                return true;
            }
            return false;
        }

        /// <summary>The paths through an IF or EVALUATE: each branch's statements, null for the missing ELSE / WHEN OTHER.</summary>
        static List<List<Stmt>?>? Paths(Stmt s)
        {
            if (s.Verb == "IF")
            {
                s.Blocks ??= [];
                if (!s.Blocks.ContainsKey("TRUE")) s.Blocks["TRUE"] = [];
                return [s.Blocks["TRUE"], s.Blocks.GetValueOrDefault("FALSE")];
            }
            var whens = s.Blocks?.GetValueOrDefault("WHEN");
            if (whens is null) return null;
            var paths = new List<List<Stmt>?>();
            foreach (var w in whens)
            {
                w.Blocks ??= [];
                if (!w.Blocks.ContainsKey("TRUE")) w.Blocks["TRUE"] = [];
                paths.Add(w.Blocks["TRUE"]);
            }
            if (!whens.Any(w => w.Str("other") == "true")) paths.Add(null);
            return paths;
        }

        /// <summary>Creates the missing ELSE / WHEN OTHER and returns its statements.</summary>
        static List<Stmt> Implicit(Stmt s)
        {
            var list = new List<Stmt>();
            if (s.Verb == "IF") s.Blocks!["FALSE"] = list;
            else s.Blocks!["WHEN"].Add(new Stmt { Verb = "WHEN", StepId = s.StepId, Line = s.Line, Text = "WHEN OTHER" }.Set("other", "true").Block("TRUE", list));
            return list;
        }

        /// <summary>IF c (nothing) ELSE f: IF NOT c f; an empty ELSE or WHEN OTHER goes.</summary>
        static void Tidy(Stmt s)
        {
            if (s.Verb == "IF")
            {
                var f = s.Blocks!.GetValueOrDefault("FALSE");
                if (f is { Count: 0 }) s.Blocks.Remove("FALSE");
                else if (f is not null && s.Blocks["TRUE"].Count == 0)
                {
                    s.Set("cond", new Node { K = "not", L = s.Get("cond") });
                    s.Blocks["TRUE"] = f;
                    s.Blocks.Remove("FALSE");
                }
                return;
            }
            s.Blocks!["WHEN"].RemoveAll(w => w.Str("other") == "true" && (w.Blocks?.GetValueOrDefault("TRUE") ?? []).Count == 0);
        }

        // ============================================================ dispatch

        /// <summary>
        /// MOVE 0 TO IX, INSPECT CODES TALLYING IX FOR CHARACTERS BEFORE INITIAL F, GO TO T1 .. Tn DEPENDING ON IX: IX is
        /// the position of F's character in the constant CODES (its length when absent), so the dispatch is EVALUATE F
        /// WHEN the character at position p GO TO Tp, and anything else falls through. The index must be read nowhere
        /// else; CODES must be a constant no statement writes; F one character. GO TO DEPENDING ON a plain index:
        /// EVALUATE IX WHEN p GO TO Tp.
        /// </summary>
        void Dispatch(Block b, List<Entry> log)
        {
            var body = b.Body;
            for (int i = 0; i < body.Count; i++)
            {
                var g = body[i];
                if (g.Verb != "GO TO" || g.Get("depending") is not { K: "ref", Id: int ixId } dep || dep.Args is { Count: > 0 } || dep.Start is not null) continue;
                var targets = (g.L?.GetValueOrDefault("targets") ?? []).Select(n => n.V ?? "").ToList();
                if (targets.Count == 0 || targets.Any(t => t.Length == 0)) continue;
                if (!sp.ItemsById.TryGetValue(ixId, out var ix)) continue;
                var whens = new List<(Node Cond, string Target)>();
                Node? subject = null;
                int start = i;
                if (i >= 2 && body[i - 2] is { Verb: "MOVE" } mv && mv.Get("src") is { K: "num", V: "0" } && mv.L?.GetValueOrDefault("dst") is [{ K: "ref", Id: int mid, Args: null or { Count: 0 }, Start: null }] && mid == ixId
                    && body[i - 1] is { Verb: "INSPECT" } ins && Regex.IsMatch(ins.Text, @"\bFOR\s+CHARACTERS\s+BEFORE\s+INITIAL\s+[A-Z0-9-]+\s*$", RegexOptions.IgnoreCase)
                    && ins.Get("target") is { K: "ref", Id: int cid, Args: null or { Count: 0 }, Start: null }
                    && ins.L?.GetValueOrDefault("clauses") is [{ K: "tally", Op: "CHARACTERS", Args: [{ K: "ref", Id: int tid }], Start: { K: "ref", Id: int fid, Args: null or { Count: 0 }, Start: null } fnode }] && tid == ixId
                    && sp.ItemsById.TryGetValue(cid, out var codes) && sp.ItemsById.TryGetValue(fid, out var fItem)
                    && fItem.IsLeaf && fItem.Length == 1 && fItem.Category != Category.Numeric && codes.IsLeaf && codes.Category != Category.Numeric
                    && codes.Values is [{ Low.Quoted: true } cv] && !Written(cid, ins) && OnlyDispatches(ixId))
                {
                    var text = cv.Low.Text.PadRight(codes.Length)[..codes.Length];
                    // a missing character tallies the whole table: its target would be Tn+1 (fail closed when there is one)
                    if (targets.Count >= text.Length) continue;
                    subject = fnode;
                    var seen = new HashSet<char>();
                    for (int p = 0; p < text.Length; p++)
                    {
                        if (!seen.Add(text[p]) || p == 0 || p > targets.Count) continue;
                        whens.Add((new Node { K = "rel", Op = "eq", L = fnode, R = new Node { K = "str", V = text[p].ToString() } }, targets[p - 1]));
                    }
                    start = i - 2;
                    log.Add(new Entry(sp.Id, g.Line, "dispatch-table", $"GO TO DEPENDING ON {ix.Name} (INSPECT {codes.Name} '{text}' FOR CHARACTERS BEFORE INITIAL {fItem.Name}): EVALUATE {fItem.Name}", [mv.Line, ins.Line, g.Line]));
                }
                else if (ix.Category == Category.Numeric && ix.Scale == 0)
                {
                    subject = dep;
                    for (int p = 1; p <= targets.Count; p++)
                        whens.Add((new Node { K = "rel", Op = "eq", L = dep, R = new Node { K = "num", V = p.ToString(System.Globalization.CultureInfo.InvariantCulture) } }, targets[p - 1]));
                    log.Add(new Entry(sp.Id, g.Line, "dispatch-index", $"GO TO DEPENDING ON {ix.Name}: EVALUATE {ix.Name}", [g.Line]));
                }
                else continue;
                // one WHEN per target, its codes ORed, in the order of the targets
                var ev = new Stmt { Verb = "EVALUATE", StepId = g.StepId, Line = g.Line, Sentence = g.Sentence, Text = "EVALUATE (GO TO DEPENDING ON " + ix.Name + ")" }
                    .Set("subjects", [subject!]);
                var list = new List<Stmt>();
                foreach (var grp in whens.GroupBy(w => w.Target, StringComparer.OrdinalIgnoreCase))
                {
                    var cond = grp.Select(w => w.Cond).Aggregate((a, c) => new Node { K = "or", L = a, R = c });
                    list.Add(new Stmt { Verb = "WHEN", StepId = g.StepId, Line = g.Line, Text = "WHEN" }.Set("cond", cond).Block("TRUE", [GoTo(grp.Key, g.Line)]));
                }
                ev.Block("WHEN", list);
                body.RemoveRange(start, i - start + 1);
                body.Insert(start, ev);
                i = start;
            }
        }

        bool Written(int id, Stmt except) =>
            paras.SelectMany(p => Model.Deep(p.Statements)).Any(s => !Same(s, except) && Refs(s).Any(n => n.Id == id)
                && !(s.Verb == "INSPECT" && s.Get("target")?.Id == id && s.L?.GetValueOrDefault("clauses") is { } cl && cl.All(c => c.K == "tally" && (c.Args ?? []).All(a => a.Id != id))));

        /// <summary>
        /// The index is written and read only by dispatches (MOVE 0 TO IX, INSPECT ... TALLYING IX, GO TO ... DEPENDING
        /// ON IX, in that order, wherever it is used): its value at every GO TO is the dispatch's.
        /// </summary>
        bool OnlyDispatches(int id)
        {
            bool Uses(Stmt s) => Refs(s).Any(n => n.Id == id);
            foreach (var list in paras.Select(p => p.Statements).Concat(paras.SelectMany(p => Model.Deep(p.Statements)).SelectMany(s => s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>())))
                for (int k = 0; k < list.Count; k++)
                {
                    var s = list[k];
                    if (!Uses(s)) continue;
                    bool move = s.Verb == "MOVE" && s.Get("src") is { K: "num", V: "0" } && k + 2 < list.Count && list[k + 1].Verb == "INSPECT" && list[k + 2].Verb == "GO TO";
                    bool inspect = s.Verb == "INSPECT" && s.L?.GetValueOrDefault("clauses") is [{ K: "tally", Op: "CHARACTERS", Args: [{ Id: int ti }] }] && ti == id && s.Get("target")?.Id != id
                        && Regex.IsMatch(s.Text, @"\bFOR\s+CHARACTERS\s+BEFORE\s+INITIAL\s+[A-Z0-9-]+\s*$", RegexOptions.IgnoreCase) && k >= 1 && k + 1 < list.Count && list[k - 1].Verb == "MOVE" && list[k + 1].Verb == "GO TO";
                    bool go = s.Verb == "GO TO" && s.Get("depending") is { Id: int d } && d == id && k >= 2 && list[k - 1].Verb == "INSPECT" && list[k - 2].Verb == "MOVE";
                    if (!move && !inspect && !go) return false;
                }
            return true;
        }

        /// <summary>The index is read and written only by its dispatch.</summary>
        bool OnlyUses(int id, params Stmt[] dispatch) =>
            !paras.SelectMany(p => Model.Deep(p.Statements)).Any(s => !dispatch.Any(d => Same(d, s)) && Refs(s).Any(n => n.Id == id));

        /// <summary>The same source statement (a range's statements are copies of the paragraphs').</summary>
        static bool Same(Stmt a, Stmt b) => a.Verb == b.Verb && a.Line == b.Line && a.StepId == b.StepId;

        internal static IEnumerable<Node> RefsOf(Stmt s) => Refs(s);

        static IEnumerable<Node> Refs(Stmt s)
        {
            foreach (var n in s.N?.Values ?? Enumerable.Empty<Node>()) foreach (var x in Nodes(n)) yield return x;
            foreach (var l in s.L?.Values ?? Enumerable.Empty<List<Node>>()) foreach (var n in l) foreach (var x in Nodes(n)) yield return x;
        }

        static IEnumerable<Node> Nodes(Node n)
        {
            yield return n;
            foreach (var c in new[] { n.L, n.R, n.Start, n.Len }.Concat(n.Args ?? []))
                if (c is not null) foreach (var x in Nodes(c)) yield return x;
        }

        // ============================================================ helpers

        static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        static string? Target(Stmt s) => s.Verb == "GO TO" && s.Get("depending") is null && s.L?.GetValueOrDefault("targets") is [{ V: { } t }] ? t : null;

        static Stmt GoTo(string label, int line) => new Stmt { Verb = "GO TO", Line = line, Text = "GO TO " + label }.Set("targets", [new Node { K = "para", V = label }]);

        static Stmt ExitRange() => new() { Verb = "EXIT PARAGRAPH", Text = "EXIT PARAGRAPH (the end of the range)" };

        /// <summary>Replaces every GO TO the function maps (deep) by its statements; true when one was replaced.</summary>
        static bool MapGoTo(List<Stmt> list, Func<string, object?> map)
        {
            bool any = false;
            for (int k = 0; k < list.Count; k++)
            {
                var s = list[k];
                if (Target(s) is { } t && map(t) is { } r)
                {
                    list.RemoveAt(k);
                    if (r is Stmt one) list.Insert(k, one);
                    else if (r is List<Stmt> many) { list.InsertRange(k, many); k += many.Count - 1; }
                    any = true;
                    continue;
                }
                foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) any |= MapGoTo(b, map);
            }
            return any;
        }

        /// <summary>Whether control can run past the statements' end (not a GO TO, GOBACK or the range's end on every path).</summary>
        public static bool Completes(List<Stmt> list)
        {
            if (list.Count == 0) return true;
            var s = list[^1];
            switch (s.Verb)
            {
                case "GO TO" or "GOBACK" or "STOP RUN" or "EXIT PROGRAM" or "EXIT PARAGRAPH": return false;
                case "IF": return Completes(s.Blocks?.GetValueOrDefault("TRUE") ?? []) || Completes(s.Blocks?.GetValueOrDefault("FALSE") ?? []);
                case "EVALUATE":
                {
                    var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
                    if (!whens.Any(w => w.Str("other") == "true")) return true;
                    return whens.Any(w => Completes(w.Blocks?.GetValueOrDefault("TRUE") ?? []));
                }
                default: return true;
            }
        }

        /// <summary>Statements after an unconditional jump at the top of a list never run.</summary>
        static void Truncate(List<Stmt> list)
        {
            for (int k = 0; k < list.Count; k++)
            {
                foreach (var b in list[k].Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) Truncate(b);
                if (list[k].Verb is "GO TO" or "GOBACK" or "STOP RUN" or "EXIT PROGRAM" or "EXIT PARAGRAPH" && k + 1 < list.Count)
                    list.RemoveRange(k + 1, list.Count - k - 1);
            }
        }

        /// <summary>The range's end as its last statement (or the last of a final IF's or EVALUATE's branch) is implicit.</summary>
        static void StripTailExit(List<Stmt> list)
        {
            if (list.Count == 0) return;
            var s = list[^1];
            if (s.Verb == "EXIT PARAGRAPH") { list.RemoveAt(list.Count - 1); StripTailExit(list); return; }
            if (s.Verb == "IF") foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) StripTailExit(b);
            if (s.Verb == "EVALUATE") foreach (var w in s.Blocks?.GetValueOrDefault("WHEN") ?? []) StripTailExit(w.Blocks?.GetValueOrDefault("TRUE") ?? []);
        }

        /// <summary>The statements, nested ones too, except inside an inline PERFORM (an EXIT PERFORM there is its own).</summary>
        static IEnumerable<Stmt> Shallow(IEnumerable<Stmt> list)
        {
            foreach (var s in list)
            {
                yield return s;
                if (s.Blocks is null || s.Verb == "PERFORM" && s.Str("inline") == "true") continue;
                foreach (var b in s.Blocks.Values) foreach (var c in Shallow(b)) yield return c;
            }
        }

        static List<Stmt> Clone(List<Stmt> list) => list.Select(Clone).ToList();

        static Stmt Clone(Stmt s)
        {
            var c = new Stmt { Verb = s.Verb, StepId = s.StepId, Line = s.Line, Sentence = s.Sentence, Text = s.Text, Frontier = s.Frontier, Tokens = s.Tokens };
            if (s.N is not null) c.N = new Dictionary<string, Node>(s.N);
            if (s.L is not null) c.L = s.L.ToDictionary(kv => kv.Key, kv => kv.Value.ToList());
            if (s.S is not null) c.S = new Dictionary<string, string>(s.S);
            if (s.Blocks is not null) c.Blocks = s.Blocks.ToDictionary(kv => kv.Key, kv => Clone(kv.Value));
            return c;
        }
    }
}
