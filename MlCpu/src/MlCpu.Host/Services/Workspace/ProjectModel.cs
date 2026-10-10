using System.Text.Json;
using System.Text.Json.Serialization;

namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// One onboarded COBOL estate (projects/&lt;id&gt;/project.json, schema mlcpu.project.v1). Paths are relative to the project
/// directory unless rooted. The project directory holds the cloned source, the GnuCOBOL oracle (built once, cached by
/// content hash), the graphs, the transpiled Java, the published estate and the Gemma runs.
/// </summary>
public sealed class ProjectDefinition
{
    public string Schema { get; set; } = "mlcpu.project.v1";
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Where the source came from (a directory or a git URL).</summary>
    public string Origin { get; set; } = "";
    public string OnboardedAt { get; set; } = "";
    public ProjectLayout Layout { get; set; } = new();
    public List<ProgramEntry> Programs { get; set; } = [];
    public List<ExcludedProgram> Excluded { get; set; } = [];
    public OracleDefinition Oracle { get; set; } = new();
    public PublishDefinition Publish { get; set; } = new();
    public GemmaState Gemma { get; set; } = new();
    public List<string> Notes { get; set; } = [];
}

public sealed class ProjectLayout
{
    /// <summary>The cloned source tree (relative to the project directory).</summary>
    public string SourceDir { get; set; } = "source";
    /// <summary>Directories (relative to <see cref="SourceDir"/>) searched for each kind of member.</summary>
    public List<string> CodeRoots { get; set; } = [];
    public List<string> CopybookRoots { get; set; } = [];
    public List<string> JclRoots { get; set; } = [];
    public List<string> ProcRoots { get; set; } = [];
    public List<string> ControlCardRoots { get; set; } = [];
    public List<string> DataRoots { get; set; } = [];
}

/// <summary>A COBOL program of the estate. <see cref="Kind"/>: batch (a job step runs it), subprogram (only CALLed), online (EXEC CICS), unreferenced.</summary>
public sealed class ProgramEntry
{
    public string Id { get; set; } = "";
    /// <summary>Path relative to the source directory.</summary>
    public string Path { get; set; } = "";
    public string Kind { get; set; } = "batch";
    public int Lines { get; set; }
    public List<string> CalledBy { get; set; } = [];
    public List<string> Jobs { get; set; } = [];
}

public sealed record ExcludedProgram(string Path, string Reason);

/// <summary>How the GnuCOBOL oracle is produced and what it is compared on.</summary>
public sealed class OracleDefinition
{
    /// <summary>"generic" (JCL-driven, this host) or "carddemo-script" (tools/carddemo/Invoke-CardDemoParity.ps1).</summary>
    public string Provider { get; set; } = "generic";
    /// <summary>The pinned batch clock (FUNCTION CURRENT-DATE, ACCEPT FROM DATE/TIME) on both sides.</summary>
    public string Clock { get; set; } = "2022-07-05T10:11:12-07:00";
    public List<string> CobcFlags { get; set; } = ["-std=ibm", "-fsign=EBCDIC", "-fstatic-call", "-Wno-others"];
    /// <summary>Jobs run in this order on one data directory (the output of one is the input of the next).</summary>
    public List<string> Jobs { get; set; } = [];
    /// <summary>Initial datasets, by DSN, prepared from source files.</summary>
    public List<DatasetDefinition> Datasets { get; set; } = [];
    /// <summary>Scenarios beyond "base": projects/&lt;id&gt;/scenarios/&lt;name&gt;/ files override datasets by source file name.</summary>
    public List<string> Scenarios { get; set; } = [];
    /// <summary>Explicit job checks (carddemo-script); the generic provider derives them from its runs.</summary>
    public List<JobCheck> JobChecks { get; set; } = [];
    /// <summary>Root the job check paths are relative to (carddemo-script: the script's work directory).</summary>
    public string? JobCheckRoot { get; set; }
    public OracleStatus? Status { get; set; }
}

/// <summary>A dataset image prepared from a source file. Formats: fixed, variable (4-byte RDW), line, ksds (fixed records in key order).</summary>
public sealed class DatasetDefinition
{
    public string Dsn { get; set; } = "";
    /// <summary>Source file relative to the source directory; null = starts empty.</summary>
    public string? Source { get; set; }
    public string Format { get; set; } = "fixed";
    public int Lrecl { get; set; }
    public int MinRecord { get; set; }
    /// <summary>Primary key "offset,length" (1-based offset, as in IDCAMS) for ksds.</summary>
    public string? Key { get; set; }
    public List<string> AlternateKeys { get; set; } = [];
    /// <summary>Which program FD this layout came from (for the reader).</summary>
    public string? From { get; set; }
}

/// <summary>A job run against the oracle: stage <see cref="Inputs"/> (DSN → file), run the job, compare <see cref="Outputs"/> ("data:DSN" or "sysout:NAME" → oracle file).</summary>
public sealed class JobCheck
{
    public string Job { get; set; } = "";
    public string Scenario { get; set; } = "base";
    public Dictionary<string, string> Inputs { get; set; } = [];
    public Dictionary<string, string> Outputs { get; set; } = [];
}

public sealed class OracleStatus
{
    public string BuiltAt { get; set; } = "";
    public int Units { get; set; }
    public int UnitsCached { get; set; }
    public string RunAt { get; set; } = "";
    public List<string> Scenarios { get; set; } = [];
    public List<string> Problems { get; set; } = [];
}

public sealed class PublishDefinition
{
    /// <summary>Jobs in the published estate (empty = every job that runs an estate program and has oracle checks).</summary>
    public List<string> Jobs { get; set; } = [];
    /// <summary>Programs with a normalized capability (Gemma's candidates); empty = every batch program and subprogram the jobs reach.</summary>
    public List<string> Capabilities { get; set; } = [];
    /// <summary>Authored capabilities root (capability-manifest.json, …); empty = generated into projects/&lt;id&gt;/capabilities at publish.</summary>
    public string? CapabilitiesRoot { get; set; }
    public PublishRecord? Last { get; set; }
}

public sealed class PublishRecord
{
    public string Path { get; set; } = "";
    public string At { get; set; } = "";
    public int Programs { get; set; }
    public int Capabilities { get; set; }
    public TestRecord? LastTest { get; set; }
}

public sealed class TestRecord
{
    public string At { get; set; } = "";
    public int Tests { get; set; }
    public int Failed { get; set; }
    public int Outputs { get; set; }
    public int Match { get; set; }
    public string Implementation { get; set; } = "legacy";
}

public sealed class GemmaState
{
    public List<string> Designed { get; set; } = [];
    public string? ServicePath { get; set; }
    public TestRecord? LastTest { get; set; }
}

public static class ProjectJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true
    };
}
