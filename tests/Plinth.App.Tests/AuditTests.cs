using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Data;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>审计字段:新增和修改时自动填写时间与操作人。</summary>
[Collection("CurrentUser")] // CurrentUser 是全局的,用到它的测试不能并行
public sealed class AuditTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly Func<string> _originalResolver = CurrentUser.Resolve;

    public void Dispose()
    {
        CurrentUser.Resolve = _originalResolver;
        _database.Dispose();
    }

    [Fact]
    public async Task Saving_stamps_creator_on_add_and_modifier_on_update()
    {
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance);
        var before = DateTime.UtcNow.AddSeconds(-1);

        CurrentUser.Resolve = () => "alice";
        var id = Guid.NewGuid();
        await service.SaveAsync(new DemoRecord { Id = id, Code = "A1", Name = "苹果" });
        var created = (await service.SearchAsync(null)).Single();

        CurrentUser.Resolve = () => "bob";
        await service.SaveAsync(new DemoRecord { Id = id, Version = created.Version, Code = "A1", Name = "新名" });
        var updated = (await service.SearchAsync(null)).Single();

        Assert.Equal("alice", created.CreatedBy);
        Assert.Equal("alice", created.UpdatedBy);
        Assert.True(created.CreatedAtUtc >= before);
        Assert.Equal("alice", updated.CreatedBy);
        Assert.Equal("bob", updated.UpdatedBy);
        Assert.Equal(created.CreatedAtUtc, updated.CreatedAtUtc);
        Assert.True(updated.UpdatedAtUtc >= created.UpdatedAtUtc);
    }

    [Fact]
    public async Task A_very_long_user_name_is_truncated_instead_of_failing_the_save()
    {
        CurrentUser.Resolve = () => new string('x', 300);
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance);

        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "A1", Name = "苹果" }));
        Assert.Equal(AppDbContext.UserNameMaxLength, (await service.SearchAsync(null)).Single().CreatedBy.Length);
    }
}
