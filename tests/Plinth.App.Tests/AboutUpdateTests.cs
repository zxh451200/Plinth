using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Auth;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Xunit;

namespace Plinth.App.Tests;

public sealed class AboutUpdateTests
{
    private const string Available = """{"version":"99.0.0","url":"https://example.test/releases","notes":"Changes"}""";

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Download_button_opens_only_after_explicit_click_and_handles_browser_failure(bool browserFails)
    {
        var browser = new Browser { Fail = browserFails };
        var handler = new Handler(_ => Task.FromResult(Response(Available)));
        using var shell = new Shell(services =>
        {
            services.AddSingleton(CreateService(handler));
            services.AddSingleton<ISystemBrowserLauncher>(browser);
        });
        shell.ViewModel.ShowAboutCommand.Execute(null);
        var page = Assert.IsType<AboutViewModel>(shell.ViewModel.SelectedTab!.Content);
        shell.Layout();
        var button = shell.PageView<AboutView>().FindControl<Button>("OpenUpdateDownloadButton")!;
        Assert.False(button.IsVisible);
        Assert.False(page.OpenUpdateDownloadCommand.CanExecute(null));

        await page.CheckForUpdatesCommand.ExecuteAsync(null);
        shell.Layout();

        Assert.True(button.IsEffectivelyVisible);
        Assert.Same(page.OpenUpdateDownloadCommand, button.Command);
        Assert.Equal(0, browser.Calls);
        shell.ClickCenter(button);
        Assert.Equal(1, browser.Calls);
        Assert.Equal(new Uri("https://example.test/releases"), browser.Address);
        if (browserFails)
        {
            Assert.Equal(Loc.Get("about.update.openFailed"), page.UpdateStatus);
            Assert.DoesNotContain("SECRET", page.UpdateStatus);
            browser.Fail = false;
            shell.ClickCenter(button);
            Assert.Equal(2, browser.Calls);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://example.test/setup.exe")]
    [InlineData("file:///tmp/setup.exe")]
    [InlineData("https://user:password@example.test/setup.exe")]
    [InlineData("https://example.test/setup.exe#fragment")]
    public async Task Invalid_download_addresses_never_enable_browser_launch(string address)
    {
        var browser = new Browser();
        var body = $$"""{"version":"99.0.0","url":"{{address}}"}""";
        using var branding = new BrandingService(NullLogger<BrandingService>.Instance);
        using var page = new AboutViewModel(CreateService(new Handler(_ => Task.FromResult(Response(body)))), branding, browser);
        await page.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.False(page.HasUpdateDownload);
        Assert.False(page.OpenUpdateDownloadCommand.CanExecute(null));
        page.OpenUpdateDownloadCommand.Execute(null);
        Assert.Equal(0, browser.Calls);
    }

    [Theory]
    [InlineData("failed")]
    [InlineData("current")]
    [InlineData("disabled")]
    public async Task Rechecking_immediately_removes_old_link_and_does_not_restore_it_for_other_results(string kind)
    {
        var response = new TaskCompletionSource<HttpResponseMessage>();
        var calls = 0;
        var handler = new Handler(_ => ++calls == 1 ? Task.FromResult(Response(Available)) : response.Task);
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.UpdateFeedUrl = "https://example.test/feed.json";
        var updates = new UpdateCheckService(settings, NullLogger<UpdateCheckService>.Instance, handler);
        var browser = new Browser();
        using var branding = new BrandingService(NullLogger<BrandingService>.Instance);
        using var page = new AboutViewModel(updates, branding, browser);
        await page.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(page.HasUpdateDownload);
        if (kind == "disabled") settings.Current.UpdateFeedUrl = "";

        var checking = page.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.False(page.HasUpdateDownload);
        Assert.False(page.OpenUpdateDownloadCommand.CanExecute(null));
        page.OpenUpdateDownloadCommand.Execute(null);
        Assert.Equal(0, browser.Calls);
        using var next = Response(kind == "current"
            ? """{"version":"0.0.0","url":"https://example.test/releases"}""" : "invalid JSON");
        response.SetResult(next);
        await checking;
        Assert.False(page.HasUpdateDownload);
        Assert.False(page.IsChecking);
        Assert.Equal(kind == "disabled" ? 1 : 2, calls);
    }

    [AvaloniaFact]
    public async Task Closing_about_cancels_local_check_and_ignores_late_response()
    {
        var response = new TaskCompletionSource<HttpResponseMessage>();
        CancellationToken requestToken = default;
        var handler = new Handler(token => { requestToken = token; return response.Task; });
        var browser = new Browser();
        using var shell = new Shell(services =>
        {
            services.AddSingleton(CreateService(handler));
            services.AddSingleton<ISystemBrowserLauncher>(browser);
        });
        shell.ViewModel.ShowAboutCommand.Execute(null);
        var page = Assert.IsType<AboutViewModel>(shell.ViewModel.SelectedTab!.Content);
        var checking = page.CheckForUpdatesCommand.ExecuteAsync(null);
        Assert.True(page.IsChecking);
        await shell.ViewModel.CloseTabCommand.ExecuteAsync(null);
        Assert.True(requestToken.IsCancellationRequested);
        var status = page.UpdateStatus;
        response.SetResult(Response(Available)); // Deliberately return despite cancellation.
        await checking;
        Assert.Equal(status, page.UpdateStatus);
        Assert.False(page.HasUpdateDownload);
        Assert.False(page.IsChecking);
        Assert.False(page.CheckForUpdatesCommand.CanExecute(null));
        page.OpenUpdateDownloadCommand.Execute(null);
        Assert.Equal(0, browser.Calls);
        shell.ViewModel.ShowAboutCommand.Execute(null);
        Assert.NotSame(page, shell.ViewModel.SelectedTab!.Content);
    }

    private static UpdateCheckService CreateService(HttpMessageHandler handler)
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.UpdateFeedUrl = "https://example.test/feed.json";
        return new UpdateCheckService(settings, NullLogger<UpdateCheckService>.Instance, handler);
    }

    private static HttpResponseMessage Response(string body) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body) };

    private sealed class Handler(Func<CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            respond(cancellationToken);
    }

    private sealed class Browser : ISystemBrowserLauncher
    {
        public int Calls { get; private set; }
        public Uri? Address { get; private set; }
        public bool Fail { get; set; }
        public void Open(Uri uri)
        {
            Calls++;
            Address = uri;
            if (Fail) throw new InvalidOperationException("SECRET /private/browser");
        }
    }
}
