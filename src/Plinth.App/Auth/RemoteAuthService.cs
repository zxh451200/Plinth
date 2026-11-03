using System;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Security;

namespace Plinth.App.Auth;

public interface IAccessTokenProvider
{
    Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default);
}

/// <summary>Singleton session. Serializes login/refresh/logout and saves rotated refresh tokens before publishing them.</summary>
/// <summary>登录／注销边界通知;身份关闭时由空实现代替,避免persistent consumers构造真实客户端。</summary>
public interface IIdentitySessionEvents
{
    event EventHandler? SessionChanged;
}

public sealed class RemoteAuthService : IIdentitySessionEvents, IAccessTokenProvider, IRemoteUserContext
{
    private readonly RemoteAuthOptions _options;
    private readonly IIdentityClient _identity;
    private readonly ICredentialStore _credentials;
    private readonly ISecurityAudit _audit;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private IdentityTokens? _tokens;
    private RemoteUserContext _user = RemoteUserContext.Anonymous;
    private long _sessionVersion;
    private bool _allowStoredRefresh = true;

    public RemoteAuthService(RemoteAuthOptions options, IIdentityClient identity,
        ICredentialStore credentials, ISecurityAudit audit, TimeProvider? timeProvider = null)
    {
        options.Validate();
        // A deployment change must construct a new session, not mix an old token with a new authority.
        _options = new RemoteAuthOptions
        {
            Enabled = options.Enabled,
            Authority = options.Authority,
            ClientId = options.ClientId,
            Scope = options.Scope,
            RedirectUri = options.RedirectUri,
            RoleClaimType = options.RoleClaimType,
            PermissionClaimType = options.PermissionClaimType,
            RefreshSkew = options.RefreshSkew,
            BrowserTimeout = options.BrowserTimeout,
        };
        _identity = identity;
        _credentials = credentials;
        _audit = audit;
        _time = timeProvider ?? TimeProvider.System;
    }

    // Never expose the ClaimsPrincipal or token object to UI state.
    public RemoteUserContext CurrentUser => Volatile.Read(ref _user);
    public long SessionVersion => Interlocked.Read(ref _sessionVersion);

    /// <summary>Identity was cleared or published. Handlers must not block; UI subscribers should post to their dispatcher.</summary>
    public event EventHandler? SessionChanged;

    public async Task LoginAsync(CancellationToken cancellationToken = default)
    {
        var correlationId = Guid.NewGuid().ToString("N");
        await _gate.WaitAsync(cancellationToken);
        try
        {
            // An attempted account switch must not leave the previous user's session active on failure.
            ClearMemory();
            _allowStoredRefresh = false;
            await _credentials.DeleteAsync(_options.CredentialKey, cancellationToken);
            var tokens = await _identity.LoginAsync(cancellationToken);
            if (RemoteUserContext.FromPrincipal(tokens.User, _options.RoleClaimType, _options.PermissionClaimType)
                is not { IsAuthenticated: true })
                throw new AuthenticationException("auth.subject_missing");
            await CommitAsync(tokens, cancellationToken);
            _allowStoredRefresh = true;
            WriteAudit("login", "success", correlationId);
        }
        catch
        {
            ClearMemory();
            WriteAudit("login", "failed", correlationId);
            throw;
        }
        finally { _gate.Release(); }
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var correlationId = Guid.NewGuid().ToString("N");
        var previousSubject = CurrentUser.Subject;
        var refreshStarted = false;
        var rotationSaved = false;
        try
        {
            if (_tokens is { } current && current.ExpiresAt > _time.GetUtcNow() + _options.RefreshSkew)
                return current.AccessToken;
            var refreshToken = _tokens?.RefreshToken;
            if (string.IsNullOrEmpty(refreshToken) && _allowStoredRefresh)
                refreshToken = await _credentials.ReadAsync(_options.CredentialKey, cancellationToken);
            if (string.IsNullOrEmpty(refreshToken))
                throw new AuthenticationException("auth.login_required", true);

            // No automatic retry: a lost response may have already consumed a rotating refresh token.
            refreshStarted = true;
            _allowStoredRefresh = false;
            var refreshed = await _identity.RefreshAsync(refreshToken, cancellationToken);
            var tokens = new IdentityTokens
            {
                AccessToken = refreshed.AccessToken,
                RefreshToken = string.IsNullOrEmpty(refreshed.RefreshToken) ? refreshToken : refreshed.RefreshToken,
                IdentityToken = refreshed.IdentityToken ?? _tokens?.IdentityToken,
                ExpiresAt = refreshed.ExpiresAt,
                User = null,
            };
            // Persist rotation before making any further network calls (including UserInfo).
            await CommitAsync(tokens, cancellationToken, publishUser: false);
            rotationSaved = true;
            _allowStoredRefresh = true;
            var principal = await _identity.GetUserAsync(tokens.AccessToken, cancellationToken);
            var user = RemoteUserContext.FromPrincipal(principal, _options.RoleClaimType, _options.PermissionClaimType);
            if (!user.IsAuthenticated)
                throw new AuthenticationException("auth.subject_missing", true);
            if (previousSubject.Length != 0 && user.Subject != previousSubject)
                throw new AuthenticationException("auth.subject_changed", true);
            PublishUser(user);
            WriteAudit("refresh", "success", correlationId);
            return tokens.AccessToken;
        }
        catch (AuthenticationException ex) when (ex.RequiresLogin)
        {
            ClearMemory();
            _allowStoredRefresh = false;
            WriteAudit("refresh", "login_required", correlationId, previousSubject);
            await _credentials.DeleteAsync(_options.CredentialKey, CancellationToken.None);
            throw;
        }
        catch
        {
            ClearMemory();
            WriteAudit("refresh", "failed", correlationId, previousSubject);
            if (refreshStarted && !rotationSaved)
            {
                // Do not replay an old rotating token after an ambiguous refresh or storage failure.
                _allowStoredRefresh = false;
                await _credentials.DeleteAsync(_options.CredentialKey, CancellationToken.None);
            }
            throw;
        }
        finally { _gate.Release(); }
    }

