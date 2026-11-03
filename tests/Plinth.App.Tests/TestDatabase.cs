using System;
using System.IO;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Data;

namespace Plinth.App.Tests;

/// <summary>每个测试一个独立的临时 SQLite 文件,用完删除。测的是真实的数据库行为(唯一索引、并发令牌),不是假数据。</summary>
public sealed class TestDatabase : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "plinth-test-" + Guid.NewGuid().ToString("N") + ".db");
    private readonly ServiceProvider _provider;

    public TestDatabase()
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite($"Data Source={_path}"));
        _provider = services.BuildServiceProvider();

        using var db = Factory.CreateDbContext();
        DatabaseInitializer.Initialize(db);
    }

    public IDbContextFactory<AppDbContext> Factory => _provider.GetRequiredService<IDbContextFactory<AppDbContext>>();

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools(); // 连接池会占着文件,不释放就删不掉
        foreach (var file in new[] { _path, _path + "-wal", _path + "-shm" })
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
