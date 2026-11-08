using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.Security;
using Plinth.App.Data;

namespace Plinth.App.Modules.Demo.ViewModels;

/// <summary>
/// 示例列表页:左边是可搜索的列表,右边是编辑表单。
/// 这个页面是"业务页面怎么写"的样板:ViewModel 只管界面状态和命令,数据操作全部交给 DemoService。
/// 数据库操作都是异步的,不会卡住界面。
/// </summary>
public partial class DemoListViewModel : ViewModelBase, IRefreshable, IUnsavedChanges, IPageDetails, IDisposable
{
    private readonly DemoService _service;
    private readonly ILogger<DemoListViewModel> _logger;
    private readonly IPermissionService? _permissions;

    /// <summary>当前这一次查询的取消开关。又发起新查询时取消旧的,避免旧结果盖住新结果。</summary>
    private CancellationTokenSource? _loadCts;

    /// <summary>正在编辑的记录的 Id。null 表示表单是"新增"。</summary>
    private Guid? _editingId;

    /// <summary>打开表单时这条记录的版本号,保存时带回去,用来发现别人先改过。</summary>
    private int _editingVersion;
    private readonly UnsavedChangesService _changes;
    private readonly WorkspacePreferencesService? _preferences;
    private DemoListPreferences _listPreferences = new();
    private readonly TaskService? _tasks;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private (string Code, string Name, string Remark, decimal? Quantity, DateTimeOffset? DueDate,
        DemoPriority Priority, bool Active) _original;
    private bool _updatingSelection;
    private bool _disposed;
    private bool _leaving;
    private bool _initializing = true;
    private bool _showValidation;
    private string _codeBusinessError = string.Empty;
    private string _nameBusinessError = string.Empty;
    private string _remarkBusinessError = string.Empty;
    private string _quantityBusinessError = string.Empty;
    private string _dueDateBusinessError = string.Empty;
    private string _priorityBusinessError = string.Empty;

    public DemoListViewModel(DemoService service, ILogger<DemoListViewModel> logger,
        UnsavedChangesService? changes = null, WorkspacePreferencesService? preferences = null,
        TaskService? tasks = null, IPermissionService? permissions = null)
    {
        _service = service;
        _logger = logger;
        _changes = changes ?? UnsavedChangesService.Default;
        _preferences = preferences;
        _tasks = tasks;
        _permissions = permissions;
        var saved = preferences?.Demo;
        _listPreferences = saved ?? new();
        SearchText = _listPreferences.SearchText;
        PageSize = saved?.PageSize is 25 or 50 or 100 or 200 ? saved.PageSize : 50;
        SortField = SortFields.Contains(saved?.SortField) ? saved!.SortField : "Code";
        Descending = saved?.Descending ?? false;
        _initializing = false;
        _ = LoadAsync();
    }

    public ObservableCollection<DemoRecord> Items { get; } = [];
    public System.Collections.Generic.IReadOnlyList<PageDetail> Details => SelectedItem is { } item
        ? [
            new(Loc.Get("demo.col.code"), item.Code),
            new(Loc.Get("demo.col.name"), item.Name),
            new(Loc.Get("demo.col.remark"), item.Remark),
            new(Loc.Get("demo.col.quantity"), item.Quantity.ToString(System.Globalization.CultureInfo.CurrentCulture)),
            new(Loc.Get("demo.col.dueDate"), item.DueDateText),
            new(Loc.Get("demo.col.priority"), PriorityLabel(item.Priority)),
            new(Loc.Get("demo.col.active"), item.Active ? Loc.Get("common.yes") : Loc.Get("common.no")),
            new(Loc.Get("demo.col.updatedAt"), item.UpdatedAtText),
            new(Loc.Get("demo.col.updatedBy"), item.UpdatedBy),
        ]
        : [];
    public string[] SortFields { get; } = ["Code", "Name", "Remark", "UpdatedAt", "UpdatedBy"];
    /// <summary>列偏好按这个顺序和表格列一一对应；排序字段只是其中一部分。</summary>
    public string[] ColumnKeys { get; } =
        ["Code", "Name", "Remark", "Quantity", "DueDate", "Priority", "Active", "UpdatedAt", "UpdatedBy"];
    public int[] PageSizes { get; } = [25, 50, 100, 200];
    private string[] _sortLabels = BuildSortLabels();
    public string[] SortLabels
    {
        get => _sortLabels;
        private set { _sortLabels = value; OnPropertyChanged(); }
    }

