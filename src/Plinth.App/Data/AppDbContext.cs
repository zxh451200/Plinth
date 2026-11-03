using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;

namespace Plinth.App.Data;

/// <summary>
/// 整个程序唯一的数据库上下文(规范 §8.2)。各模块把表结构写成 IEntityTypeConfiguration 放在自己的 Data 文件夹里,
/// 这里自动找到并使用,新增模块的表不需要改这个文件。
/// 继承 <see cref="AuditableEntity"/> 的表,保存时自动填写创建/修改的时间和人。
/// </summary>
public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public const int UserNameMaxLength = 100;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

        // 所有带审计字段的表,人名列统一限长、不允许为空
        foreach (var entity in modelBuilder.Model.GetEntityTypes().Where(type => typeof(AuditableEntity).IsAssignableFrom(type.ClrType)).ToList())
        {
            modelBuilder.Entity(entity.ClrType).Property(nameof(AuditableEntity.CreatedBy)).IsRequired().HasMaxLength(UserNameMaxLength);
            modelBuilder.Entity(entity.ClrType).Property(nameof(AuditableEntity.UpdatedBy)).IsRequired().HasMaxLength(UserNameMaxLength);
        }
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampAuditFields();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampAuditFields();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    private void StampAuditFields()
    {
        var now = DateTime.UtcNow;
        var user = CurrentUser.Name;
        if (user.Length > UserNameMaxLength)
        {
            user = user[..UserNameMaxLength];
        }

        // Entries() 会先检测改动,所以这里看到的状态是准确的
        foreach (var entry in ChangeTracker.Entries<AuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                entry.Entity.CreatedAtUtc = now;
                entry.Entity.CreatedBy = user;
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.UpdatedBy = user;
            }
            else if (entry.State == EntityState.Modified)
            {
                entry.Entity.UpdatedAtUtc = now;
                entry.Entity.UpdatedBy = user;
            }
        }
    }
}
