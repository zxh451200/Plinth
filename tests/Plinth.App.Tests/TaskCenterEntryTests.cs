using System.Linq;
using Avalonia.Headless.XUnit;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>系统通知点击和协议启动都走同一个受控入口：打开任务中心标签。</summary>
[Collection("Localization")]
public sealed class TaskCenterEntryTests
{
    [AvaloniaFact]
    public void Opening_the_task_center_uses_the_registered_diagnostics_page()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();

        shell.ViewModel.OpenTaskCenter();

        Assert.Equal(ShellNavigation.TaskDiagnosticsId, shell.ViewModel.SelectedTab?.Id);
        Assert.Single(shell.ViewModel.Tabs.Where(tab => tab.Id == ShellNavigation.TaskDiagnosticsId));
    }
}
