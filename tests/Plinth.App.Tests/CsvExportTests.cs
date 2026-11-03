using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>CSV 导出:转义规则、防公式注入、文件内容。</summary>
public sealed class CsvExportTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-csv-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        _database.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("say \"hi\"", "\"say \"\"hi\"\"\"")]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("", "")]
    public void Escape_follows_csv_rules(string input, string expected) =>
        Assert.Equal(expected, CsvWriter.Escape(input));

    [Fact]
    public async Task Writer_report_has_bom_and_cancel_preserves_destination()
    {
        Directory.CreateDirectory(_directory);
        var report = Path.Combine(_directory, "report.csv");
        await CsvWriter.WriteErrorReportAsync(report, [new CsvIssue(2, "TYPE", "Invalid number")]);
        Assert.True((await File.ReadAllBytesAsync(report)).Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        Assert.Contains("TYPE", await File.ReadAllTextAsync(report));
        Assert.StartsWith("'", CsvWriter.Escape("  =1+1"));
        using var cancellation = new CancellationTokenSource();
        System.Collections.Generic.IEnumerable<System.Collections.Generic.IReadOnlyList<string>> Rows()
        {
            yield return new[] { "first" };
            cancellation.Cancel();
            yield return new[] { "second" };
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvWriter.WriteFileAsync(report, ["a"], Rows(), cancellation.Token));
        Assert.Contains("TYPE", await File.ReadAllTextAsync(report));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Export_retry_rechecks_production_confirmation_before_writing()
    {
        var audit = new RecordingAudit();
        var users = new OfflineUserContext();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authenticationEnabled: false, production: true);
        var confirmations = 0;
        operations.ConfirmProductionAsync = () => Task.FromResult(++confirmations == 1);
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance, operations);
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        // 第一次确认后因目标为普通文件而失败，重试不得沿用上次确认。
        File.WriteAllText(_directory, "blocked");
        try
        {
            var original = tasks.Enqueue("Export", "Demo", async (_, token) =>
            {
                await service.ExportCsvAsync("", _directory, token);
            }, allowRetry: true);
            await tasks.WaitAsync(original);
            Assert.True(tasks.Snapshot().Single().CanRetry);
            File.Delete(_directory);
            var retry = tasks.Retry(original)!.Value;
            await tasks.WaitAsync(retry);
            Assert.Equal(2, confirmations);
            Assert.Equal(WorkStatus.Cancelled, tasks.Snapshot().Single(item => item.Id == retry).Status);
            Assert.False(Directory.Exists(_directory));
            Assert.Equal(new[] { "failed", "cancelled" }, audit.Results);
        }
        finally
        {
            if (File.Exists(_directory)) File.Delete(_directory);
        }
    }

    [Fact]
    public async Task Export_writes_a_bom_csv_with_a_header_and_the_matching_rows()
    {
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance);
        await service.SaveAsync(new DemoRecord { Code = "A1", Name = "苹果,红", Remark = "" });
        await service.SaveAsync(new DemoRecord { Code = "B1", Name = "香蕉", Remark = "" });

        var path = await service.ExportCsvAsync("A", _directory);

        var bytes = await File.ReadAllBytesAsync(path);
        Assert.True(bytes.Take(3).SequenceEqual(new byte[] { 0xEF, 0xBB, 0xBF }));
        var lines = (await File.ReadAllTextAsync(path)).TrimEnd().Split("\r\n");
        Assert.Equal(2, lines.Length); // 表头 + 只有 A1 匹配
        Assert.Equal("A1,\"苹果,红\",", lines[1]);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    private sealed class RecordingAudit : ISecurityAudit
    {
        public System.Collections.Generic.List<string> Results { get; } = [];
        public void Write(AuditEvent entry) => Results.Add(entry.Result);
    }
}
