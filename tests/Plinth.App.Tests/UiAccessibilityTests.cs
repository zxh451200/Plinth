using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 真实 Shell 的语义、键盘和布局回归；不代表 Windows UIA、IME 或像素验收。
/// 六个页面状态 + 一个长标题状态 + 三个语言/尺寸状态，共十个用例。
/// </summary>
[Collection("Localization")]
public sealed class UiAccessibilityTests
{
    [AvaloniaTheory]
    [InlineData(ShellNavigation.OverviewId)]
    [InlineData(ShellNavigation.SettingsId)]
    [InlineData(ShellNavigation.LogsId)]
    [InlineData(ShellNavigation.BackupId)]
    [InlineData(ShellNavigation.AboutId)]
    [InlineData(DemoModuleRegistration.PageId)]
    public async Task Main_page_controls_have_names_and_are_reachable_in_both_tab_directions(string pageId)
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var page = await OpenPageAsync(shell, pageId);
        var targets = InputControls(page);
        Assert.NotEmpty(targets);

        // 汇总错误，避免第一个名称缺口阻止本状态执行真实键盘遍历。
        var errors = new List<string>();
        foreach (var target in targets)
        {
            CheckName(target, errors);
        }

        foreach (var table in page.GetVisualDescendants().OfType<TableView>())
        {
            CheckName(table, errors);
        }

        CheckTabTraversal(shell, targets, RawInputModifiers.None, errors);
        CheckTabTraversal(shell, targets, RawInputModifiers.Shift, errors);
        Assert.True(errors.Count == 0, $"{pageId}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
    }

    [AvaloniaFact]
    public async Task Long_mixed_language_tab_title_keeps_its_close_action_named_visible_and_keyboard_operable()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        await OpenPageAsync(shell, ShellNavigation.OverviewId);
        var tab = shell.ViewModel.SelectedTab!;
        const string title = "很长的工作区标题 / A very long workspace title — 数据检查与辅助功能验收 Accessibility acceptance";
        tab.Title = title;
        await OpenPageAsync(shell, ShellNavigation.AboutId);
        shell.ViewModel.SelectedTab = tab;
        Resize(shell, 960, 560);

        Assert.Contains(shell.TabHost.GetVisualDescendants().OfType<TextBlock>(), text => text.Text == title);
        var close = shell.TabHost.GetVisualDescendants().OfType<Button>()
            .Single(button => ReferenceEquals(button.CommandParameter, tab));
        var errors = new List<string>();
        CheckName(close, errors);
        var name = ControlAutomationPeer.CreatePeerForElement(close)?.GetName() ?? string.Empty;
        if (!name.Contains(title, StringComparison.Ordinal))
        {
            errors.Add($"标签关闭操作的 Automation 名称应包含完整标题，实际为“{name}”。");
        }

        CheckInside(close, shell.TabHost, errors);
        CheckInside(close, shell.Window, errors);
        CheckTabTraversal(shell, [close], RawInputModifiers.None, errors);

        // 仅锚点调用 Focus；按钮必须由 Tab 到达，再由 Enter 激活。
        Assert.True(shell.NavList.Focus(), "无法设置键盘遍历起点。");
        var reached = false;
        var visited = new List<string>();
        for (var i = 0; i < 256; i++)
        {
            shell.Press(Key.Tab, PhysicalKey.Tab);
            if (shell.Window.FocusManager?.GetFocusedElement() is Visual focused && OwnsFocus(close, focused))
            {
                reached = true;
                shell.Press(Key.Enter, PhysicalKey.Enter);
                break;
            }
            if (shell.Window.FocusManager?.GetFocusedElement() is Control focusedControl
                && visited.Count < 40 && (visited.Count == 0 || visited[^1] != focusedControl.GetType().Name))
                visited.Add(focusedControl.GetType().Name + ":" + focusedControl.Name);
        }

        Assert.True(reached, "长标题标签的关闭按钮未能由 Tab 到达。焦点序列: " + string.Join(" > ", visited));

