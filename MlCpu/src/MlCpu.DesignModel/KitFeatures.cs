using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MlCpu.Kit;

namespace MlCpu.DesignModel;

/// <summary>
/// What a published kit offers beyond pipeline v2 (normalization/kit-features.json). A kit without the file is rendered
/// exactly as before, so a frozen kit (publish-v5) gives the prompts and feedback its runs had: the A arm of an A/B.
/// </summary>
public sealed class KitFeatures
{
    /// <summary>Worked examples in values (numbers decoded, tables as rows, after = changes only); a property of examples.md itself.</summary>
    public const string ExamplesValues = "examples-values";
    /// <summary>The implementation prompt renders the packet without statement text (the AST outline has it) and the record kit's records by reference.</summary>
    public const string PromptPacketCompact = "prompt-packet-compact";
    /// <summary>A failing subprogram gets CALL-level shadow feedback: the first CALL where its results differ from the faithful program's.</summary>
    public const string ShadowFeedback = "shadow-feedback";
    /// <summary>The prompt's starting point is the typed reference implementation (normalization/PID/typed-reference/), not the COBOL outline.</summary>
    public const string TypedReference = "typed-reference";
    /// <summary>
    /// A subprogram may CALL the programs it CALLs (runtime <c>ModernProgram.callProgram</c>, in the kit's runtime sources). Without
    /// it a subprogram had no CALL port and had to re-implement its callees: FNLS0300 faked FNEM0200's EMI engine (round 3).
    /// </summary>
    public const string SubprogramCalls = "subprogram-calls";
    /// <summary>
    /// A record of more than 120 items lists, in the implementation prompt, only the items the program's statements name
    /// (PromptRecords): a J3S program's 935-item object heap made its prompt larger than Gemma 4's context.
    /// </summary>
    public const string PassThroughRecords = "pass-through-records";

    public static readonly string[] Current = [ExamplesValues, PromptPacketCompact, ShadowFeedback, SubprogramCalls, PassThroughRecords];

    readonly HashSet<string> set;
    public string Source { get; }

    KitFeatures(IEnumerable<string> features, string source) { set = features.ToHashSet(StringComparer.Ordinal); Source = source; }

    public bool Has(string feature) => set.Contains(feature);
    public IReadOnlyCollection<string> All => set;

    /// <summary>
    /// The kit's features (normalization/kit-features.json next to the packet's folder), unless MLCPU_KIT_FEATURES overrides
    /// them: "none", "all", or a comma list.
    /// </summary>
    public static KitFeatures Of(string packetPath)
    {
        var env = Environment.GetEnvironmentVariable("MLCPU_KIT_FEATURES");
        if (!string.IsNullOrWhiteSpace(env))
            return env.Trim() switch
            {
                "none" => new([], "MLCPU_KIT_FEATURES=none"),
                "all" => new([.. Current, TypedReference], "MLCPU_KIT_FEATURES=all"),
                var list => new(list.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "MLCPU_KIT_FEATURES")
            };
        var file = Path.Combine(Path.GetDirectoryName(Path.GetDirectoryName(Path.GetFullPath(packetPath))!)!, "kit-features.json");
        if (!File.Exists(file)) return new([], "none (no kit-features.json)");
        var j = JsonNode.Parse(File.ReadAllText(file))!;
        return new(j["features"]?.AsArray().Select(x => x!.GetValue<string>()) ?? [], file);
    }
}

