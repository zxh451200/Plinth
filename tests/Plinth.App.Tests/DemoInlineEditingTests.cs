using System;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
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
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoInlineEditingTests
{
    [AvaloniaFact]
    public async Task Stale_target_does_not_prompt_or_discard_the_current_inline_draft()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Editors()[1].Text = "Keep";
        var stale = page.Vm.Items[1];
        page.Vm.Items.Remove(stale);
        var prompts = 0;
        page.Changes.PromptAsync = () =>
        {
            prompts++;
            return Task.FromResult(UnsavedChangesChoice.Discard);
        };

        Assert.False(await page.Vm.SelectRecordAsync(stale));
        Assert.Equal(0, prompts);
        Assert.True(page.Vm.IsDirty);
        Assert.Equal("Keep", page.Editors()[1].Text);
    }

    [AvaloniaFact]
    public async Task Reselecting_the_edit_row_restores_inline_editors_without_resetting_the_draft()
    {
        using var page = await Page.CreateAsync();
        var row = page.Vm.Items[0];
        await page.Vm.SelectRecordAsync(row);
        page.Editors()[1].Text = "Keep";
        page.Vm.SelectedItem = null;
        Assert.Empty(page.Editors());

        Assert.True(await page.Vm.SelectRecordAsync(row));
        Assert.Same(row, page.Vm.SelectedItem);
        Assert.True(page.Vm.CanInlineEdit);
        Assert.True(page.Vm.IsDirty);
        Assert.Equal("Keep", page.Editors()[1].Text);
    }

    [AvaloniaFact]
    public async Task Revoked_authorization_preserves_draft_and_does_not_write()
    {
        using var database = new TestDatabase();
        var permissions = new Permissions();
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(permissions, new OfflineUserContext(), audit), audit,
            authenticationEnabled: true, production: false);
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance, operations);
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "A", Name = "Original" }));
        using var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.EditName = "Draft";
        permissions.Allowed = false;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsDirty);
        Assert.True(vm.IsEditing);
        Assert.Equal("Draft", vm.EditName);
        Assert.NotEmpty(vm.ErrorMessage);
        Assert.Equal("Original", (await service.SearchAsync("A")).Single().Name);
    }

    [AvaloniaFact]
    public async Task Rejected_production_confirmation_preserves_draft_and_does_not_write()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new Permissions(), new OfflineUserContext(), audit), audit,
            authenticationEnabled: false, production: true)
        {
            ConfirmProductionAsync = () => Task.FromResult(true),
        };
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance, operations);
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "A", Name = "Original" }));
        using var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.EditName = "Draft";
        operations.ConfirmProductionAsync = () => Task.FromResult(false);

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsDirty);
        Assert.True(vm.IsEditing);
        Assert.Equal("Draft", vm.EditName);
        Assert.Equal("Original", (await service.SearchAsync("A")).Single().Name);
    }

    [AvaloniaFact]
    public async Task Filtering_out_edit_row_keeps_form_draft_and_batch_requires_discard()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.Editors()[1].Text = "Keep outside page";
        page.Vm.SearchText = "B";
        await page.Vm.LoadAsync();
        Assert.Empty(page.Editors());
        Assert.True(page.Vm.IsEditing);
        Assert.True(page.Vm.IsDirty);
        Assert.Equal("Keep outside page", page.Vm.EditName);
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        Assert.False(page.Vm.IsBatchSelecting);
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        await page.Vm.ToggleBatchSelectionCommand.ExecuteAsync(null);
        Assert.True(page.Vm.IsBatchSelecting);
        Assert.False(page.Vm.CanInlineEdit);
    }

    [AvaloniaFact]
    public async Task Real_cell_editors_share_draft_without_mutating_rows_and_save_explicitly()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        var editors = page.Editors();
        Assert.Equal(3, editors.Length);
        editors[0].Text = "A2";
        editors[1].Text = "Changed";
        editors[2].Text = "Draft remark";
        page.Layout();
        Assert.Equal(("A2", "Changed", "Draft remark"),
            (page.Vm.EditCode, page.Vm.EditName, page.Vm.EditRemark));
        Assert.Equal("A", page.Vm.Items[0].Code);
        Assert.Equal("First", (await page.Service.SearchAsync("A")).Single().Name);
        Assert.True(page.Vm.IsDirty);
        await page.Vm.SaveCommand.ExecuteAsync(null);
        Assert.False(page.Vm.IsEditing);
        Assert.Empty(page.Editors());
        var saved = (await page.Service.SearchAsync("A2")).Single();
        Assert.Equal(("Changed", "Draft remark", 1), (saved.Name, saved.Remark, saved.Version));
    }

    [AvaloniaFact]
    public async Task Failed_validation_and_conflict_keep_cell_draft_and_block_leaving()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.Editors()[0].Text = "B";
        await page.Vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(page.Vm.CodeError);
        Assert.Equal("B", page.Editors()[0].Text);
        page.Editors()[0].Text = "A";
        page.Editors()[1].Text = "Mine";
        var external = (await page.Service.SearchAsync("A")).Single();
        external.Name = "Theirs";
        Assert.Null(await page.Service.SaveAsync(external));
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Save);
        Assert.False(await page.Vm.TryLeaveAsync());
        Assert.Equal(Loc.Get("demo.err.conflict"), page.Vm.ErrorMessage);
        Assert.Equal("Mine", page.Editors()[1].Text);
        Assert.True(page.Vm.IsDirty);
        Assert.Equal("Theirs", (await page.Service.SearchAsync("A")).Single().Name);
    }

    [AvaloniaFact]
    public async Task Cancelled_switch_and_cancel_preserve_draft_then_confirmed_discard_removes_it()
    {
        using var page = await Page.CreateAsync();
        await page.Vm.SelectRecordAsync(page.Vm.Items[0]);
        page.Layout();
        page.Editors()[1].Text = "Keep";
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
        Assert.False(await page.Vm.SelectRecordAsync(page.Vm.Items[1]));
        await page.Vm.CancelCommand.ExecuteAsync(null);
        Assert.Equal("Keep", page.Editors()[1].Text);
        page.Changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        await page.Vm.CancelCommand.ExecuteAsync(null);
        Assert.False(page.Vm.IsEditing);
        Assert.Empty(page.Editors());
        Assert.Equal("First", (await page.Service.SearchAsync("A")).Single().Name);
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
        public TextBox[] Editors()
        {
            Layout();
            // 数字编辑器自己的内部 TextBox 不算文本单元格;测试里的 0..2 仍是 Code/Name/Remark。
            return _view.FindControl<TableView>("RecordsTable")!.GetVisualDescendants()
                .OfType<TextBox>()
                .Where(box => box.IsEffectivelyVisible
                    && !box.GetVisualAncestors().OfType<NumericUpDown>().Any())
                .ToArray();
        }
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

    private sealed class Permissions : IPermissionService
    {
        public bool Allowed = true;
        public bool HasPermission(string permission) => Allowed;
        public void Demand(string permission, string correlationId)
        {
            if (!Allowed) throw new UnauthorizedAccessException("Denied");
        }
    }

    private sealed class Audit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }
}
