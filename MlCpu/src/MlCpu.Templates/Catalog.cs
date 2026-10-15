using System.Text.Json.Nodes;
using YamlDotNet.RepresentationModel;

namespace MlCpu.Templates;

/// <summary>One output of a template: rendered once, or once per item of a model collection that passes a filter.</summary>
public sealed record TemplateOutput(string Path, string Template, string? ForEach, string? Where);

/// <summary>A hole the template leaves for the implementer (GemmaJavaTemplates.md §4.3): the file, the interface, the rules.</summary>
public sealed record HoleSpec(string Id, string Kind, string ForEach, string? Where, string Path, string Implements, string Rules, string Contract);

/// <summary>A template (mlcpu.template-manifest.v1): its outputs, holes, selection predicate and the card Gemma may see.</summary>
public sealed record TemplateManifest(string Id, string Version, string Family, string Tier, int Priority, string Summary, string Card,
    string SelectWhen, IReadOnlyList<TemplateOutput> Outputs, IReadOnlyList<HoleSpec> Holes, string Dir);

/// <summary>
/// The template catalog: templates/&lt;id&gt;/manifest.yaml, and templates/catalog.yaml with the catalog version and the bundles
/// (named sets selected together, such as a host's).
/// </summary>
public sealed class Catalog
{
    public required string Root { get; init; }
    public required string Version { get; init; }
    public required IReadOnlyDictionary<string, TemplateManifest> Templates { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> Bundles { get; init; }

    public static Catalog Load(string root)
    {
        var catalog = Yaml.ToJson(Yaml.Load(Path.Combine(root, "catalog.yaml"))) as JsonObject ?? [];
        var templates = new SortedDictionary<string, TemplateManifest>(StringComparer.Ordinal);
        foreach (var dir in Directory.GetDirectories(root).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(dir, "manifest.yaml");
            if (!File.Exists(file)) continue;
            var m = Yaml.ToJson(Yaml.Load(file)) as JsonObject ?? throw new InvalidDataException($"{file}: not a mapping");
            if ((string?)m["schema"] != "mlcpu.template-manifest.v1") throw new InvalidDataException($"{file}: schema must be mlcpu.template-manifest.v1");
            string S(JsonNode? n, string k, string d = "") => n?[k]?.ToString() ?? d;
            var outputs = (m["outputs"] as JsonArray ?? []).OfType<JsonObject>().Select(o =>
                new TemplateOutput(S(o, "path"), S(o, "template"), o["forEach"]?.ToString(), o["where"]?.ToString())).ToList();
            var holes = (m["holes"] as JsonArray ?? []).OfType<JsonObject>().Select(h =>
                new HoleSpec(S(h, "id"), S(h, "kind", "hook"), S(h, "forEach"), h["where"]?.ToString(), S(h, "path"), S(h, "implements"), S(h, "rules"),
                    S(h, "contract"))).ToList();
            var t = new TemplateManifest(S(m, "id"), S(m, "version"), S(m, "family"), S(m, "tier"), int.Parse(S(m, "priority", "50")), S(m, "summary").Trim(),
                S(m, "card").Trim(), S(m, "selectWhen", "true"), outputs, holes, dir);
            if (t.Id != Path.GetFileName(dir)) throw new InvalidDataException($"{file}: id {t.Id} must equal its folder name");
            templates[t.Id] = t;
        }
        var bundles = new SortedDictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var b in catalog["bundles"] as JsonObject ?? [])
            bundles[b.Key] = (b.Value as JsonArray ?? []).Select(x => x!.ToString()).ToList();
        foreach (var (name, ids) in bundles)
            foreach (var id in ids.Where(id => !templates.ContainsKey(id)))
                throw new InvalidDataException($"bundle {name}: no template {id}");
        return new Catalog { Root = root, Version = catalog["version"]?.ToString() ?? "0", Templates = templates, Bundles = bundles };
    }
}

/// <summary>YAML to JSON nodes (scalars stay strings unless they read as numbers or booleans), so models, profiles and manifests are one shape.</summary>
public static class Yaml
{
    public static YamlNode Load(string file)
    {
        var stream = new YamlStream();
        using var reader = new StreamReader(file);
        stream.Load(reader);
        return stream.Documents.Count == 0 ? new YamlMappingNode() : stream.Documents[0].RootNode;
    }

    public static JsonNode? ToJson(YamlNode node) => node switch
    {
        YamlMappingNode m => new JsonObject(m.Children.Select(kv => KeyValuePair.Create(((YamlScalarNode)kv.Key).Value!, ToJson(kv.Value)))),
        YamlSequenceNode s => new JsonArray(s.Children.Select(ToJson).ToArray()),
        YamlScalarNode v => Scalar(v),
        _ => null
    };

    static JsonNode? Scalar(YamlScalarNode v)
    {
        var text = v.Value;
        if (v.Style is YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted or YamlDotNet.Core.ScalarStyle.Literal
            or YamlDotNet.Core.ScalarStyle.Folded) return JsonValue.Create(text);
        if (text is null or "~" or "null") return null;
        if (text is "true" or "false") return JsonValue.Create(text == "true");
        if (long.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var l)) return JsonValue.Create(l);
        return JsonValue.Create(text);
    }
}
