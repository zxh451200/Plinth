using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Modules.AdvancedTable.Models;
using Plinth.App.Modules.AdvancedTable.Services;
using Plinth.App.ViewModels;
using Plinth.App.Localization;
using Plinth.App.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Avalonia.Threading;

namespace Plinth.App.Modules.AdvancedTable.ViewModels;

public sealed partial class AdvancedTableViewModel : ViewModelBase, IDisposable
{
    public AdvancedTableViewModel(IAdvancedTableRepository repository,
        SensitiveOperationService? operations = null, string? importPermission = null,
        string? readPermission = null, string? exportPermission = null, string? editPermission = null)
    {
        _operations = operations ?? SensitiveOperationService.CreateDefault(NullLogger.Instance);
        _repository = repository;
        _importPermission = importPermission ?? string.Empty;
        _readPermission = readPermission ?? string.Empty;
        _exportPermission = exportPermission ?? string.Empty;
        _editPermission = editPermission ?? string.Empty;
        _sourceKey = repository switch
        {
            AdvancedTableCsvRepository => "advanced.imported",
            AdvancedTableRepository { IsSynthetic: true } => "advanced.synthetic",
            _ => "advanced.host",
        };
        _snapshotValidation = _operations.CaptureSnapshotValidation();
        if (_operations.SessionEvents is { } events) events.SessionChanged += OnSessionChanged;
    }

    public const int DefaultPageSize = 50;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SensitiveOperationService _operations;
    private readonly string _importPermission;
    private readonly string _readPermission;
    private readonly string _exportPermission;
    private readonly string _editPermission;
    private Action _snapshotValidation;
    private volatile bool _snapshotInvalid;
    private Action? _operationSessionChanged;
    private IAdvancedTableRepository _repository;
    private CancellationTokenSource? _importCancellation;
    private string _sourceKey;
    private bool IsProtected => _operations.AuthenticationEnabled
        && _repository is not AdvancedTableRepository { IsSynthetic: true };
    private string _statusKey = string.Empty;
    private object?[] _statusArgs = [];
    private bool _disposed;
    private bool _initialized;
    private string _appliedSearch = string.Empty;
    private string _appliedGroup = string.Empty;
    public ObservableCollection<AdvancedTableRowViewModel> Rows { get; } = [];
    public ObservableCollection<string> Groups { get; } = [];

    [ObservableProperty] public partial int Page { get; set; } = 1;
    [ObservableProperty] public partial string SearchText { get; set; } = string.Empty;
    [ObservableProperty] public partial string SelectedGroup { get; set; } = string.Empty;
    [ObservableProperty] public partial int PageCount { get; set; }
    [ObservableProperty] public partial int FilteredRows { get; set; }
    [ObservableProperty] public partial int TotalRows { get; set; }
    [ObservableProperty] public partial string StatusText { get; set; } = string.Empty;
    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial AdvancedTableRowViewModel? SelectedRow { get; set; }
    [ObservableProperty] public partial decimal EditQuantity { get; set; }
    [ObservableProperty] public partial decimal EditAmount { get; set; }

    public int PageSize => DefaultPageSize;
    public bool CanInteract => CanOperate();
    public string ScopeText => Loc.Format("advanced.scope", _appliedSearch,
        string.IsNullOrEmpty(_appliedGroup) ? Loc.Get("advanced.all") : _appliedGroup, FilteredRows);
    public string TotalText => Loc.Format(_sourceKey, TotalRows);
    public bool CanEditSelected => CanOperate() && SelectedRow is { Kind: AdvancedTableRowKind.Data }
        && _repository is IAdvancedTableEditableRepository
        && (!IsProtected || _editPermission.Length > 0);

