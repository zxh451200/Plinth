using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Auth;
using Plinth.App.Remote;
using Plinth.App.Security;
using Xunit;

namespace Plinth.App.Tests.Remote;

/// <summary>Protocol/service tests use injected doubles; these do not stand in for an IdP end-to-end smoke test.</summary>
public sealed class RemoteSecurityTests
{
    [Theory]
    [InlineData("http://id.example", "http://127.0.0.1:9876/callback/")]
    [InlineData("https://id.example", "http://localhost:9876/callback/")]
    [InlineData("https://id.example", "http://0.0.0.0:9876/callback/")]
    [InlineData("https://id.example", "http://127.0.0.1:9876/callback/?q=secret")]
    public void Configuration_rejects_insecure_authority_or_callback(string authority, string redirect)
    {
        var options = Options();
        options.Authority = authority;
        options.RedirectUri = redirect;
        Assert.Throws<InvalidOperationException>(options.Validate);
    }

    [Fact]
    public async Task Concurrent_requests_refresh_once_and_persist_rotation_before_loading_claims()
    {
        var clock = new Clock();
        var store = new Store();
        var identity = new Identity(clock, store);
        var audit = new Audit();
        var session = new RemoteAuthService(Options(), identity, store, audit, clock);
        await session.LoginAsync();
        var sessionVersion = session.SessionVersion;
        clock.Now += TimeSpan.FromMinutes(10);
        var results = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => session.GetAccessTokenAsync()));
        Assert.All(results, token => Assert.Equal("access-rotated", token));
        Assert.Equal(1, identity.RefreshCalls);
        Assert.Equal("refresh-rotated", store.Secret);
        Assert.True(session.CurrentUser.Permissions.Contains("records:read"));
        Assert.Equal(sessionVersion, session.SessionVersion);
        Assert.DoesNotContain(audit.Entries, e => e.ToString().Contains("refresh-rotated", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Refresh_keeps_identity_until_userinfo_succeeds_or_session_is_cleared(int failure)
    {
        var clock = new Clock();
        var store = new Store();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var identity = new Identity(clock, store)
        {
            BeforeGetUser = async ct =>
            {
                entered.TrySetResult(true);
                await release.Task.WaitAsync(ct);
                if (failure == 1) throw new AuthenticationException("auth.subject_missing", true);
                if (failure == 2) throw new HttpRequestException("UserInfo unavailable");
            },
        };
        var session = new RemoteAuthService(Options(), identity, store, new Audit(), clock);
        await session.LoginAsync();
        var user = session.CurrentUser;
        var version = session.SessionVersion;
        clock.Now += TimeSpan.FromMinutes(10);
        var pending = session.GetAccessTokenAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("refresh-rotated", store.Secret);
            Assert.Same(user, session.CurrentUser);
            Assert.Equal(version, session.SessionVersion);
        }
        finally { release.TrySetResult(true); }
        if (failure != 0)
        {
            if (failure == 1)
            {
                await Assert.ThrowsAsync<AuthenticationException>(() => pending);
                Assert.Null(store.Secret);
            }
            else
            {
                await Assert.ThrowsAsync<HttpRequestException>(() => pending);
                // 已保存的新凭据保留，不能退回已经轮换的旧凭据。
                Assert.Equal("refresh-rotated", store.Secret);
            }
            Assert.False(session.CurrentUser.IsAuthenticated);
            Assert.True(session.SessionVersion > version);
        }
        else
        {
            Assert.Equal("access-rotated", await pending);
            Assert.Equal(user.Subject, session.CurrentUser.Subject);
            Assert.Equal(version, session.SessionVersion);
        }
    }

    [Fact]
    public async Task Logout_and_same_subject_login_advance_session_version()
    {
        var clock = new Clock();
        var store = new Store();
        var session = new RemoteAuthService(Options(), new Identity(clock, store), store, new Audit(), clock);
        await session.LoginAsync();
        var subject = session.CurrentUser.Subject;
        var before = session.SessionVersion;
        await session.LogoutAsync();
        Assert.True(session.SessionVersion > before);
        var loggedOut = session.SessionVersion;
        await session.LoginAsync();
        Assert.Equal(subject, session.CurrentUser.Subject);
        Assert.True(session.SessionVersion > loggedOut);
    }

    [Fact]
    public async Task Restart_restores_identity_from_userinfo_instead_of_decoding_access_token()
    {
        var clock = new Clock();
        var store = new Store { Secret = "refresh-initial" };
        var identity = new Identity(clock, store);
        var session = new RemoteAuthService(Options(), identity, store, new Audit(), clock);
        Assert.False(session.CurrentUser.IsAuthenticated);
        Assert.Equal("access-rotated", await session.GetAccessTokenAsync());
        Assert.Equal("subject-1", session.CurrentUser.Subject);
    }

    [Fact]
    public async Task Failed_rotated_token_storage_clears_session_and_does_not_replay_old_token()
    {
        var clock = new Clock();
        var store = new Store();
        var identity = new Identity(clock, store);
        var session = new RemoteAuthService(Options(), identity, store, new Audit(), clock);
        await session.LoginAsync();
        clock.Now += TimeSpan.FromMinutes(10);
        store.FailWrite = true;
        await Assert.ThrowsAsync<CredentialStoreException>(() => session.GetAccessTokenAsync());
        Assert.False(session.CurrentUser.IsAuthenticated);
        Assert.Null(store.Secret);
        await Assert.ThrowsAsync<AuthenticationException>(() => session.GetAccessTokenAsync());
        Assert.Equal(1, identity.RefreshCalls);
    }

    [Fact]
    public async Task Logout_removes_local_tokens_even_if_provider_logout_fails()
    {
        var clock = new Clock();
        var store = new Store();
        var identity = new Identity(clock, store) { FailLogout = true };
        var audit = new Audit();
        var session = new RemoteAuthService(Options(), identity, store, audit, clock);
        await session.LoginAsync();
        await Assert.ThrowsAsync<AuthenticationException>(() => session.LogoutAsync());
        Assert.False(session.CurrentUser.IsAuthenticated);
        Assert.Null(store.Secret);
        await Assert.ThrowsAsync<AuthenticationException>(() => session.GetAccessTokenAsync());
        Assert.Contains(audit.Entries, e => e.Operation == "logout" && e.Result == "failed");
    }

    [Fact]
    public async Task Permission_checks_default_to_deny_and_audit_denials()
    {
        var clock = new Clock();
        var store = new Store();
        var session = new RemoteAuthService(Options(), new Identity(clock, store), store, new Audit(), clock);
        var audit = new Audit();
        var permissions = new PermissionService(session, audit,
            new Dictionary<string, string[]> { ["reader"] = ["records:export"] });
        Assert.False(permissions.HasPermission("records:read"));
        await session.LoginAsync();
        Assert.True(permissions.HasPermission("records:read"));
        Assert.True(permissions.HasPermission("records:export"));
        Assert.False(permissions.HasPermission("records:delete"));
        Assert.Throws<UnauthorizedAccessException>(() => permissions.Demand("records:delete", "operation-1"));
        Assert.Contains(audit.Entries, e => e.Result == "denied" && e.CorrelationId == "operation-1");
    }

    [Theory]
    [InlineData("POST")]
    [InlineData("PATCH")]
    [InlineData("PUT")]
    [InlineData("DELETE")]
    [InlineData("CUSTOM")]
    public async Task Writes_and_unknown_methods_are_never_automatically_retried(string method)
    {
        var transport = new Transport(HttpStatusCode.ServiceUnavailable);
        using var client = RemoteHttp.CreateClient(transport);
        var api = new RemoteApiClient(client, new Uri("https://api.example/"));
        var error = await Assert.ThrowsAsync<RemoteApiException>(() => api.SendAsync(new HttpMethod(method), "/records"));
        Assert.Equal(1, transport.Calls);
        Assert.False(error.Error.CanRetry);
        Assert.NotEmpty(error.Error.CorrelationId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, 1)]
    [InlineData(HttpStatusCode.Forbidden, 1)]
    [InlineData(HttpStatusCode.ServiceUnavailable, 3)]
    public async Task Read_retries_are_bounded_and_auth_failures_are_not_retried(HttpStatusCode status, int calls)
    {
        var transport = new Transport(status);
        using var client = RemoteHttp.CreateClient(transport);
        var api = new RemoteApiClient(client, new Uri("https://api.example/"));
        var exception = await Assert.ThrowsAsync<RemoteApiException>(() => api.SendAsync(HttpMethod.Get, "/records?q=private"));
        Assert.Equal(calls, transport.Calls);
        Assert.Single(transport.CorrelationIds.Distinct());
        Assert.Equal("server-operation-1", exception.Error.CorrelationId);
        Assert.DoesNotContain("private", exception.ToString());
    }

    [Fact]
    public async Task Foreign_origin_is_rejected_before_any_bearer_token_is_requested()
    {
        var transport = new Transport(HttpStatusCode.OK);
        using var client = RemoteHttp.CreateClient(transport);
        var api = new RemoteApiClient(client, new Uri("https://api.example/"));
        await Assert.ThrowsAsync<RemoteApiException>(() => api.SendAsync(HttpMethod.Get, "//evil.example/"));
        Assert.Equal(0, transport.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var client = RemoteHttp.CreateClient(new Transport(HttpStatusCode.OK));
        var api = new RemoteApiClient(client, new Uri("https://api.example/"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            api.SendAsync(HttpMethod.Get, "/records", cancellationToken: cancelled.Token));
    }

    private static RemoteAuthOptions Options() => new()
    {
        Enabled = true,
        Authority = "https://id.example",
        ClientId = "desktop",
        RedirectUri = "http://127.0.0.1:9876/callback/",
    };

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Store : ICredentialStore
    {
        public string? Secret { get; set; }
        public bool FailWrite { get; set; }
        public Task<string?> ReadAsync(string key, CancellationToken cancellationToken = default) => Task.FromResult(Secret);
        public Task WriteAsync(string key, string secret, CancellationToken cancellationToken = default)
        {
            if (FailWrite) throw new CredentialStoreException("credential.write_failed");
            Secret = secret;
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            Secret = null;
            return Task.CompletedTask;
        }
    }

    private sealed class Identity(Clock clock, Store store) : IIdentityClient
    {
        public int RefreshCalls { get; private set; }
        public bool FailLogout { get; set; }
        public Func<CancellationToken, Task>? BeforeGetUser { get; init; }
        private static ClaimsPrincipal User => new(new ClaimsIdentity(
            [new Claim("sub", "subject-1"), new Claim("role", "reader"), new Claim("permission", "records:read")], "oidc"));
        public Task<IdentityTokens> LoginAsync(CancellationToken cancellationToken) => Task.FromResult(new IdentityTokens
        {
            AccessToken = "access-initial",
            RefreshToken = "refresh-initial",
            IdentityToken = "identity-test",
            ExpiresAt = clock.Now.AddMinutes(5),
            User = User,
        });
        public Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
        {
            RefreshCalls++;
            return Task.FromResult(new IdentityTokens
            {
                AccessToken = "access-rotated",
                RefreshToken = "refresh-rotated",
                ExpiresAt = clock.Now.AddMinutes(5),
            });
        }
        public async Task<ClaimsPrincipal> GetUserAsync(string accessToken, CancellationToken cancellationToken)
        {
            Assert.Equal("refresh-rotated", store.Secret);
            if (BeforeGetUser is not null) await BeforeGetUser(cancellationToken);
            return User;
        }
        public Task LogoutAsync(string? identityToken, CancellationToken cancellationToken)
        {
            Assert.Null(store.Secret);
            if (FailLogout) throw new AuthenticationException("auth.end_session_failed");
            return Task.CompletedTask;
        }
    }

    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Entries { get; } = [];
        public void Write(AuditEvent entry) => Entries.Add(entry);
    }

    private sealed class Transport(HttpStatusCode status) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public List<string> CorrelationIds { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Calls++;
            CorrelationIds.Add(request.Headers.GetValues("X-Correlation-ID").Single());
            var response = new HttpResponseMessage(status) { Content = new StringContent("never show this body") };
            response.Headers.Add("X-Correlation-ID", "server-operation-1");
            return Task.FromResult(response);
        }
    }
}
