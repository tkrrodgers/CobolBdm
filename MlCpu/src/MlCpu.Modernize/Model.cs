using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;

namespace MlCpu.Modernize;

public enum ClassKind { Domain, Emulation, Closure, JdkException, Job }

/// <summary>A Java class recovered from the estate.</summary>
public sealed class ClassDef
{
    public required string FullName { get; set; }
    public string SimpleName => FullName[(FullName.LastIndexOf('.') + 1)..];
    public string Package { get; set; } = "";
    /// <summary>For a nested class, the enclosing class.</summary>
    public ClassDef? Outer { get; set; }
    public ClassDef? Super { get; set; }
    public List<ClassDef> Subs { get; } = [];
    public List<FieldDef> Fields { get; } = [];
    public List<FieldDef> Statics { get; } = [];
    public List<FunctionDef> Functions { get; } = [];
    public SortedSet<int> Ids { get; } = [];
    public PoolDef? Pool { get; set; }
    public string? Abbr { get; set; }
    public ClassKind Kind { get; set; }
    /// <summary>Programs whose header names this class.</summary>
    public List<string> Programs { get; } = [];
    /// <summary>For a closure: the lambda function and the class the lambda is written in.</summary>
    public FunctionDef? Lambda { get; set; }
    public bool IsEnum { get; set; }
    public bool IsRecord { get; set; }
    public bool IsInterface { get; set; }
    public bool IsAbstract { get; set; }
    /// <summary>Enum constants in ordinal order (static fields of the own type built in the initializer).</summary>
    public List<FieldDef> EnumConstants { get; } = [];
    public override string ToString() => FullName;
}

