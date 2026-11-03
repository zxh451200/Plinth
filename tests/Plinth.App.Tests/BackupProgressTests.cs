using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class BackupProgressTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-backup-progress-" + Guid.NewGuid().ToString("N"));

    public BackupProgressTests() => Directory.CreateDirectory(_directory);

    [Fact]
    public async Task Compression_reports_measured_progress_and_publishes_a_valid_archive()
    {
        var values = new List<double?>();
        var path = Path.Combine(_directory, "backup.zip");
        await new BackupService(_database.Factory).BackupToAsync(path, progress: new ImmediateProgress(values.Add));
        Assert.Contains(values, value => value is null);
        var measured = values.Where(value => value.HasValue).Select(value => value!.Value).ToArray();
        Assert.NotEmpty(measured);
        Assert.Equal(0d, measured[0]);
        Assert.Equal(1d, measured[^1]);
        Assert.Equal(measured.OrderBy(value => value).ToArray(), measured);
        Assert.NotEmpty(BackupService.Inspect(path).Files);
    }

    [Fact]
    public async Task Cancellation_during_compression_does_not_publish_or_leave_temporary_files()
    {
        var path = Path.Combine(_directory, "backup.zip");
        using var cancellation = new CancellationTokenSource();
        var progress = new ImmediateProgress(value =>
        {
            if (value is > 0 and < 1) cancellation.Cancel();
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new BackupService(_database.Factory).BackupToAsync(path, cancellation.Token, progress));
        Assert.True(cancellation.IsCancellationRequested);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    private sealed class ImmediateProgress(Action<double?> report) : IProgress<double?>
    {
        public void Report(double? value) => report(value);
    }
}
