using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Plinth.App.Remote;

namespace Plinth.App.Services;

public enum UpdateCheckKind
{
    NotConfigured,
    UpToDate,
    Available,
    Failed,
}

/// <summary>检查更新的结果。Available 时 Version 和 Url 有值。</summary>
public sealed record UpdateCheckResult(UpdateCheckKind Kind, string Version = "", string Url = "", string Notes = "")
{
    public RemoteApiError? Error { get; init; }
}

/// <summary>
/// 检查有没有新版本:只读一个 JSON 文件,比较版本号,不下载、不安装。
/// 地址写在 settings.json 的 UpdateFeedUrl 里,没写就是功能关闭,程序不会自己联网。
/// 只在用户点「检查更新」时才请求一次,不会定时或启动时偷偷检查。
/// 文件格式:{ "version": "1.2.0", "url": "https://.../Plinth-1.2.0-setup.exe", "notes": "更新说明" }
/// </summary>
public sealed class UpdateCheckService(SettingsService settings, ILogger<UpdateCheckService> logger, HttpMessageHandler? handler = null)
{
    private const int MaxFeedBytes = 64 * 1024;

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        var feed = settings.Current.UpdateFeedUrl;
        if (string.IsNullOrWhiteSpace(feed))
        {
            return new UpdateCheckResult(UpdateCheckKind.NotConfigured);
        }

        // 只接受 https:更新信息被中间人改掉的话,用户可能被引到恶意下载地址
        if (!Uri.TryCreate(feed, UriKind.Absolute, out var feedUri) || !RemoteApiClient.IsHttpsAddress(feedUri))
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed)
            {
                Error = RemoteApiClient.Failure("remote.invalid_address", Guid.NewGuid().ToString("N")).Error,
            };
        }

        try
        {
            using var client = RemoteHttp.CreateClient(handler);
            client.MaxResponseContentBufferSize = MaxFeedBytes;
            var api = new RemoteApiClient(client, new Uri(feedUri.GetLeftPart(UriPartial.Authority)));
            using var response = await api.SendAsync(HttpMethod.Get, feedUri.PathAndQuery, cancellationToken: cancellationToken);
            var result = Evaluate(await response.Content.ReadAsStringAsync(cancellationToken), AppInfo.Version);
            return result.Kind == UpdateCheckKind.Failed
                ? result with
                {
                    Error = RemoteApiClient.Failure("remote.invalid_update_feed",
                    System.Linq.Enumerable.First(response.Headers.GetValues("X-Correlation-ID"))).Error
                }
                : result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (RemoteApiException ex)
        {
            logger.LogWarning("Update check failed: {Code} {CorrelationId}", ex.Error.Code, ex.Error.CorrelationId);
            return new UpdateCheckResult(UpdateCheckKind.Failed) { Error = ex.Error };
        }
    }

    /// <summary>比较远程版本和当前版本。JSON 不合格、版本号看不懂、下载地址不是 https,都算失败。</summary>
    public static UpdateCheckResult Evaluate(string json, string currentVersion)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
            var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
            var notes = root.TryGetProperty("notes", out var n) ? n.GetString() ?? string.Empty : string.Empty;

            if (!Version.TryParse(version, out var latest) || !Version.TryParse(currentVersion, out var current))
            {
                return new UpdateCheckResult(UpdateCheckKind.Failed);
            }

            if (latest <= current)
            {
                return new UpdateCheckResult(UpdateCheckKind.UpToDate, version!);
            }

            return Uri.TryCreate(url, UriKind.Absolute, out var downloadUri) && RemoteApiClient.IsHttpsAddress(downloadUri)
                ? new UpdateCheckResult(UpdateCheckKind.Available, version!, url!, notes)
                : new UpdateCheckResult(UpdateCheckKind.Failed);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return new UpdateCheckResult(UpdateCheckKind.Failed);
        }
    }
}