    private static string[] BuildSortLabels() =>
    [
        Loc.Get("demo.col.code"), Loc.Get("demo.col.name"), Loc.Get("demo.col.remark"),
        Loc.Get("demo.col.updatedAt"), Loc.Get("demo.col.updatedBy"),
    ];

    public override void OnUiLanguageChanged()
    {
        SortLabels = BuildSortLabels();
        OnPropertyChanged(nameof(PriorityLabels));
        OnPropertyChanged(nameof(Details));
        base.OnUiLanguageChanged();
    }
    public int SortIndex
    {
        get => Array.IndexOf(SortFields, SortField);
        set { if (value >= 0 && value < SortFields.Length) SortField = SortFields[value]; }
    }

    public DemoListPreferences ListPreferences => _listPreferences;
    public void SaveColumns(System.Collections.Generic.Dictionary<string, DemoColumnPreference> columns)
    {
        _listPreferences = ListPreferences with { Columns = columns };
        SaveListPreferences();
    }

    private void SaveListPreferences()
    {
        _listPreferences = ListPreferences with
        {
            SearchText = SearchText,
            PageSize = PageSize,
            SortField = SortField,
            Descending = Descending,
        };
        if (_preferences is not null)
            PreferencesSaveFailed = !_preferences.SaveDemo(_listPreferences);
    }

    [RelayCommand]
    private async Task ResetListAsync()
    {
        if (!await TryLeaveAsync()) return;
        _initializing = true;
        try
        {
            SearchText = string.Empty;
            Page = 1;
            PageSize = 50;
            SortField = "Code";
            Descending = false;
            // Missing column overrides mean all visible with the view's default widths.
            _listPreferences = new();
            OnPropertyChanged(nameof(ListPreferences));
        }
        finally { _initializing = false; }
        SaveListPreferences();
        await LoadAsync();
    }
    public int Page { get; private set; } = 1;
    public int TotalCount { get; private set; }
    public int PageCount => Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
    public string PageText => $"{Page} / {PageCount}";
    public bool IsDirty => IsEditing
        && _original != (EditCode, EditName, EditRemark, EditQuantity, EditDueDate, EditPriority, EditActive);
    public bool IsEmpty => !IsLoading && Items.Count == 0;
    public bool CanInlineEdit => IsEditing && !IsBatchSelecting && HasEditPermission && CanUseSelectedRecord;
    public bool CanUseSelectedRecord => !_disposed && !_leaving && !IsLoading && !IsSaving
        && SelectedItem is { } item && item.Id == _editingId;
    public bool HasEditPermission => !DeploymentConfiguration.Current.Authentication.Enabled
        || _permissions?.HasPermission("demo:save") == true;
    public bool HasDeletePermission => !DeploymentConfiguration.Current.Authentication.Enabled
        || _permissions?.HasPermission("demo:delete") == true;

