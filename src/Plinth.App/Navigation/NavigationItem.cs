using System;
using Plinth.App.ViewModels;

namespace Plinth.App.Navigation;

/// <summary>
/// Shell 导航契约(§4.6.3):Id 是稳定标识,Title 是显示名,工厂负责创建页面 ViewModel。
/// 页面 VM 由注册工厂创建,业务 ViewModel 不注入容器。
/// ShowInMenu=false 的页面照常注册、照常能用命令/快捷键打开,只是不占侧栏一行(关于页就是这种)。
/// </summary>
public sealed record NavigationItem(
    string Id,
    string Title,
    Func<ViewModelBase> CreateViewModel,
    bool ShowInMenu = true);
