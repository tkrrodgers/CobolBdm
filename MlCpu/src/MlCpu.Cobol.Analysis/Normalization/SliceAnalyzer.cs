using System.Text.RegularExpressions;

namespace MlCpu.Cobol.Analysis.Normalization;

/// <summary>What a run of statements does: the unit of paragraph splitting.</summary>
public enum SegmentKind { Logic, Io, Console, Control, ControlFlag, ErrorHandling, Decision }

public sealed record SliceStatement(string StepId, int Line, string Verb, string Cobol);

public sealed record Segment(string Id, string Paragraph, SegmentKind Kind, IReadOnlyList<SliceStatement> Statements,
    IReadOnlyList<string> Reads, IReadOnlyList<string> Writes, IReadOnlyList<Segment> Children, string? Condition = null);

public sealed record ParagraphSlices(string Paragraph, int Line, int EndLine, string Role, IReadOnlyList<Segment> Segments);

/// <summary>A field used only as a flag: assigned literals only, tested in conditions, never copied elsewhere.</summary>
public sealed record ControlFlag(string Field, IReadOnlyList<string> Values);

public sealed record LegacyQuirk(string Id, string Kind, string Paragraph, string? StepId, string Description, string Evidence);

public sealed record SliceResult(string ProgramId, IReadOnlyList<ParagraphSlices> Paragraphs, IReadOnlyList<ControlFlag> Flags,
    IReadOnlyList<string> ErrorParagraphs, IReadOnlyList<LegacyQuirk> Quirks);

/// <summary>
/// Paragraph splitting (step 1 of normalization): every paragraph is cut into segments of one kind — business logic,
/// file I/O, console output, control flow, control-flag bookkeeping and error handling — so pure logic can be lifted out of
/// the I/O that surrounds it. Error paths are those that reach the abend paragraph (CALL of CEE3ABD / STOP RUN after an
/// error DISPLAY). Also detects legacy quirks an implementer must decide on.
/// </summary>
public static partial class SliceAnalyzer
{
    static readonly HashSet<string> IoVerbs = new(StringComparer.Ordinal) { "OPEN", "CLOSE", "READ", "WRITE", "REWRITE", "DELETE", "START", "SORT", "MERGE", "RETURN", "RELEASE" };
    static readonly HashSet<string> ControlVerbs = new(StringComparer.Ordinal) { "PERFORM", "GO TO", "GOBACK", "STOP RUN", "EXIT", "EXIT PARAGRAPH", "EXIT PROGRAM", "CONTINUE", "NEXT SENTENCE", "ALTER" };

    public static SliceResult Analyze(CobolProgram p, ProgramGraph g)
    {
        var items = p.Items.ToDictionary(i => i.Id);
        var flags = Flags(p, items);
        var flagNames = flags.Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        var statusFields = StatusFields(p);
        var error = ErrorParagraphs(p, g);
        int seq = 0;
        var result = new List<ParagraphSlices>();
        foreach (var para in p.Paragraphs)
        {
            var segs = Split(para.Name, para.Statements, items, flagNames, statusFields, error, ref seq);
            var role = error.Contains(para.Name) ? "error-handling"
                : segs.All(s => s.Kind is SegmentKind.Io or SegmentKind.ControlFlag or SegmentKind.ErrorHandling or SegmentKind.Control) && segs.Any(s => s.Kind == SegmentKind.Io) ? "io-adapter"
                : para == p.Paragraphs[0] || Walk(para.Statements).Count(s => s.Verb == "PERFORM") >= 3 ? "orchestration"
                : segs.Any(s => s.Kind == SegmentKind.Logic || s.Children.Any(c => c.Kind == SegmentKind.Logic)) && !segs.Any(s => s.Kind == SegmentKind.Io) ? "business-logic"
                : segs.Any(s => s.Kind == SegmentKind.Logic || HasLogic(s)) ? "mixed"
                : segs.All(s => s.Kind is SegmentKind.Console) ? "console" : "orchestration";
            result.Add(new ParagraphSlices(para.Name, para.Line, para.EndLine, role, segs));
        }
        return new SliceResult(p.ProgramId, result, flags, error.Order(StringComparer.Ordinal).ToList(), Quirks(p, items, flags));
    }

