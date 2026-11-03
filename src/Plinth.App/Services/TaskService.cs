using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Plinth.App.Localization;

namespace Plinth.App.Services;

public enum WorkStatus { Waiting, Running, Succeeded, Failed, Cancelled }

public sealed record TaskError(string Code, string Message, string NextStep, bool CanRetry, string CorrelationId);

public sealed record WorkItem(Guid Id, string Title, string Module, WorkStatus Status,
    double? Progress, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    bool CancellationRequested, TaskError? Error)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public TaskOutcome? Outcome { get; init; }
    [System.Text.Json.Serialization.JsonIgnore]
    public string ResultText => Outcome?.ResultText ?? "";
    public Guid? RetryOf { get; init; }
    public Guid? RetriedAs { get; init; }
    public bool CanRetry => Status == WorkStatus.Failed && FinishedAt.HasValue
        && Error?.CanRetry == true && !RetriedAs.HasValue;
    public TimeSpan Elapsed => (FinishedAt ?? DateTimeOffset.UtcNow) - (StartedAt ?? CreatedAt);
    public bool CanCancel => !CancellationRequested && Status is WorkStatus.Waiting or WorkStatus.Running;
    public string StatusText => CancellationRequested && (Status is WorkStatus.Waiting or WorkStatus.Running)
        ? Loc.Get("tasks.status.Cancelling") : Loc.Get(Status switch
        {
            WorkStatus.Waiting => "tasks.status.Waiting",
            WorkStatus.Running => "tasks.status.Running",
            WorkStatus.Succeeded => "tasks.status.Succeeded",
            WorkStatus.Failed => "tasks.status.Failed",
            _ => "tasks.status.Cancelled",
        });
    public string ProgressText => Progress.HasValue ? Progress.Value.ToString("P0") : Loc.Get("tasks.indeterminate");
    public string RetryText => Loc.Get(RetriedAs.HasValue ? "tasks.retry.started"
        : CanRetry ? "tasks.retry.allowed" : "tasks.retry.manual");
}

/// <summary>Process-local owner of global work. Snapshots are immutable; UI polls on its own thread.</summary>
public sealed class TaskService(ILogger<TaskService> logger)
{
    private sealed class State(WorkItem item, Func<IProgress<double?>, CancellationToken, Task<TaskOutcome?>> work, bool allowRetry)
    {
        public WorkItem Item = item;
        public Func<IProgress<double?>, CancellationToken, Task<TaskOutcome?>>? Work = allowRetry ? work : null;
        public readonly bool AllowRetry = allowRetry;
        public readonly CancellationTokenSource Cancellation = new();
        public Task Completion = Task.CompletedTask;
        public Task CancellationCompletion = Task.CompletedTask;
    }

    private readonly object _gate = new();
    private readonly Dictionary<Guid, State> _items = [];
    private readonly SemaphoreSlim _slots = new(2, 2);
    private bool _stopping;
    public const int MaxHistory = 200;
    /// <summary>Runs on the worker thread after the final snapshot is stored. Handlers must not block.</summary>
    public event Action<WorkItem>? Completed;

    public IReadOnlyList<WorkItem> Snapshot()
    {
        lock (_gate)
            return _items.Values.Select(x => x.Item).OrderByDescending(x => x.CreatedAt).ToArray();
    }

    /// <summary>allowRetry 仅用于模块已确认可以重新执行的操作；每次仍需重新授权和确认。</summary>
    public Guid Enqueue(string title, string module, Func<IProgress<double?>, CancellationToken, Task> work,
        bool allowRetry = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
        {
            return EnqueueCore(title, module, async (progress, token) =>
            {
                await work(progress, token).ConfigureAwait(false);
                return null;
            }, allowRetry);
        }
    }

    /// <summary>正常返回即承诺操作已完成；提交前取消应由回调抛出 OperationCanceledException。</summary>
    public Guid EnqueueWithResult(string title, string module,
        Func<IProgress<double?>, CancellationToken, Task<TaskOutcome>> work, bool allowRetry = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(module);
        ArgumentNullException.ThrowIfNull(work);
        lock (_gate)
            return EnqueueCore(title, module, async (progress, token) =>
                await work(progress, token).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("Missing task outcome."), allowRetry);
    }

    // 仅在 _gate 内调用；重试检查和新任务入队不可被另一次点击打断。
    private Guid EnqueueCore(string title, string module, Func<IProgress<double?>, CancellationToken, Task<TaskOutcome?>> work,
        bool allowRetry, Guid? retryOf = null)
    {
        if (_stopping) throw new InvalidOperationException("Task service is stopping.");
        foreach (var old in _items.Values.Where(x => x.Item.FinishedAt.HasValue
                         && x.Completion.IsCompleted && x.CancellationCompletion.IsCompleted)
                     .OrderBy(x => x.Item.CreatedAt).Take(Math.Max(0, _items.Count - MaxHistory + 1)).ToArray())
            _items.Remove(old.Item.Id);
        if (_items.Count >= MaxHistory) throw new InvalidOperationException("Task capacity reached.");
        var id = Guid.NewGuid();
        var state = new State(new WorkItem(id, title, module, WorkStatus.Waiting, null,
            DateTimeOffset.UtcNow, null, null, false, null)
        { RetryOf = retryOf }, work, allowRetry);
        _items.Add(id, state);
        state.Completion = Task.Run(() => ExecuteAsync(state, work));
        return id;
    }

    /// <summary>只重排显式允许的失败任务。原记录保持失败，新尝试拥有独立 ID 和关联日志。</summary>
    public Guid? Retry(Guid id)
    {
        lock (_gate)
        {
            if (_stopping || !_items.TryGetValue(id, out var state) || state.Work is not { } work || !state.Item.CanRetry)
                return null;
            var next = EnqueueCore(state.Item.Title, state.Item.Module, work, true, id);
            state.Item = state.Item with { RetriedAs = next };
            state.Work = null;
            return next;
        }
    }

