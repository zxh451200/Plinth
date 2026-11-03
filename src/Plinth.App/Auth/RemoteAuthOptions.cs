using System;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace Plinth.App.Auth;

/// <summary>Non-secret configuration. Bind from the deployment configuration, never from token storage.</summary>
public sealed class RemoteAuthOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Scope { get; set; } = "openid profile offline_access";
    public string RedirectUri { get; set; } = "";
    public string RoleClaimType { get; set; } = "role";
    public string PermissionClaimType { get; set; } = "permission";
    public TimeSpan RefreshSkew { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan BrowserTimeout { get; set; } = TimeSpan.FromMinutes(3);

    public void Validate()
    {
        if (!Enabled)
            throw new InvalidOperationException("auth.disabled");
        if (!Uri.TryCreate(Authority, UriKind.Absolute, out var authority)
            || authority.Scheme != Uri.UriSchemeHttps || authority.UserInfo.Length != 0
            || authority.Query.Length != 0 || authority.Fragment.Length != 0
            || string.IsNullOrWhiteSpace(ClientId)
            || !Scope.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains("openid", StringComparer.Ordinal)
            || string.IsNullOrWhiteSpace(RoleClaimType) || string.IsNullOrWhiteSpace(PermissionClaimType)
            || RefreshSkew < TimeSpan.Zero || RefreshSkew > TimeSpan.FromMinutes(5)
            || BrowserTimeout < TimeSpan.FromSeconds(10) || BrowserTimeout > TimeSpan.FromMinutes(10))
            throw new InvalidOperationException("auth.invalid_configuration");
        ValidateRedirect(RedirectUri);
    }

    internal static Uri ValidateRedirect(string value)
    {
        // An explicit IPv4 literal avoids DNS resolution, wildcard bindings and IPv4/IPv6 ambiguity.
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttp || uri.Host != "127.0.0.1"
            || uri.IsDefaultPort || uri.Port < 1024 || uri.UserInfo.Length != 0
            || uri.Query.Length != 0 || uri.Fragment.Length != 0 || !uri.AbsolutePath.EndsWith('/'))
            throw new InvalidOperationException("auth.invalid_redirect_uri");
        return uri;
    }

    internal string CredentialKey =>
        "Plinth/OIDC/" + Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes($"{Authority.TrimEnd('/')}\n{ClientId}\n{Scope}")));
}
