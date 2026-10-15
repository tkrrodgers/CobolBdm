namespace MlCpu.Host.Services.Workspace;

/// <summary>
/// Every long action of the UI and the headless API, started as a background <see cref="Operation"/> (one per project and
/// kind at a time; the models' work queues on the GPU).
/// </summary>
public sealed class WorkspaceActions(OperationService ops, ProjectService projects, OnboardingService onboarding, OracleService oracle,
    GraphService graphs, ConversionService conversion, EstateService estate)
{
    public const string Onboard = "onboard", Oracle = "oracle", Graphs = "graphs", Transpile = "transpile", Publish = "publish",
        Test = "test";

    public Operation StartOnboard(string name, string source, bool buildOracle, bool buildGraphs) =>
        ops.Start(OnboardingService.Slug(name), Onboard, $"Onboard {name}", async (op, ct) =>
        {
            var p = await onboarding.OnboardAsync(name, source, op, ct);
            if (buildGraphs) await graphs.RebuildAsync(p.Id, op, ct, 0.62, buildOracle ? 0.75 : 1);
            if (buildOracle && p.Oracle.Jobs.Count > 0) await oracle.BuildAndRunAsync(p.Id, op, ct, false, buildGraphs ? 0.75 : 0.62, 1);
            if (projects.Active is null) projects.Activate(p.Id);
            estate.Reload();
        });

    public Operation StartOracle(string projectId, bool force, IReadOnlyList<string>? only = null) =>
        ops.Start(projectId, Oracle, only is { Count: > 0 } ? $"Run the GnuCOBOL oracle on {string.Join(", ", only)}"
                : force ? "Rebuild and run the GnuCOBOL oracle" : "Build (cached) and run the GnuCOBOL oracle",
            (op, ct) => oracle.BuildAndRunAsync(projectId, op, ct, force, only: only is { Count: > 0 } ? only : null));

    public Operation StartGraphs(string projectId) =>
        ops.Start(projectId, Graphs, "Rebuild graphs (AST, call graph, DDG)", (op, ct) => graphs.RebuildAsync(projectId, op, ct));

    public Operation StartTranspile(string projectId) =>
        ops.Start(projectId, Transpile, "Transpile all programs", (op, ct) => conversion.TranspileAllAsync(projectId, op, ct));

    public Operation StartPublish(string projectId, string targetDir) =>
        ops.Start(projectId, Publish, "Publish the normalized Spring estate", async (op, ct) => op.Result = await conversion.PublishAsync(projectId, targetDir, op, ct));

    public Operation StartTest(string projectId, bool runTests = true) =>
        ops.Start(projectId, Test, runTests ? "Build & test against GnuCOBOL" : "Build & job checks against GnuCOBOL",
            async (op, ct) => op.Result = await conversion.BuildAndTestAsync(projectId, op, ct, runTests));

    public Operation StartModelDesign(ModelRunService model, string projectId, IReadOnlyList<string> programs, ModelOptions options) =>
        ops.Start(projectId, model.DesignKind, $"{model.Display} designs {string.Join(", ", programs)}", (op, ct) => model.DesignAsync(projectId, programs, options, op, ct), "gpu");

    public Operation StartModelPublish(ModelRunService model, string projectId, IReadOnlyList<string> programs, ModelOptions options) =>
        ops.Start(projectId, model.PublishKind, $"{model.Display} implements {string.Join(", ", programs)}", (op, ct) => model.PublishToServiceAsync(projectId, programs, options, op, ct), "gpu");

    /// <summary>Assembles the model's service from code already on disk (a run made by a script), without running the model.</summary>
    public Operation StartModelAssemble(ModelRunService model, string projectId, IReadOnlyList<string> programs) =>
        ops.Start(projectId, model.PublishKind, $"Assemble {model.Display}'s service", (op, ct) =>
        {
            op.Result = model.Assemble(projectId, programs, op);
            return Task.CompletedTask;
        });

    public Operation StartModelTest(ModelRunService model, string projectId) =>
        ops.Start(projectId, model.TestKind, $"Test {model.Display}'s service against GnuCOBOL", async (op, ct) => op.Result = await model.TestAsync(projectId, op, ct));
}