    public Task WaitAsync(Guid id)
    {
        lock (_gate) return _items.TryGetValue(id, out var state)
            ? state.Completion : throw new KeyNotFoundException();
    }

    public async Task StopAsync()
    {
        State[] states;
        lock (_gate)
        {
            _stopping = true;
            states = _items.Values.ToArray();
            // Mark all waiting work before any worker can acquire the gate and start.
            foreach (var state in states) Cancel(state.Item.Id);
        }
        // 终态发布早于完成通知；取消回调也可能在工作结束后继续清理。
        await Task.WhenAll(states.SelectMany(state => new[] { state.Completion, state.CancellationCompletion }));
    }

    public bool Cancel(Guid id)
    {
        lock (_gate)
        {
            if (!_items.TryGetValue(id, out var state) || !state.Item.CanCancel) return false;
            state.Item = state.Item with { CancellationRequested = true };
            // Cancel callbacks run outside the lock; CTS remains alive until callbacks return.
            state.CancellationCompletion = Task.Run(() =>
            {
                try { state.Cancellation.Cancel(); }
                catch (AggregateException)
                {
                    logger.LogWarning("Task cancellation callback failed. TaskId {TaskId}", id);
                }
            });
            return true;
        }
    }

    private async Task ExecuteAsync(State state, Func<IProgress<double?>, CancellationToken, Task<TaskOutcome?>> work)
    {
        var id = state.Item.Id;
        var acquired = false;
        using var scope = logger.BeginScope(new Dictionary<string, object>
        {
            ["TaskId"] = id,
            ["CorrelationId"] = id,
            ["Module"] = state.Item.Module,
            ["EventKind"] = "Task",
        });
        if (state.Item.RetryOf is { } parent)
            logger.LogInformation("Task retry started. TaskId {TaskId}, RetryOf {RetryOf}", id, parent);
        try
        {
            await _slots.WaitAsync(state.Cancellation.Token).ConfigureAwait(false);
            acquired = true;
            lock (_gate)
            {
                if (state.Item.CancellationRequested) throw new OperationCanceledException(state.Cancellation.Token);
                state.Item = state.Item with { Status = WorkStatus.Running, StartedAt = DateTimeOffset.UtcNow };
            }
            logger.LogInformation("Task started. TaskId {TaskId}", id);
            var outcome = await work(new ProgressSink(value =>
            {
                lock (_gate)
                    if (state.Item.Status == WorkStatus.Running)
                        state.Item = state.Item with
                        {
                            Progress = value.HasValue && double.IsFinite(value.Value)
                                ? Math.Clamp(value.Value, 0, 1) : null,
                        };
            }), state.Cancellation.Token).ConfigureAwait(false);
            lock (_gate)
            {
                // A normal return is the callback's completion commitment: DB commits and
                // CSV publication cannot be undone by a late cancellation request.
                var rejected = outcome?.Kind == TaskOutcomeKind.Rejected;
                state.Item = state.Item with
                {
                    Outcome = outcome,
                    Status = rejected ? WorkStatus.Failed : WorkStatus.Succeeded,
                    Progress = rejected ? null : 1,
                    Error = rejected ? new TaskError("TASK_REJECTED", outcome!.Summary,
                        Loc.Get("tasks.error.next"), state.AllowRetry, id.ToString()) : null,
                };
            }
        }
        // 服务主动取消（如拒绝生产确认）也属于取消，不伪装为可重试失败。
        catch (OperationCanceledException)
        {
            lock (_gate) state.Item = state.Item with { Status = WorkStatus.Cancelled, Outcome = null, Progress = null };
        }
        catch (Exception ex)
        {
            // Do not put exception messages (paths, request bodies, credentials) in user state.
            logger.LogError("Task failed. TaskId {TaskId}, ExceptionType {ExceptionType}", id, ex.GetType().Name);
            lock (_gate) state.Item = state.Item with
            {
                Status = WorkStatus.Failed,
                Outcome = null,
                Progress = null,
                Error = new("TASK_FAILED", Loc.Get("tasks.error.failed"), Loc.Get("tasks.error.next"),
                    state.AllowRetry, id.ToString()),
            };
        }
        finally
        {
            WorkItem finished;
            Task cancellationCompletion;
            lock (_gate)
            {
                finished = state.Item = state.Item with { FinishedAt = DateTimeOffset.UtcNow };
                if (finished.Status != WorkStatus.Failed) state.Work = null;
                cancellationCompletion = state.CancellationCompletion;
            }
            // Only actual work holds a slot; observers remain part of Completion.
            if (acquired) _slots.Release();
            // No new cancellation is possible after the terminal snapshot. Do not wait
            // for callbacks here: they may themselves be waiting for Completion.
            _ = cancellationCompletion.ContinueWith(_ => state.Cancellation.Dispose(),
                CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            logger.LogInformation("Task finished. TaskId {TaskId}, Status {Status}", id, finished.Status);
            if (Completed is { } handlers)
                foreach (Action<WorkItem> handler in handlers.GetInvocationList())
                {
                    try { handler(finished); }
                    catch (Exception ex)
                    {
                        logger.LogWarning("Task completion observer failed. TaskId {TaskId}, ExceptionType {ExceptionType}",
                            id, ex.GetType().Name);
                    }
                }
        }
    }

    private sealed class ProgressSink(Action<double?> report) : IProgress<double?>
    {
        public void Report(double? value) => report(value);
    }
}
