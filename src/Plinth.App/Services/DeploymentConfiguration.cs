using System;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Plinth.App.Auth;
using Serilog.Events;
using Plinth.App.Remote;

namespace Plinth.App.Services;

/// <summary>Trusted deployment configuration, separate from user preferences and backups.</summary>
public sealed class DeploymentConfiguration
{
    public static DeploymentConfiguration Current { get; } = Load();
    public string Profile { get; set; } = "Development";
    public RemoteAuthOptions Authentication { get; set; } = new();
    /// <summary>空值不注册远程示例；这是受信任部署地址，不从用户偏好读取。</summary>
    public string DemoApiBaseUrl { get; set; } = "";
    /// <summary>示例模块(Demo/Notes)默认关闭,部署显式打开。</summary>
    public bool IncludeSampleModules { get; set; }
    /// <summary>高级表格试验模块默认关闭,可信部署显式打开。</summary>
    public bool EnableAdvancedTablePilot { get; set; }
    /// <summary>运行时插件模块默认关闭;打开时仍使用无参默认拒绝配置。</summary>
    public bool EnableRuntimePlugins { get; set; }
    /// <summary>离线同步试点默认关闭;主应用没有生产可信 transportFactory。</summary>
    public bool EnableOfflineSyncPilot { get; set; }
    public string? MinimumLogLevel { get; set; } = "Information";
    /// <summary>受信任部署默认值；用户可在数据目录 database.json 或 PLINTH_Database__* 覆盖。</summary>
    public Plinth.App.Data.DatabaseOptions Database { get; set; } = new();
    public bool IsProduction => Profile == "Production";

    public LogEventLevel TechnicalLogLevel => TryParseLogLevel(MinimumLogLevel, out var level)
        ? level
        : throw new InvalidDataException("Invalid deployment log level.");

    private static DeploymentConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deployment.json");
        var json = ReadOptionalConfiguration(path);
        var configuration = json is not null
            ? JsonSerializer.Deserialize<DeploymentConfiguration>(json)
                ?? throw new InvalidDataException("Empty deployment configuration.")
            : new DeploymentConfiguration();
        var environment = new ConfigurationBuilder()
            .AddEnvironmentVariables(prefix: "PLINTH_").Build();
        try
        {
            return BindOverrides(configuration, environment);
        }
        finally
        {
            // IConfigurationRoot 在 10.x 只保留 Reload；实现在具体 ConfigurationRoot 上。
            (environment as IDisposable)?.Dispose();
        }
    }

    // File.Exists also returns false for access errors and directories. Only absence may use defaults.
    internal static string? ReadOptionalConfiguration(string path)
    {
        try { return File.ReadAllText(path); }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    internal static DeploymentConfiguration BindOverrides(DeploymentConfiguration configuration, IConfiguration overrides)
    {
        try
        {
            overrides.Bind(configuration);
        }
        catch (InvalidOperationException)
        {
            // Binder 的原始异常可能包含配置值；不要把它带入启动日志。
            throw new InvalidDataException("Invalid deployment environment override.");
        }
        configuration.Validate();
        return configuration;
    }

    public void Validate()
    {
        if (Profile is not ("Development" or "Test" or "Production")
            || Authentication is null || DemoApiBaseUrl is null)
            throw new InvalidDataException("Invalid deployment configuration.");
        if (Authentication.Enabled) Authentication.Validate();
        if (!TryParseLogLevel(MinimumLogLevel, out _))
            throw new InvalidDataException("Invalid deployment log level.");
        if (DemoApiBaseUrl.Length == 0) return;
        if (!Authentication.Enabled
            || !Uri.TryCreate(DemoApiBaseUrl, UriKind.Absolute, out var uri)
            || !RemoteApiClient.IsHttpsAddress(uri) || uri.Query.Length != 0
            || !uri.AbsolutePath.EndsWith("/", StringComparison.Ordinal))
            throw new InvalidDataException("Remote demo requires identity and an HTTPS base URL ending in /.");
    }

    private static bool TryParseLogLevel(string? value, out LogEventLevel level)
    {
        switch (value)
        {
            case "Verbose": level = LogEventLevel.Verbose; return true;
            case "Debug": level = LogEventLevel.Debug; return true;
            case "Information": level = LogEventLevel.Information; return true;
            case "Warning": level = LogEventLevel.Warning; return true;
            case "Error": level = LogEventLevel.Error; return true;
            case "Fatal": level = LogEventLevel.Fatal; return true;
            default: level = default; return false;
        }
    }
}
