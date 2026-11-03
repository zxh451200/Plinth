#if PLINTH_XLSX
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using ClosedXML.Excel;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

[CollectionDefinition("Spreadsheet deployment", DisableParallelization = true)]
public sealed class SpreadsheetDeploymentCollection { }

[Collection("Spreadsheet deployment")]
public sealed class SpreadsheetPageTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"plinth-page-test-{Guid.NewGuid():N}");
    private readonly SpreadsheetService _spreadsheet = new();
    private readonly TaskService _tasks = new(NullLogger<TaskService>.Instance);
    private readonly Permissions _permissions = new();
    private readonly Audit _audit = new();
    private readonly bool _oldEnabled = DeploymentConfiguration.Current.Authentication.Enabled;
    private readonly string _oldProfile = DeploymentConfiguration.Current.Profile;
    private readonly DemoService _demo;
    private readonly SensitiveOperationService _operations;

    public SpreadsheetPageTests()
    {
        Directory.CreateDirectory(_directory);
        DeploymentConfiguration.Current.Profile = "Test";
        DeploymentConfiguration.Current.Authentication.Enabled = true;
        _operations = new(new AuthorizedOperation(_permissions, new OfflineUserContext(), _audit), _audit);
        _demo = new(_database.Factory, NullLogger<DemoService>.Instance, _operations);
    }

    public void Dispose()
    {
        DeploymentConfiguration.Current.Authentication.Enabled = _oldEnabled;
        DeploymentConfiguration.Current.Profile = _oldProfile;
        _database.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private SpreadsheetViewModel Create() => new(_spreadsheet, _demo, _tasks,
        _operations,
        NullLogger<SpreadsheetViewModel>.Instance);

    [AvaloniaFact]
    public async Task Confirmation_imports_the_preview_snapshot_and_cannot_submit_it_twice()
    {
        var path = await _spreadsheet.ExportAsync([new[] { "001", "'=1+2", "原备注" }], _directory);
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        Assert.Empty(await _demo.SearchAsync(null));
        Assert.False(vm.CanConfirm);
        File.Delete(path); // Confirmation must not reread the now missing/changed source.
        vm.Acknowledged = true;
        Assert.True(vm.CanConfirm);
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        var imported = Assert.Single(await _demo.SearchAsync(null));
        Assert.Equal("'=1+2", imported.Name);
        Assert.False(vm.CanConfirm);
        vm.Acknowledged = true;
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Equal(imported.Version, Assert.Single(await _demo.SearchAsync(null)).Version);
        Assert.Contains(_audit.Events, e => e.Operation == "xlsx.import" && e.Result == "success");
        Assert.All(_tasks.Snapshot(), t => Assert.Equal(WorkStatus.Succeeded, t.Status));
        var outcome = Assert.IsType<TaskOutcome>(Assert.Single(_tasks.Snapshot()
            .Where(t => t.Outcome is not null)).Outcome);
        Assert.Equal(TaskOutcomeKind.Import, outcome.Kind);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(0, outcome.Updated);
        Assert.Null(outcome.FilePath);
        var preview = Assert.Single(_tasks.Snapshot().Where(t => t.Title == vm.Labels["PreviewTask"]));
        Assert.Null(preview.Outcome);
        Assert.Empty(preview.ResultText);
    }

    [AvaloniaFact]
    public async Task Invalid_row_outside_displayed_sample_blocks_the_whole_batch()
    {
        var path = await _spreadsheet.ExportAsync(Enumerable.Range(1, 25)
            .Select(i => (IReadOnlyList<string>)new[] { i.ToString("D3"), "名称", "" }), _directory);
        using (var workbook = new XLWorkbook(path))
        {
            workbook.Worksheet(1).Cell("B26").Value = "";
            workbook.Save();
        }
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        Assert.Equal(20, vm.PreviewRows.Count);
        Assert.NotEmpty(vm.Issues);
        Assert.False(vm.CanConfirm);
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Empty(await _demo.SearchAsync(null));
        Assert.Equal(WorkStatus.Failed, Assert.Single(_tasks.Snapshot()).Status);
        Assert.Null(Assert.Single(_tasks.Snapshot()).Outcome);
    }

    [AvaloniaFact]
    public async Task Authorization_is_checked_again_at_confirmation_and_export()
    {
        var path = await _spreadsheet.ExportAsync([new[] { "001", "名称", "" }], _directory);
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        _permissions.Allowed = false;
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Empty(await _demo.SearchAsync(null));
        await vm.ExportToAsync(_directory);
        Assert.Empty(vm.ExportedPath);
        Assert.Contains(_audit.Events, e => e.Operation == "xlsx.import" && e.Result == "denied");
        Assert.Contains(_audit.Events, e => e.Operation == "xlsx.export.confirm" && e.Result == "denied");
        Assert.All(_tasks.Snapshot().Where(t => t.Title != vm.Labels["PreviewTask"]), t =>
        {
            Assert.Equal(WorkStatus.Failed, t.Status);
            Assert.Null(t.Outcome);
            Assert.Empty(t.ResultText);
        });
    }

    [AvaloniaFact]
    public async Task Closing_page_keeps_export_owned_by_task_centre()
    {
        await _demo.SaveAsync(new() { Code = "A001", Name = "苹果" });
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var demands = 0;
        _permissions.OnDemand = () =>
        {
            // The first two checks prepare confirmation on the UI thread;
            // the third check belongs to the already queued task.
            if (Interlocked.Increment(ref demands) != 3) return;
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("Test did not release export authorization.");
        };
        using var vm = Create();
        var export = vm.ExportToAsync(_directory);
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            vm.Dispose();
        }
        finally { release.Set(); }
        await export;
        var item = Assert.Single(_tasks.Snapshot());
        Assert.Equal(WorkStatus.Succeeded, item.Status);
        var outcome = Assert.IsType<TaskOutcome>(item.Outcome);
        Assert.Equal(TaskOutcomeKind.Export, outcome.Kind);
        Assert.Equal(1, outcome.WrittenRows);
        Assert.Equal(Assert.Single(Directory.GetFiles(_directory, "*.xlsx")), outcome.FilePath);
        Assert.Null(outcome.CsvPath);
        Assert.Contains(outcome.FilePath!, item.ResultText);
        Assert.Empty(vm.ExportedPath);
    }

    [AvaloniaFact]
    public async Task View_loads_and_binds_real_controls_and_export_respects_search()
    {
        await _demo.SaveAsync(new() { Code = "A001", Name = "苹果" });
        await _demo.SaveAsync(new() { Code = "B001", Name = "香蕉" });
        using var vm = Create();
        var view = new SpreadsheetView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            var page = Assert.IsType<UserControl>(view.Content);
            Assert.Equal(vm.Labels["ChooseFile"], page.FindControl<Button>("ChooseFileButton")!.Content);
            Assert.Equal(4, page.FindControl<TableView>("PreviewTable")!.Columns.Count);
            vm.SearchText = "A001";
            await vm.ExportToAsync(_directory);
            Assert.True(File.Exists(vm.ExportedPath));
            var data = await _spreadsheet.ImportAsync(vm.ExportedPath);
            Assert.Empty(data.Errors);
            Assert.Equal("A001", Assert.Single(data.Rows.Skip(1))[0]);
            var outcome = Assert.IsType<TaskOutcome>(Assert.Single(_tasks.Snapshot()).Outcome);
            Assert.Equal(1, outcome.WrittenRows);
            Assert.Equal(vm.ExportedPath, outcome.FilePath);
            Assert.Null(outcome.CsvPath);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Closing_page_keeps_import_counts_and_does_not_update_page_state()
    {
        await _demo.SaveAsync(new() { Code = "A001", Name = "旧名称" });
        var path = await _spreadsheet.ExportAsync(
            [new[] { "A001", "新名称", "" }, new[] { "B001", "新增", "" }], _directory);
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        _permissions.OnDemand = () =>
        {
            entered.Set();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        var import = vm.ConfirmImportCommand.ExecuteAsync(null);
        string status;
        try
        {
            Assert.True(await Task.Run(() => entered.Wait(TimeSpan.FromSeconds(5))));
            vm.Dispose();
            status = vm.StatusText;
        }
        finally { release.Set(); }
        await import;
        var item = Assert.Single(_tasks.Snapshot().Where(t => t.Outcome is not null));
        Assert.Equal(WorkStatus.Succeeded, item.Status);
        Assert.Equal(TaskOutcomeKind.Import, item.Outcome!.Kind);
        Assert.Equal(1, item.Outcome.Added);
        Assert.Equal(1, item.Outcome.Updated);
        Assert.Equal(status, vm.StatusText);
        Assert.True(vm.Acknowledged);
    }

    [AvaloniaTheory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task Legal_csv_replacement_during_authorization_or_production_confirmation_never_commits(
        bool production, bool confirmed)
    {
        var path = await _spreadsheet.ExportAsync([new[] { "A001", "名称", "" }], _directory);
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        var existing = Directory.GetDirectories(Path.GetTempPath(), "plinth-xlsx-*").ToHashSet();
        var demands = 0;
        string? temporaryDirectory = null;
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DeploymentConfiguration.Current.Profile = production ? "Production" : "Test";
        _permissions.OnDemand = () =>
        {
            if (Interlocked.Increment(ref demands) != 2 || production) return;
            entered.TrySetResult(true);
            if (!release.Task.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException();
        };
        _operations.ConfirmProductionAsync = () =>
        {
            entered.TrySetResult(true);
            return release.Task;
        };
        var import = vm.ConfirmImportCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            temporaryDirectory = Assert.Single(Directory.GetDirectories(Path.GetTempPath(), "plinth-xlsx-*")
                .Where(d => !existing.Contains(d)));
            var csv = Assert.Single(Directory.GetFiles(temporaryDirectory, "*.csv"));
            // B is independently valid and different from the acknowledged preview A.
            await CsvWriter.WriteFileAsync(Path.Combine(_directory, "replacement.csv"),
                ["Code", "Name", "Remark"], [new[] { "B001", "合法替换", "新备注" }]);
            File.Copy(Path.Combine(_directory, "replacement.csv"), csv, overwrite: true);
            var replacement = await CsvReader.ReadFileAsync(csv, "utf-8");
            Assert.Empty(replacement.Errors);
            Assert.Equal("B001", replacement.Rows[1][0]);
            Assert.Empty(await _demo.SearchAsync(null));
        }
        finally
        {
            release.TrySetResult(confirmed);
            await import;
        }
        var item = Assert.Single(_tasks.Snapshot().Where(t => t.Title == vm.Labels["ImportTask"]));
        Assert.Equal(confirmed ? WorkStatus.Failed : WorkStatus.Cancelled, item.Status);
        if (confirmed) Assert.Equal("TASK_FAILED", item.Error?.Code);
        Assert.Null(item.Outcome);
        Assert.Empty(item.ResultText);
        Assert.Empty(await _demo.SearchAsync(null));
        var auditResult = confirmed ? "failed" : "cancelled";
        Assert.Contains(_audit.Events, e => e.Operation == "import" && e.Result == auditResult);
        Assert.Contains(_audit.Events, e => e.Operation == "xlsx.import" && e.Result == auditResult);
        Assert.DoesNotContain(_audit.Events, e => (e.Operation is "import" or "xlsx.import")
            && (e.Result is "success" or "rejected"));
        Assert.NotNull(temporaryDirectory);
        Assert.False(Directory.Exists(temporaryDirectory));
    }

    [AvaloniaFact]
    public async Task Business_rejection_keeps_unknown_failed_rows_no_report_and_rejected_audits_after_close()
    {
        var path = await _spreadsheet.ExportAsync(
            [new[] { "A001", "名称", "" }, new[] { "B001", "有效", "" }], _directory);
        await using (var db = await _database.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_xlsx_import BEFORE INSERT ON "demo_records"
                WHEN NEW."Code" = 'B001'
                BEGIN SELECT RAISE(ABORT, 'private database failure'); END;
                """);
        }
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        vm.Dispose();
        var item = Assert.Single(_tasks.Snapshot().Where(t => t.Outcome is not null));
        var outcome = Assert.IsType<TaskOutcome>(item.Outcome);
        Assert.Equal(WorkStatus.Failed, item.Status);
        Assert.Equal("TASK_REJECTED", item.Error?.Code);
        Assert.Equal(TaskOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(0, outcome.Added);
        Assert.Equal(0, outcome.Updated);
        Assert.Null(outcome.FailedRows);
        Assert.False(outcome.FailureCountComplete);
        Assert.Null(outcome.FilePath);
        Assert.Null(outcome.CsvPath);
        Assert.DoesNotContain("private database failure", item.ResultText);
        Assert.NotEmpty(vm.Issues);
        Assert.Empty(await _demo.SearchAsync(null));
        Assert.Contains(_audit.Events, e => e.Operation == "import" && e.Result == "rejected");
        Assert.Contains(_audit.Events, e => e.Operation == "xlsx.import" && e.Result == "rejected");
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancelling_queued_work_does_not_publish_success_or_an_outcome(bool import)
    {
        var path = await _spreadsheet.ExportAsync([new[] { "A001", "名称", "" }], _directory);
        using var vm = Create();
        if (import)
        {
            await vm.PreviewFromAsync(path);
            vm.Acknowledged = true;
        }
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        Task Block(IProgress<double?> _, CancellationToken token)
        {
            if (Interlocked.Increment(ref count) == 2) entered.TrySetResult(true);
            return release.Task;
        }
        var first = _tasks.Enqueue("block-one", "Test", Block);
        var second = _tasks.Enqueue("block-two", "Test", Block);
        Task? pending = null;
        WorkItem? item = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pending = import ? vm.ConfirmImportCommand.ExecuteAsync(null) : vm.ExportToAsync(_directory);
            item = Assert.Single(_tasks.Snapshot().Where(t =>
                t.Title == vm.Labels[import ? "ImportTask" : "ExportTask"]));
            Assert.True(_tasks.Cancel(item.Id));
        }
        finally
        {
            release.TrySetResult(true);
            await Task.WhenAll(_tasks.WaitAsync(first), _tasks.WaitAsync(second));
            if (pending is not null) await pending;
        }
        item = Assert.Single(_tasks.Snapshot().Where(t => t.Id == item!.Id));
        Assert.Equal(WorkStatus.Cancelled, item.Status);
        Assert.Null(item.Outcome);
        Assert.Empty(item.ResultText);
        Assert.Empty(await _demo.SearchAsync(null));
        Assert.Empty(vm.ExportedPath);
        Assert.Single(Directory.GetFiles(_directory, "*.xlsx")); // Only the input workbook.
        Assert.DoesNotContain(_audit.Events, e => e.Operation == (import ? "xlsx.import" : "xlsx.export"));
    }

    [AvaloniaFact]
    public async Task Production_confirmation_and_authorization_counts_are_preserved()
    {
        var path = await _spreadsheet.ExportAsync([new[] { "A001", "名称", "" }], _directory);
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        DeploymentConfiguration.Current.Profile = "Production";
        var confirmations = 0;
        var demands = 0;
        _permissions.OnDemand = () => Interlocked.Increment(ref demands);
        _operations.ConfirmProductionAsync = () =>
        {
            Interlocked.Increment(ref confirmations);
            return Task.FromResult(true);
        };
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        Assert.Equal(1, confirmations);
        Assert.Equal(3, demands);
        await vm.ExportToAsync(_directory);
        Assert.Equal(2, confirmations);
        Assert.Equal(6, demands);
        Assert.Equal(2, _tasks.Snapshot().Count(t => t.Outcome is not null));
    }

    [AvaloniaFact]
    public async Task Declining_production_confirmation_does_not_publish_an_outcome()
    {
        var path = await _spreadsheet.ExportAsync([new[] { "A001", "名称", "" }], _directory);
        using var vm = Create();
        await vm.PreviewFromAsync(path);
        vm.Acknowledged = true;
        DeploymentConfiguration.Current.Profile = "Production";
        var confirmations = 0;
        _operations.ConfirmProductionAsync = () =>
        {
            Interlocked.Increment(ref confirmations);
            return Task.FromResult(false);
        };
        await vm.ConfirmImportCommand.ExecuteAsync(null);
        var item = Assert.Single(_tasks.Snapshot().Where(t => t.Title == vm.Labels["ImportTask"]));
        Assert.Equal(WorkStatus.Cancelled, item.Status);
        Assert.Null(item.Outcome);
        Assert.Contains(_audit.Events, e => e.Operation == "import" && e.Result == "cancelled");
        Assert.Contains(_audit.Events, e => e.Operation == "xlsx.import" && e.Result == "cancelled");
        await vm.ExportToAsync(_directory);
        Assert.Equal(2, confirmations);
        Assert.DoesNotContain(_tasks.Snapshot(), t => t.Title == vm.Labels["ExportTask"]);
        Assert.All(_tasks.Snapshot(), t => Assert.Null(t.Outcome));
        Assert.Empty(await _demo.SearchAsync(null));
        Assert.Empty(vm.ExportedPath);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Late_cancellation_after_commit_keeps_the_result(bool import)
    {
        using var vm = Create();
        if (import)
        {
            var path = await _spreadsheet.ExportAsync([new[] { "A001", "名称", "" }], _directory);
            await vm.PreviewFromAsync(path);
            vm.Acknowledged = true;
        }
        else await _demo.SaveAsync(new() { Code = "A001", Name = "名称" });
        var title = vm.Labels[import ? "ImportTask" : "ExportTask"];
        _audit.OnWrite = entry =>
        {
            if (entry.Operation == (import ? "import" : "xlsx.export") && entry.Result == "success")
                Assert.True(_tasks.Cancel(Assert.Single(_tasks.Snapshot().Where(t => t.Title == title)).Id));
        };
        if (import) await vm.ConfirmImportCommand.ExecuteAsync(null);
        else await vm.ExportToAsync(_directory);
        var item = Assert.Single(_tasks.Snapshot().Where(t => t.Title == title));
        Assert.True(item.CancellationRequested);
        Assert.Equal(WorkStatus.Succeeded, item.Status);
        if (import)
        {
            Assert.Equal(TaskOutcomeKind.Import, item.Outcome?.Kind);
            Assert.Equal(1, item.Outcome?.Added);
            Assert.Equal(0, item.Outcome?.Updated);
            Assert.Single(await _demo.SearchAsync(null));
        }
        else
        {
            Assert.Equal(1, item.Outcome?.WrittenRows);
            Assert.Equal(vm.ExportedPath, item.Outcome?.FilePath);
            Assert.True(File.Exists(vm.ExportedPath));
        }
    }

    private sealed class Permissions : IPermissionService
    {
        public bool Allowed = true;
        public Action? OnDemand;
        public bool HasPermission(string permission) => Allowed;
        public void Demand(string permission, string correlationId)
        {
            OnDemand?.Invoke();
            if (!Allowed) throw new UnauthorizedAccessException("Denied");
        }
    }

    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Events { get; } = [];
        public Action<AuditEvent>? OnWrite;
        public void Write(AuditEvent entry)
        {
            Events.Add(entry);
            OnWrite?.Invoke(entry);
        }
    }
}
#endif
