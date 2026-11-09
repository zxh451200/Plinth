using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// Fluent 默认给菜单分割线 40% 黑底和左右各 12px 外边距，渲染出来是一条粗灰带还两头断开。
/// 这里量实际绘制结果：1px、通栏、用应用边框色，防止主题升级后样式被顶掉。
/// </summary>
public sealed class MenuSeparatorStyleTests
{
    [AvaloniaFact]
    public void Menu_separator_is_a_thin_full_width_rule_without_side_gaps()
    {
        var viewMenu = new MenuItem { Header = "视图" };
        viewMenu.Items.Add(new MenuItem { Header = "快速打开" });
        viewMenu.Items.Add(new Separator());
        viewMenu.Items.Add(new MenuItem { Header = "重置工作区布局" });
        var window = new Window { Content = new Menu { Items = { viewMenu } }, Width = 600, Height = 400 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(600, 400));
        window.Arrange(new Rect(0, 0, 600, 400));
        Dispatcher.UIThread.RunJobs();

        viewMenu.IsSubMenuOpen = true;
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(600, 400));
        window.Arrange(new Rect(0, 0, 600, 400));
        Dispatcher.UIThread.RunJobs();

        var popup = viewMenu.GetVisualDescendants().OfType<Popup>().Single();
        var presenter = (Control)popup.Child!;
        var separator = presenter.GetVisualDescendants().OfType<Separator>().Single();

        Assert.Equal(0, separator.Margin.Left);
        Assert.Equal(0, separator.Margin.Right);
        Assert.Equal(1, separator.Bounds.Height);
        Assert.True(
            separator.Bounds.Width >= presenter.Bounds.Width - 2,
            $"分割线宽度 {separator.Bounds.Width} 没有铺满弹层 {presenter.Bounds.Width}");
        Assert.Equal(0, separator.Bounds.X);
        Assert.True(Application.Current!.Resources.TryGetResource(
            "PlinthBorderBrush", separator.ActualThemeVariant, out var expected));
        Assert.Equal(
            ((ISolidColorBrush)expected!).Color,
            ((ISolidColorBrush)separator.Background!).Color);

        window.Close();
    }
}
