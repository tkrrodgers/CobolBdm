using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace MlCpu.DesignModel;

/// <summary>
/// Kit feature pass-through-records: in the implementation prompt, a record the program passes through (more than
/// <see cref="MinItems"/> items, of which its own statements name under a tenth: <see cref="PassThrough"/>) lists only the
/// items the statements name (the AST outline has every statement); the others are described in one line. A program generated from Java by J3S receives the whole object heap (FS-HEAP, 83 MB, 935 items) and passes it on to
/// the programs it CALLs; its own statements name 39 of the items. Listing every item made LCLE0100's prompt 126,789 tokens
/// (146,082 with the outline), above Gemma 4's 131,072-token context, so the program could not be modernized at all.
/// The packet file, the record kit file and the record classes are unchanged (every accessor stays); only the prompt's copy
/// is shortened, so the packet's hash and every kit without the feature render as before.
/// </summary>
public static partial class PromptRecords
{
    /// <summary>Records up to this many items are listed whole.</summary>
    public const int MinItems = 120;

    /// <summary>
    /// A record the program passes through: large, and its statements name under a tenth of its items. FS-HEAP in J3S's
    /// LCLE0100 (0 of 935) and FSAPLC01 (21 of 935) are; Fineract's 2,001-field LM-MODEL, whose programs name 24% to 86% of
    /// it, is not, so its prompts stay as they were.
    /// </summary>
    public static bool PassThrough(int named, int total) => total > MinItems && named * 10 < total;

    [GeneratedRegex(@"(?<![A-Za-z0-9-])[A-Z0-9][A-Z0-9-]*[A-Z0-9](?![A-Za-z0-9-])")]
    private static partial Regex Name();

    [GeneratedRegex(@"(?m)^### (\w+): (\d+) bytes; ([A-Z0-9][A-Z0-9-]*) ")]
    private static partial Regex KitHeading();

    /// <summary>A kit item line: the accessor signature, two or more spaces, then the COBOL name (an 88's after "88 ").</summary>
    [GeneratedRegex(@"^\S.*?\S\s{2,}(?:88 )?([A-Z0-9][A-Z0-9-]*)(?:\s|$)")]
    private static partial Regex ItemLine();

    [GeneratedRegex(@"(?m)^L\d+ PROCEDURE DIVISION")]
    private static partial Regex Procedure();

    /// <summary>An outline DATA DIVISION line: L&lt;n&gt;, the level, the name.</summary>
    [GeneratedRegex(@"^\s*L\d+\s+(\d\d)\s+([A-Z0-9][A-Z0-9-]*)")]
    private static partial Regex OutlineItem();

    /// <summary>
    /// The data names the program's statements use: every COBOL word of the outline's PROCEDURE DIVISION (keywords do no
    /// harm). The outline's DATA DIVISION declares every item, so it does not count.
    /// </summary>
    public static HashSet<string> Referenced(string outline)
    {
        var p = Procedure().Match(outline);
        return Name().Matches(p.Success ? outline[p.Index..] : outline).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// The AST outline with each large record's declaration cut to its 01 line and the referenced items (LCLE0100's
    /// outline declared FS-HEAP in 936 of its 1,394 lines); the records cut.
    /// </summary>
    public static string Outline(string outline, ISet<string> referenced, out List<string> cut)
    {
        cut = [];
        var p = Procedure().Match(outline);
        if (!p.Success) return outline;
        var lines = outline[..p.Index].Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            var m = OutlineItem().Match(lines[i]);
            if (!m.Success || m.Groups[1].Value != "01")
            {
                sb.Append(lines[i]).Append(i + 1 < lines.Length ? "\n" : "");
                continue;
            }
            // the record's items: the deeper lines up to the next 01, 77, FD or section heading
            int end = i + 1;
            while (end < lines.Length && OutlineItem().Match(lines[end]) is { Success: true } x && x.Groups[1].Value is not ("01" or "77")) end++;
            int total = end - i - 1;
            sb.Append(lines[i]).Append('\n');
            var kept = Enumerable.Range(i + 1, total).Where(k => referenced.Contains(OutlineItem().Match(lines[k]).Groups[2].Value)).ToList();
            if (!PassThrough(kept.Count, total))
            {
                for (int k = i + 1; k < end; k++) sb.Append(lines[k]).Append('\n');
            }
            else
            {
                string indent = lines[i][..(lines[i].Length - lines[i].TrimStart().Length)];
                foreach (int k in kept) sb.Append(lines[k]).Append('\n');
                sb.Append(indent).Append($"  * {kept.Count} of the {total} items of {m.Groups[2].Value} shown: the ones the PROCEDURE DIVISION names " +
                                         "(the record kit has them all); the others pass unchanged to the programs it CALLs\n");
                cut.Add($"{m.Groups[2].Value} {kept.Count}/{total}");
            }
            i = end - 1;
        }
        return sb + outline[p.Index..];
    }

