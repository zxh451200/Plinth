using System;
using System.IO;
using System.Text.Json;
using Avalonia;
using Avalonia.Styling;
using Microsoft.Extensions.Logging;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Services;

/// <summary>用户设置。字段只增不改名,SchemaVersion 用来以后升级旧文件。</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 2;
    public int LogRetentionDays { get; set; } = 30;
    public int LogRetainedFileCount { get; set; } = 10;

    /// <summary>主题:System(跟随系统)、Light、Dark。</summary>
    public string Theme { get; set; } = SettingsService.ThemeSystem;
    public string Density { get; set; } = DensityService.Standard;

    /// <summary>界面语言:zh-CN 或 en-US。修改后重启程序生效。</summary>
    public string Language { get; set; } = Localization.Loc.DefaultLanguage;

    /// <summary>地区和时区独立于界面语言；空值跟随系统，重启生效。</summary>
    public string Region { get; set; } = string.Empty;
    public string TimeZoneId { get; set; } = string.Empty;

    /// <summary>检查更新用的 JSON 地址(必须是 https)。默认留空 = 关闭检查更新。由管理员手工写进 settings.json。</summary>
    public string UpdateFeedUrl { get; set; } = string.Empty;

    /// <summary>上次关闭时的窗口位置和大小。null 表示没记录过,用默认位置。</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>远程服务连接配置。默认关闭,不会联网。</summary>
    public RemoteOptions Remote { get; set; } = new();
}

/// <summary>窗口位置和大小。Maximized 为 true 时,X/Y/Width/Height 是最大化之前的普通状态。</summary>
public sealed class WindowPlacement
{
    public int X { get; set; }

    public int Y { get; set; }

    public double Width { get; set; }

    public double Height { get; set; }

    public bool Maximized { get; set; }
}

/// <summary>读写用户设置(数据目录下的 settings.json),并把主题应用到整个程序。</summary>
public sealed class SettingsService
{
    private readonly ILogger<SettingsService> logger;
    private readonly SensitiveOperationService _operations;
    private readonly SemaphoreSlim _changeGate = new(1, 1);
    private AppSettings _committed;
    private bool _saveBlocked;

    public SettingsService(ILogger<SettingsService> logger, SensitiveOperationService? operations = null)
    {
        this.logger = logger;
        _operations = operations ?? SensitiveOperationService.CreateDefault(logger);
        Current = Load(logger);
        _committed = Clone(Current);
    }
    public const string ThemeSystem = "System";
    public const string ThemeLight = "Light";
    public const string ThemeDark = "Dark";

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>当前已提交设置。用户编辑通过 ChangeAsync 授权保存；Save 用于宿主窗口位置持久化。</summary>
    public AppSettings Current { get; private set; }

    private static string FilePath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    /// <summary>把当前主题设置应用到整个程序,不用重启。</summary>
    public void ApplyTheme()
    {
        if (Application.Current is null)
        {
            return;
        }

        Application.Current.RequestedThemeVariant = Current.Theme switch
        {
            ThemeLight => ThemeVariant.Light,
            ThemeDark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }

    /// <summary>保存设置。先写临时文件再替换,避免写到一半断电留下损坏的文件。</summary>
    public void Save()
    {
        // A pending change may need the UI thread for confirmation, so do not block it here.
        if (!_changeGate.Wait(0))
            throw new InvalidOperationException("settings.change_in_progress");
        try { SaveCore(); }
        finally { _changeGate.Release(); }
    }

    private void SaveCore()
    {
        var userChanged = UserFields(Current) != UserFields(_committed);
        if (userChanged && (DeploymentConfiguration.Current.Authentication.Enabled || DeploymentConfiguration.Current.IsProduction))
        {
            Current = Clone(_committed);
            logger.LogInformation(new EventId(4100, "SecurityAudit"),
                "SecurityAudit {Actor} Settings legacySave denied {CorrelationId}",
                Data.CurrentUser.Name, Guid.NewGuid().ToString("N"));
            throw new UnauthorizedAccessException("settings.use_authorized_change");
        }
        try
        {
            Persist(Current);
            _committed = Clone(Current);
            if (userChanged)
                logger.LogInformation(new EventId(4100, "SecurityAudit"),
                    "SecurityAudit {Actor} Settings legacySave success {CorrelationId}",
                    Data.CurrentUser.Name, Guid.NewGuid().ToString("N"));
        }
        catch { Current = Clone(_committed); throw; }
    }

    public async Task ChangeAsync(string field, object value, CancellationToken token = default)
    {
        await _changeGate.WaitAsync(token);
        try
        {
            var appearance = field is nameof(AppSettings.Theme) or nameof(AppSettings.Language)
                or nameof(AppSettings.Region) or nameof(AppSettings.TimeZoneId) or nameof(AppSettings.Density);
            Task<bool> ApplyChange(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested();
                var candidate = Clone(Current);
                switch (field)
                {
                    case nameof(AppSettings.Density) when value is string density && DensityService.IsSupported(density):
                        candidate.Density = density; break;
                    case nameof(AppSettings.Theme) when value is string theme
                        && theme is ThemeSystem or ThemeLight or ThemeDark:
                        candidate.Theme = theme; break;
                    case nameof(AppSettings.Language) when value is string language
                        && Localization.Loc.SupportedLanguages.Contains(language):
                        candidate.Language = language; break;
                    case nameof(AppSettings.Region) when value is string region:
                        if (region.Length != 0 && (System.Globalization.CultureInfo.GetCultureInfo(region).IsNeutralCulture
                            || System.Globalization.CultureInfo.GetCultureInfo(region).Name.Length == 0))
                            throw new ArgumentException("settings.invalid_region", nameof(value));
                        candidate.Region = region;
                        break;
                    case nameof(AppSettings.TimeZoneId) when value is string zone:
                        if (zone.Length != 0) TimeZoneInfo.FindSystemTimeZoneById(zone);
                        candidate.TimeZoneId = zone;
                        break;
                    case nameof(AppSettings.LogRetentionDays) when value is int days:
                        candidate.LogRetentionDays = Math.Clamp(days, 1, 365); break;
                    case nameof(AppSettings.LogRetainedFileCount) when value is int files:
                        candidate.LogRetainedFileCount = Math.Clamp(files, 1, 100); break;
                    case "RemoteEnabled" when value is bool enabled: candidate.Remote.Enabled = enabled; break;
                    case "RemoteBaseUrl" when value is string url: candidate.Remote.BaseUrl = url; break;
                    case "RemoteHealthPath" when value is string path: candidate.Remote.HealthPath = path; break;
                    default: throw new ArgumentException("settings.invalid_change", nameof(field));
                }
                Persist(candidate);
                Current = candidate;
                _committed = Clone(candidate);
                if (field == nameof(AppSettings.Theme)) ApplyTheme();
                if (field == nameof(AppSettings.Density)) DensityService.Apply(Current.Density);
                return Task.FromResult(true);
            }
            if (appearance)
                await _operations.ExecuteAsync("settings:appearance", "Settings", field, ApplyChange, token);
            else
                await _operations.ExecuteConfirmedAsync("settings:manage", "Settings", field, ApplyChange, token);
        }
        finally { _changeGate.Release(); }
    }

    private static AppSettings Clone(AppSettings settings) =>
        JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

    private static string UserFields(AppSettings settings) => JsonSerializer.Serialize(new
    {
        settings.Theme,
        settings.Density,
        settings.Language,
        settings.Region,
        settings.TimeZoneId,
        settings.UpdateFeedUrl,
        settings.Remote,
        settings.LogRetentionDays,
        settings.LogRetainedFileCount,
    });

    private void Persist(AppSettings settings)
    {
        if (_saveBlocked)
        {
            logger.LogWarning("Settings save blocked because the original file could not be preserved.");
            throw new IOException("settings.original_not_preserved");
        }
        Upgrade(settings);
        var tempPath = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, FilePath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError("Settings persistence failed.");
            // 文件系统拒绝写入不是权限授权拒绝；统一成持久化失败，避免审计误报 denied。
            throw new IOException("settings.persistence_failed", ex);
        }
        finally { if (File.Exists(tempPath)) File.Delete(tempPath); }
    }

