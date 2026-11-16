using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Serilog;

namespace Plinth.App.Services;

/// <summary>
/// 定时自动备份:启动后与之后每 6 小时各检查一次,距上一份 auto-*.zip 超过设置的间隔天数
/// 就用与手动备份相同的核心再备一份,并按保留份数删最旧。默认关闭,在备份页开启。
/// 认证启用时需要已登录且具备 backup:export 权限,否则本次检查跳过、等下一次。
/// 失败只记日志,绝不影响主流程。
/// </summary>
public sealed class AutoBackupService(BackupService backup, SettingsService settings, string? directory = null)
    : IDisposable
{
    /// <summary>自动备份文件名前缀,与手动备份区分,清理只针对这一类。</summary>
    public const string FilePrefix = "auto-";
    private const int CheckIntervalHours = 6;
    private readonly string _directory = directory ?? AppPaths.BackupDirectory;
    private CancellationTokenSource? _lifetime;
    private Task? _loop;

    public void Start()
    {
        if (_loop is not null) return;
        _lifetime = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_lifetime!.Token));
    }

    private async Task LoopAsync(CancellationToken token)
    {
        try
        {
            // 启动先等 30 秒,避开首帧加载高峰;之后每 6 小时检查一次。
            await Task.Delay(TimeSpan.FromSeconds(30), token);
            var timer = new PeriodicTimer(TimeSpan.FromHours(CheckIntervalHours));
            do
            {
                await CheckAndRunAsync(DateTimeOffset.Now, token);
            }
            while (await timer.WaitForNextTickAsync(token));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Warning(ex, "自动备份循环异常终止");
        }
    }

    /// <summary>按设置检查并在到期时执行;now 由调用方传入,测试可以直接控制时间。返回本次是否创建了备份。</summary>
    public async Task<bool> CheckAndRunAsync(DateTimeOffset now, CancellationToken token = default)
    {
        var current = settings.Current;
        if (!current.AutoBackupEnabled) return false;
        var intervalDays = Math.Clamp(current.AutoBackupIntervalDays, 1, 365);
        var keep = Math.Clamp(current.AutoBackupKeepCount, 1, 100);
        Directory.CreateDirectory(_directory);
        var latest = LatestBackupUtc(_directory);
        if (latest is { } last && now.UtcDateTime - last < TimeSpan.FromDays(intervalDays)) return false;

        // 文件名用本地时间,便于用户在资源管理器里辨认。
        var destination = Path.Combine(_directory, $"{FilePrefix}{now.LocalDateTime:yyyyMMdd-HHmmss}.zip");
        try
        {
            await backup.BackupScheduledAsync(destination, token);
            Prune(_directory, keep);
            Log.Information("自动备份完成:{Path}", destination);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // 权限不足/磁盘/服务器数据库等都归为"本次跳过",等下一次检查。
            Log.Warning(ex, "自动备份未执行:{Path}", destination);
            return false;
        }
    }

    /// <summary>目录里最新一份自动备份的写入时间(UTC);没有返回 null。</summary>
    public static DateTime? LatestBackupUtc(string directory)
    {
        var latest = Directory.GetFiles(directory, FilePrefix + "*.zip")
            .Select(File.GetLastWriteTimeUtc)
            .DefaultIfEmpty(DateTime.MinValue)
            .Max();
        return latest == DateTime.MinValue ? null : latest;
    }

    /// <summary>只保留最近 keep 份自动备份,删除失败(文件被占用等)留给下次。</summary>
    public static void Prune(string directory, int keep)
    {
        var stale = Directory.GetFiles(directory, FilePrefix + "*.zip")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Skip(Math.Max(1, keep));
        foreach (var path in stale)
        {
            try { File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }

    public void Dispose()
    {
        _lifetime?.Cancel();
        _lifetime?.Dispose();
        _lifetime = null;
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch (Exception) { /* 循环已在收尾 */ }
        _loop = null;
    }
}