    partial void OnSelectedRowChanged(AdvancedTableRowViewModel? value)
    {
        if (value is { Kind: AdvancedTableRowKind.Data })
        {
            EditQuantity = value.Quantity;
            EditAmount = value.Amount;
        }
        NotifyCommands();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private async Task SaveSelectedAsync()
    {
        if (SelectedRow is not { Kind: AdvancedTableRowKind.Data } selected
            || _repository is not IAdvancedTableEditableRepository editable) return;
        await RunAsync(async token =>
        {
            ValidateRead();
            if (IsProtected) _operations.Demand(_editPermission);
            if (EditQuantity != decimal.Truncate(EditQuantity) || EditQuantity is < -100000 or > 100000)
                throw new ArgumentOutOfRangeException(nameof(EditQuantity));
            if (EditAmount is < -1000000000000m or > 1000000000000m
                || decimal.Round(EditAmount, 2) != EditAmount)
                throw new ArgumentOutOfRangeException(nameof(EditAmount));
            await editable.UpdateAsync(new(selected.Id, selected.Group, selected.Label,
                checked((int)EditQuantity), EditAmount), token);
            var result = await _repository.ReadPageAsync(
                new(_appliedSearch, _appliedGroup, Page, PageSize), token);
            Publish(result, _appliedSearch, _appliedGroup);
        });
    }

    [RelayCommand(CanExecute = nameof(CanEditSelected))]
    private void CancelSelected()
    {
        if (SelectedRow is { Kind: AdvancedTableRowKind.Data } selected)
        {
            EditQuantity = selected.Quantity;
            EditAmount = selected.Amount;
        }
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized || !CanOperate()) return;
        await RunAsync(async token =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, cancellationToken);
            ValidateRead();
            var result = await _repository.ReadPageAsync(
                new AdvancedTableQuery(null, null, 1, PageSize), linked.Token);
            linked.Token.ThrowIfCancellationRequested();
            if (_disposed) return;
            ValidateRead();
            Groups.Clear();
            Groups.Add(string.Empty);
            foreach (var group in result.Groups) Groups.Add(group.Group);
            Publish(result, string.Empty, string.Empty);
            _initialized = true;
        });
    }

    [RelayCommand(CanExecute = nameof(CanOperate))]
    private async Task ApplyFilterAsync()
    {
        if (!CanOperate()) return;
        var search = SearchText?.Trim() ?? string.Empty;
        var group = SelectedGroup?.Trim() ?? string.Empty;
        // Apply also retries a failed initial read, including group choices and export readiness.
        if (!_initialized)
        {
            await InitializeAsync();
            if (!_initialized || !CanOperate()) return;
        }
        await LoadAsync(1, search, group);
    }

    [RelayCommand(CanExecute = nameof(CanGoPrevious))]
    private async Task PreviousPageAsync()
    {
        if (CanGoPrevious()) await LoadAsync(Page - 1, _appliedSearch, _appliedGroup);
    }

    [RelayCommand(CanExecute = nameof(CanGoNext))]
    private async Task NextPageAsync()
    {
        if (CanGoNext()) await LoadAsync(Page + 1, _appliedSearch, _appliedGroup);
    }

    private bool CanOperate() => !_disposed && !IsBusy;
    private bool CanGoPrevious() => CanOperate() && Page > 1;
    private bool CanGoNext() => CanOperate() && Page < PageCount;
    private bool CanExport() => CanOperate() && _initialized;

    private Task LoadAsync(int requestedPage, string search, string group) =>
        RunAsync(async token =>
        {
            ValidateRead();
            var result = await _repository.ReadPageAsync(
                new AdvancedTableQuery(search, group, requestedPage, PageSize), token);
            token.ThrowIfCancellationRequested();
            ValidateRead();
            if (!_disposed) Publish(result, search, group);
        });

    private void Publish(AdvancedTablePage result, string search, string group)
    {
        SelectedRow = null;
        Rows.Clear();
        // Real groups: each page fragment has a group heading, its details and a
        // subtotal explicitly calculated over the ENTIRE applied scope.
        foreach (var summary in result.Groups)
        {
            Rows.Add(AdvancedTableRowViewModel.Header(summary.Group));
            foreach (var row in result.Rows.Where(row => row.Group == summary.Group))
                Rows.Add(AdvancedTableRowViewModel.Data(row));
            Rows.Add(AdvancedTableRowViewModel.Subtotal(summary));
        }
        Rows.Add(AdvancedTableRowViewModel.Total(
            result.FilteredRows, result.FilteredQuantity, result.FilteredAmount));
        Page = result.Page; PageCount = result.PageCount;
        FilteredRows = result.FilteredRows; TotalRows = result.TotalRows;
        _appliedSearch = search; _appliedGroup = group;
        OnPropertyChanged(nameof(ScopeText)); OnPropertyChanged(nameof(TotalText));
        OnPropertyChanged(nameof(CanEditSelected));
        if (result.FilteredRows == 0) SetStatus("advanced.empty");
        else SetStatus("advanced.page", Page, PageCount, result.Rows.Count);
    }

    [RelayCommand(CanExecute = nameof(CanExport))]
    private Task ExportAsync() => ExportToAsync(AppPaths.ExportDirectory);

    internal Task ExportToAsync(string directory)
    {
        if (!CanExport()) return Task.CompletedTask;
        var search = _appliedSearch;
        var group = _appliedGroup;
        return RunAsync(async token =>
        {
            ValidateRead();
            var snapshot = _repository;
            var confirmation = IsProtected
                ? await _operations.PrepareConfirmedAsync(_exportPermission, "AdvancedTable", "csv.export", token)
                : null;
            async Task<string> Export(CancellationToken ct)
            {
                ValidateRead();
                return await AdvancedTableCsvExporter.ExportAsync(snapshot, search, group, directory, ct,
                    publish =>
                    {
                        ValidateRead();
                        publish();
                        return Task.CompletedTask;
                    });
            }
            var path = confirmation is null ? await Export(token) : await confirmation.ExecuteAsync(Export, token);
            if (!_disposed) SetStatus("advanced.exported", path);
        });
    }

    // The view supplies only a native user-selected stream; authorization precedes opening it.
    internal Task ImportFromPickerAsync(Func<CancellationToken, Task<Stream?>> pick,
        CancellationToken pageToken = default) =>
        ImportFromPickerAsync((token, _) => pick(token), pageToken);

    internal Task ImportFromPickerAsync(Func<CancellationToken, Action, Task<Stream?>> pick,
        CancellationToken pageToken = default) => RunAsync(async lifetimeToken =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetimeToken, pageToken);
        _importCancellation = cancellation;
        CancelImportCommand.NotifyCanExecuteChanged();
        var token = cancellation.Token;
        try
        {
            // No invented production grant: an authenticated host must explicitly map a permission.
            var confirmation = await _operations.PrepareConfirmedAsync(
                _importPermission, "AdvancedTable", "csv.import", token);
            var snapshotValidation = _operations.CaptureSnapshotValidation();
            void ValidateSelection()
            {
                token.ThrowIfCancellationRequested();
                snapshotValidation();
                _operations.Demand(_importPermission);
                _operations.Demand(_readPermission);
            }
            ValidateSelection();
            AdvancedTableCsvRepository snapshot;
            await using (var source = await pick(token, ValidateSelection))
            {
                token.ThrowIfCancellationRequested();
                if (source is null) return; // Dismissing the native dialog keeps the prior status/scope.
                ValidateSelection();
                snapshot = await confirmation.ExecuteAsync(
                    ct => Task.Run(() => AdvancedTableCsvRepository.ImportAsync(source, ct), ct), token);
            }
            snapshotValidation();
            _operations.Demand(_readPermission);
            var first = await snapshot.ReadPageAsync(new(null, null, 1, PageSize), token);
            // Revalidate the captured identity and permission immediately before publication.
            await confirmation.ExecuteAsync(ct =>
            {
                ct.ThrowIfCancellationRequested();
                if (_disposed) return Task.FromResult(false);
                snapshotValidation();
                _operations.Demand(_readPermission);
                _repository = snapshot;
                _snapshotValidation = snapshotValidation;
                _snapshotInvalid = false;
                _sourceKey = "advanced.imported";
                _initialized = true;
                SearchText = string.Empty;
                SelectedGroup = string.Empty;
                Groups.Clear();
                Groups.Add(string.Empty);
                foreach (var group in first.Groups) Groups.Add(group.Group);
                Publish(first, string.Empty, string.Empty);
                SetStatus("advanced.importSuccess");
                return Task.FromResult(true);
            }, token);
        }
        catch (OperationCanceledException) when (pageToken.IsCancellationRequested) { }
        catch (Exception) when (pageToken.IsCancellationRequested) { }
        finally
        {
            _importCancellation = null;
            CancelImportCommand.NotifyCanExecuteChanged();
        }
    });

    [RelayCommand(CanExecute = nameof(CanCancelImport))]
    private void CancelImport() => _importCancellation?.Cancel();
    private bool CanCancelImport() => !_disposed && _importCancellation is not null;

    private void ValidateRead()
    {
        if (!IsProtected) return;
        try
        {
            if (_snapshotInvalid) throw new UnauthorizedAccessException();
            _snapshotValidation();
            _operations.Demand(_readPermission);
        }
        catch (UnauthorizedAccessException)
        {
            ClearProtectedSnapshot();
            throw;
        }
    }

    private void ClearProtectedSnapshot()
    {
        _snapshotInvalid = true;
        _initialized = false;
        Rows.Clear();
        Groups.Clear();
        SearchText = SelectedGroup = _appliedSearch = _appliedGroup = string.Empty;
        Page = 1;
        PageCount = FilteredRows = TotalRows = 0;
        OnPropertyChanged(nameof(ScopeText));
        OnPropertyChanged(nameof(TotalText));
        SetStatus("advanced.denied");
        NotifyCommands();
    }

    private void OnSessionChanged(object? sender, EventArgs e)
    {
        if (_disposed) return;
        if (IsProtected)
        {
            try { _snapshotValidation(); }
            catch (UnauthorizedAccessException) { _snapshotInvalid = true; }
        }
        // Refresh also publishes SessionChanged with unchanged identity/claims.
        // Imports from synthetic data validate their own session, independently
        // of the deliberately unprotected displayed pilot snapshot.
        _operationSessionChanged?.Invoke();
        void Refresh()
        {
            if (_disposed || !IsProtected) return;
            try { ValidateRead(); }
            catch (UnauthorizedAccessException) { }
        }
        if (Dispatcher.UIThread.CheckAccess()) Refresh();
        else Dispatcher.UIThread.Post(Refresh, DispatcherPriority.Send);
    }

    private void SetStatus(string key, params object?[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        StatusText = Loc.Format(key, args);
    }

    public override void OnUiLanguageChanged()
    {
        if (_disposed) return;
        try { ValidateRead(); }
        catch (UnauthorizedAccessException) { return; }
        if (_statusKey.Length > 0) StatusText = Loc.Format(_statusKey, _statusArgs);
        // Recreate display-only headings and summaries without changing the applied snapshot.
        for (var i = 0; i < Rows.Count; i++) Rows[i] = Rows[i].Relocalize();
        base.OnUiLanguageChanged();
    }

    private async Task RunAsync(Func<CancellationToken, Task> operation)
    {
        if (!CanOperate()) return;
        IsBusy = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var validateOperation = _operations.CaptureSnapshotValidation();
        _operationSessionChanged = () =>
        {
            try { validateOperation(); }
            catch (UnauthorizedAccessException)
            {
                // A notification can race the completion/disposal of this operation.
                try { cancellation.Cancel(); }
                catch (ObjectDisposedException) { }
            }
        };
        NotifyCommands();
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { if (!_disposed) SetStatus("advanced.cancelled"); }
        catch (UnauthorizedAccessException) { if (!_disposed) SetStatus("advanced.denied"); }
        catch (InvalidDataException) { if (!_disposed) SetStatus("advanced.invalid"); }
        catch (Exception) { if (!_disposed) SetStatus("advanced.failed"); }
        finally
        {
            _operationSessionChanged = null;
            if (!_disposed && IsProtected)
            {
                try { ValidateRead(); }
                catch (UnauthorizedAccessException) { }
            }
            if (!_disposed) { IsBusy = false; NotifyCommands(); }
        }
    }

    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(CanEditSelected));
        ApplyFilterCommand.NotifyCanExecuteChanged();
        PreviousPageCommand.NotifyCanExecuteChanged();
        NextPageCommand.NotifyCanExecuteChanged();
        ExportCommand.NotifyCanExecuteChanged();
        CancelImportCommand.NotifyCanExecuteChanged();
        SaveSelectedCommand.NotifyCanExecuteChanged();
        CancelSelectedCommand.NotifyCanExecuteChanged();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_operations.SessionEvents is { } events) events.SessionChanged -= OnSessionChanged;
        _lifetime.Cancel();
        // The shell owns this page, including any pending export.
        _lifetime.Dispose();
        NotifyCommands();
    }
}