    /// <summary>Local logout always happens first; a failed provider logout is reported to the caller.</summary>
    public async Task LogoutAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        var correlationId = Guid.NewGuid().ToString("N");
        var actor = CurrentUser.Subject;
        try
        {
            var identityToken = _tokens?.IdentityToken;
            ClearMemory();
            _allowStoredRefresh = false;
            // Once logout begins, caller cancellation cannot leave a saved refresh token behind.
            await _credentials.DeleteAsync(_options.CredentialKey, CancellationToken.None);
            await _identity.LogoutAsync(identityToken, cancellationToken);
            _audit.Write(new AuditEvent(_time.GetUtcNow(), actor, "authentication", "logout", "success", correlationId));
        }
        catch
        {
            _audit.Write(new AuditEvent(_time.GetUtcNow(), actor, "authentication", "logout", "failed", correlationId));
            throw;
        }
        finally { _gate.Release(); }
    }

    private async Task CommitAsync(IdentityTokens tokens, CancellationToken cancellationToken, bool publishUser = true)
    {
        if (string.IsNullOrWhiteSpace(tokens.AccessToken) || tokens.ExpiresAt <= _time.GetUtcNow())
            throw new AuthenticationException("auth.invalid_token_response", true);
        if (string.IsNullOrEmpty(tokens.RefreshToken))
            await _credentials.DeleteAsync(_options.CredentialKey, cancellationToken);
        else
            await _credentials.WriteAsync(_options.CredentialKey, tokens.RefreshToken, cancellationToken);
        _tokens = tokens;
        // 刷新先保存轮换凭据；UserInfo 验证完成前保留当前身份，失败由调用方清理会话。
        if (publishUser)
            PublishUser(RemoteUserContext.FromPrincipal(tokens.User, _options.RoleClaimType, _options.PermissionClaimType));
    }

    private void ClearMemory()
    {
        Interlocked.Increment(ref _sessionVersion);
        _tokens = null;
        PublishUser(RemoteUserContext.Anonymous);
    }

    private void PublishUser(RemoteUserContext user)
    {
        Volatile.Write(ref _user, user);
        var handlers = SessionChanged;
        if (handlers is null) return;
        foreach (EventHandler handler in handlers.GetInvocationList())
        {
            // An observer must neither fail authentication nor prevent other observers from updating.
            try { handler(this, EventArgs.Empty); }
            catch (Exception) { }
        }
    }

    private void WriteAudit(string operation, string result, string correlationId, string? actor = null) =>
        _audit.Write(new AuditEvent(_time.GetUtcNow(), actor ?? CurrentUser.Subject,
            "authentication", operation, result, correlationId));
}
