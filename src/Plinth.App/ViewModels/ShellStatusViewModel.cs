using System;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

/// <summary>Refresh reads local snapshots only. Health checks remain explicit settings-page actions.</summary>
public partial class ShellStatusViewModel(SettingsService settings, RemoteConnectionService remote,
    IRemoteUserContext users, TaskService tasks) : ViewModelBase
{
    [ObservableProperty] public partial string UserText { get; set; } = "";
    [ObservableProperty] public partial string EnvironmentText { get; set; } = "";
    [ObservableProperty] public partial string ConnectionText { get; set; } = "";
    [ObservableProperty] public partial string ConnectionHint { get; set; } = "";
    [ObservableProperty] public partial string TaskText { get; set; } = "";
    [ObservableProperty] public partial int ActiveTaskCount { get; set; }
    /// <summary>远程已启用但最近一次检查失败/配置无效时,状态栏这一项要用警示色。</summary>
    [ObservableProperty] public partial bool IsConnectionWarning { get; set; }

    public void Refresh()
    {
        var user = users.CurrentUser;
        var identity = user.IsAuthenticated ? user.Subject
            : DeploymentConfiguration.Current.Authentication.Enabled ? Loc.Get("auth.signedOut")
            : Environment.UserName;
        UserText = Loc.Format("shell.status.user", identity);
        EnvironmentText = Loc.Format("shell.status.environment", EnvironmentProfileName());
        var health = remote.LastHealthCheck;
        var key = !settings.Current.Remote.Enabled ? "shell.health.disabled" : health?.State switch
        {
            RemoteConnectionState.Connected => "shell.health.passed",
            RemoteConnectionState.Failed => "shell.health.failed",
            RemoteConnectionState.Invalid => "shell.health.invalid",
            _ => "shell.health.unchecked",
        };
        ConnectionText = Loc.Get(key);
        IsConnectionWarning = settings.Current.Remote.Enabled
            && health?.State is RemoteConnectionState.Failed or RemoteConnectionState.Invalid;
        ConnectionHint = Loc.Get("shell.health.hint");
        if (settings.Current.Remote.Enabled && health is not null)
            ConnectionHint = Loc.Format("shell.health.checkedAt",
                FormattingService.Current.FormatInstant(health.CheckedAt)) + " " + ConnectionHint;
        var active = tasks.Snapshot().Where(item => item.Status is WorkStatus.Waiting or WorkStatus.Running).ToArray();
        ActiveTaskCount = active.Length;
        TaskText = Loc.Format("shell.status.activeTasks", active.Length);
        if (active.Length > 0 && active.All(item => item.Progress.HasValue))
            TaskText += " · " + active.Average(item => item.Progress!.Value).ToString("P0", FormattingService.Current.Culture);
    }

    /// <summary>deployment.json 里的 Profile 是固定的英文标记,状态栏按界面语言显示。</summary>
    internal static string EnvironmentProfileName() => DeploymentConfiguration.Current.Profile switch
    {
        "Development" => Loc.Get("shell.environment.development"),
        "Test" => Loc.Get("shell.environment.test"),
        "Production" => Loc.Get("shell.environment.production"),
        _ => DeploymentConfiguration.Current.Profile,
    };
}
