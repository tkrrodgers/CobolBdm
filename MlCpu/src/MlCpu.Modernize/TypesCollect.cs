namespace MlCpu.Modernize;

public sealed partial class TypeInferenceCollector
{
    readonly TypeInference T;
    readonly Dictionary<FunctionDef, Method> methods;
    FunctionDef? fn;

    public TypeInferenceCollector(TypeInference t, Dictionary<FunctionDef, Method> methods) { T = t; this.methods = methods; }

    public void Run()
    {
        // parameters of a function and the variables of its lifted body are one slot
        foreach (var (f, m) in methods)
        {
            T.Declare(f);
            for (int i = 0; i < m.L.Params.Count && i < f.Params.Count; i++) TypeInference.Slot.Unify(T.Param(f, i), T.Of(m.L.Params[i]));
            if (f.Ret is not null && m.L.ReturnType is { } rt) T.Ret(f).Decl ??= rt;
        }
        foreach (var (f, m) in methods)
        {
            fn = f;
            foreach (var s in Passes.All(m.Body)) Statement(s);
        }
        T.Solve();
        // a function that only throws has no value to learn its type from: take it from where its result goes
        bool more = false;
        foreach (var (f, target) in uses)
        {
            var r = T.Ret(f);
            if (r.C.Count == 0 && r.J.Count == 0 && r.Decl is null) { T.Flow(target, r); more = true; }
        }
        if (more) T.Solve();
        // calls of a functional interface: the arguments reach the parameters of the lambdas the receiver can hold
        for (int round = 0; round < 6; round++)
        {
            bool grew = false;
            foreach (var (recv, args, res) in sams)
                foreach (var cl in recv.Rep.C.Where(c => c.Kind == ClassKind.Closure && c.Lambda is not null).ToList())
                {
                    var lf = cl.Lambda!;
                    if (!samDone.Add((recv.Rep, lf))) continue;
                    for (int i = 0; i < args.Count && i < lf.Params.Count; i++) T.Flow(args[i], T.Param(lf, i));
                    if (lf.Ret is not null) T.Flow(T.Ret(lf), res);
                    grew = true;
                }
            // functional variables passed to JDK methods (ifPresent(consumer)): their lambdas' parameters and results
            foreach (var (fs, pi, from, to) in lambdaUses)
                foreach (var cl in fs.Rep.C.Where(c => c.Kind == ClassKind.Closure && c.Lambda is not null).ToList())
                {
                    var lf = cl.Lambda!;
                    if (!lambdaDone.Add((fs.Rep, lf, pi))) continue;
                    if (from is not null && pi >= 0 && pi < lf.Params.Count) T.Flow(from, T.Param(lf, pi));
                    if (to is not null && lf.Ret is not null) T.Flow(T.Ret(lf), to);
                    grew = true;
                }
            if (!grew) break;
            T.Solve();
        }
        // an overriding method has the overridden one's types; so have the bodies of one enum's constants (anonymous subclasses)
        var instanceMethods = methods.Keys.Where(f => !f.IsStatic && !f.IsCtor && !f.IsClinit && !f.IsLambda && f.Owner is { Kind: ClassKind.Domain }).ToList();
        foreach (var g in instanceMethods.GroupBy(f => (f.MethodName, f.Params.Count)))
        {
            var fs = g.ToList();
            for (int a = 0; a < fs.Count; a++)
                for (int b = 0; b < fs.Count; b++)
                {
                    var (fa, fb) = (fs[a], fs[b]);
                    if (a >= b || fa.Owner == fb.Owner) continue;
                    bool overrides = IsAncestor(fa.Owner!, fb.Owner!) || IsAncestor(fb.Owner!, fa.Owner!);
                    bool constants = fa.Owner!.SimpleName.StartsWith('$') && fb.Owner!.SimpleName.StartsWith('$') && fa.Owner.Super == fb.Owner.Super;
                    if (!overrides && !constants) continue;
                    if (fa.Ret is not null && fb.Ret is not null) TypeInference.Slot.Unify(T.Ret(fa), T.Ret(fb));
                    for (int i = 0; i < fa.Params.Count; i++) TypeInference.Slot.Unify(T.Param(fa, i), T.Param(fb, i));
                }
        }
        // lambdas that reach one functional slot have one functional type: their parameters and results are one slot. Not
        // through a slot declared with a type variable (Memo.of(Supplier<? extends T>)): each call has its own T
        var allSlots = T.Vars.Values.Concat(T.Params.Values).Concat(T.Fields.Values).Select(x => x.Rep).Distinct().ToList();
        var genericLambdas = allSlots.Where(x => x.Generic).SelectMany(x => x.C).Where(c => c.Kind == ClassKind.Closure).ToHashSet();
        foreach (var slot in allSlots)
        {
            var lambdas = slot.Rep.C.Where(c => c.Kind == ClassKind.Closure && c.Lambda is not null && !genericLambdas.Contains(c)).Select(c => c.Lambda!).Distinct().ToList();
            if (lambdas.Count < 2) continue;
            var first = lambdas[0];
            foreach (var other in lambdas.Skip(1))
            {
                for (int i = 0; i < Math.Min(first.Params.Count, other.Params.Count); i++) TypeInference.Slot.Unify(T.Param(first, i), T.Param(other, i));
                if (first.Ret is not null && other.Ret is not null) TypeInference.Slot.Unify(T.Ret(first), T.Ret(other));
            }
        }
        T.Solve();
        T.RequireBackward();
        T.BuildInterfaces();
        T.Invariance();
    }

