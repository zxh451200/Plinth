using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

public sealed class TaskDiagnosticsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-diagnostics-" + Guid.NewGuid().ToString("N"));
    public TaskDiagnosticsTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public async Task Cancellation_reaches_work_and_terminal_state()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.Enqueue("Import", "Demo", async (progress, token) =>
        {
            progress.Report(.25);
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(.25, tasks.Snapshot().Single().Progress);
        Assert.True(tasks.Cancel(id));
        await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkStatus.Cancelled, tasks.Snapshot().Single().Status);
        Assert.False(tasks.Cancel(id));
    }

    [Fact]
    public async Task Failure_has_identity_without_exception_secrets()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var id = tasks.Enqueue("Export", "Demo", (_, _) => throw new IOException("password=secret"));
        await tasks.WaitAsync(id);
        var item = tasks.Snapshot().Single();
        Assert.Equal(WorkStatus.Failed, item.Status);
        Assert.Equal(id.ToString(), item.Error!.CorrelationId);
        Assert.DoesNotContain("secret", item.Error.Message);
    }

    /// <summary>选中一行后,结果与错误字段必须真的渲染到界面;未选中时空态可见、详情收起。</summary>
    [AvaloniaFact]
    public async Task Selected_task_renders_details_and_empty_state_toggles()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var tasks = shell.Provider.GetRequiredService<TaskService>();

        var nav = shell.ViewModel.NavigationItems.Single(candidate => candidate.Id == ShellNavigation.TaskDiagnosticsId);
        shell.ViewModel.OpenTabCommand.Execute(nav);
        var page = (TaskDiagnosticsViewModel)shell.ViewModel.SelectedTab!.Content;
        page.RefreshTasks();
        shell.Layout();
        var view = shell.PageView<TaskDiagnosticsView>();
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Loc.Get("tasks.list.empty") && IsShown(text));

        var id = tasks.Enqueue("Export", "Diagnostics", (_, _) => throw new IOException("password=secret"));
        await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        var item = tasks.Snapshot().Single();
        page.RefreshTasks();
        page.SelectedItem = item;
        shell.Layout();

        Assert.DoesNotContain(view.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == Loc.Get("tasks.list.empty") && IsShown(text));
        Assert.Equal(item.Id, page.SelectedItem!.Id);
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == item.Error!.Code && IsShown(text));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == item.Error.Message && IsShown(text));
        Assert.Contains(view.GetVisualDescendants().OfType<TextBox>(),
            box => box.Text == item.Error.CorrelationId && IsShown(box));
    }

    /// <summary>界面真画得出来才算数:控件本身与全部祖先都必须可见。</summary>
    private static bool IsShown(Visual visual) =>
        visual.IsVisible && visual.GetVisualAncestors().OfType<Visual>().All(ancestor => ancestor.IsVisible);
}
