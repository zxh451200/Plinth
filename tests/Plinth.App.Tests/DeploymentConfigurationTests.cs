using Plinth.App.Auth;
using Plinth.App.Services;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DeploymentConfigurationTests
{
    [Fact]
    public void Optional_configuration_defaults_only_when_missing_and_rejects_directory_occupants()
    {
        var root = Path.Combine(Path.GetTempPath(), "plinth-config-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            foreach (var name in new[] { "deployment.json", "database.json" })
            {
                var path = Path.Combine(root, name);
                Assert.Null(DeploymentConfiguration.ReadOptionalConfiguration(path));
                File.WriteAllText(path, """{"Profile":"Production"}""");
                Assert.Equal("""{"Profile":"Production"}""", DeploymentConfiguration.ReadOptionalConfiguration(path));
                if (OperatingSystem.IsWindows())
                {
                    using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                    Assert.ThrowsAny<IOException>(() => DeploymentConfiguration.ReadOptionalConfiguration(path));
                }
                File.Delete(path);
                Directory.CreateDirectory(path);
                Assert.ThrowsAny<Exception>(() => DeploymentConfiguration.ReadOptionalConfiguration(path));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Default_configuration_is_offline_and_does_not_enable_remote_demo()
    {
        var configuration = new DeploymentConfiguration();
        configuration.Validate();
        Assert.Empty(configuration.DemoApiBaseUrl);
        Assert.False(configuration.Authentication.Enabled);
        Assert.False(configuration.EnableRuntimePlugins);
        Assert.False(configuration.EnableOfflineSyncPilot);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://api.example/api/")]
    [InlineData("https://user:password@api.example/api/")]
    [InlineData("https://api.example/api/?token=value")]
    [InlineData("https://api.example/api/#fragment")]
    [InlineData("https://api.example/api")]
    [InlineData(" ")]
    public void Invalid_remote_endpoint_is_rejected(string? url)
    {
        var configuration = Authenticated();
        configuration.DemoApiBaseUrl = url!;
        Assert.Throws<InvalidDataException>(configuration.Validate);
    }

    [Fact]
    public void Remote_demo_requires_identity_configuration()
    {
        var configuration = new DeploymentConfiguration { DemoApiBaseUrl = "https://api.example/api/" };
        Assert.Throws<InvalidDataException>(configuration.Validate);
        configuration = Authenticated();
        configuration.DemoApiBaseUrl = "https://api.example/api/";
        configuration.Validate();
    }

    [Fact]
    public void Environment_overrides_deployment_values_and_preserves_unspecified_fields()
    {
        // 先初始化真实配置，避免测试用环境变量影响进程级 Current。
        _ = DeploymentConfiguration.Current;
        const string profileKey = "PLINTH_Profile";
        const string clientKey = "PLINTH_Authentication__ClientId";
        var previousProfile = Environment.GetEnvironmentVariable(profileKey);
        var previousClient = Environment.GetEnvironmentVariable(clientKey);
        try
        {
            Environment.SetEnvironmentVariable(profileKey, "Production");
            Environment.SetEnvironmentVariable(clientKey, "environment-client");
            var environment = new ConfigurationBuilder().AddEnvironmentVariables("PLINTH_").Build();
            var configuration = Authenticated();
            DeploymentConfiguration.BindOverrides(configuration, environment);
            Assert.Equal("Production", configuration.Profile);
            Assert.Equal("environment-client", configuration.Authentication.ClientId);
            Assert.Equal("https://identity.example/", configuration.Authentication.Authority);
        }
        finally
        {
            Environment.SetEnvironmentVariable(profileKey, previousProfile);
            Environment.SetEnvironmentVariable(clientKey, previousClient);
        }
    }

    [Theory]
    [InlineData("Authentication:Enabled", "not-a-boolean")]
    [InlineData("Authentication:BrowserTimeout", "not-a-duration")]
    public void Invalid_override_types_fail_without_exposing_values(string key, string value)
    {
        var overrides = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [key] = value }).Build();
        var error = Assert.Throws<InvalidDataException>(() =>
            DeploymentConfiguration.BindOverrides(Authenticated(), overrides));
        Assert.DoesNotContain(value, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void Overrides_still_require_valid_identity_and_remote_address()
    {
        var overrides = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["DemoApiBaseUrl"] = "http://api.example/api/v1/" }).Build();
        Assert.Throws<InvalidDataException>(() =>
            DeploymentConfiguration.BindOverrides(Authenticated(), overrides));
    }

    [Fact]
    public void Local_login_requires_a_valid_configured_username()
    {
        var enabled = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LocalLogin:Enabled"] = "true",
                ["LocalLogin:Username"] = "admin",
            }).Build();
        var configuration = new DeploymentConfiguration();
        DeploymentConfiguration.BindOverrides(configuration, enabled);
        Assert.True(configuration.LocalLogin.Enabled);
        Assert.Equal("admin", configuration.LocalLogin.Username);

        var invalid = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["LocalLogin:Enabled"] = "true",
                ["LocalLogin:Username"] = "bad\nname",
            }).Build();
        Assert.Throws<InvalidDataException>(() =>
            DeploymentConfiguration.BindOverrides(new DeploymentConfiguration(), invalid));
    }

    private static DeploymentConfiguration Authenticated() => new()
    {
        Authentication = new RemoteAuthOptions
        {
            Enabled = true,
            Authority = "https://identity.example/",
            ClientId = "plinth-test",
            RedirectUri = "http://127.0.0.1:7890/callback/",
        },
    };

    [Theory]
    [InlineData("Verbose", Serilog.Events.LogEventLevel.Verbose)]
    [InlineData("Debug", Serilog.Events.LogEventLevel.Debug)]
    [InlineData("Information", Serilog.Events.LogEventLevel.Information)]
    [InlineData("Warning", Serilog.Events.LogEventLevel.Warning)]
    [InlineData("Error", Serilog.Events.LogEventLevel.Error)]
    [InlineData("Fatal", Serilog.Events.LogEventLevel.Fatal)]
    public void Deployment_log_level_can_be_overridden(string level, Serilog.Events.LogEventLevel expected)
    {
        var overrides = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { ["MinimumLogLevel"] = level }).Build();
        var configuration = new DeploymentConfiguration();
        DeploymentConfiguration.BindOverrides(configuration, overrides);
        Assert.Equal(expected, configuration.TechnicalLogLevel);
        Assert.Equal(level, configuration.MinimumLogLevel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("debug")]
    [InlineData("999")]
    [InlineData("token=private-value")]
    public void Invalid_log_levels_fail_without_echoing_the_value(string? level)
    {
        var configuration = new DeploymentConfiguration { MinimumLogLevel = level! };
        var error = Assert.Throws<InvalidDataException>(configuration.Validate);
        Assert.Equal("Invalid deployment log level.", error.Message);
    }
}
