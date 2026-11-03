using System;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.Client;
using System.Net.Http;

namespace Plinth.App.Auth;

// These objects deliberately do not generate record ToString() methods containing tokens.
public sealed class IdentityTokens
{
    public required string AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? IdentityToken { get; init; }
    public required DateTimeOffset ExpiresAt { get; init; }
    public ClaimsPrincipal? User { get; init; }
}

public sealed class AuthenticationException(string code, bool requiresLogin = false) : Exception(code)
{
    public string Code { get; } = code;
    public bool RequiresLogin { get; } = requiresLogin;
}

/// <summary>Inject a test implementation to test session behavior; production uses only Duende.</summary>
public interface IIdentityClient
{
    Task<IdentityTokens> LoginAsync(CancellationToken cancellationToken);
    Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken);
    Task<ClaimsPrincipal> GetUserAsync(string accessToken, CancellationToken cancellationToken);
    Task LogoutAsync(string? identityToken, CancellationToken cancellationToken);
}

public sealed class OidcIdentityClient : IIdentityClient
{
    private readonly OidcClient _client;
    private readonly string _roleClaimType;
    private readonly SignedIdentityTokenValidator _validator = new();

    public OidcIdentityClient(RemoteAuthOptions options, SystemBrowser browser)
    {
        options.Validate();
        _roleClaimType = options.RoleClaimType;
        _client = new OidcClient(new OidcClientOptions
        {
            Authority = options.Authority,
            ClientId = options.ClientId,
            Scope = options.Scope,
            RedirectUri = options.RedirectUri,
            PostLogoutRedirectUri = options.RedirectUri,
            Browser = browser,
            LoadProfile = true,
            IdentityTokenValidator = _validator,
            Policy = new Policy { RequireIdentityTokenSignature = true, ValidateTokenIssuerName = true },
            RefreshDiscoveryOnSignatureFailure = true,
            BackchannelTimeout = TimeSpan.FromSeconds(15),
            BackchannelHandler = new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false },
            // Discovery/issuer and S256 PKCE remain enabled; nonce/signatures are explicitly enforced.
            // Do not attach the application logger: library diagnostic output can contain token/claim data.
        });
    }

    public async Task<IdentityTokens> LoginAsync(CancellationToken cancellationToken)
    {
        _validator.ExpectedNonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        try
        {
            var result = await _client.LoginAsync(new LoginRequest
            {
                FrontChannelExtraParameters = new Parameters { { "nonce", _validator.ExpectedNonce } },
            }, cancellationToken);
            if (result.IsError || string.IsNullOrWhiteSpace(result.IdentityToken))
                throw new AuthenticationException("auth.login_failed");
            return new IdentityTokens
            {
                AccessToken = result.AccessToken,
                RefreshToken = result.RefreshToken,
                IdentityToken = result.IdentityToken,
                ExpiresAt = result.AccessTokenExpiration,
                User = result.User,
            };
        }
        finally { _validator.ExpectedNonce = null; }
    }

    public async Task<IdentityTokens> RefreshAsync(string refreshToken, CancellationToken cancellationToken)
    {
        var result = await _client.RefreshTokenAsync(refreshToken, cancellationToken: cancellationToken);
        if (result.IsError)
            throw new AuthenticationException("auth.refresh_failed", result.Error == "invalid_grant");
        return new IdentityTokens
        {
            AccessToken = result.AccessToken,
            RefreshToken = result.RefreshToken,
            IdentityToken = result.IdentityToken,
            ExpiresAt = result.AccessTokenExpiration,
            // RefreshTokenResult does not expose a newly validated principal. Do not decode an unsigned JWT.
        };
    }

    public async Task LogoutAsync(string? identityToken, CancellationToken cancellationToken)
    {
        var state = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var result = await _client.LogoutAsync(new LogoutRequest { IdTokenHint = identityToken, State = state }, cancellationToken);
        if (result.IsError)
            throw new AuthenticationException("auth.end_session_failed");
        if (!Uri.TryCreate(result.Response, UriKind.Absolute, out var callback))
            throw new AuthenticationException("auth.end_session_invalid_state");
        var states = callback.Query.TrimStart('?').Split('&')
            .Select(p => p.Split('=', 2)).Where(p => p.Length == 2 && p[0] == "state")
            .Select(p => Uri.UnescapeDataString(p[1])).ToArray();
        if (states.Length != 1 || states[0] != state)
            throw new AuthenticationException("auth.end_session_invalid_state");
    }

    public async Task<ClaimsPrincipal> GetUserAsync(string accessToken, CancellationToken cancellationToken)
    {
        var result = await _client.GetUserInfoAsync(accessToken, cancellationToken);
        if (result.IsError)
            throw new AuthenticationException("auth.userinfo_failed");
        // Claims come from the discovered HTTPS UserInfo endpoint, never from decoding the access token.
        return new ClaimsPrincipal(new ClaimsIdentity(result.Claims, "oidc", "name", _roleClaimType));
    }
}
