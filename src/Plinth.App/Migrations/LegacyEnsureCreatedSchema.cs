namespace Plinth.App.Migrations;

/// <summary>已发布的无迁移历史结构及其单向补齐脚本；不读取当前 EF 模型。</summary>
internal static class LegacyEnsureCreatedSchema
{
    internal const string SixColumnSchemaVersion = "ensurecreated-six-columns-v1";

    internal const string SixColumnCreateSql = """
        CREATE TABLE "demo_records" (
            "Id" TEXT NOT NULL CONSTRAINT "PK_demo_records" PRIMARY KEY,
            "Code" TEXT COLLATE NOCASE NOT NULL,
            "Name" TEXT NOT NULL,
            "Remark" TEXT NOT NULL,
            "Version" INTEGER NOT NULL,
            "CreatedAtUtc" TEXT NOT NULL
        );
        CREATE UNIQUE INDEX "IX_demo_records_Code" ON "demo_records" ("Code");
        """;

    // 在候选副本的同一事务内重建表，避免 ADD COLUMN 的临时默认值进入固定基线。
    // 旧六列按原始 SQLite 值复制，时间不重新解释或转换，版本号不递增。
    internal const string UpgradeSixColumnSql = """
        CREATE TABLE "__plinth_demo_records_audit" (
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
        INSERT INTO "__plinth_demo_records_audit"
            ("Id", "Code", "Name", "Remark", "Version", "CreatedAtUtc", "CreatedBy", "UpdatedAtUtc", "UpdatedBy")
        SELECT "Id", "Code", "Name", "Remark", "Version", "CreatedAtUtc", 'unknown', "CreatedAtUtc", 'unknown'
        FROM "demo_records";
        DROP TABLE "demo_records";
        ALTER TABLE "__plinth_demo_records_audit" RENAME TO "demo_records";
        CREATE UNIQUE INDEX "IX_demo_records_Code" ON "demo_records" ("Code");
        """;
}
