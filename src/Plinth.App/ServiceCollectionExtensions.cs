using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Data;
using Plinth.App.Modules.AdvancedTable;
using Plinth.App.Modules.Demo;
using Plinth.App.Modules.Notes;
using Plinth.App.Modules.RemoteDemo;
using Plinth.App.Modules.RuntimePlugins;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Localization;
using Plinth.App.Auth;
using Plinth.App.Security;
using System;
using System.Linq;

namespace Plinth.App;

/// <summary>
/// 组合根注册(§4.2)。业务模块按同样形状加自己的 AddXxxModule 扩展方法,
/// 并在 <see cref="AddPlinthModules"/> 里加一行调用。
/// </summary>
public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPlinthModules(this IServiceCollection services)
    {
        // Configuration is core, even when RemoteDemo is explicitly enabled.
        var deployment = DeploymentConfiguration.Current;
        deployment.Validate();
        var optionalRemote = new OptionalModuleState("remoteDemo", "RemoteDemo", RemoteDemoModuleRegistration.PageId);
        services.AddSingleton(optionalRemote);
        services.AddSingleton<FolderService>();
        services.AddSingleton<LogAccessService>();
        services.AddSingleton<BrandingService>();
        services.AddSingleton<WorkspacePreferencesService>();
        services.AddSingleton<UnsavedChangesService>();
        services.AddSingleton<TaskService>();
        services.AddSingleton<DiagnosticService>();
        services.AddTransient<ShellStatusViewModel>();
        services.AddOfflineHelp();
        services.AddSingleton<LicenseNoticeService>();
        services.AddSingleton<RemoteAuthOptions>(_ => DeploymentConfiguration.Current.Authentication);
        services.AddSingleton<ICredentialStore, OsCredentialStore>();
        services.AddSingleton<ISecurityAudit, SecurityAudit>();
        services.AddSingleton<ISystemBrowserLauncher, SystemBrowserLauncher>();
        services.AddSingleton(provider => new SystemBrowser(
            provider.GetRequiredService<ISystemBrowserLauncher>(),
            provider.GetRequiredService<RemoteAuthOptions>().BrowserTimeout));
        services.AddSingleton<IIdentityClient, OidcIdentityClient>();
        services.AddSingleton<RemoteAuthService>();
        services.AddSingleton<IIdentitySessionEvents>(provider =>
            DeploymentConfiguration.Current.Authentication.Enabled
                ? provider.GetRequiredService<RemoteAuthService>()
                : NullIdentitySessionEvents.Instance);
        services.AddSingleton<IRemoteUserContext>(provider =>
            DeploymentConfiguration.Current.Authentication.Enabled
                ? provider.GetRequiredService<RemoteAuthService>() : new OfflineUserContext());
        services.AddSingleton<IAccessTokenProvider>(provider => provider.GetRequiredService<RemoteAuthService>());
        services.AddSingleton<IPermissionService, PermissionService>();
        services.AddSingleton<AuthorizedOperation>();
        services.AddSingleton<SensitiveOperationService>();

        // 数据库:SQLite 文件放在用户数据目录。每次操作从工厂拿一个新的 DbContext,用完释放
        services.AddDbContextFactory<AppDbContext>(options => DatabaseConfiguration.Current.Configure(options));

        services.AddTransient<MainViewModel>();
        services.AddTransient<OverviewViewModel>();
        services.AddTransient<AboutViewModel>();
        services.AddSingleton<BackupService>();
        services.AddTransient<BackupViewModel>();
        services.AddSingleton<SettingsService>();
        services.AddSingleton<UpdateCheckService>();
        services.AddSingleton(provider => new RemoteConnectionService(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<Microsoft.Extensions.Logging.ILogger<RemoteConnectionService>>(),
            tokens: DeploymentConfiguration.Current.Authentication.Enabled
                ? provider.GetRequiredService<IAccessTokenProvider>() : null,
            audit: provider.GetRequiredService<ISecurityAudit>(),
            users: provider.GetRequiredService<IRemoteUserContext>()));
        static SettingsViewModel CreateSettingsPage(System.IServiceProvider provider) => new(
            provider.GetRequiredService<SettingsService>(),
            provider.GetRequiredService<FolderService>(),
            provider.GetRequiredService<RemoteConnectionService>(),
            DeploymentConfiguration.Current.Authentication.Enabled
                ? provider.GetRequiredService<RemoteAuthService>() : null);
        services.AddTransient<SettingsViewModel>(CreateSettingsPage);

        // 导航项就是页面注册入口:加一个页面 = 加一条 NavigationItem(§4.6.3 第 6 步)
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.OverviewId,
            Loc.Get("nav.overview"),
            () => provider.GetRequiredService<OverviewViewModel>())
        { Section = NavigationSection.Workbench, Order = 100, Icon = "\uE80F", TitleKey = "nav.overview" });

        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            "shell.taskDiagnostics",
            Loc.Get("nav.taskDiagnostics"),
            () => ActivatorUtilities.CreateInstance<TaskDiagnosticsViewModel>(provider))
        { Section = NavigationSection.Runtime, Order = 400, Icon = "\uE7C4", TitleKey = "nav.taskDiagnostics" });

        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.SettingsId,
            Loc.Get("nav.settings"),
            () => CreateSettingsPage(provider))
        { Section = NavigationSection.Footer, Order = 900, Icon = "\uE713", TitleKey = "nav.settings" });

        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.LogsId,
            Loc.Get("nav.logs"),
            () => ActivatorUtilities.CreateInstance<LogViewModel>(provider))
        {
            Section = NavigationSection.System,
            Order = 600,
            Icon = "\uE7BA",
            TitleKey = "nav.logs",
            RequiredAnyPermission = ["logs:read", "audit:read"]
        });

        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.BackupId,
            Loc.Get("nav.backup"),
            () => provider.GetRequiredService<BackupViewModel>())
        {
            Section = NavigationSection.System,
            Order = 610,
            Icon = "\uE81C",
            TitleKey = "nav.backup",
            RequiredAnyPermission = ["backup:export", "backup:restore"]
        });

        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.HelpId, Loc.Get("help.title"),
            () => provider.GetRequiredService<HelpViewModel>(), ShowInMenu: false)
        { Icon = "\uE897", TitleKey = "help.title" });
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.LicensesId, Loc.Get("license.title"),
            // Shell 释放页面；避免根容器跟踪每次打开的 IDisposable 瞬态实例。
            () => ActivatorUtilities.CreateInstance<LicenseViewModel>(provider), ShowInMenu: false)
        { Icon = "\uE8A5", TitleKey = "license.title" });

        // 关于页照常注册,但不占侧栏一行:入口在「帮助」菜单
        services.AddSingleton<NavigationItem>(provider => new NavigationItem(
            ShellNavigation.AboutId,
            Loc.Get("nav.about"),
            () => ActivatorUtilities.CreateInstance<AboutViewModel>(provider),
            ShowInMenu: false)
        { Icon = "\uE946", TitleKey = "nav.about" });

        // 示例模块(不含业务)默认关闭;部署需要时显式打开 PLINTH_IncludeSampleModules
        if (ShouldRegisterSampleModules(deployment))
        {
            services.AddDemoModule();
            services.AddNotesModule();
        }
        if (deployment.EnableAdvancedTablePilot)
            services.AddAdvancedTableModule();
        if (deployment.EnableRuntimePlugins)
            services.AddRuntimePluginsModule();
        // 业务模块:新增模块时在这里加一行 services.AddXxxModule();
        // OfflineSync remains unregistered until a production/ trusted transportFactory exists.
        if (deployment.DemoApiBaseUrl.Length > 0)
        {
            var baseUri = new Uri(deployment.DemoApiBaseUrl);
            RegisterOptionalRemoteDemo(services, optionalRemote, staged =>
            {
                staged.AddRemoteDemoModule(baseUri);
                staged.AddSingleton(new HelpTopic("remoteDemo.editing", Loc.Get("help.remoteDemo.title"),
                    Loc.Get("help.remoteDemo.body"), RemoteDemoModuleRegistration.PageId)
                { TitleKey = "help.remoteDemo.title", BodyKey = "help.remoteDemo.body" });
            });
        }

        return services;
    }

    internal static bool ShouldRegisterSampleModules(DeploymentConfiguration deployment) =>
        deployment.IncludeSampleModules;

    private sealed class NullIdentitySessionEvents : IIdentitySessionEvents
    {
        public static NullIdentitySessionEvents Instance { get; } = new();
        public event EventHandler? SessionChanged { add { } remove { } }
    }

    // Internal seam for registration fault injection. Only the explicit RemoteDemo call
    // above uses this boundary; Demo/Notes/core registration and migrations stay fail-fast.
    internal static void RegisterOptionalRemoteDemo(IServiceCollection services,
        OptionalModuleState state, Action<IServiceCollection> register)
    {
        IServiceCollection staged = new ServiceCollection();
        try
        {
            register(staged); // Includes Help. Only descriptors, never a temporary provider.
            foreach (var descriptor in staged.Where(d => d.ServiceType == typeof(NavigationItem)).ToArray())
            {
                var factory = descriptor.ImplementationFactory
                    ?? throw new InvalidOperationException("RemoteDemo navigation must use its explicit factory.");
                staged.Remove(descriptor);
                staged.AddSingleton<NavigationItem>(provider =>
                {
                    var item = (NavigationItem)factory(provider);
                    return item with
                    {
                        CreateViewModel = () => state.CreatePage(() => PrepareRemoteDemoCore(provider),
                            item.CreateViewModel),
                    };
                });
            }
        }
        catch (Exception ex) when (ex is not (OutOfMemoryException or OperationCanceledException))
        {
            state.Fail(registration: true);
            return; // Nothing from the staged collection reaches the host.
        }
        foreach (var descriptor in staged) services.Add(descriptor);
    }

    private static void PrepareRemoteDemoCore(IServiceProvider provider)
    {
        // Keep this list aligned with RemoteDemoConnection, RemoteDemoService and its VM.
        // Resolve from the final provider, on first navigation, without startup warming.
        provider.GetRequiredService<IAccessTokenProvider>();
        provider.GetRequiredService<ISecurityAudit>();
        provider.GetRequiredService<IRemoteUserContext>();
        provider.GetRequiredService<AuthorizedOperation>();
        provider.GetRequiredService<SensitiveOperationService>();
        provider.GetRequiredService<UnsavedChangesService>();
    }
}
