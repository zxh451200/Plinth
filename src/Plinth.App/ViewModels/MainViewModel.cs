using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Auth;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.Localization;

namespace Plinth.App.ViewModels;

/// <summary>
/// 主窗口的 Shell 状态:导航、标签工作区、状态栏。
/// 页面由各模块通过 <see cref="NavigationItem"/> 注册,Shell 不写死业务页面。
/// </summary>
public partial class MainViewModel : ViewModelBase
{
    private readonly IClassicDesktopStyleApplicationLifetime _lifetime;
    private readonly FolderService _folderService;
    private readonly BrandingService _branding;
    private readonly WorkspacePreferencesService? _preferences;
    private bool _closingTabs;
    private bool _exitPrepared;
    private readonly TaskService? _tasks;
    private readonly OptionalModuleState? _optionalModule;
    private readonly IPermissionService? _permissions;
    private readonly IIdentitySessionEvents? _sessionEvents;
    private bool _failedModulePageDisabled;
    private bool _released;
    private Guid? _completedTaskId;
    private readonly CommandMenuContribution[] _commandContributions;
    private readonly Action _languageChangedHandler;
    private INotifyPropertyChanged? _commandPage;
    private IEnumerable<CommandMenu> AllPageCommands =>
        new[] { NewPageCommand, SavePageCommand, DeletePageCommand, CancelPageCommand }.Concat(ModuleCommands);
    public UnsavedChangesService Changes { get; }
    public ShellStatusViewModel? ShellStatus { get; }

    public MainViewModel(
        IEnumerable<NavigationItem> navigationItems,
        IClassicDesktopStyleApplicationLifetime lifetime,
        FolderService folderService,
        BrandingService branding,
        WorkspacePreferencesService? preferences = null,
        UnsavedChangesService? changes = null,
        TaskService? tasks = null,
        IEnumerable<CommandMenuContribution>? commandContributions = null,
        ShellStatusViewModel? shellStatus = null,
        IPermissionService? permissions = null,
        IIdentitySessionEvents? sessionEvents = null)
        : this(navigationItems, lifetime, folderService, branding, preferences, changes,
            tasks, commandContributions, shellStatus, null, permissions, sessionEvents)
    { }

    public MainViewModel(
        IEnumerable<NavigationItem> navigationItems,
        IClassicDesktopStyleApplicationLifetime lifetime,
        FolderService folderService,
        BrandingService branding,
        WorkspacePreferencesService? preferences,
        UnsavedChangesService? changes,
        TaskService? tasks,
        IEnumerable<CommandMenuContribution>? commandContributions,
        ShellStatusViewModel? shellStatus,
        OptionalModuleState? optionalModule,
        IPermissionService? permissions = null,
        IIdentitySessionEvents? sessionEvents = null)
    {
        _optionalModule = optionalModule;
        _permissions = permissions;
        _sessionEvents = sessionEvents;
        _lifetime = lifetime;
        _folderService = folderService;
        _branding = branding;
        _preferences = preferences;
        Changes = changes ?? UnsavedChangesService.Default;
        _tasks = tasks;
        ShellStatus = shellStatus;
        ShellStatus?.Refresh();
        _commandContributions = commandContributions?.ToArray() ?? [];
        CommandMenuContribution.Validate(_commandContributions);
        NewPageCommand = CreatePageCommand("menu.file.new", PageCommandAction.New);
        SavePageCommand = CreatePageCommand("menu.file.save", PageCommandAction.Save);
        DeletePageCommand = CreatePageCommand("menu.edit.delete", PageCommandAction.Delete);
        CancelPageCommand = CreatePageCommand("menu.edit.cancel", PageCommandAction.Cancel);
        ModuleCommands = _commandContributions.Where(item => item.Action is null)
            .Select(item => new CommandMenu(item.HeaderKey, () => ResolveContribution(item))).ToArray();

        NavigationItems = new ObservableCollection<NavigationItem>(navigationItems);
        var ordered = BuildMenuItems(VisibleNavigationItems());
        MenuItems = new ObservableCollection<NavigationItem>(ordered);
        PrimaryMenuItems = new ObservableCollection<NavigationItem>(
            ordered.Where(item => item.Section != NavigationSection.Footer));
        FooterMenuItems = new ObservableCollection<NavigationItem>(
            ordered.Where(item => item.Section == NavigationSection.Footer));
        if (_sessionEvents is not null) _sessionEvents.SessionChanged += OnSessionChanged;
        Tabs.CollectionChanged += (_, _) => OnTabsChanged();
        // 弱订阅需要订阅方自己持住委托,否则可能被回收。
        _languageChangedHandler = OnLanguageChanged;
        Loc.Subscribe(_languageChangedHandler);

        if (preferences?.Layout is { } layout)
        {
            IsSidebarVisible = layout.SidebarVisible;
            SidebarWidth = layout.SidebarWidth;
            IsSidebarCompact = layout.SidebarCompact;
            IsSidePanelVisible = layout.SidePanelVisible;
            SidePanelWidth = layout.SidePanelWidth;
            SidePanelTab = layout.SidePanelTab;
            foreach (var id in layout.OpenTabIds)
            {
                if (VisibleNavigationItems().FirstOrDefault(item => item.Id == id) is { } item) OpenTab(item);
            }
            SelectedTab = Tabs.FirstOrDefault(tab => tab.Id == layout.SelectedTabId) ?? Tabs.FirstOrDefault();
        }
        else SelectedNavigationItem = MenuItems.FirstOrDefault();
        InitializePageNavigation();
        if (_tasks is not null) _tasks.Completed += OnTaskCompleted;
        if (_optionalModule is not null) _optionalModule.FailedChanged += OnOptionalModuleFailed;
    }

