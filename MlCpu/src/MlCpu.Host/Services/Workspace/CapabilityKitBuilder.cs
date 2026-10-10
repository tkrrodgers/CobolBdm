using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis;
using MlCpu.CobolKb.Knowledge;

namespace MlCpu.Host.Services.Workspace;

/// <summary>A capability the kit prepared: where it runs, its classes and the scenarios its tests replay.</summary>
public sealed record KitCapability(string ProgramId, string Kind, string Job, string StepId, string HostProgram, string Package, string ClassName,
    string TestClass, IReadOnlyList<string> Scenarios, IReadOnlyList<string> Notes);

/// <summary>
/// Everything a model needs to modernize the programs of a generic estate (projects/&lt;id&gt;/capabilities, imported at
/// publish like the authored CardDemo capabilities):
///   capability-manifest.json                 programs, kinds (step | subprogram), packages, entry classes, the platform package
///   src/main/java/com/mlcpu/platform/**      the platform layer (datasets, print files, field codecs, decimals)
///   src/main/java/…/&lt;Cap&gt;.java          a placeholder that runs the faithful program (the model replaces it)
///   src/test/java/…/&lt;Cap&gt;EquivalenceTest the acceptance test: every oracle scenario plus record mutations, normalized vs
///                                            faithful byte for byte, and normalized vs the GnuCOBOL oracle
///   src/test/resources/…/&lt;scenario&gt;/   the step's input datasets and the oracle's outputs, SYSOUT and return code
///   normalization/&lt;PID&gt;/examples.md   worked examples decoded field by field (CALL traces for subprograms)
///   normalization/&lt;PID&gt;/ast-outline.txt the COBOL-85 AST outline
///   normalization/parity-notes.md            COBOL storage and I/O rules that decide byte parity
/// </summary>
public sealed class CapabilityKitBuilder(ProjectService projects, GraphService graphs, ConfigService config)
{
    public const string PlatformPackage = "com.mlcpu.platform";
    static readonly Encoding Latin1 = Encoding.Latin1;
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public async Task<IReadOnlyList<KitCapability>> BuildAsync(ProjectDefinition p, Operation op, CancellationToken ct)
    {
        var paths = projects.Paths(p);
        var root = paths.Capabilities;
        var src = projects.SourceDir(p);
        var sources = OnboardingService.Sources(p, src);
        // Regenerated each publish; evidence and approvals (human decisions) survive.
        foreach (var d in new[] { "src", "normalization" }.Select(d => Path.Combine(root, d)).Where(Directory.Exists)) Toolchain.DeleteTree(d);
        Directory.CreateDirectory(root);
        var platformSrc = Path.Combine(config.Settings.RepoRoot, "capabilities", "platform");
        Toolchain.CopyTree(Path.Combine(platformSrc, "src"), Path.Combine(root, "src"));
        Directory.CreateDirectory(Path.Combine(root, "normalization"));
        File.Copy(Path.Combine(platformSrc, "parity-notes.md"), Path.Combine(root, "normalization", "parity-notes.md"), true);

        var runs = OracleRuns(paths);
        if (!runs.TryGetValue("base", out var baseRun)) throw new InvalidOperationException("no oracle run: build and run the GnuCOBOL oracle first (Settings)");
        var jobs = OnboardingService.Harvest(p, src);
        var models = new Dictionary<string, CobolProgram>(StringComparer.OrdinalIgnoreCase);
        CobolProgram Model(string pid) => models.TryGetValue(pid, out var m) ? m : models[pid] = CobolModelBuilder.BuildGroundedFull(sources[pid]);
        var kit = new List<KitCapability>();
        var manifest = new JsonArray();
        foreach (var pid in p.Publish.Capabilities.Where(sources.ContainsKey))
        {
            ct.ThrowIfCancellationRequested();
            var entry = p.Programs.First(x => x.Id.Equals(pid, StringComparison.OrdinalIgnoreCase));
            var host = HostStep(baseRun, pid, sources, models);
            if (host is null) { op.Info($"{pid}: no oracle step runs or calls it; skipped"); continue; }
            var kind = host["program"]!.GetValue<string>().Equals(pid, StringComparison.OrdinalIgnoreCase) ? "step" : "subprogram";
            var job = jobs.First(j => j.Name.Equals(host["job"]!.GetValue<string>(), StringComparison.OrdinalIgnoreCase));
            var stepId = host["stepId"]!.GetValue<string>();
            var jclStep = JobSteps.Ids(job).First(x => x.Id == stepId).Step;
            var hostProgram = host["program"]!.GetValue<string>().ToUpperInvariant();
            var pkgName = Regex.Replace(pid.ToLowerInvariant(), "[^a-z0-9]", "");
            if (char.IsDigit(pkgName[0])) pkgName = "p" + pkgName;
            var package = "com.mlcpu.capabilities." + pkgName;
            var className = Pascal(pid) + (kind == "step" ? "Step" : "Program");
            var testClass = Pascal(pid) + "EquivalenceTest";
            var pkgPath = package.Replace('.', '/');
            var notes = new List<string>();

            // Scenarios: every oracle run in which the host step ran.
            var scenarios = new List<string>();
            var resRoot = Path.Combine(root, "src", "test", "resources", pkgPath);
            foreach (var (scenario, run) in runs.OrderBy(r => r.Key == "base" ? 0 : 1).ThenBy(r => r.Key, StringComparer.Ordinal))
            {
                var stepDir = Path.Combine(paths.OracleRuns, scenario, "steps", $"{job.Name}.{stepId}");
                if (!Directory.Exists(Path.Combine(stepDir, "in"))) continue;
                Toolchain.CopyTree(Path.Combine(stepDir, "in"), Path.Combine(resRoot, scenario, "in"));
                if (Directory.Exists(Path.Combine(stepDir, "out"))) Toolchain.CopyTree(Path.Combine(stepDir, "out"), Path.Combine(resRoot, scenario, "oracle"));
                var rec = StepRecord(run, job.Name, stepId);
                File.WriteAllText(Path.Combine(resRoot, scenario, "oracle", "RC.txt"), (rec?["returnCode"]?.GetValue<int>() ?? 0).ToString());
                scenarios.Add(scenario);
            }

            // Datasets the step reads (inputs) and writes (outputs), with their record formats.
            var plan = DdBindingPlanner.Plan(job, sources, DateTime.Now.ToString("yyyy-MM-dd"), out var planModels);
            foreach (var (k, v) in planModels) models.TryAdd(k, v);
            var bindings = plan.Steps.First(s => s.Order == jclStep.Order).Bindings.Where(b => b.ResolvedDsn is not null && !b.IsDummy).ToList();
            var inputs = bindings.Where(b => b.Direction.Contains("input") || b.Direction.Contains("i-o") || b.Direction.Contains("extend"))
                .Select(b => (b.Dd, Dsn: b.ResolvedDsn!, Lrecl: RecordFormat(b, models))).DistinctBy(x => x.Dsn).ToList();
            var outputs = bindings.Where(b => b.Direction.Contains("output") || b.Direction.Contains("i-o") || b.Direction.Contains("extend"))
                .Select(b => b.ResolvedDsn!).Distinct().ToList();

            var mainDir = Path.Combine(root, "src", "main", "java", pkgPath);
            var testDir = Path.Combine(root, "src", "test", "java", pkgPath);
            Directory.CreateDirectory(mainDir);
            Directory.CreateDirectory(testDir);
            File.WriteAllText(Path.Combine(mainDir, className + ".java"), Placeholder(package, className, kind, pid, hostProgram));
            File.WriteAllText(Path.Combine(testDir, testClass + ".java"),
                Test(package, className, testClass, kind, pid, hostProgram, job.Name, stepId, jclStep, inputs, outputs, scenarios, p.Oracle.Clock,
                    bindings.GroupBy(b => b.ResolvedDsn!).Select(g => (g.Key, Fmt: FormatName(g.First(), models))).Where(x => x.Fmt is not null)
                        .ToDictionary(x => x.Key, x => x.Fmt!)));

            // The model's kit: AST outline and worked examples.
            var normDir = Path.Combine(root, "normalization", pid);
            Directory.CreateDirectory(normDir);
            if (graphs.Outline(p, pid) is { } outline) File.WriteAllText(Path.Combine(normDir, "ast-outline.txt"), outline);
            else notes.Add("no AST outline: rebuild the graphs");
            string examples;
            try
            {
                examples = kind == "step"
                    ? await StepExamplesAsync(p, paths, pid, job.Name, stepId, jclStep, bindings, models, runs, scenarios, op, ct)
                    : await CallExamplesAsync(p, paths, pid, hostProgram, job.Name, stepId, jclStep, Model(pid), scenarios, op, ct);
            }
            catch (Exception e)
            {
                examples = $"# Worked examples: {pid}\n\n(not available: {e.Message})\n";
                notes.Add("examples failed: " + e.Message);
            }
            File.WriteAllText(Path.Combine(normDir, "examples.md"), examples);

            manifest.Add(new JsonObject
            {
                ["programId"] = pid, ["kind"] = kind, ["job"] = job.Name, ["hostStep"] = stepId, ["hostProgram"] = hostProgram, ["package"] = package,
                ["step"] = package + "." + className, ["classes"] = new JsonArray(className), ["acceptanceTest"] = package + "." + testClass,
                ["scenarios"] = new JsonArray(scenarios.Select(s => (JsonNode)s).ToArray()),
                ["requirements"] = $"normalization/{pid}/requirements.json", ["design"] = $"normalization/{pid}/design.json",
                ["conformance"] = $"normalization/{pid}/design-conformance.json", ["evidence"] = $"evidence/{pid}.evidence.json",
                ["approval"] = $"approvals/{pid}.approval.json", ["programKind"] = entry.Kind
            });
            kit.Add(new KitCapability(pid, kind, job.Name, stepId, hostProgram, package, className, testClass, scenarios, notes));
            op.Info($"capability {pid}: {kind} in {job.Name}.{stepId} ({hostProgram}), {scenarios.Count} oracle scenario(s), {inputs.Count} input dataset(s)");
        }
        File.WriteAllText(Path.Combine(root, "capability-manifest.json"), new JsonObject
        {
            ["schema"] = "mlcpu.capability-manifest.v1", ["estate"] = p.Id, ["generatedAt"] = DateTime.Now.ToString("s"),
            ["platformPackage"] = PlatformPackage,
            ["sources"] = new JsonObject { ["main"] = "src/main/java", ["test"] = "src/test/java", ["testResources"] = "src/test/resources" },
            ["sharedPackages"] = new JsonArray(PlatformPackage),
            ["capabilities"] = manifest
        }.ToJsonString(Indented) + "\n");
        return kit;
    }

