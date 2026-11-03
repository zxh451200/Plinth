using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class TaskConcurrencyTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static TaskService Create() => new(NullLogger<TaskService>.Instance);
    private static WorkItem Item(TaskService tasks, Guid id) => tasks.Snapshot().Single(x => x.Id == id);

    [Fact]
    public async Task At_most_two_work_delegates_run_and_other_modules_wait()
    {
        var tasks = Create();
        var bothStarted = Signal();
        var release = Signal();
        var running = 0;
        var maximum = 0;
        var count = 0;
        var sync = new object();
        async Task Work(IProgress<double?> _, CancellationToken token)
        {
            lock (sync)
            {
                running++;
                maximum = Math.Max(maximum, running);
                if (++count == 2) bothStarted.TrySetResult();
            }
            try { await release.Task.WaitAsync(token); }
            finally { lock (sync) running--; }
        }
        var first = tasks.Enqueue("First", "One", Work);
        var second = tasks.Enqueue("Second", "Two", Work);
        try
        {
            await bothStarted.Task.WaitAsync(Timeout);
            var queued = Enumerable.Range(0, 8)
                .Select(i => tasks.Enqueue($"Queued {i}", "Other", Work)).ToArray();
            foreach (var id in queued)
            {
                Assert.Equal(WorkStatus.Waiting, Item(tasks, id).Status);
                Assert.Null(Item(tasks, id).StartedAt);
            }
            Assert.Equal(WorkStatus.Running, Item(tasks, first).Status);
            Assert.Equal(WorkStatus.Running, Item(tasks, second).Status);
            release.TrySetResult();
            await Task.WhenAll(tasks.Snapshot().Select(x => tasks.WaitAsync(x.Id))).WaitAsync(Timeout);
            Assert.Equal(2, maximum);
            Assert.Equal(10, count);
        }
        finally
        {
            release.TrySetResult();
            await tasks.StopAsync().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Cancelling_waiting_work_never_calls_it_or_sets_started_at()
    {
        var tasks = Create();
        var started = new[] { Signal(), Signal() };
        var release = Signal();
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            tasks.Enqueue("Blocker", "Test", async (_, token) =>
            {
                started[index].TrySetResult();
                await release.Task.WaitAsync(token);
            });
        }
        try
        {
            await Task.WhenAll(started.Select(x => x.Task)).WaitAsync(Timeout);
            var called = 0;
            var id = tasks.Enqueue("Waiting", "Test", (_, _) =>
            {
                Interlocked.Increment(ref called);
                return Task.CompletedTask;
            });
            Assert.True(tasks.Cancel(id));
            await tasks.WaitAsync(id).WaitAsync(Timeout);
            var item = Item(tasks, id);
            Assert.Equal(WorkStatus.Cancelled, item.Status);
            Assert.True(item.CancellationRequested);
            Assert.Null(item.StartedAt);
            Assert.NotNull(item.FinishedAt);
            Assert.Equal(0, called);
            Assert.False(release.Task.IsCompleted);
        }
        finally
        {
            release.TrySetResult();
            await tasks.StopAsync().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Failure_releases_slot_before_observers_finish_and_retry_uses_the_same_limit()
    {
        var tasks = Create();
        var blockerStarted = Signal();
        var failingStarted = Signal();
        var fail = Signal();
        var release = Signal();
        var observerStarted = Signal();
        using var observerRelease = new ManualResetEventSlim();
        var attempts = 0;
        WorkItem? observedFailure = null;
        var retryStarted = Signal();
        var nextStarted = Signal();
        tasks.Completed += item =>
        {
            if (item.Title != "Failing" || item.Status != WorkStatus.Failed) return;
            observedFailure = Item(tasks, item.Id);
            observerStarted.TrySetResult();
            if (!observerRelease.Wait(Timeout)) throw new TimeoutException();
        };
        tasks.Enqueue("Blocker", "Test", async (_, token) =>
        {
            blockerStarted.TrySetResult();
            await release.Task.WaitAsync(token);
        });
        var failed = tasks.Enqueue("Failing", "Test", async (_, token) =>
        {
            if (Interlocked.Increment(ref attempts) == 1)
            {
                failingStarted.TrySetResult();
                await fail.Task;
                throw new InvalidOperationException();
            }
            retryStarted.TrySetResult();
            await release.Task.WaitAsync(token);
        }, allowRetry: true);
        try
        {
            await Task.WhenAll(blockerStarted.Task, failingStarted.Task).WaitAsync(Timeout);
            var next = tasks.Enqueue("Next", "Other", async (_, token) =>
            {
                nextStarted.TrySetResult();
                await release.Task.WaitAsync(token);
            });
            fail.TrySetResult();
            await Task.WhenAll(observerStarted.Task, nextStarted.Task).WaitAsync(Timeout);
            Assert.Equal(WorkStatus.Failed, observedFailure!.Status);
            Assert.NotNull(observedFailure.FinishedAt);
            Assert.False(tasks.WaitAsync(failed).IsCompleted);
            Assert.Equal(WorkStatus.Running, Item(tasks, next).Status);
            var retry = tasks.Retry(failed);
            Assert.True(retry.HasValue);
            Assert.Equal(WorkStatus.Waiting, Item(tasks, retry.Value).Status);
            Assert.Null(Item(tasks, retry.Value).StartedAt);
            Assert.False(retryStarted.Task.IsCompleted);
            observerRelease.Set();
            await tasks.WaitAsync(failed).WaitAsync(Timeout);
            release.TrySetResult();
            await tasks.WaitAsync(retry.Value).WaitAsync(Timeout);
            Assert.True(retryStarted.Task.IsCompletedSuccessfully);
            Assert.Equal(failed, Item(tasks, retry.Value).RetryOf);
        }
        finally
        {
            fail.TrySetResult();
            observerRelease.Set();
            release.TrySetResult();
            await tasks.StopAsync().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Waiting_work_counts_toward_history_capacity_and_finished_history_can_be_trimmed()
    {
        var tasks = Create();
        var started = new[] { Signal(), Signal() };
        var release = Signal();
        var ids = new Guid[TaskService.MaxHistory];
        try
        {
            for (var i = 0; i < 2; i++)
            {
                var index = i;
                ids[i] = tasks.Enqueue("Running", "Test", async (_, token) =>
                {
                    started[index].TrySetResult();
                    await release.Task.WaitAsync(token);
                });
            }
            await Task.WhenAll(started.Select(x => x.Task)).WaitAsync(Timeout);
            for (var i = 2; i < ids.Length; i++)
                ids[i] = tasks.Enqueue("Waiting", "Test", (_, _) => Task.CompletedTask);
            Assert.Equal(TaskService.MaxHistory, tasks.Snapshot().Count);
            Assert.Equal(TaskService.MaxHistory - 2,
                tasks.Snapshot().Count(x => x.Status == WorkStatus.Waiting));
            Assert.Throws<InvalidOperationException>(() =>
                tasks.Enqueue("Overflow", "Test", (_, _) => Task.CompletedTask));
            release.TrySetResult();
            await Task.WhenAll(ids.Select(tasks.WaitAsync)).WaitAsync(Timeout);
            var next = tasks.Enqueue("Replacement", "Test", (_, _) => Task.CompletedTask);
            await tasks.WaitAsync(next).WaitAsync(Timeout);
            Assert.Equal(TaskService.MaxHistory, tasks.Snapshot().Count);
            Assert.Equal(WorkStatus.Succeeded, Item(tasks, next).Status);
        }
        finally
        {
            release.TrySetResult();
            await tasks.StopAsync().WaitAsync(Timeout);
        }
    }

    [Fact]
    public async Task Stop_cancels_waiters_and_waits_for_work_observers_and_cancellation_callbacks()
    {
        var tasks = Create();
        var started = new[] { Signal(), Signal() };
        var cancellationStarted = new[] { Signal(), Signal() };
        var workRelease = Signal();
        var callbackStarted = Signal();
        var observerStarted = Signal();
        using var callbackRelease = new ManualResetEventSlim();
        using var observerRelease = new ManualResetEventSlim();
        Guid observed = default;
        tasks.Completed += item =>
        {
            if (item.Id != observed) return;
            observerStarted.TrySetResult();
            if (!observerRelease.Wait(Timeout)) throw new TimeoutException();
        };
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var id = tasks.Enqueue("Running", "Test", async (_, token) =>
            {
                token.Register(() =>
                {
                    // Reenter the service from a cancellation callback.
                    tasks.Snapshot();
                    cancellationStarted[index].TrySetResult();
                    if (index != 0) return;
                    callbackStarted.TrySetResult();
                    if (!callbackRelease.Wait(Timeout)) throw new TimeoutException();
                });
                started[index].TrySetResult();
                await workRelease.Task;
                token.ThrowIfCancellationRequested();
            });
            if (i == 0) observed = id;
        }
        Task? stopping = null;
        try
        {
            await Task.WhenAll(started.Select(x => x.Task)).WaitAsync(Timeout);
            var called = 0;
            var queued = Enumerable.Range(0, 4).Select(i =>
                tasks.Enqueue($"Waiting {i}", "Other", (_, _) =>
                {
                    Interlocked.Increment(ref called);
                    return Task.CompletedTask;
                })).ToArray();
            stopping = tasks.StopAsync();
            await callbackStarted.Task.WaitAsync(Timeout);
            await Task.WhenAll(cancellationStarted.Select(x => x.Task)).WaitAsync(Timeout);
            await Task.WhenAll(queued.Select(tasks.WaitAsync)).WaitAsync(Timeout);
            foreach (var id in queued)
            {
                Assert.Equal(WorkStatus.Cancelled, Item(tasks, id).Status);
                Assert.Null(Item(tasks, id).StartedAt);
            }
            Assert.Equal(0, called);
            Assert.False(stopping.IsCompleted);
            Assert.Throws<InvalidOperationException>(() =>
                tasks.Enqueue("Rejected", "Test", (_, _) => Task.CompletedTask));
            workRelease.TrySetResult();
            await observerStarted.Task.WaitAsync(Timeout);
            Assert.False(stopping.IsCompleted);
            observerRelease.Set();
            await tasks.WaitAsync(observed).WaitAsync(Timeout);
            Assert.False(stopping.IsCompleted);
            callbackRelease.Set();
            await stopping.WaitAsync(Timeout);
            Assert.All(tasks.Snapshot(), item => Assert.Equal(WorkStatus.Cancelled, item.Status));
        }
        finally
        {
            workRelease.TrySetResult();
            observerRelease.Set();
            callbackRelease.Set();
            await (stopping ?? tasks.StopAsync()).WaitAsync(Timeout);
        }
    }
}
