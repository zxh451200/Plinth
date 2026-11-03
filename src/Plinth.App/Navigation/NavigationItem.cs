using System;
using Plinth.App.ViewModels;

namespace Plinth.App.Navigation;

/// <summary>导航分组,只影响侧栏展示与排序,不代表注册语义。</summary>
public enum NavigationSection
{
    Workbench,
    Business,
    Runtime,
    System,
    Footer,
}

/// <summary>
/// Shell 导航契约(§4.6.3):Id 是稳定标识,Title 是显示名,工厂负责创建页面 ViewModel。
/// 页面 VM 由注册工厂创建,业务 ViewModel 不注入容器。
/// "注册页面"和"占不占侧栏一行"是两件事:WinUI 的 NavigationView 把设置项单列在
/// Settings 上而不是塞进 Items 集合,macOS HIG 直接把 About/Settings 归到应用菜单;
/// 所以 ShowInMenu=false 的页面照常注册、照常能用菜单/命令/快捷键打开,只是不进导航列表。
/// Section/Order/GroupTitle/ShowGroupHeader 是显示元数据:注册顺序仍是组合根里的注册顺序,
/// 它们只决定侧栏分组、组标题和先后。GroupTitle 可由模块覆盖,留空时由 Shell 按 Section 填默认组名。
/// </summary>
public sealed record NavigationItem(
    string Id,
    string Title,
    Func<ViewModelBase> CreateViewModel,
    bool ShowInMenu = true)
{
    public NavigationSection Section { get; init; } = NavigationSection.Business;

    public int Order { get; init; } = 500;

    /// <summary>Segoe Fluent Icons/MDL2 字形;折叠成图标轨道时显示。</summary>
    public string Icon { get; init; } = "\uE8B7";

    /// <summary>启用远程身份时,至少具备其中任一权限才显示;离线模式不隐藏。</summary>
    public string[] RequiredAnyPermission { get; init; } = [];

    /// <summary>模块可覆盖组标题;为 null 时由 Shell 按 Section 填默认组名。</summary>
    public string? GroupTitle { get; init; }

    /// <summary>Shell 解析后的组标题(含默认组名),供侧栏模板显示。</summary>
    public string? DisplayGroupTitle { get; set; }

    /// <summary>是否在本项之前渲染组标题,由 Shell 计算。</summary>
    public bool ShowGroupHeader { get; set; }
}
