using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>状态栏两个按钮:远程状态打开设置页,任务状态开合并可再次关闭。</summary>
[Collection("Localization")]
public sealed class StatusBarButtonTests
{
    [AvaloniaFact]
    public void Status_buttons_open_settings_and_toggle_the_task_panel()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();

        // 设置页先打开过一次,复现用户场景:点状态栏按钮要把已有标签切回来。
        var settings = shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.SettingsId);
        shell.ViewModel.OpenTabCommand.Execute(settings);
        shell.Layout();
        var backup = shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.BackupId);
        shell.ViewModel.OpenTabCommand.Execute(backup);
        shell.Layout();
        Assert.Equal(ShellNavigation.BackupId, shell.ViewModel.SelectedTab!.Id);

        var settingsButton = shell.Window.FindControl<Button>("ConnectionStatusButton")!;
        shell.ClickCenter(settingsButton);
        Assert.Equal(ShellNavigation.SettingsId, shell.ViewModel.SelectedTab!.Id);

        var taskButton = shell.Window.FindControl<Button>("TaskStatusButton")!;
        shell.ClickCenter(taskButton);
        Assert.True(shell.ViewModel.IsSidePanelVisible);
        shell.ClickCenter(taskButton);
        Assert.False(shell.ViewModel.IsSidePanelVisible);
    }

    /// <summary>状态栏四项平时都是纯文本:能点的两项悬停才出现底色,且这块灰和导航悬停是同一块,不新增第三种灰。</summary>
    [AvaloniaFact]
    public void Status_items_read_as_text_until_hovered()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var button = shell.Window.FindControl<Button>("ConnectionStatusButton")!;
        var presenter = Presenter(button);

        Assert.Equal(0, button.BorderThickness.Left);
        Assert.Equal(Colors.Transparent, ((ISolidColorBrush)button.Background!).Color);
        Assert.Equal(Colors.Transparent, ColorOf(presenter));

        shell.Window.MouseMove(Over(button));
        Dispatcher.UIThread.RunJobs();
        var statusHover = ColorOf(presenter);
        Assert.NotEqual(Colors.Transparent, statusHover);

        shell.Window.MouseMove(Over(shell.NavToggle));
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(ColorOf(Presenter(shell.NavToggle)), statusHover);
    }

    private static Point Over(Control target) =>
        target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2),
            target.GetVisualAncestors().OfType<Window>().Single())!.Value;

    private static ContentPresenter Presenter(ContentControl control) =>
        control.GetVisualDescendants().OfType<ContentPresenter>()
            .First(candidate => candidate.Name == "PART_ContentPresenter");

    private static Color ColorOf(ContentPresenter presenter) => ((ISolidColorBrush)presenter.Background!).Color;
}
