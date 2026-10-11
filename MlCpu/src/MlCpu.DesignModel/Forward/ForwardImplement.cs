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
        string reasoning, int maxTokens, int context, TimeSpan testTimeout, string arm, string designer, int plateau = 3)
    {
        Directory.CreateDirectory(outDir);
        var architecture = JsonNode.Parse(File.ReadAllText(Path.Combine(designDir, "architecture.json")))!;
        var judge = new ForwardJudge(inputs, harnessDir);
        var accepted = new SortedDictionary<string, string>(StringComparer.Ordinal);
        var results = new List<SliceResult>();
        var runs = new JsonArray();
        var started = DateTime.Now;
        var built = new List<string>();
        foreach (var slice in ForwardInputs.Slices)
        {
            built.Add(slice.Name);
            var sliceDesign = Path.Combine(designDir, $"slice-{slice.Name}.json");
            var design = File.Exists(sliceDesign) ? JsonNode.Parse(File.ReadAllText(sliceDesign)) : null;
            Program.Log($"===== {modelName}: arm {arm}, slice {slice.Name} ({built.Count}/{ForwardInputs.Slices.Count}), design by {designer}");
            var r = Slice(chat, inputs, slice, architecture, design, accepted, judge, built, Path.Combine(outDir, slice.Name), attempts, reasoning, maxTokens,
                context, testTimeout, plateau, runs);
            results.Add(r);
            WriteSummary(outDir, modelName, arm, designer, results, null, started, runs);
        }
        // The final code, judged on every scenario.
        judge.Write(accepted);
        var final = judge.Judge(built, testTimeout);
        WriteDeliverable(Path.Combine(outDir, "java"), accepted);
        WriteSummary(outDir, modelName, arm, designer, results, final, started, runs);
        Program.Log($"arm {arm}: final {(final.Compiled ? $"architecture {final.ArchPassed}/{final.ArchTotal}, public {final.PublicPassed}/{final.PublicTotal}, hidden {final.HiddenPassed}/{final.HiddenTotal}" : "does not compile")}");
        return final.AllPass ? 0 : 1;
    }

    static SliceResult Slice(IChatBackend chat, ForwardInputs inputs, ForwardInputs.Slice slice, JsonNode architecture, JsonNode? design,
        SortedDictionary<string, string> accepted, ForwardJudge judge, List<string> built, string outDir, int attempts, string reasoning, int maxTokens,
        int context, TimeSpan testTimeout, int plateau, JsonArray runs)
    {
        Directory.CreateDirectory(outDir);
        var started = DateTime.Now;
        var prompt = BuildPrompt(inputs, slice, architecture, design, accepted, built);
        if (prompt.Length / 3.6 + maxTokens > context) Program.Log($"  WARNING: the prompt (~{prompt.Length / 3.6:N0} tokens) and a {maxTokens}-token reply may not fit the context");
        File.WriteAllText(Path.Combine(outDir, "implementation.prompt.md"), prompt);
        var baseMessages = new List<ChatMessage> { new("system", SystemPrompt), new("user", prompt) };
        var messages = new List<ChatMessage>(baseMessages);
        var files = new SortedDictionary<string, string>(accepted, StringComparer.Ordinal);
        var editable = new HashSet<string>(StringComparer.Ordinal) { slice.Module, "cas-application" };
        (int Attempt, ForwardJudge.Verdict? Verdict, SortedDictionary<string, string>? Files) best = (0, null, null);
        int stale = 0, bestHidden = -1, used = 0;
        string ended = "attempts";
        string lastFeedback = "";
        for (int attempt = 1; attempt <= attempts; attempt++)
        {
            used = attempt;
            ChatResult r;
            try { r = chat.Complete(messages, maxTokens, reasoning); }
            catch (HttpRequestException e) when (e.Message.StartsWith("llama-server error:", StringComparison.Ordinal) && e.Message.Contains("\"code\":500"))
            {
                Program.Log($"slice {slice.Name} attempt {attempt}: the server could not read the reply; counted as a failed attempt");
                File.WriteAllText(Path.Combine(outDir, $"attempt{attempt}.txt"), "(no reply: " + e.Message + ")\n");
                runs.Add(new JsonObject { ["slice"] = slice.Name, ["attempt"] = attempt, ["replyError"] = ForwardJudge.Clip(e.Message, 500) });
                messages = [.. baseMessages, new("assistant", Render(files, editable)), new("user",
                    "Your last reply could not be read: the server rejected its format. Answer again, in the format asked for.\n\n" +
                    (lastFeedback.Length > 0 ? "The feedback you were answering:\n" + lastFeedback : Instructions))];
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
                        : $"{name}.java is in {module}, which this slice does not change (it may change {slice.Module} and cas-application)");
                    continue;
                }
                if (files.TryGetValue(name, out var existing) && ModuleOf(existing) != module && !editable.Contains(ModuleOf(existing)))
                {
                    problems.Add($"{name} is already a class of {ModuleOf(existing)}; simple class names must be unique across the service");
                    continue;
                }
                files[name] = src;
            }
            if (!files.ContainsKey("CasApplication") || ForwardJudge.PackageOf(files["CasApplication"]) != "com.mlcpu.cas.application")
                problems.Add("the entry point com.mlcpu.cas.application.CasApplication (public static CustomerAccountService create(CasPorts ports)) is missing");
            var attemptJson = Program.AttemptJson(attempt, r, problems);
            attemptJson["slice"] = slice.Name;
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
                message = Feedback(verdict, judge, files);
                bool better = best.Verdict is null || Rank(verdict).CompareTo(Rank(best.Verdict)) > 0;
                if (better) best = (attempt, verdict, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
                if (verdict.AllPass)
                {
                    runs.Add(attemptJson);
                    ended = "passed";
                    break;
                }
                // Plateau: every public scenario passes and the architecture is green, but the hidden count does not improve.
                if (verdict.ArchGreen && verdict.PublicPassed == verdict.PublicTotal)
                {
                    if (verdict.HiddenPassed > bestHidden) { bestHidden = verdict.HiddenPassed; stale = 0; }
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
            runs.Add(attemptJson);
            if (attempt == attempts) break;
            lastFeedback = editNote + message + "\nFix the causes. " + Instructions;
            messages = [.. baseMessages, new("assistant", Render(files, editable)), new("user", lastFeedback)];
        }
        var deliverable = best.Files ?? files;
        accepted.Clear();
        foreach (var (k, v) in deliverable) accepted[k] = v;
        WriteDeliverable(Path.Combine(outDir, "java"), deliverable);
        Program.Log($"slice {slice.Name}: {ended} after {used} attempt(s); best attempt {best.Attempt}: {(best.Verdict is null ? "nothing compiled" : Describe(best.Verdict))}");
        return new SliceResult(slice.Name, used, best.Attempt, best.Verdict, ended, Math.Round((DateTime.Now - started).TotalMinutes, 1));
    }

    /// <summary>Best attempt first: compiles, then architecture green, then the most scenarios passed (public and hidden).</summary>
    static (int, int, int) Rank(ForwardJudge.Verdict v) => (v.Compiled ? 1 : 0, v.ArchGreen ? 1 : 0, v.PublicPassed + v.HiddenPassed);

    static string ModuleOf(string source) => ForwardJudge.ModuleOf(ForwardJudge.PackageOf(source) ?? "") ?? "";

    public static string Describe(ForwardJudge.Verdict v) => !v.Compiled ? $"compile FAILED ({v.CompileErrors.Count} errors)"
        : $"architecture {v.ArchPassed}/{v.ArchTotal}; public {v.PublicPassed}/{v.PublicTotal}; hidden {v.HiddenPassed}/{v.HiddenTotal}"
          + $" ({string.Join(", ", v.Slices.Select(s => $"{s.Key} {s.Value.PublicPassed + s.Value.HiddenPassed}/{s.Value.PublicTotal + s.Value.HiddenTotal}"))}){(v.TimedOut ? " TIMED OUT" : "")}";

    public static JsonObject VerdictJson(ForwardJudge.Verdict v) => new()
    {
        ["compiled"] = v.Compiled, ["compileErrors"] = v.CompileErrors.Count, ["architecture"] = new JsonObject { ["passed"] = v.ArchPassed, ["total"] = v.ArchTotal },
        ["public"] = new JsonObject { ["passed"] = v.PublicPassed, ["total"] = v.PublicTotal },
        ["hidden"] = new JsonObject { ["passed"] = v.HiddenPassed, ["total"] = v.HiddenTotal },
        ["slices"] = new JsonObject(v.Slices.Select(s => KeyValuePair.Create(s.Key, (JsonNode?)new JsonObject
        {
            ["publicPassed"] = s.Value.PublicPassed, ["publicTotal"] = s.Value.PublicTotal, ["hiddenPassed"] = s.Value.HiddenPassed, ["hiddenTotal"] = s.Value.HiddenTotal
        }))),
        ["architectureFailures"] = Program.Strings(v.ArchFailures.Select(a => a.Test)),
        ["publicFailures"] = Program.Strings(v.PublicFailures.Select(f => f.Scenario)),
        ["timedOut"] = v.TimedOut
    };

    // ------------------------------------------------------------------------------------------------ prompt

    static string BuildPrompt(ForwardInputs inputs, ForwardInputs.Slice slice, JsonNode architecture, JsonNode? design,
        IReadOnlyDictionary<string, string> accepted, IReadOnlyList<string> built)
    {
        var sb = new StringBuilder();
        sb.Append(inputs.Spec.TrimEnd()).Append("\n\n# Design rules (RULES.md)\n").Append(inputs.Rules.TrimEnd()).Append("\n\n");
        sb.Append(inputs.ApiSection()).Append('\n');
        sb.Append(inputs.DigestSection(slice.Digests));
        sb.Append("# The design you implement\n## Architecture (mlcpu.service-design.v1)\n").Append(architecture.ToJsonString(Program.Json)).Append("\n\n");
        if (design is not null) sb.Append($"## Slice design: {slice.Name} (mlcpu.slice-design.v1)\n").Append(design.ToJsonString(Program.Json)).Append("\n\n");
        sb.Append(TestingSection(inputs));
        if (accepted.Count > 0)
        {
            sb.Append("# Code already built (earlier slices; it must keep passing their scenarios)\n");
            foreach (var (name, src) in accepted.OrderBy(f => ForwardJudge.PathOf(f.Key, f.Value), StringComparer.Ordinal))
                sb.Append($"## {ForwardJudge.PathOf(name, src)}\n```java\n{src.TrimEnd()}\n```\n");
            sb.Append('\n');
        }
        sb.Append(PublicScenarios(inputs, slice));
        var later = ForwardInputs.Slices.SkipWhile(s => s.Name != slice.Name).Skip(1).SelectMany(s => s.Methods).ToList();
        sb.Append($"""
            # Task
            Implement the slice "{slice.Name}" of the design: the classes of {slice.Module} (package {slice.Package} and below) and the
            application classes (com.mlcpu.cas.application) for CustomerAccountService.{string.Join(", CustomerAccountService.", slice.Methods)}.

            Contract:
            - Java 21. Each file's package decides its module: {slice.Package}.. is {slice.Module}, com.mlcpu.cas.application.. is
              cas-application. This slice may add or change files of those two modules only.
            - Simple class names are unique across the service (one top-level type per file).
            - Dependencies (RULES.md): {slice.Module} uses only java.* (not java.io, java.nio, java.net) and com.mlcpu.cas.api..;
              cas-application uses java.*, com.mlcpu.cas.api.. and the context modules. No frameworks.
            - The entry point com.mlcpu.cas.application.CasApplication has `public static CustomerAccountService create(CasPorts ports)`;
              every scenario reaches the service through it.{(later.Count > 0 ? $"\n- The methods of later slices ({string.Join(", ", later)}) may throw\n  UnsupportedOperationException until their slice is built." : "")}
            - Behaviour: every rule of SPEC.md and of the digests, exactly; the scenarios check every value (BigDecimal scale included).

            Output: each file as its own fenced block that starts with ```java and whose first line is the package declaration.
            Return every file of this slice now.
            """);
        return sb.ToString();
    }

    static string TestingSection(ForwardInputs inputs)
    {
        var ports = ForwardInputs.Norm(File.ReadAllText(Path.Combine(inputs.CasDir, "service", "cas-acceptance", "src", "test", "java", "com", "mlcpu", "cas",
            "acceptance", "InMemoryPorts.java")));
        return $"""
            # How the service is tested
            Each acceptance scenario loads an initial state into in-memory adapters of the ports (below), builds the service with
            CasApplication.create(ports), and runs its steps in order: before each step the clock is set to the step's time, then the
            command or query is called and its result (or the ReferenceDataMissing it throws, with "what" and "key") is compared with
            the expected one, field by field. At the end, the stores the scenario names (accounts, categoryBalances, ledger,
            rejections, customers) are compared in the order the ports list them. Values compare exactly: BigDecimal by value and
            scale ("12.30" is not "12.3"), timestamps to the microsecond. About 20% of the scenarios are public: you see their
            failures in full. The others are hidden: you see how many pass.

            ## The in-memory adapters (cas-acceptance, test code)
            ```java
            {ports.TrimEnd()}
            ```


            """;
    }

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
            sb.Append("\nExamples in full (the scenario format: initial state, steps with their expected results, expected final state):\n");
            foreach (var e in examples) sb.Append("```json\n").Append(e.TrimEnd()).Append("\n```\n");
        }
        return sb.Append('\n').ToString();
    }

    public const string Instructions = """
        Return every file you change, complete, each in its own ```java block; files you do not return stay as they are.
        For a change of a few lines you may send an edit instead of the whole file:
        <<<<<<< SEARCH Name.java
        the lines to replace, copied exactly from the current file
        =======
        the lines that take their place
        >>>>>>> REPLACE
        The SEARCH lines are removed and the REPLACE lines are put in their place, so to add lines repeat a neighbouring line in
        both parts. The SEARCH lines must occur once in that file; include a few surrounding lines when a line is not unique.
        (Missing imports of cas-api classes, of your own classes and of common java.* classes are added for you.)
        """;

    // ------------------------------------------------------------------------------------------------ feedback

    static readonly Regex ErrorHead = new(@"^(\S+?\.java):(\d+): error: (.*)$");
    static readonly Regex SyntaxError = new(@"expected$|illegal start of|not a statement|unclosed|reached end of file|class, interface, enum, or record expected|orphaned|without 'if'|without 'try'|'catch' without|'finally' without|illegal character|invalid method declaration");

    static string Feedback(ForwardJudge.Verdict v, ForwardJudge judge, IReadOnlyDictionary<string, string> files)
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
                    sb.Append($"{Show(file.Key)}:{m.Groups[2].Value}: error: {m.Groups[3].Value}\n").Append(judge.Excerpt(file.Key, int.Parse(m.Groups[2].Value), 4));
                    if (list.Count > 1) sb.Append($"({list.Count - 1} further error(s) in this file follow from this one.)\n");
                    continue;
                }
                foreach (var e in list.Take(10)) sb.Append(Show(e)).Append('\n');
                if (list.Count > 10) sb.Append($"(... {list.Count - 10} more error(s) in {Show(file.Key)})\n");
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
    static string Show(string s) => Regex.Replace(s, @"^cas-[a-z]+/src/(?:main|test)/java/(?:[\w]+/)*(\w+\.java)", "$1");

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
            if (files.ContainsKey(type)) problems.Add($"{type} is returned twice; simple class names must be unique");
            files[type] = src;
        }
        return files;
    }

    static string Render(IReadOnlyDictionary<string, string> files, ISet<string> modules) =>
        string.Join("\n", files.Where(f => modules.Contains(ModuleOf(f.Value))).Select(f => $"```java\n{f.Value.TrimEnd()}\n```"));

    static void WriteDeliverable(string dir, IReadOnlyDictionary<string, string> files)
    {
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        foreach (var (name, src) in files)
        {
            var path = Path.Combine(dir, ForwardJudge.PathOf(name, src).Replace('/', Path.DirectorySeparatorChar));
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