/// <summary>A heap pool: one per class hierarchy (FSHEAP).</summary>
public sealed class PoolDef
{
    public required string Prefix { get; init; }
    public required DataItem Entry { get; init; }
    public List<FieldDef> Fields { get; } = [];
    public Dictionary<string, FieldDef> FieldByItem { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortedSet<int> Ids { get; } = [];
    public bool IsArray => Prefix.StartsWith("HAR", StringComparison.Ordinal);
    public bool IsThrowable => Prefix == "HJTHR";
    public override string ToString() => Prefix;
}

/// <summary>An instance field (a heap pool field) or a static field (an FS-CTX group item).</summary>
public sealed class FieldDef
{
    public required string CobolName { get; init; }
    public required string Name { get; set; }
    public required VKind Kind { get; init; }
    public required DataItem Item { get; init; }
    public ClassDef? Owner { get; set; }
    public PoolDef? Pool { get; init; }
    public bool IsStatic { get; init; }
    public JType? Type { get; set; }
    /// <summary>Statics: the class abbreviation group (SAM for AmortizationMethod).</summary>
    public string? Group { get; init; }
    public int Slot { get; set; } = -1;
    public override string ToString() => (Owner?.SimpleName ?? Pool?.Prefix ?? Group ?? "?") + "." + Name;
}

public enum SlotRole { This, Param, Local, Temp, Ret, Index }

/// <summary>A frame slot of a function: receiver, parameter, local, temporary or result.</summary>
public sealed class FrameSlot
{
    public required string CobolName { get; init; }
    public required SlotRole Role { get; init; }
    public int No { get; init; }
    public string? Hint { get; init; }
    public required VKind Kind { get; init; }
    public required DataItem Item { get; init; }
    public override string ToString() => CobolName;
}

/// <summary>A function: a J3S method, constructor, lambda, initializer or job function (a PERFORM ... THRU range).</summary>
public sealed class FunctionDef
{
    public required string Id { get; init; }
    public required SourceProgram Program { get; init; }
    public required string Paragraph { get; init; }
    public string ExitParagraph { get; set; } = "";
    public string Signature { get; set; } = "";
    public string ClassSimple { get; set; } = "";
    public string MethodName { get; set; } = "";
    public List<string> ParamTypes { get; } = [];
    public ClassDef? Owner { get; set; }
    public bool IsCtor { get; set; }
    public bool IsClinit { get; set; }
    public bool IsLambda { get; set; }
    public bool IsJob { get; set; }
    public bool IsStatic => This is null;
    public FrameSlot? This { get; set; }
    public FrameSlot? Ret { get; set; }
    public List<FrameSlot> Params { get; } = [];
    public Dictionary<string, FrameSlot> Slots { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? SrcAbbr { get; set; }
    public int SrcFrom { get; set; }
    public int SrcTo { get; set; }
    /// <summary>Recursive component name (S8) when the function's frame is stacked.</summary>
    public string? Component { get; set; }
    public int ComponentSel { get; set; }
    public int FirstParagraph { get; set; } = -1;
    public int LastParagraph { get; set; } = -1;
    public JType? ReturnType { get; set; }
    public override string ToString() => $"{Id} {Signature}";
}

public sealed record EntryDef(SourceProgram Program, int No, FunctionDef Fn);

/// <summary>The estate seen through the J3S idiom pack: classes, pools, statics, functions, entries, class ids.</summary>
public sealed partial class Model
{
    public required Estate Estate { get; init; }
    public Dictionary<string, ClassDef> Classes { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, PoolDef> Pools { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FieldDef> StaticsByItem { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, FunctionDef> Functions { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, Dictionary<int, EntryDef>> Entries { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<int, ClassDef> ClassById { get; } = [];
    public Dictionary<string, ClassDef> ClassByAbbr { get; } = new(StringComparer.Ordinal);
    public List<string> Problems { get; } = [];
    /// <summary>String literal statics (SLIT-n) and their text, once the literal function is lifted.</summary>
    public Dictionary<string, string> Literals { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Exception class ids → simple names (job program's JO-SIMPLE-NAME table).</summary>
    public Dictionary<int, string> ExceptionNames { get; } = [];
    /// <summary>Paragraphs of job programs lifted as methods of the program class (PROGRAM/PARAGRAPH → method), e.g. JO-EMIT.</summary>
    public Dictionary<string, string> ClassicMethods { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The package of the application runtime (Abend, Records), set by the application model.</summary>
    public string PlatformPackage { get; set; } = "modern.batch.platform";
    /// <summary>The legacy sites whose decimal operations the lift checks against FSJBDEC's capacity (DecimalLimit).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> DecimalLimitSites { get; } = new(StringComparer.Ordinal);
    /// <summary>Where a capacity-checked decimal value leaves its function unchecked (an argument, a stored or returned value).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> DecimalLimitEscapes { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The interprocedural taint of values FSJBDEC may hold in its store (Modernizer runs the lift to a fixed point): functions
    /// whose result may be such a value (fn id), parameters that receive one (fn id#index), fields that hold one
    /// (owner.field), variables of a function that hold one at any point (fn id#name: loops and joins, flow-insensitive),
    /// and the functions whose every capacity operation is checked because they handle one (fn id).
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> LimitedReturns { get; } = new(StringComparer.Ordinal);
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> LimitedParams { get; } = new(StringComparer.Ordinal);
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> LimitedFields { get; } = new(StringComparer.Ordinal);
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> LimitedVars { get; } = new(StringComparer.Ordinal);
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> CheckedFunctions { get; } = new(StringComparer.Ordinal);
    /// <summary>The size of the taint sets: the fixed point is reached when a lift pass leaves it unchanged.</summary>
    public int TaintSize => LimitedReturns.Count + LimitedParams.Count + LimitedFields.Count + LimitedVars.Count + CheckedFunctions.Count;
    /// <summary>The legacy sites of the J3S JRE pow(int, MathContext) emulation (JBigDecimalOps, its FSJBDEC operations by op code).</summary>
    public Dictionary<string, string> JrePowSites { get; } = new(StringComparer.Ordinal);
    /// <summary>FSJBDEC operations that may store a value the lift did not check (no taint, a function that handles none): the frontier.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, byte> UncheckedDecimalOps { get; } = new(StringComparer.Ordinal);
    /// <summary>
    /// The estate's FSJBDEC has no store of values beyond 31 digits (no 7900-BIG-RESULT): every value is an unscaled integer of
    /// at most 31 digits (S9(31) COMP-3), and an operation whose result or working step does not fit returns RC 20. Then every
    /// such operation is checked at its site (DecimalLimit of the inline runtime), not only those of values a store may hold.
    /// </summary>
    public bool DecimalInline { get; private set; }
    /// <summary>
    /// System.getProperty(key, def) calls the lift resolved to def (key → site): the batch job has no system properties
    /// (J3S JSystemStatics: oracle A runs without -D options), so the modern code does not read the JVM's either.
    /// </summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> JobProperties { get; } = new(StringComparer.Ordinal);
    /// <summary>The legacy sites of J3S's unsupported-construct traps (abend 31) the lift keeps in the modern code (site → program).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> UnsupportedSites { get; } = new(StringComparer.Ordinal);
    /// <summary>The job adapter's string conversions the lift checks (JS-CHARS, JS-FIELD: printable ASCII), program and function → paragraph.</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> JobAdapterSites { get; } = new(StringComparer.Ordinal);
    /// <summary>Reflective constructions the lift turned into new C() (method: class name → class).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> ReflectionFolds { get; } = new(StringComparer.Ordinal);
    /// <summary>The class-path resources the code reads (method and call → the declared name).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> ResourceReads { get; } = new(StringComparer.Ordinal);
    /// <summary>The functions a case can run (Reflection.Lifetime: from the job, class initializers excluded).</summary>
    public int CaseReach { get; set; }
    /// <summary>J3S's build-time data of the target (resources, reflection list), when the estate has the table; else null.</summary>
    public TargetData? Target { get; set; }
    /// <summary>J3S's catch types: JX-ISA-n → the class the catch clause names (its "CATCH TYPE n: class" comment in FSJXP).</summary>
    public Dictionary<string, string> CatchTypes { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The runtime's array classes (one class id per length band): Object[] from JA-NEW-A, int[] from JA-NEW-I.</summary>
    public Dictionary<int, JType> ArrayIds { get; } = [];
    public HashSet<string> RuntimePrograms { get; } = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Pool fields that are J3S machinery (a throwable's recorded site): writes dropped.</summary>
    public HashSet<string> RuntimeFields { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> JobPrograms { get; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DriverPrograms { get; } = new(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex(@"^F(\d{4})$")] private static partial Regex FrameRx();
    [GeneratedRegex(@"^F(\d{4})-(.+)$")] private static partial Regex FrameSlotRx();
    [GeneratedRegex(@"^P(\d+)(?:-(.+))?$")] private static partial Regex ParamRx();
    [GeneratedRegex(@"^T(\d+)$")] private static partial Regex TempRx();
    [GeneratedRegex(@"^X(\d+)$")] private static partial Regex IndexRx();
    [GeneratedRegex(@"^(M\d{4})-")] private static partial Regex FnParaRx();
    [GeneratedRegex(@"^(.*?)\.([^.(]+)\((.*)\)$")] private static partial Regex SigRx();

    public static Model Build(Estate estate)
    {
        var m = new Model { Estate = estate };
        m.ClassifyPrograms();
        m.BuildPools();
        m.BuildStatics();
        m.BuildFunctions();
        m.ResultParameters();
        m.BuildClasses();
        m.BuildEntries();
        m.BuildClassIds();
        m.AssignFields();
        return m;
    }

    // ------------------------------------------------------------------ programs

    void ClassifyPrograms()
    {
        foreach (var p in Estate.Programs)
        {
            var header = J3sComments.HeaderClass(p);
            var id = p.Id.ToUpperInvariant();
            if (id == "FSJBDEC") DecimalInline = !p.Model.Paragraphs.Any(x => x.Name.Equals("7900-BIG-RESULT", StringComparison.OrdinalIgnoreCase));
            if (id.StartsWith("FSJR") || id == "FSJBDEC" || id.StartsWith("FNDK") || id.StartsWith("FNDT")) RuntimePrograms.Add(p.Id);
            else if (header is null)
            {
                // drivers have no J3S header and no FS-HEAP linkage; job programs own FS-CTX in WORKING-STORAGE
                bool hasFunctions = p.Model.Paragraphs.Any(x => FnParaRx().IsMatch(x.Name));
                if (hasFunctions) JobPrograms.Add(p.Id);
                else DriverPrograms.Add(p.Id);
            }
            else if (header == "J3S") JobPrograms.Add(p.Id);
        }
    }

    // ------------------------------------------------------------------ heap

    void BuildPools()
    {
        var sample = Estate.Programs.FirstOrDefault(p => p.ItemsByName.ContainsKey("FS-HEAP") && !RuntimePrograms.Contains(p.Id));
        if (sample is null) { Problems.Add("no program with FS-HEAP"); return; }
        var heap = sample.ItemsByName["FS-HEAP"];
        foreach (var pool in heap.Children.Where(c => c.Level != 88 && !c.IsFiller))
        {
            var entry = pool.Children.FirstOrDefault(c => c.Occurs > 0);
            if (entry is null) continue;
            var pd = new PoolDef { Prefix = pool.Name.ToUpperInvariant(), Entry = entry };
            int slot = 0;
            foreach (var f in entry.Children.Where(c => c.Level != 88 && !c.IsFiller))
            {
                var suffix = Kinds.Suffix(entry.Name.EndsWith("-E", StringComparison.OrdinalIgnoreCase) ? pool : entry, f);
                if (suffix is "ST" or "CI" or "IH" or "NX" or "EP") continue;
                var kind = Kinds.Of(f);
                if (suffix is "XF" or "XS") { RuntimeFields.Add(f.Name.ToUpperInvariant()); continue; }
                var fd = new FieldDef { CobolName = f.Name.ToUpperInvariant(), Name = Naming.Camel(suffix), Kind = kind, Item = f, Pool = pd, Slot = slot++ };
                pd.Fields.Add(fd);
                pd.FieldByItem[f.Name] = fd;
            }
            Pools[pd.Prefix] = pd;
        }
    }

    /// <summary>The pool field an item belongs to (the item itself or one of its parts), or null.</summary>
    public (FieldDef Field, string Part)? HeapField(DataItem it)
    {
        var path = new List<string>();
        var cur = it;
        while (cur is not null)
        {
            if (cur.Parent is { } par && par.Occurs > 0 && par.Parent is { } pool && Pools.TryGetValue(pool.Name, out var pd) && pd.Entry.Name.Equals(par.Name, StringComparison.OrdinalIgnoreCase))
            {
                if (!pd.FieldByItem.TryGetValue(cur.Name, out var fd)) return null;
                return (fd, PartPath(cur, it));
            }
            cur = cur.Parent;
        }
        return null;
    }

    /// <summary>The part of <paramref name="leaf"/> inside the slot <paramref name="slot"/>: "" (whole), "C", "I", "V", "P", "P.U", "P.RM", ...</summary>
    public static string PartPath(DataItem slot, DataItem leaf)
    {
        if (ReferenceEquals(slot, leaf) || slot.Name.Equals(leaf.Name, StringComparison.OrdinalIgnoreCase)) return "";
        var parts = new List<string>();
        var cur = leaf;
        while (cur is not null && !cur.Name.Equals(slot.Name, StringComparison.OrdinalIgnoreCase))
        {
            if (cur.Parent is null) return string.Join('.', parts);
            parts.Insert(0, Kinds.Suffix(cur.Parent, cur));
            cur = cur.Parent;
        }
        return string.Join('.', parts);
    }

    // ------------------------------------------------------------------ statics

    void BuildStatics()
    {
        var sample = Estate.Programs.FirstOrDefault(p => p.ItemsByName.ContainsKey("FS-CTX"));
        if (sample is null) return;
        var ctx = sample.ItemsByName["FS-CTX"];
        foreach (var grp in ctx.Children.Where(c => c.Level != 88 && !c.IsFiller))
        {
            var g = grp.Name.ToUpperInvariant();
            if (!g.StartsWith('S') || g is "SJC" && false) continue;
            if (g.Length < 2 || grp.IsElementary) continue;
            foreach (var f in grp.Children.Where(c => c.Level != 88 && !c.IsFiller))
            {
                var suffix = Kinds.Suffix(grp, f);
                var fd = new FieldDef { CobolName = f.Name.ToUpperInvariant(), Name = Naming.Constant(suffix), Kind = Kinds.Of(f), Item = f, IsStatic = true, Group = g };
                StaticsByItem[f.Name] = fd;
            }
        }
    }

    /// <summary>
    /// A job function's RESULT slot is its parameter when a caller stages it (MOVE ... TO F1138-RESULT before the PERFORM, an
    /// output function of the event job); otherwise it is the function's own result object, a local.
    /// </summary>
    void ResultParameters()
    {
        foreach (var fn in Functions.Values.ToList())
        {
            var slot = fn.Params.FirstOrDefault(p => p.Hint == "result" && p.CobolName.EndsWith("-RESULT", StringComparison.OrdinalIgnoreCase));
            if (slot is null) continue;
            var paras = fn.Program.Model.Paragraphs;
            bool staged = false;
            for (int i = 0; i < paras.Count && !staged; i++)
            {
                if (i >= fn.FirstParagraph && i <= fn.LastParagraph) continue;
                staged = Deep(paras[i].Statements).Any(s => s.Verb == "MOVE" && (s.L?.GetValueOrDefault("dst") ?? []).Any(d => d.V is { } dv && dv.StartsWith(slot.CobolName, StringComparison.OrdinalIgnoreCase)));
            }
            if (staged) continue;
            fn.Params.Remove(slot);
            fn.Slots[slot.CobolName] = new FrameSlot { CobolName = slot.CobolName, Role = SlotRole.Local, Hint = "result", Kind = slot.Kind, Item = slot.Item };
        }
    }

    public (FieldDef Field, string Part)? StaticField(DataItem it)
    {
        var cur = it;
        while (cur is not null)
        {
            if (StaticsByItem.TryGetValue(cur.Name, out var fd) && cur.Parent?.Parent?.Name.Equals("FS-CTX", StringComparison.OrdinalIgnoreCase) == true)
                return (fd, PartPath(cur, it));
            cur = cur.Parent;
        }
        return null;
    }

    // ------------------------------------------------------------------ functions and frames

    void BuildFunctions()
    {
        foreach (var p in Estate.Programs)
        {
            if (RuntimePrograms.Contains(p.Id) || DriverPrograms.Contains(p.Id)) continue;
            var (frames, srcs, _) = J3sComments.Notes(p);
            var frameSig = frames.GroupBy(f => f.Fn, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First().Signature, StringComparer.OrdinalIgnoreCase);
            // functions: paragraphs Mnnnn-NAME with an Mnnnn-X exit (or Fnnnn-END for job functions)
            var paras = p.Model.Paragraphs;
            for (int i = 0; i < paras.Count; i++)
            {
                var name = paras[i].Name;
                var mm = FnParaRx().Match(name);
                if (!mm.Success) continue;
                var id = mm.Groups[1].Value;
                if (name.EndsWith("-X", StringComparison.OrdinalIgnoreCase) && name.Length == id.Length + 2) continue;
                if (Functions.ContainsKey(id)) continue; // inner labels (Mnnnn-F1, Mnnnn-K2N, ...) follow the head
                var fn = new FunctionDef { Id = id, Program = p, Paragraph = name, FirstParagraph = i };
                var exit = id + "-X";
                int last = -1;
                for (int j = i; j < paras.Count; j++)
                {
                    if (paras[j].Name.Equals(exit, StringComparison.OrdinalIgnoreCase)) { last = j; break; }
                    var fe = "F" + id[1..] + "-END";
                    if (paras[j].Name.Equals(fe, StringComparison.OrdinalIgnoreCase)) { last = j; exit = fe; break; }
                }
                if (last < 0) { Problems.Add($"{p.Id} {name}: no exit paragraph"); continue; }
                fn.ExitParagraph = paras[last].Name;
                fn.LastParagraph = last;
                // signature: the SRC comment right before the head paragraph, else the frame comment
                var src = srcs.LastOrDefault(s => s.Line < paras[i].Line && s.Line > (i > 0 ? paras[i - 1].Line : 0));
                if (src is not null)
                {
                    fn.SrcAbbr = src.Abbr; fn.SrcFrom = src.From; fn.SrcTo = src.To;
                    fn.Signature = src.Signature;
                }
                if (frameSig.TryGetValue(id, out var fsig)) fn.Signature = fsig;
                if (string.IsNullOrEmpty(fn.Signature))
                {
                    var block = p.CommentBlockBefore(paras[i].Line);
                    fn.Signature = block.LastOrDefault()?.Trim() ?? "";
                }
                ParseSignature(fn);
                Functions[id] = fn;
                i = last;
            }
            // frame slots
            foreach (var it in p.Model.Items)
            {
                var fm = FrameSlotRx().Match(it.Name);
                if (!fm.Success) continue;
                var fid = "M" + fm.Groups[1].Value;
                if (!Functions.TryGetValue(fid, out var fn) || !ReferenceEquals(fn.Program, p)) continue;
                var rest = fm.Groups[2].Value.ToUpperInvariant();
                var par = it.Parent;
                bool inFrame = par is not null && par.Name.Length == 7 && (par.Name.EndsWith("-V", StringComparison.OrdinalIgnoreCase) || par.Name.EndsWith("-R", StringComparison.OrdinalIgnoreCase)) && par.Name[..5].Equals(it.Name[..5], StringComparison.OrdinalIgnoreCase);
                bool isRet = rest == "RET" && it.Level == 1;
                bool isIndex = it.Level == 1 && IndexRx().IsMatch(rest);
                if (!inFrame && !isRet && !isIndex) continue;
                if (fn.Slots.ContainsKey(it.Name)) continue;
                FrameSlot slot;
                if (isRet) slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Ret, Kind = Kinds.Of(it), Item = it };
                else if (isIndex) slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Index, No = int.Parse(IndexRx().Match(rest).Groups[1].Value), Kind = VKind.Ref, Item = it };
                else if (rest == "THIS") slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.This, Kind = Kinds.Of(it), Item = it };
                else if (ParamRx().Match(rest) is { Success: true } pm) slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Param, No = int.Parse(pm.Groups[1].Value), Hint = pm.Groups[2].Success ? pm.Groups[2].Value : null, Kind = Kinds.Of(it), Item = it };
                else if (TempRx().Match(rest) is { Success: true } tm) slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Temp, No = int.Parse(tm.Groups[1].Value), Kind = Kinds.Of(it), Item = it };
                else if (rest.StartsWith("L-")) slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Local, Hint = rest[2..], Kind = Kinds.Of(it), Item = it };
                // an output function's result object (the caller stages it in the -R region): its parameter
                else if (rest == "RESULT") slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Param, No = 1, Hint = "result", Kind = Kinds.Of(it), Item = it };
                // a job function's objects (O-CURRENCY, O-DATA): its locals
                else if (rest.StartsWith("O-")) slot = new FrameSlot { CobolName = it.Name.ToUpperInvariant(), Role = SlotRole.Local, Hint = rest[2..], Kind = Kinds.Of(it), Item = it };
                else continue;
                fn.Slots[slot.CobolName] = slot;
                if (slot.Role == SlotRole.This) fn.This = slot;
                if (slot.Role == SlotRole.Ret) fn.Ret = slot;
                if (slot.Role == SlotRole.Param) fn.Params.Add(slot);
            }
        }
        foreach (var fn in Functions.Values) fn.Params.Sort((a, b) => a.No.CompareTo(b.No));
    }

    static void ParseSignature(FunctionDef fn)
    {
        // type annotations (@NotNull RepaymentPeriod) are not part of the type
        var sig = Regex.Replace(fn.Signature, @"@[A-Za-z_][A-Za-z0-9_.]*(\([^)]*\))?\s*", "");
        sig = Regex.Replace(sig, @"\b(NotNull|Nullable|NonNull|NotEmpty|NotBlank|Valid|PositiveOrZero|Positive)\s+", "");
        // the comment's line wrap can glue the annotation to the type (NotNull + DaysInYearType)
        sig = Regex.Replace(sig, @"\b(NotNull|NonNull|NotEmpty|NotBlank|PositiveOrZero)(?=[A-Z][a-z])", "");
        sig = Regex.Replace(sig, @"\s+", "");
        // J3S specializations of emulation methods per closure: "JArrayList.forEach(Object>) for lambda PEC:140"
        var forAt = sig.IndexOf(")for", StringComparison.Ordinal);
        if (forAt > 0 && !sig.StartsWith("lambda")) sig = sig[..(forAt + 1)];
        if (sig.StartsWith("lambda")) { fn.IsLambda = true; fn.ClassSimple = "lambda"; fn.MethodName = "lambda"; return; }
        if (sig.StartsWith("job")) { fn.IsJob = true; fn.ClassSimple = "job"; fn.MethodName = sig[3..]; return; }
        if (sig.StartsWith("<clinit>")) { fn.IsClinit = true; fn.ClassSimple = sig["<clinit>".Length..]; fn.MethodName = "<clinit>"; return; }
        var m = SigRx().Match(sig);
        if (!m.Success) return;
        fn.ClassSimple = m.Groups[1].Value;
        fn.MethodName = m.Groups[2].Value;
        fn.IsCtor = fn.MethodName == "<init>";
        var ps = m.Groups[3].Value;
        if (ps.Length > 0)
        {
            // split on commas outside angle brackets
            int depth = 0, start = 0;
            for (int i = 0; i < ps.Length; i++)
            {
                if (ps[i] == '<') depth++;
                else if (ps[i] == '>') depth = Math.Max(0, depth - 1);
                else if (ps[i] == ',' && depth == 0) { fn.ParamTypes.Add(ps[start..i]); start = i + 1; }
            }
            fn.ParamTypes.Add(ps[start..]);
        }
    }

    public FunctionDef? FunctionOfParagraph(string paragraph)
    {
        var m = FnParaRx().Match(paragraph);
        return m.Success && Functions.TryGetValue(m.Groups[1].Value, out var fn) ? fn : null;
    }

    /// <summary>The frame slot an item belongs to (the slot item or one of its parts) and the part path.</summary>
    public (FunctionDef Fn, FrameSlot Slot, string Part)? FrameSlotOf(SourceProgram p, DataItem it)
    {
        var cur = it;
        while (cur is not null)
        {
            var fm = FrameSlotRx().Match(cur.Name);
            if (fm.Success && Functions.TryGetValue("M" + fm.Groups[1].Value, out var fn) && ReferenceEquals(fn.Program, p) && fn.Slots.TryGetValue(cur.Name, out var slot))
                return (fn, slot, PartPath(cur, it));
            cur = cur.Parent;
        }
        return null;
    }

    // ------------------------------------------------------------------ classes

    void BuildClasses()
    {
        // header classes
        foreach (var p in Estate.Programs)
        {
            var header = J3sComments.HeaderClass(p);
            if (header is null || header == "J3S" || RuntimePrograms.Contains(p.Id)) continue;
            var c = GetClass(header);
            c.Programs.Add(p.Id);
        }
        // abbreviations: the SRC abbreviation of functions whose class is the header class of their program
        foreach (var fn in Functions.Values)
        {
            if (fn.SrcAbbr is null || fn.IsLambda || fn.IsJob) continue;
            var header = J3sComments.HeaderClass(fn.Program);
            if (header is null) continue;
            var simple = header[(header.LastIndexOf('.') + 1)..];
            if (fn.ClassSimple == simple || fn.IsClinit && fn.ClassSimple == header)
                ClassByAbbr.TryAdd(fn.SrcAbbr, Classes[header]);
        }
        // anonymous classes (an empty simple name in the signatures): one per anonymous constructor, in source order ($1, $2,
        // as javac numbers them); a method belongs to the last anonymous constructor before it in the source
        foreach (var g in Functions.Values.Where(f => f.ClassSimple.Length == 0 && !f.IsLambda && !f.IsJob && !f.IsClinit && f.SrcAbbr is not null).GroupBy(f => f.SrcAbbr!))
        {
            var ctors = g.Where(f => f.IsCtor).OrderBy(f => f.SrcFrom).ToList();
            foreach (var f in g)
            {
                int k = ctors.FindLastIndex(c => c.SrcFrom <= f.SrcFrom);
                f.ClassSimple = "$" + (k < 0 ? 1 : k + 1);
            }
        }
        // owner of every function: those with a source abbreviation first (they create the nested classes)
        var bySimple = Classes.Values.GroupBy(c => c.SimpleName).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var fn in Functions.Values.OrderBy(f => f.SrcAbbr is null ? 1 : 0).ThenBy(f => f.Id, StringComparer.Ordinal))
        {
            if (fn.SrcAbbr is null && !fn.IsJob && !fn.IsLambda && !fn.IsClinit)
                bySimple = Classes.Values.GroupBy(c => c.SimpleName).ToDictionary(g => g.Key, g => g.ToList());
            if (fn.IsJob) { fn.Owner = GetClass("modern.job." + Naming.Pascal(fn.MethodName.Replace("JOB-", "").Replace("-", " "))); fn.Owner.Kind = ClassKind.Job; continue; }
            if (fn.IsLambda) continue; // closures are classes of their own, bound in BuildClassIds
            if (fn.IsClinit)
            {
                fn.ClassSimple = fn.ClassSimple.Replace('$', '.');
                if (Classes.TryGetValue(fn.ClassSimple, out var cc)) fn.Owner = cc;
                else fn.Owner = GetClass(fn.ClassSimple);
                fn.Owner.Functions.Add(fn);
                continue;
            }
            ClassDef? owner = null;
            ClassByAbbr.TryGetValue(fn.SrcAbbr ?? "", out var outer);
            if (outer is not null && outer.SimpleName == fn.ClassSimple) owner = outer;
            else if (bySimple.TryGetValue(fn.ClassSimple, out var cands) && cands.Count == 1 && (outer is null || cands[0].FullName.StartsWith(outer.FullName + ".") || cands[0].Outer is null)) owner = cands[0];
            else if (outer is not null)
            {
                owner = GetClass(outer.FullName + "." + fn.ClassSimple);
                owner.Outer = outer;
                if (outer.Kind == ClassKind.Emulation) owner.Kind = ClassKind.Emulation;
            }
            else
            {
                var hdr = J3sComments.HeaderClass(fn.Program);
                owner = hdr is not null && hdr != "J3S" ? GetClass(hdr[..hdr.LastIndexOf('.')] + "." + fn.ClassSimple) : GetClass(fn.ClassSimple);
            }
            fn.Owner = owner;
            owner.Functions.Add(fn);
            if (fn.SrcAbbr is not null && owner.Abbr is null && owner.Outer is null) owner.Abbr = fn.SrcAbbr;
        }
    }

    public ClassDef GetClass(string fullName)
    {
        if (Classes.TryGetValue(fullName, out var c)) return c;
        int dot = fullName.LastIndexOf('.');
        c = new ClassDef { FullName = fullName, Package = dot < 0 ? "" : fullName[..dot] };
        // the JRE emulation compiled into the estate (fncobolgen.jre) is never domain code
        if (fullName.StartsWith("fncobolgen.", StringComparison.Ordinal)) c.Kind = ClassKind.Emulation;
        Classes[fullName] = c;
        return c;
    }

    // ------------------------------------------------------------------ entries

    void BuildEntries()
    {
        foreach (var p in Estate.Programs)
        {
            if (RuntimePrograms.Contains(p.Id) || DriverPrograms.Contains(p.Id)) continue;
            var paras = p.Model.Paragraphs;
            // P0-MAIN: GO TO E0001 E0002 ... DEPENDING ON FA-ENTRY
            var main = paras.FirstOrDefault(x => x.Name.Equals("P0-MAIN", StringComparison.OrdinalIgnoreCase));
            if (main is null) continue;
            var go = main.Statements.FirstOrDefault(s => s.Verb == "GO TO" && s.Get("depending") is not null);
            var targets = go?.L?.GetValueOrDefault("targets");
            if (targets is null) continue;
            var map = new Dictionary<int, EntryDef>();
            for (int k = 0; k < targets.Count; k++)
            {
                var ep = targets[k].V!;
                if (!p.ParagraphIndex.TryGetValue(ep, out var idx)) continue;
                var fn = EntryFunction(p, paras[idx]);
                if (fn is null) { Problems.Add($"{p.Id} {ep}: entry without a function"); continue; }
                map[k + 1] = new EntryDef(p, k + 1, fn);
            }
            Entries[p.Id] = map;
        }
    }

    /// <summary>The function an entry paragraph runs: PERFORM Mnnnn THRU ..., or the recursive protocol (MOVE n TO JR-SEL, PERFORM Sk-RUN).</summary>
    FunctionDef? EntryFunction(SourceProgram p, Paragraph ep)
    {
        int? sel = null;
        foreach (var s in ep.Statements)
        {
            if (s.Verb == "MOVE" && s.L?["dst"] is [{ V: var dv }] && dv!.Equals("JR-SEL", StringComparison.OrdinalIgnoreCase) && s.Get("src") is { K: "num", V: var nv })
                sel = int.Parse(nv!);
            if (s.Verb != "PERFORM") continue;
            var target = s.Str("target");
            if (target is null) continue;
            var fn = FunctionOfParagraph(target);
            if (fn is not null && ReferenceEquals(fn.Program, p)) return fn;
            if (sel is int k && target.EndsWith("-RUN", StringComparison.OrdinalIgnoreCase))
            {
                var comp = target[..^4];
                var run = p.Model.Paragraphs.FirstOrDefault(x => x.Name.Equals(target, StringComparison.OrdinalIgnoreCase));
                var go = run?.Statements.FirstOrDefault(x => x.Verb == "GO TO" && x.Get("depending") is not null);
                var ts = go?.L?.GetValueOrDefault("targets");
                if (ts is not null && k >= 1 && k <= ts.Count)
                {
                    var f2 = FunctionOfParagraph(ts[k - 1].V!);
                    if (f2 is not null)
                    {
                        // every member of the component is stacked
                        for (int i = 0; i < ts.Count; i++)
                            if (FunctionOfParagraph(ts[i].V!) is { } mem) { mem.Component = comp; mem.ComponentSel = i + 1; }
                        return f2;
                    }
                }
            }
        }
        return null;
    }

    public FunctionDef? ResolveEntry(string program, int entry) =>
        Entries.TryGetValue(program, out var map) && map.TryGetValue(entry, out var e) ? e.Fn : null;
}
