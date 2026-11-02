using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

/// <summary>A code field read (or written) as its enum: the record field and the record class's enum accessor.</summary>
public sealed class ECode(ERecordField field, string accessor, JType type) : Expr
{
    public ERecordField Field { get; } = field;
    public string Accessor { get; } = accessor;
    public override IEnumerable<Expr> Children => [Field];
    public override Expr Map(Func<Expr, Expr> f) => new ECode(f(Field) as ERecordField ?? Field, Accessor, type) { Type = Type };
}

/// <summary>A constant of a record's code enum: qualified (Type.CODE), or bare (a switch label).</summary>
public sealed class EEnum(string type, string constant, bool bare) : Expr
{
    public string EnumType { get; } = type;
    public string Constant { get; } = constant;
    public bool Bare { get; } = bare;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

/// <summary>
/// Code fields as enums (JOBOL JOB033): a short text field of a record that the program's paragraphs compare with,
/// switch on and set to a closed set of codes (H, P, E) is read and written through an enum of its record class (its
/// codes and OTHER for any other value), so the paragraphs compare enum constants instead of strings. The record keeps
/// its text accessor for every other use (a copy, a DISPLAY). Equality is unchanged: the text accessor drops trailing
/// spaces as the COBOL comparison pads them, and the enum maps exactly that text.
/// </summary>
static class CodeSets
{
    public static void Run(AppModel a, AppProgram ap, IEnumerable<Method> methods)
    {
        var list = methods.ToList();
        var uses = new Dictionary<DataItem, SortedSet<string>>();
        var bad = new HashSet<DataItem>();
        bool switched(DataItem it) => list.Any(m => Passes.All(m.Body).Any(s => s is SSwitch { Subject: ERecordField f } && f.Item == it));
        DataItem? Field(Expr e) => e is ERecordField { Padded: false, Index: null, RefStart: 0, Type.Name: "java.lang.String" } f && f.Item.IsLeaf
            && f.Item.Category != Category.Numeric && f.Item.Length <= 8 && a.RecordOf(ap, f.Item) is { } r && r.Rc.Accessors.ContainsKey(f.Item.Name) && !r.Rc.Flags.Contains(f.Item.Name) ? f.Item : null;
        void Add(DataItem it, string code)
        {
            code = code.TrimEnd();
            if (!System.Text.RegularExpressions.Regex.IsMatch(code, "^[A-Z][A-Z0-9_]*$") || code == "OTHER") { bad.Add(it); return; }
            (uses.TryGetValue(it, out var s) ? s : uses[it] = new SortedSet<string>(StringComparer.Ordinal)).Add(code);
        }
        foreach (var m in list)
            foreach (var st in Passes.All(m.Body))
            {
                if (st is SSwitch { Subject: var subj } sw && Field(subj) is { } si)
                    foreach (var (labels, _) in sw.Cases)
                        foreach (var l in labels) if (l is ELit { Value: string lv }) Add(si, lv); else bad.Add(si);
                if (st is SAssign { Lhs: var lhs, Rhs: var rhs } && Field(lhs) is { } wi)
                {
                    if (rhs is ELit { Value: string wv }) Add(wi, wv);
                    else bad.Add(wi);
                }
                foreach (var e in Exprs(st).SelectMany(Deep))
                    if (Compared(e, Field) is ({ } ci, var code)) Add(ci, code);
            }
        foreach (var (it, codes) in uses)
        {
            if (bad.Contains(it) || codes.Count < 2) continue;
            var r = a.RecordOf(ap, it)!.Value;
            var acc = r.Rc.Accessors[it.Name];
            if (!r.Rc.Codes.TryGetValue(it.Name, out var known))
            {
                var name = EnumName(a, r.Rc, acc);
                if (name is null || r.Rc.Accessors.Values.Contains(acc + "Code")) continue;
                known = (name, new SortedSet<string>(StringComparer.Ordinal), Meanings(a, r.Rc, it));
                r.Rc.Codes[it.Name] = known;
            }
            known.Codes.UnionWith(codes);
            // the constants: the meanings the copybook states for every code, else the codes themselves
            var names = known.Names;
            bool meant = known.Codes.All(c => names.ContainsKey(c)) && names.Values.Distinct().Count() == names.Count;
            string Const(string c) => meant ? names[c] : c;
            var enumType = a.RecordPackage + "." + r.Rc.Name + "." + known.Enum;
            var type = JType.Of(enumType);
            ECode Code(ERecordField f) => new(f, acc + "Code", type) { Type = type };
            foreach (var m in list)
            {
                foreach (var st in Passes.All(m.Body).ToList())
                {
                    if (st is SSwitch sw && Field(sw.Subject) == it)
                    {
                        sw.Subject = Code((ERecordField)sw.Subject);
                        foreach (var (labels, _) in sw.Cases)
                            for (int k = 0; k < labels.Count; k++) labels[k] = new EEnum(enumType, Const(((string)((ELit)labels[k]).Value!).TrimEnd()), true);
                    }
                    if (st is SAssign { Lhs: var lhs } asg && Field(lhs) == it)
                    {
                        asg.Lhs = Code((ERecordField)lhs);
                        asg.Rhs = new EEnum(enumType, Const(((string)((ELit)asg.Rhs).Value!).TrimEnd()), false);
                    }
                }
                MapBody(m.Body, e => e is EUnary { Op: "!", E: var neg } && Compared(neg, Field) is ({ } ni, var ncode) && ni == it
                    ? new EBinary("!=", Code(neg is ECall { Recv: ERecordField nrf } ? nrf : (ERecordField)((ECall)neg).Args[0]), new EEnum(enumType, Const(ncode), false)) { Type = JType.Bool }
                    : Compared(e, Field) is ({ } ci, var code) && ci == it
                    ? new EBinary("==", Code(e is ECall { Recv: ERecordField rf } ? rf : (ERecordField)((ECall)e).Args[0]), new EEnum(enumType, Const(code), false)) { Type = JType.Bool } : null);
            }
        }
    }

