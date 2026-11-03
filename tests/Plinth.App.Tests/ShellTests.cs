using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Logging;
using Avalonia.Headless;
using AvaloniaLog = Avalonia.Logging.Logger;
using AvaloniaLogLevel = Avalonia.Logging.LogEventLevel;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plinth.App;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Plinth.App.Tests;

public class ShellTests
{
    [AvaloniaFact]
    public void Sidebar_column_owns_the_width_so_the_sidebar_fills_it()
    {
        using var shell = new Shell();

        Assert.Equal(260, shell.SidebarColumn.Width.Value);
        Assert.Equal(260, shell.Sidebar.Bounds.Width);
        Assert.True(
            shell.Workspace.Bounds.X <= shell.Sidebar.Bounds.Right + shell.Splitter.Bounds.Width + 1,
            $"工作区起点 {shell.Workspace.Bounds.X} 与侧栏右缘 {shell.Sidebar.Bounds.Right} 之间不该有空白");
    }

    [AvaloniaFact]
    public void Dragging_the_splitter_right_widens_the_sidebar()
    {
        using var shell = new Shell();

        shell.DragSplitterBy(200);

        Assert.True(shell.Sidebar.Bounds.Width > 400, $"侧栏只到 {shell.Sidebar.Bounds.Width}");
        Assert.Equal(shell.SidebarColumn.Width.Value, shell.Sidebar.Bounds.Width, precision: 1);
    }

    [AvaloniaFact]
    public void Dragging_past_the_minimum_clamps_and_keeps_the_workspace_clear()
    {
        using var shell = new Shell();

        shell.DragSplitterBy(-1000);

        Assert.Equal(shell.SidebarColumn.MinWidth, shell.SidebarColumn.Width.Value, precision: 1);
        Assert.True(
            shell.Workspace.Bounds.X >= shell.Sidebar.Bounds.Right - 1,
            $"侧栏右缘 {shell.Sidebar.Bounds.Right} 压到了工作区起点 {shell.Workspace.Bounds.X}");
    }

    [AvaloniaFact]
    public void Hiding_the_sidebar_zeroes_its_column_and_showing_restores_the_dragged_width()
    {
        using var shell = new Shell();
        shell.DragSplitterBy(-80);
        var dragged = shell.SidebarColumn.Width.Value;

        shell.ViewModel.IsSidebarVisible = false;
        Dispatcher.UIThread.RunJobs();
        shell.Layout();
        Assert.Equal(0, shell.SidebarColumn.Width.Value);
        Assert.True(shell.Workspace.Bounds.X < 1, "侧栏藏了却还占着列宽");

        shell.ViewModel.IsSidebarVisible = true;
        Dispatcher.UIThread.RunJobs();
        shell.Layout();
        Assert.Equal(dragged, shell.SidebarColumn.Width.Value, precision: 1);
    }

    [AvaloniaFact]
    public void Compact_toggle_uses_48px_rail_and_restores_previous_width()
    {
        using var shell = new Shell();
        Assert.Equal(260, shell.SidebarColumn.Width.Value);
        Assert.True(shell.Splitter.IsVisible);
        Assert.True(shell.NavigationTitle.IsVisible);

        shell.ViewModel.ToggleSidebarCompactCommand.Execute(null);
        shell.Layout();

        Assert.True(shell.ViewModel.IsSidebarCompact);
        Assert.Equal(48, shell.SidebarColumn.Width.Value);
        Assert.False(shell.ViewModel.IsSidebarSplitterVisible);
        Assert.False(shell.Splitter.IsVisible);
        Assert.False(shell.NavigationTitle.IsVisible);
        Assert.DoesNotContain(
            shell.NavList.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == "概览" && text.IsVisible);

        // 图标轨道要保留图标、无障碍名称和悬停提示,否则折叠后只剩空按钮。
        var first = (NavigationItem)shell.NavList.Items[0]!;
        Assert.Contains(
            shell.NavList.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text == first.Icon && text.IsVisible);
        var row = shell.NavList.GetVisualDescendants().OfType<ListBoxItem>().First();
        Assert.Contains(RowControls(row), control => AutomationProperties.GetName(control) == first.Title);
        Assert.Contains(RowControls(row), control => ToolTip.GetTip(control) as string == first.Title);

        shell.ViewModel.ToggleSidebarCompactCommand.Execute(null);
        shell.Layout();

        Assert.False(shell.ViewModel.IsSidebarCompact);
        Assert.Equal(260, shell.SidebarColumn.Width.Value);
        Assert.True(shell.Splitter.IsVisible);
        Assert.True(shell.NavigationTitle.IsVisible);
    }

