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
            Loc.Get("help.start.body"), ShellNavigation.OverviewId)
        { TitleKey = "help.start.title", BodyKey = "help.start.body" });
        services.AddSingleton<HelpTopic>(_ => new("settings", Loc.Get("help.settings.title"),
            Loc.Get("help.settings.body"), ShellNavigation.SettingsId)
        { TitleKey = "help.settings.title", BodyKey = "help.settings.body" });
        services.AddSingleton<HelpTopic>(_ => new("backup", Loc.Get("help.backup.title"),
            Loc.Get("help.backup.body"), ShellNavigation.BackupId)
        { TitleKey = "help.backup.title", BodyKey = "help.backup.body" });
        services.AddSingleton<HelpTopic>(_ => new("diagnostics", Loc.Get("help.diagnostics.title"),
            Loc.Get("help.diagnostics.body"), ShellNavigation.TaskDiagnosticsId, ["TASK_FAILED", "UI_UNEXPECTED"])
        { TitleKey = "help.diagnostics.title", BodyKey = "help.diagnostics.body" });
        services.AddSingleton<HelpTopic>(_ => new("logs", Loc.Get("help.logs.title"),
            Loc.Get("help.logs.body"), ShellNavigation.LogsId)
        { TitleKey = "help.logs.title", BodyKey = "help.logs.body" });
        services.AddSingleton<HelpTopic>(_ => new("version", Loc.Get("help.version.title"),
            Loc.Get("help.version.body"), ShellNavigation.AboutId)
        { TitleKey = "help.version.title", BodyKey = "help.version.body" });
        return services;
    }
}
