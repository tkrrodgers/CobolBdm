using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace MlCpu.Templates;

/// <summary>
/// The OFBiz facade digest (ForwardEngineeringFineract.md §7.6), generated from the pinned OFBiz source: for each service the model's
/// facade names, its definition (engine, permission, attributes with type, mode, optional and default, auto-attributes); for each
/// entity, its fields, primary key and relations; then the canonical code snippets the mapping names, cut from the source by an
/// anchor. The same source and model give the same text, so it can sit in a stable prompt prefix.
/// </summary>
public static class OfbizFacade
{
    public static string Digest(string ofbizRoot, JsonObject model)
    {
        var facade = model["facade"] as JsonObject ?? [];
        var wantedServices = (facade["services"] as JsonArray ?? []).Select(x => (string)x!).ToList();
        var wantedEntities = (facade["entities"] as JsonArray ?? []).Select(x => (string)x!).ToList();
        var services = new Dictionary<string, (XElement Def, string File)>(StringComparer.Ordinal);
        var entities = new Dictionary<string, (XElement Def, string File)>(StringComparer.Ordinal);
        foreach (var dir in new[] { "applications", "framework" })
        {
            foreach (var f in Directory.EnumerateFiles(Path.Combine(ofbizRoot, dir), "*.xml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                var rel = Path.GetRelativePath(ofbizRoot, f).Replace('\\', '/');
                if (!rel.Contains("/servicedef/") && !rel.Contains("/entitydef/")) continue;
                XDocument doc;
                try { doc = XDocument.Load(f); } catch (System.Xml.XmlException) { continue; }
                foreach (var s in doc.Root!.Elements("service"))
                    services.TryAdd((string)s.Attribute("name")!, (s, rel));
                foreach (var e in doc.Root!.Elements("entity"))
                    entities.TryAdd((string)e.Attribute("entity-name")!, (e, rel));
            }
        }
        var sb = new StringBuilder("# OFBiz facade: the services and entities the plugin may use\n\n");
        sb.Append("Generated from the pinned OFBiz source. Call services with `dispatcher.runSync(name, context)` and check the result with ")
          .Append("`ServiceUtil.isError`; read entities with `EntityQuery.use(delegator).from(entity).where(...).queryOne()/queryList()`.\n\n## Services\n");
        foreach (var name in wantedServices)
        {
            if (!services.TryGetValue(name, out var s)) { sb.Append($"\n### {name}\n(not found in the source)\n"); continue; }
            var d = s.Def;
            sb.Append($"\n### {name} ({d.Attribute("engine")?.Value}, {s.File})\n");
            var desc = d.Element("description")?.Value.Trim();
            if (!string.IsNullOrEmpty(desc)) sb.Append(Regex.Replace(desc, @"\s+", " ")).Append('\n');
            if (d.Attribute("auth")?.Value is { } auth) sb.Append($"- auth: {auth}\n");
            foreach (var p in d.Elements("permission-service"))
                sb.Append($"- permission: {p.Attribute("service-name")?.Value} {p.Attribute("main-action")?.Value}\n");
            foreach (var aa in d.Elements("auto-attributes"))
                sb.Append($"- auto-attributes: entity {aa.Attribute("entity-name")?.Value ?? d.Attribute("default-entity-name")?.Value}, " +
                          $"include {aa.Attribute("include")?.Value ?? "all"}, mode {aa.Attribute("mode")?.Value}, optional {aa.Attribute("optional")?.Value ?? "false"}" +
                          (aa.Elements("exclude").Any() ? $", exclude {string.Join(" ", aa.Elements("exclude").Select(x => x.Attribute("field-name")?.Value))}" : "") + "\n");
            foreach (var at in d.Elements("attribute"))
                sb.Append($"- {at.Attribute("mode")?.Value} {at.Attribute("name")?.Value}: {at.Attribute("type")?.Value}" +
                          (at.Attribute("optional")?.Value == "true" ? ", optional" : "") +
                          (at.Attribute("default-value")?.Value is { } dv ? $", default {dv}" : "") + "\n");
            foreach (var o in d.Elements("override"))
                sb.Append($"- override {o.Attribute("name")?.Value}:" + string.Concat(o.Attributes().Where(x => x.Name != "name").Select(x => $" {x.Name}={x.Value}")) + "\n");
        }
        sb.Append("\n## Entities\n");
        foreach (var name in wantedEntities)
        {
            if (!entities.TryGetValue(name, out var e))
            {
                // the plugin's own entity (generated from the service model), else unknown
                var own = (model["entities"] as JsonArray ?? []).FirstOrDefault(x => (string?)x!["name"] == name);
                if (own is null) { sb.Append($"\n### {name}\n(not found in the source)\n"); continue; }
                sb.Append($"\n### {name} (the plugin's own entity: {(string?)own["title"]})\n");
                sb.Append("- fields: ").Append(string.Join(", ", (own["columns"] as JsonArray ?? []).Select(c => $"{(string?)c!["name"]} ({(string?)c["type"]})"))).Append('\n');
                sb.Append("- primary key: ").Append(string.Join(", ", (own["pk"] as JsonArray ?? []).Select(p => (string?)p))).Append('\n');
                foreach (var r in own["relations"] as JsonArray ?? [])
                    sb.Append($"- relation {(string?)r!["type"]} {(string?)r["title"]}{(string?)r["entity"]}: " +
                              string.Join(", ", (r["fields"] as JsonArray ?? []).Select(f => $"{(string?)f}={(string?)r["remote"] ?? (string?)f}")) + "\n");
                continue;
            }
            var d = e.Def;
            sb.Append($"\n### {name} ({e.File})\n");
            sb.Append("- fields: ").Append(string.Join(", ", d.Elements("field").Select(f => $"{f.Attribute("name")?.Value} ({f.Attribute("type")?.Value})"))).Append('\n');
            sb.Append("- primary key: ").Append(string.Join(", ", d.Elements("prim-key").Select(f => f.Attribute("field")?.Value))).Append('\n');
            foreach (var r in d.Elements("relation"))
                sb.Append($"- relation {r.Attribute("type")?.Value} {r.Attribute("title")?.Value}{r.Attribute("rel-entity-name")?.Value}: " +
                          string.Join(", ", r.Elements("key-map").Select(k => k.Attribute("field-name")?.Value + (k.Attribute("rel-field-name")?.Value is { } rf ? $"={rf}" : "")))
                          + "\n");
        }
        var classes = (facade["classes"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (classes.Count > 0)
            sb.Append("\n## Java classes: their imports and the methods the plugin may use\n");
        foreach (var c in classes)
        {
            var fqn = (string)c["class"]!;
            var suffix = fqn.Replace('.', '/') + ".java";
            var source = new[] { "framework", "applications" }.SelectMany(d => Directory.EnumerateFiles(Path.Combine(ofbizRoot, d), Path.GetFileName(suffix),
                SearchOption.AllDirectories)).Order(StringComparer.Ordinal).FirstOrDefault(f => f.Replace('\\', '/').EndsWith("/src/main/java/" + suffix, StringComparison.Ordinal));
            sb.Append($"\n### {fqn[(fqn.LastIndexOf('.') + 1)..]}\n`import {fqn};`");
            if (source is null) { sb.Append(" (not found in the source)\n"); continue; }
            sb.Append($" ({Path.GetRelativePath(ofbizRoot, source).Replace('\\', '/')})\n");
            foreach (var m in (c["methods"] as JsonArray ?? []).Select(x => (string)x!))
            {
                // the class's own declarations, else its superclasses' (GenericValue's getString is GenericEntity's)
                var found = new List<string>();
                string? from = null;
                for (var (file, depth) = (source, 0); file is not null && found.Count == 0 && depth < 4; depth++)
                {
                    found = Signatures(File.ReadAllText(file), m);
                    if (found.Count > 0) { from = file == source ? null : Path.GetFileNameWithoutExtension(file); break; }
                    file = Superclass(file);
                }
                if (found.Count == 0) sb.Append($"- {m}: (not found)\n");
                foreach (var s in found) sb.Append($"- `{s}`{(from is null ? "" : $" (inherited from {from})")}\n");
            }
        }
        var snippets = (facade["snippets"] as JsonArray ?? []).OfType<JsonObject>().ToList();
        if (snippets.Count > 0) sb.Append("\n## Canonical code from OFBiz\n");
        foreach (var sn in snippets)
        {
            var file = (string)sn["file"]!;
            var lines = File.ReadAllLines(Path.Combine(ofbizRoot, file));
            var anchor = new Regex((string)sn["from"]!);
            var start = Array.FindIndex(lines, l => anchor.IsMatch(l));
            if (start < 0) { sb.Append($"\n### {sn["title"]}\n({file}: anchor not found)\n"); continue; }
            var count = sn["lines"]?.GetValue<int>() ?? 30;
            var ext = Path.GetExtension(file).TrimStart('.');
            sb.Append($"\n### {sn["title"]} ({file}:{start + 1})\n```{ext}\n");
            foreach (var l in lines.Skip(start).Take(count)) sb.Append(l.TrimEnd()).Append('\n');
            sb.Append("```\n");
        }
        return sb.ToString();
    }

    static readonly Regex Comments = new(@"/\*.*?\*/|//[^\n]*", RegexOptions.Singleline);

    /// <summary>The source of a class's superclass: in its own package, else by its import; null for none or outside the source.</summary>
    static string? Superclass(string file)
    {
        var text = Comments.Replace(File.ReadAllText(file), " ");
        var ext = Regex.Match(text, @"\bclass\s+\w+(?:<[^{]*?>)?\s+extends\s+([\w.]+)");
        if (!ext.Success) return null;
        var name = ext.Groups[1].Value;
        var sameDir = Path.Combine(Path.GetDirectoryName(file)!, name + ".java");
        if (File.Exists(sameDir)) return sameDir;
        var import = Regex.Match(text, $@"import\s+([\w.]+\.{Regex.Escape(name)})\s*;");
        if (!import.Success) return null;
        var marker = "/src/main/java/";
        var norm = file.Replace('\\', '/');
        var root = norm[..(norm.IndexOf(marker, StringComparison.Ordinal))];
        return Directory.EnumerateFiles(Path.GetFullPath(Path.Combine(root, "..", "..")), name + ".java", SearchOption.AllDirectories)
            .FirstOrDefault(f => f.Replace('\\', '/').EndsWith(marker + import.Groups[1].Value.Replace('.', '/') + ".java", StringComparison.Ordinal));
    }

    /// <summary>
    /// The public declarations of a method in a Java source (every overload), as one line each: modifiers, type parameters, return
    /// type, name and parameters, without annotations, bodies or throws clauses. Interface methods count as public.
    /// </summary>
    static List<string> Signatures(string source, string method)
    {
        var text = Comments.Replace(source, " ");
        var isInterface = Regex.IsMatch(text, @"\bpublic\s+interface\s+\w+");
        var found = new List<string>();
        var head = new Regex(@"(?m)^[ \t]*((?:public|protected|private|static|final|default|synchronized|abstract|\s)*)\s*(<[^>]+>\s+)?([\w.<>\[\],?\s]+?)\s+" +
                             Regex.Escape(method) + @"\s*\(");
        foreach (Match m in head.Matches(text))
        {
            var modifiers = Regex.Replace(m.Groups[1].Value, @"\s+", " ").Trim();
            if (modifiers.Contains("private") || modifiers.Contains("protected")) continue;
            if (!modifiers.Contains("public") && !isInterface) continue;
            if (Regex.IsMatch(m.Groups[3].Value, @"\b(return|new|throw|else)\b")) continue;
            int depth = 0, i = m.Index + m.Length - 1, end = -1;
            for (; i < text.Length; i++)
            {
                if (text[i] == '(') depth++;
                else if (text[i] == ')' && --depth == 0) { end = i; break; }
            }
            if (end < 0) continue;
            var parameters = Regex.Replace(text[(m.Index + m.Length)..end], @"\s+", " ").Trim();
            var sig = $"{(modifiers.Length > 0 ? modifiers + " " : "")}{m.Groups[2].Value.Trim()}{(m.Groups[2].Success ? " " : "")}{Regex.Replace(m.Groups[3].Value, @"\s+", " ").Trim()} {method}({parameters})";
            if (!found.Contains(sig)) found.Add(sig);
        }
        return found;
    }
}
