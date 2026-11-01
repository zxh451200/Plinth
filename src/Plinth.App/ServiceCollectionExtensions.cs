using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;

namespace Plinth.App;

/// <summary>
/// 组合根注册(§4.2)。业务模块按同样形状加自己的 AddXxxModule 扩展方法,
/// 并在 <see cref="AddPlinthModules"/> 里加一行调用。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlinthModules(this IServiceCollection services)
    {
        services.AddSingleton<FolderService>();

        services.AddTransient<MainViewModel>();
        services.AddTransient<OverviewViewModel>();

        // 导航项就是页面注册入口:加一个页面 = 加一条 NavigationItem(§4.6.3 第 6 步)
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            "shell.overview",
            "概览",
            () => provider.GetRequiredService<OverviewViewModel>()));

        return services;
    }
}
