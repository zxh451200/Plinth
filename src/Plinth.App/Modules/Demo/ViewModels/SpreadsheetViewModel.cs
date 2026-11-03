#if PLINTH_XLSX
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Resources;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Services;
using Plinth.App.ViewModels;

namespace Plinth.App.Modules.Demo.ViewModels;

public sealed record SpreadsheetPreviewRow(long Row, string Code, string Name, string Remark);

/// <summary>Independent resources; follows the application's persisted startup language.</summary>
public sealed class SpreadsheetLabels
{
    private static readonly ResourceManager Resources =
        new("Plinth.App.Modules.Demo.Resources.SpreadsheetResources", typeof(SpreadsheetLabels).Assembly);
    private readonly CultureInfo _culture;

    public SpreadsheetLabels()
    {
        var language = SettingsService.ReadLanguage();
        if (language is not ("zh-CN" or "en-US")) language = Loc.DefaultLanguage;
        try { _culture = CultureInfo.GetCultureInfo(language); }
        catch (CultureNotFoundException) { _culture = CultureInfo.InvariantCulture; }
    }

    public string this[string key] => Resources.GetString(key, _culture) ?? $"[{key}]";
    public string Format(string key, params object?[] args) => string.Format(_culture, this[key], args);
}

/// <summary>Independent navigation page; all sensitive work uses the task and authorization services.</summary>
public sealed class SpreadsheetViewModel : ViewModelBase, IDisposable
{
    public const string PageId = "demo.spreadsheet";
    private readonly SpreadsheetService _spreadsheet;
    private readonly DemoService _demo;
    private readonly TaskService _tasks;
    private readonly SensitiveOperationService _operations;
    private readonly ILogger<SpreadsheetViewModel> _logger;
    private CsvReadResult? _pending;
    private Guid? _activeTask;
    private bool _cancelOnDispose;
    private bool _disposed;
    private bool _isBusy;
    private bool _acknowledged;
    private string _status;
    private string _sourceName = "";
    private string _summary = "";
    private string _searchText = "";
    private string _exportedPath = "";

    public SpreadsheetViewModel(SpreadsheetService spreadsheet, DemoService demo, TaskService tasks,
        SensitiveOperationService operations, ILogger<SpreadsheetViewModel> logger)
    {
        _spreadsheet = spreadsheet;
        _demo = demo;
        _tasks = tasks;
        _operations = operations;
        _logger = logger;
        _status = Labels["Ready"];
        ConfirmImportCommand = new AsyncRelayCommand(ConfirmImportAsync, () => CanConfirm);
        CancelCommand = new RelayCommand(Cancel, () => !_disposed && _activeTask.HasValue);
        ClearCommand = new RelayCommand(Clear, () => CanInteract);
    }

