// Checks for the typed rule handoff (RuleHandoff, CallSiteInventory, JavaSidecarService, JavaProjectAssembler).
// Run from the repository root:  dotnet run --project MlCpu/src/MlCpu.Host.Checks [-- --offline] [--keep] [--dump]
// Offline checks need nothing. The end-to-end lane needs the Java sidecar serving extractRules (tools/Start-Sidecar.ps1),
// JAVA_HOME (21) and Gradle with a warm dependency cache (runs --offline); anything missing is BLOCKED, never PASS.
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.Projections;
using MlCpu.Host.Checks;
using MlCpu.Host.Services;
using Microsoft.Extensions.Logging.Abstractions;

var offline = args.Contains("--offline");
var keep = args.Contains("--keep");
var repo = Repo.Root();
var report = new Report();

Offline.Run(report, repo);
if (args.Contains("--dump"))
    foreach (var partitioned in new[] { false, true })
        Console.WriteLine("dumped " + await new JavaProjectAssembler().PublishAsync(
            new JavaProjectAssembler().Assemble(partitioned ? "syn-partitioned" : "syn", [Synthetic.Result()], new Dictionary<string, string>(), [], new AssemblyOptions(DomainPartition: partitioned)),
            Path.Combine(repo, "work", "rule-handoff-check", "synthetic")));
if (!offline) await EndToEnd.Run(report, repo, keep);
return report.Summary();

namespace MlCpu.Host.Checks
{
    sealed class Report
    {
        readonly List<(string Check, string Status, string Detail)> _rows = [];

        public void Check(string check, bool pass, string detail = "") => Add(check, pass ? "PASS" : "FAIL", detail);
        public void Blocked(string check, string why) => Add(check, "BLOCKED", why);
        public void Fail(string check, Exception e) => Add(check, "FAIL", e.GetType().Name + ": " + e.Message);

        void Add(string check, string status, string detail)
        {
            _rows.Add((check, status, detail));
            Console.WriteLine($"{status,-8}{check}{(detail.Length > 0 && status != "PASS" ? "  -- " + detail : "")}");
        }

        public int Summary()
        {
            Console.WriteLine($"\n{_rows.Count(r => r.Status == "PASS")} passed, {_rows.Count(r => r.Status == "FAIL")} failed, {_rows.Count(r => r.Status == "BLOCKED")} blocked");
            return _rows.Any(r => r.Status == "FAIL") ? 1 : 0;
        }
    }

    static class Repo
    {
        public static string Root()
        {
            for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d is not null; d = d.Parent)
                if (Directory.Exists(Path.Combine(d.FullName, "java-sidecar")) && Directory.Exists(Path.Combine(d.FullName, "MlCpu"))) return d.FullName;
            throw new InvalidOperationException("run from inside the repository");
        }

