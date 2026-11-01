using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;

namespace Plinth.App.Views;

public partial class MainWindow : Window
{
    private readonly double _sidebarMinWidth;
    private double _sidebarWidth;

    private INotifyPropertyChanged? _trackedDataContext;

    public MainWindow()
    {
        InitializeComponent();

        // 默认宽度和下限只在 XAML 的 ColumnDefinition 上写一份,这里读回来用于隐藏后还原
        var column = ShellGrid.ColumnDefinitions[0];
        _sidebarMinWidth = column.MinWidth;
        _sidebarWidth = column.Width.Value;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_trackedDataContext is not null)
        {
            _trackedDataContext.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _trackedDataContext = DataContext as INotifyPropertyChanged;

        if (_trackedDataContext is not null)
        {
            _trackedDataContext.PropertyChanged += OnViewModelPropertyChanged;
        }

        ApplySidebarVisibility();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.IsSidebarVisible))
        {
            ApplySidebarVisibility();
        }
    }

    /// <summary>
    /// 侧栏宽度归第 0 列所有(分隔条改的就是它),所以隐藏时必须连列一起归零,
    /// 否则只藏住 Border 会留下一条空列。
    /// </summary>
    private void ApplySidebarVisibility()
    {
        var column = ShellGrid.ColumnDefinitions[0];
        var visible = DataContext is not MainViewModel viewModel || viewModel.IsSidebarVisible;

        if (visible)
        {
            column.MinWidth = _sidebarMinWidth;
            column.Width = new GridLength(_sidebarWidth);
            return;
        }

        if (column.Width.IsAbsolute)
        {
            _sidebarWidth = column.Width.Value;
        }

        column.MinWidth = 0;
        column.Width = new GridLength(0);
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
