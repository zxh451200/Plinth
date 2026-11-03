using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Auth;
using Plinth.App.Security;
using Polly.Timeout;

namespace Plinth.App.Remote;

/// <summary>Resource keys, never server exception text. The host translates these keys for UI.</summary>
public sealed record RemoteApiError(string Code, string MessageKey, string SuggestionKey,
    bool CanRetry, string CorrelationId, int? StatusCode = null);

public sealed class RemoteApiException(RemoteApiError error) : Exception(error.Code)
{
    public RemoteApiError Error { get; } = error;
}

/// <summary>Use one instance per configured API origin. Inject an anonymous instance for public endpoints.</summary>
public sealed class RemoteApiClient(
    HttpClient client,
    Uri baseUri,
    IAccessTokenProvider? tokens = null,
    ISecurityAudit? audit = null,
    IRemoteUserContext? users = null)
{
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path,
        HttpContent? content = null, CancellationToken cancellationToken = default, string? operationCorrelationId = null,
        int? expectedVersion = null)
    {
        var correlationId = IsSafeCorrelationId(operationCorrelationId)
            ? operationCorrelationId! : Guid.NewGuid().ToString("N");
        if (!IsHttpsAddress(baseUri) || !Uri.TryCreate(path, UriKind.Relative, out var relative)
            || path.Contains('\\') || path.StartsWith("//", StringComparison.Ordinal))
            throw Failure("remote.invalid_address", correlationId);
        var uri = new Uri(baseUri, relative);
        if (!IsHttpsAddress(uri) || uri.Authority != baseUri.Authority || uri.Fragment.Length != 0)
            throw Failure("remote.invalid_address", correlationId);
        using var request = new HttpRequestMessage(method, uri) { Content = content };
        if (expectedVersion is not null)
        {
            if (expectedVersion <= 0 || method == HttpMethod.Get || method == HttpMethod.Head)
                throw new ArgumentOutOfRangeException(nameof(expectedVersion));
            request.Headers.IfMatch.Add(new EntityTagHeaderValue(
                "\"" + expectedVersion.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) + "\""));
        }
        request.Headers.Add("X-Correlation-ID", correlationId);
        var actor = users?.CurrentUser.Subject ?? "";
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(TimeSpan.FromSeconds(30));
            if (tokens is not null)
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer",
                    await tokens.GetAccessTokenAsync(deadline.Token));
                actor = users?.CurrentUser.Subject ?? "";
            }
            // Buffering is bounded; the outer deadline also covers body reads.
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, deadline.Token);
            var serverId = response.Headers.TryGetValues("X-Correlation-ID", out var values)
                ? values.FirstOrDefault() : null;
            if (IsSafeCorrelationId(serverId))
                correlationId = serverId!;
            if (!response.IsSuccessStatusCode)
            {
                using var failedResponse = response;
                var status = response.StatusCode;
                // Only the protocol correlation field is read; never display/log arbitrary server bodies.
                if (response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true)
                {
                    try
                    {
                        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(deadline.Token));
                        if (json.RootElement.ValueKind == JsonValueKind.Object
                            && json.RootElement.TryGetProperty("correlationId", out var id)
                            && id.ValueKind == JsonValueKind.String && IsSafeCorrelationId(id.GetString()))
                            correlationId = id.GetString()!;
                    }
                    catch (JsonException) { }
                }
                throw FromStatus(status, correlationId, method == HttpMethod.Get || method == HttpMethod.Head);
            }
            response.Headers.Remove("X-Correlation-ID");
            response.Headers.Add("X-Correlation-ID", correlationId);
            WriteAudit(method, actor, "success", correlationId);
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            WriteAudit(method, actor, "cancelled", correlationId);
            throw;
        }
        catch (RemoteApiException)
        {
            WriteAudit(method, actor, "failed", correlationId);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or TimeoutRejectedException
                                   or IOException or AuthenticationException or CredentialStoreException)
        {
            WriteAudit(method, actor, "failed", correlationId);
            throw Failure(ex switch
            {
                AuthenticationException => "remote.authentication_required",
                CredentialStoreException => "remote.credential_unavailable",
                OperationCanceledException or TimeoutRejectedException => "remote.timeout",
                _ => "remote.network_error",
            }, correlationId, canRetry: ex is not AuthenticationException and not CredentialStoreException
                && (method == HttpMethod.Get || method == HttpMethod.Head));
        }
    }

    public static bool IsHttpsAddress(Uri uri) => uri.IsAbsoluteUri
        && uri.Scheme == Uri.UriSchemeHttps && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;

    public static RemoteApiException Failure(string code, string correlationId, bool canRetry = false, int? status = null) =>
        new(new RemoteApiError(code, code + ".message",
            code switch
            {
                "remote.unauthorized" or "remote.authentication_required" => "auth.suggestion.login",
                "remote.forbidden" => "security.suggestion.request_access",
                "remote.conflict" => "remote.suggestion.reload",
                "remote.credential_unavailable" => "credential.suggestion.unlock",
                "remote.network_error" or "remote.timeout" when !canRetry => "remote.suggestion.verify_result",
                _ => canRetry ? "remote.suggestion.retry" : "remote.suggestion.check_configuration",
            },
            canRetry, correlationId, status));

    private static RemoteApiException FromStatus(HttpStatusCode status, string correlationId, bool safeToRetry) =>
        Failure(status switch
        {
            HttpStatusCode.Unauthorized => "remote.unauthorized",
            HttpStatusCode.Forbidden => "remote.forbidden",
            HttpStatusCode.NotFound => "remote.not_found",
            HttpStatusCode.Conflict or HttpStatusCode.PreconditionFailed => "remote.conflict",
            HttpStatusCode.TooManyRequests => "remote.rate_limited",
            _ => "remote.http_error",
        }, correlationId,
            safeToRetry && status is (HttpStatusCode.RequestTimeout or HttpStatusCode.TooManyRequests
                or HttpStatusCode.InternalServerError or HttpStatusCode.BadGateway
                or HttpStatusCode.ServiceUnavailable or HttpStatusCode.GatewayTimeout), (int)status);

    private static bool IsSafeCorrelationId(string? value) =>
        Plinth.App.Services.DiagnosticRedactor.IsSafeCorrelationId(value);

    private void WriteAudit(HttpMethod method, string actor, string result, string correlationId) =>
        audit?.Write(new AuditEvent(DateTimeOffset.UtcNow, actor, "remote_api", method.Method, result, correlationId));
}
