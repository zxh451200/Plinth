using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>撤销/重做:把已提交编辑的前/后值放回表单作草稿;删除后栈条目清除。</summary>
public sealed class DemoUndoRedoTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly UnsavedChangesService _changes = new();

    public void Dispose() => _database.Dispose();

    private async Task<DemoListViewModel> CreateWithSavedEditAsync(string editedName = "Changed")
    {
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance);
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "A", Name = "Original" }));
        var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance, _changes);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.EditName = editedName;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing); // 保存成功后表单关闭
        return vm;
    }

    [AvaloniaFact]
    public async Task Undo_after_save_restores_previous_values_as_draft()
    {
        using var vm = await CreateWithSavedEditAsync();
        Assert.True(vm.CanUndo);
        Assert.False(vm.CanRedo);

        await vm.UndoCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal("Original", vm.EditName);
        Assert.Equal("A", vm.EditCode);
        Assert.True(vm.IsDirty); // 草稿与库里当前值不同
        Assert.False(vm.CanUndo);
        Assert.True(vm.CanRedo);
    }

    [AvaloniaFact]
    public async Task Redo_reapplies_the_saved_values_as_draft()
    {
        using var vm = await CreateWithSavedEditAsync();
        await vm.UndoCommand.ExecuteAsync(null);
        // 撤销出的草稿是脏的,重做会被未保存确认拦住;测试选择丢弃草稿。
        _changes.PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard);

        await vm.RedoCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal("Changed", vm.EditName);
        // 库里已经是保存后的值,重放 After 与库一致,表单不脏。
        Assert.False(vm.IsDirty);
        Assert.True(vm.CanUndo);
        Assert.False(vm.CanRedo);
    }

    [AvaloniaFact]
    public async Task Undo_then_save_pushes_a_new_undo_entry()
    {
        using var vm = await CreateWithSavedEditAsync();
        await vm.UndoCommand.ExecuteAsync(null);
        await vm.SaveCommand.ExecuteAsync(null); // 把撤销出的旧值再存回去
        Assert.False(vm.IsEditing);

        // 再次撤销应该又回到 "Changed"。
        await vm.UndoCommand.ExecuteAsync(null);
        Assert.True(vm.IsEditing);
        Assert.Equal("Changed", vm.EditName);
    }

    [AvaloniaFact]
    public async Task Deleting_the_record_clears_the_stacks()
    {
        using var vm = await CreateWithSavedEditAsync();
        Assert.True(vm.CanUndo);
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.AskDeleteCommand.Execute(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);

        Assert.False(vm.CanUndo);
        Assert.False(vm.CanRedo);
    }
}
