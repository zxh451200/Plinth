using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Plinth.App.Localization;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>系统通知只传固定状态文本,不把任务标题、路径或异常带到通知里。</summary>
[Collection("Localization")]
public sealed class TaskCompletionNotifierTests
{
    [Fact]
    public void Notification_is_opt_in_and_does_not_launch_by_default()
    {
        var launched = false;
        TaskCompletionNotifier.Launcher = _ => launched = true;
        try
        {
            Assert.False(TaskCompletionNotifier.TryNotify(WorkStatus.Succeeded));
            Assert.False(launched);
        }
        finally
        {
            TaskCompletionNotifier.Launcher = null;
        }
    }

    [Fact]
    public void Launch_arguments_carry_the_fixed_status_and_task_center_uri()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        var script = Path.Combine(Path.GetTempPath(), $"plinth-notify-{Guid.NewGuid():N}.ps1");
        File.WriteAllText(script, "exit 0");
        ProcessStartInfo? captured = null;
        TaskCompletionNotifier.ScriptPathOverride = script;
        TaskCompletionNotifier.Launcher = info => captured = info;
        TaskCompletionNotifier.Enabled = true;
        try
        {
            Assert.True(TaskCompletionNotifier.TryNotify(WorkStatus.Failed));

            Assert.NotNull(captured);
            Assert.EndsWith("powershell.exe", captured!.FileName, StringComparison.OrdinalIgnoreCase);
            var arguments = captured.ArgumentList.ToArray();
            Assert.Contains(script, arguments);
            Assert.Equal(TaskCompletionNotifier.Aumid, arguments[Array.IndexOf(arguments, "-Aumid") + 1]);
            Assert.Equal(AppInfo.ProductName, arguments[Array.IndexOf(arguments, "-Title") + 1]);
            Assert.Equal(Loc.Get("tasks.notify.failed"), arguments[Array.IndexOf(arguments, "-Body") + 1]);
            Assert.Equal(StartupArguments.TaskCenterUri, arguments[Array.IndexOf(arguments, "-LaunchUri") + 1]);
        }
        finally
        {
            TaskCompletionNotifier.Enabled = false;
            TaskCompletionNotifier.Launcher = null;
            TaskCompletionNotifier.ScriptPathOverride = null;
            File.Delete(script);
        }
    }

    [Fact]
    public void Missing_script_degrades_without_throwing()
    {
        TaskCompletionNotifier.ScriptPathOverride = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.ps1");
        TaskCompletionNotifier.Launcher = _ => throw new InvalidOperationException("should not launch");
        TaskCompletionNotifier.Enabled = true;
        try
        {
            Assert.False(TaskCompletionNotifier.TryNotify(WorkStatus.Succeeded));
        }
        finally
        {
            TaskCompletionNotifier.Enabled = false;
            TaskCompletionNotifier.Launcher = null;
            TaskCompletionNotifier.ScriptPathOverride = null;
        }
    }
}
