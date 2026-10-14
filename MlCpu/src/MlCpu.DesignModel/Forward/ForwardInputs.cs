using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The forward-engineering evaluation's inputs (tools/eval/PROTOCOL-forward.md, forward/cas, forward/loans): the specification,
/// the design rules, the given API, the rule digests, the schemas, the acceptance scenarios' manifest and the implementation
/// slices. What differs between packages (modules, slices, rule ids, the application's entry point, the judge's Gradle
/// modules) comes from the package manifest forward.json (mlcpu.forward-package.v1); the code knows no package by name.
/// </summary>
public sealed class ForwardInputs
{
    /// <summary>The package directory (forward/cas, forward/loans).</summary>
    public string CasDir { get; }
    public string Dir => CasDir;
    public string Spec { get; }
    public string Rules { get; }
    public string Rubric { get; }
    /// <summary>API source files: path relative to the source root (com/mlcpu/cas/api/...) → text.</summary>
    public SortedDictionary<string, string> Api { get; } = new(StringComparer.Ordinal);
    public SortedDictionary<string, string> Digests { get; } = new(StringComparer.Ordinal);
    public JsonArray Scenarios { get; }
    public IReadOnlyList<string> AllRules { get; }

    /// <summary>
    /// One implementation slice: its context module and package, the digests and requirements packets it rests on, the rule
    /// prefixes it owns and the use-case methods it implements. Optional (forward/loans): the entry point it adds to the entry
    /// class ("public static DueDates dueDates()"), the SPI its context implements, and the API packages only it uses (a run
    /// without the slice leaves them out of the prompts), and whether its prompts show the in-memory ports (when any slice says,
    /// only those do; otherwise every slice's).
    /// </summary>
    public sealed record Slice(string Name, string Module, string Package, string[] Digests, string[] Packets, string[] RulePrefixes, string[] Methods,
        string? Entry = null, string? Implements = null, string[]? ApiPackages = null, bool Ports = false);

    // ---------------------------------------------------------------- the package manifest

    public string Name { get; }
    /// <summary>The service as the prompts name it ("Customer Account Service").</summary>
    public string ServiceName { get; }
    public string ServiceDir { get; }
    public string ApiModule { get; }
    public string ApiPackage { get; }
    public string ArchitectureModule { get; }
    public string AcceptanceModule { get; }
    /// <summary>The Gradle property that selects the slices whose scenarios run (-PcasSlices=...).</summary>
    public string SliceProperty { get; }
    /// <summary>The SME's test tools inside the acceptance module, never copied to the judge (relative to the service).</summary>
    public string? SmeDir { get; }
    /// <summary>The in-memory adapters shown in implementation prompts; null when the service has no ports (the loans quote).</summary>
    public string? InMemoryPortsFile { get; }
    public string ScenarioRoot { get; }
    public string ApplicationModule { get; }
    public string ApplicationPackage { get; }
    public string EntryClass { get; }
    public string EntrySignature { get; }
    /// <summary>The use-case interface the entry point returns (CustomerAccountService).</summary>
    public string ServiceInterface { get; }
    public IReadOnlyList<Slice> Slices { get; }
    /// <summary>Every slice of the manifest, whatever --slices selects (the later slices a shared entry point serves).</summary>
    public IReadOnlyList<Slice> AllSlices { get; }
    /// <summary>The implementation modules: every context module and the application module.</summary>
    public IReadOnlyList<(string Module, string Package)> Modules { get; }
    /// <summary>The package's walks (forward.json "walks"): &lt;dir&gt;/&lt;slice&gt;/&lt;public scenario id&gt;.md; null when it has none.</summary>
    public string? WalksDir { get; }

    /// <summary>The walk of a public scenario (the reference's intermediate values, rule by rule), or null.</summary>
    public string? Walk(string slice, string id)
    {
        if (WalksDir is null) return null;
        var f = Path.Combine(CasDir, WalksDir, slice, id + ".md");
        return File.Exists(f) ? Norm(File.ReadAllText(f)) : null;
    }

