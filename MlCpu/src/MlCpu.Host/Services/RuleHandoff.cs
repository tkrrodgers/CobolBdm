using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis.Projections;

namespace MlCpu.Host.Services;

/// <summary>A typed rule source the sidecar extracted: path under a Java source root, source set, role and the rules it serves.</summary>
public sealed record GeneratedRuleSource(string Path, string SourceSet, string Role, IReadOnlyList<string> RuleIds, string Content);

/// <summary>
/// Publishes the sidecar's typed rule contracts (composed lane, <c>extractRules</c>) as an integration kit, model-free:
/// <list type="bullet">
/// <item><c>modern-rules</c>: generated contracts, input/result records, reference implementations, per-program bindings,
/// faithful cases and the selection SPI. JDK only; no runtime, program state or framework on its compile path.</item>
/// <item><c>rules-example</c>: a minimal human-style implementation registered through the SPI (selected only explicitly).</item>
/// <item><c>rules-conformance</c>: contract tests (every implementation x faithful cases), selection, architecture and
/// generated-lock tests.</item>
/// <item><c>:human-rules</c>: the human-owned module, outside the generated root (default <c>../&lt;slug&gt;-human</c>,
/// <c>-Pmlcpu.humanRules</c>); publishing never writes it. It may depend on <c>:modern-rules</c> only.</item>
/// </list>
/// The generated program constructs its binding through <c>RuleSelector</c>: the generated implementation unless
/// <c>mlcpu.rule.&lt;ruleId&gt;</c> names a registered one. Nothing is promoted automatically and no mismatch is explained.
/// </summary>
public static partial class RuleHandoff
{
    public const string Pass = "RuleHandoff";
    public const string RulesModule = "modern-rules";
    public const string ExampleModule = "rules-example";
    public const string ConformanceModule = "rules-conformance";
    public const string HumanProject = "human-rules";
    public const string LockPath = "generated.lock.json";
    public const string ExampleId = "example";
    public const string SelectionPrefix = "mlcpu.rule.";

    static readonly HashSet<string> Roles = ["spi", "contract", "generated-implementation", "binding", "support", "cases", "contract-test"];

    /// <summary>
    /// Tokens no rule code (generated, example or human) may contain outside comments and literals: program state, the
    /// compatibility runtime, frameworks, direct I/O, clocks and randomness (forward/loans/RULES.md).
    /// </summary>
    public static readonly string[] Forbidden =
    [
        "com.mlcpu.rt.", "com.mlcpu.core.", "com.mlcpu.gen.", "com.mlcpu.app.", "com.mlcpu.capabilities.", "org.springframework.",
        "CobolBase", "State_", "Prog_", "Baseline_", "java.io.File", "java.nio.file", "java.net.", "java.sql.",
        "java.util.Random", "ThreadLocalRandom", "System.currentTimeMillis", "System.nanoTime", "System.getenv"
    ];

    /// <summary>
    /// Ambient-state tokens no rule provider (contract, generated, example or human implementation; not the selection SPI,
    /// bindings or the verifier) may contain outside comments and literals: system/property/environment access, threads,
    /// concurrency primitives, reflection and class loading, clocks, I/O, randomness, static initializers. Heuristic: a hit
    /// rejects, no hit is not a proof of purity.
    /// </summary>
    public static readonly string[] Ambient =
    [
        "System.", "Thread", "Runtime.", "ProcessBuilder", "java.lang.reflect", "Class.forName", "getDeclared", "setAccessible",
        "MethodHandle", "ServiceLoader", "ClassLoader", "ThreadLocal", "java.util.concurrent", "Atomic", "synchronized", "volatile",
        "java.time", ".now(", "java.io", "java.nio", "java.net", "java.sql", "Random", "UUID", "static {", "native ", "Unsafe"
    ];

    static readonly HashSet<string> ProviderRoles = ["contract", "generated-implementation", "support", "cases"];

    [GeneratedRegex(@"^com/mlcpu/rules/(?:[a-z0-9_]+/)+[A-Za-z_][A-Za-z0-9_]*\.java$")] private static partial Regex PathRx();
    [GeneratedRegex(@"^[A-Z0-9][A-Za-z0-9._-]*$")] private static partial Regex RuleIdRx();
    [GeneratedRegex(@"^com\.mlcpu\.rules\.(?:[a-z0-9_]+\.)+[A-Za-z_][A-Za-z0-9_]*$")] private static partial Regex TypeRx();
    [GeneratedRegex(@"^[a-z][A-Za-z0-9_]*$")] private static partial Regex MethodRx();

