using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MlCpu.DesignModel;

/// <summary>
/// Design -> Java. The model writes the whole capability (domain records, record layouts, repositories, services, step)
/// on top of the platform layer only (com.mlcpu.carddemo.platform.* and com.mlcpu.rt.ModernStep). Each attempt is
/// compiled in a harness (a copy of the exported project with the authored capability classes removed) and judged by the
/// program's equivalence test: the model's step against the faithful COBOL translation, every scenario, byte for byte.
/// Compiler errors and failing scenarios go back to the model until everything passes or the attempts run out.
/// </summary>
public static class ImplementStep
{
    public const string SystemPrompt = """
        You are a senior Java 21 engineer. You implement a capability design as production-quality, idiomatic Java:
        immutable records, small focused classes, BigDecimal for money, no reflection, no frameworks. The code replaces a
        COBOL batch program and must reproduce its observable behaviour exactly (output datasets byte for byte, console
        lines, return code), including the legacy quirks the design preserves. You answer with Java source files only,
        each in its own ```java fenced block.
        """;

    static readonly Regex JavaBlock = new(@"```java[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);
    static readonly Regex PackageDecl = new(@"^\s*package\s+([\w.]+)\s*;", RegexOptions.Multiline);
    static readonly Regex TopType = new(@"^(?:public\s+)?(?:(?:final|abstract|sealed|non-sealed|static)\s+)*(?:class|record|interface|enum)\s+(\w+)", RegexOptions.Multiline);
    static readonly Regex MlcpuRef = new(@"com\.mlcpu\.[\w.]+");

    /// <param name="Kind">"step" (a job step: implements ModernStep) or "subprogram" (a CALLed program: implements ModernProgram).</param>
    /// <param name="PlatformPackage">The platform layer the capability may use (manifest platformPackage; CardDemo's by default).</param>
    public sealed record Capability(string ProgramId, string Package, string StepClass, IReadOnlyList<string> AuthoredClasses,
        string Kind = "step", string PlatformPackage = "com.mlcpu.carddemo.platform", string? HostProgram = null);

    public static Capability FromManifest(string manifestPath, string programId)
    {
        var m = JsonNode.Parse(File.ReadAllText(manifestPath))!;
        var c = m["capabilities"]!.AsArray().FirstOrDefault(n => n!["programId"]!.GetValue<string>() == programId)
                ?? throw new ArgumentException($"{programId} is not in {manifestPath}");
        return new Capability(programId, c["package"]!.GetValue<string>(), c["step"]!.GetValue<string>(),
            c["classes"]!.AsArray().Select(n => n!.GetValue<string>()).ToList(), c["kind"]?.GetValue<string>() ?? "step",
            m["platformPackage"]?.GetValue<string>() ?? "com.mlcpu.carddemo.platform", c["hostProgram"]?.GetValue<string>());
    }

    static string EntryInterface(Capability cap) => cap.Kind == "subprogram" ? "com.mlcpu.rt.ModernProgram" : "com.mlcpu.rt.ModernStep";

    public sealed record Result(bool Compiled, int TestsTotal, int TestsPassed, int AttemptsUsed, bool FirstAttemptPassed, int BestAttempt, int OutputDifferences);

    static readonly Regex Difference = new(@"first difference at byte|only the \w+ implementation wrote it|return code: legacy|: missing|: extra");

    /// <summary>Output differences in a failing scenario; a failure without any (an exception) counts as 10.</summary>
    static int Differences(string failure)
    {
        int n = Difference.Matches(failure).Count;
        return n == 0 ? 10 : n;
    }

    public static Result Run(IChatBackend chat, Capability cap, string packetPath, string designPath, string projectDir, string outDir,
        string harnessDir, int attempts, string modelName, string reasoning, int maxTokens, TimeSpan testTimeout,
        string? examplesPath = null, string? notesPath = null, string? outlinePath = null, int context = 131072)
    {
        Directory.CreateDirectory(outDir);
        var stepSimple = cap.StepClass[(cap.StepClass.LastIndexOf('.') + 1)..];
        var pkgPath = cap.Package.Replace('.', Path.DirectorySeparatorChar);
        PrepareHarness(projectDir, harnessDir, cap);
        var harnessPkg = Path.Combine(harnessDir, "src", "main", "java", pkgPath);
        var reserved = Directory.EnumerateFiles(harnessPkg, "*.java").Select(Path.GetFileNameWithoutExtension)
            .Concat(Directory.Exists(Path.Combine(harnessDir, "src", "test", "java", pkgPath))
                ? Directory.EnumerateFiles(Path.Combine(harnessDir, "src", "test", "java", pkgPath), "*.java").Select(Path.GetFileNameWithoutExtension) : [])
            .Select(n => n!).ToHashSet(StringComparer.Ordinal);
        reserved.Add(ProbeClass);
        var testFile = Directory.EnumerateFiles(Path.Combine(harnessDir, "src", "test", "java", pkgPath), "*EquivalenceTest.java").Single();
        var testClass = cap.Package + "." + Path.GetFileNameWithoutExtension(testFile);

        var prompt = BuildPrompt(cap, stepSimple, packetPath, designPath, projectDir, File.ReadAllText(testFile), reserved, examplesPath, notesPath);
        // The AST outline goes in when the prompt, the outline and a full reply still fit the context (about 3.6 characters per token).
        bool outlineUsed = false;
        if (outlinePath is not null && File.Exists(outlinePath))
        {
            var outline = File.ReadAllText(outlinePath).Replace("\r\n", "\n");
            if ((prompt.Length + outline.Length) / 3.6 + maxTokens + 2000 < context)
            {
                // The prompt's line ends are the platform's (CRLF on Windows): find the Task heading either way.
                var task = Regex.Match(prompt, @"(\r?\n)# Task\r?\n");
                if (task.Success)
                {
                    var nl = task.Groups[1].Value;
                    prompt = prompt[..task.Index] + nl + "# COBOL-85 AST outline of the program (the original logic, statement by statement)" + nl + "```" + nl
                             + outline.TrimEnd().Replace("\n", nl) + nl + "```" + nl + prompt[task.Index..];
                    outlineUsed = true;
                }
                else Program.Log("  AST outline left out: no Task heading found in the prompt");
            }
            else Program.Log($"  AST outline left out: the prompt would not leave room for a {maxTokens}-token reply in a {context}-token context");
        }
        File.WriteAllText(Path.Combine(outDir, "implementation.prompt.md"), prompt);
        var baseMessages = new List<ChatMessage> { new("system", SystemPrompt), new("user", prompt) };
        var messages = new List<ChatMessage>(baseMessages);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var written = new HashSet<string>(StringComparer.Ordinal);
        var runs = new JsonArray();
        bool compiled = false, firstPassed = false;
        int total = 0, passed = 0, used = 0;
        (int Attempt, bool Compiled, int Passed, int Total, int Diffs, SortedDictionary<string, string>? Files) best = (0, false, 0, 0, int.MaxValue, null);
        string backend = chat.Name;
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            used = attempt;
            var r = chat.Complete(messages, maxTokens, reasoning);
            backend = r.Backend;
            Program.Log($"implement attempt {attempt}: prompt {r.PromptTokens} tok @ {r.PromptTokensPerSecond:N0} tok/s, generated {r.GeneratedTokens} tok @ {r.TokensPerSecond:N1} tok/s, {r.Seconds:N0}s, stop={r.Stop} [{r.Backend}]");
            File.WriteAllText(Path.Combine(outDir, $"implementation.attempt{attempt}.txt"), r.Content);
            if (r.Reasoning is not null) File.WriteAllText(Path.Combine(outDir, $"implementation.attempt{attempt}.reasoning.txt"), r.Reasoning);

            var problems = new List<string>();
            var returned = Parse(r.Content, problems);
            foreach (var (name, src) in returned) files[name] = src;
            problems.AddRange(Check(files, cap, stepSimple, reserved));
            var attemptJson = Program.AttemptJson(attempt, r, problems);
            attemptJson["filesReturned"] = Program.Strings(returned.Keys);
            attemptJson["files"] = Program.Strings(files.Keys);

            string feedback;
            if (problems.Count > 0 || files.Count == 0)
            {
                if (files.Count == 0 && problems.Count == 0) problems.Add("no ```java code blocks were found in the reply");
                feedback = "The files break the contract:\n- " + string.Join("\n- ", problems) + "\n";
                compiled = false;
                total = passed = 0;
            }
            else
            {
                Sync(harnessPkg, files, written);
                WriteDeliverable(outDir, pkgPath, files);
                var build = Gradle(harnessDir, "compileJava compileTestJava", TimeSpan.FromMinutes(10));
                var errors = CompileErrors(build.Output);
                compiled = build.ExitCode == 0;
                attemptJson["compile"] = new JsonObject { ["ok"] = compiled, ["errors"] = Program.Strings(errors) };
                if (!compiled)
                {
                    Program.Log($"  compile FAILED ({errors.Count} errors)");
                    foreach (var e in errors.Take(8)) Program.Log("  - " + e.Split('\n')[0]);
                    feedback = "The code does not compile:\n" + string.Join("\n", errors.Take(30)) + (errors.Count == 0 ? Tail(build.Output, 40) : "") + "\n";
                    total = passed = 0;
                }
                else
                {
                    var resultsDir = Path.Combine(harnessDir, "build", "test-results", "test");
                    if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, true);
                    var test = Gradle(harnessDir, $"test --tests {testClass}", testTimeout);
                    var cases = TestCases(resultsDir);
                    total = cases.Count;
                    passed = cases.Count(c => c.Failure is null);
                    var failing = cases.Where(c => c.Failure is not null).ToList();
                    int diffs = failing.Sum(f => Differences(f.Failure!));
                    attemptJson["tests"] = new JsonObject
                    {
                        ["total"] = total, ["passed"] = passed, ["outputDifferences"] = diffs, ["timedOut"] = test.TimedOut,
                        ["failing"] = new JsonArray(failing.Select(f => (JsonNode)new JsonObject { ["name"] = f.Name, ["message"] = Clip(f.Failure!, 1500) }).ToArray())
                    };
                    Program.Log($"  compiled; acceptance {passed}/{total}, {diffs} output difference(s){(test.TimedOut ? " (TIMED OUT)" : "")}");
                    foreach (var f in failing.Take(8)) Program.Log($"  - FAIL {f.Name}: {Clip(f.Failure!, 160).Replace('\n', ' ')}");
                    if (attempt == 1 && total > 0 && passed == total) firstPassed = true;
                    if (!best.Compiled || passed > best.Passed || (passed == best.Passed && diffs < best.Diffs))
                    {
                        best = (attempt, true, passed, total, diffs, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
                        if (Directory.Exists(resultsDir))
                            foreach (var x in Directory.EnumerateFiles(resultsDir, "*.xml")) File.Copy(x, Path.Combine(outDir, "acceptance-tests.xml"), true);
                    }
                    if (total > 0 && passed == total && !test.TimedOut)
                    {
                        runs.Add(attemptJson);
                        break;
                    }
                    feedback = test.TimedOut
                        ? $"The acceptance test did not finish within {testTimeout.TotalMinutes:N0} minutes (an endless loop?). {passed}/{total} scenarios had passed.\n"
                        : total == 0
                            ? "The acceptance test did not run:\n" + Tail(test.Output, 40) + "\n"
                            : $"{total - passed} of {total} acceptance scenarios fail. Each compares your step with the faithful COBOL program " +
                              "(datasets under data/, console under sysout/, return code):\n" +
                              string.Join("\n", failing.Take(12).Select(f => $"- {f.Name}: {Clip(f.Failure!, 900)}")) + "\n";
                    if (!test.TimedOut && failing.Count > 0 && Probe(harnessDir, testFile, cap.Package, failing.Select(f => f.Name), TimeSpan.FromMinutes(10)) is { } detail)
                    {
                        feedback += "\n" + detail + "\n";
                        attemptJson["probe"] = detail;
                        Program.Log($"  probe: {detail.Split('\n').Length - 1} line(s) of differences");
                    }
                }
            }
            runs.Add(attemptJson);
            if (attempt == attempts) break;
            messages = [.. baseMessages, new("assistant", Render(files)), new("user", feedback +
                "Fix the cause. Return every file you change, complete, each in its own ```java block; files you do not return stay as they are.")];
        }
        // The deliverable is the best attempt (most scenarios passed, then fewest output differences); a later attempt that
        // regressed (for instance stopped compiling) does not replace it.
        if (best.Files is not null) WriteDeliverable(outDir, pkgPath, best.Files);
        else WriteDeliverable(outDir, pkgPath, files);
        var result = best.Files is not null
            ? new Result(true, best.Total, best.Passed, used, firstPassed, best.Attempt, best.Diffs)
            : new Result(false, 0, 0, used, false, 0, 0);
        File.WriteAllText(Path.Combine(outDir, "implementation.run.json"), new JsonObject
        {
            ["schema"] = "mlcpu.implementation-run.v1", ["programId"] = cap.ProgramId, ["model"] = modelName, ["backend"] = backend,
            ["reasoning"] = reasoning, ["design"] = Path.GetFileName(designPath), ["package"] = cap.Package, ["step"] = cap.StepClass,
            ["allowedDependencies"] = Program.Strings(["java.*", EntryInterface(cap), cap.PlatformPackage + ".*"]),
            ["kind"] = cap.Kind, ["astOutline"] = outlineUsed,
            ["acceptanceTest"] = testClass, ["acceptanceTestShownToModel"] = true,
            ["feedback"] = "compiler errors; failing scenarios with the first difference per output; every differing region for the first failing scenario (" + ProbeClass + ")",
            ["guidance"] = Program.Strings(new[] { examplesPath, notesPath, outlineUsed ? outlinePath : null }.OfType<string>().Select(Path.GetFileName).OfType<string>()),
            ["result"] = new JsonObject
            {
                ["deliverable"] = best.Files is not null ? $"best attempt ({best.Attempt})" : "last attempt (none compiled)",
                ["compiled"] = result.Compiled, ["testsTotal"] = result.TestsTotal, ["testsPassed"] = result.TestsPassed,
                ["passedAll"] = result.TestsTotal > 0 && result.TestsPassed == result.TestsTotal, ["outputDifferences"] = result.OutputDifferences,
                ["bestAttempt"] = result.BestAttempt, ["attemptsUsed"] = used, ["firstAttemptPassed"] = firstPassed,
                ["lastAttempt"] = new JsonObject { ["compiled"] = compiled, ["testsPassed"] = passed, ["testsTotal"] = total }
            },
            ["attempts"] = runs
        }.ToJsonString(Program.Json) + "\n");
        Program.Log($"{cap.ProgramId}: {(result.Compiled ? $"acceptance {result.TestsPassed}/{result.TestsTotal}, {result.OutputDifferences} output difference(s), best attempt {result.BestAttempt}" : "does not compile")} after {used} attempt(s); code in {Path.Combine(outDir, "java")}");
        return result;
    }

