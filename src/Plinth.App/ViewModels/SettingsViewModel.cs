using System.Collections.Generic;
using System;
using System.Linq;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Services;
using Plinth.App.Localization;

namespace Plinth.App.ViewModels;

/// <summary>下拉框里的一个选项。Key 是保存到设置文件里的值,Title 是显示给用户看的文字。</summary>
public sealed record SettingOption(string Key, string Title);

/// <summary>设置页。改了就立即生效并保存,不需要点保存按钮。</summary>
public partial class SettingsViewModel : ViewModelBase, Plinth.App.Navigation.IUnsavedChanges, IDisposable
{
    private readonly SettingsService _settings;
    private readonly FolderService? _folderService;
    private readonly RemoteConnectionService? _remote;
    private bool _initializing = true;
    private readonly Auth.RemoteAuthService? _authentication;
    private int _pendingChanges;
    private bool _saveFailed;
    private int _requestVersion;
    private volatile bool _disposed;
    public bool IsDirty => IsSaving || _saveFailed;
    public Task PendingChange { get; private set; } = Task.CompletedTask;
    [ObservableProperty]
    public partial bool IsSaving { get; set; }
    [ObservableProperty]
    public partial bool IsLeaving { get; set; }

    public SettingsViewModel(
        SettingsService settings,
        FolderService? folderService = null,
        RemoteConnectionService? remote = null,
        Auth.RemoteAuthService? authentication = null)
    {
        _settings = settings;
        _folderService = folderService;
        _remote = remote;
        _authentication = authentication;
        RemoteEnabled = settings.Current.Remote.Enabled;
        RemoteBaseUrl = settings.Current.Remote.BaseUrl;
        RemoteHealthPath = settings.Current.Remote.HealthPath;
        LogRetentionDays = settings.Current.LogRetentionDays;
        LogRetainedFileCount = settings.Current.LogRetainedFileCount;

        Themes =
        [
            new SettingOption(SettingsService.ThemeSystem, Loc.Get("settings.theme.system")),
            new SettingOption(SettingsService.ThemeLight, Loc.Get("settings.theme.light")),
            new SettingOption(SettingsService.ThemeDark, Loc.Get("settings.theme.dark")),
        ];
        SelectedTheme = Themes.FirstOrDefault(option => option.Key == settings.Current.Theme) ?? Themes[0];
        Densities =
        [
            new(DensityService.Standard, Loc.Get("settings.density.standard")),
            new(DensityService.Compact, Loc.Get("settings.density.compact")),
            new(DensityService.Comfortable, Loc.Get("settings.density.comfortable")),
        ];
        SelectedDensity = Densities.First(option => option.Key == settings.Current.Density);

        // 语言名称用各自的语言来写,这样任何语言的用户都认得出来
        Languages =
        [
            new SettingOption("zh-CN", "简体中文"),
            new SettingOption("en-US", "English"),
        ];
        SelectedLanguage = Languages.FirstOrDefault(option => option.Key == settings.Current.Language) ?? Languages[0];
        Regions = new[] { new SettingOption(string.Empty, Loc.Get("settings.format.system")) }
            .Concat(CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                .OrderBy(culture => culture.NativeName, StringComparer.CurrentCulture)
                .Select(culture => new SettingOption(culture.Name, $"{culture.NativeName} ({culture.Name})")))
            .ToArray();
        TimeZones = new[] { new SettingOption(string.Empty, Loc.Get("settings.format.system")) }
            .Concat(TimeZoneInfo.GetSystemTimeZones()
                .Select(zone => new SettingOption(zone.Id, zone.DisplayName)))
            .ToArray();
        // Preserve imported identifiers unavailable on this OS; DisplayFormat applies its safe fallback.
        if (!TimeZones.Any(option => option.Key == settings.Current.TimeZoneId))
            TimeZones = TimeZones.Append(new SettingOption(settings.Current.TimeZoneId, settings.Current.TimeZoneId)).ToArray();
        if (!Regions.Any(option => option.Key == settings.Current.Region))
            Regions = Regions.Append(new SettingOption(settings.Current.Region, settings.Current.Region)).ToArray();
        SelectedRegion = Regions.First(option => option.Key == settings.Current.Region);
        SelectedTimeZone = TimeZones.First(option => option.Key == settings.Current.TimeZoneId);
        _initializing = false;
        ResetRemoteStatus();
        if (_authentication is not null)
            _authentication.SessionChanged += OnSessionChanged;
    }

    public IReadOnlyList<SettingOption> Languages { get; }
    public IReadOnlyList<SettingOption> Densities { get; }
    [ObservableProperty]
    public partial SettingOption SelectedDensity { get; set; }

