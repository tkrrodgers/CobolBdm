using System.Text.Json;

namespace MlCpu.Host.Services;

public sealed class MlCpuSettings
{
    public string CorpusRoot { get; set; } = "";
    public string CodeRoot { get; set; } = "";
    public string CopybooksRoot { get; set; } = "";
    public string JclRoot { get; set; } = "";
    public string ProcRoot { get; set; } = "";
    public string ControlCardsRoot { get; set; } = "";
    public string KbSiteDir { get; set; } = "";
    /// <summary>Promotion evidence for projections 5 and 7 (MLCPU_DOMAIN_EVIDENCE_ROOT / MLCPU_ORCHESTRATION_EVIDENCE_ROOT); empty = none, so promotions fail closed.</summary>
    public string DomainEvidenceRoot { get; set; } = "";
    public string OrchestrationEvidenceRoot { get; set; } = "";
    /// <summary>Authored normalized capabilities (capability-manifest.json, designs, evidence, approvals); default &lt;RepoRoot&gt;/capabilities/carddemo.</summary>
    public string CapabilitiesRoot { get; set; } = "";
    public string RepoRoot { get; set; } = "";
    public string SidecarUrl { get; set; } = "http://127.0.0.1:8079";
    public string ExportDir { get; set; } = "";
    public string ModelFrontend { get; set; } = "pragmatic";
    /// <summary>Onboarded projects live here (projects/&lt;id&gt;/); default &lt;RepoRoot&gt;/projects.</summary>
    public string WorkspaceRoot { get; set; } = "";
    /// <summary>Gemma 4 (first shard of the GGUF) and the llama.cpp directory with llama-server.</summary>
    public string GemmaModel { get; set; } = @"C:\models\gemma-4-31B-it-GGUF\BF16\gemma-4-31B-it-BF16-00001-of-00002.gguf";
    public string GemmaName { get; set; } = "gemma-4-31B-it";
    public string LlamaDir { get; set; } = @"C:\models\llama.cpp";
}

/// <summary>Settings from the "MlCpu" configuration section, overlaid with %LOCALAPPDATA%\MlCpu\settings.json.</summary>
public sealed class ConfigService
{
    public MlCpuSettings Settings { get; }

    public ConfigService(IConfiguration config, IWebHostEnvironment env)
    {
        Settings = config.GetSection("MlCpu").Get<MlCpuSettings>() ?? new MlCpuSettings();
        var file = config["MlCpu:SettingsFile"] ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MlCpu", "settings.json");
        if (File.Exists(file))
        {
            var overlay = JsonSerializer.Deserialize<MlCpuSettings>(File.ReadAllText(file), new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (overlay is not null)
                foreach (var p in typeof(MlCpuSettings).GetProperties())
                    if (p.GetValue(overlay) is string s && s.Length > 0) p.SetValue(Settings, s);
        }
        if (Settings.RepoRoot.Length == 0) Settings.RepoRoot = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "..", ".."));
        // Relative estate paths are relative to the repository root.
        if (Settings.CorpusRoot.Length > 0 && !Path.IsPathRooted(Settings.CorpusRoot))
            Settings.CorpusRoot = Path.GetFullPath(Path.Combine(Settings.RepoRoot, Settings.CorpusRoot));
        if (Settings.CodeRoot.Length == 0 && Settings.CorpusRoot.Length > 0) Settings.CodeRoot = Path.Combine(Settings.CorpusRoot, "cbl");
        if (Settings.JclRoot.Length == 0 && Settings.CorpusRoot.Length > 0) Settings.JclRoot = Path.Combine(Settings.CorpusRoot, "jcl");
        if (Settings.ProcRoot.Length == 0 && Settings.CorpusRoot.Length > 0) Settings.ProcRoot = Path.Combine(Settings.CorpusRoot, "proc");
        if (Settings.ControlCardsRoot.Length == 0 && Settings.CorpusRoot.Length > 0) Settings.ControlCardsRoot = Path.Combine(Settings.CorpusRoot, "ctl");
        if (Settings.KbSiteDir.Length == 0) Settings.KbSiteDir = Path.Combine(Settings.RepoRoot, "work", "kb-site");
        if (Settings.ExportDir.Length == 0) Settings.ExportDir = Path.Combine(Settings.RepoRoot, "work", "export");
        if (Settings.CapabilitiesRoot.Length == 0) Settings.CapabilitiesRoot = Path.Combine(Settings.RepoRoot, "capabilities", "carddemo");
        if (Settings.WorkspaceRoot.Length == 0) Settings.WorkspaceRoot = Path.Combine(Settings.RepoRoot, "projects");
        else if (!Path.IsPathRooted(Settings.WorkspaceRoot)) Settings.WorkspaceRoot = Path.GetFullPath(Path.Combine(Settings.RepoRoot, Settings.WorkspaceRoot));
        var sidecar = Environment.GetEnvironmentVariable("MLCPU_JAVA_SIDECAR");
        if (!string.IsNullOrEmpty(sidecar)) Settings.SidecarUrl = sidecar;
        _overlayFile = file;
    }

    readonly string _overlayFile = "";

    /// <summary>Persists the given settings (by property name) into the user's overlay (%LOCALAPPDATA%\MlCpu\settings.json) and applies them.</summary>
    public void SaveOverlay(IReadOnlyDictionary<string, string> values)
    {
        var overlay = File.Exists(_overlayFile)
            ? System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(_overlayFile)) as System.Text.Json.Nodes.JsonObject ?? []
            : [];
        foreach (var (k, v) in values)
        {
            overlay[k] = v;
            typeof(MlCpuSettings).GetProperty(k)?.SetValue(Settings, v);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_overlayFile)!);
        File.WriteAllText(_overlayFile, overlay.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
