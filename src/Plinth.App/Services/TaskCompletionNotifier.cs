using System;
using System.Diagnostics;
using System.IO;
using Plinth.App.Localization;

namespace Plinth.App.Services;

/// <summary>
/// 任务完成后的 Windows 系统通知。通过随包脚本调用系统 WinRT；内容只含固定的本地化状态，
/// 不带任务标题、路径或异常。通知失败只写调试日志，不改变任务终态。
/// </summary>
internal static class TaskCompletionNotifier
{
    internal const string Aumid = AppInfo.ProductName;

    internal static bool Enabled { get; set; }

    internal static string? ScriptPathOverride { get; set; }

    internal static Action<ProcessStartInfo>? Launcher { get; set; }

    internal static bool TryNotify(WorkStatus status)
    {
        if (!OperatingSystem.IsWindows() || !Enabled)
        {
            return false;
        }

        var script = ScriptPathOverride ?? Path.Combine(AppContext.BaseDirectory, "notify.ps1");
        if (!File.Exists(script))
        {
            return false;
        }

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell", "v1.0", "powershell.exe");
        if (!File.Exists(powershell))
        {
            return false;
        }

        var info = new ProcessStartInfo
        {
            FileName = powershell,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        };
        info.ArgumentList.Add("-NoProfile");
        info.ArgumentList.Add("-NonInteractive");
        info.ArgumentList.Add("-ExecutionPolicy");
        info.ArgumentList.Add("Bypass");
        info.ArgumentList.Add("-WindowStyle");
        info.ArgumentList.Add("Hidden");
        info.ArgumentList.Add("-File");
        info.ArgumentList.Add(script);
        info.ArgumentList.Add("-Aumid");
        info.ArgumentList.Add(Aumid);
        info.ArgumentList.Add("-Title");
        info.ArgumentList.Add(AppInfo.ProductName);
        info.ArgumentList.Add("-Body");
        info.ArgumentList.Add(Loc.Get(BodyKey(status)));
        info.ArgumentList.Add("-LaunchUri");
        info.ArgumentList.Add(StartupArguments.TaskCenterUri);

        try
        {
            (Launcher ?? Start)(info);
            return true;
        }
        catch (Exception ex)
        {
            Serilog.Log.Debug(ex, "系统通知启动失败");
            return false;
        }
    }

    private static void Start(ProcessStartInfo info) => Process.Start(info)?.Dispose();

    private static string BodyKey(WorkStatus status) => status switch
    {
        WorkStatus.Succeeded => "tasks.notify.succeeded",
        WorkStatus.Failed => "tasks.notify.failed",
        _ => "tasks.notify.cancelled",
    };
}
