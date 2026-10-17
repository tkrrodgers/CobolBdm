using System.Globalization;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>A plain field of a program class: a work field, a file, the instance of a called program.</summary>
public sealed class EProgField(string name, JType type) : Expr
{
    public string Name { get; } = name;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
    public JType FieldType { get; } = type;
}

/// <summary>Characters of a record image: the whole record (Offset null) or a slice (a group, a reference modification).</summary>
public sealed class ERecRaw(string instance, Expr? offset, int length) : Expr
{
    public string Instance { get; } = instance;
    public Expr? Offset { get; } = offset;
    public int Length { get; } = length;
    public override IEnumerable<Expr> Children => Offset is null ? [] : [Offset];
    public override Expr Map(Func<Expr, Expr> f) => new ERecRaw(Instance, Offset is null ? null : f(Offset), Length) { Type = Type };
}

/// <summary>
/// The classic lift: the paragraphs of a batch driver or of a job program's shell, structured COBOL with files and
/// records, into the structured IR the Java writer prints. Records are typed views of their images (record classes),
/// a program's own work fields are plain Java fields; the J3S runtime (heap, context, pools) has no Java counterpart.
/// </summary>
public sealed class ClassicLift
{
    readonly AppModel A;
    readonly AppProgram ap;
    readonly SourceProgram P;
    public readonly List<string> Residue = [];
    public readonly List<Var> Temps = [];
    /// <summary>Inside a job function's handler: the exception caught and its legacy site (JX-C, JX-SITE).</summary>
    public Var? Exception { get; set; }
    public Var? Site { get; set; }
    int temp;

    public ClassicLift(AppModel a, AppProgram p) { A = a; ap = p; P = p.P; }

    public List<St> Paragraph(Paragraph para) => Block(para.Statements);

    public List<St> Block(IEnumerable<Stmt> stmts)
    {
        var res = new List<St>();
        foreach (var s in stmts) Statement(s, res);
        return res;
    }

    void Statement(Stmt s, List<St> o)
    {
        int before = o.Count;
        switch (s.Verb)
        {
            case "MOVE": Move(s, o); break;
            case "IF": If(s, o); break;
            case "EVALUATE": Evaluate(s, o); break;
            case "PERFORM": Perform(s, o); break;
            case "READ": Read(s, o); break;
            case "WRITE": Write(s, o); break;
            case "OPEN": Open(s, o); break;
            case "CLOSE": Close(s, o); break;
            case "DISPLAY": Display(s, o); break;
            case "CALL": Call(s, o); break;
            case "ADD": case "SUBTRACT": case "MULTIPLY": Arith(s, o); break;
            case "COMPUTE": Compute(s, o); break;
            case "DIVIDE": Divide(s, o); break;
            case "GOBACK": case "STOP RUN": case "EXIT PROGRAM": o.Add(new SReturn(null)); break;
            case "GO TO" when EndOfFunction(s):
                // the end of a job function's paragraph range: its return
                o.Add(new SReturn(null));
                break;
            case "CONTINUE": case "EXIT": case "INITIALIZE": break;
            default: Res($"verb {s.Verb}", s); break;
        }
        for (int k = before; k < o.Count; k++) if (o[k].Line == 0) o[k].Line = s.Line;
    }

