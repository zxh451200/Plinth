using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

[Collection("Localization")]
public sealed class LocalLoginTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "plinth-login-" + Guid.NewGuid().ToString("N"));

    public LocalLoginTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private LocalLoginService Service(bool enabled = true, string username = "admin") =>
        new(new LocalLoginOptions { Enabled = enabled, Username = username }, _root);

    private static BrandingService Branding() => new(NullLogger<BrandingService>.Instance);

    [Fact]
    public async Task First_run_stores_only_a_pbkdf2_hash_and_authenticates()
    {
        var service = Service();
        Assert.True(service.NeedsSetup);
        Assert.Equal("admin", service.PrefillUsername);

        await service.CreateCredentialAsync("admin", "correct horse battery");
        Assert.False(service.NeedsSetup);
        Assert.Equal("admin", service.ReadUsername());
        var stored = File.ReadAllText(service.CredentialPath, Encoding.UTF8);
        Assert.DoesNotContain("correct horse battery", stored, StringComparison.Ordinal);
        Assert.Contains("pbkdf2-sha256$210000$", stored, StringComparison.Ordinal);

        Assert.True((await service.AuthenticateAsync("admin", "correct horse battery")).Success);
        var wrong = await service.AuthenticateAsync("admin", "wrong password");
        Assert.False(wrong.Success);
        Assert.Equal("login.error.invalid", wrong.ErrorKey);
        Assert.False((await service.AuthenticateAsync("other", "correct horse battery")).Success);
    }

    [Fact]
    public async Task Disabled_login_does_not_expose_a_gate()
    {
        var service = Service(enabled: false);
        Assert.False(service.IsEnabled);
        Assert.False(service.NeedsSetup);
        Assert.Equal("", service.ReadUsername());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.AuthenticateAsync("admin", "correct horse battery"));
    }

    [Fact]
    public async Task Invalid_username_and_password_are_rejected()
    {
        var service = Service();
        var shortPassword = await Assert.ThrowsAsync<LocalLoginException>(() =>
            service.CreateCredentialAsync("admin", "short"));
        Assert.Equal("login.error.passwordLength", shortPassword.ErrorKey);
        var controlPassword = await Assert.ThrowsAsync<LocalLoginException>(() =>
            service.CreateCredentialAsync("admin", "password\n"));
        Assert.Equal("login.error.passwordControl", controlPassword.ErrorKey);
        var badName = await Assert.ThrowsAsync<LocalLoginException>(() =>
            service.CreateCredentialAsync("bad\nname", "correct horse"));
        Assert.Equal("login.error.username", badName.ErrorKey);

        Assert.Equal("login.error.username", LocalLoginService.ValidateUsername(new string('x', 65)));
        Assert.Equal("login.error.username", LocalLoginService.ValidateUsername("bad\nname"));
        Assert.Null(LocalLoginService.ValidateUsername("admin"));
        Assert.Equal("login.error.passwordLength", LocalLoginService.ValidatePassword("short"));
        Assert.Equal("login.error.passwordControl", LocalLoginService.ValidatePassword("password\n"));
        Assert.Null(LocalLoginService.ValidatePassword("correct horse"));
    }

    [Fact]
    public async Task Corrupt_credential_requires_setup_and_can_be_recreated()
    {
        var service = Service();
        File.WriteAllText(service.CredentialPath, "not json", Encoding.UTF8);
        Assert.True(service.NeedsSetup);
        var result = await service.AuthenticateAsync("admin", "correct horse");
        Assert.False(result.Success);
        Assert.Equal("login.error.setupRequired", result.ErrorKey);

        await service.CreateCredentialAsync("admin", "correct horse");
        Assert.True((await service.AuthenticateAsync("admin", "correct horse")).Success);
    }

    [Fact]
    public async Task View_model_sets_up_then_accepts_only_the_correct_password()
    {
        Loc.Initialize("zh-CN");
        try
        {
            var service = Service();
            using var branding = Branding();
            var setup = new LoginViewModel(service, branding);
            Assert.True(setup.IsSetup);
            setup.Username = "admin";
            setup.Password = "correct horse";
            setup.ConfirmPassword = "correct horse";
            bool? outcome = null;
            setup.Completed += success => outcome = success;
            await ((IAsyncRelayCommand)setup.SubmitCommand).ExecuteAsync(null);
            Assert.True(outcome);
            Assert.False(service.NeedsSetup);

            var login = new LoginViewModel(service, branding);
            Assert.False(login.IsSetup);
            Assert.Equal("admin", login.Username);
            outcome = null;
            login.Completed += success => outcome = success;
            login.Password = "correct horse";
            await ((IAsyncRelayCommand)login.SubmitCommand).ExecuteAsync(null);
            Assert.True(outcome);

            var wrong = new LoginViewModel(service, branding);
            outcome = null;
            wrong.Completed += success => outcome = success;
            wrong.Password = "wrong password";
            await ((IAsyncRelayCommand)wrong.SubmitCommand).ExecuteAsync(null);
            Assert.Null(outcome);
            Assert.True(wrong.HasError);
            Assert.False(string.IsNullOrWhiteSpace(wrong.ErrorMessage));
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage);
        }
    }

    [Fact]
    public async Task View_model_rejects_mismatched_setup_passwords()
    {
        var service = Service();
        using var branding = Branding();
        var setup = new LoginViewModel(service, branding);
        setup.Username = "admin";
        setup.Password = "correct horse";
        setup.ConfirmPassword = "different horse";
        bool? outcome = null;
        setup.Completed += success => outcome = success;
        await ((IAsyncRelayCommand)setup.SubmitCommand).ExecuteAsync(null);
        Assert.Null(outcome);
        Assert.True(setup.HasError);
        Assert.True(service.NeedsSetup);
    }
}