    partial void OnSelectedDensityChanged(SettingOption value)
    {
        if (!_initializing)
            QueueChange(nameof(AppSettings.Density), value.Key, "settings.saved");
    }
    public IReadOnlyList<SettingOption> Regions { get; }
    public IReadOnlyList<SettingOption> TimeZones { get; }

    [ObservableProperty]
    public partial SettingOption SelectedRegion { get; set; }
    [ObservableProperty]
    public partial SettingOption SelectedTimeZone { get; set; }

    partial void OnSelectedRegionChanged(SettingOption value)
    {
        if (!_initializing)
            QueueChange(nameof(AppSettings.Region), value.Key, "settings.format.saved");
    }

    partial void OnSelectedTimeZoneChanged(SettingOption value)
    {
        if (!_initializing)
            QueueChange(nameof(AppSettings.TimeZoneId), value.Key, "settings.format.saved");
    }
    public bool AuthenticationEnabled => DeploymentConfiguration.Current.Authentication.Enabled;
    public string AuthenticationStatus => _authentication?.CurrentUser.IsAuthenticated == true
        ? Loc.Get("auth.signedIn") : Loc.Get("auth.signedOut");
    public string Profile => DeploymentConfiguration.Current.Profile;
    [ObservableProperty]
    public partial int LogRetentionDays { get; set; } = 30;
    [ObservableProperty]
    public partial int LogRetainedFileCount { get; set; } = 10;
    partial void OnLogRetentionDaysChanged(int value)
    {
        if (_initializing) return;
        QueueChange(nameof(AppSettings.LogRetentionDays), value, "settings.restart");
    }
    partial void OnLogRetainedFileCountChanged(int value)
    {
        if (_initializing) return;
        QueueChange(nameof(AppSettings.LogRetainedFileCount), value, "settings.restart");
    }

    [RelayCommand]
    private Task LoginAsync() => AuthenticateAsync(0);
    [RelayCommand]
    private Task RefreshSessionAsync() => AuthenticateAsync(1);
    [RelayCommand]
    private Task LogoutAsync() => AuthenticateAsync(2);

    private async Task AuthenticateAsync(int operation)
    {
        if (_disposed || _authentication is null || !AuthenticationEnabled) return;
        try
        {
            if (operation == 0) await _authentication.LoginAsync();
            else if (operation == 1) await _authentication.GetAccessTokenAsync();
            else await _authentication.LogoutAsync();
            if (!_disposed) StatusText = Loc.Get("auth.completed");
        }
        catch (Exception)
        {
            if (!_disposed) StatusText = Loc.Get("auth.failed");
        }
        finally
        {
            if (!_disposed) OnPropertyChanged(nameof(AuthenticationStatus));
        }
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        // Never synchronously wait for the UI while the authentication service holds its gate.
        Dispatcher.UIThread.Post(() =>
        {
            if (!_disposed) OnPropertyChanged(nameof(AuthenticationStatus));
        });
    }

    [ObservableProperty]
    public partial SettingOption SelectedLanguage { get; set; }

    public IReadOnlyList<SettingOption> Themes { get; }

    [ObservableProperty]
    public partial SettingOption SelectedTheme { get; set; }

    /// <summary>设置改完后的提示,例如"已保存"。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    public string DataDirectory => AppPaths.DataDirectory;

    public string LogDirectory => AppPaths.LogDirectory;

    [ObservableProperty]
    public partial bool RemoteEnabled { get; set; }

    [ObservableProperty]
    public partial string RemoteBaseUrl { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string RemoteHealthPath { get; set; } = "/health";

    [ObservableProperty]
    public partial string RemoteStatus { get; set; } = string.Empty;

    partial void OnRemoteEnabledChanged(bool value)
    {
        InvalidateRemoteCheck();
        if (_initializing) return;
        QueueChange("RemoteEnabled", value, "settings.saved");
    }

    partial void OnRemoteBaseUrlChanged(string value)
    {
        InvalidateRemoteCheck();
        if (_initializing) return;
        QueueChange("RemoteBaseUrl", value, "settings.saved");
    }

    partial void OnRemoteHealthPathChanged(string value)
    {
        InvalidateRemoteCheck();
        if (_initializing) return;
        QueueChange("RemoteHealthPath", value, "settings.saved");
    }

    [RelayCommand]
    private async Task CheckRemoteAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed) return;
        var version = ++_requestVersion;
        if (_remote is null)
        {
            RemoteStatus = Loc.Get("remote.status.unavailable");
            return;
        }

        // The service checks committed settings, not edits awaiting authorization/save.
        if (!RemoteInputsMatch(_remote.Current))
        {
            ResetRemoteStatus();
            return;
        }

