using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Services;

namespace Plinth.App.ViewModels;

/// <summary>日志与错误中心：跨文件有界查询，后台读取，旧请求不能覆盖新条件。</summary>
public partial class LogViewModel : ViewModelBase, IRefreshable, IDisposable
{
    private readonly FolderService _folderService;
    private readonly LogAccessService _access;
    private string _loadedSubject = "";
    private CancellationTokenSource? _loadCancellation;
    private bool _applyingQuery;
    private bool _disposed;
    private bool _resumeOnAttach;

    /// <summary>当前查询返回的有界结果。</summary>
    private IReadOnlyList<LogEntry> _loaded = [];

    public LogViewModel(FolderService folderService, LogAccessService? access = null)
    {
        _folderService = folderService;
        _access = access ?? new LogAccessService();
        Entries.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasEntries));
        _ = LoadAsync();
    }

    public ObservableCollection<LogEntry> Entries { get; } = [];

    /// <summary>空结果时页面显示空态,避免只看到一块空白表格。</summary>
    public bool HasEntries => Entries.Count > 0;

    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }
    private string[] _eventKinds = BuildEventKinds();
    public string[] EventKinds
    {
        get => _eventKinds;
        private set { _eventKinds = value; OnPropertyChanged(); }
    }

    private static string[] BuildEventKinds() =>
        [Loc.Get("logs.kind.technical"), Loc.Get("logs.kind.audit"), Loc.Get("logs.kind.task"),
            Loc.Get("logs.kind.operational")];
    [ObservableProperty] public partial int EventKindIndex { get; set; }
    private LogEventKind CurrentKind => EventKindIndex switch
    {
        1 => LogEventKind.SecurityAudit,
        2 => LogEventKind.Task,
        _ => LogEventKind.Technical,
    };
    partial void OnEventKindIndexChanged(int value)
    {
        if (value is < 0 or > 3) { EventKindIndex = 0; return; }
        if (!_applyingQuery) _ = LoadAsync();
    }

    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    [ObservableProperty] public partial string Search { get; set; } = "";
    [ObservableProperty] public partial string Level { get; set; } = "";
    // 下拉显示当前语言的名字,筛选值仍用日志里的英文级别标记。
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
        EventKinds = BuildEventKinds();
        LevelOptions = BuildLevelOptions();
        // 级别等派生文字按访问时取当前语言,重建集合让已加载的行重新绑定。
        var loaded = _loaded.ToArray();
        Entries.Clear();
        foreach (var entry in loaded) Entries.Add(entry);
        base.OnUiLanguageChanged();
    }
    [ObservableProperty] public partial int LevelIndex { get; set; }
    partial void OnLevelIndexChanged(int value) =>
        Level = value is > 0 && value <= LogLevels.Names.Length ? LogLevels.Names[value - 1] : "";
    partial void OnLevelChanged(string value)
    {
        var index = Array.IndexOf(LogLevels.Names, value);
        LevelIndex = index >= 0 ? index + 1 : 0;
    }
    [ObservableProperty] public partial string Module { get; set; } = "";
    [ObservableProperty] public partial string TaskId { get; set; } = "";
    [ObservableProperty] public partial string ErrorCode { get; set; } = "";
    [ObservableProperty] public partial string CorrelationId { get; set; } = "";
    [ObservableProperty] public partial DateTimeOffset? From { get; set; }
    [ObservableProperty] public partial DateTimeOffset? To { get; set; }
    [ObservableProperty] public partial LogEntry? SelectedEntry { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; set; }

    partial void OnOnlyProblemsChanged(bool value)
    {
        if (!_applyingQuery) _ = LoadAsync();
    }

    /// <summary>跨页跳转先替换全部条件，避免与旧日期、等级或搜索词意外叠加。</summary>
    public async Task OpenQueryAsync(LogFilter filter)
    {
        if (_disposed) return;
        _applyingQuery = true;
        try
        {
            From = filter.From;
            To = filter.To;
            Level = filter.Level;
            Module = filter.Module;
            TaskId = NormalizeId(filter.TaskId);
            ErrorCode = filter.ErrorCode;
            CorrelationId = NormalizeId(filter.CorrelationId);
            Search = filter.Search;
            OnlyProblems = filter.OnlyProblems;
            EventKindIndex = filter.EventKind switch
            {
                LogEventKind.SecurityAudit => 1,
                LogEventKind.Task => 2,
                _ when filter.IncludeTaskEvents => 3,
                _ => 0,
            };
        }
        finally { _applyingQuery = false; }
        await LoadAsync();
    }

    private static string NormalizeId(string value) =>
        Guid.TryParse(value, out var id) ? id.ToString("D") : value;

    [RelayCommand]
    private Task ClearFiltersAsync() => OpenQueryAsync(new LogFilter());

    public void Refresh() => _ = LoadAsync();

    /// <summary>重新读日志文件。可以直接 await(测试里就这样用)。</summary>
    public async Task LoadAsync()
    {
        if (_disposed) return;
        _resumeOnAttach = false;
        _loadCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _loadCancellation = cancellation;
        IsLoading = true;
        SelectedEntry = null;
        _loaded = [];
        Entries.Clear();
        _loadedSubject = _access.CurrentSubject;
        try
        {
            var subject = _access.CurrentSubject;
            var filter = new LogFilter(From, To?.AddDays(1).AddTicks(-1), Level, Module, TaskId, ErrorCode, CorrelationId, Search, OnlyProblems, CurrentKind,
                IncludeTaskEvents: EventKindIndex == 3);
            if (From > To) { StatusText = Loc.Get("logs.range.invalid"); return; }
            var result = await Task.Run(() => _access.Query(AppPaths.LogDirectory, filter,
                cancellationToken: cancellation.Token), cancellation.Token);
            if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_loadCancellation, cancellation)) return;
            if (subject != _access.CurrentSubject || !_access.CanRead(CurrentKind))
            {
                StatusText = Loc.Get("logs.accessDenied");
                return;
            }
            _loadedSubject = subject;
            _loaded = result.Entries;
            ApplyFilter();
            if (result.Truncated || result.InvalidLines > 0)
                StatusText += " " + Loc.Format("logs.scan.summary", result.InvalidLines, result.Truncated);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (_disposed || cancellation.IsCancellationRequested || !ReferenceEquals(_loadCancellation, cancellation)) return;
            // 读日志失败不能再写日志(可能就是日志目录的问题),只在页面上提示
            _loaded = [];
            Entries.Clear();
            SelectedEntry = null;
            StatusText = Loc.Get(ex is UnauthorizedAccessException ? "logs.accessDenied" : "logs.status.failed");
        }
        finally
        {
            if (ReferenceEquals(_loadCancellation, cancellation))
            {
                _loadCancellation = null;
                if (!_disposed) IsLoading = false;
            }
            cancellation.Dispose();
        }
    }

    [RelayCommand]
    private Task ReloadAsync() => LoadAsync();

    public void PageClosed()
    {
        if (_loadCancellation is not null)
        {
            _resumeOnAttach = true;
            _loadCancellation.Cancel();
        }
    }

    public async Task PageOpenedAsync()
    {
        if (_disposed) return;
        RefreshAccess();
        if (_resumeOnAttach) await LoadAsync();
    }

    internal void ReportCopyFailure()
    {
        if (!_disposed) StatusText = Loc.Get("logs.copy.failed");
    }

    // 脱离视觉树可能只是切换标签；只有 Shell 关闭页面才永久释放。
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        PageClosed();
        // CTS 由持有它的 LoadAsync finally 释放，避免后台仍在读取 Token 时提前 Dispose。
        _loaded = [];
        Entries.Clear();
        SelectedEntry = null;
        IsLoading = false;
    }

    public void RefreshAccess()
    {
        if (_disposed) return;
        if (_access.CanRead(CurrentKind) && _loadedSubject == _access.CurrentSubject) return;
        _loadCancellation?.Cancel();
        _loaded = [];
        Entries.Clear();
        SelectedEntry = null;
        StatusText = Loc.Get("logs.accessDenied");
    }

    public string? GetCopyText(bool correlationOnly)
    {
        if (_disposed) return null;
        RefreshAccess();
        if (SelectedEntry is not { } entry || !_loaded.Contains(entry)) return null;
        try { _access.DemandRead(entry.EventKind); }
        catch (UnauthorizedAccessException)
        {
            RefreshAccess();
            return null;
        }
        return correlationOnly ? entry.CorrelationId : entry.Details;
    }

    [RelayCommand]
    private void OpenFolder()
    {
        if (!_disposed)
            StatusText = _folderService.OpenWithMessage(AppPaths.LogDirectory, Loc.Get("folder.log"));
    }

    [RelayCommand]
    private async Task CleanOldAsync()
    {
        if (_disposed) return;
        var retention = SettingsService.ReadLogRetention();
        try
        {
            var deleted = await _access.CleanOldAsync(AppPaths.LogDirectory,
                TimeSpan.FromDays(retention.Days));
            if (_disposed) return;
            StatusText = Loc.Format("logs.status.cleaned", deleted);
            await LoadAsync();
        }
        catch (UnauthorizedAccessException) { if (!_disposed) StatusText = Loc.Get("logs.accessDenied"); }
        catch (OperationCanceledException) { }
        catch (IOException) { if (!_disposed) StatusText = Loc.Get("logs.status.failed"); }
    }

    private void ApplyFilter()
    {
        Entries.Clear();
        foreach (var entry in _loaded.Where(entry => !OnlyProblems || IsProblem(entry.Level)))
        {
            Entries.Add(entry);
        }

        StatusText = Entries.Count == 0 ? Loc.Get("logs.status.empty") : Loc.Format("logs.status.count", Entries.Count);
    }

    private static bool IsProblem(string level) => level is "Warning" or "Error" or "Fatal" or "Critical";
}
