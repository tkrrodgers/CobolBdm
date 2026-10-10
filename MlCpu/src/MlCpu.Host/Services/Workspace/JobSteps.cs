using System.Text.RegularExpressions;
using MlCpu.CobolKb.Knowledge;

namespace MlCpu.Host.Services.Workspace;

/// <summary>Step identifiers exactly as the generated JobDefinition names them (StepId enum), so SYSOUT files line up on both sides.</summary>
public static class JobSteps
{
    public static List<(JclStep Step, string Id)> Ids(JclJob job)
    {
        var ids = new List<(JclStep, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var s in job.Expanded)
        {
            var id = Regex.Replace(s.QualifiedStep, "[^A-Z0-9]", "_");
            if (!seen.Add(id)) id += "_" + s.Order;
            ids.Add((s, id));
        }
        return ids;
    }

    /// <summary>DSN as the job engine stores it: data/&lt;DSN&gt; (a GDG generation maps to its base name).</summary>
    public static string DataFile(string dataDir, string dsn) => Path.Combine(dataDir, dsn);
}