    // ------------------------------------------------------------------ oracle runs

    static Dictionary<string, JsonObject> OracleRuns(ProjectPaths paths)
    {
        var runs = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (!Directory.Exists(paths.OracleRuns)) return runs;
        foreach (var dir in Directory.EnumerateDirectories(paths.OracleRuns))
        {
            var f = Path.Combine(dir, "oracle-run.json");
            if (File.Exists(f) && JsonNode.Parse(File.ReadAllText(f)) is JsonObject o) runs[Path.GetFileName(dir)] = o;
        }
        return runs;
    }

    static JsonObject? StepRecord(JsonObject run, string job, string stepId) =>
        run["steps"]?.AsArray().OfType<JsonObject>().FirstOrDefault(s => s["job"]?.GetValue<string>() == job && s["stepId"]?.GetValue<string>() == stepId);

    /// <summary>The first oracle step that runs the program, or (for a subprogram) whose program's CALL closure reaches it.</summary>
    static JsonObject? HostStep(JsonObject run, string pid, Dictionary<string, string> sources, Dictionary<string, CobolProgram> models)
    {
        var steps = run["steps"]?.AsArray().OfType<JsonObject>().Where(s => s["program"]?.GetValue<string>() is not null).ToList() ?? [];
        var direct = steps.FirstOrDefault(s => s["program"]!.GetValue<string>().Equals(pid, StringComparison.OrdinalIgnoreCase));
        if (direct is not null) return direct;
        foreach (var s in steps)
        {
            var main = s["program"]!.GetValue<string>();
            if (!sources.ContainsKey(main)) continue;
            var closure = DdBindingPlanner.CallClosure(main, sources, [], models);
            if (closure.Contains(pid, StringComparer.OrdinalIgnoreCase)) return s;
        }
        return null;
    }

