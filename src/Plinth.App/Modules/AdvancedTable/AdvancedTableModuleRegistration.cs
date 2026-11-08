using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Modules.AdvancedTable.Models;
using Plinth.App.Modules.AdvancedTable.Services;
using Plinth.App.Modules.AdvancedTable.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Localization;
using Plinth.App.Services;

namespace Plinth.App.Modules.AdvancedTable;

/// <summary>Optional pilot registration; the host can call this from its composition root.</summary>
public static class AdvancedTableModuleRegistration
{
    public const string PageId = "advancedTable.pilot";

    public static IServiceCollection AddAdvancedTableModule(this IServiceCollection services)
    {
        services.AddSingleton<IAdvancedTableRepository, AdvancedTableRepository>();
        return AddPage(services);
    }

    /// <summary>Explicit host-selected snapshot; import must succeed before registration.</summary>
    public static IServiceCollection AddAdvancedTableModule(
        this IServiceCollection services, IAdvancedTableRepository repository,
        string? readPermission = null, string? exportPermission = null, string? importPermission = null,
        string? editPermission = null)
    {
        ArgumentNullException.ThrowIfNull(repository);
        services.AddSingleton<IAdvancedTableRepository>(repository);
        return AddPage(services, readPermission, exportPermission, importPermission, editPermission);
    }

    private static IServiceCollection AddPage(IServiceCollection services,
        string? readPermission = null, string? exportPermission = null, string? importPermission = null,
        string? editPermission = null)
    {
        AdvancedTableViewModel Create(IServiceProvider provider) => new(
            provider.GetRequiredService<IAdvancedTableRepository>(),
            provider.GetService<SensitiveOperationService>(), importPermission, readPermission, exportPermission, editPermission);
        services.AddTransient<AdvancedTableViewModel>(Create);
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            PageId, Loc.Get("nav.advancedTable"),
            () => Create(provider))
        {
            Section = NavigationSection.Business,
            Order = 240,
            Icon = "\uE80A",
            TitleKey = "nav.advancedTable",
        });
        return services;
    }
}
