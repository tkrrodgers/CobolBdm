using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;

namespace MlCpu.CobolKb.Knowledge;

/// <summary>
/// What the library-equivalence probe (<c>com.mlcpu.rt.EquivalenceProbe</c>) is given besides the CALL traces, built the
/// same way at publish (CapabilityKitBuilder) and by the standalone run (docs/equivalence-probe), so both produce the same
/// statements: the 88-level names of the linkage records, and the number of CALLs each scenario's probe trace keeps.
/// </summary>
public static class EquivalenceProbeInputs
{
    /// <summary>CALLs kept per scenario in the probe trace (<c>CallTrace --probe-max</c>).</summary>
    public const int ProbeTraceCalls = 1000;

    /// <summary>Generated CALLs per group with which the probe verifies a statement through the faithful program.</summary>
    public const int GeneratedCalls = 100_000;

    /// <summary>The probe's time budget in seconds.</summary>
    public const int MaxSeconds = 900;

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    /// <summary>The linkage records of a subprogram (its PROCEDURE DIVISION USING), or null when one is not found.</summary>
    public static IReadOnlyList<DataItem>? Linkage(CobolProgram sub)
    {
        var linkage = sub.Using.Select(u => sub.Items.FirstOrDefault(i => i.Name.Equals(u, StringComparison.OrdinalIgnoreCase) && i.Level is 1 or 77)).ToList();
        return linkage.Count == 0 || linkage.Any(i => i is null) ? null : linkage!;
    }

    /// <summary>mlcpu.condition-names.v1: the 88-level names of the records' items, with their values.</summary>
    public static string ConditionNames(IEnumerable<DataItem> records)
    {
        static IEnumerable<DataItem> Tree(DataItem d)
        {
            yield return d;
            foreach (var c in d.Children)
                foreach (var x in Tree(c)) yield return x;
        }
        var fields = new JsonObject();
        foreach (var rec in records)
            foreach (var it in Tree(rec))
            {
                if (it.Level == 88) continue;
                var conds = it.Children.Where(c => c.Level == 88 && c.Values.Count > 0).ToList();
                if (conds.Count == 0) continue;
                fields[it.Name] = new JsonArray(conds.Select(c => (JsonNode)new JsonObject
                {
                    ["name"] = c.Name,
                    ["values"] = new JsonArray(c.Values.Where(v => v.High is null && !v.Low.Hex).Select(v => (JsonNode)v.Low.Text).ToArray())
                }).ToArray());
            }
        return new JsonObject { ["schema"] = "mlcpu.condition-names.v1", ["fields"] = fields }.ToJsonString(Indented) + "\n";
    }

    /// <summary>
    /// The probe's arguments after the program id: layouts, 88-level names, traces, the number of generated CALLs, the time
    /// budget and the output directory (the same at publish and in the standalone run).
    /// </summary>
    public static IReadOnlyList<string> Arguments(string layouts, string conditions, string traces, string outDir) =>
    [
        "--layouts", layouts, "--conditions", conditions, "--traces", traces, "--generate", GeneratedCalls.ToString(),
        "--max-seconds", MaxSeconds.ToString(), "--out", outDir
    ];
}
