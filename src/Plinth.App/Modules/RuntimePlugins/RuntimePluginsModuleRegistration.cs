using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Modules.RuntimePlugins.ViewModels;
using Plinth.App.Navigation;
namespace Plinth.App.Modules.RuntimePlugins;

public static class RuntimePluginsModuleRegistration
{
    public const string PageId = "runtime.plugins";
    /// <summary>无参注册保持默认拒绝且不执行路径发现。</summary>
    public static IServiceCollection AddRuntimePluginsModule(this IServiceCollection services, RuntimePluginConfiguration? configuration = null)
    {
        var actual = configuration ?? RuntimePluginConfiguration.Disabled;
        services.AddSingleton(actual); services.AddSingleton(actual.CreateHost()); services.AddTransient<RuntimePluginsViewModel>();
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(PageId, RuntimePluginStrings.Get("title"), () => provider.GetRequiredService<RuntimePluginsViewModel>())
        {
            Order = 260,
            Section = NavigationSection.Runtime,
            Icon = "\uE756",
            RequiredAnyPermission = [RuntimePluginsViewModel.InvokePermission]
        });
        return services;
    }
}