    /// <summary>field.equals("CODE") or "CODE".equals(field): the field and the code.</summary>
    static (DataItem? Item, string Code) Compared(Expr e, Func<Expr, DataItem?> field) => e switch
    {
        ECall { Kind: CallKind.Jdk, Name: "equals", Recv: var r, Args: [ELit { Value: string c }] } when field(r!) is { } i => (i, c.TrimEnd()),
        ECall { Kind: CallKind.Jdk, Name: "equals", Recv: ELit { Value: string c2 }, Args: [var x] } when field(x) is { } i2 => (i2, c2.TrimEnd()),
        _ => (null, ""),
    };

    static readonly Dictionary<string, string> copybookComments = new(StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string> Stopwords = ["IN", "OF", "THE", "TO", "A", "AN", "FOR", "AT"];

    /// <summary>
    /// What the copybook's comments say each code of the field means ("FO-FUNC H = LOAN HEADER, P = PERIOD, V = EVENT,
    /// E = END OF RUN (STATISTICS)"): code → constant name (LOAN_HEADER, END_OF_RUN), the words before a data name or
    /// a parenthesis, without trailing connectives. Empty when the copybook says nothing about the field.
    /// </summary>
    static Dictionary<string, string> Meanings(AppModel a, RecordClass rc, DataItem it)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        if (rc.Copybook is not { } cb) return names;
        if (!copybookComments.TryGetValue(cb, out var text))
        {
            var file = a.M.Estate.CopybookDirs.SelectMany(d => Directory.Exists(d) ? Directory.GetFiles(d) : [])
                .FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(cb, StringComparison.OrdinalIgnoreCase));
            var lines = file is null ? [] : File.ReadAllLines(file).Where(l => l.Length > 6 && l[6] == '*').Select(l => l[7..].Trim().Trim('*').Trim());
            copybookComments[cb] = text = string.Join(" ", lines);
        }
        var m = System.Text.RegularExpressions.Regex.Match(text, @"(?<![A-Z0-9-])" + System.Text.RegularExpressions.Regex.Escape(it.Name) + @"\s+(.*)$");
        if (!m.Success) return names;
        var rest = m.Groups[1].Value;
        // up to the next field the comments describe (a data name followed by a code)
        var next = System.Text.RegularExpressions.Regex.Match(rest, @"\b[A-Z0-9]+(-[A-Z0-9]+)+\s+[A-Z0-9]{1,4}\s*=");
        if (next.Success) rest = rest[..next.Index];
        foreach (var part in rest.Split(','))
        {
            var pm = System.Text.RegularExpressions.Regex.Match(part.Trim(), @"^([A-Z0-9]{1,4})\s*=\s*(.+)$");
            if (!pm.Success) continue;
            var words = System.Text.RegularExpressions.Regex.Replace(pm.Groups[2].Value, @"\(.*?\)", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .TakeWhile(w => !w.Contains('-') && System.Text.RegularExpressions.Regex.IsMatch(w, "^[A-Z][A-Z0-9]*$")).ToList();
            while (words.Count > 0 && Stopwords.Contains(words[^1])) words.RemoveAt(words.Count - 1);
            if (words.Count == 0 || words.Count > 4) continue;
            names[pm.Groups[1].Value] = string.Join('_', words);
        }
        return names;
    }

