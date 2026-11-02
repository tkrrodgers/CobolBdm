using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.CobolKb.Knowledge;
using MlCpu.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;

namespace MlCpu.Host.Checks;

/// <summary>
/// End to end, model-free: the sidecar extracts typed rules from corpus programs; the assembler publishes monolithic and
/// partitioned Spring exports; a human module outside the generated root implements contracts; Gradle compiles and runs
/// the contract/architecture/lock tests, the characterization tests and a real job with explicit rule selection.
/// </summary>
static class EndToEnd
{
    const string Clock = "FIXED=2026-01-01T00:00:00Z";

    public static async Task Run(Report report, string repo, bool keep)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = Path.Combine(repo, "MlCpu", "src", "MlCpu.Host"), Args = [] });
        builder.Services.AddSingleton<ConfigService>();
        builder.Services.AddSingleton<JavaSidecarService>();
        await using var app = builder.Build();
        var sidecar = app.Services.GetRequiredService<JavaSidecarService>();
        if (await sidecar.HealthAsync() is null) { report.Blocked("e2e", "Java sidecar not reachable (tools/Start-Sidecar.ps1)"); return; }
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrEmpty(javaHome) || !File.Exists(Path.Combine(javaHome, "bin", OperatingSystem.IsWindows() ? "javac.exe" : "javac")))
        { report.Blocked("e2e", "JDK 21 not found (JAVA_HOME)"); return; }
        var gradle = FindOnPath(OperatingSystem.IsWindows() ? "gradle.bat" : "gradle");
        if (gradle is null) { report.Blocked("e2e", "gradle not on PATH"); return; }

        var corpus = Path.Combine(repo, "MlCpu", "src", "CobolConformance", "corpus");
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["PARITY10"] = Path.Combine(corpus, "parity", "PARITY10.cbl"),
            ["CALCDEMO"] = Path.Combine(corpus, "projection", "CALCDEMO.cbl")
        };
        var results = new List<JavaTranspileResult>();
        foreach (var (pid, path) in sources)
        {
            var r = await sidecar.TranspileAsync(path, transpileOnly: true, composeStorage: true);
            results.Add(r);
            report.Check($"e2e/transpile {pid}: composed, compiled, rules extracted", r.Ok && r.StorageComposition && r.ExtractionLedger is not null && r.RuleContracts.Count > 0,
                $"verdict={r.Verdict} composed={r.StorageComposition} ledger={r.ExtractionLedger is not null} rules={r.RuleContracts.Count} {r.Error}");
        }
        if (results.Any(r => r.RuleContracts.Count == 0)) { report.Blocked("e2e/export", "the sidecar did not serve extractRules for every pilot"); return; }
        var contracts = results.SelectMany(r => r.RuleContracts).ToList();
        var target = contracts.FirstOrDefault(c => c["programId"]?.GetValue<string>() == "PARITY10" && (c["expression"]?.ToString() ?? "").Contains("F1-ON", StringComparison.Ordinal))
            ?? contracts.First(c => c["programId"]?.GetValue<string>() == "PARITY10" && c["kind"]?.GetValue<string>() == "predicate");
        var targetId = target["ruleId"]!.GetValue<string>();
        var calc = contracts.FirstOrDefault(c => c["programId"]?.GetValue<string>() == "CALCDEMO" && c["kind"]?.GetValue<string>() == "calculation");

        var work = Path.Combine(repo, "work", "rule-handoff-check");
        var jclDir = Path.Combine(work, "jcl");
        Directory.CreateDirectory(jclDir);
        File.WriteAllText(Path.Combine(jclDir, "RULEJOB.jcl"), """
            //RULEJOB  JOB (ACCT),'TYPED RULES'
            //STEP1    EXEC PGM=PARITY10
            //SYSOUT   DD SYSOUT=*
            //STEP2    EXEC PGM=CALCDEMO
            //SYSOUT   DD SYSOUT=*

            """.Replace("\r\n", "\n"));
        var jcl = JclHarvester.Harvest([jclDir], []).Single(j => j.Name == "RULEJOB");
        var scope = EstateService.Resolve(jcl, sources);
        var runtime = await sidecar.RuntimeAsync();
        var env = new Dictionary<string, string> { ["JAVA_HOME"] = javaHome };

        foreach (var partitioned in new[] { false, true })
        {
            var layout = partitioned ? "partitioned" : "monolithic";
            var outDir = Path.Combine(work, layout);
            var assembler = new JavaProjectAssembler();
            var project = assembler.Assemble("rulecheck", results, runtime, [scope], new AssemblyOptions(ProgramSources: sources, DomainPartition: partitioned));
            var root = await assembler.PublishAsync(project, outDir);
            var human = Path.Combine(outDir, project.Name + "-human");
            if (Directory.Exists(human)) Directory.Delete(human, true);
            WriteHuman(human, contracts, targetId, mutants: false);
            (int Code, string Output) G(params string[] a) => Run(gradle, ["--offline", "--console=plain", .. a], root, env);
            var run = partitioned ? ":estate-app:run" : "run";

            var test = G("test");
            var reports = Path.Combine(root, "rules-conformance", "build", "test-results", "test");
            var xml = Directory.Exists(reports) ? string.Concat(Directory.EnumerateFiles(reports, "*.xml").Select(File.ReadAllText)) : "";
            report.Check($"e2e/{layout}: gradle test (characterization, contracts, selection, architecture, lock) passes with a human module",
                test.Code == 0 && xml.Contains("human ", StringComparison.Ordinal) && xml.Contains("example ", StringComparison.Ordinal) && xml.Contains("generated ", StringComparison.Ordinal),
                Tail(test.Output));

            string Job(string sysout, params string[] extra)
            {
                var data = Path.Combine(work, layout + "-data");
                Directory.CreateDirectory(data);
                var dir = Path.Combine(work, layout + "-" + sysout);
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                var r = G("-q", run, $"--args=--mlcpu.job=RULEJOB --mlcpu.data-dir={data} --mlcpu.sysout-dir={dir} --mlcpu.clock={Clock} {string.Join(" ", extra)}");
                var files = Directory.Exists(dir) ? string.Concat(Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(f => Path.GetFileName(f) + "\n" + File.ReadAllText(f))) : "";
                var stdout = string.Join("\n", r.Output.Split('\n').Select(l => l.TrimEnd('\r'))
                    .Where(l => !l.StartsWith("RULE ", StringComparison.Ordinal) && !l.StartsWith("JOB ", StringComparison.Ordinal)));
                return $"exit={r.Code}\n{stdout}\n--sysout--\n{files}";
            }
            var baseline = Job("generated");
            var viaHuman = Job("human", $"--mlcpu.rule.{targetId}=human");
            report.Check($"e2e/{layout}: job RULEJOB with the human rule selected matches the generated rule", baseline.StartsWith("exit=0", StringComparison.Ordinal) && baseline == viaHuman,
                baseline == viaHuman ? Tail(baseline) : Diff(baseline, viaHuman));

            WriteHuman(human, contracts, targetId, mutants: true);
            var mutantJob = Job("mutant", $"--mlcpu.rule.{targetId}=mutant");
            report.Check($"e2e/{layout}: job with a mutant rule selected differs (the selection reaches the running program)", mutantJob != baseline, Tail(mutantJob));
            if (calc is not null)
            {
                var calcJob = Job("mutant-calc", $"--mlcpu.rule.{calc["ruleId"]}=mutant");
                report.Check($"e2e/{layout}: job with a mutant calculation selected differs", calcJob != baseline, Tail(calcJob));
            }
            var unknown = Job("unknown", $"--mlcpu.rule.{targetId}=no-such-implementation");
            report.Check($"e2e/{layout}: an unknown implementation id fails the job instead of falling back", !unknown.StartsWith("exit=0", StringComparison.Ordinal) && unknown.Contains("no-such-implementation", StringComparison.Ordinal), Tail(unknown));
            var mutantCases = G(":rules-conformance:test");
            report.Check($"e2e/{layout}: contract cases detect the mutant implementations (DIFF, not explained)", mutantCases.Code != 0 && mutantCases.Output.Contains("mutant", StringComparison.Ordinal), Tail(mutantCases.Output));

            if (!runtime.ContainsKey("JobEngine.java") || !runtime["JobEngine.java"].Contains("BASELINE_PROGRAMS", StringComparison.Ordinal))
                report.Blocked($"e2e/{layout}: strangler", "the runtime has no BASELINE_PROGRAMS/STRANGLER");
            else
            {
                var jobDefinition = File.ReadAllText(Directory.EnumerateFiles(root, "JobDefinition.java", SearchOption.AllDirectories).Single());
                var wired = sources.Keys.All(p => System.Text.RegularExpressions.Regex.IsMatch(jobDefinition, $@"Map\.entry\(""{p}"", [\w.]+\.Baseline_{p}::new\)"))
                    && System.Text.RegularExpressions.Regex.Matches(jobDefinition, @"\), [\w.]+\.Baseline_\w+::new\)").Count == 2;
                report.Check($"e2e/{layout}: JobDefinition registers each frozen baseline and passes it to its step", wired, "");
                static string Legacy(string job) => string.Join("\n", job.Split('\n').Select(l => System.Text.RegularExpressions.Regex.Replace(l, @"^(\S+\s+\S+\s+rc=\d+).*$", "$1")));
                var legacyMutant = Job("legacy-mutant", "--mlcpu.strangler=LEGACY", $"--mlcpu.rule.{targetId}=mutant");
                report.Check($"e2e/{layout}: strangler LEGACY runs the frozen baselines (a selected mutant rule is not reached)",
                    Legacy(legacyMutant) == Legacy(baseline), Diff(Legacy(baseline), Legacy(legacyMutant)));
                var modern = Job("modern", "--mlcpu.strangler=MODERN");
                var modernMutant = Job("modern-mutant", "--mlcpu.strangler=MODERN", $"--mlcpu.rule.{targetId}=mutant");
                report.Check($"e2e/{layout}: strangler MODERN runs the modern programs and their selected rules",
                    Legacy(modern) == Legacy(baseline) && Legacy(modernMutant) != Legacy(baseline), Diff(Legacy(baseline), Legacy(modern)));
                var bad = Job("bad-mode", "--mlcpu.strangler=SIDEWAYS");
                report.Check($"e2e/{layout}: an unknown strangler mode fails the job", !bad.StartsWith("exit=0", StringComparison.Ordinal) && bad.Contains("SIDEWAYS", StringComparison.Ordinal), Tail(bad));
                if (!runtime.ContainsKey("StepStrangler.java")) report.Blocked($"e2e/{layout}: strangler SHADOW", "the runtime has no StepStrangler yet");
                else
                {
                    string Dir(string name) => Path.Combine(work, layout + "-" + name);
                    static Dictionary<string, byte[]> Published(string dir) => Directory.Exists(dir)
                        ? Directory.EnumerateFiles(dir).Where(f => !f.EndsWith(".shadow-report.json", StringComparison.Ordinal)).ToDictionary(Path.GetFileName, File.ReadAllBytes)!
                        : [];
                    var legacy = Job("legacy", "--mlcpu.strangler=LEGACY");
                    var shadow = Job("shadow", "--mlcpu.strangler=SHADOW");
                    var problems = new List<string>();
                    if (!shadow.StartsWith("exit=0", StringComparison.Ordinal)) problems.Add("exit " + Tail(shadow));
                    foreach (var step in new[] { "STEP1", "STEP2" })
                    {
                        var file = Path.Combine(Dir("shadow"), step + ".shadow-report.json");
                        if (!File.Exists(file)) { problems.Add(step + ": no shadow report"); continue; }
                        var r = JsonNode.Parse(File.ReadAllText(file))!;
                        if (r["schema"]?.GetValue<string>() != "mlcpu.step-shadow.v1" || r["equivalent"]?.GetValue<bool>() != true || r["promoted"]?.GetValue<bool>() != false
                            || r["authoritative"]?.GetValue<string>() != "LEGACY" || r["legacyReturnCode"]?.GetValue<int>() != 0 || r["modernReturnCode"]?.GetValue<int>() != 0
                            || r["differences"]?.AsArray().Count != 0)
                            problems.Add(step + ": " + r.ToJsonString());
                    }
                    var (shadowBytes, legacyBytes) = (Published(Dir("shadow")), Published(Dir("legacy")));
                    if (legacyBytes.Count == 0 || shadowBytes.Count != legacyBytes.Count
                        || shadowBytes.Any(kv => !legacyBytes.TryGetValue(kv.Key, out var l) || !l.AsSpan().SequenceEqual(kv.Value)))
                        problems.Add($"published SYSOUT differs from the LEGACY run: [{string.Join(", ", shadowBytes.Keys)}] vs [{string.Join(", ", legacyBytes.Keys)}]");
                    report.Check($"e2e/{layout}: strangler SHADOW (generated rules) writes step-shadow.v1 reports (equivalent, not promoted, legacy authoritative) and publishes exactly the LEGACY bytes",
                        problems.Count == 0, string.Join("; ", problems));
                    var shadowMutant = Job("shadow-mutant", "--mlcpu.strangler=SHADOW", $"--mlcpu.rule.{targetId}=mutant");
                    var shadowReports = Directory.Exists(Dir("shadow-mutant")) ? Directory.EnumerateFiles(Dir("shadow-mutant"), "*.shadow-report.json").Count() : 0;
                    report.Check($"e2e/{layout}: strangler SHADOW refuses a custom rule selection (FRONTIER rc=12, no shadow report)",
                        !shadowMutant.StartsWith("exit=0", StringComparison.Ordinal) && System.Text.RegularExpressions.Regex.IsMatch(shadowMutant, @"STEP1\s+PARITY10\s+rc=12 FRONTIER custom rule implementations require a separately certified shadow adapter")
                        && shadowReports == 0, $"reports={shadowReports} " + Tail(shadowMutant));
                }
            }

            var characterization = G("test", $"-Pmlcpu.rule.{targetId}=mutant", "-x", ":rules-conformance:test");
            report.Check($"e2e/{layout}: characterization (Prog_ vs Baseline_) with the mutant selected fails", characterization.Code != 0, Tail(characterization.Output));
            WriteHuman(human, contracts, targetId, mutants: false);

            if (partitioned) continue;
            var prog = Directory.EnumerateFiles(root, "Prog_PARITY10.java", SearchOption.AllDirectories).First();
            var original = File.ReadAllText(prog);
            File.AppendAllText(prog, "// hand edit\n");
            var edited = G(":rules-conformance:test", "--tests", "*GeneratedLockTest*");
            File.WriteAllText(prog, original);
            report.Check("e2e/monolithic: editing a regenerated program fails the lock test (TG3)", edited.Code != 0 && edited.Output.Contains("Prog_PARITY10.java (edited)", StringComparison.Ordinal), Tail(edited.Output));
            var stray = Path.Combine(root, "modern-rules", "src", "main", "java", "com", "mlcpu", "rules", "Stray.java");
            File.WriteAllText(stray, "package com.mlcpu.rules;\npublic final class Stray {}\n");
            var added = G(":rules-conformance:test", "--tests", "*GeneratedLockTest*");
            File.Delete(stray);
            report.Check("e2e/monolithic: adding a source to a generated rule module fails the lock test", added.Code != 0 && added.Output.Contains("Stray.java", StringComparison.Ordinal), Tail(added.Output));
            var impure = Path.Combine(human, "src", "main", "java", "com", "mlcpu", "rules", "human", "Dice.java");
            File.WriteAllText(impure, "package com.mlcpu.rules.human;\nfinal class Dice { int roll() { return new java.util.Random().nextInt(6); } }\n");
            var arch = G(":rules-conformance:test", "--tests", "*RuleArchitectureTest*");
            File.WriteAllText(impure, "package com.mlcpu.rules.human;\nimport com.mlcpu.core.Prog_PARITY10;\nfinal class Dice { Object p = new Prog_PARITY10(); }\n");
            var state = G(":human-rules:compileJava");
            File.Delete(impure);
            report.Check("e2e/monolithic: the architecture test rejects impure human code", arch.Code != 0 && arch.Output.Contains("java.util.Random", StringComparison.Ordinal), Tail(arch.Output));
            report.Check("e2e/monolithic: human code cannot compile against program state", state.Code != 0 && state.Output.Contains("com.mlcpu.core", StringComparison.Ordinal), Tail(state.Output));

            // Malicious stateful providers: an instance counter, a static atomic and ambient System access.
            var t = contracts.Single(c => c["ruleId"]!.GetValue<string>() == targetId);
            string T(string k) => t[k]!.GetValue<string>();
            var services = Path.Combine(human, "src", "main", "resources", "META-INF", "services", "com.mlcpu.rules.spi.RuleImplementation");
            var pkgDir = Path.Combine(human, "src", "main", "java", "com", "mlcpu", "rules", "human");
            var extra = new List<string>();
            string Delegate(string expr) => $"return {expr};";
            var gen = $"new {T("generatedImplementation")}().{T("method")}(input)";
            Write(pkgDir, "StatefulProvider", targetId, "stateful", T("contract"), T("output"), T("method"), T("input"), "calls++;\n        " + Delegate(gen), extra,
                "    private int calls;\n");
            Write(pkgDir, "AtomicProvider", targetId, "atomic", T("contract"), T("output"), T("method"), T("input"), "CALLS.incrementAndGet();\n        " + Delegate(gen), extra,
                "    private static final java.util.concurrent.atomic.AtomicInteger CALLS = new java.util.concurrent.atomic.AtomicInteger();\n");
            Write(pkgDir, "AmbientProvider", targetId, "ambient", T("contract"), T("output"), T("method"), T("input"),
                "if (System.getProperty(\"mlcpu.flip\") != null) throw new IllegalStateException();\n        " + Delegate(gen), extra);
            File.AppendAllText(services, string.Join("\n", extra) + "\n");
            var stateCases = G(":rules-conformance:test");
            report.Check("e2e/monolithic: rules-conformance rejects stateful providers (instance field, static atomic, ambient System)",
                stateCases.Code != 0 && stateCases.Output.Contains("calls: mutable field", StringComparison.Ordinal)
                && stateCases.Output.Contains("AtomicInteger is not a known immutable value", StringComparison.Ordinal)
                && stateCases.Output.Contains("ambient System.", StringComparison.Ordinal), Tail(stateCases.Output));
            var statefulJob = Job("stateful", $"--mlcpu.rule.{targetId}=stateful");
            var atomicJob = Job("atomic", $"--mlcpu.rule.{targetId}=atomic");
            var ambientJob = Job("ambient", $"--mlcpu.rule.{targetId}=ambient");
            report.Check("e2e/monolithic: the job runner refuses to select a stateful provider (instance field, static atomic)",
                !statefulJob.StartsWith("exit=0", StringComparison.Ordinal) && statefulJob.Contains("not verifiably stateless", StringComparison.Ordinal) && statefulJob.Contains("calls: mutable field", StringComparison.Ordinal)
                && !atomicJob.StartsWith("exit=0", StringComparison.Ordinal) && atomicJob.Contains("not verifiably stateless", StringComparison.Ordinal), Tail(statefulJob) + " || " + Tail(atomicJob));
            report.Check("e2e/monolithic: the job runner refuses an ambient-only provider (compiled reference to java/lang/System)",
                !ambientJob.StartsWith("exit=0", StringComparison.Ordinal) && ambientJob.Contains("not verifiably stateless", StringComparison.Ordinal)
                && ambientJob.Contains("references ambient java/lang/System", StringComparison.Ordinal)
                && !(Directory.Exists(Path.Combine(work, "monolithic-ambient")) && Directory.EnumerateFiles(Path.Combine(work, "monolithic-ambient"), "*", SearchOption.AllDirectories).Any()),
                Tail(ambientJob));
            WriteHuman(human, contracts, targetId, mutants: false);

            var humanBefore = Repo.TreeHash(human);
            var lockBefore = File.ReadAllText(Path.Combine(root, RuleHandoff.LockPath));
            var again = assembler.Assemble("rulecheck", results, runtime, [scope], new AssemblyOptions(ProgramSources: sources, DomainPartition: false));
            await assembler.PublishAsync(again, outDir);
            report.Check("e2e/monolithic: republishing is deterministic and leaves the human module untouched",
                Repo.TreeHash(human) == humanBefore && File.ReadAllText(Path.Combine(root, RuleHandoff.LockPath)) == lockBefore, "");
        }
        if (!keep) foreach (var d in Directory.EnumerateDirectories(work).Where(d => !d.EndsWith("jcl", StringComparison.Ordinal))) TryDelete(d);
    }

    /// <summary>
    /// The human module: an independent implementation of the F1-ON predicate (the 88's value 'Y' over the exact 1-byte image),
    /// delegating implementations of the other contracts, and optionally mutants that must be detected.
    /// </summary>
    static void WriteHuman(string dir, IReadOnlyList<JsonObject> contracts, string targetId, bool mutants)
    {
        var pkgDir = Path.Combine(dir, "src", "main", "java", "com", "mlcpu", "rules", "human");
        if (Directory.Exists(Path.Combine(dir, "src"))) Directory.Delete(Path.Combine(dir, "src"), true);
        Directory.CreateDirectory(pkgDir);
        File.WriteAllText(Path.Combine(dir, "build.gradle.kts"), RuleHandoff.ExampleBuild(generated: false));
        var registrations = new List<string>();
        foreach (var c in contracts)
        {
            string S(string k) => c[k]!.GetValue<string>();
            var id = S("ruleId");
            var contract = S("contract");
            var name = contract[(contract.LastIndexOf('.') + 1)..];
            var predicate = S("kind") == "predicate";
            var output = S("output");
            var inputs = c["inputs"]?.AsArray() ?? [];
            var independent = id == targetId && (c["expression"]?.ToString() ?? "").Contains("F1-ON", StringComparison.Ordinal) && inputs.Count == 1
                && inputs[0]!["javaType"]?.ToString() is "String" or "java.lang.String";
            var body = independent
                ? $"return \"Y\".equals(input.{inputs[0]!["name"]}());"
                : $"return new {S("generatedImplementation")}().{S("method")}(input);";
            Write(pkgDir, "Human" + name, id, "human", contract, output, S("method"), S("input"), body, registrations);
            if (!mutants || !(id == targetId || !predicate)) continue;
            var mutated = predicate
                ? $"return !new {S("generatedImplementation")}().{S("method")}(input);"
                : $"return new {output}(new {S("generatedImplementation")}().{S("method")}(input).value().add(java.math.BigDecimal.ONE));";
            Write(pkgDir, "Mutant" + name, id, "mutant", contract, output, S("method"), S("input"), mutated, registrations);
        }
        var services = Path.Combine(dir, "src", "main", "resources", "META-INF", "services");
        Directory.CreateDirectory(services);
        File.WriteAllText(Path.Combine(services, "com.mlcpu.rules.spi.RuleImplementation"), string.Join("\n", registrations) + "\n");
    }

    static void Write(string dir, string cls, string id, string implId, string contract, string output, string method, string input, string body, List<string> registrations,
        string fields = "")
    {
        File.WriteAllText(Path.Combine(dir, cls + ".java"), $$"""
            package com.mlcpu.rules.human;

            import com.mlcpu.rules.spi.RuleImplementation;

            public final class {{cls}} implements {{contract}} {
            {{fields}}
                @Override
                public {{output}} {{method}}({{input}} input) {
                    {{body}}
                }

                public static final class Registration implements RuleImplementation<{{contract}}> {
                    @Override public String ruleId() { return "{{id}}"; }
                    @Override public String implementationId() { return "{{implId}}"; }
                    @Override public Class<{{contract}}> contract() { return {{contract}}.class; }
                    @Override public {{contract}} create() { return new {{cls}}(); }
                }
            }

            """);
        registrations.Add($"com.mlcpu.rules.human.{cls}$Registration");
    }

    static string? FindOnPath(string exe) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
        .Select(d => Path.Combine(d.Trim(), exe)).FirstOrDefault(File.Exists);

    static (int Code, string Output) Run(string exe, IEnumerable<string> args, string cwd, Dictionary<string, string> env)
    {
        var psi = new ProcessStartInfo(OperatingSystem.IsWindows() ? "cmd.exe" : exe) { WorkingDirectory = cwd, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (OperatingSystem.IsWindows()) { psi.ArgumentList.Add("/c"); psi.ArgumentList.Add(exe); }
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Append(e.Data).Append('\n'); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.Append(e.Data).Append('\n'); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(TimeSpan.FromMinutes(20))) { p.Kill(true); return (-1, output + "\n(timeout)"); }
        p.WaitForExit();
        return (p.ExitCode, output.ToString());
    }

    static string Tail(string s) => string.Join(" | ", s.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(8));

    static string Diff(string a, string b)
    {
        var x = a.Split('\n');
        var y = b.Split('\n');
        for (int i = 0; i < Math.Max(x.Length, y.Length); i++)
            if (i >= x.Length || i >= y.Length || x[i] != y[i]) return $"line {i + 1}: '{(i < x.Length ? x[i] : "")}' vs '{(i < y.Length ? y[i] : "")}'";
        return "";
    }

    static void TryDelete(string dir)
    {
        try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
