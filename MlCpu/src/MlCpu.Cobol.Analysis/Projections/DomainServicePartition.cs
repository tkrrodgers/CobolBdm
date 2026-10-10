using System.Text;
using System.Text.RegularExpressions;

namespace MlCpu.Cobol.Analysis.Projections;

/// <summary>
/// Projection 10 (§8, Q13.10), only when a domain partition is requested: Gradle modules <c>estate-common</c>,
/// <c>estate-app</c> and <c>domain-&lt;id&gt;</c>. Ownership comes from the taxonomy (ProgramSemanticNameCatalog.Domain);
/// shared, unowned and cross-reached programs go to common. Every file of a program follows one decision; packages are
/// not rewritten. Cross-domain CALLs stay runtime-resolved by program name (ProgramRegistry), so no compile-time
/// dependency between domains exists; each is recorded as a seam.
/// </summary>
public static partial class DomainServicePartition
{
    public const string Pass = "DomainServicePartition";

    [GeneratedRegex(@"^src/(?<kind>main|test)/(?:java|resources)/com/mlcpu/capabilities/(?<pkg>[a-z0-9]+)/")] private static partial Regex CapabilityRx();
    [GeneratedRegex(@"^src/(?<kind>main|test)/java/com/mlcpu/core/(?:Prog_|State_|Baseline_)(?<program>[A-Za-z0-9_]+)\.java$")] private static partial Regex CoreRx();
    [GeneratedRegex(@"^src/(?<kind>main|test)/java/com/mlcpu/core/(?<program>[A-Za-z0-9_]+)CharacterizationTest\.java$")] private static partial Regex CharRx();
    [GeneratedRegex(@"^src/(?<kind>main|test)/java/com/mlcpu/(?:services|usecases)/(?<program>[A-Za-z0-9_]+?)(?:Service|Command|Result)\.java$")] private static partial Regex BoundaryRx();

