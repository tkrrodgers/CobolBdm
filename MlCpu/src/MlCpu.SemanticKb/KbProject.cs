using System.Text.Json.Nodes;

namespace MlCpu.SemanticKb;

/// <summary>
/// The pipeline outputs of one onboarded CobolBdm project (projects/&lt;id&gt;/), read-only. Only project.json is required;
/// every other input is optional and harvested when present.
/// </summary>
public sealed class KbProject
{
    public string Id { get; }
    public string Dir { get; }
    public string SourceDir { get; }
    public IReadOnlyList<KbProgram> Programs { get; }
    public IReadOnlyList<string> CopybookRoots { get; }
    /// <summary>The published estate (publish/&lt;id&gt;-normalized-spring), or null when the project is not published.</summary>
    public string? EstateDir { get; }

    public string KnowledgeDir => System.IO.Path.Combine(Dir, "knowledge");

    KbProject(string id, string dir, string sourceDir, IReadOnlyList<KbProgram> programs, IReadOnlyList<string> copybookRoots, string? estateDir)
    {
        Id = id; Dir = dir; SourceDir = sourceDir; Programs = programs; CopybookRoots = copybookRoots; EstateDir = estateDir;
    }

    public static KbProject Load(string workspaceRoot, string id)
    {
        var dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(workspaceRoot, id));
        var manifest = System.IO.Path.Combine(dir, "project.json");
        if (!File.Exists(manifest)) throw new FileNotFoundException($"project {id}: no project.json in {dir}");
        var json = JsonNode.Parse(File.ReadAllText(manifest))!;
        var layout = json["layout"];
        var sourceDir = System.IO.Path.Combine(dir, layout?["sourceDir"]?.GetValue<string>() ?? "source");
        var programs = (json["programs"]?.AsArray() ?? [])
            .Select(p => new KbProgram(
                p!["id"]!.GetValue<string>(),
                p["path"]?.GetValue<string>() ?? "",
                p["kind"]?.GetValue<string>() ?? "",
                p["lines"]?.GetValue<int>() ?? 0,
                Strings(p["calledBy"]),
                Strings(p["jobs"])))
            .OrderBy(p => p.Id, StringComparer.Ordinal)
            .ToList();
        var copybooks = Strings(layout?["copybookRoots"]);
        var publish = System.IO.Path.Combine(dir, "publish");
        string? estate = null;
        if (Directory.Exists(publish))
            estate = Directory.GetDirectories(publish)
                .OrderBy(d => System.IO.Path.GetFileName(d).Equals($"{id}-normalized-spring", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenBy(d => d, StringComparer.Ordinal)
                .FirstOrDefault(d => File.Exists(System.IO.Path.Combine(d, "capability-manifest.json")));
        return new KbProject(id, dir, sourceDir, programs, copybooks, estate);
    }

    static List<string> Strings(JsonNode? n) => (n?.AsArray() ?? []).Select(x => x!.GetValue<string>()).ToList();

    /// <summary>Path relative to the project directory, with forward slashes (the citation form).</summary>
    public string Relative(string path) => System.IO.Path.GetRelativePath(Dir, path).Replace('\\', '/');

    public string Absolute(string relative) => System.IO.Path.GetFullPath(System.IO.Path.Combine(Dir, relative));

    public string SourcePath(KbProgram p) => System.IO.Path.Combine(SourceDir, p.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string? Requirements(string programId) => Existing(EstateDir, "normalization", programId, "requirements.json");

    public string? Examples(string programId) => Existing(EstateDir, "normalization", programId, "examples.md");

    public string? AstOutline(string programId) => Existing(Dir, "graphs", "ast", programId + ".outline.txt");

    public string? CallGraph() => Existing(Dir, "graphs", "callgraph.json");

    public string? GemmaDesign(string programId) => Existing(Dir, "gemma", programId, "design.json");

    public string? GemmaJavaDir(string programId)
    {
        var d = System.IO.Path.Combine(Dir, "gemma", programId, "java");
        return Directory.Exists(d) ? d : null;
    }

    public string? CapabilityManifest() => Existing(EstateDir, "capability-manifest.json");

    /// <summary>The faithful Java of a program: the published estate's legacy class, else the transpiled one.</summary>
    public string? FaithfulJava(string programId)
    {
        if (EstateDir is not null)
        {
            var main = System.IO.Path.Combine(EstateDir, "src", "main", "java");
            if (Directory.Exists(main))
            {
                var legacy = Directory.EnumerateFiles(main, $"Prog_{programId}.java", SearchOption.AllDirectories)
                    .OrderBy(f => f.Contains($"{System.IO.Path.DirectorySeparatorChar}legacy{System.IO.Path.DirectorySeparatorChar}") ? 0 : 1)
                    .ThenBy(f => f, StringComparer.Ordinal)
                    .FirstOrDefault(f => File.ReadAllText(f).Contains("// PARAGRAPH "));
                if (legacy is not null) return legacy;
            }
        }
        return Existing(Dir, "transpile", "java", $"Prog_{programId}.java");
    }

    public string? Copybook(string name)
    {
        foreach (var root in CopybookRoots)
        {
            var d = System.IO.Path.Combine(SourceDir, root.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!Directory.Exists(d)) continue;
            var hit = Directory.EnumerateFiles(d)
                .FirstOrDefault(f => System.IO.Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
            if (hit is not null) return hit;
        }
        return null;
    }

    static string? Existing(string? root, params string[] parts)
    {
        if (root is null) return null;
        var p = System.IO.Path.Combine([root, .. parts]);
        return File.Exists(p) ? p : null;
    }
}

public sealed record KbProgram(string Id, string Path, string Kind, int Lines, IReadOnlyList<string> CalledBy, IReadOnlyList<string> Jobs);
