using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis.Projections;

namespace MlCpu.CobolKb.Knowledge;

/// <summary>One normalized capability as imported into a project.</summary>
/// <param name="Implementation">"normalized" when the promotion policy passed, otherwise "legacy" (the faithful program stays the default).</param>
/// <param name="Kind">"step" (a ModernStep the job engine runs in place of the program) or "subprogram" (a ModernProgram that answers CALLs).</param>
public sealed record CapabilityImport(string ProgramId, string StepClass, string Package, string RequirementsHash, string SourceHash,
    string Implementation, IReadOnlyList<string> Reasons, string Kind = "step");

/// <summary>Thresholds a normalized capability must meet before it becomes the default implementation.</summary>
public sealed record CapabilityPolicy(double MinLineCoverage = 0.90, double MinBranchCoverage = 0.80, double MinMutationScore = 0.80)
{
    public static readonly CapabilityPolicy Default = new();
}

/// <summary>
/// Step 2/3 of normalization: imports authored, idiomatic implementations (capability-manifest.json) whose design answers
/// the program's current requirements packet (requirementsHash), emits normalization/&lt;PID&gt;/ requirements, design and
/// conformance into the project, and decides per program whether the normalized implementation is the default: evidence
/// (equivalence, oracle parity, coverage, mutation) and an SME approval, both pinned to the same requirements and
/// source hashes. Anything stale or missing keeps the faithful program as the default (fail closed).
/// </summary>
public static class NormalizedCapabilities
{
    public const string Pass = "NormalizedCapabilities";
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static (IReadOnlyList<CapabilityImport> Imports, ProjectionLedger Ledger) Import(ProjectFiles files, string? root,
        IReadOnlyDictionary<string, JsonObject> packets, CapabilityPolicy? policy = null)
    {
        policy ??= CapabilityPolicy.Default;
        var ledger = new ProjectionLedger(Pass);
        var imports = new List<CapabilityImport>();
        foreach (var (pid, packet) in packets.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            files.Set($"normalization/{pid}/requirements.json", RequirementsPacketBuilder.ToJson(packet));
        if (packets.Count > 0) files.Set("normalization/design.schema.json", RequirementsPacketBuilder.DesignSchema + "\n");
        var manifestPath = root is null ? null : Path.Combine(root, "capability-manifest.json");
        if (manifestPath is null || !File.Exists(manifestPath))
        {
            ledger.Skip("estate", root is null ? "no capabilities root configured" : $"no capability manifest at {manifestPath}");
            return (imports, ledger);
        }
        var manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
        var shared = manifest["sharedPackages"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();
        var srcMain = manifest["sources"]!["main"]!.GetValue<string>();
        var included = new List<string>(shared);
        var facts = new List<object>();
        foreach (var cap in manifest["capabilities"]!.AsArray().Select(n => n!.AsObject()))
        {
            var pid = cap["programId"]!.GetValue<string>();
            if (!packets.TryGetValue(pid, out var packet)) continue;
            var pkg = cap["package"]!.GetValue<string>();
            var step = cap["step"]!.GetValue<string>();
            var requirementsHash = packet["requirementsHash"]!.GetValue<string>();
            var kind = cap["kind"]?.GetValue<string>() ?? "step";
            var design = Read(root!, cap["design"]?.GetValue<string>());
            var designHash = design?["requirementsHash"]?.GetValue<string>();
            if (design is not null && designHash != requirementsHash)
            {
                ledger.Skip(pid, $"requirements drifted: the design answers {Short(designHash)}, the program now yields {Short(requirementsHash)}; regenerate the design");
                continue;
            }
            if (design is not null) files.Set($"normalization/{pid}/design.json", design.ToJsonString(Indented) + "\n");
            // The model's kit: worked examples traced from the faithful program and the AST outline.
            foreach (var extra in new[] { "examples.md", "ast-outline.txt" })
                if (File.Exists(Path.Combine(root!, "normalization", pid, extra)))
                    files.Set($"normalization/{pid}/{extra}", File.ReadAllText(Path.Combine(root!, "normalization", pid, extra)).Replace("\r\n", "\n"));
            if (Read(root!, cap["conformance"]?.GetValue<string>()) is { } conformance)
                files.Set($"normalization/{pid}/design-conformance.json", conformance.ToJsonString(Indented) + "\n");
            var sourceHash = SourceHash(Path.Combine(root!, srcMain), [.. shared, pkg]);
            var evidence = Read(root!, cap["evidence"]?.GetValue<string>());
            var approval = Read(root!, cap["approval"]?.GetValue<string>());
            var reasons = Decide(policy, requirementsHash, sourceHash, evidence, approval);
            if (design is null) reasons.Insert(0, "no design yet (the class is a placeholder that runs the faithful program)");
            var implementation = reasons.Count == 0 ? "normalized" : "legacy";
            included.Add(pkg);
            imports.Add(new CapabilityImport(pid, step, pkg, requirementsHash, sourceHash, implementation, reasons, kind));
            ledger.Applied.Add(pid);
            facts.Add(new { programId = pid, step, requirementsHash, sourceHash, defaultImplementation = implementation, reasons });
        }
        if (imports.Count > 0)
        {
            files.Set("capability-manifest.json", File.ReadAllText(manifestPath).Replace("\r\n", "\n"));
            if (File.Exists(Path.Combine(root!, "normalization", "parity-notes.md")))
                files.Set("normalization/parity-notes.md", File.ReadAllText(Path.Combine(root!, "normalization", "parity-notes.md")).Replace("\r\n", "\n"));
        }
        if (imports.Count > 0)
            foreach (var kind in new[] { "main", "test", "testResources" })
                if (manifest["sources"]![kind]?.GetValue<string>() is { } rel && Directory.Exists(Path.Combine(root!, rel)))
                    Copy(files, Path.Combine(root!, rel), kind switch { "main" => "src/main/java", "test" => "src/test/java", _ => "src/test/resources" }, included);
        ledger.Facts["capabilitiesRoot"] = root!;
        ledger.Facts["policy"] = policy;
        ledger.Facts["capabilities"] = facts;
        return (imports, ledger);
    }

    /// <summary>The manifest's shared packages (the platform layer the capabilities build on).</summary>
    public static IReadOnlyList<string> SharedPackages(string? root)
    {
        var path = root is null ? null : Path.Combine(root, "capability-manifest.json");
        if (path is null || !File.Exists(path)) return [];
        return JsonNode.Parse(File.ReadAllText(path))!["sharedPackages"]?.AsArray().Select(n => n!.GetValue<string>()).ToList() ?? [];
    }

    /// <summary>Reasons the normalized implementation may not be the default; empty = promote.</summary>
    public static List<string> Decide(CapabilityPolicy policy, string requirementsHash, string sourceHash, JsonNode? evidence, JsonNode? approval)
    {
        var r = new List<string>();
        if (evidence is null) r.Add("no evidence (run tools/Collect-CapabilityEvidence.ps1)");
        else
        {
            if (Str(evidence["requirementsHash"]) != requirementsHash) r.Add("evidence was collected for other requirements");
            if (Str(evidence["sourceHash"]) != sourceHash) r.Add("evidence was collected for other source (the code changed since)");
            var eq = evidence["equivalence"];
            if (Num(eq?["scenarios"]) is not > 0) r.Add("no equivalence scenarios ran");
            else if (Num(eq?["failed"]) is not 0) r.Add($"{Num(eq?["failed"])} equivalence scenario(s) failed");
            if (Str(evidence["oracleParity"]?["verdict"]) != "MATCH") r.Add($"oracle parity is {Str(evidence["oracleParity"]?["verdict"]) ?? "missing"}");
            Check(r, "line coverage", Num(evidence["coverage"]?["line"]), policy.MinLineCoverage);
            Check(r, "branch coverage", Num(evidence["coverage"]?["branch"]), policy.MinBranchCoverage);
            Check(r, "mutation score", Num(evidence["mutation"]?["score"]), policy.MinMutationScore);
        }
        if (approval is null) r.Add("no SME approval");
        else
        {
            if (Str(approval["decision"]) != "approved") r.Add($"SME decision is {Str(approval["decision"]) ?? "missing"}");
            if (Str(approval["requirementsHash"]) != requirementsHash || Str(approval["sourceHash"]) != sourceHash)
                r.Add("the SME approval is for other requirements or source");
        }
        return r;
    }

    static void Check(List<string> r, string what, double? value, double min)
    {
        if (value is null) r.Add($"{what} missing");
        else if (value < min) r.Add($"{what} {value:P1} below {min:P0}");
    }

    /// <summary>SHA-256 over the capability's source files (shared packages plus its own), path-ordered, line endings normalized.</summary>
    public static string SourceHash(string mainRoot, IEnumerable<string> packages)
    {
        var sb = new StringBuilder();
        foreach (var dir in packages.Select(p => Path.Combine(mainRoot, p.Replace('.', Path.DirectorySeparatorChar))).Where(Directory.Exists))
            foreach (var f in Directory.EnumerateFiles(dir, "*.java", SearchOption.AllDirectories)
                         .Select(f => (Rel: Path.GetRelativePath(mainRoot, f).Replace('\\', '/'), Full: f)).OrderBy(f => f.Rel, StringComparer.Ordinal))
                sb.Append(f.Rel).Append('\n').Append(File.ReadAllText(f.Full).Replace("\r\n", "\n")).Append('\n');
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()))).ToLowerInvariant();
    }

    static void Copy(ProjectFiles files, string from, string to, IReadOnlyList<string> packages)
    {
        foreach (var dir in packages.Select(p => p.Replace('.', '/')).Distinct())
        {
            var full = Path.Combine(from, dir.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(full)) continue;
            foreach (var f in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories))
            {
                var rel = to + "/" + Path.GetRelativePath(from, f).Replace('\\', '/');
                if (f.EndsWith(".java", StringComparison.OrdinalIgnoreCase)) files.Set(rel, File.ReadAllText(f).Replace("\r\n", "\n"));
                else files.Set(rel, Convert.ToBase64String(File.ReadAllBytes(f)), "binary");
            }
        }
    }

    static JsonNode? Read(string root, string? rel) =>
        rel is not null && File.Exists(Path.Combine(root, rel)) ? JsonNode.Parse(File.ReadAllText(Path.Combine(root, rel))) : null;

    static string? Str(JsonNode? n) => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
    static double? Num(JsonNode? n) => n is JsonValue v && v.TryGetValue<double>(out var d) ? d : null;
    static string Short(string? h) => h is null ? "(none)" : h[..Math.Min(12, h.Length)];
}
