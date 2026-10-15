using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Scriban;
using Scriban.Runtime;

namespace MlCpu.Templates;

/// <summary>A rendered file: its path under the output root, the template that made it, and its content's SHA-256.</summary>
public sealed record GeneratedFile(string Path, string Template, string Version, string Sha256);

/// <summary>
/// Renders the selected templates over the service model and the environment profile with Scriban in strict mode (a variable the
/// model or the profile lacks is an error, never an empty string), then normalizes each file (LF, no trailing blanks, one blank
/// line at most in a row, one final newline) and stamps it with a do-not-edit header. Rendering the same model, profile and catalog
/// twice gives the same bytes; generated.lock.json records each file's hash and holes.json the places left for the implementer.
/// </summary>
public sealed class Renderer(Catalog catalog, JsonObject model, JsonObject profile)
{
    public static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Sha256(string text) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    public static string Canonical(JsonNode node) => node.ToJsonString(Indented).Replace("\r\n", "\n") + "\n";

    readonly string modelHash = Sha256(Canonical(model));
    readonly string profileHash = Sha256(Canonical(profile));

    /// <summary>The rule selector (arm RL): each template whose selectWhen predicate holds for this model and profile.</summary>
    public List<(TemplateManifest Template, bool Selected)> Select(IEnumerable<string> ids) =>
        ids.Select(id => catalog.Templates[id]).OrderBy(t => t.Priority).ThenBy(t => t.Id, StringComparer.Ordinal)
            .Select(t => (t, Truthy(Evaluate(t.SelectWhen, null, t)))).ToList();

    public (List<GeneratedFile> Files, JsonArray Holes, JsonArray Decisions) Render(IEnumerable<string> ids, string outDir)
    {
        var files = new List<GeneratedFile>();
        var holes = new JsonArray();
        var decisions = new JsonArray();
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (t, selected) in Select(ids))
        {
            decisions.Add(new JsonObject { ["template"] = $"{t.Id}@{t.Version}", ["selectWhen"] = t.SelectWhen, ["selected"] = selected });
            if (!selected) continue;
            foreach (var o in t.Outputs)
            {
                foreach (var item in Items(o.ForEach, o.Where, t))
                {
                    var rel = RenderText(o.Path, item, t, $"{t.Id} path").Trim().Replace('\\', '/');
                    if (!paths.Add(rel)) throw new InvalidOperationException($"{t.Id}: {rel} is generated twice");
                    var source = File.ReadAllText(Path.Combine(t.Dir, o.Template));
                    var text = Stamp(Normalize(RenderText(source, item, t, $"{t.Id}/{o.Template}")), rel, t);
                    var full = Path.Combine(outDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    File.WriteAllText(full, text, new UTF8Encoding(false));
                    files.Add(new GeneratedFile(rel, t.Id, t.Version, Sha256(text)));
                }
            }
            foreach (var h in t.Holes)
                foreach (var item in Items(h.ForEach.Length == 0 ? null : h.ForEach, h.Where, t))
                    holes.Add(new JsonObject
                    {
                        ["id"] = RenderText(h.Id, item, t, "hole id").Trim(), ["kind"] = h.Kind, ["template"] = t.Id,
                        ["path"] = RenderText(h.Path, item, t, "hole path").Trim(), ["implements"] = RenderText(h.Implements, item, t, "hole interface").Trim(),
                        ["rules"] = RenderText(h.Rules, item, t, "hole rules").Trim(), ["contract"] = Normalize(RenderText(h.Contract, item, t, "hole contract")).Trim()
                    });
        }
        files.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (files, holes, decisions);
    }

    public JsonObject Lock(IReadOnlyList<GeneratedFile> files) => new()
    {
        ["schema"] = "mlcpu.generated-lock.v1", ["catalog"] = catalog.Version, ["model"] = "sha256:" + modelHash, ["profile"] = "sha256:" + profileHash,
        ["files"] = new JsonArray(files.Select(f => (JsonNode)new JsonObject
        {
            ["path"] = f.Path, ["template"] = $"{f.Template}@{f.Version}", ["sha256"] = f.Sha256
        }).ToArray())
    };

    IEnumerable<ScriptObject?> Items(string? forEach, string? where, TemplateManifest t)
    {
        if (forEach is null) { yield return null; yield break; }
        var collection = Evaluate(forEach, null, t);
        if (collection is not ScriptArray array) throw new InvalidOperationException($"{t.Id}: forEach {forEach} is not a list");
        foreach (var x in array)
        {
            var item = x as ScriptObject ?? throw new InvalidOperationException($"{t.Id}: forEach {forEach} holds a non-object");
            if (where is null || Truthy(Evaluate(where, item, t))) yield return item;
        }
    }

    static bool Truthy(object? v) => v switch { null => false, bool b => b, string s => s.Length > 0, _ => true };

