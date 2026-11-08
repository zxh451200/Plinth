using System;
using System.IO;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class WorkspaceNavigationTests
{
    [AvaloniaFact]
    public async Task Cancelled_close_does_not_remove_or_dispose_the_page()
    {
        using var shell = new Shell();
        var page = new GuardedPage { AllowLeave = false };
        var tab = new WorkspaceTabViewModel("guarded", "Guarded", page);
        shell.ViewModel.Tabs.Add(tab);
        shell.ViewModel.SelectedTab = tab;
        await shell.ViewModel.CloseTabCommand.ExecuteAsync(null);
        Assert.Contains(tab, shell.ViewModel.Tabs);
        Assert.Same(tab, shell.ViewModel.SelectedTab);
        Assert.False(page.Disposed);
        page.AllowLeave = true;
        await shell.ViewModel.CloseTabCommand.ExecuteAsync(null);
        Assert.DoesNotContain(tab, shell.ViewModel.Tabs);
        Assert.True(page.Disposed);
    }

    [AvaloniaFact]
    public async Task Close_all_and_exit_preflight_keep_all_tabs_when_a_page_cancels()
    {
        using var shell = new Shell();
        var first = new GuardedPage();
        var second = new GuardedPage { AllowLeave = false };
        shell.ViewModel.Tabs.Add(new WorkspaceTabViewModel("first", "First", first));
        shell.ViewModel.Tabs.Add(new WorkspaceTabViewModel("second", "Second", second));
        var count = shell.ViewModel.Tabs.Count;
        await shell.ViewModel.CloseAllTabsCommand.ExecuteAsync(null);
        Assert.Equal(count, shell.ViewModel.Tabs.Count);
        Assert.False(first.Disposed);
        Assert.False(second.Disposed);
        Assert.False(await shell.ViewModel.PrepareExitAsync());
        Assert.False(first.Disposed);
        second.AllowLeave = true;
        Assert.True(await shell.ViewModel.PrepareExitAsync());
        Assert.False(first.Disposed);
        shell.ViewModel.ReleaseWorkspace();
        Assert.True(first.Disposed);
        Assert.True(second.Disposed);
    }

    [AvaloniaFact]
    public async Task Concurrent_close_requests_do_not_prompt_or_dispose_twice()
    {
        using var shell = new Shell();
        var response = new TaskCompletionSource<bool>();
        var page = new GuardedPage { Pending = response.Task };
        var tab = new WorkspaceTabViewModel("guarded", "Guarded", page);
        shell.ViewModel.Tabs.Add(tab);
        var closing = shell.ViewModel.CloseTabItemCommand.ExecuteAsync(tab);
        await shell.ViewModel.CloseAllTabsCommand.ExecuteAsync(null);
        Assert.False(await shell.ViewModel.PrepareExitAsync());
        Assert.Equal(1, page.LeaveCalls);
        response.SetResult(true);
        await closing;
        Assert.Equal(1, page.DisposeCalls);
    }

    [AvaloniaFact]
    public void Layout_restores_registered_ids_and_reset_keeps_live_pages()
    {
        using var shell = new Shell();
        var path = Path.Combine(Path.GetTempPath(), "plinth-layout-" + Guid.NewGuid().ToString("N") + ".json");
        try
        {
            var store = new WorkspacePreferencesService(path);
            store.SaveLayout(new WorkspaceLayoutV1
            {
                OpenTabIds = ["second", "removed", "first", "second"],
                SelectedTabId = "first",
                SidebarVisible = false,
                SidebarWidth = 340,
                SidePanelVisible = true,
                SidePanelWidth = 370,
                SidePanelTab = 1,
            });
            NavigationItem[] items =
            [
                new("first", "First", () => new GuardedPage()),
                new("second", "Second", () => new GuardedPage()),
            ];
            var vm = ActivatorUtilities.CreateInstance<MainViewModel>(shell.Provider, items, store);
            Assert.Collection(vm.Tabs, tab => Assert.Equal("second", tab.Id), tab => Assert.Equal("first", tab.Id));
            Assert.Equal("first", vm.SelectedTab?.Id);
            Assert.False(vm.IsSidebarVisible);
            Assert.Equal(340, vm.SidebarWidth);
            Assert.True(vm.IsSidePanelVisible);
            Assert.Equal(370, vm.SidePanelWidth);
            Assert.Equal(1, vm.SidePanelTab);
            var page = vm.SelectedTab!.Content;
            vm.ResetLayoutCommand.Execute(null);
            Assert.True(vm.IsSidebarVisible);
            Assert.Equal(260, vm.SidebarWidth);
            Assert.False(vm.IsSidePanelVisible);
            Assert.Equal(300, vm.SidePanelWidth);
            Assert.Equal(0, vm.SidePanelTab);
            Assert.Same(page, vm.SelectedTab.Content);
            Assert.False(((GuardedPage)page).Disposed);
            Assert.True(new WorkspacePreferencesService(path).Layout!.SidebarVisible);
            vm.ReleaseWorkspace();
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [AvaloniaTheory]
    [InlineData("allow", false)]
    [InlineData("reject", false)]
    [InlineData("error", false)]
    [InlineData("allow", true)]
    [InlineData("reject", true)]
    [InlineData("error", true)]
    public void Old_close_result_cannot_close_or_update_a_rebound_workspace(string outcome, bool restoreOriginal)
    {
        using var shell = new Shell();
        var response = new TaskCompletionSource<bool>();
        var oldPage = new GuardedPage { Pending = response.Task };
        shell.ViewModel.Tabs.Add(new WorkspaceTabViewModel("old", "Old", oldPage));
        var newPage = new GuardedPage { AllowLeave = false };
        var replacement = new MainViewModel(
            [new NavigationItem("new", "New", () => newPage)],
            shell.Provider.GetRequiredService<IClassicDesktopStyleApplicationLifetime>(),
            shell.Provider.GetRequiredService<FolderService>(),
            shell.Provider.GetRequiredService<BrandingService>());
        var closed = 0;
        shell.Window.Closed += (_, _) => closed++;
        try
        {
            shell.Window.Close();
            shell.Window.Close(); // Pending requests share the original preflight.
            Assert.Equal(1, oldPage.LeaveCalls);
            Assert.True(shell.Window.IsVisible);
            shell.Window.DataContext = replacement;
            if (restoreOriginal) shell.Window.DataContext = shell.ViewModel;
            var current = restoreOriginal ? shell.ViewModel : replacement;
            current.StatusMessage = "Current workspace";

            if (outcome == "error") response.SetException(new InvalidOperationException("Old preflight"));
            else response.SetResult(outcome == "allow");
            Dispatcher.UIThread.RunJobs();

            Assert.True(shell.Window.IsVisible);
            Assert.False(shell.Window.CloseApproved);
            Assert.Equal(0, closed);
            Assert.Equal("Current workspace", current.StatusMessage);
            Assert.Equal(0, newPage.LeaveCalls);
            Assert.Equal(0, newPage.DisposeCalls);
            Assert.Equal(0, oldPage.DisposeCalls);

            if (!restoreOriginal)
            {
                shell.Window.Close();
                Assert.True(shell.Window.IsVisible);
                Assert.Equal(1, newPage.LeaveCalls);
                newPage.AllowLeave = true;
            }
        }
        finally
        {
            oldPage.Pending = null;
            newPage.AllowLeave = true;
            shell.Window.Close();
            Dispatcher.UIThread.RunJobs();
            replacement.ReleaseWorkspace();
            shell.ViewModel.ReleaseWorkspace();
        }
        Assert.Equal(1, closed);
        Assert.Equal(1, oldPage.DisposeCalls);
        Assert.Equal(1, newPage.DisposeCalls);
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_exit_rejects_synchronous_context_rebind_even_when_original_is_restored(bool pending)
    {
        using var shell = new Shell();
        var response = new TaskCompletionSource<bool>();
        var page = new GuardedPage
        {
            Pending = pending ? response.Task : null,
            OnLeave = () =>
            {
                shell.Window.DataContext = null;
                shell.Window.DataContext = shell.ViewModel;
            },
        };
        var tab = new WorkspaceTabViewModel("guarded", "Guarded", page);
        shell.ViewModel.Tabs.Add(tab);
        var closed = 0;
        shell.Window.Closed += (_, _) => closed++;
        try
        {
            var exiting = shell.Window.RequestExitAsync();
            Assert.Equal(1, page.LeaveCalls);
            Assert.Same(shell.ViewModel, shell.Window.DataContext);
            if (pending) response.SetResult(true);

            Assert.False(await exiting);
            Assert.True(shell.Window.IsVisible);
            Assert.False(shell.Window.CloseApproved);
            Assert.Equal(0, closed);
            Assert.Contains(tab, shell.ViewModel.Tabs);
            Assert.Equal(0, page.DisposeCalls);

            page.OnLeave = null;
            Assert.True(await shell.Window.RequestExitAsync());
            Assert.False(shell.Window.IsVisible);
            Assert.Equal(1, closed);
            Assert.Equal(1, page.DisposeCalls);
        }
        finally
        {
            page.OnLeave = null;
            page.Pending = null;
            response.TrySetResult(true);
            shell.Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_exit_keeps_rejected_workspace_and_allows_retry(bool pending)
    {
        using var shell = new Shell();
        var response = new TaskCompletionSource<bool>();
        var page = new GuardedPage { AllowLeave = false, Pending = pending ? response.Task : null };
        var tab = new WorkspaceTabViewModel("guarded", "Guarded", page);
        shell.ViewModel.Tabs.Add(tab);
        shell.ViewModel.SelectedTab = tab;
        var closed = 0;
        shell.Window.Closed += (_, _) => closed++;
        try
        {
            var exiting = shell.Window.RequestExitAsync();
            if (pending)
            {
                Assert.False(exiting.IsCompleted);
                Assert.False(await shell.Window.RequestExitAsync());
                Assert.Equal(1, page.LeaveCalls);
                Assert.True(shell.Window.IsVisible);
                response.SetResult(false);
            }
            Assert.False(await exiting);
            Assert.True(shell.Window.IsVisible);
            Assert.False(shell.Window.CloseApproved);
            Assert.Equal(0, closed);
            Assert.Contains(tab, shell.ViewModel.Tabs);
            Assert.Same(tab, shell.ViewModel.SelectedTab);
            Assert.Equal(0, page.DisposeCalls);

            page.Pending = null;
            page.AllowLeave = true;
            Assert.True(await shell.Window.RequestExitAsync());
            Assert.False(shell.Window.IsVisible);
            Assert.True(shell.Window.CloseApproved);
            Assert.Equal(2, page.LeaveCalls);
            Assert.Equal(1, closed);
            Assert.Equal(1, page.DisposeCalls);
            Assert.False(await shell.Window.RequestExitAsync());
            Assert.Equal(1, closed);
            Assert.Equal(1, page.DisposeCalls);
        }
        finally
        {
            page.Pending = null;
            page.AllowLeave = true;
            response.TrySetResult(true);
            shell.Window.Close();
            Dispatcher.UIThread.RunJobs();
        }
    }

    private sealed class GuardedPage : ViewModelBase, IUnsavedChanges, IDisposable
    {
        public bool IsDirty => true;
        public bool AllowLeave { get; set; } = true;
        public Task<bool>? Pending { get; set; }
        public Action? OnLeave { get; set; }
        public int LeaveCalls { get; private set; }
        public int DisposeCalls { get; private set; }
        public bool Disposed => DisposeCalls > 0;
        public Task<bool> TryLeaveAsync()
        {
            LeaveCalls++;
            OnLeave?.Invoke();
            return Pending ?? Task.FromResult(AllowLeave);
        }
        public void Dispose() => DisposeCalls++;
    }
}