    /// <summary>
    /// 程序刚启动、界面还没创建时,只读出语言设置。读不到或文件损坏就返回 null,由调用方用默认语言。
    /// </summary>
    public static string? ReadLanguage()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath))
                : null;
            return settings is null ? null : Upgrade(settings).Language;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private AppSettings Load(ILogger logger)
    {
        if (!File.Exists(FilePath))
        {
            return new AppSettings();
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
            if (settings is not null)
            {
                Upgrade(settings);
                // 值不认识(被手改过、或来自更新版本)时换回默认,不让程序因此出错
                if (settings.Theme is not (ThemeSystem or ThemeLight or ThemeDark))
                {
                    logger.LogWarning("Unknown settings theme; using default.");
                    settings.Theme = ThemeSystem;
                }

                if (!Localization.Loc.SupportedLanguages.Contains(settings.Language))
                {
                    logger.LogWarning("Unknown settings language; using default.");
                    settings.Language = Localization.Loc.DefaultLanguage;
                }

                if (settings.UpdateFeedUrl is null)
                {
                    settings.UpdateFeedUrl = string.Empty;
                }

                settings.Remote ??= new RemoteOptions();
                return settings;
            }
        }
        catch (NotSupportedException)
        {
            logger.LogWarning("Unsupported settings schema; preserving original and using defaults.");
            _saveBlocked = !KeepOriginalFile(logger, ".unsupported");
            return new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            logger.LogWarning("Settings could not be read; preserving original and using defaults.");
        }

        _saveBlocked = !KeepOriginalFile(logger, ".bad");
        return new AppSettings();
    }

    public static AppSettings Upgrade(AppSettings settings)
    {
        if (settings.SchemaVersion is < 1 or > 2)
            throw new NotSupportedException("Unsupported settings schema; original file is preserved.");
        if (settings.SchemaVersion == 1)
        {
            settings.LogRetentionDays = 30;
            settings.LogRetainedFileCount = 10;
            settings.SchemaVersion = 2;
        }
        settings.LogRetentionDays = Math.Clamp(settings.LogRetentionDays, 1, 365);
        settings.LogRetainedFileCount = Math.Clamp(settings.LogRetainedFileCount, 1, 100);
        settings.Region ??= string.Empty;
        settings.TimeZoneId ??= string.Empty;
        if (!DensityService.IsSupported(settings.Density)) settings.Density = DensityService.Standard;
        return settings;
    }

    public static (int Days, int Files) ReadLogRetention()
    {
        try
        {
            var settings = File.Exists(FilePath)
                ? Upgrade(JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new())
                : new AppSettings();
            return (settings.LogRetentionDays, settings.LogRetainedFileCount);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return (30, 10);
        }
    }

    /// <summary>保留原文件，不覆盖已有副本；失败时调用方必须禁止保存。</summary>
    private static bool KeepOriginalFile(ILogger logger, string suffix)
    {
        try
        {
            File.Move(FilePath, FilePath + suffix, overwrite: false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning("Settings original could not be preserved; saving is blocked.");
            return false;
        }
    }
}
