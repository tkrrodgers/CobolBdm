using System.Text.Json.Nodes;

namespace MlCpu.Templates;

/// <summary>
/// The legacy layout model (mlcpu.legacy-model.v1) for C-LEGACY-LAYOUT (GemmaJavaTemplates.md 3.4, 5.3): the estate's
/// records as Java types and their decoders. The layouts are the record kit's (record-layouts.json, generated from the
/// copybooks by the CobolBdm pipeline); the SME's legacy mapping (mlcpu.legacy-mapping.v1) names only what a copybook cannot
/// say: the Java name of each record, the field prefix to drop, which digit fields are identifiers (strings) or dates
/// (yyyymmdd), which one-character fields are flags ('Y'), the record-type constants, and the discriminator of a record whose
/// REDEFINES variants share its storage (a sealed interface with one record per variant). Every other type follows from the
/// PIC: text is a String without its trailing spaces, unsigned digits an int (up to 9) or a long, digits with a scale a
/// BigDecimal at that scale (SIGN LEADING SEPARATE when signed). FILLER and group items are dropped.
/// </summary>
public sealed class LegacyModelBuilder(JsonObject layouts, JsonObject mapping, string layoutsHash)
{
    public List<string> Problems { get; } = [];

    static string[] Strings(JsonNode? n) => (n as JsonArray ?? []).Select(x => (string)x!).ToArray();

    /// <summary>RQ-SCHED-START-DATE without its prefix RQ- as schedStartDate.</summary>
    public static string JavaName(string cobol, string prefix)
    {
        var s = cobol.StartsWith(prefix, StringComparison.Ordinal) ? cobol[prefix.Length..] : cobol;
        var parts = s.ToLowerInvariant().Split('-', StringSplitOptions.RemoveEmptyEntries);
        return parts[0] + string.Concat(parts.Skip(1).Select(p => char.ToUpperInvariant(p[0]) + p[1..]));
    }

    public JsonObject Build()
    {
        var all = (layouts["layouts"] as JsonArray ?? []).ToDictionary(l => (string)l!["name"]!, l => l!.AsObject(), StringComparer.Ordinal);
        var datasets = layouts["datasets"] as JsonArray ?? [];
        var records = new JsonArray();
        foreach (var r in mapping["records"] as JsonArray ?? [])
        {
            var layoutName = (string)r!["layout"]!;
            if (!all.TryGetValue(layoutName, out var layout)) { Problems.Add($"no layout {layoutName} in the record layouts"); continue; }
            var name = (string)r["name"]!;
            var prefix = (string?)r["prefix"] ?? "";
            var length = (int)layout["length"]!;
            var dataset = datasets.FirstOrDefault(d => Strings(d!["layouts"]).Contains(layoutName));
            var fields = layout["fields"]!.AsArray();
            var constants = (r["constants"] as JsonObject ?? []).Select(c =>
            {
                var f = fields.FirstOrDefault(x => (string)x!["name"]! == c.Key);
                if (f is null) Problems.Add($"{name}: constant {c.Key} is not a field of {layoutName}");
                return (JsonNode)new JsonObject
                {
                    ["cobol"] = c.Key, ["offset"] = (int?)f?["offset"] ?? 0, ["length"] = (int?)f?["length"] ?? 0, ["value"] = (string)c.Value!
                };
            }).ToArray();
            var skip = new HashSet<string>(constants.Select(c => (string)c!["cobol"]!), StringComparer.Ordinal);
            var variants = r["variants"] as JsonArray;
            if (variants is null)
            {
                records.Add(Record(name, (string?)r["title"] ?? name, layoutName, length, dataset, prefix, Fields(fields, r, prefix, skip, null),
                    constants, null, null, null));
                continue;
            }
            // REDEFINES: the common fields (before the redefined storage), then one record per variant over its group
            var discriminator = (string?)r["discriminator"] ?? throw new InvalidDataException($"{name}: variants need a discriminator");
            var disc = fields.FirstOrDefault(x => (string)x!["name"]! == discriminator);
            if (disc is null) { Problems.Add($"{name}: discriminator {discriminator} is not a field of {layoutName}"); continue; }
            var groups = variants.Select(v => (string)v!["group"]!).ToArray();
            var firstGroup = fields.Where(x => groups.Contains((string)x!["name"]!)).Min(x => (int)x!["offset"]!);
            var redefined = fields.Where(x => (int)x!["offset"]! >= firstGroup).Select(x => (string)x!["name"]!).ToHashSet(StringComparer.Ordinal);
            var common = Fields(fields, r, prefix, skip.Concat(redefined).ToHashSet(StringComparer.Ordinal), null);
            var permits = new JsonArray();
            var cases = new JsonArray();
            foreach (var v in variants)
            {
                var vName = (string)v!["name"]!;
                var group = (string)v["group"]!;
                var g = fields.First(x => (string)x!["name"]! == group);
                int from = (int)g!["offset"]!, to = from + (int)g["length"]!;
                // the group's own fields: inside its storage, after it in layout order, until the next variant's group
                var inGroup = new List<JsonNode>();
                bool inside = false;
                foreach (var f in fields)
                {
                    var fn = (string)f!["name"]!;
                    if (fn == group) { inside = true; continue; }
                    if (!inside) continue;
                    if (groups.Contains(fn)) break;
                    if ((int)f["offset"]! >= from && (int)f["offset"]! < to) inGroup.Add(f);
                }
                var vFields = new JsonArray(common.Select(c => c!.DeepClone()).Concat(Fields(new JsonArray(inGroup.Select(f => f.DeepClone()).ToArray()), v,
                    (string?)v["prefix"] ?? prefix, new HashSet<string>(), r).Select(f => f!.DeepClone())).ToArray());
                records.Add(Record(vName, (string?)v["title"] ?? vName, layoutName, length, dataset, prefix, vFields, [], name, null, null));
                permits.Add(vName);
                cases.Add(new JsonObject { ["values"] = new JsonArray(Strings(v["when"]).Select(x => (JsonNode?)x).ToArray()), ["record"] = vName });
            }
            records.Add(Record(name, (string?)r["title"] ?? name, layoutName, length, dataset, prefix, common, constants, null, permits,
                new JsonObject { ["cobol"] = discriminator, ["offset"] = (int)disc["offset"]!, ["length"] = (int)disc["length"]!, ["cases"] = cases }));
        }
        var names = records.Select(x => (string)x!["name"]!).ToList();
        foreach (var d in names.GroupBy(n => n).Where(g => g.Count() > 1)) Problems.Add($"record name {d.Key} used twice");
        return new JsonObject
        {
            ["schema"] = "mlcpu.legacy-model.v1",
            ["layouts"] = $"{(string?)layouts["programId"]} record layouts (sha256:{layoutsHash[..12]})",
            ["package"] = (string?)mapping["package"] ?? throw new InvalidDataException("the mapping has no package"),
            ["codec"] = (string?)mapping["codec"] ?? "LegacyCodec",
            ["title"] = (string?)mapping["title"] ?? "",
            ["records"] = records,
            ["fromTerms"] = FromTerms(records) is { } ft ? new JsonArray(ft) : new JsonArray()
        };
    }

