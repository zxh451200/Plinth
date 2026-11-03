using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Results;
using Microsoft.IdentityModel.Tokens;

namespace Plinth.App.Auth;

/// <summary>
/// Duende 7 defaults to NoValidationIdentityTokenValidator for code flow.
/// This adapter delegates cryptographic validation to Microsoft's maintained JWT library.
/// </summary>
public sealed class SignedIdentityTokenValidator : IIdentityTokenValidator
{
    public SignedIdentityTokenValidator(string? expectedNonce = null) => ExpectedNonce = expectedNonce;

    // The containing session serializes OIDC operations. A nonce is required only for interactive login.
    internal string? ExpectedNonce { get; set; }

    public Task<IdentityTokenValidationResult> ValidateAsync(string identityToken, OidcClientOptions options,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            var keys = new JsonWebKeySet(JsonSerializer.Serialize(options.ProviderInformation.KeySet));
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false, MaximumTokenSizeInBytes = 64 * 1024 };
            var user = handler.ValidateToken(identityToken, new TokenValidationParameters
            {
                RequireSignedTokens = true,
                ValidateIssuerSigningKey = true,
                IssuerSigningKeys = keys.GetSigningKeys(),
                ValidAlgorithms = options.Policy.ValidSignatureAlgorithms,
                ValidateIssuer = true,
                ValidIssuer = options.ProviderInformation.IssuerName,
                ValidateAudience = true,
                ValidAudience = options.ClientId,
                IgnoreTrailingSlashWhenValidatingAudience = false,
                ValidateLifetime = true,
                RequireExpirationTime = true,
                ClockSkew = options.ClockSkew,
                NameClaimType = "name",
                RoleClaimType = "role",
            }, out var validated);
            if (validated is not JwtSecurityToken jwt
                || string.IsNullOrWhiteSpace(user.FindFirst("sub")?.Value)
                || (ExpectedNonce is not null && user.FindFirst("nonce")?.Value != ExpectedNonce))
                return Task.FromResult(new IdentityTokenValidationResult { Error = "invalid_identity_token" });
            // OIDC requires azp when an ID token is intended for multiple audiences.
            var audiences = System.Linq.Enumerable.ToArray(jwt.Audiences);
            var azp = user.FindFirst("azp")?.Value;
            if ((audiences.Length > 1 && azp != options.ClientId) || (azp is not null && azp != options.ClientId))
                return Task.FromResult(new IdentityTokenValidationResult { Error = "invalid_authorized_party" });
            return Task.FromResult(new IdentityTokenValidationResult { User = user, SignatureAlgorithm = jwt.Header.Alg });
        }
        catch (SecurityTokenSignatureKeyNotFoundException)
        {
            return Task.FromResult(new IdentityTokenValidationResult { Error = "invalid_signature" });
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException or JsonException)
        {
            // No raw token, exception details or claims escape into application logs or UI.
            return Task.FromResult(new IdentityTokenValidationResult { Error = "invalid_identity_token" });
        }
    }
}
