using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class TaskOutcomeTests
{
    [Fact]
    public async Task Completion_observers_see_the_final_result_and_logs_do_not_contain_artifacts()
    {
        var log = new CaptureLogger();
        var tasks = new TaskService(log);
        var path = Path.Combine(AppPaths.ExportDirectory, "private-output.csv");
        WorkItem? observed = null;
        TaskOutcome? storedDuringCallback = null;
        tasks.Completed += item =>
        {
            observed = item;
            storedDuringCallback = tasks.Snapshot().Single().Outcome;
        };
        var id = tasks.EnqueueWithResult("Export", "Demo", (_, _) =>
            Task.FromResult(TaskOutcome.Export(7, path)));
        await tasks.WaitAsync(id);
        Assert.NotNull(observed);
        Assert.Equal(WorkStatus.Succeeded, observed.Status);
        Assert.Equal(7, observed.Outcome!.WrittenRows);
        Assert.Same(observed.Outcome, storedDuringCallback);
        Assert.Equal(path, observed.Outcome.CsvPath);
        Assert.Equal(observed.Outcome.ResultText, observed.ResultText);
        Assert.DoesNotContain(path, string.Join("\n", log.Messages));
        var json = JsonSerializer.Serialize(observed);
        Assert.DoesNotContain("private-output", json);
        Assert.DoesNotContain("Outcome", json);
        Assert.DoesNotContain("ResultText", json);
    }

    [Fact]
    public async Task Rejection_is_failed_without_parsing_business_error_text()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var id = tasks.EnqueueWithResult("Import", "Demo", (_, _) =>
            Task.FromResult(TaskOutcome.Rejected(20, false)));
        await tasks.WaitAsync(id);
        var item = Assert.Single(tasks.Snapshot());
        Assert.Equal(WorkStatus.Failed, item.Status);
        Assert.Equal("TASK_REJECTED", item.Error!.Code);
        Assert.Equal(item.Outcome!.Summary, item.Error.Message);
        Assert.Equal(0, item.Outcome.Added);
        Assert.Equal(0, item.Outcome.Updated);
        Assert.False(item.Outcome.FailureCountComplete);
        Assert.False(item.CanRetry);
    }

    [Fact]
    public async Task Cancellation_before_commit_has_no_outcome()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.EnqueueWithResult("Import", "Demo", async (_, token) =>
        {
            started.SetResult();
            await release.Task;
            throw new OperationCanceledException(token);
        }, allowRetry: true);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(tasks.Cancel(id));
        }
        finally { release.TrySetResult(); }
        await tasks.WaitAsync(id);
        var item = Assert.Single(tasks.Snapshot());
        Assert.Equal(WorkStatus.Cancelled, item.Status);
        Assert.Null(item.Outcome);
        Assert.Empty(item.ResultText);
        Assert.Null(item.Progress);
        Assert.Null(tasks.Retry(id));
    }

    [Theory]
    [InlineData(TaskOutcomeKind.Import, false)]
    [InlineData(TaskOutcomeKind.Export, false)]
    [InlineData(TaskOutcomeKind.Report, false)]
    [InlineData(TaskOutcomeKind.Rejected, false)]
    [InlineData(TaskOutcomeKind.Import, true)]
    [InlineData(TaskOutcomeKind.Export, true)]
    [InlineData(TaskOutcomeKind.Report, true)]
    [InlineData(TaskOutcomeKind.Rejected, true)]
    public async Task Normal_return_preserves_the_committed_outcome_after_cancel_or_stop(
        TaskOutcomeKind kind, bool stop)
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var path = Path.Combine(AppPaths.ExportDirectory, "committed.csv");
        var outcome = kind switch
        {
            TaskOutcomeKind.Import => TaskOutcome.Import(100, 2),
            TaskOutcomeKind.Export => TaskOutcome.Export(7, path),
            TaskOutcomeKind.Report => TaskOutcome.Report(3, path),
            _ => TaskOutcome.Rejected(2, true, path),
        };
        WorkItem? observed = null;
        tasks.Completed += item => observed = item;
        var id = tasks.EnqueueWithResult("CSV", "Demo", async (_, _) =>
        {
            committed.SetResult();
            await release.Task;
            return outcome;
        });
        Task? stopping = null;
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (stop) stopping = tasks.StopAsync();
            else Assert.True(tasks.Cancel(id));
        }
        finally { release.TrySetResult(); }
        await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        await (stopping ?? tasks.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));
        var item = Assert.Single(tasks.Snapshot());
        Assert.True(item.CancellationRequested);
        Assert.Equal(kind == TaskOutcomeKind.Rejected ? WorkStatus.Failed : WorkStatus.Succeeded, item.Status);
        Assert.Same(outcome, item.Outcome);
        Assert.Equal(outcome.ResultText, item.ResultText);
        Assert.NotNull(item.FinishedAt);
        Assert.NotNull(observed);
        Assert.Same(outcome, observed.Outcome);
        Assert.Equal(item.Status, observed.Status);
        Assert.False(item.CanCancel);
        Assert.Null(tasks.Retry(id));
    }

    [Fact]
    public async Task Retry_uses_a_new_result_and_keeps_the_original_rejection()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var attempts = 0;
        var path = Path.Combine(AppPaths.ExportDirectory, "retry-output.csv");
        var id = tasks.EnqueueWithResult("Export", "Demo", (_, _) => Task.FromResult(
            ++attempts == 1 ? TaskOutcome.Rejected(3, true) : TaskOutcome.Export(9, path)), allowRetry: true);
        await tasks.WaitAsync(id);
        var retry = tasks.Retry(id)!.Value;
        await tasks.WaitAsync(retry);
        var original = tasks.Snapshot().Single(item => item.Id == id);
        var latest = tasks.Snapshot().Single(item => item.Id == retry);
        Assert.Equal(2, attempts);
        Assert.Equal(TaskOutcomeKind.Rejected, original.Outcome!.Kind);
        Assert.Equal(3, original.Outcome.FailedRows);
        Assert.Equal(WorkStatus.Succeeded, latest.Status);
        Assert.Equal(9, latest.Outcome!.WrittenRows);
        Assert.Equal(path, latest.Outcome.CsvPath);
        Assert.NotSame(original.Outcome, latest.Outcome);
    }

    [Fact]
    public async Task Exception_text_is_never_a_result_or_user_error()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var id = tasks.EnqueueWithResult("Export", "Demo", (_, _) =>
            throw new IOException("customer-name password=secret /private/data.csv"));
        await tasks.WaitAsync(id);
        var item = Assert.Single(tasks.Snapshot());
        Assert.Equal(WorkStatus.Failed, item.Status);
        Assert.Null(item.Outcome);
        Assert.Empty(item.ResultText);
        Assert.DoesNotContain("secret", item.Error!.Message);
        Assert.DoesNotContain("customer-name", item.Error.Message);
    }

    [Fact]
    public void Only_bounded_csv_paths_inside_export_directory_are_retained()
    {
        var valid = Path.Combine(AppPaths.ExportDirectory, "nested", "result.csv");
        Assert.Equal(valid, TaskOutcome.Export(1, valid).CsvPath);
        string[] invalid =
        [
            "relative.csv", "https://example.com/result.csv",
            Path.Combine(AppPaths.DataDirectory, "data.csv"),
            Path.Combine(AppPaths.LogDirectory, "log.csv"),
            Path.Combine(AppPaths.ExportDirectory + "-other", "result.csv"),
            Path.Combine(AppPaths.ExportDirectory, "..", "result.csv"),
            Path.Combine(AppPaths.ExportDirectory, "result.csv.exe"),
            Path.Combine(AppPaths.ExportDirectory, "result\n.csv"),
            Path.Combine(AppPaths.ExportDirectory, new string('x', 1100) + ".csv"),
        ];
        foreach (var path in invalid)
        {
            var outcome = TaskOutcome.Report(1, path);
            Assert.Null(outcome.CsvPath);
            Assert.DoesNotContain(path, outcome.ResultText);
        }
    }

    [Fact]
    public void Explicitly_selected_outputs_keep_their_location_without_relaxing_default_exports()
    {
        var csv = Path.Combine(Path.GetTempPath(), "selected-report.csv");
        var xlsx = Path.Combine(Path.GetTempPath(), "selected-workbook.xlsx");
        Assert.Null(TaskOutcome.Report(2, csv).FilePath);
        Assert.Null(TaskOutcome.Export(3, xlsx).FilePath);

        var report = TaskOutcome.ReportSelectedCsv(2, csv);
        Assert.Equal(csv, report.FilePath);
        Assert.Equal(csv, report.CsvPath);
        Assert.Contains(csv, report.ResultText);
        Assert.Equal(2, report.WrittenRows);

        var workbook = TaskOutcome.ExportSelectedXlsx(3, xlsx);
        Assert.Equal(xlsx, workbook.FilePath);
        Assert.Null(workbook.CsvPath);
        Assert.Contains(xlsx, workbook.ResultText);
        Assert.Equal(3, workbook.WrittenRows);
    }

    [Fact]
    public void Explicit_selection_still_rejects_invalid_paths_and_unexpected_extensions()
    {
        string[] invalid =
        [
            "relative.csv", "relative.xlsx", "https://example.com/report.csv",
            Path.Combine(Path.GetTempPath(), "file\n.csv"),
            Path.Combine(Path.GetTempPath(), "file\n.xlsx"),
            Path.Combine(Path.GetTempPath(), new string('x', 1100) + ".csv"),
            Path.Combine(Path.GetTempPath(), "file.exe"),
        ];
        foreach (var path in invalid)
        {
            Assert.Null(TaskOutcome.ReportSelectedCsv(1, path).FilePath);
            Assert.Null(TaskOutcome.ExportSelectedXlsx(1, path).FilePath);
        }
        Assert.Null(TaskOutcome.ReportSelectedCsv(1, Path.Combine(Path.GetTempPath(), "file.xlsx")).FilePath);
        Assert.Null(TaskOutcome.ExportSelectedXlsx(1, Path.Combine(Path.GetTempPath(), "file.csv")).FilePath);
    }

    [Fact]
    public void Symlink_artifacts_and_symlink_directories_are_rejected()
    {
        if (OperatingSystem.IsWindows()) return; // Creating links can require Windows privileges.
        Directory.CreateDirectory(AppPaths.ExportDirectory);
        var directory = Path.Combine(AppPaths.ExportDirectory, "link-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "target.csv");
        var link = Path.Combine(directory, "link.csv");
        var directoryLink = Path.Combine(directory, "outside");
        try
        {
            File.WriteAllText(target, "test");
            File.CreateSymbolicLink(link, target);
            Directory.CreateSymbolicLink(directoryLink, Path.GetTempPath());
            Assert.Null(TaskOutcome.Export(1, link).CsvPath);
            Assert.Null(TaskOutcome.Export(1, Path.Combine(directoryLink, "result.csv")).CsvPath);
            Assert.Null(TaskOutcome.ReportSelectedCsv(1, link).FilePath);
            Assert.Null(TaskOutcome.ReportSelectedCsv(1, Path.Combine(directoryLink, "result.csv")).FilePath);
            Assert.Null(TaskOutcome.ExportSelectedXlsx(1, Path.Combine(directoryLink, "result.xlsx")).FilePath);
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public void Unknown_and_lower_bound_counts_use_resource_keys()
    {
        Assert.Equal(Loc.Format("tasks.result.rejected", Loc.Get("tasks.result.unknown")),
            TaskOutcome.Rejected().Summary);
        Assert.Equal(Loc.Format("tasks.result.rejected", Loc.Format("tasks.result.atLeast", 20)),
            TaskOutcome.Rejected(20).Summary);
        Assert.Null(TaskOutcome.Rejected(-1, true).FailedRows);
        Assert.False(TaskOutcome.Rejected(-1, true).FailureCountComplete);
    }

    private sealed class CaptureLogger : ILogger<TaskService>
    {
        public List<string> Messages { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
