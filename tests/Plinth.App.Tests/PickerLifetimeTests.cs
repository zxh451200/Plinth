using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

// Real headless views and events, with only file-picker responses / slow reads substituted.
// These tests do not exercise native OS picker windows.
public sealed class PickerLifetimeTests
{
    [AvaloniaTheory]
    [InlineData("detach")]
    [InlineData("context")]
    [InlineData("host")]
    [InlineData("page")]
    [InlineData("stable")]
    public async Task Diagnostics_picker_uses_the_lifetime_at_open(string change)
    {
        var directory = Path.Combine(Path.GetTempPath(), "plinth-picker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        using var vm = new TaskDiagnosticsViewModel(tasks,
            new DiagnosticService(new LogAccessService(authenticationEnabled: false)))
        { IncludeLogs = false };
        var view = new TaskDiagnosticsView { DataContext = vm };
        var host = new Window { Content = view };
        host.Show();
        try
        {
            await vm.PreviewCommand.ExecuteAsync(null);
            Assert.True(vm.HasPreview);
            vm.Confirmed = true;
            var response = new TaskCompletionSource<string?>();
            var pending = view.ExportWithPickerAsync(_ => response.Task);
            Assert.False(pending.IsCompleted);
            switch (change)
            {
                case "detach":
                    host.Content = null;
                    host.Content = view; // Same VM and host still must not revive the picker.
                    break;
                case "context":
                    view.DataContext = null;
                    view.DataContext = vm;
                    break;
                case "host": host.Close(); break;
                case "page": vm.PageClosed(); break;
            }
            var path = Path.Combine(directory, "diagnostics.zip");
            response.SetResult(path);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            if (change == "stable")
            {
                Assert.Equal(WorkStatus.Succeeded, Assert.Single(tasks.Snapshot()).Status);
                Assert.True(File.Exists(path));
            }
            else
            {
                Assert.Empty(tasks.Snapshot());
                Assert.False(File.Exists(path));
            }
        }
        finally
        {
            host.Close();
            await tasks.StopAsync();
            Directory.Delete(directory, true);
        }
    }

