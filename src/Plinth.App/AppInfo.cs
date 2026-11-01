using System.Runtime.InteropServices;

namespace Plinth.App;

/// <summary>版本与运行时信息,概览页和关于页共用。</summary>
public static class AppInfo
{
    public const string ProductName = "Plinth";

    /// <summary>取本框架所在程序集,不是入口程序集:宿主/测试宿主里 GetEntryAssembly() 会读到别人的版本。</summary>
    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string FrameworkDescription => RuntimeInformation.FrameworkDescription;

    public static string OsDescription => RuntimeInformation.OSDescription;

    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();
}