    static bool HasLogic(Segment s) => s.Kind == SegmentKind.Logic || s.Children.Any(HasLogic);

    static List<Segment> Split(string para, List<Stmt> stmts, Dictionary<int, DataItem> items, HashSet<string> flags, HashSet<string> status,
        HashSet<string> error, ref int seq)
    {
        var segs = new List<Segment>();
        var run = new List<Stmt>();
        SegmentKind? runKind = null;
        void Flush(ref int s)
        {
            if (run.Count == 0) return;
            var (r, w) = Refs(run, items);
            segs.Add(new Segment($"SEG-{++s:000}", para, runKind!.Value, run.Select(Text).ToList(), r, w, []));
            run.Clear();
        }
        foreach (var st in stmts)
        {
            var kind = Kind(st, items, flags, status, error);
            if (kind == SegmentKind.Decision)
            {
                Flush(ref seq);
                var children = new List<Segment>();
                foreach (var (branch, block) in st.Blocks ?? [])
                {
                    if (block.Any(x => Kind(x, items, flags, status, error) == SegmentKind.ErrorHandling))
                    {
                        var (er, ew) = Refs(block, items);
                        children.Add(new Segment($"SEG-{++seq:000}", para, SegmentKind.ErrorHandling, block.Select(Text).ToList(), er, ew, [], branch));
                        continue;
                    }
                    var inner = Split(para, block, items, flags, status, error, ref seq);
                    children.AddRange(inner.Select(c => c with { Condition = c.Condition ?? branch }));
                }
                var (r, w) = Refs([st], items);
                segs.Add(new Segment($"SEG-{++seq:000}", para, SegmentKind.Decision, [Head(st)], r, w, children, ConditionText(st)));
                continue;
            }
            if (runKind != kind) { Flush(ref seq); runKind = kind; }
            run.Add(st);
        }
        Flush(ref seq);
        return segs;
    }

    /// <summary>Uniform blocks collapse to their kind; a block mixing kinds is a Decision with child segments.</summary>
    static SegmentKind Kind(Stmt s, Dictionary<int, DataItem> items, HashSet<string> flags, HashSet<string> status, HashSet<string> error)
    {
        if (s.Blocks is { Count: > 0 } && s.Verb is "IF" or "EVALUATE" or "PERFORM" or "SEARCH")
        {
            if (s.Verb == "PERFORM") return SegmentKind.Decision;
            var branchKinds = s.Blocks.Values.Select(b => b.Any(x => Kind(x, items, flags, status, error) == SegmentKind.ErrorHandling)
                ? SegmentKind.ErrorHandling
                : b.Select(x => Kind(x, items, flags, status, error)).Distinct().ToList() is { Count: 1 } one ? one[0] : SegmentKind.Decision).Distinct().ToList();
            if (branchKinds.Contains(SegmentKind.ErrorHandling) && branchKinds.All(k => k is SegmentKind.ErrorHandling or SegmentKind.ControlFlag or SegmentKind.Control))
                return SegmentKind.ErrorHandling;
            var kinds = s.Blocks.Values.SelectMany(b => b).Select(x => Kind(x, items, flags, status, error)).Distinct().ToList();
            var (reads, _) = Refs([s], items);
            bool flagTest = reads.Count > 0 && reads.All(r => flags.Contains(r) || status.Contains(r));
            if (kinds.Count == 1 && kinds[0] != SegmentKind.Decision && (kinds[0] != SegmentKind.Logic || !flagTest)) return kinds[0];
            if (kinds.All(k => k is SegmentKind.ErrorHandling or SegmentKind.ControlFlag or SegmentKind.Control) && kinds.Contains(SegmentKind.ErrorHandling)) return SegmentKind.ErrorHandling;
            if (flagTest && kinds.All(k => k is SegmentKind.ControlFlag or SegmentKind.Control or SegmentKind.ErrorHandling)) return SegmentKind.ControlFlag;
            return SegmentKind.Decision;
        }
        if (IoVerbs.Contains(s.Verb)) return s.Blocks is { Count: > 0 } ? SegmentKind.Decision : SegmentKind.Io;
        if (s.Verb == "DISPLAY") return SegmentKind.Console;
        if (s.Verb == "CALL") return s.Get("target")?.V is "CEE3ABD" or "ILBOABN0" ? SegmentKind.ErrorHandling : SegmentKind.Control;
        if (s.Verb == "PERFORM" && s.Str("target") is { } t && error.Contains(t)) return SegmentKind.ErrorHandling;
        if (ControlVerbs.Contains(s.Verb)) return SegmentKind.Control;
        var (_, writes) = Refs([s], items);
        if (writes.Count > 0 && writes.All(w => flags.Contains(w) || status.Contains(w))) return SegmentKind.ControlFlag;
        return SegmentKind.Logic;
    }

