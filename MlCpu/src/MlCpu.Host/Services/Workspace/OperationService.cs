using System.Collections.Concurrent;

namespace MlCpu.Host.Services.Workspace;

/// <summary>A long-running action (onboard, build the oracle, transpile all, publish, test, Gemma) with progress and a log tail.</summary>
public sealed class Operation
{
    readonly object _gate = new();
    readonly List<string> _log = [];
    DateTime _lastSignal = DateTime.MinValue;
    internal Action<Operation>? Signal;

    public required string Id { get; init; }
    public required string ProjectId { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public DateTime Started { get; } = DateTime.Now;
    public DateTime? Finished { get; private set; }
    public bool Running => Finished is null;
    /// <summary>0..1 overall; null = indeterminate.</summary>
    public double? Progress { get; private set; } = 0;
    public string Status { get; private set; } = "starting";
    public string? Error { get; private set; }
    public bool Succeeded { get; private set; }
    public object? Result { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();

    public IReadOnlyList<string> Log { get { lock (_gate) return _log.ToList(); } }

    public TimeSpan Elapsed => (Finished ?? DateTime.Now) - Started;

    /// <summary>Sets progress (0..1, null = indeterminate) and/or the status line.</summary>
    public void Report(double? progress, string? status = null)
    {
        Progress = progress is null ? null : Math.Clamp(progress.Value, 0, 1);
        if (status is not null) Status = status;
        Notify(status is not null);
    }

    public void Info(string line)
    {
        lock (_gate)
        {
            _log.Add($"{DateTime.Now:HH:mm:ss} {line}");
            if (_log.Count > 2000) _log.RemoveRange(0, _log.Count - 2000);
        }
        Notify(false);
    }

    internal void Complete(bool ok, string? error)
    {
        Succeeded = ok;
        Error = error;
        if (ok) Progress = 1;
        Status = ok ? "done" : error is null ? "failed" : "failed: " + error;
        Finished = DateTime.Now;
        Notify(true);
    }

    void Notify(bool force)
    {
        var now = DateTime.Now;
        if (!force && (now - _lastSignal).TotalMilliseconds < 150) return;
        _lastSignal = now;
        Signal?.Invoke(this);
    }
}

/// <summary>
/// Runs operations in the background so a page can be left and re-entered while they run. One operation per project and
/// kind at a time; operations in the same exclusive group (the GPU) queue behind each other.
/// </summary>
public sealed class OperationService(ILogger<OperationService> log)
{
    readonly ConcurrentDictionary<string, Operation> _all = new();
    readonly ConcurrentDictionary<string, SemaphoreSlim> _groups = new();

    public event Action<Operation>? Changed;

    public IReadOnlyList<Operation> All => _all.Values.OrderByDescending(o => o.Started).ToList();

    public Operation? Get(string id) => _all.GetValueOrDefault(id);

    public Operation? Latest(string projectId, string kind) =>
        _all.Values.Where(o => o.ProjectId == projectId && o.Kind == kind).OrderByDescending(o => o.Started).FirstOrDefault();

    public bool IsRunning(string projectId, string kind) => Latest(projectId, kind)?.Running == true;

    public Operation Start(string projectId, string kind, string title, Func<Operation, CancellationToken, Task> work, string? exclusiveGroup = null)
    {
        if (Latest(projectId, kind) is { Running: true } running) return running;
        var op = new Operation { Id = Guid.NewGuid().ToString("n")[..12], ProjectId = projectId, Kind = kind, Title = title };
        op.Signal = o => Changed?.Invoke(o);
        _all[op.Id] = op;
        _ = Task.Run(async () =>
        {
            SemaphoreSlim? gate = null;
            try
            {
                if (exclusiveGroup is not null)
                {
                    gate = _groups.GetOrAdd(exclusiveGroup, _ => new SemaphoreSlim(1, 1));
                    if (gate.CurrentCount == 0) op.Report(null, $"waiting for {exclusiveGroup}");
                    await gate.WaitAsync(op.Cancellation.Token);
                }
                await work(op, op.Cancellation.Token);
                op.Complete(true, null);
            }
            catch (OperationCanceledException)
            {
                op.Info("cancelled");
                op.Complete(false, "cancelled");
            }
            catch (Exception e)
            {
                log.LogError(e, "operation {Kind} for {Project} failed", kind, projectId);
                op.Info("ERROR " + e.Message);
                op.Complete(false, e.Message);
            }
            finally
            {
                gate?.Release();
            }
        });
        Changed?.Invoke(op);
        return op;
    }
}
