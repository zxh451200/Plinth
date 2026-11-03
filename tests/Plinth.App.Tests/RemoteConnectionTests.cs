using System.Net;
using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class RemoteConnectionTests
{
    private sealed class FakeHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? RequestedUrl { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestedUrl = request.RequestUri?.ToString();
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    [Fact]
    public void Health_uri_requires_https_and_keeps_the_configured_path()
    {
        var options = new RemoteOptions { Enabled = true, BaseUrl = "https://api.example.com/", HealthPath = "/status" };

        Assert.True(RemoteConnectionService.TryBuildHealthUri(options, out var uri));
        Assert.Equal("https://api.example.com/status", uri.ToString());
    }

    [Theory]
    [InlineData("http://api.example.com", "/health")]
    [InlineData("api.example.com", "/health")]
    [InlineData("https://api.example.com", "//other.example.com")]
    public void Health_uri_rejects_unsafe_or_invalid_addresses(string baseUrl, string path = "/health")
    {
        var options = new RemoteOptions { Enabled = true, BaseUrl = baseUrl, HealthPath = path };

        Assert.False(RemoteConnectionService.TryBuildHealthUri(options, out _));
    }

    [Fact]
    public async Task Disabled_remote_mode_does_not_make_a_request()
    {
        var handler = new FakeHandler(HttpStatusCode.OK);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote.Enabled = false;

        var result = await new RemoteConnectionService(
            settings,
            NullLogger<RemoteConnectionService>.Instance,
            handler).CheckAsync();

        Assert.Equal(RemoteConnectionState.Disabled, result.State);
        Assert.Null(handler.RequestedUrl);
    }

    [Fact]
    public async Task Successful_health_check_reports_connected()
    {
        var handler = new FakeHandler(HttpStatusCode.NoContent);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote = new RemoteOptions
        {
            Enabled = true,
            BaseUrl = "https://api.example.com",
            HealthPath = "/health",
        };

        var result = await new RemoteConnectionService(
            settings,
            NullLogger<RemoteConnectionService>.Instance,
            handler).CheckAsync();

        Assert.Equal(RemoteConnectionState.Connected, result.State);
        Assert.Equal("https://api.example.com/health", handler.RequestedUrl);
    }

    [Fact]
    public async Task Cached_health_is_invalidated_when_configuration_changes()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote = new() { Enabled = true, BaseUrl = "https://api.example.com" };
        var handler = new FakeHandler(HttpStatusCode.OK);
        var remote = new RemoteConnectionService(settings, NullLogger<RemoteConnectionService>.Instance, handler);
        Assert.Null(remote.LastHealthCheck);
        Assert.Null(handler.RequestedUrl);
        await remote.CheckAsync();
        Assert.Equal(RemoteConnectionState.Connected, remote.LastHealthCheck!.State);
        Assert.Equal(TimeSpan.Zero, remote.LastHealthCheck.CheckedAt.Offset);
        var previous = remote.LastHealthCheck;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => remote.CheckAsync(cancellation.Token));
        Assert.Equal(previous, remote.LastHealthCheck);
        settings.Current.Remote.HealthPath = "/another-health";
        Assert.Null(remote.LastHealthCheck);
    }

    [Fact]
    public async Task Late_health_response_does_not_replace_newer_result_or_another_configuration()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote = new() { Enabled = true, BaseUrl = "https://api.example.com" };
        using var handler = new DelayedFirstHandler();
        var remote = new RemoteConnectionService(settings, NullLogger<RemoteConnectionService>.Instance, handler);
        var first = remote.CheckAsync();
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(RemoteConnectionState.Failed, (await remote.CheckAsync()).State);
            handler.Release.TrySetResult();
            Assert.Equal(RemoteConnectionState.Connected, (await first).State);
            Assert.Equal(RemoteConnectionState.Failed, remote.LastHealthCheck!.State);
            settings.Current.Remote.BaseUrl = "https://other.example.com";
            Assert.Null(remote.LastHealthCheck);
        }
        finally
        {
            handler.Release.TrySetResult();
            await first;
        }
    }

    [Fact]
    public async Task Response_for_an_address_changed_during_check_is_not_published()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote = new() { Enabled = true, BaseUrl = "https://api.example.com" };
        using var handler = new DelayedFirstHandler();
        var remote = new RemoteConnectionService(settings, NullLogger<RemoteConnectionService>.Instance, handler);
        var check = remote.CheckAsync();
        try
        {
            await handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            settings.Current.Remote.BaseUrl = "https://other.example.com";
            handler.Release.TrySetResult();
            await check;
            Assert.Null(remote.LastHealthCheck);
        }
        finally
        {
            handler.Release.TrySetResult();
            await check;
        }
    }

    private sealed class DelayedFirstHandler : HttpMessageHandler
    {
        private int _requests;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _requests) != 1) return new(HttpStatusCode.Forbidden);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK);
        }
    }
}
