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
/// 概览页的版本、运行时与目录是排障时要贴给支持人员的关键信息，
/// 和关于页一样必须能鼠标拖选并复制（Ctrl+C）。
/// </summary>
public sealed class OverviewSelectionTests
{
    [AvaloniaFact]
    public void Overview_values_are_mouse_selectable_and_copyable()
    {
        using var shell = new Shell();
        shell.ViewModel.OpenTabCommand.Execute(
            shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.OverviewId));
        shell.Layout();

        var page = shell.PageView<OverviewView>();
        var model = (OverviewViewModel)page.DataContext!;
        var blocks = page.GetVisualDescendants().OfType<SelectableTextBlock>().ToList();
        foreach (var expected in new[]
                 {
                     model.Version, model.Framework, model.OperatingSystem, model.Architecture,
                     model.DataDirectory, model.LogDirectory, model.LogSummary,
                 })
        {
            Assert.Contains(expected, blocks.Select(block => block.Text));
        }

        var dataDirectory = blocks.First(block => block.Text == model.DataDirectory);
        var start = dataDirectory.TranslatePoint(new Point(1, dataDirectory.Bounds.Height / 2), shell.Window)!.Value;
        var end = dataDirectory.TranslatePoint(new Point(dataDirectory.Bounds.Width - 1, dataDirectory.Bounds.Height / 2), shell.Window)!.Value;
        shell.Window.MouseMove(start);
        shell.Window.MouseDown(start, MouseButton.Left);
        for (var step = 1; step <= 5; step++)
        {
            shell.Window.MouseMove(new Point(start.X + ((end.X - start.X) * step / 5), start.Y), RawInputModifiers.LeftMouseButton);
            Dispatcher.UIThread.RunJobs();
        }
        shell.Window.MouseUp(end, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(model.DataDirectory, dataDirectory.SelectedText);
        Assert.True(dataDirectory.CanCopy);
        Assert.True(dataDirectory.IsFocused, "点选后要拿到焦点，Ctrl+C 才能走键盘复制");
    }
}
