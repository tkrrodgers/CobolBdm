using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MlCpu.DesignModel.Forward;

/// <summary>
/// The host lane (ForwardEngineeringFineract.md 9.1, S8; GemmaJavaTemplates.md 5.11): the plugin around the service is rendered
/// from the service model by the host templates and the model writes only its holes (the plugin's holes.json: id, file,
/// interface, rules, contract). Everything the host lane needs comes from the package manifest's "host" section (forward.json):
/// the judge script (paths, judge), the host digests, the host facade digest, the generated types a hole uses, the scenario
/// directories and the examples. The host's own build compiles and checkstyles the holes and its own test runner runs the
/// scenarios (ofbiz-host.sh judge: OFBiz, 'ofbiz --test'). The model gets compile and checkstyle errors with their lines, the
/// public scenario failures in full and the hidden ones as a count. Debug mode as fwd-implement's: once the holes compile, each
/// repair turn gets the focused prompt (contract, digests, the types the code uses, the current code, the scenarios that pass,
/// the feedback); the ratchet names public scenarios an attempt loses; two attempts worse than the best lead back to it; with
/// most scenarios passing it asks for edits only.
/// </summary>
public static class ForwardHost
{
    public const string SystemPrompt = """
        You are a senior Java engineer who integrates services into an ERP as plugins. You write the adapter classes that connect
        a service's ports to the ERP's own services and entities, exactly as the rules say, and nothing else: idiomatic,
        production-quality Java that passes the ERP's checkstyle. You answer with Java source files only, each in its own ```java
        fenced block (or, when repairing, with SEARCH/REPLACE edit blocks).
        """;

    static readonly Regex JavaBlock = new(@"```java[ \t]*\r?\n(.*?)```", RegexOptions.Singleline);
    static readonly Regex TopType = new(@"^(?:public\s+)?(?:(?:final|abstract|sealed|non-sealed|static)\s+)*(?:class|record|interface|enum)\s+(\w+)", RegexOptions.Multiline);

    public sealed record Hole(string Id, string Path, string Type, string Implements, string Rules, string Contract);

    /// <summary>forward.json "host".</summary>
    public sealed record HostSpec(string Name, string Script, string[] Digests, string Facade, string[] Context, string[] PublicDirs, string[] HiddenDirs,
        string[] Examples, string Testing, string[] Contract, int JavaLevel, TimeSpan Timeout);

    public sealed record Failure(string Id, bool Public, string Message);

    public sealed record Verdict(bool Compiled, IReadOnlyList<string> BuildErrors, string BuildTail, bool Ran, string RunTail, int PublicTotal, int PublicPassed,
        int HiddenTotal, int HiddenPassed, IReadOnlyList<Failure> Failures, IReadOnlySet<string> PublicPassing, bool TimedOut)
    {
        public bool AllPass => Compiled && Ran && !TimedOut && PublicPassed == PublicTotal && HiddenPassed == HiddenTotal && PublicTotal + HiddenTotal > 0;
        public int Passed => PublicPassed + HiddenPassed;
        public int Total => PublicTotal + HiddenTotal;
    }

