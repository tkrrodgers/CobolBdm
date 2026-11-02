using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace MlCpu.Modernize;

/// <summary>
/// The estate's deployment profile: the optional <c>deploymentProfile</c> section of the project manifest
/// (project.json, schema mlcpu.project.v1), declared by the estate's owner and never inferred. Its only field is
/// <c>closedWorld</c> (a JSON boolean): true claims that no class of the estate is subclassed or proxied outside it,
/// which authorizes R3 final-classes when that rule is requested. Any other shape (not an object, another field, a
/// non-boolean value) fails the lift; an estate without the section (or a source tree without a manifest) has none.
/// </summary>
public sealed record DeploymentProfile(string Estate, string Manifest, string ManifestSha256, string Section, string SectionSha256, bool ClosedWorld)
{
    public const string Key = "deploymentProfile";
    public const string ClosedWorldKey = "closedWorld";
    public const string ScopeClaim = "closedWorld: the estate's owner declares that no class of this estate is subclassed or proxied outside the estate (callers, reflection and providers outside it may still exist); per-class keeps (subclasses, reflection/provider, proxy calls, model edits, unknown hierarchy) still apply";

    /// <summary>The profile of <paramref name="project"/> (a project directory), or null when it declares none.</summary>
    public static DeploymentProfile? Load(string project)
    {
        var path = Path.Combine(project, "project.json");
        if (!File.Exists(path)) return null;
        var bytes = File.ReadAllBytes(path);
        JsonNode? root;
        try { root = JsonNode.Parse(File.ReadAllText(path)); }
        catch (System.Text.Json.JsonException e) { throw new InvalidDataException($"{path}: not valid JSON ({e.Message})"); }
        if (root is not JsonObject obj) throw new InvalidDataException($"{path}: the manifest is not a JSON object");
        if (!obj.TryGetPropertyValue(Key, out var section)) return null;
        var where = $"{path}: {Key}";
        if (section is not JsonObject sec) throw new InvalidDataException($"{where} must be an object {{ \"{ClosedWorldKey}\": true|false }}");
        foreach (var (k, _) in sec)
            if (k != ClosedWorldKey) throw new InvalidDataException($"{where}: unknown field \"{k}\" (known: {ClosedWorldKey})");
        if (!sec.TryGetPropertyValue(ClosedWorldKey, out var cw) || cw is not JsonValue v || v.GetValueKind() is not (System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False))
            throw new InvalidDataException($"{where}.{ClosedWorldKey} must be a JSON boolean (found {(cw is null ? "nothing" : cw.ToJsonString())})");
        var text = sec.ToJsonString();
        return new DeploymentProfile(obj["id"]?.GetValue<string>() ?? Path.GetFileName(Path.GetFullPath(project)), "project.json", Sha(bytes),
            text, Sha(Encoding.UTF8.GetBytes(text)), v.GetValue<bool>());
    }

    static string Sha(byte[] b) => Convert.ToHexString(SHA256.HashData(b)).ToLowerInvariant();

    /// <summary>The record written to modernize/deployment-profile.json and quoted in the R3 ledger.</summary>
    public object Record(IEnumerable<string> appliedRules) => new
    {
        estate = Estate,
        manifest = Manifest + "#" + Key,
        manifestSha256 = ManifestSha256,
        section = Section,
        sectionSha256 = SectionSha256,
        closedWorld = ClosedWorld,
        scopeClaim = ScopeClaim,
        authorizes = ClosedWorld ? new[] { FinalClasses.RuleName + " (only when requested with --rule " + FinalClasses.RuleName + ")" } : [],
        appliedRules = appliedRules.Order(StringComparer.Ordinal).ToList(),
    };
}