    readonly List<(FunctionDef F, TypeInference.Slot Target)> uses = [];
    readonly List<(TypeInference.Slot Recv, List<TypeInference.Slot> Args, TypeInference.Slot Result)> sams = [];
    readonly HashSet<(TypeInference.Slot, FunctionDef)> samDone = [];
    readonly HashSet<(TypeInference.Slot, FunctionDef, int)> lambdaDone = [];

    void Statement(St s)
    {
        switch (s)
        {
            case SAssign a:
            {
                var rs = X(a.Rhs);
                var ts = Target(a.Lhs);
                // a copy between untyped variables, or an element read: one type (unify), so uses type the source too
                if (a.Lhs is EVar && rs.Rep.Decl is null && ts.Rep.Decl is null && a.Rhs is ECall { Name: "get" or "j3sAt" or "next" or "getFirst" or "getLast" or "getKey" or "getValue" or "orElse" or "previous" })
                    TypeInference.Slot.Unify(ts, rs);
                else T.Flow(rs, ts);
                // a reference copied between variables and fields is one object: its elements are shared (aliasing)
                if (a.Lhs is EVar or EField or EStatic && a.Rhs is EVar or EField or EStatic)
                {
                    TypeInference.Slot.Unify(ts.Elem, rs.Elem);
                    if (rs.HasKey || ts.HasKey) { TypeInference.Slot.Unify(ts.Key, rs.Key); TypeInference.Slot.Unify(ts.Val, rs.Val); }
                }
                if (a.Rhs is ECall { Fn: { } cf } && cf.Ret is not null) uses.Add((cf, Target(a.Lhs)));
                break;
            }
            case SExpr e: X(e.E); break;
            case SReturn { E: { } r }:
                if (fn!.Ret is not null) T.Flow(X(r), T.Ret(fn)); else X(r);
                if (r is ECall { Fn: { } rf } && rf.Ret is not null && fn.Ret is not null) uses.Add((rf, T.Ret(fn)));
                break;
            case SThrow t: X(t.E); break;
            case SIf i: X(i.C); break;
            case SLoop l:
                if (l.C is not null) X(l.C);
                if (l.Iterable is not null && l.Each is not null)
                {
                    var it = X(l.Iterable);
                    // iterated: a collection (a list when nothing else says so)
                    it.Rep.JReq.Add("java.util.List");
                    T.Flow(it.Elem, T.Of(l.Each));
                }
                break;
            case SSwitch sw: X(sw.Subject); break;
            case STry t: foreach (var (ty, v, _) in t.Catches) T.Of(v).Decl ??= ty; break;
        }
    }

    TypeInference.Slot Target(Expr lhs) => lhs switch
    {
        EVar v => T.Of(v.V),
        EField f => FieldTarget(f),
        EStatic s => T.Of(s.F),
        EIndex x => X(x.A).Elem,
        _ => Fresh(),
    };

    TypeInference.Slot FieldTarget(EField f)
    {
        var o = X(f.Obj);
        if (f.F.Owner is { Kind: ClassKind.Domain } ow) o.Rep.Req.Add(ow);
        return T.Of(f.F);
    }

