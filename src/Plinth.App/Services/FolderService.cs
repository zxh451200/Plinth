using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;
using Plinth.App.Localization;

namespace Plinth.App.Services;

/// <summary>用系统文件管理器打开目录;失败写日志并返回 false,由调用方提示用户。</summary>
public sealed class FolderService(ILogger<FolderService> logger, LogAccessService? logAccess = null)
{
    /// <summary>
    /// 打开目录并返回给用户看的结果文字("已打开数据目录"或"无法打开数据目录,请检查日志")。
    /// 页面直接把返回值显示出来即可,不用各写一份提示和日志。
    /// </summary>
    public string OpenWithMessage(string path, string displayName) =>
        TryOpen(path) ? Loc.Format("folder.opened", displayName) : Loc.Format("folder.failed", displayName);

    public bool TryOpen(string path)
    {
        try
        {
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var logs = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AppPaths.LogDirectory));
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            if (string.Equals(target, logs, comparison)
                || logs.StartsWith(Path.EndsInDirectorySeparator(target) ? target : target + Path.DirectorySeparatorChar, comparison)
                || target.StartsWith(logs + Path.DirectorySeparatorChar, comparison))
                (logAccess ?? new LogAccessService()).DemandRawAccess();
            if (!Directory.Exists(path))
            {
                logger.LogWarning("目录不存在,无法打开:{Path}", path);
                return false;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "打开目录失败:{Path}", path);
            return false;
        }
    }
}