    /// <summary>The record kit with each large record's item list cut to the referenced items; the records cut.</summary>
    public static string Kit(string kit, ISet<string> referenced, out List<string> cut)
    {
        cut = [];
        var heads = KitHeading().Matches(kit).ToList();
        var sb = new StringBuilder();
        int pos = 0;
        for (int i = 0; i < heads.Count; i++)
        {
            int start = heads[i].Index;
            sb.Append(kit[pos..start]);
            // a record's section ends at the next record's heading or at the next "## " part of the kit
            int next = i + 1 < heads.Count ? heads[i + 1].Index : kit.Length;
            var part = Regex.Match(kit[start..next], @"(?m)^## ");
            int end = part.Success ? start + part.Index : next;
            sb.Append(CutSection(kit[start..end], heads[i].Groups[1].Value, heads[i].Groups[3].Value, referenced, cut));
            pos = end;
        }
        sb.Append(kit[pos..]);
        return sb.ToString();
    }

    static string CutSection(string section, string cls, string record, ISet<string> referenced, List<string> cut)
    {
        int open = section.IndexOf("```\n", StringComparison.Ordinal);
        int close = open < 0 ? -1 : section.IndexOf("\n```", open + 4, StringComparison.Ordinal);
        if (open < 0 || close < 0) return section;
        var lines = section[(open + 4)..close].Split('\n');
        var items = lines.Select(l => (Line: l, M: ItemLine().Match(l))).ToList();
        int total = items.Count(x => x.M.Success);
        var kept = items.Where(x => !x.M.Success || referenced.Contains(x.M.Groups[1].Value)).Select(x => x.Line).ToList();
        int k = kept.Count(l => ItemLine().IsMatch(l));
        if (!PassThrough(k, total)) return section;
        cut.Add($"{record} {k}/{total}");
        return section[..(open + 4)] + string.Join('\n', kept) + section[close..] +
               $"\n{k} of the {total} items of {record} are listed: the ones this program's statements name. {cls} has an accessor for every item " +
               "(named the same way); this program does not name the others: it passes the record unchanged to the programs it CALLs.\n";
    }

    /// <summary>The packet JSON with each large dataContracts record's fields cut to the referenced ones; the records cut.</summary>
    public static string Packet(string packetJson, ISet<string> referenced, out List<string> cut)
    {
        cut = [];
        var packet = JsonNode.Parse(packetJson)!.AsObject();
        if (packet["dataContracts"] is not JsonArray contracts) return packetJson;
        foreach (var rec in contracts.OfType<JsonObject>())
        {
            if (rec["fields"] is not JsonArray fields) continue;
            var keep = fields.OfType<JsonObject>().Where(f => f["name"]?.GetValue<string>() is { } n && referenced.Contains(Base(n))).ToList();
            if (!PassThrough(keep.Count, fields.Count)) continue;
            string name = rec["record"]?.GetValue<string>() ?? "?";
            cut.Add($"{name} {keep.Count}/{fields.Count}");
            rec["fields"] = new JsonArray(keep.Select(f => f.DeepClone()).ToArray());
            rec["fieldsListed"] = $"{keep.Count} of {fields.Count}: the fields this program's statements name (offsets are within the record); " +
                                  "the program passes the others unchanged to the programs it CALLs";
        }
        return cut.Count == 0 ? packetJson : packet.ToJsonString();
    }

    /// <summary>A field's data name without its subscripts ("HI-ST(1)" → HI-ST).</summary>
    static string Base(string name)
    {
        int p = name.IndexOf('(');
        return p < 0 ? name : name[..p];
    }
}
