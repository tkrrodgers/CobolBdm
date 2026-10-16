using MlCpu.Cobol.Analysis;

namespace MlCpu.CobolKb.Knowledge;

/// <summary>Inputs of the worked examples in values (MlCpu.Kit.CallExamples) that come from the program model at publish.</summary>
public static class KitExamples
{
    /// <summary>Kit features a publish of this pipeline writes to normalization/kit-features.json.</summary>
    public static readonly string[] Features = ["examples-values", "prompt-packet-compact", "shadow-feedback", "subprogram-calls", "pass-through-records"];

    /// <summary>Elementary item name → its 88-level condition names and values, over the given records (LINKAGE records: the CALL arguments).</summary>
    public static IReadOnlyDictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>> Conditions(IEnumerable<DataItem> records)
    {
        var map = new Dictionary<string, IReadOnlyDictionary<string, IReadOnlyList<string>>>(StringComparer.Ordinal);
        foreach (var r in records)
            foreach (var leaf in ReplayPlanBuilder.Leaves(r))
                if (leaf.Roles is { Count: > 0 } roles)
                    map.TryAdd(System.Text.RegularExpressions.Regex.Replace(leaf.Name, @"\(\d+(,\d+)*\)", ""), roles);
        return map;
    }
}
