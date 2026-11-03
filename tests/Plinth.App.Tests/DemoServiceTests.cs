using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>示例模块的业务规则:校验、重复编码、搜索、并发冲突、删除。用真实的临时 SQLite 数据库,不开窗口。</summary>
public sealed class DemoServiceTests : IDisposable
{
    private readonly TestDatabase _database = new();

    public void Dispose() => _database.Dispose();

    private DemoService CreateService() => new(_database.Factory, NullLogger<DemoService>.Instance);

    private static DemoRecord Record(string code, string name = "名称", string remark = "") =>
        new() { Code = code, Name = name, Remark = remark };

    [Fact]
    public async Task A_new_record_can_be_saved_and_found()
    {
        var service = CreateService();

        var error = await service.SaveAsync(Record("  A1  ", "  苹果  "));

        Assert.Null(error);
        var found = Assert.Single(await service.SearchAsync(null));
        Assert.Equal("A1", found.Code);
        Assert.Equal("苹果", found.Name);
    }

    [Fact]
    public async Task Saved_records_survive_a_new_service_instance()
    {
        await CreateService().SaveAsync(Record("A1"));

        var again = await CreateService().SearchAsync(null);

        Assert.Single(again);
    }

    [Theory]
    [InlineData("", "名称")]
    [InlineData("   ", "名称")]
    [InlineData("A1", "")]
    public async Task Missing_code_or_name_is_rejected_and_nothing_is_saved(string code, string name)
    {
        var service = CreateService();

        var error = await service.SaveAsync(Record(code, name));

        Assert.NotNull(error);
        Assert.Empty(await service.SearchAsync(null));
    }

    [Fact]
    public async Task Too_long_fields_are_rejected()
    {
        var service = CreateService();

        Assert.NotNull(await service.SaveAsync(Record(new string('x', DemoService.MaxCodeLength + 1))));
        Assert.NotNull(await service.SaveAsync(Record("A1", new string('x', DemoService.MaxNameLength + 1))));
        Assert.NotNull(await service.SaveAsync(Record("A1", "n", new string('x', DemoService.MaxRemarkLength + 1))));
        Assert.Empty(await service.SearchAsync(null));
    }

    [Fact]
    public async Task A_duplicate_code_is_rejected_ignoring_case_but_editing_the_same_record_is_fine()
    {
        var service = CreateService();
        var first = Record("A1");
        await service.SaveAsync(first);

        Assert.NotNull(await service.SaveAsync(Record("a1")));

        first.Name = "改过的名称";
        Assert.Null(await service.SaveAsync(first));
        Assert.Equal("改过的名称", Assert.Single(await service.SearchAsync(null)).Name);
    }

    [Fact]
    public async Task Search_matches_code_or_name_ignoring_case_and_orders_by_code()
    {
        var service = CreateService();
        await service.SaveAsync(Record("B2", "Banana"));
        await service.SaveAsync(Record("A1", "Apple"));
        await service.SaveAsync(Record("C3", "cherry"));

        Assert.Equal(new[] { "A1", "B2", "C3" }, (await service.SearchAsync(null)).Select(r => r.Code).ToArray());
        Assert.Equal(new[] { "C3" }, (await service.SearchAsync("CHER")).Select(r => r.Code).ToArray());
        Assert.Equal(new[] { "B2" }, (await service.SearchAsync("b2")).Select(r => r.Code).ToArray());
        Assert.Empty(await service.SearchAsync("zzz"));
    }

    [Fact]
    public async Task Percent_and_underscore_in_the_search_box_are_plain_characters()
    {
        var service = CreateService();
        await service.SaveAsync(Record("A1", "100% 纯"));
        await service.SaveAsync(Record("A2", "ABC"));

        Assert.Equal(new[] { "A1" }, (await service.SearchAsync("100%")).Select(r => r.Code).ToArray());
        Assert.Empty(await service.SearchAsync("A_"));
    }

    [Fact]
    public async Task Saving_over_a_version_someone_else_already_changed_is_rejected()
    {
        var service = CreateService();
        var record = Record("A1", "原名");
        await service.SaveAsync(record);

        // 两个人同时打开同一条(都读到版本 0)
        var mine = (await service.SearchAsync(null)).Single();
        var theirs = (await service.SearchAsync(null)).Single();

        theirs.Name = "别人先改的";
        Assert.Null(await service.SaveAsync(theirs));

        mine.Name = "我后改的";
        var error = await service.SaveAsync(mine);

        Assert.NotNull(error);
        var stored = (await service.SearchAsync(null)).Single();
        Assert.Equal("别人先改的", stored.Name);
        Assert.Equal(1, stored.Version);
    }

    [Fact]
    public async Task Delete_removes_the_record_and_reports_when_it_is_already_gone()
    {
        var service = CreateService();
        var record = Record("A1");
        await service.SaveAsync(record);

        Assert.Null(await service.DeleteAsync(record.Id, record.Version));
        Assert.Empty(await service.SearchAsync(null));
        Assert.NotNull(await service.DeleteAsync(record.Id, record.Version));
    }
}
