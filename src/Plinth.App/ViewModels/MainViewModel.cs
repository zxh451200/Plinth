using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Avalonia.Controls.ApplicationLifetimes;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Plinth.App.Navigation;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

/// <summary>
/// 主窗口的 Shell 状态:导航、标签工作区、状态栏。
/// 页面由各模块通过 <see cref="NavigationItem"/> 注册,Shell 不写死业务页面。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly FolderService _folderService;
    private readonly ILogger<MainViewModel> _logger;

    public MainViewModel(
        IEnumerable<NavigationItem> navigationItems,
        IClassicDesktopStyleApplicationLifetime lifetime,
        FolderService folderService,
        ILogger<MainViewModel> logger)
    {
        _lifetime = lifetime;
        _folderService = folderService;
        _logger = logger;

        NavigationItems = new ObservableCollection<NavigationItem>(navigationItems);
        Tabs.CollectionChanged += (_, _) => OnTabsChanged();

        SelectedNavigationItem = NavigationItems.FirstOrDefault();
    }

    public ObservableCollection<NavigationItem> NavigationItems { get; }

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];

    public string AppTitle => AppInfo.ProductName;

    public string VersionText => $"v{AppInfo.Version}";

    [ObservableProperty]
    public partial NavigationItem? SelectedNavigationItem { get; set; }

    [ObservableProperty]
    public partial WorkspaceTabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; } = true;

    [ObservableProperty]
    public partial bool HasTabs { get; set; }

    [ObservableProperty]
    public partial string TabCountText { get; set; } = "0 个标签";

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = "就绪";

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is not null)
        {
            OpenTab(value);
        }
    }

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? value) => NotifyTabCommands();

    /// <summary>同一个导航项只保留一个标签,再次点击时切回已有标签。</summary>
    [RelayCommand]
    private void OpenTab(NavigationItem item)
    {
        var existing = Tabs.FirstOrDefault(tab => tab.Id == item.Id);
        if (existing is not null)
        {
            SelectedTab = existing;
            return;
        }

        var tab = new WorkspaceTabViewModel(item.Id, item.Title, item.CreateViewModel());
        Tabs.Add(tab);
        SelectedTab = tab;
        StatusMessage = $"已打开:{item.Title}";
    }

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseTab() => CloseTabItem(SelectedTab);

    /// <summary>标签头上的关闭按钮用;点哪个关哪个,不受当前选中项影响。</summary>
    [RelayCommand]
    private void CloseTabItem(WorkspaceTabViewModel? tab)
    {
        if (tab is null)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        Tabs.Remove(tab);
        SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
        StatusMessage = $"已关闭:{tab.Title}";
    }

    private bool CanCloseTab() => SelectedTab is not null;

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private void CloseAllTabs()
    {
        var count = Tabs.Count;
        Tabs.Clear();
        SelectedTab = null;
        StatusMessage = $"已关闭全部标签({count})";
    }

    [RelayCommand]
    private void Refresh()
    {
        if (SelectedTab?.Content is IRefreshable refreshable)
        {
            refreshable.Refresh();
            StatusMessage = $"已刷新:{SelectedTab.Title}";
            return;
        }

        StatusMessage = "当前页面没有可刷新的内容";
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.DataDirectory, "数据目录");

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.LogDirectory, "日志目录");

    [RelayCommand]
    private void ShowAbout() => OpenTab(new NavigationItem("shell.about", "关于", () => new AboutViewModel()));

    [RelayCommand]
    private void Exit() => _lifetime.TryShutdown();

    private void OpenFolder(string path, string displayName)
    {
        if (_folderService.TryOpen(path))
        {
            StatusMessage = $"已打开{displayName}";
            return;
        }

        StatusMessage = $"无法打开{displayName},请检查日志";
        _logger.LogWarning("打开{DisplayName}失败:{Path}", displayName, path);
    }

    private void OnTabsChanged()
    {
        HasTabs = Tabs.Count > 0;
        TabCountText = $"{Tabs.Count} 个标签";
        NotifyTabCommands();
    }

    private void NotifyTabCommands()
    {
        CloseTabCommand.NotifyCanExecuteChanged();
        CloseAllTabsCommand.NotifyCanExecuteChanged();
    }
}
