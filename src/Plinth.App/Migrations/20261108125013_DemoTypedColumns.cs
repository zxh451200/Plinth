using System;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Plinth.App.Migrations;

// 固定 SQLite 增列 DDL：只加列、不回填历史文本，旧行直接拿默认值。
// [DbContext]/[Migration] 与目标模型在生成的 DemoTypedColumns.Designer.cs 里。
public partial class DemoTypedColumns : Migration
{
    public const string MigrationId = "20261108125013_DemoTypedColumns";

    internal const string AddColumnsSql = """
        ALTER TABLE "demo_records" ADD COLUMN "Quantity" INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE "demo_records" ADD COLUMN "DueDate" TEXT NULL;
        ALTER TABLE "demo_records" ADD COLUMN "Priority" TEXT NOT NULL DEFAULT 'Normal';
        ALTER TABLE "demo_records" ADD COLUMN "Active" INTEGER NOT NULL DEFAULT 1;
        """;

    protected override void Up(MigrationBuilder migrationBuilder)
    {
        if (!InitialSchema.IsSqlite(migrationBuilder.ActiveProvider))
            throw new NotSupportedException("SQLite migrations require the SQLite provider.");
        migrationBuilder.Sql(AddColumnsSql);
    }

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("DemoTypedColumns 仅支持向前迁移，请恢复兼容版本的备份。");
}
