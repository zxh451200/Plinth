using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoBatchSelectionTests
{
    [AvaloniaFact]
    public async Task Batch_entry_respects_dirty_cancel_and_discard()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Vm.EditName = "Unsaved";
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        Assert.False(page.Vm.IsBatchSelecting);
        Assert.Equal("Unsaved", page.Vm.EditName);
        Assert.True(page.Vm.IsDirty);
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        Assert.True(page.Vm.IsBatchSelecting);
        Assert.False(page.Vm.IsEditing);
        Assert.False(page.Vm.CanDelete);
        Assert.False(page.Vm.IsDirty);
        Assert.Empty(page.Table.SelectedItems!);
    }

    [AvaloniaFact]
    public async Task Pointer_multiselect_does_not_open_a_form_and_ctrl_a_selects_only_current_page()
    {
        using var page = await Page.CreateAsync(count: 27);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        page.Layout();
        page.Click(page.Row(0));
        page.Click(page.Row(1));
        Assert.Equal(2, page.Vm.BatchCount);
        Assert.False(page.Vm.IsEditing);
        Assert.True(page.Table.Focus());
        page.Window.KeyPress(Key.A, RawInputModifiers.Control, PhysicalKey.A, "a");
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(25, page.Vm.BatchCount);
        Assert.Equal(27, page.Vm.TotalCount);
        Assert.Contains("25", page.Vm.BatchSelectionText);
        Assert.False(page.Vm.CanUseSelectedRecord);
        page.Vm.AskDeleteCommand.Execute(null);
        Assert.False(page.Vm.IsConfirmingDelete);
        await page.Vm.NextPageCommand.ExecuteAsync(null);
        page.Layout();
        Assert.Equal(2, page.Vm.Items.Count);
        Assert.Equal(0, page.Vm.BatchCount);
        Assert.Empty(page.Table.SelectedItems!);
    }

    [AvaloniaFact]
    public async Task Copy_follows_page_order_deduplicates_and_uses_csv_formula_protection()
    {
        using var page = await Page.CreateAsync();
        var first = page.Vm.Items[0];
        var second = page.Vm.Items[1];
        first.Name = "=1+1";
        second.Remark = "two,\n\"lines\"";
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        page.Vm.UpdateBatchSelection([second, first, second, new DemoRecord { Code = "other" }]);
        Assert.Equal(2, page.Vm.BatchCount);
        Assert.Equal(
            $"{first.Code},'=1+1,\r\n{second.Code},{second.Name},\"two,\n\"\"lines\"\"\"",
            page.Vm.GetBatchCopyText());
        page.Vm.IsSaving = true;
        Assert.False(page.Vm.CanCopyBatch);
        Assert.Null(page.Vm.GetBatchCopyText());
        page.Vm.IsSaving = false;
        await page.Vm.LoadAsync();
        Assert.Equal(0, page.Vm.BatchCount);
        Assert.Null(page.Vm.GetBatchCopyText());
    }

    [AvaloniaFact]
    public async Task Toolbar_select_and_clear_are_connected_and_normal_editing_returns_after_exit()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        page.Layout();
        page.Click(page.View.FindControl<Button>("SelectPageButton")!);
        Assert.Equal(3, page.Vm.BatchCount);
        Assert.True(page.View.FindControl<Button>("CopyBatchButton")!.IsEnabled);
        page.Click(page.View.FindControl<Button>("ClearBatchButton")!);
        Assert.Empty(page.Table.SelectedItems!);
        Assert.Equal(0, page.Vm.BatchCount);
        Assert.False(page.View.FindControl<Button>("CopyBatchButton")!.IsEnabled);
        page.Click(page.View.FindControl<Button>("SelectPageButton")!);
        Assert.Equal(3, page.Vm.BatchCount);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        Assert.False(page.Vm.IsBatchSelecting);
        Assert.Equal(SelectionMode.Single, page.Table.SelectionMode);
        Assert.Equal(0, page.Vm.BatchCount);
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        Assert.True(page.Vm.IsEditing);
        Assert.Equal(page.Vm.Items[0].Code, page.Vm.EditCode);
        Assert.True(page.Vm.CanUseSelectedRecord);
    }

    [AvaloniaFact]
    public async Task Rebinding_and_detaching_clear_batch_ids_and_visual_selection_together()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        page.Table.SelectAll();
        Assert.Equal(3, page.Vm.BatchCount);
        page.View.DataContext = null;
        Assert.Equal(0, page.Vm.BatchCount);
        Assert.Null(page.Vm.GetBatchCopyText());
        page.View.DataContext = page.Vm;
        page.Layout();
        Assert.Empty(page.Table.SelectedItems!);
        Assert.Equal(0, page.Vm.BatchCount);
        page.Table.SelectAll();
        page.Window.Content = null;
        Assert.Equal(0, page.Vm.BatchCount);
        page.Window.Content = page.View;
        page.Layout();
        Assert.Empty(page.Table.SelectedItems!);
        Assert.Equal(0, page.Vm.BatchCount);
    }

    [AvaloniaFact]
    public async Task Search_new_record_and_disposal_do_not_leave_a_stale_batch()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        page.Table.SelectAll();
        Assert.Equal(3, page.Vm.BatchCount);
        page.Vm.SearchText = "C001";
        await page.Vm.LoadAsync();
        Assert.Equal(0, page.Vm.BatchCount);
        page.Table.SelectAll();
        Assert.Equal(1, page.Vm.BatchCount);
        await page.Vm.NewCommand.ExecuteAsync(null);
        Assert.False(page.Vm.IsBatchSelecting);
        Assert.Equal(0, page.Vm.BatchCount);
        Assert.True(page.Vm.IsEditing);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        page.Table.SelectAll();
        page.Vm.Dispose();
        Assert.Equal(0, page.Vm.BatchCount);
        Assert.False(page.Vm.CanManageBatch);
        Assert.Null(page.Vm.GetBatchCopyText());
    }

    private sealed class Page : IDisposable
    {
        private readonly TestDatabase _database = new();
        public UnsavedChangesService Changes { get; } = new();
        public DemoListViewModel Vm { get; }
        public DemoListView View { get; }
        public Window Window { get; }
        public TableView Table => View.FindControl<TableView>("RecordsTable")!;

        private Page()
        {
            Vm = new(new DemoService(_database.Factory, NullLogger<DemoService>.Instance),
                NullLogger<DemoListViewModel>.Instance, Changes);
            View = new DemoListView { DataContext = Vm };
            Window = new Window { Content = View, Width = 1200, Height = 720 };
        }

        public static async Task<Page> CreateAsync(int count = 3)
        {
            var page = new Page();
            try
            {
                var service = new DemoService(page._database.Factory, NullLogger<DemoService>.Instance);
                for (var i = 0; i < count; i++)
                    await service.SaveAsync(new DemoRecord { Code = $"C{i:000}", Name = $"Record {i}" });
                page.Vm.PageSize = 25;
                await page.Vm.LoadAsync();
                page.Window.Show();
                page.Layout();
                return page;
            }
            catch { page.Dispose(); throw; }
        }

        public TableViewRow Row(int index)
        {
            var id = Vm.Items[index].Id;
            return Table.GetVisualDescendants().OfType<TableViewRow>()
                .Single(row => row.DataContext is DemoRecord record && record.Id == id);
        }

        public void Layout()
        {
            Dispatcher.UIThread.RunJobs();
            Window.Measure(new Size(1200, 720));
            Window.Arrange(new Rect(0, 0, 1200, 720));
            Dispatcher.UIThread.RunJobs();
        }

        public void Click(Control target)
        {
            var point = target.TranslatePoint(new Point(20, target.Bounds.Height / 2), Window)!.Value;
            Window.MouseMove(point);
            Window.MouseDown(point, MouseButton.Left);
            Window.MouseUp(point, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            Window.Close();
            Vm.Dispose();
            _database.Dispose();
        }
    }
}