    public SpreadsheetLabels Labels { get; } = new();
    public ObservableCollection<SpreadsheetPreviewRow> PreviewRows { get; } = [];
    public ObservableCollection<string> Issues { get; } = [];
    public IAsyncRelayCommand ConfirmImportCommand { get; }
    public IRelayCommand CancelCommand { get; }
    public IRelayCommand ClearCommand { get; }
    public bool IsBusy => _isBusy;
    public bool CanInteract => !_disposed && !IsBusy;
    public bool CanConfirm => CanInteract && _pending is { Errors.Count: 0 } && Acknowledged;
    public string SourceName { get => _sourceName; private set => SetProperty(ref _sourceName, value); }
    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }
    public string StatusText { get => _status; private set => SetProperty(ref _status, value); }
    public string SearchText { get => _searchText; set => SetProperty(ref _searchText, value); }
    public string ExportedPath { get => _exportedPath; private set => SetProperty(ref _exportedPath, value); }
    public bool Acknowledged
    {
        get => _acknowledged;
        set { if (SetProperty(ref _acknowledged, value)) NotifyCommands(); }
    }

    /// <summary>Retains the full immutable result, not just the twenty displayed sample rows.</summary>
    public async Task PreviewFromAsync(string path)
    {
        if (!CanInteract) return;
        Clear();
        SourceName = Path.GetFileName(path);
        CsvReadResult? batch = null;
        var operations = _operations;
        var spreadsheet = _spreadsheet;
        var success = await RunAsync(Labels["PreviewTask"], false, async (progress, token) =>
        {
            batch = await operations.ExecuteAsync("demo:import", "Demo", "xlsx.preview",
                ct => spreadsheet.ImportAsync(path, ct), token);
            if (batch.Errors.Count != 0) throw new InvalidDataException("XLSX validation failed.");
        });
        if (_disposed || batch is null) return;
        for (var i = 1; i < Math.Min(batch.Rows.Count, 21); i++)
        {
            var row = batch.Rows[i];
            PreviewRows.Add(new(batch.RowNumbers[i], row[0], row[1], row[2]));
        }
        foreach (var issue in batch.Errors)
            Issues.Add(Labels.Format("Issue", issue.Row, issue.Code, IssueText(issue.Code)));
        Summary = Labels.Format("PreviewSummary", Math.Max(0, batch.Rows.Count - 1), PreviewRows.Count, batch.Errors.Count);
        if (success) _pending = batch;
        else if (batch.Errors.Count != 0) StatusText = Labels["InvalidBatch"];
        NotifyCommands();
    }

    private async Task ConfirmImportAsync()
    {
        if (!CanConfirm || _pending is not { } batch) return;
        Issues.Clear();
        ImportResult? result = null;
        var cleanupFailed = false;
        var operations = _operations;
        var spreadsheet = _spreadsheet;
        var demo = _demo;
        var logger = _logger;
        var success = await RunAsync(Labels["ImportTask"], true, confirmProduction: false,
            result: async (progress, token) =>
        {
            var imported = await operations.ExecuteAsync("demo:import", "Demo", "xlsx.import", async ct =>
            {
                var directory = Path.Combine(Path.GetTempPath(), $"plinth-xlsx-{Guid.NewGuid():N}");
                try
                {
                    progress.Report(null); // 转换耗时不可预测，不能把阶段序号当作百分比。
                    var csv = await spreadsheet.PrepareConfirmedImportCsvAsync(batch, directory, ct);
                    ct.ThrowIfCancellationRequested();
                    var committed = await demo.ImportCsvAsync(csv.Path, "utf-8", ",", [0, 1, 2], ct,
                        expectedSha256: csv.Sha256, progress: progress);
                    // A committed import remains successful when cancellation arrives afterwards.
                    return committed;
                }
                finally
                {
                    try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        cleanupFailed = true;
                        logger.LogWarning("XLSX temporary CSV cleanup failed. ExceptionType {ExceptionType}", ex.GetType().Name);
                    }
                }
            }, token, value => value.Succeeded);
            result = imported;
            return imported.ToTaskOutcome();
        });
        if (_disposed) return;
        Acknowledged = false;
        if (success && result is not null)
        {
            _pending = null;
            StatusText = Labels.Format("Imported", result.Added, result.Updated);
        }
        else if (result is { Succeeded: false })
            foreach (var error in result.Errors) Issues.Add(error);
        if (cleanupFailed) StatusText += " " + Labels["CleanupWarning"];
        NotifyCommands();
    }

    public async Task ExportToAsync(string directory)
    {
        if (!CanInteract) return;
        var search = SearchText;
        string? exported = null;
        var spreadsheet = _spreadsheet;
        var demo = _demo;
        ExportedPath = "";
        var success = await RunAsync(Labels["ExportTask"], true, permission: "demo:export",
            operation: "xlsx.export", result: async (progress, ct) =>
        {
            var rows = new List<IReadOnlyList<string>>();
            var ids = new HashSet<Guid>();
            int total = -1;
            for (var page = 1; ; page++)
            {
                ct.ThrowIfCancellationRequested();
                var found = await demo.SearchPageAsync(search, page, 200, "Code", false, ct);
                if (found.TotalCount > SpreadsheetService.MaxRows)
                    throw new InvalidDataException("XLSX export row limit exceeded.");
                if (total < 0) total = found.TotalCount;
                if (found.TotalCount != total || found.Page != page)
                    throw new InvalidDataException("Demo records changed during export; retry.");
                foreach (var record in found.Items)
                {
                    if (!ids.Add(record.Id)) throw new InvalidDataException("Demo records changed during export; retry.");
                    rows.Add(new[] { record.Code, record.Name, record.Remark });
                }
                progress.Report(total == 0 ? null : (double)rows.Count / total);
                if (page * 200 >= total) break;
            }
            if (rows.Count != total) throw new InvalidDataException("Demo records changed during export; retry.");
            progress.Report(null); // ClosedXML 的同步保存没有可测量的进度。
            exported = await spreadsheet.ExportAsync(rows, directory, ct);
            return TaskOutcome.ExportSelectedXlsx(rows.Count, exported);
        });
        if (!_disposed && success && exported is not null)
        {
            ExportedPath = exported;
            StatusText = Labels["Exported"];
        }
    }

    private async Task<bool> RunAsync(string title, bool confirm,
        Func<IProgress<double?>, CancellationToken, Task>? action = null, bool confirmProduction = true,
        Func<IProgress<double?>, CancellationToken, Task<TaskOutcome>>? result = null,
        string? permission = null, string? operation = null)
    {
        if (!CanInteract) return false;
        _isBusy = true;
        OnPropertyChanged(nameof(IsBusy));
        NotifyCommands();
        try
        {
            var confirmation = confirm && confirmProduction
                ? await _operations.PrepareConfirmedAsync(
                    permission ?? throw new InvalidOperationException("Missing permission."),
                    "Demo", operation ?? throw new InvalidOperationException("Missing operation."))
                : null;
            if (_disposed) return false;
            var queuedResult = result;
            var queuedAction = action;
            if (confirmation is not null && result is { } work)
                queuedResult = (progress, token) => confirmation.ExecuteAsync(ct => work(progress, ct), token);
            if (confirmation is not null && action is { } plainWork)
                queuedAction = async (progress, token) =>
                {
                    await confirmation.ExecuteAsync(async ct =>
                    {
                        await plainWork(progress, ct);
                        return true;
                    }, token);
                };
            var id = result is not null
                ? _tasks.EnqueueWithResult(title, "Demo", queuedResult!)
                : _tasks.Enqueue(title, "Demo", queuedAction!);
            _activeTask = id;
            _cancelOnDispose = !confirm;
            CancelCommand.NotifyCanExecuteChanged();
            StatusText = Labels.Format("Running", id);
            await _tasks.WaitAsync(id);
            if (_disposed) return false;
            var item = _tasks.Snapshot().FirstOrDefault(x => x.Id == id);
            StatusText = item?.Status switch
            {
                WorkStatus.Succeeded => Labels["Completed"],
                WorkStatus.Cancelled => Labels["Cancelled"],
                _ => Labels.Format("Failed", id),
            };
            return item?.Status == WorkStatus.Succeeded;
        }
        catch (OperationCanceledException)
        {
            if (!_disposed) StatusText = Labels["NotConfirmed"];
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogError("XLSX page operation failed. ExceptionType {ExceptionType}", ex.GetType().Name);
            if (!_disposed) StatusText = Labels["StartFailed"];
            return false;
        }
        finally
        {
            _activeTask = null;
            _cancelOnDispose = false;
            _isBusy = false;
            if (!_disposed)
            {
                OnPropertyChanged(nameof(IsBusy));
                NotifyCommands();
            }
        }
    }

    public void ReportPickerFailure()
    {
        if (!_disposed) StatusText = Labels["PickerFailed"];
    }

    private string IssueText(string code) => Labels[code switch
    {
        "REQUIRED" => "RequiredIssue", "LENGTH" => "LengthIssue", "DUPLICATE" => "DuplicateIssue",
        "CELL_TYPE" => "TypeIssue", "HEADER" => "HeaderIssue", "EMPTY" => "EmptyIssue", _ => "TextIssue",
    }];

    private void Cancel()
    {
        if (_activeTask is { } id && _tasks.Cancel(id)) StatusText = Labels["Cancelling"];
    }

    private void Clear()
    {
        if (!CanInteract) return;
        _pending = null;
        Acknowledged = false;
        PreviewRows.Clear();
        Issues.Clear();
        SourceName = "";
        Summary = "";
        StatusText = Labels["Ready"];
        NotifyCommands();
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanConfirm));
        ConfirmImportCommand.NotifyCanExecuteChanged();
        CancelCommand.NotifyCanExecuteChanged();
        ClearCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // Preview belongs to this page. Committed import/export work belongs to the task centre.
        if (_cancelOnDispose && _activeTask is { } id) _tasks.Cancel(id);
        _pending = null;
        NotifyCommands();
    }
}
#endif
