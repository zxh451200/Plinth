using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using System;
using Plinth.App.Modules.RemoteDemo.ViewModels;
using Plinth.App.Navigation;

namespace Plinth.App.Modules.RemoteDemo.Views;

public partial class RemoteDemoListView : UserControl, IPageSearchTarget
{
    private readonly DispatcherTimer _accessTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public RemoteDemoListView()
    {
        InitializeComponent();
        _accessTimer.Tick += (_, _) => (DataContext as RemoteDemoListViewModel)?.RefreshAccess();
        AttachedToVisualTree += (_, _) =>
        {
            (DataContext as RemoteDemoListViewModel)?.RefreshAccess();
            _accessTimer.Start();
        };
        DetachedFromVisualTree += (_, _) => _accessTimer.Stop();
        DataContextChanged += (_, _) => (DataContext as RemoteDemoListViewModel)?.RefreshAccess();
        RecordsTable.SelectionChanged += (_, _) =>
        {
            if (DataContext is RemoteDemoListViewModel viewModel
                && !ReferenceEquals(RecordsTable.SelectedItem, viewModel.SelectedItem))
                RecordsTable.SelectedItem = viewModel.SelectedItem;
        };
    }

    public void FocusSearch()
    {
        if (DataContext is not RemoteDemoListViewModel { CanInteract: true }) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void OnEditRecordClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is RemoteDemoListViewModel { CanInteract: true, CanDelete: true })
            EditCodeBox.Focus();
    }
}
