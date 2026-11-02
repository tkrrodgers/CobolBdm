namespace MlCpu.Modernize;

/// <summary>
/// The checked exceptions each lifted method can throw (Java's throws clause): J3S raises checked exceptions like any
/// other (FileNotFoundException, IOException, ClassNotFoundException), and the JDK members the emulation maps to declare
/// theirs (BufferedReader.readLine, URL.openStream, ...). A method throws what its statements throw and its callees throw,
/// less what its try statements catch; a catch clause that rethrows its exception rethrows only the checked exceptions
/// of the try body it catches (Java's precise rethrow). Computed to a fixed point over the estate's call graph.
/// </summary>
public static class CheckedExceptions
{
    /// <summary>The checked exception classes the lift meets and their superclasses (Exception is every one's).</summary>
    static readonly Dictionary<string, string> Super = new(StringComparer.Ordinal)
    {
        ["java.io.IOException"] = "java.lang.Exception",
        ["java.io.FileNotFoundException"] = "java.io.IOException",
        ["java.io.UnsupportedEncodingException"] = "java.io.IOException",
        ["java.lang.ReflectiveOperationException"] = "java.lang.Exception",
        ["java.lang.ClassNotFoundException"] = "java.lang.ReflectiveOperationException",
        ["java.lang.NoSuchMethodException"] = "java.lang.ReflectiveOperationException",
        ["java.lang.InstantiationException"] = "java.lang.ReflectiveOperationException",
        ["java.lang.IllegalAccessException"] = "java.lang.ReflectiveOperationException",
        ["java.lang.reflect.InvocationTargetException"] = "java.lang.ReflectiveOperationException",
    };

    /// <summary>JDK members (declaring type, name) and the checked exceptions they declare.</summary>
    static readonly Dictionary<(string Owner, string Name), string[]> Members = new()
    {
        [("java.io.BufferedReader", "readLine")] = ["java.io.IOException"],
        [("java.io.BufferedReader", "read")] = ["java.io.IOException"],
        [("java.io.BufferedReader", "close")] = ["java.io.IOException"],
        [("java.io.Reader", "read")] = ["java.io.IOException"],
        [("java.io.Reader", "close")] = ["java.io.IOException"],
        [("java.io.InputStreamReader", "read")] = ["java.io.IOException"],
        [("java.io.InputStreamReader", "close")] = ["java.io.IOException"],
        [("java.io.InputStream", "read")] = ["java.io.IOException"],
        [("java.io.InputStream", "close")] = ["java.io.IOException"],
        [("java.net.URL", "openStream")] = ["java.io.IOException"],
        [("java.lang.ClassLoader", "getResources")] = ["java.io.IOException"],
        [("java.lang.ClassLoader", "loadClass")] = ["java.lang.ClassNotFoundException"],
        [("java.lang.Class", "getDeclaredConstructor")] = ["java.lang.NoSuchMethodException"],
        [("java.lang.reflect.Constructor", "newInstance")] = ["java.lang.InstantiationException", "java.lang.IllegalAccessException", "java.lang.reflect.InvocationTargetException"],
    };

    /// <summary>JDK constructors (class, argument count) and the checked exceptions they declare.</summary>
    static readonly Dictionary<(string Class, int Args), string[]> Constructors = new()
    {
        [("java.io.InputStreamReader", 2)] = ["java.io.UnsupportedEncodingException"],
    };

    public static bool IsChecked(string cls) => cls == "java.lang.Exception" || Super.ContainsKey(cls);

    /// <summary>Whether <paramref name="sub"/> is <paramref name="sup"/> or a subclass of it (RuntimeException: none of the checked ones).</summary>
    public static bool IsA(string sub, string sup)
    {
        if (sup is "java.lang.Throwable") return true;
        for (string? x = sub; x is not null; x = Super.GetValueOrDefault(x)) if (x == sup) return true;
        return false;
    }

