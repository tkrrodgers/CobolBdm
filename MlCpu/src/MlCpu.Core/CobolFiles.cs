namespace MlCpu.Core;

/// <summary>Estate file discovery (§5.1). Sorted case-insensitively; classified-out programs are reported, never dropped silently.</summary>
public static class CobolFiles
{
    public static readonly string[] ProgramExtensions = [".cbl", ".cob", ".cobol", ".ccp"];
    public static readonly string[] CopybookExtensions = [".cpy", ".cpb", ".copy"];
    public static readonly string[] JclExtensions = [".jcl", ".job", ".jobs", ".proc", ".prc"];
    public static readonly string[] ControlCardExtensions = [".ctl"];

    public static IReadOnlyList<string> ProgramCandidates(string root) => Enumerate(root, ProgramExtensions);

    public static IReadOnlyList<string> Programs(string root) =>
        ProgramCandidates(root).Where(CobolSourceClassifier.IsCobol).ToList();

    public static IReadOnlyList<string> ClassifiedOutPrograms(string root) =>
        ProgramCandidates(root).Where(p => !CobolSourceClassifier.IsCobol(p)).ToList();

    public static IReadOnlyList<string> Copybooks(string root) => Enumerate(root, CopybookExtensions);

    public static IReadOnlyList<string> Jcl(string root) => Enumerate(root, JclExtensions);

    public static IReadOnlyList<string> ControlCards(string root) => Enumerate(root, ControlCardExtensions);

    public static bool IsProgram(string path) =>
        HasExtension(path, ProgramExtensions) && !IsGenerated(path) && CobolSourceClassifier.IsCobol(path);

    static bool IsGenerated(string path)
    {
        var name = Path.GetFileName(path);
        return name.EndsWith(".expanded.CBL", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith(".preprocessed.CBL", StringComparison.OrdinalIgnoreCase);
    }

    static bool HasExtension(string path, string[] exts) =>
        exts.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    static IReadOnlyList<string> Enumerate(string root, string[] exts)
    {
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(p => HasExtension(p, exts) && !IsGenerated(p))
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
