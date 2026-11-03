using System;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Microsoft.IdentityModel.Tokens;
using Plinth.App.Auth;
using Xunit;

namespace Plinth.App.Tests.Remote;

public sealed class IdentityTokenValidationTests
{
    [Theory]
    [InlineData("https://id.example", "desktop", "expected-nonce", false, false)]
    [InlineData("https://wrong.example", "desktop", "expected-nonce", false, true)]
    [InlineData("https://id.example", "other-client", "expected-nonce", false, true)]
    [InlineData("https://id.example", "desktop/", "expected-nonce", false, true)]
    [InlineData("https://id.example", "desktop", "wrong-nonce", false, true)]
    [InlineData("https://id.example", "desktop", "expected-nonce", true, true)]
    public async Task Signature_issuer_audience_nonce_and_lifetime_are_enforced(
        string issuer, string audience, string nonce, bool expired, bool isError)
    {
        using var rsa = RSA.Create(2048);
        var now = DateTime.UtcNow;
        var token = new JwtSecurityToken(issuer, audience,
            [new Claim("sub", "test-subject"), new Claim("nonce", nonce)],
            now.AddHours(-1), expired ? now.AddMinutes(-10) : now.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(rsa) { KeyId = "test" }, SecurityAlgorithms.RsaSha256));
        var result = await new SignedIdentityTokenValidator("expected-nonce")
            .ValidateAsync(new JwtSecurityTokenHandler().WriteToken(token), Options(rsa));
        Assert.Equal(isError, result.IsError);
        if (!isError) Assert.Equal("test-subject", result.User.FindFirst("sub")?.Value);
    }

    [Fact]
    public async Task Token_signed_with_an_untrusted_key_is_rejected()
    {
        using var trusted = RSA.Create(2048);
        using var attacker = RSA.Create(2048);
        var token = new JwtSecurityToken("https://id.example", "desktop",
            [new Claim("sub", "test-subject"), new Claim("nonce", "expected-nonce")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new RsaSecurityKey(attacker) { KeyId = "test" }, SecurityAlgorithms.RsaSha256));
        var result = await new SignedIdentityTokenValidator("expected-nonce")
            .ValidateAsync(new JwtSecurityTokenHandler().WriteToken(token), Options(trusted));
        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Unsigned_token_is_rejected()
    {
        using var rsa = RSA.Create(2048);
        var token = new JwtSecurityToken("https://id.example", "desktop",
            [new Claim("sub", "test-subject"), new Claim("nonce", "expected-nonce")],
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5));
        var result = await new SignedIdentityTokenValidator("expected-nonce")
            .ValidateAsync(new JwtSecurityTokenHandler().WriteToken(token), Options(rsa));
        Assert.True(result.IsError);
    }

    private static OidcClientOptions Options(RSA rsa)
    {
        var publicKey = rsa.ExportParameters(false);
        var jwks = $$"""{"keys":[{"kty":"RSA","use":"sig","kid":"test","n":"{{Base64UrlEncoder.Encode(publicKey.Modulus!)}}","e":"{{Base64UrlEncoder.Encode(publicKey.Exponent!)}}"}]}""";
        return new OidcClientOptions
        {
            Authority = "https://id.example",
            ClientId = "desktop",
            ClockSkew = TimeSpan.Zero,
            ProviderInformation = new ProviderInformation
            {
                IssuerName = "https://id.example",
                KeySet = new Duende.IdentityModel.Jwk.JsonWebKeySet(jwks),
            },
            Policy = new Policy { RequireIdentityTokenSignature = true },
        };
    }
}