    /// <summary>Reads additionalSources/ruleContracts/extractionLedger from a /transpile-verify response; fails closed on a malformed or impure source.</summary>
    public static void ReadSidecarResponse(JsonNode response, JavaTranspileResult result)
    {
        var program = response["programId"]?.GetValue<string>() ?? result.Program;
        foreach (var n in response["additionalSources"]?.AsArray() ?? [])
        {
            var o = n!.AsObject();
            var path = Str(o, "path") ?? throw Bad(program, "additional source without path");
            var set = Str(o, "sourceSet") ?? "main";
            var role = Str(o, "role") ?? "";
            var content = Str(o, "content") ?? throw Bad(program, $"{path}: no content");
            if (!PathRx().IsMatch(path)) throw Bad(program, $"{path}: rule sources must be .java files under com/mlcpu/rules/");
            if (set is not ("main" or "test")) throw Bad(program, $"{path}: sourceSet must be main or test");
            if (!Roles.Contains(role)) throw Bad(program, $"{path}: unknown role '{role}'");
            if (Violation(content) is { } token) throw Bad(program, $"{path} references {token}; rule code must not depend on program state, the runtime or frameworks");
            if (ProviderRoles.Contains(role) && AmbientViolation(content) is { } ambient)
                throw Bad(program, $"{path} uses {ambient}; rule providers must not depend on ambient state (RunUnit snapshots do not capture it)");
            if (path.StartsWith("com/mlcpu/rules/verify/", StringComparison.Ordinal)) throw Bad(program, $"{path}: com.mlcpu.rules.verify is reserved for the publisher");
            var ids = o["ruleIds"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
            result.AdditionalSources.Add(new GeneratedRuleSource(path, set, role, ids, content));
        }
        foreach (var n in response["ruleContracts"]?.AsArray() ?? [])
        {
            var c = n!.AsObject().DeepClone().AsObject();
            var id = Str(c, "ruleId") ?? "";
            if (!RuleIdRx().IsMatch(id)) throw Bad(program, $"rule contract with invalid ruleId '{id}'");
            if (Str(c, "kind") is not ("predicate" or "calculation")) throw Bad(program, $"{id}: kind must be predicate or calculation");
            foreach (var key in new[] { "contract", "input", "generatedImplementation", "binding" })
                if (!TypeRx().IsMatch(Str(c, key) ?? "")) throw Bad(program, $"{id}: {key} must be a com.mlcpu.rules type");
            if (!MethodRx().IsMatch(Str(c, "method") ?? "")) throw Bad(program, $"{id}: invalid method");
            var output = Str(c, "output") ?? "";
            if (output != "boolean" && !TypeRx().IsMatch(output)) throw Bad(program, $"{id}: output must be boolean or a com.mlcpu.rules type");
            if (Str(c, "cases") is { } cases && !TypeRx().IsMatch(cases)) throw Bad(program, $"{id}: cases must be a com.mlcpu.rules type");
            result.RuleContracts.Add(c);
        }
        result.ExtractionLedger = response["extractionLedger"]?.DeepClone().AsObject();
        result.ExtractionLedgers = response["extractionLedgers"]?.AsArray().Select(l => l!.DeepClone().AsObject()).ToList()
            ?? (result.ExtractionLedger is { } main ? [(JsonObject)main.DeepClone()] : []);
    }

    static string? Str(JsonObject o, string key) => o[key] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static InvalidOperationException Bad(string program, string why) => new($"{program}: {why}");

    /// <summary>The first forbidden token in <paramref name="java"/>'s code (comments, strings and character literals excluded).</summary>
    public static string? Violation(string java)
    {
        var code = StripJava(java);
        return Forbidden.FirstOrDefault(t => code.Contains(t, StringComparison.Ordinal));
    }

    /// <summary>The first ambient-state token in <paramref name="java"/>'s code (comments and literals excluded).</summary>
    public static string? AmbientViolation(string java)
    {
        var code = StripJava(java);
        return Ambient.FirstOrDefault(t => code.Contains(t, StringComparison.Ordinal));
    }

    public static string StripJava(string s)
    {
        var sb = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; sb.Append('\n'); continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '*') { var end = s.IndexOf("*/", i + 2, StringComparison.Ordinal); i = end < 0 ? s.Length : end + 1; sb.Append(' '); continue; }
            if (c == '"' && i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
            {
                var end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                i = end < 0 ? s.Length : end + 2; sb.Append("\"\""); continue;
            }
            if (c is '"' or '\'')
            {
                for (i++; i < s.Length && s[i] != c && s[i] != '\n'; i++) if (s[i] == '\\') i++;
                sb.Append(c).Append(c); continue;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the rule modules, the handoff documents and the Gradle wiring for either layout. Runs after the projections
    /// (and domain partitioning), so the modules sit beside the program modules rather than inside a domain.
    /// </summary>
    public static ProjectionLedger Apply(ProjectFiles files, string slug, IReadOnlyList<JavaTranspileResult> compiled, bool partitioned)
    {
        var ledger = new ProjectionLedger(Pass);
        var composed = compiled.Where(r => r.StorageComposition).ToList();
        foreach (var r in composed.Where(r => r.ExtractionLedger is null))
            ledger.Skip(r.Program, "the sidecar returned no rule extraction ledger (extractRules not served)");
        var ledgers = composed.Where(r => r.ExtractionLedger is not null).ToList();
        if (ledgers.Count > 0)
            files.Set("modernization/rule-extraction.json", ProjectionJson.Write(new JsonObject
            {
                ["schema"] = "mlcpu.rule-extraction-ledgers.v1",
                ["note"] = "Applied rules and skipped sites exactly as the sidecar reported them; a skip is a review item, not a defect.",
                ["programs"] = new JsonArray(ledgers.OrderBy(r => r.Program, StringComparer.Ordinal)
                    .SelectMany(r => r.ExtractionLedgers.Count > 0 ? r.ExtractionLedgers : [r.ExtractionLedger!])
                    .Select(l => (JsonNode)l.DeepClone()).ToArray())
            }));
        foreach (var r in ledgers)
            foreach (var l in r.ExtractionLedgers.Count > 0 ? r.ExtractionLedgers : [r.ExtractionLedger!])
                foreach (var s in l["skipped"]?.AsArray() ?? [])
                    ledger.Skip($"{l["programId"] ?? r.Program} {s?["site"]}", $"{s?["kind"]}: {s?["reason"]}");

        var sources = new SortedDictionary<string, GeneratedRuleSource>(StringComparer.Ordinal);
        foreach (var r in compiled)
            foreach (var s in r.AdditionalSources)
            {
                var key = $"{RulesModule}/src/{s.SourceSet}/java/{s.Path}";
                if (sources.TryGetValue(key, out var prior))
                {
                    if (prior.Content != s.Content) throw new InvalidOperationException($"{r.Program}: {s.Path} differs from the copy another program generated");
                    continue;
                }
                sources[key] = s;
            }
        if (sources.Count == 0)
        {
            if (compiled.Any(r => r.RuleContracts.Count > 0)) throw new InvalidOperationException("the sidecar described rule contracts without their sources");
            ledger.Facts["rules"] = 0;
            return ledger;
        }

        var contracts = compiled.SelectMany(r => r.RuleContracts.Select(c => (r.Program, Contract: c)))
            .OrderBy(x => Str(x.Contract, "ruleId"), StringComparer.Ordinal).ToList();
        var dup = contracts.GroupBy(c => Str(c.Contract, "ruleId")).FirstOrDefault(g => g.Count() > 1);
        if (dup is not null) throw new InvalidOperationException($"rule {dup.Key} is declared twice");
        string MainPath(string fqn) => $"{RulesModule}/src/main/java/{fqn.Replace('.', '/')}.java";
        foreach (var (program, c) in contracts)
            foreach (var key in new[] { "contract", "input", "generatedImplementation", "binding", "cases", "output" })
                if (Str(c, key) is { } fqn && fqn != "boolean" && !sources.ContainsKey(MainPath(fqn)))
                    throw new InvalidOperationException($"{program}: rule {Str(c, "ruleId")} names {key} {fqn}, which the sidecar did not generate");
        if (!sources.ContainsKey(MainPath("com.mlcpu.rules.spi.RuleSelector")) || !sources.ContainsKey(MainPath("com.mlcpu.rules.spi.RuleImplementation")))
            throw new InvalidOperationException("the sidecar generated rule sources without the com.mlcpu.rules.spi selection SPI");

        foreach (var (path, s) in sources) files.Set(path, s.Content);
        files.Set($"{RulesModule}/build.gradle.kts", RulesBuild());
        files.Set($"{RulesModule}/src/main/java/com/mlcpu/rules/verify/ProviderPurity.java", ProviderPuritySource());
        var example = contracts.Count > 0 ? contracts[0].Contract : null;
        files.Set($"{ExampleModule}/build.gradle.kts", ExampleBuild(generated: true));
        if (example is not null)
        {
            var (cls, text) = ExampleSource(example);
            files.Set($"{ExampleModule}/src/main/java/com/mlcpu/rules/example/{cls}.java", text);
            files.Set($"{ExampleModule}/src/main/resources/META-INF/services/com.mlcpu.rules.spi.RuleImplementation", $"com.mlcpu.rules.example.{cls}$Registration\n");
        }
        else files.Set($"{ExampleModule}/README.md", "No rule contract was extracted, so there is no example implementation.\n");
        files.Set($"{ConformanceModule}/build.gradle.kts", ConformanceBuild());
        files.Set($"{ConformanceModule}/src/test/java/com/mlcpu/rules/conformance/RuleContractConformanceTest.java", ConformanceTest(contracts.Select(c => c.Contract).ToList()));
        files.Set($"{ConformanceModule}/src/test/java/com/mlcpu/rules/conformance/RuleArchitectureTest.java", ArchitectureTest());
        files.Set($"{ConformanceModule}/src/test/java/com/mlcpu/rules/conformance/GeneratedLockTest.java", LockTest());
        WireGradle(files, slug, partitioned);

        var contractSet = Sha256(string.Concat(sources.Select(kv => kv.Key + "\n" + kv.Value.Content + "\n")));
        files.Set("modernization/rule-contracts.json", ProjectionJson.Write(new JsonObject
        {
            ["schema"] = "mlcpu.rule-handoff.v1",
            ["generator"] = "mlcpu JavaProjectAssembler (deterministic; no model)",
            ["contractSetSha256"] = contractSet,
            ["promotion"] = "none: every rule runs its generated implementation unless mlcpu.rule.<ruleId> names a registered implementation explicitly",
            ["reconciliation"] = "a differing implementation is a DIFF; no EXPLAINED decision is generated or implied",
            ["providerState"] = new JsonObject
            {
                ["checked"] = "state: every field of a provider (and of what it references) is final and of a known immutable value type, by reflection; code: the compiled class files of the provider and every non-JDK class they reference contain no reference to an ambient JDK type or java.lang.Class member (ProviderPurity.AMBIENT), and their static fields obey the state rule; an unreadable class file is a violation; sources contain no ambient-state token (forbiddenAmbientInProviders)",
                ["enforced"] = "rules-conformance fails for any provider that does not pass; the generated JobRunner refuses to start a job selecting one; StepStrangler SHADOW refuses any custom rule selection",
                ["notEnforced"] = "selection through -Pmlcpu.rule.* in tests bypasses the JobRunner gate (the failing purity test still fails the build); RuleSelector itself does not verify",
                ["notClaimed"] = "purity or isolation of arbitrary providers: the checks are heuristics, and RunUnit/compatibility snapshots do not capture provider or RuleSelector caches"
            },
            ["forbiddenAmbientInProviders"] = new JsonArray(Ambient.Select(f => (JsonNode)f).ToArray()),
            ["modules"] = new JsonObject
            {
                ["contracts"] = ":" + RulesModule, ["example"] = ":" + ExampleModule, ["conformance"] = ":" + ConformanceModule,
                ["human"] = new JsonObject
                {
                    ["gradleProject"] = ":" + HumanProject, ["defaultRoot"] = $"../{slug}-human", ["rootProperty"] = "mlcpu.humanRules",
                    ["allowedProjectDependencies"] = new JsonArray(":" + RulesModule), ["ownedBy"] = "human; never written by publishing"
                }
            },
            ["selection"] = new JsonObject
            {
                ["systemProperty"] = SelectionPrefix + "<ruleId>", ["default"] = "generated", ["exampleImplementationId"] = ExampleId,
                ["gradle"] = $"-P{SelectionPrefix}<ruleId>=<implementationId>", ["job"] = $"--{SelectionPrefix}<ruleId>=<implementationId>"
            },
            ["forbiddenInRuleCode"] = new JsonArray(Forbidden.Select(f => (JsonNode)f).ToArray()),
            ["contracts"] = new JsonArray(contracts.Select(x =>
            {
                var c = (JsonObject)x.Contract.DeepClone();
                var hashes = new JsonObject();
                foreach (var key in new[] { "contract", "input", "output", "generatedImplementation", "cases", "binding" })
                    if (Str(x.Contract, key) is { } fqn && fqn != "boolean") hashes[key] = Sha256(sources[MainPath(fqn)].Content);
                c["sourceSha256"] = hashes;
                c["defaultImplementation"] = "generated";
                c["approval"] = "none";
                return (JsonNode)c;
            }).ToArray())
        }));
        files.Set("modernization/rule-handoff.md", HandoffDoc(slug, contracts.Select(c => c.Contract).ToList(), partitioned, example));

        ledger.Applied.AddRange(contracts.Select(c => Str(c.Contract, "ruleId")!));
        ledger.Facts["rules"] = contracts.Count;
        ledger.Facts["sources"] = sources.Count;
        ledger.Facts["contractSetSha256"] = contractSet;
        ledger.Facts["modules"] = new[] { RulesModule, ExampleModule, ConformanceModule };
        return ledger;
    }

    /// <summary>generated.lock.json: SHA-256 of every published file as written (LF text, decoded binary). TG3 for this export.</summary>
    public static string Lock(ProjectFiles files) => ProjectionJson.Write(new JsonObject
    {
        ["schema"] = "mlcpu.generated-lock.v1",
        ["generator"] = "mlcpu JavaProjectAssembler",
        ["note"] = "Generated files; edit the COBOL or the generator and republish. Human code belongs in the human-rules root.",
        ["files"] = new JsonArray(files.All().Where(f => f.Path != LockPath).Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Path,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(f.Language == "binary"
                ? Convert.FromBase64String(f.Content) : Encoding.UTF8.GetBytes(f.Content.Replace("\r\n", "\n"))))
        }).ToArray())
    });

