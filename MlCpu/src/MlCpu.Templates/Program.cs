using System.Text.Json.Nodes;

namespace MlCpu.Templates;

/// <summary>
/// The template pipeline (GemmaJavaTemplates.md TS1, TS3, TG1, TG3), deterministic and on the CPU:
///   model      --api api-model.json --mapping host-mapping.yaml --out service-model.json [--leaks dictionary.tsv]
///              (--leaks: no name the model generates may be a source-system name of the package's leak dictionary)
///   legacy-model --mapping legacy-mapping.yaml --out legacy-model.json [--leaks dictionary.tsv]: C-LEGACY-LAYOUT's model
///   render     --catalog templates --model service-model.json --profile env.yaml (--bundle B | --templates A,B) --out dir [--clean]
///              writes the files, generated.lock.json, holes.json and selection.json (the rule selector's decisions)
///   conform    the same arguments without --out: renders twice and fails unless both renders are byte-identical (TG1)
///   lock-check --out dir: every locked file still has its hash (TG3: nobody edited generated code)
///   facade     --ofbiz root --model service-model.json --out facade.md: the host facade digest (FE §7.6)
/// </summary>
public static class Program
{
    public static int Main(string[] argv)
    {
        var verb = argv.Length > 0 ? argv[0] : "help";
        var a = new Dictionary<string, string>(StringComparer.Ordinal);
        for (int i = 1; i < argv.Length; i++)
            if (argv[i].StartsWith("--")) a[argv[i][2..]] = i + 1 < argv.Length && !argv[i + 1].StartsWith("--") ? argv[++i] : "true";
        string Need(string k) => a.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required for '{verb}'");
        try
        {
            switch (verb)
            {
                case "model":
                {
                    var api = JsonNode.Parse(File.ReadAllText(Need("api")))!.AsObject();
                    var mapping = Yaml.ToJson(Yaml.Load(Need("mapping")))!.AsObject();
                    if ((string?)mapping["schema"] != "mlcpu.host-mapping.v1") throw new InvalidDataException("the mapping's schema must be mlcpu.host-mapping.v1");
                    var builder = new ServiceModelBuilder(api, mapping);
                    var model = builder.Build();
                    foreach (var p in builder.Problems) Console.Error.WriteLine("problem: " + p);
                    if (builder.Problems.Count > 0) return 1;
                    model["migration"] = Migration(mapping, Path.GetFullPath(Need("mapping")));
                    var text = Renderer.Canonical(model);
                    if (a.GetValueOrDefault("leaks") is { } dictionary && Leaks(text, dictionary) is { Count: > 0 } leaks)
                    {
                        foreach (var l in leaks) Console.Error.WriteLine("leak: " + l);
                        Console.Error.WriteLine($"the service model carries {leaks.Count} source-system name(s) ({dictionary}): rename them in the mapping");
                        return 1;
                    }
                    WriteAtomic(Need("out"), text);
                    Console.WriteLine($"service model: {model["entities"]!.AsArray().Count} entities, {model["services"]!.AsArray().Count} services, " +
                                      $"{model["enums"]!.AsArray().Count} enums, {model["codecs"]!.AsArray().Count} codecs -> {Need("out")}");
                    return 0;
                }
                case "legacy-model":
                {
                    // legacy-model --mapping legacy-mapping.yaml --out legacy-model.json [--leaks dictionary.tsv]: C-LEGACY-LAYOUT's model
                    // from the record kit's layouts (the mapping's "layouts", relative to the mapping) and the SME's legacy mapping.
                    var mappingPath = Path.GetFullPath(Need("mapping"));
                    var mapping = Yaml.ToJson(Yaml.Load(mappingPath))!.AsObject();
                    if ((string?)mapping["schema"] != "mlcpu.legacy-mapping.v1") throw new InvalidDataException("the mapping's schema must be mlcpu.legacy-mapping.v1");
                    var layoutsPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mappingPath)!, (string?)mapping["layouts"] ?? throw new InvalidDataException("the mapping has no layouts")));
                    var layoutsText = File.ReadAllText(layoutsPath).Replace("\r\n", "\n");
                    var builder = new LegacyModelBuilder(JsonNode.Parse(layoutsText)!.AsObject(), mapping, Renderer.Sha256(layoutsText));
                    var model = builder.Build();
                    foreach (var p in builder.Problems) Console.Error.WriteLine("problem: " + p);
                    if (builder.Problems.Count > 0) return 1;
                    var text = Renderer.Canonical(model);
                    if (a.GetValueOrDefault("leaks") is { } dictionary && Leaks(text, dictionary) is { Count: > 0 } leaks)
                    {
                        foreach (var l in leaks) Console.Error.WriteLine("leak: " + l);
                        return 1;
                    }
                    WriteAtomic(Need("out"), text);
                    Console.WriteLine($"legacy model: {model["records"]!.AsArray().Count} record types -> {Need("out")}");
                    return 0;
                }
                case "render":
                {
                    var (renderer, ids) = Setup(a);
                    var outDir = Path.GetFullPath(Need("out"));
                    if (a.ContainsKey("clean")) Clean(outDir);
                    var (files, holes, decisions) = renderer.Render(ids, outDir);
                    File.WriteAllText(Path.Combine(outDir, "generated.lock.json"), Renderer.Canonical(renderer.Lock(files)));
                    File.WriteAllText(Path.Combine(outDir, "holes.json"), Renderer.Canonical(new JsonObject { ["schema"] = "mlcpu.holes.v1", ["holes"] = holes }));
                    File.WriteAllText(Path.Combine(outDir, "selection.json"), Renderer.Canonical(new JsonObject { ["schema"] = "mlcpu.template-selection.v1", ["arm"] = "RL", ["decisions"] = decisions }));
                    Console.WriteLine($"rendered {files.Count} file(s) from {decisions.Count(d => d!["selected"]!.GetValue<bool>())} template(s); {holes.Count} hole(s) -> {outDir}");
                    return 0;
                }
                case "conform":
                {
                    var (renderer, ids) = Setup(a);
                    var dirs = new[] { 1, 2 }.Select(n => Path.Combine(Path.GetTempPath(), $"mlcpu-conform-{Environment.ProcessId}-{n}")).ToArray();
                    var locks = dirs.Select(d =>
                    {
                        Clean(d);
                        return Renderer.Canonical(renderer.Lock(renderer.Render(ids, d).Files));
                    }).ToArray();
                    foreach (var d in dirs) Directory.Delete(d, true);
                    var same = locks[0] == locks[1];
                    Console.WriteLine(same ? "TG1 determinism: two renders are byte-identical" : "TG1 determinism FAILED: the renders differ");
                    if (a.GetValueOrDefault("golden") is { } golden)
                    {
                        var expected = File.ReadAllText(golden).Replace("\r\n", "\n");
                        if (expected != locks[0]) { Console.WriteLine($"golden lock {golden} differs from this render"); return 1; }
                        Console.WriteLine($"golden lock {golden}: equal");
                    }
                    return same ? 0 : 1;
                }
                case "lock-check":
                {
                    var outDir = Path.GetFullPath(Need("out"));
                    var lockJson = JsonNode.Parse(File.ReadAllText(Path.Combine(outDir, "generated.lock.json")))!;
                    var drift = new List<string>();
                    foreach (var f in lockJson["files"]!.AsArray())
                    {
                        var path = Path.Combine(outDir, (string)f!["path"]!);
                        if (!File.Exists(path)) drift.Add($"missing: {f["path"]}");
                        else if (Renderer.Sha256(File.ReadAllText(path).Replace("\r\n", "\n")) != (string)f["sha256"]!) drift.Add($"edited: {f["path"]}");
                    }
                    foreach (var d in drift) Console.WriteLine(d);
                    Console.WriteLine(drift.Count == 0 ? $"TG3: {lockJson["files"]!.AsArray().Count} generated file(s) unchanged" : $"TG3 FAILED: {drift.Count} generated file(s) changed");
                    return drift.Count == 0 ? 0 : 1;
                }
                case "facade":
                {
                    var model = JsonNode.Parse(File.ReadAllText(Need("model")))!.AsObject();
                    var text = OfbizFacade.Digest(Need("ofbiz"), model);
                    WriteAtomic(Need("out"), text);
                    Console.WriteLine($"facade digest: {text.Length:N0} characters -> {Need("out")}");
                    return 0;
                }
                default:
                    Console.WriteLine("verbs: model, render, conform, lock-check, facade (see Program.cs)");
                    return verb == "help" ? 0 : 2;
            }
        }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or ArgumentException or FileNotFoundException)
        {
            Console.Error.WriteLine("error: " + e.Message);
            return 1;
        }
    }

    /// <summary>
    /// The identifiers of a text that the leak dictionary (tab-separated: name, status, ...) lists as "leak", in any case, with
    /// their line: a generated name must not be a source-system name (ForwardEngineeringFineract.md 4.3), whatever made it.
    /// </summary>
    static List<string> Leaks(string text, string dictionary)
    {
        var leak = File.ReadLines(dictionary).Where(l => !l.StartsWith('#')).Select(l => l.Split('\t'))
            .Where(p => p.Length > 1 && p[1] == "leak").Select(p => p[0]).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split('\n');
        return lines.SelectMany((line, i) => System.Text.RegularExpressions.Regex.Matches(line, "[A-Za-z_][A-Za-z0-9_]*")
            .Where(m => leak.Contains(m.Value)).Select(m => $"{m.Value} (line {i + 1})")).Distinct().ToList();
    }

    /// <summary>
    /// The model's migration section (H-OFBIZ-MIGRATION, FE 9.2), null without one: the mapping's block, with what the plugin's
    /// reader needs from the legacy model resolved: each role's record, decoder and length constant, and the offsets of the fields
    /// it groups by (the loan id, the currency code, the currency key), so the raw records are grouped without decoding; and the
    /// schedule line's fields (the legacy lines' columns).
    /// </summary>
    static JsonNode? Migration(JsonObject mapping, string mappingPath)
    {
        if (mapping["migration"] is not JsonObject m) return null;
        var legacyPath = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(mappingPath)!, (string?)m["legacyModel"] ?? throw new InvalidDataException("migration.legacyModel is missing")));
        var legacy = JsonNode.Parse(File.ReadAllText(legacyPath))!.AsObject();
        var recs = legacy["records"]!.AsArray().OfType<JsonObject>().ToDictionary(r => (string)r["name"]!, StringComparer.Ordinal);
        JsonObject Record(string name) => recs.TryGetValue(name, out var r) ? r : throw new InvalidDataException($"migration: no legacy record {name}");
        JsonObject Field(JsonObject rec, string cobol) => rec["fields"]!.AsArray().OfType<JsonObject>().FirstOrDefault(f => (string)f["cobol"]! == cobol)
            ?? throw new InvalidDataException($"migration: {rec["name"]} has no field {cobol}");
        var records = new JsonObject();
        foreach (var (role, node) in m["records"] as JsonObject ?? [])
        {
            var spec = node!.AsObject();
            var rec = Record((string)spec["record"]!);
            var o = new JsonObject
            {
                ["record"] = (string)rec["name"]!, ["decoder"] = (string)rec["decoder"]!,
                ["lengthConstant"] = ServiceModelBuilder.UpperSnake((string)rec["decoder"]!) + "_LENGTH", ["dataset"] = (string?)spec["dataset"],
                ["header"] = (string?)spec["header"], ["line"] = (string?)spec["line"],
                ["loanIdOffset"] = null, ["loanIdLength"] = null, ["currencyOffset"] = null, ["currencyLength"] = null, ["keyOffset"] = null, ["keyLength"] = null
            };
            foreach (var (key, prefix) in new[] { ("loanId", "loanId"), ("currency", "currency"), ("key", "key") })
                if ((string?)spec[key] is { } cobol)
                {
                    var f = Field(rec, cobol);
                    o[prefix + "Offset"] = (int)f["offset"]!;
                    o[prefix + "Length"] = (int)f["length"]!;
                }
            records[role] = o;
        }
        var line = Record((string?)m["records"]?["schedule"]?["line"] ?? throw new InvalidDataException("migration.records.schedule.line is missing"));
        var result = m.DeepClone().AsObject();
        result.Remove("legacyModel");
        result["package"] = (string)legacy["package"]!;
        result["codec"] = (string)legacy["codec"]!;
        // the terms-to-legacy inverse (the shadow run's export), when the legacy mapping has one
        result["fromTerms"] = (legacy["fromTerms"] as JsonArray ?? []).OfType<JsonObject>().Select(f => (string?)f["class"]).FirstOrDefault();
        if (result["export"] is null) result["export"] = null;
        result["records"] = records;
        result["lineFields"] = new JsonArray(line["fields"]!.AsArray().OfType<JsonObject>().Where(f => (string)f["name"]! != "loanId")
            .Select(f => (JsonNode)new JsonObject { ["name"] = (string)f["name"]!, ["java"] = (string)f["java"]! }).ToArray());
        return result;
    }

    /** Writes a file whole or not at all (a reader never sees it half written: another lane may be reading it). */
    static void WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp-" + Environment.ProcessId;
        File.WriteAllText(tmp, text);
        File.Move(tmp, path, true);
    }

    static (Renderer Renderer, List<string> Ids) Setup(Dictionary<string, string> a)
    {
        string Need(string k) => a.GetValueOrDefault(k) ?? throw new ArgumentException($"--{k} is required");
        var catalog = Catalog.Load(Need("catalog"));
        var model = JsonNode.Parse(File.ReadAllText(Need("model")))!.AsObject();
        var profile = Yaml.ToJson(Yaml.Load(Need("profile")))!.AsObject();
        var ids = a.GetValueOrDefault("bundle") is { } b
            ? catalog.Bundles.GetValueOrDefault(b)?.ToList() ?? throw new ArgumentException($"no bundle {b}")
            : Need("templates").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        return (new Renderer(catalog, model, profile), ids);
    }

    /// <summary>Removes the previous render (only the files its lock lists, so holes and other files stay).</summary>
    static void Clean(string outDir)
    {
        var lockFile = Path.Combine(outDir, "generated.lock.json");
        if (!File.Exists(lockFile)) { Directory.CreateDirectory(outDir); return; }
        foreach (var f in JsonNode.Parse(File.ReadAllText(lockFile))!["files"]!.AsArray())
        {
            var path = Path.Combine(outDir, (string)f!["path"]!);
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
