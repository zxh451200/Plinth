using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Plinth.App.Localization;
using Plinth.App.ViewModels;

namespace Plinth.App.Views;

public partial class TaskDiagnosticsView : UserControl
{
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _lifetimeVersion;

    public TaskDiagnosticsView()
    {
        InitializeComponent();
        _timer.Tick += (_, _) => (DataContext as TaskDiagnosticsViewModel)?.RefreshTasks();
        AttachedToVisualTree += (_, _) =>
        {
            (DataContext as TaskDiagnosticsViewModel)?.RefreshTasks();
            _timer.Start();
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _lifetimeVersion++;
            _timer.Stop();
            (DataContext as TaskDiagnosticsViewModel)?.PageClosed();
        };
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        _lifetimeVersion++;
        base.OnDataContextChanged(e);
    }

    private async void ExportClick(object? sender, RoutedEventArgs e) =>
        await ExportWithPickerAsync(async top =>
        {
            var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("diagnostics.export"),
                SuggestedFileName = $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss}.zip",
                DefaultExtension = "zip",
                ShowOverwritePrompt = false, // service never overwrites an existing file
                FileTypeChoices = [new FilePickerFileType("ZIP") { Patterns = ["*.zip"] }],
            });
            return file?.TryGetLocalPath();
        });

    internal async Task ExportWithPickerAsync(Func<TopLevel, Task<string?>> pickPath)
    {
        if (DataContext is not TaskDiagnosticsViewModel vm || TopLevel.GetTopLevel(this) is not { } top) return;
        var version = _lifetimeVersion;
        var pageVersion = vm.PageVersion;
        bool IsCurrent() => version == _lifetimeVersion && pageVersion == vm.PageVersion
            && ReferenceEquals(DataContext, vm) && ReferenceEquals(TopLevel.GetTopLevel(this), top)
            && (top is not Window window || window.IsVisible);
        try
        {
            var path = await pickPath(top);
            if (IsCurrent() && path is not null) await vm.ExportAsync(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (IsCurrent()) vm.ReportExportFailure(); }
    }
}
