namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// Every long action of the UI and the headless API, started as a background <see cref="Operation"/> (one per project and
/// kind at a time; Gemma's work queues on the GPU).
/// </summary>
public sealed class WorkspaceActions(OperationService ops, ProjectService projects, OnboardingService onboarding, OracleService oracle,
    GraphService graphs, ConversionService conversion, GemmaService gemma, EstateService estate)
{
    public const string Onboard = "onboard", Oracle = "oracle", Graphs = "graphs", Transpile = "transpile", Publish = "publish",
        Test = "test", GemmaDesign = "gemma-design", GemmaPublish = "gemma-publish", GemmaTest = "gemma-test";

    public Operation StartOnboard(string name, string source, bool buildOracle, bool buildGraphs) =>
        ops.Start(OnboardingService.Slug(name), Onboard, $"Onboard {name}", async (op, ct) =>
        {
            var p = await onboarding.OnboardAsync(name, source, op, ct);
            if (buildGraphs) await graphs.RebuildAsync(p.Id, op, ct, 0.62, buildOracle ? 0.75 : 1);
            if (buildOracle && p.Oracle.Jobs.Count > 0) await oracle.BuildAndRunAsync(p.Id, op, ct, false, buildGraphs ? 0.75 : 0.62, 1);
            if (projects.Active is null) projects.Activate(p.Id);
            estate.Reload();
        });

    public Operation StartOracle(string projectId, bool force) =>
        ops.Start(projectId, Oracle, force ? "Rebuild and run the GnuCOBOL oracle" : "Build (cached) and run the GnuCOBOL oracle",
            (op, ct) => oracle.BuildAndRunAsync(projectId, op, ct, force));

    public Operation StartGraphs(string projectId) =>
        ops.Start(projectId, Graphs, "Rebuild graphs (AST, call graph, DDG)", (op, ct) => graphs.RebuildAsync(projectId, op, ct));

    public Operation StartTranspile(string projectId) =>
        ops.Start(projectId, Transpile, "Transpile all programs", (op, ct) => conversion.TranspileAllAsync(projectId, op, ct));

    public Operation StartPublish(string projectId, string targetDir) =>
        ops.Start(projectId, Publish, "Publish the normalized Spring estate", async (op, ct) => op.Result = await conversion.PublishAsync(projectId, targetDir, op, ct));

    public Operation StartTest(string projectId, bool runTests = true) =>
        ops.Start(projectId, Test, runTests ? "Build & test against GnuCOBOL" : "Build & job checks against GnuCOBOL",
            async (op, ct) => op.Result = await conversion.BuildAndTestAsync(projectId, op, ct, runTests));

    public Operation StartGemmaDesign(string projectId, IReadOnlyList<string> programs, GemmaOptions options) =>
        ops.Start(projectId, GemmaDesign, $"Gemma 4 designs {string.Join(", ", programs)}", (op, ct) => gemma.DesignAsync(projectId, programs, options, op, ct), "gpu");

    public Operation StartGemmaPublish(string projectId, IReadOnlyList<string> programs, GemmaOptions options) =>
        ops.Start(projectId, GemmaPublish, $"Gemma 4 implements {string.Join(", ", programs)}", (op, ct) => gemma.PublishToServiceAsync(projectId, programs, options, op, ct), "gpu");

    public Operation StartGemmaTest(string projectId) =>
        ops.Start(projectId, GemmaTest, "Test Gemma's service against GnuCOBOL", async (op, ct) => op.Result = await gemma.TestAsync(projectId, op, ct));
}