    static TypeInference.Slot Fresh(string? jdk = null)
    {
        var s = new TypeInference.Slot { Name = "expr" };
        if (jdk is not null) s.J.Add(jdk);
        return s;
    }

    static TypeInference.Slot Of(JType t)
    {
        var s = new TypeInference.Slot { Name = "type" };
        if (t.Class is not null) s.C.Add(t.Class);
        else if (TypeInference.IsContainer(t.Name)) s.J.Add(t.Name);
        else s.Decl = t;
        return s;
    }

    TypeInference.Slot X(Expr e)
    {
        switch (e)
        {
            case EVar v: return T.Of(v.V);
            case EThis th:
            {
                var s = Fresh();
                if (fn?.Owner is { } o && o.Kind != ClassKind.Closure) s.C.Add(o);
                return s;
            }
            case EField f: return FieldTarget(f);
            case EStatic st:
            {
                var s = T.Of(st.F);
                return s;
            }
            case ELit l:
                return l.Value switch
                {
                    null => Fresh("null"),
                    string => Of(JType.String),
                    bool => Of(JType.Bool),
                    long v when v is >= int.MinValue and <= int.MaxValue => Of(JType.Int),
                    long => Of(JType.Long),
                    _ => Fresh(),
                };
            case EStaticJdk sj: return Of(sj.JType);
            case ECall c: return Call(c);
            case ENew n: return New(n);
            case ECond c:
            {
                X(c.C);
                var s = Fresh();
                T.Flow(X(c.A), s);
                T.Flow(X(c.B), s);
                return s;
            }
            case EBinary b:
            {
                var l = X(b.L);
                var r = X(b.R);
                if (b.Op is "==" or "!=" or "<" or "<=" or ">" or ">=" or "&&" or "||" or "==C" or "!=C") return Of(JType.Bool);
                if (b.Op == "+" && (IsString(l) || IsString(r))) return Of(JType.String);
                return Of(Numeric(l, r));
            }
            case EUnary u:
            {
                var s = X(u.E);
                return u.Op == "!" ? Of(JType.Bool) : s;
            }
            case ECast c: X(c.E); return Of(c.T);
            case EInstanceOf i: X(i.E); return Of(JType.Bool);
            case ELambda l: return Lambda(l);
            case EArrayNew a: { X(a.Len); var s = Fresh(); s.Decl = JType.Of(a.Elem.Name + "[]"); return s; }
            case EIndex x:
            {
                X(x.I);
                var arr = X(x.A);
                arr.Rep.JReq.Add("java.lang.Object[]");
                if (arr.Rep.Decl is { } d && d.Name == "int[]") return Of(JType.Int);
                // a declared array: its elements are of the declared element type (an Object[] parameter holds anything)
                if (arr.Rep.Decl is { IsArray: true } da) return Of(JType.Of(da.Name[..^2]));
                // a declared array type: its elements are of that type (Object[] reads are Object)
                if (arr.Rep.J.FirstOrDefault(j => j.EndsWith("[]")) is { } at) return Of(JType.Of(at[..^2]));
                return arr.Elem;
            }
            case EArrayLength al: X(al.A).Rep.JReq.Add("java.lang.Object[]"); return Of(JType.Int);
            case ERecordField rf: return Of(rf.Type ?? JType.String);
            case EPart p: X(p.Whole); return Fresh();
            default:
                foreach (var ch in e.Children) X(ch);
                return Fresh();
        }
    }

    static bool IsString(TypeInference.Slot s) => s.Rep.Decl?.Name == "java.lang.String" || s.Rep.J.Contains("java.lang.String");

    static JType Numeric(TypeInference.Slot a, TypeInference.Slot b)
    {
        string? A = a.Rep.Decl?.Name, B = b.Rep.Decl?.Name;
        if (A == "double" || B == "double") return JType.Double;
        if (A == "long" || B == "long") return JType.Long;
        return JType.Int;
    }

