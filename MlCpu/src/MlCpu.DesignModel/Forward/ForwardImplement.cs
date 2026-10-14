using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// Forward-engineering implementation (tools/eval/PROTOCOL-forward.md section 6): one design (an architecture and its four
/// slice designs) implemented slice by slice: accounts, customers, reporting, statements. Each slice is one conversation:
/// the model writes the slice's context module and the application classes. Every attempt is judged on every scenario of
/// every slice built so far. Repairs work as in pipeline v2: complete files or SEARCH/REPLACE edits, missing imports added by
/// the harness, compiler errors with the source lines. The model gets architecture failures and public scenario failures in
/// full, and the hidden scenarios as a count. A slice ends when everything passes, after three attempts in which every public
/// scenario passes without the hidden count improving (plateau), or when the attempts run out. Its best attempt carries on.
/// </summary>
public static class ForwardImplement
{
    public const string SystemPrompt = """
        You are a senior Java 21 engineer. You implement a service from its design: idiomatic, production-quality Java, small focused
        classes, immutable records for values, BigDecimal for money, no reflection, no frameworks. The code must satisfy the
        specification's rules exactly, including the numeric details. You answer with Java source files only, each in its own
        ```java fenced block (or, when repairing, with SEARCH/REPLACE edit blocks).
        """;

    static readonly Regex JavaBlock = new(@"```java[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);
    static readonly Regex TopType = new(@"^(?:public\s+)?(?:(?:final|abstract|sealed|non-sealed|static)\s+)*(?:class|record|interface|enum)\s+(\w+)", RegexOptions.Multiline);

    public sealed record SliceResult(string Slice, int Attempts, int BestAttempt, ForwardJudge.Verdict? Best, string EndedBy, double Minutes);

    public static int Run(IChatBackend chat, ForwardInputs inputs, string designDir, string outDir, string harnessDir, int attempts, string modelName,
        string reasoning, int maxTokens, int context, TimeSpan testTimeout, string arm, string designer, int plateau = 3, bool debugPrompt = false,
        string? resume = null, string? warmStart = null)
    {
        Directory.CreateDirectory(outDir);
        var architecture = JsonNode.Parse(File.ReadAllText(Path.Combine(designDir, "architecture.json")))!;
        var judge = new ForwardJudge(inputs, harnessDir);
        var accepted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var results = new List<SliceResult>();
        var runs = new JsonArray();
        var started = DateTime.Now;
        var built = new List<string>();
        var passed = new List<string>();
        foreach (var slice in inputs.Slices)
        {
            built.Add(slice.Name);
            var sliceDesign = Path.Combine(designDir, $"slice-{slice.Name}.json");
            var design = File.Exists(sliceDesign) ? JsonNode.Parse(File.ReadAllText(sliceDesign)) : null;
            if (resume is not null && Resume(inputs, resume, slice.Name, judge, built, testTimeout, accepted) is { } resumed)
            {
                WriteDeliverable(inputs, Path.Combine(outDir, slice.Name, "java"), accepted);
                results.Add(resumed);
                passed.Add(slice.Name);
                WriteSummary(outDir, modelName, arm, designer, results, null, started, runs);
                continue;
            }
            Program.Log($"===== {modelName}: arm {arm}, slice {slice.Name} ({built.Count}/{inputs.Slices.Count}), design by {designer}");
            var warmDir = warmStart is null ? null : Path.Combine(warmStart, slice.Name, "java");
            var warm = warmDir is not null && Directory.Exists(warmDir)
                ? Directory.EnumerateFiles(warmDir, "*.java", SearchOption.AllDirectories)
                    .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => ForwardInputs.Norm(File.ReadAllText(f)), StringComparer.Ordinal)
                : null;
            var r = Slice(chat, inputs, slice, architecture, design, accepted, judge, built, Path.Combine(outDir, slice.Name), attempts, reasoning, maxTokens,
                context, testTimeout, plateau, runs, debugPrompt, warm, warmDir, passed);
            results.Add(r);
            if (r.EndedBy == "passed") passed.Add(slice.Name);
            WriteSummary(outDir, modelName, arm, designer, results, null, started, runs);
        }
        // The final code, judged on every scenario.
        judge.Write(accepted);
        var final = judge.Judge(built, testTimeout);
        WriteDeliverable(inputs, Path.Combine(outDir, "java"), accepted);
        WriteSummary(outDir, modelName, arm, designer, results, final, started, runs);
        Program.Log($"arm {arm}: final {(final.Compiled ? $"architecture {final.ArchPassed}/{final.ArchTotal}, public {final.PublicPassed}/{final.PublicTotal}, hidden {final.HiddenPassed}/{final.HiddenTotal}" : "does not compile")}");
        return final.AllPass ? 0 : 1;
    }

