using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Logging;

namespace Plinth.App.Services;

/// <summary>用系统文件管理器打开目录;失败写日志并返回 false,由调用方提示用户。</summary>
public sealed class FolderService(ILogger<FolderService> logger)
{
    public bool TryOpen(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                logger.LogWarning("目录不存在,无法打开:{Path}", path);
                return false;
            }

            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "打开目录失败:{Path}", path);
            return false;
        }
    }
}
