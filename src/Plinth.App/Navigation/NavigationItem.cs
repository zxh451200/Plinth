using System;
using Plinth.App.ViewModels;

namespace Plinth.App.Navigation;

/// <summary>
/// Shell 导航契约(§4.6.3):Id 是稳定标识,Title 是显示名,工厂负责创建页面 ViewModel。
/// 页面 VM 由注册工厂创建,业务 ViewModel 不注入容器。
/// "注册页面"和"占不占侧栏一行"是两件事:WinUI 的 NavigationView 把设置项单列在
/// Settings 上而不是塞进 Items 集合,macOS HIG 直接把 About/Settings 归到应用菜单;
/// 所以 ShowInMenu=false 的页面照常注册、照常能用菜单/命令/快捷键打开,只是不进导航列表。
/// </summary>
public sealed record NavigationItem(
    string Id,
    string Title,
    Func<ViewModelBase> CreateViewModel,
    bool ShowInMenu = true);
