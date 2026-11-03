using System;
using System.IO;
using System.Linq;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class PageNavigationTests
{
    [AvaloniaFact]
    public void Recent_pages_follow_visits_are_bounded_and_reuse_open_tabs()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        foreach (var item in fixture.Pages.Take(12)) vm.OpenTabCommand.Execute(item);
        Assert.Equal(10, vm.RecentPages.Count);
        Assert.Equal("page.11", vm.RecentPages[0].Id);
        var count = vm.Tabs.Count;
        vm.OpenTabCommand.Execute(fixture.Pages[2]);
        Assert.Equal(count, vm.Tabs.Count);
        Assert.Equal("page.2", vm.RecentPages[0].Id);
        Assert.Single(vm.RecentPages.Where(item => item.Id == "page.2"));
        vm.OpenTabCommand.Execute(fixture.Hidden);
        Assert.DoesNotContain(vm.RecentPages, item => item.Id == fixture.Hidden.Id);
        Assert.False(vm.ToggleFavoriteCommand.CanExecute(null));
        vm.ClearRecentPagesCommand.Execute(null);
        Assert.Empty(vm.RecentPages);
        Assert.Empty(new WorkspacePreferencesService(fixture.Path).Navigation.RecentIds);
        Assert.Equal(count + 1, vm.Tabs.Count);
    }

    [AvaloniaFact]
    public void Restoring_tabs_keeps_history_order_and_prunes_unregistered_shortcuts()
    {
        using var fixture = new Fixture(saved: true);
        var vm = fixture.ViewModel;
        Assert.Equal(new[] { "page.3", "page.1" }, vm.RecentPages.Select(item => item.Id));
        Assert.Equal("page.1", Assert.Single(vm.FavoritePages).Id);
        Assert.Equal("page.0", vm.SelectedTab!.Id);
        var saved = new WorkspacePreferencesService(fixture.Path).Navigation;
        Assert.DoesNotContain("removed", saved.FavoriteIds);
        Assert.DoesNotContain("hidden", saved.RecentIds);
        vm.OpenTabCommand.Execute(fixture.Pages[1]);
        Assert.True(vm.IsCurrentPageFavorite);
        vm.ToggleFavoriteCommand.Execute(null);
        Assert.Empty(vm.FavoritePages);
        vm.ToggleFavoriteCommand.Execute(null);
        vm.ResetLayoutCommand.Execute(null);
        Assert.Single(vm.FavoritePages);
        Assert.Equal("page.1", new WorkspacePreferencesService(fixture.Path).Navigation.FavoriteIds.Single());
    }

    [AvaloniaFact]
    public void Failed_favorite_save_keeps_check_state_and_existing_history()
    {
        using var fixture = new Fixture();
        var vm = fixture.ViewModel;
        vm.OpenTabCommand.Execute(fixture.Pages[1]);
        var recent = vm.RecentPages.Select(item => item.Id).ToArray();
        Directory.CreateDirectory(fixture.Path + ".tmp");
        vm.ToggleFavoriteCommand.Execute(null);
        Assert.False(vm.IsCurrentPageFavorite);
        Assert.Empty(vm.FavoritePages);
        Assert.Equal(recent, vm.RecentPages.Select(item => item.Id));
        Assert.Equal(Plinth.App.Localization.Loc.Get("shell.navigation.saveFailed"), vm.StatusMessage);
        Assert.Empty(new WorkspacePreferencesService(fixture.Path).Navigation.FavoriteIds);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly Shell _shell = new();
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
            "plinth-page-navigation-" + Guid.NewGuid().ToString("N") + ".json");
        public NavigationItem[] Pages { get; } = Enumerable.Range(0, 25)
            .Select(i => new NavigationItem($"page.{i}", $"Page {i}", () => new EmptyPage())).ToArray();
        public NavigationItem Hidden { get; } = new("hidden", "Hidden", () => new EmptyPage(), ShowInMenu: false);
        public MainViewModel ViewModel { get; }

        public Fixture(bool saved = false)
        {
            var store = new WorkspacePreferencesService(Path);
            if (saved)
            {
                store.SaveNavigation(new PageNavigationPreferences
                {
                    FavoriteIds = ["page.1", "removed"],
                    RecentIds = ["page.3", "hidden", "removed", "page.1"],
                });
                store.SaveLayout(new WorkspaceLayoutV1 { OpenTabIds = ["page.0"], SelectedTabId = "page.0" });
            }
            ViewModel = ActivatorUtilities.CreateInstance<MainViewModel>(_shell.Provider,
                Pages.Append(Hidden).ToArray(), store);
        }

        public void Dispose()
        {
            ViewModel.ReleaseWorkspace();
            _shell.Dispose();
            File.Delete(Path);
            if (Directory.Exists(Path + ".tmp")) Directory.Delete(Path + ".tmp");
        }
    }

    private sealed class EmptyPage : ViewModelBase { }
}