    static string? ConditionText(Stmt s)
    {
        var line = s.Text.Split('\n')[0].Trim();
        return line.Length > 160 ? line[..160] : line;
    }

    static SliceStatement Text(Stmt s) => s.Blocks is { Count: > 0 } ? Head(s) : new(s.StepId, s.Line, s.Verb, Regex.Replace(s.Text, @"\s+", " ").Trim());

    static SliceStatement Head(Stmt s) => new(s.StepId, s.Line, s.Verb, ConditionText(s) ?? s.Verb);

    /// <summary>Data names read and written by statements (88-level conditions resolve to their parent).</summary>
    public static (List<string> Reads, List<string> Writes) Refs(IEnumerable<Stmt> stmts, Dictionary<int, DataItem> items)
    {
        var reads = new SortedSet<string>(StringComparer.Ordinal);
        var writes = new SortedSet<string>(StringComparer.Ordinal);
        string? Name(Node? n) => n is { K: "ref" or "c88", Id: { } id } && items.TryGetValue(id, out var it)
            ? (it.Level == 88 && it.Parent is not null ? it.Parent.Name : it.Name) : null;
        void Read(Node? n)
        {
            if (n is null) return;
            if (Name(n) is { } nm) reads.Add(nm);
            Read(n.L); Read(n.R); Read(n.Start); Read(n.Len);
            foreach (var a in n.Args ?? []) Read(a);
        }
        void Visit(Stmt s)
        {
            var targets = new List<Node>();
            IEnumerable<Node> L(string k) => s.L?.GetValueOrDefault(k) ?? [];
            switch (s.Verb)
            {
                case "MOVE" or "COMPUTE" or "INITIALIZE" or "SET": targets.AddRange(L("dst")); break;
                case "ADD" or "SUBTRACT" or "MULTIPLY" or "DIVIDE":
                    targets.AddRange(L("dst")); targets.AddRange(L("giving"));
                    if (s.Get("rem") is { } rem) targets.Add(rem);
                    foreach (var d in L("dst")) Read(d);
                    break;
                case "ACCEPT": if (s.Get("dst") is { } a) targets.Add(a); break;
                case "STRING": if (s.Get("into") is { } i) targets.Add(i); break;
                case "UNSTRING": targets.AddRange(L("targets").Select(t => t.L).OfType<Node>()); break;
                case "READ": if (s.Get("into") is { } r) targets.Add(r); break;
                case "WRITE" or "REWRITE": if (s.Get("record") is { } w) targets.Add(w); break;
            }
            foreach (var t in targets) if (Name(t) is { } nm) writes.Add(nm);
            foreach (var (k, n) in s.N ?? []) if (!targets.Contains(n)) Read(n);
            foreach (var (k, list) in s.L ?? [])
                foreach (var n in list) if (!targets.Contains(n)) Read(n);
            foreach (var b in s.Blocks?.Values ?? Enumerable.Empty<List<Stmt>>()) foreach (var x in b) Visit(x);
        }
        foreach (var s in stmts) Visit(s);
        return (reads.ToList(), writes.ToList());
    }

