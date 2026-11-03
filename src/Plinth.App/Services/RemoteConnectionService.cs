using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Plinth.App.Remote;
using Plinth.App.Auth;
using Plinth.App.Security;

namespace Plinth.App.Services;

public sealed class RemoteOptions
{
    public bool Enabled { get; set; }
    public string BaseUrl { get; set; } = string.Empty;
    public string HealthPath { get; set; } = "/health";
}

public enum RemoteConnectionState
{
    Disabled,
    Connected,
    Failed,
    Invalid,
}

public sealed record RemoteConnectionResult(RemoteConnectionState State, string Detail = "")
{
    public RemoteApiError? Error { get; init; }
}

public sealed record RemoteHealthSnapshot(RemoteConnectionState State, DateTimeOffset CheckedAt);

/// <summary>
/// 远程模式的通用连接层。只负责地址校验和健康检查,不假设业务 API 的格式。
/// 业务模块通过它拿到已校验的 BaseUrl 后,再注册自己的 HttpClient 和接口。
/// </summary>
public sealed class RemoteConnectionService(
    SettingsService settings,
    ILogger<RemoteConnectionService> logger,
    HttpMessageHandler? handler = null,
    IAccessTokenProvider? tokens = null,
    ISecurityAudit? audit = null,
    IRemoteUserContext? users = null)
{
    private readonly object _healthGate = new();
    private long _checkSequence;
    private RemoteHealthSnapshot? _lastHealth;
    private RemoteOptions? _checkedOptions;
    public RemoteOptions Current => settings.Current.Remote;

    /// <summary>Only cached local state; reading this property never sends a request.</summary>
    public RemoteHealthSnapshot? LastHealthCheck
    {
        get
        {
            lock (_healthGate)
                return SameOptions(_checkedOptions, Current) ? _lastHealth : null;
        }
    }

    public async Task<RemoteConnectionResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var sequence = Interlocked.Increment(ref _checkSequence);
        var current = Current;
        var options = new RemoteOptions
        {
            Enabled = current.Enabled,
            BaseUrl = current.BaseUrl,
            HealthPath = current.HealthPath,
        };
        var result = await CheckCoreAsync(options, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_healthGate)
        {
            // A late response must not replace a newer check or describe another address.
            if (sequence == Volatile.Read(ref _checkSequence) && SameOptions(options, Current))
            {
                _checkedOptions = options;
                _lastHealth = new(result.State, DateTimeOffset.UtcNow);
            }
        }
        return result;
    }

    private async Task<RemoteConnectionResult> CheckCoreAsync(RemoteOptions options, CancellationToken cancellationToken)
    {
        if (!options.Enabled)
        {
            return new RemoteConnectionResult(RemoteConnectionState.Disabled);
        }

        if (!TryBuildHealthUri(options, out var uri))
        {
            return new RemoteConnectionResult(RemoteConnectionState.Invalid)
            {
                Error = RemoteApiClient.Failure("remote.invalid_address", Guid.NewGuid().ToString("N")).Error,
            };
        }

        try
        {
            using var client = RemoteHttp.CreateClient(handler);
            var api = new RemoteApiClient(client, new Uri(uri.GetLeftPart(UriPartial.Authority)), tokens, audit, users);
            using var response = await api.SendAsync(HttpMethod.Get, uri.PathAndQuery, cancellationToken: cancellationToken);
            return new RemoteConnectionResult(RemoteConnectionState.Connected);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteApiException ex)
        {
            logger.LogWarning("Remote health check failed: {Code} {CorrelationId}",
                ex.Error.Code, ex.Error.CorrelationId);
            return new RemoteConnectionResult(RemoteConnectionState.Failed, ex.Error.Code) { Error = ex.Error };
        }
    }

    private static bool SameOptions(RemoteOptions? first, RemoteOptions second) =>
        first is not null && first.Enabled == second.Enabled
        && string.Equals(first.BaseUrl, second.BaseUrl, StringComparison.Ordinal)
        && string.Equals(first.HealthPath, second.HealthPath, StringComparison.Ordinal);

    public static bool TryBuildHealthUri(RemoteOptions options, out Uri uri)
    {
        uri = null!;
        if (!options.Enabled
            || !Uri.TryCreate(options.BaseUrl?.Trim(), UriKind.Absolute, out var baseUri)
            || baseUri.Scheme != Uri.UriSchemeHttps
            || baseUri.UserInfo.Length > 0
            || baseUri.Query.Length > 0
            || baseUri.Fragment.Length > 0
            || !Uri.TryCreate(options.HealthPath?.Trim(), UriKind.Relative, out var path)
            || path.ToString().StartsWith("//", StringComparison.Ordinal))
        {
            return false;
        }

        uri = new Uri(baseUri, path);
        return uri.Scheme == Uri.UriSchemeHttps
            && uri.Authority == baseUri.Authority
            && uri.UserInfo.Length == 0
            && uri.Fragment.Length == 0;
    }
}
