using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Plinth.App.Data;

namespace Plinth.App.Migrations;

// 固定 SQLite 基线，显式记录本次交付的 DDL，不调用当前 AppDbContext 的模型配置。
[DbContext(typeof(AppDbContext))]
[Migration("20261101000000_InitialSchema")]
public sealed class InitialSchema : Migration
{
    public const string MigrationId = "20261101000000_InitialSchema";

    internal const string CreateSql = """
        CREATE TABLE "demo_records" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_demo_records" PRIMARY KEY,
            "Code" TEXT COLLATE NOCASE NOT NULL,
            "Name" TEXT NOT NULL,
            "Remark" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "IX_demo_records_Code" ON "demo_records" ("Code");
        """;

    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(CreateSql);
    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("demo_records");

    protected override void BuildTargetModel(ModelBuilder modelBuilder) => BuildInitialModel(modelBuilder);

    // 历史定义留在迁移本身，未来 EF 重写 snapshot 不会改变本迁移。
    internal static void BuildInitialModel(ModelBuilder modelBuilder)
    {
        modelBuilder.HasAnnotation("ProductVersion", "10.0.14");
        modelBuilder.Entity("Plinth.App.Modules.Demo.Data.DemoRecord", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("TEXT").UseCollation("NOCASE");
            entity.Property<string>("Name").IsRequired().HasMaxLength(200).HasColumnType("TEXT");
            entity.Property<string>("Remark").IsRequired().HasMaxLength(500).HasColumnType("TEXT");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("INTEGER");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("Code").IsUnique();
            entity.ToTable("demo_records");
        });
    }
}
