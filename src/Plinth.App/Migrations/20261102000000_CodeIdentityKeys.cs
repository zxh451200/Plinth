using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Plinth.App.Data;

namespace Plinth.App.Migrations;

// 固定 SQLite DDL 和目标模型，独立于后续实体配置。
[DbContext(typeof(AppDbContext))]
[Migration("20261102000000_CodeIdentityKeys")]
public sealed class CodeIdentityKeys : Migration
{
    public const string MigrationId = "20261102000000_CodeIdentityKeys";

    // SQLite 内置 lower 只折叠 ASCII；不 trim、不改写历史 Code。
    internal const string UpgradeSql = """
        CREATE TEMP TABLE "__code_identity_encoding" (
            "Value" TEXT NOT NULL CHECK ("Value" = 'UTF-8')
        );
        INSERT INTO "__code_identity_encoding" SELECT encoding FROM pragma_encoding;
        DROP TABLE "__code_identity_encoding";

        CREATE TABLE "__code_identity_demo_records" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_demo_records" PRIMARY KEY,
            "Code" TEXT NOT NULL,
            "CodeKey" TEXT NOT NULL,
            "Name" TEXT NOT NULL,
            "Remark" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );
        INSERT INTO "__code_identity_demo_records"
            ("Id", "Code", "CodeKey", "Name", "Remark", "Version",
             "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy")
        SELECT "Id", "Code", hex(CAST(lower("Code") AS BLOB)), "Name", "Remark", "Version",
               "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy"
        FROM "demo_records";
        DROP TABLE "demo_records";
        ALTER TABLE "__code_identity_demo_records" RENAME TO "demo_records";
        CREATE UNIQUE INDEX "IX_demo_records_CodeKey" ON "demo_records" ("CodeKey");

        CREATE TABLE "__code_identity_notes_entries" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_notes_entries" PRIMARY KEY,
            "Code" TEXT NOT NULL,
            "CodeKey" TEXT NOT NULL,
            "Text" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );
        INSERT INTO "__code_identity_notes_entries"
            ("Id", "Code", "CodeKey", "Text", "Version",
             "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy")
        SELECT "Id", "Code", hex(CAST(lower("Code") AS BLOB)), "Text", "Version",
               "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy"
        FROM "notes_entries";
        DROP TABLE "notes_entries";
        ALTER TABLE "__code_identity_notes_entries" RENAME TO "notes_entries";
        CREATE UNIQUE INDEX "IX_notes_entries_CodeKey" ON "notes_entries" ("CodeKey");
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!InitialSchema.IsSqlite(migrationBuilder.ActiveProvider))
            throw new NotSupportedException("SQLite migrations require the SQLite provider.");
        migrationBuilder.Sql(UpgradeSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("CodeIdentityKeys 仅支持向前迁移，请恢复兼容版本的备份。");

    protected override void BuildTargetModel(ModelBuilder modelBuilder)
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
