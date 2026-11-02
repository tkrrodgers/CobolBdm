using System.Text.Json;
using System.Text.Json.Serialization;

namespace MlCpu.Modernize;

/// <summary>
/// Phase M rule R5 (program-interfaces, opt-in): a declaration (field, static, parameter, local, method result) printed
/// with a class whose every use is a member of one interface the class implements is declared with that interface; the
/// construction keeps the class (<c>new C(..)</c>, <c>new ArrayList&lt;&gt;()</c>). Two families: an estate interface of
/// the class (the interfaces the lift recovered, <see cref="Shapes.Interfaces"/>), and the JDK collection contracts
/// (ArrayList: List, HashMap/LinkedHashMap: Map, TreeMap: SortedMap, HashSet/LinkedHashSet: Set, TreeSet: SortedSet, ...).
/// The decision is generic and sees opaque units and uses only: the unit's options (opaque interface ids or JDK interface
/// names), its keep obligations, and per use its kind, the options whose declared members bind the call exactly as the
/// class did, the unit or fixed declaration a value flows into and the options that declaration accepts. It never sees a
/// Java or COBOL name (display fields only go to the ledger). Fail closed:
/// <list type="bullet">
/// <item>a keep obligation (external or unknown callers, override/interface/abstract/JDK-contract obligations, overloads of
/// the declaring method, type variables, accessors, reflection, model edits): kept;</item>
/// <item>an escape (a JDK or unknown call argument, a cast, instanceof, a reference comparison with a non-null operand,
/// a conditional, a lambda capture or result, an array store, a field access through the value, iteration): kept;</item>
/// <item>an outbound argument to an overloaded or generic target, or a member the interface declares differently (another
/// overload, parameter, result or exception list) or not at all, or an implementation-specific JDK member
/// (ensureCapacity, trimToSize, clone, descending and navigation views): kept;</item>
/// <item>several interfaces covering every use: kept (ambiguous); none: kept;</item>
/// <item>a flow into a declaration that does not accept the interface (one that stays the class): kept, to a fixpoint.</item>
/// </list>
/// </summary>
public static class ProgramInterfaces
{
    public const string RuleName = "program-interfaces";

    /// <summary>The declarations R5 widened: the writer prints these types where the declaration is printed, nowhere else.</summary>
    public sealed class Widened
    {
        public Dictionary<FieldDef, JType> Fields { get; } = [];
        public Dictionary<(FunctionDef, int), JType> Params { get; } = [];
        public Dictionary<FunctionDef, JType> Returns { get; } = [];
        public Dictionary<Var, JType> Locals { get; } = [];
        public int Count => Fields.Count + Params.Count + Returns.Count + Locals.Count;
    }

    public enum UseKind { Receiver, Flow, Argument, NullCompare, StringConversion, Discard, Escape }

    public sealed class Use
    {
        public required string Site { get; init; }
        public required UseKind Kind { get; init; }
        /// <summary>Receiver and argument: the member (an opaque function id or a JDK member name).</summary>
        public string? Member { get; init; }
        /// <summary>Receiver: the options whose declared members bind this call exactly as the class did.</summary>
        public List<string> Covers { get; init; } = [];
        /// <summary>Flow and argument: the unit the value flows into (null: a declaration R5 does not decide).</summary>
        public string? Target { get; init; }
        /// <summary>Flow and argument: the options the target's declared type accepts as it is printed now.</summary>
        public List<string> Accepts { get; init; } = [];
        /// <summary>Escape: why; argument: an obstacle at the target (an overload, a type variable); receiver: what is missing.</summary>
        public string? Why { get; init; }
    }

