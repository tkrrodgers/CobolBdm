using MlCpu.Host.Components;
using MlCpu.Host.Services;
using MlCpu.Host.Services.Workspace;
using MlCpu.Host.Services.Knowledge;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents().AddInteractiveServerComponents();
builder.Services.AddSingleton<ConfigService>();
builder.Services.AddSingleton<ProjectService>();
builder.Services.AddSingleton<OperationService>();
builder.Services.AddSingleton<EstateService>();
builder.Services.AddSingleton<JavaSidecarService>();
builder.Services.AddSingleton<JavaProjectAssembler>();
builder.Services.AddSingleton<JobJavaConversionService>();
builder.Services.AddSingleton<ParityService>();
builder.Services.AddSingleton<OnboardingService>();
builder.Services.AddSingleton<GnuCobolOracle>();
builder.Services.AddSingleton<OracleService>();
builder.Services.AddSingleton<GraphService>();
builder.Services.AddSingleton<CapabilityKitBuilder>();
builder.Services.AddSingleton<JobParityService>();
builder.Services.AddSingleton<ConversionService>();
builder.Services.AddSingleton<GemmaService>();
builder.Services.AddSingleton<MuseService>();
builder.Services.AddSingleton<ModelRunners>();
builder.Services.AddSingleton<ForwardService>();
builder.Services.AddSingleton<WorkspaceActions>();
builder.Services.AddSingleton<ReferenceSweepService>();
builder.Services.AddSingleton<KnowledgeService>();

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseAntiforgery();
app.MapStaticAssets();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

var indented = new System.Text.Json.JsonSerializerOptions(ProjectJson.Options);

// Headless entry points (§10).
app.MapGet("/health", async (JavaSidecarService sidecar) =>
{
    var h = await sidecar.HealthAsync();
    return h is null ? Results.Json(new { status = "sidecar-unreachable" }, statusCode: 503) : Results.Json(new { status = "ok", sidecar = h });
});

