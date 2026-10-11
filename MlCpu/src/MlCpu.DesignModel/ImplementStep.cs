using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MlCpu.DesignModel;

/// <summary>
/// Design -> Java. The model writes the capability's business logic on top of the platform layer and the generated record kit
/// (&lt;package&gt;.records: every record area and report line of the program, typed, with COBOL MOVE rules). Each attempt is
/// compiled in a harness (a copy of the exported project with the authored capability classes removed) and judged by the
/// program's equivalence test: the model's step against the faithful COBOL translation, every scenario, byte for byte.
/// Compiler errors (with the source lines, and the API of a platform or kit class the code misused) and failing scenarios
/// (field by field, and where the logic parts from the faithful program) go back to the model, which may repair with
/// SEARCH/REPLACE edits, until everything passes or the attempts run out.
/// </summary>
public static class ImplementStep
{
    public const string SystemPrompt = """
        You are a senior Java 21 engineer. You implement a capability design as production-quality, idiomatic Java:
        immutable records, small focused classes, BigDecimal for money, no reflection, no frameworks. The code replaces a
        COBOL batch program and must reproduce its observable behaviour exactly (output datasets byte for byte, console
        lines, return code), including the legacy quirks the design preserves. You answer with Java source files only,
        each in its own ```java fenced block (or, when repairing, with SEARCH/REPLACE edit blocks).
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

    /// <summary>The generated record kit's package (pipeline v2).</summary>
    static string KitPackage(Capability cap) => cap.Package + ".records";

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
        string? examplesPath = null, string? notesPath = null, string? outlinePath = null, int context = 131072,
        string? patternsDir = null, string? embeddingsUrl = null)
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
        reserved.Add(Probe.ProbeClass);
        var testFile = Directory.EnumerateFiles(Path.Combine(harnessDir, "src", "test", "java", pkgPath), "*EquivalenceTest.java").Single();
        var testClass = cap.Package + "." + Path.GetFileNameWithoutExtension(testFile);
        // The record kit and its layouts sit next to the packet (normalization/<PID>/); a v1 publish has neither.
        var normDir = Path.GetDirectoryName(Path.GetFullPath(packetPath))!;
        var kitPath = Path.Combine(normDir, "record-kit.md");
        var kit = File.Exists(kitPath) && Directory.Exists(Path.Combine(harnessPkg, "records")) ? File.ReadAllText(kitPath).Replace("\r\n", "\n") : null;
        var layoutsPath = Path.Combine(normDir, "record-layouts.json");
        var feedback = new FeedbackRenderer(File.Exists(layoutsPath) ? layoutsPath : null, packetPath, Path.Combine(harnessDir, "src", "main", "java"));
        var api = PublicApi(harnessDir, cap);
        var known = Imports.Known(Path.Combine(harnessDir, "src", "main", "java"), cap.PlatformPackage, KitPackage(cap));

        var patterns = patternsDir is null ? null : new PatternLibrary(patternsDir, embeddingsUrl);
        string? patternFrom = null;
        var pattern = patterns?.Retrieve(cap.ProgramId, packetPath, out patternFrom);
        if (pattern is not null) Program.Log($"  pattern library: the verified solution of {patternFrom} goes into the prompt");

        var prompt = BuildPrompt(cap, stepSimple, packetPath, designPath, projectDir, File.ReadAllText(testFile), reserved, examplesPath, notesPath, kit, pattern);
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
        var fixes = new List<string>();
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
            // Complete files first; edits apply to the other files. An edit that does not apply is reported with the result,
            // it does not void the attempt (the files it would have changed stay as they were).
            var (edits, rest) = Edits.Extract(r.Content);
            var returned = Parse(rest, problems);
            var editProblems = new List<string>();
            int applied = edits.Count == 0 ? 0 : Edits.Apply(files, edits, editProblems, returned.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var (name, src) in returned) files[name] = src;
            problems.AddRange(Check(files, cap, stepSimple, reserved));
            var attemptJson = Program.AttemptJson(attempt, r, problems);
            attemptJson["filesReturned"] = Program.Strings(returned.Keys);
            attemptJson["edits"] = new JsonObject { ["blocks"] = edits.Count, ["applied"] = applied, ["notApplied"] = Program.Strings(editProblems) };
            if (editProblems.Count > 0) Program.Log($"  {editProblems.Count} edit(s) not applied; first: {Clip(editProblems[0], 160)}");
            var editNote = editProblems.Count == 0 ? "" : "Some of your edits were not applied (their files stay as they were):\n- " + string.Join("\n- ", editProblems) + "\n\n";
            attemptJson["files"] = Program.Strings(files.Keys);

            string message;
            if (problems.Count > 0 || files.Count == 0)
            {
                if (files.Count == 0 && problems.Count == 0) problems.Add("no ```java code blocks were found in the reply");
                message = "The files break the contract:\n- " + string.Join("\n- ", problems) + "\n";
                Program.Log($"  contract: {problems.Count} problem(s); first: {Clip(problems[0], 200)}");
                compiled = false;
                total = passed = 0;
            }
            else
            {
                var importsAdded = Imports.Complete(files, known);
                if (importsAdded.Count > 0)
                {
                    attemptJson["importsAdded"] = Program.Strings(importsAdded);
                    Program.Log($"  {importsAdded.Count} missing import(s) added");
                }
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
                    message = errors.Count == 0 ? "The code does not compile:\n" + Tail(build.Output, 40) + "\n" : CompileFeedback(errors, harnessDir, api);
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
                    message = test.TimedOut
                        ? $"The acceptance test did not finish within {testTimeout.TotalMinutes:N0} minutes (an endless loop?). {passed}/{total} scenarios had passed.\n"
                        : total == 0
                            ? "The acceptance test did not run:\n" + Tail(test.Output, 40) + "\n"
                            : $"{total - passed} of {total} acceptance scenarios fail. Each compares your step with the faithful COBOL program " +
                              "(datasets under data/, console under sysout/, return code):\n" +
                              string.Join("\n", failing.Take(12).Select(f => $"- {f.Name}: {Clip(f.Failure!, 400)}")) + "\n";
                    if (!test.TimedOut && failing.Count > 0)
                    {
                        var chosen = ProbeScenarios(failing.Select(f => (f.Name, f.Failure!)));
                        if (chosen.Count > 0 && RunProbe(harnessDir, testFile, cap, chosen, TimeSpan.FromMinutes(10)) is { } probed && probed.Count > 0)
                        {
                            var detail = feedback.Render(probed);
                            message += $"\nDetail for {chosen.Count} failing scenario(s) with different symptoms (fields by their COBOL names; offsets count from 0):\n" + detail + "\n";
                            attemptJson["probe"] = detail;
                            Program.Log($"  probe: {probed.Count} scenario(s), {detail.Split('\n').Length} line(s) of feedback");
                        }
                    }
                }
            }
            message = editNote + message;
            fixes.Add(Clip(message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "", 300));
            runs.Add(attemptJson);
            if (attempt == attempts) break;
            messages = [.. baseMessages, new("assistant", Render(files)), new("user", message + "\nFix the cause. " + Edits.Instructions)];
        }
        // The deliverable is the best attempt (most scenarios passed, then fewest output differences); a later attempt that
        // regressed (for instance stopped compiling) does not replace it.
        if (best.Files is not null) WriteDeliverable(outDir, pkgPath, best.Files);
        else WriteDeliverable(outDir, pkgPath, files);
        var result = best.Files is not null
            ? new Result(true, best.Total, best.Passed, used, firstPassed, best.Attempt, best.Diffs)
            : new Result(false, 0, 0, used, false, 0, 0);
        if (patterns is not null && best.Files is not null && result.TestsTotal > 0 && result.TestsPassed == result.TestsTotal)
            patterns.Record(cap.ProgramId, modelName, packetPath, best.Files, fixes.Take(Math.Max(0, best.Attempt - 1)));
        File.WriteAllText(Path.Combine(outDir, "implementation.run.json"), new JsonObject
        {
            ["schema"] = "mlcpu.implementation-run.v1", ["pipeline"] = "v2", ["programId"] = cap.ProgramId, ["model"] = modelName, ["backend"] = backend,
            ["reasoning"] = reasoning, ["design"] = Path.GetFileName(designPath), ["package"] = cap.Package, ["step"] = cap.StepClass,
            ["allowedDependencies"] = Program.Strings(["java.*", EntryInterface(cap), cap.PlatformPackage + ".*", KitPackage(cap) + ".*"]),
            ["kind"] = cap.Kind, ["astOutline"] = outlineUsed, ["recordKit"] = kit is not null, ["fieldLayouts"] = feedback.HasLayouts,
            ["patternFrom"] = patternFrom,
            ["acceptanceTest"] = testClass, ["acceptanceTestShownToModel"] = true,
            ["feedback"] = "compiler errors with source lines and the API of misused classes; failing scenarios with the first difference per output; " +
                           "for up to three failing scenarios with different symptoms, every differing record by field name and where the logic parts from the faithful program (" + Probe.ProbeClass + "); repairs as SEARCH/REPLACE edits or whole files",
            ["guidance"] = Program.Strings(new[] { examplesPath, notesPath, outlineUsed ? outlinePath : null, kit is not null ? kitPath : null }.OfType<string>().Select(Path.GetFileName).OfType<string>()),
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
        string? examplesPath, string? notesPath, string? kit, string? pattern)
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
        if (kit is not null)
        {
            sb.AppendLine(kit.TrimEnd());
            sb.AppendLine();
        }
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
        if (pattern is not null)
        {
            sb.AppendLine(pattern.TrimEnd());
            sb.AppendLine();
        }
        var callPort = cap.PlatformPackage == "com.mlcpu.carddemo.platform" ? "" : """

