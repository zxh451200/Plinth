using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;

namespace Plinth.App.Modules.Demo.ViewModels;

public partial class DemoListViewModel
{
    private readonly HashSet<Guid> _batchIds = [];

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanCopyBatch))]
    public partial bool IsBatchSelecting { get; private set; }

    public int BatchCount => _batchIds.Count;
    public string BatchSelectionText => Loc.Format("demo.batch.count", BatchCount);
    public bool CanManageBatch => !_disposed && !_leaving && !IsLoading && !IsSaving;
    public bool CanCopyBatch => IsBatchSelecting && CanManageBatch && BatchCount > 0;

    [RelayCommand]
    private async Task ToggleBatchSelectionAsync()
    {
        if (!CanManageBatch) return;
        if (IsBatchSelecting)
        {
            IsBatchSelecting = false;
            UpdateBatchSelection([]);
            SelectedItem = null;
            return;
        }
        if (!await TryLeaveAsync() || !CanManageBatch) return;
        CloseForm();
        IsBatchSelecting = true;
        UpdateBatchSelection([]);
    }

    /// <summary>仅保存当前页 ID，不跨页保留，不把选择当作全查询结果。</summary>
    public void UpdateBatchSelection(IEnumerable<DemoRecord> records)
    {
        _batchIds.Clear();
        if (IsBatchSelecting && CanManageBatch)
        {
            var currentIds = Items.Select(item => item.Id).ToHashSet();
            foreach (var record in records)
                if (currentIds.Contains(record.Id)) _batchIds.Add(record.Id);
        }
        OnPropertyChanged(nameof(BatchCount));
        OnPropertyChanged(nameof(BatchSelectionText));
        OnPropertyChanged(nameof(CanCopyBatch));
    }

    /// <summary>按当前页顺序复制已加载值；保留 CSV 转义和公式保护，不复制未保存表单。</summary>
    public string? GetBatchCopyText()
    {
        if (!CanCopyBatch) return null;
        var lines = Items.Where(item => _batchIds.Contains(item.Id))
            .Select(item => string.Join(",", new[] { item.Code, item.Name, item.Remark }.Select(CsvWriter.Escape)));
        return string.Join("\r\n", lines);
    }

    private void NotifyBatchAvailability()
    {
        OnPropertyChanged(nameof(CanInlineEdit));
        OnPropertyChanged(nameof(CanManageBatch));
        OnPropertyChanged(nameof(CanCopyBatch));
    }
}
