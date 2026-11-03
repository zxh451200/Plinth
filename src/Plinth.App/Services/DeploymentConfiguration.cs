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
    public string? MinimumLogLevel { get; set; } = "Information";
    public bool IsProduction => Profile == "Production";

    public LogEventLevel TechnicalLogLevel => TryParseLogLevel(MinimumLogLevel, out var level)
        ? level
        : throw new InvalidDataException("Invalid deployment log level.");

    private static DeploymentConfiguration Load()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "deployment.json");
        var configuration = File.Exists(path)
            ? JsonSerializer.Deserialize<DeploymentConfiguration>(File.ReadAllText(path))
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