    static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    static void WireGradle(ProjectFiles files, string slug, bool partitioned)
    {
        files.Set("settings.gradle.kts", (files.Get("settings.gradle.kts") ?? "") + $$"""

            // Typed rule handoff (modernization/rule-handoff.md): generated rule modules, and the human-owned module outside
            // this generated root. Publishing replaces this folder; it never writes the human root.
            include("{{RulesModule}}")
            include("{{ExampleModule}}")
            include("{{ConformanceModule}}")
            val humanRules = file(providers.gradleProperty("mlcpu.humanRules").getOrElse("../{{slug}}-human"))
            if (humanRules.resolve("build.gradle.kts").isFile) {
                include("{{HumanProject}}")
                project(":{{HumanProject}}").projectDir = humanRules
            }

            """);
        const string forward = """

            // Typed rule selection: -Pmlcpu.rule.<ruleId>=<implementationId> reaches every test and run JVM. Default: generated.
            allprojects {
                tasks.withType<Test>().configureEach {
                    project.properties.filterKeys { it.startsWith("mlcpu.rule.") }.forEach { (k, v) -> if (v != null) systemProperty(k, v.toString()) }
                }
                tasks.withType<JavaExec>().configureEach {
                    project.properties.filterKeys { it.startsWith("mlcpu.rule.") }.forEach { (k, v) -> if (v != null) systemProperty(k, v.toString()) }
                }
            }

            """;
        const string runtime = $$"""

            dependencies {
                runtimeOnly(project(":{{ExampleModule}}"))
                if (findProject(":{{HumanProject}}") != null) runtimeOnly(project(":{{HumanProject}}"))
            }

            """;
        if (!partitioned)
        {
            files.Set("build.gradle.kts", files.Get("build.gradle.kts") + $$"""

                // Generated programs call typed rules through their bindings (modernization/rule-handoff.md).
                dependencies {
                    implementation(project(":{{RulesModule}}"))
                }
                """ + runtime + forward);
            return;
        }
        var root = (files.Get("build.gradle.kts") ?? "").Replace("\r\n", "\n");
        const string block = "subprojects {\n";
        if (root.IndexOf(block, StringComparison.Ordinal) is var at && (at < 0 || root.IndexOf(block, at + 1, StringComparison.Ordinal) >= 0))
            throw new InvalidOperationException("partitioned root build: expected exactly one subprojects block");
        // The estate convention (Spring on every module) must not reach rule code: those modules configure themselves.
        root = root.Replace(block, $"configure(subprojects.filter {{ it.name !in setOf(\"{RulesModule}\", \"{ExampleModule}\", \"{ConformanceModule}\", \"{HumanProject}\") }}) {{\n");
        files.Set("build.gradle.kts", root + forward);
        files.Set("estate-common/build.gradle.kts", (files.Get("estate-common/build.gradle.kts") ?? "") + $$"""

            // Generated programs call typed rules through their bindings (modernization/rule-handoff.md).
            dependencies {
                api(project(":{{RulesModule}}"))
            }

            """);
        files.Set("estate-app/build.gradle.kts", (files.Get("estate-app/build.gradle.kts") ?? "") + runtime);
    }

