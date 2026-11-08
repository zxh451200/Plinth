using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 菜单的勾选与子菜单箭头：Fluent 模板把盒子定死为 16x16 / 8x16，且模板优先级高于 Style，
/// 只能用居中的 RenderTransform 收小实际绘制。这里量的是"画出来的尺寸"，防止样式被主题升级顶掉后退回大图标。
/// </summary>
public sealed class MenuGlyphSizeTests
{
    [AvaloniaFact]
    public void Check_and_chevron_glyphs_stay_at_desktop_menu_size()
    {
        var toggle = new MenuItem
        {
            Header = "导航面板",
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = true,
        };
        var submenu = new MenuItem { Header = "最近页面" };
        submenu.Items.Add(new MenuItem { Header = "子项" });
        var host = new StackPanel { Width = 320 };
        host.Children.Add(toggle);
        host.Children.Add(submenu);
        var window = new Window { Content = host, Width = 320, Height = 160 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(320, 160));
        window.Arrange(new Rect(0, 0, 320, 160));
        Dispatcher.UIThread.RunJobs();

        var check = toggle.GetVisualDescendants().OfType<ContentControl>()
            .Single(control => control.Name == "PART_ToggleIconPresenter")
            .GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>().Single();
        var checkSize = RenderedSize(check);
        Assert.InRange(checkSize.Width, 8, 12);
        Assert.InRange(checkSize.Height, 6, 10);

        var chevron = submenu.GetVisualDescendants().OfType<Avalonia.Controls.Shapes.Path>()
            .Single(path => path.Name == "PART_ChevronPath");
        var chevronSize = RenderedSize(chevron);
        Assert.InRange(chevronSize.Width, 4, 7);
        Assert.InRange(chevronSize.Height, 6, 9);
    }

    /// <summary>缩放可能落在当前元素或它的祖先上（勾选缩放挂在 presenter，不在 Path）。</summary>
    private static Size RenderedSize(Visual visual)
    {
        var scaleX = 1d;
        var scaleY = 1d;
        for (Visual? current = visual; current is not null; current = current.GetVisualParent())
        {
            switch (current.RenderTransform)
            {
                case ScaleTransform scale:
                    scaleX *= scale.ScaleX;
                    scaleY *= scale.ScaleY;
                    break;
                case TransformGroup group:
                    scaleX *= group.Children.OfType<ScaleTransform>().Aggregate(1d, (product, item) => product * item.ScaleX);
                    scaleY *= group.Children.OfType<ScaleTransform>().Aggregate(1d, (product, item) => product * item.ScaleY);
                    break;
            }

            if (current is MenuItem) break;
        }

        return new Size(visual.Bounds.Width * scaleX, visual.Bounds.Height * scaleY);
    }
}