    [ObservableProperty]
    public partial bool HasTaskNotification { get; set; }
    [ObservableProperty]
    public partial string TaskNotificationText { get; set; } = "";

    public bool HasModuleFailure => _optionalModule?.Failed == true;
    public string ModuleFailureText => HasModuleFailure
        ? Loc.Format("shell.module.failed", _optionalModule!.ModuleName, _optionalModule.CorrelationId) : "";

    private void OnOptionalModuleFailed() => Dispatcher.UIThread.Post(() =>
    {
        // ViewLocator can run inside rendering. Never mutate Tabs on that stack,
        // and never apply a queued notification after the workspace has closed.
        if (_released || _exitPrepared) return;
        DisableFailedModulePage();
    });

    private void DisableFailedModulePage()
    {
        if (_failedModulePageDisabled) return;
        _failedModulePageDisabled = true;
        var selected = SelectedTab;
        foreach (var item in NavigationItems.Where(item =>
                     item.Id == _optionalModule?.PageId).ToArray())
        {
            NavigationItems.Remove(item);
            MenuItems.Remove(item);
            PrimaryMenuItems.Remove(item);
            FooterMenuItems.Remove(item);
            FavoritePages.Remove(item);
            RecentPages.Remove(item);
        }
        if (SelectedNavigationItem?.Id == _optionalModule?.PageId)
            SelectedNavigationItem = null;
        foreach (var tab in Tabs.Where(tab => tab.Id == _optionalModule?.PageId).ToArray())
        {
            Tabs.Remove(tab);
            // Initialization never produced an editable view: no unsaved prompt.
            ReleasePage(tab);
        }
        SelectedTab = selected is not null && Tabs.Contains(selected) ? selected : Tabs.FirstOrDefault();
        // During startup, initialization will load and filter the saved navigation.
        // Once loaded, persist the removal without waiting for another user action.
        if (_pageNavigationReady)
            SavePageNavigation(FavoritePages.ToArray(), RecentPages.ToArray());
        // Also runs while restoring the first tab, before InitializePageNavigation.
        // FavoritePages/RecentPages have property initializers; the generated relay
        // command getters initialize lazily. NavigationItems/MenuItems are assigned
        // before restoration begins, so command predicates are safe at this point.
        RefreshFavoriteCommands();
        OnPropertyChanged(nameof(HasModuleFailure));
        OnPropertyChanged(nameof(ModuleFailureText));
        OnCommandPageChanged(this, new PropertyChangedEventArgs(null));
    }

    private void OnTaskCompleted(WorkItem item) => Dispatcher.UIThread.Post(() =>
    {
        if (_released || _exitPrepared) return;
        _completedTaskId = item.Id;
        TaskNotificationText = Loc.Format("tasks.notification", item.Title, item.StatusText);
        if (item.ResultText.Length > 0)
            TaskNotificationText += Environment.NewLine + item.ResultText;
        HasTaskNotification = true;
        TaskCompletionNotifier.TryNotify(item.Status);
    });