    const string JavaConvention = """
        repositories { mavenCentral() }

        java {
            sourceCompatibility = JavaVersion.VERSION_21
            targetCompatibility = JavaVersion.VERSION_21
        }

        tasks.withType<JavaCompile>().configureEach { options.encoding = "UTF-8" }

        """;

    static string RulesBuild() => """
        // GENERATED. DO NOT EDIT (generated.lock.json). Typed rule contracts, input/result records, reference implementations,
        // per-program bindings, faithful cases and the selection SPI. The JDK is the only compile dependency.
        plugins {
            `java-library`
        }

        """ + JavaConvention;

    /// <summary>The example's build; the human module copies it unchanged apart from the comment.</summary>
    public static string ExampleBuild(bool generated) => (generated
        ? "// GENERATED example of a human-owned rule module (modernization/rule-handoff.md). Copy it to the human root; do not edit it here.\n"
        : "// Human-owned rule implementations. Allowed project dependency: :modern-rules (the public contracts) only.\n") + $$"""
        plugins {
            `java-library`
        }

        """ + JavaConvention + $$"""
        dependencies {
            implementation(project(":{{RulesModule}}"))
        }

        """;

    static string ConformanceBuild() => $$"""
        // GENERATED. DO NOT EDIT. Contract, selection, architecture and generated-lock tests over every rule implementation.
        plugins {
            java
        }

        """ + JavaConvention + $$"""
        dependencies {
            testImplementation(project(":{{RulesModule}}"))
            testRuntimeOnly(project(":{{ExampleModule}}"))
            if (findProject(":{{HumanProject}}") != null) testRuntimeOnly(project(":{{HumanProject}}"))
            testImplementation(platform("org.junit:junit-bom:5.11.3"))
            testImplementation("org.junit.jupiter:junit-jupiter")
            testRuntimeOnly("org.junit.platform:junit-platform-launcher")
        }

        tasks.withType<Test>().configureEach {
            useJUnitPlatform()
            systemProperty("mlcpu.generated.root", rootDir.absolutePath)
            findProject(":{{HumanProject}}")?.let { systemProperty("mlcpu.human.root", it.projectDir.absolutePath) }
            testLogging {
                events("failed")
                exceptionFormat = org.gradle.api.tasks.testing.logging.TestExceptionFormat.FULL
            }
        }

        """;

    static string Simple(string fqn) => fqn[(fqn.LastIndexOf('.') + 1)..];