    static IEnumerable<Stmt> Walk(IEnumerable<Stmt> list)
    {
        foreach (var s in list)
        {
            yield return s;
            if (s.Blocks is not null) foreach (var b in s.Blocks.Values) foreach (var x in Walk(b)) yield return x;
        }
    }

    static HashSet<string> StatusFields(CobolProgram p)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        foreach (var f in p.Files.Where(f => f.FileStatus is not null))
            foreach (var it in p.Items.Where(i => i.Name == f.FileStatus))
            {
                set.Add(it.Name);
                foreach (var c in it.Children) set.Add(c.Name);
            }
        return set;
    }

    /// <summary>Fields assigned only literals (or figuratives), and only ever read in conditions.</summary>
    static List<ControlFlag> Flags(CobolProgram p, Dictionary<int, DataItem> items)
    {
        var all = Walk(p.Paragraphs.SelectMany(x => x.Statements)).ToList();
        var literal = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var disqualified = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in all)
        {
            var (reads, writes) = Refs([s], items);
            if (s.Verb is "MOVE" && s.Get("src") is { K: "str" or "num" or "fig" } src)
                foreach (var w in writes) (literal.TryGetValue(w, out var v) ? v : literal[w] = new SortedSet<string>(StringComparer.Ordinal)).Add(src.V ?? "");
            else if (s.Verb is "SET" && s.Str("mode") == "TRUE")
                foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                    if (d is { Id: { } id } && items.TryGetValue(id, out var c88) && c88.Parent is { } parent)
                        (literal.TryGetValue(parent.Name, out var v) ? v : literal[parent.Name] = new SortedSet<string>(StringComparer.Ordinal)).Add(c88.Values.FirstOrDefault().Low?.Text ?? "?");
            else foreach (var w in writes) disqualified.Add(w);
            if (s.Verb is not ("IF" or "EVALUATE" or "PERFORM" or "WHEN")) foreach (var r in reads) if (!writes.Contains(r)) disqualified.Add(r);
        }
        // A field reached through a group (MOVE group, WRITE FROM group, DISPLAY group) carries data, not control.
        foreach (var name in disqualified.ToList())
            foreach (var it in p.Items.Where(i => i.Name == name))
                foreach (var d in Descendants(it)) disqualified.Add(d.Name);
        foreach (var reg in new[] { "RETURN-CODE", "SORT-RETURN", "TALLY" }) disqualified.Add(reg);
        return literal.Where(kv => !disqualified.Contains(kv.Key))
            .Select(kv =>
            {
                var it = p.Items.FirstOrDefault(i => i.Name == kv.Key);
                var values = new SortedSet<string>(kv.Value, StringComparer.Ordinal);
                if (it?.Values.Count > 0) values.Add(it.Values[0].Low.Text);
                return new ControlFlag(kv.Key, values.ToList());
            }).OrderBy(f => f.Field, StringComparer.Ordinal).ToList();
    }

    /// <summary>Paragraphs that abend (CALL CEE3ABD / ILBOABN0) and paragraphs that only report I/O status for them.</summary>
    static HashSet<string> ErrorParagraphs(CobolProgram p, ProgramGraph g)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var para in p.Paragraphs)
            if (Walk(para.Statements).Any(s => s.Verb == "CALL" && s.Get("target")?.V is "CEE3ABD" or "ILBOABN0"))
                set.Add(para.Name);
        foreach (var para in p.Paragraphs.Where(x => x.Name.Contains("IO-STATUS", StringComparison.OrdinalIgnoreCase) || x.Name.Contains("ABEND", StringComparison.OrdinalIgnoreCase)))
            set.Add(para.Name);
        return set;
    }

    /// <summary>Behaviours a faithful port must keep (for byte parity) and a modern design would question.</summary>
    static List<LegacyQuirk> Quirks(CobolProgram p, Dictionary<int, DataItem> items, List<ControlFlag> flags)
    {
        var quirks = new List<LegacyQuirk>();
        int n = 0;
        string Id() => $"Q-{++n:00}";
        var flagValues = flags.ToDictionary(f => f.Field, f => f.Values.ToHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
        var intoRecords = Walk(p.Paragraphs.SelectMany(x => x.Statements)).Where(s => s.Verb == "READ" && s.Get("into") is not null)
            .Select(s => items.TryGetValue(s.Get("into")!.Id ?? -1, out var r) ? r : null).OfType<DataItem>().ToList();
        var eofFlags = flags.Where(f => f.Values.Contains("Y") && f.Values.Contains("N") && f.Field.Contains("EOF", StringComparison.OrdinalIgnoreCase) || f.Field.Contains("END-OF-FILE", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Field).ToHashSet(StringComparer.Ordinal);
        foreach (var para in p.Paragraphs)
        {
            foreach (var s in Walk(para.Statements))
            {
                // PERFORM UNTIL F = a { IF F = b ... ELSE x }: the ELSE runs only for a value outside the flag's domain.
                if (s.Verb == "PERFORM" && s.Get("until") is { K: "rel", Op: "eq" } u && Flag(u.L) is { } f && Literal(u.R) is { } stop
                    && flagValues.TryGetValue(f, out var domain) && s.Blocks?.GetValueOrDefault("LOOP") is { } body)
                    foreach (var iff in body.Where(b => b.Verb == "IF" && b.Blocks?.ContainsKey("FALSE") == true))
                        if (iff.Get("cond") is { K: "rel", Op: "eq" } c && Flag(c.L) == f && Literal(c.R) is { } go && domain.All(v => v == stop || v == go))
                            quirks.Add(new(Id(), "unreachable-branch", para.Name, iff.StepId,
                                $"The ELSE of '{Line(iff)}' can never run: inside 'PERFORM UNTIL {f} = {stop}' the flag only takes the values {string.Join(", ", domain)}. " +
                                $"Statements there ({string.Join("; ", iff.Blocks!["FALSE"].Select(Line))}) are dead code.",
                                $"{p.ProgramId}.cbl:{iff.Line}"));
                // PERFORM VARYING I ... WITH TEST AFTER UNTIL I > N: the body also runs with I = N + 1, so a table of N entries
                // subscripted by I is written one entry past its end (no subscript check): the bytes after the table change.
                if (s.Verb == "PERFORM" && s.Str("test") == "AFTER" && s.Get("var") is { Id: { } varId } loopVar && s.Get("until") is { K: "rel" } tu
                    && tu.L?.Id == varId && tu.R?.K == "num")
                {
                    var loopBody = s.Blocks?.GetValueOrDefault("LOOP") ?? [];
                    var tables = Walk(loopBody).SelectMany(Nodes).Where(nd => nd.Args?.Any(a => a.Id == varId) == true && nd.Id is { } tid && items.ContainsKey(tid))
                        .Select(nd => items[nd.Id!.Value]).DistinctBy(t => t.Id).ToList();
                    var bound = tu.R.V;
                    var detail = tables.Count == 0 ? "" : " Subscripted in the body: " + string.Join("; ", tables.Select(t =>
                    {
                        var table = t.Occurs > 0 ? t : t.Parent is { Occurs: > 0 } pt ? pt : t;
                        return $"{t.Name} (OCCURS {table.Occurs}, {table.Length} bytes per entry): pass {loopVar.V} = {(int.TryParse(bound, out var b) ? (b + (tu.Op == "gt" ? 1 : 0)).ToString() : bound + "+1")} " +
                               $"addresses the {table.Length} bytes right after entry {table.Occurs}, i.e. the next field(s) of the record";
                    })) + ".";
                    quirks.Add(new(Id(), "test-after-overrun", para.Name, s.StepId,
                        $"'{Line(s)}' tests its condition after each pass and before the increment, so the body runs once more with {loopVar.V} " +
                        $"past the bound ({loopVar.V} = {bound}{(tu.Op == "gt" ? " + 1" : "")} on the last pass).{detail} The overwritten bytes are part of the output.",
                        $"{p.ProgramId}.cbl:{s.Line}"));
                }
                if (s.Verb == "NEXT SENTENCE")
                    quirks.Add(new(Id(), "next-sentence", para.Name, s.StepId,
                        "NEXT SENTENCE transfers control past the next period, not to the end of the enclosing IF; inside an inline PERFORM it leaves the loop.",
                        $"{p.ProgramId}.cbl:{s.Line}"));
                // IF EOF-FLAG = 'N' ... ELSE <uses fields of the record read INTO>: the area still holds the last record read.
                if (s.Verb == "IF" && s.Get("cond") is { K: "rel", Op: "eq" } ec && Flag(ec.L) is { } ef && eofFlags.Contains(ef) && Literal(ec.R) == "N"
                    && s.Blocks?.GetValueOrDefault("FALSE") is { } atEnd)
                {
                    var (reads, _) = Refs(atEnd, items);
                    var stale = reads.Where(r => intoRecords.Any(rec => rec.Name == r || Descendants(rec).Any(d => d.Name == r))).ToList();
                    if (stale.Count > 0)
                        quirks.Add(new(Id(), "record-used-after-end-of-file", para.Name, s.StepId,
                            $"After end of file the ELSE of '{Line(s)}' uses {string.Join(", ", stale)} from the record area, which still holds the last record read " +
                            $"({string.Join("; ", atEnd.Select(Line))}).",
                            $"{p.ProgramId}.cbl:{s.Line}"));
                }
                if (s.Verb == "READ" && s.Get("into") is not null && s.Blocks?.ContainsKey("INVALID") == true)
                    quirks.Add(new(Id(), "stale-record-on-invalid-key", para.Name, s.StepId,
                        $"On INVALID KEY the INTO record ({Name(s.Get("into"), items)}) keeps the previously read record's values.",
                        $"{p.ProgramId}.cbl:{s.Line}"));
                if (s.Verb == "STRING" && s.Get("into") is { } into && Name(into, items) is { } target)
                {
                    var cleared = Walk(p.Paragraphs.SelectMany(x => x.Statements)).Any(x => x.Verb is "MOVE" or "INITIALIZE" && (x.L?.GetValueOrDefault("dst") ?? []).Any(d => Name(d, items) == target || Encloses(d, target, items)));
                    if (!cleared)
                        quirks.Add(new(Id(), "string-without-padding", para.Name, s.StepId,
                            $"STRING does not pad {target}; bytes beyond the text keep their prior content (initially the storage default, x'00').",
                            $"{p.ProgramId}.cbl:{s.Line}"));
                }
            }
            var conds = Walk(para.Statements).Where(s => s.Verb == "IF").ToList();
            var assigns = Walk(para.Statements).Where(s => s.Verb == "MOVE" && s.Get("src")?.K == "num").GroupBy(s => string.Join(",", (s.L?.GetValueOrDefault("dst") ?? []).Select(d => Name(d, items))));
            foreach (var g in assigns.Where(g => g.Count() > 1 && conds.Count(c => c.Blocks?.Values.Any(b => b.Any(x => g.Contains(x))) == true) > 1))
                if (conds.Where(c => c.Blocks?.Values.Any(b => b.Any(x => g.Contains(x))) == true).Select(c => c.StepId).Distinct().Count() > 1
                    && !g.Key.Contains("APPL-RESULT") && !flagValues.ContainsKey(g.Key))
                    quirks.Add(new(Id(), "last-assignment-wins", para.Name, g.First().StepId,
                        $"{g.Key} is set by {g.Count()} independent IFs ({string.Join(", ", g.Select(x => x.Get("src")!.V))}); when several fire, the last one wins.",
                        $"{p.ProgramId}.cbl:{g.First().Line}"));
        }
        // A CALLed program's USING arguments are the caller's storage (BY REFERENCE): whatever it writes there is what the caller
        // sees after the CALL, including changes to an argument the caller treats as input.
        if (p.Using.Count > 0)
        {
            var (_, writes) = Refs(Walk(p.Paragraphs.SelectMany(x => x.Statements)), items);
            var written = p.Using
                .Select(u => p.Items.FirstOrDefault(i => i.Name.Equals(u, StringComparison.OrdinalIgnoreCase) && i.Section == DataSection.Linkage && i.Level is 1 or 77))
                .OfType<DataItem>()
                .Select(arg => (Arg: arg, Fields: writes.Where(w => w == arg.Name || Descendants(arg).Any(d => d.Name == w)).ToList()))
                .Where(x => x.Fields.Count > 0).ToList();
            if (written.Count > 0)
                quirks.Add(new(Id(), "by-reference-writes", p.Paragraphs.FirstOrDefault()?.Name ?? "", null,
                    "The caller passes its own storage BY REFERENCE: every byte this program writes into a USING argument is what the caller sees " +
                    "after the CALL. Written here: " + string.Join("; ", written.Select(x => $"{x.Arg.Name} (argument {p.Using.FindIndex(u => u.Equals(x.Arg.Name, StringComparison.OrdinalIgnoreCase)) + 1}): {string.Join(", ", x.Fields)}")) +
                    ". Fields changed in an argument the caller passes as input (for example its input record) stay changed in the caller's record, " +
                    "and the caller may write that record out afterwards.",
                    $"{p.ProgramId}.cbl:{p.Paragraphs.FirstOrDefault()?.Line ?? 1}"));
        }
        return quirks;
    }

    static string? Flag(Node? n) => n is { K: "ref", V: { } v } ? v : null;

    static IEnumerable<DataItem> Descendants(DataItem it) => it.Children.SelectMany(c => new[] { c }.Concat(Descendants(c)));
    static string? Literal(Node? n) => n is { K: "str" or "num", V: { } v } ? v : null;
    static string Line(Stmt s) => Regex.Replace(s.Text.Split('\n')[0], @"\s+", " ").Trim();

    /// <summary>Every operand node of a statement (subscripts, reference modifiers and expression parts included).</summary>
    static IEnumerable<Node> Nodes(Stmt s)
    {
        var stack = new Stack<Node>((s.N?.Values ?? Enumerable.Empty<Node>()).Concat(s.L?.Values.SelectMany(l => l) ?? Enumerable.Empty<Node>()));
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            foreach (var c in new[] { n.L, n.R, n.Start, n.Len }.OfType<Node>()) stack.Push(c);
            foreach (var a in n.Args ?? []) stack.Push(a);
        }
    }
    static string? Name(Node? n, Dictionary<int, DataItem> items) => n is { Id: { } id } && items.TryGetValue(id, out var it) ? it.Name : null;

    static bool Encloses(Node d, string target, Dictionary<int, DataItem> items)
    {
        if (d is not { Id: { } id } || !items.TryGetValue(id, out var g)) return false;
        var t = items.Values.FirstOrDefault(i => i.Name == target);
        for (var x = t?.Parent; x is not null; x = x.Parent) if (x == g) return true;
        return false;
    }
}
