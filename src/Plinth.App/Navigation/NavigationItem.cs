using System;
using Plinth.App.ViewModels;

namespace Plinth.App.Navigation;

/// <summary>
/// Shell 导航契约(§4.6.3):Id 是稳定标识,Title 是显示名,工厂负责创建页面 ViewModel。
/// 页面 VM 由注册工厂创建,业务 ViewModel 不注入容器。
/// </summary>
public sealed record NavigationItem(string Id, string Title, Func<ViewModelBase> CreateViewModel);
