using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Plinth.App.Modules.Demo.Data;

/// <summary>表结构配置。AppDbContext 会自动找到并使用它,新增模块的表照这个写法。</summary>
public class DemoRecordConfiguration : IEntityTypeConfiguration<DemoRecord>
{
    public void Configure(EntityTypeBuilder<DemoRecord> builder)
    {
        // 表名带模块前缀,方便区分是谁的表
        builder.ToTable("demo_records");

        builder.HasKey(record => record.Id);

        builder.Property(record => record.Code).IsRequired().HasMaxLength(50);
        // 50 个 UTF-16 字符至多 150 个 UTF-8 字节，每字节两个十六进制字符。
        builder.Property(record => record.CodeKey).IsRequired().HasMaxLength(300);
        builder.Property(record => record.Name).IsRequired().HasMaxLength(200);
        builder.Property(record => record.Remark).IsRequired().HasMaxLength(500);
        // 枚举按名字存,换库和直接读库都清楚;未知值在读取时由 EF 拒绝。
        builder.Property(record => record.Priority).HasConversion<string>().HasMaxLength(16);
        builder.Property(record => record.Version).IsConcurrencyToken();

        builder.HasIndex(record => record.CodeKey).IsUnique();
    }
}