    /// <summary>The throws clause of every method: its checked exceptions, without one a superclass of which is listed.</summary>
    public static Dictionary<FunctionDef, List<string>> Compute(IReadOnlyDictionary<FunctionDef, Method> methods)
    {
        var sets = methods.Keys.ToDictionary(f => f, _ => new SortedSet<string>(StringComparer.Ordinal));
        var overriders = methods.Keys.Where(f => !f.IsStatic && !f.IsCtor).GroupBy(f => (f.MethodName, f.Params.Count)).ToDictionary(g => g.Key, g => g.ToList());
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var (fn, m) in methods)
            {
                var s = Thrown(m.Body, [], sets, overriders);
                foreach (var x in s) changed |= sets[fn].Add(x);
            }
        }
        return sets.ToDictionary(kv => kv.Key, kv => kv.Value.Where(x => !kv.Value.Any(y => y != x && IsA(x, y))).ToList());
    }

    static HashSet<string> Thrown(List<St> body, Dictionary<Var, HashSet<string>> rethrow, Dictionary<FunctionDef, SortedSet<string>> sets,
        Dictionary<(string, int), List<FunctionDef>> overriders)
    {
        var res = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in body)
        {
            switch (s)
            {
                case STry t:
                {
                    var inner = Thrown(t.Body, rethrow, sets, overriders);
                    var open = new HashSet<string>(inner);
                    foreach (var (type, v, cbody) in t.Catches)
                    {
                        var alts = type.Name.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Select(Qualify).ToList();
                        var caught = open.Where(x => alts.Any(a => IsA(x, a))).ToHashSet();
                        open.ExceptWith(caught);
                        var env = new Dictionary<Var, HashSet<string>>(rethrow) { [v] = caught };
                        res.UnionWith(Thrown(cbody, env, sets, overriders));
                    }
                    res.UnionWith(open);
                    continue;
                }
                case SThrow { E: EVar { V: var tv } } when rethrow.TryGetValue(tv, out var rs): res.UnionWith(rs); break;
                case SThrow { E: ENew { JdkClass: { } jc } } when IsChecked(jc): res.Add(jc); break;
            }
            foreach (var e in Fold.Exprs(s).SelectMany(Deep)) res.UnionWith(OfExpr(e, sets, overriders));
            switch (s)
            {
                case SIf i: res.UnionWith(Thrown(i.Then, rethrow, sets, overriders)); res.UnionWith(Thrown(i.Else, rethrow, sets, overriders)); break;
                case SLoop l: res.UnionWith(Thrown(l.Body, rethrow, sets, overriders)); break;
                case SBlock b: res.UnionWith(Thrown(b.Body, rethrow, sets, overriders)); break;
                case SSwitch sw:
                    foreach (var (_, cb) in sw.Cases) res.UnionWith(Thrown(cb, rethrow, sets, overriders));
                    if (sw.Default is not null) res.UnionWith(Thrown(sw.Default, rethrow, sets, overriders));
                    break;
            }
        }
        return res;
    }

    static string Qualify(string simple) => simple.Contains('.') ? simple : FunctionLifter.ExceptionClass(simple);

    static IEnumerable<string> OfExpr(Expr e, Dictionary<FunctionDef, SortedSet<string>> sets, Dictionary<(string, int), List<FunctionDef>> overriders)
    {
        switch (e)
        {
            case ECall { Fn: { } f } c when c.Kind is not CallKind.Jdk and not CallKind.JdkStatic:
            {
                var impls = c.Virtual && overriders.TryGetValue((f.MethodName, f.Params.Count), out var o) ? o : [f];
                foreach (var g in impls) if (sets.TryGetValue(g, out var gs)) foreach (var x in gs) yield return x;
                break;
            }
            case ECall { Owner: { } owner, Name: var name } when Members.TryGetValue((owner, name), out var ms):
                foreach (var x in ms) yield return x;
                break;
            case ENew { Ctor: { } ct }:
                if (sets.TryGetValue(ct, out var cs)) foreach (var x in cs) yield return x;
                break;
            case ENew { JdkClass: { } jc, Args: var args } when Constructors.TryGetValue((jc, args.Count), out var js):
                foreach (var x in js) yield return x;
                break;
        }
    }

    static IEnumerable<Expr> Deep(Expr e)
    {
        yield return e;
        if (e is ELambda) yield break;
        foreach (var c in e.Children) foreach (var d in Deep(c)) yield return d;
    }
}