        public static string Sha(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

        /// <summary>Hash of every file under a directory (relative path + bytes), to prove a tree was not touched.</summary>
        public static string TreeHash(string dir)
        {
            var sb = new StringBuilder();
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Where(f => !f.Contains($"{Path.DirectorySeparatorChar}build{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}.gradle{Path.DirectorySeparatorChar}")).Order(StringComparer.Ordinal))
                sb.Append(Path.GetRelativePath(dir, f)).Append(' ').Append(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(f)))).Append('\n');
            return Sha(sb.ToString());
        }
    }

    /// <summary>A minimal but complete typed rule, as the sidecar would emit it (agreed wire shape).</summary>
    static class Synthetic
    {
        public const string RuleId = "SYN.PRED-0000abcd";

        public static readonly string Selector = """
            package com.mlcpu.rules.spi;

            import java.util.Map;
            import java.util.ServiceLoader;
            import java.util.concurrent.ConcurrentHashMap;
            import java.util.function.Supplier;

            public final class RuleSelector {
                public static final String GENERATED = "generated";
                public static final String PROPERTY_PREFIX = "mlcpu.rule.";
                private static final Map<String, Object> SELECTED = new ConcurrentHashMap<>();
                private RuleSelector() {}
                public static String selection(String ruleId) {
                    String v = System.getProperty(PROPERTY_PREFIX + ruleId);
                    return v == null || v.isBlank() ? GENERATED : v.trim();
                }
                public static <C> C select(String ruleId, Class<C> contract, Supplier<? extends C> generated) {
                    return contract.cast(SELECTED.computeIfAbsent(ruleId, id -> resolve(id, contract, generated)));
                }
                @SuppressWarnings("rawtypes")
                private static Object resolve(String ruleId, Class<?> contract, Supplier<?> generated) {
                    String want = selection(ruleId);
                    if (want.equals(GENERATED)) return generated.get();
                    for (RuleImplementation r : ServiceLoader.load(RuleImplementation.class))
                        if (r.ruleId().equals(ruleId) && r.implementationId().equals(want) && r.contract() == contract) return r.create();
                    throw new IllegalStateException("rule " + ruleId + ": implementation '" + want + "' is not registered");
                }
                public static void reset() { SELECTED.clear(); }
            }
            """;

        public static readonly string Implementation = """
            package com.mlcpu.rules.spi;

            public interface RuleImplementation<C> {
                String ruleId();
                String implementationId();
                Class<C> contract();
                C create();
            }
            """;

        public static JsonObject Response(string? contractOverride = null) => new()
        {
            ["programId"] = "SYN",
            ["additionalSources"] = new JsonArray(
                Src("com/mlcpu/rules/spi/RuleSelector.java", "spi", Selector),
                Src("com/mlcpu/rules/spi/RuleImplementation.java", "spi", Implementation),
                Src("com/mlcpu/rules/syn/IsPositive.java", "contract", contractOverride ?? """
                    package com.mlcpu.rules.syn;

                    /** Provisional name. From Prog_SYN paragraph MAIN (comments may name program classes). */
                    public interface IsPositive {
                        boolean evaluate(IsPositiveInput input);
                    }
                    """),
                Src("com/mlcpu/rules/syn/IsPositiveInput.java", "contract", """
                    package com.mlcpu.rules.syn;

                    public record IsPositiveInput(java.math.BigDecimal wsAmt) {
                        public IsPositiveInput { java.util.Objects.requireNonNull(wsAmt); }
                    }
                    """),
                Src("com/mlcpu/rules/syn/IsPositiveGenerated.java", "generated-implementation", """
                    package com.mlcpu.rules.syn;

                    public final class IsPositiveGenerated implements IsPositive {
                        @Override public boolean evaluate(IsPositiveInput input) { return input.wsAmt().signum() > 0; }
                    }
                    """),
                Src("com/mlcpu/rules/syn/IsPositiveCases.java", "cases", """
                    package com.mlcpu.rules.syn;

                    import java.math.BigDecimal;
                    import java.util.List;

                    public final class IsPositiveCases {
                        public record Case(String id, IsPositiveInput input, boolean expected) {}
                        public static List<Case> cases() {
                            return List.of(new Case("zero", new IsPositiveInput(BigDecimal.ZERO), false), new Case("one", new IsPositiveInput(BigDecimal.ONE), true));
                        }
                    }
                    """),
                Src("com/mlcpu/rules/syn/SynRules.java", "binding", """
                    package com.mlcpu.rules.syn;

                    import com.mlcpu.rules.spi.RuleSelector;

                    public final class SynRules {
                        private final IsPositive isPositive;
                        private SynRules(IsPositive isPositive) { this.isPositive = isPositive; }
                        public static SynRules selected() { return new SynRules(RuleSelector.select("SYN.PRED-0000abcd", IsPositive.class, IsPositiveGenerated::new)); }
                        public static SynRules generated() { return new SynRules(new IsPositiveGenerated()); }
                        public IsPositive isPositive() { return isPositive; }
                    }
                    """)),
            ["ruleContracts"] = new JsonArray(new JsonObject
            {
                ["schema"] = "mlcpu.rule-contract.v1", ["ruleId"] = RuleId, ["programId"] = "SYN", ["kind"] = "predicate",
                ["contract"] = "com.mlcpu.rules.syn.IsPositive", ["method"] = "evaluate", ["input"] = "com.mlcpu.rules.syn.IsPositiveInput",
                ["output"] = "boolean", ["generatedImplementation"] = "com.mlcpu.rules.syn.IsPositiveGenerated", ["binding"] = "com.mlcpu.rules.syn.SynRules",
                ["cases"] = "com.mlcpu.rules.syn.IsPositiveCases", ["evidence"] = new JsonArray("faithful-runtime-cases"), ["level"] = "L2",
                ["inputs"] = new JsonArray(new JsonObject { ["name"] = "wsAmt", ["javaType"] = "java.math.BigDecimal", ["cobolName"] = "WS-AMT", ["picture"] = "S9(5)V99", ["usage"] = "DISPLAY", ["fieldId"] = 3 }),
                ["provenance"] = new JsonObject { ["paragraph"] = "MAIN", ["statementIds"] = new JsonArray("S0004"), ["cobolLines"] = new JsonArray(12) },
                ["hash"] = new string('a', 64), ["provisionalName"] = true, ["expression"] = "WS-AMT > 0", ["scope"] = "slice"
            }),
            ["extractionLedger"] = new JsonObject
            {
                ["schema"] = "mlcpu.rule-extraction-ledger.v1", ["programId"] = "SYN",
                ["applied"] = new JsonArray(new JsonObject { ["ruleId"] = RuleId, ["site"] = "S0004", ["kind"] = "predicate" }),
                ["skipped"] = new JsonArray(new JsonObject { ["site"] = "S0007", ["kind"] = "calculation", ["reason"] = "division: rounding not representable", ["line"] = 15 })
            }
        };

        static JsonObject Src(string path, string role, string content) => new()
        {
            ["path"] = path, ["sourceSet"] = "main", ["role"] = role, ["ruleIds"] = new JsonArray(RuleId), ["content"] = content
        };

        public static JavaTranspileResult Result(JsonObject? response = null)
        {
            var r = new JavaTranspileResult
            {
                Program = "SYN", Ok = true, Compiled = true, Verdict = "TRANSPILED", StorageComposition = true, ClassName = "Prog_SYN",
                JavaSource = "package com.mlcpu.gen;\n\n/** Generated from SYN */\npublic final class Prog_SYN {\n    final com.mlcpu.rules.syn.SynRules rules = com.mlcpu.rules.syn.SynRules.selected();\n}\n",
                FaithfulJavaSource = "package com.mlcpu.gen;\n\n/** Generated from SYN */\npublic final class Prog_SYN {\n}\n"
            };
            RuleHandoff.ReadSidecarResponse(response ?? Response(), r);
            return r;
        }
    }

    static class Offline
    {
        public static void Run(Report report, string repo)
        {
            Try(report, "offline/read: agreed wire shape parses", () =>
            {
                var r = Synthetic.Result();
                return (r.AdditionalSources.Count == 7 && r.RuleContracts.Count == 1 && r.ExtractionLedger?["skipped"]?.AsArray().Count == 1, "");
            });
            Try(report, "offline/read: a program-state import is rejected", () =>
                Rejects(Synthetic.Response("package com.mlcpu.rules.syn;\nimport com.mlcpu.core.State_SYN;\npublic interface IsPositive { boolean evaluate(IsPositiveInput input); }\n"), "com.mlcpu.core."));
            Try(report, "offline/read: a runtime reference is rejected", () =>
                Rejects(Synthetic.Response("package com.mlcpu.rules.syn;\npublic interface IsPositive { boolean evaluate(IsPositiveInput input); default com.mlcpu.rt.CobField f() { return null; } }\n"), "com.mlcpu.rt."));
            Try(report, "offline/read: a path outside com/mlcpu/rules is rejected", () =>
            {
                var resp = Synthetic.Response();
                resp["additionalSources"]![0]!["path"] = "com/mlcpu/core/RuleSelector.java";
                return Rejects(resp, "com/mlcpu/rules");
            });
            Try(report, "offline/read: an invalid descriptor is rejected", () =>
            {
                var resp = Synthetic.Response();
                resp["ruleContracts"]![0]!["contract"] = "com.mlcpu.core.IsPositive";
                return Rejects(resp, "contract");
            });
            Try(report, "offline/read: ambient state in a generated provider is rejected", () =>
            {
                var resp = Synthetic.Response();
                resp["additionalSources"]![4]!["content"] = "package com.mlcpu.rules.syn;\npublic final class IsPositiveGenerated implements IsPositive {\n" +
                    "    private static final java.util.concurrent.atomic.AtomicLong N = new java.util.concurrent.atomic.AtomicLong();\n" +
                    "    @Override public boolean evaluate(IsPositiveInput input) { N.incrementAndGet(); return input.wsAmt().signum() > 0; }\n}\n";
                var atomic = Rejects(resp, "ambient state");
                resp["additionalSources"]![4]!["content"] = "package com.mlcpu.rules.syn;\npublic final class IsPositiveGenerated implements IsPositive {\n" +
                    "    @Override public boolean evaluate(IsPositiveInput input) { return Boolean.getBoolean(\"x\") || System.getProperty(\"y\") != null; }\n}\n";
                var ambient = Rejects(resp, "System.");
                return (atomic.Item1 && ambient.Item1, atomic.Item2 + " / " + ambient.Item2);
            });
            Try(report, "offline/jobrunner: the provider-state gate is generated only with typed rules", () =>
            {
                string Runner(JavaProject p) => p.Files.Single(f => f.RelPath.EndsWith("JobRunner.java", StringComparison.Ordinal)).Content;
                var with = Runner(Assemble(false));
                var without = Runner(new JavaProjectAssembler().Assemble("syn", [new JavaTranspileResult { Program = "X", Compiled = true, JavaSource = "package com.mlcpu.gen;\npublic final class Prog_X {}\n" }],
                    new Dictionary<string, string>(), [], new AssemblyOptions()));
                return (with.Contains("ProviderPurity.violations(r.create())") && with.Contains("not verifiably stateless") && !without.Contains("ProviderPurity"), "");
            });

            foreach (var partitioned in new[] { false, true })
            {
                var layout = partitioned ? "partitioned" : "monolithic";
                Try(report, $"offline/{layout}: modules, wiring, docs and lock", () =>
                {
                    var p = Assemble(partitioned);
                    var f = p.Files.ToDictionary(x => x.RelPath, x => x.Content, StringComparer.Ordinal);
                    var missing = new[]
                    {
                        "modern-rules/build.gradle.kts", "modern-rules/src/main/java/com/mlcpu/rules/spi/RuleSelector.java",
                        "modern-rules/src/main/java/com/mlcpu/rules/verify/ProviderPurity.java",
                        "modern-rules/src/main/java/com/mlcpu/rules/syn/IsPositive.java", "modern-rules/src/main/java/com/mlcpu/rules/syn/IsPositiveCases.java",
                        "rules-example/build.gradle.kts", "rules-example/src/main/java/com/mlcpu/rules/example/IsPositiveExample.java",
                        "rules-example/src/main/resources/META-INF/services/com.mlcpu.rules.spi.RuleImplementation",
                        "rules-conformance/build.gradle.kts", "rules-conformance/src/test/java/com/mlcpu/rules/conformance/RuleContractConformanceTest.java",
                        "rules-conformance/src/test/java/com/mlcpu/rules/conformance/RuleArchitectureTest.java",
                        "rules-conformance/src/test/java/com/mlcpu/rules/conformance/GeneratedLockTest.java",
                        "modernization/rule-contracts.json", "modernization/rule-extraction.json", "modernization/rule-handoff.md",
                        "modernization/storage-composition.json", RuleHandoff.LockPath
                    }.Where(x => !f.ContainsKey(x)).ToList();
                    var settings = f["settings.gradle.kts"];
                    var wired = settings.Contains("include(\"modern-rules\")") && settings.Contains("project(\":human-rules\").projectDir = humanRules")
                        && (partitioned
                            ? f["estate-common/build.gradle.kts"].Contains("api(project(\":modern-rules\"))") && f["estate-app/build.gradle.kts"].Contains("runtimeOnly(project(\":human-rules\"))")
                              && f["build.gradle.kts"].Contains("configure(subprojects.filter") && !f["build.gradle.kts"].Contains("\nsubprojects {")
                            : f["build.gradle.kts"].Contains("implementation(project(\":modern-rules\"))") && f["build.gradle.kts"].Contains("runtimeOnly(project(\":human-rules\"))"))
                        && f["build.gradle.kts"].Contains("startsWith(\"mlcpu.rule.\")");
                    var pure = !f["modern-rules/build.gradle.kts"].Contains("project(") && f["rules-example/build.gradle.kts"].Contains("implementation(project(\":modern-rules\"))")
                        && !f["rules-example/build.gradle.kts"].Replace("project(\":modern-rules\")", "").Contains("project(");
                    var lockOk = JsonNode.Parse(f[RuleHandoff.LockPath])!["files"]!.AsArray().Count == f.Count - 1;
                    var composition = JsonNode.Parse(f["modernization/storage-composition.json"])!;
                    var stage = composition["stage"]!.GetValue<string>() == "storage-composition+typed-rule-slices" && composition["businessRulesExtracted"]!.GetValue<bool>();
                    var noPromotion = f["modernization/rule-contracts.json"].Contains("\"approval\": \"none\"") && f["modernization/rule-contracts.json"].Contains("\"defaultImplementation\": \"generated\"")
                        && !f["modernization/rule-contracts.json"].Contains("\"EXPLAINED\"");
                    var orphans = partitioned && f.Keys.Any(k => k.StartsWith("src/", StringComparison.Ordinal));
                    return (missing.Count == 0 && wired && pure && lockOk && stage && noPromotion && !orphans,
                        $"missing [{string.Join(", ", missing)}] wired={wired} pure={pure} lock={lockOk} stage={stage} noPromotion={noPromotion} orphans={orphans}");
                });
            }
            Try(report, "offline/determinism: two assemblies are byte-identical (TG1)", () =>
            {
                var a = Assemble(false).Files.Select(x => x.RelPath + "\n" + x.Content);
                var b = Assemble(false).Files.Select(x => x.RelPath + "\n" + x.Content);
                return (a.SequenceEqual(b), "");
            });
            Try(report, "offline/dedupe: identical shared sources merge, conflicting ones fail", () =>
            {
                var one = Synthetic.Result();
                var two = Synthetic.Result();
                two.Program = "SYN2";
                two.RuleContracts.Clear();
                two.AdditionalSources.RemoveAll(s => s.Role != "spi");
                var files = new ProjectFiles();
                RuleHandoff.Apply(files, "x-spring", [one, two], false);
                var three = Synthetic.Result();
                three.Program = "SYN3";
                three.RuleContracts.Clear();
                three.AdditionalSources.RemoveAll(s => s.Role != "spi");
                three.AdditionalSources[0] = three.AdditionalSources[0] with { Content = three.AdditionalSources[0].Content + "// changed\n" };
                try { RuleHandoff.Apply(new ProjectFiles(), "x-spring", [one, three], false); return (false, "conflict accepted"); }
                catch (InvalidOperationException e) { return (e.Message.Contains("differs"), e.Message); }
            });
            Try(report, "offline/fail-closed: contracts naming ungenerated types or without the SPI fail", () =>
            {
                var r = Synthetic.Result();
                r.AdditionalSources.RemoveAll(s => s.Path.EndsWith("IsPositiveCases.java", StringComparison.Ordinal));
                var noCases = Throws(() => RuleHandoff.Apply(new ProjectFiles(), "x", [r], false), "did not generate");
                var s = Synthetic.Result();
                s.AdditionalSources.RemoveAll(x => x.Role == "spi");
                var noSpi = Throws(() => RuleHandoff.Apply(new ProjectFiles(), "x", [s], false), "SPI");
                return (noCases && noSpi, $"cases={noCases} spi={noSpi}");
            });
            Try(report, "offline/ledger: a composed program without extraction is recorded as skipped, no modules", () =>
            {
                var r = new JavaTranspileResult { Program = "OLD", StorageComposition = true, Compiled = true, JavaSource = "x", FaithfulJavaSource = "x" };
                var files = new ProjectFiles();
                var l = RuleHandoff.Apply(files, "x", [r], false);
                return (l.Skipped.Count == 1 && !files.Paths().Any(), "");
            });
            Try(report, "offline/strangler: wiring only with a strangler runtime; default OFF; baselines registered by final package", () =>
            {
                string Get(JavaProject p, string suffix) => p.Files.Single(f => f.RelPath.EndsWith(suffix, StringComparison.Ordinal)).Content;
                var plain = new JavaProjectAssembler().Assemble("syn", [Synthetic.Result()], new Dictionary<string, string>(), [], new AssemblyOptions());
                var runtime = new Dictionary<string, string> { ["JobEngine.java"] = "class JobEngine { BASELINE_PROGRAMS; STRANGLER; }" };
                var wired = new JavaProjectAssembler().Assemble("syn", [Synthetic.Result()], runtime, [], new AssemblyOptions());
                var off = !Get(plain, "JobDefinition.java").Contains("baselinePrograms") && !Get(plain, "JobRunner.java").Contains("STRANGLER")
                          && !Get(plain, "application.properties").Contains("mlcpu.strangler");
                var on = Get(wired, "JobDefinition.java").Contains("Map.entry(\"SYN\", com.mlcpu.core.Baseline_SYN::new)")
                         && Get(wired, "JobRunner.java").Contains("JobEngine.BASELINE_PROGRAMS.putAll(JobDefinition.baselinePrograms());")
                         && Get(wired, "JobRunner.java").Contains("@Value(\"${mlcpu.strangler:OFF}\") String strangler;")
                         && Get(wired, "application.properties").Contains("mlcpu.strangler=OFF");
                return (off && on, $"off={off} on={on}");
            });
            Try(report, "offline/strip: comments and literals do not trip the purity scan; code does", () =>
                (RuleHandoff.Violation("/** Prog_X */ class A { String s = \"com.mlcpu.core.X\"; // State_X\n }") is null
                 && RuleHandoff.Violation("class A { java.util.Random r; }") == "java.util.Random", ""));
            Try(report, "offline/inventory: BY VALUE blocked, CANCEL unavailable, dynamic unverified", () =>
            {
                var dir = Path.Combine(repo, "work", "rule-handoff-check");
                Directory.CreateDirectory(dir);
                var cbl = Path.Combine(dir, "CALLINV.cbl");
                File.WriteAllText(cbl, """
                           IDENTIFICATION DIVISION.
                           PROGRAM-ID. CALLINV.
                           DATA DIVISION.
                           WORKING-STORAGE SECTION.
                           01 WS-A        PIC S9(9) COMP-5 VALUE 1.
                           01 WS-B        PIC X(4) VALUE 'ABCD'.
                           01 WS-PGM      PIC X(8).
                           PROCEDURE DIVISION.
                               CALL 'SUBA' USING BY REFERENCE WS-B BY VALUE WS-A
                               CALL 'SUBB' USING WS-B BY CONTENT WS-A
                               ACCEPT WS-PGM
                               CALL WS-PGM USING WS-B
                               CANCEL 'SUBA'
                               GOBACK.
                    """.Replace("\r\n", "\n"));
                var model = CobolModelBuilder.BuildGroundedFull(cbl);
                var sites = JsonNode.Parse(System.Text.Json.JsonSerializer.Serialize(CallSiteInventory.Build(model, ProgramGraphBuilder.Build(model, dataDeps: false), "")))!.AsArray();
                string Status(int i) => sites[i]!["composedLane"]!["status"]!.GetValue<string>();
                var ok = sites.Count == 4 && Status(0) == "blocked" && Status(1) == "supported" && Status(2) == "unverified" && Status(3) == "unavailable"
                    && sites[0]!["arguments"]![1]!["mode"]!.GetValue<string>() == "VALUE" && sites[1]!["arguments"]![1]!["mode"]!.GetValue<string>() == "CONTENT";
                return (ok, sites.ToJsonString());
            });
        }

        static JavaProject Assemble(bool partitioned) =>
            new JavaProjectAssembler().Assemble("syn", [Synthetic.Result()], new Dictionary<string, string>(), [], new AssemblyOptions(DomainPartition: partitioned));

        static (bool, string) Rejects(JsonObject response, string expected)
        {
            try { Synthetic.Result(response); return (false, "accepted"); }
            catch (InvalidOperationException e) { return (e.Message.Contains(expected, StringComparison.Ordinal), e.Message); }
        }

        static bool Throws(Action a, string expected)
        {
            try { a(); return false; }
            catch (InvalidOperationException e) { return e.Message.Contains(expected, StringComparison.Ordinal); }
        }

        internal static void Try(Report report, string check, Func<(bool Pass, string Detail)> body)
        {
            try { var (pass, detail) = body(); report.Check(check, pass, detail); }
            catch (Exception e) { report.Fail(check, e); }
        }
    }
}
