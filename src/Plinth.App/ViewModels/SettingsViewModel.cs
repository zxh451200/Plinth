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
public sealed record SettingOption(string Key, string Title) : ISelectOption;

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

        ApplyOptionLists();
        SelectedTheme = Themes.FirstOrDefault(option => option.Key == settings.Current.Theme) ?? Themes[0];
        SelectedDensity = Densities.First(option => option.Key == settings.Current.Density);

        // 语言名称用各自的语言来写,这样任何语言的用户都认得出来
        Languages =
        [
            new SettingOption("zh-CN", "简体中文"),
            new SettingOption("en-US", "English"),
        ];
        SelectedLanguage = Languages.FirstOrDefault(option => option.Key == settings.Current.Language) ?? Languages[0];
        SelectedRegion = Regions.First(option => option.Key == settings.Current.Region);
        SelectedTimeZone = TimeZones.First(option => option.Key == settings.Current.TimeZoneId);
        _initializing = false;
        ResetRemoteStatus();
        if (_authentication is not null)
            _authentication.SessionChanged += OnSessionChanged;
    }

    public IReadOnlyList<SettingOption> Languages { get; }
    public IReadOnlyList<SettingOption> Densities { get; private set; } = [];
    [ObservableProperty]
    public partial SettingOption SelectedDensity { get; set; }

    partial void OnSelectedDensityChanged(SettingOption value)
    {
        if (!_initializing)
            QueueChange(nameof(AppSettings.Density), value.Key, "settings.saved");
    }
    public IReadOnlyList<SettingOption> Regions { get; private set; } = [];
    public IReadOnlyList<SettingOption> TimeZones { get; private set; } = [];

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

    public IReadOnlyList<SettingOption> Themes { get; private set; } = [];

    [ObservableProperty]
    public partial SettingOption SelectedTheme { get; set; }

    /// <summary>语言切换后重建会随语言变化的选项并保留当前选择。</summary>
    public override void OnUiLanguageChanged()
    {
        var theme = SelectedTheme?.Key;
        var density = SelectedDensity?.Key;
        var language = SelectedLanguage?.Key;
        var region = SelectedRegion?.Key;
        var zone = SelectedTimeZone?.Key;
        var initializing = _initializing;
        _initializing = true;
        try
        {
            ApplyOptionLists();
            SelectedTheme = Themes.FirstOrDefault(option => option.Key == theme) ?? Themes[0];
            SelectedDensity = Densities.FirstOrDefault(option => option.Key == density) ?? Densities[0];
            SelectedLanguage = Languages.FirstOrDefault(option => option.Key == language) ?? Languages[0];
            SelectedRegion = Regions.FirstOrDefault(option => option.Key == region) ?? Regions[0];
            SelectedTimeZone = TimeZones.FirstOrDefault(option => option.Key == zone) ?? TimeZones[0];
        }
        finally
        {
            _initializing = initializing;
        }
        OnPropertyChanged(nameof(Themes));
        OnPropertyChanged(nameof(Densities));
        OnPropertyChanged(nameof(Regions));
        OnPropertyChanged(nameof(TimeZones));
        base.OnUiLanguageChanged();
    }

    private void ApplyOptionLists()
    {
        Themes =
        [
            new SettingOption(SettingsService.ThemeSystem, Loc.Get("settings.theme.system")),
            new SettingOption(SettingsService.ThemeLight, Loc.Get("settings.theme.light")),
            new SettingOption(SettingsService.ThemeDark, Loc.Get("settings.theme.dark")),
        ];
        Densities =
        [
            new SettingOption(DensityService.Standard, Loc.Get("settings.density.standard")),
            new SettingOption(DensityService.Compact, Loc.Get("settings.density.compact")),
            new SettingOption(DensityService.Comfortable, Loc.Get("settings.density.comfortable")),
        ];
        Regions = BuildRegions(_settings.Current.Region);
        TimeZones = BuildTimeZones(_settings.Current.TimeZoneId);
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<SettingOption>>> RegionCache = new();
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Lazy<IReadOnlyList<SettingOption>>> ZoneCache = new();

    /// <summary>后台预热；第一次打开设置页不再同步枚举上千个 Culture。</summary>
    public static void Prewarm()
    {
        _ = Task.Run(() =>
        {
            _ = GetRegions();
            _ = GetTimeZones();
        });
    }

    private static IReadOnlyList<SettingOption> GetRegions() =>
        RegionCache.GetOrAdd(Loc.Culture.Name, _ => new Lazy<IReadOnlyList<SettingOption>>(BuildRegionList)).Value;

    private static IReadOnlyList<SettingOption> GetTimeZones() =>
        ZoneCache.GetOrAdd(Loc.Culture.Name, _ => new Lazy<IReadOnlyList<SettingOption>>(BuildTimeZoneList)).Value;

    private static IReadOnlyList<SettingOption> BuildRegionList() =>
        new[] { new SettingOption(string.Empty, Loc.Get("settings.format.system")) }
            .Concat(CultureInfo.GetCultures(CultureTypes.SpecificCultures)
                // 同一语言同一地区只留一条:.NET 会给 zh-Hans-HK 和 zh-Hant-HK 这类并列写法,
                // 下拉里看着像重复;按地区主导脚本保留一条。
                .GroupBy(culture => RegionIdentity(culture), StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderBy(ScriptPreference).ThenBy(culture => culture.Name.Length).First())
                // DisplayName 跟随界面语言；不能用 NativeName，否则中文界面也全是 English (…)。
                .OrderBy(culture => culture.DisplayName, StringComparer.Create(Loc.Culture, ignoreCase: true))
                .Select(culture => new SettingOption(culture.Name, culture.DisplayName)))
            .ToArray();

    private static IReadOnlyList<SettingOption> BuildTimeZoneList() =>
        new[] { new SettingOption(string.Empty, Loc.Get("settings.format.system")) }
            .Concat(TimeZoneInfo.GetSystemTimeZones()
                .Select(zone => new SettingOption(zone.Id, zone.DisplayName)))
            .ToArray();

    private static IReadOnlyList<SettingOption> BuildRegions(string selected) =>
        WithCurrent(GetRegions(), selected);

    private static IReadOnlyList<SettingOption> BuildTimeZones(string selected) =>
        WithCurrent(GetTimeZones(), selected);

    private static IReadOnlyList<SettingOption> WithCurrent(IReadOnlyList<SettingOption> options, string selected) =>
        options.Any(option => option.Key == selected)
            ? options : options.Append(new SettingOption(selected, selected)).ToArray();

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
            // 语言保存成功后立即切换界面;其余设置本来就在修改后即时生效。
            if (field == nameof(AppSettings.Language) && value is string language)
                Loc.SetLanguage(language);
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

    /// <summary>语言 + 地区的身份:zh-Hant-HK 与 zh-Hans-HK 视为同一个地区。</summary>
    private static string RegionIdentity(CultureInfo culture)
    {
        var parts = culture.Name.Split('-');
        return parts.Length < 2 ? culture.Name : $"{parts[0]}-{parts[^1]}";
    }

    /// <summary>同一地区有多种脚本写法时,按该地区的主要书写系统挑选。</summary>
    private static int ScriptPreference(CultureInfo culture)
    {
        var parts = culture.Name.Split('-');
        if (parts.Length < 3) return 0;
        var script = parts[1];
        var region = parts[^1];
        var expected = region.ToUpperInvariant() switch
        {
            "TW" or "HK" or "MO" => "Hant",
            "CN" or "SG" => "Hans",
            _ => null,
        };
        return expected is null ? 1 : string.Equals(script, expected, StringComparison.OrdinalIgnoreCase) ? 0 : 1;
    }
}