    /// <summary>The conservative provider-state check the conformance tests and the generated JobRunner apply.</summary>
    static string ProviderPuritySource() => """
        package com.mlcpu.rules.verify;

        import java.io.ByteArrayInputStream;
        import java.io.DataInputStream;
        import java.io.IOException;
        import java.io.InputStream;
        import java.lang.reflect.Field;
        import java.lang.reflect.Modifier;
        import java.math.BigDecimal;
        import java.math.BigInteger;
        import java.util.ArrayDeque;
        import java.util.ArrayList;
        import java.util.Deque;
        import java.util.HashSet;
        import java.util.IdentityHashMap;
        import java.util.List;
        import java.util.Map;
        import java.util.Set;

        /**
         * GENERATED. DO NOT EDIT. A conservative, fail-closed check that a rule provider holds and reaches no mutable or
         * ambient state. Two parts:
         * <ol>
         * <li>State: every field of the provider and of every object it references is final and either a primitive, an
         * immutable JDK value (String, BigDecimal, BigInteger, boxed primitives) or a non-JDK object passing the same check;
         * arrays, other JDK types (collections, atomics, functions), lambdas and inaccessible fields are rejected.</li>
         * <li>Code: the compiled class files of the provider and of every non-JDK class they reference (transitively) are
         * read from the class path, and their constant pools must not reference ambient JDK types (System, threads,
         * concurrency, reflection and class loading, clocks, I/O, randomness, ...) or {@code java.lang.Class} members;
         * the static fields of each such class obey rule 1. A class file that cannot be read is a violation.</li>
         * </ol>
         * Passing is not a proof of purity, and RunUnit/compatibility snapshots never capture provider state.
         */
        public final class ProviderPurity {
            private static final Set<Class<?>> VALUES = Set.of(String.class, BigDecimal.class, BigInteger.class, Boolean.class, Character.class,
                    Byte.class, Short.class, Integer.class, Long.class, Float.class, Double.class);

            /** Internal names (exact, nested with '$', or package prefixes ending in '/') a provider's code may not reference. */
            static final List<String> AMBIENT = List.of("java/lang/System", "java/lang/Thread", "java/lang/ThreadLocal", "java/lang/InheritableThreadLocal",
                    "java/lang/ThreadGroup", "java/lang/Runtime", "java/lang/Process", "java/lang/ProcessBuilder", "java/lang/ProcessHandle",
                    "java/lang/ClassLoader", "java/lang/ScopedValue", "java/lang/StackWalker", "java/lang/reflect/", "java/lang/invoke/MethodHandle",
                    "java/lang/invoke/VarHandle", "java/lang/foreign/", "java/lang/management/", "java/lang/ref/", "java/util/concurrent/",
                    "java/util/Random", "java/util/SplittableRandom", "java/util/random/", "java/util/UUID", "java/util/ServiceLoader", "java/util/Timer",
                    "java/util/logging/", "java/util/prefs/", "java/util/Properties", "java/time/", "java/io/", "java/nio/", "java/net/", "java/sql/",
                    "java/security/", "javax/", "jdk/", "sun/", "com/sun/", "com/mlcpu/rules/spi/RuleSelector", "com/mlcpu/rules/verify/");
            private static final int MAX_CLASSES = 2000;

            private ProviderPurity() {
            }

            /** Every reason {@code provider} may hold or reach mutable or ambient state; empty when none was found. */
            public static List<String> violations(Object provider) {
                List<String> found = new ArrayList<>();
                if (provider == null) {
                    found.add("null provider");
                    return found;
                }
                Map<Object, Boolean> seen = new IdentityHashMap<>();
                seen.put(provider, Boolean.TRUE);
                Set<Class<?>> reached = new HashSet<>();
                visit(provider, provider.getClass().getName(), found, seen, reached);
                code(provider.getClass(), reached, found);
                return found;
            }

            static boolean jdk(String internal) {
                return internal.startsWith("java/") || internal.startsWith("javax/") || internal.startsWith("jdk/") || internal.startsWith("sun/")
                        || internal.startsWith("com/sun/");
            }

            private static boolean jdk(Class<?> c) {
                return c.getClassLoader() == null || jdk(c.getName().replace('.', '/'));
            }

            static String ambient(String internal) {
                for (String a : AMBIENT)
                    if (a.endsWith("/") ? internal.startsWith(a) : internal.equals(a) || internal.startsWith(a + "$")) return a;
                return null;
            }

            /** Instance fields along the object graph (rule 1); static fields are checked per class in {@link #code}. */
            private static void visit(Object instance, String path, List<String> found, Map<Object, Boolean> seen, Set<Class<?>> reached) {
                Class<?> type = instance.getClass();
                if (type.isSynthetic() || type.isHidden() || type.getName().contains("$$Lambda")) {
                    found.add(path + ": lambda or synthetic class " + type.getName() + " (state not inspectable)");
                    return;
                }
                for (Class<?> k = type; k != null && k != Object.class && k != Record.class && k != Enum.class; k = k.getSuperclass()) {
                    if (jdk(k)) {
                        found.add(path + ": extends JDK class " + k.getName());
                        return;
                    }
                    reached.add(k);
                    for (Field f : k.getDeclaredFields())
                        if (!Modifier.isStatic(f.getModifiers())) field(f, instance, path + "." + f.getName(), found, seen, reached);
                }
            }

            private static void field(Field f, Object owner, String where, List<String> found, Map<Object, Boolean> seen, Set<Class<?>> reached) {
                int m = f.getModifiers();
                if (f.isSynthetic() && f.getName().startsWith("$")) return;
                if (!Modifier.isFinal(m)) {
                    found.add(where + ": mutable field");
                    return;
                }
                Class<?> t = f.getType();
                if (t.isPrimitive() || VALUES.contains(t)) return;
                if (t.isArray()) {
                    found.add(where + ": array (mutable contents)");
                    return;
                }
                Object value;
                try {
                    f.setAccessible(true);
                    value = f.get(Modifier.isStatic(m) ? null : owner);
                } catch (RuntimeException | IllegalAccessException | ExceptionInInitializerError e) {
                    found.add(where + ": not inspectable (" + e + ")");
                    return;
                }
                if (value == null || VALUES.contains(value.getClass())) return;
                if (jdk(value.getClass())) {
                    found.add(where + ": " + value.getClass().getName() + " is not a known immutable value");
                    return;
                }
                if (seen.put(value, Boolean.TRUE) == null) visit(value, where, found, seen, reached);
            }

            /** Rule 2: the constant pools of the provider's classes and of every non-JDK class they reference. */
            private static void code(Class<?> root, Set<Class<?>> reached, List<String> found) {
                ClassLoader loader = root.getClassLoader();
                Deque<String> work = new ArrayDeque<>();
                Set<String> scanned = new HashSet<>();
                work.add(root.getName().replace('.', '/'));
                for (Class<?> c : reached) work.add(c.getName().replace('.', '/'));
                Map<Object, Boolean> seen = new IdentityHashMap<>();
                while (!work.isEmpty()) {
                    String name = work.poll();
                    if (jdk(name) || name.equals("com/mlcpu/rules/spi/RuleImplementation") || !scanned.add(name)) continue;
                    if (scanned.size() > MAX_CLASSES) {
                        found.add(root.getName() + ": more than " + MAX_CLASSES + " reachable classes; not inspected");
                        return;
                    }
                    byte[] bytes;
                    try (InputStream in = loader == null ? null : loader.getResourceAsStream(name + ".class")) {
                        if (in == null) {
                            found.add(name + ": class file not readable; not inspectable");
                            continue;
                        }
                        bytes = in.readAllBytes();
                    } catch (IOException e) {
                        found.add(name + ": class file not readable (" + e + ")");
                        continue;
                    }
                    List<String[]> refs;
                    try {
                        refs = constants(bytes);
                    } catch (IOException | RuntimeException e) {
                        found.add(name + ": class file not parseable (" + e + ")");
                        continue;
                    }
                    for (String[] r : refs) {
                        String owner = r[0];
                        String a = ambient(owner);
                        if (a != null) found.add(name + ": references ambient " + owner + (r[1] == null ? "" : "." + r[1]));
                        else if (r[1] != null && owner.equals("java/lang/Class")) found.add(name + ": reflective java/lang/Class." + r[1]);
                        else if (r[1] != null && (owner.equals("java/lang/Math") || owner.equals("java/lang/StrictMath")) && r[1].equals("random"))
                            found.add(name + ": references ambient " + owner + ".random");
                        else if (!jdk(owner)) work.add(owner);
                    }
                    Class<?> c;
                    try {
                        c = Class.forName(name.replace('/', '.'), false, loader);
                    } catch (ClassNotFoundException | LinkageError e) {
                        found.add(name + ": not loadable (" + e + ")");
                        continue;
                    }
                    for (Field f : c.getDeclaredFields())
                        if (Modifier.isStatic(f.getModifiers())) field(f, null, c.getName() + "." + f.getName(), found, seen, new HashSet<>());
                }
            }

            /** {owner internal name, member name or null} for every class constant and member reference of a class file. */
            static List<String[]> constants(byte[] bytes) throws IOException {
                DataInputStream in = new DataInputStream(new ByteArrayInputStream(bytes));
                if (in.readInt() != 0xCAFEBABE) throw new IOException("not a class file");
                in.readUnsignedShort();
                in.readUnsignedShort();
                int n = in.readUnsignedShort();
                String[] utf = new String[n];
                int[] cls = new int[n], refClass = new int[n], refNat = new int[n], natName = new int[n];
                boolean[] isClass = new boolean[n], isRef = new boolean[n];
                for (int i = 1; i < n; i++) {
                    int tag = in.readUnsignedByte();
                    switch (tag) {
                        case 1 -> utf[i] = in.readUTF();
                        case 3, 4 -> in.readInt();
                        case 5, 6 -> { in.readLong(); i++; }
                        case 7 -> { cls[i] = in.readUnsignedShort(); isClass[i] = true; }
                        case 8, 16, 19, 20 -> in.readUnsignedShort();
                        case 9, 10, 11 -> { refClass[i] = in.readUnsignedShort(); refNat[i] = in.readUnsignedShort(); isRef[i] = true; }
                        case 12 -> { natName[i] = in.readUnsignedShort(); in.readUnsignedShort(); }
                        case 15 -> { in.readUnsignedByte(); in.readUnsignedShort(); }
                        case 17, 18 -> { in.readUnsignedShort(); in.readUnsignedShort(); }
                        default -> throw new IOException("unknown constant pool tag " + tag);
                    }
                }
                List<String[]> out = new ArrayList<>();
                for (int i = 1; i < n; i++) {
                    if (isClass[i]) out.add(new String[] { element(utf[cls[i]]), null });
                    if (isRef[i]) out.add(new String[] { element(utf[cls[refClass[i]]]), utf[natName[refNat[i]]] });
                }
                out.removeIf(r -> r[0] == null);
                return out;
            }

            /** The element class of an array descriptor ("[Ljava/lang/String;" is java/lang/String; primitive arrays are null). */
            private static String element(String internal) {
                if (internal == null || !internal.startsWith("[")) return internal;
                String e = internal.replaceFirst("^\\[+", "");
                return e.startsWith("L") && e.endsWith(";") ? e.substring(1, e.length() - 1) : null;
            }
        }

        """;

