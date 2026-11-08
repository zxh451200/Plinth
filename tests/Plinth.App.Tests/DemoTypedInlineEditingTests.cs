using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.Views.Controls;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoTypedInlineEditingTests
{
    [AvaloniaFact]
    public async Task Typed_editors_share_the_draft_and_save_all_four_types()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();

        var quantity = page.QuantityEditor();
        var date = page.DateEditor();
        var priority = page.PriorityEditor();
        var active = page.ActiveEditor();

        quantity.Value = 42m;
        date.SelectedDate = new DateTimeOffset(2026, 12, 31, 0, 0, 0, TimeSpan.Zero);
        priority.SelectedIndex = (int)DemoPriority.High;
        active.IsChecked = false;
        page.Layout();

        Assert.Equal(42m, page.Vm.EditQuantity);
        Assert.Equal(new DateTime(2026, 12, 31), page.Vm.EditDueDate?.Date);
        Assert.Equal(DemoPriority.High, page.Vm.EditPriority);
        Assert.False(page.Vm.EditActive);
        Assert.True(page.Vm.IsDirty);
        // 草稿不能提前写回行数据。
        Assert.Equal(0, page.Vm.Items[0].Quantity);
        Assert.Equal(DemoPriority.Normal, page.Vm.Items[0].Priority);
        Assert.True(page.Vm.Items[0].Active);

        await page.Vm.SaveCommand.ExecuteAsync(null);

        var saved = (await page.Service.SearchAsync("A")).Single();
        Assert.Equal((42, new DateTime(2026, 12, 31), DemoPriority.High, false, 1),
            (saved.Quantity, saved.DueDate!.Value.ToDateTime(TimeOnly.MinValue), saved.Priority, saved.Active, saved.Version));
        Assert.False(page.Vm.IsEditing);
    }

    [AvaloniaFact]
    public async Task Out_of_range_typed_values_block_save_with_field_errors()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();

        page.Vm.EditQuantity = DemoService.MaxQuantity + 1m;
        page.Vm.EditDueDate = new DateTimeOffset(2200, 1, 1, 0, 0, 0, TimeSpan.Zero);
        page.Vm.EditPriority = (DemoPriority)99;
        await page.Vm.SaveCommand.ExecuteAsync(null);

        Assert.True(page.Vm.IsEditing);
        Assert.NotEmpty(page.Vm.QuantityError);
        Assert.NotEmpty(page.Vm.DueDateError);
        Assert.NotEmpty(page.Vm.PriorityError);
        Assert.Equal(0, (await page.Service.SearchAsync("A")).Single().Version);

        page.Vm.EditQuantity = 7;
        page.Vm.EditDueDate = new DateTimeOffset(2026, 1, 31, 0, 0, 0, TimeSpan.Zero);
        page.Vm.EditPriority = DemoPriority.Low;
        await page.Vm.SaveCommand.ExecuteAsync(null);

        var saved = (await page.Service.SearchAsync("A")).Single();
        Assert.Equal((7, DemoPriority.Low), (saved.Quantity, saved.Priority));
    }

    [AvaloniaFact]
    public async Task Only_the_selected_row_renders_typed_editors()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        Assert.Single(page.QuantityEditors());
        Assert.Single(page.DateEditors());
        Assert.Single(page.PriorityEditors());
        Assert.Single(page.ActiveEditors());

        await page.Vm.SelectRecordAsync(page.Vm.Items[1]);
        page.Layout();
        Assert.Single(page.QuantityEditors());
        Assert.Single(page.DateEditors());
        Assert.Single(page.PriorityEditors());
        Assert.Single(page.ActiveEditors());
        Assert.Equal(0m, page.Vm.EditQuantity);
    }

    [AvaloniaFact]
    public async Task Cancel_discards_the_typed_draft_without_writing()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.QuantityEditor().Value = 55m;
        page.PriorityEditor().SelectedIndex = (int)DemoPriority.High;
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);

        await page.Vm.CancelCommand.ExecuteAsync(null);

        Assert.False(page.Vm.IsEditing);
        var stored = (await page.Service.SearchAsync("A")).Single();
        Assert.Equal((0, DemoPriority.Normal, 0), (stored.Quantity, stored.Priority, stored.Version));
    }

    private sealed class Page : IDisposable
    {
        private readonly TestDatabase _database = new();
        public UnsavedChangesService Changes { get; } = new();
        public DemoService Service { get; }
        public DemoListViewModel Vm { get; }
        private readonly DemoListView _view;
        private readonly Window _window;
        private Page()
        {
            Service = new(_database.Factory, NullLogger<DemoService>.Instance);
            Vm = new(Service, NullLogger<DemoListViewModel>.Instance, Changes);
            _view = new() { DataContext = Vm };
            _window = new() { Content = _view, Width = 1400, Height = 800 };
        }
        public static async Task<Page> CreateAsync()
        {
            var page = new Page();
            try
            {
                await page.Service.SaveAsync(new DemoRecord { Code = "A", Name = "First" });
                await page.Service.SaveAsync(new DemoRecord { Code = "B", Name = "Second" });
                await page.Vm.LoadAsync();
                page._window.Show();
                page.Layout();
                return page;
            }
            catch { page.Dispose(); throw; }
        }

        public NumericUpDown QuantityEditor() => QuantityEditors().Single();
        public DateField DateEditor() => DateEditors().Single();
        public ComboBox PriorityEditor() => PriorityEditors().Single();
        public CheckBox ActiveEditor() => ActiveEditors().Single();

        public NumericUpDown[] QuantityEditors() => InTable<NumericUpDown>(Loc.Get("demo.col.quantity"));
        public DateField[] DateEditors()
        {
            Layout();
            return Table().GetVisualDescendants().OfType<DateField>()
                .Where(editor => editor.IsEffectivelyVisible).ToArray();
        }
        public ComboBox[] PriorityEditors() => InTable<ComboBox>(Loc.Get("demo.col.priority"));
        public CheckBox[] ActiveEditors() => InTable<CheckBox>(Loc.Get("demo.col.active"));

        private T[] InTable<T>(string automationName) where T : Control
        {
            Layout();
            return Table().GetVisualDescendants().OfType<T>()
                .Where(control => control.IsEffectivelyVisible
                    && AutomationProperties.GetName(control) == automationName)
                .ToArray();
        }

        private TableView Table() =>
            _view.FindControl<TableView>("RecordsTable")!;

        public void Layout()
        {
            Dispatcher.UIThread.RunJobs();
            _window.Measure(new Size(1400, 800));
            _window.Arrange(new Rect(0, 0, 1400, 800));
            Dispatcher.UIThread.RunJobs();
        }

        public void Dispose()
        {
            _window.Close();
            Vm.Dispose();
            _database.Dispose();
        }
    }
}