    static string BuildPrompt(Capability cap, string stepSimple, string packetPath, string designPath, string projectDir, string acceptanceTest, ISet<string> reserved,
        string? examplesPath, string? notesPath)
    {
        var main = Path.Combine(projectDir, "src", "main", "java");
        var platform = Directory.EnumerateFiles(Path.Combine(main, Path.Combine(cap.PlatformPackage.Split('.'))), "*.java", SearchOption.AllDirectories)
            .Append(Path.Combine(main, "com", "mlcpu", "rt", "ModernStep.java"))
            .Concat(cap.Kind == "subprogram" ? [Path.Combine(main, "com", "mlcpu", "rt", "ModernProgram.java")] : [])
            .OrderBy(f => f, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.AppendLine("# Requirements packet (mlcpu.requirements-packet.v1, extracted from the COBOL source)");
        sb.AppendLine(JsonNode.Parse(File.ReadAllText(packetPath))!.ToJsonString());
        sb.AppendLine();
        sb.AppendLine("# Your design (mlcpu.capability-design.v1)");
        sb.AppendLine(JsonNode.Parse(File.ReadAllText(designPath))!.ToJsonString(Program.Json));
        sb.AppendLine();
        sb.AppendLine("# Platform API you may use (complete source)");
        foreach (var f in platform)
        {
            sb.AppendLine($"## {Path.GetRelativePath(main, f).Replace('\\', '/')}");
            sb.AppendLine("```java");
            sb.AppendLine(File.ReadAllText(f).Replace("\r\n", "\n").TrimEnd());
            sb.AppendLine("```");
        }
        sb.AppendLine();
        sb.AppendLine("# Acceptance test (JUnit 5). It runs your step and the faithful COBOL translation on the same inputs and requires identical datasets, SYSOUT and return code");
        sb.AppendLine("```java");
        sb.AppendLine(acceptanceTest.Replace("\r\n", "\n").TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        if (notesPath is not null)
        {
            sb.AppendLine(File.ReadAllText(notesPath).Replace("\r\n", "\n").TrimEnd().Replace("# COBOL parity notes", "# COBOL parity notes (read before you write the code)"));
            sb.AppendLine();
        }
        if (examplesPath is not null)
        {
            sb.AppendLine(File.ReadAllText(examplesPath).Replace("\r\n", "\n").TrimEnd());
            sb.AppendLine();
            sb.AppendLine("Use the worked examples as acceptance criteria: before you answer, walk each example through your code and check that it produces exactly these field values and bytes.");
            sb.AppendLine();
        }
        var callPort = cap.PlatformPackage == "com.mlcpu.carddemo.platform" ? "" : """

              A `CALL 'PGM' USING a b ...` to another program of the job is `ctx.call("PGM", a, b, ...)` with byte arrays laid out like the
              arguments; the called program reads and writes them BY REFERENCE (reimplementing its logic in your own classes is fine too).
            """.TrimEnd();
        var entry = cap.Kind == "subprogram"
            ? $"""
            - Entry point: `public final class {stepSimple} implements com.mlcpu.rt.ModernProgram` with a public no-argument constructor.
              It replaces the COBOL subprogram {cap.ProgramId}: every `CALL '{cap.ProgramId}' USING ...` made by {cap.HostProgram ?? "its caller"} (which stays the
              faithful COBOL translation) reaches `call(byte[][] parameters)`. parameters[i] holds the bytes of the i-th USING argument, its full
              length, in the LINKAGE SECTION layout (dataContracts, AST outline). Arguments are BY REFERENCE: write your results into the same
              arrays; every byte you leave in them is what the caller sees after the CALL. One instance serves every CALL of the run, so keep what
              the COBOL subprogram keeps in WORKING-STORAGE between CALLs in instance fields.
            - Dependencies: only the Java 21 standard library, `com.mlcpu.rt.ModernProgram` and the platform classes in
              `{cap.PlatformPackage}.*` shown above. Write everything else yourself in `{cap.Package}`: domain records, record layouts
              (byte offsets and PICTUREs from dataContracts), services and the entry point. Do not reference any other com.mlcpu class.
            """
            : $"""
            - Entry point: `public final class {stepSimple} implements com.mlcpu.rt.ModernStep` with a public no-argument constructor.
              The job engine calls `run(ctx)`: datasets by DD name through `ctx.dataset(dd)` (a missing input file means the OPEN fails),
              console lines through `ctx.display(line)`, the PARM through `ctx.parm()`, the batch clock through `ctx.now()`. Return the step
              return code; end abnormally the way the COBOL program does (the platform's BatchFailure shows how).{callPort}
            - Dependencies: only the Java 21 standard library, `com.mlcpu.rt.ModernStep` (with its nested types) and the platform classes in
              `{cap.PlatformPackage}.*` shown above. Write everything else yourself in `{cap.Package}`: domain records, record layouts
              (byte offsets and PICTUREs from dataContracts), repositories, readers and writers over the platform datasets, services and the step.
              Do not reference any other com.mlcpu class.
            """;
        sb.AppendLine($"""
            # Task
            Implement the design above for program {cap.ProgramId} as Java 21 source files.

            Contract:
            - Every file is in package `{cap.Package}`.
            {entry.TrimEnd()}
            - Behaviour: reproduce the COBOL program exactly in every acceptance scenario: each output dataset byte for byte (unassigned
              bytes, FILLER, signs and zones included), each console line, the return code, and the error and abend paths.
            - Do not use these type names (they already exist in the package): {string.Join(", ", reserved.Order(StringComparer.Ordinal))}.

            Output: each file as its own fenced block that starts with ```java and whose first line is the package declaration; one top-level
            type per file. Return all files now.
            """);
        return sb.ToString();
    }

    static Dictionary<string, string> Parse(string reply, List<string> problems)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in JavaBlock.Matches(reply))
        {
            var src = m.Groups[1].Value.Replace("\r\n", "\n").TrimEnd() + "\n";
            var type = TopType.Matches(src).Select(t => t.Groups[1].Value).FirstOrDefault();
            if (type is null) { problems.Add($"a ```java block declares no top-level type: {Clip(src, 120)}"); continue; }
            if (files.ContainsKey(type)) problems.Add($"{type} is returned twice; return each file once");
            files[type] = src;
        }
        return files;
    }

