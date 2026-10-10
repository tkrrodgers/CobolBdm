using System.Diagnostics;
using System.Text.Json;

namespace MlCpu.Host.Services;

public sealed record ParityRow(string Step, string Artifact, string Verdict, string Detail);

/// <summary>Runs the CardDemo parity harness (GnuCOBOL oracle vs generated Java) and reads its report.</summary>
public sealed class ParityService(ConfigService config, MlCpu.Host.Services.Workspace.ProjectService projects)
{
    readonly object _gate = new();
    readonly List<string> _log = [];
    public bool Running { get; private set; }
    public int? LastExitCode { get; private set; }
    public event Action? Changed;

    /// <summary>The harness's work directory: the active CardDemo project's oracle/script, else work/parity.</summary>
    string Work => projects.Active is { } p && p.Oracle.Provider == "carddemo-script"
        ? Path.Combine(projects.Paths(p).Dir, p.Oracle.JobCheckRoot ?? "oracle/script")
        : Path.Combine(config.Settings.RepoRoot, "work", "parity");

    string CardDemoApp => projects.Active is { } p && p.Oracle.Provider == "carddemo-script" ? projects.Resolve(p, "app") : config.Settings.CorpusRoot;

    public bool Applies => projects.Active is null || projects.Active.Oracle.Provider == "carddemo-script";

    string ReportPath => Path.Combine(Work, "parity-report.json");

    public IReadOnlyList<string> Log { get { lock (_gate) return _log.ToList(); } }

    public (DateTime? When, IReadOnlyList<ParityRow> Rows) Report()
    {
        if (!File.Exists(ReportPath)) return (null, []);
        using var doc = JsonDocument.Parse(File.ReadAllText(ReportPath));
        var rows = doc.RootElement.EnumerateArray().Select(e => new ParityRow(
            e.GetProperty("Step").GetString()!, e.GetProperty("Artifact").GetString()!,
            e.GetProperty("Verdict").GetString()!, e.GetProperty("Detail").GetString()!)).ToList();
        return (File.GetLastWriteTime(ReportPath), rows);
    }

    public void Start(bool skipConvert)
    {
        lock (_gate)
        {
            if (Running) return;
            Running = true;
            _log.Clear();
        }
        var script = Path.Combine(config.Settings.RepoRoot, "tools", "carddemo", "Invoke-CardDemoParity.ps1");
        var psi = new ProcessStartInfo("powershell.exe")
        {
            RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            WorkingDirectory = config.Settings.RepoRoot
        };
        foreach (var a in new[] { "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", script, "-CardDemo", CardDemoApp, "-Sidecar", config.Settings.SidecarUrl, "-Work", Work })
            psi.ArgumentList.Add(a);
        if (skipConvert) psi.ArgumentList.Add("-SkipConvert");
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        proc.OutputDataReceived += (_, e) => Append(e.Data);
        proc.ErrorDataReceived += (_, e) => Append(e.Data);
        proc.Exited += (_, _) =>
        {
            LastExitCode = proc.ExitCode;
            Running = false;
            Changed?.Invoke();
        };
        proc.Start();
        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();
    }

    void Append(string? line)
    {
        if (line is null) return;
        lock (_gate) _log.Add(line);
        Changed?.Invoke();
    }
}
