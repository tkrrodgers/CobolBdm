using MlCpu.Host.Components;
using MlCpu.Host.Services;
using MlCpu.Host.Services.Workspace;

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
builder.Services.AddSingleton<WorkspaceActions>();

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

app.MapPost("/migrate/job-java/{job}", async (string job, string? exportDir, bool? includeCalled, JobJavaConversionService svc, JavaProjectAssembler assembler) =>
{
    if (string.IsNullOrWhiteSpace(exportDir)) return Results.BadRequest(new { error = "exportDir is required" });
    var conv = await svc.ConvertAsync(job, includeCalled ?? true);
    if (conv.Project is null) return Results.UnprocessableEntity(new { job, error = conv.Scope is null ? "job not found" : "no program compiled", results = conv.Results.Select(r => new { r.Program, r.Verdict, r.Error }) });
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
app.MapPost("/migrate/estate-java", async (string jobs, string? exportDir, string? name, bool? partition, JobJavaConversionService svc, JavaProjectAssembler assembler) =>
{
    if (string.IsNullOrWhiteSpace(exportDir)) return Results.BadRequest(new { error = "exportDir is required" });
    var list = jobs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    var conv = await svc.ConvertEstateAsync(name ?? "estate", list, partition ?? true);
    if (conv.Project is null) return Results.UnprocessableEntity(new { jobs = list, error = conv.Scope is null ? "no job found" : "no program compiled" });
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
    oracle = p.Oracle.Status, published = p.Publish.Last, gemma = p.Gemma
}), indented));
api.MapGet("/projects/{id}", (string id, ProjectService ps) => ps.Get(id) is { } p ? Results.Json(p, indented) : Results.NotFound());
api.MapPost("/projects/onboard", (string name, string source, bool? oracle, bool? graphs, WorkspaceActions a) =>
    Results.Json(OpJson(a.StartOnboard(name, source, oracle ?? true, graphs ?? true))));
api.MapPost("/projects/{id}/activate", (string id, ProjectService ps, EstateService es) => { ps.Activate(id); es.Reload(); return Results.Ok(new { active = id }); });
api.MapPost("/projects/{id}/oracle", (string id, bool? force, WorkspaceActions a) => Results.Json(OpJson(a.StartOracle(id, force ?? false))));
api.MapPost("/projects/{id}/graphs", (string id, WorkspaceActions a) => Results.Json(OpJson(a.StartGraphs(id))));
api.MapPost("/projects/{id}/transpile", (string id, WorkspaceActions a) => Results.Json(OpJson(a.StartTranspile(id))));
api.MapPost("/projects/{id}/publish", (string id, string? dir, WorkspaceActions a, ProjectService ps) =>
    Results.Json(OpJson(a.StartPublish(id, string.IsNullOrWhiteSpace(dir) ? ps.Paths(ps.Get(id)!).Publish : dir))));
api.MapPost("/projects/{id}/test", (string id, WorkspaceActions a) => Results.Json(OpJson(a.StartTest(id))));
api.MapPost("/projects/{id}/gemma/design", (string id, string programs, bool? outline, int? attempts, WorkspaceActions a) =>
    Results.Json(OpJson(a.StartGemmaDesign(id, programs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        new GemmaOptions(DesignAttempts: attempts ?? 3, AstOutline: outline ?? true)))));
api.MapPost("/projects/{id}/gemma/publish", (string id, string programs, bool? outline, bool? examples, int? attempts, WorkspaceActions a) =>
    Results.Json(OpJson(a.StartGemmaPublish(id, programs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        new GemmaOptions(ImplementationAttempts: attempts ?? 15, AstOutline: outline ?? true, WorkedExamples: examples ?? true)))));
api.MapPost("/projects/{id}/gemma/test", (string id, WorkspaceActions a) => Results.Json(OpJson(a.StartGemmaTest(id))));
api.MapGet("/projects/{id}/gemma/status", (string id, ProjectService ps, GemmaService g) => ps.Get(id) is { } p
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

app.Run();
