using System;
using Microsoft.EntityFrameworkCore.Migrations;
namespace Plinth.App.Migrations;
// 固定 SQLite 增列 DDL：只加列，旧行直接拿默认值（未删除）。
// [DbContext]/[Migration] 与目标模型在 DemoSoftDelete.Designer.cs 里。
public partial class DemoSoftDelete : Migration
{
    public const string MigrationId = "20261112000000_DemoSoftDelete";
    internal const string AddColumnsSql = """
        ALTER TABLE "demo_records" ADD COLUMN "DeletedAtUtc" TEXT NULL;
        ALTER TABLE "demo_records" ADD COLUMN "IsDeleted" INTEGER NOT NULL DEFAULT 0;
        """;
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!InitialSchema.IsSqlite(migrationBuilder.ActiveProvider))
            throw new NotSupportedException("SQLite migrations require the SQLite provider.");
        migrationBuilder.Sql(AddColumnsSql);
    }
    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("DemoSoftDelete 仅支持向前迁移，请恢复兼容版本的备份。");
}