        try
        {
            var result = await _remote.CheckAsync(cancellationToken);
            if (_disposed || version != _requestVersion || !RemoteInputsMatch(_remote.Current)) return;
            var status = result.State switch
            {
                RemoteConnectionState.Disabled => Loc.Get("remote.status.disabled"),
                RemoteConnectionState.Connected => Loc.Get("remote.status.connected"),
                RemoteConnectionState.Invalid => Loc.Get("remote.status.invalid"),
                _ => Loc.Get("remote.status.failed"),
            };
            if (result.Error is { } error)
                status = Loc.Get(error.MessageKey) + " " + Loc.Get(error.SuggestionKey)
                    + " · " + error.CorrelationId;
            RemoteStatus = status;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && version == _requestVersion)
                ResetRemoteStatus();
        }
        catch (Exception)
        {
            if (!_disposed && version == _requestVersion)
                RemoteStatus = Loc.Get("remote.status.failed");
        }
    }

    private bool RemoteInputsMatch(RemoteOptions options) =>
        RemoteEnabled == options.Enabled
        && string.Equals(RemoteBaseUrl, options.BaseUrl, StringComparison.Ordinal)
        && string.Equals(RemoteHealthPath, options.HealthPath, StringComparison.Ordinal);

    private void InvalidateRemoteCheck()
    {
        ++_requestVersion;
        if (!_disposed) ResetRemoteStatus();
    }

    private void ResetRemoteStatus() =>
        RemoteStatus = Loc.Get(_remote is null ? "remote.status.unavailable"
            : !RemoteEnabled ? "remote.status.disabled"
            : !RemoteConnectionService.TryBuildHealthUri(new RemoteOptions
            {
                Enabled = RemoteEnabled,
                BaseUrl = RemoteBaseUrl,
                HealthPath = RemoteHealthPath,
            }, out _) ? "remote.status.invalid" : "shell.health.unchecked");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_authentication is not null)
            _authentication.SessionChanged -= OnSessionChanged;
        ++_requestVersion;
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        StatusText = _folderService?.OpenWithMessage(AppPaths.DataDirectory, Loc.Get("folder.data"))
                     ?? Loc.Get("folder.failed");

    [RelayCommand]
    private void OpenLogFolder() =>
        StatusText = _folderService?.OpenWithMessage(AppPaths.LogDirectory, Loc.Get("folder.log"))
                     ?? Loc.Get("folder.failed");

    partial void OnSelectedThemeChanged(SettingOption value)
    {
        if (!_initializing && _settings.Current.Theme != value.Key)
            QueueChange(nameof(AppSettings.Theme), value.Key, "settings.saved");
    }

    /// <summary>语言改了要重启程序才生效(界面文字在启动时就定了)。</summary>
    partial void OnSelectedLanguageChanged(SettingOption value)
    {
        if (!_initializing && _settings.Current.Language != value.Key)
            QueueChange(nameof(AppSettings.Language), value.Key, "settings.language.saved");
    }

    private void QueueChange(string field, object value, string successKey) =>
        PendingChange = Task.WhenAll(PendingChange, PersistChangeAsync(field, value, successKey));

    public async Task<bool> TryLeaveAsync()
    {
        IsLeaving = true;
        try
        {
            Task pending;
            do
            {
                pending = PendingChange;
                await pending;
            }
            while (!ReferenceEquals(pending, PendingChange));
            if (!_saveFailed) return true;
            _saveFailed = false;
            StatusText = Loc.Get("settings.saveFailed");
            return false;
        }
        finally { IsLeaving = false; }
    }

    private async Task PersistChangeAsync(string field, object value, string successKey)
    {
        _pendingChanges++;
        IsSaving = true;
        try
        {
            await _settings.ChangeAsync(field, value);
            StatusText = Loc.Get(successKey);
        }
        catch (OperationCanceledException) { StatusText = Loc.Get("settings.cancelled"); }
        catch (Exception)
        {
            _saveFailed = true;
            StatusText = Loc.Get("settings.saveFailed");
        }
        finally
        {
            if (--_pendingChanges == 0)
            {
                // Rejected/cancelled/failed edits must not stay displayed as though they took effect.
                _initializing = true;
                SelectedTheme = Themes.First(t => t.Key == _settings.Current.Theme);
                SelectedDensity = Densities.First(t => t.Key == _settings.Current.Density);
                SelectedLanguage = Languages.First(t => t.Key == _settings.Current.Language);
                SelectedRegion = Regions.First(t => t.Key == _settings.Current.Region);
                SelectedTimeZone = TimeZones.First(t => t.Key == _settings.Current.TimeZoneId);
                RemoteEnabled = _settings.Current.Remote.Enabled;
                RemoteBaseUrl = _settings.Current.Remote.BaseUrl;
                RemoteHealthPath = _settings.Current.Remote.HealthPath;
                LogRetentionDays = _settings.Current.LogRetentionDays;
                LogRetainedFileCount = _settings.Current.LogRetainedFileCount;
                _initializing = false;
                IsSaving = false;
            }
        }
    }
}
