using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>检查更新:版本比较、地址限制、没配置时不联网。用假的 HTTP 处理器,测试不访问真实网络。</summary>
public sealed class UpdateCheckTests
{
    private sealed class FakeHandler(string body, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private static UpdateCheckService Create(string feedUrl, HttpMessageHandler handler)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.UpdateFeedUrl = feedUrl;
        return new UpdateCheckService(settings, NullLogger<UpdateCheckService>.Instance, handler);
    }

    [Theory]
    [InlineData("1.2.0", "1.1.0", UpdateCheckKind.Available)]
    [InlineData("1.1.0", "1.1.0", UpdateCheckKind.UpToDate)]
    [InlineData("1.0.9", "1.1.0", UpdateCheckKind.UpToDate)]
    [InlineData("abc", "1.1.0", UpdateCheckKind.Failed)]
    public void Evaluate_compares_versions(string remote, string current, UpdateCheckKind expected)
    {
        var json = $$"""{"version":"{{remote}}","url":"https://example.com/setup.exe","notes":"n"}""";

        Assert.Equal(expected, UpdateCheckService.Evaluate(json, current).Kind);
    }

    [Theory]
    [InlineData("""{"version":"2.0.0","url":"http://example.com/setup.exe"}""")]
    [InlineData("""{"version":"2.0.0"}""")]
    [InlineData("not json")]
    public void Evaluate_rejects_feeds_with_a_bad_download_link_or_bad_json(string json) =>
        Assert.Equal(UpdateCheckKind.Failed, UpdateCheckService.Evaluate(json, "1.0.0").Kind);

    [Fact]
    public async Task Without_a_feed_url_nothing_is_requested()
    {
        var handler = new FakeHandler("{}");

        var result = await Create("", handler).CheckAsync();

        Assert.Equal(UpdateCheckKind.NotConfigured, result.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_non_https_feed_url_is_refused_without_a_request()
    {
        var handler = new FakeHandler("{}");

        var result = await Create("http://example.com/feed.json", handler).CheckAsync();

        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task A_newer_version_in_the_feed_is_reported()
    {
        var handler = new FakeHandler("""{"version":"99.0.0","url":"https://example.com/setup.exe","notes":"fixes"}""");

        var result = await Create("https://example.com/feed.json", handler).CheckAsync();

        Assert.Equal(UpdateCheckKind.Available, result.Kind);
        Assert.Equal("99.0.0", result.Version);
        Assert.Equal("fixes", result.Notes);
    }

    [Fact]
    public async Task A_server_error_is_a_failed_check_not_an_exception()
    {
        var result = await Create("https://example.com/feed.json", new FakeHandler("oops", HttpStatusCode.InternalServerError)).CheckAsync();

        Assert.Equal(UpdateCheckKind.Failed, result.Kind);
    }
}
