using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Plinth.App.Data;
using Plinth.App.Modules.Demo.Data;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 服务器数据库集成冒烟。没有连接串时显式 Skip，不能把 Skip 当通过；
/// 连接串只从环境变量读取，避免把密码写进仓库或测试输出。
/// </summary>
public sealed class DatabaseProviderIntegrationTests
{
    [Fact]
    public async Task PostgreSql_migrates_and_round_trips_a_demo_record()
    {
        var connectionString = Environment.GetEnvironmentVariable("PLINTH_TEST_POSTGRES");
        if (string.IsNullOrWhiteSpace(connectionString)) Assert.Skip("PLINTH_TEST_POSTGRES is not set");
        RequireIsolatedDatabase();
        await RunRedactedAsync(() => RoundTripAsync(DatabaseConfiguration.PostgreSql, connectionString!));
    }

    [Fact]
    public async Task MySql_migrates_and_round_trips_a_demo_record()
    {
        var connectionString = Environment.GetEnvironmentVariable("PLINTH_TEST_MYSQL");
        if (string.IsNullOrWhiteSpace(connectionString)) Assert.Skip("PLINTH_TEST_MYSQL is not set");
        RequireIsolatedDatabase();
        await RunRedactedAsync(() => RoundTripAsync(DatabaseConfiguration.MySql, connectionString!));
    }

    private static async Task RunRedactedAsync(Func<Task> test)
    {
        try
        {
            await test();
        }
        catch (Xunit.Sdk.XunitException)
        {
            throw;
        }
        catch (Exception)
        {
            // Provider messages may contain the connection string. Never attach the
            // original exception or its message to the test result.
            throw new InvalidOperationException("服务器数据库集成冒烟失败；原始异常未写入测试输出，请在专用环境中查看服务端日志。");
        }
    }

    private static void RequireIsolatedDatabase()
    {
        // A connection string cannot prove that the server database is disposable.
        // Require an explicit operator attestation before applying migrations or deleting rows.
        if (!string.Equals(Environment.GetEnvironmentVariable("PLINTH_TEST_DATABASE_ISOLATED"),
                "true", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(Environment.GetEnvironmentVariable("PLINTH_TEST_DATABASE_PURPOSE"),
                "plinth-integration", StringComparison.Ordinal))
        {
            Assert.Skip("服务器数据库集成测试要求专用、可清理数据库，并显式设置 PLINTH_TEST_DATABASE_ISOLATED=true 和 PLINTH_TEST_DATABASE_PURPOSE=plinth-integration。");
        }
    }

    private static async Task RoundTripAsync(string provider, string connectionString)
    {
        var configuration = DatabaseConfiguration.Resolve(new DatabaseOptions
        {
            Provider = provider,
            ConnectionString = connectionString,
        }, null, null, null);
        var builder = new DbContextOptionsBuilder<AppDbContext>();
        configuration.Configure(builder);
        var options = builder.Options;
        await using var db = new AppDbContext(options);
        // 覆盖应用实际启动入口，不以 GenerateScript 或直接 Migrate 替代。
        DatabaseInitializer.Initialize(db);
        await using var migrations = DatabaseConfiguration.CreateMigrationContext(db);
        var expected = provider == DatabaseConfiguration.MySql
            ? Plinth.App.Migrations.MySql.MySqlInitialSchema.MigrationId
            : Plinth.App.Migrations.PostgreSql.PostgreSqlInitialSchema.MigrationId;
        Assert.Equal(new[] { expected }, await migrations.Database.GetAppliedMigrationsAsync());
        Assert.Empty(await migrations.Database.GetPendingMigrationsAsync());
        Assert.False(migrations.Database.HasPendingModelChanges());
        DatabaseInitializer.Initialize(db);
        var runPrefix = "PLINTH-INT-" + Guid.NewGuid().ToString("N");
        var record = new DemoRecord
        {
            Code = runPrefix,
            Name = "服务器数据库",
            Remark = "集成冒烟",
        };
        try
        {
            db.Add(record);
            await db.SaveChangesAsync();
            await using var read = new AppDbContext(options);
            var stored = await read.Set<DemoRecord>().AsNoTracking().SingleAsync(row => row.Id == record.Id);
            Assert.Equal(record.Code, stored.Code);
            Assert.Equal(CodeIdentity.Create(record.Code), stored.CodeKey);
            Assert.Equal(record.Name, stored.Name);
            Assert.Equal(record.Remark, stored.Remark);
            Assert.NotEqual(default, stored.CreatedAtUtc);
        }
        finally
        {
            // Use the unique run prefix so a failed assertion still removes this run's row.
            await using var cleanup = new AppDbContext(options);
            await cleanup.Set<DemoRecord>().Where(row => row.Code == runPrefix).ExecuteDeleteAsync();
        }
    }
}