    TemplateContext Context(ScriptObject? item, TemplateManifest t)
    {
        var globals = new ScriptObject();
        globals.SetValue("model", ToScript(model), true);
        globals.SetValue("profile", ToScript(profile), true);
        globals.SetValue("item", item, true);
        globals.SetValue("template", ToScript(new JsonObject { ["id"] = t.Id, ["version"] = t.Version }), true);
        globals.SetValue("x", Helpers.Create(), true);
        var context = new TemplateContext { StrictVariables = true, LoopLimit = 100_000, MemberRenamer = m => m.Name, EnableRelaxedMemberAccess = false };
        context.PushGlobal(globals);
        return context;
    }

    object? Evaluate(string expression, ScriptObject? item, TemplateManifest t)
    {
        var template = Template.Parse("{{ " + expression + " }}");
        if (template.HasErrors) throw new InvalidOperationException($"{t.Id}: {expression}: {string.Join("; ", template.Messages)}");
        return template.Evaluate(Context(item, t));
    }

    string RenderText(string source, ScriptObject? item, TemplateManifest t, string name)
    {
        var template = Template.Parse(source, name);
        if (template.HasErrors) throw new InvalidOperationException($"{name}: {string.Join("; ", template.Messages)}");
        try { return template.Render(Context(item, t)); }
        catch (Scriban.Syntax.ScriptRuntimeException e) { throw new InvalidOperationException($"{name}: {e.Message}", e); }
    }

    static string Normalize(string text)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n').Select(l => l.TrimEnd()).ToList();
        var sb = new StringBuilder();
        bool blank = false;
        foreach (var l in lines)
        {
            if (l.Length == 0) { if (!blank && sb.Length > 0) sb.Append('\n'); blank = true; continue; }
            blank = false;
            sb.Append(l).Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    string Stamp(string text, string path, TemplateManifest t)
    {
        var note = $"GENERATED by mlcpu-templates {t.Id}@{t.Version} from model sha256:{modelHash[..12]}. DO NOT EDIT: change the model or the template.";
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".java" or ".gradle" or ".groovy") return $"// {note}\n{text}";
        if (ext == ".xml")
        {
            if (text.StartsWith("<?xml", StringComparison.Ordinal))
            {
                var end = text.IndexOf('\n') + 1;
                return text[..end] + $"<!-- {note} -->\n" + text[end..];
            }
            return $"<!-- {note} -->\n{text}";
        }
        return text;
    }

    /// <summary>JSON to Scriban values: objects keep their keys as they are, numbers become long or double.</summary>
    public static object? ToScript(JsonNode? node)
    {
        switch (node)
        {
            case null: return null;
            case JsonObject o:
            {
                var so = new ScriptObject();
                foreach (var (k, v) in o) so.SetValue(k, ToScript(v), false);
                return so;
            }
            case JsonArray a:
            {
                var sa = new ScriptArray();
                foreach (var v in a) sa.Add(ToScript(v));
                return sa;
            }
            case JsonValue v:
                if (v.TryGetValue<bool>(out var b)) return b;
                if (v.TryGetValue<long>(out var l)) return l;
                if (v.TryGetValue<double>(out var d)) return d;
                if (v.TryGetValue<string>(out var s)) return s;
                return v.ToString();
            default: return node.ToString();
        }
    }
}

/// <summary>The helpers templates call as x.&lt;name&gt;: names, escaping and the copybook-to-host type rules that every template shares.</summary>
public static class Helpers
{
    public static ScriptObject Create()
    {
        var x = new ScriptObject();
        x.Import("pascal", new Func<string, string>(s => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..]));
        x.Import("camel", new Func<string, string>(s => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..]));
        x.Import("upper_snake", new Func<string, string>(ServiceModelBuilder.UpperSnake));
        x.Import("xml", new Func<object?, string>(s => System.Security.SecurityElement.Escape(s?.ToString() ?? "") ?? ""));
        x.Import("java_string", new Func<object?, string>(s => "\"" + (s?.ToString() ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""));
        x.Import("package_path", new Func<string, string>(s => s.Replace('.', '/')));
        x.Import("simple", new Func<string, string>(s => s[(s.LastIndexOf('.') + 1)..]));
        x.Import("json", new Func<object?, string>(o => JsonSerializer.Serialize(Plain(o))));
        // Words wrapped into lines of at most n characters (for comments under a line-length rule).
        x.Import("wrap", new Func<object?, int, ScriptArray>((text, n) =>
        {
            var lines = new ScriptArray();
            var line = new StringBuilder();
            foreach (var w in (text?.ToString() ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Length > 0 && line.Length + 1 + w.Length > n) { lines.Add(line.ToString()); line.Clear(); }
                if (line.Length > 0) line.Append(' ');
                line.Append(w);
            }
            if (line.Length > 0) lines.Add(line.ToString());
            return lines;
        }));
        // A database name cut to n characters, deterministically: the start, then a 4-character hash of the whole.
        x.Import("short", new Func<string, int, string>((s, n) => s.Length <= n ? s
            : s[..(n - 5)] + "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))[..4]));
        return x;
    }

    /// <summary>A Scriban value as plain .NET values, for json.</summary>
    static object? Plain(object? o) => o switch
    {
        ScriptObject so => so.ToDictionary(kv => kv.Key, kv => Plain(kv.Value)),
        ScriptArray sa => sa.Select(Plain).ToList(),
        _ => o
    };
}
