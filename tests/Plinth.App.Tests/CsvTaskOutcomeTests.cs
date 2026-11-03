using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CsvTaskOutcomeTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-outcome-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> _artifacts = [];

    public CsvTaskOutcomeTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_directory, true);
        foreach (var artifact in _artifacts)
            if (File.Exists(artifact)) File.Delete(artifact);
    }

    private DemoService Service(SensitiveOperationService? operations = null) =>
        new(_database.Factory, NullLogger<DemoService>.Instance, operations);

    private async Task<string> Csv(string text)
    {
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".csv");
        await File.WriteAllTextAsync(path, text);
        return path;
    }

    private ImportResult Track(ImportResult result)
    {
        if (result.ReportPath is { } report) _artifacts.Add(report);
        return result;
    }

    [Fact]
    public void Raw_import_errors_and_external_report_paths_never_enter_task_results()
    {
        var result = new ImportResult(0, 0, ["customer name, password=secret, raw database exception"])
        {
            ReportPath = Path.Combine(AppPaths.LogDirectory, "private.csv"),
            DiscoveredErrorRows = 1,
            ErrorRowCountComplete = true,
        };
        var outcome = result.ToTaskOutcome();
        Assert.Null(outcome.CsvPath);
        Assert.Equal(1, outcome.FailedRows);
        Assert.DoesNotContain("customer name", outcome.ResultText);
        Assert.DoesNotContain("secret", outcome.ResultText);
        Assert.DoesNotContain("private.csv", outcome.ResultText);
    }

    [Fact]
    public async Task Diagnostic_archive_excludes_task_results_and_csv_artifact_contents()
    {
        Directory.CreateDirectory(AppPaths.ExportDirectory);
        var path = Path.Combine(AppPaths.ExportDirectory, Guid.NewGuid().ToString("N") + ".csv");
        _artifacts.Add(path);
        await File.WriteAllTextAsync(path, "private-artifact-marker");
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var id = tasks.EnqueueWithResult("Export", "Demo", (_, _) => Task.FromResult(TaskOutcome.Export(1, path)));
        await tasks.WaitAsync(id);
        var diagnostics = new DiagnosticService(new LogAccessService(authenticationEnabled: false));
        var preview = await diagnostics.PreviewAsync(AppPaths.ExportDirectory, new DiagnosticOptions(new LogFilter()));
        var destination = Path.Combine(_directory, "diagnostics.zip");
        await diagnostics.ExportAsync(preview.Id, true, destination);
        using var archive = ZipFile.OpenRead(destination);
        Assert.All(archive.Entries, entry => Assert.Contains(entry.FullName, new[] { "logs.ndjson", "runtime.json" }));
        foreach (var entry in archive.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.DoesNotContain(path, text);
            Assert.DoesNotContain("private-artifact-marker", text);
            Assert.DoesNotContain("Outcome", text);
        }
    }

    [Fact]
    public async Task Successful_import_retains_added_updated_counts_and_real_read_progress()
    {
        var service = Service();
        await service.SaveAsync(new DemoRecord { Code = "A", Name = "before" });
        var reports = new List<double?>();
        var result = await service.ImportCsvAsync(await Csv("code,name,remark\nA,after,\nB,new,\n"),
            progress: new InlineProgress(reports.Add));
        Assert.True(result.Succeeded);
        var outcome = result.ToTaskOutcome();
        Assert.Equal(TaskOutcomeKind.Import, outcome.Kind);
        Assert.Equal(1, outcome.Added);
        Assert.Equal(1, outcome.Updated);
        Assert.Contains(reports, value => value > 0);
        Assert.Null(reports[^1]);
    }

    [Fact]
    public async Task Multiple_issues_on_one_physical_row_count_once_and_reject_the_whole_batch()
    {
        var service = Service();
        var result = Track(await service.ImportCsvAsync(await Csv("code,name,remark\nA,valid,\nbroken\n")));
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.DiscoveredErrorRows);
        Assert.NotNull(result.ReportPath);
        Assert.True(File.Exists(result.ReportPath));
        var outcome = result.ToTaskOutcome();
        Assert.Equal(TaskOutcomeKind.Rejected, outcome.Kind);
        Assert.Equal(0, outcome.Added);
        Assert.Equal(0, outcome.Updated);
        Assert.Equal(result.ReportPath, outcome.CsvPath);
        Assert.Empty(await service.SearchAsync(null));
    }

    [Fact]
    public async Task Parser_error_limit_is_a_lower_bound_of_distinct_rows()
    {
        var service = Service();
        var result = Track(await service.ImportCsvAsync(await Csv("code,name,remark\n" +
            string.Concat(Enumerable.Repeat("broken\n", 150)))));
        Assert.False(result.Succeeded);
        Assert.Equal(100, result.DiscoveredErrorRows);
        Assert.False(result.ErrorRowCountComplete);
        Assert.Equal(100, result.ToTaskOutcome().FailedRows);
        Assert.Empty(await service.SearchAsync(null));
    }

    [Theory]
    [InlineData(3, true)]
    [InlineData(20, true)]
    [InlineData(25, false)]
    public async Task Business_validation_limit_records_completeness_and_zero_commits(int invalidRows, bool complete)
    {
        var service = Service();
        var result = Track(await service.ImportCsvAsync(await Csv("code,name,remark\nA,valid,\n" +
            string.Concat(Enumerable.Repeat(",invalid,\n", invalidRows)))));
        Assert.False(result.Succeeded);
        Assert.Equal(Math.Min(20, invalidRows), result.DiscoveredErrorRows);
        Assert.Equal(complete, result.ErrorRowCountComplete);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Empty(await service.SearchAsync(null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("code,name,remark\n")]
    public async Task Empty_input_does_not_claim_zero_failed_rows(string text)
    {
        var result = await Service().ImportCsvAsync(await Csv(text));
        Assert.False(result.Succeeded);
        Assert.Null(result.DiscoveredErrorRows);
        Assert.Null(result.ToTaskOutcome().FailedRows);
        Assert.False(result.ErrorRowCountComplete);
    }

    [Fact]
    public async Task Cancellation_during_read_does_not_commit_or_publish_a_result()
    {
        var service = Service();
        using var cancellation = new CancellationTokenSource();
        var path = await Csv("code,name,remark\nA,valid,\n");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ImportCsvAsync(path, cancellation.Token,
                new InlineProgress(value => { if (value > 0) cancellation.Cancel(); })));
        Assert.Empty(await service.SearchAsync(null));
    }

    [Fact]
    public async Task Task_cancellation_during_import_read_has_no_outcome_or_committed_rows()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var service = Service();
        var cancellationDelivered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = await Csv("code,name,remark\nA,valid,\n");
        var id = tasks.EnqueueWithResult("Import", "Demo", async (_, token) =>
        {
            using var registration = token.Register(() => cancellationDelivered.TrySetResult());
            var result = await service.ImportCsvAsync(path, token, new InlineProgress(value =>
            {
                if (value > 0)
                {
                    Assert.True(tasks.Cancel(Assert.Single(tasks.Snapshot()).Id));
                    // Cancel dispatches the token on a worker; wait for delivery before
                    // returning to the reader's next cancellation check.
                    cancellationDelivered.Task.WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
                }
            }));
            return result.ToTaskOutcome();
        });
        try { await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5)); }
        finally { await tasks.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        var item = Assert.Single(tasks.Snapshot());
        Assert.Equal(WorkStatus.Cancelled, item.Status);
        Assert.Null(item.Outcome);
        Assert.Empty(item.ResultText);
        Assert.Empty(await service.SearchAsync(null));
    }

    [Theory]
    [InlineData("import", false)]
    [InlineData("export", false)]
    [InlineData("report", false)]
    [InlineData("import", true)]
    [InlineData("export", true)]
    [InlineData("report", true)]
    public async Task Late_cancellation_preserves_real_database_commits_and_published_csv(
        string operation, bool stop)
    {
        var service = Service();
        await service.SaveAsync(new DemoRecord { Code = "A", Name = "before" });
        var input = await Csv("code,name,remark\nA,after,\nB,new,\n");
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var committed = new TaskCompletionSource<TaskOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        WorkItem? observed = null;
        tasks.Completed += item => observed = item;
        var id = tasks.EnqueueWithResult("CSV", "Demo", async (progress, token) =>
        {
            TaskOutcome outcome;
            if (operation == "import")
                outcome = (await service.ImportCsvAsync(input, token, progress)).ToTaskOutcome();
            else if (operation == "export")
                outcome = await service.ExportCsvWithResultAsync(null, AppPaths.ExportDirectory, token, progress);
            else
            {
                Directory.CreateDirectory(AppPaths.ExportDirectory);
                var path = Path.Combine(AppPaths.ExportDirectory, Guid.NewGuid().ToString("N") + ".csv");
                outcome = TaskOutcome.Report(1, await service.ExportPreviewErrorsAsync(path,
                    [new CsvIssue(2, "TYPE", "invalid value")], token));
            }
            committed.SetResult(outcome);
            await release.Task;
            return outcome;
        }, allowRetry: true);
        Task? stopping = null;
        try
        {
            var outcome = await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (outcome.CsvPath is { } artifact)
            {
                _artifacts.Add(artifact);
                Assert.True(File.Exists(artifact));
                var content = await CsvReader.ReadFileAsync(artifact, "utf-8");
                Assert.Equal(outcome.WrittenRows, content.Rows.Count - 1);
            }
            else
            {
                Assert.Equal(TaskOutcomeKind.Import, outcome.Kind);
                Assert.Equal(1, outcome.Added);
                Assert.Equal(1, outcome.Updated);
                var records = await service.SearchAsync(null);
                Assert.Equal(2, records.Count);
                Assert.Equal("after", records.Single(record => record.Code == "A").Name);
            }
            if (stop) stopping = tasks.StopAsync();
            else Assert.True(tasks.Cancel(id));
        }
        finally
        {
            release.TrySetResult();
            await (stopping ?? tasks.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        }
        var item = Assert.Single(tasks.Snapshot());
        Assert.True(item.CancellationRequested);
        Assert.Equal(WorkStatus.Succeeded, item.Status);
        Assert.Same(await committed.Task, item.Outcome);
        Assert.NotNull(observed);
        Assert.Same(item.Outcome, observed.Outcome);
        Assert.Equal(WorkStatus.Succeeded, observed.Status);
        Assert.Null(tasks.Retry(id));
    }

    [Fact]
    public async Task Database_rejection_rolls_back_all_rows_and_keeps_failed_row_count_unknown()
    {
        var service = Service();
        await using (var db = await _database.Factory.CreateDbContextAsync())
        {
            await db.Database.ExecuteSqlRawAsync("""
                CREATE TRIGGER reject_test_import BEFORE INSERT ON "demo_records"
                WHEN NEW."Code" = 'B'
                BEGIN SELECT RAISE(ABORT, 'private database failure'); END;
                """);
        }
        var result = await service.ImportCsvAsync(await Csv("code,name,remark\nA,valid,\nB,valid,\n"));
        Assert.False(result.Succeeded);
        Assert.Equal(0, result.Added);
        Assert.Equal(0, result.Updated);
        Assert.Null(result.DiscoveredErrorRows);
        Assert.Null(result.ToTaskOutcome().FailedRows);
        Assert.DoesNotContain("private database failure", result.ToTaskOutcome().ResultText);
        Assert.Empty(await service.SearchAsync(null));
    }

    [Fact]
    public async Task Export_reports_written_row_fractions_and_returns_the_same_artifact_count()
    {
        var service = Service();
        for (var i = 0; i < 3; i++)
            await service.SaveAsync(new DemoRecord { Code = $"A{i}", Name = "test" });
        var progress = new List<double?>();
        var outcome = await service.ExportCsvWithResultAsync(null, AppPaths.ExportDirectory,
            progress: new InlineProgress(progress.Add));
        Assert.NotNull(outcome.CsvPath);
        _artifacts.Add(outcome.CsvPath);
        Assert.Equal(3, outcome.WrittenRows);
        var read = await CsvReader.ReadFileAsync(outcome.CsvPath, "utf-8");
        Assert.Equal(outcome.WrittenRows, read.Rows.Count - 1);
        Assert.Equal(new double?[] { null, 0, 1d / 3, 2d / 3, 1, null }, progress);
    }

    [Fact]
    public async Task Cancelled_export_removes_partial_files_and_does_not_return_success()
    {
        var service = Service();
        await service.SaveAsync(new DemoRecord { Code = "A", Name = "test" });
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.ExportCsvAsync(null, _directory, cancellation.Token,
                new InlineProgress(value => { if (value > 0) cancellation.Cancel(); })));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [AvaloniaFact]
    public async Task Completion_notification_includes_the_same_safe_task_result()
    {
        using var shell = new Shell();
        var tasks = shell.Provider.GetRequiredService<TaskService>();
        var id = tasks.EnqueueWithResult("Import", "Demo", (_, _) =>
            Task.FromResult(TaskOutcome.Rejected(20, false)));
        await tasks.WaitAsync(id);
        Dispatcher.UIThread.RunJobs();
        var item = Assert.Single(tasks.Snapshot());
        Assert.Contains(item.ResultText, shell.ViewModel.TaskNotificationText);
        shell.ViewModel.ViewCompletedTaskCommand.Execute(null);
        var centre = Assert.IsType<TaskDiagnosticsViewModel>(shell.ViewModel.SelectedTab!.Content);
        Assert.Equal(id, centre.SelectedItem!.Id);
        Assert.Same(item.Outcome, centre.SelectedItem.Outcome);
    }

    [AvaloniaFact]
    public async Task Closing_demo_page_keeps_import_export_and_report_results_in_task_centre()
    {
        foreach (var operation in new[] { "import", "export", "report" })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var audit = new SecurityAudit(NullLogger<SecurityAudit>.Instance);
            var users = new OfflineUserContext();
            var operations = new SensitiveOperationService(
                new AuthorizedOperation(new PermissionService(users, audit), users, audit), audit,
                authenticationEnabled: false, production: true)
            {
                ConfirmProductionAsync = () => { entered.TrySetResult(); return release.Task; },
            };
            var tasks = new TaskService(NullLogger<TaskService>.Instance);
            using var page = new DemoListViewModel(Service(operations), NullLogger<DemoListViewModel>.Instance,
                tasks: tasks);
            await page.LoadAsync();
            Directory.CreateDirectory(AppPaths.ExportDirectory);
            var reportPath = Path.Combine(AppPaths.ExportDirectory, Guid.NewGuid().ToString("N") + ".csv");
            if (operation == "import")
                await page.ImportFromAsync(await Csv("code,name,remark\nA,valid,\n"));
            else if (operation == "export")
                await page.ExportCommand.ExecuteAsync(null);
            else
                await page.ExportPreviewErrorsAsync(reportPath, [new CsvIssue(2, "TYPE", "private business value")]);
            try
            {
                await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                var id = Assert.Single(tasks.Snapshot()).Id;
                page.Dispose();
                var changesAfterClose = 0;
                page.PropertyChanged += (_, _) => changesAfterClose++;
                release.SetResult(true);
                await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
                using var centre = new TaskDiagnosticsViewModel(tasks, new DiagnosticService());
                centre.SelectTask(id);
                var item = centre.SelectedItem!;
                Assert.Equal(WorkStatus.Succeeded, item.Status);
                Assert.NotNull(item.Outcome);
                Assert.Equal(operation switch
                {
                    "import" => TaskOutcomeKind.Import,
                    "export" => TaskOutcomeKind.Export,
                    _ => TaskOutcomeKind.Report,
                }, item.Outcome.Kind);
                if (item.Outcome.CsvPath is { } artifact) _artifacts.Add(artifact);
                Assert.NotEmpty(item.ResultText);
                Assert.DoesNotContain("private business value", item.ResultText);
                Assert.Equal(0, changesAfterClose);
            }
            finally
            {
                release.TrySetResult(true);
                await tasks.StopAsync();
            }
        }
    }

    [AvaloniaFact]
    public async Task Selected_error_report_outside_default_exports_keeps_its_path_in_task_centre()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        using var page = new DemoListViewModel(Service(), NullLogger<DemoListViewModel>.Instance, tasks: tasks);
        var path = Path.Combine(_directory, "selected-report.csv");
        try
        {
            await page.ExportPreviewErrorsAsync(path, [new CsvIssue(2, "TYPE", "private business value")]);
            var id = Assert.Single(tasks.Snapshot()).Id;
            await tasks.WaitAsync(id);
            var item = Assert.Single(tasks.Snapshot());
            Assert.Equal(WorkStatus.Succeeded, item.Status);
            Assert.Equal(TaskOutcomeKind.Report, item.Outcome!.Kind);
            Assert.Equal(1, item.Outcome.WrittenRows);
            Assert.Equal(path, item.Outcome.FilePath);
            Assert.Equal(path, item.Outcome.CsvPath);
            Assert.True(File.Exists(path));
            Assert.Contains(path, item.ResultText);
            Assert.DoesNotContain("private business value", item.ResultText);
        }
        finally { await tasks.StopAsync(); }
    }

    private sealed class InlineProgress(Action<double?> report) : IProgress<double?>
    {
        public void Report(double? value) => report(value);
    }
}