    /// <summary>系统通知点击/协议启动的受控入口;权限过滤后页面不存在时什么也不做。</summary>
    public void OpenTaskCenter() => OpenRegisteredPage(ShellNavigation.TaskDiagnosticsId);

    [RelayCommand]
    private void DismissTaskNotification()
    {
        HasTaskNotification = false;
        _completedTaskId = null;
    }

    [RelayCommand]
    private void ViewCompletedTask()
    {
        if (_closingTabs || _exitPrepared || _released) return;
        OpenRegisteredPage(ShellNavigation.TaskDiagnosticsId);
        if (SelectedTab?.Content is TaskDiagnosticsViewModel page && _completedTaskId is { } id)
        {
            page.SelectTask(id);
            DismissTaskNotification();
        }
    }

    /// <summary>全部已注册页面,按 Id 找页面时用这份。</summary>
    public ObservableCollection<NavigationItem> NavigationItems { get; }

    /// <summary>侧栏只渲染 ShowInMenu 的页面(关于页这类不进菜单);含 Footer 项,保持既有命令/测试口径。</summary>
    public ObservableCollection<NavigationItem> MenuItems { get; }

    /// <summary>侧栏主列表:除 Footer 之外的菜单项,按 Order/Title 排好序。</summary>
    public ObservableCollection<NavigationItem> PrimaryMenuItems { get; }

    /// <summary>侧栏底部列表(如设置页),同样按 Order/Title 排好序。</summary>
    public ObservableCollection<NavigationItem> FooterMenuItems { get; }

    /// <summary>过滤 ShowInMenu 后按 Order、Title 排序,并解析组标题与组头显示。</summary>
    internal static bool IsNavigationAllowed(NavigationItem item, bool authenticationEnabled,
        IPermissionService? permissions) =>
        item.RequiredAnyPermission.Length == 0
        || !authenticationEnabled
        || permissions is null
        || item.RequiredAnyPermission.Any(permissions.HasPermission);

    private IEnumerable<NavigationItem> VisibleNavigationItems() =>
        NavigationItems.Where(item => IsNavigationAllowed(
            item, DeploymentConfiguration.Current.Authentication.Enabled, _permissions));

    private void OnSessionChanged(object? sender, EventArgs e) =>
        Dispatcher.UIThread.Post(RefreshNavigationVisibility);

