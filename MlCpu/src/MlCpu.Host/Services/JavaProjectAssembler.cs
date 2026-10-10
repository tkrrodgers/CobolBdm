using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using MlCpu.Cobol.Analysis.Entities;
using MlCpu.Cobol.Analysis.Projections;
using MlCpu.CobolKb.Knowledge;

namespace MlCpu.Host.Services;

public sealed record JavaProjectFile(string RelPath, string Content, string Language);

public sealed record JavaProject(string Name, string Package, IReadOnlyList<JavaProjectFile> Files, int Programs, int Compiled, int Passed, int Lowered, int Abstained,
    int CharacterizationSeeds, IReadOnlyList<ProjectionLedger>? Projections = null);

/// <param name="ProgramSources">Program id → COBOL path, for the DD binding and replay plans.</param>
/// <param name="DomainPartition">Apply projection 10 (Gradle modules per business domain).</param>
public sealed record AssemblyOptions(string? ControlCardsRoot = null, IReadOnlyDictionary<string, string>? ProgramSources = null, bool DomainPartition = false,
    string? DomainEvidenceRoot = null, string? OrchestrationEvidenceRoot = null, string AsOf = "2026-10-01", string? CapabilitiesRoot = null,
    string? RegressionNote = null);

/// <summary>
/// Assembles a Gradle Java 21 Spring application (§8): shell and runtime, faithful core (com.mlcpu.core.Prog_*), frozen
/// Baseline_* comparators, partition plans, characterization tests (§8.12), the harvested JCL jobs as typed step lists,
/// the comparison data layer (DdBindingPlan.json, replay/ReplayPlan.json), then projections 2–10 and their ledgers.
/// </summary>
public sealed partial class JavaProjectAssembler
{
    public const string CharacterizationClock = "2026-01-01T00:00:00Z";

    [GeneratedRegex(@"^package\s+com\.mlcpu\.gen\s*;", RegexOptions.Multiline)] private static partial Regex GenPackage();
    [GeneratedRegex(@"public final class (\w+)")] private static partial Regex ClassName();

    public JavaProject Assemble(string name, IReadOnlyList<JavaTranspileResult> results, IReadOnlyDictionary<string, string> runtime,
        JobScope? job = null, string? controlCardsRoot = null) =>
        Assemble(name, results, runtime, job is null ? [] : [job], new AssemblyOptions(controlCardsRoot));

