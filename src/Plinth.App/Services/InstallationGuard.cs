using System;
using System.IO;
using System.Runtime.InteropServices;
using Plinth.App.Localization;

namespace Plinth.App.Services;

/// <summary>
/// Windows 应用持有只读共享句柄，允许同一程序目录的多个数据实例运行；
/// 安装器持有独占读取句柄时，新进程不能启动。锁文件随程序交付，不存用户状态。
/// </summary>
public static class InstallationGuard
{
    public const string FileName = "plinth.install.lock";

    public static FileStream Acquire(string applicationDirectory) =>
        new(Path.Combine(applicationDirectory, FileName), FileMode.Open, FileAccess.Read, FileShare.Read);

    internal static void ShowUnavailable()
    {
        if (OperatingSystem.IsWindows())
            MessageBoxW(IntPtr.Zero, Loc.Get("installation.unavailable"), AppInfo.ProductName, 0x10);
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