    public void RefreshRecordActions()
    {
        OnPropertyChanged(nameof(HasEditPermission));
        OnPropertyChanged(nameof(HasDeletePermission));
        OnPropertyChanged(nameof(CanInlineEdit));
    }
    public string CodeError => !_showValidation ? string.Empty
        : string.IsNullOrWhiteSpace(EditCode) ? Loc.Get("demo.err.codeRequired")
        : EditCode.Trim().Length > DemoService.MaxCodeLength ? Loc.Format("demo.err.codeTooLong", DemoService.MaxCodeLength)
        : !CodeIdentity.IsValid(EditCode.Trim()) ? Loc.Get("demo.err.codeInvalid") : _codeBusinessError;
    public string NameError => !_showValidation ? string.Empty
        : string.IsNullOrWhiteSpace(EditName) ? Loc.Get("demo.err.nameRequired")
        : EditName.Trim().Length > DemoService.MaxNameLength ? Loc.Format("demo.err.nameTooLong", DemoService.MaxNameLength) : _nameBusinessError;
    public string RemarkError => _showValidation && EditRemark.Trim().Length > DemoService.MaxRemarkLength
        ? Loc.Format("demo.err.remarkTooLong", DemoService.MaxRemarkLength)
        : _showValidation ? _remarkBusinessError : string.Empty;
    public string QuantityError => !_showValidation ? string.Empty
        : EditQuantity is not { } quantity
          || quantity < DemoService.MinQuantity || quantity > DemoService.MaxQuantity
          || quantity != decimal.Truncate(quantity)
            ? Loc.Format("demo.err.quantityRange", DemoService.MinQuantity, DemoService.MaxQuantity)
            : _quantityBusinessError;
    public string DueDateError => !_showValidation ? string.Empty
        : EditDueDate is { } due
          && (DateOnly.FromDateTime(due.Date) < DemoService.MinDueDate
              || DateOnly.FromDateTime(due.Date) > DemoService.MaxDueDate)
            ? Loc.Format("demo.err.dueDateRange", DemoService.MinDueDate.ToString("d"), DemoService.MaxDueDate.ToString("d"))
            : _dueDateBusinessError;
    public string PriorityError => !_showValidation ? string.Empty
        : !Enum.IsDefined(EditPriority) ? Loc.Get("demo.err.priorityInvalid") : _priorityBusinessError;

    public string[] PriorityLabels => [PriorityLabel(DemoPriority.Low), PriorityLabel(DemoPriority.Normal),
        PriorityLabel(DemoPriority.High)];

    public decimal QuantityMinimum => DemoService.MinQuantity;
    public decimal QuantityMaximum => DemoService.MaxQuantity;

    public static string PriorityLabel(DemoPriority priority) => priority switch
    {
        DemoPriority.Low => Loc.Get("demo.priority.low"),
        DemoPriority.High => Loc.Get("demo.priority.high"),
        _ => Loc.Get("demo.priority.normal"),
    };

