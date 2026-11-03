using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class BackupRestorePreviewTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(),
        "plinth-restore-preview-" + Guid.NewGuid().ToString("N"));

    public BackupRestorePreviewTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Manifest_and_confirmation_hash_describe_the_same_snapshot()
    {
        var path = Path.Combine(_directory, "source.zip");
        await new BackupService(_database.Factory).BackupToAsync(path);
        var work = Path.Combine(_directory, "preview");
        using var source = File.OpenRead(path);

        var preview = BackupService.PreviewRestoreFromStream(source, work, default);

        Assert.Equal(SHA256.HashData(File.ReadAllBytes(path)), preview.Hash);
        Assert.Equal(BackupService.Inspect(path).Files, preview.Manifest.Files);
        Assert.False(Directory.Exists(work));
        Assert.True(source.CanRead); // 调用者仍拥有源流。
    }

    [Fact]
    public async Task Renaming_the_source_during_preview_cannot_mix_manifest_and_confirmation_hash()
    {
        if (OperatingSystem.IsWindows()) return; // 默认共享模式在 Windows 阻止重命名。
        var path = Path.Combine(_directory, "source.zip");
        var replacement = Path.Combine(_directory, "replacement.zip");
        var original = Path.Combine(_directory, "original.zip");
        var backup = new BackupService(_database.Factory);
        await backup.BackupToAsync(path);
        var originalBytes = File.ReadAllBytes(path);
        var originalManifest = BackupService.Inspect(path);
        await new DemoService(_database.Factory, NullLogger<DemoService>.Instance)
            .SaveAsync(new DemoRecord { Code = "B", Name = "Replacement" });
        await backup.BackupToAsync(replacement);
        var replacementManifest = BackupService.Inspect(replacement);
        Assert.NotEqual(originalManifest.Files.Single(file => file.Path == "database.db").Sha256,
            replacementManifest.Files.Single(file => file.Path == "database.db").Sha256);

        var work = Path.Combine(_directory, "preview");
        using var source = new ReplacingFileStream(path, () =>
        {
            File.Move(path, original);
            File.Copy(replacement, path);
        });
        var preview = BackupService.PreviewRestoreFromStream(source, work, default);

        Assert.Equal(originalManifest.Files, preview.Manifest.Files);
        Assert.Equal(SHA256.HashData(originalBytes), preview.Hash);
        Assert.Equal(File.ReadAllBytes(replacement), File.ReadAllBytes(path));
        Assert.False(Directory.Exists(work));

        // 预览后路径已指向另一份有效包，暂存必须拒绝而不能恢复未预览的包。
        var target = Path.Combine(_directory, "target.db");
        File.WriteAllText(target + ".restore", "previous pending");
        Assert.NotNull(BackupService.StageRestore(path, target, preview.Hash));
        Assert.Equal("previous pending", File.ReadAllText(target + ".restore"));
        Assert.Empty(Directory.GetFiles(_directory, "target.db.restore.tmp-*"));
    }

    [Fact]
    public void Cancellation_during_snapshot_copy_preserves_source_and_removes_only_its_work()
    {
        var work = Path.Combine(_directory, "preview");
        var unrelated = Path.Combine(_directory, "unrelated");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep"), "keep");
        using var cancellation = new CancellationTokenSource();
        using var source = new CancellingStream(new byte[20000], cancellation.Cancel);

        Assert.Throws<OperationCanceledException>(() =>
            BackupService.PreviewRestoreFromStream(source, work, cancellation.Token));

        Assert.False(Directory.Exists(work));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep")));
        Assert.True(source.CanRead);
    }

    [Fact]
    public void Precancelled_preview_does_not_create_work()
    {
        var work = Path.Combine(_directory, "preview");
        using var source = new MemoryStream();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BackupService.PreviewRestoreFromStream(source, work, cancellation.Token));

        Assert.False(Directory.Exists(work));
    }

    [Fact]
    public void Invalid_archive_removes_snapshot_and_extracted_work()
    {
        var work = Path.Combine(_directory, "preview");
        using var source = new MemoryStream(new byte[1024]);

        Assert.Throws<InvalidDataException>(() =>
            BackupService.PreviewRestoreFromStream(source, work, default));

        Assert.False(Directory.Exists(work));
    }

    private sealed class ReplacingFileStream(string path, Action replace)
        : FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read)
    {
        private bool _replaced;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            if (!_replaced)
            {
                _replaced = true;
                replace();
            }
            return read;
        }
    }

    private sealed class CancellingStream(byte[] bytes, Action cancel) : MemoryStream(bytes)
    {
        private int _reads;
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, Math.Min(count, 4096));
            if (++_reads == 2) cancel();
            return read;
        }
    }
}