    public static ProjectionLedger Apply(ProjectFiles files, IReadOnlyList<ProgramUnit> units, string projectName)
    {
        var ledger = new ProjectionLedger(Pass);
        var domainOf = units.ToDictionary(u => u.Pid, u => ProgramSemanticNameCatalog.ForProgram(u.ProgramId).Domain, StringComparer.Ordinal);
        var pidOf = units.ToDictionary(u => u.ProgramId, u => u.Pid, StringComparer.OrdinalIgnoreCase);
        var callers = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        foreach (var u in units)
            foreach (var t in u.Graph.Calls.SelectMany(c => c.ResolvedTargets).Where(pidOf.ContainsKey))
            {
                var callee = pidOf[t];
                if (callee == u.Pid) continue;
                if (!callers.TryGetValue(callee, out var set)) callers[callee] = set = [];
                set.Add(u.Pid);
            }
        var placement = new Dictionary<string, string>(StringComparer.Ordinal);
        var reasons = new List<object>();
        foreach (var u in units)
        {
            var d = domainOf[u.Pid];
            string module, why;
            if (d is "shared" or "general_application") { module = "estate-common"; why = "no domain membership"; }
            else if (callers.TryGetValue(u.Pid, out var cs) && cs.Any(c => domainOf[c] != d)) { module = "estate-common"; why = $"referenced by {string.Join(", ", cs.OrderBy(x => x, StringComparer.Ordinal))}"; }
            else { module = "domain-" + d; why = $"taxonomy classified it {d}"; }
            placement[u.Pid] = module;
            reasons.Add(new { program = u.ProgramId, module, reason = why });
        }
        var pkgOwner = units.ToDictionary(u => JavaNames.Package(ProgramSemanticNameCatalog.ForProgram(u.ProgramId).Package), u => u.Pid, StringComparer.Ordinal);
        var seams = units.SelectMany(u => u.Graph.Calls.SelectMany(c => c.ResolvedTargets).Where(pidOf.ContainsKey).Distinct()
                .Where(t => placement[pidOf[t]] != placement[u.Pid]).Select(t => new { callee = t, port = "ProgramRegistry (runtime name resolution)", owner = placement[pidOf[t]], callers = new[] { u.ProgramId } }))
            .ToList();

        string Route(string path)
        {
            if (path.StartsWith("src/main/java/com/mlcpu/app/", StringComparison.Ordinal) || path.StartsWith("src/main/resources/", StringComparison.Ordinal)
                || path.StartsWith("src/test/java/com/mlcpu/app/", StringComparison.Ordinal)) return "estate-app";
            if (CoreRx().Match(path) is { Success: true } c && placement.TryGetValue(c.Groups["program"].Value, out var m1)) return m1;
            if (CharRx().Match(path) is { Success: true } t && placement.TryGetValue(t.Groups["program"].Value, out var m2)) return m2;
            if (BoundaryRx().Match(path) is { Success: true } b && placement.TryGetValue(b.Groups["program"].Value, out var m3)) return m3;
            if (CapabilityRx().Match(path) is { Success: true } k)
            {
                if (pkgOwner.TryGetValue(k.Groups["pkg"].Value, out var owner)) return placement[owner];
                var content = files.Get(path) ?? "";
                var byContent = units.FirstOrDefault(u => content.Contains(u.ProgramId, StringComparison.Ordinal));
                if (byContent is not null) return placement[byContent.Pid];
            }
            return "estate-common";
        }

        var modules = new SortedSet<string>(StringComparer.Ordinal) { "estate-common", "estate-app" };
        foreach (var path in files.Paths("src/"))
        {
            var module = Route(path);
            modules.Add(module);
            files.Move(path, $"{module}/{path}");
        }
        var domains = modules.Where(m => m.StartsWith("domain-", StringComparison.Ordinal)).ToList();
        files.Set("settings.gradle.kts", $"rootProject.name = \"{projectName}\"\n" + string.Join("", modules.Select(m => $"include(\"{m}\")\n")));
        files.Set("build.gradle.kts", RootBuild());
        files.Set("estate-common/build.gradle.kts", "// Runtime, shared copybook storage, entities, codecs, and shared or cross-reached programs.\n");
        foreach (var d in domains)
            files.Set($"{d}/build.gradle.kts", "dependencies {\n    api(project(\":estate-common\"))\n}\n");
        files.Set("estate-app/build.gradle.kts", AppBuild(domains));

        ledger.Applied.AddRange(modules);
        ledger.Facts["modules"] = modules.ToList();
        ledger.Facts["sharedPrograms"] = placement.Where(kv => kv.Value == "estate-common").Select(kv => kv.Key).ToList();
        ledger.Facts["crossDomainSeams"] = seams.Count;
        ledger.Facts["domains"] = domains.Select(d => new { id = d, programs = placement.Where(kv => kv.Value == d).Select(kv => kv.Key).ToList() }).ToList();
        ledger.Facts["common"] = placement.Where(kv => kv.Value == "estate-common").Select(kv => kv.Key).ToList();
        ledger.Facts["seams"] = seams;
        ledger.Facts["placements"] = reasons;
        return ledger;
    }

    static string RootBuild() => """
        // Domain-partitioned estate (projection 10): estate-common <- domain-* <- estate-app.
        subprojects {
            apply(plugin = "java-library")
            repositories { mavenCentral() }
            configure<JavaPluginExtension> {
                sourceCompatibility = JavaVersion.VERSION_21
                targetCompatibility = JavaVersion.VERSION_21
            }
            dependencies {
                "api"(platform("org.springframework.boot:spring-boot-dependencies:3.3.4"))
                "api"("org.springframework.boot:spring-boot-starter")
                "testImplementation"(platform("org.junit:junit-bom:5.11.3"))
                "testImplementation"("org.springframework.boot:spring-boot-starter-test")
                "testImplementation"("org.junit.jupiter:junit-jupiter")
                "testRuntimeOnly"("org.junit.platform:junit-platform-launcher")
            }
            tasks.withType<JavaCompile>().configureEach { options.encoding = "UTF-8" }
            tasks.withType<Test>().configureEach {
                useJUnitPlatform()
                jvmArgs("-Xss64m")
                systemProperty("mlcpu.characterization.dir", "${rootDir}/characterization")
            }
        }

        """;

    static string AppBuild(IEnumerable<string> domains)
    {
        var sb = new StringBuilder("plugins {\n    application\n}\n\ndependencies {\n    implementation(project(\":estate-common\"))\n");
        foreach (var d in domains) sb.Append($"    implementation(project(\":{d}\"))\n");
        sb.Append("""
            }

            application {
                mainClass.set("com.mlcpu.app.Application")
                applicationDefaultJvmArgs = listOf("-Xss64m")
            }

            tasks.named<JavaExec>("run") { workingDir = rootProject.projectDir }

            """);
        return sb.ToString();
    }
}
