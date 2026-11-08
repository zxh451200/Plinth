using System;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Plinth.App.Data;

namespace Plinth.App.Migrations;

// 固定 DDL 与历史目标模型；不冒充 SDK 生成或真实 EF 验证的产物。
[DbContext(typeof(AppDbContext))]
[Migration("20261101010000_NotesEntries")]
public sealed class NotesEntries : Migration
{
    public const string MigrationId = "20261101010000_NotesEntries";
    internal const string CreateSql = """
        CREATE TABLE "notes_entries" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_notes_entries" PRIMARY KEY,
            "Code" TEXT COLLATE NOCASE NOT NULL,
            "Text" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL,
            "CreatedBy" TEXT NOT NULL,
            "UpdatedAtUtc" TEXT NOT NULL,
            "UpdatedBy" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "IX_notes_entries_Code" ON "notes_entries" ("Code");
        """;
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!InitialSchema.IsSqlite(migrationBuilder.ActiveProvider))
            throw new NotSupportedException("SQLite migrations require the SQLite provider.");
        migrationBuilder.Sql(CreateSql);
    }
    protected override void Down(MigrationBuilder migrationBuilder)
    {
        if (!InitialSchema.IsSqlite(migrationBuilder.ActiveProvider))
            throw new NotSupportedException("服务器数据库仅支持向前迁移，请恢复兼容版本的备份。");
        migrationBuilder.DropTable("notes_entries");
    }
    protected override void BuildTargetModel(ModelBuilder modelBuilder)
    {
        InitialSchema.BuildInitialModel(modelBuilder);
        BuildNotesModel(modelBuilder);
    }

    internal static void BuildNotesModel(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity("Plinth.App.Modules.Notes.Data.Note", entity =>
        {
            entity.Property<Guid>("Id").ValueGeneratedOnAdd().HasColumnType("TEXT");
            entity.Property<string>("Code").IsRequired().HasMaxLength(50).HasColumnType("TEXT").UseCollation("NOCASE");
            entity.Property<string>("Text").IsRequired().HasMaxLength(2000).HasColumnType("TEXT");
            entity.Property<int>("Version").IsConcurrencyToken().HasColumnType("INTEGER");
            entity.Property<DateTime>("CreatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("CreatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.Property<DateTime>("UpdatedAtUtc").HasColumnType("TEXT");
            entity.Property<string>("UpdatedBy").IsRequired().HasMaxLength(100).HasColumnType("TEXT");
            entity.HasKey("Id");
            entity.HasIndex("Code").IsUnique();
            entity.ToTable("notes_entries");
        });
    }
}