    /// <summary>
    /// --resume: a slice that passed in an earlier run of the same design (its summary says so) takes that run's accepted code for
    /// it (&lt;run&gt;/&lt;slice&gt;/java: every module so far) instead of a model call, if the code still passes the slice now; null
    /// otherwise (the slice then runs as usual).
    /// </summary>
    static SliceResult? Resume(ForwardInputs inputs, string run, string slice, ForwardJudge judge, List<string> built, TimeSpan testTimeout,
        SortedDictionary<string, string> accepted)
    {
        var summary = Path.Combine(run, "summary.json");
        var java = Path.Combine(run, slice, "java");
        if (!File.Exists(summary) || !Directory.Exists(java)) return null;
        var entry = (JsonNode.Parse(File.ReadAllText(summary))!["slices"] as JsonArray ?? []).FirstOrDefault(s => s?["slice"]?.ToString() == slice);
        if (entry is null) return null;
        // The earlier run's code for this slice's module and the application; every other module as this run has it (that run's
        // copies of the other modules may be older or newer than this run's).
        var mine = new HashSet<string>(StringComparer.Ordinal) { inputs.Slices.First(s => s.Name == slice).Module, inputs.ApplicationModule };
        var earlier = Directory.EnumerateFiles(java, "*.java", SearchOption.AllDirectories)
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => ForwardInputs.Norm(File.ReadAllText(f)), StringComparer.Ordinal);
        var files = new SortedDictionary<string, string>(accepted.Where(f => !mine.Contains(inputs.ModuleOfSource(f.Value)))
            .ToDictionary(f => f.Key, f => f.Value), StringComparer.Ordinal);
        foreach (var (k, src) in earlier)
            if (mine.Contains(inputs.ModuleOfSource(src))) files[k] = src;
        var started = DateTime.Now;
        judge.Write(files);
        var v = judge.Judge(built, testTimeout);
        var own = v.Slices.TryGetValue(slice, out var c) ? c : new ForwardJudge.SliceCount(0, 0, 0, 0);
        bool ok = v.ArchGreen && own.PublicPassed == own.PublicTotal && own.HiddenPassed == own.HiddenTotal && own.PublicTotal + own.HiddenTotal > 0;
        Program.Log($"slice {slice}: {(ok ? "resumed from" : "not resumed (its code no longer passes) from")} {run}: {Describe(v)}");
        if (!ok)
        {
            judge.Write(accepted);
            return null;
        }
        accepted.Clear();
        foreach (var (k, src) in files) accepted[k] = src;
        return new SliceResult(slice, 0, 0, v, "resumed", Math.Round((DateTime.Now - started).TotalMinutes, 1));
    }

    static SliceResult Slice(IChatBackend chat, ForwardInputs inputs, ForwardInputs.Slice slice, JsonNode architecture, JsonNode? design,
        SortedDictionary<string, string> accepted, ForwardJudge judge, List<string> built, string outDir, int attempts, string reasoning, int maxTokens,
        int context, TimeSpan testTimeout, int plateau, JsonArray runs, bool debugPrompt, IReadOnlyDictionary<string, string>? warm = null,
        string? warmDir = null, IReadOnlyList<string>? passedBefore = null)
    {
        // The slice passes when the architecture is green and it and every slice that passed before it pass in full: a slice that
        // failed earlier (in this run) does not stop a later one from passing.
        var mustPass = (passedBefore ?? []).Append(slice.Name).ToList();
        bool Passes(ForwardJudge.Verdict v) => v.ArchGreen && !v.TimedOut && mustPass.All(n =>
            v.Slices.TryGetValue(n, out var c) && c.PublicPassed == c.PublicTotal && c.HiddenPassed == c.HiddenTotal && c.PublicTotal + c.HiddenTotal > 0);
        Directory.CreateDirectory(outDir);
        var started = DateTime.Now;
        var prompt = BuildPrompt(inputs, slice, architecture, design, accepted, built);
        if (prompt.Length / 3.6 + maxTokens > context) Program.Log($"  WARNING: the prompt (~{prompt.Length / 3.6:N0} tokens) and a {maxTokens}-token reply may not fit the context");
        File.WriteAllText(Path.Combine(outDir, "implementation.prompt.md"), prompt);
        inputs.CheckLeaks(prompt, $"implementation prompt of the slice {slice.Name}",
            [architecture.ToJsonString(Program.Json), design?.ToJsonString(Program.Json), .. accepted.Values.Select(v => v.TrimEnd())]);
        var system = inputs.Java(SystemPrompt);
        var baseMessages = new List<ChatMessage> { new("system", system), new("user", prompt) };
        var messages = new List<ChatMessage>(baseMessages);
        var files = new SortedDictionary<string, string>(accepted, StringComparer.Ordinal);
        var editable = new HashSet<string>(StringComparer.Ordinal) { slice.Module, inputs.ApplicationModule };
        string ModuleOf(string source) => inputs.ModuleOfSource(source);
        var instructions = Instructions(inputs);
        (int Attempt, ForwardJudge.Verdict? Verdict, SortedDictionary<string, string>? Files) best = (0, null, null);
        int stale = 0, bestHidden = -1, used = 0;
        string ended = "attempts";
        string lastFeedback = "";
        // Debug mode (--debug-prompt: the repair loop DebugPrompt proved on FNEM0300, ported): the feedback the best code got;
        // the attempts in a row that did worse than it (two lead back to it); the public scenarios it passes (the ratchet: an
        // attempt that loses one is told which, and counts as worse unless more pass); the repairs tried since it; and whether
        // the next turn is a debug turn, whose reply may use the context its smaller prompt leaves.
        string? bestFeedback = null;
        int worseStreak = 0;
        var bestPassing = new HashSet<string>(StringComparer.Ordinal);
        var tried = new List<string>();
        bool debugTurn = false;
        int debugReplyTokens = maxTokens;
        List<ChatMessage> DebugMessages(int forAttempt, string status, IEnumerable<string> passing)
        {
            var b = best.Verdict!;
            // the slice's own scenarios decide (as the plateau): earlier slices passing must not hold a failing slice to edits
            var own = b.Slices.TryGetValue(slice.Name, out var sc) ? sc : new ForwardJudge.SliceCount(0, 0, 0, 0);
            int total = own.PublicTotal + own.HiddenTotal;
            bool editsOnly = total > 0 && (own.PublicPassed + own.HiddenPassed) * 2 >= total;
            var failing = b.PublicFailures.Select(f => f.Scenario).Where(n => n.Split(' ', 3) is [_, var s, _] && s == slice.Name).Select(ScenarioId).ToList();
            var text = ForwardDebugPrompt.Build(inputs, slice, prompt, design, Render(inputs, files, editable), lastFeedback, status,
                passing.Select(ScenarioId).OrderBy(x => x, StringComparer.Ordinal).ToList(), editsOnly, tried, failing);
            File.WriteAllText(Path.Combine(outDir, $"debug{forAttempt}.prompt.md"), text);
            int size = (int)(text.Length / 3.6);
            debugReplyTokens = Math.Max(maxTokens, context - size - 1024);
            Program.Log($"  debug prompt for attempt {forAttempt}: ~{size:N0} tokens (the first prompt: ~{prompt.Length / 3.6:N0}; reply up to {debugReplyTokens:N0})" +
                        (editsOnly ? "; edits only" : ""));
            return [new("system", system), new("user", text)];
        }
        if (warm is not null)
        {
            // --warm-start: the slice continues from an earlier run's best code (every module, as that run left it): judged as
            // attempt 0, then repaired as usual; the attempts that follow are this run's.
            foreach (var (k, v) in warm) files[k] = v;
            AddImports(files, inputs);
            judge.Write(files);
            var v0 = judge.Judge(built, testTimeout);
            Program.Log($"slice {slice.Name}: warm start from {warmDir}: {Describe(v0)}");
            runs.Add(new JsonObject { ["slice"] = slice.Name, ["attempt"] = 0, ["warmStart"] = warmDir, ["verdict"] = VerdictJson(v0) });
            best = (0, v0, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
            var message0 = Feedback(inputs, v0, judge, files);
            bestFeedback = message0;
            bestPassing = v0.PublicPassing?.ToHashSet(StringComparer.Ordinal) ?? [];
            lastFeedback = message0 + "\nFix the causes. " + instructions;
            if (Passes(v0))
            {
                Program.Log($"slice {slice.Name}: passed as it was (warm start)");
                WriteDeliverable(inputs, Path.Combine(outDir, "java"), files);
                accepted.Clear();
                foreach (var (k, v) in files) accepted[k] = v;
                return new SliceResult(slice.Name, 0, 0, v0, "passed", Math.Round((DateTime.Now - started).TotalMinutes, 1));
            }
            debugTurn = debugPrompt && v0.Compiled;
            messages = debugTurn
                ? DebugMessages(1, $"Your code (from an earlier run) compiles; {v0.PublicPassed + v0.HiddenPassed} of {v0.PublicTotal + v0.HiddenTotal} scenarios pass " +
                                   $"(public {v0.PublicPassed}/{v0.PublicTotal}, hidden {v0.HiddenPassed}/{v0.HiddenTotal}).", bestPassing)
                : [.. baseMessages, new("assistant", Render(inputs, files, editable)), new("user", lastFeedback)];
        }
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            used = attempt;
            ChatResult r;
            try { r = chat.Complete(messages, debugTurn ? debugReplyTokens : maxTokens, reasoning); }
            catch (HttpRequestException e) when (e.Message.StartsWith("llama-server error:", StringComparison.Ordinal) && e.Message.Contains("\"code\":500"))
            {
                Program.Log($"slice {slice.Name} attempt {attempt}: the server could not read the reply; counted as a failed attempt");
                File.WriteAllText(Path.Combine(outDir, $"attempt{attempt}.txt"), "(no reply: " + e.Message + ")\n");
                runs.Add(new JsonObject { ["slice"] = slice.Name, ["attempt"] = attempt, ["replyError"] = ForwardJudge.Clip(e.Message, 500) });
                messages = [.. baseMessages, new("assistant", Render(inputs, files, editable)), new("user",
                    "Your last reply could not be read: the server rejected its format. Answer again, in the format asked for.\n\n" +
                    (lastFeedback.Length > 0 ? "The feedback you were answering:\n" + lastFeedback : instructions))];
                continue;
            }
            Program.Log($"slice {slice.Name} attempt {attempt}: prompt {r.PromptTokens} tok, generated {r.GeneratedTokens} tok @ {r.TokensPerSecond:N1} tok/s, {r.Seconds:N0}s, stop={r.Stop}");
            File.WriteAllText(Path.Combine(outDir, $"attempt{attempt}.txt"), r.Content);
            if (r.Reasoning is not null) File.WriteAllText(Path.Combine(outDir, $"attempt{attempt}.reasoning.txt"), r.Reasoning);

            var problems = new List<string>();
            var (edits, rest) = Edits.Extract(r.Content);
            var returned = Parse(rest, problems);
            var editProblems = new List<string>();
            var mine = new SortedDictionary<string, string>(files.Where(f => editable.Contains(ModuleOf(f.Value))).ToDictionary(f => f.Key, f => f.Value), StringComparer.Ordinal);
            int applied = edits.Count == 0 ? 0 : Edits.Apply(mine, edits, editProblems, returned.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var (k, v) in mine) files[k] = v;
            foreach (var (name, src) in returned)
            {
                var module = ModuleOf(src);
                if (!editable.Contains(module))
                {
                    problems.Add(module.Length == 0
                        ? $"{name}.java declares package {ForwardJudge.PackageOf(src) ?? "(none)"}, which is in no implementation module (RULES.md A-7)"
                        : $"{name}.java is in {module}, which this slice does not change (it may change {slice.Module} and {inputs.ApplicationModule})");
                    continue;
                }
                if (files.TryGetValue(name, out var existing) && ModuleOf(existing) != module && !editable.Contains(ModuleOf(existing)))
                {
                    problems.Add($"{name} is already a class of {ModuleOf(existing)}; simple class names must be unique across the service");
                    continue;
                }
                files[name] = src;
            }
            if (!files.ContainsKey(inputs.EntryClass) || ForwardJudge.PackageOf(files[inputs.EntryClass]) != inputs.ApplicationPackage)
                problems.Add($"the entry point {inputs.ApplicationPackage}.{inputs.EntryClass} ({inputs.EntrySignature}) is missing");
            var attemptJson = Program.AttemptJson(attempt, r, problems);
            attemptJson["slice"] = slice.Name;
            if (debugTurn) attemptJson["debugPrompt"] = true;
            attemptJson["filesReturned"] = Program.Strings(returned.Keys);
            attemptJson["edits"] = new JsonObject { ["blocks"] = edits.Count, ["applied"] = applied, ["notApplied"] = Program.Strings(editProblems) };
            var editNote = editProblems.Count == 0 ? "" : "Some of your edits were not applied (their files stay as they were):\n- " + string.Join("\n- ", editProblems) + "\n\n";

            string message;
            ForwardJudge.Verdict? verdict = null;
            if (problems.Count > 0 || returned.Count + applied == 0)
            {
                if (problems.Count == 0) problems.Add("no ```java code blocks or applicable edits were found in the reply");
                message = "The files break the contract:\n- " + string.Join("\n- ", problems) + "\n";
                Program.Log($"  contract: {problems.Count} problem(s); first: {ForwardJudge.Clip(problems[0], 200)}");
            }
            else
            {
                var importsAdded = AddImports(files, inputs);
                if (importsAdded.Count > 0) attemptJson["importsAdded"] = Program.Strings(importsAdded);
                judge.Write(files);
                verdict = judge.Judge(built, testTimeout);
                attemptJson["verdict"] = VerdictJson(verdict);
                Program.Log("  " + Describe(verdict));
                message = Feedback(inputs, verdict, judge, files);
                bool better = best.Verdict is null || Rank(verdict).CompareTo(Rank(best.Verdict)) > 0;
                if (better) best = (attempt, verdict, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
                if (Passes(verdict))
                {
                    runs.Add(attemptJson);
                    ended = "passed";
                    break;
                }
                // Plateau: every public scenario passes and the architecture is green, but the hidden count does not improve. The
                // slice's own scenarios decide (FE 8.4, a CAS lesson: a hidden failure carried over from an earlier slice must not
                // end this one).
                var own = verdict.Slices.TryGetValue(slice.Name, out var sc) ? sc : new ForwardJudge.SliceCount(0, 0, 0, 0);
                if (verdict.ArchGreen && own.PublicPassed == own.PublicTotal)
                {
                    if (own.HiddenPassed > bestHidden) { bestHidden = own.HiddenPassed; stale = 0; }
                    else if (++stale >= plateau)
                    {
                        runs.Add(attemptJson);
                        ended = "plateau";
                        Program.Log($"  plateau: every public scenario passes and the hidden count has not improved for {plateau} attempts");
                        break;
                    }
                }
                else stale = 0;
            }
            // The ratchet (debug mode): public scenarios the best code passes that this attempt fails are named.
            var lost = debugPrompt && verdict?.PublicPassing is { } now && best.Attempt != attempt ? bestPassing.Where(s => !now.Contains(s)).ToList() : [];
            if (lost.Count > 0)
                message += $"\nThese public scenarios passed in your best version and fail now (restore them): {string.Join(", ", lost.Take(20).Select(ScenarioId))}" +
                           $"{(lost.Count > 20 ? $" and {lost.Count - 20} more" : "")}.\n";
            runs.Add(attemptJson);
            if (best.Attempt == attempt)
            {
                bestFeedback = editNote + message;
                bestPassing = verdict?.PublicPassing?.ToHashSet(StringComparer.Ordinal) ?? [];
                tried.Clear();
                worseStreak = 0;
            }
            else if (best.Verdict is not null)
            {
                tried.Add($"attempt {attempt}: " + (verdict is null ? ForwardJudge.Clip(message.Split('\n')[0], 200)
                    : !verdict.Compiled ? $"it did not compile ({verdict.CompileErrors.Count} error(s))"
                    : Describe(verdict) + (lost.Count > 0 ? $"; {lost.Count} public scenario(s) that passed now fail" : "")));
                int cmp = verdict is null ? -1 : Rank(verdict).CompareTo(Rank(best.Verdict));
                worseStreak = cmp < 0 || (cmp == 0 && lost.Count > 0) ? worseStreak + 1 : 0;
            }
            if (attempt == attempts) break;
            bool rolledBack = false;
            if (debugPrompt && worseStreak >= 2 && best.Files is not null && bestFeedback is not null)
            {
                // Two attempts in a row did worse than the best: each builds on the one before, so the run drifts. Back to the
                // best code and the feedback it got.
                Program.Log($"  {worseStreak} attempts worse than the best (attempt {best.Attempt}): back to it");
                attemptJson["rolledBackTo"] = best.Attempt;
                files.Clear();
                foreach (var (k, v) in best.Files) files[k] = v;
                lastFeedback = $"Your last {worseStreak} attempts did worse than your attempt {best.Attempt}. Here is that code again with its feedback; start from it.\n\n" +
                               bestFeedback + "\nFix the causes. " + instructions;
                worseStreak = 0;
                rolledBack = true;
            }
            else lastFeedback = editNote + message + "\nFix the causes. " + instructions;
            // Debug mode: with code that compiled once, every next turn gets the focused prompt in place of the first prompt.
            debugTurn = debugPrompt && best.Verdict is { Compiled: true };
            if (debugTurn)
            {
                var b = best.Verdict!;
                string Count(ForwardJudge.Verdict v) => $"{v.PublicPassed + v.HiddenPassed} of {v.PublicTotal + v.HiddenTotal} scenarios pass " +
                                                        $"(public {v.PublicPassed}/{v.PublicTotal}, hidden {v.HiddenPassed}/{v.HiddenTotal})";
                var status = rolledBack ? $"Your code (your best version) compiles; {Count(b)}."
                    : verdict is not { Compiled: true } ? $"Your current code does not compile or breaks the contract (the feedback says why); your best version: {Count(b)}."
                    : $"Your code compiles; {Count(verdict)}.";
                var passing = rolledBack || verdict?.PublicPassing is null ? bestPassing : verdict.PublicPassing;
                messages = DebugMessages(attempt + 1, status, passing);
            }
            else messages = [.. baseMessages, new("assistant", Render(inputs, files, editable)), new("user", lastFeedback)];
        }
        var deliverable = best.Files ?? files;
        accepted.Clear();
        foreach (var (k, v) in deliverable) accepted[k] = v;
        WriteDeliverable(inputs, Path.Combine(outDir, "java"), deliverable);
        Program.Log($"slice {slice.Name}: {ended} after {used} attempt(s); best attempt {best.Attempt}: {(best.Verdict is null ? "nothing compiled" : Describe(best.Verdict))}");
        return new SliceResult(slice.Name, used, best.Attempt, best.Verdict, ended, Math.Round((DateTime.Now - started).TotalMinutes, 1));
    }

    /// <summary>
    /// fwd-judge: the files of a directory (the SME's reference, or a run's deliverable) judged exactly as an attempt is, on the
    /// slices of the package (--slices). Prints the verdict and the failures; exit 0 when everything passes.
    /// </summary>
    public static int JudgeOnly(ForwardInputs inputs, string javaDir, string harnessDir, TimeSpan testTimeout)
    {
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var f in Directory.EnumerateFiles(javaDir, "*.java", SearchOption.AllDirectories))
            files[Path.GetFileNameWithoutExtension(f)] = ForwardInputs.Norm(File.ReadAllText(f));
        var judge = new ForwardJudge(inputs, harnessDir);
        judge.Write(files);
        var v = judge.Judge(inputs.Slices.Select(s => s.Name).ToList(), testTimeout);
        Program.Log($"judged {files.Count} file(s) of {javaDir}: {Describe(v)}");
        foreach (var e in v.CompileErrors.Take(10)) Program.Log("  " + e.Split('\n')[0]);
        foreach (var (t, m) in v.ArchFailures) Program.Log($"  architecture {t}: {ForwardJudge.Clip(m, 300)}");
        foreach (var (s, m) in v.PublicFailures.Take(10)) Program.Log($"  {s}: {ForwardJudge.Clip(m, 300)}");
        return v.AllPass ? 0 : 1;
    }

    /// <summary>Best attempt first: compiles, then architecture green, then the most scenarios passed (public and hidden).</summary>
    static (int, int, int) Rank(ForwardJudge.Verdict v) => (v.Compiled ? 1 : 0, v.ArchGreen ? 1 : 0, v.PublicPassed + v.HiddenPassed);

    /// <summary>A scenario's id from its test name ("public calendar cal-0001").</summary>
    static string ScenarioId(string testName) => testName.Split(' ', 3)[^1];

    public static string Describe(ForwardJudge.Verdict v) => !v.Compiled ? $"compile FAILED ({v.CompileErrors.Count} errors)"
        : $"architecture {v.ArchPassed}/{v.ArchTotal}; public {v.PublicPassed}/{v.PublicTotal}; hidden {v.HiddenPassed}/{v.HiddenTotal}"
          + $" ({string.Join(", ", v.Slices.Select(s => $"{s.Key} {s.Value.PublicPassed + s.Value.HiddenPassed}/{s.Value.PublicTotal + s.Value.HiddenTotal}"))})"
          + (v.PrivilegedTotal > 0 ? $"; privileged {v.PrivilegedPassed}/{v.PrivilegedTotal}" : "") + (v.TimedOut ? " TIMED OUT" : "");

    public static JsonObject VerdictJson(ForwardJudge.Verdict v) => new()
    {
        ["compiled"] = v.Compiled, ["compileErrors"] = v.CompileErrors.Count, ["architecture"] = new JsonObject { ["passed"] = v.ArchPassed, ["total"] = v.ArchTotal },
        ["public"] = new JsonObject { ["passed"] = v.PublicPassed, ["total"] = v.PublicTotal },
        ["hidden"] = new JsonObject { ["passed"] = v.HiddenPassed, ["total"] = v.HiddenTotal },
        ["privileged"] = new JsonObject { ["passed"] = v.PrivilegedPassed, ["total"] = v.PrivilegedTotal },
        ["slices"] = new JsonObject(v.Slices.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)new JsonObject
        {
            ["publicPassed"] = s.Value.PublicPassed, ["publicTotal"] = s.Value.PublicTotal, ["hiddenPassed"] = s.Value.HiddenPassed, ["hiddenTotal"] = s.Value.HiddenTotal,
            ["privilegedPassed"] = s.Value.PrivilegedPassed, ["privilegedTotal"] = s.Value.PrivilegedTotal
        }))),
        ["architectureFailures"] = Program.Strings(v.ArchFailures.Select(a => a.Test)),
        ["publicFailures"] = Program.Strings(v.PublicFailures.Select(f => f.Scenario)),
        ["timedOut"] = v.TimedOut
    };

    // ------------------------------------------------------------------------------------------------ prompt

    public static string BuildPrompt(ForwardInputs inputs, ForwardInputs.Slice slice, JsonNode architecture, JsonNode? design,
        IReadOnlyDictionary<string, string> accepted, IReadOnlyList<string> built)
    {
        var sb = new StringBuilder();
        sb.Append(inputs.Spec.TrimEnd()).Append("\n\n# Design rules (RULES.md)\n").Append(inputs.Rules.TrimEnd()).Append("\n\n");
        sb.Append(inputs.ApiSection()).Append('\n');
        sb.Append(inputs.DigestSection(slice.Digests));
        sb.Append("# The design you implement\n## Architecture (mlcpu.service-design.v1)\n").Append(architecture.ToJsonString(Program.Json)).Append("\n\n");
        if (design is not null) sb.Append($"## Slice design: {slice.Name} (mlcpu.slice-design.v1)\n").Append(design.ToJsonString(Program.Json)).Append("\n\n");
        sb.Append(TestingSection(inputs, slice));
        if (accepted.Count > 0)
        {
            sb.Append("# Code already built (earlier slices; it must keep passing their scenarios)\n");
            foreach (var (name, src) in accepted.OrderBy(f => inputs.PathOf(f.Key, f.Value), StringComparer.Ordinal))
                sb.Append($"## {inputs.PathOf(name, src)}\n```java\n{src.TrimEnd()}\n```\n");
            sb.Append('\n');
        }
        sb.Append(PublicScenarios(inputs, slice));
        var later = inputs.Slices.SkipWhile(s => s.Name != slice.Name).Skip(1).SelectMany(s => s.Methods).ToList();
        var svc = inputs.ServiceInterface;
        string Packages(IEnumerable<string> modules) => string.Join(" and ", modules.Where(m => m != "@contexts")
            .Select(m => (m == inputs.ApiModule ? inputs.ApiPackage : inputs.Modules.First(x => x.Module == m).Package) + ".."));
        var appUses = Packages(inputs.ApplicationDependencies) + (inputs.ApplicationDependencies.Contains("@contexts") ? " and the context modules" : "");
        if (slice.Entry is { } entry)
        {
            // A slice that brings its own entry point (forward/loans: an SPI's implementation, reached through the entry class).
            var name = Regex.Match(entry, @"(\w+)\s*\(").Groups[1].Value;
            // Later slices that share this entry point (forward/loans: quotes() serves terms, schedule and disclosure).
            var sharing = inputs.AllSlices.SkipWhile(s => s.Name != slice.Name).Skip(1).Where(s => s.Entry == entry).Select(s => s.Name).ToList();
            var shared = sharing.Count == 0 ? ""
                : $"\n  The entry point is shared with the later slice(s) {string.Join(", ", sharing)}: until they are built, what only they do may" +
                  "\n  throw UnsupportedOperationException.";
            sb.Append($"""
                # Task
                Implement the slice "{slice.Name}" of the design: the classes of {slice.Module} (package {slice.Package} and below) and the
                application's entry point `{entry}` in {inputs.ApplicationPackage}.{inputs.EntryClass}{(slice.Implements is { } spi ? $", which returns your implementation of {spi}" : "")}.

                Contract:
                - Java {inputs.JavaLevel ?? 21}{(inputs.JavaLevel is { } level && level < 21 ? $": the build compiles with --release {level}, so no newer features (no pattern matching in switch, no record patterns, no List.getFirst or getLast)" : "")}.
                  Each file's package decides its module: {slice.Package}.. is {slice.Module}, {inputs.ApplicationPackage}.. is
                  {inputs.ApplicationModule}. This slice may add or change files of those two modules only.
                - Simple class names are unique across the service (one top-level type per file).
                - Dependencies (RULES.md): {slice.Module} uses only java.* (not java.io, java.nio, java.net) and {Packages(inputs.ContextDependencies)};
                  {inputs.ApplicationModule} uses java.*, {appUses}. No frameworks.
                - The entry point {inputs.ApplicationPackage}.{inputs.EntryClass}.{name}() has `{entry}`; every scenario of this slice reaches
                  your code through it. The entry points of later slices are added with their slices.{shared}
                - Behaviour: every rule of SPEC.md and of the digests, exactly; the scenarios check every value (BigDecimal scale included).

                Output: each file as its own fenced block that starts with ```java and whose first line is the package declaration.
                Return every file of this slice now.
                """);
            return sb.ToString();
        }
        sb.Append($"""
            # Task
            Implement the slice "{slice.Name}" of the design: the classes of {slice.Module} (package {slice.Package} and below) and the
            application classes ({inputs.ApplicationPackage}) for {svc}.{string.Join($", {svc}.", slice.Methods)}.

            Contract:
            - Java 21. Each file's package decides its module: {slice.Package}.. is {slice.Module}, {inputs.ApplicationPackage}.. is
              {inputs.ApplicationModule}. This slice may add or change files of those two modules only.
            - Simple class names are unique across the service (one top-level type per file).
            - Dependencies (RULES.md): {slice.Module} uses only java.* (not java.io, java.nio, java.net) and {Packages(inputs.ContextDependencies)};
              {inputs.ApplicationModule} uses java.*, {appUses}. No frameworks.
            - The entry point {inputs.ApplicationPackage}.{inputs.EntryClass} has `{inputs.EntrySignature}`;
              every scenario reaches the service through it.{(later.Count > 0 ? $"\n- The methods of later slices ({string.Join(", ", later)}) may throw\n  UnsupportedOperationException until their slice is built." : "")}
            - Behaviour: every rule of SPEC.md and of the digests, exactly; the scenarios check every value (BigDecimal scale included).

            Output: each file as its own fenced block that starts with ```java and whose first line is the package declaration.
            Return every file of this slice now.
            """);
        return sb.ToString();
    }

    /// <summary>How the service is tested (forward.json "testing") and, for a slice that uses them, the in-memory adapters' source.</summary>
    static string TestingSection(ForwardInputs inputs, ForwardInputs.Slice slice) =>
        inputs.InMemoryPorts() is { } ports && (slice.Ports || inputs.AllSlices.All(s => !s.Ports))
        ? $"{inputs.Testing}\n\n## The in-memory adapters ({inputs.AcceptanceModule}, test code)\n```java\n{ports.TrimEnd()}\n```\n\n"
        : $"{inputs.Testing}\n\n";

    static readonly Regex LegacyNote = new(@" \(the faithful program[^)]*\)$");

    /// <summary>The slice's public scenarios: the list, and the smallest few in full as examples.</summary>
    static string PublicScenarios(ForwardInputs inputs, ForwardInputs.Slice slice)
    {
        var pub = inputs.ScenariosOf(slice.Name).Where(s => s["visibility"]!.GetValue<string>() == "public").ToList();
        var sb = new StringBuilder($"# Public scenarios of the slice \"{slice.Name}\" ({pub.Count})\n");
        foreach (var s in pub)
            sb.Append($"- {s["id"]}: {LegacyNote.Replace(s["origin"]!.GetValue<string>(), "")} (rules {string.Join(", ", s["rules"]!.AsArray().Select(x => x!.GetValue<string>()))})\n");
        var examples = pub.Select(s => File.ReadAllText(inputs.ScenarioFile(slice.Name, "public", s["id"]!.GetValue<string>())))
            .Select(ForwardInputs.Norm).Where(t => t.Length <= 9000).OrderBy(t => t.Length).Take(3).ToList();
        if (examples.Count > 0)
        {
            sb.Append($"\nExamples in full ({inputs.ScenarioFormat}):\n");
            foreach (var e in examples) sb.Append("```json\n").Append(e.TrimEnd()).Append("\n```\n");
        }
        return sb.Append('\n').ToString();
    }

    public static string Instructions(ForwardInputs inputs) => $"""
        Return every file you change, complete, each in its own ```java block; files you do not return stay as they are.
        For a change of a few lines you may send an edit instead of the whole file:
        <<<<<<< SEARCH Name.java
        the lines to replace, copied exactly from the current file
        =======
        the lines that take their place
        >>>>>>> REPLACE
        The SEARCH lines are removed and the REPLACE lines are put in their place, so to add lines repeat a neighbouring line in
        both parts. The SEARCH lines must occur once in that file; include a few surrounding lines when a line is not unique.
        (Missing imports of {inputs.ApiModule} classes, of your own classes and of common java.* classes are added for you.)
        """;

    // ------------------------------------------------------------------------------------------------ feedback

    static readonly Regex ErrorHead = new(@"^(\S+?\.java):(\d+): error: (.*)$");
    static readonly Regex SyntaxError = new(@"expected$|illegal start of|not a statement|unclosed|reached end of file|class, interface, enum, or record expected|orphaned|without 'if'|without 'try'|'catch' without|'finally' without|illegal character|invalid method declaration");

    static string Feedback(ForwardInputs inputs, ForwardJudge.Verdict v, ForwardJudge judge, IReadOnlyDictionary<string, string> files)
    {
        var sb = new StringBuilder();
        if (!v.Compiled)
        {
            if (v.CompileErrors.Count == 0) return "The code does not compile:\n" + string.Join("\n", v.BuildOutput.Split('\n').TakeLast(40)) + "\n";
            sb.Append("The code does not compile:\n");
            foreach (var file in v.CompileErrors.GroupBy(e => ErrorHead.Match(e.Split('\n')[0]) is { Success: true } m ? m.Groups[1].Value : ""))
            {
                var list = file.ToList();
                var syntax = list.FirstOrDefault(e => ErrorHead.Match(e.Split('\n')[0]) is { Success: true } m && SyntaxError.IsMatch(m.Groups[3].Value));
                if (syntax is not null && file.Key.Length > 0)
                {
                    var m = ErrorHead.Match(syntax.Split('\n')[0]);
                    sb.Append($"{Show(inputs, file.Key)}:{m.Groups[2].Value}: error: {m.Groups[3].Value}\n").Append(judge.Excerpt(file.Key, int.Parse(m.Groups[2].Value), 4));
                    if (list.Count > 1) sb.Append($"({list.Count - 1} further error(s) in this file follow from this one.)\n");
                    continue;
                }
                foreach (var e in list.Take(10)) sb.Append(Show(inputs, e)).Append('\n');
                if (list.Count > 10) sb.Append($"(... {list.Count - 10} more error(s) in {Show(inputs, file.Key)})\n");
            }
            return sb.ToString();
        }
        sb.Append($"Result: {Describe(v)}.\n");
        if (v.ArchFailures.Count > 0)
        {
            sb.Append("\nArchitecture tests that fail (RULES.md):\n");
            foreach (var (test, msg) in v.ArchFailures) sb.Append($"- {test}: {ForwardJudge.Clip(msg, 2500)}\n");
        }
        if (v.PublicFailures.Count > 0)
        {
            sb.Append($"\nPublic scenarios that fail ({v.PublicFailures.Count}; the first differences of each):\n");
            foreach (var (name, msg) in v.PublicFailures.Take(12)) sb.Append($"- {name}:\n  {ForwardJudge.Clip(msg, 1200).Replace("\n", "\n  ")}\n");
            if (v.PublicFailures.Count > 12) sb.Append($"(... {v.PublicFailures.Count - 12} more public scenario(s) fail)\n");
        }
        var hidden = v.Slices.Where(s => s.Value.HiddenPassed < s.Value.HiddenTotal).Select(s => $"{s.Key} {s.Value.HiddenPassed}/{s.Value.HiddenTotal}").ToList();
        if (hidden.Count > 0) sb.Append($"\nHidden scenarios that pass: {string.Join(", ", hidden)} (no details: check your code against every rule of SPEC.md and the digests).\n");
        if (v.TimedOut) sb.Append("\nThe tests did not finish in time (an endless loop?).\n");
        return sb.ToString();
    }

    /// <summary>A harness path as the model knows the file: its type name.</summary>
    static string Show(ForwardInputs inputs, string s) =>
        Regex.Replace(s, $@"^(?:{string.Join("|", inputs.AllModules().Select(Regex.Escape))})/src/(?:main|test)/java/(?:[\w]+/)*(\w+\.java)", "$1");

    // ------------------------------------------------------------------------------------------------ files

    static Dictionary<string, string> Parse(string reply, List<string> problems)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in JavaBlock.Matches(reply))
        {
            var src = m.Groups[1].Value.Replace("\r\n", "\n").TrimEnd() + "\n";
            if (src.Trim().Length == 0) continue;
            var type = TopType.Matches(src).Select(t => t.Groups[1].Value).FirstOrDefault();
            if (type is null) { problems.Add($"a ```java block declares no top-level type: {ForwardJudge.Clip(src, 120)}"); continue; }
            // The same class returned twice: the later copy wins (FE 8.4, a CAS lesson). Two classes with one simple name in
            // different packages break the contract.
            if (files.TryGetValue(type, out var earlier) && ForwardJudge.PackageOf(earlier) != ForwardJudge.PackageOf(src))
                problems.Add($"{type} is declared in two packages ({ForwardJudge.PackageOf(earlier)}, {ForwardJudge.PackageOf(src)}); simple class names must be unique");
            files[type] = src;
        }
        return files;
    }

    static string Render(ForwardInputs inputs, IReadOnlyDictionary<string, string> files, ISet<string> modules) =>
        string.Join("\n", files.Where(f => modules.Contains(inputs.ModuleOfSource(f.Value))).Select(f => $"```java\n{f.Value.TrimEnd()}\n```"));

    static void WriteDeliverable(ForwardInputs inputs, string dir, IReadOnlyDictionary<string, string> files)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (var (name, src) in files)
        {
            var path = Path.Combine(dir, inputs.PathOf(name, src).Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, src);
        }
    }

    static readonly string[] Jdk =
    [
        "java.math.BigDecimal", "java.math.BigInteger", "java.math.RoundingMode", "java.math.MathContext",
        "java.util.List", "java.util.ArrayList", "java.util.Map", "java.util.HashMap", "java.util.LinkedHashMap", "java.util.TreeMap",
        "java.util.Set", "java.util.HashSet", "java.util.TreeSet", "java.util.LinkedHashSet", "java.util.Optional", "java.util.Arrays",
        "java.util.Collections", "java.util.Objects", "java.util.Iterator", "java.util.Deque", "java.util.ArrayDeque", "java.util.Locale",
        "java.util.Comparator", "java.util.SortedMap", "java.util.NavigableMap", "java.util.EnumSet", "java.util.EnumMap",
        "java.util.stream.Collectors", "java.util.stream.Stream", "java.util.stream.IntStream",
        "java.util.function.Consumer", "java.util.function.Supplier", "java.util.function.Function", "java.util.function.BiFunction",
        "java.util.function.BiConsumer", "java.util.function.Predicate", "java.util.function.UnaryOperator",
        "java.time.Clock", "java.time.LocalDate", "java.time.LocalDateTime", "java.time.LocalTime", "java.time.YearMonth", "java.time.Instant",
        "java.time.format.DateTimeFormatter"
    ];

    static readonly Regex ImportLine = new(@"^\s*import\s+(static\s+)?([\w.]+)(\.\*)?\s*;", RegexOptions.Multiline);
    static readonly Regex PackageLine = new(@"^\s*package\s+[\w.]+\s*;[^\n]*\n", RegexOptions.Multiline);
    static readonly Regex Strings = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])'|//[^\n]*|/\*.*?\*/", RegexOptions.Singleline);
    static readonly Regex TypeToken = new(@"(?<![\w.])([A-Z]\w*)\b");

    /// <summary>
    /// Missing imports (pipeline v2, across packages): a type used by its simple name that is a cas-api class, one of the
    /// service's own classes in another package, or a common java.* class, and that the file neither declares nor imports.
    /// </summary>
    static List<string> AddImports(SortedDictionary<string, string> files, ForwardInputs inputs)
    {
        var all = new List<string>(Jdk);
        all.AddRange(inputs.Api.Keys.Select(p => p[..^5].Replace('/', '.')));
        foreach (var (name, src) in files) if (ForwardJudge.PackageOf(src) is { } pkg) all.Add(pkg + "." + name);
        var known = all.Distinct().GroupBy(fq => fq[(fq.LastIndexOf('.') + 1)..]).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
        var added = new List<string>();
        foreach (var name in files.Keys.ToList())
        {
            var src = files[name];
            var own = ForwardJudge.PackageOf(src) ?? "";
            var imported = new HashSet<string>(StringComparer.Ordinal);
            var wildcards = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match m in ImportLine.Matches(src))
            {
                if (m.Groups[3].Success) wildcards.Add(m.Groups[2].Value);
                else imported.Add(m.Groups[2].Value[(m.Groups[2].Value.LastIndexOf('.') + 1)..]);
            }
            var declared = Regex.Matches(src, @"\b(?:class|record|interface|enum)\s+([A-Z]\w*)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var missing = new SortedSet<string>(StringComparer.Ordinal);
            foreach (Match t in TypeToken.Matches(Strings.Replace(src, " ")))
            {
                var simple = t.Groups[1].Value;
                if (declared.Contains(simple) || imported.Contains(simple) || !known.TryGetValue(simple, out var fq)) continue;
                var pkg = fq[..fq.LastIndexOf('.')];
                if (pkg == own || wildcards.Contains(pkg)) continue;
                missing.Add(fq);
            }
            if (missing.Count == 0) continue;
            var lines = string.Concat(missing.Select(fq => $"import {fq};\n"));
            var lastImport = ImportLine.Matches(src).LastOrDefault();
            if (lastImport is not null)
            {
                int end = src.IndexOf('\n', lastImport.Index + lastImport.Length - 1);
                end = end < 0 ? src.Length : end + 1;
                files[name] = src[..end] + lines + src[end..];
            }
            else if (PackageLine.Match(src) is { Success: true } p)
                files[name] = src[..(p.Index + p.Length)] + "\n" + lines + src[(p.Index + p.Length)..];
            else continue;
            added.AddRange(missing.Select(fq => $"{name}: import {fq}"));
        }
        if (added.Count > 0) Program.Log($"  {added.Count} missing import(s) added");
        return added;
    }

    // ------------------------------------------------------------------------------------------------ summary

    static void WriteSummary(string outDir, string model, string arm, string designer, List<SliceResult> slices, ForwardJudge.Verdict? final,
        DateTime started, JsonArray runs)
    {
        var o = new JsonObject
        {
            ["schema"] = "mlcpu.forward-implementation-run.v1", ["pipeline"] = "v2", ["model"] = model, ["arm"] = arm, ["designer"] = designer,
            ["updated"] = DateTime.Now.ToString("O"), ["minutes"] = Math.Round((DateTime.Now - started).TotalMinutes, 1),
            ["slices"] = new JsonArray(slices.Select(s => (JsonNode)new JsonObject
            {
                ["slice"] = s.Slice, ["attempts"] = s.Attempts, ["bestAttempt"] = s.BestAttempt, ["endedBy"] = s.EndedBy, ["minutes"] = s.Minutes,
                ["best"] = s.Best is null ? null : VerdictJson(s.Best)
            }).ToArray()),
            ["final"] = final is null ? null : VerdictJson(final),
            ["passed"] = final is not null && final.ArchGreen && final.PublicPassed == final.PublicTotal && final.HiddenTotal > 0
                         && final.HiddenPassed * 100 >= final.HiddenTotal * 90,
            ["exact"] = final?.AllPass ?? false,
            ["attempts"] = runs.DeepClone()
        };
        File.WriteAllText(Path.Combine(outDir, "summary.json"), o.ToJsonString(Program.Json) + "\n");
    }
}