        Assert.DoesNotContain(tab, shell.ViewModel.Tabs);
        Assert.Contains(shell.ViewModel.Tabs, item => item.Id == ShellNavigation.AboutId);
        Assert.True(errors.Count == 0, string.Join(Environment.NewLine, errors));
    }

    [AvaloniaTheory]
    [InlineData("zh-CN", 960, 560)]
    [InlineData("en-US", 1200, 720)]
    [InlineData("en-US", 1600, 1000)]
    public async Task Localized_pages_keep_primary_actions_inside_the_workspace(string language, int width, int height)
    {
        try
        {
            // TExtension 在视图创建时取词，必须先设语言再创建 Shell。
            Loc.Initialize(language);
            using var shell = new Shell();
            var errors = new List<string>();
            foreach (var pageId in new[]
                     {
                         ShellNavigation.OverviewId, ShellNavigation.SettingsId, ShellNavigation.LogsId,
                         ShellNavigation.BackupId, ShellNavigation.AboutId, DemoModuleRegistration.PageId,
                     })
            {
                var page = await OpenPageAsync(shell, pageId);
                Resize(shell, width, height);
                Assert.Equal((double)width, shell.Window.Bounds.Width, precision: 1);
                Assert.Equal((double)height, shell.Window.Bounds.Height, precision: 1);
                CheckInside(shell.Workspace, shell.Window, errors);
                CheckInside(page, shell.Workspace, errors);
                if (shell.Workspace.Bounds.X < shell.Sidebar.Bounds.Right + shell.Splitter.Bounds.Width - 1)
                {
                    errors.Add($"{pageId}: 侧栏与工作区重叠。");
                }

                // 不仅检查资源 key：断言真实视图已经显示对应语言的主操作。
                var actionKey = pageId switch
                {
                    ShellNavigation.OverviewId => "overview.openData",
                    ShellNavigation.SettingsId => "settings.openDataPath",
                    ShellNavigation.LogsId => "logs.reload",
                    ShellNavigation.BackupId => "backup.create",
                    ShellNavigation.AboutId => "about.update.check",
                    _ => "demo.save",
                };
                // 行内编辑器与标准表单可能各有一个同文案主按钮，取当前可见的那个。
                var action = page.GetVisualDescendants().OfType<Button>()
                    .Where(button => button.Content as string == Loc.Get(actionKey))
                    .FirstOrDefault(IsShown);
                Assert.True(action is not null, $"{pageId}: {language} 主操作不可见。");
                CheckName(action, errors);
                CheckTabTraversal(shell, [action], RawInputModifiers.None, errors);
                // 遍历会触发 ScrollViewer 的焦点滚动，再检查目标的实际位置。
                CheckInside(action, shell.Workspace, errors);
                foreach (var text in page.GetVisualDescendants().OfType<TextBlock>().Where(IsShown))
                {
                    if (text.Text is { Length: > 2 } value && value.StartsWith("[", StringComparison.Ordinal)
                        && value.EndsWith("]", StringComparison.Ordinal))
                    {
                        errors.Add($"{pageId}: 视图中出现疑似未解析资源 {value}。");
                    }
                }
            }

            Assert.True(errors.Count == 0, $"{language} {width}×{height}:{Environment.NewLine}{string.Join(Environment.NewLine, errors)}");
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage);
        }
    }

    private static async Task<Control> OpenPageAsync(Shell shell, string pageId)
    {
        var item = shell.ViewModel.NavigationItems.Single(candidate => candidate.Id == pageId);
        shell.ViewModel.OpenTabCommand.Execute(item);
        if (shell.ViewModel.SelectedTab!.Content is DemoListViewModel demo)
        {
            await demo.LoadAsync();
            await demo.NewCommand.ExecuteAsync(null);
        }

        if (shell.ViewModel.SelectedTab.Content is LogViewModel logs)
        {
            await logs.LoadAsync();
        }

        shell.Layout();
        return pageId switch
        {
            ShellNavigation.OverviewId => shell.PageView<OverviewView>(),
            ShellNavigation.SettingsId => shell.PageView<SettingsView>(),
            ShellNavigation.LogsId => shell.PageView<LogView>(),
            ShellNavigation.BackupId => shell.PageView<BackupView>(),
            ShellNavigation.AboutId => shell.PageView<AboutView>(),
            DemoModuleRegistration.PageId => shell.PageView<DemoListView>(),
            _ => throw new ArgumentOutOfRangeException(nameof(pageId), pageId, "未定义页面状态"),
        };
    }

    private static void Resize(Shell shell, double width, double height)
    {
        // Shell.Layout() 固定 1200×720，这里独立布局，后续遍历不可再调用它。
        shell.Window.Width = width;
        shell.Window.Height = height;
        Dispatcher.UIThread.RunJobs();
        shell.Window.Measure(new Size(width, height));
        shell.Window.Arrange(new Rect(0, 0, width, height));
        Dispatcher.UIThread.RunJobs();
    }

    private static bool IsShown(Visual visual) =>
        visual.IsVisible && visual.GetVisualAncestors().All(ancestor => ancestor.IsVisible);

    private static bool IsInput(Control control) =>
        control is Button or TextBox or ComboBox or NumericUpDown;

    private static Control[] InputControls(Control page) =>
        page.GetVisualDescendants().OfType<Control>()
            .Where(control => IsInput(control) && control.TemplatedParent is null
                && IsShown(control) && control.IsEffectivelyEnabled)
            // 检查页面级控件，模板内的 TextBox/按钮归属外层复合控件。
            .Where(control => !control.GetVisualAncestors().TakeWhile(ancestor => !ReferenceEquals(ancestor, page))
                .OfType<Control>().Any(IsInput))
            .ToArray();

    private static void CheckName(Control control, List<string> errors)
    {
        var name = ControlAutomationPeer.CreatePeerForElement(control)?.GetName();
        if (string.IsNullOrWhiteSpace(name) || name.Trim() is "✕" or "×"
            || (name.StartsWith("[", StringComparison.Ordinal) && name.EndsWith("]", StringComparison.Ordinal)))
        {
            errors.Add($"{control.GetType().Name} ({control.Name ?? "无 x:Name"}) 缺少有意义的 Automation 名称，实际为“{name}”。");
        }
    }

    private static bool OwnsFocus(Control target, Visual focused) =>
        ReferenceEquals(target, focused) || focused.GetVisualAncestors().Any(ancestor => ReferenceEquals(ancestor, target));

    private static void CheckTabTraversal(Shell shell, Control[] targets, RawInputModifiers modifiers, List<string> errors)
    {
        Assert.True(shell.NavList.Focus(),
            $"无法设置导航锚点；不能证明 Tab 可达性。focusable={shell.NavList.Focusable} "
            + $"enabled={shell.NavList.IsEffectivelyEnabled} visible={shell.NavList.IsVisible} "
            + $"active={shell.Window.IsActive} focused={shell.Window.FocusManager?.GetFocusedElement()?.GetType().Name}");
        var reached = new HashSet<Control>();
        // 设上限以显式失败，不把焦点循环或焦点陷阱当作成功。
        for (var i = 0; i < 256 && reached.Count < targets.Length; i++)
        {
            shell.Press(Key.Tab, PhysicalKey.Tab, modifiers);
            if (shell.Window.FocusManager?.GetFocusedElement() is not Visual focused)
            {
                continue;
            }

            foreach (var target in targets.Where(target => OwnsFocus(target, focused)))
            {
                if (reached.Add(target))
                {
                    CheckInside(target, shell.Window, errors);
                }
            }
        }

        foreach (var missing in targets.Where(target => !reached.Contains(target)))
        {
            errors.Add($"{modifiers}+Tab 未到达 {missing.GetType().Name}，名称“{ControlAutomationPeer.CreatePeerForElement(missing)?.GetName()}”。");
        }
    }

    private static void CheckInside(Control child, Control container, List<string> errors)
    {
        var origin = child.TranslatePoint(default, container);
        if (origin is not { } point || !double.IsFinite(point.X) || !double.IsFinite(point.Y)
            || !double.IsFinite(child.Bounds.Width) || !double.IsFinite(child.Bounds.Height)
            || child.Bounds.Width <= 0 || child.Bounds.Height <= 0
            || point.X < -1 || point.Y < -1
            || point.X + child.Bounds.Width > container.Bounds.Width + 1
            || point.Y + child.Bounds.Height > container.Bounds.Height + 1)
        {
            errors.Add($"{child.GetType().Name} 超出 {container.GetType().Name} 或布局无效：原点 {origin}，尺寸 {child.Bounds.Size}，容器 {container.Bounds.Size}。");
        }
    }
}
