using System;
using System.Runtime.InteropServices;

namespace Plinth.App.Services;

/// <summary>WinExe 仅在处理参数时连接已有终端，不为普通桌面启动创建控制台。</summary>
internal static class CommandLineConsole
{
    public static void AttachParentIfNeeded()
    {
        if (!OperatingSystem.IsWindows()) return;
        const int standardOutput = -11;
        const int standardError = -12;
        var output = GetStdHandle(standardOutput);
        var error = GetStdHandle(standardError);
        if (IsValid(output) && IsValid(error)) return;
        if (!AttachConsole(uint.MaxValue)) return;

        // AttachConsole 会重设标准句柄；保留调用方传入的重定向。
        if (IsValid(output)) SetStdHandle(standardOutput, output);
        if (IsValid(error)) SetStdHandle(standardError, error);
    }

    private static bool IsValid(IntPtr handle) => handle != IntPtr.Zero && handle != new IntPtr(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int standardHandle, IntPtr handle);
}