    static IEnumerable<string> Check(IDictionary<string, string> files, Capability cap, string stepSimple, ISet<string> reserved)
    {
        foreach (var (name, src) in files)
        {
            var pkg = PackageDecl.Match(src);
            if (!pkg.Success || pkg.Groups[1].Value != cap.Package) yield return $"{name}.java must declare `package {cap.Package};`";
            if (reserved.Contains(name)) yield return $"{name} is already a class in the package (generated); rename yours";
            foreach (var r in MlcpuRef.Matches(src).Select(m => m.Value).Distinct())
                if (!r.StartsWith(EntryInterface(cap)) && !r.StartsWith("com.mlcpu.rt.ModernStep") && !r.StartsWith(cap.PlatformPackage + ".") && !r.StartsWith(cap.Package))
                    yield return $"{name}.java references {r}; only {EntryInterface(cap)} and {cap.PlatformPackage}.* are allowed";
        }
        var iface = cap.Kind == "subprogram" ? "ModernProgram" : "ModernStep";
        if (!files.TryGetValue(stepSimple, out var step)) yield return $"the entry point {stepSimple}.java is missing";
        else if (!Regex.IsMatch(step, @"implements\s+[\w.]*" + iface)) yield return $"{stepSimple} must implement com.mlcpu.rt.{iface}";
    }

