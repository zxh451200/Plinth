using System;
using System.IO;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Linq;
using Microsoft.Extensions.Logging;
using Plinth.App.Localization;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

/// <summary>备份与恢复页。选文件属于界面操作,在 View 的代码后置里做,选好的路径传给这里的方法。</summary>
public partial class BackupViewModel : ViewModelBase
{
    private readonly BackupService _backup;
    private readonly ILogger<BackupViewModel> _logger;
    private readonly TaskService? _tasks;
    private readonly SensitiveOperationService _operations;
    private string? _restorePath;
    private byte[]? _restoreHash;
    private BackupService.BackupPreview? _backupPreview;
    private CancellationTokenSource? _backupPreviewCancellation;
    private CancellationTokenSource? _restorePreviewCancellation;
    private int _pageVersion;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanChooseFile))]
    [NotifyPropertyChangedFor(nameof(CanConfirmRestore))]
    public partial bool IsBusy { get; private set; }
    public bool CanChooseFile => !IsBusy;
    [ObservableProperty]
    public partial string CreatePreview { get; private set; } = "";
    public bool HasBackupPreview => _backupPreview is not null;

    public void ClearBackupPreview()
    {
        _backupPreviewCancellation?.Cancel();
        _backupPreview = null;
        CreatePreview = "";
        OnPropertyChanged(nameof(HasBackupPreview));
    }

    public void ClearRestorePreview()
    {
        _restorePreviewCancellation?.Cancel();
        _restorePath = null;
        _restoreHash = null;
        RestorePreview = "";
        RestoreConfirmed = false;
        OnPropertyChanged(nameof(HasRestorePreview));
    }

    public void PageClosed()
    {
        _pageVersion++;
        ClearBackupPreview();
        ClearRestorePreview();
    }

    public async Task PreviewBackupAsync(string path)
    {
        if (IsBusy) return;
        ClearBackupPreview();
        ClearRestorePreview();
        StatusText = "";
        IsBusy = true;
        var version = _pageVersion;
        using var cancellation = new CancellationTokenSource();
        _backupPreviewCancellation = cancellation;
        try
        {
            var preview = await _backup.PreviewBackupAsync(path, cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != _pageVersion || !ReferenceEquals(_backupPreviewCancellation, cancellation)) return;
            _backupPreview = preview;
            CreatePreview = Loc.Format("backup.createPreview.summary", preview.Destination, preview.EstimatedBytes) +
                "\n" + string.Join("\n", preview.Files.Select(file => $"{file.Path} · ~{file.EstimatedBytes} B")) +
                "\n" + Loc.Get("backup.createPreview.estimateNote") +
                "\n" + Loc.Get("backup.createPreview.excluded");
            OnPropertyChanged(nameof(HasBackupPreview));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (version == _pageVersion)
            {
                _logger.LogError(ex, "备份预览失败");
                ClearBackupPreview();
                StatusText = Loc.Get("backup.status.failed");
            }
        }
        finally
        {
            if (ReferenceEquals(_backupPreviewCancellation, cancellation)) _backupPreviewCancellation = null;
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ConfirmBackupAsync()
    {
        if (IsBusy || _backupPreview is not { } preview) return;
        ClearBackupPreview(); // 消耗本次预览，重复点击不能再次提交。
        await BackupToAsync(preview.Destination);
    }

    public BackupViewModel(BackupService backup, ILogger<BackupViewModel> logger,
        TaskService? tasks = null, SensitiveOperationService? operations = null)
    {
        _backup = backup;
        _logger = logger;
        _tasks = tasks;
        _operations = operations ?? backup.Operations;
        if (BackupService.HasPendingRestore())
        {
            StatusText = Loc.Get("backup.status.pending");
        }
    }

    /// <summary>操作结果或失败原因。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>默认的备份文件名,带日期,选择框里直接用。</summary>
    public static string SuggestedFileName => $"plinth-backup-{DateTime.Now:yyyyMMdd-HHmmss}.zip";

    [ObservableProperty]
    public partial string RestorePreview { get; set; } = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanConfirmRestore))]
    public partial bool RestoreConfirmed { get; set; }
    public bool CanConfirmRestore => RestoreConfirmed && !IsBusy;
    public bool HasRestorePreview => _restorePath is not null;

    public async Task PreviewRestoreAsync(string path)
    {
        if (IsBusy) return;
        ClearBackupPreview();
        _restorePath = null;
        _restoreHash = null;
        RestoreConfirmed = false;
        RestorePreview = "";
        OnPropertyChanged(nameof(HasRestorePreview));
        IsBusy = true;
        var version = _pageVersion;
        using var cancellation = new CancellationTokenSource();
        _restorePreviewCancellation = cancellation;
        try
        {
            var preview = await Task.Run(
                () => BackupService.PreviewRestore(path, cancellation.Token), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (version != _pageVersion) return;
            _restorePath = path;
            _restoreHash = preview.Hash;
            RestorePreview = Loc.Format("backup.preview.summary",
                preview.Manifest.ApplicationVersion, preview.Manifest.SchemaVersion,
                preview.Manifest.CreatedUtc) + "\n" +
                string.Join("\n", preview.Manifest.Files.Select(f => $"{f.Path} · {f.Size} · SHA-256 {f.Sha256}"));
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (version == _pageVersion) StatusText = Loc.Get("backup.status.failed"); }
        finally
        {
            _restorePreviewCancellation = null;
            IsBusy = false;
        }
        OnPropertyChanged(nameof(HasRestorePreview));
    }

    [RelayCommand]
    private async Task ConfirmRestoreAsync()
    {
        if (IsBusy || !RestoreConfirmed || _restorePath is null) return;
        ClearBackupPreview();
        var path = _restorePath;
        var hash = _restoreHash;
        IsBusy = true;
        try
        {
            await RunAsync("backup.restore", "backup:restore", async token =>
            {
                await using var stream = File.OpenRead(path);
                var actualHash = await System.Security.Cryptography.SHA256.HashDataAsync(stream, token);
                if (hash is null || !hash.AsSpan().SequenceEqual(actualHash))
                    throw new IOException("Backup changed after preview.");
                // StageRestore copies and validates again; expected hash is checked on the staged copy.
                var error = await Task.Run(
                    () => BackupService.StageRestore(path, expectedHash: hash, cancellationToken: token), token);
                if (error is not null) throw new IOException("Restore staging failed.");
            });
        }
        catch (OperationCanceledException) { }
        catch (Exception) { StatusText = Loc.Get("backup.status.failed"); }
        finally { IsBusy = false; }
        _restorePath = null;
        _restoreHash = null;
        RestoreConfirmed = false;
        OnPropertyChanged(nameof(HasRestorePreview));
    }

    private async Task RunAsync(string title, string permission,
        Func<System.Threading.CancellationToken, Task> work)
    {
        var version = _pageVersion;
        var confirmation = await _operations.PrepareConfirmedAsync(permission, "Backup", title);
        if (version != _pageVersion) return;
        async Task Execute(System.Threading.CancellationToken token)
        {
            await confirmation.ExecuteAsync(
                async ct => { await work(ct); return true; }, token);
        }
        if (_tasks is null) await Execute(default);
        else
        {
            _tasks.Enqueue(Loc.Get(title), "Backup", (progress, token) =>
            {
                progress.Report(null);
                return Execute(token);
            });
            StatusText = Loc.Get("tasks.queued");
        }
    }

    public async Task BackupToAsync(string path)
    {
        if (IsBusy) return;
        ClearBackupPreview();
        IsBusy = true;
        var version = _pageVersion;
        try
        {
            var confirmation = await _backup.ConfirmOperationAsync();
            if (version != _pageVersion) return;
            if (_tasks is null)
            {
                await _backup.BackupAfterConfirmationAsync(path, confirmation, default);
                if (version == _pageVersion) StatusText = Loc.Format("backup.status.done", path);
            }
            else
            {
                var id = _tasks.Enqueue(Loc.Get("backup.create"), "Backup", async (progress, token) =>
                {
                    await _backup.BackupAfterConfirmationAsync(path, confirmation, token, progress);
                });
                StatusText = Loc.Get("tasks.queued");
                await _tasks.WaitAsync(id); // 页面离开不会取消全局任务。
                if (version == _pageVersion)
                {
                    var item = _tasks.Snapshot().FirstOrDefault(item => item.Id == id);
                    StatusText = item?.Status == WorkStatus.Succeeded
                        ? Loc.Format("backup.status.done", path)
                        : Loc.Get(item?.Status == WorkStatus.Cancelled ? "backup.status.cancelled" : "backup.status.failed");
                }
            }
        }
        catch (OperationCanceledException)
        {
            if (version == _pageVersion) StatusText = Loc.Get("backup.status.cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "备份失败:{Path}", path);
            if (version == _pageVersion) StatusText = Loc.Get("backup.status.failed");
        }
        finally { IsBusy = false; }
    }

    public void ReportPickerFailure()
    {
        ClearBackupPreview();
        StatusText = Loc.Get("backup.status.failed");
    }

    public async Task RestoreFromAsync(string path)
    {
        await PreviewRestoreAsync(path);
    }
}
