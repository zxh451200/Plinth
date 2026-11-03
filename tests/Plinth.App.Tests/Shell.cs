using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App;
using Plinth.App.ViewModels;
using Plinth.App.Views;

namespace Plinth.App.Tests;

/// <summary>
/// 一个真实的 Shell:走组合根解析 MainViewModel、开 MainWindow、量好布局,
/// 并提供指针/键盘输入——断言打在用户真点得着的东西上,而不是只调 Command。
/// </summary>
internal sealed class Shell : IDisposable
{
    private const double Width = 1200;
    private const double Height = 720;

    public Shell(Action<IServiceCollection>? configure = null)
    {
        // 用例共享同一临时数据目录；清掉上一个用例保存的工作区，保证默认标签存在。
        try { File.Delete(Path.Combine(AppPaths.DataDirectory, "workspace-preferences.json")); }
        catch (IOException) { }
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IClassicDesktopStyleApplicationLifetime>(
            new ClassicDesktopStyleApplicationLifetime { Args = Array.Empty<string>() });
        services.AddPlinthModules();
        configure?.Invoke(services);
        Provider = services.BuildServiceProvider();

        ViewModel = Provider.GetRequiredService<MainViewModel>();
        Window = new MainWindow { DataContext = ViewModel };
        Window.Show();
        Window.Activate();
        Layout();
    }

    public ServiceProvider Provider { get; }

    public MainWindow Window { get; }

    public MainViewModel ViewModel { get; }

    public ListBox NavList => Window.FindControl<ListBox>("NavigationList")!;

    public ListBox NavFooter => Window.FindControl<ListBox>("NavigationFooter")!;

    public Button NavToggle => Window.FindControl<Button>("NavigationToggle")!;

    public TextBlock NavigationTitle => Window.FindControl<TextBlock>("NavigationTitle")!;

    public TabControl TabHost => Window.FindControl<TabControl>("WorkspaceTabs")!;

    public GridSplitter Splitter => Window.FindControl<GridSplitter>("SidebarSplitter")!;

    public Grid ShellGrid => (Grid)Splitter.Parent!;

    public ColumnDefinition SidebarColumn => ShellGrid.ColumnDefinitions[0];

    public Control Sidebar => (Control)ShellGrid.Children[0];

    public Control Workspace => (Control)ShellGrid.Children[2];

    public void Layout()
    {
        Dispatcher.UIThread.RunJobs();
        Window.Measure(new Size(Width, Height));
        Window.Arrange(new Rect(0, 0, Width, Height));
        Dispatcher.UIThread.RunJobs();
    }

    public void ClickCenter(Visual target) => Click(PointOf(target, target.Bounds.Width / 2, target.Bounds.Height / 2));

    public void ClickAt(Visual target, double x, double y) => Click(PointOf(target, x, y));

    public void Click(Point point)
    {
        Window.MouseMove(point);
        Window.MouseDown(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Window.MouseUp(point, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    public void DragSplitterBy(double dx)
    {
        var start = PointOf(Splitter, Splitter.Bounds.Width / 2, Splitter.Bounds.Height / 2);
        Window.MouseDown(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        for (var i = 1; i <= 10; i++)
        {
            Window.MouseMove(new Point(start.X + ((dx * i) / 10), start.Y));
            Dispatcher.UIThread.RunJobs();
        }

        Window.MouseUp(start, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
        Layout();
    }

    public Button TabCloseButton(string tabTitle)
    {
        var tab = ViewModel.Tabs.Single(t => t.Title == tabTitle);
        ViewModel.SelectedTab = tab;
        Layout();
        return Window.GetVisualDescendants().OfType<Button>()
            .Single(b => (b.Content as string)?.Contains("✕", StringComparison.Ordinal) == true);
    }

    /// <summary>真按键:通过窗口输入验证 AppShortcuts 与冒泡分发是否生效。</summary>
    public void Press(Key key, PhysicalKey physicalKey, RawInputModifiers modifiers = RawInputModifiers.None)
    {
        Window.KeyPress(key, modifiers, physicalKey, key.ToString());
        Dispatcher.UIThread.RunJobs();
    }

    public T PageView<T>() => Window.GetVisualDescendants().OfType<T>().Single();

    /// <summary>Bounds 是父容器坐标系,先按控件自身坐标平移再换算到窗口,否则点歪。</summary>
    private static Point PointOf(Visual target, double x, double y)
    {
        var window = target.GetVisualAncestors().OfType<Window>().Single();
        return target.TranslatePoint(new Point(x, y), window)
               ?? throw new InvalidOperationException($"{target.GetType().Name} 还不在窗口可视树里");
    }

    public void Dispose()
    {
        Window.Close();
        Provider.Dispose();
    }
}
