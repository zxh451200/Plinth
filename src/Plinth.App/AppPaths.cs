using System;
using System.IO;

namespace Plinth.App;

/// <summary>
/// 应用数据与日志目录(§15.3):不写安装目录,不写只读缓存目录。
/// Windows 用 %LOCALAPPDATA%;Linux 日志必须落 XDG_STATE_HOME,由这里统一适配。
/// </summary>
public static class AppPaths
{
    public const string ProductName = "Plinth";

    public static string DataDirectory { get; } = Path.Combine(StateRoot("XDG_DATA_HOME", "share"), ProductName);

    public static string LogDirectory { get; } = Path.Combine(StateRoot("XDG_STATE_HOME", "state"), ProductName, "logs");

    /// <summary>Serilog 按天滚动,实际写入形如 plinth-20261101.ndjson。</summary>
    public static string LogFilePath => Path.Combine(LogDirectory, "plinth-.ndjson");

    public static void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogDirectory);
    }

    private static string StateRoot(string xdgVariable, string xdgFallback)
    {
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(xdgVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return fromEnvironment;
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", xdgFallback);
    }
}
