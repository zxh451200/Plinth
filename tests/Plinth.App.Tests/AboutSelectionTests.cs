using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 关于页的版本/运行环境等信息是静态文字，必须能鼠标拖选并复制（Ctrl+C），
/// 否则用户没法把版本号贴进问题单。
/// </summary>
public sealed class AboutSelectionTests
{
    [AvaloniaFact]
    public void About_text_is_mouse_selectable_and_copyable()
    {
        using var shell = new Shell();
        shell.ViewModel.OpenTabCommand.Execute(
            shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.AboutId));
        shell.Layout();

        var page = shell.PageView<AboutView>();
        var model = (AboutViewModel)page.DataContext!;
        var blocks = page.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        foreach (var expected in new[] { model.ProductName, model.VersionText, model.Description, model.Framework, model.OperatingSystem })
        {
            Assert.Contains(expected, blocks.Select(block => block.Text));
        }

        var framework = blocks.First(block => block.Text == model.Framework);
        var start = framework.TranslatePoint(new Point(1, framework.Bounds.Height / 2), shell.Window)!.Value;
        var end = framework.TranslatePoint(new Point(framework.Bounds.Width - 1, framework.Bounds.Height / 2), shell.Window)!.Value;
        shell.Window.MouseMove(start);
        shell.Window.MouseDown(start, MouseButton.Left);
        for (var step = 1; step <= 5; step++)
        {
            shell.Window.MouseMove(new Point(start.X + ((end.X - start.X) * step / 5), start.Y), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        shell.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(model.Framework, framework.SelectedText);
        Assert.True(framework.CanCopy);
        Assert.True(framework.IsFocused, "点选后要拿到焦点，Ctrl+C 才能走键盘复制");
    }
}
