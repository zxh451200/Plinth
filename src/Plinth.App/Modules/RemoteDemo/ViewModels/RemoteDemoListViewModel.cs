using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Modules.RemoteDemo.Data;
using Plinth.App.Modules.RemoteDemo.Services;
using Plinth.App.Navigation;
using Plinth.App.Remote;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;

namespace Plinth.App.Modules.RemoteDemo.ViewModels;

public partial class RemoteDemoListViewModel : ViewModelBase, IUnsavedChanges, IRefreshable, IDisposable
{
    private readonly RemoteDemoService _service;
    private readonly UnsavedChangesService _changes;
    private readonly IRemoteUserContext _users;
    private string _actor;
    private long _sessionVersion;
    private bool _accessInvalidated;
    private int _accessGeneration;
    private readonly CancellationTokenSource _lifetime = new();
    private CancellationTokenSource? _load;
    private CancellationTokenSource? _write;
    private bool _disposed, _transition, _settingSelection, _writing, _confirming;
    private RemoteDemoRecord? _editing;
    private (string Code, string Name, string Remark) _original;
    private string _appliedSearch = "", _appliedSort = "Code";
    private bool _appliedDescending;
    private int _appliedSize = 50;

    public RemoteDemoListViewModel(RemoteDemoService service, UnsavedChangesService changes, IRemoteUserContext users)
    {
        _service = service;
        _changes = changes;
        _users = users;
        _actor = users.CurrentUser.Subject;
        _sessionVersion = users.SessionVersion;
        Items.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsEmpty));
        _ = LoadAsync(1);
    }

    public ObservableCollection<RemoteDemoRecord> Items { get; } = [];
    public int[] PageSizes { get; } = [25, 50, 100, 200];
    public string[] SortLabels { get; } = [Loc.Get("demo.col.code"), Loc.Get("demo.col.name")];
    public bool IsDirty => IsEditing && _original != (EditCode, EditName, EditRemark);
    public string PageText => $"{Page} / {Math.Max(1L, ((long)TotalCount + _appliedSize - 1) / _appliedSize)}";
    public bool CanDelete => IsEditing && _editing is not null;
    public bool CanInteract => !_disposed && !IsBusy;
    public bool IsEmpty => CanInteract && !_accessInvalidated && _actor.Length > 0
        && ErrorMessage.Length == 0 && Items.Count == 0;

    [ObservableProperty] public partial bool IsBusy { get; set; }
    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial bool IsConfirmingDelete { get; set; }
    [ObservableProperty] public partial string SearchText { get; set; } = "";
    [ObservableProperty] public partial int PageSize { get; set; } = 50;
    [ObservableProperty] public partial int SortIndex { get; set; }
    [ObservableProperty] public partial bool Descending { get; set; }
    [ObservableProperty] public partial int Page { get; set; } = 1;
    [ObservableProperty] public partial int TotalCount { get; set; }
    [ObservableProperty] public partial string ErrorMessage { get; set; } = "";
    [ObservableProperty] public partial string CodeError { get; set; } = "";
    [ObservableProperty] public partial string NameError { get; set; } = "";
    [ObservableProperty] public partial string RemarkError { get; set; } = "";
    [ObservableProperty] public partial string EditCode { get; set; } = "";
    [ObservableProperty] public partial string EditName { get; set; } = "";
    [ObservableProperty] public partial string EditRemark { get; set; } = "";
    [ObservableProperty] public partial RemoteDemoRecord? SelectedItem { get; set; }

    partial void OnIsBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(IsEmpty));
    }
    partial void OnErrorMessageChanged(string value) => OnPropertyChanged(nameof(IsEmpty));
    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanDelete));
    }
    partial void OnEditCodeChanged(string value) => OnPropertyChanged(nameof(IsDirty));
    partial void OnEditNameChanged(string value) => OnPropertyChanged(nameof(IsDirty));
    partial void OnEditRemarkChanged(string value) => OnPropertyChanged(nameof(IsDirty));
    partial void OnSelectedItemChanged(RemoteDemoRecord? oldValue, RemoteDemoRecord? newValue)
    {
        if (_settingSelection) return;
        SetSelection(oldValue);
        if (newValue is not null) _ = SelectRecordAsync(newValue);
    }

    private bool CheckActor()
    {
        RefreshAccess();
        return !_disposed && !_accessInvalidated && _actor.Length > 0;
    }

    public void RefreshAccess()
    {
        if (_disposed || _accessInvalidated
            || (_actor == _users.CurrentUser.Subject && _sessionVersion == _users.SessionVersion)) return;
        _accessInvalidated = true;
        _accessGeneration++;
        _load?.Cancel();
        _write?.Cancel();
        Items.Clear();
        CloseForm();
        EditCode = EditName = EditRemark = SearchText = "";
        _original = ("", "", "");
        _appliedSearch = "";
        CodeError = NameError = RemarkError = "";
        Page = 1;
        TotalCount = 0;
        OnPropertyChanged(nameof(PageText));
        ErrorMessage = Loc.Get("remoteDemo.actorChanged.message") + " " + Loc.Get("remote.suggestion.verify_result");
    }

    private void SetSelection(RemoteDemoRecord? item)
    {
        _settingSelection = true;
        SelectedItem = item;
        _settingSelection = false;
    }

    private async Task TransitionAsync(Func<Task> action, bool allowDuringQuery = false)
    {
        if (_disposed || _transition || (IsBusy && !allowDuringQuery) || !CheckActor()) return;
        _transition = true;
        IsBusy = true;
        try { await action(); }
        catch (OperationCanceledException) { }
        catch (Exception ex) { ShowError(ex); }
        finally
        {
            _transition = false;
            if (!_disposed) IsBusy = false;
        }
    }

    public Task SelectRecordAsync(RemoteDemoRecord record) => TransitionAsync(async () =>
    {
        if (!await LeaveCoreAsync() || !CheckActor()) return;
        var current = Items.FirstOrDefault(r => r.Id == record.Id);
        if (current is not null) OpenForm(current);
    });

    [RelayCommand]
    private Task NewAsync() => TransitionAsync(async () =>
    {
        if (await LeaveCoreAsync() && CheckActor()) OpenForm(null);
    });

    [RelayCommand]
    private Task CancelAsync() => TransitionAsync(async () =>
    {
        if (await LeaveCoreAsync() && CheckActor()) CloseForm();
    });

    [RelayCommand]
    private Task SearchAsync() => TransitionAsync(async () =>
    {
        if (!await LeaveCoreAsync() || !CheckActor()) return;
        // Search/sort/size are drafts until this explicit action passes the dirty guard.
        if (!RemoteDemoService.ValidText(SearchText, RemoteDemoService.MaxSearchLength, false)
            || !PageSizes.Contains(PageSize) || SortIndex is < 0 or > 1)
        {
            ErrorMessage = Loc.Get("remoteDemo.invalidQuery.message");
            return;
        }
        CloseForm();
        _appliedSearch = SearchText;
        _appliedSort = SortIndex == 0 ? "Code" : "Name";
        _appliedSize = PageSize;
        _appliedDescending = Descending;
        await LoadAsync(1);
    });

    [RelayCommand]
    private Task PreviousPageAsync() => ChangePageAsync(-1);
    [RelayCommand]
    private Task NextPageAsync() => ChangePageAsync(1);
    private Task ChangePageAsync(int delta) => TransitionAsync(async () =>
    {
        var target = (long)Page + delta;
        var count = Math.Max(1L, ((long)TotalCount + _appliedSize - 1) / _appliedSize);
        if (target < 1 || target > count || !await LeaveCoreAsync() || !CheckActor()) return;
        CloseForm();
        await LoadAsync((int)target);
    });

    [RelayCommand]
    public async Task RefreshAsync()
    {
        if (_disposed || _transition || _writing) return;
        RefreshAccess();
        if (_accessInvalidated || _actor.Length == 0)
        {
            _actor = _users.CurrentUser.Subject;
            _sessionVersion = _users.SessionVersion;
            _accessInvalidated = false;
            _accessGeneration++;
        }
        if (!CheckActor()) return;
        _load?.Cancel();
        await TransitionAsync(async () =>
        {
            if (!await LeaveCoreAsync() || !CheckActor()) return;
            CloseForm();
            await LoadAsync(Page);
        }, allowDuringQuery: true);
    }

    public void Refresh() => _ = RefreshAsync();

    public async Task<bool> TryLeaveAsync()
    {
        if (_disposed || _writing || _confirming || (_transition && _load is null)) return false;
        if (!IsDirty)
        {
            _load?.Cancel();
            return true;
        }
        if (_transition) return false;
        _transition = true;
        IsBusy = true;
        try { return await LeaveCoreAsync(); }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { ShowError(ex); return false; }
        finally { _transition = false; if (!_disposed) IsBusy = false; }
    }

    private async Task<bool> LeaveCoreAsync()
    {
        if (_disposed) return false;
        if (!IsDirty) return true;
        _confirming = true;
        UnsavedChangesChoice choice;
        try { choice = await _changes.AskAsync(); }
        finally { _confirming = false; }
        if (_disposed) return false;
        if (choice == UnsavedChangesChoice.Save) return await SaveCoreAsync();
        if (choice != UnsavedChangesChoice.Discard) return false;
        CloseForm();
        return true;
    }

    [RelayCommand]
    private Task SaveAsync() => TransitionAsync(async () => { await SaveCoreAsync(); });

    private async Task<bool> SaveCoreAsync()
    {
        if (!IsEditing || !CheckActor()) return false;
        CodeError = FieldError(EditCode, RemoteDemoService.MaxCodeLength, "code");
        NameError = FieldError(EditName, RemoteDemoService.MaxNameLength, "name");
        RemarkError = RemoteDemoService.ValidText(EditRemark, RemoteDemoService.MaxRemarkLength, false)
            ? "" : EditRemark.Length > RemoteDemoService.MaxRemarkLength
                ? Loc.Format("demo.err.remarkTooLong", RemoteDemoService.MaxRemarkLength)
                : Loc.Get("remoteDemo.invalidText.message");
        if (CodeError.Length + NameError.Length + RemarkError.Length > 0) return false;
        var input = new RemoteDemoInput(EditCode, EditName, EditRemark);
        using var write = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _write = write;
        _writing = true;
        try
        {
            if (_editing is { } record)
                await _service.UpdateAsync(record.Id, record.Version, input, write.Token);
            else
                await _service.CreateAsync(input, write.Token);
            if (_disposed || !CheckActor()) return false;
            CloseForm();
            _writing = false;
            await LoadAsync(Page);
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex) { ShowError(ex); return false; }
        finally
        {
            _writing = false;
            if (ReferenceEquals(_write, write)) _write = null;
        }
    }

    private static string FieldError(string text, int max, string field) => field switch
    {
        "code" when string.IsNullOrWhiteSpace(text) => Loc.Get("demo.err.codeRequired"),
        "name" when string.IsNullOrWhiteSpace(text) => Loc.Get("demo.err.nameRequired"),
        "code" when text.Length > max => Loc.Format("demo.err.codeTooLong", max),
        "name" when text.Length > max => Loc.Format("demo.err.nameTooLong", max),
        _ when !RemoteDemoService.ValidText(text, max, true) => Loc.Get("remoteDemo.invalidText.message"),
        _ => "",
    };

    [RelayCommand]
    private void AskDelete()
    {
        if (CanInteract && CanDelete && CheckActor()) IsConfirmingDelete = true;
    }
    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;
    [RelayCommand]
    private Task ConfirmDeleteAsync() => TransitionAsync(async () =>
    {
        if (!IsConfirmingDelete || _editing is not { } record) return;
        if (IsDirty)
        {
            _confirming = true;
            UnsavedChangesChoice choice;
            try { choice = await _changes.AskAsync(); }
            finally { _confirming = false; }
            if (_disposed || !CheckActor()) return;
            if (choice == UnsavedChangesChoice.Save)
            {
                await SaveCoreAsync();
                return; // A successful save closes the form; deletion needs a fresh selection/version.
            }
            if (choice != UnsavedChangesChoice.Discard) return;
        }
        using var write = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _write = write;
        _writing = true;
        try { await _service.DeleteAsync(record.Id, record.Version, write.Token); }
        finally
        {
            _writing = false;
            if (ReferenceEquals(_write, write)) _write = null;
        }
        if (!CheckActor() || _disposed) return;
        CloseForm();
        await LoadAsync(Page);
    });

    private async Task LoadAsync(int target)
    {
        if (_disposed || !CheckActor()) return;
        _load?.Cancel();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _load = cts;
        var generation = _accessGeneration;
        IsBusy = true;
        try
        {
            var result = await _service.SearchPageAsync(_appliedSearch, target, _appliedSize,
                _appliedSort, _appliedDescending, cts.Token);
            if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(_load, cts)
                || generation != _accessGeneration || !CheckActor()) return;
            SetSelection(null);
            Items.Clear();
            foreach (var item in result.Items) Items.Add(item);
            Page = result.Page;
            TotalCount = result.TotalCount;
            ErrorMessage = "";
            OnPropertyChanged(nameof(PageText));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (ReferenceEquals(_load, cts) && !cts.IsCancellationRequested
                && generation == _accessGeneration && CheckActor()) ShowError(ex);
        }
        finally
        {
            if (ReferenceEquals(_load, cts))
            {
                _load = null;
                if (!_disposed && !_transition) IsBusy = false;
            }
        }
    }

    private void OpenForm(RemoteDemoRecord? record)
    {
        _editing = record;
        SetSelection(record);
        EditCode = record?.Code ?? "";
        EditName = record?.Name ?? "";
        EditRemark = record?.Remark ?? "";
        _original = (EditCode, EditName, EditRemark);
        IsEditing = true;
        IsConfirmingDelete = false;
        CodeError = NameError = RemarkError = ErrorMessage = "";
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(CanDelete));
    }

    private void CloseForm()
    {
        _editing = null;
        SetSelection(null);
        IsEditing = IsConfirmingDelete = false;
        OnPropertyChanged(nameof(CanDelete));
    }

    private void ShowError(Exception ex)
    {
        if (_disposed) return;
        ErrorMessage = ex is RemoteApiException remote
            ? $"{Loc.Get(remote.Error.MessageKey)} {Loc.Get(remote.Error.SuggestionKey)} [{remote.Error.CorrelationId}]"
            : Loc.Get(ex is UnauthorizedAccessException ? "remote.forbidden.message" : "remoteDemo.operationFailed.message");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _load?.Cancel();
        _write?.Cancel();
        _lifetime.Cancel();
        _lifetime.Dispose();
        OnPropertyChanged(nameof(CanInteract));
        OnPropertyChanged(nameof(IsEmpty));
    }
}
