using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Plinth.App.Localization;
using Plinth.App.Services;
using Avalonia.Layout;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using Avalonia.Threading;

namespace Plinth.App.Views;

public partial class MainWindow : Window
{
    private readonly double _sidebarMinWidth;
    private double _sidebarWidth;
    private double _sidePanelWidth = 300;
    private readonly DispatcherTimer _panelTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private INotifyPropertyChanged? _trackedDataContext;
    private bool _closeApproved;
    public bool CloseApproved => _closeApproved;
    private bool _closePending;
    private long _closeContextVersion;

    // Avalonia 12 的 TabControl 容器不在窗口 Tab 顺序内；Tab 落在标签条时先进入当前标签头。
    private void OnWorkspaceTabsKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled || e.Key != Key.Tab || e.KeyModifiers != KeyModifiers.None) return;
        // 只处理焦点停在标签条本身的 Tab；标签头内部的 Tab 必须继续冒泡。
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is not TabControl) return;
        if (WorkspaceTabs.SelectedItem is not { } selected
            || WorkspaceTabs.ContainerFromItem(selected) is not TabItem tab) return;
        e.Handled = true;
        var close = tab.GetVisualDescendants().OfType<Button>()
            .FirstOrDefault(button => ReferenceEquals(button.CommandParameter, selected));
        close?.Focus(NavigationMethod.Tab, KeyModifiers.None);
    }

    public MainWindow()
    {
        AppShortcuts.Validate(AppShortcuts.All);
        InitializeComponent();
        WorkspaceTabs.KeyDown += OnWorkspaceTabsKeyDown;
        EditMenu.SubmenuOpened += (_, _) => FindPageMenu.IsEnabled = GetActiveSearchTarget() is not null;

        // 默认宽度和下限只在 XAML 的 ColumnDefinition 上写一份,这里读回来用于隐藏后还原
        var column = ShellGrid.ColumnDefinitions[0];
        _sidebarMinWidth = column.MinWidth;
        _sidebarWidth = column.Width.Value;
        column.PropertyChanged += (_, change) =>
        {
            if (change.Property == ColumnDefinition.WidthProperty && column.Width.IsAbsolute
                && DataContext is MainViewModel { IsSidebarVisible: true, IsSidebarCompact: false } vm)
                vm.SidebarWidth = _sidebarWidth = column.Width.Value;
        };
        var sideColumn = ShellGrid.ColumnDefinitions[4];
        sideColumn.PropertyChanged += (_, change) =>
        {
            if (change.Property == ColumnDefinition.WidthProperty && sideColumn.Width.IsAbsolute
                && DataContext is MainViewModel { IsSidePanelVisible: true } vm)
                vm.SidePanelWidth = _sidePanelWidth = sideColumn.Width.Value;
        };
        _panelTimer.Tick += (_, _) =>
        {
            (DataContext as MainViewModel)?.ShellStatus?.Refresh();
            if (DataContext is MainViewModel { IsSidePanelVisible: true, SidePanelTab: 1 } vm)
                vm.RefreshPanelTasks();
        };
        Opened += (_, _) => _panelTimer.Start();
        Closed += (_, _) =>
        {
            _panelTimer.Stop();
            if (DataContext is MainViewModel vm)
            {
                vm.Changes.PromptAsync = null;
                vm.ReleaseWorkspace();
            }
            if (_trackedDataContext is not null)
                _trackedDataContext.PropertyChanged -= OnViewModelPropertyChanged;
            Serilog.Log.Information("工作区已关闭");
        };
    }

    // 使用冒泡且不监听已处理事件，保留文本框和弹出控件自己的按键行为。
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || DataContext is not MainViewModel vm) return;
        if (e.KeyModifiers == KeyModifiers.Control && e.Key == Key.K)
        {
            if (vm.IsQuickOpen) vm.CloseQuickOpenCommand.Execute(null);
            else vm.OpenQuickOpenCommand.Execute(null);
            e.Handled = true;
            return;
        }
        if (e.KeyModifiers == KeyModifiers.Alt && !IsTextInput(e.Source)
            && TryOpenNumberedPage(vm, e.Key))
        {
            e.Handled = true;
            return;
        }
        var action = AppShortcuts.Match(e, IsTextInput(e.Source));
        if (action == ShortcutAction.Find)
        {
            e.Handled = FindActivePage();
            return;
        }
        ICommand? command = action switch
        {
            ShortcutAction.New => vm.NewPageCommand,
            ShortcutAction.Save => vm.SavePageCommand,
            ShortcutAction.Delete => vm.DeletePageCommand,
            ShortcutAction.Cancel => vm.CancelPageCommand,
            ShortcutAction.CloseTab => vm.CloseTabCommand,
            ShortcutAction.Refresh => vm.RefreshCommand,
            ShortcutAction.Help => vm.ShowHelpCommand,
            _ => null,
        };
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
            e.Handled = true;
        }
    }

    private void OnQuickOpenScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is MainViewModel vm) vm.CloseQuickOpenCommand.Execute(null);
        e.Handled = true;
    }

    private void OnQuickOpenKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        switch (e.Key)
        {
            case Key.Escape:
                vm.CloseQuickOpenCommand.Execute(null);
                e.Handled = true;
                break;
            case Key.Enter:
                vm.AcceptQuickOpenCommand.Execute(vm.QuickOpenSelection);
                e.Handled = true;
                break;
            case Key.Down:
                MoveQuickOpenSelection(vm, 1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveQuickOpenSelection(vm, -1);
                e.Handled = true;
                break;
        }
    }

    private static void MoveQuickOpenSelection(MainViewModel vm, int delta)
    {
        if (vm.QuickOpenItems.Count == 0) return;
        var index = vm.QuickOpenSelection is null ? -1 : vm.QuickOpenItems.IndexOf(vm.QuickOpenSelection);
        index = Math.Clamp(index + delta, 0, vm.QuickOpenItems.Count - 1);
        vm.QuickOpenSelection = vm.QuickOpenItems[index];
    }

    private static bool IsTextInput(object? source) => source is TextBox
        || source is Visual visual && visual.GetVisualAncestors().Any(parent => parent is TextBox);

    private static bool TryOpenNumberedPage(MainViewModel vm, Key key)
    {
        var index = key switch
        {
            Key.D1 or Key.NumPad1 => 0,
            Key.D2 or Key.NumPad2 => 1,
            Key.D3 or Key.NumPad3 => 2,
            Key.D4 or Key.NumPad4 => 3,
            Key.D5 or Key.NumPad5 => 4,
            Key.D6 or Key.NumPad6 => 5,
            Key.D7 or Key.NumPad7 => 6,
            Key.D8 or Key.NumPad8 => 7,
            Key.D9 or Key.NumPad9 => 8,
            Key.D0 or Key.NumPad0 => -1,
            _ => -2,
        };
        var item = index switch
        {
            -2 => null,
            -1 => vm.FooterMenuItems.FirstOrDefault(),
            _ => vm.PrimaryMenuItems.ElementAtOrDefault(index),
        };
        if (item is null || !vm.OpenTabCommand.CanExecute(item)) return false;
        vm.OpenTabCommand.Execute(item);
        return true;
    }

    private IPageSearchTarget? GetActiveSearchTarget()
    {
        if (DataContext is not MainViewModel { SelectedTab: { } tab }) return null;
        return this.GetVisualDescendants().OfType<Control>()
            .FirstOrDefault(control => control is IPageSearchTarget && control.IsVisible
                && ReferenceEquals(control.DataContext, tab.Content)) as IPageSearchTarget;
    }

    private bool FindActivePage()
    {
        var target = GetActiveSearchTarget();
        if (target is null) return false;
        target.FocusSearch();
        return true;
    }

    private void OnFindPageClick(object? sender, RoutedEventArgs e) => e.Handled = FindActivePage();

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        _closeContextVersion++;
        _closeApproved = false;

        if (_trackedDataContext is not null)
        {
            _trackedDataContext.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (_trackedDataContext is MainViewModel previous) previous.Changes.PromptAsync = null;
        _trackedDataContext = DataContext as INotifyPropertyChanged;

        if (_trackedDataContext is not null)
        {
            _trackedDataContext.PropertyChanged += OnViewModelPropertyChanged;
        }

        if (DataContext is MainViewModel vm)
        {
            _sidebarWidth = vm.SidebarWidth;
            _sidePanelWidth = vm.SidePanelWidth;
            ShellGrid.ColumnDefinitions[0].Width = new GridLength(_sidebarWidth);
            vm.Changes.PromptAsync = ShowUnsavedChangesAsync;
        }
        ApplySidebarVisibility();
        ApplySidePanelVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.LayoutRevision) && DataContext is MainViewModel vm)
        {
            _sidebarWidth = vm.SidebarWidth;
            _sidePanelWidth = vm.SidePanelWidth;
            ApplySidebarVisibility();
            ApplySidePanelVisibility();
        }
        if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible))
        {
            ApplySidebarVisibility();
        }
        if (e.PropertyName == nameof(MainViewModel.IsSidebarCompact))
        {
            ApplySidebarVisibility();
        }
        if (e.PropertyName == nameof(MainViewModel.IsQuickOpen)
            && DataContext is MainViewModel { IsQuickOpen: true })
        {
            Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                QuickOpenBox.Focus();
                QuickOpenBox.SelectAll();
            });
        }
        if (e.PropertyName == nameof(MainViewModel.IsSidePanelVisible))
            ApplySidePanelVisibility();
        if ((e.PropertyName is nameof(MainViewModel.IsSidePanelVisible) or nameof(MainViewModel.SidePanelTab))
            && DataContext is MainViewModel { IsSidePanelVisible: true, SidePanelTab: 1 } tasks)
            tasks.RefreshPanelTasks();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (!_closeApproved && DataContext is MainViewModel vm)
        {
            if (_closePending) e.Cancel = true;
            else
            {
                vm.SidebarWidth = vm.IsSidebarVisible && !vm.IsSidebarCompact
                    ? ShellGrid.ColumnDefinitions[0].Width.Value
                    : _sidebarWidth;
                vm.SidePanelWidth = vm.IsSidePanelVisible ? ShellGrid.ColumnDefinitions[4].Width.Value : _sidePanelWidth;
                var contextVersion = _closeContextVersion;
                var preparation = vm.PrepareExitAsync();
                if (preparation.IsCompletedSuccessfully)
                {
                    _closeApproved = contextVersion == _closeContextVersion
                        && ReferenceEquals(DataContext, vm) && preparation.Result;
                    e.Cancel = !_closeApproved;
                }
                else
                {
                    e.Cancel = true;
                    _closePending = true;
                    _ = RequestCloseAsync(preparation, vm, contextVersion);
                }
            }
        }
        // 确认前不触发宿主的 Closing 订阅，避免取消时提前写窗口状态。
        if (!e.Cancel) base.OnClosing(e);
    }

    private async Task RequestCloseAsync(Task<bool> preparation, MainViewModel owner, long contextVersion)
    {
        try
        {
            if (!await preparation) return;
            if (contextVersion != _closeContextVersion || !ReferenceEquals(DataContext, owner)) return;
            _closeApproved = true;
            Close();
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "检查工作区关闭条件失败");
            if (contextVersion == _closeContextVersion && ReferenceEquals(DataContext, owner))
                owner.StatusMessage = Loc.Get("shell.unsaved.failed");
        }
        finally { _closePending = false; }
    }

    private Task<UnsavedChangesChoice> ShowUnsavedChangesAsync()
    {
        var dialog = new Window
        {
            Title = Loc.Get("shell.unsaved.title"),
            Width = 420,
            Height = 180,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var (key, choice) in new[]
        {
            ("shell.unsaved.save", UnsavedChangesChoice.Save),
            ("shell.unsaved.discard", UnsavedChangesChoice.Discard),
            ("shell.unsaved.cancel", UnsavedChangesChoice.Cancel),
        })
        {
            var button = new Button { Content = Loc.Get(key) };
            button.Click += (_, _) => dialog.Close(choice);
            buttons.Children.Add(button);
        }
        var content = new StackPanel { Margin = new Thickness(20), Spacing = 20 };
        content.Children.Add(new TextBlock { Text = Loc.Get("shell.unsaved.message"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        content.Children.Add(buttons);
        dialog.Content = content;
        return dialog.ShowDialog<UnsavedChangesChoice>(this);
    }

    public Task<bool> ConfirmProductionAsync()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            Dispatcher.UIThread.Post(async () =>
            {
                try { completion.TrySetResult(await ConfirmProductionAsync()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
            return completion.Task;
        }
        if (!IsVisible || _closeApproved) return Task.FromResult(false);
        var dialog = new Window
        {
            Title = Loc.Get("profile.production"),
            Width = 440,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
        };
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 12 };
        panel.Children.Add(new TextBlock { Text = Loc.Get("profile.confirm"), TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        var confirm = new Button { Content = Loc.Get("common.confirm") };
        confirm.Click += (_, _) => dialog.Close(true);
        var cancel = new Button { Content = Loc.Get("shell.unsaved.cancel") };
        cancel.Click += (_, _) => dialog.Close(false);
        panel.Children.Add(confirm);
        panel.Children.Add(cancel);
        dialog.Content = panel;
        return dialog.ShowDialog<bool>(this);
    }

    /// <summary>
    /// 侧栏宽度归第 0 列所有(分隔条改的就是它),所以隐藏时必须连列一起归零,
    /// 否则只藏住 Border 会留下一条空列。折叠成图标轨道时列宽固定 48,
    /// 用户宽度留在 _sidebarWidth 里,展开时原样还原。
    /// </summary>
    private void ApplySidebarVisibility()
    {
        var column = ShellGrid.ColumnDefinitions[0];
        var viewModel = DataContext as MainViewModel;
        var visible = viewModel?.IsSidebarVisible ?? true;

        if (visible && viewModel is { IsSidebarCompact: true })
        {
            column.MinWidth = 48;
            column.Width = new GridLength(48);
            return;
        }

        if (visible)
        {
            column.MinWidth = _sidebarMinWidth;
            column.Width = new GridLength(_sidebarWidth);
            return;
        }

        if (column.Width.IsAbsolute && column.Width.Value > 0
            && viewModel is not { IsSidebarCompact: true })
        {
            // 折叠态的 48px 不是用户宽度,隐藏时不能覆盖展开宽度。
            _sidebarWidth = column.Width.Value;
        }

        column.MinWidth = 0;
        column.Width = new GridLength(0);
    }

    private void ApplySidePanelVisibility()
    {
        var column = ShellGrid.ColumnDefinitions[4];
        if (DataContext is MainViewModel { IsSidePanelVisible: true })
        {
            column.MinWidth = 240;
            column.Width = new GridLength(_sidePanelWidth);
        }
        else
        {
            if (column.Width.IsAbsolute && column.Width.Value > 0) _sidePanelWidth = column.Width.Value;
            column.MinWidth = 0;
            column.Width = new GridLength(0);
        }
    }

    /// <summary>
    /// 主列表和底部列表都只对 VM 做单向同步,选中变化统一从这里回写;
    /// 另一个列表被同步清空选择时会带着 null 进来,模式匹配直接跳过。
    /// </summary>
    private void OnNavigationSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ListBox { SelectedItem: NavigationItem item }
            && DataContext is MainViewModel viewModel
            && !ReferenceEquals(viewModel.SelectedNavigationItem, item))
            viewModel.SelectedNavigationItem = item;
    }

    /// <summary>
    /// 已经选中的导航项再点一次不会触发 SelectedItem 变化,这里补一次打开;
    /// OpenTab 对同一个 Id 是幂等的,不会重复开标签。
    /// </summary>
    private void OnNavigationTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is ListBox { SelectedItem: NavigationItem item })
        {
            viewModel.OpenTabCommand.Execute(item);
        }
    }
}