    static void PrepareHarness(string projectDir, string harnessDir, Capability cap)
    {
        if (Directory.Exists(harnessDir)) Directory.Delete(harnessDir, true);
        CopyTree(projectDir, harnessDir);
        var pkgDir = Path.Combine(harnessDir, "src", "main", "java", cap.Package.Replace('.', Path.DirectorySeparatorChar));
        foreach (var c in cap.AuthoredClasses)
        {
            var f = Path.Combine(pkgDir, c + ".java");
            if (File.Exists(f)) File.Delete(f);
        }
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

    static void Sync(string pkgDir, IDictionary<string, string> files, HashSet<string> written)
    {
        foreach (var old in written.Where(n => !files.ContainsKey(n)).ToList())
        {
            File.Delete(Path.Combine(pkgDir, old + ".java"));
            written.Remove(old);
        }
        foreach (var (name, src) in files)
        {
            File.WriteAllText(Path.Combine(pkgDir, name + ".java"), src);
            written.Add(name);
        }
    }

    static void WriteDeliverable(string outDir, string pkgPath, IDictionary<string, string> files)
    {
        var dir = Path.Combine(outDir, "java", pkgPath);
        if (Directory.Exists(Path.Combine(outDir, "java"))) Directory.Delete(Path.Combine(outDir, "java"), true);
        Directory.CreateDirectory(dir);
        foreach (var (name, src) in files) File.WriteAllText(Path.Combine(dir, name + ".java"), src);
    }

    static string Render(IDictionary<string, string> files) =>
        string.Join("\n", files.Select(f => $"```java\n{f.Value.TrimEnd()}\n```"));

    sealed record GradleRun(int ExitCode, string Output, bool TimedOut);

    static GradleRun Gradle(string dir, string tasks, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo("cmd.exe", $"/c gradle {tasks} --offline --no-daemon --console=plain")
        {
            WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        bool done = p.WaitForExit(timeout);
        if (!done) { try { p.Kill(entireProcessTree: true); } catch { } p.WaitForExit(30000); }
        else p.WaitForExit();
        lock (output) return new GradleRun(done ? p.ExitCode : -1, output.ToString(), !done);
    }

    static List<string> CompileErrors(string output)
    {
        var lines = output.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var errors = new List<string>();
        for (int i = 0; i < lines.Count; i++)
            if (Regex.IsMatch(lines[i], @"\.java:\d+: error:"))
            {
                var e = new StringBuilder(Regex.Replace(lines[i], @"^.*[\\/](src[\\/])", "$1"));
                for (int k = i + 1; k < lines.Count && k <= i + 3 && !Regex.IsMatch(lines[k], @"\.java:\d+: (error|warning):"); k++) e.Append('\n').Append(lines[k]);
                errors.Add(e.ToString());
            }
        return errors;
    }

    sealed record TestCase(string Name, string? Failure);

    static List<TestCase> TestCases(string resultsDir)
    {
        var list = new List<TestCase>();
        if (!Directory.Exists(resultsDir)) return list;
        foreach (var f in Directory.EnumerateFiles(resultsDir, "TEST-*.xml"))
            foreach (var tc in XDocument.Load(f).Descendants("testcase"))
            {
                var fail = tc.Element("failure") ?? tc.Element("error");
                list.Add(new TestCase(tc.Attribute("name")!.Value,
                    fail is null ? null : (fail.Attribute("message")?.Value is { Length: > 0 } msg ? msg : Clip(fail.Value, 1500))));
            }
        return list;
    }

    static string Clip(string s, int n) => s.Length <= n ? s : s[..n] + " …";

    const string ProbeClass = "MlcpuDiffProbe";

    /// <summary>
    /// A JUnit class compiled into the harness next to the acceptance test. For the scenario named in
    /// build/mlcpu-probe-request.txt it runs both implementations through the acceptance test's own scenarios(), step() and
    /// @BeforeAll setup (by reflection) and writes every differing byte region of every output to build/mlcpu-probe.txt,
    /// not only the first one the acceptance test reports.
    /// </summary>
    static string ProbeSource(string package, string testClass) => $$"""
        package {{package}};

        import com.mlcpu.rt.JobEngine;
        import com.mlcpu.rt.StepEquivalence;
        import java.lang.reflect.Method;
        import java.nio.charset.StandardCharsets;
        import java.nio.file.Files;
        import java.nio.file.Path;
        import java.util.*;
        import org.junit.jupiter.api.Test;
        import org.junit.jupiter.params.provider.Arguments;

        class {{ProbeClass}} {
            @Test
            @SuppressWarnings("unchecked")
            void probe() throws Exception {
                String scenario = Files.readString(Path.of("build", "mlcpu-probe-request.txt")).trim();
                Class<?> t = Class.forName("{{testClass}}");
                for (Method m : t.getDeclaredMethods())
                    if (m.isAnnotationPresent(org.junit.jupiter.api.BeforeAll.class)) { m.setAccessible(true); m.invoke(null); }
                Method scenarios = t.getDeclaredMethod("scenarios");
                scenarios.setAccessible(true);
                Object[] args = null;
                for (Arguments a : ((java.util.stream.Stream<Arguments>) scenarios.invoke(null)).toList())
                    if (scenario.equals(a.get()[0])) args = a.get();
                StringBuilder out = new StringBuilder();
                if (args == null) { Files.writeString(Path.of("build", "mlcpu-probe.txt"), ""); return; }
                Method step = null;
                for (Method m : t.getDeclaredMethods()) if (m.getName().equals("step") && m.getParameterCount() == args.length - 2) step = m;
                step.setAccessible(true);
                JobEngine.Step s = (JobEngine.Step) (args.length == 3 ? step.invoke(null, args[1]) : step.invoke(null));
                Map<String, byte[]> inputs = (Map<String, byte[]>) args[args.length - 1];
                Map<String, java.util.function.Supplier<com.mlcpu.rt.ModernProgram>> calls = Map.of();
                for (Method m : t.getDeclaredMethods())
                    if (m.getName().equals("subprograms") && m.getParameterCount() == 0) { m.setAccessible(true); calls = (Map<String, java.util.function.Supplier<com.mlcpu.rt.ModernProgram>>) m.invoke(null); }
                var legacy = StepEquivalence.run(s, inputs, "legacy");
                var normalized = StepEquivalence.run(s, inputs, "normalized", calls);
                if (legacy.returnCode() != normalized.returnCode())
                    out.append("return code: legacy ").append(legacy.returnCode()).append(" (").append(legacy.detail()).append("), yours ")
                       .append(normalized.returnCode()).append(" (").append(normalized.detail()).append(")\n");
                // Record formats the acceptance test declares (DSN -> fixed:N | variable | print:N): differences are then shown record by record.
                Map<String, String> formats = Map.of();
                try {
                    java.lang.reflect.Field ff = t.getDeclaredField("RECORD_FORMATS");
                    ff.setAccessible(true);
                    formats = (Map<String, String>) ff.get(null);
                } catch (NoSuchFieldException e) {
                    // byte regions only
                }
                var names = new TreeSet<>(legacy.files().keySet());
                names.addAll(normalized.files().keySet());
                for (String n : names) {
                    byte[] a = legacy.files().get(n), b = normalized.files().get(n);
                    if (a == null || b == null) { out.append(n).append(": only ").append(a == null ? "yours" : "the legacy program").append(" wrote it\n"); continue; }
                    if (Arrays.equals(a, b)) continue;
                    String fmt = n.startsWith("data/") ? formats.get(n.substring(5)) : null;
                    if (fmt != null) {
                        out.append(n).append(" (legacy ").append(a.length).append(" bytes, yours ").append(b.length).append("; ").append(fmt).append(" records):\n");
                        recordDiff(out, a, b, fmt);
                        continue;
                    }
                    out.append(n).append(" (legacy ").append(a.length).append(" bytes, yours ").append(b.length).append("):\n");
                    int shown = 0, i = 0, min = Math.min(a.length, b.length), regions = 0;
                    while (i < min) {
                        if (a[i] == b[i]) { i++; continue; }
                        int s0 = i, e = i;
                        while (e + 1 < min) {
                            int k = e + 1;
                            while (k < min && k <= e + 8 && a[k] == b[k]) k++;
                            if (k < min && k <= e + 8) e = k; else break;
                        }
                        regions++;
                        if (shown < 12) {
                            shown++;
                            out.append("  bytes ").append(s0).append('-').append(e).append(where(a, s0)).append(": legacy «").append(show(a, s0 - 12, e + 12))
                               .append("» yours «").append(show(b, s0 - 12, e + 12)).append("»\n");
                        }
                        i = e + 1;
                    }
                    if (a.length != b.length) out.append("  lengths differ from byte ").append(min).append('\n');
                    if (regions > shown) out.append("  ... ").append(regions - shown).append(" more differing region(s)\n");
                }
                Files.writeString(Path.of("build", "mlcpu-probe.txt"), out.toString());
            }

            /** A dataset as records: RDW-delimited (variable), fixed slices, or (print) fixed slices with the ADVANCING bytes taken out. */
            static List<String> records(byte[] f, String fmt, List<String> controls) {
                List<String> recs = new ArrayList<>();
                if (fmt.equals("variable")) {
                    int at = 0;
                    while (at + 4 <= f.length) {
                        int len = ((f[at] & 0xff) << 8) | (f[at + 1] & 0xff);
                        recs.add("(" + len + " bytes) " + show(f, at + 4, Math.min(f.length, at + 4 + len) - 1));
                        at += 4 + len;
                    }
                    return recs;
                }
                int lrecl = Integer.parseInt(fmt.substring(fmt.indexOf(':') + 1));
                boolean print = fmt.startsWith("print");
                StringBuilder ctl = new StringBuilder();
                int i = 0;
                while (i < f.length) {
                    if (print && (f[i] == '\n' || f[i] == '\f')) { ctl.append(f[i] == '\n' ? "\\n" : "\\f"); i++; continue; }
                    if (ctl.length() > 0) { controls.add(ctl + " before record " + (recs.size() + 1)); ctl.setLength(0); }
                    int end = Math.min(f.length, i + lrecl);
                    recs.add(show(f, i, end - 1));
                    i = end;
                }
                if (ctl.length() > 0) controls.add(ctl + " after record " + recs.size());
                return recs;
            }

            static void recordDiff(StringBuilder out, byte[] a, byte[] b, String fmt) {
                List<String> ca = new ArrayList<>(), cb = new ArrayList<>();
                List<String> ra = records(a, fmt, ca), rb = records(b, fmt, cb);
                int diffs = 0, shown = 0;
                for (int k = 0; k < Math.max(ra.size(), rb.size()); k++) {
                    String x = k < ra.size() ? ra.get(k) : null, y = k < rb.size() ? rb.get(k) : null;
                    if (x != null && x.equals(y)) continue;
                    diffs++;
                    if (shown++ < 10)
                        out.append("  record ").append(k + 1).append(":\n    legacy «").append(x == null ? "(none)" : x).append("»\n    yours  «")
                           .append(y == null ? "(none)" : y).append("»\n");
                }
                if (diffs > 10) out.append("  ... ").append(diffs - 10).append(" more differing record(s)\n");
                out.append("  ").append(ra.size()).append(" legacy record(s), ").append(rb.size()).append(" of yours, ").append(diffs).append(" differ\n");
                if (!ca.equals(cb))
                    out.append("  ADVANCING control bytes differ:\n    legacy: ").append(String.join("; ", ca)).append("\n    yours:  ").append(String.join("; ", cb)).append('\n');
            }

            static String where(byte[] f, int at) {
                int line = 1, col = 0;
                boolean text = false;
                for (int i = 0; i < at; i++) if (f[i] == '\n') { line++; col = 0; text = true; } else col++;
                return text ? " (line " + line + ", column " + (col + 1) + ")" : "";
            }

            static String show(byte[] f, int from, int to) {
                StringBuilder sb = new StringBuilder();
                for (int i = Math.max(0, from); i <= Math.min(f.length - 1, to); i++) {
                    int c = f[i] & 0xff;
                    sb.append(c == '\n' ? "\\n" : c >= 0x20 && c < 0x7f ? String.valueOf((char) c) : String.format("\\x%02x", c));
                }
                return sb.toString();
            }
        }
        """;

    /// <summary>Runs the difference probe in an existing harness for one scenario (diagnostics).</summary>
    public static string? ProbeOnly(string harnessDir, string programId, string manifestPath, string scenario)
    {
        var cap = FromManifest(manifestPath, programId);
        var testDir = Path.Combine(harnessDir, "src", "test", "java", cap.Package.Replace('.', Path.DirectorySeparatorChar));
        var testFile = Directory.EnumerateFiles(testDir, "*EquivalenceTest.java").Single();
        return Probe(harnessDir, testFile, cap.Package, [scenario], TimeSpan.FromMinutes(10));
    }

    /// <summary>Every differing region for the first failing parameterized scenario, or null when the probe cannot run.</summary>
    static string? Probe(string harnessDir, string testFile, string package, IEnumerable<string> failing, TimeSpan timeout)
    {
        var scenario = failing.FirstOrDefault(n => !n.EndsWith(')'));
        if (scenario is null) return null;
        var testClass = package + "." + Path.GetFileNameWithoutExtension(testFile);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(testFile)!, ProbeClass + ".java"), ProbeSource(package, testClass));
        Directory.CreateDirectory(Path.Combine(harnessDir, "build"));
        File.WriteAllText(Path.Combine(harnessDir, "build", "mlcpu-probe-request.txt"), scenario);
        var result = Path.Combine(harnessDir, "build", "mlcpu-probe.txt");
        if (File.Exists(result)) File.Delete(result);
        Gradle(harnessDir, $"test --tests {package}.{ProbeClass}", timeout);
        if (!File.Exists(result)) return null;
        var text = File.ReadAllText(result).Trim();
        return text.Length == 0 ? null : $"Every difference in scenario '{scenario}' (up to 12 regions per output; offsets count from 0; context of 12 bytes on each side):\n{Clip(text, 6000)}";
    }

    static string Tail(string s, int lines) => string.Join("\n", s.Split('\n').TakeLast(lines));
}
