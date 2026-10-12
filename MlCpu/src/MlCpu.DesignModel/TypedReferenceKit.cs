using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Kit feature typed-reference (docs/PIPELINE-OUTPUT.md, Phase B): normalization/PID/typed-reference/ holds the typed-interface
/// projection of the program (record views, verified operations, Storage, the logic and the entry class) and
/// typed-reference.md its prompt section. It is used only when verified.txt records that it passed the capability's own
/// acceptance test. The views, operations and Storage are installed in the harness's package as given classes; the logic
/// and the entry class are the model's starting files; the prompt shows them instead of the COBOL outline.
/// </summary>
public sealed class TypedReferenceKit
{
    public required string Prompt { get; init; }
    public required IReadOnlyDictionary<string, string> Given { get; init; }
    public required IReadOnlyDictionary<string, string> Starting { get; init; }
    public required string Verified { get; init; }

    public static TypedReferenceKit? Load(string packetPath, KitFeatures features)
    {
        if (!features.Has(KitFeatures.TypedReference)) return null;
        var norm = Path.GetDirectoryName(Path.GetFullPath(packetPath))!;
        var dir = Path.Combine(norm, "typed-reference");
        var md = Path.Combine(norm, "typed-reference.md");
        if (!Directory.Exists(dir) || !File.Exists(md)) return null;
        var verified = Path.Combine(dir, "verified.txt");
        var verdict = File.Exists(verified) ? File.ReadAllText(verified).Trim() : "";
        if (Regex.Match(verdict, @"^acceptance (\d+)/(\d+)") is not { Success: true } v || v.Groups[1].Value != v.Groups[2].Value)
        {
            Program.Log($"  typed reference not used: it has not passed its acceptance test ({(verdict.Length == 0 ? "no verified.txt" : verdict)})");
            return null;
        }
        var starting = File.Exists(Path.Combine(dir, "starting.txt"))
            ? File.ReadAllLines(Path.Combine(dir, "starting.txt")).Select(l => l.Trim()).Where(l => l.Length > 0).ToHashSet(StringComparer.Ordinal) : [];
        var java = Directory.EnumerateFiles(dir, "*.java").ToDictionary(f => Path.GetFileNameWithoutExtension(f), f => File.ReadAllText(f).Replace("\r\n", "\n"), StringComparer.Ordinal);
        return new TypedReferenceKit
        {
            Prompt = File.ReadAllText(md).Replace("\r\n", "\n"), Verified = verdict,
            Starting = java.Where(kv => starting.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal),
            Given = java.Where(kv => !starting.Contains(kv.Key)).ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal)
        };
    }

    /// <summary>The given classes into the harness package (before the reserved names are taken).</summary>
    public void Install(string harnessPackageDir)
    {
        foreach (var (name, src) in Given) File.WriteAllText(Path.Combine(harnessPackageDir, name + ".java"), src);
    }
}
