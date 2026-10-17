using System.Text;

namespace MlCpu.Modernize;

/// <summary>A variable of a lifted function: parameter, local, or a temporary the passes may fold away.</summary>
public sealed class Var
{
    static int next;
    public int Id { get; } = Interlocked.Increment(ref next);
    public required string Name { get; set; }
    public JType? Type { get; set; }
    public VKind Kind { get; set; }
    public bool IsParam { get; init; }
    public bool IsThis { get; init; }
    public bool IsTemp { get; set; }
    public FrameSlot? Slot { get; init; }
    /// <summary>The COBOL words of the name (MONTHLY-INTE), when another variable's: the frame slot's hint otherwise.</summary>
    public string? Hint { get => hint ?? Slot?.Hint; set => hint = value; }
    string? hint;
    /// <summary>Type inference slot.</summary>
    public int TypeSlot { get; set; } = -1;
    public override string ToString() => Name;
}

public abstract class Expr
{
    public JType? Type { get; set; }
    public abstract IEnumerable<Expr> Children { get; }
    public abstract Expr Map(Func<Expr, Expr> f);
}

public sealed class EVar(Var v) : Expr
{
    public Var V { get; set; } = v;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

public sealed class EThis(ClassDef? c) : Expr
{
    public ClassDef? Class { get; } = c;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

public sealed class EField(Expr obj, FieldDef f) : Expr
{
    public Expr Obj { get; set; } = obj;
    public FieldDef F { get; } = f;
    public override IEnumerable<Expr> Children => [Obj];
    public override Expr Map(Func<Expr, Expr> f) => new EField(f(Obj), F) { Type = Type };
}

public sealed class EStatic(FieldDef f) : Expr
{
    public FieldDef F { get; } = f;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

/// <summary>A literal: null, int, long, boolean, String, char, or a BigDecimal constant (unscaled, scale).</summary>
public sealed class ELit(object? value, JType type) : Expr
{
    public object? Value { get; } = value;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
    public static ELit Null => new(null, JType.Null);
    public static ELit Int(long v) => new(v, JType.Int) { Type = JType.Int };
    public static ELit Bool(bool v) => new(v, JType.Bool) { Type = JType.Bool };
    public static ELit Str(string v) => new(v, JType.String) { Type = JType.String };
    public bool IsNull => Value is null;
}

public enum CallKind { Static, Instance, Super, Jdk, JdkStatic }

/// <summary>A call of an estate function or a JDK method.</summary>
public sealed class ECall : Expr
{
    public required CallKind Kind { get; init; }
    public FunctionDef? Fn { get; init; }
    /// <summary>JDK owner type for JdkStatic, or the declaring JDK type for Jdk.</summary>
    public string? Owner { get; init; }
    public required string Name { get; init; }
    public Expr? Recv { get; set; }
    public List<Expr> Args { get; init; } = [];
    /// <summary>A call dispatched over several classes (J3S EVALUATE on the receiver's class): Fn is one implementation.</summary>
    public bool Virtual { get; init; }
    /// <summary>A virtual call's implementations J3S inlined as a field read (a getter): the class and the field it returns.</summary>
    public List<(ClassDef Cls, FieldDef Field)>? FieldImpls { get; init; }
    /// <summary>A call continuing a chain on its own line (.add(x)) with its own legacy site: the receiver was a statement of that site.</summary>
    public string? LineSite { get; set; }
    public override IEnumerable<Expr> Children => Recv is null ? Args : [Recv, .. Args];
    public override Expr Map(Func<Expr, Expr> f) =>
        new ECall { Kind = Kind, Fn = Fn, Owner = Owner, Name = Name, Recv = Recv is null ? null : f(Recv), Args = Args.Select(f).ToList(), Type = Type, Virtual = Virtual, FieldImpls = FieldImpls, LineSite = LineSite };
}

/// <summary>new Class(args) (estate class with constructor function) or a JDK class.</summary>
public sealed class ENew : Expr
{
    public ClassDef? Class { get; init; }
    public string? JdkClass { get; init; }
    public FunctionDef? Ctor { get; init; }
    public List<Expr> Args { get; init; } = [];
    public override IEnumerable<Expr> Children => Args;
    public override Expr Map(Func<Expr, Expr> f) => new ENew { Class = Class, JdkClass = JdkClass, Ctor = Ctor, Args = Args.Select(f).ToList(), Type = Type };
}

public sealed class EUnary(string op, Expr e) : Expr
{
    public string Op { get; } = op;
    public Expr E { get; set; } = e;
    public override IEnumerable<Expr> Children => [E];
    public override Expr Map(Func<Expr, Expr> f) => new EUnary(Op, f(E)) { Type = Type };
}

public sealed class EBinary(string op, Expr l, Expr r) : Expr
{
    public string Op { get; } = op;
    public Expr L { get; set; } = l;
    public Expr R { get; set; } = r;
    public override IEnumerable<Expr> Children => [L, R];
    public override Expr Map(Func<Expr, Expr> f) => new EBinary(Op, f(L), f(R)) { Type = Type };
}

public sealed class ECond(Expr c, Expr a, Expr b) : Expr
{
    public Expr C { get; set; } = c;
    public Expr A { get; set; } = a;
    public Expr B { get; set; } = b;
    public override IEnumerable<Expr> Children => [C, A, B];
    public override Expr Map(Func<Expr, Expr> f) => new ECond(f(C), f(A), f(B)) { Type = Type };
}

public sealed class EInstanceOf(Expr e, JType t) : Expr
{
    public Expr E { get; set; } = e;
    public JType T { get; } = t;
    public override IEnumerable<Expr> Children => [E];
    public override Expr Map(Func<Expr, Expr> f) => new EInstanceOf(f(E), T) { Type = Type };
}

public sealed class ECast(JType t, Expr e) : Expr
{
    public JType T { get; set; } = t;
    public Expr E { get; set; } = e;
    public override IEnumerable<Expr> Children => [E];
    public override Expr Map(Func<Expr, Expr> f) => new ECast(T, f(E)) { Type = Type };
}

/// <summary>A lambda: the closure's captured values and the lifted lambda function.</summary>
public sealed class ELambda : Expr
{
    public required ClassDef Closure { get; init; }
    public Dictionary<FieldDef, Expr> Captures { get; init; } = [];
    public override IEnumerable<Expr> Children => Captures.Values;
    public override Expr Map(Func<Expr, Expr> f) => new ELambda { Closure = Closure, Captures = Captures.ToDictionary(kv => kv.Key, kv => f(kv.Value)), Type = Type };
}

public sealed class EArrayNew(JType elem, Expr len) : Expr
{
    public JType Elem { get; } = elem;
    public Expr Len { get; set; } = len;
    public override IEnumerable<Expr> Children => [Len];
    public override Expr Map(Func<Expr, Expr> f) => new EArrayNew(Elem, f(Len)) { Type = Type };
}

/// <summary>A class literal (X.class).</summary>
public sealed class EClassLit(JType t) : Expr
{
    public JType T { get; } = t;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

public sealed class EIndex(Expr a, Expr i) : Expr
{
    public Expr A { get; set; } = a;
    public Expr I { get; set; } = i;
    public override IEnumerable<Expr> Children => [A, I];
    public override Expr Map(Func<Expr, Expr> f) => new EIndex(f(A), f(I)) { Type = Type };
}

/// <summary>A part of a J3S slot value that has no Java counterpart on its own (the class id -C, the payload -P, ...).</summary>
public sealed class EPart(Expr whole, string part) : Expr
{
    public Expr Whole { get; set; } = whole;
    public string Part { get; } = part;
    public override IEnumerable<Expr> Children => [Whole];
    public override Expr Map(Func<Expr, Expr> f) => new EPart(f(Whole), Part) { Type = Type };
}

/// <summary>Something the lift could not express: kept as text, counted as residue.</summary>
public sealed class EOpaque(string text) : Expr
{
    public string Text { get; } = text;
    public override IEnumerable<Expr> Children => [];
    public override Expr Map(Func<Expr, Expr> f) => this;
}

// ---------------------------------------------------------------------------------------------- statements

public abstract class St
{
    /// <summary>COBOL source line the statement came from.</summary>
    public int Line { get; set; }
    /// <summary>The legacy site (ABBR:line) the COBOL attached to this statement, for the source map.</summary>
    public string? Site { get; set; }
}

public sealed class SAssign(Expr lhs, Expr rhs) : St
{
    public Expr Lhs { get; set; } = lhs;
    public Expr Rhs { get; set; } = rhs;
    /// <summary>Set by the emitter: this assignment declares the variable.</summary>
    public bool Declares { get; set; }
}

public sealed class SExpr(Expr e) : St { public Expr E { get; set; } = e; }
public sealed class SReturn(Expr? e) : St { public Expr? E { get; set; } = e; }
public sealed class SThrow(Expr e) : St { public Expr E { get; set; } = e; }
public sealed class SIf(Expr c, List<St> then, List<St> els) : St
{
    public Expr C { get; set; } = c;
    public List<St> Then { get; set; } = then;
    public List<St> Else { get; set; } = els;
}
/// <summary>while (C) { Body } — C null means while (true).</summary>
public sealed class SLoop(string label, Expr? c, List<St> body) : St
{
    public string Label { get; set; } = label;
    public Expr? C { get; set; } = c;
    public List<St> Body { get; set; } = body;
    /// <summary>for-each form: for (Var : Iterable).</summary>
    public Var? Each { get; set; }
    public Expr? Iterable { get; set; }
    /// <summary>Counting form: for (Init; C; Update).</summary>
    public SAssign? Init { get; set; }
    public SAssign? Update { get; set; }
}
public sealed class SBlock(string label, List<St> body) : St
{
    public string Label { get; set; } = label;
    public List<St> Body { get; set; } = body;
}
public sealed class SBreak(string? label) : St { public string? Label { get; set; } = label; }
public sealed class SContinue(string? label) : St { public string? Label { get; set; } = label; }
public sealed class SSwitch(Expr subject) : St
{
    public Expr Subject { get; set; } = subject;
    public List<(List<Expr> Labels, List<St> Body)> Cases { get; } = [];
    public List<St>? Default { get; set; }
}
public sealed class STry(List<St> body) : St
{
    public List<St> Body { get; set; } = body;
    public List<(JType Type, Var V, List<St> Body)> Catches { get; } = [];
}
public sealed class SComment(string text) : St { public string Text { get; } = text; }

// ---------------------------------------------------------------------------------------------- low IR (before structuring)

public sealed class LLabel(string name) : St { public string Name { get; } = name; }
public sealed class LGoto(string target) : St { public string Target { get; set; } = target; }
/// <summary>if (C) goto Target (fall through otherwise).</summary>
public sealed class LCondGoto(Expr c, string target) : St
{
    public Expr C { get; set; } = c;
    public string Target { get; set; } = target;
}
/// <summary>switch: goto the label of the first case whose value equals the subject, else Default.</summary>
public sealed class LSwitchGoto(Expr subject) : St
{
    public Expr Subject { get; set; } = subject;
    public List<(List<Expr> Values, string Target)> Cases { get; } = [];
    public string Default { get; set; } = "";
}

public static class IrText
{
    public static string Show(Expr e) => e switch
    {
        EVar v => v.V.Name,
        EThis => "this",
        EField f => Show(f.Obj) + "." + f.F.Name,
        EStatic s => (s.F.Owner?.SimpleName ?? s.F.Group) + "." + s.F.Name,
        ELit l => l.Value switch { null => "null", string s => "\"" + s + "\"", bool b => b ? "true" : "false", _ => l.Value.ToString()! },
        ECall c => (c.Recv is null ? (c.Owner ?? c.Fn?.Owner?.SimpleName ?? "?") : Show(c.Recv)) + "." + c.Name + "(" + string.Join(", ", c.Args.Select(Show)) + ")",
        ENew n => "new " + (n.Class?.SimpleName ?? n.JdkClass) + "(" + string.Join(", ", n.Args.Select(Show)) + ")",
        EUnary u => u.Op + Show(u.E),
        EBinary b => "(" + Show(b.L) + " " + b.Op + " " + Show(b.R) + ")",
        ECond c => "(" + Show(c.C) + " ? " + Show(c.A) + " : " + Show(c.B) + ")",
        EInstanceOf i => Show(i.E) + " instanceof " + i.T.Simple,
        ECast c => "(" + c.T.Simple + ") " + Show(c.E),
        ELambda l => "lambda" + l.Closure.SimpleName,
        EArrayNew a => "new " + a.Elem.Simple + "[" + Show(a.Len) + "]",
        EIndex x => Show(x.A) + "[" + Show(x.I) + "]",
        EPart p => Show(p.Whole) + "#" + p.Part,
        EOpaque o => "/*?" + o.Text + "*/",
        EStaticJdk sj => sj.Owner[(sj.Owner.LastIndexOf('.') + 1)..] + "." + sj.Name,
        EAlloc al => "alloc#" + al.Id + "(" + al.V.Name + ")",
        ERecordField rf => "record." + rf.Item.Name,
        EArrayLength al2 => Show(al2.A) + ".length",
        _ => e.GetType().Name,
    };

