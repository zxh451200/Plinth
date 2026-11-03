using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Migrations;

namespace Plinth.App.Data;

/// <summary>只向前迁移；既有库在副本上升级，成功后才替换原库。</summary>
public static class DatabaseInitializer
{
    public static void EnsureDatabase(this IServiceProvider services)
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        using var scope = services.CreateScope();
        using var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
        Initialize(db);
    }

    public static void Initialize(AppDbContext db)
    {
        var path = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        if (!db.Database.GetMigrations().Any())
            throw new InvalidOperationException("未找到 EF 迁移，拒绝使用 EnsureCreated 创建无版本数据库。");
        if (db.Database.HasPendingModelChanges())
            throw new InvalidOperationException("当前模型存在未生成迁移的变更，拒绝启动。");

        if (!File.Exists(path))
        {
            if (File.Exists(path + "-wal") || File.Exists(path + "-shm"))
                throw new IOException("数据库缺失但存在 SQLite 日志，请先恢复原数据。");
            db.Database.Migrate();
            ValidateIntegrity(path);
            return;
        }

        ValidateCompatible(path);
        using (var check = Open(path))
        {
            using var versionCheck = CreateContext(path);
            if (HasHistory(check) && !versionCheck.Database.GetPendingMigrations().Any())
                return;
        }

        var safety = path + ".before-migrate-" + Guid.NewGuid().ToString("N");
        var candidate = path + ".upgrade-" + Guid.NewGuid().ToString("N");
        CreateConsistentCopy(path, safety);
        try
        {
            File.Copy(safety, candidate);
            using (var upgraded = CreateContext(candidate))
            {
                using (var connection = Open(candidate, readOnly: false))
                {
                    if (!HasHistory(connection))
                    {
                        // 六列旧库只在候选副本上补齐，再为完整审计结构建立同一初始基线。
                        var legacy = ValidateLegacySchema(connection);
                        using var transaction = connection.BeginTransaction();
                        if (legacy == LegacySchema.SixColumns)
                        {
                            using var upgrade = connection.CreateCommand();
                            upgrade.Transaction = transaction;
                            upgrade.CommandText = LegacyEnsureCreatedSchema.UpgradeSixColumnSql;
                            upgrade.ExecuteNonQuery();
                        }
                        if (ValidateLegacySchema(connection, transaction) != LegacySchema.FullAudit)
                            throw new InvalidDataException("审计字段补齐后结构不符合固定初始版本。");
                        using var command = connection.CreateCommand();
                        command.Transaction = transaction;
                        command.CommandText = """
                            CREATE TABLE "__EFMigrationsHistory" (
                                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                                "ProductVersion" TEXT NOT NULL);
                            INSERT INTO "__EFMigrationsHistory" VALUES ($id, '10.0.14');
                            """;
                        command.Parameters.AddWithValue("$id", InitialSchema.MigrationId);
                        command.ExecuteNonQuery();
                        transaction.Commit();
                    }
                }
                upgraded.Database.Migrate();
            }
            ValidateIntegrity(candidate);
            Checkpoint(candidate);
            Checkpoint(path);
            File.Move(candidate, path, overwrite: true);
        }
        catch (Exception ex)
        {
            // 原库没有执行任何迁移；安全副本和诊断留在原目录供人工恢复。
            try { File.WriteAllText(safety + ".error", ex.ToString()); }
            catch (Exception writeError) when (writeError is IOException or UnauthorizedAccessException)
            {
                Serilog.Log.Error(writeError, "无法写入迁移错误文件；安全副本:{Path}", safety);
            }
            throw;
        }
        finally
        {
            File.Delete(candidate);
        }
    }

    internal static AppDbContext CreateContext(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);

    internal static SqliteConnection Open(string path, bool readOnly = true)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        try { connection.Open(); return connection; }
        catch { connection.Dispose(); throw; }
    }

    internal static void CreateConsistentCopy(string source, string destination)
    {
        using var input = Open(source);
        using var output = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = destination,
            Pooling = false,
        }.ToString());
        output.Open();
        input.BackupDatabase(output); // 包括已提交但仍在 WAL 中的数据。
    }

    internal static void Checkpoint(string path)
    {
        using var connection = Open(path, readOnly: false);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(0) != 0)
            throw new IOException("数据库仍被占用，无法安全 checkpoint，已取消替换。");
    }

    internal static void ValidateIntegrity(string path)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetString(0) != "ok" || reader.Read())
            throw new InvalidDataException("数据库完整性检查失败。");
    }

    internal static string ValidateCompatible(string path)
    {
        ValidateIntegrity(path);
        using var connection = Open(path);
        // 编码键迁移把 TEXT 转为 UTF-8 字节。外部创建的 UTF-16 库不能误按相同规则回填。
        using (var encoding = connection.CreateCommand())
        {
            encoding.CommandText = "PRAGMA encoding";
            if (!string.Equals(encoding.ExecuteScalar() as string, "UTF-8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("数据库不是应用支持的 UTF-8 编码，拒绝自动升级；请保留原库并迁移数据。");
        }
        using var db = CreateContext(path);
        var known = db.Database.GetMigrations().ToArray();
        if (known.Length == 0)
            throw new InvalidDataException("当前程序没有可用迁移，无法确定数据库升级路径。");
        if (!HasHistory(connection))
        {
            return ValidateLegacySchema(connection) == LegacySchema.SixColumns
                ? LegacyEnsureCreatedSchema.SixColumnSchemaVersion
                : InitialSchema.MigrationId;
        }
        var applied = db.Database.GetAppliedMigrations().ToArray();
        if (applied.Length == 0 || applied.Length > known.Length ||
            !applied.SequenceEqual(known.Take(applied.Length), StringComparer.Ordinal))
            throw new InvalidDataException("数据库版本较新或迁移路径未知，请使用兼容版本的备份。");
        // 初始版本也验证实际结构，不能只信任历史表。
        if (applied.Length == 1)
        {
            if (ValidateLegacySchema(connection) != LegacySchema.FullAudit)
                throw new InvalidDataException("数据库声称已建立迁移基线，但仍缺少完整审计字段。");
        }
        else if (applied[^1] == NotesEntries.MigrationId || applied[^1] == CodeIdentityKeys.MigrationId)
        {
            using var expected = new SqliteConnection("Data Source=:memory:");
            expected.Open();
            using var create = expected.CreateCommand();
            create.CommandText = InitialSchema.CreateSql + NotesEntries.CreateSql;
            if (applied[^1] == CodeIdentityKeys.MigrationId)
                create.CommandText += CodeIdentityKeys.UpgradeSql;
            create.ExecuteNonQuery();
            if (!Schema(connection).SequenceEqual(Schema(expected), StringComparer.Ordinal))
                throw new InvalidDataException("数据库结构与声明的迁移版本不一致。");
        }
        return applied[^1];
    }

    private static bool HasHistory(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM sqlite_master WHERE type='table' AND name='__EFMigrationsHistory'";
        return (long)command.ExecuteScalar()! != 0;
    }

    private enum LegacySchema { SixColumns, FullAudit }

    private static LegacySchema ValidateLegacySchema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        var actual = Schema(connection, transaction);
        foreach (var (kind, sql) in new[]
        {
            (LegacySchema.FullAudit, InitialSchema.CreateSql),
            (LegacySchema.SixColumns, LegacyEnsureCreatedSchema.SixColumnCreateSql),
        })
        {
            using var expected = new SqliteConnection("Data Source=:memory:");
            expected.Open();
            using var create = expected.CreateCommand();
            create.CommandText = sql;
            create.ExecuteNonQuery();
            if (actual.SequenceEqual(Schema(expected), StringComparer.Ordinal))
                return kind;
        }
        throw new InvalidDataException("数据库不符合已知六列或完整审计历史结构，拒绝自动建立迁移基线。");
    }

    private static string[] Schema(SqliteConnection connection, SqliteTransaction? transaction = null)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT sql FROM sqlite_master
            WHERE name NOT LIKE 'sqlite_%'
              AND name NOT IN ('__EFMigrationsHistory', '__EFMigrationsLock')
            ORDER BY type, name
            """;
        using var reader = command.ExecuteReader();
        var result = new System.Collections.Generic.List<string>();
        while (reader.Read())
        {
            var sql = Regex.Replace(reader.GetString(0), @"\s+", " ").Trim().TrimEnd(';');
            // EnsureCreated 与迁移的列顺序可以不同；当前固定基线没有复合约束/带逗号表达式。
            if (sql.StartsWith("CREATE TABLE ", StringComparison.Ordinal))
            {
                var start = sql.IndexOf('(');
                var end = sql.LastIndexOf(')');
                sql = sql[..start].Trim() + "(" + string.Join(",",
                    sql[(start + 1)..end].Split(',').Select(column => column.Trim())
                        .OrderBy(column => column, StringComparer.Ordinal)) + ")";
            }
            result.Add(sql);
        }
        return result.ToArray();
    }
}