    [AvaloniaFact]
    public async Task Diagnostics_old_picker_failure_does_not_change_the_reattached_page()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        using var vm = new TaskDiagnosticsViewModel(tasks,
            new DiagnosticService(new LogAccessService(authenticationEnabled: false)));
        var view = new TaskDiagnosticsView { DataContext = vm };
        var host = new Window { Content = view };
        host.Show();
        try
        {
            var response = new TaskCompletionSource<string?>();
            var pending = view.ExportWithPickerAsync(_ => response.Task);
            host.Content = null;
            host.Content = view;
            vm.StatusText = "new page state";
            response.SetException(new IOException("synthetic picker failure"));
            await pending;
            Assert.Equal("new page state", vm.StatusText);
            Assert.Empty(tasks.Snapshot());
        }
        finally
        {
            host.Close();
            await tasks.StopAsync();
        }
    }

    [AvaloniaTheory]
    [InlineData(false, "success")]
    [InlineData(false, "failure")]
    [InlineData(false, "cancel")]
    [InlineData(true, "success")]
    [InlineData(true, "failure")]
    [InlineData(true, "cancel")]
    public async Task Closing_csv_cancels_initial_or_button_read_and_ignores_late_completion(
        bool buttonRead, string completion)
    {
        await using var page = new CsvPage();
        var result = await CsvReader.PreviewAsync(page.Source);
        var response = new TaskCompletionSource<CsvEncodingPreview>();
        CancellationToken readToken = default;
        var pending = page.Start((_, encoding, _, token) =>
        {
            if (buttonRead && encoding is null) return Task.FromResult(result);
            readToken = token;
            return response.Task; // Deliberately ignores cancellation to test the late-result guard.
        });
        var dialog = page.Dialog;
        if (buttonRead) Click(page.Button("csv.preview"));
        Assert.True(readToken.CanBeCanceled);
        var sample = dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn);
        var encodingChoice = dialog.GetVisualDescendants().OfType<ComboBox>().First();
        var encodingBefore = encodingChoice.SelectedIndex;
        var before = sample.Text;
        dialog.Close();
        Assert.True(readToken.IsCancellationRequested);
        if (completion == "failure") response.SetException(new IOException("late read failure"));
        else if (completion == "cancel") response.SetCanceled(readToken);
        else response.SetResult(result with { Encoding = "utf-16" });
        Dispatcher.UIThread.RunJobs();
        await pending;
        Assert.Equal(before, sample.Text);
        Assert.Equal(encodingBefore, encodingChoice.SelectedIndex);
        Assert.False(page.Button(dialog, "demo.import").IsEnabled);
        Assert.False(page.Button(dialog, "csv.exportErrors").IsEnabled);
        Assert.Empty(page.Tasks.Snapshot());
    }

    [AvaloniaFact]
    public async Task Cancelled_csv_read_is_silent_and_can_be_retried()
    {
        await using var page = new CsvPage();
        var result = await CsvReader.PreviewAsync(page.Source);
        var calls = 0;
        var pending = page.Start((_, _, _, _) =>
            ++calls == 2 ? Task.FromException<CsvEncodingPreview>(new OperationCanceledException())
                : Task.FromResult(result));
        var sample = page.Dialog.GetVisualDescendants().OfType<TextBox>().Single(box => box.AcceptsReturn);
        var before = sample.Text;
        Click(page.Button("csv.preview"));
        Assert.Equal(before, sample.Text);
        Assert.False(page.Button("csv.exportErrors").IsEnabled);
        Click(page.Button("csv.preview"));
        Assert.Contains("KEEP", sample.Text);
        page.Dialog.Close();
        await pending;
    }

    [AvaloniaTheory]
    [InlineData("dialog")]
    [InlineData("detach")]
    [InlineData("context")]
    [InlineData("host")]
    [InlineData("revision")]
    [InlineData("stable")]
    public async Task Csv_report_picker_cannot_enqueue_a_stale_report(string change)
    {
        await using var page = new CsvPage();
        var result = await CsvReader.PreviewAsync(page.Source);
        var invalid = result with { Content = new CsvReadResult([], [new CsvIssue(2, "TEST", "bad row")]) };
        var response = new TaskCompletionSource<string?>();
        var picked = new TaskCompletionSource();
        var pending = page.Start((_, _, _, _) => Task.FromResult(invalid), _ =>
        {
            picked.SetResult();
            return response.Task;
        });
        var dialog = page.Dialog;
        Click(page.Button("csv.preview"));
        Assert.True(page.Button("csv.exportErrors").IsEnabled);
        Click(page.Button("csv.exportErrors"));
        await picked.Task;
        switch (change)
        {
            case "dialog": dialog.Close(); break;
            case "detach":
                page.Host.Content = null;
                page.Host.Content = page.View;
                break;
            case "context":
                page.View.DataContext = null;
                page.View.DataContext = page.Vm;
                break;
            case "host": page.Host.Close(); break;
            case "revision":
                dialog.GetVisualDescendants().OfType<ComboBox>().First().SelectedIndex = 1;
                break;
        }
        var report = Path.Combine(page.Directory, "report.csv");
        response.SetResult(report);
        Dispatcher.UIThread.RunJobs();
        if (change == "stable")
        {
            var item = Assert.Single(page.Tasks.Snapshot());
            await page.Tasks.WaitAsync(item.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkStatus.Succeeded, page.Tasks.Snapshot().Single(t => t.Id == item.Id).Status);
            Assert.Contains("TEST", await File.ReadAllTextAsync(report));
        }
        else
        {
            Assert.Empty(page.Tasks.Snapshot());
            Assert.False(File.Exists(report));
        }
        if (dialog.IsVisible) dialog.Close();
        await pending;
    }

    [AvaloniaFact]
    public async Task Confirmed_csv_snapshot_imports_once_and_survives_page_close_as_global_work()
    {
        await using var page = new CsvPage();
        var release = new TaskCompletionSource();
        var started = new[] { new TaskCompletionSource(), new TaskCompletionSource() };
        foreach (var signal in started)
            page.Tasks.Enqueue("blocker", "Test", async (_, token) =>
            {
                signal.SetResult();
                await release.Task.WaitAsync(token);
            });
        try
        {
            await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var result = await CsvReader.PreviewAsync(page.Source);
            var pending = page.Start((_, _, _, _) => Task.FromResult(result));
            var dialog = page.Dialog;
            Click(page.Button("csv.preview"));
            dialog.GetVisualDescendants().OfType<CheckBox>().Single().IsChecked = true;
            var import = page.Button("demo.import");
            Assert.True(import.IsEnabled);
            Click(import);
            Click(import); // A duplicate queued click after Closed must not submit twice.
            await pending;
            var item = Assert.Single(page.Tasks.Snapshot().Where(t => t.Module == "Demo"));
            Assert.Equal(WorkStatus.Waiting, item.Status);
            page.Host.Close();
            page.Vm.Dispose();
            release.SetResult();
            await page.Tasks.WaitAsync(item.Id).WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(WorkStatus.Succeeded, page.Tasks.Snapshot().Single(t => t.Id == item.Id).Status);
            Assert.Equal("KEEP", Assert.Single(await page.Service.SearchAsync("")).Code);
        }
        finally { release.TrySetResult(); }
    }

    private static void Click(Button button) => button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));

    private sealed class CsvPage : IAsyncDisposable
    {
        private readonly TestDatabase _database = new();
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "plinth-csv-picker-" + Guid.NewGuid().ToString("N"));
        public string Source => Path.Combine(Directory, "source.csv");
        public TaskService Tasks { get; } = new(NullLogger<TaskService>.Instance);
        public DemoService Service { get; }
        public DemoListViewModel Vm { get; }
        public DemoListView View { get; }
        public Window Host { get; }
        public Window Dialog
        {
            get
            {
                var dialog = Host.OwnedWindows.Single();
                Dispatcher.UIThread.RunJobs();
                dialog.Measure(new Size(760, 580));
                dialog.Arrange(new Rect(0, 0, 760, 580));
                Dispatcher.UIThread.RunJobs();
                return dialog;
            }
        }

        public CsvPage()
        {
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Source, "code,name,remark\nKEEP,Example,Remark\n");
            Service = new(_database.Factory, NullLogger<DemoService>.Instance);
            Vm = new(Service, NullLogger<DemoListViewModel>.Instance, tasks: Tasks);
            View = new DemoListView { DataContext = Vm };
            Host = new Window { Content = View };
            Host.Show();
        }

        public Task Start(Func<string, string?, string, CancellationToken, Task<CsvEncodingPreview>> read,
            Func<Window, Task<string?>>? pick = null) => View.ShowCsvPreviewAsync(Host, Vm, Source, read, pick);

        public Button Button(string key) => Button(Dialog, key);
        public Button Button(Window dialog, string key) => dialog.GetVisualDescendants().OfType<Button>()
            .Single(button => Equals(button.Content, Loc.Get(key)));

        public async ValueTask DisposeAsync()
        {
            Host.Close();
            Vm.Dispose();
            await Tasks.StopAsync();
            _database.Dispose();
            System.IO.Directory.Delete(Directory, true);
        }
    }
}
