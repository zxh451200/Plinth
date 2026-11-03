using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Plinth.App.ViewModels;
using Plinth.App.Navigation;
using System;
using Avalonia.Threading;
using Plinth.App.Services;

namespace Plinth.App.Views;

public partial class LogView : UserControl, IPageSearchTarget
{
    private readonly DispatcherTimer _accessTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    public void FocusSearch() => LogSearch.Focus();
    public LogView()
    {
        InitializeComponent();
        _accessTimer.Tick += (_, _) => (DataContext as LogViewModel)?.RefreshAccess();
        AttachedToVisualTree += async (_, _) =>
        {
            try
            {
                _accessTimer.Start();
                if (DataContext is LogViewModel vm) await vm.PageOpenedAsync();
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _accessTimer.Stop();
            (DataContext as LogViewModel)?.PageClosed();
        };
    }

    private async void CopyDetails(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm || vm.GetCopyText(false) is not { } text) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception) { vm.ReportCopyFailure(); }
    }

    private async void CopyId(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not LogViewModel vm || vm.GetCopyText(true) is not { } text) return;
        try
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
        }
        catch (Exception) { vm.ReportCopyFailure(); }
    }
}
