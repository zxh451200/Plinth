using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.Modules.OfflineSync.ViewModels;

namespace Plinth.App.Modules.OfflineSync;

/// <summary>Optional pilot registration. The host supplies the transport; no HTTP adapter is provided.</summary>
public static class OfflineSyncModuleRegistration
{
    public const string PageId = "offlineSync.pilot";

    public static IServiceCollection AddOfflineSyncModule(this IServiceCollection services,
        Func<string, IPilotTransport> transportFactory)
    {
        ArgumentNullException.ThrowIfNull(transportFactory);
        // Resolve per page after authentication has established the current subject.
        // A singleton would freeze the anonymous/startup subject into the database scope.
        services.AddTransient(provider =>
        {
            var users = provider.GetRequiredService<IRemoteUserContext>();
            var scope = CreateScope(users);
            var path = Path.Combine(AppPaths.DataDirectory, "offline-sync-pilot.db");
            return new OfflineSyncPilot(path, scope, transportFactory(scope));
        });
        services.AddTransient<OfflineSyncViewModel>();
        services.AddSingleton<NavigationItem>(provider => new(PageId, "离线同步试点",
            () => provider.GetRequiredService<OfflineSyncViewModel>())
        { Order = 230, Icon = "\uE895", RequiredAnyPermission = ["offlineSync:edit", "offlineSync:sync", "offlineSync:resolve"] });
        return services;
    }

    internal static string CreateScope(IRemoteUserContext users)
    {
        var auth = DeploymentConfiguration.Current.Authentication;
        var deployment = auth.Enabled ? $"{auth.Authority.TrimEnd('/')}\n{auth.ClientId}\n{auth.Scope}" : "offline";
        var subject = auth.Enabled ? users.CurrentUser.Subject : Environment.UserName;
        var material = deployment + "\n" + subject + "\n" + Path.GetFullPath(AppPaths.DataDirectory);
        return "app/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
