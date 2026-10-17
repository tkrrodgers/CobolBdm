using System.Globalization;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

public sealed partial class FunctionLifter
{
    int labelNo;
    string NewLabel(string hint) => $"{hint}_{++labelNo}";

    [GeneratedRegex(@"^[A-Z0-9_]+:\d+$")] private static partial Regex SiteRx();

    void Block(List<Stmt> list)
    {
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            line = s.Line;
            if (ClosureDispatch(list, ref i)) { CommitUntouched(); continue; }
            Statement(s);
            CommitUntouched();
        }
    }

    /// <summary>Paragraphs already lifted out of order (the branches of a closure dispatch).</summary>
    readonly HashSet<string> skipParagraphs = new(StringComparer.OrdinalIgnoreCase);
    Expr? samReceiver;

    /// <summary>The closure class id of an IF x-C = id GO TO label, or null.</summary>
    (Node Subject, string Label)? ClosureTest(Stmt s)
    {
        if (s.Verb != "IF" || s.Blocks?.GetValueOrDefault("FALSE") is { Count: > 0 }) return null;
        if (s.Get("cond") is not { K: "rel", Op: "eq", L: { K: "ref" } l, R: { K: "num" } r }) return null;
        if (!l.V!.EndsWith("-C", StringComparison.OrdinalIgnoreCase)) return null;
        if (!M.ClassById.TryGetValue(int.Parse(r.V!), out var c) || c.Kind != ClassKind.Closure) return null;
        if (s.Blocks?.GetValueOrDefault("TRUE") is not [{ Verb: "GO TO" } g] || g.L?["targets"] is not [{ V: var t }]) return null;
        return (l, t!);
    }

    /// <summary>
    /// A call of a functional interface on a closure of unknown class: IF x-C = id GO TO Wk for every closure class,
    /// each Wk calls that closure's lambda. One branch is lifted with its call turned into x.accept/apply/test(...).
    /// </summary>
    bool ClosureDispatch(List<Stmt> list, ref int i)
    {
        if (ClosureTest(list[i]) is not { } first) return false;
        var labels = new List<string> { first.Label };
        int j = i + 1;
        while (j < list.Count && ClosureTest(list[j]) is { } next && next.Subject.V!.Equals(first.Subject.V, StringComparison.OrdinalIgnoreCase)) { labels.Add(next.Label); j++; }
        var paras = P.Model.Paragraphs;
        if (!P.ParagraphIndex.TryGetValue(labels[0], out var pi)) return false;
        var whole = new Node { K = "ref", V = first.Subject.V![..^2], Id = P.ItemsById.GetValueOrDefault(first.Subject.Id ?? -1)?.Parent?.Id, Args = first.Subject.Args };
        var recv = Operand(whole);
        // the branches: each label and the return-point paragraph that follows a recursive call
        foreach (var lb in labels)
        {
            if (!P.ParagraphIndex.TryGetValue(lb, out var k)) continue;
            skipParagraphs.Add(lb);
            if (k + 1 < paras.Count && IsReturnPoint(paras[k + 1].Name)) skipParagraphs.Add(paras[k + 1].Name);
        }
        Flush();
        samReceiver = recv;
        Block(paras[pi].Statements);
        if (pi + 1 < paras.Count && IsReturnPoint(paras[pi + 1].Name)) Block(paras[pi + 1].Statements);
        samReceiver = null;
        Flush();
        i = list.Count - 1; // the statements after the chain handle "no closure matched": unreachable now
        return true;
    }

    bool IsReturnPoint(string para) => Fn.Component is not null && para.StartsWith(Fn.Component + "-R", StringComparison.OrdinalIgnoreCase);

    /// <summary>The functional interface method a lambda implements, from its shape.</summary>
    static string SamName(FunctionDef lambda) => (lambda.Params.Count, lambda.Ret?.Kind) switch
    {
        (0, null) => "run",
        (0, _) => "get",
        (1, null) => "accept",
        (1, VKind.Bool) => "test",
        (1, _) => "apply",
        (2, null) => "accept",
        (2, VKind.Int) => "compare",
        (2, _) => "apply",
        _ => "apply",
    };

    [GeneratedRegex(@"^(F\d{4}-SP|JR-[A-Z]+)$", RegexOptions.IgnoreCase)] private static partial Regex StackRegRx();

    /// <summary>Recursion bookkeeping (frame stack pointers, return-point stack): Java's call stack does it.</summary>
    static bool StackBookkeeping(Stmt s)
    {
        if (s.Verb is not ("ADD" or "SUBTRACT" or "MOVE" or "COMPUTE")) return false;
        var d = s.L?.GetValueOrDefault("dst") ?? s.L?.GetValueOrDefault("giving");
        return d is [{ V: var n }] && StackRegRx().IsMatch(n!);
    }

    void Statement(Stmt s)
    {
        if (s.Verb == "MOVE" && s.Get("src") is { K: "num" } jsn && int.TryParse(jsn.V, out var jsv) && Model.DstName(s)?.Equals("JR-SEL", StringComparison.OrdinalIgnoreCase) == true) { jrSel = jsv; return; }
        if (StackBookkeeping(s)) return;
        switch (s.Verb)
        {
            case "MOVE": Move(s); break;
            case "IF": If(s); break;
            case "GO TO": GoTo(s); break;
            case "PERFORM": Perform(s); break;
            case "CALL": Call(s); break;
            case "EVALUATE": Evaluate(s); break;
            case "COMPUTE": Compute(s); break;
            case "ADD": case "SUBTRACT": case "MULTIPLY": Arith(s); break;
            case "DIVIDE": Divide(s); break;
            case "EXIT": case "CONTINUE": case "GOBACK": case "DISPLAY": break;
            case "INITIALIZE": Initialize(s); break;
            case "STRING": StringStmt(s); break;
            case "SET": Residue("SET"); break;
            default: Residue("verb " + s.Verb); break;
        }
    }

    // ================================================================== MOVE

    /// <summary>The member of a recursive component the next PERFORM Sn-RUN runs (MOVE k TO JR-SEL).</summary>
    int? jrSel;

    void Move(Stmt s)
    {
        var src = s.Get("src");
        var dsts = s.L?.GetValueOrDefault("dst") ?? [];
        if (src is null) return;
        foreach (var d in dsts)
        {
            // MOVE JB-R-U TO JO-AMTU, MOVE JO-AMT TO field: a decimal of the job's scale into a record field
            if (d.V?.Equals("JO-AMTU", StringComparison.OrdinalIgnoreCase) == true)
            {
                var amt = Operand(src);
                SetReg("JO-AMT", amt is ECall { Name: "unscaledValue", Recv: { } whole } ? whole : amt);
                continue;
            }
            if (d.K != "ref") { Residue("move to " + d.K); continue; }
            var dloc = Resolve(d);
            var dit = P.ItemsById.GetValueOrDefault(d.Id ?? -1);
            var dpart = dit is null ? "" : PartOf(dloc, dit);
            // JS-BYTES (1:n) and record fields with reference modification: string registers
            if (d.Start is not null && dloc is LReg { Name: "JS-BYTES" })
            {
                var piece = Operand(src);
                if (d.Start is { K: "num", V: "1" }) { SymWrite(Key(dloc), "", piece); continue; }
                // a long literal built in pieces: JS-BYTES (k:n) after the first piece
                var prev = SymReadOr(Key(dloc), ELit.Str(""));
                if (prev is ELit { Value: string ps } && piece is ELit { Value: string pc } && d.Start is { K: "num" } st && int.TryParse(st.V, out var at))
                    SymWrite(Key(dloc), "", ELit.Str(ps.PadRight(at - 1)[..(at - 1)] + pc));
                else SymWrite(Key(dloc), "", new EBinary("+", prev, piece) { Type = JType.String });
                continue;
            }
            var v = Operand(src, KindOfLoc(dloc), dpart);
            Write(dloc, dpart, v);
        }
    }

    void RegisterWritten(string reg, string part, Expr value)
    {
        if (reg == "JX-AT")
        {
            if (value is ELit { Value: string t } && SiteRx().IsMatch(t.Trim())) site = t.Trim();
            else if (value is ELit { Value: string sp } && sp.Trim().Length == 0) { }
        }
    }

    // ================================================================== operands and conditions

    /// <summary>An operand; <paramref name="target"/> is the kind it is stored into (for literals 'Y'/'N' into booleans).</summary>
    Expr Operand(Node n, VKind target = VKind.Unknown, string targetPart = "")
    {
        switch (n.K)
        {
            case "ref":
            {
                var loc = Resolve(n);
                var it = P.ItemsById.GetValueOrDefault(n.Id ?? -1);
                var part = it is null ? "" : PartOf(loc, it);
                var v = Read(loc, part, n);
                if (n.Start is not null || n.Len is not null) v = Substring(v, n);
                return v;
            }
            case "num":
            {
                var t = n.V!;
                if (long.TryParse(t, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var l)) return ELit.Int(l);
                return new ELit(decimal.Parse(t, CultureInfo.InvariantCulture), JType.Double) { Type = JType.Double };
            }
            case "str":
            {
                if (target == VKind.Bool && targetPart.Length == 0 && n.V is "Y" or "N") return ELit.Bool(n.V == "Y");
                if (targetPart is "P.V" or "V" && n.V is "Y" or "N") return ELit.Bool(n.V == "Y");
                return ELit.Str(n.V!);
            }
            case "fig":
                return n.V switch
                {
                    "SPACES" or "SPACE" => ELit.Str(" "),
                    "ZERO" or "ZEROS" or "ZEROES" => ELit.Int(0),
                    "HIGH-VALUES" or "HIGH-VALUE" => ELit.Str("￿"),
                    "LOW-VALUES" or "LOW-VALUE" => ELit.Str("\0"),
                    _ => new EOpaque("fig " + n.V),
                };
            case "bin":
            {
                var l = Operand(n.L!);
                var r = Operand(n.R!);
                return n.Op switch
                {
                    "+" => Arith("+", l, r),
                    "-" => Arith("-", l, r),
                    "*" => Arith("*", l, r),
                    "/" => new EBinary("/", l, r),
                    "**" => JdkStatic("java.lang.Math", "pow", JType.Double, l, r),
                    _ => new EOpaque("op " + n.Op),
                };
            }
            case "neg":
                return Arith("-", ELit.Int(0), Operand(n.L!));
            default:
                Residue("operand " + n.K);
                return new EOpaque("operand " + n.K);
        }
    }

    static Expr Arith(string op, Expr l, Expr r)
    {
        if (l is ELit { Value: long a } && r is ELit { Value: long b })
            return op switch { "+" => ELit.Int(a + b), "-" => ELit.Int(a - b), "*" => ELit.Int(a * b), _ => new EBinary(op, l, r) };
        return new EBinary(op, l, r);
    }

    Expr Substring(Expr v, Node n)
    {
        // a record field read with its reference modification already is that part
        if (v is ERecordField { RefStart: > 0 }) return v;
        var start = n.Start is null ? ELit.Int(1) : Operand(n.Start);
        if (n.Len is null) return Jdk(v, "substring", JType.String, Arith("-", start, ELit.Int(1)));
        var len = Operand(n.Len);
        return Jdk(v, "substring", JType.String, Arith("-", start, ELit.Int(1)), Arith("+", Arith("-", start, ELit.Int(1)), len));
    }

    Expr Cond(Node n)
    {
        switch (n.K)
        {
            case "and": return And(Cond(n.L!), Cond(n.R!));
            case "or": return Or(Cond(n.L!), Cond(n.R!));
            case "not": return Not(Cond(n.L!));
            case "bool": return ELit.Bool(n.V == "TRUE");
            case "rel": return Rel(n.Op!, n.L!, n.R!);
            case "c88": return Cond88(n);
            default:
                Residue("condition " + n.K);
                return new EOpaque("cond " + n.K);
        }
    }

    /// <summary>The part path and slot kind a reference operand designates ("" and Unknown for other operands).</summary>
    (string Part, VKind Kind) PartInfo(Node n)
    {
        if (n.K != "ref") return ("", VKind.Unknown);
        var it = P.ItemsById.GetValueOrDefault(n.Id ?? -1);
        if (it is null) return ("", VKind.Unknown);
        var loc = Resolve(n);
        return (PartOf(loc, it), KindOfLoc(loc));
    }

    static bool IsPayload(string part) => part is "P" or "D" or "P.D" or "V" or "P.V" or "O" or "P.O" or "P.U" or "P.S" or "P.Z" or "U" or "S" or "Z";

    Expr Rel(string op, Node ln, Node rn)
    {
        var l = Operand(ln);
        var r = Operand(rn);
        var jop = op switch { "eq" => "==", "ne" => "!=", "lt" => "<", "le" => "<=", "gt" => ">", "ge" => ">=", _ => op };
        var (lp, lk) = PartInfo(ln);
        var (rp, rk) = PartInfo(rn);
        // two payloads of value objects: Java compares values with equals (scale-sensitive for BigDecimal, as J3S)
        if (IsPayload(lp) && IsPayload(rp) && jop is "==" or "!=")
        {
            Expr eq;
            if (lp is "O" or "P.O" || lk == VKind.JEnum) eq = new EBinary("==", l, r) { Type = JType.Bool };
            else if (lp is "P.U" or "U") eq = Jdk(l, "equals", JType.Bool, r);
            else if (lp is "P.Z" or "Z") eq = ELit.Bool(true);
            else if (lp is "P.S" or "S") eq = new EBinary("==", l, r) { Type = JType.Bool };
            else if (lk is VKind.BoxInt or VKind.BoxLong or VKind.BoxBool && rk is VKind.BoxInt or VKind.BoxLong or VKind.BoxBool) eq = Jdk(l, "equals", JType.Bool, r);
            else eq = Jdk(l, "equals", JType.Bool, r);
            return jop == "==" ? eq : Not(eq);
        }
        // two dates compared by day number
        if ((lp is "D" or "P.D" || lk == VKind.Date && lp is "P") && (rp is "D" or "P.D" || rk == VKind.Date && rp is "P") && jop is "<" or ">" or "<=" or ">=")
            return jop switch
            {
                ">" => Jdk(l, "isAfter", JType.Bool, r),
                "<" => Jdk(l, "isBefore", JType.Bool, r),
                ">=" => Not(Jdk(l, "isBefore", JType.Bool, r)),
                _ => Not(Jdk(l, "isAfter", JType.Bool, r)),
            };
        // an enum ordinal against a number: ordinal()
        if ((lp is "O" or "P.O") && r is ELit { Value: long }) return new EBinary(jop, Jdk(l, "ordinal", JType.Int), r) { Type = JType.Bool };
        // the class id part: null tests and class tests
        if (l is EPart { Part: "C" } lc) return ClassTest(lc.Whole, jop, r);
        if (r is EPart { Part: "C" } rc) return ClassTest(rc.Whole, Flip(jop), l);
        // booleans held as 'Y'/'N'
        if (r is ELit { Value: "Y" or "N" } yn && IsBoolish(l)) return (yn.Value as string == "Y") == (jop == "==") ? l : Not(l);
        if (l is ELit { Value: "Y" or "N" } yn2 && IsBoolish(r)) return (yn2.Value as string == "Y") == (jop == "==") ? r : Not(r);
        if (r is ELit { Value: bool rb }) return rb == (jop == "==") ? l : Not(l);
        return new EBinary(jop, l, r) { Type = JType.Bool };
    }

    bool IsBoolish(Expr e) => e switch
    {
        EVar v => v.V.Kind is VKind.Bool or VKind.BoxBool or VKind.Unknown && !(v.V.Type?.Name == "java.lang.String"),
        EField f => f.F.Kind is VKind.Bool or VKind.BoxBool,
        EStatic s => s.F.Kind == VKind.Bool,
        ECall c => c.Type is null || c.Type.Name == "boolean",
        EOpaque => true,
        ERecordField => false,
        _ => e.Type is null || e.Type.Name == "boolean",
    };

    static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    Expr ClassTest(Expr whole, string op, Expr other)
    {
        if (other is ELit { Value: 0L })
            return new EBinary(op == "==" ? "==" : "!=", whole, ELit.Null) { Type = JType.Bool };
        if (other is ELit { Value: long id })
        {
            var t = TypeOfClassId((int)id);
            Expr test = t is null ? new EOpaque("class " + id) : new EInstanceOf(whole, t) { Type = JType.Bool };
            return op == "==" ? test : Not(test);
        }
        if (other is EPart { Part: "C" } oc)
            return new EBinary(op == "==" ? "==C" : "!=C", whole, oc.Whole) { Type = JType.Bool };
        Residue("class id compared with " + IrText.Show(other));
        return new EOpaque("classtest");
    }

    public JType? TypeOfClassId(int id)
    {
        if (JType.ValueClass(id) is { } vt) return vt;
        if (M.ClassById.TryGetValue(id, out var c)) return JType.Of(c);
        if (M.ExceptionNames.TryGetValue(id, out var n)) return JType.Of(ExceptionClass(n));
        if (M.ArrayIds.TryGetValue(id, out var at)) return at;
        return null;
    }

    Expr Cond88(Node n)
    {
        var it = P.ItemsById.GetValueOrDefault(n.Id ?? -1);
        if (it?.Parent is null) { Residue("88 " + n.V); return new EOpaque("88"); }
        var parentRef = new Node { K = "ref", V = it.Parent.Name, Id = it.Parent.Id, Args = n.Args };
        Expr? acc = null;
        foreach (var (lo, hi) in it.Values)
        {
            var lit = lo.Quoted ? new Node { K = "str", V = lo.Text } : new Node { K = "num", V = lo.Text };
            Expr t = hi is null ? Rel("eq", parentRef, lit) : And(Rel("ge", parentRef, lit), Rel("le", parentRef, hi.Quoted ? new Node { K = "str", V = hi.Text } : new Node { K = "num", V = hi.Text }));
            acc = acc is null ? t : Or(acc, t);
        }
        return acc ?? ELit.Bool(false);
    }

    public static Expr Not(Expr e) => e switch
    {
        EUnary { Op: "!" } u => u.E,
        ELit { Value: bool b } => ELit.Bool(!b),
        EBinary { Op: "==" } b => new EBinary("!=", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: "!=" } b => new EBinary("==", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: "==C" } b => new EBinary("!=C", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: "!=C" } b => new EBinary("==C", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: "<" } b => new EBinary(">=", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: ">=" } b => new EBinary("<", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: ">" } b => new EBinary("<=", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: "<=" } b => new EBinary(">", b.L, b.R) { Type = JType.Bool },
        EBinary { Op: "&&" } b => Or(Not(b.L), Not(b.R)),
        EBinary { Op: "||" } b => And(Not(b.L), Not(b.R)),
        _ => new EUnary("!", e) { Type = JType.Bool },
    };

    /// <summary>The chain of op (a || b || c) already has the term t (J3S tests each class id of a type: one instanceof).</summary>
    static bool HasTerm(Expr chain, string op, Expr t)
    {
        if (t is not (EInstanceOf or EUnary { E: EInstanceOf })) return false;
        if (Same(chain, t)) return true;
        return chain is EBinary b && b.Op == op && (HasTerm(b.L, op, t) || HasTerm(b.R, op, t));
    }

    public static Expr And(Expr a, Expr b)
    {
        if (a is ELit { Value: true }) return b;
        if (b is ELit { Value: true }) return a;
        if (HasTerm(a, "&&", b)) return a;
        // x ==C y && x#I == y#I  →  x == y (reference equality: J3S compares class id and slot)
        if (a is EBinary { Op: "==C" } ca && b is EBinary { Op: "==", L: EPart { Part: "I" } li, R: EPart { Part: "I" } ri } && Same(li.Whole, ca.L) && Same(ri.Whole, ca.R))
            return new EBinary("==", ca.L, ca.R) { Type = JType.Bool };
        // same class && x.equals(y)  →  x.equals(y) (equals checks the class)
        if (a is EBinary { Op: "==C" } cb && b is ECall { Name: "equals", Recv: { } er, Args: [var ea] } && (Same(er, cb.L) && Same(ea, cb.R) || Same(er, cb.R) && Same(ea, cb.L)))
            return b;
        // u.unscaledValue().equals(v.unscaledValue()) && u.scale() == v.scale()  →  u.equals(v)
        if (a is ECall { Name: "equals", Recv: ECall { Name: "unscaledValue", Recv: { } ua }, Args: [ECall { Name: "unscaledValue", Recv: { } va }] }
            && b is EBinary { Op: "==", L: ECall { Name: "scale", Recv: { } us }, R: ECall { Name: "scale", Recv: { } vs } } && Same(ua, us) && Same(va, vs))
            return Jdk(ua, "equals", JType.Bool, va);
        if (a is EBinary { Op: "&&" } inner && inner.R is ECall { Name: "equals" } && b is ELit { Value: true }) return a;
        return new EBinary("&&", a, b) { Type = JType.Bool };
    }

    public static Expr Or(Expr a, Expr b)
    {
        if (a is ELit { Value: false }) return b;
        if (b is ELit { Value: false }) return a;
        if (HasTerm(a, "||", b)) return a;
        // x !=C y || x#I != y#I  →  x != y
        if (a is EBinary { Op: "!=C" } ca && b is EBinary { Op: "!=", L: EPart { Part: "I" } li, R: EPart { Part: "I" } ri } && Same(li.Whole, ca.L) && Same(ri.Whole, ca.R))
            return new EBinary("!=", ca.L, ca.R) { Type = JType.Bool };
        return new EBinary("||", a, b) { Type = JType.Bool };
    }

    // ================================================================== IF and guards

    static readonly HashSet<string> GuardParagraphs = new(StringComparer.OrdinalIgnoreCase)
    {
        "JX-NPE", "JX-INTERNAL", "JX-DEAD", "JX-PROP", "JB-ERR", "JX-SIZE", "JX-DEPTH", "JX-JOBFAIL", "JX-INEXACT", "JX-UNSUPPORTED",
    };

    /// <summary>Java exception class ids whose J3S checks Java performs by itself (cast, index, arithmetic, dates, iterators).</summary>
    /// <summary>Exceptions the JDK raises by itself where J3S checks and raises them explicitly (by name: ids differ per estate).</summary>
    static readonly HashSet<string> NaturalNames = new(StringComparer.Ordinal)
    {
        "ArrayIndexOutOfBoundsException", "ClassCastException", "DateTimeException", "ArithmeticException", "NegativeArraySizeException",
        "NullPointerException", "IndexOutOfBoundsException", "ConcurrentModificationException", "NoSuchElementException", "StringIndexOutOfBoundsException",
    };

    bool Natural(int id) => M.ExceptionNames.TryGetValue(id, out var n) ? NaturalNames.Contains(n) : id is 103 or 104 or 105 or 106 or 108 or 110 or 111 or 112 or 113 or 114;

    /// <summary>
    /// A guard: an IF whose only effect is to stop on a condition Java checks by itself. Returns the site the guard names.
    /// </summary>
    bool IsGuard(Stmt s, out string? guardSite)
    {
        guardSite = null;
        if (s.Blocks?.GetValueOrDefault("FALSE") is { Count: > 0 }) return false;
        var body = s.Blocks?.GetValueOrDefault("TRUE");
        if (body is null || body.Count == 0) return false;
        // IF FO-STATUS = 'R' GO TO Fnnnn-REJECT after a job function's output function: the exception it threw propagates
        if (Fn.IsJob && s.Get("cond") is { K: "rel", Op: "eq", L: { K: "ref" } fs, R: { K: "str", V: "R" } } && fs.V!.Equals("FO-STATUS", StringComparison.OrdinalIgnoreCase)
            && body is [{ Verb: "GO TO" } jg] && jg.L?["targets"] is [{ V: var jt }] && jt!.EndsWith("-REJECT", StringComparison.OrdinalIgnoreCase))
            return true;
        int? raised = null;
        bool stops = false;
        // IF JB-RC NOT = 0 ... MOVE n TO JX-ABEND-CODE, PERFORM JX-ABEND: the BigDecimal service failed; Java's own call cannot
        bool jbAbend = s.Get("cond") is { K: "rel", L: { K: "ref" } jr } && jr.V!.Equals("JB-RC", StringComparison.OrdinalIgnoreCase);
        foreach (var b in Model.Deep(body))
        {
            switch (b.Verb)
            {
                case "MOVE":
                {
                    var dn = Model.DstName(b)?.ToUpperInvariant();
                    if (dn == "JX-AT") { if (b.Get("src") is { K: "str" } st && SiteRx().IsMatch(st.V!.Trim())) guardSite ??= st.V!.Trim(); continue; }
                    if (dn == "JX-C" && b.Get("src") is { K: "num" } cn) { raised = int.Parse(cn.V!); continue; }
                    if (jbAbend && dn == "JX-ABEND-CODE") continue;
                    return false;
                }
                case "PERFORM":
                {
                    var t = s.Line >= 0 ? b.Str("target") ?? "" : "";
                    if (GuardParagraphs.Contains(t) || jbAbend && t.Equals("JX-ABEND", StringComparison.OrdinalIgnoreCase)) { stops = true; continue; }
                    if (t.Equals("JX-RAISE", StringComparison.OrdinalIgnoreCase) && raised is int r && Natural(r)) { stops = true; continue; }
                    return false;
                }
                case "GO TO":
                    continue;
                case "IF":
                    continue; // nested JB-RC = 1 test inside the JB guard
                default:
                    return false;
            }
        }
        if (stops) return true;
        // IF JX-C NOT = 0 GO TO exit: exception propagation after a call
        var c = s.Get("cond");
        if (c is { K: "rel", L: { K: "ref" } l } && l.V!.Equals("JX-C", StringComparison.OrdinalIgnoreCase) && body.All(b => b.Verb is "GO TO" or "MOVE" or "PERFORM"))
            return true;
        return false;
    }

    void If(Stmt s)
    {
        // IF JW-L > 2147483647 OR JW-L < -2147483648 PERFORM JW-WRAP: Java int arithmetic wraps by itself
        if (s.Blocks?.GetValueOrDefault("TRUE") is [{ Verb: "PERFORM" } wp] && wp.Str("target")?.Equals("JW-WRAP", StringComparison.OrdinalIgnoreCase) == true
            && s.Blocks?.GetValueOrDefault("FALSE") is null or { Count: 0 })
        {
            Runtime("JW-WRAP", wp);
            return;
        }
        // MOVE x TO JW-L; COMPUTE JW-DR = x - JW-L; IF JW-DR < 0 SUBTRACT 1 FROM JW-L: Math.floor(x)
        if (s.Get("cond") is { K: "rel", L: { K: "ref" } fl, R: { K: "num", V: "0" } } && fl.V!.Equals("JW-DR", StringComparison.OrdinalIgnoreCase)
            && s.Blocks?.GetValueOrDefault("TRUE") is [{ Verb: "SUBTRACT" } fsub] && (fsub.L?.GetValueOrDefault("dst") is [{ V: var fdv }] && fdv!.Equals("JW-L", StringComparison.OrdinalIgnoreCase))
            && s.Blocks?.GetValueOrDefault("FALSE") is null or { Count: 0 }
            && SymReadOr("R#JW-DR", ELit.Null) is EBinary { Op: "-", L: var fx })
        {
            SetReg("JW-L", JdkStatic("java.lang.Math", "floor", JType.Double, fx));
            return;
        }
        // IF v > K OR v < -K: MOVE v TO JW-L, PERFORM JW-WRAP, MOVE JW-L TO v. J3S keeps an int in a wide field and wraps it
        // only when the next operation could overflow; Java int arithmetic wraps at every step, to the same result
        if (s.Blocks?.GetValueOrDefault("TRUE") is [{ Verb: "MOVE" } wm1, { Verb: "PERFORM" } wp1, { Verb: "MOVE" } wm2]
            && wp1.Str("target")?.Equals("JW-WRAP", StringComparison.OrdinalIgnoreCase) == true
            && Model.DstName(wm1)?.Equals("JW-L", StringComparison.OrdinalIgnoreCase) == true
            && wm2.Get("src") is { K: "ref" } wsrc && wsrc.V!.Equals("JW-L", StringComparison.OrdinalIgnoreCase)
            && s.Blocks?.GetValueOrDefault("FALSE") is null or { Count: 0 }
            && wm2.L?.GetValueOrDefault("dst") is [{ } wdst])
        {
            if (Resolve(wdst) is LVar wv) { wv.V.Kind = VKind.Int; wv.V.Type = JType.Int; }
            return;
        }
        if (IsGuard(s, out var gs))
        {
            if (gs is not null) site = gs;
            // IF JX-C NOT = 0 ... JX-PROP after a CALL names the call's own site: the exception it propagates was raised there
            if (gs is not null && Model.Deep(s.Blocks?.GetValueOrDefault("TRUE") ?? []).Any(b => b.Verb == "PERFORM" && b.Str("target")?.Equals("JX-PROP", StringComparison.OrdinalIgnoreCase) == true))
                for (int k = Low.Count - 1; k >= 0; k--)
                {
                    if (Low[k] is LLabel or LGoto or LCondGoto) break;
                    if (Low[k] is SAssign { Rhs: ECall or ENew } or SExpr { E: ECall or ENew }) { Low[k].Site = gs; break; }
                }
            // a dropped guard still reads nothing; but a handler target (job functions) is a try region
            NoteHandler(s);
            return;
        }
        var cond = Cond(s.Get("cond")!);
        var then = s.Blocks?.GetValueOrDefault("TRUE") ?? [];
        var els = s.Blocks?.GetValueOrDefault("FALSE") ?? [];
        Flush();
        var lElse = NewLabel("else");
        var lEnd = NewLabel("endif");
        Emit(new LCondGoto(Not(cond), els.Count > 0 ? lElse : lEnd));
        var before = Snapshot();
        int mark = Low.Count;
        Block(then);
        Flush();
        if (els.Count > 0)
        {
            var afterThen = Snapshot();
            Emit(new LGoto(lEnd));
            Emit(new LLabel(lElse));
            Restore(before);
            Block(els);
            Flush();
            MergeConditional(cond, afterThen, mark);
        }
        else MergeConditional(cond, null, mark, before);
        Emit(new LLabel(lEnd));
    }

    Dictionary<string, Dictionary<string, Expr>> Snapshot() =>
        sym.ToDictionary(kv => kv.Key, kv => new Dictionary<string, Expr>(kv.Value), StringComparer.Ordinal);

    void Restore(Dictionary<string, Dictionary<string, Expr>> snap) => sym = snap.ToDictionary(kv => kv.Key, kv => new Dictionary<string, Expr>(kv.Value), StringComparer.Ordinal);

    /// <summary>
    /// After an if: a register that holds different values in the two branches holds (cond ? a : b), when the branches
    /// assign nothing the condition reads (J3S's String.format writes "null" or the date into JS-BYTES this way).
    /// </summary>
    void MergeConditional(Expr cond, Dictionary<string, Dictionary<string, Expr>>? thenState, int mark, Dictionary<string, Dictionary<string, Expr>>? before = null)
    {
        var elseState = Snapshot();
        var thenS = thenState ?? elseState;
        var elsS = thenState is null ? before! : elseState;
        if (thenState is null) { thenS = Snapshot(); }
        bool stable = !Low.Skip(mark).OfType<SAssign>().Any(a => a.Lhs is EVar v && Fold.Mentions(cond, v.V));
        var merged = new Dictionary<string, Dictionary<string, Expr>>(StringComparer.Ordinal);
        foreach (var (key, tparts) in thenS)
        {
            if (!elsS.TryGetValue(key, out var eparts)) continue;
            var parts = new Dictionary<string, Expr>();
            foreach (var (p, tv) in tparts)
            {
                if (!eparts.TryGetValue(p, out var ev)) continue;
                if (Same(tv, ev)) parts[p] = tv;
                else if (stable && key.StartsWith("R#JS-", StringComparison.Ordinal)) parts[p] = FactorConcat(cond, tv, ev);
            }
            if (parts.Count > 0) merged[key] = parts;
        }
        sym = merged;
    }

    void Merge(Dictionary<string, Dictionary<string, Expr>> other)
    {
        foreach (var key in sym.Keys.ToList())
        {
            if (!other.TryGetValue(key, out var op)) { sym.Remove(key); continue; }
            var mine = sym[key];
            foreach (var part in mine.Keys.ToList())
                if (!op.TryGetValue(part, out var ov) || !Same(ov, mine[part])) mine.Remove(part);
            if (mine.Count == 0) sym.Remove(key);
        }
    }

    // ================================================================== GO TO

    void GoTo(Stmt s)
    {
        var targets = s.L?.GetValueOrDefault("targets") ?? [];
        if (s.Get("depending") is { } dep)
        {
            Flush();
            var sw = new LSwitchGoto(Operand(dep));
            for (int k = 0; k < targets.Count; k++) sw.Cases.Add(([ELit.Int(k + 1)], targets[k].V!));
            var fall = NewLabel("dep");
            sw.Default = fall;
            Emit(sw);
            Emit(new LLabel(fall));
            return;
        }
        if (targets is not [{ V: var t }]) { Residue("GO TO without one target"); return; }
        // recursive call protocol: GO TO the head of another function of the component
        if (M.FunctionOfParagraph(t!) is { } callee && callee.Paragraph.Equals(t, StringComparison.OrdinalIgnoreCase) && callee.Component is not null
            && !(ReferenceEquals(callee, Fn) && false))
        {
            if (!(ReferenceEquals(callee, Fn) && !t!.Equals(Fn.Paragraph, StringComparison.OrdinalIgnoreCase)))
            {
                InvokeLocal(callee);
                return;
            }
        }
        Flush();
        if (t!.Equals(Fn.ExitParagraph, StringComparison.OrdinalIgnoreCase)) { Emit(new LGoto(t)); return; }
        if (t.EndsWith("-RET", StringComparison.OrdinalIgnoreCase) && Fn.Component is not null && t.StartsWith(Fn.Component + "-", StringComparison.OrdinalIgnoreCase))
        {
            Emit(new LGoto(Fn.ExitParagraph));
            return;
        }
        if (handlerLabels.Contains(t)) { Emit(new LGoto(t)); return; }
        Emit(new LGoto(t));
    }

    readonly HashSet<string> handlerLabels = new(StringComparer.OrdinalIgnoreCase);

    void NoteHandler(Stmt guard)
    {
        foreach (var b in Model.Deep(guard.Blocks?.GetValueOrDefault("TRUE") ?? []))
            if (b.Verb == "GO TO" && b.L?["targets"] is [{ V: var t }] && !t!.Equals(Fn.ExitParagraph, StringComparison.OrdinalIgnoreCase))
                handlerLabels.Add(t);
    }

    // ================================================================== EVALUATE

    void Evaluate(Stmt s)
    {
        var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
        // virtual dispatch: EVALUATE x-C, every branch the same call on x
        if (s.L?["subjects"] is [{ K: "ref" } subj] && TryDispatch(s, subj, whens)) return;
        Flush();
        var lEnd = NewLabel("endeval");
        var before = Snapshot();
        Dictionary<string, Dictionary<string, Expr>>? merged = null;
        foreach (var w in whens)
        {
            var body = w.Blocks?.GetValueOrDefault("TRUE") ?? [];
            bool other = w.Str("other") == "true";
            Restore(before);
            var lNext = NewLabel("when");
            if (!other)
            {
                var c = Cond(w.Get("cond")!);
                Emit(new LCondGoto(Not(c), lNext));
            }
            if (other && body.All(b => b.Verb == "PERFORM" && GuardParagraphs.Contains(b.Str("target") ?? "") || b.Verb == "MOVE" && Model.DstName(b)?.Equals("JX-AT", StringComparison.OrdinalIgnoreCase) == true))
                Emit(new SThrow(new ENew { JdkClass = "java.lang.IllegalStateException", Args = [ELit.Str("unreachable")] }));
            else Block(body);
            Flush();
            if (merged is null) merged = Snapshot(); else { var cur = Snapshot(); Restore(merged); Merge(cur); merged = Snapshot(); }
            Emit(new LGoto(lEnd));
            Emit(new LLabel(lNext));
            if (other) break;
        }
        if (merged is not null) Restore(merged);
        Emit(new LLabel(lEnd));
    }

    // ================================================================== arithmetic

    void Compute(Stmt s)
    {
        var e = Operand(s.Get("expr")!);
        foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
        {
            var loc = Resolve(d);
            var it = P.ItemsById.GetValueOrDefault(d.Id ?? -1);
            Write(loc, it is null ? "" : PartOf(loc, it), e);
        }
    }

    void Arith(Stmt s)
    {
        var ops = (s.L?.GetValueOrDefault("ops") ?? []).Select(o => Operand(o)).ToList();
        var form = s.Str("form") ?? "";
        var giving = s.L?.GetValueOrDefault("giving");
        var dsts = s.L?.GetValueOrDefault("dst") ?? [];
        string op = s.Verb switch { "ADD" => "+", "SUBTRACT" => "-", _ => "*" };
        if (ops.Count == 0) return;
        var sum = ops.Skip(1).Aggregate(ops[0], (a, b) => Arith(s.Verb == "MULTIPLY" ? "*" : "+", a, b));
        if (s.Verb == "MULTIPLY" && s.Get("by") is { } by) sum = Arith("*", sum, Operand(by));
        foreach (var d in giving ?? dsts)
        {
            var loc = Resolve(d);
            var it = P.ItemsById.GetValueOrDefault(d.Id ?? -1);
            var part = it is null ? "" : PartOf(loc, it);
            Expr v = giving is not null && s.Verb != "MULTIPLY" ? sum : Arith(op, Read(loc, part, d), sum);
            if (s.Verb == "MULTIPLY" && giving is null) v = Arith("*", Read(loc, part, d), sum);
            Write(loc, part, v);
        }
    }

    void Divide(Stmt s)
    {
        var ops = (s.L?.GetValueOrDefault("ops") ?? []).Select(o => Operand(o)).ToList();
        var by = s.Get("by") is { } b ? Operand(b) : null;
        var form = s.Str("form") ?? "";
        if (ops.Count != 1 || by is null) { Residue("DIVIDE form " + form); return; }
        // DIVIDE a BY b GIVING q [REMAINDER r]; DIVIDE a INTO b GIVING q
        Expr dividend = form.StartsWith("BY") || form == "GIVING" && false ? ops[0] : ops[0];
        Expr divisor = by;
        if (form is "INTO" or "INTO-GIVING") { dividend = by; divisor = ops[0]; }
        foreach (var g in s.L?.GetValueOrDefault("giving") ?? [])
        {
            var loc = Resolve(g);
            var it = P.ItemsById.GetValueOrDefault(g.Id ?? -1);
            // a quotient into a double is a floating division (Java would divide integers)
            var q = KindOfLoc(loc) == VKind.Dbl && divisor.Type?.Name is not "double"
                ? new EBinary("/", dividend, divisor is ELit { Value: long dl } ? new ELit((double)dl, JType.Double) : divisor is ELit { Value: int di } ? new ELit((double)di, JType.Double) : new ECast(JType.Double, divisor) { Type = JType.Double }) { Type = JType.Double }
                : new EBinary("/", dividend, divisor);
            Write(loc, it is null ? "" : PartOf(loc, it), q);
        }
        if (s.Get("rem") is { } rem)
        {
            var loc = Resolve(rem);
            var it = P.ItemsById.GetValueOrDefault(rem.Id ?? -1);
            Write(loc, it is null ? "" : PartOf(loc, it), new EBinary("%", dividend, divisor));
        }
    }

    void Initialize(Stmt s)
    {
        // INITIALIZE FS-CTX in job programs: the runtime context; nothing to do in Java
    }

    void StringStmt(Stmt s)
    {
        // STRING pieces DELIMITED BY SIZE INTO JS-BYTES WITH POINTER JS-PTR: concatenation into the string register
        var into = s.Get("into");
        if (into is null) { Residue("STRING without INTO"); return; }
        var loc = Resolve(into);
        // the string starts empty at pointer 1 (MOVE SPACES TO JS-BYTES; MOVE 1 TO JS-PTR)
        Expr acc = SymReadOr("R#JS-PTR", ELit.Null) is ELit { Value: 1L } ? ELit.Str("") : SymReadOr(Key(loc), ELit.Str(""));
        var pieces = (s.L?.GetValueOrDefault("pieces") ?? []).Select(p => p.L).Where(p => p is not null).Cast<Node>().ToList();
        // a date as yyyy-MM-dd: D (1:4) '-' D (5:2) '-' D (7:2) (LocalDate.toString)
        if (pieces.Count == 5 && pieces[0] is { K: "ref", Start: { K: "num", V: "1" } } d0 && pieces[1] is { K: "str", V: "-" } && pieces[3] is { K: "str", V: "-" }
            && Operand(new Node { K = "ref", V = d0.V, Id = d0.Id, Args = d0.Args }) is { } dv && dv.Type?.Name == "java.time.LocalDate")
            pieces = [new Node { K = "date" }];
        foreach (var piece in pieces)
        {
            Expr v = piece.K == "date" ? Jdk(Operand(new Node { K = "ref", V = "DT-YYYYMMDD", Id = P.ItemsByName.GetValueOrDefault("DT-YYYYMMDD")?.Id }), "toString", JType.String) : Operand(piece);
            acc = acc is ELit { Value: "" } ? v : acc is ELit { Value: string a1 } && v is ELit { Value: string b1 } ? ELit.Str(a1 + b1) : JdkConcat(acc, v);
        }
        SymWrite(Key(loc), "", acc);
        SymWrite("R#JS-PTR", "", new EOpaque("ptr"));
    }

    Expr SymReadOr(string key, Expr dflt) => sym.TryGetValue(key, out var parts) && parts.TryGetValue("", out var v) ? v : dflt;

    static Expr JdkConcat(Expr a, Expr b) => new EBinary("+", a, b) { Type = JType.String };

    static List<Expr> Pieces(Expr e)
    {
        var res = new List<Expr>();
        void Walk(Expr x)
        {
            if (x is EBinary { Op: "+" } b && b.Type?.Name == "java.lang.String") { Walk(b.L); Walk(b.R); return; }
            if (x is ELit { Value: string s } && res.Count > 0 && res[^1] is ELit { Value: string p }) { res[^1] = ELit.Str(p + s); return; }
            res.Add(x);
        }
        Walk(e);
        return res;
    }

    static Expr ConcatOf(List<Expr> pieces)
    {
        if (pieces.Count == 0) return ELit.Str("");
        var first = pieces[0];
        // the first piece makes it a string concatenation: a value that is not a String is converted
        if (!(first is ELit { Value: string } || first.Type?.Name == "java.lang.String"))
            first = pieces.Count == 1 ? JdkStatic("java.lang.String", "valueOf", JType.String, first) : new EBinary("+", ELit.Str(""), first) { Type = JType.String };
        return pieces.Skip(1).Aggregate(first, JdkConcat);
    }

    /// <summary>
    /// cond ? a : b over two strings built from the same pieces (J3S's String.format writes each argument in a branch):
    /// only the pieces that differ are conditional, and x == null ? "null" : x.toString() is x itself in a concatenation.
    /// </summary>
    static Expr FactorConcat(Expr cond, Expr a, Expr b)
    {
        var pa = Pieces(a); var pb = Pieces(b);
        var prefix = new List<Expr>();
        int i = 0;
        while (i < pa.Count && i < pb.Count && Same(pa[i], pb[i])) prefix.Add(pa[i++]);
        var ra = pa.Skip(i).ToList(); var rb = pb.Skip(i).ToList();
        if (ra.Count > 0 && rb.Count > 0 && ra[0] is ELit { Value: string sa } && rb[0] is ELit { Value: string sb })
        {
            int k = 0;
            while (k < sa.Length && k < sb.Length && sa[k] == sb[k]) k++;
            if (k > 0)
            {
                prefix.Add(ELit.Str(sa[..k]));
                if (sa.Length > k) ra[0] = ELit.Str(sa[k..]); else ra.RemoveAt(0);
                if (sb.Length > k) rb[0] = ELit.Str(sb[k..]); else rb.RemoveAt(0);
            }
        }
        var suffix = new List<Expr>();
        while (ra.Count > 0 && rb.Count > 0 && Same(ra[^1], rb[^1])) { suffix.Insert(0, ra[^1]); ra.RemoveAt(ra.Count - 1); rb.RemoveAt(rb.Count - 1); }
        Expr ta = ConcatOf(ra), tb = ConcatOf(rb);
        Expr middle = NullText(cond, ta, tb) ?? new ECond(cond, ta, tb) { Type = JType.String };
        if (prefix.Count == 0 && suffix.Count == 0) return middle is ECond ? middle : ConcatOf([middle]);
        return ConcatOf([.. prefix, middle, .. suffix]);
    }

    /// <summary>x == null ? "null" : x.toString(): what a concatenation does with x.</summary>
    static Expr? NullText(Expr cond, Expr ifTrue, Expr ifFalse)
    {
        if (cond is EBinary { Op: "!=", R: ELit { Value: null } } ne) return NullText(new EBinary("==", ne.L, ne.R), ifFalse, ifTrue);
        if (cond is EBinary { Op: "==", L: var x, R: ELit { Value: null } } && ifTrue is ELit { Value: "null" }
            && ifFalse is ECall { Name: "toString", Recv: { } r, Args.Count: 0 } && Same(r, x))
            return x;
        return null;
    }
}
