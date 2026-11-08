using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

public partial class TaskDiagnosticsViewModel(TaskService tasks, DiagnosticService diagnostics,
    SensitiveOperationService? operations = null) : ViewModelBase, IDisposable
{
    private readonly SensitiveOperationService _operations = operations ??
        SensitiveOperationService.CreateDefault(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    private DiagnosticPreview? _preview;
    private CancellationTokenSource? _previewCancellation;
    private bool _disposed;
    private int _pageVersion;
    internal int PageVersion => _pageVersion;
    public ObservableCollection<WorkItem> Items { get; } = [];
    public ObservableCollection<DiagnosticFile> Files { get; } = [];
    /// <summary>未选中任务时取消按钮禁用;bool 目标不允许绑可空链,否则空选中会记绑定告警。</summary>
    public bool CanCancelSelected => SelectedItem?.CanCancel == true;
    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RetryTaskCommand))]
    [NotifyPropertyChangedFor(nameof(CanCancelSelected))]
    public partial WorkItem? SelectedItem { get; set; }
    [ObservableProperty] public partial DateTimeOffset? From { get; set; }
    [ObservableProperty] public partial DateTimeOffset? To { get; set; }
    [ObservableProperty] public partial string Level { get; set; } = "";
    // 下拉显示当前语言的名字,筛选值仍用日志里的英文标记。
    private string[] _levelOptions = BuildLevelOptions();
    public string[] LevelOptions
    {
        get => _levelOptions;
        private set { _levelOptions = value; OnPropertyChanged(); }
    }

    private static string[] BuildLevelOptions() =>
        [Loc.Get("logs.level.all"), .. LogLevels.Names.Select(LogLevels.DisplayName)];

    public override void OnUiLanguageChanged()
    {
        LevelOptions = BuildLevelOptions();
        base.OnUiLanguageChanged();
    }
    [ObservableProperty] public partial int LevelIndex { get; set; }
    partial void OnLevelIndexChanged(int value)
    {
        Level = value is > 0 && value <= LogLevels.Names.Length ? LogLevels.Names[value - 1] : "";
    }
    [ObservableProperty] public partial bool IncludeLogs { get; set; } = true;
    [ObservableProperty] public partial bool IncludeRuntime { get; set; } = true;
    [ObservableProperty] public partial bool Confirmed { get; set; }
    [ObservableProperty] public partial bool HasPreview { get; set; }
    [ObservableProperty] public partial bool IsTaskListEmpty { get; set; } = true;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = "";
    [ObservableProperty] public partial string ExcludedText { get; set; } = "";

    public void RefreshTasks()
    {
        if (_disposed) return;
        var id = SelectedItem?.Id;
        Items.Clear();
        foreach (var item in tasks.Snapshot()) Items.Add(item);
        SelectedItem = Items.FirstOrDefault(x => x.Id == id);
        IsTaskListEmpty = Items.Count == 0;
    }

    public void SelectTask(Guid id)
    {
        if (_disposed) return;
        RefreshTasks();
        SelectedItem = Items.FirstOrDefault(item => item.Id == id);
    }

    partial void OnFromChanged(DateTimeOffset? value) => InvalidatePreview();
    partial void OnToChanged(DateTimeOffset? value) => InvalidatePreview();
    partial void OnLevelChanged(string value)
    {
        var index = Array.IndexOf(LogLevels.Names, value);
        LevelIndex = index >= 0 ? index + 1 : 0;
        InvalidatePreview();
    }
    partial void OnIncludeLogsChanged(bool value) => InvalidatePreview();
    partial void OnIncludeRuntimeChanged(bool value) => InvalidatePreview();

    private void InvalidatePreview()
    {
        if (_disposed) return;
        _previewCancellation?.Cancel();
        _preview = null;
        HasPreview = false;
        Confirmed = false;
        Files.Clear();
        ExcludedText = "";
    }

    [RelayCommand]
    private void CancelTask()
    {
        if (_disposed) return;
        if (SelectedItem is not null) tasks.Cancel(SelectedItem.Id);
        RefreshTasks();
    }

    [RelayCommand]
    private void OpenPreviousAttempt()
    {
        if (SelectedItem?.RetryOf is { } id) SelectAttempt(id);
    }

    [RelayCommand]
    private void OpenNextAttempt()
    {
        if (SelectedItem?.RetriedAs is { } id) SelectAttempt(id);
    }

    private void SelectAttempt(Guid id)
    {
        if (_disposed) return;
        RefreshTasks();
        var item = Items.FirstOrDefault(item => item.Id == id);
        if (item is null) StatusText = Loc.Get("tasks.retry.historyMissing");
        else SelectedItem = item;
    }

    private bool CanRetryTask() => !_disposed && SelectedItem?.CanRetry == true;

    [RelayCommand(CanExecute = nameof(CanRetryTask))]
    private void RetryTask()
    {
        if (_disposed) return;
        if (SelectedItem is not { } item) return;
        try
        {
            if (tasks.Retry(item.Id) is { } id)
            {
                SelectTask(id);
                StatusText = Loc.Get("tasks.retry.queued");
            }
            else
            {
                RefreshTasks();
                StatusText = Loc.Get("tasks.retry.unavailable");
            }
        }
        catch (InvalidOperationException)
        {
            RefreshTasks();
            StatusText = Loc.Get("tasks.retry.unavailable");
        }
    }

    [RelayCommand]
    private void CancelPreview() => _previewCancellation?.Cancel();

    [RelayCommand]
    private async Task PreviewAsync()
    {
        if (_disposed) return;
        InvalidatePreview();
        if (From > To) { StatusText = Loc.Get("logs.range.invalid"); return; }
        IsBusy = true;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        try
        {
            var preview = await diagnostics.PreviewAsync(AppPaths.LogDirectory,
                new(new(From, To?.AddDays(1).AddTicks(-1), Level), IncludeLogs, IncludeRuntime), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_previewCancellation, cancellation)) return;
            _preview = preview;
            foreach (var file in preview.Files) Files.Add(file);
            ExcludedText = string.Join(", ", preview.Excluded);
            HasPreview = true;
            StatusText = Loc.Format("diagnostics.preview.summary", preview.Files.Sum(x => x.Bytes),
                preview.InvalidLines, preview.Truncated);
        }
        catch (OperationCanceledException)
        {
            if (!_disposed && ReferenceEquals(_previewCancellation, cancellation))
                StatusText = Loc.Get("diagnostics.cancelled");
        }
        catch (Exception)
        {
            if (!_disposed && ReferenceEquals(_previewCancellation, cancellation))
                StatusText = Loc.Get("diagnostics.failed");
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                if (!_disposed) IsBusy = false;
            }
        }
    }

    public async Task ExportAsync(string path)
    {
        if (_disposed) return;
        if (_preview is null || !Confirmed || IsBusy)
        {
            StatusText = Loc.Get("diagnostics.confirmRequired");
            return;
        }
        var previewId = _preview.Id;
        var version = _pageVersion;
        IsBusy = true;
        try
        {
            var confirmation = await _operations.PrepareConfirmedAsync("diagnostics:export", "Diagnostics", "export");
            // 确认对话框期间可能关页或改筛选。
            if (_disposed || version != _pageVersion || _preview?.Id != previewId || !Confirmed) return;
            var id = tasks.Enqueue(Loc.Get("diagnostics.export"), "Diagnostics",
                async (progress, token) =>
                {
                    progress.Report(null);
                    await confirmation.ExecuteAsync(
                        async ct => { await diagnostics.ExportAsync(previewId, true, path, ct); return true; }, token);
                });
            RefreshTasks();
            await tasks.WaitAsync(id);
            if (_disposed) return;
            var item = tasks.Snapshot().FirstOrDefault(x => x.Id == id);
            StatusText = Loc.Get(item?.Status switch
            {
                WorkStatus.Succeeded => "diagnostics.exported",
                WorkStatus.Cancelled => "diagnostics.cancelled",
                _ => "diagnostics.failed",
            });
        }
        catch (OperationCanceledException) { if (!_disposed) StatusText = Loc.Get("diagnostics.cancelled"); }
        catch (Exception) { if (!_disposed) StatusText = Loc.Get("diagnostics.failed"); }
        finally
        {
            if (!_disposed) { IsBusy = false; Confirmed = false; RefreshTasks(); }
        }
    }

    public void PageClosed()
    {
        _pageVersion++;
        _previewCancellation?.Cancel();
    }

    internal void ReportExportFailure()
    {
        if (!_disposed) StatusText = Loc.Get("diagnostics.failed");
    }

    public void Dispose()
    {
        if (_disposed) return;
        InvalidatePreview();
        _disposed = true;
        Items.Clear();
        SelectedItem = null;
        IsBusy = false;
        RetryTaskCommand.NotifyCanExecuteChanged();
        // 只取消本页预览；已入队的导出及其他全局任务继续执行。
    }
}
