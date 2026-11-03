using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoEditingProtectionTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly UnsavedChangesService _changes = new();
    private DemoService Service => new(_database.Factory, NullLogger<DemoService>.Instance);
    public void Dispose() => _database.Dispose();

    private async Task<DemoListViewModel> CreateAsync()
    {
        await Service.SaveAsync(new DemoRecord { Code = "A", Name = "First" });
        await Service.SaveAsync(new DemoRecord { Code = "B", Name = "Second" });
        var vm = new DemoListViewModel(Service, NullLogger<DemoListViewModel>.Instance, _changes);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items[0]);
        return vm;
    }

    [Fact]
    public async Task Reverting_input_clears_dirty_state_without_a_prompt()
    {
        using var vm = await CreateAsync();
        var name = vm.EditName;
        vm.EditName = "Changed";
        Assert.True(vm.IsDirty);
        vm.EditName = name;
        Assert.False(vm.IsDirty);
        Assert.True(await vm.TryLeaveAsync());
    }

    [Fact]
    public async Task Cancel_preserves_record_selection_and_all_input()
    {
        using var vm = await CreateAsync();
        var selected = vm.SelectedItem;
        vm.EditRemark = "Unsaved";
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
        Assert.False(await vm.SelectRecordAsync(vm.Items[1]));
        Assert.Same(selected, vm.SelectedItem);
        Assert.Equal("Unsaved", vm.EditRemark);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Discard_switches_record_without_writing_dirty_values()
    {
        using var vm = await CreateAsync();
        vm.EditName = "Unsaved";
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        Assert.True(await vm.SelectRecordAsync(vm.Items[1]));
        Assert.Equal("Second", vm.EditName);
        Assert.False(vm.IsDirty);
        Assert.Equal("First", (await Service.SearchPageAsync("A")).Items.Single().Name);
    }

    [Fact]
    public async Task Save_before_switch_persists_the_previous_record()
    {
        using var vm = await CreateAsync();
        vm.EditName = "Saved";
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Save);
        var target = vm.Items[1];
        Assert.True(await vm.SelectRecordAsync(target));
        Assert.Equal(target.Id, vm.SelectedItem?.Id);
        Assert.Equal("Saved", (await Service.SearchPageAsync("A")).Items.Single().Name);
    }

    [Fact]
    public async Task Failed_save_blocks_leaving_and_preserves_dirty_input()
    {
        using var vm = await CreateAsync();
        vm.EditCode = "B";
        vm.EditName = "Duplicate";
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Save);
        Assert.False(await vm.TryLeaveAsync());
        Assert.True(vm.IsDirty);
        Assert.True(vm.IsEditing);
        Assert.Equal("Duplicate", vm.EditName);
        Assert.NotEmpty(vm.CodeError);
        Assert.Empty(vm.ErrorMessage);
    }

    [Fact]
    public async Task Invalid_fields_block_save_and_are_reported_at_the_field()
    {
        using var vm = await CreateAsync();
        vm.EditCode = string.Empty;
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Save);
        Assert.False(await vm.TryLeaveAsync());
        Assert.NotEmpty(vm.CodeError);
        Assert.Empty(vm.NameError);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task New_and_cancel_use_the_same_discard_guard()
    {
        using var vm = await CreateAsync();
        vm.EditName = "Unsaved";
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Cancel);
        await vm.NewCommand.ExecuteAsync(null);
        Assert.Equal("Unsaved", vm.EditName);
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);
        await vm.CancelCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        Assert.False(vm.IsDirty);
    }

    [Fact]
    public async Task Disposal_while_prompting_rejects_late_record_switch()
    {
        using var vm = await CreateAsync();
        vm.EditName = "Unsaved";
        var response = new TaskCompletionSource<UnsavedChangesChoice>();
        _changes.PromptAsync = () => response.Task;
        var switching = vm.SelectRecordAsync(vm.Items[1]);
        vm.Dispose();
        response.SetResult(UnsavedChangesChoice.Discard);
        Assert.False(await switching);
        Assert.Equal("Unsaved", vm.EditName);
        Assert.True(vm.IsDirty);
    }

    [Fact]
    public async Task Refresh_and_filter_preserve_dirty_form()
    {
        using var vm = await CreateAsync();
        vm.EditName = "Unsaved";
        vm.SearchText = "B";
        await vm.LoadAsync();
        Assert.Single(vm.Items);
        Assert.Equal("Unsaved", vm.EditName);
        Assert.True(vm.IsDirty);
    }
}
