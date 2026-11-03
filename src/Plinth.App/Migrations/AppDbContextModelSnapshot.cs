using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Plinth.App.Data;

namespace Plinth.App.Migrations;

// 固定复制 CodeIdentityKeys 的目标模型；不引用可变的当前配置。
[DbContext(typeof(AppDbContext))]
public sealed class AppDbContextModelSnapshot : ModelSnapshot
{
    protected override void BuildModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.14");
        modelBuilder.Entity("Plinth.App.Modules.Demo.Data.DemoRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("TEXT");
            entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("TEXT");
            entity.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            entity.Property<string>("Remark").IsRequired().HasMaxLength(500).HasColumnType("TEXT");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("INTEGER");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("CodeKey").IsUnique();
            entity.ToTable("demo_records");
        });
        modelBuilder.Entity("Plinth.App.Modules.Notes.Data.Note", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("TEXT");
            entity.Property<string>("CodeKey").IsRequired().HasMaxLength(300).HasColumnType("TEXT");
            entity.Property<string>("Text").IsRequired().HasMaxLength(2000).HasColumnType("TEXT");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("INTEGER");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("CodeKey").IsUnique();
            entity.ToTable("notes_entries");
        });
    }
}
