using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

public sealed class TaskRetryTests
{
    [Fact]
    public async Task Retry_is_opt_in_and_never_automatic()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var attempts = 0;
        var id = tasks.Enqueue("Import", "Demo", (_, _) =>
        {
            Interlocked.Increment(ref attempts);
            throw new IOException("password=secret");
        });
        await tasks.WaitAsync(id);
        var failed = Assert.Single(tasks.Snapshot());
        Assert.False(failed.CanRetry);
        Assert.False(failed.Error!.CanRetry);
        Assert.Null(tasks.Retry(id));
        Assert.Null(tasks.Retry(Guid.NewGuid()));
        Assert.Equal(1, attempts);
        Assert.DoesNotContain("secret", failed.Error.Message);
    }

    [Fact]
    public async Task Parallel_retry_clicks_create_one_linked_attempt_and_keep_the_original_failure()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var attempts = 0;
        var id = tasks.Enqueue("Export", "Demo", (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) == 1) throw new IOException("Unavailable");
            return Task.CompletedTask;
        }, allowRetry: true);
        await tasks.WaitAsync(id);
        Assert.True(Assert.Single(tasks.Snapshot()).CanRetry);
        Assert.Equal(1, attempts);
        var retries = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => Task.Run(() => tasks.Retry(id))));
        var childId = Assert.Single(retries.Where(result => result.HasValue))!.Value;
        Assert.NotEqual(id, childId);
        await tasks.WaitAsync(childId);
        Assert.Equal(2, attempts);
        var original = tasks.Snapshot().Single(item => item.Id == id);
        var child = tasks.Snapshot().Single(item => item.Id == childId);
        Assert.Equal(WorkStatus.Failed, original.Status);
        Assert.Equal(childId, original.RetriedAs);
        Assert.False(original.CanRetry);
        Assert.Equal(id, child.RetryOf);
        Assert.Equal(WorkStatus.Succeeded, child.Status);
        Assert.False(child.CanRetry);
        Assert.Null(tasks.Retry(childId));
    }

    [Fact]
    public async Task Running_cancelled_and_stopped_tasks_cannot_be_retried()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.Enqueue("Export", "Demo", async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        }, allowRetry: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(tasks.Retry(id));
        tasks.Cancel(id);
        await tasks.WaitAsync(id);
        Assert.Equal(WorkStatus.Cancelled, Assert.Single(tasks.Snapshot()).Status);
        Assert.Null(tasks.Retry(id));

        var failed = tasks.Enqueue("Export", "Demo", (_, _) => throw new IOException(), allowRetry: true);
        await tasks.WaitAsync(failed);
        await tasks.StopAsync();
        Assert.Null(tasks.Retry(failed));
    }

    [Fact]
    public async Task Each_attempt_executes_its_service_checks_again_and_has_its_own_error_id()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var checks = 0;
        var allowed = false;
        var id = tasks.Enqueue("Export", "Demo", (_, _) =>
        {
            Interlocked.Increment(ref checks);
            if (!allowed) throw new UnauthorizedAccessException("Not granted");
            throw new IOException("Disk unavailable");
        }, allowRetry: true);
        await tasks.WaitAsync(id);
        allowed = true;
        var child = tasks.Retry(id)!.Value;
        await tasks.WaitAsync(child);
        Assert.Equal(2, checks);
        Assert.Equal(id.ToString(), tasks.Snapshot().Single(item => item.Id == id).Error!.CorrelationId);
        var failedChild = tasks.Snapshot().Single(item => item.Id == child);
        Assert.Equal(child.ToString(), failedChild.Error!.CorrelationId);
        Assert.True(failedChild.CanRetry);
        Assert.NotEqual(id, tasks.Retry(child));
        await tasks.StopAsync();
    }

    [Fact]
    public async Task Service_declining_confirmation_is_cancelled_without_retry()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var id = tasks.Enqueue("Export", "Demo",
            (_, token) => throw new OperationCanceledException(token), allowRetry: true);
        await tasks.WaitAsync(id);
        var item = Assert.Single(tasks.Snapshot());
        Assert.Equal(WorkStatus.Cancelled, item.Status);
        Assert.Null(item.Error);
        Assert.False(item.CanRetry);
        Assert.Null(tasks.Retry(id));
    }

    [Fact]
    public async Task Stop_waits_for_cancellation_cleanup_after_work_has_finished()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var callbackStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cleanup = new ManualResetEventSlim();
        var id = tasks.Enqueue("Export", "Demo", async (_, token) =>
        {
            // Register 的回调在任务终止后仍可执行清理。
            token.Register(() =>
            {
                callbackStarted.SetResult();
                workFinished.SetResult();
                if (!cleanup.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException();
            });
            started.SetResult();
            await workFinished.Task;
        }, allowRetry: true);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopping = tasks.StopAsync();
        try
        {
            await callbackStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(stopping.IsCompleted);
            Assert.Null(tasks.Retry(id));
        }
        finally
        {
            cleanup.Set();
            await stopping.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task Retrying_old_history_remains_bounded()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var first = tasks.Enqueue("Export", "Demo", (_, _) => throw new IOException(), allowRetry: true);
        await tasks.WaitAsync(first);
        for (var index = 1; index < TaskService.MaxHistory; index++)
        {
            var id = tasks.Enqueue("Read", "Demo", (_, _) => Task.CompletedTask);
            await tasks.WaitAsync(id);
        }
        var child = tasks.Retry(first)!.Value;
        await tasks.WaitAsync(child);
        Assert.Equal(TaskService.MaxHistory, tasks.Snapshot().Count);
        Assert.Equal(first, tasks.Snapshot().Single(item => item.Id == child).RetryOf);
    }

    [AvaloniaFact]
    public async Task Task_centre_retry_selects_the_new_attempt_and_can_navigate_between_attempts()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var attempts = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.Enqueue("Export", "Demo", async (_, _) =>
        {
            if (Interlocked.Increment(ref attempts) > 1) await release.Task;
            throw new IOException();
        }, allowRetry: true);
        await tasks.WaitAsync(id);
        var vm = new TaskDiagnosticsViewModel(tasks, new DiagnosticService());
        var view = new TaskDiagnosticsView { DataContext = vm };
        vm.SelectTask(id);
        Assert.True(vm.RetryTaskCommand.CanExecute(null));
        var button = view.FindControl<Button>("RetryTaskButton")!;
        Assert.Same(vm.RetryTaskCommand, button.Command);
        vm.RetryTaskCommand.Execute(null);
        var child = vm.SelectedItem!.Id;
        Assert.NotEqual(id, child);
        Assert.False(vm.RetryTaskCommand.CanExecute(null));
        release.SetResult();
        await tasks.WaitAsync(child);
        vm.OpenPreviousAttemptCommand.Execute(null);
        Assert.Equal(id, vm.SelectedItem!.Id);
        Assert.False(vm.RetryTaskCommand.CanExecute(null));
        vm.OpenNextAttemptCommand.Execute(null);
        Assert.Equal(child, vm.SelectedItem!.Id);
        Assert.True(vm.RetryTaskCommand.CanExecute(null));
        Assert.Equal(2, vm.Items.Count);
    }
}