/// <summary>
/// kit-upgrade: a new kit from a published one without republishing. The project is copied; every subprogram capability's
/// examples.md is rendered again in values from the same CALL traces the publish made (work/calltrace/PID/scenario/trace.json)
/// with the same code the publish now uses (MlCpu.Kit.CallExamples); normalization/kit-features.json names the features.
/// Requirements packets, record kits, tests and every other file are copied unchanged.
/// </summary>
public static class KitUpgrade
{
    public static int Run(string from, string traces, string to, int budget, ISet<string>? only, string label, string? runtime = null, string? outlines = null)
    {
        from = Path.GetFullPath(from);
        to = Path.GetFullPath(to);
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("--out must differ from --from (the published kit stays frozen)");
        if (Directory.Exists(to)) Directory.Delete(to, true);
        CopyTree(from, to);
        var features = RefreshRuntime(to, runtime ?? Path.Combine("java-sidecar", "src", "main", "java", "com", "mlcpu", "rt"))
            ? KitFeatures.Current : KitFeatures.Current.Where(f => f != KitFeatures.SubprogramCalls).ToArray();
        // --outlines: the AST outlines of a newer graph build (graphs/ast/PID.outline.txt) replace the published ones; the outline
        // printed PICTURE strings with spaces between their tokens (FNRP0100's -Z,ZZZ,ZZZ,ZZZ,ZZ9.9999 as "- Z, ZZZ, …") until it was fixed.
        if (outlines is not null)
        {
            int replaced = 0;
            foreach (var dir in Directory.EnumerateDirectories(Path.Combine(to, "normalization")))
            {
                var pid = Path.GetFileName(dir);
                var src = Path.Combine(outlines, pid + ".outline.txt");
                var dest = Path.Combine(dir, "ast-outline.txt");
                if (!File.Exists(src) || !File.Exists(dest)) continue;
                var text = File.ReadAllText(src);
                if (File.ReadAllText(dest).Replace("\r\n", "\n") == text.Replace("\r\n", "\n")) continue;
                File.WriteAllText(dest, text);
                replaced++;
            }
            Program.Log($"AST outlines from {Path.GetFullPath(outlines)}: {replaced} replaced");
        }
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(to, "capability-manifest.json")))!;
        int rendered = 0;
        foreach (var c in manifest["capabilities"]!.AsArray().OfType<JsonObject>())
        {
            var pid = c["programId"]!.GetValue<string>();
            if (only is not null && !only.Contains(pid)) continue;
            if (c["kind"]?.GetValue<string>() != "subprogram") { Program.Log($"{pid}: a step; examples.md kept (its records are DISPLAY data)"); continue; }
            var norm = Path.Combine(to, "normalization", pid);
            var layoutsPath = Path.Combine(norm, "record-layouts.json");
            if (!File.Exists(layoutsPath)) { Program.Log($"{pid}: no record-layouts.json; examples.md kept"); continue; }
            var layouts = KitLayouts.Parse(File.ReadAllText(layoutsPath));
            var conditions = Conditions(Path.Combine(norm, "requirements.json"));
            var scenarios = new List<TracedScenario>();
            foreach (var s in c["scenarios"]!.AsArray().Select(x => x!.GetValue<string>()))
            {
                var t = Path.Combine(traces, pid, s, "trace.json");
                if (File.Exists(t)) scenarios.Add(CallExamples.ReadTrace(s, t));
            }
            if (scenarios.Count == 0) { Program.Log($"{pid}: no CALL traces under {traces}; examples.md kept"); continue; }
            var pkgPath = c["package"]!.GetValue<string>().Replace('.', Path.DirectorySeparatorChar);
            var testFile = Directory.EnumerateFiles(Path.Combine(to, "src", "test", "java", pkgPath), "*EquivalenceTest.java").FirstOrDefault();
            var clock = testFile is null ? "" : Regex.Match(File.ReadAllText(testFile), "CLOCK = \"([^\"]*)\"").Groups[1].Value;
            var old = File.ReadAllText(Path.Combine(norm, "examples.md"));
            var text = CallExamples.Render(pid, c["hostProgram"]!.GetValue<string>(), layouts.LinkageNames, c["job"]!.GetValue<string>(),
                c["hostStep"]!.GetValue<string>(), clock, layouts, conditions, scenarios, budget);
            File.WriteAllText(Path.Combine(norm, "examples.md"), text);
            rendered++;
            Program.Log($"{pid}: examples.md in values: {old.Length:N0} -> {text.Length:N0} characters; CALLs shown {Regex.Matches(old, "(?m)^### CALL").Count} -> " +
                        $"{Regex.Matches(text, "(?m)^### CALL").Count}, scenarios {Regex.Matches(old, "(?m)^## Scenario").Count} -> {Regex.Matches(text, "(?m)^## Scenario").Count} " +
                        $"(of {scenarios.Count} traced)");
        }
        File.WriteAllText(Path.Combine(to, "normalization", "kit-features.json"), new JsonObject
        {
            ["schema"] = "mlcpu.kit-features.v1", ["kit"] = label, ["from"] = from, ["generatedAt"] = DateTime.Now.ToString("s"),
            ["features"] = Program.Strings(features),
            ["notes"] = "examples.md of every subprogram rendered in values from the publish's CALL traces; " +
                        (features.Contains(KitFeatures.SubprogramCalls) ? "runtime sources (com/mlcpu/rt) from the current runtime; " : "") +
                        "every other file as published"
        }.ToJsonString(Program.Json) + "\n");
        Program.Log($"kit {label}: {rendered} examples file(s) rendered in values; features {string.Join(", ", features)}; {to}");
        return 0;
    }

    /// <summary>
    /// The kit's runtime sources (src/main/java/com/mlcpu/rt) replaced by the current runtime's, when that runtime has the
    /// subprogram CALL port (<c>ModernProgram.callProgram</c>); true when the kit then has it. The published files a CALL port
    /// needs are only the runtime's, so the rest of the kit stays as published.
    /// </summary>
    static bool RefreshRuntime(string kit, string runtime)
    {
        var target = Path.Combine(kit, "src", "main", "java", "com", "mlcpu", "rt");
        var modern = Path.Combine(runtime, "ModernProgram.java");
        if (!Directory.Exists(target) || !File.Exists(modern) || !File.ReadAllText(modern).Contains("callProgram(", StringComparison.Ordinal))
        {
            Program.Log($"runtime sources kept as published ({(File.Exists(modern) ? "the runtime has no subprogram CALL port" : "no runtime at " + Path.GetFullPath(runtime))})");
            return File.Exists(Path.Combine(target, "ModernProgram.java")) && File.ReadAllText(Path.Combine(target, "ModernProgram.java")).Contains("callProgram(", StringComparison.Ordinal);
        }
        var changed = new List<string>();
        foreach (var f in Directory.EnumerateFiles(runtime, "*.java"))
        {
            // Only files whose content changed are written, in the kit's line ends (a checkout's CRLF is not a change).
            var dest = Path.Combine(target, Path.GetFileName(f));
            var text = File.ReadAllText(f).Replace("\r\n", "\n");
            var old = File.Exists(dest) ? File.ReadAllText(dest) : null;
            if (old is not null && old.Replace("\r\n", "\n") == text) continue;
            changed.Add(Path.GetFileName(f));
            File.WriteAllText(dest, old is not null && old.Contains("\r\n", StringComparison.Ordinal) ? text.Replace("\n", "\r\n") : text);
        }
        Program.Log($"runtime sources from {Path.GetFullPath(runtime)}: {(changed.Count == 0 ? "unchanged" : "changed " + string.Join(", ", changed))}");
        return true;
    }

    /// <summary>Field name → its 88-level names and values, from the packet's dataContracts.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Conditions(string packetPath)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        if (!File.Exists(packetPath)) return map;
        foreach (var r in JsonNode.Parse(File.ReadAllText(packetPath))!["dataContracts"]?.AsArray().OfType<JsonObject>() ?? [])
            foreach (var f in r["fields"]?.AsArray().OfType<JsonObject>() ?? [])
                if (f["conditionNames"] is JsonObject cn && f["name"]?.GetValue<string>() is { } name)
                    map.TryAdd(name, cn.ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)(kv.Value?.AsArray().Select(x => x!.ToString()).ToList() ?? [])));
        return map;
    }

    static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var d in Directory.EnumerateDirectories(from))
        {
            var n = Path.GetFileName(d);
            if (n is "build" or ".gradle" or ".idea") continue;
            CopyTree(d, Path.Combine(to, n));
        }
        foreach (var f in Directory.EnumerateFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)), true);
    }
}
