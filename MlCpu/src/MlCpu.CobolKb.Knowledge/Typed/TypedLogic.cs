using System.Text;
using MlCpu.Cobol.Analysis;
using MlCpu.Kit;

namespace MlCpu.CobolKb.Knowledge.Typed;

/// <summary>
/// The faithful program's logic rewritten mechanically onto the typed views (typed-interface projection, step 2):
/// paragraphs become methods, PERFORM a call or a loop, EVALUATE an if-chain, MOVE/COMPUTE/ADD/SUBTRACT typed assignments that
/// store with the COBOL rules (the record kit's setters for record items; <c>Storage</c> for WORKING-STORAGE kept in Java
/// fields), and a CALL of a program whose CALL groups the equivalence probe verified becomes its generated JDK operations.
/// Anything outside the supported subset is recorded as a gap and generated as an exception, never guessed.
/// </summary>
sealed class TypedLogic
{
    readonly CobolProgram m;
    readonly TypedRecords recs;
    readonly Dictionary<int, DataItem> byId;
    readonly List<string> gaps;
    readonly IReadOnlyDictionary<string, string> operations;   // callee PID → operations class
    readonly string className;

    /// <summary>Where an item lives: a member of a record view, or a Java field of WORKING-STORAGE.</summary>
    abstract record Loc;
    sealed record InView(string Var, TypedRecords.Record Rec, TypedRecords.Member Member) : Loc;
    /// <summary>A table occurrence of a record view as a whole (its bytes: a group MOVE).</summary>
    sealed record InOccurrence(string Var, TypedRecords.Record Rec, TypedRecords.View View) : Loc;
    sealed record WsField(string Var, DataItem Item, string Type, int Count = 0) : Loc;
    sealed record WsView(WsField Base, int Shift) : Loc;               // numeric REDEFINES of a numeric: the unscaled view
    sealed record WsGroup(DataItem Item, List<DataItem> Leaves) : Loc;
    /// <summary>An unsigned DISPLAY integer redefining a group of unsigned DISPLAY integers (WS-DATE-N over YYYY, MM, DD): their digits in a row.</summary>
    sealed record WsDigits(DataItem Item, List<WsField> Parts) : Loc;
    sealed record RecordRoot(string Var, TypedRecords.Record Rec) : Loc;

    readonly Dictionary<int, Loc> locs = [];
    readonly List<(string Var, string Type, string Init, string Comment)> fields = [];
    readonly List<string> arrayInits = [];
    readonly List<(string Var, TypedRecords.Record Rec, int Arg)> arguments = [];
    readonly List<(string Var, TypedRecords.Record Rec)> workingRecords = [];
    readonly Dictionary<string, string> methodOf = new(StringComparer.Ordinal);
    int temp;

    /// <summary>CALLed programs that run their embedded typed reference: PID → (wrapper class, entry class).</summary>
    readonly IReadOnlyDictionary<string, (string Wrapper, string Entry)> embedded;

    public TypedLogic(CobolProgram m, TypedRecords recs, string className, IReadOnlyDictionary<string, string> operations, List<string> gaps,
        IReadOnlyDictionary<string, (string Wrapper, string Entry)>? embedded = null)
    {
        this.embedded = embedded ?? new Dictionary<string, (string, string)>();
        this.m = m;
        this.recs = recs;
        this.className = className;
        this.operations = operations;
        this.gaps = gaps;
        byId = m.Items.ToDictionary(i => i.Id);
    }

    public IReadOnlyList<(string Var, TypedRecords.Record Rec, int Arg)> Arguments => arguments;

    // ------------------------------------------------------------------ storage

    public void Bind()
    {
        var usedVars = new HashSet<string>(StringComparer.Ordinal) { "console" };
        string Var(string cobol)
        {
            var v = TypedRecords.Camel(System.Text.RegularExpressions.Regex.Replace(cobol, "^(WS|LS)-", ""));
            if (v is "class" or "new" or "int" or "long" or "default" or "switch" or "case" or "this") v += "Value";
            var n = v;
            for (int i = 2; !usedVars.Add(n); i++) n = v + i;
            return n;
        }
        // Arguments (LINKAGE, in USING order) and WORKING-STORAGE areas the record kit has (CALL USING areas).
        for (int a = 0; a < m.Using.Count; a++)
        {
            var root = m.Items.FirstOrDefault(i => i.Section == DataSection.Linkage && i.Level is 1 or 77 && i.Name.Equals(m.Using[a], StringComparison.OrdinalIgnoreCase));
            if (root is null || !recs.ByLayout.TryGetValue(root.Name, out var rec)) { gaps.Add($"USING {m.Using[a]}: no record kit view"); continue; }
            var v = Var(VarName(rec.Interface));
            arguments.Add((v, rec, a));
            BindRecord(root, v, rec);
        }
        foreach (var root in m.Items.Where(i => i.Level is 1 or 77 && i.Section is DataSection.WorkingStorage or DataSection.LocalStorage))
        {
            if (recs.ByLayout.TryGetValue(root.Name, out var rec) && root.RedefinesItem is null)
            {
                var v = Var(VarName(rec.Interface));
                workingRecords.Add((v, rec));
                BindRecord(root, v, rec);
                continue;
            }
            BindWorking(root, Var);
        }
    }

