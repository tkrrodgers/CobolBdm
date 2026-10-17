namespace MlCpu.Modernize;

/// <summary>
/// The J3S JRE emulation classes (fncobolgen.jre, compiled into the estate) and the JDK types they stand for. Calls into
/// them become JDK calls; the emulation programs are not emitted.
/// </summary>
public static class JdkMap
{
    /// <summary>Emulation class (simple name, nested with '.') → the JDK type a variable of it is declared as.</summary>
    public static readonly Dictionary<string, string> Types = new(StringComparer.Ordinal)
    {
        ["JArrayList"] = "java.util.List", ["JAbstractList"] = "java.util.List", ["JImmutableList"] = "java.util.List", ["JSubList"] = "java.util.List",
        ["JReversedList"] = "java.util.List", ["JUnmodifiableList"] = "java.util.List", ["JReversedList.Descending"] = "java.util.Iterator",
        ["JHashMap"] = "java.util.Map", ["JTreeMap"] = "java.util.Map", ["JImmutableMap"] = "java.util.Map", ["JIdentityHashMap"] = "java.util.Map", ["JEmptyMap"] = "java.util.Map",
        ["JHashSet"] = "java.util.Set", ["JTreeSet"] = "java.util.NavigableSet", ["JAbstractSet"] = "java.util.Set", ["JEmptySet"] = "java.util.Set",
        ["JIdentityEntrySet"] = "java.util.Set", ["JHashEntrySet"] = "java.util.Set", ["JHashKeySet"] = "java.util.Set", ["JSnapshotSet"] = "java.util.Set",
        ["JOptional"] = "java.util.Optional", ["JString"] = "java.lang.String", ["JStringBuilder"] = "java.lang.StringBuilder",
        ["JAtomicReference"] = "java.util.concurrent.atomic.AtomicReference", ["JMapEntry"] = "java.util.Map.Entry",
        ["JArrayListItr"] = "java.util.Iterator", ["JListItr"] = "java.util.Iterator", ["JAbstractSet.Itr"] = "java.util.Iterator", ["JSubList.JSubListItr"] = "java.util.Iterator",
        ["JHashItr"] = "java.util.Iterator",
        ["JCollections.ReverseComparator"] = "java.util.Comparator", ["JComparators.KeyComparator"] = "java.util.Comparator",
        ["JClass"] = "java.lang.Class",
    };

    /// <summary>Concrete JDK class for `new` of an emulation class.</summary>
    public static readonly Dictionary<string, string> Concrete = new(StringComparer.Ordinal)
    {
        ["JArrayList"] = "java.util.ArrayList", ["JHashMap"] = "java.util.HashMap", ["JTreeMap"] = "java.util.TreeMap", ["JHashSet"] = "java.util.HashSet",
        ["JTreeSet"] = "java.util.TreeSet", ["JIdentityHashMap"] = "java.util.IdentityHashMap", ["JStringBuilder"] = "java.lang.StringBuilder",
        ["JAtomicReference"] = "java.util.concurrent.atomic.AtomicReference", ["JString"] = "java.lang.String",
    };

    /// <summary>Static emulation helpers → the JDK class that has the static method.</summary>
    public static readonly Dictionary<string, string> Statics = new(StringComparer.Ordinal)
    {
        ["JCollections"] = "java.util.Collections", ["JComparators"] = "java.util.Comparator", ["JMapStatics"] = "java.util.Map", ["JObjects"] = "java.util.Objects",
        ["JOptional"] = "java.util.Optional", ["JImmutableList"] = "java.util.List", ["JIntegerStatics"] = "java.lang.Integer", ["JString"] = "java.lang.String",
    };

    public static string EmulationName(ClassDef c)
    {
        var full = c.FullName;
        const string pkg = "fncobolgen.jre.";
        return full.StartsWith(pkg, StringComparison.Ordinal) ? full[pkg.Length..] : c.SimpleName;
    }

    public static string? JdkTypeOf(ClassDef c) =>
        c.Kind == ClassKind.Emulation && Types.TryGetValue(EmulationName(c), out var t) ? t : null;

    /// <summary>Methods J3S adds to the emulation for stream fusion; recognised as loop idioms, never emitted.</summary>
    public static bool Internal(string method) => method.StartsWith("j3s", StringComparison.Ordinal);

    /// <summary>JDK method renames (none so far: the emulation follows the JDK API).</summary>
    public static string MethodName(ClassDef owner, string method) => method;
}
