using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

public partial class MainViewModel
{
    private bool _pageNavigationReady;
    public ObservableCollection<NavigationItem> FavoritePages { get; } = [];
    public ObservableCollection<NavigationItem> RecentPages { get; } = [];
    public bool HasFavoritePages => FavoritePages.Count > 0;
    public bool HasRecentPages => RecentPages.Count > 0;
    public bool IsCurrentPageFavorite => FavoritePages.Any(item => item.Id == SelectedTab?.Id);

    private NavigationItem? CurrentMenuPage =>
        MenuItems.FirstOrDefault(item => item.Id == SelectedTab?.Id);
    private bool CanToggleFavorite() => !_released && !_closingTabs && !_exitPrepared
        && CurrentMenuPage is not null
        && (IsCurrentPageFavorite || FavoritePages.Count < WorkspacePreferencesService.MaxFavorites);

    private void InitializePageNavigation()
    {
        var saved = _preferences?.Navigation ?? new PageNavigationPreferences();
        foreach (var id in saved.FavoriteIds)
            if (MenuItems.FirstOrDefault(item => item.Id == id) is { } item) FavoritePages.Add(item);
        foreach (var id in saved.RecentIds)
            if (MenuItems.FirstOrDefault(item => item.Id == id) is { } item) RecentPages.Add(item);
        _pageNavigationReady = true;
        // Removed modules and hidden pages must not leave unusable menu entries.
        var savedIds = saved.FavoriteIds.Concat(saved.RecentIds).Distinct().ToArray();
        var missing = savedIds.Where(id => MenuItems.All(item => item.Id != id)).ToArray();
        // 未注册的页面可能只是示例模块被部署开关关闭,不能借启动裁掉磁盘上的收藏/最近。
        // 示例模块开启时仍按原规则清理真正移除的页面;可选模块确认失败时立即落盘裁剪。
        var preserveUnknown = ShouldPreserveUnknownNavigation(
                DeploymentConfiguration.Current.IncludeSampleModules, _optionalModule?.Failed == true)
            && missing.Any(id => NavigationItems.All(item => item.Id != id));
        if (!preserveUnknown
            && (!saved.FavoriteIds.SequenceEqual(FavoritePages.Select(item => item.Id))
                || !saved.RecentIds.SequenceEqual(RecentPages.Select(item => item.Id))))
            SavePageNavigation(FavoritePages.ToArray(), RecentPages.ToArray());
        RefreshFavoriteCommands();
    }

    private bool SavePageNavigation(NavigationItem[] favorites, NavigationItem[] recent)
    {
        if (_preferences is not null && !_preferences.SaveNavigation(new PageNavigationPreferences
        {
            FavoriteIds = favorites.Select(item => item.Id).ToArray(),
            RecentIds = recent.Select(item => item.Id).ToArray(),
        }))
        {
            StatusMessage = Loc.Get("shell.navigation.saveFailed");
            RefreshFavoriteCommands();
            return false;
        }
        FavoritePages.Clear();
        foreach (var item in favorites) FavoritePages.Add(item);
        RecentPages.Clear();
        foreach (var item in recent) RecentPages.Add(item);
        RefreshFavoriteCommands();
        return true;
    }

    private void RecordCurrentPage()
    {
        if (!_pageNavigationReady || _released || _closingTabs || _exitPrepared
            || CurrentMenuPage is not { } page || RecentPages.FirstOrDefault()?.Id == page.Id) return;
        var recent = new[] { page }.Concat(RecentPages.Where(item => item.Id != page.Id))
            .Take(WorkspacePreferencesService.MaxRecentPages).ToArray();
        SavePageNavigation(FavoritePages.ToArray(), recent);
    }

    [RelayCommand(CanExecute = nameof(CanToggleFavorite))]
    private void ToggleFavorite()
    {
        if (!CanToggleFavorite() || CurrentMenuPage is not { } page) return;
        var favorites = IsCurrentPageFavorite
            ? FavoritePages.Where(item => item.Id != page.Id).ToArray()
            : FavoritePages.Append(page).ToArray();
        SavePageNavigation(favorites, RecentPages.ToArray());
    }

    private bool CanClearRecentPages() => HasRecentPages && !_released && !_closingTabs && !_exitPrepared;

    [RelayCommand(CanExecute = nameof(CanClearRecentPages))]
    private void ClearRecentPages()
    {
        if (CanClearRecentPages()) SavePageNavigation(FavoritePages.ToArray(), []);
    }

    private void RefreshFavoriteCommands()
    {
        OnPropertyChanged(nameof(HasFavoritePages));
        OnPropertyChanged(nameof(HasRecentPages));
        OnPropertyChanged(nameof(IsCurrentPageFavorite));
        ToggleFavoriteCommand.NotifyCanExecuteChanged();
        ClearRecentPagesCommand.NotifyCanExecuteChanged();
    }
}