    static (string Class, string Text) ExampleSource(JsonObject c)
    {
        var contract = Str(c, "contract")!;
        var name = Simple(contract);
        var cls = name + "Example";
        var output = Str(c, "output")!;
        var outType = output == "boolean" ? "boolean" : output;
        var text = $$"""
            package com.mlcpu.rules.example;

            import com.mlcpu.rules.spi.RuleImplementation;

            /**
             * GENERATED EXAMPLE of a human-owned implementation of rule {{Str(c, "ruleId")}} ({{name}}). It compiles against the public
             * contract (module modern-rules) only and runs only when selected: -Pmlcpu.rule.{{Str(c, "ruleId")}}={{ExampleId}} or
             * --mlcpu.rule.{{Str(c, "ruleId")}}={{ExampleId}}. A real implementation replaces the delegation below with its own design
             * and keeps the Registration; rules-conformance runs the faithful cases against every registered implementation.
             */
            public final class {{cls}} implements {{contract}} {
                private final {{contract}} reference = new {{Str(c, "generatedImplementation")}}();

                @Override
                public {{outType}} {{Str(c, "method")}}({{Str(c, "input")}} input) {
                    return reference.{{Str(c, "method")}}(input);
                }

                /** ServiceLoader registration: META-INF/services/com.mlcpu.rules.spi.RuleImplementation. */
                public static final class Registration implements RuleImplementation<{{contract}}> {
                    @Override public String ruleId() { return "{{Str(c, "ruleId")}}"; }
                    @Override public String implementationId() { return "{{ExampleId}}"; }
                    @Override public Class<{{contract}}> contract() { return {{contract}}.class; }
                    @Override public {{contract}} create() { return new {{cls}}(); }
                }
            }

            """;
        return (cls, text);
    }

    static string TestName(int i, JsonObject c) => $"r{i:D3}_{Regex.Replace(Str(c, "ruleId")!, "[^A-Za-z0-9]", "_")}";

    static string ConformanceTest(IReadOnlyList<JsonObject> contracts)
    {
        var sb = new StringBuilder("""
            package com.mlcpu.rules.conformance;

            import static org.junit.jupiter.api.Assertions.assertEquals;
            import static org.junit.jupiter.api.Assertions.assertNotEquals;
            import static org.junit.jupiter.api.Assertions.assertNull;
            import static org.junit.jupiter.api.Assertions.assertSame;
            import static org.junit.jupiter.api.Assertions.assertThrows;
            import static org.junit.jupiter.api.Assertions.assertTrue;

            import com.mlcpu.rules.spi.RuleImplementation;
            import com.mlcpu.rules.spi.RuleSelector;
            import java.util.Map;
            import java.util.ServiceLoader;
            import java.util.TreeMap;
            import java.util.function.Supplier;
            import java.util.stream.Stream;
            import org.junit.jupiter.api.DynamicTest;
            import org.junit.jupiter.api.Test;
            import org.junit.jupiter.api.TestFactory;

            /**
             * GENERATED. DO NOT EDIT. Every implementation of every typed rule (the generated one and each registered example or
             * human one) against the rule's faithful cases, and explicit selection: generated by default, a registered implementation
             * only when named, an unknown name fails. A failing case is a DIFF; nothing here approves or promotes an implementation.
             */
            class RuleContractConformanceTest {
                static <C> Map<String, C> implementations(String ruleId, Class<C> contract, C generated) {
                    Map<String, C> all = new TreeMap<>();
                    all.put(RuleSelector.GENERATED, generated);
                    for (RuleImplementation<?> r : ServiceLoader.load(RuleImplementation.class)) {
                        if (!r.ruleId().equals(ruleId)) continue;
                        assertSame(contract, r.contract(), ruleId + ": " + r.implementationId() + " registers another contract");
                        assertNotEquals(RuleSelector.GENERATED, r.implementationId(), ruleId + ": implementation id 'generated' is reserved");
                        Object instance = r.create();
                        assertTrue(contract.isInstance(instance), ruleId + ": " + r.implementationId() + " does not implement " + contract.getName());
                        assertNull(all.put(r.implementationId(), contract.cast(instance)), ruleId + ": duplicate implementation id " + r.implementationId());
                    }
                    return all;
                }

                static <C> void selection(String ruleId, Class<C> contract, Supplier<C> generated) {
                    String key = RuleSelector.PROPERTY_PREFIX + ruleId;
                    String prior = System.getProperty(key);
                    try {
                        System.clearProperty(key);
                        RuleSelector.reset();
                        assertSame(generated.get().getClass(), RuleSelector.select(ruleId, contract, generated).getClass(), ruleId + ": the default must be generated");
                        for (Map.Entry<String, C> e : implementations(ruleId, contract, generated.get()).entrySet()) {
                            System.setProperty(key, e.getKey());
                            RuleSelector.reset();
                            assertSame(e.getValue().getClass(), RuleSelector.select(ruleId, contract, generated).getClass(), ruleId + ": selecting " + e.getKey());
                        }
                        System.setProperty(key, "no-such-implementation");
                        RuleSelector.reset();
                        assertThrows(IllegalStateException.class, () -> RuleSelector.select(ruleId, contract, generated), ruleId + ": an unknown implementation must fail");
                    } finally {
                        if (prior == null) System.clearProperty(key); else System.setProperty(key, prior);
                        RuleSelector.reset();
                    }
                }

            """);
        for (int i = 0; i < contracts.Count; i++)
        {
            var c = contracts[i];
            var id = Str(c, "ruleId")!;
            var contract = Str(c, "contract")!;
            var gen = Str(c, "generatedImplementation")!;
            var method = Str(c, "method")!;
            var name = TestName(i, c);
            sb.Append($"    @Test\n    void {name}_selection() {{\n        selection(\"{id}\", {contract}.class, {gen}::new);\n    }}\n\n");
            sb.Append($$"""
                    @TestFactory
                    Stream<DynamicTest> {{name}}_stateless() {
                        return implementations("{{id}}", {{contract}}.class, new {{gen}}()).entrySet().stream()
                                .map(impl -> DynamicTest.dynamicTest(impl.getKey() + " holds no mutable state", () ->
                                        assertEquals(java.util.List.of(), com.mlcpu.rules.verify.ProviderPurity.violations(impl.getValue()), "{{id}} " + impl.getKey())));
                    }


                """);
            if (Str(c, "cases") is not { } cases)
            {
                sb.Append($"    // {id}: no faithful cases were generated; implementations are checked for registration and selection only.\n\n");
                continue;
            }
            var compare = Str(c, "output") == "boolean"
                ? $"assertEquals(c.expected(), impl.getValue().{method}(c.input()), \"{id} \" + impl.getKey() + \" \" + c.id())"
                : Str(c, "kind") == "calculation"
                    ? $"assertEquals(0, c.expected().value().compareTo(impl.getValue().{method}(c.input()).value()), () -> \"{id} \" + impl.getKey() + \" \" + c.id() + \": expected \" + c.expected().value())"
                    : $"assertEquals(c.expected(), impl.getValue().{method}(c.input()), \"{id} \" + impl.getKey() + \" \" + c.id())";
            sb.Append($$"""
                    @TestFactory
                    Stream<DynamicTest> {{name}}_cases() {
                        var cases = {{cases}}.cases();
                        assertTrue(!cases.isEmpty(), "{{id}}: no faithful cases");
                        return implementations("{{id}}", {{contract}}.class, new {{gen}}()).entrySet().stream().flatMap(impl -> cases.stream()
                                .map(c -> DynamicTest.dynamicTest(impl.getKey() + " " + c.id(), () -> {{compare}})));
                    }


                """);
        }
        return sb.Append("}\n").ToString();
    }