    /// <summary>Record length for record-level mutations: the FD's fixed length, or 0 for variable-length (RDW) files.</summary>
    static int RecordFormat(DdBinding b, Dictionary<string, CobolProgram> models)
    {
        if (!models.TryGetValue(b.OwnerProgram, out var m)) return -1;
        var fd = m.Files.FirstOrDefault(f => f.Name == b.LogicalFile);
        if (fd is null) return -1;
        if (fd.Organization.Contains("INDEXED") || fd.Organization.Contains("LINE")) return -1;
        bool variable = string.Equals(fd.RecordingMode, "V", StringComparison.OrdinalIgnoreCase) || fd.Records.Select(r => r.Length).Distinct().Count() > 1
                        || (fd.MinRecord > 0 && fd.MinRecord != fd.MaxRecord);
        return variable ? 0 : fd.MaxRecord > 0 ? fd.MaxRecord : fd.Records.Select(r => r.Length).DefaultIfEmpty(0).Max();
    }

    /// <summary>fixed:LRECL, variable, or print:LRECL (the program writes the file with ADVANCING); null for LINE SEQUENTIAL or unknown.</summary>
    static string? FormatName(DdBinding b, Dictionary<string, CobolProgram> models)
    {
        if (!models.TryGetValue(b.OwnerProgram, out var m) || m.Files.FirstOrDefault(f => f.Name == b.LogicalFile) is not { } fd) return null;
        if (fd.Organization.Contains("LINE")) return null;
        int lrecl = fd.MaxRecord > 0 ? fd.MaxRecord : fd.Records.Select(r => r.Length).DefaultIfEmpty(0).Max();
        if (lrecl <= 0) return null;
        bool variable = !fd.Organization.Contains("INDEXED") && (string.Equals(fd.RecordingMode, "V", StringComparison.OrdinalIgnoreCase)
                        || fd.Records.Select(r => r.Length).Distinct().Count() > 1 || (fd.MinRecord > 0 && fd.MinRecord != fd.MaxRecord));
        if (variable) return "variable";
        bool print = GraphService.Statements(m).Any(s => s.Stmt.Verb == "WRITE" && s.Stmt.Str("advWhen") is not null && RecordFile(m, s.Stmt) == fd.Name);
        return (print ? "print:" : "fixed:") + lrecl;
    }

    // ------------------------------------------------------------------ Java sources

    static string Pascal(string pid)
    {
        var parts = Regex.Split(pid.ToLowerInvariant(), "[^a-z0-9]+").Where(x => x.Length > 0);
        var s = string.Concat(parts.Select(x => char.ToUpperInvariant(x[0]) + x[1..]));
        return char.IsDigit(s[0]) ? "P" + s : s;
    }

    static string Placeholder(string package, string className, string kind, string pid, string host) => kind == "step"
        ? $$"""
            package {{package}};

            import com.mlcpu.rt.JobEngine;
            import com.mlcpu.rt.ModernStep;

            /**
             * {{pid}}: placeholder until the capability is modernized. It runs the faithful translation (com.mlcpu.core.Prog_{{pid}})
             * with the step's datasets, PARM and SYSOUT, so the normalized path behaves exactly like the legacy one. A model's
             * implementation (normalization/{{pid}}/design.json) replaces this class.
             */
            public final class {{className}} implements ModernStep {
                @Override
                public int run(Context ctx) throws Exception {
                    return JobEngine.runFaithful(ctx);
                }
            }

            """
        : $$"""
            package {{package}};

            import com.mlcpu.rt.ModernProgram;
            import com.mlcpu.rt.ProgramRegistry;

            /**
             * {{pid}} (CALLed by {{host}}): placeholder until the capability is modernized. It runs the faithful translation
             * (com.mlcpu.core.Prog_{{pid}}) on the caller's arguments. A model's implementation replaces this class.
             */
            public final class {{className}} implements ModernProgram {
                @Override
                public void call(byte[][] parameters) {
                    ProgramRegistry.callFaithful("{{pid}}", parameters);
                }
            }

            """;

