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

    GemmaCodeNode Node(string dir, string label)
    {
        string Rel(string f) => Path.GetRelativePath(Root, f).Replace('\\', '/');
        var children = Directory.GetDirectories(dir).Where(d => Path.GetFileName(d) != "harness").Order(StringComparer.OrdinalIgnoreCase)
            .Select(d => Node(d, Path.GetFileName(d)))
            .Concat(Directory.GetFiles(dir).Where(f => !f.EndsWith(".log", StringComparison.OrdinalIgnoreCase)).Order(StringComparer.OrdinalIgnoreCase)
                .Select(f => new GemmaCodeNode(Path.GetFileName(f), Rel(f), null)))
            .ToList();
        // A chain of single folders (com/mlcpu/cas/...) collapses into one node, as in an IDE.
        while (children.Count == 1 && children[0].IsDir && children[0].Children is { } only)
        {
            label += "/" + children[0].Name;
            dir = Path.Combine(Root, children[0].Path);
            children = only.ToList();
        }
        return new GemmaCodeNode(label, Rel(dir), children);
    }

    /// <summary>A file of the forward output by its tree path; null when it is missing or lies outside the forward folder.</summary>
    public string? Read(string path)
    {
        var root = Path.GetFullPath(Root) + Path.DirectorySeparatorChar;
        var full = Path.GetFullPath(Path.Combine(root, path));
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(full) ? File.ReadAllText(full) : null;
    }
}