    public JavaProject Assemble(string name, IReadOnlyList<JavaTranspileResult> results, IReadOnlyDictionary<string, string> runtime,
        IReadOnlyList<JobScope> jobs, AssemblyOptions options)
    {
        var slug = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-') + "-spring";
        var compiled = results.Where(r => !string.IsNullOrEmpty(r.JavaSource)).OrderBy(r => r.Program, StringComparer.Ordinal).ToList();
        var files = new ProjectFiles();
        files.Set("settings.gradle.kts", $"rootProject.name = \"{slug}\"\n");
        files.Set("build.gradle.kts", BuildScript());
        files.Set("src/main/resources/application.properties", Properties());
        files.Set("src/main/java/com/mlcpu/app/Application.java", ApplicationSource());
        files.Set("src/main/java/com/mlcpu/app/JobRunner.java", JobRunnerSource());
        files.Set("src/test/java/com/mlcpu/app/ApplicationContextTest.java", ContextTestSource());
        foreach (var (file, text) in runtime.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            files.Set($"src/main/java/com/mlcpu/rt/{file}", text);

        var summary = new StringBuilder(BulkPartitionPlanner.SummaryHeader + "\n");
        var units = new List<ProgramUnit>();
        int seeds = 0;
        foreach (var r in compiled)
        {
            var src = GenPackage().Replace(r.JavaSource!, "package com.mlcpu.core;");
            var cls = ClassName().Match(src) is { Success: true } m ? m.Groups[1].Value : "Prog_" + r.Program;
            files.Set($"src/main/java/com/mlcpu/core/{cls}.java", src);
            var baseline = cls.Replace("Prog_", "Baseline_");
            files.Set($"src/main/java/com/mlcpu/core/{baseline}.java",
                src.Replace($"public final class {cls} ", $"public final class {baseline} ")
                   .Replace("/** Generated from", "/** Frozen pre-normalization comparator. Generated from"));
            if (r.Model is null || r.Graph is null) continue;
            var plan = BulkPartitionPlanner.Plan(r.Model, r.Graph);
            units.Add(new ProgramUnit(r.Program, cls, r.Model, r.Graph, plan));
            files.Set($"partition-plan/{r.Program}.json", JsonSerializer.Serialize(plan, Json) + "\n");
            summary.Append(BulkPartitionPlanner.SummaryRow(plan)).Append('\n');
            var vectors = CharacterizationSeedBuilder.Build(r.Model, r.Graph, plan);
            if (vectors.Count == 0) continue;
            seeds += vectors.Count;
            files.Set($"src/test/java/com/mlcpu/core/{cls.Replace("Prog_", "")}CharacterizationTest.java",
                JavaCharacterizationRenderer.RenderTest(r.Program, vectors, CharacterizationClock));
        }
        files.Set("partition-plan/summary.csv", summary.ToString());

        if (options.ControlCardsRoot is { } ctl && Directory.Exists(ctl))
            foreach (var member in jobs.SelectMany(j => j.Jcl?.Steps ?? []).SelectMany(s => s.Datasets).Select(d => d.Member).OfType<string>().Distinct())
            {
                var card = Directory.EnumerateFiles(ctl).FirstOrDefault(f => string.Equals(Path.GetFileNameWithoutExtension(f), member, StringComparison.OrdinalIgnoreCase));
                if (card is not null) files.Set($"ctl/{member}.ctl", File.ReadAllText(card));
            }

        var packets = new Dictionary<string, System.Text.Json.Nodes.JsonObject>(StringComparer.OrdinalIgnoreCase);
        var compiledIds = compiled.Select(r => r.Program).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (options.ProgramSources is { } sources)
            foreach (var j in jobs.Where(j => j.Jcl is not null))
            {
                var suffix = jobs.Count > 1 ? "-" + j.Name : "";
                var plan = DdBindingPlanner.Plan(j.Jcl!, sources, options.AsOf, out var models);
                files.Set($"DdBindingPlan{suffix}.json", DdBindingPlanner.ToJson(plan));
                var replay = ReplayPlanBuilder.Build(plan, models, slug);
                files.Set($"replay/ReplayPlan{suffix}.json", ReplayPlanBuilder.BuildJson(plan, models, slug));
                foreach (var (pid, model) in models)
                    if (compiledIds.Contains(pid) && !packets.ContainsKey(pid) && sources.TryGetValue(pid, out var src))
                        packets[pid] = RequirementsPacketBuilder.Build(model, MlCpu.Cobol.Analysis.ProgramGraphBuilder.Build(model), src, plan, replay, options.RegressionNote);
            }

        var (capabilities, capabilityLedger) = NormalizedCapabilities.Import(files, options.CapabilitiesRoot, packets);
        files.Set("normalization/ledger.json", ProjectionJson.Ledger(capabilityLedger));
        files.Set("src/main/java/com/mlcpu/app/JobDefinition.java", JobDefinitionSource(jobs.Select(j => j.Jcl).OfType<JclJob>().ToList(), compiled, capabilities));
        if (capabilities.Count > 0) files.Set("build.gradle.kts", BuildScript(capabilities, NormalizedCapabilities.SharedPackages(options.CapabilitiesRoot)));

        var ledgers = ProjectionPipeline.Run(files, units, slug, new ProjectionOptions(options.DomainPartition, options.DomainEvidenceRoot, options.OrchestrationEvidenceRoot))
            .Prepend(capabilityLedger).ToList();

        files.Set("MIGRATION.md", Migration(name, slug, results, jobs, ledgers, options.DomainPartition, capabilities));
        var frontiers = results.SelectMany(r => r.AbstainedSteps.Select(a => (r.Program, a))).ToList();
        if (frontiers.Count > 0)
            files.Set("FRONTIERS.md", "# Frontiers\n\nStatements the transpiler abstained on. Each stops the program with `CobRt.frontier` if reached.\n\n" +
                string.Join("\n", frontiers.Select(f => $"- `{f.Program}` {f.a}")) + "\n");
        return new JavaProject(slug, "com.mlcpu.core", files.All().Select(f => new JavaProjectFile(f.Path, f.Content, f.Language)).ToList(),
            results.Count, results.Count(r => r.Compiled), results.Count(r => r.Verdict == "PASS"), results.Sum(r => r.Lowered), results.Sum(r => r.Abstained), seeds, ledgers);
    }

    static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    static string BuildScript() => """
        plugins {
            application
            java
        }

        java {
            sourceCompatibility = JavaVersion.VERSION_21
            targetCompatibility = JavaVersion.VERSION_21
        }

        repositories { mavenCentral() }

        dependencies {
            implementation(platform("org.springframework.boot:spring-boot-dependencies:3.3.4"))
            implementation("org.springframework.boot:spring-boot-starter")
            testImplementation(platform("org.junit:junit-bom:5.11.3"))
            testImplementation("org.springframework.boot:spring-boot-starter-test")
            testImplementation("org.junit.jupiter:junit-jupiter")
            testRuntimeOnly("org.junit.platform:junit-platform-launcher")
        }

        val verifyJava21 by tasks.registering {
            doLast { if (JavaVersion.current() != JavaVersion.VERSION_21) throw GradleException("Java 21 is required (found ${JavaVersion.current()})") }
        }
        tasks.named("compileJava") { dependsOn(verifyJava21) }

        application {
            mainClass.set("com.mlcpu.app.Application")
            applicationDefaultJvmArgs = listOf("-Xss64m")
        }

        tasks.withType<JavaCompile>().configureEach { options.encoding = "UTF-8" }
        tasks.test {
            useJUnitPlatform()
            jvmArgs("-Xss64m")
            testLogging { events("passed", "failed", "skipped") }
            systemProperty("mlcpu.characterization.dir", "${projectDir}/characterization")
        }
        tasks.named<JavaExec>("run") { workingDir = projectDir }

        """;

    /// <summary>
    /// With normalized capabilities: JaCoCo and PIT over the normalized classes only (shared platform/domain/data packages
    /// plus each capability's own classes), driven by their tests. tools/Collect-CapabilityEvidence.ps1 reads the reports.
    /// </summary>
    static string BuildScript(IReadOnlyList<CapabilityImport> capabilities, IReadOnlyList<string> sharedPackages)
    {
        var shared = sharedPackages.Count > 0 ? sharedPackages.Select(p => p + ".*").ToArray() : ["com.mlcpu.carddemo.*"];
        var targets = shared.Concat(capabilities.Select(c => c.Package + ".*")).Select(t => $"\"{t}\"");
        var excluded = capabilities.Select(c => $"\"{c.Package}.legacy.*\"").Concat(capabilities.Select(c => $"\"{c.Package}.*Workflow*\""));
        var tests = shared.Concat(capabilities.Select(c => c.Package + ".*"))
            .Select(t => $"\"{t}\"");
        var script = BuildScript().Replace("\r\n", "\n")
            .Replace("plugins {\n    application\n    java\n}", "plugins {\n    application\n    java\n    jacoco\n    id(\"info.solidsoft.pitest\") version \"1.19.0\"\n}");
        if (!script.Contains("info.solidsoft.pitest", StringComparison.Ordinal)) throw new InvalidOperationException("build script plugins block not found");
        return script + $$"""
            // Normalized capabilities: coverage and mutation evidence for the promotion policy.
            tasks.jacocoTestReport {
                dependsOn(tasks.test)
                reports { xml.required.set(true); html.required.set(false); csv.required.set(false) }
            }

            pitest {
                junit5PluginVersion.set("1.2.1")
                targetClasses.set(listOf({{string.Join(", ", targets)}}))
                excludedClasses.set(listOf({{string.Join(", ", excluded)}}))
                targetTests.set(listOf({{string.Join(", ", tests)}}))
                excludedTestClasses.set(listOf("*WorkflowTest", "*CharacterizationTest"))
                threads.set(Runtime.getRuntime().availableProcessors().coerceAtMost(24))
                outputFormats.set(listOf("XML"))
                timestampedReports.set(false)
                jvmArgs.set(listOf("-Xss64m"))
                timeoutConstInMillis.set(20000)
            }

            """;
    }

    static string Properties() => """
        spring.main.banner-mode=off
        spring.main.web-application-type=none
        logging.level.root=WARN
        # Job execution (JobRunner)
        mlcpu.run=true
        # Job to run (empty = the first job in JobDefinition.JOBS)
        mlcpu.job=
        mlcpu.data-dir=data
        mlcpu.sysout-dir=sysout
        mlcpu.ctl-dir=ctl
        # Comma-separated step ids to run (empty = all unconditional steps)
        mlcpu.steps=
        # FIXED=<ISO offset date-time> pins FUNCTION CURRENT-DATE / ACCEPT; empty = wall clock
        mlcpu.clock=
        # zos (stored bytes) or gnucobol (digits + separate sign)
        mlcpu.display-numeric=zos
        # yes = TRUNC(STD); no = full binary capacity
        mlcpu.binary-truncate=yes
        # legacy | normalized; empty = each program's default from the capability promotion policy (JobDefinition.DEFAULT_IMPLEMENTATIONS)
        mlcpu.implementation=

        """;

    static string ApplicationSource() => """
        package com.mlcpu.app;

        import org.springframework.boot.SpringApplication;
        import org.springframework.boot.autoconfigure.SpringBootApplication;

        @SpringBootApplication
        public class Application {
            public static void main(String[] args) {
                System.exit(SpringApplication.exit(SpringApplication.run(Application.class, args)));
            }
        }

        """;

    static string JobRunnerSource() => """
        package com.mlcpu.app;

        import com.mlcpu.rt.CobClock;
        import com.mlcpu.rt.CobConfig;
        import com.mlcpu.rt.JobEngine;
        import java.nio.file.Path;
        import java.util.Arrays;
        import java.util.LinkedHashSet;
        import java.util.List;
        import java.util.Set;
        import org.springframework.beans.factory.annotation.Value;
        import org.springframework.boot.ApplicationArguments;
        import org.springframework.boot.ApplicationRunner;
        import org.springframework.boot.ExitCodeGenerator;
        import org.springframework.stereotype.Component;

        /** Runs the job's steps in JCL order; the process exit code is the highest step return code. */
        @Component
        public class JobRunner implements ApplicationRunner, ExitCodeGenerator {
            @Value("${mlcpu.run:true}") boolean run;
            @Value("${mlcpu.job:}") String job;
            @Value("${mlcpu.data-dir:data}") String dataDir;
            @Value("${mlcpu.sysout-dir:sysout}") String sysoutDir;
            @Value("${mlcpu.ctl-dir:ctl}") String ctlDir;
            @Value("${mlcpu.steps:}") String steps;
            @Value("${mlcpu.clock:}") String clock;
            @Value("${mlcpu.display-numeric:zos}") String displayNumeric;
            @Value("${mlcpu.binary-truncate:yes}") String binaryTruncate;
            @Value("${mlcpu.implementation:}") String implementation;

            private int exitCode;

            @Override
            public void run(ApplicationArguments args) throws Exception {
                if (!run) return;
                CobConfig.DISPLAY_NUMERIC = displayNumeric;
                CobConfig.BINARY_TRUNCATE = !"no".equalsIgnoreCase(binaryTruncate);
                if (!clock.isBlank()) {
                    String[] kv = clock.split("=", 2);
                    CobClock.configure(kv[0], kv.length > 1 ? kv[1] : "");
                }
                Set<String> only = new LinkedHashSet<>();
                if (!steps.isBlank()) only.addAll(Arrays.asList(steps.split("\\s*,\\s*")));
                JobEngine.IMPLEMENTATION_BY_PROGRAM.clear();
                JobEngine.MODERN_PROGRAMS.putAll(JobDefinition.modernPrograms());
                if (implementation.isBlank()) {
                    JobEngine.IMPLEMENTATION = "legacy";
                    JobEngine.IMPLEMENTATION_BY_PROGRAM.putAll(JobDefinition.DEFAULT_IMPLEMENTATIONS);
                } else {
                    JobEngine.IMPLEMENTATION = implementation.toLowerCase();
                }
                String name = job.isBlank() ? JobDefinition.JOB : job.toUpperCase();
                System.out.println("JOB " + name + " clock=" + (clock.isBlank() ? "WALL" : clock) + " display-numeric=" + displayNumeric
                        + " binary-truncate=" + binaryTruncate + " CURRENT-DATE=" + CobClock.currentDate()
                        + " implementation=" + (implementation.isBlank() ? "policy" + JobDefinition.DEFAULT_IMPLEMENTATIONS : implementation));
                JobEngine engine = new JobEngine(Path.of(dataDir), Path.of(sysoutDir), Path.of(ctlDir), JobDefinition.programs(), System.out::println);
                List<JobEngine.StepResult> results = engine.run(JobDefinition.steps(name), only);
                exitCode = results.stream().mapToInt(JobEngine.StepResult::returnCode).max().orElse(0);
                System.out.println("MAXCC=" + exitCode);
            }

            @Override
            public int getExitCode() {
                return exitCode;
            }
        }

        """;

    static string ContextTestSource() => """
        package com.mlcpu.app;

        import static org.junit.jupiter.api.Assertions.assertFalse;

        import org.junit.jupiter.api.Test;
        import org.springframework.boot.test.context.SpringBootTest;

        @SpringBootTest(properties = "mlcpu.run=false")
        class ApplicationContextTest {
            @Test
            void contextLoadsAndJobIsDefined() {
                assertFalse(JobDefinition.JOB.isBlank());
            }
        }

        """;

    static string JobDefinitionSource(IReadOnlyList<JclJob> jobs, IReadOnlyList<JavaTranspileResult> programs, IReadOnlyList<CapabilityImport>? capabilities = null)
    {
        var classes = programs.ToDictionary(r => r.Program, r => "Prog_" + new string(r.Program.Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()), StringComparer.OrdinalIgnoreCase);
        var modern = (capabilities ?? []).ToDictionary(c => c.ProgramId, StringComparer.OrdinalIgnoreCase);
        var sb = new StringBuilder("""
            package com.mlcpu.app;

            import com.mlcpu.core.*;
            import com.mlcpu.rt.CobolBase;
            import com.mlcpu.rt.JobEngine;
            import com.mlcpu.rt.JobEngine.Dd;
            import java.util.List;
            import java.util.Map;
            import java.util.function.Supplier;

            /** Generated from the harvested JCL: unconditional steps in order, PROCs expanded, symbols substituted. No reflection. */
            public final class JobDefinition {
                private JobDefinition() {}


            """);
        string Id(string job) => (char.IsDigit(job[0]) ? "J_" : "") + Regex.Replace(job.ToUpperInvariant(), "[^A-Z0-9]", "_");
        sb.Append($"    public static final String JOB = {Lit(jobs.FirstOrDefault()?.Name ?? "NONE")};\n");
        sb.Append($"    public static final List<String> JOBS = List.of({string.Join(", ", jobs.Select(j => Lit(j.Name)))});\n\n");
        sb.Append("    /** Default implementation per program with a normalized capability: \"normalized\" only where the promotion policy passed. */\n");
        sb.Append("    public static final Map<String, String> DEFAULT_IMPLEMENTATIONS = Map.ofEntries(")
          .Append(string.Join(", ", modern.Values.OrderBy(c => c.ProgramId, StringComparer.Ordinal).Select(c => $"Map.entry({Lit(c.ProgramId)}, {Lit(c.Implementation)})"))).Append(");\n\n");
        sb.Append("    public static List<JobEngine.Step> steps(String job) {\n        return switch (job.toUpperCase()) {\n");
        foreach (var j in jobs) sb.Append($"            case {Lit(j.Name.ToUpperInvariant())} -> {Id(j.Name)}.steps();\n");
        sb.Append("            default -> throw new IllegalArgumentException(\"unknown job \" + job + \"; jobs: \" + JOBS);\n        };\n    }\n");
        foreach (var job in jobs)
        {
            var steps = job.Expanded.ToList();
            var ids = new List<string>();
            foreach (var s in steps)
            {
                var id = Regex.Replace(s.QualifiedStep, "[^A-Z0-9]", "_");
                if (ids.Contains(id)) id += "_" + s.Order;
                ids.Add(id);
            }
            var lrecl = steps.SelectMany(s => s.Datasets).Where(d => d.Dsn is not null && d.Lrecl is not null)
                .GroupBy(d => d.Dsn!).ToDictionary(g => g.Key, g => g.First().Lrecl!);
            sb.Append($"\n    /** Job {job.Name} ({Path.GetFileName(job.Path)}). */\n    public static final class {Id(job.Name)} {{\n        private {Id(job.Name)}() {{}}\n\n");
            sb.Append("        public enum StepId { ").Append(ids.Count == 0 ? "NONE" : string.Join(", ", ids)).Append(" }\n\n");
            sb.Append("        public static List<JobEngine.Step> steps() {\n            return List.of(\n");
            for (int i = 0; i < steps.Count; i++)
            {
                var s = steps[i];
                var factory = s.Program is not null && classes.TryGetValue(s.Program, out var cls) ? $"{cls}::new" : "null";
                var normalized = s.Program is not null && modern.TryGetValue(s.Program, out var cap) && cap.Kind == "step" ? $"{cap.StepClass}::new, " : "";
                var dds = s.Datasets.Select(d =>
                {
                    var l = d.Lrecl ?? (d.Dsn is not null ? lrecl.GetValueOrDefault(d.Dsn) : null);
                    return $"new Dd({Lit(d.Dd)}, {LitOrNull(d.Dsn)}, {LitOrNull(d.Member)}, {LitOrNull(d.Generation)}, {(d.Dummy ? "true" : "false")}, " +
                           $"{(d.Sysout ? "true" : "false")}, {LitOrNull(d.InStream is null ? null : d.InStream + "\n")}, {(int.TryParse(l, out var n) ? n.ToString() : "null")})";
                });
                sb.Append($"                // {s.QualifiedStep} EXEC PGM={s.Program} ({s.Citation.Member}:{s.Citation.Line}{(s.InvocationChain.Count > 1 ? ", via " + string.Join(" > ", s.InvocationChain.Skip(1)) : "")})\n");
                sb.Append($"                new JobEngine.Step(StepId.{ids[i]}.name(), {LitOrNull(s.Program)}, {LitOrNull(s.Parm)}, {LitOrNull(s.Cond)}, {factory}, {normalized}\n");
                sb.Append("                    List.of(").Append(string.Join(",\n                        ", dds)).Append($")){(i + 1 < steps.Count ? "," : "")}\n");
            }
            sb.Append("            );\n        }\n");
            foreach (var s in job.Steps.Where(s => s.Conditional))
                sb.Append($"        // conditional step not in the unconditional job: {s.QualifiedStep} PGM={s.Program}\n");
            sb.Append("    }\n");
        }
        sb.Append("\n    /** Every converted program, for CALL resolution. */\n    public static Map<String, Supplier<CobolBase>> programs() {\n        return Map.ofEntries(\n");
        sb.Append(string.Join(",\n", classes.OrderBy(k => k.Key, StringComparer.Ordinal).Select(kv => $"            Map.entry({Lit(kv.Key)}, {kv.Value}::new)")));
        sb.Append("\n        );\n    }\n");
        var subprograms = modern.Values.Where(c => c.Kind == "subprogram").OrderBy(c => c.ProgramId, StringComparer.Ordinal).ToList();
        sb.Append("\n    /** Normalized CALLed subprograms: a CALL reaches one when the job selects \"normalized\" for that program. */\n");
        sb.Append("    public static Map<String, Supplier<com.mlcpu.rt.ModernProgram>> modernPrograms() {\n        return Map.ofEntries(");
        sb.Append(string.Join(",", subprograms.Select(c => $"\n            Map.entry({Lit(c.ProgramId)}, {c.StepClass}::new)")));
        sb.Append(subprograms.Count > 0 ? "\n        );\n    }\n}\n" : ");\n    }\n}\n");
        return sb.ToString();
    }

    static string LitOrNull(string? s) => s is null ? "null" : Lit(s);

    static string Lit(string s)
    {
        var sb = new StringBuilder("\"");
        foreach (var c in s)
        {
            if (c is '"' or '\\') sb.Append('\\').Append(c);
            else if (c == '\n') sb.Append("\\n");
            else if (c < 0x20 || c > 0x7E) sb.Append($"\\u{(int)c:x4}");
            else sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    static string Migration(string name, string slug, IReadOnlyList<JavaTranspileResult> results, IReadOnlyList<JobScope> jobs,
        IReadOnlyList<ProjectionLedger> ledgers, bool partitioned, IReadOnlyList<CapabilityImport> capabilities)
    {
        var sb = new StringBuilder($"# {name} — COBOL to Java migration\n\n");
        if (capabilities.Count > 0)
        {
            sb.Append("""
                ## Normalized capabilities

                Idiomatic implementations designed from `normalization/<PID>/requirements.json` (design in `design.json`,
                traceability in `design-conformance.json`). The faithful program stays as the baseline; `--mlcpu.implementation=legacy|normalized`
                overrides the per-program default below.

                | Program | Implementation class | Default | Why not promoted |
                |---|---|---|---|

                """);
            foreach (var c in capabilities)
                sb.Append($"| {c.ProgramId} | `{c.StepClass}` | {c.Implementation} | {string.Join("; ", c.Reasons).Replace("|", "/")} |\n");
            sb.Append('\n');
        }
        foreach (var job in jobs.Where(j => j.Jcl is not null))
        {
            sb.Append($"## Job {job.Name} (harvested JCL, PROCs expanded)\n\n| # | Step | Program | PARM | Via |\n|---|---|---|---|---|\n");
            foreach (var s in job.Jcl!.Steps)
                sb.Append($"| {s.Order} | {s.QualifiedStep}{(s.Conditional ? " (conditional)" : "")} | {s.Program} | {s.Parm} | {string.Join(" > ", s.InvocationChain.Skip(1))} |\n");
            sb.Append('\n');
        }
        sb.Append("## Programs\n\n| Program | Verdict | Compiled | Lowered | Abstained |\n|---|---|---|---|---|\n");
        foreach (var r in results.OrderBy(r => r.Program, StringComparer.Ordinal))
            sb.Append($"| {r.Program} | {r.Verdict} | {r.Compiled} | {r.Lowered} | {r.Abstained} |\n");
        sb.Append("\n## Projections (§8)\n\nEach pass gates itself and records why it skipped; see the ledger files at the project root.\n\n| Pass | Applied | Skipped | Reasons (first) |\n|---|---|---|---|\n");
        foreach (var l in ledgers)
            sb.Append($"| {l.Pass} | {l.Applied.Count} | {l.Skipped.Count} | {string.Join("; ", l.Skipped.Select(s => s.Reason).Distinct().Take(2)).Replace("|", "/")} |\n");
        var run = partitioned ? "gradle :estate-app:run" : "gradle run";
        sb.Append($$"""

            ## Run

            ```
            gradle test                                   # characterization (Prog_* vs Baseline_*), codecs, facades, context
            {{run}} --args="--mlcpu.job=<JOB> --mlcpu.data-dir=<dir> --mlcpu.clock=FIXED=2022-07-05T10:11:12-07:00"
            java -cp <classes> com.mlcpu.rt.RecordCompare --plan replay/ReplayPlan.json --step <STEP> --dd <DD> --expected <ref> --actual <out>
            ```

            Datasets are files named by DSN in `mlcpu.data-dir` (GDG generations map to the base name); INDEXED files are
            fixed-length record images in primary-key order. SYSOUT and in-stream data go to `mlcpu.sysout-dir`;
            control-card members are read from `ctl/`. `--mlcpu.steps=STEP15` runs selected steps.
            `DdBindingPlan.json` binds every program file to its DD; `replay/ReplayPlan.json` lists output layouts and the
            clock-derived (volatile) fields that `RecordCompare` excludes.

            """);
        return sb.ToString();
    }

    /// <summary>Publish: validate paths, stage into a temporary directory, then swap (the whole folder is replaced).</summary>
    public async Task<string> PublishAsync(JavaProject project, string targetDir, CancellationToken ct = default)
    {
        var root = Path.GetFullPath(Path.Combine(targetDir, project.Name));
        var stage = root + ".staging";
        if (Directory.Exists(stage)) Directory.Delete(stage, true);
        foreach (var f in project.Files)
        {
            var full = Path.GetFullPath(Path.Combine(stage, f.RelPath));
            if (!full.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"path escapes the project root: {f.RelPath}");
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            if (f.Language == "binary") await File.WriteAllBytesAsync(full, Convert.FromBase64String(f.Content), ct);
            else await File.WriteAllTextAsync(full, f.Content.Replace("\r\n", "\n"), new UTF8Encoding(false), ct);
        }
        var backup = root + ".bak";
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        if (Directory.Exists(root)) Directory.Move(root, backup);
        Directory.Move(stage, root);
        if (Directory.Exists(backup)) Directory.Delete(backup, true);
        return root;
    }
}
