using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.Modernize;

/// <summary>A lifted, structured, typed function ready to print.</summary>
public sealed class Method
{
    public required FunctionDef Fn { get; init; }
    public required Lifted L { get; init; }
    public required List<St> Body { get; set; }
}

/// <summary>Prints the recovered classes as Java source: one file per top-level class, a source map of legacy sites.</summary>
public sealed class JavaWriter
{
    readonly Model M;
    readonly TypeInference T;
    readonly Dictionary<FunctionDef, Method> methods;
    readonly Shapes shapes;
    public readonly List<(string File, int Line, string Site)> SourceMap = [];
    public readonly List<string> Residue = [];
    /// <summary>The application layer, and the program class being printed (its records and job objects).</summary>
    public AppModel? App { get; set; }
    public AppProgram? Prog { get; set; }
    /// <summary>Record every statement's legacy site (program classes too, not only calculation classes).</summary>
    bool markAll;
    /// <summary>The model edits applied while printing (opt-in, <see cref="ModelEdits"/>); null: the model as recovered.</summary>
    public ModelEdits? Edits { get; init; }
    /// <summary>The checked exceptions each method declares (<see cref="CheckedExceptions"/>).</summary>
    public Dictionary<FunctionDef, List<string>>? Throws { get; init; }
    /// <summary>The enum constant whose body is being printed, and its enum (a model edit names them).</summary>
    string? constant, constantOf;

    /// <summary>A model edit's Java at the current indentation, <paramref name="suffix"/> after its last line.</summary>
    void Emit(ModelEdits.Edit e, string suffix = "")
    {
        var lines = ModelEdits.Lines(e.Text);
        for (int i = 0; i < lines.Count; i++) o.L(lines[i] + (i == lines.Count - 1 ? suffix : ""));
        Edits!.Applied(e);
    }

    /// <summary>
    /// A replaced method's recovered body printed into nothing: the imports it needs stay (the file's other code, and the
    /// replacement, may use them), its text and its legacy sites go.
    /// </summary>
    void ImportsOf(Method m)
    {
        var saved = o;
        int mark = SourceMap.Count;
        o = new Out { Indent = saved.Indent, OnSite = (_, _) => { } };
        Declarations(m.Body);
        NameLocals(m);
        Block(m.Body);
        o = saved;
        if (SourceMap.Count > mark) SourceMap.RemoveRange(mark, SourceMap.Count - mark);
    }

    static string EditDoc(ModelEdits.Edit e) => $"/** Model edit {e.No}{(e.Note is { Length: > 0 } n ? ": " + n : "")}. */";

    /// <summary>A model edit's doc line at an indentation (a program class's members, AppWriter).</summary>
    public static string EditDoc(ModelEdits.Edit e, int indent) => new string(' ', indent * 4) + EditDoc(e) + "\n";

    public JavaWriter(Model m, TypeInference t, Dictionary<FunctionDef, Method> methods, Shapes shapes)
    {
        M = m; T = t; this.methods = methods; this.shapes = shapes;
    }

    // ================================================================== files

