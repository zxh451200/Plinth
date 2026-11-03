using System;
using System.Net.Http;
using Microsoft.Extensions.Http.Resilience;
using Polly;

namespace Plinth.App.Remote;

/// <summary>One resilience pipeline shared by remote business, health and update clients.</summary>
public static class RemoteHttp
{
    public static HttpClient CreateClient(HttpMessageHandler? transport = null)
    {
        var options = new HttpStandardResilienceOptions();
        options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(30);
        options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(8);
        options.Retry.MaxRetryAttempts = 2;
        options.Retry.Delay = TimeSpan.FromMilliseconds(300);
        options.Retry.MaxDelay = TimeSpan.FromSeconds(5);
        options.Retry.UseJitter = true;
        // GET/HEAD only. In particular no replay of POST/PATCH, nor implicit retries after 401.
        var transient = options.Retry.ShouldHandle;
        options.Retry.ShouldHandle = args =>
        {
            var method = args.Context.GetRequestMessage()?.Method;
            return method == HttpMethod.Get || method == HttpMethod.Head
                ? transient(args) : System.Threading.Tasks.ValueTask.FromResult(false);
        };
        var pipeline = new ResiliencePipelineBuilder<HttpResponseMessage>()
            .AddTimeout(options.TotalRequestTimeout)
            .AddRetry(options.Retry)
            .AddTimeout(options.AttemptTimeout)
            .Build();
        var handler = new ResilienceHandler(pipeline)
        {
            InnerHandler = transport is null
                ? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }
                : new BorrowedHandler(transport),
        };
        return new HttpClient(handler)
        {
            Timeout = System.Threading.Timeout.InfiniteTimeSpan,
            MaxResponseContentBufferSize = 1024 * 1024,
        };
    }

    // Preserve the existing test seam: disposing a client must not dispose a caller-owned fake transport.
    private sealed class BorrowedHandler(HttpMessageHandler transport) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker _invoker = new(transport, disposeHandler: false);
        protected override System.Threading.Tasks.Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, System.Threading.CancellationToken cancellationToken) =>
            _invoker.SendAsync(request, cancellationToken);
        protected override void Dispose(bool disposing)
        {
            if (disposing) _invoker.Dispose();
            base.Dispose(disposing);
        }
    }
}
