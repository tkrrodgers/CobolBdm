using System.Globalization;
using System.Text;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>
/// A decimal library the estate's programs CALL (P1, the kernel-replacement method): a subprogram whose one LINKAGE
/// record is an operation code, three decimal values (an unscaled integer, its scale and an extension), an integer
/// parameter (a precision or a scale), a rounding mode number, an integer result and a return code. The operations are
/// the codes the callers move into the record (every write of the code a literal), each classified by the paragraph its
/// dispatch PERFORMs (its name states the operation: ADD, SUBTRACT, MULTIPLY, DIVIDE MC or PRECISION, DIVIDE SCALE, SET
/// SCALE, PRECISION, COMPARE), or as the store's new generation (the branch that counts the generation and empties the
/// store); one unclassified reached code, a caller that reads an operand after the CALL, or a library that CALLs a
/// program that is no kernel leaves the lifted library in place. The capacity of the library (its store of values past the
/// unscaled digits, its registers, its scale and generation fields) is read from its WORKING-STORAGE declarations and kept
/// as declared guards (return code 20). The JDK class is BigDecimal behind the record interface; the lifted library is the
/// differential gate's reference (test lane).
/// </summary>
static class DecimalKernels
{
    public const string Kind = "decimal";

    static readonly Dictionary<string, string> ByWords = new(StringComparer.Ordinal)
    {
        ["ADD"] = "add",
        ["SUBTRACT"] = "subtract",
        ["MULTIPLY"] = "multiply",
        ["DIVIDE MC"] = "divideToPrecision",
        ["DIVIDE PRECISION"] = "divideToPrecision",
        ["DIVIDE SCALE"] = "divideToScale",
        ["SET SCALE"] = "setScale",
        ["PRECISION"] = "precision",
        ["COMPARE"] = "compare",
    };

    static bool Bin(DataItem it) => it is { IsLeaf: true, Category: Category.Numeric, Usage: Usage.Binary or Usage.Comp5, Scale: 0, Signed: true, Occurs: 0 };

    /// <summary>A decimal value group: an unscaled signed integer of 18 digits or more, a binary scale, a binary extension.</summary>
    static (DataItem U, DataItem S, DataItem Z)? ValueGroup(DataItem g)
    {
        if (g.IsLeaf || g.Occurs > 0 || g.RedefinesItem is not null) return null;
        var f = g.Children.Where(c => c.Level != 88 && !c.IsFiller).ToList();
        if (f.Count != 3 || f.Count != g.Children.Count(c => c.Level != 88)) return null;
        if (f[0] is not { IsLeaf: true, Category: Category.Numeric, Scale: 0, Signed: true, Occurs: 0 } u || u.Digits < 18) return null;
        if (!Bin(f[1]) || f[1].Digits < 9 || !Bin(f[2])) return null;
        return (f[0], f[1], f[2]);
    }

