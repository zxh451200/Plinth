using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>示例页的界面逻辑:新增、编辑、保存失败保留输入、删除要确认、搜索。用真实的临时数据库。</summary>
public sealed class DemoListViewModelTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    /// <summary>建好服务,先存三条数据,再建页面并等它读完。</summary>
    private async Task<DemoListViewModel> CreateAsync()
    {
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance);
        foreach (var (code, name) in new[] { ("A001", "示例一"), ("A002", "示例二"), ("B001", "示例三") })
        {
            await service.SaveAsync(new DemoRecord { Code = code, Name = name });
        }

        var vm = new DemoListViewModel(service, NullLogger<DemoListViewModel>.Instance, new UnsavedChangesService());
        await vm.LoadAsync();
        return vm;
    }

    [Fact]
    public async Task The_list_starts_with_the_saved_data_and_the_form_is_hidden()
    {
        var vm = await CreateAsync();

        Assert.Equal(3, vm.Items.Count);
        Assert.False(vm.IsEditing);
        Assert.Equal("共 3 条", vm.StatusText);
    }

    [Fact]
    public async Task Selecting_a_row_opens_the_form_with_its_values_and_allows_delete()
    {
        var vm = await CreateAsync();

        vm.SelectedItem = vm.Items[0];

        Assert.True(vm.IsEditing);
        Assert.Equal(vm.Items[0].Code, vm.EditCode);
        Assert.True(vm.CanDelete);
    }

    [Fact]
    public async Task A_new_record_is_added_to_the_list_and_the_form_closes()
    {
        var vm = await CreateAsync();

        vm.NewCommand.Execute(null);
        Assert.True(vm.IsEditing);
        Assert.False(vm.CanDelete);
        vm.EditCode = "Z9";
        vm.EditName = "新记录";
        await vm.SaveCommand.ExecuteAsync(null);

        Assert.False(vm.IsEditing);
        Assert.Equal(4, vm.Items.Count);
        Assert.Contains(vm.Items, item => item.Code == "Z9");
    }

    [Fact]
    public async Task A_failed_save_keeps_the_form_open_and_the_input_and_shows_the_reason()
    {
        var vm = await CreateAsync();
        vm.NewCommand.Execute(null);
        vm.EditCode = "A001"; // 已经有了
        vm.EditName = "重复编码";

        await vm.SaveCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.Equal("A001", vm.EditCode);
        Assert.Equal("重复编码", vm.EditName);
        Assert.NotEmpty(vm.CodeError);
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(3, vm.Items.Count);
    }

    [Fact]
    public async Task Delete_needs_a_second_confirmation()
    {
        var vm = await CreateAsync();
        vm.SelectedItem = vm.Items[0];

        vm.AskDeleteCommand.Execute(null);
        Assert.True(vm.IsConfirmingDelete);
        Assert.Equal(3, vm.Items.Count);

        vm.CancelDeleteCommand.Execute(null);
        Assert.False(vm.IsConfirmingDelete);
        Assert.Equal(3, vm.Items.Count);

        vm.AskDeleteCommand.Execute(null);
        await vm.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Items.Count);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task Typing_in_the_search_box_filters_the_list()
    {
        var vm = await CreateAsync();

        vm.SearchText = "B001";
        await vm.LoadAsync(); // 输入会自动触发查询;这里再等一次,确保读完

        Assert.Equal(new[] { "B001" }, vm.Items.Select(item => item.Code).ToArray());
        Assert.Equal("共 1 条", vm.StatusText);
    }

    [Fact]
    public async Task Cancel_keeps_dirty_input_when_no_discard_is_confirmed()
    {
        var vm = await CreateAsync();
        vm.NewCommand.Execute(null);
        vm.EditCode = "Z9";
        vm.EditName = "不保存";

        await vm.CancelCommand.ExecuteAsync(null);

        Assert.True(vm.IsEditing);
        Assert.True(vm.IsDirty);
        Assert.Equal("不保存", vm.EditName);
        Assert.Equal(3, vm.Items.Count);
    }
}