              A `CALL 'PGM' USING a b ...` to another program of the job is `ctx.call("PGM", a, b, ...)` with byte arrays laid out like the
              arguments; the called program reads and writes them BY REFERENCE (reimplementing its logic in your own classes is fine too).
            """.TrimEnd();
        var kitDep = kit is null ? "" : $", the generated record kit `{KitPackage(cap)}.*`";
        var kitUse = kit is null ? "" : $"""

            - Records and report lines: use the generated record kit (`{KitPackage(cap)}`) for every record you read, write, rewrite or pass
              to a CALL and for every report or message line. Where the design names record layouts, codecs or line formatters, the kit's
              classes are those: do not write your own offsets, field codecs or edited-number formatting.
            """.TrimEnd();
        var entry = cap.Kind == "subprogram"
            ? $"""
            - Entry point: `public final class {stepSimple} implements com.mlcpu.rt.ModernProgram` with a public no-argument constructor.
              It replaces the COBOL subprogram {cap.ProgramId}: every `CALL '{cap.ProgramId}' USING ...` made by {cap.HostProgram ?? "its caller"} (which stays the
              faithful COBOL translation) reaches `call(byte[][] parameters)`. parameters[i] holds the bytes of the i-th USING argument, its full
              length, in the LINKAGE SECTION layout (dataContracts, AST outline). Arguments are BY REFERENCE: write your results into the same
              arrays; every byte you leave in them is what the caller sees after the CALL. One instance serves every CALL of the run, so keep what
              the COBOL subprogram keeps in WORKING-STORAGE between CALLs in instance fields.
            - Dependencies: only the Java 21 standard library, `com.mlcpu.rt.ModernProgram`, the platform classes in
              `{cap.PlatformPackage}.*` shown above{kitDep}. Write everything else yourself in `{cap.Package}`: domain records,
              services and the entry point. Do not reference any other com.mlcpu class.{kitUse}
            """
            : $"""
            - Entry point: `public final class {stepSimple} implements com.mlcpu.rt.ModernStep` with a public no-argument constructor.
              The job engine calls `run(ctx)`: datasets by DD name through `ctx.dataset(dd)` (a missing input file means the OPEN fails),
              console lines through `ctx.display(line)`, the PARM through `ctx.parm()`, the batch clock through `ctx.now()`. Return the step
              return code; end abnormally the way the COBOL program does (the platform's BatchFailure shows how).{callPort}
            - Dependencies: only the Java 21 standard library, `com.mlcpu.rt.ModernStep` (with its nested types), the platform classes in
              `{cap.PlatformPackage}.*` shown above{kitDep}. Write everything else yourself in `{cap.Package}`: domain records,
              repositories, readers and writers over the platform datasets, services and the step. Do not reference any other com.mlcpu class.{kitUse}
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
            if (src.Trim().Length == 0) continue;
            var type = TopType.Matches(src).Select(t => t.Groups[1].Value).FirstOrDefault();
            if (type is null) { problems.Add($"a ```java block declares no top-level type: {Clip(src, 120)}"); continue; }
            if (files.ContainsKey(type)) problems.Add($"{type} is returned twice; return each file once");
            files[type] = src;
        }
        return files;
    }

    static IEnumerable<string> Check(IDictionary<string, string> files, Capability cap, string stepSimple, ISet<string> reserved)
    {
        var kitPkg = KitPackage(cap) + ".";
        foreach (var (name, src) in files)
        {
            var pkg = PackageDecl.Match(src);
            if (!pkg.Success || pkg.Groups[1].Value != cap.Package) yield return $"{name}.java must declare `package {cap.Package};`";
            if (reserved.Contains(name)) yield return $"{name} is already a class in the package (generated); rename yours";
            foreach (var r in MlcpuRef.Matches(src).Select(m => m.Value).Distinct())
            {
                bool own = r == cap.Package || (r.StartsWith(cap.Package + ".") && !r[(cap.Package.Length + 1)..].Contains('.') && char.IsUpper(r[cap.Package.Length + 1]));
                if (!r.StartsWith(EntryInterface(cap)) && !r.StartsWith("com.mlcpu.rt.ModernStep") && !r.StartsWith(cap.PlatformPackage + ".")
                    && !r.StartsWith(kitPkg) && r != KitPackage(cap) && !own)
                    yield return $"{name}.java references {r}; only {EntryInterface(cap)}, {cap.PlatformPackage}.*, {KitPackage(cap)}.* and your own classes are allowed";
            }
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
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < lines.Count; i++)
            // Gradle repeats javac's errors in its failure summary: keep each file:line:message once.
            if (Regex.IsMatch(lines[i], @"\.java:\d+: error:") && seen.Add(Regex.Replace(lines[i].Trim(), @"^.*[\\/](src[\\/])", "$1")))
            {
                var e = new StringBuilder(Regex.Replace(lines[i], @"^.*[\\/](src[\\/])", "$1"));
                for (int k = i + 1; k < lines.Count && k <= i + 5 && !Regex.IsMatch(lines[k], @"\.java:\d+: (error|warning):") && !lines[k].StartsWith("> Task", StringComparison.Ordinal)
                                    && !Regex.IsMatch(lines[k], @"^\d+ errors?$"); k++) e.Append('\n').Append(lines[k]);
                errors.Add(e.ToString());
            }
        return errors;
    }

    static readonly Regex ErrorHead = new(@"^(src[\\/].*?\.java):(\d+): error: (.*)$");
    static readonly Regex SyntaxError = new(@"expected$|illegal start of|not a statement|unclosed|reached end of file|class, interface, enum, or record expected|orphaned|without 'if'|without 'try'|'catch' without|'finally' without|illegal character|invalid method declaration");

    /// <summary>
    /// Compiler errors for the model: per file, a syntax error shows only its first occurrence with the numbered source lines
    /// around it (the errors after it follow from it); other errors keep javac's context, and an error about a platform or kit
    /// class (cannot find symbol, no suitable method, cannot be applied) adds that class's public API.
    /// </summary>
    static string CompileFeedback(List<string> errors, string harnessDir, IReadOnlyDictionary<string, List<string>> api)
    {
        var sb = new StringBuilder("The code does not compile:\n");
        var classesShown = new HashSet<string>(StringComparer.Ordinal);
        int shown = 0;
        foreach (var file in errors.GroupBy(e => ErrorHead.Match(e.Split('\n')[0]) is { Success: true } m ? m.Groups[1].Value : ""))
        {
            var list = file.ToList();
            var syntax = list.FirstOrDefault(e => ErrorHead.Match(e.Split('\n')[0]) is { Success: true } m && SyntaxError.IsMatch(m.Groups[3].Value));
            if (syntax is not null && file.Key.Length > 0)
            {
                var m = ErrorHead.Match(syntax.Split('\n')[0]);
                int line = int.Parse(m.Groups[2].Value);
                sb.Append($"{file.Key}:{line}: error: {m.Groups[3].Value}\n").Append(Excerpt(Path.Combine(harnessDir, file.Key), line, 4));
                if (list.Count > 1) sb.Append($"({list.Count - 1} further error(s) in this file are not shown: they follow from this one. Check brackets, parentheses and semicolons around these lines.)\n");
                shown++;
                continue;
            }
            foreach (var e in list.Take(10))
            {
                sb.Append(e).Append('\n');
                shown++;
                foreach (Match c in Regex.Matches(e, @"(?:location: (?:class|variable \w+ of type|interface) |no suitable method found for \w+\(|method \w+ in class |cannot find symbol\s+symbol:\s+class |is not public in |has private access in )([\w.]+)"))
                {
                    var cls = c.Groups[1].Value[(c.Groups[1].Value.LastIndexOf('.') + 1)..];
                    if (api.TryGetValue(cls, out var members) && classesShown.Add(cls))
                        sb.Append($"  ({cls}'s public API: {string.Join("; ", members.Take(60))})\n");
                }
            }
            if (list.Count > 10) sb.Append($"(… {list.Count - 10} more error(s) in {file.Key})\n");
        }
        return sb.ToString();
    }

    static string Excerpt(string path, int line, int around)
    {
        if (!File.Exists(path)) return "";
        var lines = File.ReadAllLines(path);
        var sb = new StringBuilder();
        for (int i = Math.Max(1, line - around); i <= Math.Min(lines.Length, line + around); i++)
            sb.Append(i == line ? ">" : " ").Append($"{i,5}: ").Append(lines[i - 1]).Append('\n');
        return sb.ToString();
    }

    static readonly Regex PublicMember = new(@"^\s*public\s+(?!final\s+class|class|interface|record|enum)(?:static\s+)?(?:final\s+)?([\w<>\[\],.? ]+?\s+)?(\w+)\s*(\([^)]*\))?\s*(?:throws [\w., ]+)?\s*(?:[{;=]|$)", RegexOptions.Multiline);

    /// <summary>Public members (one line each) of the platform layer, the record kit and the entry interfaces, by simple class name.</summary>
    static Dictionary<string, List<string>> PublicApi(string harnessDir, Capability cap)
    {
        var main = Path.Combine(harnessDir, "src", "main", "java");
        var roots = new[] { Path.Combine(main, Path.Combine(cap.PlatformPackage.Split('.'))), Path.Combine(main, Path.Combine(KitPackage(cap).Split('.'))) };
        var api = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var f in roots.Where(Directory.Exists).SelectMany(r => Directory.EnumerateFiles(r, "*.java", SearchOption.AllDirectories))
                     .Append(Path.Combine(main, "com", "mlcpu", "rt", "ModernStep.java")).Where(File.Exists))
        {
            var members = new List<string>();
            foreach (Match m in PublicMember.Matches(File.ReadAllText(f)))
            {
                var text = Regex.Replace(m.Value.Trim().TrimEnd('{', ';', '=').Trim(), @"\s+", " ");
                if (text.StartsWith("public ")) text = text[7..];
                members.Add(text);
            }
            api[Path.GetFileNameWithoutExtension(f)] = members;
        }
        return api;
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

    static readonly Regex Symptom = new(@"(data/[^:\s]+|sysout/[^:\s]+|return code)");
    static readonly Regex FirstLegacy = new(@"legacy [«�]([^»�]{0,40})");

    /// <summary>
    /// Up to three failing parameterized scenarios with different symptoms (which outputs differ, and the legacy text at the first
    /// difference, digits folded), oracle scenarios first.
    /// </summary>
    static List<string> ProbeScenarios(IEnumerable<(string Name, string Failure)> failing)
    {
        var chosen = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Plain test methods ("…Default()") are not scenarios() entries; scenario names may end in ")" ("unknown card (abend)").
        foreach (var (name, failure) in failing.Where(f => !f.Name.EndsWith("()") && !f.Name.StartsWith("GnuCOBOL oracle", StringComparison.Ordinal))
                     .OrderBy(f => f.Name.StartsWith("oracle scenario", StringComparison.Ordinal) ? 0 : 1))
        {
            var text = FirstLegacy.Match(failure) is { Success: true } m ? Regex.Replace(m.Groups[1].Value.Trim(), @"\d", "9") : "";
            var symptom = string.Join(",", Symptom.Matches(failure).Select(x => x.Value).Distinct().Order(StringComparer.Ordinal))
                          + "|" + (text.Length > 12 ? text[..12] : text);
            if (!seen.Add(symptom)) continue;
            chosen.Add(name);
            if (chosen.Count == 3) break;
        }
        return chosen;
    }

    /// <summary>
    /// Replays a recorded run's replies 1..attempt (each applied as the run applied it) in a fresh harness of the given project,
    /// compiles and tests the result, and returns the feedback this pipeline gives for it (diagnostics: the replay verb).
    /// </summary>
    public static string Replay(Capability cap, string packetPath, string projectDir, string harnessDir, IReadOnlyList<string> replies, TimeSpan testTimeout)
    {
        var stepSimple = cap.StepClass[(cap.StepClass.LastIndexOf('.') + 1)..];
        var pkgPath = cap.Package.Replace('.', Path.DirectorySeparatorChar);
        PrepareHarness(projectDir, harnessDir, cap);
        var harnessPkg = Path.Combine(harnessDir, "src", "main", "java", pkgPath);
        var reserved = Directory.EnumerateFiles(harnessPkg, "*.java").Select(Path.GetFileNameWithoutExtension).Select(n => n!).ToHashSet(StringComparer.Ordinal);
        var testFile = Directory.EnumerateFiles(Path.Combine(harnessDir, "src", "test", "java", pkgPath), "*EquivalenceTest.java").Single();
        var testClass = cap.Package + "." + Path.GetFileNameWithoutExtension(testFile);
        var normDir = Path.GetDirectoryName(Path.GetFullPath(packetPath))!;
        var layoutsPath = Path.Combine(normDir, "record-layouts.json");
        var feedback = new FeedbackRenderer(File.Exists(layoutsPath) ? layoutsPath : null, packetPath, Path.Combine(harnessDir, "src", "main", "java"));
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var reply in replies)
        {
            var (edits, rest) = Edits.Extract(reply);
            var returned = Parse(rest, problems);
            if (edits.Count > 0) Edits.Apply(files, edits, [], returned.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var (name, src) in returned) files[name] = src;
        }
        problems.AddRange(Check(files, cap, stepSimple, reserved));
        if (problems.Count > 0) return "The files break the contract:\n- " + string.Join("\n- ", problems) + "\n";
        var added = Imports.Complete(files, Imports.Known(Path.Combine(harnessDir, "src", "main", "java"), cap.PlatformPackage, KitPackage(cap)));
        Sync(harnessPkg, files, []);
        var build = Gradle(harnessDir, "compileJava compileTestJava", TimeSpan.FromMinutes(10));
        if (build.ExitCode != 0)
        {
            var errors = CompileErrors(build.Output);
            return (added.Count > 0 ? $"imports added: {string.Join("; ", added)}\n" : "") + (errors.Count == 0 ? Tail(build.Output, 40) : CompileFeedback(errors, harnessDir, PublicApi(harnessDir, cap)));
        }
        var resultsDir = Path.Combine(harnessDir, "build", "test-results", "test");
        if (Directory.Exists(resultsDir)) Directory.Delete(resultsDir, true);
        var test = Gradle(harnessDir, $"test --tests {testClass}", testTimeout);
        var cases = TestCases(resultsDir);
        var failing = cases.Where(c => c.Failure is not null).ToList();
        var sb = new StringBuilder($"{(added.Count > 0 ? $"imports added: {string.Join("; ", added)}\n" : "")}acceptance {cases.Count - failing.Count}/{cases.Count}\n");
        foreach (var f in failing.Take(12)) sb.Append($"- {f.Name}: {Clip(f.Failure!, 400)}\n");
        var chosen = ProbeScenarios(failing.Select(f => (f.Name, f.Failure!)));
        if (!test.TimedOut && chosen.Count > 0 && RunProbe(harnessDir, testFile, cap, chosen, TimeSpan.FromMinutes(10)) is { } probed)
            sb.Append($"\nDetail for {chosen.Count} failing scenario(s) with different symptoms:\n").Append(feedback.Render(probed)).Append('\n');
        return sb.ToString();
    }

    /// <summary>Runs the difference probe in an existing harness for the given scenarios (diagnostics: the probe verb).</summary>
    public static string? ProbeOnly(string harnessDir, string programId, string manifestPath, string scenario)
    {
        var cap = FromManifest(manifestPath, programId);
        var testDir = Path.Combine(harnessDir, "src", "test", "java", cap.Package.Replace('.', Path.DirectorySeparatorChar));
        var testFile = Directory.EnumerateFiles(testDir, "*EquivalenceTest.java").Single();
        var probed = RunProbe(harnessDir, testFile, cap, scenario.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), TimeSpan.FromMinutes(10));
        if (probed is null) return null;
        var norm = Path.Combine(harnessDir, "normalization", programId);
        var layouts = Path.Combine(norm, "record-layouts.json");
        return new FeedbackRenderer(File.Exists(layouts) ? layouts : null, Path.Combine(norm, "requirements.json"), Path.Combine(harnessDir, "src", "main", "java")).Render(probed);
    }

    /// <summary>Runs the probe class in the harness for the scenarios; null when it cannot run.</summary>
    static IReadOnlyList<ProbeScenario>? RunProbe(string harnessDir, string testFile, Capability cap, List<string> scenarios, TimeSpan timeout)
    {
        var testClass = cap.Package + "." + Path.GetFileNameWithoutExtension(testFile);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(testFile)!, Probe.ProbeClass + ".java"), Probe.Source(cap.Package, testClass, cap.Kind, cap.ProgramId));
        Directory.CreateDirectory(Path.Combine(harnessDir, "build"));
        File.WriteAllText(Path.Combine(harnessDir, "build", "mlcpu-probe-request.txt"), string.Join("\n", scenarios) + "\n");
        var result = Path.Combine(harnessDir, "build", "mlcpu-probe.json");
        if (File.Exists(result)) File.Delete(result);
        var run = Gradle(harnessDir, $"test --tests {cap.Package}.{Probe.ProbeClass}", timeout);
        if (!File.Exists(result))
        {
            Program.Log("  probe did not run: " + Clip(Tail(run.Output, 6).Replace('\n', ' '), 300));
            return null;
        }
        try { return Probe.Parse(File.ReadAllText(result)); }
        catch (Exception e) { Program.Log("  probe output unreadable: " + e.Message); return null; }
    }

    static string Tail(string s, int lines) => string.Join("\n", s.Split('\n').TakeLast(lines));
}
