using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Data;
using Plinth.App.Security;
using Plinth.App.ViewModels;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>备份与恢复:备份文件可用、坏文件被拒绝、启动时换库并保留旧库。</summary>
public sealed class BackupTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-backup-" + Guid.NewGuid().ToString("N"));

    public BackupTests() => Directory.CreateDirectory(_directory);

    private sealed class Audit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }

    private static SensitiveOperationService Operations(bool authentication = false, bool production = false)
    {
        var users = new OfflineUserContext();
        var audit = new Audit();
        return new(new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authentication, production);
    }

    private sealed class BackupFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public int Reads { get; private set; }
        public TaskCompletionSource? Gate { get; set; }
        public AppDbContext CreateDbContext()
        {
            Reads++;
            return DatabaseInitializer.CreateContext(path);
        }
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            if (Gate is { } gate) await gate.Task.WaitAsync(cancellationToken);
            return CreateDbContext();
        }
    }

    private BackupFactory CreateBackupFactory()
    {
        var path = Path.Combine(_directory, "source.db");
        using var db = DatabaseInitializer.CreateContext(path);
        DatabaseInitializer.Initialize(db);
        return new BackupFactory(path);
    }

    [Fact]
    public async Task Creation_preview_reads_only_metadata_and_includes_only_existing_preferences()
    {
        var factory = CreateBackupFactory();
        var settings = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settings, "{\"Theme\":\"Dark\",\"token\":\"never export\"}");
        File.WriteAllText(Path.Combine(_directory, "workspace-preferences.json"), "{}");
        File.WriteAllText(Path.Combine(_directory, "secret.txt"), "excluded");
        var before = Directory.GetFileSystemEntries(_directory).Order().ToArray();
        var content = File.ReadAllBytes(settings);
        var target = Path.Combine(_directory, "missing-parent", "backup.zip");

        var preview = await new BackupService(factory, Operations()).PreviewBackupAsync(target);

        Assert.Equal(target, preview.Destination);
        Assert.Equal(new[] { "database.db", "settings.json", "workspace-preferences.json" },
            preview.Files.Select(file => file.Path));
        Assert.Equal(content.Length, preview.Files.Single(file => file.Path == "settings.json").EstimatedBytes);
        Assert.Equal(preview.Files.Sum(file => file.EstimatedBytes), preview.EstimatedBytes);
        Assert.Contains("SQLite WAL/SHM/journal files", preview.Excluded);
        Assert.Equal(before, Directory.GetFileSystemEntries(_directory).Order().ToArray());
        Assert.Equal(content, File.ReadAllBytes(settings));
        Assert.False(Directory.Exists(Path.GetDirectoryName(target)));
    }

    [Fact]
    public async Task Preview_and_direct_backup_authorize_before_reading_sources()
    {
        var factory = new BackupFactory(Path.Combine(_directory, "does-not-exist.db"));
        var service = new BackupService(factory, Operations(authentication: true));
        var target = Path.Combine(_directory, "backup.zip");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.PreviewBackupAsync(target));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.BackupToAsync(target));
        Assert.Equal(0, factory.Reads);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Theory]
    [InlineData("source.db")]
    [InlineData("source.db-wal")]
    [InlineData("source.db-shm")]
    [InlineData("source.db-journal")]
    [InlineData("source.db.restore")]
    [InlineData("settings.json")]
    [InlineData("workspace-preferences.json")]
    public async Task Preview_and_direct_backup_reject_protected_targets_without_creating_work(string name)
    {
        var factory = CreateBackupFactory();
        var before = Directory.GetFileSystemEntries(_directory).Order().ToArray();
        var target = Path.Combine(_directory, name);
        var service = new BackupService(factory, Operations());
        await Assert.ThrowsAsync<IOException>(() => service.PreviewBackupAsync(target));
        await Assert.ThrowsAsync<IOException>(() => service.BackupToAsync(target));
        Assert.Equal(before, Directory.GetFileSystemEntries(_directory).Order().ToArray());
    }

    [Fact]
    public async Task Cancelled_preview_and_backup_do_not_read_or_create_files()
    {
        var factory = new BackupFactory(Path.Combine(_directory, "missing.db"));
        var service = new BackupService(factory, Operations());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var target = Path.Combine(_directory, "backup.zip");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreviewBackupAsync(target, cancellation.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BackupToAsync(target, cancellation.Token));
        Assert.Equal(0, factory.Reads);
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
    }

    [Fact]
    public async Task Read_only_preview_does_not_prompt_and_production_rejection_prevents_backup_writes()
    {
        var factory = CreateBackupFactory();
        var operations = Operations(production: true);
        var confirmations = 0;
        operations.ConfirmProductionAsync = () => { confirmations++; return Task.FromResult(false); };
        var service = new BackupService(factory, operations);
        var target = Path.Combine(_directory, "backup.zip");
        var preview = await service.PreviewBackupAsync(target);
        Assert.Equal(target, preview.Destination);
        Assert.Equal(0, confirmations);
        var reads = factory.Reads;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.BackupToAsync(target));
        Assert.Equal(1, confirmations);
        Assert.Equal(reads, factory.Reads);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetDirectories(_directory, "*.work-*"));
    }

    [Fact]
    public async Task Preview_does_not_checkpoint_or_count_WAL_as_an_archived_file()
    {
        var factory = CreateBackupFactory();
        var path = Path.Combine(_directory, "source.db");
        using var connection = new SqliteConnection($"Data Source={path}");
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE preview_probe (id INTEGER); INSERT INTO preview_probe VALUES (1);";
        await command.ExecuteNonQueryAsync();
        var database = ReadShared(path);
        var wal = ReadShared(path + "-wal");
        Assert.NotEmpty(wal);

        var preview = await new BackupService(factory, Operations())
            .PreviewBackupAsync(Path.Combine(_directory, "backup.zip"));

        Assert.Equal(database.LongLength, Assert.Single(preview.Files).EstimatedBytes);
        Assert.Equal(database, ReadShared(path));
        Assert.Equal(wal, ReadShared(path + "-wal"));
    }

    // Windows 上 SQLite 连接打开时不能按默认 FileShare.Read 打开数据库文件。
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    [Fact]
    public async Task Destination_created_after_preview_is_not_overwritten_and_preview_is_consumed()
    {
        var factory = CreateBackupFactory();
        var vm = new BackupViewModel(new BackupService(factory, Operations()), NullLogger<BackupViewModel>.Instance);
        var target = Path.Combine(_directory, "backup.zip");
        await vm.PreviewBackupAsync(target);
        File.WriteAllText(target, "another operation");
        await vm.ConfirmBackupCommand.ExecuteAsync(null);
        Assert.Equal("another operation", File.ReadAllText(target));
        Assert.False(vm.HasBackupPreview);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        Assert.Empty(Directory.GetDirectories(_directory, "*.work-*"));
    }

    [Fact]
    public async Task Late_preview_after_page_close_cannot_enable_confirmation()
    {
        var factory = CreateBackupFactory();
        factory.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new BackupViewModel(new BackupService(factory, Operations()), NullLogger<BackupViewModel>.Instance);
        var target = Path.Combine(_directory, "backup.zip");
        var pending = vm.PreviewBackupAsync(target);
        Assert.True(vm.IsBusy);
        vm.PageClosed();
        factory.Gate.SetResult();
        await pending;
        await vm.ConfirmBackupCommand.ExecuteAsync(null);
        Assert.False(vm.HasBackupPreview);
        Assert.Empty(vm.CreatePreview);
        Assert.False(vm.IsBusy);
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task Confirmation_is_consumed_once_and_global_backup_survives_page_close()
    {
        var factory = CreateBackupFactory();
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var vm = new BackupViewModel(new BackupService(factory, Operations()),
            NullLogger<BackupViewModel>.Instance, tasks);
        var target = Path.Combine(_directory, "backup.zip");
        await vm.PreviewBackupAsync(target);
        Assert.True(vm.HasBackupPreview);
        Assert.False(File.Exists(target));
        factory.Gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirming = vm.ConfirmBackupCommand.ExecuteAsync(null);
        try
        {
            Assert.True(vm.IsBusy);
            Assert.False(vm.HasBackupPreview);
            await vm.BackupToAsync(target); // 直接方法同样不能绕过忙状态。
            Assert.Null(Assert.Single(tasks.Snapshot()).Progress);
            vm.PageClosed();
            factory.Gate.SetResult();
            await confirming.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(WorkStatus.Succeeded, Assert.Single(tasks.Snapshot()).Status);
            Assert.True(File.Exists(target));
            Assert.False(vm.HasBackupPreview);
        }
        finally { factory.Gate.TrySetResult(); await tasks.StopAsync(); }
    }

    [Fact]
    public async Task New_target_or_preview_failure_discards_previous_confirmation()
    {
        var factory = CreateBackupFactory();
        var vm = new BackupViewModel(new BackupService(factory, Operations()), NullLogger<BackupViewModel>.Instance);
        var first = Path.Combine(_directory, "first.zip");
        await vm.PreviewBackupAsync(first);
        Assert.True(vm.HasBackupPreview);
        await vm.PreviewBackupAsync(Path.Combine(_directory, "source.db"));
        Assert.False(vm.HasBackupPreview);
        Assert.Empty(vm.CreatePreview);
        await vm.ConfirmBackupCommand.ExecuteAsync(null);
        Assert.False(File.Exists(first));
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task Backup_creates_a_valid_copy_that_passes_staging()
    {
        await new DemoService(_database.Factory, NullLogger<DemoService>.Instance)
            .SaveAsync(new DemoRecord { Code = "A1", Name = "苹果" });
        var backupPath = Path.Combine(_directory, "backup.db");

        await new BackupService(_database.Factory).BackupToAsync(backupPath);

        Assert.True(File.Exists(backupPath));
        Assert.False(File.Exists(backupPath + ".tmp"));
        Assert.Null(BackupService.StageRestore(backupPath, Path.Combine(_directory, "plinth.db")));
        Assert.True(BackupService.HasPendingRestore(Path.Combine(_directory, "plinth.db")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_during_staging_removes_only_this_attempt_and_preserves_source_and_pending(bool hasPending)
    {
        var backupPath = Path.Combine(_directory, "backup.zip");
        var target = Path.Combine(_directory, "plinth.db");
        await new BackupService(_database.Factory).BackupToAsync(backupPath);
        var original = File.ReadAllBytes(backupPath);
        File.WriteAllText(target, "current database");
        if (hasPending) File.WriteAllText(target + ".restore", "previous pending");
        var unrelatedTemp = target + ".restore.tmp-other-attempt";
        File.WriteAllText(unrelatedTemp, "keep");
        var unrelatedInspect = Path.Combine(_directory, ".inspect-other-attempt");
        Directory.CreateDirectory(unrelatedInspect);
        File.WriteAllText(Path.Combine(unrelatedInspect, "keep"), "keep");
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelOnSecondReadStream(original, () =>
        {
            var temp = Assert.Single(Directory.GetFiles(_directory, "plinth.db.restore.tmp-*")
                .Where(path => path != unrelatedTemp));
            Assert.True(File.Exists(temp)); // 第一块已经交给输出流，再在第二次读取时取消。
            cancellation.Cancel();
        });

        var error = Assert.Throws<OperationCanceledException>(() =>
            StageFromStream(source, target, SHA256.HashData(original), cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.Equal(original, File.ReadAllBytes(backupPath));
        Assert.Equal("current database", File.ReadAllText(target));
        Assert.Equal(hasPending, BackupService.HasPendingRestore(target));
        if (hasPending) Assert.Equal("previous pending", File.ReadAllText(target + ".restore"));
        Assert.Equal(new[] { unrelatedTemp }, Directory.GetFiles(_directory, "plinth.db.restore.tmp-*"));
        Assert.Equal(new[] { unrelatedInspect }, Directory.GetDirectories(_directory, ".inspect-*"));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelatedInspect, "keep")));
    }

    [Fact]
    public void Already_cancelled_public_operations_throw_without_touching_files()
    {
        var source = Path.Combine(_directory, "source.zip");
        var target = Path.Combine(_directory, "plinth.db");
        File.WriteAllText(source, "source");
        File.WriteAllText(target + ".restore", "previous pending");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            BackupService.StageRestore(source, target, cancellationToken: cancellation.Token));
        Assert.Throws<OperationCanceledException>(() => BackupService.Inspect(source, cancellation.Token));

        Assert.Equal("source", File.ReadAllText(source));
        Assert.Equal("previous pending", File.ReadAllText(target + ".restore"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
        Assert.Empty(Directory.GetDirectories(_directory, ".inspect-*"));
    }

    [Fact]
    public void Hash_validation_observes_cancellation_during_read()
    {
        using var cancellation = new CancellationTokenSource();
        using var source = new CancelOnSecondReadStream(new byte[20000], cancellation.Cancel);

        var error = Assert.Throws<OperationCanceledException>(() =>
            InvokeBackupHelper("HashWithCancellation", source, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Cancellation_during_inspection_cleans_its_extraction_directory_only()
    {
        var backupPath = Path.Combine(_directory, "backup.zip");
        await new BackupService(_database.Factory).BackupToAsync(backupPath);
        var original = File.ReadAllBytes(backupPath);
        var inspection = Path.Combine(_directory, ".inspect-current-attempt");
        var unrelated = Path.Combine(_directory, ".inspect-previous-attempt");
        Directory.CreateDirectory(unrelated);
        File.WriteAllText(Path.Combine(unrelated, "keep"), "keep");
        using var cancellation = new CancellationTokenSource();
        using var source = new ObservedReadStream(original, () =>
        {
            // ZIP 元数据和 manifest 已读完，数据库输出文件已创建才取消。
            if (File.Exists(Path.Combine(inspection, "database.db"))) cancellation.Cancel();
        });

        var error = Assert.Throws<OperationCanceledException>(() =>
            InvokeBackupHelper("InspectFromStream", source, inspection, cancellation.Token));

        Assert.Equal(cancellation.Token, error.CancellationToken);
        Assert.False(Directory.Exists(inspection));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(unrelated, "keep")));
        Assert.Equal(original, File.ReadAllBytes(backupPath));
        Assert.False(BackupService.HasPendingRestore(Path.Combine(_directory, "plinth.db")));
    }

    [Fact]
    public async Task Task_service_reports_cancelled_when_staging_is_cancelled_after_partial_copy()
    {
        var target = Path.Combine(_directory, "plinth.db");
        File.WriteAllText(target + ".restore", "previous pending");
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.Empty;
        id = tasks.Enqueue("Restore", "Backup", async (_, token) =>
        {
            await start.Task;
            using var source = new CancelOnSecondReadStream(new byte[20000], () =>
            {
                Assert.True(tasks.Cancel(id));
                Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
            });
            StageFromStream(source, target, null, token);
        });
        start.SetResult();
        try
        {
            await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(10));

            var item = Assert.Single(tasks.Snapshot());
            Assert.Equal(WorkStatus.Cancelled, item.Status);
            Assert.Null(item.Error);
            Assert.Equal("previous pending", File.ReadAllText(target + ".restore"));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
            Assert.Empty(Directory.GetDirectories(_directory, ".inspect-*"));
        }
        finally { await tasks.StopAsync(); }
    }

    [Fact]
    public async Task Cancellation_after_publication_keeps_success_and_the_published_restore()
    {
        var source = Path.Combine(_directory, "backup.zip");
        var target = Path.Combine(_directory, "plinth.db");
        await new BackupService(_database.Factory).BackupToAsync(source);
        var original = File.ReadAllBytes(source);
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var id = Guid.Empty;
        id = tasks.Enqueue("Restore", "Backup", async (_, token) =>
        {
            await start.Task;
            Assert.Null(BackupService.StageRestore(source, target, SHA256.HashData(original), token));
            Assert.True(tasks.Cancel(id));
            Assert.True(token.WaitHandle.WaitOne(TimeSpan.FromSeconds(10)));
        });
        start.SetResult();
        try
        {
            await tasks.WaitAsync(id).WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Equal(WorkStatus.Succeeded, Assert.Single(tasks.Snapshot()).Status);
            Assert.Equal(original, File.ReadAllBytes(target + ".restore"));
            Assert.Equal(original, File.ReadAllBytes(source));
            Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
            Assert.Empty(Directory.GetDirectories(_directory, ".inspect-*"));
        }
        finally { await tasks.StopAsync(); }
    }

    // 直接向实际暂存实现提供受控输入流；不在生产代码增加测试回调。
    private static void StageFromStream(Stream source, string target, byte[]? hash, CancellationToken token) =>
        InvokeBackupHelper("StageRestoreFromStream", source, target, hash, token);

    private static void InvokeBackupHelper(string name, params object?[] arguments)
    {
        var method = typeof(BackupService).GetMethod(name, BindingFlags.NonPublic | BindingFlags.Static)!;
        try { method.Invoke(null, arguments); }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            ExceptionDispatchInfo.Capture(ex.InnerException).Throw();
        }
    }

    private sealed class CancelOnSecondReadStream(byte[] bytes, Action cancel) : MemoryStream(bytes)
    {
        private int _reads;

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, Math.Min(count, 4096));
            if (++_reads == 2) cancel();
            return read;
        }
    }

    private sealed class ObservedReadStream(byte[] bytes, Action observe) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, count);
            observe();
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            var read = base.Read(buffer);
            observe();
            return read;
        }
    }

    [Fact]
    public void Staging_rejects_files_that_are_not_sqlite_databases()
    {
        var fake = Path.Combine(_directory, "fake.db");
        File.WriteAllText(fake, "this is not a database, just some text that is long enough");
        var target = Path.Combine(_directory, "plinth.db");

        Assert.NotNull(BackupService.StageRestore(fake, target));
        Assert.False(BackupService.HasPendingRestore(target));
    }

    [Fact]
    public void Applying_an_invalid_pending_restore_keeps_the_old_database()
    {
        var target = Path.Combine(_directory, "plinth.db");
        File.WriteAllText(target, "old");
        File.WriteAllText(target + ".restore", "new");

        Assert.False(BackupService.ApplyPendingRestore(target));

        Assert.Equal("old", File.ReadAllText(target));
        Assert.True(File.Exists(target + ".restore"));
        Assert.Empty(Directory.GetFiles(_directory, "plinth.db.before-restore-*"));
    }

    [Fact]
    public void Applying_without_a_pending_restore_does_nothing()
    {
        var target = Path.Combine(_directory, "plinth.db");
        File.WriteAllText(target, "old");

        Assert.False(BackupService.ApplyPendingRestore(target));
        Assert.Equal("old", File.ReadAllText(target));
    }
}
