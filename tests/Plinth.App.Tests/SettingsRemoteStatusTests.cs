using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class SettingsRemoteStatusTests
{
    [AvaloniaTheory]
    [InlineData("success")]
    [InlineData("failure")]
    [InlineData("exception")]
    [InlineData("cancel")]
    public async Task Late_A_cannot_replace_B_after_configuration_change(string outcome)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.FirstOutcome = outcome;
        var first = Check(fixture.Page, cancellation.Token);
        try
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Page.RemoteBaseUrl = "https://b.example.com";
            await fixture.Page.PendingChange;
            Assert.Equal("https://b.example.com", fixture.Settings.Current.Remote.BaseUrl);
            // Invoke the method directly: the command's CanExecute normally prevents a second click.
            await Check(fixture.Page);
            var newer = fixture.Page.RemoteStatus;
            Assert.Contains(Loc.Get("remote.forbidden.message"), newer);
            Assert.Equal("https://b.example.com/health", fixture.Handler.LastUrl);
            if (outcome == "cancel") cancellation.Cancel();
            fixture.Handler.Release.TrySetResult();
            await first;
            Assert.Equal(newer, fixture.Page.RemoteStatus);
        }
        finally
        {
            fixture.Handler.Release.TrySetResult();
            await first;
        }
    }

    [AvaloniaTheory]
    [InlineData("url", "shell.health.unchecked")]
    [InlineData("path", "shell.health.unchecked")]
    [InlineData("enabled", "remote.status.disabled")]
    [InlineData("invalid", "remote.status.invalid")]
    public async Task Editing_without_B_immediately_invalidates_A(string input, string statusKey)
    {
        using var fixture = new Fixture();
        var first = Check(fixture.Page);
        try
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            switch (input)
            {
                case "url": fixture.Page.RemoteBaseUrl = "https://b.example.com"; break;
                case "path": fixture.Page.RemoteHealthPath = "/ready"; break;
                case "enabled": fixture.Page.RemoteEnabled = false; break;
                default: fixture.Page.RemoteBaseUrl = "http://unsafe.example.com"; break;
            }
            Assert.Equal(Loc.Get(statusKey), fixture.Page.RemoteStatus);
            await fixture.Page.PendingChange;
            fixture.Handler.Release.TrySetResult();
            await first;
            Assert.Equal(Loc.Get(statusKey), fixture.Page.RemoteStatus);
            Assert.Equal(1, fixture.Handler.Requests);
        }
        finally
        {
            fixture.Handler.Release.TrySetResult();
            await first;
        }
    }

    [AvaloniaFact]
    public async Task A_new_check_alone_invalidates_an_older_check()
    {
        using var fixture = new Fixture();
        var first = Check(fixture.Page);
        try
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Check(fixture.Page);
            var newer = fixture.Page.RemoteStatus;
            Assert.Contains(Loc.Get("remote.forbidden.message"), newer);
            fixture.Handler.Release.TrySetResult();
            await first;
            Assert.Equal(newer, fixture.Page.RemoteStatus);
        }
        finally
        {
            fixture.Handler.Release.TrySetResult();
            await first;
        }
    }

    [AvaloniaTheory]
    [InlineData("success", "remote.status.connected")]
    [InlineData("failure", "remote.forbidden.message")]
    [InlineData("exception", "remote.status.failed")]
    [InlineData("cancel", "shell.health.unchecked")]
    public async Task Current_check_handles_success_failure_exception_and_cancellation(string outcome, string key)
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Handler.FirstOutcome = outcome;
        var check = Check(fixture.Page, cancellation.Token);
        try
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (outcome == "cancel") cancellation.Cancel();
            fixture.Handler.Release.TrySetResult();
            await check;
            Assert.Contains(Loc.Get(key), fixture.Page.RemoteStatus);
        }
        finally
        {
            fixture.Handler.Release.TrySetResult();
            await check;
        }
    }

    [AvaloniaTheory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Pending_authorization_and_backfill_do_not_publish_old_health(bool approve)
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var fixture = new Fixture(() => confirmation.Task);
        var first = Check(fixture.Page);
        try
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            fixture.Page.RemoteBaseUrl = "https://b.example.com";
            Assert.False(fixture.Page.PendingChange.IsCompleted);
            Assert.Equal("https://a.example.com", fixture.Settings.Current.Remote.BaseUrl);
            await Check(fixture.Page);
            Assert.Equal(1, fixture.Handler.Requests);
            Assert.Equal(Loc.Get("shell.health.unchecked"), fixture.Page.RemoteStatus);
            confirmation.TrySetResult(approve);
            await fixture.Page.PendingChange;
            Assert.Equal(approve ? "https://b.example.com" : "https://a.example.com",
                fixture.Page.RemoteBaseUrl);
            fixture.Handler.Release.TrySetResult();
            await first;
            Assert.Equal(Loc.Get("shell.health.unchecked"), fixture.Page.RemoteStatus);
            Assert.Equal(Loc.Get(approve ? "settings.saved" : "settings.cancelled"), fixture.Page.StatusText);
        }
        finally
        {
            confirmation.TrySetResult(false);
            fixture.Handler.Release.TrySetResult();
            await fixture.Page.PendingChange;
            await first;
        }
    }

    [AvaloniaFact]
    public async Task Closing_invalidates_pending_health_and_prevents_new_checks()
    {
        using var fixture = new Fixture();
        var first = Check(fixture.Page);
        try
        {
            await fixture.Handler.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(await fixture.Page.TryLeaveAsync());
            fixture.Page.Dispose();
            var before = fixture.Page.RemoteStatus;
            fixture.Handler.Release.TrySetResult();
            await first;
            await Check(fixture.Page);
            Assert.Equal(before, fixture.Page.RemoteStatus);
            Assert.Equal(1, fixture.Handler.Requests);
        }
        finally
        {
            fixture.Handler.Release.TrySetResult();
            await first;
        }
    }

    [AvaloniaFact]
    public async Task Command_can_still_check_normally_and_missing_service_reports_unavailable()
    {
        using var fixture = new Fixture();
        fixture.Handler.Release.TrySetResult();
        await fixture.Page.CheckRemoteCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("remote.status.connected"), fixture.Page.RemoteStatus);
        using var unavailable = new SettingsViewModel(fixture.Settings);
        await unavailable.CheckRemoteCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("remote.status.unavailable"), unavailable.RemoteStatus);
    }

    [AvaloniaFact]
    public async Task Editing_clears_an_already_published_success_without_starting_a_check()
    {
        using var fixture = new Fixture();
        fixture.Handler.Release.TrySetResult();
        await Check(fixture.Page);
        Assert.Equal(Loc.Get("remote.status.connected"), fixture.Page.RemoteStatus);
        fixture.Page.RemoteHealthPath = "/ready";
        await fixture.Page.PendingChange;
        Assert.Equal(Loc.Get("shell.health.unchecked"), fixture.Page.RemoteStatus);
        Assert.Equal(1, fixture.Handler.Requests);
    }

    [AvaloniaFact]
    public async Task Already_cancelled_check_does_not_send_HTTP_or_report_success()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Check(fixture.Page, cancellation.Token);
        Assert.Equal(Loc.Get("shell.health.unchecked"), fixture.Page.RemoteStatus);
        Assert.Equal(0, fixture.Handler.Requests);
    }

    private static Task Check(SettingsViewModel page, CancellationToken token = default) =>
        (Task)typeof(SettingsViewModel).GetMethod("CheckRemoteAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, new object[] { token })!;

    private sealed class ControlledHandler : HttpMessageHandler
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public string FirstOutcome { get; set; } = "success";
        public string? LastUrl { get; private set; }
        private int _requests;
        public int Requests => _requests;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUrl = request.RequestUri!.ToString();
            if (Interlocked.Increment(ref _requests) != 1)
                return new(HttpStatusCode.Forbidden);
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (FirstOutcome == "exception") throw new InvalidOperationException("Unexpected transport failure");
            return new(FirstOutcome == "failure" ? HttpStatusCode.Forbidden : HttpStatusCode.OK);
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(AppPaths.DataDirectory, "settings.json");
        private readonly byte[]? _original;
        public ControlledHandler Handler { get; } = new();
        public SettingsService Settings { get; }
        public SettingsViewModel Page { get; }

        public Fixture(Func<Task<bool>>? confirm = null)
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            _original = File.Exists(_path) ? File.ReadAllBytes(_path) : null;
            File.WriteAllText(_path,
                """{"Remote":{"Enabled":true,"BaseUrl":"https://a.example.com","HealthPath":"/health"}}""");
            var users = new OfflineUserContext();
            var audit = new TestAudit();
            var operations = new SensitiveOperationService(
                new AuthorizedOperation(new PermissionService(users, audit), users, audit),
                audit, authenticationEnabled: false, production: confirm is not null)
            {
                ConfirmProductionAsync = confirm,
            };
            Settings = new SettingsService(NullLogger<SettingsService>.Instance, operations);
            var remote = new RemoteConnectionService(Settings,
                NullLogger<RemoteConnectionService>.Instance, Handler);
            Page = new SettingsViewModel(Settings, remote: remote);
            Assert.Equal(Loc.Get("shell.health.unchecked"), Page.RemoteStatus);
        }

        public void Dispose()
        {
            Page.Dispose();
            Handler.Dispose();
            if (_original is null) File.Delete(_path);
            else File.WriteAllBytes(_path, _original);
        }
    }

    private sealed class TestAudit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }
}
