using System;
using System.IO;
using System.Linq;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>日志读取:解析 CompactJson 行、取最新文件的最后 N 条、坏行跳过。</summary>
public sealed class LogReaderTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-logs-" + Guid.NewGuid().ToString("N"));

    public LogReaderTests() => Directory.CreateDirectory(_directory);

    public void Dispose() => Directory.Delete(_directory, recursive: true);

    [Fact]
    public void Parse_fills_template_placeholders_and_defaults_to_information()
    {
        var entry = LogReader.Parse("""{"@t":"2026-11-01T08:00:00.0000000Z","@mt":"Opened {Page} in {Ms}ms","Page":"demo","Ms":12}""");

        Assert.NotNull(entry);
        Assert.Equal("Information", entry.Level);
        Assert.Equal("Opened demo in 12ms", entry.Message);
    }

    [Fact]
    public void Parse_keeps_level_and_appends_the_exception()
    {
        var entry = LogReader.Parse("""{"@t":"2026-11-01T08:00:00.0000000Z","@mt":"Failed","@l":"Error","@x":"System.Exception: boom"}""");

        Assert.NotNull(entry);
        Assert.Equal("Error", entry.Level);
        Assert.Contains("boom", entry.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("""{"@mt":"no time"}""")]
    [InlineData("[1,2]")]
    public void Parse_returns_null_for_lines_that_are_not_log_entries(string line) =>
        Assert.Null(LogReader.Parse(line));

    [Fact]
    public void Parse_tolerates_wrong_json_types_and_redacts_secrets()
    {
        Assert.Null(LogReader.Parse("""{"@t":42,"@l":{}}"""));
        var entry = LogReader.Parse("""{"@t":"2026-11-01T08:00:00Z","@mt":"Failed {Token} password=hunter2 alice@example.com https://host?q=private","Token":"raw-token"}""");
        Assert.NotNull(entry);
        foreach (var secret in new[] { "raw-token", "hunter2", "alice", "private" })
            Assert.DoesNotContain(secret, entry.Details);
    }

    [Fact]
    public void Numeric_guid_ids_survive_redaction_and_query_filters_all_files()
    {
        const string id = "10000000-0000-0000-0000-000000000001";
        File.WriteAllText(Path.Combine(_directory, "old.ndjson"),
            """{"@t":"2026-10-01T08:00:00Z","@mt":"wanted","Module":"Demo","TaskId":"10000000-0000-0000-0000-000000000001"}""");
        File.WriteAllText(Path.Combine(_directory, "new.ndjson"),
            """{"@t":"2026-11-01T08:00:00Z","@mt":"other","Module":"Shell"}""");
        var result = LogReader.Query(_directory, new(Module: "Demo", TaskId: id), 1);
        var entry = Assert.Single(result.Entries);
        Assert.Equal(id, entry.TaskId);
        Assert.Equal("wanted", entry.Message);
    }

    [Fact]
    public void Problems_are_filtered_before_result_limit()
    {
        File.WriteAllLines(Path.Combine(_directory, "log.ndjson"),
        [
            """{"@t":"2026-11-01T08:00:00Z","@mt":"failure","@l":"Error"}""",
            """{"@t":"2026-11-01T09:00:00Z","@mt":"normal"}""",
        ]);
        var result = LogReader.Query(_directory, new(OnlyProblems: true), 1);
        Assert.Equal("failure", Assert.Single(result.Entries).Message);
        Assert.False(result.Truncated);
    }

    [Fact]
    public async Task Successful_commit_remains_successful_after_late_cancel_request()
    {
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = tasks.Enqueue("Commit", "Demo", async (_, _) =>
        {
            started.SetResult();
            await finish.Task;
        });
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        tasks.Cancel(id);
        finish.SetResult();
        await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkStatus.Succeeded, tasks.Snapshot().Single().Status);
    }

    [Fact]
    public void ReadLatest_returns_the_last_entries_of_the_newest_file_newest_first()
    {
        File.WriteAllText(Path.Combine(_directory, "plinth-20261031.ndjson"), """{"@t":"2026-10-31T08:00:00Z","@mt":"old file"}""");
        File.WriteAllLines(Path.Combine(_directory, "plinth-20261101.ndjson"),
        [
            """{"@t":"2026-11-01T08:00:01Z","@mt":"one"}""",
            "broken line",
            """{"@t":"2026-11-01T08:00:02Z","@mt":"two"}""",
            """{"@t":"2026-11-01T08:00:03Z","@mt":"three"}""",
        ]);

        var entries = LogReader.ReadLatest(_directory, maxEntries: 3);

        // 取文件最后 3 行(含坏行),坏行丢掉,新的在前
        Assert.Equal(["three", "two"], entries.Select(entry => entry.Message));
    }

    [Fact]
    public void Query_rejects_a_line_larger_than_one_MiB()
    {
        File.WriteAllText(Path.Combine(_directory, "oversized.ndjson"), new string('x', 1024 * 1024 + 1));

        Assert.Throws<InvalidDataException>(() => LogReader.Query(_directory, new LogFilter()));
    }

    [Fact]
    public void ReadLatest_returns_empty_when_the_folder_is_missing_or_has_no_logs()
    {
        Assert.Empty(LogReader.ReadLatest(Path.Combine(_directory, "missing")));
        Assert.Empty(LogReader.ReadLatest(_directory));
    }

    [Fact]
    public void DeleteOlderThan_removes_only_old_log_files()
    {
        var old = Path.Combine(_directory, "plinth-old.ndjson");
        var recent = Path.Combine(_directory, "plinth-recent.ndjson");
        File.WriteAllText(old, "old");
        File.WriteAllText(recent, "recent");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-40));
        File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-2));

        var deleted = LogReader.DeleteOlderThan(_directory, TimeSpan.FromDays(30));

        Assert.Equal(1, deleted);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(recent));
    }

    [Fact]
    public async Task Task_cancellation_reaches_work_and_failure_does_not_expose_secrets()
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
        var failed = tasks.Enqueue("Export", "Demo", (_, _) => throw new IOException("password=secret"));
        await tasks.WaitAsync(failed);
        var error = tasks.Snapshot().Single(x => x.Id == failed).Error!;
        Assert.Equal(failed.ToString(), error.CorrelationId);
        Assert.DoesNotContain("secret", error.Message);
    }

    [Fact]
    public async Task Diagnostic_archive_requires_confirmation_and_excludes_free_text()
    {
        var log = Path.Combine(_directory, "test.ndjson");
        await File.WriteAllTextAsync(log, """
            {"@t":"2026-11-01T08:00:00Z","@l":"Error","@mt":"客户张三 password=hunter2 token=abc alice@example.com https://example.com?q=private /home/alice/db","@x":"secret stack","TaskId":"not-a-guid","Body":{"token":"hidden"}}
            """);
        var service = new DiagnosticService();
        var preview = await service.PreviewAsync(_directory, new(new()));
        var target = Path.Combine(_directory, "bundle.zip");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(preview.Id, false, target));
        Assert.False(File.Exists(target));
        await File.AppendAllTextAsync(log, "\n" +
            """{"@t":"2026-11-01T09:00:00Z","@l":"Warning","@mt":"added after preview"}""");
        await service.ExportAsync(preview.Id, true, target);
        using var zip = ZipFile.OpenRead(target);
        Assert.Equal(preview.Files.Select(x => x.Name).OrderBy(x => x), zip.Entries.Select(x => x.FullName).OrderBy(x => x));
        foreach (var entry in zip.Entries)
        {
            using var reader = new StreamReader(entry.Open());
            var text = await reader.ReadToEndAsync();
            Assert.Equal(preview.Files.Single(x => x.Name == entry.FullName).Bytes, entry.Length);
            foreach (var secret in new[] { "张三", "hunter2", "abc", "alice", "private", "secret stack", "hidden", "not-a-guid" })
                Assert.DoesNotContain(secret, text);
        }
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Expired_preview_rejected_and_cancel_leaves_no_archive()
    {
        var service = new DiagnosticService();
        var first = await service.PreviewAsync(_directory, new(new()));
        var second = await service.PreviewAsync(_directory, new(new()));
        var path = Path.Combine(_directory, "bundle.zip");
        await Assert.ThrowsAsync<InvalidOperationException>(() => service.ExportAsync(first.Id, true, path));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportAsync(second.Id, true, path, cancellation.Token));
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }
}
