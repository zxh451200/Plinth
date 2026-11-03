using System;
using System.Diagnostics;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient.Browser;

namespace Plinth.App.Auth;

public interface ISystemBrowserLauncher
{
    void Open(Uri uri);
}

public sealed class SystemBrowserLauncher : ISystemBrowserLauncher
{
    public void Open(Uri uri)
    {
        if (uri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidOperationException("auth.invalid_browser_url");
        using var process = Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }
}

/// <summary>Only transports the callback; OidcClient validates state, nonce and tokens.</summary>
public sealed class SystemBrowser(ISystemBrowserLauncher launcher, TimeSpan timeout) : IBrowser
{
    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        var callback = RemoteAuthOptions.ValidateRedirect(options.EndUrl);
        using var listener = new HttpListener();
        listener.Prefixes.Add(callback.AbsoluteUri);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            // Bind before launching the browser. A busy port is an error, never pick an unregistered redirect.
            listener.Start();
            launcher.Open(new Uri(options.StartUrl, UriKind.Absolute));
            while (true)
            {
                var context = await listener.GetContextAsync().WaitAsync(deadline.Token);
                if (context.Request.HttpMethod != "GET"
                    || context.Request.Url is not { } received
                    || received.Authority != callback.Authority
                    || received.AbsolutePath != callback.AbsolutePath
                    || received.AbsoluteUri.Length > 16384)
                {
                    context.Response.StatusCode = 400;
                    context.Response.Close();
                    continue;
                }

                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers["Cache-Control"] = "no-store";
                context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; frame-ancestors 'none'";
                context.Response.Headers["Referrer-Policy"] = "no-referrer";
                // No identity, tokens or untrusted callback content is reflected into the page.
                var body = Encoding.UTF8.GetBytes("You may close this browser window.");
                context.Response.ContentLength64 = body.Length;
                try
                {
                    await context.Response.OutputStream.WriteAsync(body, deadline.Token);
                }
                catch (HttpListenerException) { /* A closed browser must not discard a valid callback. */ }
                finally { context.Response.Close(); }
                return new BrowserResult { ResultType = BrowserResultType.Success, Response = received.AbsoluteUri };
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout };
        }
        finally
        {
            listener.Stop();
        }
    }
}