    static string Test(string package, string className, string testClass, string kind, string pid, string host, string job, string stepId, JclStep step,
        List<(string Dd, string Dsn, int Lrecl)> inputs, List<string> outputs, List<string> scenarios, string clock, IReadOnlyDictionary<string, string> formats)
    {
        var sb = new StringBuilder();
        string Lit(string? s) => s is null ? "null" : "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n") + "\"";
        var dds = step.Datasets.Select(d =>
            $"new Dd({Lit(d.Dd)}, {Lit(d.Dsn)}, {Lit(d.Member)}, {Lit(d.Generation)}, {(d.Dummy ? "true" : "false")}, {(d.Sysout ? "true" : "false")}, " +
            $"{Lit(d.InStream is null ? null : d.InStream + "\n")}, {(int.TryParse(d.Lrecl, out var n) ? n.ToString() : "null")})");
        var inputNames = inputs.Select(i => i.Dsn).Concat(outputs).Distinct().ToList();
        var normalizedRun = kind == "step"
            ? "StepEquivalence.run(step(), inputs, \"normalized\")"
            : $"StepEquivalence.run(step(), inputs, \"normalized\", Map.of({Lit(pid)}, {className}::new))";
        var compare = kind == "step"
            ? "StepEquivalence.compare(step(), inputs)"
            : $"StepEquivalence.compareCall(step(), {Lit(pid)}, {className}::new, inputs)";
        var subject = kind == "step" ? $"the normalized {className}" : $"the normalized {className} (answering every CALL '{pid}' made by the faithful {host})";
        sb.Append($$"""
            package {{package}};

            import static org.junit.jupiter.api.Assertions.assertEquals;

            import com.mlcpu.rt.JobEngine;
            import com.mlcpu.rt.JobEngine.Dd;
            import com.mlcpu.rt.StepEquivalence;
            import java.io.IOException;
            import java.nio.charset.StandardCharsets;
            import java.util.ArrayList;
            import java.util.List;
            import java.util.Map;
            import java.util.TreeMap;
            import java.util.function.UnaryOperator;
            import java.util.stream.Stream;
            import org.junit.jupiter.api.BeforeAll;
            import org.junit.jupiter.api.Test;
            import org.junit.jupiter.params.ParameterizedTest;
            import org.junit.jupiter.params.provider.Arguments;
            import org.junit.jupiter.params.provider.MethodSource;

            /**
             * {{pid}} acceptance test (job {{job}}, step {{stepId}}): {{subject}} against the faithful translation in every
             * scenario (all datasets, SYSOUT and the return code byte for byte), and against the GnuCOBOL oracle's outputs.
             * Scenarios: the oracle runs ({{string.Join(", ", scenarios)}}) plus record-level variations of each input dataset.
             * Generated at publish from the oracle; do not edit.
             */
            class {{testClass}} {
                static final String CLOCK = {{Lit(clock)}};
                static final List<String> ORACLE_SCENARIOS = List.of({{string.Join(", ", scenarios.Select(Lit))}});
                /** Every dataset the step reads or writes, by DSN. */
                static final String[] DATASETS = { {{string.Join(", ", inputNames.Select(Lit))}} };
                /** Record format of each dataset (fixed:LRECL, variable = 4-byte descriptors, print:LRECL = fixed records with ADVANCING bytes). */
                static final Map<String, String> RECORD_FORMATS = Map.ofEntries({{string.Join(", ", formats.Select(kv => $"Map.entry({Lit(kv.Key)}, {Lit(kv.Value)})"))}});

                @BeforeAll
                static void oracleEnvironment() {
                    StepEquivalence.pinOracleEnvironment(CLOCK);
                }

                static JobEngine.Step step() {
                    return new JobEngine.Step({{Lit(stepId)}}, {{Lit(host)}}, {{Lit(step.Parm)}}, null, com.mlcpu.core.Prog_{{host}}::new, {{(kind == "step" ? className + "::new" : "null")}},
                            List.of({{string.Join(",\n                        ", dds)}}));
                }

            {{(kind == "subprogram" ? $"    /** The normalized subprograms in play (the CALL targets the normalized run uses). */\n    static Map<String, java.util.function.Supplier<com.mlcpu.rt.ModernProgram>> subprograms() {{\n        return Map.of({Lit(pid)}, {className}::new);\n    }}\n\n" : "")}}    /** The step's datasets in an oracle scenario (DSN → image), as they were when the step started. */
                static Map<String, byte[]> inputs(String scenario) throws IOException {
                    return StepEquivalence.optionalResources({{testClass}}.class, scenario + "/in", "", DATASETS);
                }

                static Map<String, byte[]> with(Map<String, byte[]> in, String dsn, UnaryOperator<byte[]> change) {
                    Map<String, byte[]> out = new TreeMap<>(in);
                    if (out.containsKey(dsn)) out.put(dsn, change.apply(out.get(dsn)));
                    return out;
                }

                static Stream<Arguments> scenarios() throws IOException {
                    List<Arguments> a = new ArrayList<>();
                    for (String s : ORACLE_SCENARIOS) a.add(Arguments.of("oracle scenario " + s, inputs(s)));
                    Map<String, byte[]> base = inputs(ORACLE_SCENARIOS.get(0));

            """);
        foreach (var (dd, dsn, lrecl) in inputs.Where(i => i.Lrecl >= 0))
        {
            sb.Append($"        a.add(Arguments.of(\"{dd}: empty\", with(base, {Lit(dsn)}, f -> new byte[0])));\n");
            sb.Append($"        a.add(Arguments.of(\"{dd}: first record only\", with(base, {Lit(dsn)}, f -> StepEquivalence.firstRecords(f, {lrecl}, 1))));\n");
            sb.Append($"        a.add(Arguments.of(\"{dd}: records in reverse order\", with(base, {Lit(dsn)}, f -> StepEquivalence.reverseRecords(f, {lrecl}))));\n");
            sb.Append($"        a.add(Arguments.of(\"{dd}: every record twice\", with(base, {Lit(dsn)}, f -> StepEquivalence.duplicateRecords(f, {lrecl}))));\n");
        }
        var sysoutName = $"{stepId}.SYSOUT.txt";
        var sysoutDd = step.Datasets.FirstOrDefault(d => d.Dd == "SYSOUT" && d.Sysout) is not null ? $"{stepId}.SYSOUT.txt" : sysoutName;
        sb.Append($$"""
                    return a.stream();
                }

                @ParameterizedTest(name = "{0}")
                @MethodSource("scenarios")
                void normalizedEqualsFaithful(String scenario, Map<String, byte[]> inputs) throws IOException {
                    assertEquals(List.of(), {{compare}}, scenario);
                }

                @Test
                void normalizedEqualsFaithfulWithSpaceStorageDefault() throws IOException {
                    byte saved = com.mlcpu.rt.CobConfig.DEFAULT_BYTE;
                    com.mlcpu.rt.CobConfig.DEFAULT_BYTE = ' ';
                    try {
                        Map<String, byte[]> inputs = inputs(ORACLE_SCENARIOS.get(0));
                        assertEquals(List.of(), {{compare}});
                    } finally {
                        com.mlcpu.rt.CobConfig.DEFAULT_BYTE = saved;
                    }
                }

                static Stream<String> oracleScenarios() {
                    return ORACLE_SCENARIOS.stream();
                }

                @ParameterizedTest(name = "GnuCOBOL oracle: {0}")
                @MethodSource("oracleScenarios")
                void normalizedEqualsGnuCobolOracle(String scenario) throws IOException {
                    Map<String, byte[]> expected = new TreeMap<>();
                    for (var e : StepEquivalence.optionalResources(getClass(), scenario + "/oracle", "", {{string.Join(", ", outputs.Select(Lit).Append("\"SYSOUT.txt\""))}}).entrySet())
                        expected.put(e.getKey().equals("SYSOUT.txt") ? {{Lit("sysout/" + sysoutDd)}} : "data/" + e.getKey(), e.getValue());
                    Map<String, byte[]> inputs = inputs(scenario);
                    var actual = {{normalizedRun}};
                    assertEquals(List.of(), StepEquivalence.againstOracle(actual, expected), scenario);
                    var rc = StepEquivalence.optionalResources(getClass(), scenario + "/oracle", "", "RC.txt").get("RC.txt");
                    if (rc != null) assertEquals(Integer.parseInt(new String(rc, StandardCharsets.US_ASCII).trim()), actual.returnCode(), scenario + ": return code");
                }
            }

            """);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ worked examples