    static string JavaStringArray(IEnumerable<string> items) => string.Join(", ", items.Select(i => "\"" + i.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));

    static string ArchitectureTest() => $$"""
        package com.mlcpu.rules.conformance;

        import static org.junit.jupiter.api.Assertions.assertEquals;
        import static org.junit.jupiter.api.Assertions.assertFalse;

        import java.io.IOException;
        import java.nio.file.Files;
        import java.nio.file.Path;
        import java.util.ArrayList;
        import java.util.List;
        import java.util.regex.Matcher;
        import java.util.regex.Pattern;
        import java.util.stream.Stream;
        import org.junit.jupiter.api.Assumptions;
        import org.junit.jupiter.api.Test;

        /**
         * GENERATED. DO NOT EDIT. Rule code (generated, example and human) depends on the public contracts only: no program
         * state, compatibility runtime, framework, direct I/O, clock or randomness (comments and literals excluded). The human
         * module lives outside the generated root and depends on :{{RulesModule}} only.
         */
        class RuleArchitectureTest {
            static final List<String> FORBIDDEN = List.of({{JavaStringArray(Forbidden)}});
            /** Ambient state in providers; the selection SPI and the verifier are the only exemptions. A hit rejects; none is no proof. */
            static final List<String> AMBIENT = List.of({{JavaStringArray(Ambient)}});
            static final Path ROOT = Path.of(System.getProperty("mlcpu.generated.root")).toAbsolutePath().normalize();

            @Test
            void generatedRuleCodeIsPure() throws IOException {
                assertEquals(List.of(), violations(ROOT.resolve("{{RulesModule}}/src")));
            }

            @Test
            void exampleIsPure() throws IOException {
                assertEquals(List.of(), violations(ROOT.resolve("{{ExampleModule}}/src")));
            }

            @Test
            void humanImplementationIsPureAndOutsideTheGeneratedRoot() throws IOException {
                String human = System.getProperty("mlcpu.human.root");
                Assumptions.assumeTrue(human != null && !human.isBlank(), "no :{{HumanProject}} module is included");
                Path dir = Path.of(human).toAbsolutePath().normalize();
                assertFalse(dir.startsWith(ROOT), "the human root must be outside the generated root: " + dir);
                assertEquals(List.of(), violations(dir.resolve("src")));
                Matcher m = Pattern.compile("project\\(\\s*\"([^\"]+)\"\\s*\\)").matcher(Files.readString(dir.resolve("build.gradle.kts")));
                List<String> projects = new ArrayList<>();
                while (m.find()) if (!m.group(1).equals(":{{RulesModule}}")) projects.add(m.group(1));
                assertEquals(List.of(), projects, "the human module may depend on :{{RulesModule}} only");
            }

            static List<String> violations(Path dir) throws IOException {
                List<String> found = new ArrayList<>();
                if (!Files.isDirectory(dir)) return found;
                try (Stream<Path> files = Files.walk(dir)) {
                    for (Path f : files.filter(p -> p.toString().endsWith(".java")).sorted().toList()) {
                        String code = strip(Files.readString(f));
                        String rel = ROOT.relativize(f.toAbsolutePath().normalize()).toString().replace('\\', '/');
                        for (String token : FORBIDDEN) if (code.contains(token)) found.add(rel + ": " + token);
                        String unix = f.toString().replace('\\', '/');
                        if (unix.contains("/com/mlcpu/rules/spi/") || unix.contains("/com/mlcpu/rules/verify/")) continue;
                        for (String token : AMBIENT) if (code.contains(token)) found.add(rel + ": ambient " + token);
                    }
                }
                return found;
            }

            static String strip(String s) {
                StringBuilder sb = new StringBuilder(s.length());
                for (int i = 0; i < s.length(); i++) {
                    char c = s.charAt(i);
                    if (c == '/' && i + 1 < s.length() && s.charAt(i + 1) == '/') {
                        while (i < s.length() && s.charAt(i) != '\n') i++;
                        sb.append('\n');
                    } else if (c == '/' && i + 1 < s.length() && s.charAt(i + 1) == '*') {
                        int end = s.indexOf("*/", i + 2);
                        i = end < 0 ? s.length() : end + 1;
                        sb.append(' ');
                    } else if (c == '"' && s.startsWith("\"\"\"", i)) {
                        int end = s.indexOf("\"\"\"", i + 3);
                        i = end < 0 ? s.length() : end + 2;
                        sb.append("\"\"");
                    } else if (c == '"' || c == '\'') {
                        for (i++; i < s.length() && s.charAt(i) != c && s.charAt(i) != '\n'; i++) if (s.charAt(i) == '\\') i++;
                        sb.append(c).append(c);
                    } else {
                        sb.append(c);
                    }
                }
                return sb.toString();
            }
        }

        """;

    static string LockTest() => $$"""
        package com.mlcpu.rules.conformance;

        import static org.junit.jupiter.api.Assertions.assertEquals;
        import static org.junit.jupiter.api.Assertions.assertFalse;

        import java.io.IOException;
        import java.nio.file.Files;
        import java.nio.file.Path;
        import java.security.MessageDigest;
        import java.util.ArrayList;
        import java.util.HexFormat;
        import java.util.List;
        import java.util.Map;
        import java.util.TreeMap;
        import java.util.regex.Matcher;
        import java.util.regex.Pattern;
        import java.util.stream.Stream;
        import org.junit.jupiter.api.Test;

        /**
         * GENERATED. DO NOT EDIT. TG3 for this export: every published file still has the hash recorded in {{LockPath}}, and the
         * generated rule modules hold no unlisted source. Change the COBOL or the generator and republish; human code belongs in
         * the :{{HumanProject}} root outside this folder.
         */
        class GeneratedLockTest {
            static final Path ROOT = Path.of(System.getProperty("mlcpu.generated.root")).toAbsolutePath().normalize();

            static Map<String, String> lock() throws IOException {
                Matcher m = Pattern.compile("\"path\"\\s*:\\s*\"([^\"]+)\"\\s*,\\s*\"sha256\"\\s*:\\s*\"([0-9a-f]{64})\"")
                        .matcher(Files.readString(ROOT.resolve("{{LockPath}}")));
                Map<String, String> files = new TreeMap<>();
                while (m.find()) files.put(m.group(1), m.group(2));
                assertFalse(files.isEmpty(), "{{LockPath}} lists no files");
                return files;
            }

            @Test
            void generatedFilesAreUnchanged() throws Exception {
                List<String> changed = new ArrayList<>();
                for (Map.Entry<String, String> e : lock().entrySet()) {
                    Path f = ROOT.resolve(e.getKey());
                    if (!Files.isRegularFile(f)) { changed.add(e.getKey() + " (missing)"); continue; }
                    String sha = HexFormat.of().formatHex(MessageDigest.getInstance("SHA-256").digest(Files.readAllBytes(f)));
                    if (!sha.equals(e.getValue())) changed.add(e.getKey() + " (edited)");
                }
                assertEquals(List.of(), changed, "regenerated sources were edited");
            }

            @Test
            void generatedRuleModulesHoldNoUnlistedSources() throws IOException {
                Map<String, String> lock = lock();
                List<String> extra = new ArrayList<>();
                for (String module : List.of("{{RulesModule}}", "{{ExampleModule}}", "{{ConformanceModule}}")) {
                    Path src = ROOT.resolve(module).resolve("src");
                    if (!Files.isDirectory(src)) continue;
                    try (Stream<Path> files = Files.walk(src)) {
                        for (Path f : files.filter(Files::isRegularFile).sorted().toList()) {
                            String rel = ROOT.relativize(f.toAbsolutePath().normalize()).toString().replace('\\', '/');
                            if (!lock.containsKey(rel)) extra.add(rel);
                        }
                    }
                }
                assertEquals(List.of(), extra, "sources were added to generated rule modules; put them in the :{{HumanProject}} root");
            }
        }

        """;

