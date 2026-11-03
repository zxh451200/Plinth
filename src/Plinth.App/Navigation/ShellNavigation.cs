namespace Plinth.App.Navigation;

/// <summary>Shell 自带页面的稳定 Id,组合根注册和 Shell 命令共用同一份。</summary>
public static class ShellNavigation
{
    public const string OverviewId = "shell.overview";

    public const string AboutId = "shell.about";

    public const string SettingsId = "shell.settings";

    public const string LogsId = "shell.logs";

    public const string TaskDiagnosticsId = "shell.taskDiagnostics";

    public const string BackupId = "shell.backup";
    public const string HelpId = "shell.help";
    public const string LicensesId = "shell.licenses";
}
