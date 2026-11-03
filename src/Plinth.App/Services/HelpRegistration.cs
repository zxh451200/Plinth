using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;

namespace Plinth.App.Services;

public static class HelpRegistration
{
    public static IServiceCollection AddOfflineHelp(this IServiceCollection services)
    {
        services.AddTransient<HelpViewModel>();
        services.AddSingleton<HelpTopic>(_ => new("start", Loc.Get("help.start.title"),
            Loc.Get("help.start.body"), ShellNavigation.OverviewId));
        services.AddSingleton<HelpTopic>(_ => new("settings", Loc.Get("help.settings.title"),
            Loc.Get("help.settings.body"), ShellNavigation.SettingsId));
        services.AddSingleton<HelpTopic>(_ => new("backup", Loc.Get("help.backup.title"),
            Loc.Get("help.backup.body"), ShellNavigation.BackupId));
        services.AddSingleton<HelpTopic>(_ => new("diagnostics", Loc.Get("help.diagnostics.title"),
            Loc.Get("help.diagnostics.body"), ShellNavigation.TaskDiagnosticsId, ["TASK_FAILED", "UI_UNEXPECTED"]));
        services.AddSingleton<HelpTopic>(_ => new("logs", Loc.Get("help.logs.title"),
            Loc.Get("help.logs.body"), ShellNavigation.LogsId));
        services.AddSingleton<HelpTopic>(_ => new("version", Loc.Get("help.version.title"),
            Loc.Get("help.version.body"), ShellNavigation.AboutId));
        return services;
    }
}