    /// <summary>The manifest's implementation modules a slice filter leaves out (empty without one): SPEC and RULES name them, the run does not build them.</summary>
    public IReadOnlyList<string> NotBuilt { get; }
    public IReadOnlyList<string> UseCaseMethods { get; }
    /// <summary>The modules a context module may depend on (besides java.*).</summary>
    public IReadOnlyList<string> ContextDependencies { get; }
    /// <summary>The modules the application module may depend on; "@contexts" stands for every context module.</summary>
    public IReadOnlyList<string> ApplicationDependencies { get; }
    /// <summary>"How the service is tested" (before the in-memory adapters' source).</summary>
    public string Testing { get; }
    /// <summary>How the public scenario examples are introduced (forward.json "scenarioFormat"; the CAS wording by default).</summary>
    public string ScenarioFormat { get; }
    /// <summary>The leak checker (forward.json "leakDictionary"), or null for a package without one (CAS).</summary>
    public LeakChecker? Leaks { get; }
    /// <summary>The slices this run is limited to (--slices), or null for all of them.</summary>
    public IReadOnlyCollection<string>? OnlySlices { get; }
    /// <summary>The Java release the service compiles for (forward.json "javaLevel"; null: Java 21, as CAS).</summary>
    public int? JavaLevel { get; }

    /// <summary>A prompt text with its "Java 21" made the package's Java level (unchanged for CAS).</summary>
    public string Java(string text) => JavaLevel is { } j ? text.Replace("Java 21", $"Java {j}") : text;

