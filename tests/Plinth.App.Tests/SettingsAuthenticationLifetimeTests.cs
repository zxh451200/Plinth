using System;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Auth;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class SettingsAuthenticationLifetimeTests
{
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Reopened_page_observes_old_login_completion_without_writes_to_disposed_page(bool fail)
    {
        var enabled = DeploymentConfiguration.Current.Authentication.Enabled;
        DeploymentConfiguration.Current.Authentication.Enabled = true;
        var identity = new Identity();
        var gate = new Gate();
        identity.BeforeLogin = gate.WaitAsync;
        identity.FailLogin = fail;
        var session = CreateSession(identity);
        using var oldPage = Page(session);
        var pending = oldPage.LoginCommand.ExecuteAsync(null);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldPage.Dispose();
            var oldStatus = oldPage.StatusText;
            var oldNotifications = 0;
            oldPage.PropertyChanged += (_, _) => oldNotifications++;
            using var newPage = Page(session);
            var notifications = Observe(newPage);

            gate.Release.TrySetResult();
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(0, oldNotifications);
            Assert.Equal(oldStatus, oldPage.StatusText);
            Assert.NotEmpty(notifications);
            Assert.All(notifications, status => Assert.Equal(
                Loc.Get(fail ? "auth.signedOut" : "auth.signedIn"), status));
            Assert.Equal(string.Empty, newPage.StatusText);

            // Even directly executing commands after disposal must not start another operation.
            await oldPage.LoginCommand.ExecuteAsync(null);
            await oldPage.RefreshSessionCommand.ExecuteAsync(null);
            await oldPage.LogoutCommand.ExecuteAsync(null);
            Assert.Equal(1, identity.LoginCalls);
            Assert.Equal(0, identity.RefreshCalls);
            Assert.Equal(0, identity.LogoutCalls);
        }
        finally
        {
            gate.Release.TrySetResult();
            await pending;
            DeploymentConfiguration.Current.Authentication.Enabled = enabled;
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Refresh_preserves_identity_while_pending_then_notifies_publish_or_clear(bool fail)
    {
        var identity = new Identity();
        var session = CreateSession(identity);
        await session.LoginAsync();
        using var page = Page(session);
        var notifications = Observe(page);
        var user = session.CurrentUser;
        var version = session.SessionVersion;
        var gate = new Gate();
        identity.BeforeUserInfo = gate.WaitAsync;
        identity.FailUserInfo = fail;
        identity.Clock.Now += TimeSpan.FromMinutes(10);
        var pending = Task.Run(() => session.GetAccessTokenAsync());
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Same(user, session.CurrentUser);
            Assert.Equal(version, session.SessionVersion);
            Assert.Empty(notifications);
            gate.Release.TrySetResult();
            if (fail)
                await Assert.ThrowsAsync<AuthenticationException>(() => pending);
            else
                Assert.Equal("access", await pending.WaitAsync(TimeSpan.FromSeconds(5)));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(notifications);
            Assert.Equal(Loc.Get(fail ? "auth.signedOut" : "auth.signedIn"), notifications[0]);
            if (!fail) Assert.Equal(version, session.SessionVersion);
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await pending; }
            catch (AuthenticationException) when (fail) { }
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logout_clear_notifies_before_provider_finishes_even_if_provider_fails(bool fail)
    {
        var identity = new Identity { FailLogout = fail };
        var session = CreateSession(identity);
        await session.LoginAsync();
        using var page = Page(session);
        var notifications = Observe(page);
        var gate = new Gate();
        identity.BeforeLogout = gate.WaitAsync;
        var pending = Task.Run(() => session.LogoutAsync());
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Dispatcher.UIThread.RunJobs();
            Assert.Single(notifications);
            Assert.Equal(Loc.Get("auth.signedOut"), notifications[0]);
            Assert.False(session.CurrentUser.IsAuthenticated);
            gate.Release.TrySetResult();
            if (fail)
                await Assert.ThrowsAsync<AuthenticationException>(() => pending);
            else
                await pending.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            gate.Release.TrySetResult();
            try { await pending; }
            catch (AuthenticationException) when (fail) { }
        }
    }

    [AvaloniaFact]
    public void Queued_notification_after_dispose_is_ignored_and_later_changes_are_unsubscribed()
    {
        var session = CreateSession(new Identity());
        using var page = Page(session);
        var notifications = Observe(page);
        // Keep the UI thread occupied until the worker has queued notifications and released the auth gate.
        // A synchronous dispatcher invocation in the subscriber would time out here.
        Task.Run(() => session.LoginAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Assert.Empty(notifications);
        page.Dispose();
        page.Dispose();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(notifications);
        Task.Run(() => session.LogoutAsync()).WaitAsync(TimeSpan.FromSeconds(5)).GetAwaiter().GetResult();
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(notifications);
        // Check actual unsubscription as well as the disposed callback guard.
        var handlers = (EventHandler?)typeof(RemoteAuthService)
            .GetField("SessionChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(session);
        Assert.Null(handlers);
    }

    [Fact]
    public async Task Throwing_observer_cannot_change_auth_result_or_skip_other_observers()
    {
        var identity = new Identity();
        var session = CreateSession(identity);
        var states = new List<bool>();
        session.SessionChanged += (_, _) => throw new InvalidOperationException("observer");
        session.SessionChanged += (_, _) => states.Add(session.CurrentUser.IsAuthenticated);
        await session.LoginAsync();
        Assert.Equal(new[] { false, true }, states);
        states.Clear();
        identity.FailLogin = true;
        var error = await Assert.ThrowsAsync<AuthenticationException>(() => session.LoginAsync());
        Assert.Equal("auth.test_login_failed", error.Message);
        Assert.Equal(new[] { false, false }, states);
        states.Clear();
        await session.LogoutAsync();
        Assert.Equal(new[] { false }, states);
    }

    [AvaloniaFact]
    public async Task Settings_without_auth_service_remains_offline()
    {
        using var page = new SettingsViewModel(new SettingsService(NullLogger<SettingsService>.Instance));
        await page.LoginCommand.ExecuteAsync(null);
        await page.RefreshSessionCommand.ExecuteAsync(null);
        await page.LogoutCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("auth.signedOut"), page.AuthenticationStatus);
        Assert.Equal(string.Empty, page.StatusText);
    }

    private static SettingsViewModel Page(RemoteAuthService session) =>
        new(new SettingsService(NullLogger<SettingsService>.Instance), authentication: session);

    private static List<string> Observe(SettingsViewModel page)
    {
        var notifications = new List<string>();
        page.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(SettingsViewModel.AuthenticationStatus)) return;
            Assert.True(Dispatcher.UIThread.CheckAccess());
            notifications.Add(page.AuthenticationStatus);
        };
        return notifications;
    }

    private static RemoteAuthService CreateSession(Identity identity) => new(new RemoteAuthOptions
    {
        Enabled = true,
        Authority = "https://identity.example",
        ClientId = "test",
        RedirectUri = "http://127.0.0.1:9876/callback/",
    }, identity, new Store(), new Audit(), identity.Clock);

    private sealed class Gate
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task WaitAsync(CancellationToken token)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(token);
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Identity : IIdentityClient
    {
        public Clock Clock { get; } = new();
        public Func<CancellationToken, Task>? BeforeLogin { get; set; }
        public Func<CancellationToken, Task>? BeforeUserInfo { get; set; }
        public Func<CancellationToken, Task>? BeforeLogout { get; set; }
        public bool FailLogin { get; set; }
        public bool FailUserInfo { get; set; }
        public bool FailLogout { get; set; }
        public int LoginCalls { get; private set; }
        public int RefreshCalls { get; private set; }
        public int LogoutCalls { get; private set; }
        private static ClaimsPrincipal User => new(new ClaimsIdentity([new Claim("sub", "test-user")], "oidc"));
        private IdentityTokens Tokens() => new()
        {
            AccessToken = "access",
            RefreshToken = "refresh",
            ExpiresAt = Clock.Now.AddMinutes(5),
            User = User,
        };
        public async Task<IdentityTokens> LoginAsync(CancellationToken cancellationToken)
        {
            LoginCalls++;
            if (BeforeLogin is not null) await BeforeLogin(cancellationToken);
            if (FailLogin) throw new AuthenticationException("auth.test_login_failed");
            return Tokens();
        }
        public Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(Tokens());
        }
        public async Task<ClaimsPrincipal> GetUserAsync(string accessToken, CancellationToken cancellationToken)
        {
            if (BeforeUserInfo is not null) await BeforeUserInfo(cancellationToken);
            if (FailUserInfo) throw new AuthenticationException("auth.subject_missing", true);
            return User;
        }
        public async Task LogoutAsync(string? identityToken, CancellationToken cancellationToken)
        {
            LogoutCalls++;
            if (BeforeLogout is not null) await BeforeLogout(cancellationToken);
            if (FailLogout) throw new AuthenticationException("auth.end_session_failed");
        }
    }

    private sealed class Store : ICredentialStore
    {
        private string? _secret;
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(_secret);
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            _secret = secret;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            _secret = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Audit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }
}