    public int SelectedPriorityIndex
    {
        get => (int)EditPriority;
        set { if (value is >= 0 and <= 2) EditPriority = (DemoPriority)value; }
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseSelectedRecord))]
    [NotifyPropertyChangedFor(nameof(CanInlineEdit))]
    public partial bool IsLoading { get; set; }
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanUseSelectedRecord))]
    [NotifyPropertyChangedFor(nameof(CanInlineEdit))]
    public partial bool IsSaving { get; set; }
    [ObservableProperty]
    public partial bool PreferencesSaveFailed { get; set; }
    [ObservableProperty]
    public partial int PageSize { get; set; } = 50;
    [ObservableProperty]
    public partial string SortField { get; set; } = "Code";
    [ObservableProperty]
    public partial bool Descending { get; set; }

    partial void OnPageSizeChanged(int value)
    {
        if (!PageSizes.Contains(value)) { PageSize = 50; return; }
        QueryChanged();
    }
    partial void OnSortFieldChanged(string value)
    {
        if (!SortFields.Contains(value)) { SortField = "Code"; return; }
        QueryChanged();
        OnPropertyChanged(nameof(SortIndex));
    }
    partial void OnDescendingChanged(bool value) => QueryChanged();
    partial void OnIsLoadingChanged(bool value)
    {
        OnPropertyChanged(nameof(IsEmpty));
        NotifyBatchAvailability();
        if (value) UpdateBatchSelection([]);
    }
    partial void OnIsSavingChanged(bool value) => NotifyBatchAvailability();
    partial void OnIsEditingChanged(bool value)
    {
        OnPropertyChanged(nameof(CanInlineEdit));
        OnPropertyChanged(nameof(IsDirty));
    }
    partial void OnEditCodeChanged(string value) { _codeBusinessError = string.Empty; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(CodeError)); }
    partial void OnEditNameChanged(string value) { _nameBusinessError = string.Empty; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(NameError)); }
    partial void OnEditRemarkChanged(string value) { _remarkBusinessError = string.Empty; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(RemarkError)); }
    partial void OnEditQuantityChanged(decimal? value) { _quantityBusinessError = string.Empty; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(QuantityError)); }
    partial void OnEditDueDateChanged(DateTimeOffset? value) { _dueDateBusinessError = string.Empty; OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(DueDateError)); }
    partial void OnEditPriorityChanged(DemoPriority value)
    {
        _priorityBusinessError = string.Empty;
        OnPropertyChanged(nameof(IsDirty));
        OnPropertyChanged(nameof(PriorityError));
        OnPropertyChanged(nameof(SelectedPriorityIndex));
    }
    partial void OnEditActiveChanged(bool value) => OnPropertyChanged(nameof(IsDirty));

    private void QueryChanged()
    {
        if (_initializing) return;
        Page = 1;
        SaveListPreferences();
        _ = LoadAsync();
    }

    [RelayCommand]
    private async Task PreviousPageAsync()
    {
        if (Page <= 1) return;
        Page--;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task NextPageAsync()
    {
        if (Page >= PageCount) return;
        Page++;
        await LoadAsync();
    }

    [ObservableProperty]
    public partial string SearchText { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Details))]
    [NotifyPropertyChangedFor(nameof(CanUseSelectedRecord))]
    [NotifyPropertyChangedFor(nameof(CanInlineEdit))]
    public partial DemoRecord? SelectedItem { get; set; }

    /// <summary>表单是否显示。</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    [ObservableProperty]
    public partial string EditCode { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string EditRemark { get; set; } = string.Empty;

    /// <summary>数字单元格用 decimal? 承接 NumericUpDown;保存前校验为整数。</summary>
    [ObservableProperty]
    public partial decimal? EditQuantity { get; set; }

    [ObservableProperty]
    public partial DateTimeOffset? EditDueDate { get; set; }

    [ObservableProperty]
    public partial DemoPriority EditPriority { get; set; } = DemoPriority.Normal;

    [ObservableProperty]
    public partial bool EditActive { get; set; } = true;

    /// <summary>保存或删除失败的原因,显示在表单里。保存失败时用户填的内容保留。</summary>
    [ObservableProperty]
    public partial string ErrorMessage { get; set; } = string.Empty;

    /// <summary>列表下方的状态提示,例如"共 3 条"。</summary>
    [ObservableProperty]
    public partial string StatusText { get; set; } = string.Empty;

    /// <summary>点了「删除」之后,再确认一次才真正删。</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; set; }

    /// <summary>正在编辑已有记录时才能删除。</summary>
    public bool CanDelete => _editingId is not null;

    partial void OnSearchTextChanged(string value)
    {
        var bounded = new DemoListPreferences { SearchText = value }.SearchText;
        if (value != bounded) { SearchText = bounded; return; }
        QueryChanged();
    }

    partial void OnSelectedItemChanged(DemoRecord? oldValue, DemoRecord? newValue)
    {
        if (_updatingSelection || _disposed || IsBatchSelecting || newValue is null) return;
        _updatingSelection = true;
        SelectedItem = oldValue;
        _updatingSelection = false;
        _ = ObserveSelectionAsync(newValue);
    }

    private async Task ObserveSelectionAsync(DemoRecord record)
    {
        try { await SelectRecordAsync(record); }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            if (!_disposed) UnhandledErrorReporter.Report(ex);
        }
    }

    public async Task<bool> SelectRecordAsync(DemoRecord record)
    {
        if (_disposed || IsBatchSelecting || !Items.Any(item => item.Id == record.Id)) return false;
        if (record.Id == _editingId)
        {
            // 刷新或清空选择后重新选回同一行，只恢复选择，不能重建并覆盖草稿。
            _updatingSelection = true;
            try { SelectedItem = Items.First(item => item.Id == record.Id); }
            finally { _updatingSelection = false; }
            return true;
        }
        if (!await TryLeaveAsync()) return false;
        // 保存未提交输入会刷新列表，目标必须重新取当前页中的实例。
        var current = Items.FirstOrDefault(item => item.Id == record.Id);
        if (current is null || _disposed) return false;
        _updatingSelection = true;
        SelectedItem = current;
        _updatingSelection = false;
        StartEdit(current);
        return true;
    }

    public void Refresh() => _ = LoadAsync();

    /// <summary>按当前搜索条件重新读一遍列表。可以直接 await(测试里就这样用)。</summary>
    public async Task LoadAsync()
    {
        if (_disposed) return;
        // 又发起了新的查询时,取消还没完成的旧查询
        _loadCts?.Cancel();
        var cts = new CancellationTokenSource();
        _loadCts = cts;
        IsLoading = true;

        try
        {
            var result = await _service.SearchPageAsync(SearchText, Page, PageSize, SortField, Descending, cts.Token);
            if (_disposed || cts.IsCancellationRequested || !ReferenceEquals(_loadCts, cts))
            {
                return; // 已经被更新的查询取代
            }

            _updatingSelection = true;
            Items.Clear();
            foreach (var record in result.Items)
            {
                Items.Add(record);
            }
            SelectedItem = Items.FirstOrDefault(item => item.Id == _editingId);
            _updatingSelection = false;
            TotalCount = result.TotalCount;
            Page = result.Page;
            OnPropertyChanged(nameof(TotalCount));
            OnPropertyChanged(nameof(Page));
            OnPropertyChanged(nameof(PageCount));
            OnPropertyChanged(nameof(PageText));
            StatusText = Loc.Format("demo.status.count", TotalCount);
        }
        catch (OperationCanceledException)
        {
            // 被新的查询取代,不用处理
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "加载示例数据失败");
            if (!_disposed && ReferenceEquals(_loadCts, cts))
                StatusText = Loc.Get("demo.status.loadFailed");
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _loadCts = null;
                if (!_disposed) IsLoading = false;
            }
            cts.Dispose();
        }
    }

    /// <summary>关闭时取消页面操作；全局导入导出由任务服务持有，不依赖页面寿命。</summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        UpdateBatchSelection([]);
        NotifyBatchAvailability();
        OnPropertyChanged(nameof(CanUseSelectedRecord));
        _loadCts?.Cancel();
        _lifetimeCts.Cancel();
    }

    public async Task<bool> TryLeaveAsync()
    {
        if (_disposed || _leaving || IsSaving) return false;
        if (!IsDirty) return true;
        _leaving = true;
        NotifyBatchAvailability();
        OnPropertyChanged(nameof(CanUseSelectedRecord));
        try
        {
            var choice = await _changes.AskAsync();
            if (_disposed) return false;
            if (choice == UnsavedChangesChoice.Save) return await SaveChangesAsync();
            if (choice != UnsavedChangesChoice.Discard) return false;
            CloseForm();
            return true;
        }
        finally
        {
            _leaving = false;
            if (!_disposed)
            {
                NotifyBatchAvailability();
                OnPropertyChanged(nameof(CanUseSelectedRecord));
            }
        }
    }

    [RelayCommand]
    private async Task NewAsync()
    {
        if (!await TryLeaveAsync()) return;
        if (IsBatchSelecting)
        {
            IsBatchSelecting = false;
            UpdateBatchSelection([]);
        }
        SelectedItem = null;
        _editingId = null;
        _editingVersion = 0;
        EditCode = string.Empty;
        EditName = string.Empty;
        EditRemark = string.Empty;
        EditQuantity = 0;
        EditDueDate = null;
        EditPriority = DemoPriority.Normal;
        EditActive = true;
        ErrorMessage = string.Empty;
        IsConfirmingDelete = false;
        IsEditing = true;
        MarkClean();
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUseSelectedRecord));
    }

    [RelayCommand]
    private async Task SaveAsync() => await SaveChangesAsync();

    private async Task<bool> SaveChangesAsync()
    {
        if (_disposed || IsSaving || !IsEditing) return false;
        ClearBusinessErrors();
        ErrorMessage = string.Empty;
        _showValidation = true;
        NotifyValidation();
        if (CodeError.Length > 0 || NameError.Length > 0 || RemarkError.Length > 0
            || QuantityError.Length > 0 || DueDateError.Length > 0 || PriorityError.Length > 0) return false;
        var editingId = _editingId;
        var version = _editingVersion;
        var quantity = checked((int)(EditQuantity ?? 0));
        var snapshot = (Code: EditCode, Name: EditName, Remark: EditRemark, Quantity: EditQuantity,
            DueDate: EditDueDate, Priority: EditPriority, Active: EditActive);
        var input = new DemoSaveInput(editingId ?? Guid.NewGuid(), version,
            snapshot.Code, snapshot.Name, snapshot.Remark, MustExist: editingId.HasValue,
            Quantity: quantity, DueDate: EditDueDate is { } due ? DateOnly.FromDateTime(due.Date) : null,
            Priority: EditPriority, Active: EditActive);
        bool MatchesSnapshot() => !_disposed && IsEditing && _editingId == editingId
            && _editingVersion == version
            && snapshot == (EditCode, EditName, EditRemark, EditQuantity, EditDueDate, EditPriority, EditActive);
        IsSaving = true;
        try
        {
            var error = await _service.SaveValidatedAsync(input, _lifetimeCts.Token);
            if (_disposed) return false;

            // 保存期间输入发生变化时保留新输入，不绑定旧错误，也不关闭新表单。
            if (!MatchesSnapshot())
            {
                if (error is null && IsEditing && _editingId == editingId && _editingVersion == version)
                {
                    _editingId = input.Id;
                    _editingVersion = editingId is null ? 0 : version + 1;
                    _original = snapshot;
                    OnPropertyChanged(nameof(IsDirty));
                    OnPropertyChanged(nameof(CanDelete));
                    OnPropertyChanged(nameof(CanUseSelectedRecord));
                    await LoadAsync();
                }
                return false;
            }

            if (error is not null)
            {
                switch (error.Field)
                {
                    case DemoSaveField.Code: _codeBusinessError = error.Message; break;
                    case DemoSaveField.Name: _nameBusinessError = error.Message; break;
                    case DemoSaveField.Remark: _remarkBusinessError = error.Message; break;
                    case DemoSaveField.Quantity: _quantityBusinessError = error.Message; break;
                    case DemoSaveField.DueDate: _dueDateBusinessError = error.Message; break;
                    case DemoSaveField.Priority: _priorityBusinessError = error.Message; break;
                    default: ErrorMessage = error.Message; break;
                }
                NotifyValidation();
                return false;
            }

            CloseForm();
            await LoadAsync();
            return true;
        }
        catch (OperationCanceledException) { return false; }
        catch (Exception ex)
        {
            _logger.LogError(ex, "保存示例数据失败");
            if (MatchesSnapshot()) ErrorMessage = Loc.Get("demo.err.saveFailed");
            return false;
        }
        finally { if (!_disposed) IsSaving = false; }
    }

    [RelayCommand]
    private async Task ExportAsync()
    {
        try
        {
            if (_disposed) return;
            if (_tasks is not null)
            {
                var search = SearchText;
                var service = _service;
                var directory = AppPaths.ExportDirectory;
                // 重试捕获服务和条件，不依赖已关闭的页面；每次生成新文件并重新授权。
                _tasks.EnqueueWithResult(Loc.Get("demo.export"), "Demo", (progress, token) =>
                    service.ExportCsvWithResultAsync(search, directory, token, progress), allowRetry: true);
                StatusText = Loc.Get("tasks.queued");
                return;
            }
            var path = await _service.ExportCsvAsync(SearchText, AppPaths.ExportDirectory, _lifetimeCts.Token);
            if (_disposed) return;
            StatusText = Loc.Format("demo.status.exported", path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError("导出示例数据失败。ExceptionType {ExceptionType}", ex.GetType().Name);
            if (!_disposed) StatusText = Loc.Get("demo.status.exportFailed");
        }
    }

    /// <summary>导出当前页为固定模板 PDF；文件由界面的保存框选好后传进来。</summary>
    public async Task ExportPdfAsync(string path)
    {
        try
        {
            var records = Items.ToArray();
            var bytes = await Task.Run(() => DemoPdfExporter.Render(records, DateTimeOffset.Now), _lifetimeCts.Token);
            await File.WriteAllBytesAsync(path, bytes, _lifetimeCts.Token);
            if (!_disposed) StatusText = Loc.Format("demo.status.pdfExported", path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError("导出 PDF 失败。ExceptionType {ExceptionType}", ex.GetType().Name);
            if (!_disposed) StatusText = Loc.Get("demo.status.pdfFailed");
        }
    }

    /// <summary>生成当前页 PDF 后交给系统默认打印动作；没有 PDF 处理程序时只提示，不影响任务。</summary>
    public async Task PrintPdfAsync()
    {
        try
        {
            var records = Items.ToArray();
            var bytes = await Task.Run(() => DemoPdfExporter.Render(records, DateTimeOffset.Now), _lifetimeCts.Token);
            var path = Path.Combine(Path.GetTempPath(),
                $"plinth-demo-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.pdf");
            await File.WriteAllBytesAsync(path, bytes, _lifetimeCts.Token);
            if (_disposed) return;
            if (PdfPrinter.TryPrint(path, out var error))
            {
                StatusText = Loc.Get("demo.status.printStarted");
            }
            else
            {
                _logger.LogWarning("打印 PDF 失败。Reason {Reason}", error);
                StatusText = Loc.Get("demo.status.printFailed");
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError("打印 PDF 失败。ExceptionType {ExceptionType}", ex.GetType().Name);
            if (!_disposed) StatusText = Loc.Get("demo.status.printFailed");
        }
    }

    /// <summary>导入一个 CSV 文件(文件由界面的选择框选好后传进来)。成功后刷新列表,失败时在状态栏显示原因。</summary>
    public async Task ImportFromAsync(string path, string encoding = "utf-8", string delimiter = ",",
        int[]? columns = null, string? expectedSha256 = null)
    {
        try
        {
            if (_disposed) return;
            if (_tasks is not null)
            {
                var mapping = (columns ?? [0, 1, 2]).ToArray();
                var service = _service;
                _tasks.EnqueueWithResult(Loc.Get("demo.import"), "Demo", async (progress, token) =>
                {
                    var imported = await service.ImportCsvAsync(path, encoding, delimiter, mapping, token,
                        expectedSha256, progress);
                    return imported.ToTaskOutcome();
                });
                StatusText = Loc.Get("tasks.queued");
                return;
            }
            var result = await _service.ImportCsvAsync(path, encoding, delimiter, columns ?? [0, 1, 2],
                _lifetimeCts.Token, expectedSha256);
            if (_disposed) return;
            if (result.Succeeded)
            {
                StatusText = Loc.Format("demo.status.imported", result.Added, result.Updated);
                await LoadAsync();
                return;
            }

            // 整批取消:列出前几条问题,让用户改好文件再来
            StatusText = Loc.Format("demo.status.importRejected", string.Join(" ", result.Errors.Take(3)));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError("导入示例数据失败。ExceptionType {ExceptionType}", ex.GetType().Name);
            if (!_disposed) StatusText = Loc.Get("demo.status.importFailed");
        }
    }

    public async Task ExportPreviewErrorsAsync(string path, System.Collections.Generic.IReadOnlyList<CsvIssue> issues)
    {
        if (_disposed) return;
        var snapshot = issues.ToArray();
        var service = _service;
        async Task<TaskOutcome> Write(CancellationToken token)
        {
            var actualPath = await service.ExportPreviewErrorsAsync(path, snapshot, token);
            return TaskOutcome.ReportSelectedCsv(snapshot.Length, actualPath);
        }
        if (_tasks is null) await Write(default);
        else _tasks.EnqueueWithResult(Loc.Get("csv.exportErrors"), "Demo", (_, token) => Write(token));
        if (!_disposed) StatusText = Loc.Get("tasks.queued");
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (await TryLeaveAsync()) CloseForm();
    }

    [RelayCommand]
    private void AskDelete()
    {
        if (!_disposed && !_leaving && !IsSaving && HasDeletePermission)
            IsConfirmingDelete = _editingId is not null;
    }

    [RelayCommand]
    private void CancelDelete() => IsConfirmingDelete = false;

    [RelayCommand]
    private async Task ConfirmDeleteAsync()
    {
        if (_disposed || IsSaving || _leaving || _editingId is not { } id)
        {
            return;
        }

        IsSaving = true;
        try
        {
            var error = await _service.DeleteAsync(id, _editingVersion, _lifetimeCts.Token);
            if (_disposed) return;
            if (error is not null)
            {
                ErrorMessage = error;
                IsConfirmingDelete = false;
                return;
            }

            CloseForm();
            await LoadAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "删除示例数据失败");
            if (!_disposed) ErrorMessage = Loc.Get("demo.err.saveFailed");
        }
        finally { if (!_disposed) IsSaving = false; }
    }

    private void StartEdit(DemoRecord record)
    {
        _editingId = record.Id;
        _editingVersion = record.Version;
        EditCode = record.Code;
        EditName = record.Name;
        EditRemark = record.Remark;
        EditQuantity = record.Quantity;
        EditDueDate = record.DueDate is { } due ? new DateTimeOffset(due.ToDateTime(TimeOnly.MinValue)) : null;
        EditPriority = record.Priority;
        EditActive = record.Active;
        ErrorMessage = string.Empty;
        IsConfirmingDelete = false;
        IsEditing = true;
        MarkClean();
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUseSelectedRecord));
        OnPropertyChanged(nameof(CanInlineEdit));
    }

    private void CloseForm()
    {
        IsEditing = false;
        IsConfirmingDelete = false;
        ErrorMessage = string.Empty;
        _editingId = null;
        _editingVersion = 0;
        SelectedItem = null;
        MarkClean();
        OnPropertyChanged(nameof(CanDelete));
        OnPropertyChanged(nameof(CanUseSelectedRecord));
    }

    private void MarkClean()
    {
        _original = (EditCode, EditName, EditRemark, EditQuantity, EditDueDate, EditPriority, EditActive);
        ClearBusinessErrors();
        _showValidation = false;
        NotifyValidation();
        OnPropertyChanged(nameof(IsDirty));
    }

    private void ClearBusinessErrors()
    {
        _codeBusinessError = _nameBusinessError = _remarkBusinessError = string.Empty;
        _quantityBusinessError = _dueDateBusinessError = _priorityBusinessError = string.Empty;
    }

    private void NotifyValidation()
    {
        OnPropertyChanged(nameof(CodeError));
        OnPropertyChanged(nameof(NameError));
        OnPropertyChanged(nameof(RemarkError));
        OnPropertyChanged(nameof(QuantityError));
        OnPropertyChanged(nameof(DueDateError));
        OnPropertyChanged(nameof(PriorityError));
    }
}