    /// <summary>
    /// The inverse mapping (G11's export, FE 9.3): the mapping's "fromTerms" section as Java expressions over the service's
    /// terms, one per component of each target record, in component order. A string is a path ("currency.code" is
    /// terms.currency().code()); {from, codes} a switch over an enum; {present} a null test; {from, absent} a value or its
    /// stand-in when null; {constant} a literal; {param} the method's parameter; {from, name: true} an enum's name.
    /// </summary>
    JsonNode? FromTerms(JsonArray records)
    {
        if (mapping["fromTerms"] is not JsonObject spec) return null;
        string Path(string p, string root) => root + string.Concat(p.Split('.').Select(x => "." + x + "()"));
        string Literal(JsonNode? v, string java) => java switch
        {
            "String" => "\"" + ((string?)v ?? "").Replace("\"", "\\\"") + "\"",
            "boolean" => v?.GetValue<bool>() == true ? "true" : "false",
            "BigDecimal" => $"new java.math.BigDecimal(\"{v}\")",
            _ => v?.ToString() ?? "0"
        };
        var targets = new JsonArray();
        foreach (var (role, node) in spec)
        {
            if (node is not JsonObject t) continue;
            var recordName = (string)t["record"]!;
            var record = records.OfType<JsonObject>().FirstOrDefault(r => (string)r["name"]! == recordName);
            if (record is null) { Problems.Add($"fromTerms.{role}: no record {recordName}"); continue; }
            var root = (string?)t["root"] ?? "terms";
            var fields = t["fields"] as JsonObject ?? [];
            var args = new JsonArray();
            foreach (var f in record["fields"]!.AsArray().OfType<JsonObject>())
            {
                var name = (string)f["name"]!;
                var java = (string)f["java"]!;
                var s = fields[name];
                string expr;
                if (s is null) { Problems.Add($"fromTerms.{role}: no source for {name} ({f["cobol"]})"); continue; }
                if (s is JsonValue) expr = Path((string)s!, root);
                else
                {
                    var o = s.AsObject();
                    if (o["param"] is { } param) expr = (string)param!;
                    else if (o.ContainsKey("constant")) expr = Literal(o["constant"], java);
                    else if (o["present"] is { } present) expr = Path((string)present!, root) + " != null";
                    else if (o["codes"] is JsonObject codes)
                        expr = "switch (" + Path((string)o["from"]!, root) + ") { " + string.Concat(codes.Select(c => $"case {c.Key} -> {Literal(c.Value, java)}; ")) + "}";
                    else if (o.ContainsKey("absent"))
                    {
                        var v = Path((string)o["from"]!, root);
                        expr = $"{v} == null ? {Literal(o["absent"], java)} : {v}";
                    }
                    else if (o["name"]?.GetValue<bool>() == true) expr = Path((string)o["from"]!, root) + ".name()";
                    else { Problems.Add($"fromTerms.{role}.{name}: an unknown source form"); continue; }
                }
                args.Add(new JsonObject { ["name"] = name, ["java"] = java, ["expr"] = expr });
            }
            targets.Add(new JsonObject
            {
                ["role"] = role, ["record"] = recordName, ["method"] = (string?)t["method"] ?? role, ["param"] = (string?)t["paramType"] ?? "",
                ["paramName"] = root, ["extra"] = (string?)t["extra"] ?? "", ["args"] = args
            });
        }
        return new JsonObject { ["class"] = (string?)spec["class"] ?? "LegacyTermsCodec", ["targets"] = targets };
    }