    public static HostSpec Spec(ForwardInputs inputs)
    {
        var m = JsonNode.Parse(File.ReadAllText(Path.Combine(inputs.Dir, "forward.json")))!["host"]
                ?? throw new InvalidDataException($"{inputs.Dir}/forward.json has no \"host\" section");
        static string[] Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => x!.GetValue<string>()).ToArray();
        string S(string key) => m[key]?.GetValue<string>() ?? throw new InvalidDataException($"forward.json host.{key} is missing");
        return new HostSpec(S("name"), Path.GetFullPath(Path.Combine(inputs.Dir, S("script"))), Strings(m["digests"]), S("facade"), Strings(m["context"]),
            Strings(m["scenarios"]?["public"]), Strings(m["scenarios"]?["hidden"]), Strings(m["examples"]), string.Join("\n", Strings(m["testing"])),
            Strings(m["contract"]), m["javaLevel"]?.GetValue<int>() ?? inputs.JavaLevel ?? 17, TimeSpan.FromMinutes(m["timeoutMinutes"]?.GetValue<int>() ?? 20));
    }

    // ------------------------------------------------------------------------------------------------ the host's side

    /// <summary>
    /// Git Bash (the judge scripts are bash): MLCPU_BASH, else the bash of the Git on PATH (git.exe in &lt;Git&gt;\cmd), of a
    /// machine-wide or per-user Git; never a bare "bash", which Windows resolves to WSL's.
    /// </summary>
    static string Bash()
    {
        if (Environment.GetEnvironmentVariable("MLCPU_BASH") is { Length: > 0 } b) return b;
        var roots = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(d => d.Length > 0 && File.Exists(Path.Combine(d, "git.exe"))).Select(d => Path.GetDirectoryName(d.TrimEnd('\\', '/')))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git"))
            .Append(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git"));
        return roots.OfType<string>().Select(r => Path.Combine(r, "bin", "bash.exe")).FirstOrDefault(File.Exists)
               ?? throw new InvalidOperationException("Git Bash not found: set MLCPU_BASH to its bash.exe");
    }

    static (int Code, string Output, bool TimedOut) Script(HostSpec host, string verb, IDictionary<string, string> env, TimeSpan timeout)
    {
        var psi = new ProcessStartInfo(Bash()) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        psi.ArgumentList.Add(host.Script.Replace('\\', '/'));
        psi.ArgumentList.Add(verb);
        foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var output = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        if (!p.WaitForExit(timeout))
        {
            try { p.Kill(true); } catch (InvalidOperationException) { }
            return (-1, output.ToString(), true);
        }
        p.WaitForExit();
        return (p.ExitCode, output.ToString(), false);
    }

    /// <summary>The rendered plugin's root and its hole package directory ('paths': PLUGIN=..., SRC=...).</summary>
    public static (string Plugin, string Src) Paths(HostSpec host)
    {
        var (code, output, _) = Script(host, "paths", new Dictionary<string, string>(), TimeSpan.FromMinutes(2));
        string Get(string key) => Regex.Match(output, $@"(?m)^{key}=(.+?)\s*$") is { Success: true } m ? m.Groups[1].Value
            : throw new InvalidOperationException($"{host.Script} paths (exit {code}) did not print {key}=: {ForwardJudge.Clip(output, 500)}");
        return (Get("PLUGIN"), Get("SRC"));
    }

    public static List<Hole> Holes(string plugin)
    {
        var file = Path.Combine(plugin, "holes.json");
        if (!File.Exists(file)) throw new InvalidOperationException($"{file} is missing: render and prepare the plugin first (the host script's 'prepare')");
        return JsonNode.Parse(File.ReadAllText(file))!["holes"]!.AsArray().Select(h => new Hole(h!["id"]!.GetValue<string>(), h["path"]!.GetValue<string>(),
            Path.GetFileNameWithoutExtension(h["path"]!.GetValue<string>()), h["implements"]!.GetValue<string>(), h["rules"]?.GetValue<string>() ?? "",
            ForwardInputs.Norm(h["contract"]?.GetValue<string>() ?? ""))).ToList();
    }

    /// <summary>The scenario ids of a set of directories (file names without .json).</summary>
    static HashSet<string> Ids(ForwardInputs inputs, IEnumerable<string> dirs) => dirs.Select(d => Path.Combine(inputs.Dir, d)).Where(Directory.Exists)
        .SelectMany(d => Directory.EnumerateFiles(d, "*.json")).Select(Path.GetFileNameWithoutExtension).OfType<string>().ToHashSet(StringComparer.Ordinal);

    static readonly Regex JavacError = new(@"^(?<file>\S.*?\.java):(?<line>\d+): error: (?<msg>.*)$");
    static readonly Regex CheckstyleError = new(@"\[ant:checkstyle\] \[(?:ERROR|WARN)\] (?<file>.+?\.java):(?<line>\d+)(?::(?<col>\d+))?: (?<msg>.*)$");
    static readonly Regex ScenarioName = new(@"^scenarios\[(.+)\]$");

    /// <summary>Judges a set of hole files with the host script; the attempt's directory keeps the holes, the build log and the JUnit XML.</summary>
    public static Verdict Judge(ForwardInputs inputs, HostSpec host, IReadOnlyDictionary<string, string> holes, string attemptDir)
    {
        var holeDir = Path.Combine(attemptDir, "holes");
        if (Directory.Exists(holeDir)) Directory.Delete(holeDir, true);
        Directory.CreateDirectory(holeDir);
        foreach (var (type, src) in holes) File.WriteAllText(Path.Combine(holeDir, type + ".java"), src);
        var judgeDir = Path.Combine(attemptDir, "judge");
        var env = new Dictionary<string, string> { ["HOLES"] = holeDir.Replace('\\', '/'), ["OUT"] = judgeDir.Replace('\\', '/'), ["ENGINE"] = "", ["SCENARIOS"] = "" };
        var (code, output, timedOut) = Script(host, "judge", env, host.Timeout);
        File.WriteAllText(Path.Combine(attemptDir, "judge.log"), output);
        var publicIds = Ids(inputs, host.PublicDirs);
        var hiddenIds = Ids(inputs, host.HiddenDirs);
        var none = new HashSet<string>(StringComparer.Ordinal);
        if (timedOut) return new Verdict(true, [], "", false, "the judge did not finish in time", publicIds.Count, 0, hiddenIds.Count, 0, [], none, true);
        var buildLog = Path.Combine(judgeDir, "build.log");
        var build = File.Exists(buildLog) ? ForwardInputs.Norm(File.ReadAllText(buildLog)) : "";
        if (code == 2 || !File.Exists(Path.Combine(judgeDir, "verdict.txt")))
        {
            // A hole the host refused (a generated file) or a build that failed: javac and checkstyle errors with their next lines.
            var errors = new List<string>();
            var lines = build.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                if (JavacError.Match(lines[i]) is { Success: true } j)
                    errors.Add($"{Path.GetFileName(j.Groups["file"].Value)}:{j.Groups["line"].Value}: error: {j.Groups["msg"].Value}\n" +
                               string.Join("\n", lines.Skip(i + 1).TakeWhile(l => !JavacError.IsMatch(l) && !l.StartsWith("[ant:", StringComparison.Ordinal)
                                   && l.Length > 0 && !Regex.IsMatch(l, @"^\d+ errors?$")).Take(4)));
                else if (CheckstyleError.Match(lines[i]) is { Success: true } c)
                    errors.Add($"{Path.GetFileName(c.Groups["file"].Value)}:{c.Groups["line"].Value}: checkstyle: {c.Groups["msg"].Value}");
            }
            var tail = string.Join("\n", (build.Length > 0 ? build : output).Split('\n').Where(l => l.Trim().Length > 0).TakeLast(30));
            return new Verdict(false, errors, tail, false, "", publicIds.Count, 0, hiddenIds.Count, 0, [], none, false);
        }
        var failures = new List<Failure>();
        var passing = new HashSet<string>(StringComparer.Ordinal);
        int publicPassed = 0, hiddenPassed = 0, cases = 0;
        var results = Path.Combine(judgeDir, "test-results");
        foreach (var xml in Directory.Exists(results) ? Directory.EnumerateFiles(results, "*.xml") : [])
            foreach (var tc in XDocument.Load(xml).Descendants("testcase"))
            {
                cases++;
                var name = (string?)tc.Attribute("name") ?? "";
                var id = ScenarioName.Match(name) is { Success: true } sm ? sm.Groups[1].Value : name;
                bool isPublic = !hiddenIds.Contains(id);
                var bad = tc.Element("failure") ?? tc.Element("error");
                if (bad is null)
                {
                    if (publicIds.Contains(id)) { publicPassed++; passing.Add(id); }
                    else if (hiddenIds.Contains(id)) hiddenPassed++;
                    continue;
                }
                var msg = ((string?)bad.Attribute("message") ?? "") is { Length: > 0 } a ? a : (bad.Value ?? "");
                // the assertion's text, not its stack
                msg = string.Join("\n", ForwardInputs.Norm(msg).Split('\n').Where(l => !l.TrimStart().StartsWith("at ", StringComparison.Ordinal)));
                failures.Add(new Failure(id, isPublic, msg.Trim()));
            }
        // A test outside the scenario sets (the data load) that fails is a public failure, and the run did not happen without results.
        var runTail = string.Join("\n", output.Split('\n').Where(l => l.Trim().Length > 0).TakeLast(20));
        if (cases == 0)
        {
            var testLog = Path.Combine(judgeDir, "test.log");
            runTail = File.Exists(testLog) ? string.Join("\n", File.ReadAllLines(testLog).Where(l => l.Contains("ERROR") || l.Contains("Exception")).Take(20)) : runTail;
        }
        return new Verdict(true, [], "", cases > 0, runTail, publicIds.Count, publicPassed, hiddenIds.Count, hiddenPassed, failures, passing, false);
    }

    public static string Describe(Verdict v) => !v.Compiled ? $"build FAILED ({v.BuildErrors.Count} error(s))"
        : !v.Ran ? "the tests did not run" + (v.TimedOut ? " (timed out)" : "")
        : $"public {v.PublicPassed}/{v.PublicTotal}; hidden {v.HiddenPassed}/{v.HiddenTotal}";

    static JsonObject VerdictJson(Verdict v) => new()
    {
        ["compiled"] = v.Compiled, ["buildErrors"] = v.BuildErrors.Count, ["ran"] = v.Ran, ["timedOut"] = v.TimedOut,
        ["public"] = new JsonObject { ["passed"] = v.PublicPassed, ["total"] = v.PublicTotal },
        ["hidden"] = new JsonObject { ["passed"] = v.HiddenPassed, ["total"] = v.HiddenTotal },
        ["publicFailures"] = Program.Strings(v.Failures.Where(f => f.Public).Select(f => f.Id)),
        ["hiddenFailures"] = Program.Strings(v.Failures.Where(f => !f.Public).Select(f => f.Id))
    };

    static (int, int, int) Rank(Verdict v) => (v.Compiled ? 1 : 0, v.Ran ? 1 : 0, v.Passed);

    // ------------------------------------------------------------------------------------------------ prompts

    /// <summary>The API types the holes implement and every API type they reach (records, enums), by simple name.</summary>
    static List<(string Path, string Source)> PortTypes(ForwardInputs inputs, IEnumerable<string> roots, string? also = null)
    {
        var byType = inputs.Api.ToDictionary(a => Path.GetFileNameWithoutExtension(a.Key), a => a, StringComparer.Ordinal);
        var want = new SortedSet<string>(roots.Select(r => r[(r.LastIndexOf('.') + 1)..]), StringComparer.Ordinal);
        if (also is not null)
            foreach (var t in byType.Keys) if (Regex.IsMatch(also, $@"\b{Regex.Escape(t)}\b")) want.Add(t);
        var queue = new Queue<string>(want);
        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            if (!byType.TryGetValue(t, out var e)) continue;
            var body = Regex.Replace(e.Value, @"(?s)/\*.*?\*/|//[^\n]*", " ");
            foreach (var other in byType.Keys)
                if (!want.Contains(other) && Regex.IsMatch(body, $@"\b{Regex.Escape(other)}\b")) { want.Add(other); queue.Enqueue(other); }
        }
        return want.Where(byType.ContainsKey).Select(t => (byType[t].Key, byType[t].Value)).OrderBy(x => x.Key, StringComparer.Ordinal).ToList();
    }

    static string Digests(ForwardInputs inputs, HostSpec host) => string.Concat(host.Digests.Select(d =>
        $"# Rule digest {d}\n{ForwardInputs.Norm(File.ReadAllText(Path.Combine(inputs.Dir, d))).TrimEnd()}\n\n"));

    static string HoleList(IReadOnlyList<Hole> holes) => string.Concat(holes.Select(h =>
        $"## {h.Id}: {h.Type}.java ({h.Path})\n- implements {h.Implements}; rules {h.Rules}\n- contract: {h.Contract.Replace("\n", " ")}\n"));

    static string Task(ForwardInputs inputs, HostSpec host, IReadOnlyList<Hole> holes) => $"""
        # Task
        Write the {ForwardInputs.Words(holes.Count)} hole classes of the plugin: {string.Join(", ", holes.Select(h => h.Type))}.

        Contract:
        - Java {host.JavaLevel} (the host compiles with --release {host.JavaLevel}); package {PackageOfPath(holes[0].Path)}; one top-level class per file, named as above.
        - Each hole keeps its contract (above) and implements its interface exactly; the generated code constructs it.
        - Only the OFBiz services and entities of the facade digest, java.*, the given API and the generated types shown.
        - Behaviour: every rule of the host digest, exactly; the scenarios compare the rows OFBiz holds afterwards, GL entries included.
        {string.Concat(host.Contract.Select(c => c.StartsWith(' ') ? $"{c}\n" : $"- {c}\n"))}
        Output: each file as its own fenced block that starts with ```java and whose first line is the package declaration.
        Return every hole class now.
        """;

    static string PackageOfPath(string path) => Regex.Match(path.Replace('\\', '/'), @"src/main/java/(.+)/[^/]+\.java$") is { Success: true } m
        ? m.Groups[1].Value.Replace('/', '.') : "";

    static string Generated(string src, HostSpec host) => string.Concat(host.Context.Select(t =>
        File.Exists(Path.Combine(src, t + ".java")) ? $"## {t}.java (generated; do not change)\n```java\n{ForwardInputs.Norm(File.ReadAllText(Path.Combine(src, t + ".java"))).TrimEnd()}\n```\n" : ""));

    public static string BuildPrompt(ForwardInputs inputs, HostSpec host, IReadOnlyList<Hole> holes, string src)
    {
        var sb = new StringBuilder();
        sb.Append($"# The {inputs.ServiceName} inside {host.Name}: the plugin\n").Append(host.Testing.TrimEnd()).Append("\n\n");
        sb.Append("# The holes (yours to write; everything else in the plugin is generated)\n").Append(HoleList(holes)).Append('\n');
        sb.Append($"# The given API types the holes implement and use ({inputs.ApiModule}; fixed)\n");
        foreach (var (path, s) in PortTypes(inputs, holes.Select(h => h.Implements)))
            sb.Append($"## {path}\n```java\n{s.TrimEnd()}\n```\n");
        sb.Append("\n# Generated code the holes use\n").Append(Generated(src, host)).Append('\n');
        sb.Append(Digests(inputs, host));
        sb.Append(ForwardInputs.Norm(File.ReadAllText(Path.Combine(inputs.Dir, host.Facade))).TrimEnd()).Append("\n\n");
        sb.Append(PublicScenarios(inputs, host));
        sb.Append(Task(inputs, host, holes));
        return sb.ToString();
    }

    static string PublicScenarios(ForwardInputs inputs, HostSpec host)
    {
        var files = host.PublicDirs.Select(d => Path.Combine(inputs.Dir, d)).Where(Directory.Exists)
            .SelectMany(d => Directory.EnumerateFiles(d, "*.json")).OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).ToList();
        var sb = new StringBuilder($"# Public scenarios ({files.Count})\n");
        foreach (var f in files)
        {
            var j = JsonNode.Parse(File.ReadAllText(f))!;
            var rules = string.Join(", ", (j["rules"] as JsonArray ?? []).Select(x => x!.GetValue<string>()));
            var steps = string.Join(", ", (j["steps"] as JsonArray ?? []).Select(s => s!["service"]?.GetValue<string>()).Distinct());
            sb.Append($"- {j["id"]}: {steps} (rules {rules})\n");
        }
        var examples = host.Examples.Select(id => files.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f) == id)).OfType<string>().ToList();
        if (examples.Count > 0)
        {
            sb.Append("\nExamples in full (each step calls a plugin service, or an OFBiz service to pay or void an invoice, on the business date \"today\"; " +
                      "\"*\" matches any value; \"bind\" names a result value that later steps and checks use as $name; \"checks\" are the rows " +
                      "OFBiz holds at the end, compared field by field):\n");
            foreach (var e in examples) sb.Append("```json\n").Append(ForwardInputs.Norm(File.ReadAllText(e)).TrimEnd()).Append("\n```\n");
        }
        return sb.Append('\n').ToString();
    }

    static string DebugPrompt(ForwardInputs inputs, HostSpec host, IReadOnlyList<Hole> holes, string src, string code, string feedback, string status,
        IReadOnlyCollection<string> passing, bool editsOnly, IReadOnlyList<string> tried)
    {
        var sb = new StringBuilder($"# Debugging the {host.Name} plugin's holes\n");
        sb.Append($"{status} Below: the holes' contract, the host rules, the types the code uses, your current code and the test feedback. " +
                  "Repair the code: change what the feedback needs, keep the rest, and keep every scenario that passes passing.");
        if (editsOnly)
            sb.Append(" Most scenarios pass, so answer with SEARCH/REPLACE edits of the lines the feedback points at, not with whole files: a rewrite loses what passes.");
        sb.Append("\n\n");
        if (tried.Count > 0)
            sb.Append("Repairs already tried since your best version, none better than it (do not repeat them; look for the cause another way):\n")
              .Append(string.Concat(tried.TakeLast(8).Select(t => $"- {t}\n"))).Append('\n');
        sb.Append("# The holes\n").Append(HoleList(holes)).Append('\n');
        var task = Task(inputs, host, holes);
        sb.Append(task[..task.IndexOf("\nOutput:", StringComparison.Ordinal)].TrimEnd()).Append("\n\n");
        sb.Append(Digests(inputs, host));
        sb.Append($"# The API types your code uses ({inputs.ApiModule}; fixed)\n");
        foreach (var (path, s) in PortTypes(inputs, holes.Select(h => h.Implements), code + "\n" + feedback))
            sb.Append($"## {path}\n```java\n{s.TrimEnd()}\n```\n");
        sb.Append("\n# Generated code the holes use\n").Append(Generated(src, host)).Append('\n');
        // the facade's entries the code or the feedback name (all of it when it names none)
        sb.Append(FacadeFor(inputs, host, code + "\n" + feedback));
        sb.Append("# Your current code\n").Append(code.TrimEnd()).Append("\n\n");
        if (passing.Count > 0)
            sb.Append($"Public scenarios that pass now (keep them passing): {string.Join(", ", passing.OrderBy(x => x, StringComparer.Ordinal))}.\n\n");
        if (editsOnly) feedback = feedback.Replace(Instructions.Replace("\r\n", "\n"), ForwardDebugPrompt.EditsOnlyInstructions.Replace("\r\n", "\n"));
        sb.Append("# Feedback\n").Append(feedback.TrimEnd()).Append('\n');
        return sb.ToString();
    }

    /// <summary>The facade digest's sections whose service or entity the text names, under the digest's own headings.</summary>
    static string FacadeFor(ForwardInputs inputs, HostSpec host, string text)
    {
        var facade = ForwardInputs.Norm(File.ReadAllText(Path.Combine(inputs.Dir, host.Facade)));
        var parts = Regex.Split(facade, @"(?m)^(?=### )");
        var keep = parts.Skip(1).Where(p => Regex.Match(p, @"^### (\w+)") is { Success: true } m && Regex.IsMatch(text, $@"\b{m.Groups[1].Value}\b")).ToList();
        if (keep.Count == 0) return facade.TrimEnd() + "\n\n";
        return "# OFBiz facade: the entries your code uses\n" + string.Concat(keep.Select(k => k.TrimEnd() + "\n")) + "\n";
    }

    public static readonly string Instructions = """
        Return every file you change, complete, each in its own ```java block; files you do not return stay as they are.
        For a change of a few lines you may send an edit instead of the whole file:
        <<<<<<< SEARCH Name.java
        the lines to replace, copied exactly from the current file
        =======
        the lines that take their place
        >>>>>>> REPLACE
        The SEARCH lines are removed and the REPLACE lines are put in their place, so to add lines repeat a neighbouring line in
        both parts. The SEARCH lines must occur once in that file; include a few surrounding lines when a line is not unique.
        """;

    static string Feedback(Verdict v)
    {
        var sb = new StringBuilder();
        if (!v.Compiled)
        {
            sb.Append("The plugin does not build with your classes (javac, then OFBiz's checkstyle with 0 violations allowed):\n");
            if (v.BuildErrors.Count == 0) return sb.Append(v.BuildTail).Append('\n').ToString();
            foreach (var e in v.BuildErrors.Take(25)) sb.Append(e.TrimEnd()).Append('\n');
            if (v.BuildErrors.Count > 25) sb.Append($"(... {v.BuildErrors.Count - 25} more)\n");
            return sb.ToString();
        }
        if (!v.Ran) return $"The plugin builds, but its tests did not run{(v.TimedOut ? " in time" : "")}:\n{v.RunTail}\n";
        sb.Append($"Result: {Describe(v)}.\n");
        var pub = v.Failures.Where(f => f.Public).ToList();
        if (pub.Count > 0)
        {
            sb.Append($"\nPublic scenarios that fail ({pub.Count}; the differences of each):\n");
            foreach (var f in pub.Take(12)) sb.Append($"- {f.Id}:\n  {ForwardJudge.Clip(f.Message, 1500).Replace("\n", "\n  ")}\n");
            if (pub.Count > 12) sb.Append($"(... {pub.Count - 12} more public scenario(s) fail)\n");
        }
        if (v.HiddenPassed < v.HiddenTotal)
            sb.Append($"\nHidden scenarios that pass: {v.HiddenPassed}/{v.HiddenTotal} (no details: check your code against every rule of the host digest).\n");
        return sb.ToString();
    }

    // ------------------------------------------------------------------------------------------------ the run

    static Dictionary<string, string> Parse(string reply, ISet<string> holeTypes, List<string> problems)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in JavaBlock.Matches(reply))
        {
            var src = m.Groups[1].Value.Replace("\r\n", "\n").TrimEnd() + "\n";
            if (src.Trim().Length == 0) continue;
            var type = TopType.Matches(src).Select(t => t.Groups[1].Value).FirstOrDefault();
            if (type is null) { problems.Add($"a ```java block declares no top-level type: {ForwardJudge.Clip(src, 120)}"); continue; }
            if (!holeTypes.Contains(type))
            {
                problems.Add($"{type} is not a hole: only {string.Join(", ", holeTypes)} are yours (the rest of the plugin is generated and cannot change)");
                continue;
            }
            files[type] = src;
        }
        return files;
    }

    static string Render(IReadOnlyDictionary<string, string> files) => string.Join("\n", files.Select(f => $"```java\n{f.Value.TrimEnd()}\n```"));

    static readonly Regex FacadeImport = new(@"`import ([\w.]+\.(\w+));`");
    static readonly Regex ImportLine = new(@"(?m)^import\s+(static\s+)?([\w.]+)\.(\w+)\s*;[ \t]*\r?\n");
    static readonly Regex CodeNoise = new(@"""(?:\\.|[^""\\])*""|'(?:\\.|[^'\\])'|//[^\n]*|/\*.*?\*/", RegexOptions.Singleline);
    static readonly string[] JdkTypes = ["java.math.BigDecimal", "java.math.RoundingMode", "java.sql.Timestamp", "java.time.LocalDate", "java.time.LocalDateTime",
        "java.util.List", "java.util.ArrayList", "java.util.Map", "java.util.HashMap", "java.util.LinkedHashMap", "java.util.Objects", "java.util.Optional",
        "java.util.Locale"];

    /// <summary>
    /// The classes a hole may use, by simple name: the facade digest's Java classes (their exact imports), the given API's types,
    /// the generated types shown to the model and common java.* types; a simple name with two candidates is left out.
    /// </summary>
    static Dictionary<string, string> KnownTypes(ForwardInputs inputs, HostSpec host, string package)
    {
        var all = new List<string>(JdkTypes);
        all.AddRange(FacadeImport.Matches(ForwardInputs.Norm(File.ReadAllText(Path.Combine(inputs.Dir, host.Facade)))).Select(m => m.Groups[1].Value));
        all.AddRange(inputs.Api.Keys.Select(p => p[..^5].Replace('/', '.')));
        all.AddRange(host.Context.Select(t => package + "." + t));
        return all.Distinct().GroupBy(f => f[(f.LastIndexOf('.') + 1)..]).Where(g => g.Count() == 1)
            .ToDictionary(g => g.Key, g => g.Single(), StringComparer.Ordinal);
    }

    /// <summary>
    /// Import repair and layout (as fwd-implement adds missing imports, as the templates normalize their files): trailing blanks and tabs
    /// go; an import of a known class from the wrong package is corrected, and
    /// a known class used without an import gets one. Returns what changed ("OfbizLedger: org.apache.ofbiz.entity.EntityQuery ->
    /// org.apache.ofbiz.entity.util.EntityQuery").
    /// </summary>
    static List<string> RepairImports(SortedDictionary<string, string> files, IReadOnlyDictionary<string, string> known)
    {
        var changes = new List<string>();
        foreach (var name in files.Keys.ToList())
        {
            // Layout, as the templates normalize theirs: no trailing blanks, spaces for tabs, one final newline.
            var layout = files[name].Replace("\r\n", "\n").Split('\n');
            int trailing = layout.Count(l => l.Length > 0 && char.IsWhiteSpace(l[^1])), tabs = layout.Count(l => l.Contains('\t'));
            var tidy = string.Join("\n", layout.Select(l => l.Replace("\t", "    ").TrimEnd())).TrimEnd('\n') + "\n";
            if (trailing > 0) changes.Add($"{name}: trailing blanks removed on {trailing} line(s)");
            if (tabs > 0) changes.Add($"{name}: tabs replaced by spaces on {tabs} line(s)");
            var src = tidy;
            var own = ForwardJudge.PackageOf(src) ?? "";
            src = ImportLine.Replace(src, m =>
            {
                if (m.Groups[1].Success || !known.TryGetValue(m.Groups[3].Value, out var fq)) return m.Value;
                var given = m.Groups[2].Value + "." + m.Groups[3].Value;
                if (given == fq) return m.Value;
                changes.Add($"{name}: import {given} -> {fq}");
                return $"import {fq};\n";
            });
            // an import whose class the code never names: never needed, and checkstyle refuses it (UnusedImports)
            var used = CodeNoise.Replace(ImportLine.Replace(src, ""), " ");
            src = ImportLine.Replace(src, m =>
            {
                if (m.Groups[1].Success || Regex.IsMatch(used, $@"\b{Regex.Escape(m.Groups[3].Value)}\b")) return m.Value;
                changes.Add($"{name}: unused import {m.Groups[2].Value}.{m.Groups[3].Value} removed");
                return "";
            });
            var imported = ImportLine.Matches(src).Select(m => m.Groups[3].Value).ToHashSet(StringComparer.Ordinal);
            var declared = Regex.Matches(src, @"\b(?:class|record|interface|enum)\s+([A-Z]\w*)").Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
            var body = CodeNoise.Replace(ImportLine.Replace(src, ""), " ");
            var missing = Regex.Matches(body, @"(?<![\w.])([A-Z]\w*)\b").Select(m => m.Groups[1].Value).Distinct()
                .Where(t => !imported.Contains(t) && !declared.Contains(t) && known.TryGetValue(t, out var fq) && fq[..fq.LastIndexOf('.')] != own
                            && !fq.StartsWith("java.lang.", StringComparison.Ordinal))
                .OrderBy(t => t, StringComparer.Ordinal).ToList();
            if (missing.Count > 0)
            {
                var lines = string.Concat(missing.Select(t => $"import {known[t]};\n"));
                var last = ImportLine.Matches(src).LastOrDefault();
                int at = last is not null ? last.Index + last.Length : Regex.Match(src, @"(?m)^package\s+[\w.]+\s*;[^\n]*\n") is { Success: true } p ? p.Index + p.Length : 0;
                src = src[..at] + (last is null ? "\n" : "") + lines + src[at..];
                changes.AddRange(missing.Select(t => $"{name}: import {known[t]} added"));
            }
            files[name] = src;
        }
        return changes;
    }

    /// <summary>fwd-host: the model writes the host holes; every attempt is judged by the host. Exit 0 when every scenario passes.</summary>
    public static int Run(IChatBackend chat, ForwardInputs inputs, string outDir, int attempts, string modelName, string reasoning, int maxTokens, int context,
        int plateau, bool debugPrompt, string? warmStart)
    {
        Directory.CreateDirectory(outDir);
        var host = Spec(inputs);
        var (plugin, src) = Paths(host);
        var holes = Holes(plugin);
        var holeTypes = holes.Select(h => h.Type).ToHashSet(StringComparer.Ordinal);
        var known = KnownTypes(inputs, host, PackageOfPath(holes[0].Path));
        var started = DateTime.Now;
        var prompt = BuildPrompt(inputs, host, holes, src);
        File.WriteAllText(Path.Combine(outDir, "host.prompt.md"), prompt);
        inputs.CheckLeaks(prompt, $"{host.Name} host prompt");
        if (prompt.Length / 3.6 + maxTokens > context) Program.Log($"  WARNING: the prompt (~{prompt.Length / 3.6:N0} tokens) and a {maxTokens}-token reply may not fit the context");
        Program.Log($"===== {modelName}: host {host.Name}, {holes.Count} hole(s) ({string.Join(", ", holeTypes)}); plugin {plugin}; prompt ~{prompt.Length / 3.6:N0} tokens");
        var baseMessages = new List<ChatMessage> { new("system", SystemPrompt), new("user", prompt) };
        var messages = new List<ChatMessage>(baseMessages);
        var files = new SortedDictionary<string, string>(StringComparer.Ordinal);
        (int Attempt, Verdict? Verdict, SortedDictionary<string, string>? Files) best = (0, null, null);
        var runs = new JsonArray();
        string ended = "attempts", lastFeedback = "";
        string? bestFeedback = null;
        int used = 0, worseStreak = 0, stale = 0, bestHidden = -1;
        var bestPassing = new HashSet<string>(StringComparer.Ordinal);
        var tried = new List<string>();
        bool debugTurn = false;
        int debugReplyTokens = maxTokens;

        List<ChatMessage> Debug(int forAttempt, string status, IReadOnlyCollection<string> passing)
        {
            var b = best.Verdict!;
            bool editsOnly = b.Total > 0 && b.Passed * 2 >= b.Total;
            var text = DebugPrompt(inputs, host, holes, src, Render(files), lastFeedback, status, passing, editsOnly, tried);
            File.WriteAllText(Path.Combine(outDir, $"debug{forAttempt}.prompt.md"), text);
            inputs.CheckLeaks(text, $"{host.Name} host debug prompt", Render(files));
            int size = (int)(text.Length / 3.6);
            debugReplyTokens = Math.Max(maxTokens, context - size - 1024);
            Program.Log($"  debug prompt for attempt {forAttempt}: ~{size:N0} tokens; reply up to {debugReplyTokens:N0}{(editsOnly ? "; edits only" : "")}");
            return [new("system", SystemPrompt), new("user", text)];
        }

        if (warmStart is not null && Directory.Exists(warmStart))
        {
            foreach (var f in Directory.EnumerateFiles(warmStart, "*.java"))
                if (holeTypes.Contains(Path.GetFileNameWithoutExtension(f))) files[Path.GetFileNameWithoutExtension(f)] = ForwardInputs.Norm(File.ReadAllText(f));
            var v0 = Judge(inputs, host, files, Path.Combine(outDir, "attempt0"));
            Program.Log($"warm start from {warmStart}: {Describe(v0)}");
            runs.Add(new JsonObject { ["attempt"] = 0, ["warmStart"] = warmStart, ["verdict"] = VerdictJson(v0) });
            best = (0, v0, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
            bestFeedback = Feedback(v0);
            bestPassing = v0.PublicPassing.ToHashSet(StringComparer.Ordinal);
            lastFeedback = bestFeedback + "\nFix the causes. " + Instructions;
            if (v0.AllPass) ended = "passed";
            else
            {
                debugTurn = debugPrompt && v0.Compiled;
                messages = debugTurn ? Debug(1, $"Your code (from an earlier run) builds; {v0.Passed} of {v0.Total} scenarios pass.", bestPassing)
                    : [.. baseMessages, new("assistant", Render(files)), new("user", lastFeedback)];
            }
        }
        for (int attempt = 1; attempt <= attempts && ended != "passed"; attempt++)
        {
            used = attempt;
            var attemptDir = Path.Combine(outDir, $"attempt{attempt}");
            Directory.CreateDirectory(attemptDir);
            ChatResult r;
            try { r = chat.Complete(messages, debugTurn ? debugReplyTokens : maxTokens, reasoning); }
            catch (HttpRequestException e) when (e.Message.StartsWith("llama-server error:", StringComparison.Ordinal) && e.Message.Contains("\"code\":500"))
            {
                Program.Log($"attempt {attempt}: the server could not read the reply; counted as a failed attempt");
                runs.Add(new JsonObject { ["attempt"] = attempt, ["replyError"] = ForwardJudge.Clip(e.Message, 500) });
                messages = [.. baseMessages, new("assistant", Render(files)), new("user",
                    "Your last reply could not be read: the server rejected its format. Answer again, in the format asked for.\n\n" + (lastFeedback.Length > 0 ? lastFeedback : Instructions))];
                continue;
            }
            Program.Log($"attempt {attempt}: prompt {r.PromptTokens} tok, generated {r.GeneratedTokens} tok @ {r.TokensPerSecond:N1} tok/s, {r.Seconds:N0}s, stop={r.Stop}");
            File.WriteAllText(Path.Combine(attemptDir, "reply.txt"), r.Content);
            if (r.Reasoning is not null) File.WriteAllText(Path.Combine(attemptDir, "reasoning.txt"), r.Reasoning);
            var problems = new List<string>();
            var (edits, rest) = Edits.Extract(r.Content);
            var returned = Parse(rest, holeTypes, problems);
            var editProblems = new List<string>();
            int applied = edits.Count == 0 ? 0 : Edits.Apply(files, edits, editProblems, returned.Keys.ToHashSet(StringComparer.Ordinal));
            foreach (var (k, v) in returned) files[k] = v;
            foreach (var missing in holeTypes.Where(t => !files.ContainsKey(t))) problems.Add($"the hole {missing} is missing");
            var attemptJson = Program.AttemptJson(attempt, r, problems);
            if (debugTurn) attemptJson["debugPrompt"] = true;
            attemptJson["filesReturned"] = Program.Strings(returned.Keys);
            attemptJson["edits"] = new JsonObject { ["blocks"] = edits.Count, ["applied"] = applied, ["notApplied"] = Program.Strings(editProblems) };
            var editNote = editProblems.Count == 0 ? "" : "Some of your edits were not applied (their files stay as they were):\n- " + string.Join("\n- ", editProblems) + "\n\n";
            string message;
            Verdict? verdict = null;
            if (problems.Count > 0 || returned.Count + applied == 0)
            {
                if (problems.Count == 0) problems.Add("no ```java code blocks or applicable edits were found in the reply");
                message = "The files break the contract:\n- " + string.Join("\n- ", problems) + "\n";
                Program.Log($"  contract: {problems.Count} problem(s); first: {ForwardJudge.Clip(problems[0], 200)}");
            }
            else
            {
                var repaired = RepairImports(files, known);
                if (repaired.Count > 0)
                {
                    attemptJson["importsRepaired"] = Program.Strings(repaired);
                    Program.Log($"  {repaired.Count} import(s) repaired");
                }
                verdict = Judge(inputs, host, files, attemptDir);
                attemptJson["verdict"] = VerdictJson(verdict);
                Program.Log("  " + Describe(verdict));
                message = (repaired.Count > 0 ? "Imports corrected for you (your code now has them):\n- " + string.Join("\n- ", repaired) + "\n\n" : "")
                          + Feedback(verdict);
                if (best.Verdict is null || Rank(verdict).CompareTo(Rank(best.Verdict)) > 0)
                    best = (attempt, verdict, new SortedDictionary<string, string>(files, StringComparer.Ordinal));
                if (verdict.AllPass) { runs.Add(attemptJson); ended = "passed"; break; }
                if (verdict.Ran && verdict.PublicPassed == verdict.PublicTotal)
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
            var lost = debugPrompt && verdict is { Ran: true } && best.Attempt != attempt ? bestPassing.Where(s => !verdict.PublicPassing.Contains(s)).ToList() : [];
            if (lost.Count > 0) message += $"\nThese public scenarios passed in your best version and fail now (restore them): {string.Join(", ", lost.Take(20))}.\n";
            runs.Add(attemptJson);
            if (best.Attempt == attempt)
            {
                bestFeedback = editNote + message;
                bestPassing = verdict?.PublicPassing.ToHashSet(StringComparer.Ordinal) ?? [];
                tried.Clear();
                worseStreak = 0;
            }
            else if (best.Verdict is not null)
            {
                tried.Add($"attempt {attempt}: " + (verdict is null ? ForwardJudge.Clip(message.Split('\n')[0], 200)
                    : !verdict.Compiled ? $"it did not build ({verdict.BuildErrors.Count} error(s))"
                    : Describe(verdict) + (lost.Count > 0 ? $"; {lost.Count} public scenario(s) that passed now fail" : "")));
                int cmp = verdict is null ? -1 : Rank(verdict).CompareTo(Rank(best.Verdict));
                worseStreak = cmp < 0 || (cmp == 0 && lost.Count > 0) ? worseStreak + 1 : 0;
            }
            WriteSummary(outDir, modelName, host, best, ended, used, started, runs);
            if (attempt == attempts) break;
            bool rolledBack = false;
            if (debugPrompt && worseStreak >= 2 && best.Files is not null && bestFeedback is not null)
            {
                Program.Log($"  {worseStreak} attempts worse than the best (attempt {best.Attempt}): back to it");
                attemptJson["rolledBackTo"] = best.Attempt;
                files = new SortedDictionary<string, string>(best.Files, StringComparer.Ordinal);
                lastFeedback = $"Your last {worseStreak} attempts did worse than your attempt {best.Attempt}. Here is that code again with its feedback; start from it.\n\n" +
                               bestFeedback + "\nFix the causes. " + Instructions;
                worseStreak = 0;
                rolledBack = true;
            }
            else lastFeedback = editNote + message + "\nFix the causes. " + Instructions;
            debugTurn = debugPrompt && best.Verdict is { Compiled: true };
            if (debugTurn)
            {
                var b = best.Verdict!;
                var status = rolledBack ? $"Your code (your best version) builds; {b.Passed} of {b.Total} scenarios pass."
                    : verdict is not { Compiled: true } ? $"Your current code does not build or breaks the contract (the feedback says why); your best version: {b.Passed} of {b.Total} scenarios pass."
                    : $"Your code builds; {verdict.Passed} of {verdict.Total} scenarios pass.";
                messages = Debug(attempt + 1, status, rolledBack || verdict is not { Ran: true } ? bestPassing : verdict.PublicPassing);
            }
            else messages = [.. baseMessages, new("assistant", Render(files)), new("user", lastFeedback)];
        }
        var deliverable = best.Files ?? files;
        var java = Path.Combine(outDir, "java");
        if (Directory.Exists(java)) Directory.Delete(java, true);
        Directory.CreateDirectory(java);
        foreach (var (k, v) in deliverable) File.WriteAllText(Path.Combine(java, k + ".java"), v);
        WriteSummary(outDir, modelName, host, best, ended, used, started, runs);
        Program.Log($"host {host.Name}: {ended} after {used} attempt(s); best attempt {best.Attempt}: {(best.Verdict is null ? "nothing built" : Describe(best.Verdict))}; holes in {java}");
        return best.Verdict?.AllPass == true ? 0 : 1;
    }

    static void WriteSummary(string outDir, string model, HostSpec host, (int Attempt, Verdict? Verdict, SortedDictionary<string, string>? Files) best, string ended,
        int used, DateTime started, JsonArray runs)
    {
        var o = new JsonObject
        {
            ["schema"] = "mlcpu.forward-host-run.v1", ["model"] = model, ["host"] = host.Name, ["updated"] = DateTime.Now.ToString("O"),
            ["minutes"] = Math.Round((DateTime.Now - started).TotalMinutes, 1), ["attempts"] = used, ["bestAttempt"] = best.Attempt, ["endedBy"] = ended,
            ["best"] = best.Verdict is null ? null : VerdictJson(best.Verdict), ["passed"] = best.Verdict?.AllPass ?? false, ["runs"] = runs.DeepClone()
        };
        File.WriteAllText(Path.Combine(outDir, "summary.json"), o.ToJsonString(Program.Json) + "\n");
    }

    /// <summary>fwd-host-prompt: the first prompt of a host run, written and leak-checked, without a model.</summary>
    public static int PromptOnly(ForwardInputs inputs, string outDir)
    {
        var host = Spec(inputs);
        var (plugin, src) = Paths(host);
        var holes = Holes(plugin);
        var prompt = BuildPrompt(inputs, host, holes, src);
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "host.prompt.md"), prompt);
        inputs.CheckLeaks(prompt, $"{host.Name} host prompt");
        Program.Log($"host prompt: {prompt.Length:N0} characters (~{prompt.Length / 3.6:N0} tokens), {holes.Count} hole(s) -> {Path.Combine(outDir, "host.prompt.md")}");
        return 0;
    }

    /// <summary>fwd-host-judge: a directory of hole files judged as an attempt is (the SME's reference, or a run's deliverable).</summary>
    public static int JudgeOnly(ForwardInputs inputs, string holeDir, string outDir, bool repairImports = false)
    {
        var host = Spec(inputs);
        var (plugin, _) = Paths(host);
        var holes = Holes(plugin);
        var types = holes.Select(h => h.Type).ToHashSet(StringComparer.Ordinal);
        var files = new SortedDictionary<string, string>(Directory.EnumerateFiles(holeDir, "*.java").Where(f => types.Contains(Path.GetFileNameWithoutExtension(f)))
            .ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => ForwardInputs.Norm(File.ReadAllText(f)), StringComparer.Ordinal), StringComparer.Ordinal);
        if (repairImports)
            foreach (var r in RepairImports(files, KnownTypes(inputs, host, PackageOfPath(holes[0].Path)))) Program.Log("  repaired: " + r);
        Directory.CreateDirectory(outDir);
        var v = Judge(inputs, host, files, outDir);
        Program.Log($"judged {files.Count} hole(s) of {holeDir}: {Describe(v)}");
        Console.WriteLine(Feedback(v));
        return v.AllPass ? 0 : 1;
    }
}
