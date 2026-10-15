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
    // A qualified name (`enum com.mlcpu.x.RoundingMode`, which javac rejects) names the file by its last part: taking "com" made a
    // stray com.java that no reply could replace or remove, and every later attempt failed on it (kit A/B/C, arm B, FNDKRND).
    static readonly Regex TopType = new(@"^(?:public\s+)?(?:(?:final|abstract|sealed|non-sealed|static)\s+)*(?:class|record|interface|enum)\s+(?:\w+\.)*(\w+)", RegexOptions.Multiline);
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
        string? patternsDir = null, string? embeddingsUrl = null, IReadOnlyDictionary<string, string>? warmStart = null, bool debugPrompt = false)
    {
        Directory.CreateDirectory(outDir);
        var stepSimple = cap.StepClass[(cap.StepClass.LastIndexOf('.') + 1)..];
        var pkgPath = cap.Package.Replace('.', Path.DirectorySeparatorChar);
        PrepareHarness(projectDir, harnessDir, cap);
        var harnessPkg = Path.Combine(harnessDir, "src", "main", "java", pkgPath);
        // Kit features (normalization/kit-features.json): a kit without them is rendered exactly as pipeline v2 rendered it.
        var features = KitFeatures.Of(packetPath);
        if (features.All.Count > 0) Program.Log($"  kit features: {string.Join(", ", features.All)} ({features.Source})");
        // The typed reference (kit feature typed-reference): its given classes go into the package before the names are reserved.
        var typed = cap.Kind == "subprogram" ? TypedReferenceKit.Load(packetPath, features) : null;
        if (typed is not null)
        {
            typed.Install(harnessPkg);
            Program.Log($"  typed reference ({typed.Verified}): {typed.Given.Count} given class(es) in the package, starting files {string.Join(", ", typed.Starting.Keys)}");
        }
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

        // The library-equivalence probe's verified statements (a subprogram's), also next to the packet.
        var equivalences = DesignStep.Equivalences(packetPath);
        if (equivalences is not null) Program.Log("  verified library equivalences go into the prompt");
        // The verified contracts of the programs it CALLs (callee-contracts.md), likewise.
        var callees = DesignStep.CalleeContracts(packetPath);
        if (callees is not null) Program.Log("  contracts of the programs it CALLs go into the prompt");
        // The worked examples are cut, whole scenario sections at a time, so that the prompt and a full reply fit the context
        // (a step over a 262 KB record can trace 144 KB of examples).
        // Sizes are the tokenizer's (a packet runs about 2 characters per token, not 3.6): the reply keeps maxTokens of
        // the context, lowered to no less than 16,000 when the core prompt is large; then whole example sections are added
        // while they fit, and the AST outline only if it still fits. FNEM0700's first prompt, sized by characters, was
        // 128,245 tokens of a 131,072 context, and every reply was cut off.
        int Tokens(string s) => chat.CountTokens(s) ?? (int)Math.Ceiling(s.Length / 2.0);
        var plan = PlanPrompt(cap, stepSimple, packetPath, designPath, projectDir, File.ReadAllText(testFile), reserved, examplesPath, notesPath, outlinePath,
            kit, pattern, equivalences, callees, features, context, maxTokens, Tokens, outDir, typed?.Prompt);
        maxTokens = plan.MaxTokens;
        examplesPath = plan.ExamplesPath;
        var prompt = plan.Prompt;
        bool outlineUsed = plan.OutlineUsed;
        var shadow = features.Has(KitFeatures.ShadowFeedback) && cap.Kind == "subprogram"
            ? new ShadowFeedback(cap.ProgramId, File.Exists(layoutsPath) ? layoutsPath : null, packetPath, NestedShadow(features, projectDir)) : null;
        var stepCalls = cap.Kind != "subprogram" && NestedShadow(features, projectDir)
            ? new ShadowFeedback(cap.ProgramId, File.Exists(layoutsPath) ? layoutsPath : null, packetPath, nested: true) : null;
        // The faithful statement at a differing CALL, from the step tags of the faithful classes in the harness.
        if (shadow is not null) shadow.Statements = feedback.StatementsAt;
        if (stepCalls is not null) stepCalls.Statements = feedback.StatementsAt;
        // Debug mode: the outline the debug prompt takes its COBOL slices from.
        var debugOutline = debugPrompt && outlinePath is not null && File.Exists(outlinePath) ? File.ReadAllText(outlinePath) : null;
        // Debug mode: the packet's legacy quirks and the parity notes, which the first prompt carries and a repair must keep.
        var debugNotes = debugPrompt ? DebugPrompt.Notes(packetPath, notesPath) : null;
        File.WriteAllText(Path.Combine(outDir, "implementation.prompt.md"), prompt);
        var baseMessages = new List<ChatMessage> { new("system", SystemPrompt), new("user", prompt) };
        var messages = new List<ChatMessage>(baseMessages);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // The typed reference's logic and entry class are the starting files: edits apply to them, a returned file replaces one.
        if (typed is not null) foreach (var (n, src) in typed.Starting) files[n] = src;
        var written = new HashSet<string>(StringComparer.Ordinal);
        var runs = new JsonArray();
        var fixes = new List<string>();
        string lastFeedback = "";
        string? lastReply = null;
        bool compiled = false, firstPassed = false;
        int total = 0, passed = 0, used = 0;
        (int Attempt, bool Compiled, int Passed, int Total, int Diffs, SortedDictionary<string, string>? Files) best = (0, false, 0, 0, int.MaxValue, null);
        // The feedback the best code got, and how many attempts in a row have done worse than it (see the rollback below).
        string? bestFeedback = null;
        int worseStreak = 0;
        // CALLs with other results than the faithful program's (subprograms' shadow run): the tie-breaker between attempts with the
        // same acceptance result. FNEM0300 cut its differing CALLs from 27 to 11 while its output differences stayed at 71.
        int bestCalls = int.MaxValue;
        // The scenarios the best code passes (the ratchet: an attempt that loses one of them is told which, and counts as worse
        // unless it passes more), and whether the next turn is a debug turn.
        var bestPassing = new HashSet<string>(StringComparer.Ordinal);
        bool debugTurn = false;
        // The COBOL paragraphs the best code's feedback points at: the slice of a debug turn after a rollback or after an
        // attempt that gave no CALL-level feedback (it did not compile).
        IReadOnlyList<string> bestGuide = [];
        // The attempts since the best code, each worse than it: a debug turn names them. With a fixed seed and a rollback, the
        // debug prompt was otherwise the same as two turns before, and so was the reply (Gemma's FNEM0300 looped on two answers).
        var tried = new List<string>();
        // A debug turn's reply may use the context its smaller prompt leaves (the reply budget was planned for the first prompt:
        // Muse Glimmer's reasoning and answer on FNEM0300 run past it).
        int debugReplyTokens = maxTokens;
        // A debug turn's messages: the focused prompt alone, built from the current code and the last feedback (also written out).
        List<ChatMessage> DebugMessages(int forAttempt, string status, IReadOnlyList<string> guide, IReadOnlyCollection<string> passing)
        {
            // Most scenarios pass (in the code shown): edits only.
            bool editsOnly = best.Total > 0 && passing.Count * 2 >= best.Total;
            var text = DebugPrompt.Build(cap.ProgramId, prompt, debugOutline, kit, equivalences, callees, guide, Render(files), lastFeedback, status, passing,
                editsOnly, tried, debugNotes);
            File.WriteAllText(Path.Combine(outDir, $"implementation.debug{forAttempt}.prompt.md"), text);
            int size = Tokens(text);
            debugReplyTokens = Math.Max(maxTokens, context - size - Tokens(SystemPrompt) - 512);
            Program.Log($"  debug prompt for attempt {forAttempt}: {size:N0} tokens (the first prompt: {Tokens(prompt):N0}; reply up to {debugReplyTokens:N0}); " +
                        $"COBOL slice: {SliceLabel(guide)}");
            return [new("system", SystemPrompt), new("user", text)];
        }
        string SliceLabel(IReadOnlyList<string> guide)
        {
            if (guide.Count > 0) return string.Join(", ", guide.Take(6));
            if (debugOutline is null) return "none";
            var (heading, slice) = DebugPrompt.Slice(debugOutline.Replace("\r\n", "\n"), guide, lastFeedback);
            return slice.Length == 0 ? "none" : heading.Contains("whole PROCEDURE DIVISION") ? "the whole PROCEDURE DIVISION" : "the paragraphs that write the differing items";
        }
        string backend = chat.Name;
        // Warm start (implement-all --warm-start): the previous run's best code is where this run begins, with the feedback this
        // pipeline gives for it, so a run that ended while still converging (FNEM0300, round 3b: 85 differences at its last
        // attempt, the cause then named by the nested-CALL feedback but never shown) goes on instead of starting over.
        if (warmStart is { Count: > 0 })
        {
            foreach (var (n, src) in warmStart) files[n] = src;
            // The starting code is the best so far: an attempt replaces it only by doing better, so a run that only regresses
            // (FNEM0300, round 3c: 71 differences in, 143 after its first attempt) hands back the code it began with.
            IReadOnlyList<string> startGuide = [];
            var start = FeedbackFor(cap, packetPath, projectDir, harnessDir, files, testTimeout,
                results: s =>
                {
                    best = (0, true, s.Passed, s.Total, s.Diffs, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
                    bestCalls = s.Calls;
                    bestPassing = s.Passing;
                    startGuide = s.Paragraphs;
                });
            bestGuide = startGuide;
            File.WriteAllText(Path.Combine(outDir, "implementation.warm-start.txt"), start);
            Program.Log($"  warm start: {warmStart.Count} file(s) of the previous run's best attempt; " +
                        Clip(start.Split('\n').FirstOrDefault(l => l.StartsWith("acceptance", StringComparison.Ordinal)) ?? "feedback ready", 80));
            lastFeedback = start + "\nFix the cause. " + Edits.Instructions;
            if (best.Files is not null) bestFeedback = start;
            // Debug mode: the code compiles, so the first turn is already a repair with the focused prompt.
            debugTurn = debugPrompt && best.Files is not null;
            messages = debugTurn
                ? DebugMessages(1, $"Your {cap.ProgramId} compiles and {best.Passed} of {best.Total} acceptance scenarios pass.", startGuide, bestPassing)
                : [.. baseMessages, new("assistant", Render(files)), new("user", lastFeedback)];
        }
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            used = attempt;
            // This attempt's acceptance result (-1: it did not compile or test), for the rollback below.
            int attemptPassed = -1, attemptDiffs = int.MaxValue, attemptCalls = int.MaxValue;
            HashSet<string>? passingNow = null;
            IReadOnlyList<string>? attemptGuide = null;
            ChatResult r;
            try
            {
                r = chat.Complete(messages, debugTurn ? debugReplyTokens : maxTokens, reasoning);
            }
            catch (HttpRequestException e) when (e.Message.StartsWith("llama-server error:", StringComparison.Ordinal) && e.Message.Contains("\"code\":500"))
            {
                // The server could not read the model's reply (for instance a reply outside the chat template's format): a
                // malformed reply, so a failed attempt; the model is asked to answer again. A server that is down still ends the run.
                Program.Log($"implement attempt {attempt}: the server could not read the model's reply ({Clip(e.Message, 200)}); counted as a failed attempt");
                File.WriteAllText(Path.Combine(outDir, $"implementation.attempt{attempt}.txt"), "(no reply: " + e.Message + ")\n");
                var failed = new JsonObject { ["attempt"] = attempt, ["backend"] = chat.Name, ["replyError"] = Clip(e.Message, 500), ["problems"] = new JsonArray() };
                runs.Add(failed);
                fixes.Add("the reply could not be read by the server");
                if (attempt == attempts) break;
                messages = [.. baseMessages, new("assistant", Render(files)), new("user",
                    "Your last reply could not be read: the server rejected its format. Answer again, in the format asked for." +
                    (lastFeedback.Length > 0 ? "\n\nThe feedback you were answering:\n" + lastFeedback : "\n\n" + Edits.Instructions))];
                continue;
            }
            backend = r.Backend;
            Program.Log($"implement attempt {attempt}: prompt {r.PromptTokens} tok @ {r.PromptTokensPerSecond:N0} tok/s, generated {r.GeneratedTokens} tok @ {r.TokensPerSecond:N1} tok/s, {r.Seconds:N0}s, stop={r.Stop} [{r.Backend}]");
            // A reply cut off by the length limit that repeats the previous one exactly: the context is exhausted and further
            // attempts would repeat it (FNEM0700 repeated one truncated reply 14 times). End the program here.
            if (r.Stop.StartsWith("length", StringComparison.Ordinal) && r.Content.Length > 0 && r.Content == lastReply)
            {
                Program.Log("  the reply was cut off and repeats the previous one: the context is exhausted; no further attempts");
                File.WriteAllText(Path.Combine(outDir, $"implementation.attempt{attempt}.txt"), r.Content);
                break;
            }
            lastReply = r.Content;
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
            // Recorded here, so an attempt that passes or does not compile is marked too.
            if (debugTurn) attemptJson["debugPrompt"] = true;
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
                // The deliverable folder holds the best code so far (written where the best changes), not this attempt's: a run that
                // is stopped leaves its best behind, and a warm start begins from it.
                if (best.Files is null) WriteDeliverable(outDir, pkgPath, files);
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
                    var cases = TestCases(resultsDir, cap.Package);
                    total = cases.Count;
                    passed = cases.Count(c => c.Failure is null);
                    var failing = cases.Where(c => c.Failure is not null).ToList();
                    int diffs = failing.Sum(f => Differences(f.Failure!));
                    (attemptPassed, attemptDiffs) = (passed, diffs);
                    passingNow = cases.Where(c => c.Failure is null).Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
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
                        WriteDeliverable(outDir, pkgPath, best.Files);
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
                              FailureLines(failing);
                    if (!test.TimedOut && failing.Count > 0)
                    {
                        var chosen = ProbeScenarios(failing.Select(f => (f.Name, f.Failure!)));
                        var shadowWanted = shadow is null ? null : ShadowScenarios(failing.Select(f => f.Name));
                        if (chosen.Count > 0 || shadowWanted is { Count: > 0 })
                        {
                            var (probed, shadowed) = RunProbe(harnessDir, testFile, cap, chosen, shadowWanted, TimeSpan.FromMinutes(10), shadow?.Callees,
                                stepCalls?.Callees);
                            if (probed is { Count: > 0 })
                            {
                                var detail = feedback.Render(probed);
                                message += $"\nDetail for {chosen.Count} failing scenario(s) with different symptoms (fields by their COBOL names; offsets count from 0):\n" + detail + "\n";
                                attemptJson["probe"] = detail;
                                Program.Log($"  probe: {probed.Count} scenario(s), {detail.Split('\n').Length} line(s) of feedback");
                            }
                            if (stepCalls is not null && ReadStepCalls(harnessDir) is { Count: > 0 } sc)
                            {
                                // The step's first CALL that parts from the faithful step's goes first, like the subprograms' shadow.
                                var text = stepCalls.RenderStepCalls(sc);
                                attemptGuide = stepCalls.Paragraphs.ToList();
                                message = text + "\n" + message;
                                attemptJson["stepCalls"] = text;
                                Program.Log($"  step CALLs: '{sc[0].Name}': faithful {sc[0].Nested.FaithfulTotal}, yours {sc[0].Nested.YoursTotal} (rc {sc[0].FaithfulRc} / {sc[0].YoursRc})");
                            }
                            if (shadowed is { Count: > 0 }) attemptCalls = shadowed.Sum(s => s.Differing);
                            if (shadow is not null && shadowed is { Count: > 0 })
                            {
                                // CALL-level shadow feedback goes first: the first CALL whose results differ, not the output byte far after it.
                                var text = shadow.Render(shadowed);
                                attemptGuide = shadow.Paragraphs.ToList();
                                message = text + "\n" + message;
                                attemptJson["shadow"] = text;
                                Program.Log($"  shadow: {shadowed.Count} scenario(s), first differing CALL: " +
                                            (shadowed.FirstOrDefault(s => s.First.Count > 0) is { } h ? $"{h.First[0].Seq} of '{h.Name}'" : "none"));
                            }
                        }
                    }
                }
            }
            // The ratchet: scenarios the best code passes that this attempt fails are named (a fix elsewhere broke them).
            var lost = passingNow is null || best.Attempt == attempt ? new List<string>() : bestPassing.Where(s => !passingNow.Contains(s)).ToList();
            if (lost.Count > 0)
                message += $"\nThese scenarios passed in your best version and fail now (restore them): {string.Join(", ", lost.Take(20))}" +
                           $"{(lost.Count > 20 ? $" and {lost.Count - 20} more" : "")}.\n";
            message = editNote + message;
            fixes.Add(Clip(message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "", 300));
            runs.Add(attemptJson);
            // The same acceptance result with fewer differing CALLs is a better attempt (closer to the faithful program).
            if (best.Files is not null && best.Attempt != attempt && attemptPassed == best.Passed && attemptDiffs == best.Diffs && attemptCalls < bestCalls)
            {
                best = (attempt, true, attemptPassed, best.Total, attemptDiffs, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
                WriteDeliverable(outDir, pkgPath, best.Files);
                Program.Log($"  best so far: same acceptance result, {attemptCalls} differing CALL(s) (was {(bestCalls == int.MaxValue ? "unknown" : bestCalls.ToString())})");
            }
            if (best.Attempt == attempt)
            {
                bestCalls = attemptCalls;
                if (passingNow is not null) bestPassing = passingNow;
                if (attemptGuide is not null) bestGuide = attemptGuide;
                tried.Clear();
            }
            else
                tried.Add($"attempt {attempt}: " + (attemptPassed < 0
                    ? Clip(message.Split('\n').FirstOrDefault(l => l.Trim().Length > 0) ?? "it did not compile", 200)
                    : $"{attemptPassed}/{best.Total} pass, {attemptDiffs} output difference(s){(lost.Count > 0 ? $", {lost.Count} that passed now fail" : "")}"));
            // Only attempts that do strictly worse than the best, one after another, lead to a rollback: one that ties it is other code
            // with the same result, which may be on its way (FNEM0300 tied at 71 differences and was rolled back each second attempt).
            // Losing a scenario the best passes is worse unless more pass.
            bool worse = attemptPassed < best.Passed || (attemptPassed == best.Passed
                         && (lost.Count > 0 || attemptDiffs > best.Diffs || (attemptDiffs == best.Diffs && attemptCalls > bestCalls)));
            if (best.Attempt == attempt && best.Files is not null) { bestFeedback = message; worseStreak = 0; }
            else if (best.Files is not null && worse) worseStreak++;
            else worseStreak = 0;
            if (attempt == attempts) break;
            if (worseStreak >= 2 && best.Files is not null && bestFeedback is not null)
            {
                // Two attempts in a row did worse than the best: each builds on the one before, so the run drifts (FNLS0100 went
                // from 2 differences to 7 and stayed there). Back to the best code and the feedback it got.
                var from = best.Attempt == 0 ? "the code you started from" : $"your attempt {best.Attempt}";
                Program.Log($"  {worseStreak} attempts worse than the best ({from}): back to it");
                attemptJson["rolledBackTo"] = best.Attempt;
                files.Clear();
                foreach (var (n, src) in best.Files) files[n] = src;
                lastFeedback = $"Your last {worseStreak} attempts did worse than {from}. Here is that code again with its feedback; start from it.\n\n" +
                               bestFeedback + "\nFix the cause. " + Edits.Instructions;
                worseStreak = 0;
            }
            else lastFeedback = message + "\nFix the cause. " + Edits.Instructions;
            // Debug mode: with code that compiled, every next turn gets the focused prompt (contract, the COBOL slice the feedback
            // points at, record kit, current code, feedback) in place of the whole first prompt. A turn with the whole prompt
            // after two debug turns without a new best was tried and dropped: on Muse Glimmer's FNEM0300 both such turns rewrote
            // the program (53/71 to 5/71; a contract break), while the rollback to the best code already ends a drift.
            debugTurn = debugPrompt && best.Files is not null;
            if (debugTurn)
            {
                bool rolledBack = attemptJson["rolledBackTo"] is not null;
                var status = rolledBack || attemptPassed < 0 && best.Attempt != attempt
                    ? (attemptPassed < 0 && !rolledBack
                        ? $"Your current {cap.ProgramId} does not compile (the feedback has the errors); your best version passed {best.Passed} of {best.Total} acceptance scenarios."
                        : $"Your {cap.ProgramId} (your best version) compiles and {best.Passed} of {best.Total} acceptance scenarios pass.")
                    : $"Your {cap.ProgramId} compiles and {attemptPassed} of {best.Total} acceptance scenarios pass.";
                var guide = rolledBack ? bestGuide : attemptGuide ?? bestGuide;
                messages = DebugMessages(attempt + 1, status, guide, rolledBack ? bestPassing : passingNow ?? bestPassing);
            }
            else messages = [.. baseMessages, new("assistant", Render(files)), new("user", lastFeedback)];
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
                ["deliverable"] = best.Files is null ? "last attempt (none compiled)"
                    : best.Attempt == 0 ? "the warm-start code (no attempt did better)" : $"best attempt ({best.Attempt})",
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

    /// <param name="Compact">The packet is rendered under kit feature prompt-packet-compact (the outline is then part of the core prompt).</param>
    public sealed record PromptPlan(string Prompt, int MaxTokens, string? ExamplesPath, bool Compact, bool OutlineUsed, PromptPacket.Rendering? Packet);

    /// <summary>
    /// The implementation prompt and the reply budget. Pipeline v2 (no kit feature): the core prompt (packet, design, platform,
    /// kit, test, equivalences, contracts, notes) keeps a full reply, then whole example sections are added while they fit,
    /// then the AST outline if it still fits. Under prompt-packet-compact the packet's statements are given by step id, so the
    /// outline is part of the core and comes before the examples; when even that does not fit, the v2 rendering is used.
    /// </summary>
    public static PromptPlan PlanPrompt(Capability cap, string stepSimple, string packetPath, string designPath, string projectDir, string acceptanceTest,
        ISet<string> reserved, string? examplesPath, string? notesPath, string? outlinePath, string? kit, string? pattern, string? equivalences, string? callees,
        KitFeatures features, int context, int maxTokens, Func<string, int> tokens, string outDir, string? typedReference = null)
    {
        string? outline = outlinePath is not null && File.Exists(outlinePath) ? File.ReadAllText(outlinePath).Replace("\r\n", "\n") : null;
        // The typed reference (kit feature typed-reference) carries the logic in Java: it takes the outline's place.
        if (typedReference is not null) outline = null;
        if (features.Has(KitFeatures.PromptPacketCompact) && (outline is not null || typedReference is not null))
        {
            // Under the typed reference the packet names its record views (interface X   // RECORD-NAME, n bytes: ...).
            var views = typedReference is null ? null : Regex.Matches(typedReference, @"(?m)^interface (\w+)\s+// ([A-Z0-9][A-Z0-9-]*), \d+ bytes")
                .GroupBy(x => x.Groups[2].Value).ToDictionary(g => g.Key, g => g.First().Groups[1].Value, StringComparer.Ordinal);
            var packet = PromptPacket.Compact(packetPath, kit, views);
            var bareC = BuildPrompt(cap, stepSimple, packetPath, designPath, projectDir, acceptanceTest, reserved, null, notesPath, kit, pattern, equivalences, callees,
                packet, outline, typedReference);
            int coreC = tokens(SystemPrompt) + tokens(bareC);
            if (coreC + 16000 + 2000 <= context)
            {
                Program.Log($"  compact packet: {packet.StatementsByStepId} statement(s) by step id (their text is in the AST outline), " +
                            $"{packet.RecordsByReference.Count} record(s) by record kit class{(packet.RecordsByReference.Count > 0 ? ": " + string.Join(", ", packet.RecordsByReference) : "")}");
                int maxC = maxTokens;
                if (coreC + maxC + 2000 > context)
                {
                    maxC = context - coreC - 2000;
                    Program.Log($"  reply budget lowered from {maxTokens:N0} to {maxC:N0} tokens so the core prompt ({coreC:N0} tokens) fits");
                }
                var ex = examplesPath is not null && File.Exists(examplesPath)
                    ? FitExamples(examplesPath, context - maxC - 2000 - coreC, outDir, tokens, features.Has(KitFeatures.ExamplesValues)) : null;
                var promptC = BuildPrompt(cap, stepSimple, packetPath, designPath, projectDir, acceptanceTest, reserved, ex, notesPath, kit, pattern, equivalences, callees,
                    packet, outline, typedReference);
                return new PromptPlan(promptC, maxC, ex, true, outline is not null, packet);
            }
            Program.Log($"  compact packet with the outline is {coreC:N0} tokens: too large for a {context:N0}-token context; the full packet is used");
        }
        var bare = BuildPrompt(cap, stepSimple, packetPath, designPath, projectDir, acceptanceTest, reserved, null, notesPath, kit, pattern,
            equivalences, callees, null, null, typedReference);
        int coreTokens = tokens(SystemPrompt) + tokens(bare);
        if (coreTokens + maxTokens + 2000 > context)
        {
            int room = context - coreTokens - 2000;
            if (room < 16000)
                throw new PromptTooLargeException($"the core prompt is {coreTokens:N0} tokens; a {context:N0}-token context leaves {room:N0} for the reply");
            Program.Log($"  reply budget lowered from {maxTokens:N0} to {room:N0} tokens so the core prompt ({coreTokens:N0} tokens) fits");
            maxTokens = room;
        }
        int budget = context - maxTokens - 2000;
        if (examplesPath is not null && File.Exists(examplesPath))
            examplesPath = FitExamples(examplesPath, budget - coreTokens, outDir, tokens, features.Has(KitFeatures.ExamplesValues));
        var prompt = BuildPrompt(cap, stepSimple, packetPath, designPath, projectDir, acceptanceTest, reserved, examplesPath, notesPath, kit, pattern,
            equivalences, callees, null, null, typedReference);
        // The AST outline goes in when the prompt, the outline and a full reply still fit the context.
        bool outlineUsed = false;
        if (outline is not null)
        {
            if (tokens(SystemPrompt) + tokens(prompt) + tokens(outline) + 40 <= budget)
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
        return new PromptPlan(prompt, maxTokens, examplesPath, false, outlineUsed, null);
    }

    /// <summary>
    /// The examples file itself when it fits <paramref name="budget"/> characters; otherwise a copy (examples.fitted.md in
    /// <paramref name="outDir"/>) with the leading text and as many whole "## " sections as fit, and a note of how many
    /// were left out. With no room at all the examples are left out (null).
    /// </summary>
    static string? FitExamples(string examplesPath, int budget, string outDir, Func<string, int> tokens, bool byCall = false)
    {
        var text = File.ReadAllText(examplesPath).Replace("\r\n", "\n");
        if (tokens(text) <= budget) return examplesPath;
        var parts = Regex.Split(text, @"(?m)^(?=## )");
        var sb = new StringBuilder(parts[0]);
        int used = tokens(parts[0]) + 60, kept = 0;
        for (int i = 1; i < parts.Length; i++, kept++)
        {
            int t = tokens(parts[i]);
            if (used + t > budget)
            {
                // Examples in values (kit feature examples-values) are cut by CALL inside a scenario: its heading, then whole CALLs.
                if (byCall)
                {
                    var calls = Regex.Split(parts[i], @"(?m)^(?=### )");
                    int part = tokens(calls[0]) + 30, n = 0;
                    var text2 = new StringBuilder(calls[0]);
                    for (int k = 1; k < calls.Length; k++)
                    {
                        int tk = tokens(calls[k]);
                        if (used + part + tk > budget) break;
                        text2.Append(calls[k]);
                        part += tk;
                        n++;
                    }
                    if (n > 0)
                    {
                        sb.Append(text2).Append($"({calls.Length - 1 - n} further CALL(s) of this scenario left out to fit the context.)\n\n");
                        used += part;
                        kept++;
                    }
                }
                break;
            }
            sb.Append(parts[i]);
            used += t;
        }
        if (used > budget || (kept == 0 && parts.Length > 1))
        {
            Program.Log($"  worked examples left out: no room in the context ({tokens(text):N0} tokens, budget {Math.Max(0, budget):N0})");
            return null;
        }
        sb.Append($"\n({parts.Length - 1 - kept} further example section(s) left out to fit the context.)\n");
        var fitted = Path.Combine(outDir, "examples.fitted.md");
        File.WriteAllText(fitted, sb.ToString());
        Program.Log($"  worked examples cut to fit the context: {kept} of {parts.Length - 1} section(s), {sb.Length:N0} of {text.Length:N0} characters");
        return fitted;
    }

    static string BuildPrompt(Capability cap, string stepSimple, string packetPath, string designPath, string projectDir, string acceptanceTest, ISet<string> reserved,
        string? examplesPath, string? notesPath, string? kit, string? pattern, string? equivalences = null, string? callees = null,
        PromptPacket.Rendering? packet = null, string? outline = null, string? typedReference = null)
    {
        var main = Path.Combine(projectDir, "src", "main", "java");
        var platform = Directory.EnumerateFiles(Path.Combine(main, Path.Combine(cap.PlatformPackage.Split('.'))), "*.java", SearchOption.AllDirectories)
            .Append(Path.Combine(main, "com", "mlcpu", "rt", "ModernStep.java"))
            .Concat(cap.Kind == "subprogram" ? [Path.Combine(main, "com", "mlcpu", "rt", "ModernProgram.java")] : [])
            .OrderBy(f => f, StringComparer.Ordinal);
        var sb = new StringBuilder();
        if (packet is null)
        {
            sb.AppendLine("# Requirements packet (mlcpu.requirements-packet.v1, extracted from the COBOL source)");
            sb.AppendLine(JsonNode.Parse(File.ReadAllText(packetPath))!.ToJsonString());
        }
        else
        {
            sb.AppendLine("# Requirements packet (mlcpu.requirements-packet.v1, extracted from the COBOL source)");
            sb.AppendLine(typedReference is null
                ? "Statements are given by step id (paragraph:n:VERB:Lline); their text is in the COBOL-85 AST outline below, by the same line numbers. " +
                  "Records that the generated record kit lists are named with their kit class, not repeated field by field."
                : "Statements are given by step id (paragraph:n:VERB:Lline); their logic is in the typed reference implementation below, one method per " +
                  "paragraph (the method's comment names it). Records that the generated record kit lists are named with their kit class, not repeated field by field.");
            sb.AppendLine(packet.Json);
        }
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
        // Under the typed reference its record views (over the kit) carry the records: the kit summary would repeat them.
        if (kit is not null && typedReference is null)
        {
            sb.AppendLine(kit.TrimEnd());
            sb.AppendLine();
        }
        sb.AppendLine("# Acceptance test (JUnit 5). It runs your step and the faithful COBOL translation on the same inputs and requires identical datasets, SYSOUT and return code");
        sb.AppendLine("```java");
        sb.AppendLine(acceptanceTest.Replace("\r\n", "\n").TrimEnd());
        sb.AppendLine("```");
        sb.AppendLine();
        if (equivalences is not null)
        {
            sb.AppendLine(equivalences.TrimEnd());
            sb.AppendLine();
        }
        if (callees is not null)
        {
            sb.AppendLine(callees.TrimEnd());
            sb.AppendLine();
        }
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
        if (outline is not null)
        {
            sb.AppendLine("# COBOL-85 AST outline of the program (the original logic, statement by statement)");
            sb.AppendLine("```");
            sb.AppendLine(outline.TrimEnd());
            sb.AppendLine("```");
        }
        if (typedReference is not null)
        {
            sb.AppendLine(typedReference.TrimEnd());
            sb.AppendLine();
        }
        var callPort = cap.PlatformPackage == "com.mlcpu.carddemo.platform" ? "" : """

              A `CALL 'PGM' USING a b ...` to another program of the job is `ctx.call("PGM", a, b, ...)` with byte arrays laid out like the
              arguments; the called program reads and writes them BY REFERENCE (reimplementing its logic in your own classes is fine too).
            """.TrimEnd();
        // Kit feature subprogram-calls: a subprogram CALLs its callees through the runtime (ModernProgram.callProgram) instead of
        // re-implementing them. Only when the kit's runtime sources have the port, so no prompt names a method the kit lacks.
        var called = cap.Kind == "subprogram" && KitFeatures.Of(packetPath).Has(KitFeatures.SubprogramCalls) && HasCallPort(projectDir)
            ? CalledPrograms(packetPath) : [];
        var subprogramCallPort = called.Count == 0 ? "" : $"""

              A `CALL 'PGM' USING a b ...` to another program of the job is `ModernProgram.callProgram("PGM", a.bytes(), b.bytes(), ...)` over the
              record kit's areas (BY REFERENCE: what the called program writes into them is what you see when it returns; its abnormal end
              ends yours). {cap.ProgramId} CALLs {string.Join(", ", called)}. CALL them for what they do to the arguments instead of re-deriving
              their logic, even where the design plans to; the CALLs this prompt gives as verified library equivalences or verified contracts
              are the exception: compute those yourself.
            """.TrimEnd();
        // A text getter returns the whole field, trailing spaces included, while the examples and feedback print text without them:
        // FNLS0100 compared "DAYS".equals(rq.rqFreqType()) on a PIC X(10) and never rejected a 366-day repayment interval. Said for
        // the kits after pipeline v2 (a kit without kit-features.json renders as before).
        var textGetters = KitFeatures.Of(packetPath).Has(KitFeatures.ExamplesValues) ? """

              A text getter returns the whole field, trailing spaces included (`PIC X(10)` holding DAYS is "DAYS      "), as COBOL compares it
              with a shorter literal padded by spaces: compare with the trailing spaces removed (`stripTrailing()`), not `trim()` (a leading
              space counts: ' FLAT' is not 'FLAT'), or use the 88-level testers.
            """.TrimEnd() : "";
        var kitDep = kit is null ? "" : $", the generated record kit `{KitPackage(cap)}.*`";
        var kitUse = kit is null ? "" : typedReference is not null ? """

            - Records: use the typed reference's record views (over the generated record kit's bytes) for every record you receive or pass to
              a CALL; do not write your own offsets or field codecs.
            """.TrimEnd() : $"""

            - Records and report lines: use the generated record kit (`{KitPackage(cap)}`) for every record you read, write, rewrite or pass
              to a CALL and for every report or message line. Where the design names record layouts, codecs or line formatters, the kit's
              classes are those: do not write your own offsets, field codecs or edited-number formatting.{textGetters}
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
              services and the entry point. Do not reference any other com.mlcpu class.{subprogramCallPort}{kitUse}
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
        var eqRule = equivalences is null ? "" : """

            - Verified library equivalences: implement each group of CALLs listed under "# Verified library equivalences" with the JDK
              operation named there (BigDecimal with MathContext and RoundingMode, LocalDate, ChronoUnit) and its field mapping, wherever
              its stated condition holds; do not re-derive the COBOL digit or calendar algorithm for it. Groups not listed there, and the
              stated exceptions, follow the COBOL logic.
            """.TrimEnd();
        if (callees is not null)
            eqRule += """

            - Programs it CALLs: where you compute a CALLed program's result yourself instead of calling it, produce every effect its
              contract under "# Contracts of the programs ... CALLs (verified)" lists for that CALL: the result fields, the return and
              status fields, and the fields it copies from the caller's arguments (such as a reject site from the value the caller set
              before that CALL), on the error paths as well as the normal one.
            """.TrimEnd();
        if (typedReference is not null)
            eqRule += """

            - Typed reference: start from the typed reference implementation above. Its views, operations and Storage are in the package
              (use them); its logic and entry class are your starting files. Keep its behaviour while you bring the code to the design.
            """.TrimEnd();
        var output = typedReference is null
            ? "Output: each file as its own fenced block that starts with ```java and whose first line is the package declaration; one top-level\ntype per file. Return all files now."
            : "Output: whole files, each as its own fenced block that starts with ```java and whose first line is the package declaration (one top-level\n" +
              "type per file), and/or SEARCH/REPLACE edits of your starting files. A file you do not return stays as it is. Answer now.\n\n" + Edits.Instructions;
        sb.AppendLine($"""
            # Task
            Implement the design above for program {cap.ProgramId} as Java 21 source files.

            Contract:
            - Every file is in package `{cap.Package}`.
            {entry.TrimEnd()}
            - Behaviour: reproduce the COBOL program exactly in every acceptance scenario: each output dataset byte for byte (unassigned
              bytes, FILLER, signs and zones included), each console line, the return code, and the error and abend paths.{eqRule}
            - Do not use these type names (they already exist in the package): {string.Join(", ", reserved.Order(StringComparer.Ordinal))}.

            {output}
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

    static List<TestCase> TestCases(string resultsDir, string? package = null)
    {
        var list = new List<TestCase>();
        if (!Directory.Exists(resultsDir)) return list;
        foreach (var f in Directory.EnumerateFiles(resultsDir, "TEST-*.xml"))
            foreach (var tc in XDocument.Load(f).Descendants("testcase"))
            {
                var fail = tc.Element("failure") ?? tc.Element("error");
                string? text = fail is null ? null : (fail.Attribute("message")?.Value is { Length: > 0 } msg ? msg : Clip(fail.Value, 1500));
                // An exception (not an output difference) gets where it was thrown in the model's own code: kit A/B/C's FNLS0200
                // attempt 2 failed 69 scenarios with "ArithmeticException: Invalid operation" and nothing said which line.
                if (fail is not null && package is not null && ThrownAt(fail.Value, package) is { } at) text += "\n  " + at;
                list.Add(new TestCase(tc.Attribute("name")!.Value, text));
            }
        return list;
    }

    /// <summary>
    /// "thrown at Class.method(File.java:n), called from …": up to three frames of the program's own package from a test's stack
    /// trace (not the faithful <c>legacy</c> classes or the tests); null for an assertion failure or when none is the model's.
    /// </summary>
    internal static string? ThrownAt(string stackTrace, string package)
    {
        if (stackTrace.Contains("AssertionFailedError", StringComparison.Ordinal) || stackTrace.Contains("java.lang.AssertionError", StringComparison.Ordinal)) return null;
        var all = Regex.Matches(stackTrace, @"^\s*at (?:[\w.]+/)?([\w.$]+)\.([\w$<>]+)\(([^)]*)\)", RegexOptions.Multiline)
            .Select(m => (Cls: m.Groups[1].Value, Method: m.Groups[2].Value, Where: m.Groups[3].Value)).ToList();
        var frames = all
            .Where(f => f.Cls.StartsWith(package + ".", StringComparison.Ordinal) && !f.Cls.Contains(".legacy.", StringComparison.Ordinal)
                        && !f.Cls.EndsWith("EquivalenceTest", StringComparison.Ordinal) && !f.Cls.Contains(Probe.ProbeClass, StringComparison.Ordinal))
            .Select(f => $"{f.Cls[(package.Length + 1)..]}.{f.Method}({f.Where})")
            .Distinct().Take(3).ToList();
        if (frames.Count == 0) return null;
        // The JDK call that threw, when the innermost frame is one (FNLS0200: BigDecimal.pow with a negative exponent).
        var jdk = all.Count > 0 && all[0].Cls.StartsWith("java.", StringComparison.Ordinal) ? $"by {all[0].Cls[(all[0].Cls.LastIndexOf('.') + 1)..]}.{all[0].Method} " : "";
        return $"thrown {jdk}at " + frames[0] + (frames.Count > 1 ? ", called from " + string.Join(", ", frames.Skip(1)) : "");
    }

    /// <summary>
    /// The programs a program CALLs, without the abend services (CEE3ABD, ILBOABN0): its packet's CALL statements and its AST
    /// outline's (the packet leaves out statements nested in an IF of a control segment: FNLS0100's CALL 'FNLS0200' at L246).
    /// </summary>
    internal static List<string> CalledPrograms(string packetPath)
    {
        var outline = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packetPath))!, "ast-outline.txt");
        return Regex.Matches(File.ReadAllText(packetPath), "\"cobol\":\\s*\"CALL '([A-Z0-9#@$-]+)'")
            .Concat(File.Exists(outline) ? Regex.Matches(File.ReadAllText(outline), @"\bCALL '([A-Z0-9#@$-]+)'") : Enumerable.Empty<Match>())
            .Select(m => m.Groups[1].Value).Where(p => p is not ("CEE3ABD" or "ILBOABN0")).Distinct().ToList();
    }

    /// <summary>The step-CALL comparison the last probe run wrote (build/mlcpu-stepcalls.json), or null.</summary>
    static IReadOnlyList<StepCallsScenario>? ReadStepCalls(string harnessDir)
    {
        var f = Path.Combine(harnessDir, "build", "mlcpu-stepcalls.json");
        if (!File.Exists(f)) return null;
        try { return Probe.ParseStepCalls(File.ReadAllText(f)); }
        catch (Exception e) { Program.Log("  step CALLs unreadable: " + e.Message); return null; }
    }

    /// <summary>
    /// Shadow feedback looks inside each differing CALL at the CALLs it makes (kit feature subprogram-calls): a model that CALLs
    /// its callees can then be shown which of its CALLs first passes or gets something different. FNEM0300 (round 3b) read
    /// FNMY0100's result from the wrong field and saw only that its rate factors were 0.
    /// </summary>
    static bool NestedShadow(KitFeatures features, string projectDir) => features.Has(KitFeatures.SubprogramCalls) && HasCallPort(projectDir);

    /// <summary>Whether the project's runtime sources have the subprogram CALL port (ModernProgram.callProgram).</summary>
    static bool HasCallPort(string projectDir)
    {
        var f = Path.Combine(projectDir, "src", "main", "java", "com", "mlcpu", "rt", "ModernProgram.java");
        return File.Exists(f) && File.ReadAllText(f).Contains("callProgram(", StringComparison.Ordinal);
    }

    /// <summary>
    /// The failing scenarios, up to 12 lines; scenarios that fail identically (an exception thrown in every scenario) share one
    /// line with their count instead of repeating it.
    /// </summary>
    static string FailureLines(IReadOnlyList<TestCase> failing, int max = 12) =>
        string.Concat(failing.GroupBy(f => Clip(f.Failure!, 400)).Take(max).Select(g =>
            $"- {g.First().Name}{(g.Count() > 1 ? $" and {g.Count() - 1} more scenario(s) the same" : "")}: {g.Key}\n"));

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
    /// The scenarios the shadow run takes, in the acceptance test's own names: the failing scenarios (a failing "GnuCOBOL oracle: X"
    /// test stands for the scenario "oracle scenario X"), oracle scenarios first, at most six.
    /// </summary>
    static List<string> ShadowScenarios(IEnumerable<string> failing) =>
        failing.Where(n => !n.EndsWith("()"))
            .Select(n => n.StartsWith("GnuCOBOL oracle: ", StringComparison.Ordinal) ? "oracle scenario " + n["GnuCOBOL oracle: ".Length..] : n)
            .Distinct().OrderBy(n => n.StartsWith("oracle scenario", StringComparison.Ordinal) ? 0 : 1).Take(6).ToList();

    /// <summary>
    /// Replays a recorded run's replies 1..attempt (each applied as the run applied it) in a fresh harness of the given project,
    /// compiles and tests the result, and returns the feedback this pipeline gives for it (diagnostics: the replay verb).
    /// </summary>
    public static string Replay(Capability cap, string packetPath, string projectDir, string harnessDir, IReadOnlyList<string> replies, TimeSpan testTimeout)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var reply in replies)
        {
            var (edits, rest) = Edits.Extract(reply);
            var returned = Parse(rest, problems);
            if (edits.Count > 0) Edits.Apply(files, edits, [], returned.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var (name, src) in returned) files[name] = src;
        }
        return FeedbackFor(cap, packetPath, projectDir, harnessDir, files, testTimeout, problems);
    }

    /// <summary>
    /// What <see cref="FeedbackFor"/> found: the acceptance result, the differing CALLs (int.MaxValue: no shadow run), the
    /// scenarios that pass and the COBOL paragraphs the feedback points at.
    /// </summary>
    public sealed record FeedbackResult(int Passed, int Total, int Diffs, int Calls, HashSet<string> Passing, IReadOnlyList<string> Paragraphs);

    /// <summary>
    /// The feedback this pipeline gives for the given capability classes (name → source), compiled and tested in a fresh
    /// harness of the project: the shadow verb (a model's delivered java folder) and replay. Under kit feature
    /// shadow-feedback the CALL-level comparison comes first, as in an implementation run.
    /// </summary>
    public static string FeedbackFor(Capability cap, string packetPath, string projectDir, string harnessDir, IDictionary<string, string> files,
        TimeSpan testTimeout, List<string>? problems = null, Action<FeedbackResult>? results = null)
    {
        problems ??= [];
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
        var features = KitFeatures.Of(packetPath);
        var shadow = features.Has(KitFeatures.ShadowFeedback) && cap.Kind == "subprogram"
            ? new ShadowFeedback(cap.ProgramId, File.Exists(layoutsPath) ? layoutsPath : null, packetPath, NestedShadow(features, projectDir)) : null;
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
        var cases = TestCases(resultsDir, cap.Package);
        var failing = cases.Where(c => c.Failure is not null).ToList();
        int passedN = cases.Count - failing.Count, diffsN = failing.Sum(f => Differences(f.Failure!)), callsN = int.MaxValue;
        var sb = new StringBuilder($"{(added.Count > 0 ? $"imports added: {string.Join("; ", added)}\n" : "")}acceptance {cases.Count - failing.Count}/{cases.Count}\n");
        sb.Append(FailureLines(failing));
        var chosen = ProbeScenarios(failing.Select(f => (f.Name, f.Failure!)));
        var shadowWanted = shadow is null ? null : ShadowScenarios(failing.Select(f => f.Name));
        string? lead = null;
        IReadOnlyList<string> guide = [];
        if (!test.TimedOut && (chosen.Count > 0 || shadowWanted is { Count: > 0 }))
        {
            var stepCalls = cap.Kind != "subprogram" && NestedShadow(features, projectDir)
                ? new ShadowFeedback(cap.ProgramId, File.Exists(layoutsPath) ? layoutsPath : null, packetPath, nested: true) : null;
            if (shadow is not null) shadow.Statements = feedback.StatementsAt;
            if (stepCalls is not null) stepCalls.Statements = feedback.StatementsAt;
            var (probed, shadowed) = RunProbe(harnessDir, testFile, cap, chosen, shadowWanted, TimeSpan.FromMinutes(10), shadow?.Callees, stepCalls?.Callees);
            if (probed is { Count: > 0 }) sb.Append($"\nDetail for {chosen.Count} failing scenario(s) with different symptoms:\n").Append(feedback.Render(probed)).Append('\n');
            if (shadowed is { Count: > 0 }) callsN = shadowed.Sum(s => s.Differing);
            if (shadow is not null && shadowed is { Count: > 0 }) { lead = shadow.Render(shadowed); guide = shadow.Paragraphs; }
            else if (stepCalls is not null && ReadStepCalls(harnessDir) is { Count: > 0 } sc) { lead = stepCalls.RenderStepCalls(sc); guide = stepCalls.Paragraphs; }
        }
        results?.Invoke(new FeedbackResult(passedN, cases.Count, diffsN, callsN,
            cases.Where(c => c.Failure is null).Select(c => c.Name).ToHashSet(StringComparer.Ordinal), guide));
        return lead is null ? sb.ToString() : lead + "\n" + sb;
    }

    /// <summary>Runs the difference probe in an existing harness for the given scenarios (diagnostics: the probe verb).</summary>
    public static string? ProbeOnly(string harnessDir, string programId, string manifestPath, string scenario)
    {
        var cap = FromManifest(manifestPath, programId);
        var testDir = Path.Combine(harnessDir, "src", "test", "java", cap.Package.Replace('.', Path.DirectorySeparatorChar));
        var testFile = Directory.EnumerateFiles(testDir, "*EquivalenceTest.java").Single();
        var (probed, _) = RunProbe(harnessDir, testFile, cap, scenario.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(), null, TimeSpan.FromMinutes(10));
        if (probed is null) return null;
        var norm = Path.Combine(harnessDir, "normalization", programId);
        var layouts = Path.Combine(norm, "record-layouts.json");
        return new FeedbackRenderer(File.Exists(layouts) ? layouts : null, Path.Combine(norm, "requirements.json"), Path.Combine(harnessDir, "src", "main", "java")).Render(probed);
    }

    /// <summary>
    /// Runs the probe class in the harness for the scenarios (null when it cannot run) and, when <paramref name="shadow"/> names
    /// scenarios, the CALL-level shadow run in the same Gradle invocation (null when there is none).
    /// </summary>
    static (IReadOnlyList<ProbeScenario>? Probe, IReadOnlyList<ShadowScenario>? Shadow) RunProbe(string harnessDir, string testFile, Capability cap,
        List<string> scenarios, List<string>? shadow, TimeSpan timeout, IReadOnlyList<string>? callees = null, IReadOnlyList<string>? stepCallees = null)
    {
        var testClass = cap.Package + "." + Path.GetFileNameWithoutExtension(testFile);
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(testFile)!, Probe.ProbeClass + ".java"), Probe.Source(cap.Package, testClass, cap.Kind, cap.ProgramId));
        Directory.CreateDirectory(Path.Combine(harnessDir, "build"));
        File.WriteAllText(Path.Combine(harnessDir, "build", "mlcpu-probe-request.txt"), string.Join("\n", scenarios) + "\n");
        var shadowRequest = Path.Combine(harnessDir, "build", "mlcpu-shadow-request.txt");
        var shadowResult = Path.Combine(harnessDir, "build", "mlcpu-shadow.json");
        if (File.Exists(shadowResult)) File.Delete(shadowResult);
        if (shadow is { Count: > 0 }) File.WriteAllText(shadowRequest, string.Join("\n", shadow) + "\n");
        else if (File.Exists(shadowRequest)) File.Delete(shadowRequest);
        // The programs it CALLs: the shadow run logs the CALLs each shadowed CALL makes to them (kit feature subprogram-calls).
        var calleeFile = Path.Combine(harnessDir, "build", "mlcpu-shadow-callees.txt");
        if (shadow is { Count: > 0 } && callees is { Count: > 0 }) File.WriteAllText(calleeFile, string.Join("\n", callees) + "\n");
        else if (File.Exists(calleeFile)) File.Delete(calleeFile);
        // A step's CALLs, faithful step against yours, in the first probed scenario (kit feature subprogram-calls).
        var stepRequest = Path.Combine(harnessDir, "build", "mlcpu-stepcalls-request.txt");
        var stepResult = Path.Combine(harnessDir, "build", "mlcpu-stepcalls.json");
        if (File.Exists(stepResult)) File.Delete(stepResult);
        if (stepCallees is { Count: > 0 } && scenarios.Count > 0)
        {
            File.WriteAllText(stepRequest, string.Join("\n", scenarios) + "\n");
            File.WriteAllText(calleeFile, string.Join("\n", stepCallees) + "\n");
        }
        else if (File.Exists(stepRequest)) File.Delete(stepRequest);
        var result = Path.Combine(harnessDir, "build", "mlcpu-probe.json");
        if (File.Exists(result)) File.Delete(result);
        // The probe keeps the arguments of up to 400 CALLs before and after each (FNEM0800's and FNEM0700's LM-MODEL is 262 KB):
        // in Gradle's default 512 MB test heap it died with "Java heap space" and the probe never ran for those programs
        // (kit A/B/C, 2026-10-12). Only the probe's run gets a larger heap; the acceptance runs keep the project's settings.
        var heap = Path.Combine(harnessDir, "build", "mlcpu-probe-heap.gradle");
        File.WriteAllText(heap, "allprojects { tasks.withType(Test).configureEach { maxHeapSize = \"8g\" } }\n");
        var run = Gradle(harnessDir, $"test --tests {cap.Package}.{Probe.ProbeClass} --init-script \"{heap}\"", timeout);
        IReadOnlyList<ShadowScenario>? shadowed = null;
        if (shadow is { Count: > 0 })
        {
            if (!File.Exists(shadowResult)) Program.Log("  shadow run did not run: " + Clip(Tail(run.Output, 6).Replace('\n', ' '), 300));
            else
                try { shadowed = Probe.ParseShadow(File.ReadAllText(shadowResult)); }
                catch (Exception e) { Program.Log("  shadow output unreadable: " + e.Message); }
        }
        if (!File.Exists(result))
        {
            if (scenarios.Count > 0) Program.Log("  probe did not run: " + Clip(Tail(run.Output, 6).Replace('\n', ' '), 300));
            return (null, shadowed);
        }
        try { return (Probe.Parse(File.ReadAllText(result)), shadowed); }
        catch (Exception e) { Program.Log("  probe output unreadable: " + e.Message); return (null, shadowed); }
    }

    static string Tail(string s, int lines) => string.Join("\n", s.Split('\n').TakeLast(lines));
}