    static JsonObject Record(string name, string title, string layout, int length, JsonNode? dataset, string prefix, JsonArray fields, JsonNode[] constants,
        string? parent, JsonArray? permits, JsonObject? discriminator) => new()
    {
        ["name"] = name, ["title"] = title, ["layout"] = layout, ["length"] = length,
        ["dsn"] = (string?)dataset?["dsn"], ["dd"] = (string?)dataset?["dd"],
        ["decoder"] = char.ToLowerInvariant(name.Replace("Legacy", "")[0]) + name.Replace("Legacy", "")[1..],
        ["prefix"] = prefix, ["fields"] = fields, ["constants"] = new JsonArray(constants.Select(c => c.DeepClone()).ToArray()),
        ["parent"] = parent, ["sealed"] = permits is not null, ["permits"] = permits, ["discriminator"] = discriminator
    };

    JsonArray Fields(JsonArray fields, JsonNode spec, string prefix, ISet<string> skip, JsonNode? parentSpec)
    {
        string[] Of(string key) => Strings(spec[key]).Concat(parentSpec is null ? [] : Strings(parentSpec[key])).ToArray();
        var ids = Of("ids");
        var dates = Of("dates");
        var flags = Of("flags");
        var renames = spec["names"] as JsonObject ?? [];
        var outFields = new JsonArray();
        foreach (var f in fields)
        {
            var cobol = (string)f!["name"]!;
            if (skip.Contains(cobol) || cobol == "FILLER" || f["group"]?.GetValue<bool>() == true) continue;
            var kind = (string)f["kind"]!;
            var pic = (string?)f["pic"] ?? "";
            int length = (int)f["length"]!, scale = (int)f["scale"]!, digits = (int)f["digits"]!;
            bool signed = f["signed"]?.GetValue<bool>() == true;
            string decode, java;
            if (ids.Contains(cobol)) (decode, java) = ("id", "String");
            else if (dates.Contains(cobol))
            {
                if (kind != "zoned" || length != 8) Problems.Add($"{cobol}: a date must be 9(8), not {pic}");
                (decode, java) = ("date", "LocalDate");
            }
            else if (flags.Contains(cobol))
            {
                if (length != 1) Problems.Add($"{cobol}: a flag must be one character, not {pic}");
                (decode, java) = ("flag", "boolean");
            }
            else if (kind == "text") (decode, java) = ("text", "String");
            else if (kind == "zoned" && scale > 0) (decode, java) = (signed ? "signedDecimal" : "unsignedDecimal", "BigDecimal");
            else if (kind == "zoned" && !signed) (decode, java) = digits <= 9 ? ("unsignedInt", "int") : ("unsignedLong", "long");
            else if (kind == "zoned") (decode, java) = digits <= 9 ? ("signedInt", "int") : ("signedLong", "long");
            else { Problems.Add($"{cobol}: kind {kind} ({pic}) has no Java mapping"); continue; }
            if (signed && f["signSeparate"]?.GetValue<bool>() != true) Problems.Add($"{cobol}: only SIGN LEADING SEPARATE is decoded ({pic})");
            var name = (string?)renames[cobol] ?? JavaName(cobol, cobol.StartsWith(prefix, StringComparison.Ordinal) ? prefix : cobol[..(cobol.IndexOf('-') + 1)]);
            outFields.Add(new JsonObject
            {
                ["cobol"] = cobol, ["name"] = name, ["java"] = java, ["decode"] = decode, ["offset"] = (int)f["offset"]!, ["length"] = length,
                ["scale"] = scale, ["signed"] = signed, ["pic"] = pic
            });
        }
        return outFields;
    }
}
