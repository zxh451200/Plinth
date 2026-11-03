using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Navigation;
using Plinth.App.Security;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>使用 TestDatabase 的真实 SQLite 文件验证字段错误及表单生命周期。</summary>
public sealed class DemoFieldValidationTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Permissions _permissions = new();
    private readonly Audit _audit = new();
    private readonly SensitiveOperationService _operations;
    private readonly DemoService _service;
    private int _confirmations;

    public DemoFieldValidationTests()
    {
        var users = new OfflineUserContext();
        _operations = new(new AuthorizedOperation(_permissions, users, _audit), _audit,
            authenticationEnabled: true, production: true)
        {
            ConfirmProductionAsync = () =>
            {
                _confirmations++;
                return Task.FromResult(true);
            },
        };
        _service = new(_database.Factory, NullLogger<DemoService>.Instance, _operations);
    }

    public void Dispose() => _database.Dispose();

    private static DemoSaveInput Input(string code = "A1", string name = "名称", string remark = "") =>
        new(Guid.NewGuid(), 0, code, name, remark);

    private async Task<DemoListViewModel> CreateNewAsync()
    {
        var vm = new DemoListViewModel(_service, NullLogger<DemoListViewModel>.Instance,
            new UnsavedChangesService { PromptAsync = () => Task.FromResult(UnsavedChangesChoice.Discard) });
        await vm.LoadAsync();
        await vm.NewCommand.ExecuteAsync(null);
        vm.EditCode = "A1";
        vm.EditName = "名称";
        return vm;
    }

    private static void AssertNoFieldErrors(DemoListViewModel vm)
    {
        Assert.Empty(vm.CodeError);
        Assert.Empty(vm.NameError);
        Assert.Empty(vm.RemarkError);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Unpaired_surrogates_are_field_errors_before_entity_construction(bool high)
    {
        // 运行时构造，避免测试数据序列化器替换非法 UTF-16。
        var code = new string(high ? '\uD800' : '\uDC00', 1);
        var error = await _service.SaveValidatedAsync(Input(code));
        Assert.NotNull(error);
        Assert.Equal(DemoSaveField.Code, error.Field);
        Assert.Equal(Loc.Get("demo.err.codeInvalid"), error.Message);
        Assert.Empty(await _service.SearchAsync(null));

        using var vm = await CreateNewAsync();
        vm.EditCode = code;
        var before = _permissions.Demands;
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("demo.err.codeInvalid"), vm.CodeError);
        Assert.Empty(vm.ErrorMessage);
        Assert.True(vm.IsEditing);
        Assert.Equal(before, _permissions.Demands);
    }

    [Fact]
    public async Task Required_and_length_errors_have_stable_fields()
    {
        var cases = new[]
        {
            (Input(" "), DemoSaveField.Code),
            (Input(new string('x', DemoService.MaxCodeLength + 1)), DemoSaveField.Code),
            (Input(name: " "), DemoSaveField.Name),
            (Input(name: new string('x', DemoService.MaxNameLength + 1)), DemoSaveField.Name),
            (Input(remark: new string('x', DemoService.MaxRemarkLength + 1)), DemoSaveField.Remark),
        };
        foreach (var (input, field) in cases)
        {
            var error = await _service.SaveValidatedAsync(input);
            Assert.NotNull(error);
            Assert.Equal(field, error.Field);
            Assert.NotEmpty(error.Message);
        }
        Assert.Empty(await _service.SearchAsync(null));
    }

    [Fact]
    public async Task Valid_surrogate_pair_is_saved_without_replacement()
    {
        const string code = "A\U0001F600";
        Assert.Null(await _service.SaveValidatedAsync(Input(code)));
        Assert.Equal(code, Assert.Single(await _service.SearchAsync(null)).Code);
    }

    [Fact]
    public async Task Local_errors_clear_as_each_field_is_corrected()
    {
        using var vm = await CreateNewAsync();
        vm.EditCode = " ";
        vm.EditName = "";
        vm.EditRemark = new string('x', DemoService.MaxRemarkLength + 1);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.CodeError);
        Assert.NotEmpty(vm.NameError);
        Assert.NotEmpty(vm.RemarkError);
        Assert.Equal(0, _permissions.Demands);
        vm.EditCode = "B1";
        Assert.Empty(vm.CodeError);
        Assert.NotEmpty(vm.NameError);
        Assert.NotEmpty(vm.RemarkError);
        vm.EditName = "名称";
        Assert.Empty(vm.NameError);
        Assert.NotEmpty(vm.RemarkError);
        vm.EditRemark = "";
        AssertNoFieldErrors(vm);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
    }

    [Fact]
    public async Task Duplicate_has_code_field_and_legacy_entry_confirms_and_audits_once_with_two_demands()
    {
        var record = new DemoRecord { Code = " A1 ", Name = "名称" };
        Assert.Null(await _service.SaveAsync(record));
        Assert.Equal(2, _permissions.Demands);
        Assert.Equal(1, _confirmations);
        Assert.Equal("success", Assert.Single(_audit.Events).Result);

        var error = await _service.SaveValidatedAsync(Input("a1"));
        Assert.NotNull(error);
        Assert.Equal(DemoSaveField.Code, error.Field);
        Assert.Equal(4, _permissions.Demands);
        Assert.Equal(2, _confirmations);
        Assert.Equal("rejected", _audit.Events.Last().Result);
        Assert.Equal(2, _audit.Events.Count);
        Assert.Single(await _service.SearchAsync(null));
    }

    [Fact]
    public async Task Duplicate_is_inline_and_only_editing_code_clears_it()
    {
        Assert.Null(await _service.SaveValidatedAsync(Input()));
        using var vm = await CreateNewAsync();
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.CodeError);
        Assert.Empty(vm.ErrorMessage);
        var error = vm.CodeError;
        vm.EditName = "新名称";
        vm.EditRemark = "备注";
        Assert.Equal(error, vm.CodeError);
        vm.EditCode = "B1";
        AssertNoFieldErrors(vm);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        Assert.Equal(2, (await _service.SearchAsync(null)).Count);
    }

    [Fact]
    public async Task Retry_without_editing_rechecks_duplicate_after_other_record_is_deleted()
    {
        var occupied = Input();
        Assert.Null(await _service.SaveValidatedAsync(occupied));
        using var vm = await CreateNewAsync();
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.CodeError);
        Assert.Null(await _service.DeleteAsync(occupied.Id, occupied.Version));
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        AssertNoFieldErrors(vm);
        Assert.Single(await _service.SearchAsync(null));
    }

    [Theory]
    [InlineData("new")]
    [InlineData("select")]
    [InlineData("cancel")]
    public async Task Changing_form_clears_all_validation(string action)
    {
        Assert.Null(await _service.SaveValidatedAsync(Input()));
        using var vm = await CreateNewAsync();
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.NotEmpty(vm.CodeError);
        if (action == "new") await vm.NewCommand.ExecuteAsync(null);
        else if (action == "select") Assert.True(await vm.SelectRecordAsync(vm.Items.Single()));
        else await vm.CancelCommand.ExecuteAsync(null);
        AssertNoFieldErrors(vm);
        Assert.Empty(vm.ErrorMessage);
        Assert.Equal(action != "cancel", vm.IsEditing);
    }

    [Fact]
    public async Task Concurrency_conflict_stays_at_form_level()
    {
        Assert.Null(await _service.SaveValidatedAsync(Input()));
        using var vm = await CreateNewAsync();
        Assert.True(await vm.SelectRecordAsync(vm.Items.Single()));
        var theirs = (await _service.SearchAsync(null)).Single();
        theirs.Name = "他人先保存";
        Assert.Null(await _service.SaveAsync(theirs));
        var stale = await _service.SaveValidatedAsync(new(theirs.Id, 0, theirs.Code, "旧版本", ""));
        Assert.NotNull(stale);
        Assert.Equal(DemoSaveField.Form, stale.Field);
        vm.EditName = "我的修改";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("demo.err.conflict"), vm.ErrorMessage);
        AssertNoFieldErrors(vm);
        Assert.True(vm.IsEditing);
        Assert.Equal("他人先保存", (await _service.SearchAsync(null)).Single().Name);
    }

    [Fact]
    public async Task Deleted_version_zero_record_is_not_recreated_by_stale_editor()
    {
        var input = Input();
        Assert.Null(await _service.SaveValidatedAsync(input));
        using var vm = await CreateNewAsync();
        var record = Assert.Single(vm.Items);
        Assert.Equal(0, record.Version);
        Assert.True(await vm.SelectRecordAsync(record));
        Assert.Null(await _service.DeleteAsync(record.Id, record.Version));

        var error = await _service.SaveValidatedAsync(input with { MustExist = true });
        Assert.NotNull(error);
        Assert.Equal(DemoSaveField.Form, error.Field);
        Assert.Equal(Loc.Get("demo.err.conflict"), error.Message);

        vm.EditName = "删除后仍保留的输入";
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("demo.err.conflict"), vm.ErrorMessage);
        Assert.True(vm.IsEditing);
        Assert.True(vm.IsDirty);
        Assert.Equal("删除后仍保留的输入", vm.EditName);
        AssertNoFieldErrors(vm);
        Assert.Empty(await _service.SearchAsync(null));
    }

    [Fact]
    public async Task Non_field_exception_stays_at_form_level_and_can_be_retried()
    {
        using var vm = await CreateNewAsync();
        _operations.ConfirmProductionAsync = () => throw new System.IO.IOException("Connection lost");
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.Equal(Loc.Get("demo.err.saveFailed"), vm.ErrorMessage);
        AssertNoFieldErrors(vm);
        _operations.ConfirmProductionAsync = () => Task.FromResult(true);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        Assert.Empty(vm.ErrorMessage);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Results_for_old_input_do_not_bind_to_changed_input(bool succeeds)
    {
        if (!succeeds) Assert.Null(await _service.SaveValidatedAsync(Input()));
        using var vm = await CreateNewAsync();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.ConfirmProductionAsync = async () =>
        {
            entered.TrySetResult(true);
            return await release.Task;
        };
        var save = vm.SaveCommand.ExecuteAsync(null);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.EditCode = "B1";
            vm.EditName = "保存期间的新输入";
        }
        finally { release.TrySetResult(true); }
        await save;
        Assert.True(vm.IsEditing);
        Assert.Equal("B1", vm.EditCode);
        Assert.True(vm.IsDirty);
        AssertNoFieldErrors(vm);
        Assert.Empty(vm.ErrorMessage);

        _operations.ConfirmProductionAsync = () => Task.FromResult(true);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        var stored = await _service.SearchAsync(null);
        Assert.Equal(succeeds ? 1 : 2, stored.Count);
        Assert.Contains(stored, r => r.Code == "B1" && r.Name == "保存期间的新输入");
    }

    [Fact]
    public async Task Successful_edit_with_new_input_keeps_updated_version_for_retry()
    {
        Assert.Null(await _service.SaveValidatedAsync(Input()));
        using var vm = await CreateNewAsync();
        Assert.True(await vm.SelectRecordAsync(vm.Items.Single()));
        vm.EditName = "第一次修改";
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _operations.ConfirmProductionAsync = () => release.Task;
        var save = vm.SaveCommand.ExecuteAsync(null);
        Assert.True(vm.IsSaving);
        vm.EditName = "保存期间的第二次修改";
        release.SetResult(true);
        await save;
        Assert.True(vm.IsEditing);
        Assert.True(vm.IsDirty);
        AssertNoFieldErrors(vm);
        _operations.ConfirmProductionAsync = () => Task.FromResult(true);
        await vm.SaveCommand.ExecuteAsync(null);
        Assert.False(vm.IsEditing);
        var stored = Assert.Single(await _service.SearchAsync(null));
        Assert.Equal("保存期间的第二次修改", stored.Name);
        Assert.Equal(2, stored.Version);
    }

    private sealed class Permissions : IPermissionService
    {
        public int Demands { get; private set; }
        public bool HasPermission(string permission) => true;
        public void Demand(string permission, string correlationId) => Demands++;
    }

    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Events { get; } = [];
        public void Write(AuditEvent entry) => Events.Add(entry);
    }
}