    public sealed class Unit
    {
        /// <summary>Opaque, stable identity (ordered by COBOL-side keys).</summary>
        public required string Id { get; init; }
        /// <summary>field, static, parameter, return or local.</summary>
        public required string Kind { get; init; }
        /// <summary>The declared type now: an opaque class id (T012) or a JDK class name (semantic: the JDK contract).</summary>
        public required string From { get; init; }
        /// <summary>Candidate interfaces: opaque estate interface ids (I01) or JDK interface names.</summary>
        public List<string> Options { get; init; } = [];
        public List<string> Keep { get; init; } = [];
        public List<Use> Uses { get; init; } = [];
        /// <summary>For the ledger only.</summary>
        public string Display { get; init; } = "";
        public string FromDisplay { get; init; } = "";
        public Dictionary<string, string> OptionDisplay { get; init; } = [];
        public List<string> SourceIds { get; init; } = [];
    }

    public sealed record Decision(Unit U, string Outcome, string Reason, string? To)
    {
        public bool Applied => Outcome == "applied";
    }

    public static List<Decision> Decide(IEnumerable<Unit> units)
    {
        var all = units.OrderBy(u => u.Id, StringComparer.Ordinal).ToList();
        var byId = all.ToDictionary(u => u.Id, StringComparer.Ordinal);
        var kept = new Dictionary<string, string>(StringComparer.Ordinal);
        var chosen = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var u in all)
        {
            if (u.Options.Count == 0) { kept[u.Id] = "no interface: the class implements none"; continue; }
            if (u.Keep.Count > 0) { kept[u.Id] = "keep obligation: " + string.Join("; ", u.Keep.Distinct().Order(StringComparer.Ordinal)); continue; }
            var esc = u.Uses.Where(x => x.Kind == UseKind.Escape).Select(x => x.Why ?? "unknown use").Distinct().Order(StringComparer.Ordinal).ToList();
            if (esc.Count > 0) { kept[u.Id] = "escape: " + Few(esc); continue; }
            var obstacle = u.Uses.Where(x => x.Kind is UseKind.Argument or UseKind.Flow && x.Why is not null).Select(x => x.Why!).Distinct().Order(StringComparer.Ordinal).ToList();
            if (obstacle.Count > 0) { kept[u.Id] = "outbound binding: " + Few(obstacle); continue; }
            var opts = u.Options.Where(o => u.Uses.Where(x => x.Kind == UseKind.Receiver).All(r => r.Covers.Contains(o))).ToList();
            var missing = u.Uses.Where(x => x.Kind == UseKind.Receiver && x.Why is not null && !u.Options.All(x.Covers.Contains)).Select(x => x.Why!).Distinct().Order(StringComparer.Ordinal).ToList();
            if (opts.Count == 0) { kept[u.Id] = "member: " + (missing.Count > 0 ? Few(missing) : "a use the interfaces do not declare or bind differently"); continue; }
            if (opts.Count > 1) { kept[u.Id] = "ambiguous: " + opts.Count + " interfaces cover every use (" + string.Join(", ", opts.Order(StringComparer.Ordinal)) + ")"; continue; }
            chosen[u.Id] = opts[0];
        }
        // a value of a widened declaration flows only into declarations that accept the interface (widened alike, or declared
        // with the interface or Object): to a fixpoint, a unit whose target stays the class is kept
        for (bool changed = true; changed;)
        {
            changed = false;
            foreach (var u in all)
            {
                if (!chosen.TryGetValue(u.Id, out var o)) continue;
                var fails = u.Uses.Where(x => x.Kind is UseKind.Flow or UseKind.Argument)
                    .Where(x => !(x.Target is { } t && chosen.TryGetValue(t, out var to) && to == o || x.Accepts.Contains(o)))
                    .Select(x => x.Target is { } tt && byId.ContainsKey(tt) ? "into " + tt + ", which stays the class" : "into a declaration that does not accept the interface")
                    .Distinct().Order(StringComparer.Ordinal).ToList();
                if (fails.Count == 0) continue;
                chosen.Remove(u.Id);
                kept[u.Id] = "flow: " + Few(fails);
                changed = true;
            }
        }
        return all.Select(u => chosen.TryGetValue(u.Id, out var o)
            ? new Decision(u, "applied", "every use is a member of " + o + "; outbound flows accept it", o)
            : new Decision(u, u.Options.Count == 0 ? "skipped" : "kept", kept[u.Id], null)).ToList();
    }

    static string Few(List<string> l) => string.Join("; ", l.Take(4)) + (l.Count > 4 ? $"; +{l.Count - 4} more" : "");

    /// <summary>The kind of a reason (the text before its first colon), for the summary.</summary>
    public static string ReasonClass(string reason) => reason.IndexOf(':') is var k && k > 0 ? reason[..k] : reason.Split(' ')[0];

    // ------------------------------------------------------------------ Java's binding rules, as data

    /// <summary>
    /// A call through the interface binds the member the class bound: the class (with its superclasses) declares exactly one
    /// signature under the call's name and arity, the interface exactly one, and they are equal (parameters, result,
    /// exceptions). Anything else may rebind silently (Overload.java case 3: m(String) on the class, m(Object) on the interface).
    /// </summary>
    public static bool SameBinding(IEnumerable<string> classSignatures, IEnumerable<string> interfaceSignatures)
    {
        var c = classSignatures.Distinct(StringComparer.Ordinal).ToList();
        var i = interfaceSignatures.Distinct(StringComparer.Ordinal).ToList();
        return c.Count == 1 && i.Count == 1 && c[0] == i[0];
    }

    /// <summary>An outbound argument whose target has several signatures under one name and arity (f(ArrayList) and f(List), g(C) and g(I)) may rebind silently.</summary>
    public static bool Overloaded(IEnumerable<string> targetSignatures) => targetSignatures.Distinct(StringComparer.Ordinal).Count() > 1;

    // ------------------------------------------------------------------ the JDK collection contracts (JDK 21)

    /// <summary>A concrete JDK collection → the interfaces it may be declared with (narrowest proper first).</summary>
    public static readonly Dictionary<string, string[]> JdkOptions = new(StringComparer.Ordinal)
    {
        ["java.util.ArrayList"] = ["java.util.List"], ["java.util.LinkedList"] = ["java.util.List", "java.util.Deque"],
        ["java.util.HashMap"] = ["java.util.Map"], ["java.util.LinkedHashMap"] = ["java.util.Map"], ["java.util.TreeMap"] = ["java.util.SortedMap"],
        ["java.util.HashSet"] = ["java.util.Set"], ["java.util.LinkedHashSet"] = ["java.util.Set"], ["java.util.TreeSet"] = ["java.util.SortedSet"],
        ["java.util.ArrayDeque"] = ["java.util.Deque"], ["java.util.PriorityQueue"] = ["java.util.Queue"],
        ["java.util.concurrent.ConcurrentHashMap"] = ["java.util.concurrent.ConcurrentMap"],
    };

    /// <summary>Concrete JDK collections whose contract differs from their interface's (identity, weak or enum keys, a navigable concurrent map): never widened.</summary>
    public static readonly Dictionary<string, string> JdkKeep = new(StringComparer.Ordinal)
    {
        ["java.util.IdentityHashMap"] = "identity keys: Map's equals-based contract does not describe it",
        ["java.util.WeakHashMap"] = "weak keys: entries vanish, Map's contract does not describe it",
        ["java.util.EnumMap"] = "enum keys: kept",
        ["java.util.concurrent.ConcurrentSkipListMap"] = "a concurrent navigable map: kept",
        ["java.util.concurrent.ConcurrentSkipListSet"] = "a concurrent navigable set: kept",
        ["java.util.concurrent.CopyOnWriteArrayList"] = "copy-on-write iteration semantics: kept",
    };

    static readonly string[] CollectionMembers = ["add", "addAll", "clear", "contains", "containsAll", "equals", "hashCode", "isEmpty", "iterator", "parallelStream",
        "remove", "removeAll", "removeIf", "retainAll", "size", "spliterator", "stream", "toArray", "forEach", "toString"];
    static readonly string[] SequencedMembers = ["addFirst", "addLast", "getFirst", "getLast", "removeFirst", "removeLast", "reversed"];
    static readonly string[] MapMembers = ["clear", "compute", "computeIfAbsent", "computeIfPresent", "containsKey", "containsValue", "entrySet", "equals", "forEach", "get",
        "getOrDefault", "hashCode", "isEmpty", "keySet", "merge", "put", "putAll", "putIfAbsent", "remove", "replace", "replaceAll", "size", "values", "toString"];

    /// <summary>The members each JDK interface declares (name, or name/arity where the navigable overload differs).</summary>
    public static readonly Dictionary<string, HashSet<string>> JdkMembers = new(StringComparer.Ordinal)
    {
        ["java.util.List"] = [.. CollectionMembers, .. SequencedMembers, "get", "set", "indexOf", "lastIndexOf", "listIterator", "subList", "replaceAll", "sort"],
        ["java.util.Set"] = [.. CollectionMembers],
        ["java.util.SortedSet"] = [.. CollectionMembers, .. SequencedMembers, "comparator", "first", "last", "headSet/1", "tailSet/1", "subSet/2"],
        ["java.util.Queue"] = [.. CollectionMembers, "offer", "poll", "peek", "element"],
        ["java.util.Deque"] = [.. CollectionMembers, .. SequencedMembers, "offer", "poll", "peek", "element", "offerFirst", "offerLast", "pollFirst", "pollLast",
            "peekFirst", "peekLast", "removeFirstOccurrence", "removeLastOccurrence", "push", "pop"],
        ["java.util.Map"] = [.. MapMembers],
        ["java.util.SortedMap"] = [.. MapMembers, "comparator", "firstKey", "lastKey", "headMap/1", "tailMap/1", "subMap/2", "firstEntry", "lastEntry", "pollFirstEntry",
            "pollLastEntry", "putFirst", "putLast", "reversed", "sequencedKeySet", "sequencedValues", "sequencedEntrySet"],
        ["java.util.concurrent.ConcurrentMap"] = [.. MapMembers],
    };

    /// <summary>Implementation-specific members (the concrete type is kept, and the ledger says why).</summary>
    public static readonly HashSet<string> ImplementationOnly = new(StringComparer.Ordinal)
    {
        "ensureCapacity", "trimToSize", "clone", "removeRange", "descendingMap", "descendingKeySet", "descendingSet", "descendingIterator", "navigableKeySet",
        "lowerKey", "lowerEntry", "floorKey", "floorEntry", "ceilingKey", "ceilingEntry", "higherKey", "higherEntry", "lower", "floor", "ceiling", "higher",
    };

    /// <summary>JDK interfaces a declaration of the option also satisfies (a flow into one of these accepts the option).</summary>
    public static readonly Dictionary<string, string[]> JdkSupers = new(StringComparer.Ordinal)
    {
        ["java.util.List"] = ["java.util.Collection", "java.lang.Iterable", "java.util.SequencedCollection"],
        ["java.util.Set"] = ["java.util.Collection", "java.lang.Iterable"],
        ["java.util.SortedSet"] = ["java.util.Set", "java.util.Collection", "java.lang.Iterable", "java.util.SequencedCollection", "java.util.SequencedSet"],
        ["java.util.Queue"] = ["java.util.Collection", "java.lang.Iterable"],
        ["java.util.Deque"] = ["java.util.Queue", "java.util.Collection", "java.lang.Iterable", "java.util.SequencedCollection"],
        ["java.util.Map"] = [],
        ["java.util.SortedMap"] = ["java.util.Map", "java.util.SequencedMap"],
        ["java.util.concurrent.ConcurrentMap"] = ["java.util.Map"],
    };

    /// <summary>The JDK interface declares a member (by name, or name/arity for the navigable overloads).</summary>
    public static bool JdkDeclares(string iface, string member, int arity) =>
        JdkMembers.TryGetValue(iface, out var ms) && !ImplementationOnly.Contains(member)
        && (ms.Contains(member + "/" + arity) || ms.Contains(member) && !ms.Any(x => x.StartsWith(member + "/", StringComparison.Ordinal)));

    // ------------------------------------------------------------------ ledger

    /// <summary>The per-rule ledger (modernize/program-interfaces.json): every candidate's options, outcome, reason, uses and proof.</summary>
    public static string Ledger(List<Decision> decisions, object inputs, IEnumerable<object> skipped, IEnumerable<string> notes)
    {
        var doc = new
        {
            rule = "R5 " + RuleName,
            version = 1,
            evidence = new[]
            {
                "the declarations the lift prints (fields, statics, parameters, locals, results) and their types as the existing type inference solved them",
                "every use of each declaration in the lifted IR: receiver calls (by function symbol), flows (assignments, returns, arguments by parameter symbol), comparisons, string conversion, and everything else as an escape",
                "the interfaces the lift recovered and the classes implementing them (Shapes.Interfaces/Implements: symbols and their member functions), superclass links, the JDK collection contracts (JDK 21 interface members)",
                "binding facts Java decides by: printed member name and arity (as equivalence classes), the printed parameter, result and checked-exception types; keep facts: R1's COBOL CALL census (external entry, unknown and runtime callers), overrides, interface and abstract methods, JDK contracts, accessors, reflection, model edits",
            },
            forbidden = "never read: original Java, J3S CLASS and FRAME comment text, FRAME signatures and parameter type strings, legacy PEC citations, Java or COBOL names (opaque ids and name equivalence classes; names only in the ledger's display fields)",
            disclosure = "pre-existing and unchanged: in J3S estates the model's class objects, packages and method names come from the J3S CLASS/FRAME program headers (Model.cs), the shapes recognise enum constant bodies by J3S '$' naming, and the existing type inference types parameters from FRAME parameter type names and synthesizes the interfaces 'the COBOL never shows' from them (Types.cs DeclaredName/BuildInterfaces): the estate interfaces R5 can declare exist because of that pre-existing step; R5 consumes the resulting symbols only",
            inputs,
            summary = new
            {
                units = decisions.Count,
                applied = decisions.Count(d => d.Applied),
                kept = decisions.Count(d => d.Outcome == "kept"),
                skipped = decisions.Count(d => d.Outcome == "skipped"),
                byKind = decisions.GroupBy(d => d.U.Kind + " " + d.Outcome).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
                byOutcome = decisions.GroupBy(d => d.Outcome + ": " + ReasonClass(d.Reason)).OrderBy(g => g.Key, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.Count()),
            },
            notes,
            units = decisions.Select(d => new
            {
                id = d.U.Id,
                kind = d.U.Kind,
                from = d.U.From,
                options = d.U.Options,
                outcome = d.Outcome,
                reason = d.Reason,
                to = d.To,
                keep = d.U.Keep.Count > 0 ? d.U.Keep.Distinct().Order(StringComparer.Ordinal).ToList() : null,
                uses = d.U.Uses.Select(x => new
                {
                    site = x.Site,
                    kind = x.Kind.ToString().ToLowerInvariant(),
                    member = x.Member,
                    covers = x.Kind == UseKind.Receiver ? x.Covers : null,
                    target = x.Target,
                    accepts = x.Kind is UseKind.Flow or UseKind.Argument ? x.Accepts : null,
                    why = x.Why,
                }).ToList(),
                declaration = d.U.Display,
                fromType = d.U.FromDisplay,
                toType = d.To is { } t ? d.U.OptionDisplay.GetValueOrDefault(t, t) : null,
                sourceIds = d.U.SourceIds,
            }).ToList(),
            skipped,
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions
        {
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }
}