    public static string Show(IEnumerable<St> list, int indent = 0)
    {
        var sb = new StringBuilder();
        foreach (var s in list) Show(sb, s, indent);
        return sb.ToString();
    }

    static void Show(StringBuilder sb, St s, int ind)
    {
        var pad = new string(' ', ind * 2);
        switch (s)
        {
            case SAssign a: sb.Append(pad).Append(Show(a.Lhs)).Append(" = ").Append(Show(a.Rhs)).Append(';').AppendLine(s.Site is null ? "" : "   // " + s.Site); break;
            case SExpr e: sb.Append(pad).Append(Show(e.E)).Append(';').AppendLine(s.Site is null ? "" : "   // " + s.Site); break;
            case SReturn r: sb.Append(pad).Append("return").Append(r.E is null ? "" : " " + Show(r.E)).AppendLine(";"); break;
            case SThrow t: sb.Append(pad).Append("throw ").Append(Show(t.E)).AppendLine(";"); break;
            case SIf i:
                sb.Append(pad).Append("if (").Append(Show(i.C)).AppendLine(") {");
                foreach (var c in i.Then) Show(sb, c, ind + 1);
                if (i.Else.Count > 0) { sb.Append(pad).AppendLine("} else {"); foreach (var c in i.Else) Show(sb, c, ind + 1); }
                sb.Append(pad).AppendLine("}");
                break;
            case SLoop l:
                sb.Append(pad).Append(l.Label).Append(": ").Append(l.Each is not null ? $"for ({l.Each.Name} : {Show(l.Iterable!)})" : l.C is null ? "while (true)" : "while (" + Show(l.C) + ")").AppendLine(" {");
                foreach (var c in l.Body) Show(sb, c, ind + 1);
                sb.Append(pad).AppendLine("}");
                break;
            case SBlock b:
                sb.Append(pad).Append(b.Label).AppendLine(": {");
                foreach (var c in b.Body) Show(sb, c, ind + 1);
                sb.Append(pad).AppendLine("}");
                break;
            case SBreak br: sb.Append(pad).Append("break").Append(br.Label is null ? "" : " " + br.Label).AppendLine(";"); break;
            case SContinue co: sb.Append(pad).Append("continue").Append(co.Label is null ? "" : " " + co.Label).AppendLine(";"); break;
            case SSwitch sw:
                sb.Append(pad).Append("switch (").Append(Show(sw.Subject)).AppendLine(") {");
                foreach (var (labels, body) in sw.Cases) { sb.Append(pad).Append("case ").Append(string.Join(", ", labels.Select(Show))).AppendLine(" -> {"); foreach (var c in body) Show(sb, c, ind + 2); sb.Append(pad).AppendLine("  }"); }
                if (sw.Default is not null) { sb.Append(pad).AppendLine("default -> {"); foreach (var c in sw.Default) Show(sb, c, ind + 2); sb.Append(pad).AppendLine("  }"); }
                sb.Append(pad).AppendLine("}");
                break;
            case STry t:
                sb.Append(pad).AppendLine("try {");
                foreach (var c in t.Body) Show(sb, c, ind + 1);
                foreach (var (ty, v, body) in t.Catches) { sb.Append(pad).Append("} catch (").Append(ty.Simple).Append(' ').Append(v.Name).AppendLine(") {"); foreach (var c in body) Show(sb, c, ind + 1); }
                sb.Append(pad).AppendLine("}");
                break;
            case SComment c: sb.Append(pad).Append("// ").AppendLine(c.Text); break;
            case LLabel l: sb.Append(l.Name).AppendLine(":"); break;
            case LGoto g: sb.Append(pad).Append("goto ").Append(g.Target).AppendLine(";"); break;
            case LCondGoto cg: sb.Append(pad).Append("if (").Append(Show(cg.C)).Append(") goto ").Append(cg.Target).AppendLine(";"); break;
            case LSwitchGoto sg:
                sb.Append(pad).Append("switch (").Append(Show(sg.Subject)).Append(") ");
                foreach (var (vals, t) in sg.Cases) sb.Append(string.Join("|", vals.Select(Show))).Append("->").Append(t).Append(' ');
                sb.Append("default->").AppendLine(sg.Default);
                break;
        }
    }
}
