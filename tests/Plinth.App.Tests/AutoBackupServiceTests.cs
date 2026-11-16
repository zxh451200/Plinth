using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Data;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>定时自动备份:到期判断、无人值守创建、间隔跳过与保留份数清理。</summary>
public sealed class AutoBackupServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-autobackup-" + Guid.NewGuid().ToString("N"));

    public AutoBackupServiceTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
        _database.Dispose();
    }

    private sealed class Audit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }

    private static SensitiveOperationService Operations()
    {
        var users = new OfflineUserContext();
        var audit = new Audit();
        return new(new AuthorizedOperation(new PermissionService(users, audit), users, audit), audit);
    }

    private AutoBackupService Create(bool enabled, int intervalDays = 7, int keepCount = 5)
    {
        var path = Path.Combine(_directory, "source.db");
        using var db = DatabaseInitializer.CreateContext(path);
        DatabaseInitializer.Initialize(db);
        var factory = new SingleDbContextFactory(path);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.AutoBackupEnabled = enabled;
        settings.Current.AutoBackupIntervalDays = intervalDays;
        settings.Current.AutoBackupKeepCount = keepCount;
        return new AutoBackupService(new BackupService(factory, Operations()), settings, _directory);
    }

    private sealed class SingleDbContextFactory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => DatabaseInitializer.CreateContext(path);
        public async Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
            => await Task.FromResult(CreateDbContext());
    }

    private static DateTimeOffset Now => new(2026, 11, 11, 10, 0, 0, TimeSpan.FromHours(8));

    [Fact]
    public async Task Disabled_setting_does_nothing()
    {
        using var service = Create(enabled: false);
        Assert.False(await service.CheckAndRunAsync(Now));
        Assert.Empty(Directory.GetFiles(_directory, AutoBackupService.FilePrefix + "*.zip"));
    }

    [Fact]
    public async Task First_check_creates_backup_and_skips_within_interval()
    {
        using var service = Create(enabled: true, intervalDays: 7);
        Assert.True(await service.CheckAndRunAsync(Now));

        var files = Directory.GetFiles(_directory, AutoBackupService.FilePrefix + "*.zip");
        var backup = Assert.Single(files);
        Assert.Matches(@"auto-\d{8}-\d{6}\.zip", Path.GetFileName(backup));
        Assert.True(new FileInfo(backup).Length > 0);

        // 间隔内的下一次检查不再创建。
        Assert.False(await service.CheckAndRunAsync(Now.AddDays(1)));
        Assert.Single(Directory.GetFiles(_directory, AutoBackupService.FilePrefix + "*.zip"));
    }

    [Fact]
    public async Task After_interval_a_new_backup_is_created_and_oldest_is_pruned()
    {
        using var service = Create(enabled: true, intervalDays: 2, keepCount: 2);
        // 两份"历史"自动备份(内容无关紧要,清理与到期只看时间戳),都早于本次。
        var old1 = Path.Combine(_directory, "auto-20261001000000.zip");
        var old2 = Path.Combine(_directory, "auto-20261005000000.zip");
        File.WriteAllText(old1, "old");
        File.WriteAllText(old2, "old");
        File.SetLastWriteTimeUtc(old1, Now.UtcDateTime.AddDays(-20));
        File.SetLastWriteTimeUtc(old2, Now.UtcDateTime.AddDays(-10));

        // 最新的一份已经 5 天前,超过 2 天间隔,应该创建新备份。
        Assert.True(await service.CheckAndRunAsync(Now));

        var files = Directory.GetFiles(_directory, AutoBackupService.FilePrefix + "*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc).ToArray();
        Assert.Equal(2, files.Length); // 新备份 + 保留 1 份,其余被清理
        Assert.Equal(old2, files[1], new PathComparer()); // 保留较新的 old2,最旧的 old1 被清掉
        Assert.False(File.Exists(old1));
    }

    private sealed class PathComparer : IEqualityComparer<string>
    {
        public bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.OrdinalIgnoreCase);
        public int GetHashCode(string obj) => StringComparer.OrdinalIgnoreCase.GetHashCode(obj);
    }
}
