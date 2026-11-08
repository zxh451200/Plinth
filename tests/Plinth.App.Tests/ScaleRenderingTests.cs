using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>渲染缩放 100/125/150/200% 下像素尺寸与主框架占位必须同比例且不越界。</summary>
[Collection("Localization")]
public sealed class ScaleRenderingTests
{
    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Main_window_renders_at_requested_scale(double scale)
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        shell.Window.SetRenderScaling(scale);
        shell.Layout();

        Assert.Equal(scale, shell.Window.RenderScaling, 2);

        var frame = shell.Window.CaptureRenderedFrame();
        Assert.NotNull(frame);
        Assert.Equal((int)Math.Round(1200 * scale), frame!.PixelSize.Width);
        Assert.Equal((int)Math.Round(720 * scale), frame.PixelSize.Height);

        var workspaceRight = shell.Workspace.Bounds.Right;
        Assert.True(
            workspaceRight <= shell.Window.Bounds.Width + 1,
            $"{scale:P0} 下工作区右缘 {workspaceRight} 超出窗口 {shell.Window.Bounds.Width}");
        Assert.True(shell.NavList.Bounds.Width > 0, $"{scale:P0} 下导航列表宽度为 0");
        Assert.True(shell.NavigationTitle.Bounds.Width > 0, $"{scale:P0} 下导航标题宽度为 0");

        var artifacts = Environment.GetEnvironmentVariable("PLINTH_SCALE_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(artifacts))
        {
            Directory.CreateDirectory(artifacts);
            frame.Save(Path.Combine(artifacts, $"dpi-{scale * 100:0}.png"));
        }
    }

    [AvaloniaTheory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void Dense_pages_do_not_scroll_horizontally_at_scale(double scale)
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        shell.Window.SetRenderScaling(scale);
        shell.Layout();

        var ids = new[]
        {
            ShellNavigation.OverviewId, ShellNavigation.LogsId,
            ShellNavigation.TaskDiagnosticsId, ShellNavigation.SettingsId
        };
        foreach (var id in ids)
        {
            var item = shell.ViewModel.NavigationItems.Single(candidate => candidate.Id == id);
            shell.ViewModel.OpenTabCommand.Execute(item);
            shell.Layout();

            var horizontal = shell.Window.GetVisualDescendants().OfType<ScrollViewer>()
                .Where(viewer => viewer.IsVisible && viewer.Viewport.Width > 0)
                .Where(viewer => viewer.HorizontalScrollBarVisibility != ScrollBarVisibility.Disabled)
                // 表格自身允许横向滚动；这里只查页面级容器是否把内容裁出工作区。
                .Where(viewer => viewer.FindAncestorOfType<TableView>() is null)
                .ToArray();
            foreach (var viewer in horizontal)
            {
                Assert.True(
                    viewer.Extent.Width <= viewer.Viewport.Width + 1,
                    $"{item.Title} 在 {scale:P0} 下横向溢出:extent {viewer.Extent.Width} > viewport {viewer.Viewport.Width}");
            }
        }
    }
}
