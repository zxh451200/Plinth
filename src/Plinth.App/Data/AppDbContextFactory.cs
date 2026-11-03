using System.IO;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace Plinth.App.Data;

/// <summary>
/// 只给 `dotnet ef` 命令用(生成迁移时它不能启动整个桌面程序)。
/// 用临时目录里的一个数据库文件,绝不碰用户真实的数据库。
/// 生成迁移的命令见 GETTING_STARTED.md。
/// </summary>
public sealed class AppDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var path = Path.Combine(Path.GetTempPath(), "plinth-design-time.db");
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options;
        return new AppDbContext(options);
    }
}