    /// <param name="onlySlices">Limit the package to these slices (a pilot): their modules and the application module, their
    /// use-case methods, their digests and rules. Null: the whole package.</param>
    /// <param name="scanPackage">Scan the package's files with the leak checker and refuse a package with a hit (the default).</param>
    public ForwardInputs(string casDir, IReadOnlyCollection<string>? onlySlices = null, bool scanPackage = true)
    {
        CasDir = Path.GetFullPath(casDir);
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(CasDir, "forward.json")))!.AsObject();
        if (m["schema"]?.ToString() != "mlcpu.forward-package.v1") throw new InvalidDataException($"{CasDir}/forward.json: schema must be mlcpu.forward-package.v1");
        static string[] Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray();
        string S(JsonNode? o, string key) => o?[key]?.GetValue<string>() ?? throw new InvalidDataException($"{CasDir}/forward.json: {key} is missing");
        Name = S(m, "name");
        ServiceName = S(m, "serviceName");
        var service = m["service"];
        ServiceDir = S(service, "dir");
        ApiModule = S(service, "apiModule");
        ApiPackage = S(service, "apiPackage");
        ArchitectureModule = S(service, "architectureModule");
        AcceptanceModule = S(service, "acceptanceModule");
        SliceProperty = S(service, "sliceProperty");
        SmeDir = service?["smeDir"]?.GetValue<string>();
        InMemoryPortsFile = service?["inMemoryPorts"]?.GetValue<string>();
        ScenarioRoot = S(service, "scenarioRoot");
        var app = m["application"];
        ApplicationModule = S(app, "module");
        ApplicationPackage = S(app, "package");
        EntryClass = S(app, "entryClass");
        EntrySignature = S(app, "entrySignature");
        ServiceInterface = S(app, "serviceInterface");
        var slices = (m["slices"] as JsonArray ?? []).Select(s => new Slice(S(s, "name"), S(s, "module"), S(s, "package"), Strings(s!["digests"]),
            Strings(s!["packets"]), Strings(s!["rulePrefixes"]), Strings(s!["methods"]), s!["entry"]?.GetValue<string>(),
            s!["implements"]?.GetValue<string>(), s!["apiPackages"] is null ? null : Strings(s!["apiPackages"]),
            s!["ports"]?.GetValue<bool>() ?? false)).ToList();
        var allSlices = slices;
        var modules = (m["modules"] as JsonArray ?? []).Select(x => (S(x, "module"), S(x, "package"))).ToList();
        var methods = Strings(m["useCaseMethods"]);
        if (onlySlices is { Count: > 0 })
        {
            var unknown = onlySlices.Where(o => slices.All(s => s.Name != o)).ToList();
            if (unknown.Count > 0) throw new ArgumentException($"{CasDir}/forward.json has no slice {string.Join(", ", unknown)}");
            OnlySlices = onlySlices;
            slices = slices.Where(s => onlySlices.Contains(s.Name)).ToList();
            var keep = slices.Select(s => s.Module).Append(S(m["application"], "module")).ToHashSet(StringComparer.Ordinal);
            modules = modules.Where(x => keep.Contains(x.Item1)).ToList();
            var used = slices.SelectMany(s => s.Methods).ToHashSet(StringComparer.Ordinal);
            methods = methods.Where(used.Contains).ToArray();
        }
        AllSlices = allSlices;
        Slices = slices;
        NotBuilt = (m["modules"] as JsonArray ?? []).Select(x => S(x, "module")).Where(x => modules.All(k => k.Item1 != x)).ToList();
        Modules = modules;
        UseCaseMethods = methods;
        ContextDependencies = Strings(m["dependencies"]?["context"]);
        ApplicationDependencies = Strings(m["dependencies"]?["application"]);
        if (OnlySlices is not null)
        {
            // A pilot builds only its slices: a dependency on a module it does not build (an empty one) is not offered.
            bool Built(string d) => d == ApiModule || d == "@contexts" || Modules.Any(x => x.Module == d);
            ContextDependencies = ContextDependencies.Where(Built).ToList();
            ApplicationDependencies = ApplicationDependencies.Where(Built).ToList();
        }
        Testing = string.Join("\n", Strings(m["testing"]));
        ScenarioFormat = m["scenarioFormat"]?.GetValue<string>() ?? "the scenario format: initial state, steps with their expected results, expected final state";
        if (m["leakDictionary"]?.GetValue<string>() is { } dict) Leaks = new LeakChecker(Path.Combine(CasDir, dict));
        JavaLevel = m["javaLevel"]?.GetValue<int>();
        WalksDir = m["walks"]?.GetValue<string>();

        Spec = Read("SPEC.md");
        Rules = Read("RULES.md");
        Rubric = File.Exists(Path.Combine(CasDir, "rubric.md")) ? Read("rubric.md") : "";
        var apiRoot = Path.Combine(CasDir, ServiceDir, S(service, "apiSourceRoot"));
        // API packages that only slices outside this run use are left out (forward.json "apiPackages")
        var foreign = AllSlices.Where(a => Slices.All(x => x.Name != a.Name)).SelectMany(a => a.ApiPackages ?? []).ToList();
        foreach (var f in Directory.EnumerateFiles(apiRoot, "*.java", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(apiRoot, f).Replace('\\', '/');
            var package = Path.GetDirectoryName(rel)!.Replace('\\', '/').Replace('/', '.');
            if (foreign.Any(p => package == p || package.StartsWith(p + ".", StringComparison.Ordinal))) continue;
            Api[rel] = Norm(File.ReadAllText(f));
        }
        var wanted = OnlySlices is null ? null : Slices.SelectMany(s => s.Digests).ToHashSet(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(Path.Combine(CasDir, "digests"), "*.md"))
            if (wanted is null || wanted.Contains(Path.GetFileNameWithoutExtension(f)))
                Digests[Path.GetFileNameWithoutExtension(f)] = Norm(File.ReadAllText(f));
        Scenarios = JsonNode.Parse(File.ReadAllText(Path.Combine(CasDir, "scenarios.json")))!["scenarios"]!.AsArray();
        var docs = string.Join("\n", Digests.Values) + "\n" + Spec;
        AllRules = Strings(m["rulePatterns"]).SelectMany(p => Regex.Matches(docs, p).Select(x => x.Groups[1].Value))
            .Where(r => OnlySlices is null || Slices.Any(s => s.RulePrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal))))
            .Distinct().OrderBy(r => r, StringComparer.Ordinal).ToList();
        // Package material that can reach a prompt must not carry the source system's names (section 4.3).
        if (Leaks is not null && scanPackage)
        {
            var hits = Leaks.ScanPackage(CasDir);
            if (hits.Count > 0)
                throw new LeakException("package " + Name, hits.Select(h => h.Hit with { Where = h.File + (h.Hit.Where.Length > 0 ? ", " + h.Hit.Where : "") }).ToList());
        }
    }

    string Read(string name) => Norm(File.ReadAllText(Path.Combine(CasDir, name)));

    public static string Norm(string s) => s.Replace("\r\n", "\n");

    /// <summary>
    /// A JSON schema of the package. Placeholders (forward/loans; the CAS schemas have none) take the package's values: the
    /// JSON strings "@MODULES@", "@CONTEXTS@" and "@METHODS@" become arrays of the (selected) modules, slices and use-case
    /// methods; @SERVICE@, @SERVICE_INTERFACE@, @API_MODULE@, @N_MODULES@, @MODULE_LIST@, @CONTEXT_LIST@, @METHOD_LIST@ and
    /// @RULE_PREFIXES@ become text.
    /// </summary>
    public string Schema(string name)
    {
        var s = Norm(File.ReadAllText(Path.Combine(CasDir, "schemas", name)));
        static string Array(IEnumerable<string> xs) => "[" + string.Join(", ", xs.Select(x => "\"" + x + "\"")) + "]";
        var modules = Modules.Select(x => x.Module).ToList();
        var contexts = Slices.Select(x => x.Name).ToList();
        return s.Replace("\"@MODULES@\"", Array(modules)).Replace("\"@CONTEXTS@\"", Array(contexts)).Replace("\"@METHODS@\"", Array(UseCaseMethods))
            .Replace("@SERVICE_INTERFACE@", ServiceInterface).Replace("@SERVICE@", ServiceName).Replace("@API_MODULE@", ApiModule)
            .Replace("@N_MODULES@", Words(modules.Count)).Replace("@MODULE_LIST@", string.Join(", ", modules))
            .Replace("@CONTEXT_LIST@", string.Join(", ", contexts)).Replace("@METHOD_LIST@", string.Join(", ", UseCaseMethods))
            .Replace("@RULE_PREFIXES@", string.Join(", ", Slices.SelectMany(x => x.RulePrefixes).Select(p => p.TrimEnd('-'))));
    }

    /// <summary>
    /// The Task's sentence on the modules SPEC and RULES name but this run does not build (a slice filter), or "": without it a
    /// model reads A-1 as permitting a dependency on them and the check rejects it.
    /// </summary>
    public string NotBuiltNote() => NotBuilt.Count == 0 ? "" :
        $"{string.Join(", ", NotBuilt)} {(NotBuilt.Count == 1 ? "is" : "are")} not built in this run (later slices): no module may depend on " +
        $"{(NotBuilt.Count == 1 ? "it" : "them")}, and the code a module would take from {(NotBuilt.Count == 1 ? "it" : "them")} stays in its own package.";

    public IEnumerable<string> RulesOf(Slice s) => AllRules.Where(r => s.RulePrefixes.Any(p => r.StartsWith(p, StringComparison.Ordinal)));

    /// <summary>The API as one markdown section: every source file, complete.</summary>
    public string ApiSection()
    {
        var sb = new StringBuilder($"# The given API: {ApiModule} (complete source; it is fixed)\n");
        foreach (var (path, src) in Api)
            sb.Append($"## {path}\n```java\n{src.TrimEnd()}\n```\n");
        return sb.ToString();
    }

    public string DigestSection(IEnumerable<string> names)
    {
        var sb = new StringBuilder();
        foreach (var n in names)
            sb.Append($"# Rule digest digests/{n}.md\n{Digests[n].TrimEnd()}\n\n");
        return sb.ToString();
    }

    public string AllDigests() => DigestSection(Digests.Keys);

    /// <summary>The scenarios of a slice in the manifest (id, visibility, rules, origin).</summary>
    public IEnumerable<JsonNode> ScenariosOf(string slice) => Scenarios.Where(s => s!["slice"]!.GetValue<string>() == slice)!;

    /// <summary>A scenario's JSON file (public or hidden).</summary>
    public string ScenarioFile(string slice, string visibility, string id) =>
        Path.Combine(CasDir, ServiceDir, ScenarioRoot, visibility, slice, id + ".json");

    /// <summary>The in-memory adapters' source (test code), shown in the implementation prompts; null when there are none.</summary>
    public string? InMemoryPorts() => InMemoryPortsFile is null ? null : Norm(File.ReadAllText(Path.Combine(CasDir, ServiceDir, InMemoryPortsFile)));

    /// <summary>
    /// The prompt must not carry the source system's names from the package: a hit stops the step (no-op for a package without
    /// a dictionary). Text the model wrote itself (its architecture, slice designs, accepted code) is left out of the check and
    /// measured instead (contamination, section 10.3): a name it chose is logged, not blocked.
    /// </summary>
    public void CheckLeaks(string prompt, string what, params string?[] modelText)
    {
        if (Leaks is null) return;
        var own = prompt;
        foreach (var t in modelText)
        {
            if (string.IsNullOrEmpty(t)) continue;
            own = own.Replace(t, "");
            var (_, leaked, words) = Leaks.Overlap(t);
            if (leaked.Count + words.Count > 0)
                Program.Log($"  contamination (not blocked): the model's own text in the {what} uses {string.Join(", ", leaked.Concat(words))}");
        }
        Leaks.Check(own, what);
    }

    // ---------------------------------------------------------------- where a class goes

    static readonly Regex PackageDecl = new(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline);

    public static string? PackageOf(string source) => PackageDecl.Match(source) is { Success: true } m ? m.Groups[1].Value : null;

    /// <summary>The module a package belongs to (by root package), or null.</summary>
    public string? ModuleOf(string package) =>
        Modules.Where(m => package == m.Package || package.StartsWith(m.Package + ".", StringComparison.Ordinal)).Select(m => m.Module).FirstOrDefault();

    /// <summary>The module of a source file (by its package declaration), or "".</summary>
    public string ModuleOfSource(string source) => ModuleOf(PackageOf(source) ?? "") ?? "";

    /// <summary>Relative path of a file in the harness: module/src/main/java/package/Type.java.</summary>
    public string PathOf(string type, string source)
    {
        var pkg = PackageOf(source) ?? "";
        return $"{ModuleOf(pkg) ?? "unplaced"}/src/main/java/{pkg.Replace('.', '/')}/{type}.java";
    }

    /// <summary>The service's Gradle modules (implementation, API, architecture and acceptance), for paths in build output.</summary>
    public IEnumerable<string> AllModules() => Modules.Select(m => m.Module).Append(ApiModule).Append(ArchitectureModule).Append(AcceptanceModule).Distinct();

    /// <summary>The modules a module may depend on (RULES.md A-1, A-2), as the manifest states them.</summary>
    public ISet<string> AllowedDependencies(string module)
    {
        var contexts = Slices.Select(s => s.Module).ToHashSet(StringComparer.Ordinal);
        var list = module == ApplicationModule ? ApplicationDependencies : contexts.Contains(module) ? ContextDependencies : [];
        return list.SelectMany(d => d == "@contexts" ? contexts : [d]).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>A small number in words, as the prompts write it (the five implementation modules).</summary>
    public static string Words(int n) => n switch
    {
        1 => "one", 2 => "two", 3 => "three", 4 => "four", 5 => "five", 6 => "six", 7 => "seven", 8 => "eight", 9 => "nine", 10 => "ten",
        11 => "eleven", 12 => "twelve", _ => n.ToString()
    };
}
