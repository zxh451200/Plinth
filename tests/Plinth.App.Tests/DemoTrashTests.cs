using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Localization;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>回收站:删除进回收站、活动列表过滤、恢复回列表、编码冲突拒绝恢复。</summary>
public sealed class DemoTrashTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly UnsavedChangesService _changes = new();

    public void Dispose() => _database.Dispose();

    private DemoService Service => new(_database.Factory, NullLogger<DemoService>.Instance);

    private async Task<DemoListViewModel> CreateWithDeletedRecordAsync()
    {
        var service = Service;
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "TRASH-1", Name = "待删除" }));
        var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance, _changes);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.AskDeleteCommand.Execute(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);
        return vm;
    }

    [AvaloniaFact]
    public async Task Delete_soft_deletes_and_active_list_hides_it()
    {
        var service = Service;
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "T1", Name = "原始" }));
        var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance, _changes);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.AskDeleteCommand.Execute(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Empty(vm.Items);

        // 数据仍在库里,标记为已删除,活动查询查不到,回收站查询能查到。
        var active = await service.SearchPageAsync(null, trashOnly: false);
        Assert.Empty(active.Items);
        var trash = await service.SearchPageAsync(null, trashOnly: true);
        var record = Assert.Single(trash.Items);
        Assert.True(record.IsDeleted);
        Assert.NotNull(record.DeletedAtUtc);
        Assert.EndsWith("#" + record.Id.ToString("N"), record.CodeKey);

        // 回收站模式选中后只读:不能进编辑,但出现恢复按钮。
        vm.ShowTrash = true;
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        Assert.False(vm.CanInlineEdit);
        Assert.True(vm.CanRestoreTrash);
    }

    [AvaloniaFact]
    public async Task Restore_returns_record_to_the_active_list()
    {
        using var vm = await CreateWithDeletedRecordAsync();
        vm.ShowTrash = true;
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());

        await vm.RestoreTrashCommand.ExecuteAsync(null);

        // 恢复后回收站空了,活动列表重新出现这条记录。
        Assert.Empty(vm.Items);
        vm.ShowTrash = false;
        await vm.LoadAsync();
        var restored = Assert.Single(vm.Items);
        Assert.Equal("TRASH-1", restored.Code);
        Assert.False(restored.IsDeleted);
    }

    [AvaloniaFact]
    public async Task Restore_conflicts_when_the_code_is_taken_again()
    {
        var service = Service;
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "T2", Name = "删除我" }));
        var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance, _changes);
        await vm.LoadAsync();
        await vm.SelectRecordAsync(vm.Items.Single());
        vm.AskDeleteCommand.Execute(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);

        // 同编码重新创建(回收站记录不占用编码),此时恢复应被拒绝。
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "T2", Name = "重建" }));
        var trash = (await service.SearchPageAsync(null, trashOnly: true)).Items.Single();
        var error = await service.RestoreAsync(trash.Id);
        Assert.Equal(Loc.Get("demo.trash.conflict"), error);

        // 冲突后记录仍在回收站。
        Assert.Single((await service.SearchPageAsync(null, trashOnly: true)).Items);
    }
}
