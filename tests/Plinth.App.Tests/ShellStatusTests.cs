using System;
using System.Collections.Immutable;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class ShellStatusTests
{
    [Fact]
    public async Task Refresh_reads_local_state_without_checking_health_and_tracks_user_changes()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote = new() { Enabled = true, BaseUrl = "https://example.com" };
        using var handler = new CountingHandler();
        var remote = new RemoteConnectionService(settings, NullLogger<RemoteConnectionService>.Instance, handler);
        var users = new Users();
        var vm = new ShellStatusViewModel(settings, remote, users, new TaskService(NullLogger<TaskService>.Instance));
        for (var i = 0; i < 3; i++) vm.Refresh();
        Assert.Equal(0, handler.Requests);
        Assert.Equal(Loc.Get("shell.health.unchecked"), vm.ConnectionText);
        users.CurrentUser = new("verified-subject", ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);
        vm.Refresh();
        Assert.Equal(Loc.Format("shell.status.user", "verified-subject"), vm.UserText);
        Assert.Equal(Loc.Format("shell.status.environment",
            Loc.Get("shell.environment." + DeploymentConfiguration.Current.Profile.ToLowerInvariant())), vm.EnvironmentText);
        await remote.CheckAsync();
        vm.Refresh();
        Assert.Equal(1, handler.Requests);
        Assert.Equal(Loc.Get("shell.health.passed"), vm.ConnectionText);
        Assert.DoesNotContain("example.com", vm.ConnectionHint);
        settings.Current.Remote.Enabled = false;
        vm.Refresh();
        Assert.Equal(Loc.Get("shell.health.disabled"), vm.ConnectionText);
        Assert.Equal(1, handler.Requests);
    }

    [Fact]
    public async Task Task_count_includes_waiting_and_running_and_does_not_invent_unknown_progress()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance);
        settings.Current.Remote = new();
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var remote = new RemoteConnectionService(settings, NullLogger<RemoteConnectionService>.Instance);
        var vm = new ShellStatusViewModel(settings, remote, new Users(), tasks);
        var firstStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tasks.Enqueue("Known", "Test", async (progress, token) =>
        {
            progress.Report(.25);
            firstStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        tasks.Enqueue("Unknown", "Test", async (_, token) =>
        {
            secondStarted.TrySetResult();
            await Task.Delay(Timeout.Infinite, token);
        });
        try
        {
            await Task.WhenAll(firstStarted.Task, secondStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
            vm.Refresh();
            Assert.Equal(2, vm.ActiveTaskCount);
            Assert.Equal(Loc.Format("shell.status.activeTasks", 2), vm.TaskText);
        }
        finally { await tasks.StopAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
        vm.Refresh();
        Assert.Equal(0, vm.ActiveTaskCount);
    }

    [AvaloniaFact]
    public void Status_actions_open_settings_and_tasks_without_replacing_navigation_message()
    {
        using var shell = new Shell();
        var status = shell.ViewModel.ShellStatus!;
        var message = shell.ViewModel.StatusMessage;
        status.Refresh();
        Assert.Equal(message, shell.ViewModel.StatusMessage);
        var connection = shell.Window.FindControl<Button>("ConnectionStatusButton")!;
        connection.Command!.Execute(null);
        Assert.Equal(ShellNavigation.SettingsId, shell.ViewModel.SelectedTab!.Id);
        var selected = shell.ViewModel.SelectedTab;
        var tasks = shell.Window.FindControl<Button>("TaskStatusButton")!;
        tasks.Command!.Execute(null);
        Assert.Same(selected, shell.ViewModel.SelectedTab);
        Assert.True(shell.ViewModel.IsSidePanelVisible);
        Assert.Equal(1, shell.ViewModel.SidePanelTab);
    }

    private sealed class Users : IRemoteUserContext
    {
        public RemoteUserContext CurrentUser { get; set; } = RemoteUserContext.Anonymous;
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }
}