    TypeInference.Slot New(ENew n)
    {
        if (n.Class is not null)
        {
            if (n.Ctor is not null)
                for (int i = 0; i < n.Args.Count && i < n.Ctor.Params.Count; i++) T.Flow(X(n.Args[i]), T.Param(n.Ctor, i));
            else foreach (var a in n.Args) X(a);
            var s = Fresh();
            s.C.Add(n.Class);
            return s;
        }
        var args = n.Args.Select(X).ToList();
        var jdk = n.JdkClass ?? "java.lang.Object";
        var iface = jdk switch
        {
            "java.util.ArrayList" => "java.util.List", "java.util.HashMap" or "java.util.TreeMap" or "java.util.IdentityHashMap" => "java.util.Map",
            // a tree set is declared navigable: new TreeSet<>(it) then copies its order (the TreeSet(SortedSet) constructor)
            "java.util.HashSet" => "java.util.Set", "java.util.TreeSet" => "java.util.NavigableSet", _ => null,
        };
        if (iface is not null)
        {
            var s = Fresh(iface);
            // new ArrayList<>(collection): the same elements
            if (args.Count == 1 && n.Args[0].Type?.Name != "int" && !(n.Args[0] is ELit)) { if (iface == "java.util.Map") { TypeInference.Slot.Unify(s.Key, args[0].Key); TypeInference.Slot.Unify(s.Val, args[0].Val); } else TypeInference.Slot.Unify(s.Elem, args[0].Elem); }
            return s;
        }
        if (jdk == "java.util.concurrent.atomic.AtomicReference")
        {
            var s = Fresh(jdk);
            if (args.Count == 1) T.Flow(args[0], s.Elem);
            return s;
        }
        return Of(n.Type ?? JType.Of(jdk));
    }

    TypeInference.Slot Lambda(ELambda l)
    {
        var s = Fresh();
        s.C.Add(l.Closure);
        if (l.Closure.Lambda is { } lf && methods.TryGetValue(lf, out var lm))
            foreach (var (field, v) in lm.L.Captured)
                if (l.Captures.TryGetValue(field, out var cap))
                {
                    var cs = X(cap);
                    var vs = T.Of(v);
                    T.Flow(cs, vs);
                    // a captured container is the same object inside the lambda
                    TypeInference.Slot.Unify(cs.Elem, vs.Elem);
                    if (cs.HasKey || vs.HasKey) { TypeInference.Slot.Unify(cs.Key, vs.Key); TypeInference.Slot.Unify(cs.Val, vs.Val); }
                }
        foreach (var cap in l.Captures.Values) X(cap);
        return s;
    }

    /// <summary>Lambda parameter i receives values of the slot (forEach(x -> ...), removeIf, comparators).</summary>
    static bool IsAncestor(ClassDef sup, ClassDef c)
    {
        for (var x = c.Super; x is not null; x = x.Super) if (x == sup) return true;
        return false;
    }

    void LambdaParam(Expr arg, int i, TypeInference.Slot from)
    {
        if (arg is ELambda { Closure.Lambda: { } lf } && i < lf.Params.Count) T.Flow(from, T.Param(lf, i));
        // a functional variable: the lambdas it can hold, once solved
        else if (arg is not ELambda) lambdaUses.Add((X(arg), i, from, null));
    }

    void LambdaReturn(Expr arg, TypeInference.Slot to)
    {
        if (arg is ELambda { Closure.Lambda: { Ret: not null } lf }) T.Flow(T.Ret(lf), to);
        else if (arg is not ELambda) lambdaUses.Add((X(arg), -1, null, to));
    }

    readonly List<(TypeInference.Slot Fn, int Param, TypeInference.Slot? From, TypeInference.Slot? To)> lambdaUses = [];

