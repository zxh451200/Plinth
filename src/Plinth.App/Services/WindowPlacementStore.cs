using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Controls;

namespace Plinth.App.Services;

/// <summary>
/// 记住主窗口的位置和大小:关闭时写进设置,下次启动时恢复。
/// 恢复前先确认窗口标题栏还在某个屏幕里(换了显示器、改了分辨率后,保存的位置可能已经在屏幕外),不在就用默认位置。
/// </summary>
public static class WindowPlacementStore
{
    private const double MinWidth = 400;
    private const double MinHeight = 300;

    /// <summary>窗口左上角往里这么多像素的点必须落在某个屏幕的工作区内,才算"用户还够得着这个窗口"。</summary>
    private const int GrabOffsetX = 60;
    private const int GrabOffsetY = 12;

    /// <summary>保存的位置能不能用。纯计算,不碰界面,方便测试。</summary>
    public static bool IsUsable(WindowPlacement? placement, IReadOnlyList<PixelRect> screenAreas)
    {
        if (placement is null || placement.Width < MinWidth || placement.Height < MinHeight)
        {
            return false;
        }

        var grabPoint = new PixelPoint(placement.X + GrabOffsetX, placement.Y + GrabOffsetY);
        return screenAreas.Any(area => area.Contains(grabPoint));
    }

    /// <summary>窗口创建后、显示前调用。</summary>
    public static void Restore(Window window, SettingsService settings)
    {
        var areas = window.Screens?.All.Select(screen => screen.WorkingArea).ToList() ?? [];
        if (settings.Current.Window is not { } placement || !IsUsable(placement, areas))
        {
            return;
        }

        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Position = new PixelPoint(placement.X, placement.Y);
        window.Width = placement.Width;
        window.Height = placement.Height;
        if (placement.Maximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>窗口关闭时调用。最小化时不更新(最小化的坐标没有意义);最大化时只记"最大化了",保留之前的普通大小。</summary>
    public static void Remember(Window window, SettingsService settings)
    {
        if (window.WindowState == WindowState.Minimized)
        {
            return;
        }

        var placement = settings.Current.Window ?? new WindowPlacement();
        if (window.WindowState == WindowState.Normal)
        {
            placement.X = window.Position.X;
            placement.Y = window.Position.Y;
            placement.Width = window.ClientSize.Width;
            placement.Height = window.ClientSize.Height;
        }
        else if (placement.Width <= 0)
        {
            // 从没记录过普通大小就直接最大化了:先记当前尺寸,免得下次取消最大化后窗口是 0
            placement.X = window.Position.X;
            placement.Y = window.Position.Y;
            placement.Width = window.ClientSize.Width;
            placement.Height = window.ClientSize.Height;
        }

        placement.Maximized = window.WindowState == WindowState.Maximized;
        settings.Current.Window = placement;
        settings.Save();
    }
}