    const string Legend = "Raw bytes are shown between « and » (ISO-8859-1; \\x00 is byte 0, \\n a line feed x'0A', \\f a form feed x'0C'). " +
                          "Zoned decimals carry the sign overpunched in the last byte ({ A-I positive, } J-R negative); unsigned ones are plain digits.";

    async Task<string> StepExamplesAsync(ProjectDefinition p, ProjectPaths paths, string pid, string job, string stepId, JclStep step, List<DdBinding> bindings,
        Dictionary<string, CobolProgram> models, Dictionary<string, JsonObject> runs, List<string> scenarios, Operation op, CancellationToken ct)
    {
        var sb = new StringBuilder($"# Worked examples: {pid} (traced from the GnuCOBOL oracle)\n\n");
        sb.Append($"The original program, compiled with GnuCOBOL (-std=ibm -fsign=EBCDIC) and run as step {stepId} of job {job} with the batch clock pinned at ");
        sb.Append($"{p.Oracle.Clock} and storage default x'00'. These runs are acceptance scenarios: your implementation must reproduce every byte shown, ");
        sb.Append("including the output datasets' unwritten bytes, and the SYSOUT lines and return code. Each scenario lists the input datasets, then ");
        sb.Append("the order of effects (every READ, WRITE with its ADVANCING, REWRITE, DELETE and DISPLAY the program executed, in order), then the ");
        sb.Append("outputs.\n\n").Append(Legend).Append("\n\n");
        int budget = 70_000;
        foreach (var scenario in scenarios)
        {
            var stepDir = Path.Combine(paths.OracleRuns, scenario, "steps", $"{job}.{stepId}");
            var rec = StepRecord(runs[scenario], job, stepId);
            sb.Append($"## Scenario {scenario}\n\n### Inputs (datasets when the step starts)\n\n");
            foreach (var b in bindings.Where(b => b.Direction.Contains("input") || b.Direction.Contains("i-o")).DistinctBy(b => b.ResolvedDsn))
                Dataset(sb, b, Path.Combine(stepDir, "in", b.ResolvedDsn!), models, budget / 5);
            try { await OrderOfEffectsAsync(sb, p, paths, pid, step, Path.Combine(stepDir, "in"), scenario, ct); }
            catch (Exception e) { op.Info($"{pid}: I/O trace of scenario {scenario} not available: {e.Message}"); }
            sb.Append("### Outputs (as the step leaves them)\n\n");
            foreach (var b in bindings.Where(b => b.Direction.Contains("output") || b.Direction.Contains("i-o") || b.Direction.Contains("extend")).DistinctBy(b => b.ResolvedDsn))
                Dataset(sb, b, Path.Combine(stepDir, "out", b.ResolvedDsn!), models, budget / 5);
            var sysout = Path.Combine(stepDir, "out", "SYSOUT.txt");
            sb.Append("### SYSOUT\n\n```\n").Append(File.Exists(sysout) ? File.ReadAllText(sysout, Latin1).Replace("\r\n", "\n").TrimEnd('\n') : "(empty)").Append("\n```\n\n");
            sb.Append($"Return code: {rec?["returnCode"]?.GetValue<int>() ?? 0}\n\n");
            if (sb.Length > budget) { sb.Append("(further scenarios omitted for length)\n"); break; }
        }
        return sb.ToString();
    }