app.MapPost("/migrate/job-java/{job}", async (string job, string? exportDir, bool? includeCalled, bool? composeStorage, JobJavaConversionService svc, JavaProjectAssembler assembler) =>
{
    if (string.IsNullOrWhiteSpace(exportDir)) return Results.BadRequest(new { error = "exportDir is required" });
    var conv = await svc.ConvertAsync(job, includeCalled ?? true, composeStorage: composeStorage ?? false);
    if (conv.Project is null) return Results.UnprocessableEntity(new
    {
        job,
        error = conv.Scope is null ? "job not found" : composeStorage == true ? "composed storage did not compile for every program" : "no program compiled",
        results = conv.Results.Select(r => new { r.Program, r.Verdict, r.Error, r.Diagnostics })
    });
    var root = await assembler.PublishAsync(conv.Project, exportDir);
    return Results.Json(new
    {
        job = conv.JobName,
        projectRoot = root,
        programs = conv.Results.Count,
        compiled = conv.Results.Count(r => r.Compiled),
        verdicts = conv.Results.GroupBy(r => r.Verdict).ToDictionary(g => g.Key, g => g.Count()),
        lowered = conv.Project.Lowered,
        abstained = conv.Project.Abstained
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}).DisableAntiforgery();

// Several jobs as one project; partition=true applies projection 10 (Gradle module per business domain).
app.MapPost("/migrate/estate-java", async (string jobs, string? exportDir, string? name, bool? partition, bool? composeStorage, JobJavaConversionService svc, JavaProjectAssembler assembler) =>
{
    if (string.IsNullOrWhiteSpace(exportDir)) return Results.BadRequest(new { error = "exportDir is required" });
    var list = jobs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var conv = await svc.ConvertEstateAsync(name ?? "estate", list, partition ?? true, composeStorage: composeStorage ?? false);
    if (conv.Project is null) return Results.UnprocessableEntity(new
    {
        jobs = list,
        error = conv.Scope is null ? "no job found" : composeStorage == true ? "composed storage did not compile for every program" : "no program compiled",
        results = conv.Results.Select(r => new { r.Program, r.Verdict, r.Error, r.Diagnostics })
    });
    var root = await assembler.PublishAsync(conv.Project, exportDir);
    return Results.Json(new
    {
        jobs = list,
        projectRoot = root,
        programs = conv.Results.Count,
        compiled = conv.Results.Count(r => r.Compiled),
        projections = conv.Project.Projections?.Select(l => new { l.Pass, applied = l.Applied.Count, skipped = l.Skipped.Count })
    }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
}).DisableAntiforgery();

// Workspace: projects and their operations (the same actions the pages start).
var api = app.MapGroup("/api").DisableAntiforgery();
object OpJson(Operation o, bool log = false) => new
{
    o.Id, o.ProjectId, o.Kind, o.Title, o.Running, o.Progress, o.Status, o.Succeeded, o.Error, elapsedSeconds = Math.Round(o.Elapsed.TotalSeconds),
    log = log ? o.Log.TakeLast(200) : null, result = o.Finished is null ? null : o.Result
};
api.MapGet("/projects", (ProjectService ps) => Results.Json(ps.Projects.Select(p => new
{
    p.Id, p.Name, p.Origin, active = ps.Active?.Id == p.Id, programs = p.Programs.Count, jobs = p.Oracle.Jobs, jobChecks = p.Oracle.JobChecks.Count,
    oracle = p.Oracle.Status, published = p.Publish.Last, gemma = p.Gemma, muse = p.Muse
}), indented));
api.MapGet("/projects/{id}", (string id, ProjectService ps) => ps.Get(id) is { } p ? Results.Json(p, indented) : Results.NotFound());
api.MapPost("/projects/onboard", (string name, string source, bool? oracle, bool? graphs, WorkspaceActions a) =>
    Results.Json(OpJson(a.StartOnboard(name, source, oracle ?? true, graphs ?? true))));
api.MapPost("/projects/{id}/activate", (string id, ProjectService ps, EstateService es) => { ps.Activate(id); es.Reload(); return Results.Ok(new { active = id }); });
// only=a,b: those scenarios (and base) alone, e.g. a shadow run's days; the project's job checks and status stay as they are
api.MapPost("/projects/{id}/oracle", (string id, bool? force, string? only, WorkspaceActions a) => Results.Json(OpJson(a.StartOracle(id, force ?? false,
    only?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)))));
api.MapPost("/projects/{id}/graphs", (string id, WorkspaceActions a) => Results.Json(OpJson(a.StartGraphs(id))));
api.MapPost("/projects/{id}/transpile", (string id, WorkspaceActions a) => Results.Json(OpJson(a.StartTranspile(id))));
api.MapPost("/projects/{id}/publish", (string id, string? dir, bool? composeStorage, WorkspaceActions a, ProjectService ps) =>
    Results.Json(OpJson(a.StartPublish(id, string.IsNullOrWhiteSpace(dir) ? ps.Paths(ps.Get(id)!).Publish : dir, composeStorage ?? false))));
api.MapPost("/projects/{id}/test", (string id, bool? tests, WorkspaceActions a) => Results.Json(OpJson(a.StartTest(id, tests ?? true))));
// A local model's steps (gemma, muse): design, publish the design to a service (implement + assemble), assemble from code a
// script wrote, test the service against GnuCOBOL. debug=true sends the focused debug prompt on repair turns; warm=true starts
// from the model's previous best code. Unset options take the model's defaults (ModelRunService.Defaults).
static string[] Programs(string programs) => programs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
api.MapPost("/projects/{id}/{model}/design", (string id, string model, string programs, bool? outline, int? attempts, WorkspaceActions a, ModelRunners m) =>
    m.Get(model) is not { } svc ? Results.NotFound()
        : Results.Json(OpJson(a.StartModelDesign(svc, id, Programs(programs),
            svc.Defaults with { DesignAttempts = attempts ?? svc.Defaults.DesignAttempts, AstOutline = outline ?? svc.Defaults.AstOutline }))));
api.MapPost("/projects/{id}/{model}/publish", (string id, string model, string programs, bool? outline, bool? examples, int? attempts, bool? debug, bool? warm,
        WorkspaceActions a, ModelRunners m) =>
    m.Get(model) is not { } svc ? Results.NotFound()
        : Results.Json(OpJson(a.StartModelPublish(svc, id, Programs(programs), svc.Defaults with
        {
            ImplementationAttempts = attempts ?? svc.Defaults.ImplementationAttempts, AstOutline = outline ?? svc.Defaults.AstOutline,
            WorkedExamples = examples ?? svc.Defaults.WorkedExamples, DebugMode = debug ?? svc.Defaults.DebugMode, WarmStart = warm ?? svc.Defaults.WarmStart
        }))));
api.MapPost("/projects/{id}/{model}/assemble", (string id, string model, string programs, WorkspaceActions a, ModelRunners m) =>
    m.Get(model) is not { } svc ? Results.NotFound() : Results.Json(OpJson(a.StartModelAssemble(svc, id, Programs(programs)))));
api.MapPost("/projects/{id}/{model}/test", (string id, string model, WorkspaceActions a, ModelRunners m) =>
    m.Get(model) is not { } svc ? Results.NotFound() : Results.Json(OpJson(a.StartModelTest(svc, id))));
// The command lines the page would run (after `dotnet MlCpu.DesignModel.dll`), for a script that runs the same steps.
api.MapGet("/projects/{id}/{model}/commands", (string id, string model, string programs, bool? debug, ProjectService ps, ModelRunners m) =>
    ps.Get(id) is not { } p || m.Get(model) is not { } svc ? Results.NotFound()
        : Results.Json(new
        {
            design = svc.DesignArgs(p, Programs(programs), svc.Defaults), implement = svc.ImplementArgs(p, Programs(programs), svc.Defaults with { DebugMode = debug ?? svc.Defaults.DebugMode }),
            env = new { MLCPU_REASONING_BUDGET = svc.ReasoningBudget }
        }, indented));
// Model evaluation: any assembled service (a copy of the published project with a model's capability classes) is tested
// exactly as Gemma's is: every test, then each job with --mlcpu.implementation=normalized against the GnuCOBOL oracle.
// The report goes to runs/<label>/report.json.
api.MapPost("/projects/{id}/eval/verify", (string id, string service, string label, ProjectService ps, OperationService ops, JobParityService parity) =>
    ps.Get(id) is not { } p ? Results.NotFound()
        : Results.Json(OpJson(ops.Start(id, "eval-verify", $"Verify {label}", async (op, ct) =>
            op.Result = await parity.BuildAndTestAsync(p, service, "normalized", label, op, 0, 1, ct)))));
// Reference sweep (gate G5 / G6b over the full corpus): a built service — by default the published estate (legacy) or
// Gemma's service (normalized) — runs every scenario the reference oracle has whose inputs are under `inputs`, in parallel,
// and each job output is compared with the reference. Report: runs/sweep-<implementation>-<time>/REPORT.md.
api.MapPost("/projects/{id}/reference-sweep", (string id, string inputs, string? implementation, string? service, string? filter, int? parallel,
        ProjectService ps, OperationService ops, ReferenceSweepService sweep) =>
{
    if (ps.Get(id) is not { } p) return Results.NotFound();
    var impl = implementation ?? "legacy";
    var dir = !string.IsNullOrWhiteSpace(service) ? service : impl == "normalized" ? p.Gemma.ServicePath : p.Publish.Last?.Path;
    if (string.IsNullOrWhiteSpace(dir)) return Results.BadRequest(new { error = "no service: publish (legacy) or assemble Gemma's service (normalized) first, or pass service=" });
    return Results.Json(OpJson(ops.Start(id, "reference-sweep", $"Reference sweep ({impl}, {filter ?? "*"})", async (op, ct) =>
        op.Result = await sweep.RunAsync(id, dir, impl, inputs, filter, parallel ?? Math.Max(1, Environment.ProcessorCount / 3), op, ct))));
});
api.MapGet("/projects/{id}/{model}/status", (string id, string model, ProjectService ps, ModelRunners m) => ps.Get(id) is { } p && m.Get(model) is { } g
    ? Results.Json(g.Capabilities(p).Select(c => g.Status(p, c.ProgramId) with { Design = null }), indented) : Results.NotFound());
api.MapGet("/operations", (string? project, OperationService ops) =>
    Results.Json(ops.All.Where(o => project is null || o.ProjectId == project).Select(o => OpJson(o)), indented));
api.MapGet("/operations/{opId}", (string opId, OperationService ops) => ops.Get(opId) is { } o ? Results.Json(OpJson(o, log: true), indented) : Results.NotFound());
api.MapPost("/operations/{opId}/cancel", (string opId, OperationService ops) =>
{
    if (ops.Get(opId) is not { } o) return Results.NotFound();
    o.Cancellation.Cancel();
    return Results.Ok();
});

// Knowledge base: search, catalogs, glossary, grounding packets (knowledgeBaseGroundGemma4.md §10).
app.MapKnowledgeApi();

app.Run();
