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
    /// <summary>The item referenced (a field, or the field a reference modification is part of), when known.</summary>
    public DataItem? Item { get; init; }
    /// <summary>The record as a parameter or local (a restructured method), in place of the program's instance field.</summary>
    public Expr? Via { get; init; }
    /// <summary>A length computed at run time (a reference modification by a variable: X (1:N)), in place of <see cref="Length"/>.</summary>
    public Expr? Len { get; init; }
    public override IEnumerable<Expr> Children => (Offset is null ? [] : new[] { Offset }).Concat(Via is null ? [] : [Via]).Concat(Len is null ? [] : [Len]);
    public override Expr Map(Func<Expr, Expr> f) => new ERecRaw(Instance, Offset is null ? null : f(Offset), Length) { Type = Type, Item = Item, Via = Via is null ? null : f(Via), Len = Len is null ? null : f(Len) };
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

    /// <summary>An inline PERFORM being lifted: its loop's label (when an EXIT PERFORM needs it), the EVALUATEs entered since.</summary>
    sealed class LoopFrame
    {
        public string? Label;
        public int Switches;
        public bool Exits;
    }

    readonly Stack<LoopFrame> loops = new();
    int exitLabels;

    /// <summary>Lifting the subprogram's entry paragraph (its GOBACK ends the CALL).</summary>
    public bool InEntry { get; set; }

    /// <summary>The subprogram's own unchecked exception that ends its CALL from a performed paragraph (GOBACK there).</summary>
    public const string CallEndedClass = "CallEnded";

    /// <summary>A LINKAGE record's parameter (a subprogram's paragraph methods take the caller's records), else null.</summary>
    Expr? Via(DataItem it) => ap.LinkageVars.TryGetValue(it.Record, out var v) ? new EVar(v) { Type = v.Type } : null;

    /// <summary>A record as a value: the program's instance field, or the LINKAGE parameter.</summary>
    Expr RecordExpr(DataItem rec, RecordClass rc, string instance) =>
        Via(rec) ?? new EProgField(instance, JType.Of(A.RecordPackage + "." + rc.Name)) { Type = JType.Of(A.RecordPackage + "." + rc.Name) };

    /// <summary>The LINKAGE arguments of a PERFORM of the paragraph (its method's parameters).</summary>
    List<Expr> LinkageArgs(string paragraph) =>
        ap.ParagraphLinkage.TryGetValue(paragraph, out var recs) ? recs.Select(r => (Expr)new EVar(ap.LinkageVars[r]) { Type = ap.LinkageVars[r].Type }).ToList() : [];

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
            case "GOBACK" or "EXIT PROGRAM" when ap.IsSubprogram && !InEntry:
                // a performed paragraph ends the CALL: it unwinds to the entry method (call() catches it and returns)
                ap.EndsCallInParagraph = true;
                o.Add(new SThrow(new ENew { JdkClass = CallEndedClass, Type = JType.Of(CallEndedClass) }));
                break;
            case "STOP RUN" when ap.IsSubprogram && !InEntry:
                // a subprogram's paragraph method returns to its PERFORM: ending the CALL from there needs the entry's help
                Res($"{s.Verb} outside the entry paragraph", s);
                break;
            case "GOBACK": case "STOP RUN": case "EXIT PROGRAM": o.Add(new SReturn(null)); break;
            case "GO TO" when EndOfFunction(s):
                // the end of a job function's paragraph range: its return
                o.Add(new SReturn(null));
                break;
            case "SET" when s.Str("mode") == "TRUE": SetTrue(s, o); break;
            case "INITIALIZE": Initialize(s, o); break;
            case "EXIT PARAGRAPH": o.Add(new SReturn(null)); break;
            case "EXIT PERFORM" when loops.Count > 0:
            {
                // inside an EVALUATE (a Java switch, where a plain break leaves the switch): the loop's label
                var frame = loops.Peek();
                if (frame.Switches > 0) frame.Label ??= "perform" + (++exitLabels);
                frame.Exits = true;
                o.Add(new SBreak(frame.Switches > 0 ? frame.Label : null));
                break;
            }
            case "CONTINUE": case "EXIT": break;
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
        // a subprogram's LINKAGE item is the caller's storage (its VALUE clause has no effect)
        if (ap.IsSubprogram && it.Record.Section == DataSection.Linkage) return null;
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
            if (it.Category == Category.Numeric)
            {
                // a zoned unsigned integer's characters are its digits; a packed, binary or signed number is no text
                if (it.Usage != Usage.Display || it.Signed || it.Scale != 0) return null;
                if (v.ToUpperInvariant() is "ZERO" or "ZEROS" or "ZEROES") return new string('0', it.Length);
                if (!long.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
                var digits = n.ToString(CultureInfo.InvariantCulture).PadLeft(it.Length, '0');
                return digits[^it.Length..];
            }
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
                // a record a CALL passes by reference: the called program may write it
                if (s.Verb == "CALL")
                    foreach (var a in s.L?.GetValueOrDefault("args") ?? [])
                        if (Item(a.L ?? a) is { } ai && !AppModel.IsRuntime(ai)) w.Add(ai);
                // SET condition TO TRUE writes the condition's field; INITIALIZE its group
                if (s.Verb is "SET" or "INITIALIZE")
                    foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
                        if (Item(d) is { } di) w.Add(di.Level == 88 && di.Parent is { } dp ? dp : di);
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
            var idx = Subscript(Num(subs[k]));
            off = Add(off, Mul(Sub(idx, ELit.Int(1)), ELit.Int(it.Dims[k].Stride)));
        }
        return off;
    }

    /// <summary>A wide subscript narrows exactly so an out-of-range value fails rather than wraps.</summary>
    Expr Subscript(Expr e)
    {
        if (IsBig(e)) return Jdk(e, "intValueExact", JType.Int);
        if (IsLong(e)) return JdkStatic("java.lang.Math", "toIntExact", JType.Int, e);
        return e;
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
            return Numeric(v, it);
        }
        if (A.RecordOf(ap, it) is { } r)
        {
            if (n.Start is not null || !it.IsLeaf || it.IsFiller || !r.Rc.Accessors.ContainsKey(it.Name))
            {
                var off = OffsetOf(it, n);
                int len = it.Length;
                Expr? dynamic = null;
                if (n.Start is not null)
                {
                    off = Add(off, Sub(Num(n.Start), ELit.Int(1)));
                    if (n.Len is { K: "num" } ln) len = int.Parse(ln.V!, CultureInfo.InvariantCulture);
                    else if (n.Len is not null) dynamic = Num(n.Len);
                    else if (n.Start is not { K: "num" }) dynamic = Sub(ELit.Int(it.Length), Sub(Num(n.Start), ELit.Int(1)));
                    else len = it.Length - (int.Parse(n.Start.V!, CultureInfo.InvariantCulture) - 1);
                }
                if (dynamic is not null) return new ERecRaw(r.Instance, off, len) { Type = JType.String, Item = it, Via = Via(it), Len = dynamic };
                if (off is ELit { Value: 0L } && len == r.Rc.Length) return new ERecRaw(r.Instance, null, len) { Type = JType.String, Item = it, Via = Via(it) };
                return new ERecRaw(r.Instance, off, len) { Type = JType.String, Item = it, Via = Via(it) };
            }
            return Numeric(Field(it, n), it);
        }
        Res("no storage for " + it.Name, new Stmt());
        return new EOpaque(it.Name);
    }

    /// <summary>A record field with its subscripts (1-based, one per OCCURS dimension) and record (a LINKAGE parameter's).</summary>
    ERecordField Field(DataItem it, Node n, JType? type = null, bool padded = false)
    {
        var subs = (n.Args ?? []).Select(a => Subscript(Num(a))).ToList();
        if (subs.Count > 1 && subs.Count != it.Dims.Count) Res($"subscripts of {it.Name}", new Stmt());
        return new ERecordField(it) { Type = type ?? AppModel.FieldType(it), Padded = padded, Index = subs.Count > 0 ? subs[0] : null, More = subs.Skip(1).ToList(), Via = Via(it) };
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

    /// <summary>A number literal: an int or long, a BigDecimal past 18 digits or with decimals.</summary>
    Expr NumberLiteral(string t)
    {
        t = t.TrimStart('+');
        if (t.Contains('.') || t.TrimStart('-').Length > 18)
        {
            var digits = t.Count(char.IsDigit);
            var scale = t.Contains('.') ? t.Length - t.IndexOf('.') - 1 : 0;
            // past what a double spells exactly: the decimal's text
            Expr lit = digits > 15 ? new ENew { JdkClass = "java.math.BigDecimal", Args = [ELit.Str(t)], Type = JType.BigDecimal } : new ELit(decimal.Parse(t, CultureInfo.InvariantCulture), JType.BigDecimal);
            info[lit] = (digits, scale);
            return lit;
        }
        var v = long.Parse(t, CultureInfo.InvariantCulture);
        return v is >= int.MinValue and <= int.MaxValue ? ELit.Int(v) : new ELit(v, JType.Long);
    }

    /// <summary>A numeric item never written with a VALUE: its value as a number literal (null when not one).</summary>
    Expr? NumericConstant(DataItem it)
    {
        if (!IsNumeric(it) || it.Level == 88 || ap.Promoted.ContainsKey(it) || writtenItems.Value.Contains(it) || Ancestors(it).Any(writtenItems.Value.Contains)) return null;
        if (it.Values.Count != 1 || it.Values[0].Low is not { Quoted: false } v) return null;
        var text = v.Text.ToUpperInvariant() is "ZERO" or "ZEROS" or "ZEROES" ? "0" : v.Text;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _)) return null;
        var lit = NumberLiteral(text);
        if (lit.Type?.Name == "java.math.BigDecimal" || it.Scale > 0) { var b = Big(lit); info[b] = (it.Digits, it.Scale); return b; }
        info[lit] = (it.Digits, 0);
        return lit;
    }

    Expr Operand(Node n, bool numeric = false)
    {
        switch (n.K)
        {
            case "num": return NumberLiteral(n.V!);
            case "ref" when numeric && n.Start is null && Item(n) is { } ci && NumericConstant(ci) is { } constant: return constant;
            case "fn": return Function(n);
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

    Expr Big(Expr e)
    {
        if (IsBig(e) && e is not ELit { Value: decimal }) return e;
        var b = e is ELit { Value: decimal d } ? new ECall { Kind = CallKind.JdkStatic, Owner = "java.math.BigDecimal", Name = "valueOf", Args = [new ELit(d, JType.Double)], Type = JType.BigDecimal }
            : new ECall { Kind = CallKind.JdkStatic, Owner = "java.math.BigDecimal", Name = "valueOf", Args = [e], Type = JType.BigDecimal };
        info[b] = Info(e);
        return b;
    }

    // ------------------------------------------------------------------ the size of values (the PICTURE proof)

    /// <summary>The digits (integer and fraction) and scale an expression's value can have: from the PICTUREs it reads.</summary>
    readonly Dictionary<Expr, (int Digits, int Scale)> info = new(ReferenceEqualityComparer.Instance);

    Expr Numeric(Expr e, DataItem it)
    {
        if (!IsNumeric(it)) return e;
        if (e.Type?.Name == "java.math.BigInteger")
            e = new ENew { JdkClass = "java.math.BigDecimal", Args = [e], Type = JType.BigDecimal };
        info[e] = (it.Digits, it.Scale);
        if (!it.Signed) unsigned.Add(e);
        return e;
    }

    (int Digits, int Scale) Info(Expr e) => info.TryGetValue(e, out var x) ? x : e switch
    {
        ELit { Value: long v } => (Math.Max(1, v == long.MinValue ? 19 : Math.Abs(v).ToString(CultureInfo.InvariantCulture).Length), 0),
        ELit { Value: decimal d } => DecimalInfo(d),
        ECast c => Info(c.E),
        _ => e.Type?.Name switch { "int" => (9, 0), "long" => (18, 0), "java.math.BigDecimal" => (31, -1), _ => (18, 0) },
    };

    static (int, int) DecimalInfo(decimal d)
    {
        var t = Math.Abs(d).ToString(CultureInfo.InvariantCulture);
        int scale = t.Contains('.') ? t.Length - t.IndexOf('.') - 1 : 0;
        return (t.Count(char.IsDigit), scale);
    }

    /// <summary>
    /// COBOL arithmetic: integers in Java int or long when the operands' PICTUREs prove the result fits (a sum of two
    /// 9-digit numbers fits an int, a product of 9 and 5 digits needs a long), else BigDecimal; decimals in BigDecimal.
    /// </summary>
    Expr Arith(string op, Expr l, Expr r)
    {
        if (op == "/") return Quotient(l, r);
        return Arithmetic(op, l, r);
    }

    Expr IntegerQuotient(Expr l, Expr r) => Arithmetic("/", l, r);

    /// <summary>
    /// GnuCOBOL expression division retains max(dividend scale - divisor scale, 0) + 38 digits before the receiving store.
    /// This is a dialect property, not an IBM ARITH precision guarantee.
    /// </summary>
    Expr Quotient(Expr l, Expr r)
    {
        var (dl, sl) = Info(l);
        var (dr, sr) = Info(r);
        int il = dl - Math.Max(sl, 0);
        int scale = sl < 0 || sr < 0 ? 38 : Math.Max(sl - sr, 0) + 38;
        Expr q;
        if (r is ELit { Value: long rv } && rv > 0 && PowerOfTen(rv) is int k && sl >= 0 && sl + k <= 38)
        {
            q = Jdk(Big(l), "movePointLeft", JType.BigDecimal, ELit.Int(k));
            scale = sl + k;
        }
        else q = Jdk(Big(l), "divide", JType.BigDecimal, Big(r), ELit.Int(scale), new EStaticJdk("java.math.RoundingMode", "DOWN", JType.RoundingMode));
        info[q] = (il + Math.Max(sr, 0) + scale, scale);
        return q;
    }

    static int? PowerOfTen(long v)
    {
        int k = 0;
        for (; v > 1 && v % 10 == 0; v /= 10) k++;
        return v == 1 ? k : null;
    }

    Expr Arithmetic(string op, Expr l, Expr r)
    {
        var (dl, sl) = Info(l);
        var (dr, sr) = Info(r);
        int il = dl - Math.Max(sl, 0), ir = dr - Math.Max(sr, 0);
        // the result's digits and scale (scale -1: not known)
        int scale = sl < 0 || sr < 0 ? -1 : op == "*" ? sl + sr : op is "+" or "-" ? Math.Max(sl, sr) : -1;
        int ints = op == "*" ? il + ir : Math.Max(il, ir) + 1;
        int digits = ints + Math.Max(scale, 0);
        if (IsBig(l) || IsBig(r) || op is "+" or "-" && Math.Max(il, ir) > 18 || op == "*" && il + ir > 18)
        {
            var name = op switch { "+" => "add", "-" => "subtract", "*" => "multiply", _ => "divide" };
            var big = new ECall { Kind = CallKind.Jdk, Name = name, Recv = Big(l), Args = [Big(r)], Type = JType.BigDecimal };
            info[big] = (digits, scale);
            return big;
        }
        // int when both are: unless the product of their digits may pass an int (a long then)
        var t = IsLong(l) || IsLong(r) ? JType.Long : JType.Int;
        if (t.Name == "int" && op == "*" && il + ir > 9 || t.Name == "int" && op is "+" or "-" && Math.Max(il, ir) > 9)
        {
            t = JType.Long;
            l = new ECast(JType.Long, l) { Type = JType.Long };
        }
        var e = new EBinary(op, l, r) { Type = t };
        info[e] = (ints, 0);
        return e;
    }

    // ------------------------------------------------------------------ intrinsic functions

    Expr Function(Node n)
    {
        // FUNCTION LENGTH of an item: its size in bytes (a fixed layout)
        if ((n.V ?? "").Equals("LENGTH", StringComparison.OrdinalIgnoreCase) && n.Args is [{ K: "ref" } la] && Item(la) is { DependingOnItem: null } li)
            return ELit.Int(la.Start is null ? li.Length : la.Len is { K: "num" } ll ? int.Parse(ll.V!, CultureInfo.InvariantCulture) : li.Length);
        var args = (n.Args ?? []).Select(Num).ToList();
        string numbers = A.PlatformPackage + ".Numbers";
        Expr? e = (n.V ?? "").ToUpperInvariant() switch
        {
            // the day number of a yyyyMMdd date (1601-01-01 is day 1), and back
            "INTEGER-OF-DATE" when args.Count == 1 => JdkStatic(numbers, "integerOfDate", JType.Int, args[0]),
            "DATE-OF-INTEGER" when args.Count == 1 => JdkStatic(numbers, "dateOfInteger", JType.Int, args[0]),
            _ => null,
        };
        if (e is null) return Opaque("function " + n.V);
        info[e] = (8, 0);
        return e;
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
        {
            // a text field's characters are its padded accessor; a number's are its bytes (a view of the record)
            if (!IsNumeric(it)) return Field(it, n, JType.String, padded: true);
            return new ERecRaw(r.Instance, OffsetOf(it, n), it.Length) { Type = JType.String, Item = it, Via = Via(it) };
        }
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
            if (it.Name.Equals("JX-AT", StringComparison.OrdinalIgnoreCase)) jobFailAt = src is { K: "str" } jas && jas.V!.Trim().Length > 0 ? jas.V!.Trim() : null;
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
            // a record's edited field: its codec edits the number (the PICTURE stays in the record class)
            if (A.RecordOf(ap, it) is { } er && !ap.Promoted.ContainsKey(it) && er.Rc.Accessors.ContainsKey(it.Name))
            {
                er.Rc.Edited.Add(it.Name);
                o.Add(new SAssign(Target(d, it), Big(Num(src))));
                return;
            }
            var ev = Num(src);
            o.Add(new SAssign(Target(d, it), IsBig(ev) ? JdkStatic(A.PlatformPackage + ".Numbers", "edit", JType.String, ev, ELit.Str(it.Pic ?? ""))
                : JdkStatic(A.PlatformPackage + ".Records", "edit", JType.String, ev, ELit.Str(it.Pic ?? ""))));
            return;
        }
        if (IsNumeric(it))
        {
            var v = srcNumeric ? Num(src) : JdkStatic(A.PlatformPackage + ".Records", "unsigned", JType.Long, Text(src));
            o.Add(new SAssign(Target(d, it), Fit(v, it, rounded: false, truncate: true)));
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
        return Field(it, d);
    }

    /// <summary>Values known not to be negative (an unsigned item's).</summary>
    readonly HashSet<Expr> unsigned = new(ReferenceEqualityComparer.Instance);

    bool MayBeNegative(Expr e) => e switch
    {
        ELit { Value: long v } => v < 0,
        ELit { Value: decimal d } => d < 0,
        _ when unsigned.Contains(e) => false,
        ECast c => MayBeNegative(c.E),
        EBinary { Op: "+" or "*" or "/" or "%" } b => MayBeNegative(b.L) || MayBeNegative(b.R),
        ECall { Kind: CallKind.Jdk, Name: "add" or "multiply", Recv: { } rv, Args: [var a] } => MayBeNegative(rv) || MayBeNegative(a),
        ECall { Kind: CallKind.JdkStatic, Owner: "java.math.BigDecimal", Name: "valueOf", Args: [var a, ..] } => MayBeNegative(a),
        _ => true,
    };

    /// <summary>
    /// A value as the item holds it (a MOVE or an arithmetic statement's store): the item's Java type; its scale (the
    /// fraction truncated, or ROUNDED half away from zero); for a MOVE, its high-order digits truncated when the PICTUREs
    /// do not prove they fit; an unsigned work field's value without its sign. A record field's setter does the same in
    /// its codec (a packed or zoned field keeps its digits), so only a work field and a binary field need it here.
    /// </summary>
    Expr Fit(Expr v, DataItem it, bool rounded, bool truncate)
    {
        var t = AppModel.FieldType(it);
        var (vd, vs) = Info(v);
        int vInts = vd - Math.Max(vs, 0);
        bool work = ap.Promoted.ContainsKey(it);
        bool own = work || it.Usage is Usage.Binary or Usage.Comp5;
        string numbers = A.PlatformPackage + ".Numbers";
        if (t.Name == "java.math.BigDecimal")
        {
            v = Big(v);
            if (rounded && (vs < 0 || vs > it.Scale)) v = Jdk(v, "setScale", JType.BigDecimal, ELit.Int(it.Scale), new EStaticJdk("java.math.RoundingMode", "HALF_UP", JType.RoundingMode));
            else if (work && vs != it.Scale) v = Jdk(v, "setScale", JType.BigDecimal, ELit.Int(it.Scale), new EStaticJdk("java.math.RoundingMode", "DOWN", JType.RoundingMode));
            if (truncate && work && (vs < 0 || vInts > it.Digits - it.Scale)) v = JdkStatic(numbers, "truncate", JType.BigDecimal, v, ELit.Int(it.Digits), ELit.Int(it.Scale));
            if (work && !it.Signed && MayBeNegative(v)) v = Jdk(v, "abs", JType.BigDecimal);
            info[v] = (it.Digits, it.Scale);
            return v;
        }
        if (IsBig(v))
        {
            v = Big(v);
            if (rounded && vs != 0) v = Jdk(v, "setScale", JType.BigDecimal, ELit.Int(0), new EStaticJdk("java.math.RoundingMode", "HALF_UP", JType.RoundingMode));
            if (truncate && (vs < 0 || vInts > it.Digits)) v = JdkStatic(numbers, "truncate", JType.BigDecimal, v, ELit.Int(it.Digits), ELit.Int(0));
            v = Jdk(v, t.Name switch { "long" => "longValue", "java.math.BigInteger" => "toBigInteger", _ => "intValue" }, t);
        }
        else if (t.Name == "java.math.BigInteger" && v.Type?.Name != "java.math.BigInteger")
            v = JdkStatic("java.math.BigInteger", "valueOf", t, v);
        else if (t.Name != "java.math.BigInteger")
        {
            if (truncate && own && vInts > it.Digits) v = JdkStatic(A.PlatformPackage + ".Records", "truncate", JType.Long, v, ELit.Int(it.Digits));
            if (t.Name == "int" && IsLong(v)) v = new ECast(JType.Int, v) { Type = JType.Int };
        }
        if (work && !it.Signed && MayBeNegative(v)) v = JdkStatic("java.lang.Math", "abs", t, v);
        info[v] = (it.Digits, 0);
        return v;
    }

    void Arith(Stmt s, List<St> o)
    {
        var ops = (s.L?.GetValueOrDefault("ops") ?? []).Select(Num).ToList();
        var giving = s.L?.GetValueOrDefault("giving");
        var dsts = s.L?.GetValueOrDefault("dst") ?? [];
        string op = s.Verb switch { "ADD" => "+", "SUBTRACT" => "-", _ => "*" };
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        // MULTIPLY a BY b [GIVING c]: b * a into c (or b)
        if (s.Verb == "MULTIPLY" && s.Get("by") is { } byNode && ops.Count == 1)
        {
            if (giving is { Count: > 0 })
            {
                var product = Arith("*", ops[0], Num(byNode));
                foreach (var g in giving) if (Item(g) is { } gi && !AppModel.IsRuntime(gi)) Assign(g, gi, product, sizeError, o, g.Rounded == true);
            }
            else if (Item(byNode) is { } bi && !AppModel.IsRuntime(bi)) Assign(byNode, bi, Arith("*", Num(byNode), ops[0]), sizeError, o, byNode.Rounded == true);
            return;
        }
        Expr sum = ops.Count == 0 ? ELit.Int(0) : ops.Skip(1).Aggregate(ops[0], (a, b) => Arith(s.Verb == "MULTIPLY" ? "*" : "+", a, b));
        if (giving is { Count: > 0 })
        {
            // ADD a b GIVING c; SUBTRACT a FROM b GIVING c
            var from = dsts.Count > 0 ? Num(dsts[0]) : null;
            var value = s.Verb == "SUBTRACT" && from is not null ? Arith("-", from, sum) : from is not null ? Arith(op, from, sum) : sum;
            foreach (var g in giving) if (Item(g) is { } gi && !AppModel.IsRuntime(gi)) Assign(g, gi, value, sizeError, o, g.Rounded == true);
            return;
        }
        foreach (var d in dsts)
        {
            if (Item(d) is not { } it || AppModel.IsRuntime(it)) continue;
            Assign(d, it, Arith(op, Num(d), sum), sizeError, o, d.Rounded == true);
        }
    }

    /// <summary>
    /// A computed value into a numeric item; ON SIZE ERROR when it does not fit the item's digits (the item keeps its
    /// value). Without SIZE ERROR the value is stored as computed: the generator relies on the PICTUREs as declared.
    /// </summary>
    void Assign(Node d, DataItem it, Expr value, List<Stmt>? sizeError, List<St> o, bool rounded = false)
    {
        var store = new SAssign(Target(d, it), Fit(value, it, rounded, truncate: false));
        if (sizeError is null || sizeError.Count == 0) { o.Add(store); return; }
        o.Add(new SIf(FunctionLifter.Not(Fits(value, it, rounded)), Block(sizeError), [store]));
    }

    /// <summary>Whether a value fits an item's digits (its fraction truncated, or rounded); a long or int by Records.fits.</summary>
    Expr Fits(Expr value, DataItem it, bool rounded)
    {
        if (!IsBig(value) && it.Scale == 0) return JdkStatic(A.PlatformPackage + ".Records", "fits", JType.Bool, value, ELit.Int(it.Digits));
        var v = Big(value);
        if (rounded) v = Jdk(v, "setScale", JType.BigDecimal, ELit.Int(it.Scale), new EStaticJdk("java.math.RoundingMode", "HALF_UP", JType.RoundingMode));
        return JdkStatic(A.PlatformPackage + ".Numbers", "fits", JType.Bool, v, ELit.Int(it.Digits), ELit.Int(it.Scale));
    }

    void Compute(Stmt s, List<St> o)
    {
        var expr = s.Get("expr")!;
        var dsts = s.L?.GetValueOrDefault("dst") ?? [];
        Expr e;
        if (expr is { K: "bin", Op: "/" } && dsts.Count > 0 && dsts.All(d => d.Rounded != true && Item(d) is { Scale: 0 })
            && Operand(expr.L!, true) is var ql && Operand(expr.R!, true) is var qr && !IsBig(ql) && !IsBig(qr))
            e = IntegerQuotient(ql, qr);
        else e = Num(expr);
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
            if (Item(d) is { } it && !AppModel.IsRuntime(it)) Assign(d, it, e, sizeError, o, d.Rounded == true);
    }

    /// <summary>
    /// DIVIDE: the quotient truncated to the receiving item's scale (ROUNDED: half up), the remainder the dividend less
    /// the quotient times the divisor; ON SIZE ERROR for a zero divisor or a quotient past the item's digits, and then
    /// neither is stored.
    /// </summary>
    void Divide(Stmt s, List<St> o)
    {
        var form = s.Str("form") ?? "";
        if (form == "INTO" && s.Get("by") is null && s.L?.GetValueOrDefault("dst") is { Count: > 0 } intoDsts)
        {
            foreach (var d in intoDsts) Divide(s, o, d);
            return;
        }
        Divide(s, o, s.Get("by"));
    }

    void Divide(Stmt s, List<St> o, Node? byNode)
    {
        var ops = (s.L?.GetValueOrDefault("ops") ?? []).Select(Num).ToList();
        var by = byNode is null ? null : Num(byNode);
        var form = s.Str("form") ?? "";
        if (ops.Count != 1 || by is null) { Res("DIVIDE form " + form, s); return; }
        Expr dividend = ops[0], divisor = by;
        if (form.StartsWith("INTO")) { dividend = by; divisor = ops[0]; }
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        var targets = (s.L?.GetValueOrDefault("giving") ?? []).ToList();
        if (targets.Count == 0 && form == "INTO" && byNode is { K: "ref" }) targets.Add(byNode);
        var rem = s.Get("rem");
        bool big = IsBig(dividend) || IsBig(divisor);
        var (dd, ds) = Info(dividend);
        var (vd, vs) = Info(divisor);
        var body = new List<St>();
        Expr? fitsAll = null;
        foreach (var g in targets)
        {
            if (Item(g) is not { } gi || AppModel.IsRuntime(gi)) continue;
            bool rounded = g.Rounded == true;
            Expr q = !big && gi.Scale == 0 && !rounded ? IntegerQuotient(dividend, divisor)
                : Jdk(Big(dividend), "divide", JType.BigDecimal, Big(divisor), ELit.Int(gi.Scale), new EStaticJdk("java.math.RoundingMode", rounded ? "HALF_UP" : "DOWN", JType.RoundingMode));
            // an integer divisor: |quotient| <= |dividend|, so the quotient fits when the dividend's integer digits do
            info[q] = (dd - Math.Max(ds, 0) + gi.Scale, gi.Scale);
            bool proven = vs == 0 && dd - Math.Max(ds, 0) <= gi.Digits - gi.Scale;
            if (sizeError is { Count: > 0 } && !proven) fitsAll = fitsAll is null ? Fits(q, gi, false) : FunctionLifter.And(fitsAll, Fits(q, gi, false));
            body.Add(new SAssign(Target(g, gi), Fit(q, gi, false, truncate: sizeError is null or { Count: 0 })));
            if (rem is not null && Item(rem) is { } ri && g == targets[0])
            {
                // the remainder of the stored quotient (an integer quotient: Java's %, its sign the dividend's)
                Expr r = !big && gi.Scale == 0 && !rounded ? new EBinary("%", dividend, divisor) { Type = IsLong(dividend) || IsLong(divisor) ? JType.Long : JType.Int }
                    : gi.Scale == 0 && !rounded ? Jdk(Big(dividend), "remainder", JType.BigDecimal, Big(divisor))
                    : Jdk(Big(dividend), "subtract", JType.BigDecimal, Jdk(Big(Target(g, gi)), "multiply", JType.BigDecimal, Big(divisor)));
                info[r] = (vd, Math.Max(ds, vs));
                bool remProven = vd - Math.Max(vs, 0) <= ri.Digits - ri.Scale && ds <= ri.Scale && vs <= ri.Scale;
                if (sizeError is { Count: > 0 } && !remProven) fitsAll = fitsAll is null ? Fits(r, ri, false) : FunctionLifter.And(fitsAll, Fits(r, ri, false));
                body.Add(new SAssign(Target(rem, ri), Fit(r, ri, false, truncate: sizeError is null or { Count: 0 })));
            }
        }
        if (sizeError is not { Count: > 0 }) { o.AddRange(body); return; }
        Expr zero = big ? new EBinary("==", Jdk(Big(divisor), "signum", JType.Int), ELit.Int(0)) { Type = JType.Bool }
            : new EBinary("==", divisor, ELit.Int(0)) { Type = JType.Bool };
        // a literal divisor is not zero
        if (divisor is ELit { Value: long dl } && dl != 0) zero = ELit.Bool(false);
        Expr bad = fitsAll is null ? zero : zero is ELit { Value: false } ? FunctionLifter.Not(fitsAll) : FunctionLifter.Or(zero, FunctionLifter.Not(fitsAll));
        if (bad is ELit { Value: false }) { o.AddRange(body); return; }
        o.Add(new SIf(bad, Block(sizeError), body));
    }

    // ------------------------------------------------------------------ SET, INITIALIZE

    /// <summary>SET condition TO TRUE: its field takes the condition's (first) value.</summary>
    void SetTrue(Stmt s, List<St> o)
    {
        foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
        {
            if (Item(d) is not { Level: 88, Parent: { } parent } c88 || c88.Values.Count == 0) { Res("SET " + d.V, s); continue; }
            if (AppModel.IsRuntime(parent)) continue;
            var lo = c88.Values[0].Low;
            var lit = lo.Quoted ? new Node { K = "str", V = lo.Text } : new Node { K = lo.Text.StartsWith("ZERO", StringComparison.OrdinalIgnoreCase) || lo.Text.StartsWith("SPACE", StringComparison.OrdinalIgnoreCase) ? "fig" : "num", V = lo.Text };
            Store(new Node { K = "ref", V = parent.Name, Id = parent.Id, Args = d.Args }, parent, lit, o);
        }
    }

    /// <summary>
    /// INITIALIZE: every elementary item of the group (FILLER and REDEFINES alternatives excepted, every occurrence) to
    /// zero when numeric, spaces otherwise. A record's group: its record class's initialize method (the codec knows the
    /// layout); a work field: its zero or empty value.
    /// </summary>
    void Initialize(Stmt s, List<St> o)
    {
        foreach (var d in s.L?.GetValueOrDefault("dst") ?? [])
        {
            if (Item(d) is not { } it || AppModel.IsRuntime(it)) continue;
            if (ap.Promoted.TryGetValue(it, out var pf))
            {
                o.Add(new SAssign(Target(d, it), pf.Type.Name switch
                {
                    "boolean" => ELit.Bool(false),
                    "java.lang.String" => ELit.Str(""),
                    "java.math.BigDecimal" => Fit(ELit.Int(0), it, false, false),
                    "long" => new ELit(0L, JType.Long) { Type = JType.Long },
                    _ => ELit.Int(0),
                }));
                continue;
            }
            var subs = (d.Args ?? []).Select(Num).ToList();
            if (A.RecordOf(ap, it) is not { } r || subs.Count != it.Dims.Count) { Res("INITIALIZE " + it.Name, s); continue; }
            if (it.IsLeaf && r.Rc.Accessors.ContainsKey(it.Name))
            {
                Store(d, it, IsNumeric(it) ? new Node { K = "num", V = "0" } : new Node { K = "fig", V = "SPACES" }, o);
                continue;
            }
            var canon = r.Rc.Canon(it.Name);
            if (!r.Rc.Initializers.TryGetValue(canon, out var method))
            {
                var taken = r.Rc.Initializers.Values.ToHashSet(StringComparer.Ordinal);
                method = it == it.Record ? "initialize" : "initialize" + Naming.Pascal(string.Join('-', canon.Split('-').Skip(canon.Contains('-') ? 1 : 0)));
                for (int k = 2; taken.Contains(method); k++) method = method.TrimEnd("0123456789".ToCharArray()) + k;
                r.Rc.Initializers[canon] = method;
            }
            o.Add(new SExpr(Jdk(RecordExpr(it.Record, r.Rc, r.Instance), method, JType.Void, [.. subs])));
        }
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
        if (loops.Count == 0) { EvaluateCore(s, o); return; }
        loops.Peek().Switches++;
        try { EvaluateCore(s, o); }
        finally { loops.Peek().Switches--; }
    }

    void EvaluateCore(Stmt s, List<St> o)
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
            var frame = new LoopFrame();
            loops.Push(frame);
            List<St> body;
            try { body = Block(s.Blocks?.GetValueOrDefault("LOOP") ?? []); }
            finally { loops.Pop(); }
            Loop(s, body, o, frame.Label, frame.Exits);
            return;
        }
        var t = s.Str("target") ?? "";
        // VARYING ... AFTER: nested variables this lift has no loop for (LegacyFlow writes them as nested loops)
        if (s.L?.ContainsKey("after") == true) { Res("PERFORM VARYING ... AFTER", s); return; }
        var thru = s.Str("thru");
        var calls = Calls(t, thru, s);
        if (calls.Count == 0) return;
        Loop(s, calls, o);
    }

    /// <summary>The calls a PERFORM makes: a paragraph's method, a THRU range in order, a job function.</summary>
    List<St> Calls(string target, string? thru, Stmt s)
    {
        var res = new List<St>();
        // the paragraph that empties FSJBDEC's store at a loan header (MOVE 'BZ' TO JB-OP): the checked decimal sites' new loan
        if (!A.M.DecimalLimitSites.IsEmpty && EmptiesDecimalStore(target))
            res.Add(new SExpr(new ECall { Kind = CallKind.JdkStatic, Owner = A.PlatformPackage + ".DecimalLimit", Name = "newLoan", Type = JType.Void }));
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
                res.Add(new SExpr(new ECall { Kind = CallKind.Instance, Name = mi, Args = LinkageArgs(P.Model.Paragraphs[i].Name) }));
        return res;
    }

    readonly Dictionary<string, bool> emptyCache = new(StringComparer.OrdinalIgnoreCase);

    bool EmptiesDecimalStore(string paragraph) =>
        P.Model.Paragraphs.FirstOrDefault(p => p.Name.Equals(paragraph, StringComparison.OrdinalIgnoreCase)) is { } para
        && Model.Deep(para.Statements).Any(b => b.Verb == "MOVE" && Model.DstName(b)?.Equals("JB-OP", StringComparison.OrdinalIgnoreCase) == true && b.Get("src") is { K: "str", V: "BZ" });

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

    /// <summary>What the last MOVE to JX-AT named (a field or a step of the job adapter): the site of JX-JOBFAIL.</summary>
    string? jobFailAt;

    /// <summary>PERFORMs of runtime paragraphs: the abend, the J3S runtime (nothing in Java).</summary>
    bool Runtime(string target, List<St> res)
    {
        var t = target.ToUpperInvariant();
        if (t is "JX-JOBFAIL" && jobFailAt is { } at)
        {
            // the job adapter's failure: the legacy run displays it and abends 38 at what JX-AT names
            A.M.JobAdapterSites.TryAdd($"{P.Id} JX-JOBFAIL {at}", "JX-JOBFAIL");
            res.Add(new SThrow(new ENew { JdkClass = A.PlatformPackage + ".Abend", Args = [ELit.Int(38), ELit.Str("job adapter failure"), ELit.Str("CEE3ABD"), ELit.Str(at), ELit.Str("JOB ADAPTER FAILURE")] }));
            return true;
        }
        if (t is "JX-JOBFAIL" or "JX-ABEND")
        {
            res.Add(new SThrow(new ENew { JdkClass = A.PlatformPackage + ".Abend", Args = [ELit.Int(38), ELit.Str(P.Id + " job adapter failure")] }));
            return true;
        }
        if (t.StartsWith("JA-") || t.StartsWith("JX-") || t.StartsWith("JG-") || t.StartsWith("JD-") || t.StartsWith("JB-") || t.StartsWith("JW-") || t.StartsWith("JH-") || t.StartsWith("JS-") || t == "JO-SIMPLE-NAME")
            return true;
        return false;
    }

    void Loop(Stmt s, List<St> body, List<St> o, string? label = null, bool exits = false)
    {
        bool after = s.Str("test") == "AFTER";
        if (after && s.Get("var") is not null) { Res("PERFORM VARYING WITH TEST AFTER", s); return; }
        if (after && s.Get("until") is { } untilAfter)
        {
            // WITH TEST AFTER: the body runs once before the condition is tested
            o.Add(new SLoop(label ?? NewLabel(), ELit.Bool(true), [.. body, new SIf(Cond(untilAfter), [new SBreak(null)], [])]));
            return;
        }
        if (s.Get("var") is { } v && Item(v) is { } vi)
        {
            // PERFORM VARYING v FROM a BY b UNTIL c
            var init = new List<St>();
            Store(v, vi, s.Get("from")!, init);
            o.AddRange(init);
            var step = new List<St>();
            Assign(v, vi, Arith("+", Num(v), Num(s.Get("by")!)), null, step);
            o.Add(new SLoop(label ?? NewLabel(), FunctionLifter.Not(Cond(s.Get("until")!)), [.. body, .. step]));
            return;
        }
        if (s.Get("until") is { } until)
        {
            o.Add(new SLoop(label ?? NewLabel(), FunctionLifter.Not(Cond(until)), body));
            return;
        }
        if (s.Get("times") is { } times)
        {
            var k = NewTemp(JType.Int, "k");
            o.Add(new SAssign(new EVar(k), ELit.Int(0)));
            o.Add(new SLoop(label ?? NewLabel(), new EBinary("<", new EVar(k), Num(times)) { Type = JType.Bool }, [.. body, new SAssign(new EVar(k), new EBinary("+", new EVar(k), ELit.Int(1)) { Type = JType.Int })]));
            return;
        }
        // a PERFORM that runs once, left by an EXIT PERFORM: a loop of one pass
        if (exits) { o.Add(new SLoop(label ?? NewLabel(), ELit.Bool(true), [.. body, new SBreak(null)])); return; }
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
            // DISPLAY writes a field's every character: a number's digits, a text field's trailing spaces
            var v = n.K == "ref" && Item(n) is { } it && (it.Category == Category.Numeric && it.Usage == Usage.Display || !it.IsLeaf
                || it.IsLeaf && it.Level != 88 && !IsNumeric(it) && n.Start is null && A.RecordOf(ap, it) is not null) ? Text(n) : Operand(n);
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
            o.Add(new SThrow(new ENew { JdkClass = A.PlatformPackage + ".Abend", Args = [code, ELit.Str(P.Id + " abend"), ELit.Str("CEE3ABD")] }));
            return;
        }
        // a call of the J3S runtime (CALL 'FSJBDEC' USING JB): its arguments are runtime storage only
        if (args.Count > 0 && args.All(a => Item(a) is { } ai0 && AppModel.IsRuntime(ai0))) return;
        if (ap.Callees.TryGetValue(target, out var sfield) && A.ById.TryGetValue(target, out var sub) && sub.IsSubprogram)
        {
            // a subprogram: the records it declares in its PROCEDURE DIVISION USING are the caller's, by reference
            var actual = new List<Expr>();
            for (int k = 0; k < args.Count; k++)
            {
                if (Item(args[k]) is not { } ai || k >= sub.LinkageOrder.Count) { Res($"CALL {target} argument {k + 1}", s); return; }
                var expected = sub.Records[sub.LinkageOrder[k]];
                if (ai != ai.Record || A.RecordOf(ap, ai) is not { } ar || ar.Rc != expected)
                {
                    Res($"CALL {target} argument {ai.Name}: not a {expected.Name}", s);
                    return;
                }
                actual.Add(RecordExpr(ai, ar.Rc, ar.Instance));
            }
            if (args.Count != sub.LinkageOrder.Count) { Res($"CALL {target}: {args.Count} arguments for {sub.LinkageOrder.Count}", s); return; }
            o.Add(new SExpr(Jdk(new EProgField(sfield, JType.Of(A.AppPackage + "." + sub.ClassName)) { Type = JType.Of(A.AppPackage + "." + sub.ClassName) }, "call", JType.Void, [.. actual])));
            return;
        }
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