    [AvaloniaFact]
    public void Compact_state_survives_reopening_workspace()
    {
        var directory = Path.Combine(Path.GetTempPath(), "plinth-compact-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(directory, "workspace.json");
        Directory.CreateDirectory(directory);
        try
        {
            // Shell 只清默认工作区文件,自定义路径要留给第二个实例读回。
            using (var first = new Shell(services => services.AddSingleton(new WorkspacePreferencesService(path))))
            {
                Assert.False(first.ViewModel.IsSidebarCompact);
                first.ViewModel.ToggleSidebarCompactCommand.Execute(null);
                first.Layout();
                Assert.True(first.ViewModel.IsSidebarCompact);
                Assert.Equal(48, first.SidebarColumn.Width.Value);
            }

            using (var second = new Shell(services => services.AddSingleton(new WorkspacePreferencesService(path))))
            {
                Assert.True(second.ViewModel.IsSidebarCompact);
                Assert.Equal(48, second.SidebarColumn.Width.Value);

                second.ViewModel.ToggleSidebarCompactCommand.Execute(null);
                second.Layout();
                Assert.False(second.ViewModel.IsSidebarCompact);
                Assert.Equal(260, second.SidebarColumn.Width.Value);
            }
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    [AvaloniaFact]
    public void About_page_is_registered_as_a_page_but_not_listed_in_the_menu()
    {
        using var shell = new Shell();

        Assert.Contains(shell.ViewModel.NavigationItems, item => item.Id == ShellNavigation.AboutId);
        Assert.DoesNotContain(shell.ViewModel.MenuItems, item => item.Id == ShellNavigation.AboutId);
        Assert.IsType<NavigationItem>(shell.NavList.Items[0]);
        Assert.Equal(shell.ViewModel.PrimaryMenuItems.Count, shell.NavList.Items.Count);
        Assert.DoesNotContain(shell.ViewModel.PrimaryMenuItems, item => item.Id == ShellNavigation.SettingsId);
        Assert.Contains(shell.ViewModel.FooterMenuItems, item => item.Id == ShellNavigation.SettingsId);
        Assert.Contains(shell.NavFooter.Items.OfType<NavigationItem>(), item => item.Id == ShellNavigation.SettingsId);
        Assert.True(shell.NavFooter.IsEffectivelyVisible);
        Assert.True(shell.NavFooter.Bounds.Height > 0);
    }

    [AvaloniaFact]
    public void Alt_number_opens_the_numbered_primary_page()
    {
        using var shell = new Shell();
        var target = shell.ViewModel.PrimaryMenuItems[1];

        shell.Press(Key.D2, PhysicalKey.Digit2, RawInputModifiers.Alt);

        Assert.Equal(target.Id, shell.ViewModel.SelectedTab?.Id);
    }

    [AvaloniaFact]
    public void Alt_zero_opens_settings_from_the_footer()
    {
        using var shell = new Shell();

        shell.Press(Key.D0, PhysicalKey.Digit0, RawInputModifiers.Alt);

        Assert.Equal(ShellNavigation.SettingsId, shell.ViewModel.SelectedTab?.Id);
    }

    [AvaloniaFact]
    public void Ctrl_k_opens_quick_open_and_enter_navigates()
    {
        using var shell = new Shell();

        shell.Press(Key.K, PhysicalKey.K, RawInputModifiers.Control);
        Assert.True(shell.ViewModel.IsQuickOpen);
        Assert.True(shell.Window.FindControl<Border>("QuickOpenPanel")!.IsVisible);
        Assert.NotEmpty(shell.ViewModel.QuickOpenItems);

        shell.ViewModel.QuickOpenText = "设置";
        Assert.Single(shell.ViewModel.QuickOpenItems);
        Assert.Equal(ShellNavigation.SettingsId, shell.ViewModel.QuickOpenItems[0].Id);

        shell.ViewModel.AcceptQuickOpenCommand.Execute(shell.ViewModel.QuickOpenSelection);

        Assert.False(shell.ViewModel.IsQuickOpen);
        Assert.Equal(ShellNavigation.SettingsId, shell.ViewModel.SelectedTab?.Id);
    }

    [AvaloniaFact]
    public void Ctrl_k_closes_an_open_quick_open()
    {
        using var shell = new Shell();
        shell.Press(Key.K, PhysicalKey.K, RawInputModifiers.Control);
        Assert.True(shell.ViewModel.IsQuickOpen);

        shell.Press(Key.K, PhysicalKey.K, RawInputModifiers.Control);

        Assert.False(shell.ViewModel.IsQuickOpen);
    }

    [AvaloniaFact]
    public void F1_opens_the_help_page_once_even_when_pressed_twice()
    {
        using var shell = new Shell();

        shell.Press(Key.F1, PhysicalKey.F1);
        shell.Press(Key.F1, PhysicalKey.F1);
        Dispatcher.UIThread.RunJobs();
        shell.Layout();

        Assert.Single(shell.ViewModel.Tabs, tab => tab.Id == ShellNavigation.HelpId);
        Assert.IsType<HelpView>(shell.PageView<HelpView>());
    }

    [AvaloniaFact]
    public void Ctrl_w_closes_the_selected_tab()
    {
        using var shell = new Shell();
        Assert.NotEmpty(shell.ViewModel.Tabs);

        shell.Press(Key.W, PhysicalKey.W, RawInputModifiers.Control);
        Dispatcher.UIThread.RunJobs();

        Assert.Empty(shell.ViewModel.Tabs);
        Assert.Contains("已关闭", shell.ViewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void F5_refreshes_a_page_that_implements_IRefreshable()
    {
        using var shell = new Shell();

        shell.Press(Key.F5, PhysicalKey.F5);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("已刷新", shell.ViewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void F5_on_a_page_without_refresh_says_so_instead_of_going_quiet()
    {
        using var shell = new Shell();
        shell.ViewModel.ShowAboutCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        shell.Press(Key.F5, PhysicalKey.F5);
        Dispatcher.UIThread.RunJobs();

        Assert.Contains("没有可刷新", shell.ViewModel.StatusMessage);
    }

    [AvaloniaFact]
    public void Clicking_the_cross_on_a_tab_header_closes_that_tab()
    {
        using var shell = new Shell();
        var closeButton = shell.TabCloseButton("概览");

        shell.ClickCenter(closeButton);

        Assert.DoesNotContain(shell.ViewModel.Tabs, tab => tab.Title == "概览");
    }

    [AvaloniaFact]
    public void Clicking_a_nav_item_reopens_a_page_that_was_closed()
    {
        using var shell = new Shell();
        shell.ViewModel.CloseAllTabsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(shell.ViewModel.Tabs);

        shell.ClickAt(shell.NavList, shell.NavList.Bounds.Width / 2, 20);

        Assert.Single(shell.ViewModel.Tabs);
    }

    [AvaloniaFact]
    public void Closing_every_tab_shows_the_empty_state()
    {
        using var shell = new Shell();

        shell.ViewModel.CloseAllTabsCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.False(shell.ViewModel.HasTabs);
        Assert.Contains(
            shell.Window.GetVisualDescendants().OfType<TextBlock>(),
            text => text.Text != null && text.Text.Contains("从左侧导航打开"));
    }

    [AvaloniaFact]
    public void Page_view_models_resolve_to_their_views()
    {
        using var shell = new Shell();

        Assert.IsType<OverviewView>(shell.PageView<OverviewView>());
    }

    [AvaloniaFact]
    public void Overview_action_hint_is_rendered_only_when_it_has_text()
    {
        using var shell = new Shell();
        var page = (OverviewViewModel)shell.ViewModel.Tabs.Single(tab => tab.Title == "概览").Content;
        var view = shell.PageView<OverviewView>();

        Assert.Equal(string.Empty, page.ActionHint);

        page.ActionHint = "无法打开数据目录,请检查日志";
        Dispatcher.UIThread.RunJobs();
        var hint = view.GetVisualDescendants().OfType<TextBlock>()
            .Single(text => text.Text == page.ActionHint);
        Assert.True(hint.IsVisible);

        page.ActionHint = string.Empty;
        Dispatcher.UIThread.RunJobs();
        Assert.False(hint.IsVisible);
    }

    [AvaloniaFact]
    public void FolderService_reports_failure_instead_of_throwing()
    {
        var collect = new CollectSink();
        var provider = new ServiceCollection()
            .AddLogging(builder => builder.ClearProviders().AddSerilog(collect.Logger))
            .BuildServiceProvider();
        var service = ActivatorUtilities.CreateInstance<FolderService>(provider);

        Assert.False(service.TryOpen(@"Z:\definitely\not\here"));
        Assert.Contains(collect.Lines, line => line.Contains("目录不存在"));
    }

    [AvaloniaFact]
    public void Version_is_read_from_the_framework_assembly_not_the_entry_assembly()
    {
        var expected = typeof(AppInfo).Assembly.GetName().Version?.ToString(3);

        Assert.Equal(expected, AppInfo.Version);
        Assert.Equal($"v{expected}", new Shell().ViewModel.VersionText);
    }

    [AvaloniaFact]
    public void Avalonia_log_bridge_writes_into_whichever_serilog_logger_is_current()
    {
        var collect = new CollectSink();
        var previousSerilog = Serilog.Log.Logger;
        var previousSink = AvaloniaLog.Sink;
        var sinkType = typeof(App).Assembly.GetType("Plinth.App.Diagnostics.AvaloniaSerilogSink")
                       ?? throw new InvalidOperationException("AvaloniaSerilogSink 被改名了");

        try
        {
            // 先装 sink,再换 logger:桥接必须是每次写时现取,不能快照
            AvaloniaLog.Sink = (ILogSink)Activator.CreateInstance(sinkType)!;
            Serilog.Log.Logger = collect.Logger;

            if (AvaloniaLog.TryGet(AvaloniaLogLevel.Warning, LogArea.Binding, out var avaloniaLogger))
            {
                avaloniaLogger.Log(new object(), "桥接探针 {Probe}", "OK");
            }

            Assert.Contains(collect.Lines, line => line.Contains("桥接探针"));
        }
        finally
        {
            Serilog.Log.Logger = previousSerilog;
            AvaloniaLog.Sink = previousSink;
        }
    }

    /// <summary>导航行的名称/提示可能挂在 ListBoxItem 或模板根上,按行内任意控件判断。</summary>
    private static IEnumerable<Control> RowControls(Control row) =>
        new[] { row }.Concat(row.GetVisualDescendants().OfType<Control>());

    private sealed class CollectSink : ILogEventSink
    {
        public Serilog.Core.Logger Logger { get; }

        public CollectSink()
        {
            Logger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(this).CreateLogger();
        }

        public System.Collections.Generic.List<string> Lines { get; } = [];

        public void Emit(LogEvent logEvent) => Lines.Add(logEvent.RenderMessage());
    }
}
