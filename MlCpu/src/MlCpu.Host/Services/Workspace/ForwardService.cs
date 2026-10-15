using System.Text.Json.Nodes;

namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// The forward-engineering results (tools/eval/PROTOCOL-forward.md): the Customer Account Service package in the repository
/// (forward/cas) and the evaluation's output folder (MlCpu:ForwardRoot, default &lt;evalRoot&gt;\forward), whose results.json
/// New-ForwardReport.ps1 writes. Read-only: the runs are made by tools/eval/Invoke-ForwardEval.ps1.
/// </summary>
public sealed class ForwardService(ConfigService config)
{
    public string Root => config.Settings.ForwardRoot;
    public string Package => Path.Combine(config.Settings.RepoRoot, "forward", "cas");

    public bool HasResults() => File.Exists(Path.Combine(Root, "results.json"));

    public JsonObject? Results()
    {
        var f = Path.Combine(Root, "results.json");
        try { return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The explorer: each arm's implementation (code, slices with prompts and attempts) and the designs that were used.</summary>
    public IReadOnlyList<GemmaCodeNode> CodeTree(JsonObject results)
    {
        var nodes = new List<GemmaCodeNode>();
        foreach (var arm in results["arms"]?.AsArray() ?? [])
            if (arm?["folder"]?.GetValue<string>() is { } folder && Directory.Exists(Path.Combine(Root, folder)))
                nodes.Add(Node(Path.Combine(Root, folder), $"Arm {arm["arm"]}, seed {arm["seed"]}: {arm["designer"]} design, {arm["implementer"]} code"));
        foreach (var d in results["designs"]?.AsArray() ?? [])
            if (d?["chosen"]?.GetValue<bool>() == true && d["folder"]?.GetValue<string>() is { } folder && Directory.Exists(Path.Combine(Root, folder)))
                nodes.Add(Node(Path.Combine(Root, folder), $"Design: {d["model"]}, seed {d["seed"]}"));
        return nodes;
    }

    GemmaCodeNode Node(string dir, string label) => Node(Root, "", dir, label);

    /// <summary>
    /// A folder as an explorer node: paths are prefix + the path relative to root ('/' separators); harness folders, build output and
    /// logs are left out, and a chain of single folders (com/mlcpu/cas/...) collapses into one node, as in an IDE.
    /// </summary>
    static GemmaCodeNode Node(string root, string prefix, string dir, string label)
    {
        string Rel(string f) => prefix + Path.GetRelativePath(root, f).Replace('\\', '/');
        var children = Directory.GetDirectories(dir).Where(d => Path.GetFileName(d) is not ("harness" or "build" or ".gradle")).Order(StringComparer.OrdinalIgnoreCase)
            .Select(d => Node(root, prefix, d, Path.GetFileName(d)))
            .Concat(Directory.GetFiles(dir).Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new GemmaCodeNode(Path.GetFileName(f), Rel(f), null)))
            .ToList();
        while (children.Count == 1 && children[0].IsDir && children[0].Children is { } only)
        {
            label += "/" + children[0].Name;
            dir = Path.Combine(root, children[0].Path[prefix.Length..]);
            children = only.ToList();
        }
        return new GemmaCodeNode(label, Rel(dir), children);
    }

    /// <summary>A file of the forward output by its tree path; null when it is missing or lies outside the forward folder.</summary>
    public string? Read(string path) => ReadUnder(Root, path);

    static string? ReadUnder(string rootDir, string path)
    {
        var root = Path.GetFullPath(rootDir) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? File.ReadAllText(full) : null;
    }

    // ---- The loans service (ForwardEngineeringFineract.md): Fineract's loans engine forward-engineered by Gemma from the SME package
    // forward/loans (fixed API, SPEC, RULES, digests, scenarios), judged by architecture tests and 1,489 scenarios; runs in
    // MlCpu:ForwardLoansRoot (default <RepoRoot>\..\modelEval\forward-loans), one folder per run, the whole service in gemma-final.

    /// <summary>The run whose code is the whole passing service (every slice resumed from the run that passed it).</summary>
    public const string LoansFinal = "gemma-final";

    public string LoansRoot => config.Settings.ForwardLoansRoot;
    public string LoansPackage => Path.Combine(config.Settings.RepoRoot, "forward", "loans");

    public bool HasLoans() => File.Exists(Path.Combine(LoansRoot, LoansFinal, "implementation", "summary.json"));

    static JsonObject? ParseFile(string f)
    {
        try { return File.Exists(f) ? JsonNode.Parse(File.ReadAllText(f)) as JsonObject : null; }
        catch (System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The final run's summary: per slice, the judge's counts for every slice built so far (architecture, public, hidden, privileged).</summary>
    public JsonObject? LoansSummary() => ParseFile(Path.Combine(LoansRoot, LoansFinal, "implementation", "summary.json"));

    /// <summary>The package's Phase 0 gate (forward/loans/gate.json): the reference against every suite, mutation per module, leaks, cross-check.</summary>
    public JsonObject? LoansGate() => ParseFile(Path.Combine(LoansPackage, "gate.json"));

    /// <summary>One slice of one run in which the model worked on it (a resumed slice is not listed).</summary>
    public sealed record LoansSliceRun(string Run, string Slice, int Attempts, int BestAttempt, string EndedBy, double Minutes, string Updated,
        int PublicPassed, int PublicTotal, int HiddenPassed, int HiddenTotal);

    /// <summary>Every run's slices the model worked on, oldest first: how each slice got to its pass.</summary>
    public IReadOnlyList<LoansSliceRun> LoansHistory()
    {
        var runs = new List<LoansSliceRun>();
        if (!Directory.Exists(LoansRoot)) return runs;
        foreach (var dir in Directory.GetDirectories(LoansRoot))
        {
            if (ParseFile(Path.Combine(dir, "implementation", "summary.json")) is not { } s) continue;
            var updated = s["updated"]?.ToString() ?? "";
            foreach (var x in s["slices"]?.AsArray().OfType<JsonObject>() ?? [])
            {
                var attempts = x["attempts"]?.GetValue<int>() ?? 0;
                if (attempts == 0) continue;
                var slice = x["slice"]!.ToString();
                var own = x["best"]?["slices"]?[slice];
                runs.Add(new LoansSliceRun(Path.GetFileName(dir), slice, attempts, x["bestAttempt"]?.GetValue<int>() ?? 0, x["endedBy"]?.ToString() ?? "",
                    x["minutes"]?.GetValue<double>() ?? 0, updated, own?["publicPassed"]?.GetValue<int>() ?? 0, own?["publicTotal"]?.GetValue<int>() ?? 0,
                    own?["hiddenPassed"]?.GetValue<int>() ?? 0, own?["hiddenTotal"]?.GetValue<int>() ?? 0));
            }
        }
        return runs.OrderBy(r => r.Updated, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The loans explorer: the results write-up, Gemma's code of the whole service (one folder per module), each run's work on a slice
    /// (prompts, debug prompts, every attempt with its reasoning, its code), and the fixed API Gemma coded against (package/...).
    /// </summary>
    public IReadOnlyList<GemmaCodeNode> LoansCodeTree()
    {
        var nodes = new List<GemmaCodeNode>();
        if (File.Exists(Path.Combine(LoansRoot, "RESULTS.md"))) nodes.Add(new GemmaCodeNode("RESULTS.md", "RESULTS.md", null));
        var final = Path.Combine(LoansRoot, LoansFinal, "implementation", "java");
        if (Directory.Exists(final)) nodes.Add(Node(LoansRoot, "", final, "Gemma's code: the loans service"));
        var history = LoansHistory()
            .Where(r => Directory.Exists(Path.Combine(LoansRoot, r.Run, "implementation", r.Slice)))
            .Select(r => Node(LoansRoot, "", Path.Combine(LoansRoot, r.Run, "implementation", r.Slice),
                $"{r.Slice} in {r.Run} ({r.Attempts} attempt(s), {r.EndedBy})"))
            .ToList();
        if (history.Count > 0) nodes.Add(new GemmaCodeNode("How each slice got there (prompts, attempts, reasoning)", "runs", history));
        var api = Path.Combine(LoansPackage, "service", "loans-api");
        if (Directory.Exists(api)) nodes.Add(Node(LoansPackage, "package/", api, "The fixed API Gemma coded against (loans-api)"));
        return nodes;
    }

    /// <summary>Gemma's code of the whole service: Java files and lines (all, and without blank and comment lines).</summary>
    public (int Files, int Lines, int Code) LoansCodeSize()
    {
        var dir = Path.Combine(LoansRoot, LoansFinal, "implementation", "java");
        if (!Directory.Exists(dir)) return (0, 0, 0);
        int files = 0, lines = 0, code = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories))
        {
            files++;
            var inComment = false;
            foreach (var raw in File.ReadLines(f))
            {
                lines++;
                var t = raw.Trim();
                if (inComment) { inComment = !t.Contains("*/"); continue; }
                if (t.Length == 0 || t.StartsWith("//")) continue;
                if (t.StartsWith("/*")) { inComment = !t.Contains("*/"); continue; }
                code++;
            }
        }
        return (files, lines, code);
    }

    /// <summary>A file of the loans explorer: package/... in the SME package, anything else in the runs folder.</summary>
    public string? ReadLoans(string path) => path.StartsWith("package/", StringComparison.Ordinal)
        ? ReadUnder(LoansPackage, path["package/".Length..])
        : ReadUnder(LoansRoot, path);
}
