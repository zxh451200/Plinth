using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class TaskNotificationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Observers_receive_final_snapshot_and_do_not_change_task_result(bool fail)
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        WorkItem? received = null;
        tasks.Completed += _ => throw new InvalidOperationException("observer");
        tasks.Completed += item =>
        {
            // Snapshot access is safe inside the observer.
            received = tasks.Snapshot().Single(x => x.Id == item.Id);
        };
        var id = tasks.Enqueue("Export", "Demo", (_, _) =>
            fail ? Task.FromException(new InvalidOperationException("secret")) : Task.CompletedTask);
        await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.NotNull(received);
        Assert.NotNull(received.FinishedAt);
        Assert.Equal(fail ? WorkStatus.Failed : WorkStatus.Succeeded, received.Status);
        Assert.Equal(received, tasks.Snapshot().Single());
    }

    [AvaloniaFact]
    public async Task Notification_opens_matching_task_and_stops_after_workspace_release()
    {
        using var shell = new Shell();
        var vm = shell.ViewModel;
        var tasks = shell.Provider.GetRequiredService<TaskService>();
        var status = vm.StatusMessage;
        var id = tasks.Enqueue("Export", "Demo", (_, _) => Task.CompletedTask);
        await tasks.WaitAsync(id);
        Dispatcher.UIThread.RunJobs();
        Assert.True(vm.HasTaskNotification);
        Assert.Equal(status, vm.StatusMessage);
        vm.ViewCompletedTaskCommand.Execute(null);
        Assert.Equal(id, Assert.IsType<TaskDiagnosticsViewModel>(vm.SelectedTab!.Content).SelectedItem!.Id);
        Assert.False(vm.HasTaskNotification);
        vm.ReleaseWorkspace();
        var later = tasks.Enqueue("Later", "Demo", (_, _) => Task.CompletedTask);
        await tasks.WaitAsync(later);
        Dispatcher.UIThread.RunJobs();
        Assert.False(vm.HasTaskNotification);
    }
}