    /// <summary>The enum's name: the field's (RecType), with the record's for a one-word field (OutStatus), unique among the records.</summary>
    static string? EnumName(AppModel a, RecordClass rc, string acc)
    {
        var baseName = rc.Name.EndsWith("Record", StringComparison.Ordinal) ? rc.Name[..^6] : rc.Name;
        var name = Naming.UpperFirst(acc);
        if (!acc.Skip(1).Any(char.IsUpper)) name = baseName + name;
        var taken = a.Programs.SelectMany(p => p.Records.Values).Concat(a.Shared.Values).Distinct().SelectMany(r => r.Codes.Values.Select(c => c.Enum)).ToHashSet(StringComparer.Ordinal);
        if (taken.Contains(name)) name = baseName + Naming.UpperFirst(acc);
        if (taken.Contains(name) || name == rc.Name) return null;
        return name;
    }

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        foreach (var c in e.Children)
            foreach (var d in Deep(c)) yield return d;
    }

    static IEnumerable<Expr> Exprs(St s) => s switch
    {
        SAssign x => [x.Rhs, .. x.Lhs.Children],
        SExpr x => [x.E],
        SReturn { E: { } r } => [r],
        SThrow t => [t.E],
        SIf i => [i.C],
        SLoop l => (l.C is null ? Array.Empty<Expr>() : [l.C]).Concat(l.Iterable is null ? [] : [l.Iterable]),
        SSwitch w => [w.Subject],
        _ => [],
    };

    static Expr Rw(Expr e, Func<Expr, Expr?> f) => f(e) ?? e.Map(c => Rw(c, f));

    static void MapBody(List<St> body, Func<Expr, Expr?> f)
    {
        foreach (var s in Passes.All(body).ToList())
            switch (s)
            {
                case SAssign x: x.Rhs = Rw(x.Rhs, f); x.Lhs = x.Lhs is ERecordField or ECode or EVar or EProgField ? x.Lhs : Rw(x.Lhs, f); break;
                case SExpr x: x.E = Rw(x.E, f); break;
                case SReturn { E: not null } r: r.E = Rw(r.E!, f); break;
                case SThrow t: t.E = Rw(t.E, f); break;
                case SIf i: i.C = Rw(i.C, f); break;
                case SLoop l:
                    if (l.C is not null) l.C = Rw(l.C, f);
                    if (l.Iterable is not null) l.Iterable = Rw(l.Iterable, f);
                    break;
                case SSwitch w: w.Subject = Rw(w.Subject, f); break;
            }
    }

    /// <summary>The Java of a record class's code enums and their accessors (AppWriter).</summary>
    public static IEnumerable<string> Java(RecordClass rc)
    {
        foreach (var (itemName, (name, codes, names)) in rc.Codes.OrderBy(c => c.Key, StringComparer.Ordinal))
        {
            // the key may be another copy's name of the field (COPY ... REPLACING): the field is the first copy's
            var it = rc.Fields.First(f => f.Name.Equals(rc.Canon(itemName), StringComparison.OrdinalIgnoreCase));
            var acc = rc.Accessors[it.Name];
            yield return "";
            yield return $"    /** The codes of {it.Name} (PIC X({it.Length})); OTHER is any other value. */";
            yield return $"    public enum {name} {{";
            bool meant = codes.All(c => names.ContainsKey(c)) && names.Values.Distinct().Count() == names.Count;
            foreach (var c in codes)
            {
                yield return $"        /** '{c}'{(meant ? " (the copybook: " + names[c].Replace('_', ' ') + ")" : "")} */";
                yield return $"        {(meant ? names[c] : c)}(\"{c}\"),";
            }
            yield return "        /** A value that is none of the codes. */";
            yield return "        OTHER(\"\");";
            yield return "";
            yield return "        private final String code;";
            yield return "";
            yield return $"        {name}(String code) {{";
            yield return "            this.code = code;";
            yield return "        }";
            yield return "";
            yield return "        /** The code's characters. */";
            yield return "        public String code() {";
            yield return "            return code;";
            yield return "        }";
            yield return "";
            yield return "        /** The code of a field's text (without trailing spaces), OTHER when it is none. */";
            yield return $"        public static {name} of(String text) {{";
            yield return $"            for ({name} c : values()) if (c != OTHER && c.code.equals(text)) return c;";
            yield return "            return OTHER;";
            yield return "        }";
            yield return "    }";
            yield return "";
            yield return $"    /** {it.Name} as its code. */";
            yield return $"    public {name} {acc}Code() {{ return {name}.of({acc}()); }}";
            yield return $"    /** {it.Name}: set to a code. */";
            yield return $"    public void {acc}Code({name} value) {{ {acc}(value.code()); }}";
        }
    }
}