    /// <summary>The faithful translation (which matches the oracle byte for byte) run with com.mlcpu.rt.IoTrace: every I/O statement and DISPLAY in order.</summary>
    async Task OrderOfEffectsAsync(StringBuilder sb, ProjectDefinition p, ProjectPaths paths, string pid, JclStep step, string inDir, string scenario, CancellationToken ct)
    {
        var classes = Path.Combine(paths.Transpile, "classes");
        if (!Directory.Exists(classes)) throw new InvalidOperationException("no compiled translation (transpile first)");
        var work = Path.Combine(paths.Dir, "work", "iotrace", pid, scenario);
        if (Directory.Exists(work)) Toolchain.DeleteTree(work);
        Directory.CreateDirectory(work);
        var args = new List<string> { "-cp", $"{RuntimeClasses()};{classes}", "com.mlcpu.rt.IoTrace", "--program", step.Program!.ToUpperInvariant(),
            "--clock", "FIXED=" + p.Oracle.Clock, "--out", Path.Combine(work, "io.json"), "--max", "300" };
        if (step.Parm is not null) { args.Add("--parm"); args.Add(step.Parm); }
        foreach (var d in step.Datasets.Where(d => d.Dd.Length > 0))
        {
            var target = Path.Combine(work, GnuCobolOracle.Safe(d.Dd) + ".dat");
            if (d.Dsn is not null && File.Exists(Path.Combine(inDir, d.Dsn))) File.Copy(Path.Combine(inDir, d.Dsn), target, true);
            args.Add("--dd");
            args.Add($"{d.Dd}={target}");
        }
        var env = new Dictionary<string, string> { ["MLCPU_DISPLAY_NUMERIC"] = "gnucobol", ["MLCPU_BINARY_TRUNCATE"] = "no", ["MLCPU_DEFAULT_BYTE"] = "0" };
        var r = await Toolchain.RunAsync(Toolchain.Java, args, work, env: env, timeout: TimeSpan.FromMinutes(5), ct: ct);
        if (r.ExitCode != 0 || !File.Exists(Path.Combine(work, "io.json"))) throw new InvalidOperationException(r.Output.Trim());
        var trace = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "io.json")))!;
        var events = trace["trace"]!.AsArray().OfType<JsonObject>().ToList();
        sb.Append($"### Order of effects ({trace["events"]} statement(s){(events.Count < trace["events"]!.GetValue<int>() ? $", first {events.Count} shown" : "")})\n\n");
        int n = 0;
        foreach (var e in events)
        {
            n++;
            var opName = e["op"]!.GetValue<string>();
            var dd = e["dd"]?.GetValue<string>() ?? "";
            var status = e["status"]!.GetValue<int>();
            var data = Latin1.GetBytes(e["data"]!.GetValue<string>());
            var adv = e["advancing"]?.GetValue<string>();
            string shown = data.Length <= 140 ? Escape(data) : Escape(data[..100]) + "…";
            if (opName == "DISPLAY") { sb.Append($"{n}. DISPLAY «{Escape(data)}»\n"); continue; }
            if (opName.StartsWith("READ") && status == 10) { sb.Append($"{n}. {opName} {dd}: AT END (status 10)\n"); continue; }
            sb.Append($"{n}. {opName} {dd}{(adv is null ? "" : " " + adv)}{(status == 0 ? "" : $" (status {status:00})")}: «{shown}» ({data.Length} bytes)\n");
        }
        sb.Append('\n');
    }

    static void Dataset(StringBuilder sb, DdBinding b, string file, Dictionary<string, CobolProgram> models, int budget)
    {
        var dsn = b.ResolvedDsn!;
        if (!File.Exists(file)) { sb.Append($"#### {b.Dd} = {dsn}: not present\n\n"); return; }
        var bytes = File.ReadAllBytes(file);
        var m = models.GetValueOrDefault(b.OwnerProgram);
        var fd = m?.Files.FirstOrDefault(f => f.Name == b.LogicalFile);
        bool print = m is not null && fd is not null && GraphService.Statements(m).Any(s => s.Stmt.Verb == "WRITE" && s.Stmt.Str("advWhen") is not null
                                                       && RecordFile(m, s.Stmt) == fd.Name);
        int start = sb.Length;
        if (print || fd is null)
        {
            sb.Append($"#### {b.Dd} = {dsn} ({(print ? "print file written with ADVANCING" : "raw")}, {bytes.Length} bytes)\n\n```\n");
            sb.Append(Escape(bytes, keepNewlines: true)).Append("\n```\n\n");
            return;
        }
        bool variable = string.Equals(fd.RecordingMode, "V", StringComparison.OrdinalIgnoreCase) || fd.Records.Select(r => r.Length).Distinct().Count() > 1;
        int lrecl = fd.MaxRecord > 0 ? fd.MaxRecord : fd.Records.Select(r => r.Length).DefaultIfEmpty(1).Max();
        var records = new List<byte[]>();
        if (variable)
        {
            int at = 0;
            while (at + 4 <= bytes.Length) { int len = (bytes[at] << 8) | bytes[at + 1]; records.Add(bytes[(at + 4)..Math.Min(bytes.Length, at + 4 + len)]); at += 4 + len; }
        }
        else for (int i = 0; i * lrecl < bytes.Length; i++) records.Add(bytes[(i * lrecl)..Math.Min(bytes.Length, (i + 1) * lrecl)]);
        sb.Append($"#### {b.Dd} = {dsn} ({(variable ? "variable-length records, 4-byte descriptor each" : $"fixed {lrecl}-byte records")}, {records.Count} record(s))\n\n");
        for (int i = 0; i < records.Count; i++)
        {
            if (sb.Length - start > budget) { sb.Append($"- … {records.Count - i} more record(s)\n"); break; }
            var r = records[i];
            var layout = fd.Records.FirstOrDefault(x => x.Length == r.Length) ?? fd.Records.FirstOrDefault();
            sb.Append($"- Record {i + 1} ({r.Length} bytes{(layout is null ? "" : ", " + layout.Name)}): ");
            if (layout is null) { sb.Append('«').Append(Escape(r)).Append("»\n"); continue; }
            sb.Append(string.Join(", ", ReplayPlanBuilder.Layout(layout).Where(f => f.Offset < r.Length).Select(f => Field(f, r)))).Append('\n');
        }
        sb.Append('\n');
    }

    static string? RecordFile(CobolProgram m, Stmt s)
    {
        if (s.Get("record")?.Id is not { } id) return null;
        var item = m.Items.FirstOrDefault(i => i.Id == id);
        return item?.FileName ?? item?.Record?.FileName;
    }

    static string Field(ReplayPlanBuilder.LayoutField f, byte[] r)
    {
        var len = Math.Min(f.Length, r.Length - f.Offset);
        var raw = r[f.Offset..(f.Offset + len)];
        var s = $"{f.Name} «{Escape(raw)}»";
        // A zoned value: digits, the last byte possibly overpunched with the sign (only when the field is signed). Anything else is
        // shown raw only: decoding letters elsewhere would invent a number the program never sees.
        bool digits = raw.Length > 0 && raw[..^1].All(c => c is >= (byte)'0' and <= (byte)'9');
        byte last = raw.Length > 0 ? raw[^1] : (byte)0;
        bool lastOk = last is >= (byte)'0' and <= (byte)'9' || (f.Signed && (last is (byte)'{' or (byte)'}' || last is >= (byte)'A' and <= (byte)'R'));
        if (f.Numeric && f.Usage == "DISPLAY" && digits && lastOk) s += " = " + Zoned(raw, f.Scale);
        else if (f.Numeric && f.Usage == "DISPLAY") s += " (not numeric)";
        return s;
    }

    static string Zoned(byte[] raw, int scale)
    {
        var digits = new StringBuilder();
        bool negative = false;
        for (int i = 0; i < raw.Length; i++)
        {
            char c = (char)raw[i];
            if (c is >= '0' and <= '9') digits.Append(c);
            else if (c == '{') digits.Append('0');
            else if (c is >= 'A' and <= 'I') digits.Append((char)('1' + c - 'A'));
            else if (c == '}') { digits.Append('0'); negative = true; }
            else if (c is >= 'J' and <= 'R') { digits.Append((char)('1' + c - 'J')); negative = true; }
        }
        var v = decimal.Parse(digits.Length == 0 ? "0" : digits.ToString()) / (decimal)Math.Pow(10, scale);
        return (negative ? -v : v).ToString(scale > 0 ? "0." + new string('0', scale) : "0", System.Globalization.CultureInfo.InvariantCulture);
    }

    static string Escape(byte[] b, bool keepNewlines = false)
    {
        var sb = new StringBuilder(b.Length);
        foreach (var x in b)
        {
            if (x == '\n') sb.Append(keepNewlines ? "\\n\n" : "\\n");
            else if (x == '\f') sb.Append("\\f");
            else if (x is >= 0x20 and < 0x7F) sb.Append((char)x);
            else sb.Append($"\\x{x:x2}");
        }
        return sb.ToString();
    }

    /// <summary>A subprogram's examples: every CALL the faithful caller makes in each oracle scenario, arguments before and after.</summary>
    async Task<string> CallExamplesAsync(ProjectDefinition p, ProjectPaths paths, string pid, string host, string job, string stepId, JclStep step,
        CobolProgram sub, IReadOnlyList<string> scenarios, Operation op, CancellationToken ct)
    {
        var classes = Path.Combine(paths.Transpile, "classes");
        if (!Directory.Exists(classes) || !Directory.EnumerateFiles(classes, "*.class", SearchOption.AllDirectories).Any())
            throw new InvalidOperationException("no compiled translation (transpile first)");
        var linkage = sub.Using.Select(u => sub.Items.FirstOrDefault(i => i.Name.Equals(u, StringComparison.OrdinalIgnoreCase) && i.Level is 1 or 77)).ToList();
        var sb = new StringBuilder($"# Worked examples: {pid} (every CALL in the oracle scenarios)\n\n");
        sb.Append($"{pid} is a subprogram: it runs only when {host} executes `CALL ... USING {string.Join(" ", sub.Using)}` (step {stepId} of job {job}, ");
        sb.Append($"clock {p.Oracle.Clock}). These are the faithful {pid}'s CALLs in each oracle scenario, traced from the translation that matches the ");
        sb.Append("GnuCOBOL oracle byte for byte: each argument as the caller passed it, and the fields that differ when the CALL returns. Your ");
        sb.Append("implementation gets the arguments as byte arrays in this order and must leave exactly these bytes in them.\n\n").Append(Legend).Append("\n\n");
        int traced = 0;
        foreach (var scenario in scenarios)
        {
            var work = Path.Combine(paths.Dir, "work", "calltrace", pid, scenario);
            if (Directory.Exists(work)) Toolchain.DeleteTree(work);
            Directory.CreateDirectory(work);
            var inDir = Path.Combine(paths.OracleRuns, scenario, "steps", $"{job}.{stepId}", "in");
            var args = new List<string> { "-cp", $"{RuntimeClasses()};{classes}", "com.mlcpu.rt.CallTrace", "--program", host, "--trace", pid,
                "--clock", "FIXED=" + p.Oracle.Clock, "--out", Path.Combine(work, "trace.json"), "--max", "40" };
            if (step.Parm is not null) { args.Add("--parm"); args.Add(step.Parm); }
            foreach (var d in step.Datasets.Where(d => d.Dd.Length > 0))
            {
                var target = Path.Combine(work, GnuCobolOracle.Safe(d.Dd) + ".dat");
                if (d.Dsn is not null && File.Exists(Path.Combine(inDir, d.Dsn))) File.Copy(Path.Combine(inDir, d.Dsn), target, true);
                args.Add("--dd");
                args.Add($"{d.Dd}={target}");
            }
            var env = new Dictionary<string, string> { ["MLCPU_DISPLAY_NUMERIC"] = "gnucobol", ["MLCPU_BINARY_TRUNCATE"] = "no", ["MLCPU_DEFAULT_BYTE"] = "0" };
            var r = await Toolchain.RunAsync(Toolchain.Java, args, work, env: env, timeout: TimeSpan.FromMinutes(5), ct: ct);
            if (r.ExitCode != 0 || !File.Exists(Path.Combine(work, "trace.json"))) throw new InvalidOperationException("call trace failed: " + r.Output.Trim());
            var trace = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "trace.json")))!;
            sb.Append($"## Scenario {scenario}\n\n");
            foreach (var call in trace["calls"]!.AsArray().OfType<JsonObject>())
            {
                traced++;
                var before = call["before"]!.AsArray().Select(x => Latin1.GetBytes(x!.GetValue<string>())).ToList();
                var after = call["after"]!.AsArray().Select(x => Latin1.GetBytes(x!.GetValue<string>())).ToList();
                sb.Append($"### CALL {call["seq"]}\n\n");
                for (int i = 0; i < before.Count; i++)
                {
                    var item = i < linkage.Count ? linkage[i] : null;
                    var name = item?.Name ?? $"argument {i + 1}";
                    var fields = item is null ? [] : ReplayPlanBuilder.Layout(item).Where(f => f.Offset < before[i].Length).ToList();
                    var shown = fields.Where(f => !before[i][f.Offset..Math.Min(before[i].Length, f.Offset + f.Length)].All(x => x == ' ')).ToList();
                    sb.Append($"- {name} ({before[i].Length} bytes) before: ");
                    sb.Append(fields.Count == 0 ? "«" + Escape(before[i]) + "»" : string.Join(", ", shown.Select(f => Field(f, before[i])))).Append('\n');
                    var changed = fields.Count == 0
                        ? (before[i].SequenceEqual(after[i]) ? [] : new List<string> { "«" + Escape(after[i]) + "»" })
                        : fields.Where(f => !before[i][f.Offset..Math.Min(before[i].Length, f.Offset + f.Length)].SequenceEqual(after[i][f.Offset..Math.Min(after[i].Length, f.Offset + f.Length)]))
                            .Select(f => Field(f, after[i])).ToList();
                    sb.Append($"  after: {(changed.Count == 0 ? "unchanged" : string.Join(", ", changed))}\n");
                }
                sb.Append('\n');
                if (sb.Length > 70_000) { sb.Append("(further calls omitted for length)\n"); break; }
            }
            if (sb.Length > 70_000) break;
        }
        op.Info($"{pid}: {traced} CALL(s) traced over {scenarios.Count} scenario(s)");
        return sb.ToString();
    }

    string RuntimeClasses() => Path.Combine(config.Settings.RepoRoot, "java-sidecar", "build", "classes", "java", "main");
}