    public Dictionary<string, string> Files()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var c in shapes.TopLevel.OrderBy(c => c.FullName, StringComparer.Ordinal))
        {
            var path = c.FullName.Replace('.', '/') + ".java";
            files[path] = File(c, path);
        }
        foreach (var i in shapes.Interfaces.Values.OrderBy(i => i.FullName, StringComparer.Ordinal))
        {
            var path = i.FullName.Replace('.', '/') + ".java";
            files[path] = InterfaceFile(i);
        }
        return files;
    }

    sealed class Out
    {
        public readonly StringBuilder Sb = new();
        public int Indent;
        public int Line = 1;
        /// <summary>Lines of lambda bodies already in the statement being built (not printed yet).</summary>
        public int Pending;
        /// <summary>Where a chained call on its own line records its legacy site (the text marks it \u0001site\u0002).</summary>
        public Action<int, string>? OnSite;
        /// <summary>A lambda body inside a statement: its sites stay marks in the text, the statement resolves them to lines.</summary>
        public bool Deferred;
        public void Text(string s)
        {
            if (s.Contains('\u0001') && !Deferred)
            {
                var sb = new StringBuilder();
                int line = Line;
                for (int i = 0; i < s.Length; i++)
                {
                    if (s[i] == '\u0001') { int j = s.IndexOf('\u0002', i); OnSite?.Invoke(line, s[(i + 1)..j]); i = j; continue; }
                    if (s[i] == '\n') line++;
                    sb.Append(s[i]);
                }
                s = sb.ToString();
            }
            Sb.Append(s);
            Line += s.Count(c => c == '\n');
        }
        public void Nl() { Sb.Append('\n'); Line++; }
        public void Open(string s) { Pad(); Text(s); Nl(); Indent++; Pending = 0; }
        public void Close(string s = "}") { Indent--; Pad(); Sb.Append(s); Nl(); }
        public void Pad() => Sb.Append(new string(' ', Indent * 4));
        public void L(string s) { Pad(); Text(s); Nl(); Pending = 0; }
    }

    ClassDef? current;
    HashSet<string> imports = [];
    string currentFile = "";
    Out o = new();

    string File(ClassDef c, string path)
    {
        imports = [];
        currentFile = path;
        o = new Out { OnSite = SiteAt };
        // body first (collects imports), then the header
        var body = new Out { Indent = 0, OnSite = SiteAt };
        var saved = o;
        o = body;
        Class(c, false);
        o = saved;
        foreach (var e in Edits?.For("add-import", c.FullName) ?? [])
        {
            imports.Add(Regex.Replace(e.Text.Trim(), @"^import\s+|;$", ""));
            Edits!.Applied(e);
        }
        var head = new StringBuilder();
        if (c.Package.Length > 0) head.Append("package ").Append(c.Package).Append(";\n\n");
        var imps = imports.Where(i => !i.StartsWith("java.lang.") || i.Count(ch => ch == '.') > 2).Where(i => Pkg(i) != c.Package).Order(StringComparer.Ordinal).ToList();
        foreach (var i in imps) head.Append("import ").Append(i).Append(";\n");
        if (imps.Count > 0) head.Append('\n');
        int offset = head.ToString().Count(ch => ch == '\n');
        // shift the source map lines recorded for this file by the header
        for (int k = 0; k < SourceMap.Count; k++)
            if (SourceMap[k].File == path && SourceMap[k].Line < 0) SourceMap[k] = (path, -SourceMap[k].Line + offset, SourceMap[k].Site);
        return head + body.Sb.ToString();
    }

    static string Pkg(string fqcn) => fqcn.LastIndexOf('.') is var d && d > 0 ? fqcn[..d] : "";

    // ================================================================== classes

    void Class(ClassDef c, bool nested)
    {
        var prev = current;
        current = c;
        Doc(c);
        var shape = shapes.Of(c);
        bool record = shape.Components is not null && !ExceptionRoots.Contains(c);
        // R3 (opt-in): final only on a printed class (records and enums are implicitly final, abstract classes cannot be)
        bool final = shapes.FinalClasses?.Contains(c) == true && !shape.IsEnum && !shape.IsAbstract && !record;
        var mods = "public " + (nested ? "static " : "") + (shape.IsAbstract ? "abstract " : "") + (final ? "final " : "");
        string kind = shape.IsEnum ? "enum" : "class";
        var ext = !shape.IsEnum && c.Super is not null ? " extends " + TypeName(JType.Of(c.Super))
            : !shape.IsEnum && ExceptionRoots.Contains(c) ? " extends RuntimeException" : "";
        var impls = (shapes.Implements.TryGetValue(c, out var ifs) ? ifs.Select(i => TypeName(JType.Of(i.FullName))) : []).ToList();
        // compareTo of its own type: the class is Comparable (sorted sets and maps, Collections.reverseOrder rely on it)
        if (!shape.IsEnum && shape.Methods.Any(f => f.MethodName == "compareTo" && f.Params.Count == 1 && !f.IsStatic && T.JavaType(T.Param(f, 0)).Class == c))
            impls.Add("Comparable<" + c.SimpleName + ">");
        var impl = impls.Count > 0 ? " implements " + string.Join(", ", impls) : "";
        // a value class: a record of its components (the canonical constructor and the private final fields are Java's)
        if (shape.Components is { } comps && !ExceptionRoots.Contains(c))
        {
            // components a model edit adds, after the recovered ones
            var more = Edits?.For("add-component", c.FullName).ToList() ?? [];
            foreach (var e in more) Edits!.Applied(e);
            o.Open($"{mods}record {c.SimpleName}({string.Join(", ", comps.Select(f => TypeName(FieldType(f)) + " " + f.Name).Concat(more.Select(e => e.Text.Trim().TrimEnd(',', ';'))))}){impl} {{");
        }
        else o.Open($"{mods}{kind} {c.SimpleName}{ext}{impl} {{");
        if (shape.IsEnum) EnumConstants(c, shape);
        Fields(c, shape);
        foreach (var af in shape.AbstractMethods)
        {
            o.Nl();
            var aps = af.Params.Select((p, i) => TypeName(ParamType(af, i)) + " " + ParamName(af, i));
            o.L(MethodDoc(af, "implemented by the subclasses"));
            o.L($"public abstract {(af.Ret is null ? "void" : TypeName(T.JavaType(T.Ret(af))))} {shapes.MethodName(af)}({string.Join(", ", aps)});");
        }
        foreach (var fn in shape.Methods)
        {
            if (fn == shape.CanonicalCtor && shape.Components is not null && !ExceptionRoots.Contains(c)) continue;
            o.Nl();
            Method(fn);
        }
        foreach (var e in Edits?.For("add-method", c.FullName) ?? [])
        {
            o.Nl();
            o.L(EditDoc(e));
            Emit(e);
        }
        if (shape.Components is not null && shape.IdentityToString && !ExceptionRoots.Contains(c))
        {
            o.Nl();
            o.L($"/** Object's format, as the legacy class had no toString of its own (legacy {c.SimpleName}). */");
            o.L("@Override");
            o.L("public String toString() {");
            o.Indent++;
            o.L("return getClass().getName() + \"@\" + Integer.toHexString(hashCode());");
            o.Indent--;
            o.L("}");
        }
        foreach (var st in (shapes.Setters.GetValueOrDefault(c) ?? []).OrderBy(f => FieldOrder(c, f)))
        {
            o.Nl();
            o.L($"public void set{Naming.UpperFirst(st.Name)}({TypeName(FieldType(st))} {st.Name}) {{");
            o.Indent++;
            o.L($"this.{st.Name} = {st.Name};");
            o.Indent--;
            o.L("}");
        }
        foreach (var (gname, g) in shape.Getters.Select(g => (shape.GetterName[g], g)).Concat(shape.ExtraGetters).OrderBy(x => FieldOrder(c, x.Item2)).ThenBy(x => x.Item1, StringComparer.Ordinal))
        {
            o.Nl();
            o.L($"/** The {g.Name} field (legacy {g.CobolName}). */");
            o.L($"public {TypeName(FieldType(g))} {gname}() {{");
            o.Indent++;
            o.L($"return {g.Name};");
            o.Indent--;
            o.L("}");
        }
        foreach (var n in shapes.NestedOf(c).Where(n => !shapes.Of(n).IsEnumBody))
        {
            o.Nl();
            Class(n, true);
        }
        o.Close();
        current = prev;
    }

    void Doc(ClassDef c)
    {
        var progs = c.Programs.Count > 0 ? string.Join(", ", c.Programs) : c.Outer?.Programs.FirstOrDefault() ?? "";
        o.L("/**");
        o.L($" * {c.SimpleName}: recovered from the COBOL estate by semantic normalization.");
        o.L($" * <p>Legacy: {progs}{(c.Abbr is null ? "" : " (" + c.Abbr + ")")}. Generated by MlCpu.Modernize: do not edit, regenerate.");
        o.L(" */");
    }

    void EnumConstants(ClassDef c, Shapes.Shape shape)
    {
        var parts = new List<string>();
        int k = 0;
        var extra = Edits?.For("add-enum-constant", c.FullName).ToList() ?? [];
        foreach (var (name, args) in shape.EnumValues)
        {
            var head = args.Count == 0 ? name : name + "(" + string.Join(", ", args.Select(a => Expr(a))) + ")";
            bool last = ++k == shape.EnumValues.Count && extra.Count == 0;
            if (shape.EnumBodies.TryGetValue(name, out var body))
            {
                // a constant with a body: the anonymous subclass's methods
                o.Open(head + " {");
                var saved = current;
                current = body;
                constant = name; constantOf = c.FullName;
                bool first = true;
                foreach (var fn in shapes.Of(body).Methods.Where(f => !f.IsCtor))
                {
                    if (!first) o.Nl();
                    first = false;
                    Method(fn);
                }
                constant = null; constantOf = null;
                current = saved;
                o.Close(last ? "};" : "},");
                continue;
            }
            o.L(head + (last ? ";" : ","));
        }
        // constants a model edit adds, after the recovered ones
        for (int x = 0; x < extra.Count; x++)
        {
            o.L(EditDoc(extra[x]));
            Emit(extra[x] with { Text = extra[x].Text.TrimEnd().TrimEnd(';', ',') }, x == extra.Count - 1 ? ";" : ",");
        }
        o.Nl();
    }

    void Fields(ClassDef c, Shapes.Shape shape)
    {
        foreach (var f in c.Statics.Where(s => !shape.EnumFields.Contains(s)))
        {
            var init = shape.StaticInit.TryGetValue(f, out var e) ? " = " + Expr(e) : "";
            var mods = shapes.StaticAccess?.TryGetValue(f, out var sa) == true
                ? ApiVisibility.Modifier(sa) + (shape.FinalStatics.Contains(f) ? "static final" : "static")
                : shape.FinalStatics.Contains(f) ? "public static final" : "static";
            o.L($"{mods} {TypeName(DeclField(f))} {f.Name}{init};");
        }
        // a record's fields are its components
        if (shape.Components is null || ExceptionRoots.Contains(c))
            foreach (var f in c.Fields)
                o.L($"{(shape.FinalFields.Contains(f) ? "private final" : "private")} {TypeName(DeclField(f))} {f.Name};");
        foreach (var e in Edits?.For("add-field", c.FullName) ?? [])
        {
            o.L(EditDoc(e));
            Emit(e);
        }
        if (c.Statics.Count + c.Fields.Count > 0 && shape.Methods.Count > 0) { }
    }

    JType FieldType(FieldDef f) => T.JavaType(T.Of(f));

    /// <summary>A parameter's name in a declaration without a body (abstract, interface): the lifted method's, completed.</summary>
    string ParamName(FunctionDef fn, int i) =>
        Naming.Safe(methods.TryGetValue(fn, out var lm) && i < lm.L.Params.Count ? lm.L.Params[i].Name
            : fn.Params[i].Hint is { } h ? Naming.Camel(h) : "p" + fn.Params[i].No);

    // ================================================================== methods

    Method? cur;
    Dictionary<Var, string> names = [];
    HashSet<string> taken = [];

    void Method(FunctionDef fn)
    {
        if (!methods.TryGetValue(fn, out var m)) { o.L($"// {fn.Id} {fn.Signature}: not lifted"); Residue.Add(fn.Id + " not lifted"); return; }
        cur = m;
        names = [];
        taken = new HashSet<string>(StringComparer.Ordinal);
        var ps = new List<string>();
        for (int i = 0; i < m.L.Params.Count; i++)
        {
            var p = m.L.Params[i];
            var nm = Unique(p.Name);
            names[p] = nm;
            ps.Add(TypeName(DeclParam(fn, i)) + " " + nm);
        }
        var shape = shapes.Of(current!);
        o.L(MethodDoc(fn, null));
        string head;
        var throws = Throws?.GetValueOrDefault(fn) is { Count: > 0 } tl ? " throws " + string.Join(", ", tl.Select(x => TypeName(JType.Of(x)))) : "";
        if (fn.IsClinit && throws.Length > 0) Residue.Add($"{fn.Id} static initializer throws checked{throws[7..]}");
        if (fn.IsClinit) head = "static";
        else if (fn.IsCtor) head = (shape.IsEnum ? "" : shapes.MemberAccess?.TryGetValue(fn, out var ca) == true ? ApiVisibility.Modifier(ca) : "public ") + current!.SimpleName + "(" + string.Join(", ", ps) + ")" + throws;
        else
        {
            var ret = fn.Ret is null ? "void" : TypeName(DeclReturn(fn));
            var vis = shapes.MemberAccess?.TryGetValue(fn, out var ma) == true ? ApiVisibility.Modifier(ma) : shape.Private.Contains(fn) ? "private " : "public ";
            head = vis + (fn.IsStatic ? "static " : "") + ret + " " + shape.MethodName(fn) + "(" + string.Join(", ", ps) + ")" + throws;
        }
        o.Open(head + " {");
        // a model edit's body in place of the recovered one: the signature (the call sites) stays the model's
        var owner = constantOf ?? current!.FullName;
        if (Edits is not null && !fn.IsClinit
            && Edits.Replacement(owner, constant, (fn.IsCtor ? current!.SimpleName : shape.MethodName(fn)) + "(" + string.Join(", ", fn.Params.Select((_, i) => TypeName(ParamType(fn, i)))) + ")") is { } edit)
        {
            ImportsOf(m);
            o.L("// " + EditDoc(edit)[4..^3]);
            Emit(edit);
            o.Close();
            cur = null;
            return;
        }
        Declarations(m.Body);
        NameLocals(m);
        Block(m.Body);
        o.Close();
        cur = null;
    }

    string Unique(string n)
    {
        n = Naming.Safe(n);
        if (taken.Add(n)) return n;
        for (int k = 2; ; k++) if (taken.Add(n + k)) return n + k;
    }

    /// <summary>
    /// The method's local names, in order of first appearance; a for-each variable J3S left as a frame temporary (t20_) is
    /// named last, after its element type, so the variables the method already names keep their names.
    /// </summary>
    void NameLocals(Method m)
    {
        TempHints(m);
        var deferred = new List<Var>();
        foreach (var st in Passes.All(m.Body))
            foreach (var v in VarsOf(st))
            {
                if (v.IsThis || m.L.Captured.ContainsValue(v)) continue;
                if (!v.IsTemp && FrameTemp.IsMatch(v.Name) && eachVars.Contains(v) && !names.ContainsKey(v)) { if (!deferred.Contains(v)) deferred.Add(v); }
                else NameOf(v);
            }
        foreach (var v in deferred) if (!names.ContainsKey(v)) names[v] = Unique(ElementName(v));
    }

    static readonly System.Text.RegularExpressions.Regex FrameTemp = new(@"^t\d+_$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>A loop variable's name from its element type (RepaymentPeriod: repaymentPeriod, else period when that is taken).</summary>
    string ElementName(Var v)
    {
        var full = TempName(v);
        if (!taken.Contains(Naming.Safe(full))) return full;
        var words = System.Text.RegularExpressions.Regex.Split(full, "(?<=[a-z0-9])(?=[A-Z])");
        if (words.Length > 1)
        {
            var last = Naming.LowerFirst(words[^1]);
            if (!taken.Contains(Naming.Safe(last))) return last;
        }
        return full;
    }

    string NameOf(Var v)
    {
        if (names.TryGetValue(v, out var n)) return n;
        var basis = v.IsTemp ? tempHints.GetValueOrDefault(v) ?? TempName(v) : v.Name;
        n = Unique(basis);
        names[v] = n;
        return n;
    }

    /// <summary>
    /// A temporary assigned once from a call of an estate method without arguments (an accessor: principal(),
    /// getTotalPaid()) is named after it (principal, totalPaid), not after its type (bigDecimal, bigDecimal2).
    /// </summary>
    readonly Dictionary<Var, string> tempHints = [];

    void TempHints(Method m)
    {
        tempHints.Clear();
        var assigned = new Dictionary<Var, int>();
        var calls = new Dictionary<Var, ECall>();
        foreach (var st in Passes.All(m.Body))
            if (st is SAssign { Lhs: EVar { V: { IsTemp: true } tv } } ta)
            {
                assigned[tv] = assigned.GetValueOrDefault(tv) + 1;
                if (ta.Rhs is ECall { Fn: not null, Args.Count: 0, Recv: not null } tc) calls[tv] = tc;
            }
        foreach (var (tv, c) in calls)
        {
            if (assigned[tv] != 1) continue;
            var name = shapes.MethodName(c.Fn!);
            // an accessor only (a getter, a record component): a method that computes or builds (build(), next()) names nothing
            if (c.Fn!.Owner is not { } fnOwner || !IsAccessor(fnOwner, name, c.Fn)) continue;
            if (name.Length > 3 && name.StartsWith("get", StringComparison.Ordinal) && char.IsUpper(name[3])) name = Naming.LowerFirst(name[3..]);
            else if (name.Length > 2 && name.StartsWith("is", StringComparison.Ordinal) && char.IsUpper(name[2])) continue;
            if (name.Length == 0 || !char.IsLetter(name[0])) continue;
            // a field of the class keeps its name: the local is qualified by what it was read from (loanTermsPrincipal)
            if (reserved.Contains(name))
            {
                var recvType = ExprType(c.Recv!)?.Simple;
                if (recvType is not { Length: > 0 } || !char.IsLetter(recvType[0])) continue;
                name = Naming.LowerFirst(recvType) + char.ToUpperInvariant(name[0]) + name[1..];
            }
            tempHints[tv] = Naming.Safe(name);
        }
    }

    /// <summary>The field names of the class whose methods are written (a program class's: set by AppWriter).</summary>
    public HashSet<string>? ReservedNames { get; set; }

    /// <summary>A getter of the class (or a record component), or a method whose whole body returns one of its fields.</summary>
    bool IsAccessor(ClassDef owner, string name, FunctionDef fn)
    {
        if (methods.TryGetValue(fn, out var fm) && fm.Body is [SReturn { E: EField { Obj: EThis } }]) return true;
        for (var x = owner; x is not null; x = x.Super)
        {
            var s = shapes.Of(x);
            if (s.GetterName.Values.Contains(name) || s.ExtraGetters.Any(g => g.Name == name) || s.Components?.Any(f => f.Name == name) == true) return true;
        }
        return false;
    }

    HashSet<string> reserved => ReservedNames ?? FieldNames(current);

    static HashSet<string> FieldNames(ClassDef? c)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        for (var x = c; x is not null; x = x.Super) foreach (var f in x.Fields) set.Add(f.Name);
        return set;
    }

    string TempName(Var v)
    {
        // a temporary that survived folding: name it by its type
        var t = T.JavaType(T.Of(v));
        if (t.IsPrimitive) return t.Name switch { "boolean" => "flag", "int" => "n", "long" => "count", _ => "value" };
        var simple = t.Simple;
        if (simple.Length == 0 || simple == "Object") return "value";
        return Naming.LowerFirst(simple.Replace("[]", "s"));
    }

    // ------------------------------------------------------------------ declarations of locals

    /// <summary>Where each local is declared: the statement list that encloses all its uses, at its first use there.</summary>
    readonly Dictionary<St, List<(Var V, bool Inline)>> declareAt = new(ReferenceEqualityComparer.Instance);

    readonly HashSet<Var> eachVars = [];

    void Declarations(List<St> body)
    {
        declareAt.Clear();
        eachVars.Clear();
        foreach (var st in Passes.All(body)) if (st is SLoop { Each: { } ev }) eachVars.Add(ev);
        // a counting loop declares its variable in its header
        foreach (var st in Passes.All(body)) if (st is SLoop { Init.Lhs: EVar { V: var iv } }) eachVars.Add(iv);
        // a catch clause declares its variable
        foreach (var st in Passes.All(body)) if (st is STry tr) foreach (var c in tr.Catches) eachVars.Add(c.V);
        // occurrence paths: (list, index, statement-is-a-loop) from the body down to the statement holding the occurrence
        var occ = new Dictionary<Var, List<List<(List<St> List, int Index)>>>();
        void Walk(List<St> list, List<(List<St>, int)> path)
        {
            for (int i = 0; i < list.Count; i++)
            {
                var s = list[i];
                var here = new List<(List<St>, int)>(path) { (list, i) };
                foreach (var v in VarsOf(s))
                {
                    if (cur!.L.Params.Contains(v) || v.IsThis || cur.L.Captured.ContainsValue(v) || eachVars.Contains(v)) continue;
                    if (!occ.TryGetValue(v, out var l)) occ[v] = l = [];
                    l.Add(here);
                }
                foreach (var child in ChildLists(s)) Walk(child, here);
            }
        }
        Walk(body, []);
        foreach (var (v, paths) in occ)
        {
            // deepest depth k where every path has the same list (and, above it, the same statement)
            int k = 0;
            while (true)
            {
                if (paths.Any(p => p.Count <= k + 1)) break;
                var list0 = paths[0][k + 1].List;
                if (!paths.All(p => ReferenceEquals(p[k + 1].List, list0) && p[k].Index == paths[0][k].Index)) break;
                k++;
            }
            // the declaration list and the first statement in it that holds an occurrence
            var declList = paths[0][k].List;
            int idx = paths.Min(p => p[k].Index);
            // inside a loop, a variable whose first use is not an assignment keeps its value across iterations: hoist it
            while (k > 0 && InLoop(paths[0], k) && !(declList[idx] is SAssign { Lhs: EVar ev0 } a0 && ev0.V == v && !Fold.Mentions(a0.Rhs, v)))
            {
                k--;
                declList = paths[0][k].List;
                idx = paths[0][k].Index;
            }
            var st = declList[idx];
            bool inline = st is SAssign { Lhs: EVar ev } a && ev.V == v && !Fold.Mentions(a.Rhs, v) && paths.Any(p => p.Count == k + 1 && p[k].Index == idx);
            if (!declareAt.TryGetValue(st, out var l2)) declareAt[st] = l2 = [];
            l2.Add((v, inline));
        }
    }

    /// <summary>Whether the list at depth k of the path is the body of a loop (or nested in one below the root).</summary>
    static bool InLoop(List<(List<St> List, int Index)> path, int k)
    {
        for (int d = 0; d < k; d++)
            if (path[d].List[path[d].Index] is SLoop) return true;
        return false;
    }

    static IEnumerable<List<St>> ChildLists(St s) => s switch
    {
        SIf i => [i.Then, i.Else],
        SLoop l => [l.Body],
        SBlock b => [b.Body],
        SSwitch sw => sw.Cases.Select(c => c.Body).Concat(sw.Default is null ? [] : [sw.Default]),
        STry t => new[] { t.Body }.Concat(t.Catches.Select(c => c.Body)),
        _ => [],
    };

    static IEnumerable<Var> VarsOf(St s)
    {
        var acc = new List<Var>();
        void E(Expr e)
        {
            if (e is EVar v) acc.Add(v.V);
            if (e is ELambda l) foreach (var c in l.Captures.Values) E(c);
            foreach (var c in e.Children) if (e is not ELambda) E(c);
        }
        if (s is SAssign { Lhs: EVar lv }) acc.Add(lv.V);
        if (s is SLoop { Each: { } each }) acc.Add(each);
        foreach (var e in Fold.Exprs(s)) E(e);
        return acc.Distinct();
    }

    // ------------------------------------------------------------------ statements

    void Block(List<St> list)
    {
        var used = Passes.UsedLabels(list);
        foreach (var s in list) Statement(s, used);
    }

    HashSet<string> labelsUsed = [];

    void Statement(St s, HashSet<string> used)
    {
        if (declareAt.TryGetValue(s, out var decls))
            foreach (var (v, inline) in decls)
                if (!inline) o.L($"{TypeName(DeclVar(v))} {NameOf(v)} = {Default(VarType(v))};");
        Mark(s);
        switch (s)
        {
            case SAssign { Lhs: EField lf } fa when lf.F.Owner is { } wo && current is not null && TopOf(wo) != TopOf(current):
                o.L($"{Expr(lf.Obj, 16)}.{shapes.Setter(wo, lf.F)}({Cast(FieldType(lf.F), fa.Rhs)});");
                break;
            case SAssign { Lhs: ERecordField glrf } gra when GroupRaw(glrf) is { } graw:
                o.L(graw.EndsWith(".text") ? $"{graw}({Expr(gra.Rhs)});" : graw.EndsWith("(") ? $"{graw}{Expr(gra.Rhs)});" : $"{graw}, {Expr(gra.Rhs)});");
                break;
            case SAssign { Lhs: ERecordField lrf } ra:
            {
                if (RecordAccessor(lrf) is not { } acc) { o.L($"/* RESIDUE record {lrf.Item.Name} */"); break; }
                var ft = ExprType(ra.Rhs)?.Name == "java.time.LocalDate" ? null : AppModel.FieldType(lrf.Item);
                // a whole number into a decimal field
                var value = ft?.Name == "java.math.BigDecimal" && ra.Rhs is ELit { Value: long lz } ? (lz == 0 ? "BigDecimal.ZERO" : $"BigDecimal.valueOf({lz})") : Cast(ft, ra.Rhs);
                if (value.StartsWith("BigDecimal.")) imports.Add("java.math.BigDecimal");
                o.L($"{Inst(lrf.Via, acc.R.Instance)}.{acc.Name}({Subscripts(lrf, ", ")}{value});");
                break;
            }
            case SAssign { Lhs: ECode lcd } rcd:
                o.L($"{CodeInstance(lcd)}.{lcd.Accessor}({Expr(rcd.Rhs)});");
                break;
            case SAssign { Lhs: ERecRaw { Len: { } dl } dlr } dra:
                o.L($"{Inst(dlr.Via, dlr.Instance)}.raw({(dlr.Offset is null ? "0" : Expr(dlr.Offset))}, {Expr(dl)}, {Expr(dra.Rhs)});");
                break;
            case SAssign { Lhs: ERecRaw lrr } ra2:
                o.L(lrr.Offset is null ? $"{Inst(lrr.Via, lrr.Instance)}.text({Expr(ra2.Rhs)});" : ViewOf(lrr) is { } wv ? $"{Inst(lrr.Via, lrr.Instance)}.{wv}({Expr(ra2.Rhs)});" : $"{Inst(lrr.Via, lrr.Instance)}.raw({Expr(lrr.Offset)}, {lrr.Length}, {Expr(ra2.Rhs)});");
                break;
            case SAssign a:
            {
                bool decl = declareAt.TryGetValue(s, out var d2) && a.Lhs is EVar ev && d2.Any(x => x.V == ev.V && x.Inline);
                var lhs = a.Lhs is EVar ev2 && decl ? TypeName(DeclVar(ev2.V)) + " " + NameOf(ev2.V) : Expr(a.Lhs);
                o.L(!decl && Compound(a) is { } cmp ? cmp + ";" : $"{lhs} = {Cast(TargetType(a.Lhs), a.Rhs)};");
                break;
            }
            case SExpr e: o.L(Expr(e.E) + ";"); break;
            case SReturn r: o.L(r.E is null ? "return;" : "return " + Cast(cur?.Fn.Ret is null || cur.Fn.IsLambda ? null : T.JavaType(T.Ret(cur.Fn)), r.E) + ";"); break;
            case SThrow t: o.L("throw " + Expr(t.E) + ";"); break;
            case SIf i:
            {
                o.Open($"if ({Cond(i.C)}) {{");
                Block(i.Then);
                while (i.Else is [SIf elif] && !declareAt.ContainsKey(elif))
                {
                    o.Indent--;
                    o.Pad(); o.Text($"}} else if ({Cond(elif.C)}) {{"); o.Nl();
                    o.Indent++;
                    Mark(elif);
                    Block(elif.Then);
                    i = elif;
                }
                if (i.Else.Count > 0)
                {
                    o.Indent--;
                    o.L("} else {");
                    o.Indent++;
                    Block(i.Else);
                }
                o.Close();
                break;
            }
            case SLoop l:
            {
                var label = Passes.UsedLabels(l.Body).Contains(l.Label) || Labeled(l.Label) ? l.Label + ": " : "";
                if (l.Each is not null) o.Open($"{label}for ({TypeName(DeclVar(l.Each))} {NameOf(l.Each)} : {Expr(l.Iterable!)}) {{");
                else if (l.Init is { Lhs: EVar iv } init)
                    o.Open($"{label}for ({TypeName(DeclVar(iv.V))} {NameOf(iv.V)} = {Cast(TargetType(init.Lhs), init.Rhs)}; {(l.C is null ? "" : Cond(l.C))}; {Compound(l.Update!) ?? Expr(l.Update!.Lhs) + " = " + Expr(l.Update.Rhs)}) {{");
                else o.Open($"{label}while ({(l.C is null ? "true" : Cond(l.C))}) {{");
                Block(l.Body);
                o.Close();
                break;
            }
            case SBlock b:
                o.Open($"{b.Label}: {{");
                Block(b.Body);
                o.Close();
                break;
            case SBreak br: o.L(br.Label is null ? "break;" : $"break {br.Label};"); break;
            case SContinue co: o.L(co.Label is null ? "continue;" : $"continue {co.Label};"); break;
            case SSwitch sw:
                o.Open($"switch ({Expr(sw.Subject)}) {{");
                foreach (var (labels, body) in sw.Cases)
                {
                    o.Open($"case {string.Join(", ", labels.Select(CaseLabel))} -> {{");
                    Block(body);
                    o.Close();
                }
                if (sw.Default is not null) { o.Open("default -> {"); Block(sw.Default); o.Close(); }
                o.Close();
                break;
            case STry t:
                o.Open("try {");
                Block(t.Body);
                foreach (var (ty, v, body) in t.Catches)
                {
                    o.Indent--;
                    o.L($"}} catch ({TypeName(ty)} {NameOf(v)}) {{");
                    o.Indent++;
                    Block(body);
                }
                o.Close();
                break;
            case SComment c: o.L("// " + c.Text); break;
            default: o.L("/* " + s.GetType().Name + " */"); Residue.Add($"{cur?.Fn.Id} statement {s.GetType().Name}"); break;
        }
    }

    /// <summary>v = v + 1 â†’ v++, v = v - k â†’ v -= k (a number or a string; the compound form narrows as the cast did).</summary>
    string? Compound(SAssign a)
    {
        if (a.Lhs is not EVar { V: var v } || a.Rhs is not EBinary { Op: "+" or "-" or "*" } b || b.L is not EVar { V: var l } || l != v) return null;
        var t = VarType(v);
        if (!(t.IsPrimitive && t.Name != "boolean" || b.Op == "+" && t.Name == "java.lang.String")) return null;
        if (b.R is ELit { Value: long or int } one && Convert.ToInt64(one.Value) == 1 && b.Op != "*" && t.Name != "java.lang.String") return NameOf(v) + (b.Op == "+" ? "++" : "--");
        return $"{NameOf(v)} {b.Op}= {Expr(b.R)}";
    }

    bool Labeled(string label) => cur is not null && Passes.All(cur.Body).Any(x => x is SBreak { Label: var b } && b == label || x is SContinue { Label: var c } && c == label);

    /// <summary>A method's Javadoc: its signature and its legacy provenance (the source lines when J3S recorded them, the program and function).</summary>
    static string MethodDoc(FunctionDef fn, string? note) =>
        "/** " + Esc(fn.Signature) + (note is null ? "" : ": " + note)
        + (fn.SrcAbbr is null ? $" (legacy {fn.Program.Id} {fn.Id})" : $" (legacy {fn.SrcAbbr}:{fn.SrcFrom}-{fn.SrcTo}, {fn.Program.Id} {fn.Id})") + " */";

    /// <summary>A Y/N indicator field (read as is, or its padded characters trimmed): the field read through its boolean accessor; else null.</summary>
    ERecordField? FlagField(Expr e)
    {
        var rf = e switch
        {
            ERecordField { Padded: false } f => f,
            ECall { Name: "stripTrailing", Recv: ERecordField { Padded: true } p, Args.Count: 0 } => p,
            _ => null,
        };
        if (rf is null || App is null || Prog is null || rf.RefStart != 0 || !rf.Item.IsLeaf || App.RecordOf(Prog, rf.Item) is not { } r || !r.Rc.Flags.Contains(rf.Item.Name)) return null;
        return new ERecordField(rf.Item) { Index = rf.Index, More = rf.More, Type = JType.Bool, Via = rf.Via };
    }

    void SiteAt(int line, string site)
    {
        if (markAll || current is not null && M.IsCalculationClass(current)) SourceMap.Add((currentFile, -line, site));
    }

    /// <summary>The dot before a call: on a new line, marked with the call's own site, when it continues a chain.</summary>
    string Dot(ECall c) => c.LineSite is null ? "." : "\n" + new string(' ', (o.Indent + 2) * 4) + "\u0001" + c.LineSite + "\u0002.";

    void Mark(St s)
    {
        if (s.Site is null || !(markAll || current is not null && M.IsCalculationClass(current))) return;
        // in a lambda body the line is known only when the enclosing statement is printed: a mark in the text
        if (o.Deferred) o.Sb.Append('\u0001').Append(s.Site).Append('\u0002');
        else SourceMap.Add((currentFile, -o.Line, s.Site));
    }

    string CaseLabel(Expr e) => e is ELit { Value: long v } ? v.ToString(CultureInfo.InvariantCulture) : Expr(e);

    JType VarType(Var v) => VarTypeOf(T, v);
    bool Vars(Var v) => T.Vars.ContainsKey(v);

    /// <summary>The type a local is declared with (<see cref="VarType"/>), shared with R5 (which reads declarations, never prints).</summary>
    internal static JType VarTypeOf(TypeInference T, Var v) => v.Fixed && v.Type is { } ft ? ft : v.Type is { } t && !t.IsObject && t.Name != "null" && (t.IsPrimitive || t.Class is not null) && v.TypeSlot < 0 && !T.Vars.ContainsKey(v) ? t : T.JavaType(T.Of(v));

    // R5 program-interfaces (opt-in): only where a declaration is printed; expression types, casts and names keep the lift's types
    JType DeclVar(Var v) => shapes.Widened?.Locals.TryGetValue(v, out var w) == true ? w : VarType(v);
    JType DeclField(FieldDef f) => shapes.Widened?.Fields.TryGetValue(f, out var w) == true ? w : FieldType(f);
    JType DeclParam(FunctionDef fn, int i) => shapes.Widened?.Params.TryGetValue((fn, i), out var w) == true ? w : ParamType(fn, i);
    JType DeclReturn(FunctionDef fn) => shapes.Widened?.Returns.TryGetValue(fn, out var w) == true ? w : T.JavaType(T.Ret(fn));

    static string Default(JType t) => t.Name switch
    {
        "int" or "long" or "double" or "short" or "byte" => "0", "boolean" => "false", "char" => "'\\0'", _ => "null",
    };

    // ================================================================== casts

    JType? TargetType(Expr lhs) => lhs switch
    {
        EVar v => VarType(v.V),
        EField f => FieldType(f.F),
        EStatic st => FieldType(st.F),
        EProgField pf => pf.FieldType,
        _ => null,
    };

    /// <summary>The static type of an expression as printed (null when not known).</summary>
    JType? ExprType(Expr e) => e switch
    {
        EVar v when cur is not null && cur.L.Captured.ContainsValue(v.V) => null,
        EVar v => VarType(v.V),
        EField f => FieldType(f.F),
        EStatic st => FieldType(st.F),
        EProgField pf => pf.FieldType,
        ERecordField rf => rf.Type,
        ERecRaw => JType.String,
        ECode cd => cd.Type,
        ECall { Fn: { Ret: not null } fn, Kind: CallKind.Static or CallKind.Instance } => T.JavaType(T.Ret(fn)),
        ENew { Class: { } c } => JType.Of(c),
        ECast c => c.T,
        ECall { Type: { } ct } => ct,
        EIndex ix when ExprType(ix.A) is { IsArray: true } at => JType.Of(at.Name[..^2]),
        _ => null,
    };

    /// <summary>The expression, cast when an Object value reaches a typed target (J3S checked these casts).</summary>
    JType? arrayTarget;

    /// <summary>
    /// A parameter's type. A collection of Object that callers pass with different element types is a generic method's
    /// List&lt;T&gt; (generateInterestScheduleModel): List&lt;?&gt; reads the same and accepts every caller.
    /// </summary>
    JType ParamType(FunctionDef fn, int i) => ParamTypeOf(T, fn, i);

    /// <summary>The type a parameter is declared with (<see cref="ParamType"/>), shared with R5.</summary>
    internal static JType ParamTypeOf(TypeInference T, FunctionDef fn, int i)
    {
        var t = T.JavaType(T.Param(fn, i));
        if (t.Name is "java.util.List" or "java.util.Collection" or "java.util.Set" or "java.util.Iterator" && t.Args is [{ IsObject: true }] && T.Param(fn, i).Rep.Generic)
            return JType.Of(t.Name, JType.Of("?"));
        return t;
    }

    /// <summary>The topmost estate class of each class J3S raises as an exception (its JO-SIMPLE-NAME ids): Java's RuntimeException below it.</summary>
    HashSet<ClassDef> ExceptionRoots => exceptionRoots ??= M.ExceptionNames.Keys
        .Select(id => M.ClassById.GetValueOrDefault(id)).Where(c => c is not null)
        .Select(c => { var t = c!; while (t.Super is not null) t = t.Super; return t; }).ToHashSet();
    HashSet<ClassDef>? exceptionRoots;

    string Cast(JType? target, Expr e)
    {
        arrayTarget = target;
        var text = Expr(e);
        arrayTarget = null;
        if (target is null) return text;
        if (e is ELit { Value: long lv } && target.Name is "java.lang.Long" or "long") return lv + "L";
        if (target.Name == "int" && ExprType(e) is { Name: "long" }) return "(int) " + Expr(e, 14);
        if (target.IsPrimitive || target.IsObject) return text;
        var st = ExprType(e);
        if (st is not null && st.IsObject) return "(" + TypeName(target) + ") " + Expr(e, 14);
        if (st is not null && st.Class is { } sc && target.Class is { } tc && sc != tc && IsSuper(sc, tc)) return "(" + TypeName(target) + ") " + Expr(e, 14);
        if (st is not null && target.Class is not null && shapes.Interfaces.ContainsKey(st.Name)) return "(" + TypeName(target) + ") " + Expr(e, 14);
        return text;
    }

    /// <summary>A receiver, cast when its static type does not have the member's class (an Object parameter used as a class).</summary>
    string Receiver(Expr recv, ClassDef? owner)
    {
        var text = Expr(recv, 16);
        if (owner is null || owner.Kind != ClassKind.Domain || recv is EThis) return text;
        var st = ExprType(recv);
        if (st is null) return text;
        if (st.Class is { } sc && (sc == owner || IsSuper(owner, sc))) return text;
        if (st.Class is null && !st.IsObject && !shapes.Interfaces.ContainsKey(st.Name)) return text;
        if (st.Class is null && shapes.Interfaces.TryGetValue(st.Name, out var iface) && iface.Methods.Values.Any(m => m.Owner == owner || owner.Subs.Contains(m.Owner!))) return text;
        return "((" + TypeName(JType.Of(owner)) + ") " + Expr(recv, 14) + ")";
    }

    static bool IsSuper(ClassDef sup, ClassDef sub)
    {
        for (var x = sub.Super; x is not null; x = x.Super) if (x == sup) return true;
        return false;
    }

    // ================================================================== expressions

    string Cond(Expr e) => Expr(e, 0);

    public string Expr(Expr e, int ctx = 0)
    {
        var (text, prec) = Print(e);
        return prec < ctx ? "(" + text + ")" : text;
    }

    static int Prec(string op) => op switch
    {
        "||" => 4, "&&" => 5, "|" => 6, "^" => 7, "&" => 8, "==" or "!=" => 9, "<" or "<=" or ">" or ">=" => 10,
        "<<" or ">>" or ">>>" => 11, "+" or "-" => 12, "*" or "/" or "%" => 13, _ => 9,
    };

    (string Text, int Prec) Print(Expr e)
    {
        switch (e)
        {
            case EVar v:
                if (cur is not null && cur.L.Captured.ContainsValue(v.V)) return (names.TryGetValue(v.V, out var capName) ? capName : v.V.Name, 16);
                return (NameOf(v.V), 16);
            case EThis: return ("this", 16);
            case ELit l: return (Literal(l), 16);
            case EStaticJdk sj: return (TypeName(JType.Of(sj.Owner)) + "." + sj.Name, 16);
            case EField f: return (Field(f), 16);
            case EStatic s: return (Static(s), 16);
            // BigDecimal.valueOf(F * 10^k, k), F a decimal record field of scale k: the field
            case ECall { Kind: CallKind.JdkStatic, Owner: "java.math.BigDecimal", Name: "valueOf", Args: [EBinary { Op: "*", L: ERecordField { Type.Name: "java.math.BigDecimal" } vf, R: ELit { Value: long vm } }, ELit { Value: long vs }] }
                when vf.Item.Scale == vs && vm == (long)Math.Pow(10, vs):
                return (RecordField(vf), 16);
            // a trimmed record field compared with a literal: COBOL pads the shorter operand, so trailing spaces do not count
            // a Y/N indicator compared with 'Y': the record's boolean accessor
            case ECall { Name: "equals", Recv: { } yr, Args: [ELit { Value: "Y" }] } when FlagField(yr) is { } yf:
                return (RecordField(yf), 16);
            case ECall { Name: "equals", Recv: ELit { Value: "Y" }, Args: [{ } ya] } when FlagField(ya) is { } yf2:
                return (RecordField(yf2), 16);
            case ECall { Name: "equals", Recv: ERecordField { Padded: false, Type.Name: "java.lang.String" } ef, Args: [ELit { Value: string el }] } when el != el.TrimEnd():
                return el.TrimEnd().Length == 0 ? (RecordField(ef) + ".isEmpty()", 16) : (RecordField(ef) + ".equals(" + Literal(ELit.Str(el.TrimEnd())) + ")", 16);
            case ECall { Name: "equals", Recv: ELit { Value: string el2 }, Args: [ERecordField { Padded: false, Type.Name: "java.lang.String" } ef2] } when el2 != el2.TrimEnd():
                return el2.TrimEnd().Length == 0 ? (RecordField(ef2) + ".isEmpty()", 16) : (Literal(ELit.Str(el2.TrimEnd())) + ".equals(" + RecordField(ef2) + ")", 16);
            case ECall { Name: "stripTrailing", Recv: ERecordField { Padded: true } prf, Args.Count: 0 }: return (RecordField(new ERecordField(prf.Item) { Index = prf.Index, More = prf.More, Type = prf.Type, Via = prf.Via }), 16);
            case ECall c: return (Call(c), 16);
            case ENew n: return (New(n), 16);
            case EUnary u when u.Op == "!" && u.E is EInstanceOf io: return ("!(" + Expr(io, 0) + ")", 14);
            case EUnary u: return (u.Op + Expr(u.E, 14), 14);
            // a Y/N indicator compared with 'Y' (a job function's string comparison): the record's boolean accessor
            case EBinary { Op: "==" or "!=", L: { } fl, R: ELit { Value: "Y" } } fb when FlagField(fl) is { } ff:
                return fb.Op == "==" ? (RecordField(ff), 16) : ("!" + RecordField(ff), 14);
            case EBinary { Op: "==" or "!=", L: ELit { Value: "Y" }, R: { } fr } fb2 when FlagField(fr) is { } ff2:
                return fb2.Op == "==" ? (RecordField(ff2), 16) : ("!" + RecordField(ff2), 14);
            case EBinary b when b.Op is "==C" or "!=C":
                // same class (J3S compares class ids; the left side may be null, the right is the receiver)
                return b.Op == "==C"
                    ? (Expr(b.L, 9) + " != null && " + Expr(b.L, 16) + ".getClass() == " + Expr(b.R, 16) + ".getClass()", 5)
                    : (Expr(b.L, 9) + " == null || " + Expr(b.L, 16) + ".getClass() != " + Expr(b.R, 16) + ".getClass()", 4);
            case EBinary { Op: "==" or "!=", L: ECall { Name: "ordinal", Recv: { } er, Args.Count: 0 }, R: ELit { Value: long ord } } eo
                when ExprType(er) is { Class: { } ec } && shapes.Of(ec).IsEnum && ord >= 0 && ord < shapes.Of(ec).EnumValues.Count:
                return (Expr(er, 9) + " " + eo.Op + " " + TypeName(JType.Of(ec)) + "." + shapes.Of(ec).EnumValues[(int)ord].Name, 9);
            case EBinary b:
            {
                int p = Prec(b.Op);
                var r = b.Op is "-" or "/" or "%" || b.Op is "==" or "!=" ? p + 1 : p;
                if (b.Op is "==" or "!=" && (IsStringType(b.L) || IsStringType(b.R)) && !(b.R is ELit { Value: null } || b.L is ELit { Value: null }))
                {
                    var eq = Expr(b.L, 16) + ".equals(" + Expr(b.R) + ")";
                    // a trimmed record field against a literal with trailing spaces (SPACES): the literal without them
                    var (fl, lit) = b.L is ERecordField { Padded: false } ? (b.L, b.R) : (b.R, b.L);
                    if (fl is ERecordField { Padded: false } && lit is ELit { Value: string ls } && ls != ls.TrimEnd())
                        eq = ls.TrimEnd().Length == 0 ? Expr(fl, 16) + ".isEmpty()" : Expr(fl, 16) + ".equals(" + Literal(ELit.Str(ls.TrimEnd())) + ")";
                    return b.Op == "==" ? (eq, 16) : ("!" + eq, 14);
                }
                return (Expr(b.L, p) + " " + b.Op + " " + Expr(b.R, r), p);
            }
            case ECond c:
            {
                // c ? Optional.of(x) : Optional.empty() as a receiver: Java needs the type argument
                string Branch(Expr x, Expr other, int ctx) => x is ECall { Kind: CallKind.JdkStatic, Owner: "java.util.Optional", Name: "empty", Args.Count: 0 }
                    && other is ECall { Kind: CallKind.JdkStatic, Owner: "java.util.Optional", Name: "of" or "ofNullable", Args: [var ov] } && ExprType(ov) is { IsObject: false, IsPrimitive: false } ot
                    ? "Optional.<" + TypeName(ot) + ">empty()" : Expr(x, ctx);
                return (Expr(c.C, 4) + " ? " + Branch(c.A, c.B, 4) + " : " + Branch(c.B, c.A, 3), 3);
            }
            case EInstanceOf i: return (Expr(i.E, 10) + " instanceof " + TypeName(Erased(i.T)), 10);
            case ECast c: return ("(" + TypeName(c.T) + ") " + Expr(c.E, 14), 14);
            case ELambda l: return (Lambda(l), 2);
            case EArrayNew a: return ("new " + TypeName(arrayTarget is { IsArray: true } at && a.Elem.IsObject ? JType.Of(at.Name[..^2]) : a.Elem) + "[" + Expr(a.Len) + "]", 16);
            case EIndex x: return (Expr(x.A, 16) + "[" + Expr(x.I) + "]", 16);
            case EArrayLength al: return (Expr(al.A, 16) + ".length", 16);
            case ERecordField rf: return (RecordField(rf), 16);
            case ECode cd: return ($"{CodeInstance(cd)}.{cd.Accessor}()", 16);
            case EEnum en: return (en.Bare ? en.Constant : TypeName(JType.Of(en.EnumType)) + "." + en.Constant, 16);
            case EProgField pf: return (pf.Name, 16);
            case EClassLit cl: return (TypeName(cl.T) + ".class", 16);
            case ERecRaw { Len: { } dl } dr: return ($"{Inst(dr.Via, dr.Instance)}.raw({(dr.Offset is null ? "0" : Expr(dr.Offset))}, {Expr(dl)})", 16);
            case ERecRaw rr: return (rr.Offset is null ? $"{Inst(rr.Via, rr.Instance)}.text()" : ViewOf(rr) is { } rv ? $"{Inst(rr.Via, rr.Instance)}.{rv}()" : $"{Inst(rr.Via, rr.Instance)}.raw({Expr(rr.Offset)}, {rr.Length})", 16);
            case EPart p:
                Residue.Add($"{cur?.Fn.Id} part {p.Part} of {IrText.Show(p.Whole)}");
                return ($"RESIDUE(\"part {p.Part} of {Esc(IrText.Show(p.Whole))}\")", 16);
            case EOpaque op:
                Residue.Add($"{cur?.Fn.Id} {op.Text}");
                return ($"RESIDUE(\"{Esc(op.Text)}\")", 16);
            case EAlloc al2:
                Residue.Add($"{cur?.Fn.Id} allocation {al2.Id}");
                return ($"RESIDUE(\"alloc {al2.Id}\")", 16);
        }
        Residue.Add($"{cur?.Fn.Id} expression {e.GetType().Name}");
        return ($"RESIDUE(\"{e.GetType().Name}\")", 16);
    }

    bool IsStringType(Expr e) => e is ELit { Value: string } || e.Type?.Name == "java.lang.String"
        || e is EVar v && T.JavaType(T.Of(v.V)).Name == "java.lang.String" || e is EField f && FieldType(f.F).Name == "java.lang.String";

    static JType Erased(JType t) => t.Args.Count == 0 ? t : JType.Of(t.Name);

    static string Literal(ELit l) => l.Value switch
    {
        null => "null",
        bool b => b ? "true" : "false",
        string s => "\"" + Esc(s) + "\"",
        long v when v is >= int.MinValue and <= int.MaxValue => v.ToString(CultureInfo.InvariantCulture),
        long v => v.ToString(CultureInfo.InvariantCulture) + "L",
        decimal d => d.ToString(CultureInfo.InvariantCulture),
        double dv => dv.ToString("R", CultureInfo.InvariantCulture) is var ds && (ds.Contains('.') || ds.Contains('E')) ? ds : ds + ".0",
        char c => "'" + Esc(c.ToString()) + "'",
        _ => l.Value.ToString()!,
    };

    static string Esc(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\0", "\\0").Replace("\uFFFF", "\\uFFFF");

    string Field(EField f)
    {
        var owner = f.F.Owner;
        bool sameTop = owner is not null && current is not null && TopOf(owner) == TopOf(current);
        if (f.Obj is EThis && cur?.Fn.IsLambda != true)
            return names.ContainsValue(f.F.Name) ? "this." + f.F.Name : f.F.Name;
        if (sameTop) return Receiver(f.Obj, owner) + "." + f.F.Name;
        // another class: its accessor
        var getter = owner is null ? f.F.Name : shapes.Accessor(owner, f.F);
        return Receiver(f.Obj, owner) + "." + getter + "()";
    }

    static ClassDef TopOf(ClassDef c) { while (c.Outer is not null) c = c.Outer; return c; }

    string Static(EStatic s)
    {
        var f = s.F;
        var owner = f.Owner;
        // a job object (SJOB group): an instance field of the job class (this. when a local of the method has its name)
        if (owner is null && f.Group == "SJOB" && Prog is not null) { var jn = JobObjectName(f); return names.ContainsValue(jn) ? "this." + jn : jn; }
        if (owner is null) { Residue.Add($"static {f.CobolName} without a class"); return $"RESIDUE(\"static {f.CobolName}\")"; }
        if (current is not null && (owner == current || TopOf(owner) == TopOf(current) && shapes.Of(owner).IsEnum == false && owner == current)) return f.Name;
        return TypeName(JType.Of(owner)) + "." + f.Name;
    }

    string Call(ECall c)
    {
        var args = string.Join(", ", c.Args.Select((a, i) => c.Fn is { } f && i < f.Params.Count && c.Kind is CallKind.Static or CallKind.Instance or CallKind.Super ? Cast(T.JavaType(T.Param(f, i)), a) : Expr(a)));
        // a job function calling another of its job program: a method of the program class
        if (c.Fn is { IsJob: true } jf && Prog is not null && Prog.MethodNames.TryGetValue(jf.Paragraph, out var jobMethod)) return $"{jobMethod}({args})";
        switch (c.Kind)
        {
            case CallKind.Static:
            {
                var name = shapes.MethodName(c.Fn!);
                if (c.Fn!.Owner == current || current is not null && IsAncestor(c.Fn.Owner, current)) return $"{name}({args})";
                return $"{TypeName(JType.Of(c.Fn.Owner!))}.{name}({args})";
            }
            case CallKind.Instance:
            {
                var name = c.Fn is null ? c.Name : shapes.MethodName(c.Fn);
                if (c.Recv is null or EThis) return $"{name}({args})";
                // a virtual call through a superclass of the implementation it was lifted from: no cast
                if (c.Virtual && c.Fn?.Owner is { } vo && ExprType(c.Recv)?.Class is { } vc && IsSuper(vc, vo)) return $"{Expr(c.Recv, 16)}{Dot(c)}{name}({args})";
                return $"{Receiver(c.Recv, c.Fn?.Owner)}{Dot(c)}{name}({args})";
            }
            case CallKind.Super: return $"{c.Name}({args})";
            case CallKind.Jdk:
                if (c.Recv is ELit { Value: long lv }) return $"Integer.valueOf({lv}).{c.Name}({args})";
                if (c.Recv is ELit { Value: bool bv }) return $"{(bv ? "Boolean.TRUE" : "Boolean.FALSE")}.{c.Name}({args})";
                return c.Recv is null ? $"{c.Name}({args})" : $"{Expr(c.Recv, 16)}{Dot(c)}{c.Name}({args})";
            case CallKind.JdkStatic:
                return $"{TypeName(JType.Of(c.Owner!))}.{c.Name}({args})";
        }
        return $"{c.Name}({args})";
    }

    public static string JobObjectName(FieldDef f) => Naming.Camel(f.Name);

    /// <summary>A field's place in its class (declaration order), for a stable order of accessors.</summary>
    static int FieldOrder(ClassDef c, FieldDef f) => f.Owner?.Fields.IndexOf(f) is int k and >= 0 ? k : c.Fields.IndexOf(f) is int k2 and >= 0 ? k2 : int.MaxValue;

    static bool IsAncestor(ClassDef? a, ClassDef c)
    {
        for (var x = c.Super; x is not null; x = x.Super) if (x == a) return true;
        return false;
    }

    string New(ENew n)
    {
        var args = string.Join(", ", n.Args.Select((a, i) => n.Ctor is { } ct && i < ct.Params.Count ? Cast(T.JavaType(T.Param(ct, i)), a) : Expr(a)));
        if (n.Class is not null)
        {
            if (shapes.Of(n.Class).IsEnum) { Residue.Add($"new of enum {n.Class.SimpleName}"); }
            return $"new {TypeName(JType.Of(n.Class))}({args})";
        }
        var jdk = n.JdkClass ?? "java.lang.Object";
        bool generic = jdk.StartsWith("java.util.") && jdk != "java.util.Optional" && !jdk.EndsWith("Exception") || jdk == "java.util.concurrent.atomic.AtomicReference";
        // DateTimeException has no constructor without a message
        if (jdk == "java.time.DateTimeException" && n.Args.Count == 0) args = "(String) null";
        return $"new {TypeName(JType.Of(jdk))}{(generic ? "<>" : "")}({args})";
    }

    /// <summary>A record field read: the typed accessor (a date view, the padded text) of the record instance.</summary>
    string RecordField(ERecordField rf)
    {
        if (GroupRaw(rf) is { } raw) return raw + (raw.EndsWith(".text") ? "()" : ")");
        if (RecordAccessor(rf) is not { } acc) return $"RESIDUE(\"record {rf.Item.Name}\")";
        var (r, name) = acc;
        var view = rf.Type?.Name == "java.time.LocalDate" ? "" : r.Rc.DateViews.Contains(rf.Item.Name) ? "Number" : "";
        if (rf.Padded) view = "Text";
        // a Y/N indicator read as a value (copied): its character, not its boolean accessor
        else if (rf.Type?.Name != "boolean" && rf.Item.IsLeaf && r.Rc.Flags.Contains(rf.Item.Name)) view = "Text";
        return $"{Inst(rf.Via, r.Instance)}.{name}{view}({Subscripts(rf, "")})";
    }

    /// <summary>A record field's subscripts as accessor arguments (then the separator when there are any).</summary>
    string Subscripts(ERecordField rf, string sep) =>
        rf.Index is null ? "" : string.Join(", ", new[] { rf.Index }.Concat(rf.More).Select(x => Expr(x))) + sep;

    /// <summary>A group of a record (MOVE SPACES TO FN-SCHEDULE-REC): the characters of its image, "inst.text" or "inst.raw(off, len".</summary>
    string? GroupRaw(ERecordField rf)
    {
        if (App is null || Prog is null || rf.Item.IsLeaf && rf.RefStart == 0 || App.RecordOf(Prog, rf.Item) is not { } r) return null;
        int off = rf.Item.Offset - rf.Item.Record.Offset, len = rf.Item.Length;
        // a reference modification: that part of the field
        if (rf.RefStart > 0) { off += rf.RefStart - 1; len = rf.RefLen > 0 ? rf.RefLen : len - rf.RefStart + 1; }
        if (off == 0 && len == r.Rc.Length) return $"{Inst(rf.Via, r.Instance)}.text";
        // a named view of the record class (a group, adjacent fields, a sign): "inst.view(" (the callers close it)
        return r.Rc.View(off, len) is { } view ? $"{Inst(rf.Via, r.Instance)}.{view}(" : $"{Inst(rf.Via, r.Instance)}.raw({off}, {len}";
    }

    /// <summary>The record instance of a code field (the program's field, or a restructured method's parameter).</summary>
    string CodeInstance(ECode cd) => Inst(cd.Field.Via, App?.RecordOf(Prog!, cd.Field.Item)?.Instance ?? "?");

    /// <summary>The record class's named view of a slice at a constant offset (registered on the class when new), else null.</summary>
    string? ViewOf(ERecRaw rr)
    {
        if (rr.Offset is not ELit { Value: long off } || App is null || Prog is null) return null;
        var rc = Prog.Records.FirstOrDefault(kv => Prog.Instances.TryGetValue(kv.Key, out var i) && i == rr.Instance).Value;
        return rc?.View((int)off, rr.Length);
    }

    /// <summary>A record's instance as printed: the program's field, or the parameter or local a restructured method has.</summary>
    string Inst(Expr? via, string instance) => via is null ? instance : Expr(via, 16);

    ((RecordClass Rc, string Instance) R, string Name)? RecordAccessor(ERecordField rf)
    {
        if (App is null || Prog is null || App.RecordOf(Prog, rf.Item) is not { } r || !r.Rc.Accessors.TryGetValue(rf.Item.Name, out var acc))
        {
            Residue.Add($"record field {rf.Item.Name} in {Prog?.P.Id}");
            return null;
        }
        return (r, acc);
    }

    /// <summary>
    /// One method of a program class (a job function, a paragraph), printed into its own text with the imports it needs
    /// collected into the program file's set; source-map lines are relative to the method text.
    /// </summary>
    public string MethodText(Method m, string head, int indent, HashSet<string> imps, string file, List<(int Line, string Site)> sites)
    {
        var savedO = o; var savedImports = imports; var savedCur = cur; var savedCurrent = current; var savedFile = currentFile;
        o = new Out { Indent = indent, OnSite = SiteAt };
        imports = imps;
        current = m.Fn.Owner;
        currentFile = file;
        cur = m;
        names = [];
        taken = new HashSet<string>(StringComparer.Ordinal);
        // the program class's fields are in scope: locals keep other names
        if (Prog is not null) taken.UnionWith(Prog.FieldNames());
        markAll = true;
        int mark = SourceMap.Count;
        var ps = new List<string>();
        var pts = new List<string>();
        for (int i = 0; i < m.L.Params.Count; i++)
        {
            var p = m.L.Params[i];
            names[p] = Unique(p.Name);
            // a parameter of a synthesized method has its type; a job function's comes from inference
            var pt = m.Fn.Params.Count == 0 && p.Type is { } vt ? vt : ParamType(m.Fn, i);
            pts.Add(TypeName(pt));
            ps.Add(pts[^1] + " " + names[p]);
        }
        o.Open(head + "(" + string.Join(", ", ps) + ") {");
        // a model edit's body in place of a program class's method (the class: its file's)
        if (Edits?.Replacement(file.EndsWith(".java") ? file[..^5].Replace('/', '.') : file, null, head.Split(' ')[^1] + "(" + string.Join(", ", pts) + ")") is { } edit)
        {
            ImportsOf(m);
            o.L("// " + EditDoc(edit)[4..^3]);
            Emit(edit);
            o.Close();
            for (int k = mark; k < SourceMap.Count; k++) sites.Add((-SourceMap[k].Line, SourceMap[k].Site));
            SourceMap.RemoveRange(mark, SourceMap.Count - mark);
            markAll = false;
            var edited = o.Sb.ToString();
            o = savedO; imports = savedImports; cur = savedCur; current = savedCurrent; currentFile = savedFile;
            return edited;
        }
        Declarations(m.Body);
        NameLocals(m);
        Block(m.Body);
        o.Close();
        for (int k = mark; k < SourceMap.Count; k++) sites.Add((-SourceMap[k].Line, SourceMap[k].Site));
        SourceMap.RemoveRange(mark, SourceMap.Count - mark);
        markAll = false;
        var text = o.Sb.ToString();
        o = savedO; imports = savedImports; cur = savedCur; current = savedCurrent; currentFile = savedFile;
        return text;
    }

    /// <summary>A type's name as printed in a program class (its import added to the set).</summary>
    public string TypeNameIn(JType t, HashSet<string> imps)
    {
        var saved = imports;
        imports = imps;
        var n = TypeName(t);
        imports = saved;
        return n;
    }

    /// <summary>The Java type of a job object (a static of the job group, an instance field of the job class).</summary>
    public JType JobObjectType(FieldDef f) => FieldType(f);

    string Lambda(ELambda l)
    {
        var fn = l.Closure.Lambda;
        if (fn is null || !methods.TryGetValue(fn, out var m)) { Residue.Add($"lambda {l.Closure.SimpleName} not lifted"); return $"RESIDUE(\"lambda {l.Closure.SimpleName}\")"; }
        // the captured variables are the values at the capture: substitute them into a copy of the body
        var body = m.Body;
        var saved = (cur, names, taken);
        var savedDecl = new Dictionary<St, List<(Var, bool)>>(declareAt, ReferenceEqualityComparer.Instance);
        var outerNames = names;
        cur = m;
        names = new Dictionary<Var, string>(outerNames);
        taken = new HashSet<string>(saved.taken, StringComparer.Ordinal);
        foreach (var (field, v) in m.L.Captured)
            if (l.Captures.TryGetValue(field, out var cap))
                names[v] = Expr(cap, 16);
        var ps = m.L.Params.Select((p, i) =>
        {
            var nm = Unique(p.Name);
            names[p] = nm;
            // an explicit parameter type when known: the lambda types itself (Comparator.comparing, raw targets)
            var pt = T.JavaType(T.Param(fn, i));
            return pt.IsObject || pt.IsPrimitive ? nm : TypeName(pt) + " " + nm;
        }).ToList();
        bool typed = ps.Any(p => p.Contains(' '));
        if (typed) ps = ps.Select(p => p.Contains(' ') ? p : "Object " + p).ToList();
        string text;
        var head = ps.Count == 1 && !typed ? ps[0] : "(" + string.Join(", ", ps) + ")";
        if (body is [SReturn { E: { } single }]) text = head + " -> " + Expr(single, 3);
        else if (body is [SExpr { E: var single2 }]) text = head + " -> " + Expr(single2, 3);
        else
        {
            // the body's first line follows the statement's first line (and the lambdas before it in the statement)
            var inner = new Out { Indent = o.Indent + 1, Line = o.Line + 1 + o.Pending, OnSite = SiteAt, Deferred = true };
            var savedOut = o;
            o = inner;
            Declarations(body);
            Block(body);
            o = savedOut;
            text = head + " -> {\n" + inner.Sb + new string(' ', o.Indent * 4) + "}";
            o.Pending += text.Count(c => c == '\n');
        }
        (cur, names, taken) = saved;
        declareAt.Clear();
        foreach (var kv in savedDecl) declareAt[kv.Key] = kv.Value;
        return text;
    }

    // ================================================================== types

    public string TypeName(JType t)
    {
        if (t.IsPrimitive) return t.Name;
        if (t.Name == "null") return "Object";
        if (t.IsArray) return TypeName(JType.Of(t.Name[..^2])) + "[]";
        var name = t.Name;
        if ((t.Class ?? M.Classes.GetValueOrDefault(name)) is { Kind: ClassKind.Emulation } emu && JdkMap.JdkTypeOf(emu) is { } jdkName)
            return TypeName(JType.Of(jdkName, [.. t.Args]));
        string simple;
        if (t.Class is { } c && c.Outer is not null)
        {
            var top = TopOf(c);
            imports.Add(top.FullName);
            simple = c.FullName[(top.Package.Length + 1)..];
        }
        else if (M.Classes.TryGetValue(name, out var ec) && ec.Outer is not null)
        {
            var top = TopOf(ec);
            imports.Add(top.FullName);
            simple = ec.FullName[(top.Package.Length + 1)..];
        }
        else if (name == "java.util.Map.Entry") { imports.Add("java.util.Map"); simple = "Map.Entry"; }
        else
        {
            if (name.Contains('.')) imports.Add(name);
            simple = name[(name.LastIndexOf('.') + 1)..];
        }
        if (t.Args.Count > 0) simple += "<" + string.Join(", ", t.Args.Select(TypeName)) + ">";
        return simple;
    }

    // ================================================================== interfaces

    string InterfaceFile(Shapes.Iface i)
    {
        imports = [];
        current = null;
        var body = new Out();
        var saved = o;
        o = body;
        o.L("/** " + i.SimpleName + ": an interface the legacy code dispatches through (recovered from its implementations). */");
        o.Open($"public interface {i.SimpleName} {{");
        foreach (var (key, fn) in i.Methods.OrderBy(x => x.Key, StringComparer.Ordinal))
        {
            var name = shapes.MethodName(fn);
            var ps = fn.Params.Select((p, k) => TypeName(T.JavaType(T.Param(fn, k))) + " " + ParamName(fn, k));
            var ret = fn.Ret is null ? "void" : TypeName(T.JavaType(T.Ret(fn)));
            o.L(MethodDoc(fn, "dispatched to the implementations"));
            o.L($"{ret} {name}({string.Join(", ", ps)});");
        }
        o.Close();
        o = saved;
        var head = new StringBuilder();
        if (i.Package.Length > 0) head.Append("package ").Append(i.Package).Append(";\n\n");
        foreach (var imp in imports.Where(x => !x.StartsWith("java.lang.") && Pkg(x) != i.Package).Order(StringComparer.Ordinal)) head.Append("import ").Append(imp).Append(";\n");
        head.Append('\n');
        return head + body.Sb.ToString();
    }
}