    // 登录／注销后按当前权限重建导航;不关闭已打开的标签,页面级授权仍在服务入口执行。
    private void RefreshNavigationVisibility()
    {
        if (_released) return;
        var selectedId = SelectedNavigationItem?.Id;
        var ordered = BuildMenuItems(VisibleNavigationItems());
        MenuItems.Clear();
        PrimaryMenuItems.Clear();
        FooterMenuItems.Clear();
        foreach (var item in ordered)
        {
            MenuItems.Add(item);
            if (item.Section == NavigationSection.Footer) FooterMenuItems.Add(item);
            else PrimaryMenuItems.Add(item);
        }
        var visibleIds = ordered.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var page in FavoritePages.Where(page => !visibleIds.Contains(page.Id)).ToArray())
            FavoritePages.Remove(page);
        foreach (var page in RecentPages.Where(page => !visibleIds.Contains(page.Id)).ToArray())
            RecentPages.Remove(page);
        SelectedNavigationItem = MenuItems.FirstOrDefault(item => item.Id == selectedId)
            ?? MenuItems.FirstOrDefault();
        RefreshFavoriteCommands();
    }

    internal static IReadOnlyList<NavigationItem> BuildMenuItems(IEnumerable<NavigationItem> items)
    {
        // 分区先于 Order:模块不设 Order 也不会跑到任务/系统分区之间。
        var ordered = items
            .Where(item => item.ShowInMenu)
            .OrderBy(item => SectionRank(item.Section))
            .ThenBy(item => item.Order)
            .ThenBy(item => item.Title, StringComparer.CurrentCulture)
            .ToArray();
        string? previousGroup = null;
        foreach (var item in ordered)
        {
            // 模块可覆盖组标题;Business/System 有默认组名,其余分组不显示组头。
            // DisplayGroupTitle 与模块提供的 GroupTitle 分开,语言切换后可重新解析。
            var resolved = item.GroupTitle
                ?? (item.Section == NavigationSection.Business ? Loc.Get("nav.group.business")
                    : item.Section == NavigationSection.System ? Loc.Get("nav.group.system")
                    : null);
            item.DisplayGroupTitle = resolved;
            item.ShowGroupHeader = resolved != null && resolved != previousGroup;
            previousGroup = resolved;
        }
        return ordered;
    }

    /// <summary>切换语言后刷新导航/标签标题、状态栏和已打开页面的自生成文字。</summary>
    private void OnLanguageChanged()
    {
        foreach (var item in NavigationItems) item.RefreshTitle();
        BuildMenuItems(VisibleNavigationItems());
        foreach (var tab in Tabs)
        {
            if (NavigationItems.FirstOrDefault(item => item.Id == tab.Id) is { } source)
                tab.Title = source.Title;
            (tab.Content as IUiLanguageAware)?.OnUiLanguageChanged();
        }
        ShellStatus?.Refresh();
        TabCountText = Loc.Format("shell.status.tabCount", Tabs.Count);
        // 状态行是上一次操作的瞬时文字,切换语言后回到通用就绪提示。
        StatusMessage = Loc.Get("shell.status.ready");
        OnPropertyChanged(string.Empty);
    }

    internal static bool ShouldPreserveUnknownNavigation(bool includeSamples, bool moduleFailed) =>
        !includeSamples && !moduleFailed;

    private static int SectionRank(NavigationSection section) => section switch
    {
        NavigationSection.Workbench => 0,
        NavigationSection.Business => 1,
        NavigationSection.Runtime => 2,
        NavigationSection.System => 3,
        NavigationSection.Footer => 4,
        _ => int.MaxValue,
    };

    public ObservableCollection<WorkspaceTabViewModel> Tabs { get; } = [];
    public ObservableCollection<WorkItem> PanelTasks { get; } = [];
    public IReadOnlyList<PageDetail> PageDetails =>
        (SelectedTab?.Content as IPageDetails)?.Details ?? Array.Empty<PageDetail>();
    public bool HasPageDetails => PageDetails.Count > 0;
    public string PageDetailsTitle => SelectedTab?.Title ?? Loc.Get("shell.details");
    public bool HasPanelTasks => PanelTasks.Count > 0;

    public void RefreshPanelTasks()
    {
        if (_released) return;
        var snapshot = _tasks?.Snapshot() ?? Array.Empty<WorkItem>();
        // Keep unchanged rows so keyboard focus is not lost on each timer tick.
        for (var i = PanelTasks.Count - 1; i >= 0; i--)
            if (!snapshot.Any(item => item.Id == PanelTasks[i].Id)) PanelTasks.RemoveAt(i);
        for (var i = 0; i < snapshot.Count; i++)
        {
            var item = snapshot[i];
            var existing = PanelTasks.FirstOrDefault(row => row.Id == item.Id);
            if (existing is null) PanelTasks.Insert(i, item);
            else
            {
                var index = PanelTasks.IndexOf(existing);
                if (existing != item) PanelTasks[index] = item;
                if (index != i) PanelTasks.Move(index, i);
            }
        }
        OnPropertyChanged(nameof(HasPanelTasks));
    }

    [RelayCommand]
    private void CancelPanelTask(WorkItem? item)
    {
        if (_released || item is null) return;
        _tasks?.Cancel(item.Id);
        RefreshPanelTasks();
    }

    [RelayCommand]
    private void OpenPanelTask(WorkItem? item)
    {
        if (_released || _closingTabs || _exitPrepared || item is null) return;
        OpenRegisteredPage(ShellNavigation.TaskDiagnosticsId);
        if (SelectedTab?.Content is TaskDiagnosticsViewModel page) page.SelectTask(item.Id);
    }

    [RelayCommand]
    private void ShowTaskPanel()
    {
        // 状态栏按钮是开关:已经在任务页签时再点一下收起,而不是无反应。
        if (IsSidePanelVisible && SidePanelTab == 1)
        {
            IsSidePanelVisible = false;
            return;
        }

        IsSidePanelVisible = true;
        SidePanelTab = 1;
        RefreshPanelTasks();
    }

    [RelayCommand]
    private void HideSidePanel() => IsSidePanelVisible = false;
    public CommandMenu NewPageCommand { get; }
    public CommandMenu SavePageCommand { get; }
    public CommandMenu DeletePageCommand { get; }
    public CommandMenu CancelPageCommand { get; }
    public CommandMenu[] ModuleCommands { get; }
    public bool HasModuleCommands => ModuleCommands.Length > 0;

    private ICommand? ResolveContribution(CommandMenuContribution item) =>
        !_closingTabs && !_exitPrepared
            && !(HasModuleFailure && item.PageId == _optionalModule?.PageId)
            && SelectedTab is { } tab && tab.Id == item.PageId
            ? item.ResolveCommand(tab.Content) : null;

    private CommandMenu CreatePageCommand(string key, PageCommandAction action) =>
        new(key, () => _commandContributions
            .Where(item => item.Action == action)
            .Select(ResolveContribution).FirstOrDefault(command => command is not null));

    private void OnCommandPageChanged(object? sender, PropertyChangedEventArgs e)
    {
        foreach (var command in AllPageCommands) command.RefreshTarget();
        RefreshFavoriteCommands();
        if (string.IsNullOrEmpty(e.PropertyName) || e.PropertyName == nameof(IPageDetails.Details))
        {
            OnPropertyChanged(nameof(PageDetails));
            OnPropertyChanged(nameof(HasPageDetails));
            OnPropertyChanged(nameof(PageDetailsTitle));
        }
    }

    public string AppTitle => _branding.Current.ProductName
        + (DeploymentConfiguration.Current.IsProduction ? " · " + Loc.Get("profile.production") : "");

    public string VersionText => $"v{AppInfo.Version}";
    public double SidebarWidth { get; set; } = 260;
    public double SidePanelWidth { get; set; } = 300;
    [ObservableProperty]
    public partial bool IsSidePanelVisible { get; set; }
    [ObservableProperty]
    public partial int SidePanelTab { get; set; }

    [ObservableProperty]
    public partial int LayoutRevision { get; set; }

    public void RememberLayout()
    {
        if (_preferences is not null && !_preferences.SaveLayout(new WorkspaceLayoutV1
        {
            OpenTabIds = Tabs.Select(tab => tab.Id).ToArray(),
            SelectedTabId = SelectedTab?.Id,
            SidebarVisible = IsSidebarVisible,
            SidebarWidth = SidebarWidth,
            SidebarCompact = IsSidebarCompact,
            SidePanelVisible = IsSidePanelVisible,
            SidePanelWidth = SidePanelWidth,
            SidePanelTab = SidePanelTab,
        }))
            StatusMessage = Loc.Get("shell.layout.saveFailed");
    }

    [RelayCommand]
    private void ResetLayout()
    {
        IsSidebarVisible = true;
        IsSidebarCompact = false;
        SidebarWidth = 260;
        IsSidePanelVisible = false;
        SidePanelWidth = 300;
        SidePanelTab = 0;
        LayoutRevision++;
        RememberLayout();
    }

    [ObservableProperty]
    public partial NavigationItem? SelectedNavigationItem { get; set; }

    [ObservableProperty]
    public partial WorkspaceTabViewModel? SelectedTab { get; set; }

    [ObservableProperty]
    public partial bool IsSidebarVisible { get; set; } = true;

    /// <summary>侧栏折叠为图标轨道。与 SidebarVisible 是两件事:折叠仍保留导航入口。</summary>
    [ObservableProperty]
    public partial bool IsSidebarCompact { get; set; }

    /// <summary>折叠时不显示分隔条,避免留下不可拖动的空档。</summary>
    public bool IsSidebarSplitterVisible => IsSidebarVisible && !IsSidebarCompact;

    public string SidebarToggleText =>
        Loc.Get(IsSidebarCompact ? "menu.view.sidebarExpand" : "menu.view.sidebarCompact");

    partial void OnIsSidebarVisibleChanged(bool value) =>
        OnPropertyChanged(nameof(IsSidebarSplitterVisible));

    // 只刷新派生属性。布局持久化必须留在用户动作与退出路径上,
    // 否则构造期间的恢复会立刻覆盖已保存的标签。
    partial void OnIsSidebarCompactChanged(bool value)
    {
        OnPropertyChanged(nameof(IsSidebarSplitterVisible));
        OnPropertyChanged(nameof(SidebarToggleText));
    }

    [RelayCommand]
    private void ToggleSidebarCompact()
    {
        // 从隐藏状态恢复时先显示展开态,避免“点一下只出来一条图标栏”。
        if (!IsSidebarVisible)
        {
            IsSidebarVisible = true;
            IsSidebarCompact = false;
        }
        else IsSidebarCompact = !IsSidebarCompact;
        RememberLayout();
    }

    [ObservableProperty]
    public partial bool HasTabs { get; set; }

    [ObservableProperty]
    public partial string TabCountText { get; set; } = Loc.Format("shell.status.tabCount", 0);

    [ObservableProperty]
    public partial string StatusMessage { get; set; } = Loc.Get("shell.status.ready");

    partial void OnSelectedNavigationItemChanged(NavigationItem? value)
    {
        if (value is not null)
        {
            OpenTab(value);
        }
    }

    partial void OnSelectedTabChanged(WorkspaceTabViewModel? value)
    {
        if (_commandPage is not null) _commandPage.PropertyChanged -= OnCommandPageChanged;
        _commandPage = value?.Content as INotifyPropertyChanged;
        if (_commandPage is not null) _commandPage.PropertyChanged += OnCommandPageChanged;
        OnCommandPageChanged(this, new PropertyChangedEventArgs(null));
        NotifyTabCommands();
        RecordCurrentPage();
        RefreshFavoriteCommands();
    }

    [RelayCommand]
    private void SelectTab(WorkspaceTabViewModel? tab)
    {
        if (!_closingTabs && !_exitPrepared && tab is not null && Tabs.Contains(tab)) SelectedTab = tab;
    }

    /// <summary>同一个导航项只保留一个标签,再次点击时切回已有标签。</summary>
    [RelayCommand]
    private void OpenTab(NavigationItem item)
    {
        if (_released || _closingTabs || _exitPrepared) return;
        if (HasModuleFailure && item.Id == _optionalModule?.PageId) return;
        var existing = Tabs.FirstOrDefault(tab => tab.Id == item.Id);
        if (existing is not null)
        {
            if (SelectedTab == existing) RecordCurrentPage();
            SelectedTab = existing;
            return;
        }

        ViewModelBase content;
        try { content = item.CreateViewModel(); }
        catch (OptionalModuleUnavailableException) when (
            item.Id == _optionalModule?.PageId && HasModuleFailure)
        {
            DisableFailedModulePage();
            return;
        }
        var tab = new WorkspaceTabViewModel(item.Id, item.Title, content);
        Tabs.Add(tab);
        StatusMessage = Loc.Format("shell.status.opened", item.Title);
        SelectedTab = tab;
    }

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private Task CloseTabAsync() => CloseTabItemAsync(SelectedTab);

    /// <summary>标签头上的关闭按钮用;点哪个关哪个,不受当前选中项影响。</summary>
    [RelayCommand]
    private async Task CloseTabItemAsync(WorkspaceTabViewModel? tab)
    {
        if (_released || tab is null || _closingTabs || _exitPrepared)
        {
            return;
        }

        var index = Tabs.IndexOf(tab);
        if (index < 0)
        {
            return;
        }

        _closingTabs = true;
        try
        {
            if (!await CanLeaveTabAsync(tab)) return;
            var wasSelected = SelectedTab == tab;
            Tabs.Remove(tab);
            ReleasePage(tab);
            if (wasSelected) SelectedTab = Tabs.Count > 0 ? Tabs[Math.Min(index, Tabs.Count - 1)] : null;
            StatusMessage = Loc.Format("shell.status.closed", tab.Title);
            RememberLayout();
        }
        finally
        {
            _closingTabs = false;
            OnCommandPageChanged(this, new PropertyChangedEventArgs(null));
        }
    }

    private bool CanCloseTab() => SelectedTab is not null;

    /// <summary>页面关闭时,让实现了 IDisposable 的页面取消还在进行的查询、释放资源(规范 §7.2)。</summary>
    private static void ReleasePage(WorkspaceTabViewModel tab) => (tab.Content as IDisposable)?.Dispose();

    [RelayCommand(CanExecute = nameof(CanCloseTab))]
    private async Task CloseAllTabsAsync()
    {
        if (_released || _closingTabs || _exitPrepared) return;
        _closingTabs = true;
        try
        {
            foreach (var tab in Tabs.ToArray())
                if (!await CanLeaveTabAsync(tab)) return;
            var count = Tabs.Count;
            foreach (var tab in Tabs)
            {
                ReleasePage(tab);
            }

            Tabs.Clear();
            SelectedTab = null;
            StatusMessage = Loc.Format("shell.status.closedAll", count);
            RememberLayout();
        }
        finally
        {
            _closingTabs = false;
            OnCommandPageChanged(this, new PropertyChangedEventArgs(null));
        }
    }

    /// <summary>确认全部页面后才释放；取消时标签集合和布局保持。</summary>
    public async Task<bool> PrepareExitAsync()
    {
        if (_exitPrepared) return true;
        if (_closingTabs) return false;
        _closingTabs = true;
        try
        {
            foreach (var tab in Tabs.ToArray())
                if (!await CanLeaveTabAsync(tab)) return false;
            if (_tasks is not null) await _tasks.StopAsync();
            RememberLayout();
            _exitPrepared = true;
            return true;
        }
        finally
        {
            _closingTabs = false;
            OnCommandPageChanged(this, new PropertyChangedEventArgs(null));
        }
    }

    public void ReleaseWorkspace()
    {
        if (_released) return;
        _released = true;
        if (_optionalModule is not null) _optionalModule.FailedChanged -= OnOptionalModuleFailed;
        if (_tasks is not null) _tasks.Completed -= OnTaskCompleted;
        if (_sessionEvents is not null) _sessionEvents.SessionChanged -= OnSessionChanged;
        Loc.Unsubscribe(_languageChangedHandler);
        if (_commandPage is not null) _commandPage.PropertyChanged -= OnCommandPageChanged;
        _commandPage = null;
        foreach (var command in AllPageCommands) command.Dispose();
        foreach (var tab in Tabs.ToArray()) ReleasePage(tab);
    }

    private async Task<bool> CanLeaveTabAsync(WorkspaceTabViewModel tab)
    {
        if (tab.Content.OptionalModule is { Failed: true, Operation: "ViewConstruction" }) return true;
        if (tab.Content is not IUnsavedChanges changes || await changes.TryLeaveAsync()) return true;
        SelectedTab = tab;
        return false;
    }

    [RelayCommand]
    private void Refresh()
    {
        if (_released) return;
        if (SelectedTab?.Content is IRefreshable refreshable)
        {
            refreshable.Refresh();
            StatusMessage = Loc.Format("shell.status.refreshed", SelectedTab.Title);
            return;
        }

        StatusMessage = Loc.Get("shell.status.nothingToRefresh");
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(AppPaths.DataDirectory, Loc.Get("folder.data"));

    [RelayCommand]
    private void OpenLogFolder() => OpenFolder(AppPaths.LogDirectory, Loc.Get("folder.log"));

    [RelayCommand]
    private void ShowLogs() => OpenRegisteredPage(ShellNavigation.LogsId);

    [RelayCommand]
    private void ShowSettings() => OpenRegisteredPage(ShellNavigation.SettingsId);

    [RelayCommand]
    private void ShowTaskDiagnostics() => OpenRegisteredPage(ShellNavigation.TaskDiagnosticsId);

    [RelayCommand]
    private void ShowHelp(string? topicKey)
    {
        if (_released || _closingTabs || _exitPrepared) return;
        var pageId = SelectedTab?.Id;
        OpenRegisteredPage(ShellNavigation.HelpId);
        if (SelectedTab?.Content is not HelpViewModel help) return;
        if (string.IsNullOrEmpty(topicKey)) help.ShowForPage(pageId);
        else help.ShowTopic(topicKey);
    }

    [RelayCommand]
    private void ShowLicenses() => OpenRegisteredPage(ShellNavigation.LicensesId);

    private void OpenRegisteredPage(string id)
    {
        if (VisibleNavigationItems().FirstOrDefault(item => item.Id == id) is { } item) OpenTab(item);
    }

    /// <summary>关于页和其余页面走同一条注册路径:从注入的导航项里按 Id 找,不在 Shell 里造页面。</summary>
    [RelayCommand]
    private void ShowAbout()
    {
        var about = NavigationItems.FirstOrDefault(item => item.Id == ShellNavigation.AboutId);
        if (about is not null)
        {
            OpenTab(about);
        }
    }

    [RelayCommand]
    private void Exit() => _lifetime.MainWindow?.Close();

    private void OpenFolder(string path, string displayName) =>
        StatusMessage = _folderService.OpenWithMessage(path, displayName);

    private void OnTabsChanged()
    {
        HasTabs = Tabs.Count > 0;
        TabCountText = Loc.Format("shell.status.tabCount", Tabs.Count);
        NotifyTabCommands();
    }

    private void NotifyTabCommands()
    {
        CloseTabCommand.NotifyCanExecuteChanged();
        CloseAllTabsCommand.NotifyCanExecuteChanged();
    }
}
