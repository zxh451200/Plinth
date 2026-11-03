#if PLINTH_XLSX
using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

[Collection("Spreadsheet deployment")]
public sealed class SpreadsheetPickerLifetimeTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Stable_picker_runs_real_work_and_blocks_both_buttons_until_completion(bool folder)
    {
        await using var page = new PickerPage();
        var path = await page.SelectionAsync(folder);
        var pending = page.Click(folder);
        Assert.False(pending.IsCompleted);
        Assert.Same(page.Host, page.PickerHost);
        Assert.Equal(folder, page.PickedFolder);
        _ = page.Click(folder);
        _ = page.Click(!folder);
        Assert.Equal(1, page.Calls);
        Assert.Empty(page.Tasks.Snapshot());

        page.Response.SetResult(path);
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var item = Assert.Single(page.Tasks.Snapshot());
        Assert.Equal(WorkStatus.Succeeded, item.Status);
        if (folder)
        {
            Assert.Equal(TaskOutcomeKind.Export, item.Outcome?.Kind);
            Assert.Equal(1, item.Outcome?.WrittenRows);
            Assert.True(File.Exists(page.Vm.ExportedPath));
        }
        else
        {
            Assert.Equal("KEEP", Assert.Single(page.Vm.PreviewRows).Code);
            Assert.Equal(Path.GetFileName(path), page.Vm.SourceName);
            Assert.Null(item.Outcome);
        }
    }

    [AvaloniaTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Obsolete_picker_never_starts_work_or_reports_failure_even_when_identity_is_restored(
        bool folder, bool failure)
    {
        foreach (var change in new[] { "detach", "context", "host-close", "host-hide", "host-change" })
        {
            await using var page = new PickerPage();
            using var replacement = page.CreateVm();
            var pending = page.Click(folder);
            var status = page.Vm.StatusText;
            var replacementStatus = replacement.StatusText;
            Window? otherHost = null;
            try
            {
                switch (change)
                {
                    case "detach":
                        page.Host.Content = null;
                        page.Host.Content = page.View;
                        Assert.Same(page.Host, TopLevel.GetTopLevel(page.View));
                        break;
                    case "context":
                        page.View.DataContext = replacement;
                        page.View.DataContext = page.Vm;
                        Assert.Same(page.Vm, page.View.DataContext);
                        break;
                    case "host-close":
                        page.Host.Close();
                        break;
                    case "host-hide":
                        // Keep the view attached so visibility itself must reject the response.
                        page.Host.Hide();
                        Assert.Same(page.Host, TopLevel.GetTopLevel(page.View));
                        break;
                    case "host-change":
                        page.Host.Content = null;
                        otherHost = new Window { Content = page.View };
                        otherHost.Show();
                        break;
                }
                if (failure) page.Response.SetException(new IOException("obsolete picker"));
                else page.Response.SetResult(page.PathFor(folder));
                await pending.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Empty(page.Tasks.Snapshot());
                Assert.Equal(status, page.Vm.StatusText);
                Assert.Equal(replacementStatus, replacement.StatusText);
                Assert.Empty(page.Vm.SourceName);
                Assert.Empty(page.Vm.ExportedPath);

                // An obsolete response must release the guard for a fresh page lifetime.
                if (change is "detach" or "context")
                {
                    page.Response = PickerPage.NewResponse();
                    var retry = page.Click(folder);
                    Assert.Equal(2, page.Calls);
                    page.Response.SetResult(null);
                    await retry.WaitAsync(TimeSpan.FromSeconds(5));
                }
            }
            finally { otherHost?.Close(); }
        }
    }

    [AvaloniaTheory]
    [InlineData(false, "failure")]
    [InlineData(true, "failure")]
    [InlineData(false, "sync-failure")]
    [InlineData(true, "sync-failure")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "empty")]
    [InlineData(true, "empty")]
    public async Task Failure_or_cancellation_releases_guard_and_allows_successful_retry(bool folder, string outcome)
    {
        await using var page = new PickerPage();
        var path = await page.SelectionAsync(folder);
        var status = page.Vm.StatusText;
        page.ThrowSynchronously = outcome == "sync-failure";
        var pending = page.Click(folder);
        switch (outcome)
        {
            case "failure": page.Response.SetException(new IOException("picker failed")); break;
            case "cancel": page.Response.SetCanceled(); break;
            case "empty": page.Response.SetResult(null); break;
        }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(page.Tasks.Snapshot());
        Assert.Equal(outcome.Contains("failure") ? page.Vm.Labels["PickerFailed"] : status, page.Vm.StatusText);

        page.ThrowSynchronously = false;
        page.Response = PickerPage.NewResponse();
        var retry = page.Click(folder);
        Assert.Equal(2, page.Calls);
        page.Response.SetResult(path);
        await retry.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkStatus.Succeeded, Assert.Single(page.Tasks.Snapshot()).Status);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Disposed_vm_rejects_pending_result_and_exception(bool folder)
    {
        foreach (var failure in new[] { false, true })
        {
            await using var page = new PickerPage();
            var pending = page.Click(folder);
            page.Vm.Dispose();
            var status = page.Vm.StatusText;
            if (failure) page.Response.SetException(new IOException("late failure"));
            else page.Response.SetResult(page.PathFor(folder));
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(status, page.Vm.StatusText);
            Assert.Empty(page.Tasks.Snapshot());
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Busy_vm_rejects_pending_result_and_exception(bool folder)
    {
        foreach (var failure in new[] { false, true })
        {
            await using var page = new PickerPage();
            var source = await page.SelectionAsync(false);
            var pending = page.Click(folder);
            var release = PickerPage.NewResponse();
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var started = 0;
            Task Block()
            {
                if (System.Threading.Interlocked.Increment(ref started) == 2) entered.TrySetResult();
                return release.Task;
            }
            var first = page.Tasks.Enqueue("block-one", "Test", (_, _) => Block());
            var second = page.Tasks.Enqueue("block-two", "Test", (_, _) => Block());
            Task? busy = null;
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                busy = page.Vm.PreviewFromAsync(source);
                Assert.False(page.Vm.CanInteract);
                var status = page.Vm.StatusText;
                if (failure) page.Response.SetException(new IOException("late failure"));
                else page.Response.SetResult(page.PathFor(folder));
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.Equal(status, page.Vm.StatusText);
                Assert.Equal(3, page.Tasks.Snapshot().Count);
            }
            finally
            {
                release.TrySetResult(null);
                await Task.WhenAll(page.Tasks.WaitAsync(first), page.Tasks.WaitAsync(second));
                if (busy is not null) await busy.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Detaching_after_work_is_queued_does_not_cancel_it(bool folder)
    {
        await using var page = new PickerPage();
        var path = await page.SelectionAsync(folder);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = PickerPage.NewResponse();
        var demands = 0;
        // Preview has one authorization check; export has two before queueing and one in the task.
        page.Permissions.OnDemand = () =>
        {
            if (System.Threading.Interlocked.Increment(ref demands) != (folder ? 3 : 1)) return;
            entered.TrySetResult();
            if (!release.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var pending = page.Click(folder);
        page.Response.SetResult(path);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(page.Tasks.Snapshot());
            page.Host.Content = null;
        }
        finally { release.TrySetResult(null); }
        await pending.WaitAsync(TimeSpan.FromSeconds(10));
        var item = Assert.Single(page.Tasks.Snapshot());
        Assert.Equal(WorkStatus.Succeeded, item.Status);
        Assert.False(item.CancellationRequested);
    }

    private sealed class PickerPage : IAsyncDisposable
    {
        private readonly TestDatabase _database = new();
        private readonly SpreadsheetService _spreadsheet = new();
        private readonly bool _oldEnabled = DeploymentConfiguration.Current.Authentication.Enabled;
        private readonly string _oldProfile = DeploymentConfiguration.Current.Profile;
        private readonly SensitiveOperationService _operations;
        private readonly DemoService _demo;
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), $"plinth-xlsx-picker-{Guid.NewGuid():N}");
        public TaskService Tasks { get; } = new(NullLogger<TaskService>.Instance);
        public AllowPermissions Permissions { get; } = new();
        public SpreadsheetViewModel Vm { get; }
        public SpreadsheetView View { get; }
        public Window Host { get; }
        public TaskCompletionSource<string?> Response { get; set; } = NewResponse();
        public int Calls { get; private set; }
        public TopLevel? PickerHost { get; private set; }
        public bool PickedFolder { get; private set; }
        public bool ThrowSynchronously { get; set; }

        public PickerPage()
        {
            System.IO.Directory.CreateDirectory(Directory);
            DeploymentConfiguration.Current.Profile = "Test";
            DeploymentConfiguration.Current.Authentication.Enabled = true;
            var audit = new NoAudit();
            _operations = new(new AuthorizedOperation(Permissions, new OfflineUserContext(), audit), audit);
            _demo = new(_database.Factory, NullLogger<DemoService>.Instance, _operations);
            Vm = CreateVm();
            View = new SpreadsheetView((top, folder) =>
            {
                Calls++;
                PickerHost = top;
                PickedFolder = folder;
                if (ThrowSynchronously) throw new IOException("synchronous picker failure");
                return Response.Task;
            }) { DataContext = Vm };
            Host = new Window { Content = View, Width = 1100, Height = 800 };
            Host.Show();
        }

        public static TaskCompletionSource<string?> NewResponse() =>
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SpreadsheetViewModel CreateVm() => new(_spreadsheet, _demo, Tasks, _operations,
            NullLogger<SpreadsheetViewModel>.Instance);

        public string PathFor(bool folder) => folder ? Directory : Path.Combine(Directory, "source.xlsx");

        public async Task<string> SelectionAsync(bool folder)
        {
            if (!folder) return await _spreadsheet.ExportAsync([new[] { "KEEP", "Example", "" }], Directory);
            await _demo.SaveAsync(new() { Code = "KEEP", Name = "Example" });
            return Directory;
        }

        public Task Click(bool folder)
        {
            var page = Assert.IsType<UserControl>(View.Content);
            var button = page.FindControl<Button>(folder ? "ExportButton" : "ChooseFileButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            return View.PickerCompletion;
        }

        public async ValueTask DisposeAsync()
        {
            Response.TrySetResult(null);
            Host.Close();
            await View.PickerCompletion.WaitAsync(TimeSpan.FromSeconds(10));
            await Tasks.StopAsync();
            Vm.Dispose();
            _database.Dispose();
            DeploymentConfiguration.Current.Authentication.Enabled = _oldEnabled;
            DeploymentConfiguration.Current.Profile = _oldProfile;
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }

    private sealed class AllowPermissions : IPermissionService
    {
        public Action? OnDemand;
        public bool HasPermission(string permission) => true;
        public void Demand(string permission, string correlationId) => OnDemand?.Invoke();
    }

    private sealed class NoAudit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }
}
#endif
