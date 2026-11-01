using System.Reflection;
using System.Runtime.InteropServices;

namespace Plinth.App;

/// <summary>版本与运行时信息,概览页和关于页共用。</summary>
public static class AppInfo
{
    public const string ProductName = "Plinth";

    public static string Version { get; } =
        Assembly.GetEntryAssembly()?.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string FrameworkDescription => RuntimeInformation.FrameworkDescription;

    public static string OsDescription => RuntimeInformation.OSDescription;

    public static string ProcessArchitecture => RuntimeInformation.ProcessArchitecture.ToString();
}