    static string HandoffDoc(string slug, IReadOnlyList<JsonObject> contracts, bool partitioned, JsonObject? example)
    {
        var sb = new StringBuilder($$"""
            # Typed rule handoff

            Generated deterministically from the resolved COBOL model; no model service is involved. Contracts, reference
            implementations, bindings and faithful cases are generated and locked (`{{LockPath}}`). Your implementation lives in
            a separate Gradle module **outside this folder**; publishing replaces this folder but never writes your module.

            | Module | Owner | Contents |
            |---|---|---|
            | `:{{RulesModule}}` | generated | contracts (`evaluate`), input/result records, `*Generated` reference implementations, `*Rules` bindings, `*Cases`, `com.mlcpu.rules.spi` |
            | `:{{ExampleModule}}` | generated | a minimal implementation registered as `{{ExampleId}}` (copy it; do not edit it here) |
            | `:{{ConformanceModule}}` | generated | contract cases x every implementation, selection, architecture and lock tests |
            | `:{{HumanProject}}` | **you** | `../{{slug}}-human` by default, or `-Pmlcpu.humanRules=<dir>`; included only when it has `build.gradle.kts` |

            ## Rules

            Each rule is a slice of a program, not a capability. Names are provisional (from COBOL names) until reviewed.
            Inputs are values the program supplies at the call: numeric items as `BigDecimal`, alphanumeric items as their exact
            byte image (ISO-8859-1, full declared length, trailing spaces kept). Rounding, truncation and SIZE ERROR stay in the
            generated program, which stores the result.

            | Rule | Kind | Contract | Input | Output | COBOL | Cases | Requires |
            |---|---|---|---|---|---|---|---|

            """);
        foreach (var c in contracts)
        {
            var inputs = string.Join(", ", c["inputs"]?.AsArray().Select(i => $"{i?["name"]}: {Simple(i?["javaType"]?.GetValue<string>() ?? "?")} ({i?["cobolName"]} {i?["picture"]} {i?["usage"]})".Trim()) ?? []);
            var prov = c["provenance"];
            var where = $"{prov?["paragraph"]} lines {string.Join(",", prov?["cobolLines"]?.AsArray().Select(x => x?.ToString()) ?? [])}";
            var requires = string.Join(", ", c["requires"]?.AsArray().Select(x => x?.ToString()) ?? []);
            sb.Append($"| `{Str(c, "ruleId")}` | {Str(c, "kind")} | `{Simple(Str(c, "contract")!)}.{Str(c, "method")}` | {inputs.Replace("|", "/")} | `{Simple(Str(c, "output")!)}` | {where.Replace("|", "/")} | {(Str(c, "cases") is null ? "none" : "`" + Simple(Str(c, "cases")!) + "`")} | {(requires.Length == 0 ? "-" : requires.Replace("|", "/"))} |\n");
        }
        var exampleId = example is null ? "<ruleId>" : Str(example, "ruleId");
        var run = partitioned ? "gradle :estate-app:run" : "gradle run";
        sb.Append($$"""

            Skipped sites and their reasons are in `modernization/rule-extraction.json`; a skipped site stays in the generated
            program and is a review item. Hashes of every contract source are in `modernization/rule-contracts.json`.
            `recordCharset=LATIN1` means the rule is called only when records are LATIN1-encoded; under CP037 the generated
            program evaluates the faithful comparison itself, so a selected implementation is not reached there.

            ## Implement a rule

            1. Create `../{{slug}}-human/build.gradle.kts`:

               ```kotlin
            {{Indent(ExampleBuild(generated: false), "   ")}}
               ```

            2. Copy `{{ExampleModule}}/src` into it, rename the class, write your implementation of the contract and set
               `implementationId()` to e.g. `human`. Keep `META-INF/services/com.mlcpu.rules.spi.RuleImplementation` listing
               your `Registration`. Import only `com.mlcpu.rules.*` (and the JDK); the build cannot see program state.
            3. Test it against the faithful cases and the architecture rules: `gradle :{{ConformanceModule}}:test`
               (all implementations; failures are DIFFs, not approvals).
            4. Run it in a job, explicitly:

               ```
               {{run}} -Pmlcpu.rule.{{exampleId}}=human --args="--mlcpu.job=<JOB> --mlcpu.data-dir=<dir>"
               {{run}} --args="--mlcpu.rule.{{exampleId}}=human --mlcpu.job=<JOB> --mlcpu.data-dir=<dir>"
               gradle test -Pmlcpu.rule.{{exampleId}}=human     # characterization: the program with your rule vs Baseline_*
               ```

            Without a selection every rule runs its generated implementation. An unknown implementation id fails at program
            construction; there is no silent fallback. Selecting an implementation is not a promotion: equivalence evidence
            and human review remain separate, and any differing output is a DIFF.

            ## Provider state

            An implementation must be stateless: the run-unit snapshots and the serialized compatibility bridge restore program
            and runtime state, **not** rule providers or the selector's cache, and one provider instance serves every
            invocation. `rules-conformance` therefore fails, and the generated job runner refuses to start a job selecting it,
            when a provider (or anything it references) has a non-final field, an array, a JDK type other than String,
            BigDecimal, BigInteger or a boxed primitive (collections, atomics, functions), a lambda, when its compiled code (or
            that of any non-JDK class it references) references System, threads, concurrency, reflection or class loading,
            clocks, I/O or randomness, or when a class file cannot be read; the conformance tests additionally scan sources
            for ambient-state tokens (`modernization/rule-contracts.json`, `forbiddenAmbientInProviders`). SHADOW strangler
            runs refuse any custom rule selection.
            These checks are conservative heuristics, not a proof of purity, and isolation of arbitrary providers is not
            claimed. Selection through `-Pmlcpu.rule.*` in Gradle tests bypasses the job runner's check; the failing
            purity test still fails the build.

            """);
        return sb.ToString();
    }

    static string Indent(string text, string pad) => string.Join("\n", text.TrimEnd('\n').Split('\n').Select(l => l.Length == 0 ? "" : pad + l));
}