    TypeInference.Slot Call(ECall c)
    {
        if (c.Kind is CallKind.Static or CallKind.Instance or CallKind.Super && c.Fn is { } f)
        {
            if (c.Recv is not null)
            {
                var r = X(c.Recv);
                // a virtual call, or a method every object has, says nothing about the receiver's class
                if (f.Owner is { Kind: ClassKind.Domain } o && !c.Virtual && f.MethodName is not ("equals" or "hashCode" or "toString" or "compareTo" or "canEqual")) r.Rep.Req.Add(o);
            }
            for (int i = 0; i < c.Args.Count; i++)
            {
                var a = X(c.Args[i]);
                if (i < f.Params.Count) T.Flow(a, T.Param(f, i));
            }
            return f.Ret is null ? Fresh() : T.Ret(f);
        }
        // JDK (and emulation mapped to JDK)
        var recv = c.Recv is null ? null : X(c.Recv);
        var args = c.Args.Select(X).ToList();
        var owner = c.Owner ?? "";
        if (recv is not null && owner.Contains('.') && c.Kind == CallKind.Jdk) recv.Rep.JReq.Add(owner);
        switch (c.Name)
        {
            case "add" when recv is not null && args.Count == 1: T.Flow(args[0], recv.Elem); return Of(JType.Bool);
            case "add" when recv is not null && args.Count == 2: T.Flow(args[1], recv.Elem); return Fresh();
            case "addFirst" or "addLast" or "push" or "offer" when recv is not null && args.Count == 1: T.Flow(args[0], recv.Elem); return Fresh();
            case "set" when recv is not null && args.Count == 2: T.Flow(args[1], recv.Elem); return recv.Elem;
            case "set" when recv is not null && args.Count == 1: T.Flow(args[0], recv.Elem); return Fresh();
            case "get" when recv is not null && args.Count == 1 && (owner == "java.util.Map" || recv.Rep.J.Contains("java.util.Map")): T.Flow(args[0], recv.Key); return recv.Val;
            case "get" or "getFirst" or "getLast" or "removeFirst" or "removeLast" or "next" or "orElseThrow" or "j3sAt" or "previous" or "first" or "last" or "peek" or "poll" when recv is not null:
                return recv.Elem;
            case "remove" when recv is not null && args.Count == 1 && c.Args[0].Type?.Name == "int": return recv.Elem;
            case "orElse" when recv is not null && args.Count == 1: T.Flow(args[0], recv.Elem); return recv.Elem;
            case "put" or "putIfAbsent" when recv is not null && args.Count == 2: T.Flow(args[0], recv.Key); T.Flow(args[1], recv.Val); return recv.Val;
            case "getOrDefault" when recv is not null && args.Count == 2: T.Flow(args[0], recv.Key); T.Flow(args[1], recv.Val); return recv.Val;
            case "computeIfAbsent" when recv is not null && args.Count == 2: T.Flow(args[0], recv.Key); LambdaParam(c.Args[1], 0, recv.Key); LambdaReturn(c.Args[1], recv.Val); return recv.Val;
            case "merge" when recv is not null && args.Count == 3: T.Flow(args[0], recv.Key); T.Flow(args[1], recv.Val); return recv.Val;
            case "containsKey" when recv is not null: T.Flow(args[0], recv.Key); return Of(JType.Bool);
            case "keySet" when recv is not null: { var s = Fresh("java.util.Set"); TypeInference.Slot.Unify(s.Elem, recv.Key); return s; }
            case "values" when recv is not null: { var s = Fresh("java.util.Collection"); TypeInference.Slot.Unify(s.Elem, recv.Val); return s; }
            case "entrySet" when recv is not null:
            {
                var s = Fresh("java.util.Set");
                var en = s.Elem;
                en.J.Add("java.util.Map.Entry");
                TypeInference.Slot.Unify(en.Key, recv.Key);
                TypeInference.Slot.Unify(en.Val, recv.Val);
                return s;
            }
            case "getKey" when recv is not null: return recv.Key;
            case "getValue" when recv is not null: return recv.Val;
            case "iterator" or "listIterator" or "descendingIterator" when recv is not null: { var s = Fresh("java.util.Iterator"); TypeInference.Slot.Unify(s.Elem, recv.Elem); return s; }
            case "subList" or "reversed" when recv is not null: { var s = Fresh("java.util.List"); TypeInference.Slot.Unify(s.Elem, recv.Elem); return s; }
            case "addAll" when recv is not null && args.Count == 1: TypeInference.Slot.Unify(recv.Elem, args[0].Elem); return Of(JType.Bool);
            case "forEach" when recv is not null && args.Count == 1:
                if (owner == "java.util.Map" || recv.Rep.J.Contains("java.util.Map")) { LambdaParam(c.Args[0], 0, recv.Key); LambdaParam(c.Args[0], 1, recv.Val); }
                else LambdaParam(c.Args[0], 0, recv.Elem);
                return Fresh();
            case "removeIf" or "anyMatch" or "allMatch" or "noneMatch" when recv is not null: LambdaParam(c.Args[0], 0, recv.Elem); return Of(JType.Bool);
            case "ifPresent" when recv is not null: LambdaParam(c.Args[0], 0, recv.Elem); return Fresh();
            case "map" when recv is not null && args.Count == 1:
            {
                LambdaParam(c.Args[0], 0, recv.Elem);
                var s = Fresh("java.util.Optional");
                LambdaReturn(c.Args[0], s.Elem);
                return s;
            }
            case "flatMap" when recv is not null && args.Count == 1:
            {
                LambdaParam(c.Args[0], 0, recv.Elem);
                var s = Fresh("java.util.Optional");
                if (c.Args[0] is ELambda { Closure.Lambda: { Ret: not null } fl }) { TypeInference.Slot.Unify(s.Elem, T.Ret(fl).Elem); T.Ret(fl).Rep.JReq.Add("java.util.Optional"); }
                return s;
            }
            case "filter" when recv is not null && args.Count == 1:
            {
                LambdaParam(c.Args[0], 0, recv.Elem);
                var s = Fresh("java.util.Optional");
                TypeInference.Slot.Unify(s.Elem, recv.Elem);
                return s;
            }
            case "sort" when recv is not null && args.Count == 1: LambdaParam(c.Args[0], 0, recv.Elem); LambdaParam(c.Args[0], 1, recv.Elem); return Fresh();
            case "replaceAll" when recv is not null: LambdaParam(c.Args[0], 0, recv.Elem); return Fresh();
            case "size" or "indexOf" or "lastIndexOf" or "hashCode" or "compareTo" or "length" or "ordinal" or "j3sFence" or "j3sModCount" or "signum" or "precision" or "scale" or "intValue" or "getYear" or "getMonthValue" or "getDayOfMonth" or "lengthOfMonth" or "lengthOfYear" or "getPrecision" or "identityHashCode" or "compare":
                return Of(JType.Int);
            case "isEmpty" or "contains" or "equals" or "isPresent" or "isEmptyOptional" or "hasNext" or "isAfter" or "isBefore" or "isEqual" or "isLeap" or "startsWith" or "test":
                return Of(JType.Bool);
            case "of" or "ofNullable" when owner == "java.util.Optional": { var s = Fresh("java.util.Optional"); if (args.Count == 1) T.Flow(args[0], s.Elem); return s; }
            case "empty" when owner == "java.util.Optional": return Fresh("java.util.Optional");
            case "of" or "copyOf" when owner is "java.util.List" or "java.util.Set":
            {
                var s = Fresh(owner);
                if (c.Name == "copyOf" && args.Count == 1) TypeInference.Slot.Unify(s.Elem, args[0].Elem);
                else foreach (var a in args) T.Flow(a, s.Elem);
                return s;
            }
            // X.valueOf(...): an X (String.valueOf, Integer.valueOf, an enum's valueOf)
            case "valueOf" when c.Kind == CallKind.JdkStatic && owner is "java.lang.String" or "java.lang.Integer" or "java.lang.Long" or "java.lang.Boolean" or "java.math.BigDecimal":
                return Of(JType.Of(owner));
            case "valueOf" when c.Kind == CallKind.JdkStatic && T.ClassNamed(owner) is { } vc:
            {
                var vs = Fresh();
                vs.C.Add(vc);
                return vs;
            }
            case "of" when owner == "java.util.Map":
            {
                var s = Fresh("java.util.Map");
                for (int i = 0; i + 1 < args.Count; i += 2) { T.Flow(args[i], s.Key); T.Flow(args[i + 1], s.Val); }
                return s;
            }
            case "unmodifiableList" or "unmodifiableSet" or "unmodifiableCollection": { var s = Fresh(c.Name == "unmodifiableList" ? "java.util.List" : c.Name == "unmodifiableSet" ? "java.util.Set" : "java.util.Collection"); if (args.Count == 1) TypeInference.Slot.Unify(s.Elem, args[0].Elem); return s; }
            case "emptyList": return Fresh("java.util.List");
            case "emptyMap": return Fresh("java.util.Map");
            case "emptySet": return Fresh("java.util.Set");
            case "reverseOrder" or "comparing" or "comparingInt" or "thenComparing" or "naturalOrder": return Fresh("java.util.Comparator");
            case "requireNonNull" when args.Count >= 1: return args[0];
            case "toString" or "trim" or "strip" or "stripTrailing" or "substring" or "toPlainString" or "name" or "concat": return Of(JType.String);
            case "accept" or "run" or "apply" or "test" or "compare" when recv is not null:
            {
                var res = Fresh();
                sams.Add((recv, args, res));
                return c.Name is "test" ? Of(JType.Bool) : c.Name is "compare" ? Of(JType.Int) : res;
            }
        }
        return c.Type is not null ? Of(c.Type) : Fresh();
    }
}
