using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MlCpu.Cobol.Analysis;
using MlCpu.Cobol.Analysis.JavaBridge;
using MlCpu.Core;

namespace MlCpu.Host.Services;

public sealed class JavaTranspileResult
{
    public string Program { get; set; } = "";
    public bool Ok { get; set; }
    public bool Compiled { get; set; }
    public string Verdict { get; set; } = "";
    public string? JavaSource { get; set; }
    public string? ClassName { get; set; }
    public int Paragraphs { get; set; }
    public int Fields { get; set; }
    public int Lowered { get; set; }
    public int Abstained { get; set; }
    public List<string> AbstainedSteps { get; set; } = [];
    public List<IrFrontier> Frontiers { get; set; } = [];
    public List<string> Diagnostics { get; set; } = [];
    public string? Error { get; set; }
    public CobolProgram? Model { get; set; }
    public ProgramGraph? Graph { get; set; }
}

/// <summary>Client of the Java sidecar (§5.7): model → IR → POST /transpile-verify. 120 s timeout, no retries.</summary>
public sealed class JavaSidecarService(ConfigService config, ILogger<JavaSidecarService> log)
{
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(120) };
    static readonly string RuntimeCache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MlCpu", "runtime-cache");

    Uri Url(string path) => new(new Uri(config.Settings.SidecarUrl), path);

    public async Task<JsonNode?> HealthAsync(CancellationToken ct = default)
    {
        try { return JsonNode.Parse(await _http.GetStringAsync(Url("/health"), ct)); }
        catch (Exception e) { log.LogDebug(e, "sidecar health failed"); return null; }
    }

    /// <summary>The com.mlcpu.rt sources. The last good copy is kept on disk so assembly works while the sidecar is down.</summary>
    public async Task<Dictionary<string, string>> RuntimeAsync(CancellationToken ct = default)
    {
        try
        {
            var node = JsonNode.Parse(await _http.GetStringAsync(Url("/runtime"), ct))!["files"]!.AsObject();
            var files = node.ToDictionary(kv => kv.Key, kv => kv.Value!.GetValue<string>(), StringComparer.Ordinal);
            Directory.CreateDirectory(RuntimeCache);
            foreach (var (name, text) in files) await File.WriteAllTextAsync(Path.Combine(RuntimeCache, name), text, ct);
            return files;
        }
        catch (HttpRequestException) when (Directory.Exists(RuntimeCache))
        {
            return Directory.EnumerateFiles(RuntimeCache, "*.java").ToDictionary(p => Path.GetFileName(p), File.ReadAllText, StringComparer.Ordinal);
        }
    }

    public bool IsCobolProgram(string path) => CobolSourceClassifier.IsCobol(path);

    public async Task<JavaTranspileResult> TranspileAsync(string programPath, bool transpileOnly = true, CancellationToken ct = default)
    {
        var result = new JavaTranspileResult { Program = Path.GetFileNameWithoutExtension(programPath).ToUpperInvariant() };
        try
        {
            var model = CobolModelBuilder.BuildGroundedFull(programPath);
            var graph = ProgramGraphBuilder.Build(model, dataDeps: false); // the sidecar and the assembler never read data dependences
            var ir = IrExporter.Export(model, graph);
            result.Model = model;
            result.Graph = graph;
            result.Program = ir.Program.Id;
            result.Frontiers = ir.Frontiers;
            var request = IrExporter.TranspileRequest(ir, new JsonObject
            {
                ["battery"] = new JsonArray(new JsonObject { ["inputs"] = new JsonObject(), ["expect"] = new JsonObject() }),
                ["useLir"] = false,
                ["loopPolicy"] = "TERMINATE",
                ["includeJavaSource"] = true,
                ["transpileOnly"] = transpileOnly
            });
            using var content = new StringContent(request, Encoding.UTF8, "application/json");
            using var resp = await _http.PostAsync(Url("/transpile-verify"), content, ct);
            var r = JsonNode.Parse(await resp.Content.ReadAsStringAsync(ct))!;
            result.Compiled = r["compiled"]?.GetValue<bool>() ?? false;
            result.Verdict = r["verdict"]?.GetValue<string>() ?? "ERROR";
            result.JavaSource = r["javaSource"]?.GetValue<string>();
            result.ClassName = r["className"]?.GetValue<string>();
            result.Paragraphs = r["paragraphCount"]?.GetValue<int>() ?? 0;
            result.Fields = r["fieldCount"]?.GetValue<int>() ?? 0;
            result.Lowered = r["loweredStatements"]?.GetValue<int>() ?? 0;
            result.Abstained = r["abstainedStatements"]?.GetValue<int>() ?? 0;
            result.AbstainedSteps = r["abstained"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
            result.Diagnostics = r["diagnostics"]?.AsArray().Select(x => x!.GetValue<string>()).ToList() ?? [];
            result.Error = r["error"]?.GetValue<string>();
            result.Ok = result.Compiled && result.Verdict is "PASS" or "INCOMPLETE" or "TRANSPILED";
        }
        catch (Exception e)
        {
            result.Verdict = "ERROR";
            result.Error = e.Message;
        }
        return result;
    }
}