    static string? Words(string paragraph)
    {
        var w = paragraph.ToUpperInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries).Where(x => !x.All(char.IsDigit));
        return ByWords.GetValueOrDefault(string.Join(' ', w));
    }

    static IEnumerable<Stmt> Deep(IEnumerable<Stmt> s) => Model.Deep(s.ToList());

    static DataItem? ItemOf(SourceProgram p, Node? n) => n is { K: "ref" } ? p.ItemsById.GetValueOrDefault(n.Id ?? -1) : null;

    /// <summary>The literals of a WHEN's condition on the subject (WHEN 'AD' WHEN 'SB': an or).</summary>
    static bool Codes(Node? c, DataItem subject, SourceProgram p, List<string> codes)
    {
        if (c is null) return false;
        if (c.K == "or") return Codes(c.L, subject, p, codes) && Codes(c.R, subject, p, codes);
        if (c is { K: "rel", Op: "eq", L: { K: "ref" } l, R: { K: "str" } r } && ItemOf(p, l) == subject) { codes.Add(r.V ?? ""); return true; }
        return false;
    }

    static KernelContract? No(int at) { if (Environment.GetEnvironmentVariable("MLCPU_DEBUG_DECIMAL") is not null) Console.Error.WriteLine("decimal kernel: not recognized at check " + at); return null; }

    public static KernelContract? Recognize(AppModel a, AppProgram ap)
    {
        if (!ap.IsSubprogram || ap.Kernel is not null || ap.LinkageOrder.Count != 1 || ap.Classic.Count < 2) return No(1);
        var P = ap.P;
        var rec = ap.LinkageOrder[0];
        var kids = rec.Children.Where(c => c.Level != 88 && !c.IsFiller && c.RedefinesItem is null).ToList();
        if (kids.Count < 8 || kids[0] is not { IsLeaf: true, Occurs: 0 } code || code.Category == Category.Numeric || code.Length > 4) return No(2);
        var groups = new List<(DataItem G, DataItem U, DataItem S, DataItem Z)>();
        int i = 1;
        for (; i < kids.Count && ValueGroup(kids[i]) is { } vg; i++) groups.Add((kids[i], vg.U, vg.S, vg.Z));
        if (groups.Count != 3) return No(3);
        var rest = kids.Skip(i).ToList();
        if (rest.Count < 4 || rest.Any(x => !x.IsLeaf || x.Occurs > 0)) return No(4);
        DataItem parm = rest[0], mode = rest[1], n = rest[2], rc = rest[^1];
        if (!Bin(parm) || parm.Digits < 9 || !Bin(mode) || !Bin(n) || n.Digits < 9 || !Bin(rc)) return No(5);
        // the library calls only kernels the JDK replaces (the JDK class calls nothing)
        foreach (var s in Deep(ap.Classic.SelectMany(x => x.Statements)).Where(s => s.Verb == "CALL"))
            if (s.Get("target") is not { K: "str", V: { } t } || !a.ById.TryGetValue(t, out var callee) || callee.Kernel is null) return No(6);

        // the dispatch: the entry's EVALUATEs of the code; a WHEN that PERFORMs one paragraph is that paragraph's operation
        var entry = ap.Classic[0];
        var ops = new Dictionary<string, string>(StringComparer.Ordinal);
        DataItem? generation = null, used = null, mark = null;
        // the statements of each WHEN branch of the dispatch, with the branch's codes
        var branchCodes = new Dictionary<Stmt, List<string>>(ReferenceEqualityComparer.Instance);
        foreach (var ev in Deep(entry.Statements).Where(s => s.Verb == "EVALUATE" && s.L?.GetValueOrDefault("subjects") is [{ K: "ref" } sj] && ItemOf(P, sj) == code))
            foreach (var w in ev.Blocks?.GetValueOrDefault("WHEN") ?? [])
            {
                var codes = new List<string>();
                if (w.Str("other") == "true" || !Codes(w.Get("cond"), code, P, codes)) continue;
                var body = w.Blocks?.GetValueOrDefault("TRUE") ?? [];
                foreach (var st in Deep(body)) (branchCodes.TryGetValue(st, out var bc) ? bc : branchCodes[st] = []).AddRange(codes);
                if (body is [{ Verb: "PERFORM" } pf] && pf.Str("inline") != "true" && pf.Str("thru") is null && pf.Str("target") is { } target && Words(target) is { } op)
                    foreach (var c in codes) ops.TryAdd(c, op);
                // the store's new generation: ADD 1 TO the generation (ON SIZE ERROR it starts at 1), the mark into the count, GOBACK
                if (codes.Count == 1 && body is [{ Verb: "ADD" } add, { Verb: "MOVE" } mv, { Verb: "GOBACK" }]
                    && add.L?.GetValueOrDefault("ops") is [{ K: "num", V: "1" }] && add.L?.GetValueOrDefault("dst") is [var gd] && ItemOf(P, gd) is { } g && Bin(g)
                    && mv.Get("src") is { } ms && ItemOf(P, ms) is { } mk && Bin(mk) && Model.DstName(mv) is not null && mv.L?.GetValueOrDefault("dst") is [var ud] && ItemOf(P, ud) is { } u && Bin(u))
                {
                    generation = g; used = u; mark = mk;
                    ops.TryAdd(codes[0], "generation");
                }
            }
        if (generation is null || used is null || mark is null) return No(7);
        // the mark is the count's start: never set by a reached code (its VALUE, 0)
        if (mark.Values.Count != 1 || mark.Values[0].Low.Text != "0") return No(8);

        // the codes the callers reach: every write of the caller's code field is a literal, and no caller reads an operand
        var reached = new SortedSet<string>(StringComparer.Ordinal);
        var callers = a.Programs.Where(c => c != ap && c.Classic.SelectMany(x => Deep(x.Statements)).Any(s => s.Verb == "CALL" && s.Get("target") is { V: var t } && string.Equals(t, P.Id, StringComparison.OrdinalIgnoreCase))).ToList();
        if (callers.Count == 0) return No(9);
        foreach (var c in callers)
            if (!CallerCodes(c, P.Id, rec, code, groups[0].G, groups[1].G, reached)) return No(10);
        if (reached.Count == 0 || reached.Any(c => !ops.ContainsKey(c))) return No(11);
        // the gate's cases cover the nine operations, each reached by one code
        if (Gated.Any(g => reached.Count(c => ops[c] == g) != 1) || reached.Count != Gated.Length) return No(12);
        // the mark is set only in branches of codes no caller reaches: the count starts at the mark's VALUE
        foreach (var s in Deep(P.Model.Paragraphs.SelectMany(x => x.Statements)))
            if ((s.L?.GetValueOrDefault("dst") ?? []).Concat(s.L?.GetValueOrDefault("giving") ?? []).Any(d => ItemOf(P, d) == mark)
                && !(branchCodes.TryGetValue(s, out var bcs) && bcs.All(c => !reached.Contains(c)))) return No(13);

        // the capacity, from the declarations: the store (OCCURS entries of OCCURS limbs), the registers' limbs, the handle
        var limits = Capacity(ap, generation, groups[0].U, groups[0].S);
        if (limits is null || ReturnCodes(ap, rc) is not { } codes2) return No(14);
        limits["CAPACITY_CODE"] = codes2.Capacity;
        limits["ARITHMETIC_CODE"] = codes2.Arithmetic;
        var all = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var ev in Deep(entry.Statements).Where(s => s.Verb == "EVALUATE" && s.L?.GetValueOrDefault("subjects") is [{ K: "ref" } sj] && ItemOf(P, sj) == code))
            foreach (var w in ev.Blocks?.GetValueOrDefault("WHEN") ?? [])
            {
                var cs = new List<string>();
                if (w.Str("other") != "true" && Codes(w.Get("cond"), code, P, cs)) foreach (var c in cs) all.Add(c);
            }
        var roles = new Dictionary<string, DataItem>
        {
            ["op"] = code, ["parm"] = parm, ["mode"] = mode, ["n"] = n, ["rc"] = rc,
            ["a"] = groups[0].G, ["aU"] = groups[0].U, ["aS"] = groups[0].S, ["aZ"] = groups[0].Z,
            ["b"] = groups[1].G, ["bU"] = groups[1].U, ["bS"] = groups[1].S, ["bZ"] = groups[1].Z,
            ["r"] = groups[2].G, ["rU"] = groups[2].U, ["rS"] = groups[2].S, ["rZ"] = groups[2].Z,
        };
        return new KernelContract
        {
            Kind = Kind, ClassName = "DecimalKernel", Parms = rec, Roles = roles,
            Operations = reached.ToDictionary(c => c, c => ops[c], StringComparer.Ordinal),
            Unreached = all.Where(c => !reached.Contains(c)).ToList(),
            Limits = limits,
        };
    }

    /// <summary>
    /// The return codes: the capacity's, which the paragraph its ON SIZE ERRORs PERFORM moves into the return code, and
    /// the arithmetic one, the only other non-zero code the library moves there.
    /// </summary>
    static (long Capacity, long Arithmetic)? ReturnCodes(AppProgram ap, DataItem rc)
    {
        var P = ap.P;
        var all = Deep(P.Model.Paragraphs.SelectMany(x => x.Statements)).ToList();
        var onSize = all.SelectMany(s => s.Blocks?.GetValueOrDefault("SIZE-ERROR") ?? []).Where(s => s.Verb == "PERFORM" && s.Str("inline") != "true").Select(s => s.Str("target")).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        long? Moved(Stmt s) => s.Verb == "MOVE" && s.L?.GetValueOrDefault("dst") is [var d] && ItemOf(P, d) == rc && s.Get("src") is { K: "num", V: { } v } && long.TryParse(v, out var x) && x != 0 ? x : null;
        var capacity = P.Model.Paragraphs.Where(x => onSize.Contains(x.Name)).SelectMany(x => Deep(x.Statements)).Select(Moved).OfType<long>().Distinct().ToList();
        if (capacity.Count != 1) return null;
        var others = all.Select(Moved).OfType<long>().Where(x => x != capacity[0]).Distinct().ToList();
        return others.Count == 1 ? (capacity[0], others[0]) : null;
    }

    /// <summary>The literals a caller moves into its code field (every write one), and that it never reads an operand A or B.</summary>
    static bool CallerCodes(AppProgram c, string kernelId, DataItem kernelRec, DataItem code, DataItem ga, DataItem gb, SortedSet<string> reached)
    {
        var P = c.P;
        // the caller's record at the CALLs: the same layout, the code at the same offset
        var recs = new HashSet<DataItem>();
        foreach (var s in c.Classic.SelectMany(x => Deep(x.Statements)).Where(s => s.Verb == "CALL" && s.Get("target") is { V: var t } && string.Equals(t, kernelId, StringComparison.OrdinalIgnoreCase)))
        {
            var args = (s.L?.GetValueOrDefault("args") ?? []).Select(x => x.L ?? x).ToList();
            if (args is not [var arg] || ItemOf(P, arg) is not { } r || r.Length != kernelRec.Length) return false;
            recs.Add(r);
        }
        if (recs.Count != 1) return false;
        var rec = recs.First();
        int Off(DataItem d, DataItem root) => d.Offset - root.Offset;
        bool Overlaps(DataItem d, DataItem part) => d.Record == rec && Off(d, rec) < Off(part, kernelRec) + part.Length && Off(part, kernelRec) < Off(d, rec) + d.Length;
        bool IsCode(DataItem d) => d.Record == rec && Off(d, rec) == Off(code, kernelRec) && d.Length == code.Length;
        foreach (var s in c.Classic.SelectMany(x => Deep(x.Statements)))
        {
            bool callOfKernel = s.Verb == "CALL" && s.Get("target") is { V: var t } && string.Equals(t, kernelId, StringComparison.OrdinalIgnoreCase);
            var dsts = (s.L?.GetValueOrDefault("dst") ?? []).Concat(s.L?.GetValueOrDefault("giving") ?? []).ToHashSet();
            foreach (var d in dsts)
                if (ItemOf(P, d) is { } di && di.Record == rec && Overlaps(di, code))
                {
                    if (!(s.Verb == "MOVE" && IsCode(di) && s.Get("src") is { K: "str", V: { } lit })) return false;
                    reached.Add(lit.TrimEnd());
                }
            if (callOfKernel) continue;
            // a read of an operand (or of the record around it) after a CALL: the operands are the interface's inputs only
            foreach (var r in Refs(s).Where(x => !dsts.Contains(x)))
                if (ItemOf(P, r) is { } ri && (Overlaps(ri, ga) || Overlaps(ri, gb))) return false;
        }
        return true;
    }

    static IEnumerable<Node> Refs(Stmt s)
    {
        IEnumerable<Node> Of(Node? n)
        {
            if (n is null) yield break;
            if (n.K == "ref") yield return n;
            foreach (var x in new[] { n.L, n.R, n.Start, n.Len }.Concat(n.Args ?? [])) foreach (var y in Of(x)) yield return y;
        }
        foreach (var n in s.N?.Values ?? Enumerable.Empty<Node>()) foreach (var r in Of(n)) yield return r;
        foreach (var l in s.L?.Values ?? Enumerable.Empty<List<Node>>()) foreach (var n in l) foreach (var r in Of(n)) yield return r;
    }

    /// <summary>
    /// The library's capacity from its declarations: the store (a table of entries, each a generation and a value of
    /// limbs), the registers (groups of a sign, a length and limbs), the limb's digits, the handle (generation times a
    /// literal plus the entry, in a field of so many digits), the scale's and the generation's PICTUREs, the unscaled digits.
    /// </summary>
    static Dictionary<string, long>? Capacity(AppProgram ap, DataItem generation, DataItem unscaled, DataItem scale)
    {
        var P = ap.P;
        var ws = P.Model.Items.Where(x => x.Record.Section != DataSection.Linkage).ToList();
        static IEnumerable<DataItem> Desc(DataItem d) { foreach (var c in d.Children) { yield return c; foreach (var x in Desc(c)) yield return x; } }
        // a magnitude: a sign, a length and a table of limbs (binary)
        static int? Limbs(DataItem g)
        {
            var f = g.Children.Where(c => c.Level != 88).ToList();
            return !g.IsLeaf && f.Count == 3 && Bin(f[0]) && Bin(f[1]) && f[2] is { IsLeaf: true, Category: Category.Numeric, Usage: Usage.Binary or Usage.Comp5, Occurs: > 1 } l ? l.Occurs : null;
        }
        var store = ws.FirstOrDefault(t => t.Occurs > 1 && Desc(t).Any(d => Limbs(d) is not null) && t.Children.Any(c => Bin(c)));
        if (store is null) return null;
        var storedLimbs = Desc(store).Select(Limbs).First(x => x is not null)!.Value;
        var regs = ws.Where(g => g.Occurs == 0 && Limbs(g) is not null && !Desc(store).Contains(g)).Select(g => Limbs(g)!.Value).Distinct().ToList();
        if (regs.Count != 1) return null;
        var limb = Desc(store).First(d => Limbs(d) is not null).Children.Where(c => c.Level != 88).ToList()[2];
        // the handle: COMPUTE h = generation * k + entry
        long? handleBase = null; int handleDigits = 0;
        foreach (var s in Deep(P.Model.Paragraphs.SelectMany(x => x.Statements)).Where(s => s.Verb == "COMPUTE"))
            if (s.Get("expr") is { K: "bin", Op: "+", L: { K: "bin", Op: "*", L: { K: "ref" } gl, R: { K: "num" } kl } } && ItemOf(P, gl) == generation
                && s.L?.GetValueOrDefault("dst") is [var hd] && ItemOf(P, hd) is { } h && Bin(h))
            {
                handleBase = long.Parse(kl.V!, CultureInfo.InvariantCulture);
                handleDigits = h.Digits;
            }
        if (handleBase is null) return null;
        long Max(DataItem d) => (long)Math.Pow(10, d.Digits) - 1;
        return new Dictionary<string, long>
        {
            ["OPERAND_DIGITS"] = unscaled.Digits,
            ["STORE_ENTRIES"] = store.Occurs,
            ["STORED_LIMBS"] = storedLimbs,
            ["REGISTER_LIMBS"] = regs[0],
            ["LIMB_DIGITS"] = limb.Digits,
            ["HANDLE_ENTRIES"] = handleBase.Value,
            ["HANDLE_DIGITS"] = handleDigits,
            ["MAX_SCALE"] = Max(scale),
            ["MAX_GENERATION"] = Max(generation),
        };
    }

    /// <summary>The record class's view of the contract's fields.</summary>
    sealed class View(AppModel a, AppProgram ap)
    {
        public readonly RecordClass Rc = ap.Records[ap.Kernel!.Parms];
        readonly KernelContract k = ap.Kernel!;
        public string Record => Rc.Name;
        public string RecordImport => a.RecordPackage + "." + Rc.Name;
        public string Acc(string role) => Rc.Accessors[k.Roles[role].Name];
        public string? Enum(string role) => Rc.Codes.TryGetValue(k.Roles[role].Name, out var c) ? c.Enum : null;
        public string? Const(string role, string code)
        {
            if (!Rc.Codes.TryGetValue(k.Roles[role].Name, out var c) || !c.Codes.Contains(code)) return null;
            bool meant = c.Codes.All(x => c.Names.ContainsKey(x)) && c.Names.Values.Distinct().Count() == c.Names.Count;
            return meant ? c.Names[code] : code;
        }
        public string? Group(string role)
        {
            var g = k.Roles[role];
            return Rc.View(g.Offset - g.Record.Offset, g.Length);
        }
    }

    static string Codes(KernelContract k, string op) => string.Join(", ", k.Operations.Where(x => x.Value == op).Select(x => x.Key));

    /// <summary>The JDK class; null when the record cannot name every reached code or a group view.</summary>
    public static string? JavaText(AppModel a, AppProgram ap)
    {
        var v = new View(a, ap);
        var k = ap.Kernel!;
        if (v.Enum("op") is not { } opEnum || k.Operations.Keys.Any(c => v.Const("op", c) is null)) return null;
        if (v.Group("a") is not { } aText || v.Group("r") is not { } rText) return null;
        string Cases(params string[] ops) => string.Join(", ", k.Operations.Where(x => ops.Contains(x.Value)).Select(x => v.Const("op", x.Key)));
        bool Has(string op) => k.Operations.ContainsValue(op);
        string aU = v.Acc("aU"), aS = v.Acc("aS"), aZ = v.Acc("aZ"), bU = v.Acc("bU"), bS = v.Acc("bS"), bZ = v.Acc("bZ");
        string rU = v.Acc("rU"), rS = v.Acc("rS"), rZ = v.Acc("rZ"), p = v.Acc("parm"), m = v.Acc("mode"), n = v.Acc("n"), rc = v.Acc("rc"), op = v.Acc("op");
        var L = k.Limits;
        string Num(string key) => L[key].ToString("N0", CultureInfo.InvariantCulture).Replace(",", "_");
        var parms = k.Parms.Name;
        string Name(string role) => k.Roles[role].Name;
        var cases = new StringBuilder();
        void Case(string ops, string body) { if (ops.Length > 0) cases.Append($"                case {ops} -> {body}\n"); }
        if (Has("divideToPrecision"))
            Case(Cases("divideToPrecision"), $$"""
{
                    if (jb.{{aZ}}() < 0 || jb.{{bZ}}() < 0) {
                        throw capacity("a division to a precision of a stored value");
                    }
                    result(jb, divide(inline(jb.{{aU}}(), jb.{{aS}}(), jb.{{aZ}}()), inline(jb.{{bU}}(), jb.{{bS}}(), jb.{{bZ}}()), jb.{{p}}(), mode(jb.{{m}}())));
                }
""".TrimEnd());
        Case(Cases("precision"), $"jb.{n}(operand(jb.{aU}(), jb.{aS}(), jb.{aZ}()).value().precision());");
        if (Has("setScale"))
            Case(Cases("setScale"), $$"""
{
                    if (jb.{{p}}() == jb.{{aS}}()) {
                        // at its own scale the operand is the result as it is passed (a handle not looked up)
                        if (jb.{{aZ}}() > 0) {
                            inline(jb.{{aU}}(), jb.{{aS}}(), jb.{{aZ}}());
                        }
                        jb.{{rText}}(jb.{{aText}}());
                    } else {
                        result(jb, setScale(operand(jb.{{aU}}(), jb.{{aS}}(), jb.{{aZ}}()), jb.{{p}}(), mode(jb.{{m}}())));
                    }
                }
""".TrimEnd());
        var binary = new StringBuilder();
        void Bin2(string ops, string body) { if (ops.Length > 0) binary.Append($"                        case {ops} -> {body}\n"); }
        Bin2(Cases("add"), "result(jb, add(a, b.value()));");
        Bin2(Cases("subtract"), "result(jb, add(a, b.value().negate()));");
        Bin2(Cases("multiply"), "result(jb, multiply(a, b));");
        Bin2(Cases("divideToScale"), $"result(jb, divide(a, b, jb.{p}(), mode(jb.{m}())));");
        Bin2(Cases("compare"), $"jb.{n}(compare(a, b));");
        var binaryOps = Cases("add", "subtract", "multiply", "divideToScale", "compare");
        if (binaryOps.Length > 0)
            Case(binaryOps, $$"""
{
                    Operand a = operand(jb.{{aU}}(), jb.{{aS}}(), jb.{{aZ}}());
                    Operand b = operand(jb.{{bU}}(), jb.{{bS}}(), jb.{{bZ}}());
                    switch (jb.{{op}}Code()) {
{{binary.ToString().TrimEnd()}}
                        default -> throw new IllegalStateException();
                    }
                }
""".TrimEnd());
        var computing = Cases("add", "subtract", "multiply", "divideToPrecision", "divideToScale", "setScale", "precision", "compare");
        var generationCase = Has("generation") ? $$"""
            case {{Cases("generation")}} -> {
                newGeneration();
                return;
            }

""" : "";
        var operations = string.Join(", ", k.Operations.Select(x => $"{x.Key} {x.Value}"));
        return $$"""
package {{a.AppPackage}};

import java.math.BigDecimal;
import java.math.BigInteger;
import java.math.MathContext;
import java.math.RoundingMode;
import java.util.ArrayList;
import java.util.List;
import {{v.RecordImport}};

/**
 * Decimal arithmetic with BigDecimal: the operations the estate's programs CALL the decimal library {{ap.P.Id}} for, on
 * values its interface passes as an unscaled integer and a scale. It replaces the lifted library; the differential gate
 * {{a.AppPackage}}.reference.DecimalKernelGate runs both on the same records and fails the build on any difference. The
 * operations its callers reach ({{operations}}) are the JDK's: an operation no caller reaches is outside the gated
 * domain (SemanticFrontier).
 * <p>The library's capacity is kept as declared guards (return code {{L["CAPACITY_CODE"]}}, its callers' capacity error): a value of more
 * than {{L["OPERAND_DIGITS"]}} digits lives in its store ({{Num("STORE_ENTRIES")}} values a generation, each of at most {{Num("STORED_LIMBS")}} limbs of {{L["LIMB_DIGITS"]}}
 * digits), the interface passing its handle (generation * {{Num("HANDLE_ENTRIES")}} + entry, the extension -1); an operation's
 * registers hold {{Num("REGISTER_LIMBS")}} limbs; a scale is within +-{{Num("MAX_SCALE")}}; the division to a precision works in
 * {{L["OPERAND_DIGITS"]}}-digit registers.
 * <p>Legacy: {{ap.P.Id}} (CALL '{{ap.P.Id}}' USING {{parms}}).
 * <p>Generated by MlCpu.Modernize: do not edit, regenerate.
 */
public final class DecimalKernel {
    /** The CALL interface's return code of an ArithmeticException (a zero divisor, a rounding the mode forbids). */
    public static final int ARITHMETIC = {{L["ARITHMETIC_CODE"]}};
    /** The CALL interface's return code of an operation past the library's capacity. */
    public static final int CAPACITY = {{L["CAPACITY_CODE"]}};
    /** The digits of an operand's unscaled value ({{Name("aU")}}): a value past them is stored. */
    private static final int OPERAND_DIGITS = {{L["OPERAND_DIGITS"]}};
    private static final BigInteger INLINE_LIMIT = BigInteger.TEN.pow(OPERAND_DIGITS);
    /** The values the store holds a generation (its OCCURS). */
    private static final int STORE_ENTRIES = {{Num("STORE_ENTRIES")}};
    /** The limbs of a stored value (its OCCURS). */
    private static final int STORED_LIMBS = {{Num("STORED_LIMBS")}};
    /** The limbs of an operation's register (their OCCURS). */
    private static final int REGISTER_LIMBS = {{Num("REGISTER_LIMBS")}};
    /** The digits of a limb. */
    private static final int LIMB_DIGITS = {{L["LIMB_DIGITS"]}};
    /** A handle: generation * HANDLE_ENTRIES + entry. */
    private static final long HANDLE_ENTRIES = {{Num("HANDLE_ENTRIES")}}L;
    /** The digits of a handle's field. */
    private static final int HANDLE_DIGITS = {{L["HANDLE_DIGITS"]}};
    /** The largest scale the interface holds ({{Name("aS")}}). */
    private static final long MAX_SCALE = {{Num("MAX_SCALE")}}L;
    /** The largest generation; the next is 1. */
    private static final int MAX_GENERATION = {{Num("MAX_GENERATION")}};

    /** The stored values of the current generation (entry 1 first): unscaled, the scale is the interface's. */
    private final List<BigInteger> store = new ArrayList<>();
    private int generation = 1;

    /** An operation the interface cannot complete, with its return code. */
    public static final class Failure extends ArithmeticException {
        private static final long serialVersionUID = 1L;
        private final int returnCode;

        Failure(int returnCode, String message) {
            super(message);
            this.returnCode = returnCode;
        }

        /** The CALL interface's return code. */
        public int returnCode() {
            return returnCode;
        }
    }

    /**
     * An input outside the gated domain: an operation no caller reaches, an operand with virtual trailing zeros (an
     * extension above 0, which none of the reached operations makes), a negative precision. The run fails rather than
     * guess.
     */
    public static final class SemanticFrontier extends UnsupportedOperationException {
        private static final long serialVersionUID = 1L;

        SemanticFrontier(String message) {
            super("semantic frontier: " + message);
        }
    }

    /** An operand: its value, and whether it is a stored one (passed by its handle). */
    private record Operand(BigDecimal value, boolean stored) {
        BigInteger magnitude() {
            return value.unscaledValue().abs();
        }
    }

    /** The RoundingMode of the interface's mode number (java.math's numbering, 0..6); UNNECESSARY otherwise. */
    public static RoundingMode mode(int number) {
        return number >= 0 && number <= 6 ? RoundingMode.valueOf(number) : RoundingMode.UNNECESSARY;
    }

    /**
     * The CALL interface (legacy {{ap.P.Id}} USING {{parms}}): {{Name("op")}} the operation, {{Name("a")}} and {{Name("b")}} the
     * operands, {{Name("parm")}} the precision or the scale, {{Name("mode")}} the rounding mode; {{Name("r")}} the result,
     * {{Name("n")}} the precision or the comparison; {{Name("rc")}} 0, ARITHMETIC or CAPACITY. The outputs are defined for
     * return code 0; the operands are inputs only (their content after the call is not part of the interface).
     */
    public void call({{v.Record}} jb) {
        jb.{{rc}}(0);
        switch (jb.{{op}}Code()) {
{{generationCase}}            case {{computing}} -> {
            }
            default -> throw new SemanticFrontier("operation '" + jb.{{op}}() + "' is none the callers reach");
        }
        try {
            switch (jb.{{op}}Code()) {
{{cases.ToString().TrimEnd()}}
                default -> throw new IllegalStateException();
            }
        } catch (Failure e) {
            jb.{{rc}}(e.returnCode());
        } catch (ArithmeticException e) {
            jb.{{rc}}(ARITHMETIC);
        }
    }

    /** A new generation of the store: its values are no longer valid handles. */
    public void newGeneration() {
        generation = generation == MAX_GENERATION ? 1 : generation + 1;
        store.clear();
    }

    /** An operand of the interface: an inline value, or a stored one by its handle (a handle of no stored value: CAPACITY). */
    private Operand operand(BigDecimal unscaled, int scale, int extension) {
        if (extension >= 0) {
            return new Operand(inline(unscaled, scale, extension), false);
        }
        // the handle is the field's low-order digits
        long handle = unscaled.toBigInteger().remainder(BigInteger.TEN.pow(HANDLE_DIGITS)).longValue();
        long entry = handle % HANDLE_ENTRIES;
        if (entry < 1 || entry > store.size() || handle / HANDLE_ENTRIES != generation) {
            throw capacity("handle " + handle + " is no stored value of generation " + generation);
        }
        return new Operand(new BigDecimal(store.get((int) entry - 1), scale), true);
    }

    /** An inline operand (extension 0): its unscaled value at its scale. */
    private static BigDecimal inline(BigDecimal unscaled, int scale, int extension) {
        if (extension > 0) {
            throw new SemanticFrontier("an operand with " + extension + " virtual trailing zeros");
        }
        return new BigDecimal(unscaled.toBigInteger(), scale);
    }

    /** The result: inline below 10**OPERAND_DIGITS, else a new stored value (at most STORED_LIMBS limbs, STORE_ENTRIES a generation). */
    private void result({{v.Record}} jb, BigDecimal r) {
        BigInteger u = r.unscaledValue();
        if (u.abs().compareTo(INLINE_LIMIT) < 0) {
            jb.{{rU}}(new BigDecimal(u));
            jb.{{rZ}}(0);
        } else {
            if (limbs(u) > STORED_LIMBS || store.size() >= STORE_ENTRIES) {
                throw capacity("the store holds no more values of " + limbs(u) + " limbs");
            }
            store.add(u);
            jb.{{rU}}(BigDecimal.valueOf(generation * HANDLE_ENTRIES + store.size()));
            jb.{{rZ}}(-1);
        }
        jb.{{rS}}(r.scale());
    }

    /** a + b (subtract: b negated): the operand of the smaller scale raised to the other's. */
    private static BigDecimal add(Operand a, BigDecimal b) {
        aligned(a.value(), b);
        return checked(a.value().add(b));
    }

    /** a * b: the product of the magnitudes in one register. */
    private static BigDecimal multiply(Operand a, Operand b) {
        if (a.value().signum() != 0 && b.value().signum() != 0 && limbs(a.magnitude()) + limbs(b.magnitude()) > REGISTER_LIMBS) {
            throw capacity("a product of " + (limbs(a.magnitude()) + limbs(b.magnitude())) + " limbs");
        }
        checkScale((long) a.value().scale() + b.value().scale());
        return a.value().multiply(b.value());
    }

    /** a / b at a scale with a rounding mode: the dividend or the divisor raised, then long division. */
    private static BigDecimal divide(Operand a, Operand b, int scale, RoundingMode mode) {
        if (b.value().signum() == 0) {
            throw new Failure(ARITHMETIC, "division by zero");
        }
        if (a.value().signum() == 0) {
            return BigDecimal.valueOf(0, scale);
        }
        long raise = (long) scale + b.value().scale() - a.value().scale();
        BigInteger dividend = a.magnitude();
        BigInteger divisor = b.magnitude();
        if (raise > 0) {
            dividend = shifted(dividend, raise);
        } else if (raise < 0) {
            divisor = shifted(divisor, -raise);
        }
        division(dividend, divisor, mode);
        return a.value().divide(b.value(), scale, mode);
    }

    /** a at a new scale with a rounding mode: raised, or divided by a power of ten. */
    private static BigDecimal setScale(Operand a, int scale, RoundingMode mode) {
        long by = (long) scale - a.value().scale();
        if (a.value().signum() != 0) {
            if (by > 0) {
                shifted(a.magnitude(), by);
            } else if (a.stored()) {
                division(a.magnitude(), shifted(BigInteger.ONE, -by), mode);
            } else if (-by > digits(a.magnitude()) + 1) {
                // an inline value rounds in its own register: all its digits dropped, it is less than a tenth of the new
                // unit and rounds as any such value of its sign (one hundredth)
                return BigDecimal.valueOf(a.value().signum(), scale + 2).setScale(scale, mode);
            }
        }
        return a.value().setScale(scale, mode);
    }

    /** a.compareTo(b): a stored operand's comparison aligns the scales in one register. */
    private static int compare(Operand a, Operand b) {
        int sa = a.value().signum();
        if ((a.stored() || b.stored()) && sa == b.value().signum() && sa != 0) {
            aligned(a.value(), b.value());
        }
        return a.value().compareTo(b.value());
    }

    /**
     * a / b to a precision with a rounding mode: BigDecimal.divide(MathContext), for values of at most OPERAND_DIGITS
     * digits. The library divides in OPERAND_DIGITS-digit registers: the dividend and the divisor, their trailing zeros
     * stripped, raised to give the precision's digits must fit, and so must ten times the quotient and the remainder of
     * an inexact one; a precision of 0 and a result scale past +-MAX_SCALE are its capacity too.
     */
    private static BigDecimal divide(BigDecimal a, BigDecimal b, int precision, RoundingMode mode) {
        if (precision < 0) {
            throw new SemanticFrontier("precision " + precision);
        }
        if (precision == 0) {
            throw capacity("precision 0");
        }
        if (b.signum() == 0) {
            throw new Failure(ARITHMETIC, "division by zero");
        }
        long preferred = (long) a.scale() - b.scale();
        if (a.signum() == 0) {
            checkScale(preferred);
            return BigDecimal.valueOf(0, (int) preferred);
        }
        BigInteger ua = a.unscaledValue();
        BigInteger ub = b.unscaledValue();
        long da = digits(ua);
        long ys = digits(ub);
        // the magnitudes compared left-aligned: a quotient digit fewer when the dividend's leading digits are larger
        if (ua.abs().multiply(BigInteger.TEN.pow(OPERAND_DIGITS - (int) da)).compareTo(ub.abs().multiply(BigInteger.TEN.pow(OPERAND_DIGITS - (int) ys))) > 0) {
            ys--;
        }
        long raise = precision + ys - da;
        long ea = 0;
        long eb = 0;
        if (raise > 0) {
            ea = raise;
        } else if (da - precision != ys) {
            eb = da - precision - ys;
        }
        int za = trailingZeros(ua);
        int zb = trailingZeros(ub);
        ea += za;
        eb += zb;
        if (ea < eb) {
            eb -= ea;
            ea = 0;
        } else {
            ea -= eb;
            eb = 0;
        }
        BigInteger y = raised(ub.divide(BigInteger.TEN.pow(zb)), eb);
        BigInteger x = raised(ua.divide(BigInteger.TEN.pow(za)), ea);
        BigInteger[] qr = x.divideAndRemainder(y);
        if (qr[1].signum() != 0) {
            if (mode == RoundingMode.UNNECESSARY) {
                throw new Failure(ARITHMETIC, "rounding necessary");
            }
            if (!fits(qr[1].multiply(BigInteger.TEN)) || !fits(qr[0].multiply(BigInteger.TEN))) {
                throw capacity("a quotient register past " + OPERAND_DIGITS + " digits");
            }
        }
        return checked(a.divide(b, new MathContext(precision, mode)));
    }

    // ------------------------------------------------------------------ the library's registers

    /** A magnitude raised by k digits in one register (its limbs, the whole limbs of k and one more), else CAPACITY. */
    private static BigInteger shifted(BigInteger magnitude, long k) {
        if (magnitude.signum() != 0 && limbs(magnitude) + k / LIMB_DIGITS + 1 > REGISTER_LIMBS) {
            throw capacity("a value raised by " + k + " digits");
        }
        return magnitude.signum() == 0 ? magnitude : magnitude.multiply(BigInteger.TEN.pow((int) k));
    }

    /** The operand of the smaller scale raised to the other's (in one register). */
    private static void aligned(BigDecimal a, BigDecimal b) {
        long by = (long) b.scale() - a.scale();
        if (by > 0 && a.signum() != 0) {
            shifted(a.unscaledValue().abs(), by);
        }
        if (by < 0 && b.signum() != 0) {
            shifted(b.unscaledValue().abs(), -by);
        }
    }

    /**
     * Long division of magnitudes and the rounding of its quotient: a divisor of several limbs needs the dividend's limbs
     * and one more; a half mode doubles the remainder, an increment adds one, each in one register.
     */
    private static void division(BigInteger dividend, BigInteger divisor, RoundingMode mode) {
        if (dividend.compareTo(divisor) >= 0 && limbs(divisor) > 1 && limbs(dividend) + 1 > REGISTER_LIMBS) {
            throw capacity("a dividend of " + limbs(dividend) + " limbs");
        }
        BigInteger[] qr = dividend.divideAndRemainder(divisor);
        if (qr[1].signum() == 0 || mode == RoundingMode.UNNECESSARY) {
            return;
        }
        if ((mode == RoundingMode.HALF_UP || mode == RoundingMode.HALF_DOWN || mode == RoundingMode.HALF_EVEN) && limbs(qr[1].shiftLeft(1)) > REGISTER_LIMBS) {
            throw capacity("a doubled remainder past the register");
        }
        if (limbs(qr[0].add(BigInteger.ONE)) > REGISTER_LIMBS) {
            throw capacity("a rounded quotient past the register");
        }
    }

    /** A value raised by k digits in an OPERAND_DIGITS-digit register (more than OPERAND_DIGITS - 1 digits, or past it: CAPACITY). */
    private static BigInteger raised(BigInteger v, long k) {
        if (k > 0 && v.signum() != 0) {
            if (k > OPERAND_DIGITS - 1) {
                throw capacity("a register raised by " + k + " digits");
            }
            BigInteger r = v.multiply(BigInteger.TEN.pow((int) k));
            if (!fits(r)) {
                throw capacity("a register past " + OPERAND_DIGITS + " digits");
            }
            return r;
        }
        return v;
    }

    private static BigDecimal checked(BigDecimal r) {
        checkScale(r.scale());
        return r;
    }

    private static void checkScale(long scale) {
        if (scale > MAX_SCALE || scale < -MAX_SCALE) {
            throw capacity("scale " + scale);
        }
    }

    private static boolean fits(BigInteger v) {
        return v.abs().compareTo(INLINE_LIMIT) < 0;
    }

    /** The digits of a magnitude (zero has one). */
    private static int digits(BigInteger v) {
        return v.signum() == 0 ? 1 : v.abs().toString().length();
    }

    private static int trailingZeros(BigInteger v) {
        int n = 0;
        for (BigInteger x = v; x.signum() != 0 && x.mod(BigInteger.TEN).signum() == 0; x = x.divide(BigInteger.TEN)) {
            n++;
        }
        return n;
    }

    /** The limbs of a magnitude (one for zero). */
    private static int limbs(BigInteger v) {
        return (digits(v) + LIMB_DIGITS - 1) / LIMB_DIGITS;
    }

    private static Failure capacity(String message) {
        return new Failure(CAPACITY, "beyond the decimal library's capacity: " + message);
    }
}

""";
    }

    /// <summary>The nine operations the gate's cases cover (a library whose callers reach others or fewer stays lifted).</summary>
    public static readonly string[] Gated = ["generation", "add", "subtract", "multiply", "divideToPrecision", "divideToScale", "setScale", "precision", "compare"];

    /// <summary>The differential gate of the JDK class against the lifted library (test lane), with its mutants and frontier.</summary>
    public static string GateText(AppModel a, AppProgram ap)
    {
        var v = new View(a, ap);
        var k = ap.Kernel!;
        string Lit(string c) => "\"" + c + "\"";
        string CodeOf(string op) => Lit(k.Operations.First(x => x.Value == op).Key);
        var refName = Kernels.ReferenceName(ap);
        var callees = ap.Callees.Keys.Select(c => a.ById[c]).ToList();
        var imports = new SortedSet<string>(StringComparer.Ordinal) { v.RecordImport };
        foreach (var c in callees) imports.Add(a.AppPackage + "." + c.ClassName);
        var text = GateTemplate
            .Replace("@PKG@", a.AppPackage)
            .Replace("@IMPORTS@", string.Join('\n', imports.Select(i => "import " + i + ";")))
            .Replace("@REFNEW@", $"new {refName}({string.Join(", ", callees.Select(c => $"new {c.ClassName}()"))})")
            .Replace("@REF@", refName)
            .Replace("@PROG@", ap.P.Id)
            .Replace("@REC@", v.Record)
            .Replace("@RNAME@", k.Roles["r"].Name)
            .Replace("@NNAME@", k.Roles["n"].Name)
            .Replace("@OPS@", string.Join(", ", k.Operations.Keys))
            .Replace("@UNREACHED@", string.Join(", ", k.Unreached.Concat(["XX", "  "]).Distinct().Where(c => !k.Operations.ContainsKey(c)).Select(Lit)))
            .Replace("@COMPUTING@", string.Join(", ", Gated.Skip(1).Select(CodeOf)))
            .Replace("@BINARY@", string.Join(", ", new[] { "add", "subtract", "multiply", "divideToScale", "compare" }.Select(CodeOf)))
            .Replace("@ADD@", CodeOf("add")).Replace("@SUBTRACT@", CodeOf("subtract")).Replace("@MULTIPLY@", CodeOf("multiply"))
            .Replace("@DIVIDE_PRECISION@", CodeOf("divideToPrecision")).Replace("@DIVIDE_SCALE@", CodeOf("divideToScale"))
            .Replace("@SET_SCALE@", CodeOf("setScale")).Replace("@PRECISION@", CodeOf("precision")).Replace("@COMPARE@", CodeOf("compare"))
            .Replace("@GENERATION@", CodeOf("generation"));
        foreach (var role in new[] { "aU", "aS", "aZ", "bU", "bS", "bZ", "rU", "rS", "rZ", "n", "rc", "op" })
            text = text.Replace("@" + role + "@", v.Acc(role));
        text = text.Replace("@p@", v.Acc("parm")).Replace("@m@", v.Acc("mode"));
        text = text.Replace("@aText@", v.Group("a")!).Replace("@bText@", v.Group("b")!).Replace("@rText@", v.Group("r")!);
        return text;
    }

    const string GateTemplate = """
package @PKG@.reference;

import java.math.BigDecimal;
import java.math.BigInteger;
import java.util.ArrayList;
import java.util.List;
import java.util.Random;
import java.util.function.Supplier;
import @PKG@.DecimalKernel;
@IMPORTS@

/**
 * The differential gate of DecimalKernel (BigDecimal) against @REF@, the lifted COBOL library @PROG@. Both
 * run on the same record images, in the same order (the library keeps a store of values past 31 digits between CALLs):
 * the return code is compared always, the result (@RNAME@) and @NNAME@ when it is 0 or 1 (the callers abend on 20). The cases:
 * the reached operations (@OPS@) over edge values (zero, one, powers of ten and their
 * neighbours, halves, the 31-digit extremes, both signs) at edge scales, every rounding mode number (0..6, 7, 9, -1) and
 * edge precisions and scales; stored values made by products (up to past the store's and the registers' capacity) and
 * operated on; handles of no stored value; the store filled; a fixed-seed sample (seed 20261101). A mutant (a
 * plausible wrong substitution) must make the gate fail; the frontier (an operation no caller reaches, virtual trailing
 * zeros, a negative precision) must throw DecimalKernel's SemanticFrontier.
 * <p>Generated by MlCpu.Modernize: do not edit, regenerate.
 */
public final class DecimalKernelGate {
    private DecimalKernelGate() {
    }

    /** A CALL of the interface on a record. */
    interface Kernel {
        void call(@REC@ jb);
    }

    /** An operand: inline (an unscaled value, a scale), or the result of an earlier request (its handle or value). */
    record Operand(BigInteger unscaled, int scale, int extension, int result) {
        static Operand of(BigInteger u, int s) {
            return new Operand(u, s, 0, -1);
        }

        static Operand handle(long h, int s) {
            return new Operand(BigInteger.valueOf(h), s, -1, -1);
        }

        static Operand result(int k) {
            return new Operand(null, 0, 0, k);
        }
    }

    /** One comparison of two kernels over a stream of requests. */
    static final class Comparison {
        private final Kernel reference;
        private final Kernel candidate;
        private final @REC@ expected = new @REC@();
        private final @REC@ actual = new @REC@();
        /** The reference's result of each request (JB-R), for later requests that pass it on. */
        final List<String> results = new ArrayList<>();
        final List<Integer> codes = new ArrayList<>();
        final java.util.Map<String, Integer> outcomes = new java.util.TreeMap<>();
        long cases;
        long differences;
        String first;

        Comparison(Kernel reference, Kernel candidate) {
            this.reference = reference;
            this.candidate = candidate;
        }

        private void put(@REC@ jb, Operand o, boolean a) {
            if (o.result() >= 0) {
                if (a) jb.@aText@(results.get(o.result())); else jb.@bText@(results.get(o.result()));
                return;
            }
            if (a) {
                jb.@aU@(new BigDecimal(o.unscaled()));
                jb.@aS@(o.scale());
                jb.@aZ@(o.extension());
            } else {
                jb.@bU@(new BigDecimal(o.unscaled()));
                jb.@bS@(o.scale());
                jb.@bZ@(o.extension());
            }
        }

        /** After its first difference (a mutant's detection) the comparison runs no more requests. */
        boolean stopAtFirst;

        /** A request; its index (for a later operand). */
        int request(String op, Operand a, Operand b, int p, int m) {
            if (stopAtFirst && differences > 0) {
                results.add(results.get(results.size() - 1));
                codes.add(-1);
                return results.size() - 1;
            }
            expected.text("");
            expected.@op@(op);
            put(expected, a == null ? Operand.of(BigInteger.ZERO, 0) : a, true);
            put(expected, b == null ? Operand.of(BigInteger.ZERO, 0) : b, false);
            expected.@p@(p);
            expected.@m@(m);
            expected.@n@(-7);
            expected.@rU@(BigDecimal.valueOf(-7));
            expected.@rS@(-7);
            expected.@rZ@(-7);
            expected.@rc@(-1);
            actual.bytes(expected.bytes());
            reference.call(expected);
            candidate.call(actual);
            cases++;
            int rc = expected.@rc@();
            boolean same = rc == actual.@rc@() && (rc != 0 && rc != 1 || expected.@rText@().equals(actual.@rText@()) && expected.@n@() == actual.@n@());
            if (!same) {
                differences++;
                if (first == null) {
                    first = op + " a=" + show(a) + " b=" + show(b) + " p=" + p + " m=" + m + ": rc " + rc + "/" + actual.@rc@()
                        + " r " + expected.@rU@() + "," + expected.@rS@() + "," + expected.@rZ@() + "/" + actual.@rU@() + "," + actual.@rS@() + "," + actual.@rZ@()
                        + " n " + expected.@n@() + "/" + actual.@n@();
                }
            }
            outcomes.merge(op + ":" + rc + (rc == 0 && expected.@rZ@() < 0 && !op.equals(@PRECISION@) && !op.equals(@COMPARE@) && !op.equals(@GENERATION@) ? "h" : ""), 1, Integer::sum);
            results.add(expected.@rText@());
            codes.add(rc);
            return results.size() - 1;
        }

        String show(Operand o) {
            if (o == null) return "-";
            if (o.result() >= 0) return "#" + o.result();
            return o.unscaled() + "e-" + o.scale() + (o.extension() != 0 ? "z" + o.extension() : "");
        }
    }

    static final String[] BINARY = {@BINARY@};
    static final int[] MODES = {0, 1, 2, 3, 4, 5, 6, 7, 9, -1};

    /** The edge values: zero, one, powers of ten and their neighbours, halves, the 31-digit extremes, both signs. */
    static List<BigInteger> edgeValues() {
        List<BigInteger> values = new ArrayList<>();
        values.add(BigInteger.ZERO);
        for (int k : new int[] {0, 1, 2, 9, 15, 18, 19, 29, 30}) {
            BigInteger p = BigInteger.TEN.pow(k);
            for (BigInteger x : List.of(p, p.multiply(BigInteger.valueOf(5)), p.add(BigInteger.ONE), p.subtract(BigInteger.ONE), p.multiply(BigInteger.valueOf(7)).add(BigInteger.valueOf(3)))) {
                if (x.signum() > 0 && x.compareTo(BigInteger.TEN.pow(31)) < 0) {
                    values.add(x);
                    values.add(x.negate());
                }
            }
        }
        values.add(BigInteger.TEN.pow(31).subtract(BigInteger.ONE));
        values.add(BigInteger.TEN.pow(31).subtract(BigInteger.ONE).negate());
        return values.stream().distinct().toList();
    }

    static final int[] SCALES = {0, 1, 2, 4, 13, 20, 31, -1, -3, 999_999_999, -999_999_999, 999_999_990};
    static final int[] PARMS = {0, 1, 2, 4, 12, 20, 31, 40, -2, -30, 999_999_999, -999_999_999, 20_000};

    static void cases(Comparison c) {
        List<BigInteger> values = edgeValues();
        List<Operand> ops = new ArrayList<>();
        for (BigInteger v : values) for (int s : new int[] {0, 2, -3}) ops.add(Operand.of(v, s));
        for (BigInteger v : List.of(BigInteger.ONE, BigInteger.TEN.pow(30), BigInteger.valueOf(-7))) for (int s : new int[] {13, 31, 999_999_999, -999_999_999}) ops.add(Operand.of(v, s));
        // the binary operations over operand pairs, half-up and one other mode number each
        int k = 0;
        for (String op : BINARY) {
            for (Operand a : ops) {
                for (Operand b : ops) {
                    int m = MODES[k++ % MODES.length];
                    c.request(op, a, b, op.equals(@DIVIDE_SCALE@) ? PARMS[k % PARMS.length] : 0, op.equals(@DIVIDE_SCALE@) ? 4 : m);
                    if (op.equals(@DIVIDE_SCALE@)) c.request(op, a, b, PARMS[(k + 5) % PARMS.length], m);
                }
            }
        }
        // the unary operations at every precision or scale and mode
        for (Operand a : ops) {
            for (int p : PARMS) {
                for (int m : MODES) c.request(@SET_SCALE@, a, null, p, m);
            }
            c.request(@PRECISION@, a, null, 0, 0);
        }
        // the division to a precision: values of at most 31 digits, precisions 0..33, every mode
        Random random = new Random(20_261_101L);
        for (Operand a : ops) {
            for (Operand b : ops) {
                c.request(@DIVIDE_PRECISION@, a, b, 1 + random.nextInt(33), MODES[random.nextInt(MODES.length)]);
            }
        }
        for (int p = 0; p <= 33; p++) for (int m : MODES) c.request(@DIVIDE_PRECISION@, Operand.of(BigInteger.ONE, 0), Operand.of(BigInteger.valueOf(3), 0), p, m);
        // stored values: products past 31 digits squared up to the registers' capacity, and the operations on them
        c.request(@GENERATION@, null, null, 0, 0);
        List<Operand> stored = new ArrayList<>();
        int x = c.request(@MULTIPLY@, Operand.of(BigInteger.TEN.pow(31).subtract(BigInteger.ONE), 2), Operand.of(BigInteger.TEN.pow(30).add(BigInteger.valueOf(7)), 3), 0, 0);
        stored.add(Operand.result(x));
        for (int i = 0; i < 10; i++) {
            x = c.request(@MULTIPLY@, Operand.result(x), Operand.result(x), 0, 0);
            stored.add(Operand.result(x));
        }
        int neg = c.request(@SUBTRACT@, Operand.of(BigInteger.ZERO, 0), stored.get(2), 0, 0);
        stored.add(Operand.result(neg));
        for (Operand s : List.copyOf(stored)) {
            c.request(@PRECISION@, s, null, 0, 0);
            for (Operand o : List.of(Operand.of(BigInteger.ONE, 0), Operand.of(BigInteger.valueOf(-3), 20), Operand.of(BigInteger.TEN.pow(30), -5), stored.get(0), stored.get(3), Operand.of(BigInteger.valueOf(7), 999_999_999))) {
                for (String op : BINARY) {
                    c.request(op, s, o, 25, 4);
                    c.request(op, o, s, 3, 6);
                }
                c.request(@DIVIDE_PRECISION@, s, o, 20, 4);
                c.request(@DIVIDE_PRECISION@, o, s, 20, 4);
            }
            for (int p : new int[] {-40, -2, 0, 2, 5, 25, 1000, 40_000, 999_999_999}) {
                for (int m : new int[] {0, 1, 2, 3, 4, 5, 6, 7}) {
                    int r = c.request(@SET_SCALE@, s, null, p, m);
                    c.request(@PRECISION@, Operand.result(r), null, 0, 0);
                }
            }
        }
        // handles of no stored value, and of an earlier generation
        c.request(@ADD@, Operand.handle(1_000_099L, 0), Operand.of(BigInteger.ONE, 0), 0, 0);
        c.request(@PRECISION@, Operand.handle(-5L, 0), null, 0, 0);
        c.request(@SET_SCALE@, Operand.handle(7L, 2), null, 2, 4);
        c.request(@GENERATION@, null, null, 0, 0);
        c.request(@MULTIPLY@, stored.get(1), Operand.of(BigInteger.ONE, 0), 0, 0);
        c.request(@SET_SCALE@, stored.get(1), null, stored.get(1).scale(), 4);
        // the store filled: its last entries, and one more
        for (int i = 0; i < 2502; i++) {
            c.request(@MULTIPLY@, Operand.of(BigInteger.TEN.pow(30).add(BigInteger.valueOf(i)), 1), Operand.of(BigInteger.TEN.pow(20).subtract(BigInteger.valueOf(i)), 2), 0, 0);
        }
        c.request(@GENERATION@, null, null, 0, 0);
        // a fixed-seed sample: operands of 1..31 digits, scales -40..40, a third of them results of earlier requests
        for (int i = 0; i < 100_000; i++) {
            if (i % 5_000 == 0) c.request(@GENERATION@, null, null, 0, 0);
            Supplier<Operand> operand = () -> {
                if (random.nextInt(3) == 0 && c.results.size() > 0) {
                    int back = Math.max(0, c.results.size() - 1 - random.nextInt(20));
                    if (c.codes.get(back) == 0) return Operand.result(back);
                }
                int digits = 1 + random.nextInt(31);
                BigInteger v = new BigInteger(digits * 4, random).mod(BigInteger.TEN.pow(digits));
                return Operand.of(random.nextBoolean() ? v : v.negate(), random.nextInt(81) - 40);
            };
            String op = new String[] {@COMPUTING@}[random.nextInt(8)];
            int p = op.equals(@DIVIDE_PRECISION@) ? 1 + random.nextInt(34) : random.nextInt(81) - 40;
            c.request(op, operand.get(), operand.get(), p, MODES[random.nextInt(MODES.length)]);
        }
    }

    /** A mutant: the kernel with one plausible wrong substitution. */
    record Mutant(String name, Kernel kernel) {
    }

    static List<Mutant> mutants(Supplier<DecimalKernel> kernels) {
        DecimalKernel half = kernels.get();
        DecimalKernel store = kernels.get();
        DecimalKernel dm = kernels.get();
        DecimalKernel cp = kernels.get();
        DecimalKernel pr = kernels.get();
        DecimalKernel ds = kernels.get();
        return List.of(
            new Mutant("HALF_EVEN as HALF_UP", jb -> {
                int m = jb.@m@();
                if (m == 6) jb.@m@(4);
                half.call(jb);
                jb.@m@(m);
            }),
            new Mutant("the store kept at BZ", jb -> {
                if (!jb.@op@().equals(@GENERATION@)) store.call(jb); else jb.@rc@(0);
            }),
            new Mutant("DM to a scale", jb -> {
                if (jb.@op@().equals(@DIVIDE_PRECISION@)) {
                    jb.@op@(@DIVIDE_SCALE@);
                    dm.call(jb);
                    jb.@op@(@DIVIDE_PRECISION@);
                } else dm.call(jb);
            }),
            new Mutant("CP reversed", jb -> {
                cp.call(jb);
                if (jb.@op@().equals(@COMPARE@) && jb.@rc@() == 0) jb.@n@(-jb.@n@());
            }),
            new Mutant("PR of a stored value one less", jb -> {
                pr.call(jb);
                if (jb.@op@().equals(@PRECISION@) && jb.@rc@() == 0 && jb.@aZ@() < 0) jb.@n@(jb.@n@() - 1);
            }),
            new Mutant("DS truncating", jb -> {
                int m = jb.@m@();
                if (jb.@op@().equals(@DIVIDE_SCALE@)) jb.@m@(1);
                ds.call(jb);
                jb.@m@(m);
            }));
    }

    /** Whether the request throws DecimalKernel's frontier. */
    static boolean frontier(String op, int extension, int p) {
        @REC@ jb = new @REC@();
        jb.text("");
        jb.@op@(op);
        jb.@aU@(BigDecimal.valueOf(12_345));
        jb.@aS@(0);
        jb.@aZ@(extension);
        jb.@bU@(BigDecimal.valueOf(3));
        jb.@bS@(0);
        jb.@bZ@(0);
        jb.@p@(p);
        jb.@m@(4);
        try {
            new DecimalKernel().call(jb);
            return false;
        } catch (DecimalKernel.SemanticFrontier e) {
            return true;
        }
    }

    /** The frontier: the operations no caller reaches, virtual trailing zeros, a negative precision. */
    static boolean frontier() {
        boolean ok = true;
        for (String op : new String[] {@UNREACHED@}) if (!frontier(op, 0, 2)) { ok = false; System.out.println("  no frontier: " + op); }
        for (String op : new String[] {@COMPUTING@}) if (!(frontier(op, 1, 2) && !frontier(op, 0, 2))) { ok = false; System.out.println("  no frontier z: " + op); }
        if (!(frontier(@DIVIDE_PRECISION@, 0, -1) && !frontier(@DIVIDE_PRECISION@, 0, 0))) { ok = false; System.out.println("  no frontier p"); }
        System.out.println("DecimalKernel frontier: unreached operations, virtual trailing zeros and a negative precision throw SemanticFrontier: " + (ok ? "yes" : "NO"));
        return ok;
    }

    /** Runs the gate; true when the kernels agree on every case, every mutant is detected and the frontier holds. */
    public static boolean run() {
        boolean frontierOk = frontier();
        Comparison gate = new Comparison(@REFNEW@::call, new DecimalKernel()::call);
        cases(gate);
        System.out.println("DecimalKernel gate: " + gate.cases + " cases, " + gate.differences + " differences" + (gate.first == null ? "" : "; first: " + gate.first));
        System.out.println("  outcomes (operation:return code, h a stored result): " + gate.outcomes);
        boolean ok = gate.differences == 0 && frontierOk;
        for (Mutant m : mutants(DecimalKernel::new)) {
            Comparison c = new Comparison(@REFNEW@::call, m.kernel());
            c.stopAtFirst = true;
            cases(c);
            System.out.println("  mutant \"" + m.name() + "\": " + (c.differences > 0 ? "detected at case " + c.cases + ": " + c.first : "NOT DETECTED in " + c.cases + " cases"));
            ok &= c.differences > 0;
        }
        return ok;
    }

}
""";
}
