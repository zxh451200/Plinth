using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Plinth.App.Localization;
using Plinth.App.ViewModels;

namespace Plinth.App.Views;

public partial class BackupView : UserControl
{
    private bool _picking;
    private int _pageVersion;
    private BackupViewModel? _viewModel;
    public BackupView()
    {
        InitializeComponent();
        DetachedFromVisualTree += (_, _) =>
        {
            _pageVersion++;
            (DataContext as BackupViewModel)?.PageClosed();
        };
        DataContextChanged += (_, _) =>
        {
            _pageVersion++;
            _viewModel?.PageClosed();
            _viewModel = DataContext as BackupViewModel;
        };
    }

    private static FilePickerFileType BackupFileType => new(Loc.Get("backup.fileType")) { Patterns = ["*.zip"] };

    private async void OnBackupClick(object? sender, RoutedEventArgs e)
    {
        if (_picking || DataContext is not BackupViewModel viewModel || viewModel.IsBusy ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _picking = true;
        var version = _pageVersion;
        viewModel.ClearBackupPreview();
        viewModel.ClearRestorePreview();
        try
        {
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("backup.pickSave"),
                SuggestedFileName = BackupViewModel.SuggestedFileName,
                DefaultExtension = "zip",
                FileTypeChoices = [BackupFileType],
                ShowOverwritePrompt = false,
            });

            if (version == _pageVersion && ReferenceEquals(DataContext, viewModel) && file?.TryGetLocalPath() is { } path)
            {
                await viewModel.PreviewBackupAsync(path);
            }
        }
        catch (Exception) { if (version == _pageVersion) viewModel.ReportPickerFailure(); }
        finally { _picking = false; }
    }

    private async void OnBackupAndExitClick(object? sender, RoutedEventArgs e)
    {
        if (_picking || DataContext is not BackupViewModel viewModel ||
            TopLevel.GetTopLevel(this) is not MainWindow window) return;
        await viewModel.ConfirmBackupAndExitAsync(window.RequestExitAsync);
    }

    private async void OnRestoreClick(object? sender, RoutedEventArgs e)
    {
        if (_picking || DataContext is not BackupViewModel viewModel || viewModel.IsBusy ||
            TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        _picking = true;
        var version = _pageVersion;
        viewModel.ClearBackupPreview();
        viewModel.ClearRestorePreview();
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.Get("backup.pickOpen"),
                AllowMultiple = false,
                FileTypeFilter = [BackupFileType],
            });

            if (version == _pageVersion && ReferenceEquals(DataContext, viewModel) &&
                files.Count == 1 && files[0].TryGetLocalPath() is { } path)
            {
                await viewModel.RestoreFromAsync(path);
            }
        }
        catch (Exception) { if (version == _pageVersion) viewModel.ReportPickerFailure(); }
        finally { _picking = false; }
    }
}