    static bool EndOfFunction(Stmt s) =>
        s.L?.GetValueOrDefault("targets") is [{ V: { } gt }] && System.Text.RegularExpressions.Regex.IsMatch(gt, @"^F\d+-(END|DONE)$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    void Res(string what, Stmt s) => Residue.Add($"{P.Id}:{s.Line} {what}");

    // ================================================================== data

    DataItem? Item(Node n) => P.ItemsById.GetValueOrDefault(n.Id ?? -1);

    bool IsNumeric(DataItem it) => it.Category == Category.Numeric;

    /// <summary>An item never written by the program whose value is fixed (texts in VALUE clauses).</summary>
    string? Constant(DataItem it)
    {
        if (writtenItems.Value.Contains(it) || Ancestors(it).Any(writtenItems.Value.Contains)) return null;
        var text = ValueText(it);
        return text;
    }

    static IEnumerable<DataItem> Ancestors(DataItem it) { for (var x = it.Parent; x is not null; x = x.Parent) yield return x; }

    static string? ValueText(DataItem it)
    {
        if (it.IsLeaf)
        {
            if (it.Values.Count != 1) return null;
            var v = it.Values[0].Low.Text;
            return Records.Pad(v, it.Length);
        }
        var sb = new System.Text.StringBuilder();
        foreach (var c in it.Children.Where(c => c.Level != 88))
        {
            var t = ValueText(c);
            if (t is null) return null;
            sb.Append(t);
        }
        return sb.ToString();
    }

    Lazy<HashSet<DataItem>> writtenItems => written ??= new Lazy<HashSet<DataItem>>(() =>
    {
        var w = new HashSet<DataItem>();
        foreach (var para in P.Model.Paragraphs)
            foreach (var s in Model.Deep(para.Statements))
            {
                foreach (var d in s.L?.GetValueOrDefault("dst") ?? []) if (Item(d) is { } it) w.Add(it);
                foreach (var d in s.L?.GetValueOrDefault("giving") ?? []) if (Item(d) is { } it) w.Add(it);
                if (s.Get("rem") is { } r && Item(r) is { } ri) w.Add(ri);
                if (s.Get("into") is { } i && Item(i) is { } ii) w.Add(ii);
            }
        return w;
    });
    Lazy<HashSet<DataItem>>? written;

    /// <summary>The offset of an item in its record, with OCCURS subscripts (1-based) applied.</summary>
    Expr OffsetOf(DataItem it, Node n)
    {
        Expr off = ELit.Int(it.Offset - it.Record.Offset);
        var subs = n.Args ?? [];
        for (int k = 0; k < subs.Count && k < it.Dims.Count; k++)
        {
            var idx = Num(subs[k]);
            off = Add(off, Mul(Sub(idx, ELit.Int(1)), ELit.Int(it.Dims[k].Stride)));
        }
        return off;
    }

    static Expr Add(Expr a, Expr b) => a is ELit { Value: long x } && b is ELit { Value: long y } ? ELit.Int(x + y) : b is ELit { Value: 0L } ? a : new EBinary("+", a, b) { Type = JType.Int };
    static Expr Sub(Expr a, Expr b) => a is ELit { Value: long x } && b is ELit { Value: long y } ? ELit.Int(x - y) : b is ELit { Value: 0L } ? a : new EBinary("-", a, b) { Type = JType.Int };
    static Expr Mul(Expr a, Expr b) => a is ELit { Value: long x } && b is ELit { Value: long y } ? ELit.Int(x * y) : b is ELit { Value: 1L } ? a : new EBinary("*", a, b) { Type = JType.Int };

    /// <summary>The value of a reference: a typed field, the characters of a group or of a reference modification.</summary>
    Expr Read(Node n)
    {
        var it = Item(n);
        if (it is null) { Res("unknown item " + n.V, new Stmt()); return new EOpaque(n.V ?? "?"); }
        if (it.Level == 88) return Cond88(it, n);
        if (Exception is not null && AppModel.IsRuntime(it)) return RuntimeRead(it, n);
        if (Constant(it) is { } c && (AppModel.IsRuntime(it) || !ap.Promoted.ContainsKey(it))) return n.Start is null ? ELit.Str(c.TrimEnd()) : ELit.Str(Slice(c, n).TrimEnd());
        if (ap.Promoted.TryGetValue(it, out var pf))
        {
            Expr v = new EProgField(pf.Name, pf.Type) { Type = pf.Type };
            // a Y/N flag read as a value (copied into a code): its letter
            if (pf.Type.Name == "boolean") return new ECond(v, ELit.Str("Y"), ELit.Str("N")) { Type = JType.String };
            if (n.Start is not null) return JdkStatic("java.lang.String", "valueOf", JType.String, RefMod(PadText(v, it), n));
            return v;
        }
        if (A.RecordOf(ap, it) is { } r)
        {
            if (n.Start is not null || !it.IsLeaf || it.IsFiller || !r.Rc.Accessors.ContainsKey(it.Name))
            {
                var off = OffsetOf(it, n);
                int len = it.Length;
                if (n.Start is not null) { off = Add(off, Sub(Num(n.Start), ELit.Int(1))); len = n.Len is { K: "num" } ln ? int.Parse(ln.V!, CultureInfo.InvariantCulture) : it.Length; }
                if (off is ELit { Value: 0L } && len == r.Rc.Length) return new ERecRaw(r.Instance, null, len) { Type = JType.String };
                return new ERecRaw(r.Instance, off, len) { Type = JType.String };
            }
            var idx = (n.Args ?? []).Count > 0 ? Num(n.Args![0]) : null;
            return new ERecordField(it) { Type = AppModel.FieldType(it), Index = idx };
        }
        Res("no storage for " + it.Name, new Stmt());
        return new EOpaque(it.Name);
    }

    static string Slice(string s, Node n)
    {
        int start = int.Parse(n.Start!.V!, CultureInfo.InvariantCulture) - 1;
        int len = n.Len is { K: "num" } l ? int.Parse(l.V!, CultureInfo.InvariantCulture) : s.Length - start;
        return s.Substring(start, Math.Min(len, s.Length - start));
    }

    /// <summary>The image of a promoted field (its characters, padded to the field's length).</summary>
    Expr PadText(Expr v, DataItem it) => IsNumeric(it)
        ? JdkStatic(A.PlatformPackage + ".Records", "pad", JType.String, Digits(v, it), ELit.Int(it.Length))
        : JdkStatic(A.PlatformPackage + ".Records", "pad", JType.String, v, ELit.Int(it.Length));

    Expr Digits(Expr v, DataItem it) => JdkStatic("java.lang.String", "format", JType.String, ELit.Str("%0" + it.Digits + "d"), v);

    Expr RefMod(Expr text, Node n)
    {
        var start = Sub(Num(n.Start!), ELit.Int(1));
        Expr end = n.Len is null ? Jdk(text, "length", JType.Int) : Add(start, Num(n.Len));
        return Jdk(text, "substring", JType.String, start, end);
    }

    /// <summary>JX-C and JX-SITE in a job function's handler: the exception's class and its legacy site.</summary>
    Expr RuntimeRead(DataItem it, Node n)
    {
        if (it.Name.Equals("JX-SITE", StringComparison.OrdinalIgnoreCase)) return new EVar(Site!) { Type = JType.String };
        if (it.Name.Equals("JO-NAME", StringComparison.OrdinalIgnoreCase))
            return Jdk(Jdk(new EVar(Exception!), "getClass", JType.Of("java.lang.Class")), "getSimpleName", JType.String);
        if (Constant(it) is { } c) return ELit.Str(c.TrimEnd());
        Res("runtime item read " + it.Name, new Stmt());
        return new EOpaque(it.Name);
    }

    Expr Num(Node n) => Operand(n, numeric: true);

    Expr Operand(Node n, bool numeric = false)
    {
        switch (n.K)
        {
            case "num":
            {
                var t = n.V!;
                if (t.Contains('.')) return new ELit(decimal.Parse(t, CultureInfo.InvariantCulture), JType.BigDecimal);
                var v = long.Parse(t, CultureInfo.InvariantCulture);
                return v is >= int.MinValue and <= int.MaxValue ? ELit.Int(v) : new ELit(v, JType.Long);
            }
            case "str": return ELit.Str(n.V ?? "");
            case "fig":
                return (n.V ?? "").ToUpperInvariant() switch
                {
                    "ZERO" or "ZEROS" or "ZEROES" => numeric ? ELit.Int(0) : ELit.Str("0"),
                    "SPACE" or "SPACES" => ELit.Str(""),
                    _ => ELit.Str(""),
                };
            case "ref": return Read(n);
            case "bin":
            {
                var l = Operand(n.L!, true);
                var r = Operand(n.R!, true);
                return Arith(n.Op!, l, r);
            }
            case "neg": return Arith("-", ELit.Int(0), Operand(n.L!, true));
        }
        Res("operand " + n.K, new Stmt());
        return new EOpaque(n.K);
    }

    static bool IsBig(Expr e) => e.Type?.Name == "java.math.BigDecimal" || e is ELit { Value: decimal };
    static bool IsLong(Expr e) => e.Type?.Name == "long" || e is ELit { Value: long v } && v is < int.MinValue or > int.MaxValue;

    static Expr Big(Expr e) => IsBig(e) ? (e is ELit { Value: decimal d } ? new ECall { Kind = CallKind.JdkStatic, Owner = "java.math.BigDecimal", Name = "valueOf", Args = [new ELit(d, JType.Double)], Type = JType.BigDecimal } : e)
        : new ECall { Kind = CallKind.JdkStatic, Owner = "java.math.BigDecimal", Name = "valueOf", Args = [e], Type = JType.BigDecimal };

    /// <summary>COBOL arithmetic: integers in Java long/int, decimals in BigDecimal.</summary>
    static Expr Arith(string op, Expr l, Expr r)
    {
        if (IsBig(l) || IsBig(r))
        {
            var name = op switch { "+" => "add", "-" => "subtract", "*" => "multiply", _ => "divide" };
            return new ECall { Kind = CallKind.Jdk, Name = name, Recv = Big(l), Args = [Big(r)], Type = JType.BigDecimal };
        }
        var t = IsLong(l) || IsLong(r) ? JType.Long : JType.Int;
        return new EBinary(op, l, r) { Type = t };
    }

    // ================================================================== conditions

    Expr Cond(Node c)
    {
        switch (c.K)
        {
            case "and" or "or" when YesNoValidation(c) is { } valid: return valid;
            case "and": return FunctionLifter.And(Cond(c.L!), Cond(c.R!));
            case "or": return FunctionLifter.Or(Cond(c.L!), Cond(c.R!));
            case "not": return FunctionLifter.Not(Cond(c.L!));
            case "bool": return ELit.Bool(c.V == "TRUE");
            case "any": return ELit.Bool(true);
            case "c88": case "ref" when Item(c) is { Level: 88 } c88: return Cond88(Item(c)!, c);
            case "class":
            {
                var v = Text(c.L!);
                Expr test = (c.V ?? c.Op ?? "").ToUpperInvariant() switch
                {
                    "NUMERIC" => JdkStatic(A.PlatformPackage + ".Records", "isNumeric", JType.Bool, v),
                    _ => Opaque("class test " + c.V),
                };
                return c.Op == "not" ? FunctionLifter.Not(test) : test;
            }
            case "rel": return Rel(c);
        }
        return Opaque("condition " + c.K);
    }

    Expr Opaque(string what) { Residue.Add($"{P.Id} {what}"); return new EOpaque(what); }

    /// <summary>x NOT = 'Y' AND x NOT = 'N' (x = 'Y' OR x = 'N'): the field's character is (not) a valid Y/N indicator.</summary>
    Expr? YesNoValidation(Node c)
    {
        static (Node? F, string? L) Side(Node? n) => n is { K: "rel", L: { K: "ref" } f, R: { K: "str", V: "Y" or "N" } l } ? (f, l.V) : (null, null);
        var (fa, la) = Side(c.L);
        var (fb, lb) = Side(c.R);
        string op = c.K == "and" ? "ne" : "eq";
        if (fa is null || fb is null || la == lb || c.L!.Op != op || c.R!.Op != op || fa.Start is not null || fb.Start is not null || Item(fa) is not { } ia || ia != Item(fb)) return null;
        Expr valid = JdkStatic(A.PlatformPackage + ".Records", "isYesNo", JType.Bool, Text(fa));
        return op == "ne" ? FunctionLifter.Not(valid) : valid;
    }

    /// <summary>A Y/N work flag compared with 'Y' or 'N': the boolean or its negation.</summary>
    Expr? FlagTest(Node c)
    {
        Node? f = null, l = null;
        if (c.L is { K: "ref" } && c.R is { K: "str", V: "Y" or "N" }) (f, l) = (c.L, c.R);
        else if (c.R is { K: "ref" } && c.L is { K: "str", V: "Y" or "N" }) (f, l) = (c.R, c.L);
        if (f is null || l is null || Item(f) is not { } it || !ap.Promoted.TryGetValue(it, out var pf) || pf.Type.Name != "boolean") return null;
        Expr field = new EProgField(pf.Name, JType.Bool) { Type = JType.Bool };
        Expr test = l.V == "Y" ? field : FunctionLifter.Not(field);
        return c.Op == "ne" ? FunctionLifter.Not(test) : test;
    }

    /// <summary>The characters of an operand (a class test reads the field's bytes).</summary>
    Expr Text(Node n)
    {
        var it = Item(n);
        if (it is not null && it.Level != 88 && A.RecordOf(ap, it) is { } r && n.Start is null && it.IsLeaf && r.Rc.Accessors.ContainsKey(it.Name))
            return new ERecRaw(r.Instance, OffsetOf(it, n), it.Length) { Type = JType.String };
        var v = Operand(n);
        if (it is not null && ap.Promoted.ContainsKey(it) && n.Start is null) return PadText(v, it);
        return v;
    }

    Expr Cond88(DataItem c88, Node n)
    {
        var parent = c88.Parent!;
        // an 88 of a Y/N work flag: the boolean
        if (ap.Promoted.TryGetValue(parent, out var pf) && pf.Type.Name == "boolean" && c88.Values is [(var yv, null)])
        {
            Expr field = new EProgField(pf.Name, JType.Bool) { Type = JType.Bool };
            return yv.Text == "Y" ? field : FunctionLifter.Not(field);
        }
        var pref = new Node { K = "ref", V = parent.Name, Id = parent.Id, Args = n.Args };
        Expr? acc = null;
        foreach (var (lo, hi) in c88.Values)
        {
            var lit = lo.Quoted ? new Node { K = "str", V = lo.Text } : new Node { K = "num", V = lo.Text };
            Expr t = hi is null ? Rel(new Node { K = "rel", Op = "eq", L = pref, R = lit })
                : FunctionLifter.And(Rel(new Node { K = "rel", Op = "ge", L = pref, R = lit }), Rel(new Node { K = "rel", Op = "le", L = pref, R = hi.Quoted ? new Node { K = "str", V = hi.Text } : new Node { K = "num", V = hi.Text } }));
            acc = acc is null ? t : FunctionLifter.Or(acc, t);
        }
        return acc ?? ELit.Bool(false);
    }

    bool NumericOperand(Node n) => n.K is "num" or "bin" or "neg" || n.K == "fig" && (n.V ?? "").StartsWith("ZERO", StringComparison.OrdinalIgnoreCase)
        || n.K == "ref" && Item(n) is { } it && it.Level != 88 && IsNumeric(it) && n.Start is null;

    Expr Rel(Node c)
    {
        var op = c.Op switch { "eq" => "==", "ne" => "!=", "lt" => "<", "le" => "<=", "gt" => ">", "ge" => ">=", _ => "==" };
        if (c.Op is "eq" or "ne" && FlagTest(c) is { } flag) return flag;
        // JX-C = id in a handler: the exception's class
        if (Exception is not null && c.L is { K: "ref" } jl && Item(jl) is { } jit && jit.Name.Equals("JX-C", StringComparison.OrdinalIgnoreCase) && c.R is { K: "num" } idn)
        {
            var t = A.M.ClassIdType(int.Parse(idn.V!, CultureInfo.InvariantCulture));
            Expr test = t is null ? Opaque("exception class " + idn.V) : new EInstanceOf(new EVar(Exception), t) { Type = JType.Bool };
            return op == "==" ? test : FunctionLifter.Not(test);
        }
        bool numeric = NumericOperand(c.L!) && NumericOperand(c.R!) || NumericOperand(c.L!) && c.R!.K == "num" || c.L!.K == "num" && NumericOperand(c.R!);
        if (numeric)
        {
            var l = Num(c.L!);
            var r = Num(c.R!);
            if (IsBig(l) || IsBig(r))
                return new EBinary(op, new ECall { Kind = CallKind.Jdk, Name = "compareTo", Recv = Big(l), Args = [Big(r)], Type = JType.Int }, ELit.Int(0)) { Type = JType.Bool };
            return new EBinary(op, l, r) { Type = JType.Bool };
        }
        // alphanumeric: the shorter operand padded with spaces
        var a = Operand(c.L!);
        var b = Operand(c.R!);
        if (op is "==" or "!=")
        {
            Expr eq = b is ELit { Value: "" } ? Jdk(a, "isEmpty", JType.Bool) : a is ELit { Value: "" } ? Jdk(b, "isEmpty", JType.Bool)
                : b is ELit ? Jdk(a, "equals", JType.Bool, b) : Jdk(b, "equals", JType.Bool, a);
            if (a is ERecRaw || b is ERecRaw) eq = new EBinary("==", JdkStatic(A.PlatformPackage + ".Records", "compare", JType.Int, a, b), ELit.Int(0)) { Type = JType.Bool };
            return op == "==" ? eq : FunctionLifter.Not(eq);
        }
        return new EBinary(op, JdkStatic(A.PlatformPackage + ".Records", "compare", JType.Int, a, b), ELit.Int(0)) { Type = JType.Bool };
    }

    // ================================================================== MOVE and arithmetic

    void Move(Stmt s, List<St> o)
    {
        var src = s.Get("src");
        if (src is null) return;
        foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
        {
            var it = Item(d);
            if (it is null) { Res("move to " + d.V, s); continue; }
            if (AppModel.IsRuntime(it)) continue;
            Store(d, it, src, o);
        }
    }

    /// <summary>A value into an item, converted the way MOVE converts (numeric, alphanumeric, edited, group).</summary>
    void Store(Node d, DataItem it, Node src, List<St> o)
    {
        // MOVE 'Y' / 'N' TO a Y/N work flag
        if (ap.Promoted.TryGetValue(it, out var bf) && bf.Type.Name == "boolean")
        {
            if (src is { K: "str", V: "Y" or "N" }) o.Add(new SAssign(Target(d, it), ELit.Bool(src.V == "Y")));
            else if (src.K == "ref" && Item(src) is { } sf && ap.Promoted.TryGetValue(sf, out var spf) && spf.Type.Name == "boolean")
                o.Add(new SAssign(Target(d, it), new EProgField(spf.Name, JType.Bool) { Type = JType.Bool }));
            else Res("flag " + it.Name + " set from " + src.K, new Stmt());
            return;
        }
        var srcItem = src.K == "ref" ? Item(src) : null;
        bool srcNumeric = NumericOperand(src);
        // a group, a filler, a reference modification: characters
        if (A.RecordOf(ap, it) is { } r && (d.Start is not null || !it.IsLeaf || !r.Rc.Accessors.ContainsKey(it.Name)))
        {
            var target = Read(d);
            o.Add(new SAssign(target, srcNumeric && srcItem is not null ? PadText(Operand(src), srcItem) : Text(src)));
            return;
        }
        if (it.Category == Category.NumericEdited)
        {
            o.Add(new SAssign(Target(d, it), JdkStatic(A.PlatformPackage + ".Records", "edit", JType.String, Num(src), ELit.Str(it.Pic ?? ""))));
            return;
        }
        if (IsNumeric(it))
        {
            var v = srcNumeric ? Num(src) : JdkStatic(A.PlatformPackage + ".Records", "unsigned", JType.Long, Text(src));
            var t = AppModel.FieldType(it);
            if (t.Name == "java.math.BigDecimal") v = Big(v);
            else if (IsBig(v)) v = Jdk(v, t.Name == "long" ? "longValue" : "intValue", t);
            else if (t.Name == "int" && IsLong(v)) v = new ECast(JType.Int, v) { Type = JType.Int };
            o.Add(new SAssign(Target(d, it), v));
            return;
        }
        // alphanumeric
        Expr text = srcNumeric && srcItem is not null ? PadText(Operand(src), srcItem) : srcNumeric ? JdkStatic("java.lang.String", "valueOf", JType.String, Num(src)) : Operand(src);
        if (ap.Promoted.ContainsKey(it))
        {
            bool fits = text is ELit { Value: string sl } && sl.Length <= it.Length || srcItem is not null && srcItem.Length <= it.Length && srcItem.IsLeaf && src.Start is null;
            if (!fits) text = JdkStatic(A.PlatformPackage + ".Records", "fit", JType.String, text, ELit.Int(it.Length));
        }
        o.Add(new SAssign(Target(d, it), text));
    }

    Expr Target(Node d, DataItem it)
    {
        if (ap.Promoted.TryGetValue(it, out var pf)) return new EProgField(pf.Name, pf.Type) { Type = pf.Type };
        var idx = (d.Args ?? []).Count > 0 ? Num(d.Args![0]) : null;
        return new ERecordField(it) { Type = AppModel.FieldType(it), Index = idx };
    }

    void Arith(Stmt s, List<St> o)
    {
        var ops = (s.L?.GetValueOrDefault("ops") ?? []).Select(Num).ToList();
        var giving = s.L?.GetValueOrDefault("giving");
        var dsts = s.L?.GetValueOrDefault("dst") ?? [];
        string op = s.Verb switch { "ADD" => "+", "SUBTRACT" => "-", _ => "*" };
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        Expr sum = ops.Count == 0 ? ELit.Int(0) : ops.Skip(1).Aggregate(ops[0], (a, b) => Arith(s.Verb == "MULTIPLY" ? "*" : "+", a, b));
        if (giving is { Count: > 0 })
        {
            // ADD a b GIVING c; SUBTRACT a FROM b GIVING c
            var from = dsts.Count > 0 ? Num(dsts[0]) : null;
            var value = s.Verb == "SUBTRACT" && from is not null ? Arith("-", from, sum) : from is not null ? Arith(op, from, sum) : sum;
            foreach (var g in giving) if (Item(g) is { } gi && !AppModel.IsRuntime(gi)) Assign(g, gi, value, sizeError, o);
            return;
        }
        foreach (var d in dsts)
        {
            if (Item(d) is not { } it || AppModel.IsRuntime(it)) continue;
            Assign(d, it, Arith(op, Num(d), sum), sizeError, o);
        }
    }

    /// <summary>A computed value into a numeric item; ON SIZE ERROR when it does not fit the item's digits.</summary>
    void Assign(Node d, DataItem it, Expr value, List<Stmt>? sizeError, List<St> o)
    {
        var t = AppModel.FieldType(it);
        if (t.Name == "int" && IsLong(value)) value = new ECast(JType.Int, value) { Type = JType.Int };
        if (t.Name == "java.math.BigDecimal") value = Big(value);
        var store = new SAssign(Target(d, it), value);
        if (sizeError is null || sizeError.Count == 0 || t.Name == "java.math.BigDecimal") { o.Add(store); return; }
        var fits = JdkStatic(A.PlatformPackage + ".Records", "fits", JType.Bool, value, ELit.Int(it.Digits));
        o.Add(new SIf(FunctionLifter.Not(fits), Block(sizeError), [store]));
    }

    void Compute(Stmt s, List<St> o)
    {
        var e = Num(s.Get("expr")!);
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
            if (Item(d) is { } it && !AppModel.IsRuntime(it)) Assign(d, it, e, sizeError, o);
    }

    void Divide(Stmt s, List<St> o)
    {
        var ops = (s.L?.GetValueOrDefault("ops") ?? []).Select(Num).ToList();
        var by = s.Get("by") is { } b ? Num(b) : null;
        var form = s.Str("form") ?? "";
        if (ops.Count != 1 || by is null) { Res("DIVIDE form " + form, s); return; }
        Expr dividend = ops[0], divisor = by;
        if (form.StartsWith("INTO")) { dividend = by; divisor = ops[0]; }
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        foreach (var g in s.L?.GetValueOrDefault("giving") ?? [])
            if (Item(g) is { } gi) Assign(g, gi, Arith("/", dividend, divisor), null, o);
        if (s.Get("rem") is { } rem && Item(rem) is { } ri) Assign(rem, ri, new EBinary("%", dividend, divisor) { Type = dividend.Type ?? JType.Int }, null, o);
        _ = sizeError;
    }

    // ================================================================== control flow

    void If(Stmt s, List<St> o)
    {
        var c = s.Get("cond")!;
        // IF FS-INIT = 'N' PERFORM 0100-INIT: the job program's one-time initialization is its constructor
        if (c is { K: "rel", L: { K: "ref" } l } && Item(l) is { } li && AppModel.IsRuntime(li) && Exception is null) return;
        var then = Block(s.Blocks?.GetValueOrDefault("TRUE") ?? []);
        var els = Block(s.Blocks?.GetValueOrDefault("FALSE") ?? []);
        if (then.Count == 0 && els.Count == 0) return;
        o.Add(new SIf(Cond(c), then, els));
    }

    void Evaluate(Stmt s, List<St> o)
    {
        var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
        if (s.L?["subjects"] is [{ K: "ref" } subj] && Item(subj) is { } si && AppModel.IsRuntime(si) && Exception is null && !si.Name.StartsWith("FO-", StringComparison.OrdinalIgnoreCase))
        {
            Res("EVALUATE of runtime item " + si.Name, s);
            return;
        }
        // a switch: every WHEN compares the subject with literals
        if (s.L?["subjects"] is [{ K: "ref" } subject] && Item(subject) is { } sub && sub.Level != 88 && subject.Start is null)
        {
            var cases = new List<(List<Expr> Labels, List<St> Body)>();
            List<St>? deflt = null;
            bool ok = true;
            foreach (var w in whens)
            {
                var body = Block(w.Blocks?.GetValueOrDefault("TRUE") ?? []);
                if (w.Str("other") == "true") { deflt = body; continue; }
                var labels = new List<Expr>();
                if (!Labels(w.Get("cond"), sub, labels)) { ok = false; break; }
                cases.Add((labels, body));
            }
            if (ok && cases.Count >= 2)
            {
                var sw = new SSwitch(Operand(subject));
                sw.Cases.AddRange(cases);
                sw.Default = deflt ?? [];
                o.Add(sw);
                return;
            }
        }
        // an if-else chain (EVALUATE TRUE, conditions)
        List<St>? chain = null;
        for (int k = whens.Count - 1; k >= 0; k--)
        {
            var w = whens[k];
            var body = Block(w.Blocks?.GetValueOrDefault("TRUE") ?? []);
            if (w.Str("other") == "true") { chain = body; continue; }
            chain = [new SIf(Cond(w.Get("cond")!), body, chain ?? [])];
        }
        if (chain is not null) o.AddRange(chain);
    }

    bool Labels(Node? c, DataItem sub, List<Expr> labels)
    {
        if (c is null) return false;
        if (c.K == "or") return Labels(c.L, sub, labels) && Labels(c.R, sub, labels);
        if (c is { K: "rel", Op: "eq", L: { K: "ref" } l, R: var r } && Item(l) == sub && r is { K: "num" or "str" })
        {
            labels.Add(IsNumeric(sub) ? Num(r) : ELit.Str(r.V ?? ""));
            return true;
        }
        return false;
    }

    void Perform(Stmt s, List<St> o)
    {
        if (s.Str("inline") == "true")
        {
            var body = Block(s.Blocks?.GetValueOrDefault("LOOP") ?? []);
            Loop(s, body, o);
            return;
        }
        var t = s.Str("target") ?? "";
        var thru = s.Str("thru");
        var calls = Calls(t, thru, s);
        if (calls.Count == 0) return;
        Loop(s, calls, o);
    }

    /// <summary>The calls a PERFORM makes: a paragraph's method, a THRU range in order, a job function.</summary>
    List<St> Calls(string target, string? thru, Stmt s)
    {
        var res = new List<St>();
        if (Runtime(target, res)) return res;
        if (!ap.MethodNames.TryGetValue(target, out var m)) { Res("PERFORM " + target, s); return res; }
        // the reject mapping function, from a reject handler: the exception and its site
        if (ap.RejectFunctions.Any(f => f.Paragraph.Equals(target, StringComparison.OrdinalIgnoreCase)) && Exception is not null)
        {
            res.Add(new SExpr(new ECall { Kind = CallKind.Instance, Name = m, Args = [new EVar(Exception), new EVar(Site!)] }));
            return res;
        }
        // a job function is one method (its THRU names the function's last paragraph)
        if (ap.JobFunctions.Any(f => f.Paragraph.Equals(target, StringComparison.OrdinalIgnoreCase)))
        {
            res.Add(new SExpr(new ECall { Kind = CallKind.Instance, Name = m }));
            return res;
        }
        int from = P.ParagraphIndex.GetValueOrDefault(target, -1), to = thru is null ? from : P.ParagraphIndex.GetValueOrDefault(thru, from);
        for (int i = from; i >= 0 && i <= to; i++)
            if (ap.MethodNames.TryGetValue(P.Model.Paragraphs[i].Name, out var mi) && !EmptyParagraph(P.Model.Paragraphs[i]))
                res.Add(new SExpr(new ECall { Kind = CallKind.Instance, Name = mi }));
        return res;
    }

    readonly Dictionary<string, bool> emptyCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A paragraph whose statements all maintain the J3S runtime (0200-RESET, 0900-STATISTICS): no Java.</summary>
    public bool EmptyParagraph(Paragraph para)
    {
        if (emptyCache.TryGetValue(para.Name, out var e)) return e;
        emptyCache[para.Name] = false;
        var saved = Residue.Count;
        var body = Block(para.Statements);
        Residue.RemoveRange(saved, Residue.Count - saved);
        return emptyCache[para.Name] = body.Count == 0;
    }

    /// <summary>PERFORMs of runtime paragraphs: the abend, the J3S runtime (nothing in Java).</summary>
    bool Runtime(string target, List<St> res)
    {
        var t = target.ToUpperInvariant();
        if (t is "JX-JOBFAIL" or "JX-ABEND")
        {
            res.Add(new SThrow(new ENew { JdkClass = A.PlatformPackage + ".Abend", Args = [ELit.Int(38), ELit.Str(P.Id + " job adapter failure")] }));
            return true;
        }
        if (t.StartsWith("JA-") || t.StartsWith("JX-") || t.StartsWith("JG-") || t.StartsWith("JD-") || t.StartsWith("JB-") || t.StartsWith("JW-") || t.StartsWith("JH-") || t.StartsWith("JS-") || t == "JO-SIMPLE-NAME")
            return true;
        return false;
    }

    void Loop(Stmt s, List<St> body, List<St> o)
    {
        if (s.Get("var") is { } v && Item(v) is { } vi)
        {
            // PERFORM VARYING v FROM a BY b UNTIL c
            var init = new List<St>();
            Store(v, vi, s.Get("from")!, init);
            o.AddRange(init);
            var step = new List<St>();
            Assign(v, vi, Arith("+", Num(v), Num(s.Get("by")!)), null, step);
            o.Add(new SLoop(NewLabel(), FunctionLifter.Not(Cond(s.Get("until")!)), [.. body, .. step]));
            return;
        }
        if (s.Get("until") is { } until)
        {
            o.Add(new SLoop(NewLabel(), FunctionLifter.Not(Cond(until)), body));
            return;
        }
        if (s.Get("times") is { } times)
        {
            var k = NewTemp(JType.Int, "k");
            o.Add(new SAssign(new EVar(k), ELit.Int(0)));
            o.Add(new SLoop(NewLabel(), new EBinary("<", new EVar(k), Num(times)) { Type = JType.Bool }, [.. body, new SAssign(new EVar(k), new EBinary("+", new EVar(k), ELit.Int(1)) { Type = JType.Int })]));
            return;
        }
        o.AddRange(body);
    }

    string NewLabel() => "loop" + (++temp);

    Var NewTemp(JType t, string name)
    {
        var v = new Var { Name = name, Type = t, Kind = VKind.Unknown, IsTemp = false };
        Temps.Add(v);
        return v;
    }

    // ================================================================== files and calls

    FileDecl? FileOf(string name) => P.Model.Files.FirstOrDefault(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    Expr FileField(FileDecl f) => new EProgField(ap.FileFields[f.Name], JType.Of(A.PlatformPackage + ".Datasets." + (f.Organization == "INDEXED" ? "Indexed" : "Sequential")));

    /// <summary>The FILE STATUS item after an operation on the file.</summary>
    void Status(FileDecl f, List<St> o)
    {
        if (f.FileStatus is null || !P.ItemsByName.TryGetValue(f.FileStatus, out var st)) return;
        var node = new Node { K = "ref", V = st.Name, Id = st.Id };
        o.Add(new SAssign(Target(node, st), Jdk(FileField(f), "status", JType.String)));
    }

    void Open(Stmt s, List<St> o)
    {
        foreach (var fn in s.L?.GetValueOrDefault("files") ?? [])
        {
            if (FileOf(fn.V!) is not { } f) { Res("OPEN " + fn.V, s); continue; }
            o.Add(new SExpr(Jdk(FileField(f), (fn.Op ?? "INPUT").ToUpperInvariant() == "INPUT" ? "openInput" : "openOutput", JType.Void)));
            Status(f, o);
        }
    }

    void Close(Stmt s, List<St> o)
    {
        foreach (var fn in s.L?.GetValueOrDefault("files") ?? [])
        {
            if (FileOf(fn.V!) is not { } f) { Res("CLOSE " + fn.V, s); continue; }
            o.Add(new SExpr(Jdk(FileField(f), "close", JType.Void)));
            Status(f, o);
        }
    }

    void Read(Stmt s, List<St> o)
    {
        var f = FileOf(s.Str("file") ?? "");
        if (f is null) { Res("READ " + s.Str("file"), s); return; }
        // READ [INTO]: the record class reads the image (the bytes stay in the record package)
        var into = s.Get("into") is { } i ? Item(i) : f.Records.FirstOrDefault();
        Expr target = into is not null && A.RecordOf(ap, into) is { } r ? new EProgField(r.Instance, JType.Of(r.Rc.Name)) : ELit.Null;
        var found = NewTemp(JType.Bool, "found");
        Expr read;
        if (f.Organization == "INDEXED")
        {
            // a keyed read: the key is the record key field's value
            var keyItem = f.RecordKey is null ? null : P.ItemsByName.GetValueOrDefault(f.RecordKey);
            var key = keyItem is null ? ELit.Str("") : Read(new Node { K = "ref", V = keyItem.Name, Id = keyItem.Id });
            read = Jdk(FileField(f), "read", JType.Bool, key, target);
        }
        else read = Jdk(FileField(f), "read", JType.Bool, target);
        o.Add(new SAssign(new EVar(found), read));
        Status(f, o);
        var got = Block(s.Blocks?.GetValueOrDefault("NOT-AT-END") ?? s.Blocks?.GetValueOrDefault("NOT-INVALID") ?? []);
        var miss = Block(s.Blocks?.GetValueOrDefault("AT-END") ?? s.Blocks?.GetValueOrDefault("INVALID") ?? []);
        if (miss.Count > 0) o.Add(new SIf(FunctionLifter.Not(new EVar(found)), miss, got));
        else if (got.Count > 0) o.Add(new SIf(new EVar(found), got, []));
    }

    void Write(Stmt s, List<St> o)
    {
        var recNode = s.Get("record");
        var recItem = recNode is null ? null : Item(recNode);
        var f = P.Model.Files.FirstOrDefault(x => x.Records.Any(r => r == recItem || r.Name == recItem?.Name));
        if (f is null) { Res("WRITE " + recNode?.V, s); return; }
        var from = s.Get("from") ?? recNode!;
        o.Add(new SExpr(Jdk(FileField(f), "write", JType.Void, Text(from))));
        Status(f, o);
    }

    void Display(Stmt s, List<St> o)
    {
        Expr? acc = null;
        foreach (var n in s.L?.GetValueOrDefault("items") ?? [])
        {
            var v = n.K == "ref" && Item(n) is { } it && (it.Category == Category.Numeric && it.Usage == Usage.Display || !it.IsLeaf) ? Text(n) : Operand(n);
            acc = acc is null ? (v.Type?.Name == "java.lang.String" || v is ELit { Value: string } ? v : JdkStatic("java.lang.String", "valueOf", JType.String, v)) : new EBinary("+", acc, v) { Type = JType.String };
        }
        o.Add(new SExpr(Jdk(new EStaticJdk("java.lang.System", "out", JType.Of("java.io.PrintStream")), "println", JType.Void, acc ?? ELit.Str(""))));
    }

    void Call(Stmt s, List<St> o)
    {
        var target = s.Get("target") is { K: "str", V: var tv } ? tv ?? "" : "";
        var args = (s.L?.GetValueOrDefault("args") ?? []).Select(a => a.L ?? a).ToList();
        if (target.Equals("CEE3ABD", StringComparison.OrdinalIgnoreCase))
        {
            // the Language Environment abend: the step ends with the code
            var code = args.Count > 0 ? Num(args[0]) : ELit.Int(16);
            o.Add(new SThrow(new ENew { JdkClass = A.PlatformPackage + ".Abend", Args = [code, ELit.Str(P.Id + " abend")] }));
            return;
        }
        // a call of the J3S runtime (CALL 'FSJBDEC' USING JB): its arguments are runtime storage only
        if (args.Count > 0 && args.All(a => Item(a) is { } ai0 && AppModel.IsRuntime(ai0))) return;
        if (ap.Callees.TryGetValue(target, out var field) && A.ById.TryGetValue(target, out var callee))
        {
            var actual = new List<Expr>();
            foreach (var a in args)
            {
                if (Item(a) is not { } ai || AppModel.IsRuntime(ai)) continue;
                if (A.RecordOf(ap, ai) is { } r) actual.Add(new EProgField(r.Instance, JType.Of(r.Rc.Name)));
                else actual.Add(Operand(a));
            }
            o.Add(new SExpr(Jdk(new EProgField(field, JType.Of(A.AppPackage + "." + callee.ClassName)), "call", JType.Void, [.. actual])));
            return;
        }
        Res("CALL " + target, s);
    }

    // ================================================================== helpers

    static ECall Jdk(Expr recv, string name, JType type, params Expr[] args) => new() { Kind = CallKind.Jdk, Name = name, Recv = recv, Args = [.. args], Type = type };
    static ECall JdkStatic(string owner, string name, JType type, params Expr[] args) => new() { Kind = CallKind.JdkStatic, Owner = owner, Name = name, Args = [.. args], Type = type };

    /// <summary>The C# side of Records.pad (constants computed when lifting).</summary>
    static class Records
    {
        public static string Pad(string s, int n) => s.Length >= n ? s[..n] : s + new string(' ', n - s.Length);
    }
}
