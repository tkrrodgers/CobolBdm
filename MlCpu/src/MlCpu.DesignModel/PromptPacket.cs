using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// The requirements packet as the implementation prompt shows it under kit feature prompt-packet-compact. The packet file and
/// its hash are unchanged; only the prompt's copy is rendered so that each fact appears once:
/// - statements (rules[].statements, paragraphs[].segments[].statements) by step id only ("2000-FREQUENCY-TYPE:1:EVALUATE:L73"),
///   because the AST outline in the same prompt carries every statement's text; rule ids, kinds, guarding conditions (when,
///   condition), reads and writes stay;
/// - a dataContracts record that the record kit lists with every one of its fields is named, with its kit class, instead of
///   repeated field by field (a 262 KB model record is described once, in the record kit).
/// </summary>
public static partial class PromptPacket
{
    public sealed record Rendering(string Json, IReadOnlyList<string> RecordsByReference, int StatementsByStepId);

    [GeneratedRegex(@"(?m)^### (\w+): (\d+) bytes; ([A-Z0-9][A-Z0-9-]*) ")]
    private static partial Regex KitHeading();

    /// <param name="views">Record name → the typed reference's view interface, which then stands for the kit class.</param>
    public static Rendering Compact(string packetPath, string? recordKit, IReadOnlyDictionary<string, string>? views = null)
    {
        var packet = JsonNode.Parse(File.ReadAllText(packetPath))!.AsObject();
        int statements = 0;
        foreach (var r in packet["rules"]?.AsArray().OfType<JsonObject>() ?? []) statements += StepIdsOnly(r);
        foreach (var p in packet["paragraphs"]?.AsArray().OfType<JsonObject>() ?? [])
            foreach (var s in p["segments"]?.AsArray().OfType<JsonObject>() ?? []) statements += Segment(s);
        var byReference = new List<string>();
        if (recordKit is not null && packet["dataContracts"] is JsonArray contracts)
        {
            var sections = KitSections(recordKit);
            for (int i = 0; i < contracts.Count; i++)
            {
                if (contracts[i] is not JsonObject rec || rec["record"]?.GetValue<string>() is not { } name) continue;
                if (!sections.TryGetValue(name, out var kit)) continue;
                var fields = rec["fields"]?.AsArray().OfType<JsonObject>().Select(f => f["name"]?.GetValue<string>()).OfType<string>().ToList() ?? [];
                // Only when the kit's section names every field (with its 88-level names, which the kit lists as testers).
                if (fields.Count == 0 || !fields.All(f => Regex.IsMatch(kit.Text, $@"(?<![A-Z0-9-]){Regex.Escape(f)}(?![A-Z0-9-])"))) continue;
                var conds = rec["fields"]!.AsArray().OfType<JsonObject>().SelectMany(f => (f["conditionNames"] as JsonObject)?.Select(kv => kv.Key) ?? []).ToList();
                if (!conds.All(c => kit.Text.Contains(c, StringComparison.Ordinal))) continue;
                var view = views?.GetValueOrDefault(name);
                contracts[i] = new JsonObject
                {
                    ["record"] = name, ["copybook"] = rec["copybook"]?.DeepClone(), ["length"] = rec["length"]?.DeepClone(),
                    ["fields"] = view is not null ? $"{fields.Count} fields: see the record view {view}" : $"{fields.Count} fields: see record kit class {kit.Class}"
                };
                byReference.Add($"{name} ({view ?? kit.Class})");
            }
        }
        return new Rendering(packet.ToJsonString(), byReference, statements);
    }

    /// <summary>statements: [{stepId, line, verb, cobol}] → ["stepId", ...]; the number replaced.</summary>
    static int StepIdsOnly(JsonObject o)
    {
        if (o["statements"] is not JsonArray st) return 0;
        var ids = st.Select(s => s is JsonObject so ? so["stepId"]?.GetValue<string>() : s?.ToString()).OfType<string>().ToList();
        o["statements"] = new JsonArray(ids.Select(x => (JsonNode)x).ToArray());
        return ids.Count;
    }

    /// <summary>
    /// A paragraph's segment tree keeps ids, kinds, conditions and statement ids; the reads and writes each segment repeats
    /// from its children (the rules list them, once) and the paragraph name each segment repeats are left out.
    /// </summary>
    static int Segment(JsonObject s)
    {
        int n = StepIdsOnly(s);
        s.Remove("reads");
        s.Remove("writes");
        s.Remove("paragraph");
        if (s["children"] is JsonArray { Count: 0 }) s.Remove("children");
        foreach (var c in s["children"]?.AsArray().OfType<JsonObject>() ?? []) n += Segment(c);
        return n;
    }

    sealed record KitSection(string Class, string Text);

    /// <summary>The record kit's "### Class: n bytes; RECORD-NAME (role)..." sections by record name.</summary>
    static Dictionary<string, KitSection> KitSections(string kit)
    {
        var map = new Dictionary<string, KitSection>(StringComparer.Ordinal);
        var heads = KitHeading().Matches(kit).ToList();
        for (int i = 0; i < heads.Count; i++)
        {
            int end = i + 1 < heads.Count ? heads[i + 1].Index : kit.Length;
            map.TryAdd(heads[i].Groups[3].Value, new KitSection(heads[i].Groups[1].Value, kit[heads[i].Index..end]));
        }
        return map;
    }
}