    /// <summary>A variable for a view, from its interface name (LoanRequest → loanRequest; MathContextRecord → mathContext).</summary>
    static string VarName(string iface)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(iface, "(Record|View)$", "");
        if (s.Length < 3) s = iface;
        return System.Text.RegularExpressions.Regex.Replace(s, "(?<=[a-z0-9])(?=[A-Z])", "-").ToUpperInvariant();
    }

    void BindRecord(DataItem root, string var, TypedRecords.Record rec)
    {
        locs[root.Id] = new RecordRoot(var, rec);
        foreach (var it in Tree(root).Skip(1))
            if (recs.Members.TryGetValue((rec.Layout.Name, it.Name), out var mem)) locs[it.Id] = new InView(var, rec, mem);
            else if (recs.Occurrences.TryGetValue((rec.Layout.Name, it.Name), out var occ) && occ.Owner is not null) locs[it.Id] = new InOccurrence(var, rec, occ);
            // The same bytes under another name (a copybook under another prefix): by place in the record.
            else if (recs.ByPlace(rec.Layout.Name, it.Offset - root.Offset, it.Length, it.Dims.Count - root.Dims.Count) is { } byPlace)
                locs[it.Id] = new InView(var, rec, byPlace);
            else if (recs.OccurrenceByPlace(rec.Layout.Name, it.Offset - root.Offset, it.Length, it.Dims.Count - root.Dims.Count) is { } occPlace)
                locs[it.Id] = new InOccurrence(var, rec, occPlace);
    }

    static bool UnderRedefines(DataItem it, DataItem root)
    {
        for (var x = it; x is not null && x != root; x = x.Parent) if (x.RedefinesItem is not null) return true;
        return false;
    }

    static IEnumerable<DataItem> Tree(DataItem root)
    {
        yield return root;
        foreach (var c in root.Children.Where(c => c.Level != 88))
            foreach (var x in Tree(c)) yield return x;
    }

    static string Kind(DataItem it) => it.Category switch
    {
        Category.Group => "group",
        Category.Alphanumeric or Category.Alphabetic => "text",
        Category.NumericEdited => "edited",
        Category.Numeric => it.Usage switch { Usage.Display => "zoned", Usage.Comp3 => "packed", Usage.Binary or Usage.Comp5 => "binary", _ => "other" },
        _ => "other"
    };

    void BindWorking(DataItem root, Func<string, string> var)
    {
        if (root.Dims.Count > 0 || root.Occurs > 0) { gaps.Add($"{root.Name}: OCCURS in WORKING-STORAGE outside the record kit"); return; }
        // A numeric 01 REDEFINES of a numeric with the same bytes and digits: the same number at another scale.
        if (root.RedefinesItem is { } baseItem && locs.TryGetValue(baseItem.Id, out var bl) && bl is WsField bf && Kind(root) == Kind(baseItem)
            && Kind(root) is "packed" or "zoned" && root.Length == baseItem.Length && root.Digits == baseItem.Digits && root.Signed == baseItem.Signed)
        {
            locs[root.Id] = new WsView(bf, baseItem.Scale - root.Scale);
            return;
        }
        // An unsigned DISPLAY integer over a group of unsigned DISPLAY integers of the same digits: their digits in a row.
        if (root.RedefinesItem is { } g2 && root.IsLeaf && Kind(root) == "zoned" && !root.Signed && root.Scale == 0 && locs.GetValueOrDefault(g2.Id) is WsGroup wg
            && wg.Leaves.All(l => Kind(l) == "zoned" && !l.Signed && l.Scale == 0 && locs.GetValueOrDefault(l.Id) is WsField) && wg.Leaves.Sum(l => l.Digits) == root.Digits
            && root.Length == g2.Length)
        {
            locs[root.Id] = new WsDigits(root, wg.Leaves.Select(l => (WsField)locs[l.Id]).ToList());
            return;
        }
        if (root.RedefinesItem is not null) { gaps.Add($"{root.Name} REDEFINES {root.RedefinesItem.Name}: not a numeric view of a numeric item"); return; }
        var leaves = Tree(root).Where(i => i.IsLeaf).ToList();
        foreach (var it in leaves)
        {
            bool table = it.Dims.Count == 1 && it.Occurs > 0;
            if (it.Dims.Count > 0 && !table) { gaps.Add($"{it.Name}: a WORKING-STORAGE group table outside the record kit"); continue; }
            var kind = Kind(it);
            var type = kind switch
            {
                "binary" when it.Scale == 0 && it.Signed && it.Length <= 4 => "int",
                "binary" when it.Scale == 0 => "long",
                "packed" or "zoned" or "binary" => "BigDecimal",
                _ => "String"
            };
            var v = var(it.Name);
            var pic = it.Pic is null ? "" : " PIC " + it.Pic + (kind switch { "packed" => " COMP-3", "binary" => it.Usage == Usage.Comp5 ? " COMP-5" : " COMP", _ => "" });
            if (table)
            {
                // An elementary table: a Java array, element k-1 for occurrence k.
                fields.Add((v, type + "[]", $"new {type}[{it.Occurs}]", $"{it.Name}{pic} OCCURS {it.Occurs}"));
                var init = Initial(it, kind, type);
                if (type is "BigDecimal" or "String" || init is not ("0" or "0L")) arrayInits.Add($"java.util.Arrays.fill({v}, {init});");
                locs[it.Id] = new WsField(v, it, type, it.Occurs);
                continue;
            }
            fields.Add((v, type, Initial(it, kind, type), it.Name + pic));
            locs[it.Id] = new WsField(v, it, type);
        }
        if (!root.IsLeaf) locs[root.Id] = new WsGroup(root, leaves);
        // Group items under the record: their leaves.
        foreach (var g in Tree(root).Skip(1).Where(i => !i.IsLeaf)) locs[g.Id] = new WsGroup(g, Tree(g).Where(i => i.IsLeaf).ToList());
    }

    static string Initial(DataItem it, string kind, string type)
    {
        var value = it.Values.FirstOrDefault().Low;
        if (type == "String")
        {
            if (value is null) return Str(new string('\0', it.Length));
            var t = value.Quoted ? value.Text : value.Text.ToUpperInvariant() switch { "SPACE" or "SPACES" => "", "ZERO" or "ZEROS" or "ZEROES" => new string('0', it.Length), _ => value.Text };
            return kind == "edited" ? Str(t.PadRight(it.Length)) : Str(t.PadRight(it.Length)[..it.Length].TrimEnd(' '));
        }
        var num = value is null || !decimal.TryParse(value.Text, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? 0m : d;
        if (type == "int") return ((long)num).ToString();
        if (type == "long") return ((long)num).ToString() + "L";
        return num == 0 ? $"BigDecimal.ZERO.setScale({Math.Max(0, it.Scale)})" : $"new BigDecimal(\"{num.ToString(System.Globalization.CultureInfo.InvariantCulture)}\").setScale({Math.Max(0, it.Scale)})";
    }

    // ------------------------------------------------------------------ expressions

    /// <summary>
    /// A Java expression and its type: D BigDecimal, L long (or int), S text (without trailing spaces), R raw bytes of a group,
    /// F figurative. For numbers, what is known of the value: its scale and an upper bound of its integer digits (-1: unknown),
    /// the constant, and whether the Java expression is an int (a subscript needs no cast).
    /// </summary>
    sealed record Ex(string Code, char T, string? Fig = null, int Scale = -1, int Int = -1, decimal? Const = null, bool IsInt = false);

    /// <summary>Decimal digits a binary item of <paramref name="bytes"/> bytes can hold (binary-truncate no: its capacity, not its PICTURE).</summary>
    static int CapacityDigits(int bytes) => bytes switch { 1 => 3, 2 => 5, 4 => 10, 8 => 19, _ => (int)Math.Ceiling(bytes * 8 * Math.Log10(2)) };

    static Ex Number(string code, char t, int scale, int intDigits, bool isInt = false) => new(code, t, null, scale, intDigits, null, isInt);

    static string Str(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
            sb.Append(c switch { '"' => "\\\"", '\\' => "\\\\", '\0' => "\\0", < ' ' or > '~' => $"\\u{(int)c:x4}", _ => c.ToString() });
        return sb.Append('"').ToString();
    }

    static Ex Dec(Ex e) => e.T switch
    {
        'D' => e,
        'L' => e.Const is { } c ? new Ex(c == 0 ? "BigDecimal.ZERO" : c == 1 ? "BigDecimal.ONE" : c == 10 ? "BigDecimal.TEN" : $"BigDecimal.valueOf({(long)c}L)", 'D', null, 0, e.Int, c)
                                 : new Ex($"BigDecimal.valueOf({e.Code})", 'D', null, 0, e.Int),
        'F' when e.Fig == "ZERO" => new Ex("BigDecimal.ZERO", 'D', null, 0, 0, 0m),
        _ => throw new NotSupportedException($"a number expected, found {e.Code}")
    };

    /// <summary>A long literal for arithmetic (long, so that int operands cannot overflow), or a plain one elsewhere.</summary>
    static string LongLit(decimal c) => (long)c + "L";

    string Path(InView v, Node? refNode) => Path(v.Var, v.Member.Owner, refNode);

    string Path(string var, TypedRecords.View owner, Node? refNode)
    {
        var sb = new StringBuilder(var);
        var chain = new List<TypedRecords.View>();
        for (var w = owner; w is not null && w.Depth > 0; w = w.Parent) chain.Insert(0, w);
        var args = refNode?.Args ?? [];
        if (args.Count < chain.Count) throw new NotSupportedException($"{refNode?.V}: {chain.Count} subscript(s) expected");
        for (int i = 0; i < chain.Count; i++) sb.Append('.').Append(chain[i].Accessor).Append('(').Append(Int(Expr(args[i]))).Append(')');
        return sb.ToString();
    }

    static string Int(Ex e) => e.T switch
    {
        'L' when e.Const is { } c => ((long)c).ToString(),
        'L' when e.IsInt => e.Code,
        'L' => $"(int) {Paren(e.Code)}",
        'D' => $"{Paren(e.Code)}.intValue()",
        _ => throw new NotSupportedException("a subscript must be numeric")
    };

    /// <summary>Parentheses only when the expression has an operator outside parentheses (a call chain needs none).</summary>
    static string Paren(string c)
    {
        int depth = 0;
        bool inString = false;
        for (int i = 0; i < c.Length; i++)
        {
            char ch = c[i];
            if (inString) { if (ch == '\\') i++; else if (ch == '"') inString = false; continue; }
            if (ch == '"') inString = true;
            else if (ch is '(' or '[') depth++;
            else if (ch is ')' or ']') depth--;
            else if (depth == 0 && (ch is ' ' or '+' or '*' or '/' or '<' or '>' or '=' or '!' or '&' or '|' or '?' or ':' || (ch == '-' && i > 0))) return "(" + c + ")";
        }
        return c;
    }

    Ex Read(Node n)
    {
        if (n.Id is not { } id || !locs.TryGetValue(id, out var loc))
            throw new NotSupportedException($"{n.V}: no storage");
        if (n.Start is not null) throw new NotSupportedException($"{n.V}: reference modification");
        switch (loc)
        {
            case InView v:
            {
                var p = Path(v, n) + "." + v.Member.Name + (v.Member.Enum is not null ? "Code" : "") + "()";
                var k = v.Member.Kit;
                if (v.Member.Group) return new Ex(p, 'R');
                return v.Member.Type switch
                {
                    "long" => Number(p, 'L', 0, k.Kind == "binary" ? CapacityDigits(k.Length) : k.Digits - Math.Max(0, k.Scale)),
                    "BigDecimal" => Number(p, 'D', Math.Max(0, k.Scale), k.Kind == "binary" ? CapacityDigits(k.Length) : k.Digits - Math.Max(0, k.Scale)),
                    _ => new Ex(p, 'S')
                };
            }
            case WsField f:
            {
                var it = f.Item;
                var code = f.Count > 0 ? $"{f.Var}[{Sub(n)}]" : f.Var;
                return f.Type switch
                {
                    "int" => Number(code, 'L', 0, CapacityDigits(it.Length), true),
                    "long" => Number(code, 'L', 0, CapacityDigits(it.Length)),
                    "BigDecimal" => Number(code, 'D', Math.Max(0, it.Scale), it.Digits - Math.Max(0, it.Scale)),
                    _ => new Ex(code, 'S')
                };
            }
            case WsView w: return Number($"{w.Base.Var}.movePointRight({w.Shift})", 'D', Math.Max(0, w.Base.Item.Scale - w.Shift), w.Base.Item.Digits - Math.Max(0, w.Base.Item.Scale - w.Shift));
            case WsDigits dg:
            {
                int after = dg.Item.Digits;
                var terms = dg.Parts.Select(p =>
                {
                    after -= p.Item.Digits;
                    var v = p.Type == "BigDecimal" ? $"{p.Var}.longValue()" : p.Var;
                    return after == 0 ? v : $"{v} * {(long)Math.Pow(10, after)}L";
                });
                return Number(string.Join(" + ", terms), 'L', 0, dg.Item.Digits);
            }
            case RecordRoot r: return new Ex($"new String({r.Var}.bytes(), java.nio.charset.StandardCharsets.ISO_8859_1)", 'R');
            case InOccurrence o: return new Ex($"{Path(o.Var, o.View, n)}.raw()", 'R');
            default: throw new NotSupportedException($"{n.V}: a group outside the record kit is not read as a whole");
        }
    }

    Ex Expr(Node n)
    {
        switch (n.K)
        {
            case "ref": return Read(n);
            case "num":
            {
                var t = n.V!.TrimStart('+');
                var digits = t.TrimStart('-').Split('.');
                int intDigits = digits[0].TrimStart('0').Length, scale = digits.Length > 1 ? digits[1].Length : 0;
                if (long.TryParse(t, out var l)) return new Ex(LongLit(l), 'L', null, 0, intDigits, l);
                return new Ex($"new BigDecimal(\"{t}\")", 'D', null, scale, intDigits,
                    decimal.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var d) ? d : null);
            }
            case "str": return new Ex(Str(n.V!.TrimEnd(' ')), 'S');
            case "fig":
            {
                var f = n.V!.ToUpperInvariant().TrimEnd('S').Replace("ZEROE", "ZERO");
                return new Ex("", 'F', f switch { "ZERO" => "ZERO", "SPACE" => "SPACE", "LOW-VALUE" => "LOW", "HIGH-VALUE" => "HIGH", _ => f });
            }
            case "neg":
            {
                var e = Expr(n.L!);
                return e.T == 'L' ? new Ex($"-{Paren(e.Code)}", 'L', null, 0, e.Int, -e.Const) : new Ex($"{Paren(Dec(e).Code)}.negate()", 'D', null, e.Scale, e.Int, -e.Const);
            }
            case "bin": return Arith(Expr(n.L!), Expr(n.R!), n.Op!);
            case "fn":
            {
                var args = n.Args ?? [];
                switch (n.V)
                {
                    case "LENGTH" when args.Count == 1 && args[0].Id is { } id && byId.TryGetValue(id, out var it):
                        return new Ex(LongLit(it.Length), 'L', null, 0, it.Length.ToString().Length, it.Length);
                    case "INTEGER-OF-DATE" when args.Count == 1:
                        return Number($"Storage.integerOfDate({Long(Expr(args[0]))})", 'L', 0, 7);
                    case "DATE-OF-INTEGER" when args.Count == 1:
                        return Number($"Storage.dateOfInteger({Long(Expr(args[0]))})", 'L', 0, 8);
                }
                throw new NotSupportedException($"FUNCTION {n.V}");
            }
            default: throw new NotSupportedException($"expression kind {n.K}");
        }
    }

    /// <summary>The Java index of an element of a WORKING-STORAGE table (COBOL subscripts count from 1).</summary>
    string Sub(Node n)
    {
        if (n.Args is not { Count: 1 } a) throw new NotSupportedException($"{n.V}: one subscript expected");
        var e = Expr(a[0]);
        if (e.Const is { } c) return ((long)c - 1).ToString();
        // (k + 1) - 1 is k, for an int counter.
        if (e.T == 'L' && System.Text.RegularExpressions.Regex.Match(e.Code, @"^(\w+) \+ 1L$") is { Success: true } plus1
            && fields.Any(f => f.Var == plus1.Groups[1].Value && f.Type == "int")) return plus1.Groups[1].Value;
        return Int(e) + " - 1";
    }

    static string Long(Ex e) => e.T == 'L' ? e.Code : e.T == 'D' ? $"{Paren(e.Code)}.longValue()" : throw new NotSupportedException("a number expected");

    /// <summary>a + b, a - b, a × b: exact (long when it surely fits, else BigDecimal), with the scale and integer digits it can have.</summary>
    static Ex Arith(Ex a, Ex b, string op)
    {
        bool known = a.Scale >= 0 && b.Scale >= 0 && a.Int >= 0 && b.Int >= 0;
        int scale = !known ? -1 : op == "*" ? a.Scale + b.Scale : Math.Max(a.Scale, b.Scale);
        int ints = !known ? -1 : op == "*" ? a.Int + b.Int : Math.Max(a.Int, b.Int) + 1;
        if (a.T == 'L' && b.T == 'L' && op is "+" or "-" or "*" && ints is >= 0 and <= 18)
            return new Ex($"{a.Code} {op} {Wrap(b.Code, op)}", 'L', null, 0, ints);
        var (da, db) = (Dec(a), Dec(b));
        return op switch
        {
            "+" => new Ex($"{Paren(da.Code)}.add({db.Code})", 'D', null, scale, ints),
            "-" => new Ex($"{Paren(da.Code)}.subtract({db.Code})", 'D', null, scale, ints),
            "*" => new Ex($"{Paren(da.Code)}.multiply({db.Code})", 'D', null, scale, ints),
            _ => throw new NotSupportedException($"arithmetic operator {op} (COBOL division scale) is not projected yet")
        };
    }

    /// <summary>
    /// DIVIDE a BY b GIVING q [REMAINDER r]: the quotient truncated (or ROUNDED) at q's scale, the remainder a - q × b with
    /// q as stored; a zero divisor or a quotient beyond q's PICTURE is the ON SIZE ERROR condition (q and r unchanged).
    /// </summary>
    string Divide(Stmt s, string ind)
    {
        var form = s.Str("form") ?? "";
        if (form != "BY-GIVING") throw new NotSupportedException($"DIVIDE {form}");
        var giving = s.L!["giving"];
        if (giving.Count != 1) throw new NotSupportedException("DIVIDE with several receivers");
        var q = giving[0];
        var a = Dec(Expr(s.L!["ops"][0]));
        var b = Dec(Expr(s.Get("by")!));
        var rem = s.Get("rem");
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        var sb = new StringBuilder();
        int scale = ScaleOf(q);
        var (dv, qv) = ($"d{++temp}", $"q{temp}");
        sb.Append($"{ind}BigDecimal {dv} = {b.Code};\n");
        sb.Append($"{ind}if ({dv}.signum() == 0) {{\n");
        if (sizeError is { Count: > 0 }) Block(sb, sizeError, ind + "    ");
        else sb.Append($"{ind}    // a zero divisor: the receivers keep their values\n");
        sb.Append($"{ind}}} else {{\n");
        var quotient = $"{Paren(a.Code)}.divide({dv}, {scale}, RoundingMode.{(q.Rounded == true ? "HALF_UP" : "DOWN")})";
        sb.Append($"{ind}    BigDecimal {qv} = {quotient};\n");
        var inner = new StringBuilder();
        inner.Append(Store(q, new Ex(qv, 'D', null, scale, -1), false, sizeError is { Count: > 0 })).Append('\n');
        if (rem is not null) inner.Append(Store(rem, new Ex($"{Paren(a.Code)}.subtract({Paren(Dec(Read(q)).Code)}.multiply({dv}))", 'D'))).Append('\n');
        if (sizeError is { Count: > 0 })
        {
            sb.Append($"{ind}    if ({Overflow(q, new Ex(qv, 'D'))}) {{\n");
            Block(sb, sizeError, ind + "        ");
            sb.Append($"{ind}    }} else {{\n");
            foreach (var l in inner.ToString().TrimEnd('\n').Split('\n')) sb.Append($"{ind}        {l}\n");
            sb.Append($"{ind}    }}\n");
        }
        else foreach (var l in inner.ToString().TrimEnd('\n').Split('\n')) sb.Append($"{ind}    {l}\n");
        sb.Append($"{ind}}}\n");
        return sb.ToString();
    }

    static string Wrap(string code, string? op) => op == "-" && (code.Contains(' ') || code.StartsWith('-')) ? "(" + code + ")" : code;

    string Cond(Node n)
    {
        switch (n.K)
        {
            case "and": return $"{CondP(n.L!, "and")} && {CondP(n.R!, "and")}";
            case "or": return $"{CondP(n.L!, "or")} || {CondP(n.R!, "or")}";
            case "not": return $"!{Atom(Cond(n.L!))}";
            case "bool": return n.V == "TRUE" ? "true" : "false";
            case "any": return "true";
            case "c88": return Condition88(n);
            case "rel": return Relation(n.Op!, Expr(n.L!), Expr(n.R!));
            default: throw new NotSupportedException($"condition kind {n.K}");
        }
    }

    string CondP(Node n, string within)
    {
        var c = Cond(n);
        return (n.K is "and" or "or") && n.K != within ? "(" + c + ")" : c;
    }

    static string Atom(string c) => c.StartsWith('(') || System.Text.RegularExpressions.Regex.IsMatch(c, @"^[\w.()]+$") ? c : "(" + c + ")";

    static string Op(string op) => op switch { "eq" => "==", "ne" => "!=", "lt" => "<", "gt" => ">", "le" => "<=", "ge" => ">=", _ => throw new NotSupportedException(op) };

    string Relation(string op, Ex a, Ex b)
    {
        if (a.T == 'F') (a, b, op) = (b, a, op switch { "lt" => "gt", "gt" => "lt", "le" => "ge", "ge" => "le", _ => op });
        if (b.T == 'F')
        {
            if (b.Fig == "ZERO" && a.T is 'D' or 'L') b = a.T == 'L' ? new Ex("0L", 'L') : new Ex("BigDecimal.ZERO", 'D');
            else if (b.Fig == "SPACE" && a.T == 'S') return op switch { "eq" => $"{a.Code}.isEmpty()", "ne" => $"!{a.Code}.isEmpty()", _ => throw new NotSupportedException("SPACES ordering") };
            else throw new NotSupportedException($"comparison with {b.Fig}");
        }
        if (a.T is 'D' or 'L' && b.T is 'D' or 'L')
        {
            if (a.Const is not null && b.Const is null) (a, b, op) = (b, a, op switch { "lt" => "gt", "gt" => "lt", "le" => "ge", "ge" => "le", _ => op });
            if (a.T == 'L' && b.T == 'L') return $"{a.Code} {Op(op)} {(b.Const is { } c ? ((long)c).ToString() : b.Code)}";
            if (b.Const == 0m) return $"{Paren(Dec(a).Code)}.signum() {Op(op)} 0";
            return $"{Paren(Dec(a).Code)}.compareTo({Dec(b).Code}) {Op(op)} 0";
        }
        if (a.T == 'S' && b.T == 'S')
        {
            if (op == "eq") return b.Code.StartsWith('"') ? $"{b.Code}.equals({a.Code})" : $"{a.Code}.equals({b.Code})";
            if (op == "ne") return b.Code.StartsWith('"') ? $"!{b.Code}.equals({a.Code})" : $"!{a.Code}.equals({b.Code})";
            return $"Storage.compareText({a.Code}, {b.Code}) {Op(op)} 0";
        }
        throw new NotSupportedException($"comparison of {a.T} with {b.T}");
    }

    string Condition88(Node n)
    {
        var cond = ItemOf(n);
        if (recs.Conditions.TryGetValue(cond.Name, out var c) && cond.Parent is { } parent && locs.TryGetValue(parent.Id, out var ploc) && ploc is InView v)
        {
            var path = Path(v, n);
            if (c.Constant is not null) return $"{path}.{c.Field.Name}() == {v.Rec.Interface}.{(v.Member.Owner.Depth > 0 ? v.Member.Owner.Interface + "." : "")}{c.Field.Enum!.Type}.{c.Constant}";
            return $"{path}.{c.Tester!.Value.Name}()";
        }
        if (cond.Parent is { } p && locs.TryGetValue(p.Id, out var wl) && wl is WsField f)
            return string.Join(" || ", cond.Values.Select(val => ValueTest(f, val)));
        // An 88-level name the views do not carry (its item is the same bytes under another name): its values on the item.
        if (cond.Parent is { } q && locs.TryGetValue(q.Id, out var ql) && ql is InView)
        {
            var item = new Node { K = "ref", Id = q.Id, V = q.Name, Args = n.Args };
            Ex Lit(RawWord w) => w.Quoted ? new Ex(Str(w.Text.TrimEnd(' ')), 'S') : Expr(new Node { K = "num", V = w.Text });
            return string.Join(" || ", cond.Values.Select(val => val.High is null
                ? Relation("eq", Read(item), Lit(val.Low))
                : $"({Relation("ge", Read(item), Lit(val.Low))} && {Relation("le", Read(item), Lit(val.High))})"));
        }
        throw new NotSupportedException($"88 {cond.Name}");
    }

    string ValueTest(WsField f, (RawWord Low, RawWord? High) val)
    {
        if (f.Type == "String") return $"{Str(val.Low.Text.TrimEnd(' '))}.equals({f.Var})";
        if (val.High is null) return f.Type is "long" or "int" ? $"{f.Var} == {val.Low.Text}" : $"{f.Var}.compareTo(new BigDecimal(\"{val.Low.Text}\")) == 0";
        return f.Type is "long" or "int" ? $"({f.Var} >= {val.Low.Text} && {f.Var} <= {val.High.Text})"
            : $"({f.Var}.compareTo(new BigDecimal(\"{val.Low.Text}\")) >= 0 && {f.Var}.compareTo(new BigDecimal(\"{val.High.Text}\")) <= 0)";
    }

    // ------------------------------------------------------------------ stores

    /// <summary>
    /// The statement that stores <paramref name="value"/> into the item, with the COBOL rules of its category. A value that
    /// provably fits (its scale and integer digits are within the PICTURE, or <paramref name="fits"/>: the ON SIZE ERROR test
    /// just passed) is assigned directly; a binary item wraps as a Java short or int of its size does.
    /// </summary>
    string Store(Node dst, Ex value, bool rounded = false, bool fits = false)
    {
        if (dst.Id is not { } id || !locs.TryGetValue(id, out var loc)) throw new NotSupportedException($"{dst.V}: no storage");
        if (dst.Start is not null) throw new NotSupportedException($"{dst.V}: reference modification");
        switch (loc)
        {
            case InView v:
            {
                var k = v.Member.Kit;
                // A code with an 88-level name: the enum constant.
                if (v.Member.Enum is { } en && value.T is 'S' or 'L' && (value.T == 'S' ? Unquote(value.Code) : value.Const?.ToString(System.Globalization.CultureInfo.InvariantCulture)) is { } lit
                    && en.Values.FirstOrDefault(x => en.Text ? x.Value.TrimEnd(' ') == lit : decimal.TryParse(x.Value, out var xv) && xv.ToString(System.Globalization.CultureInfo.InvariantCulture) == lit) is { Constant: not null } match)
                    return $"{Path(v, dst)}.{v.Member.Name}({v.Rec.Interface}.{(v.Member.Owner.Depth > 0 ? v.Member.Owner.Interface + "." : "")}{en.Type}.{match.Constant});";
                var target = Path(v, dst) + "." + v.Member.Name + (v.Member.Enum is not null ? "Code" : "");
                if (v.Member.Group || v.Member.Type == "String") return $"{target}({TextOf(value, k.Length, k)});";
                if (rounded && k.Scale >= 0) value = new Ex($"{Paren(Dec(value).Code)}.setScale({k.Scale}, RoundingMode.HALF_UP)", 'D', null, k.Scale, value.Int);
                return $"{target}({NumberOf(value, v.Member.Type)});";
            }
            case WsField f:
            {
                var it = f.Item;
                var kind = Kind(it);
                var target = f.Count > 0 ? $"{f.Var}[{Sub(dst)}]" : f.Var;
                if (f.Type == "String")
                {
                    if (kind == "edited")
                        return value.T is 'D' or 'L' or 'F' ? $"{target} = Pic.edit({Dec(value.T == 'F' ? new Ex("BigDecimal.ZERO", 'D') : value).Code}, \"{it.Pic}\");"
                            : throw new NotSupportedException($"{it.Name}: text into an edited item");
                    return value.T == 'S' && value.Code.StartsWith('"') && Unquote(value.Code)!.Length <= it.Length
                        ? $"{target} = {value.Code};" : $"{target} = Storage.text({TextOf(value, it.Length, null)}, {it.Length});";
                }
                if (value.T is 'S' or 'R') throw new NotSupportedException($"{it.Name}: text into a number");
                value = Fig0(value);
                if (rounded) value = new Ex($"{Paren(Dec(value).Code)}.setScale({it.Scale}, RoundingMode.HALF_UP)", 'D', null, it.Scale, value.Int);
                if (f.Type is "int" or "long")
                {
                    int cap = CapacityDigits(it.Length);
                    bool lossless = value.T == 'L' && (value.Const is { } c ? Math.Abs(c) < (decimal)Math.Pow(2, it.Length * 8 - 1) : value.Int >= 0 && value.Int < cap) && it.Signed;
                    if (lossless) return value.Const is { } cc ? $"{target} = {(long)cc}{(f.Type == "long" ? "L" : "")};" : $"{target} = {(f.Type == "int" && !value.IsInt ? "(int) " + Paren(value.Code) : value.Code)};";
                    var wrapTo = it.Signed ? it.Length switch { 2 => "short", 4 => "int", 8 => "long", _ => null } : null;
                    if (wrapTo is not null)
                    {
                        var asLong = value.T == 'L' ? Paren(value.Code) : $"{Paren(value.Code)}.longValue()";
                        return wrapTo == "long" ? $"{target} = {asLong};" : $"{target} = ({wrapTo}) {asLong};";
                    }
                    return $"{target} = {(f.Type == "int" ? "(int) " : "")}Storage.binary({NumberOf(value, "any")}, {it.Length}, {(it.Signed ? "true" : "false")});";
                }
                int scale = Math.Max(0, it.Scale);
                bool fit = value.Scale >= 0 && value.Scale <= scale && (fits || (value.Int >= 0 && value.Int <= it.Digits - scale)) && (it.Signed || value.Const >= 0);
                if (fit)
                {
                    var dv = Dec(value);
                    return value.Scale == scale ? $"{target} = {dv.Code};" : $"{target} = {Paren(dv.Code)}.setScale({scale});";
                }
                return $"{target} = Storage.decimal({Dec(value).Code}, {it.Digits}, {it.Scale}, {(it.Signed ? "true" : "false")});";
            }
            case WsView w:
            {
                var it = w.Base.Item;
                return $"{w.Base.Var} = Storage.decimal({Paren(Dec(Fig0(value)).Code)}.movePointLeft({w.Shift}), {it.Digits}, {it.Scale}, {(it.Signed ? "true" : "false")});";
            }
            case WsDigits dg:
            {
                var t = $"t{++temp}";
                var sb = new StringBuilder($"{{ long {t} = Storage.decimal({Dec(Fig0(value)).Code}, {dg.Item.Digits}, 0, false).longValue();");
                int after = dg.Item.Digits;
                foreach (var p in dg.Parts)
                {
                    after -= p.Item.Digits;
                    var part = $"{t}{(after > 0 ? $" / {(long)Math.Pow(10, after)}L" : "")} % {(long)Math.Pow(10, p.Item.Digits)}L";
                    sb.Append($" {p.Var} = {(p.Type == "BigDecimal" ? $"BigDecimal.valueOf({part})" : p.Type == "int" ? $"(int) ({part})" : part)};");
                }
                return sb.Append(" }").ToString();
            }
            case RecordRoot r:
                if (value.T == 'F') return value.Fig switch
                {
                    "SPACE" => $"java.util.Arrays.fill({r.Var}.bytes(), (byte) ' ');",
                    "ZERO" => $"java.util.Arrays.fill({r.Var}.bytes(), (byte) '0');",
                    "LOW" => $"java.util.Arrays.fill({r.Var}.bytes(), (byte) 0);",
                    _ => throw new NotSupportedException("MOVE " + value.Fig)
                };
                return $"Storage.move({TextOf(value, r.Rec.Layout.Length, null)}, {r.Var}.bytes());";
            case InOccurrence o:
                return $"{Path(o.Var, o.View, dst)}.raw({TextOf(value, o.View.Owner!.Length, null)});";
            case WsGroup g:
                throw new NotSupportedException($"{g.Item.Name}: a WORKING-STORAGE group outside the record kit is not stored as a whole");
        }
        throw new NotSupportedException(dst.V);
    }

    static Ex Fig0(Ex e) => e.T == 'F' ? (e.Fig == "ZERO" ? new Ex("0L", 'L', null, 0, 0, 0m) : throw new NotSupportedException("MOVE " + e.Fig + " to a number")) : e;

    /// <summary>The text of a Java string literal ("…"), or null.</summary>
    static string? Unquote(string code) =>
        code.Length >= 2 && code[0] == '"' && code[^1] == '"' && !code[1..^1].Contains('\\') && !code[1..^1].Contains('"') ? code[1..^1] : null;

    static string NumberOf(Ex e, string type)
    {
        e = Fig0(e);
        if (e.T == 'L') return e.Code;
        if (e.T == 'D') return e.Code;
        throw new NotSupportedException("text into a number");
    }

    /// <summary>The text a MOVE stores in a text or group item of <paramref name="length"/> bytes.</summary>
    static string TextOf(Ex e, int length, KitField? k)
    {
        switch (e.T)
        {
            case 'S': case 'R': return e.Code;
            case 'F':
                return e.Fig switch
                {
                    "SPACE" => "\"\"",
                    "ZERO" => Str(new string('0', length)),
                    "LOW" => Str(new string('\0', length)),
                    "HIGH" => Str(new string('ÿ', length)),
                    _ => throw new NotSupportedException(e.Fig)
                };
            case 'L': case 'D':
                throw new NotSupportedException("a number moved to a text item (the projection keeps such MOVEs out)");
        }
        throw new NotSupportedException("text");
    }

    // ------------------------------------------------------------------ statements

    public string Source(string package, string kitPackage, IReadOnlyList<string> imports)
    {
        // Paragraph methods.
        var usedMethods = new HashSet<string>(StringComparer.Ordinal) { "call", "run" };
        foreach (var p in m.Paragraphs)
        {
            var name = TypedRecords.Camel(System.Text.RegularExpressions.Regex.Replace(p.Name, @"^\d+[A-Z]?-", ""));
            if (name is "abstract" or "assert" or "default" or "new" or "return" or "switch" or "case" or "continue" or "break" or "goto") name += "Paragraph";
            var n = name;
            for (int i = 2; !usedMethods.Add(n); i++) n = name + i;
            methodOf[p.Name] = n;
        }
        var body = new StringBuilder();
        foreach (var p in m.Paragraphs)
        {
            body.Append($"\n    /** {p.Name} */\n    private void {methodOf[p.Name]}() {{\n");
            Block(body, p.Statements, "        ", p);
            body.Append("    }\n");
        }
        var sb = new StringBuilder();
        sb.Append($"package {package};\n\n");
        sb.Append("import com.mlcpu.rt.ModernStep;\n");
        sb.Append($"import {kitPackage}.Pic;\nimport java.math.BigDecimal;\nimport java.math.RoundingMode;\n");
        foreach (var i in imports) sb.Append($"import {i};\n");
        sb.Append($"\n/**\n * {m.ProgramId} rewritten onto typed views of its records: each paragraph is a method, each statement a typed assignment that\n");
        sb.Append(" * stores with the COBOL rules, and each CALL whose groups the equivalence probe verified is its JDK operation. Generated by\n");
        sb.Append(" * the typed-interface projection from the COBOL; it passes the capability's acceptance test (it is the faithful behaviour).\n */\n");
        sb.Append($"final class {className} {{\n");
        sb.Append("    /** GOBACK from anywhere in a PERFORM chain: ends the CALL. */\n    private static final class Goback extends RuntimeException {\n        Goback() {\n            super(null, null, false, false);\n        }\n    }\n\n    private static final Goback GOBACK = new Goback();\n\n");
        if (fields.Count > 0) sb.Append("    // WORKING-STORAGE: kept between CALLs, as in COBOL.\n");
        foreach (var f in fields) sb.Append($"    /** {f.Comment} */\n    private {(f.Type.EndsWith("[]") ? "final " : "")}{f.Type} {f.Var} = {f.Init};\n");
        if (arrayInits.Count > 0) sb.Append("\n    {\n").Append(string.Concat(arrayInits.Select(x => $"        {x}\n"))).Append("    }\n");
        foreach (var (pid, (wrapper, entry)) in embedded)
            sb.Append($"    /** CALL '{pid}': its typed reference (embedded; its WORKING-STORAGE persists between CALLs). */\n    private final {wrapper}.{entry} {TypedRecords.Camel(pid)} = new {wrapper}.{entry}();\n");
        foreach (var w in workingRecords) sb.Append($"    /** {w.Rec.Layout.Name}: a WORKING-STORAGE area passed to CALLs. */\n    private final {w.Rec.Interface} {w.Var} = {w.Rec.Interface}.create();\n");
        sb.Append("    /** What the last DISPLAY showed (a CALLed program has no console: the abend message carries it). */\n    private String console = \"\";\n\n");
        foreach (var a in arguments) sb.Append($"    private {a.Rec.Interface} {a.Var};\n");
        sb.Append($"\n    /** One CALL: the arguments, BY REFERENCE. */\n    void call({string.Join(", ", arguments.Select(a => $"{a.Rec.Interface} {a.Var}"))}) {{\n");
        foreach (var a in arguments) sb.Append($"        this.{a.Var} = {a.Var};\n");
        sb.Append("        try {\n");
        // Paragraphs run in order from the first until a GOBACK (falling through, as COBOL does).
        foreach (var p in m.Paragraphs)
        {
            sb.Append($"            {methodOf[p.Name]}();\n");
            if (EndsWithGoback(p.Statements)) break;
        }
        sb.Append("        } catch (Goback g) {\n            // GOBACK\n        }\n    }\n");
        sb.Append(body);
        sb.Append("}\n");
        return sb.ToString();
    }

    static bool EndsWithGoback(List<Stmt> s) => s.Count > 0 && s[^1].Verb is "GOBACK" or "STOP RUN" or "EXIT PROGRAM";

    void Block(StringBuilder sb, List<Stmt> statements, string ind, Paragraph? para = null)
    {
        for (int i = 0; i < statements.Count; i++)
        {
            var s = statements[i];
            bool last = para is not null && i == statements.Count - 1;
            try
            {
                Statement(sb, s, ind, last && para is not null && m.Paragraphs.IndexOf(para) == FirstGobackParagraph());
            }
            catch (NotSupportedException e)
            {
                gaps.Add($"{s.StepId} {s.Text}: {e.Message}");
                sb.Append($"{ind}if (true) throw new UnsupportedOperationException({Str($"not projected: {s.StepId} {Clip(s.Text, 80)} ({e.Message})")});\n");
            }
        }
    }

    int FirstGobackParagraph()
    {
        for (int i = 0; i < m.Paragraphs.Count; i++) if (EndsWithGoback(m.Paragraphs[i].Statements)) return i;
        return -1;
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + "…";

    void Statement(StringBuilder sb, Stmt s, string ind, bool finalGoback)
    {
        if (s.Frontier is not null) throw new NotSupportedException("a frontier statement: " + s.Frontier);
        var code = new StringBuilder();
        switch (s.Verb)
        {
            case "MOVE":
            {
                var srcNode = s.Get("src")!;
                foreach (var d in s.L!["dst"])
                {
                    if (srcNode.Start is not null || d.Start is not null) { code.Append(ind).Append(RefModMove(srcNode, d)).Append('\n'); continue; }
                    if (GroupMove(srcNode, d, ind) is { } moved) { code.Append(moved); continue; }
                    code.Append(ind).Append(Store(d, Expr(srcNode))).Append('\n');
                }
                break;
            }
            case "INITIALIZE":
                foreach (var d in s.L!["dst"]) code.Append(Initialize(d, ind));
                break;
            case "SET":
            {
                if (s.Str("mode") != "TRUE") throw new NotSupportedException("SET " + s.Str("mode"));
                foreach (var d in s.L!["dst"]) code.Append(ind).Append(SetTrue(d)).Append('\n');
                break;
            }
            case "COMPUTE":
            {
                var value = Expr(s.Get("expr")!);
                var dsts = s.L!["dst"];
                bool rounded = dsts.Any(d => d.Rounded == true);
                code.Append(Arithmetic(dsts, value, s, ind, rounded));
                break;
            }
            case "ADD":
            case "SUBTRACT":
            {
                var form = s.Str("form") ?? "TO";
                var ops = s.L!["ops"].Select(Expr).ToList();
                if (form is not ("TO" or "FROM")) throw new NotSupportedException($"{s.Verb} {form}");
                var dsts = s.L!["dst"];
                foreach (var d in dsts)
                {
                    var value = Read(d);
                    foreach (var o in ops) value = Arith(value, o, s.Verb == "ADD" ? "+" : "-");
                    code.Append(Arithmetic([d], value, s, ind, d.Rounded == true));
                }
                break;
            }
            case "MULTIPLY":
            {
                var form = s.Str("form") ?? "BY";
                var a = Expr(s.L!["ops"][0]);
                var by = s.Get("by")!;
                if (form == "GIVING")
                {
                    var dsts = s.L!["giving"];
                    code.Append(Arithmetic(dsts, Arith(a, Expr(by), "*"), s, ind, dsts.Any(d => d.Rounded == true)));
                }
                else if (form == "BY") code.Append(Arithmetic([by], Arith(a, Expr(by), "*"), s, ind, by.Rounded == true));
                else throw new NotSupportedException($"MULTIPLY {form}");
                break;
            }
            case "DIVIDE":
                code.Append(Divide(s, ind));
                break;
            case "IF":
            {
                code.Append($"{ind}if ({Cond(s.Get("cond")!)}) {{\n");
                Block(code, s.Blocks?.GetValueOrDefault("TRUE") ?? [], ind + "    ");
                var f = s.Blocks?.GetValueOrDefault("FALSE");
                if (f is { Count: > 0 })
                {
                    if (f.Count == 1 && f[0].Verb == "IF")
                    {
                        var inner = new StringBuilder();
                        Statement(inner, f[0], ind, false);
                        code.Append($"{ind}}} else ").Append(inner.ToString().TrimStart());
                        break;
                    }
                    code.Append($"{ind}}} else {{\n");
                    Block(code, f, ind + "    ");
                }
                code.Append($"{ind}}}\n");
                break;
            }
            case "EVALUATE":
            {
                var whens = s.Blocks?.GetValueOrDefault("WHEN") ?? [];
                bool first = true, open = false;
                foreach (var w in whens)
                {
                    bool other = w.Str("other") == "true";
                    var stmts = w.Blocks?.GetValueOrDefault("TRUE") ?? [];
                    if (other)
                    {
                        if (first) { Block(code, stmts, ind); open = false; break; }
                        code.Append($"{ind}}} else {{\n");
                    }
                    else code.Append(first ? $"{ind}if ({Cond(w.Get("cond")!)}) {{\n" : $"{ind}}} else if ({Cond(w.Get("cond")!)}) {{\n");
                    if (stmts.Count == 0 || stmts.All(x => x.Verb == "CONTINUE")) code.Append($"{ind}    // CONTINUE\n");
                    else Block(code, stmts, ind + "    ");
                    first = false;
                    open = true;
                    if (other) break;
                }
                if (open) code.Append($"{ind}}}\n");
                break;
            }
            case "PERFORM": code.Append(Perform(s, ind)); break;
            case "CALL": code.Append(Call(s, ind)); break;
            case "DISPLAY":
            {
                var items = s.L!["items"].Select(Expr).Select(e => e.T switch
                {
                    'S' or 'R' => e.Code,
                    'L' or 'D' => $"String.valueOf({e.Code})",
                    _ => throw new NotSupportedException("DISPLAY " + e.Fig)
                });
                code.Append($"{ind}console = {string.Join(" + ", items)};\n");
                break;
            }
            case "GOBACK":
                if (!finalGoback) code.Append($"{ind}throw GOBACK;\n");
                break;
            case "CONTINUE": code.Append($"{ind}// CONTINUE\n"); break;
            case "EXIT PARAGRAPH": code.Append($"{ind}return;\n"); break;
            case "EXIT PERFORM" when !s.Text.Contains("CYCLE", StringComparison.OrdinalIgnoreCase) && inlinePerforms.Count > 0:
                code.Append($"{ind}break {inlinePerforms[^1]};\n");
                break;
            default: throw new NotSupportedException($"verb {s.Verb}");
        }
        sb.Append(code);
    }

    /// <summary>
    /// A group MOVE that involves a WORKING-STORAGE group kept in Java fields: item by item when both groups have the same
    /// items (offsets, lengths, USAGEs, PICTUREs), which stores exactly the bytes a group MOVE copies. Null when neither side
    /// is such a group (the MOVE is then a plain one; a group of the record kit moves as its bytes).
    /// </summary>
    string? GroupMove(Node src, Node dst, string ind)
    {
        bool Ws(Node n) => n.K == "ref" && n.Id is { } id && locs.GetValueOrDefault(id) is WsGroup;
        if (!Ws(src) && !Ws(dst)) return null;
        var (ls, gs) = Leaves(src);
        var (ld, gd) = Leaves(dst);
        if (ls is null || ld is null || gs is null || gd is null) throw new NotSupportedException($"group MOVE {src.V} TO {dst.V}");
        bool same = ls.Count == ld.Count && ls.Zip(ld).All(p => p.First.Offset - gs.Offset == p.Second.Offset - gd.Offset && p.First.Length == p.Second.Length
            && Kind(p.First) == Kind(p.Second) && p.First.Digits == p.Second.Digits && p.First.Scale == p.Second.Scale && p.First.Signed == p.Second.Signed);
        if (!same) throw new NotSupportedException($"group MOVE {src.V} TO {dst.V}: the groups have different items");
        var sb = new StringBuilder();
        foreach (var (a, b) in ls.Zip(ld))
            sb.Append(ind).Append(Store(new Node { K = "ref", Id = b.Id, V = b.Name, Args = dst.Args }, Read(new Node { K = "ref", Id = a.Id, V = a.Name, Args = src.Args }))).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// MOVE x (start : length) TO y (start : length) between items of the record kit's areas: the bytes copied, space padded or
    /// cut to the receiving length (an alphanumeric MOVE).
    /// </summary>
    string RefModMove(Node src, Node dst)
    {
        (string Bytes, string Offset, string Length) Place(Node n)
        {
            var loc = LocOf(n);
            var (var, offset, length, dims) = loc switch
            {
                InView v => (v.Var, v.Member.Kit.Offset, v.Member.Kit.Length, v.Member.Kit.Occurs.Count),
                RecordRoot r => (r.Var, 0, r.Rec.Layout.Length, 0),
                _ => throw new NotSupportedException($"{n.V}: reference modification outside the record kit")
            };
            if (dims > 0 || n.Args is { Count: > 0 }) throw new NotSupportedException($"{n.V}: reference modification of a table item");
            if (n.Start is null) return ($"{var}.bytes()", offset.ToString(), length.ToString());
            var start = Expr(n.Start);
            var off = start.Const is { } c ? (offset + (int)c - 1).ToString() : $"{offset} + {Int(start)} - 1";
            var len = n.Len is null ? (start.Const is { } c2 ? (length - (int)c2 + 1).ToString() : $"{length} - {Int(start)} + 1") : Int(Expr(n.Len));
            return ($"{var}.bytes()", off, len);
        }
        var (sb, so, sl) = Place(src);
        var (db, dof, dl) = Place(dst);
        return $"Storage.moveBytes({sb}, {so}, {sl}, {db}, {dof}, {dl});";
    }

    (List<DataItem>? Leaves, DataItem? Group) Leaves(Node n)
    {
        if (n.K != "ref" || n.Id is not { } id || !byId.TryGetValue(id, out var it)) return (null, null);
        return locs.GetValueOrDefault(id) switch
        {
            WsGroup g => (g.Leaves, g.Item),
            InView { Member.Group: true } or RecordRoot or InOccurrence => (Tree(it).Where(i => i.IsLeaf).ToList(), it),
            _ => (null, null)
        };
    }

    string Arithmetic(List<Node> dsts, Ex value, Stmt s, string ind, bool rounded)
    {
        var sizeError = s.Blocks?.GetValueOrDefault("SIZE-ERROR");
        var notSizeError = s.Blocks?.GetValueOrDefault("NOT-SIZE-ERROR");
        if (notSizeError is { Count: > 0 }) throw new NotSupportedException("NOT ON SIZE ERROR");
        var sb = new StringBuilder();
        if (sizeError is null || sizeError.Count == 0)
        {
            foreach (var d in dsts) sb.Append(ind).Append(Store(d, value, rounded)).Append('\n');
            return sb.ToString();
        }
        if (dsts.Count != 1) throw new NotSupportedException("ON SIZE ERROR with several receivers");
        var v = $"v{++temp}";
        var type = value.T == 'L' ? "long" : "BigDecimal";
        if (rounded && type == "BigDecimal") value = new Ex($"{Paren(value.Code)}.setScale({ScaleOf(dsts[0])}, RoundingMode.HALF_UP)", 'D');
        sb.Append($"{ind}{type} {v} = {value.Code};\n");
        sb.Append($"{ind}if ({Overflow(dsts[0], new Ex(v, value.T))}) {{\n");
        Block(sb, sizeError, ind + "    ");
        sb.Append($"{ind}}} else {{\n{ind}    {Store(dsts[0], new Ex(v, value.T, null, value.Scale, value.Int), false, true)}\n{ind}}}\n");
        return sb.ToString();
    }

    Loc LocOf(Node n) => n.Id is { } id && locs.TryGetValue(id, out var l) ? l : throw new NotSupportedException($"{n.V}: no storage");

    DataItem ItemOf(Node n) => n.Id is { } id && byId.TryGetValue(id, out var it) ? it : throw new NotSupportedException($"{n.V}: unknown item");

    int ScaleOf(Node dst) => LocOf(dst) switch
    {
        InView v => v.Member.Kit.Scale,
        WsField f => f.Item.Scale,
        WsView w => w.Base.Item.Scale - w.Shift,
        _ => 0
    };

    /// <summary>The ON SIZE ERROR condition: integer digits beyond the PICTURE (packed, zoned), beyond the binary size (binary).</summary>
    string Overflow(Node dst, Ex v)
    {
        var dv = v.T == 'L' ? v.Code : Dec(v).Code;
        switch (LocOf(dst))
        {
            case InView iv:
            {
                var k = iv.Member.Kit;
                return k.Kind == "binary" ? $"Storage.binaryOverflow({dv}, {k.Length}, {(k.Signed ? "true" : "false")})" : $"Storage.decimalOverflow({dv}, {k.Digits}, {k.Scale})";
            }
            case WsField f:
            {
                var it = f.Item;
                return Kind(it) == "binary" ? $"Storage.binaryOverflow({dv}, {it.Length}, {(it.Signed ? "true" : "false")})" : $"Storage.decimalOverflow({dv}, {it.Digits}, {it.Scale})";
            }
            case WsView w:
                return $"Storage.decimalOverflow({Paren(dv)}.movePointLeft({w.Shift}), {w.Base.Item.Digits}, {w.Base.Item.Scale})";
            case WsDigits dg:
                return $"Storage.decimalOverflow({dv}, {dg.Item.Digits}, 0)";
        }
        throw new NotSupportedException("ON SIZE ERROR receiver");
    }

    string Initialize(Node d, string ind)
    {
        // A group of a record view (a table occurrence, say): item by item, text to spaces and numbers to zero; FILLER and
        // REDEFINES items keep their bytes, as INITIALIZE leaves them.
        if (d.Id is { } gid && byId.TryGetValue(gid, out var git) && !git.IsLeaf && (!locs.TryGetValue(gid, out var gl) || gl is InView { Member.Group: true } or InOccurrence))
        {
            var sb = new StringBuilder();
            foreach (var leaf in Tree(git).Where(l => l.IsLeaf && !l.IsFiller && !UnderRedefines(l, git)))
                if (locs.GetValueOrDefault(leaf.Id) is InView lv)
                    sb.Append(ind).Append(Store(new Node { K = "ref", Id = leaf.Id, V = leaf.Name, Args = d.Args },
                        lv.Member.Type == "String" ? new Ex("", 'F', "SPACE") : new Ex("", 'F', "ZERO"))).Append('\n');
            if (sb.Length == 0) throw new NotSupportedException($"INITIALIZE {d.V}");
            return sb.ToString();
        }
        if (d.Id is not { } id || !locs.TryGetValue(id, out var loc)) throw new NotSupportedException($"{d.V}: no storage");
        switch (loc)
        {
            case RecordRoot r: return $"{ind}{r.Var}.initialize();\n";
            case InView v when !v.Member.Group:
                return ind + Store(d, v.Member.Type == "String" ? new Ex("", 'F', "SPACE") : new Ex("", 'F', "ZERO")) + "\n";
            case WsField f:
                return ind + Store(d, f.Type == "String" ? new Ex("", 'F', "SPACE") : new Ex("", 'F', "ZERO")) + "\n";
            case WsGroup g:
            {
                var sb = new StringBuilder();
                foreach (var leaf in g.Leaves.Where(l => !l.IsFiller && l.RedefinesItem is null))
                    if (locs.GetValueOrDefault(leaf.Id) is WsField lf)
                        sb.Append(ind).Append(Store(new Node { K = "ref", Id = leaf.Id, V = leaf.Name }, lf.Type == "String" ? new Ex("", 'F', "SPACE") : new Ex("", 'F', "ZERO"))).Append('\n');
                return sb.ToString();
            }
        }
        throw new NotSupportedException($"INITIALIZE {d.V}");
    }

    string SetTrue(Node d)
    {
        var cond = ItemOf(d);
        if (recs.Conditions.TryGetValue(cond.Name, out var c) && cond.Parent is { } parent && locs.TryGetValue(parent.Id, out var ploc) && ploc is InView v)
        {
            var path = Path(v, d);
            if (c.Constant is not null) return $"{path}.{c.Field.Name}({v.Rec.Interface}.{(v.Member.Owner.Depth > 0 ? v.Member.Owner.Interface + "." : "")}{c.Field.Enum!.Type}.{c.Constant});";
            return $"{path}.set{c.Tester!.Value.Name[2..]}();";
        }
        if (cond.Parent is { } p && locs.TryGetValue(p.Id, out var wl) && wl is WsField f)
        {
            var first = cond.Values[0].Low;
            var lit = f.Type == "String" ? new Ex(Str(first.Text.TrimEnd(' ')), 'S') : long.TryParse(first.Text, out var l) ? new Ex(l + "L", 'L') : new Ex($"new BigDecimal(\"{first.Text}\")", 'D');
            return Store(new Node { K = "ref", Id = p.Id, V = p.Name }, lit);
        }
        if (cond.Parent is { } q && locs.TryGetValue(q.Id, out var ql) && ql is InView)
        {
            var first = cond.Values[0].Low;
            var lit = first.Quoted ? new Ex(Str(first.Text.TrimEnd(' ')), 'S') : Expr(new Node { K = "num", V = first.Text });
            return Store(new Node { K = "ref", Id = q.Id, V = q.Name, Args = d.Args }, lit);
        }
        throw new NotSupportedException($"SET {cond.Name} TO TRUE");
    }

    /// <summary>The labels of the inline PERFORMs being rendered, innermost last (EXIT PERFORM breaks out of the innermost).</summary>
    readonly List<string> inlinePerforms = [];
    int inlineLabels;

    string Perform(Stmt s, string ind)
    {
        var r = PerformLoop(s, ind, out var label);
        if (label is null || !r.Contains($"break {label};", StringComparison.Ordinal)) return r;
        // EXIT PERFORM inside: the inline PERFORM is a labeled statement (a block when it runs once).
        var lead = r.Length - r.TrimStart().Length;
        var body = r.TrimStart();
        return body.StartsWith("while ", StringComparison.Ordinal) || body.StartsWith("do ", StringComparison.Ordinal)
            ? r[..lead] + label + ": " + body
            : $"{ind}{label}: {{\n" + string.Concat(r.Split('\n').Where(l => l.Length > 0).Select(l => "    " + l + "\n")) + $"{ind}}}\n";
    }

    string PerformLoop(Stmt s, string ind, out string? label)
    {
        var sb = new StringBuilder();
        label = s.Str("target") is null ? $"perform{++inlineLabels}" : null;
        var myLabel = label;
        string Body(string i2)
        {
            var b = new StringBuilder();
            if (s.Str("target") is { } t)
            {
                var thru = s.Str("thru");
                int a = m.Paragraphs.FindIndex(p => p.Name == t), z = thru is null ? a : m.Paragraphs.FindIndex(p => p.Name == thru);
                if (a < 0 || z < a) throw new NotSupportedException($"PERFORM {t}{(thru is null ? "" : " THRU " + thru)}");
                for (int k = a; k <= z; k++) b.Append($"{i2}{methodOf[m.Paragraphs[k].Name]}();\n");
            }
            else
            {
                inlinePerforms.Add(myLabel!);
                try { Block(b, s.Blocks?.GetValueOrDefault("LOOP") ?? [], i2); }
                finally { inlinePerforms.RemoveAt(inlinePerforms.Count - 1); }
            }
            return b.ToString();
        }
        var until = s.Get("until");
        var var = s.Get("var");
        if (s.Get("times") is not null) throw new NotSupportedException("PERFORM TIMES");
        bool after = s.Str("test") == "AFTER";
        if (var is not null)
        {
            var from = Expr(s.Get("from")!);
            var by = Expr(s.Get("by")!);
            var cur = Read(var);
            var next = cur.T == 'L' && by.T == 'L' ? new Ex($"{cur.Code} + {by.Code}", 'L') : new Ex($"{Paren(Dec(cur).Code)}.add({Dec(by).Code})", 'D');
            sb.Append(ind).Append(Store(var, from)).Append('\n');
            if (!after)
            {
                sb.Append($"{ind}while (!{Atom(Cond(until!))}) {{\n").Append(Body(ind + "    "));
                sb.Append(ind + "    ").Append(Store(var, next)).Append('\n').Append($"{ind}}}\n");
            }
            else
            {
                sb.Append($"{ind}while (true) {{\n").Append(Body(ind + "    "));
                sb.Append($"{ind}    if ({Cond(until!)}) break;\n{ind}    ").Append(Store(var, next)).Append('\n').Append($"{ind}}}\n");
            }
            return sb.ToString();
        }
        if (until is not null)
        {
            if (!after) sb.Append($"{ind}while (!{Atom(Cond(until))}) {{\n").Append(Body(ind + "    ")).Append($"{ind}}}\n");
            else sb.Append($"{ind}do {{\n").Append(Body(ind + "    ")).Append($"{ind}}} while (!{Atom(Cond(until))});\n");
            return sb.ToString();
        }
        return Body(ind);
    }

    string Call(Stmt s, string ind)
    {
        if (s.Get("target") is not { K: "str", V: { } target }) throw new NotSupportedException("CALL through a data item");
        var args = s.L?.GetValueOrDefault("args") ?? [];
        target = target.ToUpperInvariant();
        if (target is "CEE3ABD" or "ILBOABN0")
        {
            var code = Read(args[0].L!);
            return $"{ind}throw new ModernStep.Abend((int) {(code.T == 'L' ? code.Code : Paren(Dec(code).Code) + ".longValue()")}, console);\n";
        }
        if (embedded.TryGetValue(target, out var emb))
        {
            var arrays = new List<string>();
            foreach (var a in args)
            {
                if (a.Op is not ("REFERENCE" or null)) throw new NotSupportedException($"CALL ... BY {a.Op}");
                if (a.L?.Id is not { } aid || !locs.TryGetValue(aid, out var al) || al is not RecordRoot ar) throw new NotSupportedException($"CALL argument {a.L?.V} is not a record of the kit");
                arrays.Add($"{ar.Var}.bytes()");
            }
            return $"{ind}{TypedRecords.Camel(target)}.call(new byte[][] {{ {string.Join(", ", arrays)} }});\n";
        }
        if (!operations.TryGetValue(target, out var ops)) throw new NotSupportedException($"CALL '{target}': no verified library operations");
        var bytes = new List<string>();
        foreach (var a in args)
        {
            if (a.Op is not ("REFERENCE" or null)) throw new NotSupportedException($"CALL ... BY {a.Op}");
            var n = a.L!;
            if (n.Id is not { } id || !locs.TryGetValue(id, out var loc) || loc is not RecordRoot r) throw new NotSupportedException($"CALL argument {n.V} is not a record of the kit");
            bytes.Add($"{r.Var}.bytes()");
        }
        return $"{ind}{ops}.call({string.Join(", ", bytes)});\n";
    }
}
