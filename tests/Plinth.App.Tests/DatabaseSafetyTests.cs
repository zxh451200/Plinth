using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Data;
using Plinth.App.Migrations;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DatabaseSafetyTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-safety-" + Guid.NewGuid().ToString("N"));
    public DatabaseSafetyTests() => Directory.CreateDirectory(_directory);
    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Fresh_database_uses_migrations_and_snapshot_matches_current_model()
    {
        using var db = Context(Path.Combine(_directory, "new.db"));
        DatabaseInitializer.Initialize(db);
        Assert.Equal(new[] { InitialSchema.MigrationId, NotesEntries.MigrationId, CodeIdentityKeys.MigrationId }, db.Database.GetAppliedMigrations());
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.False(db.Database.GetPendingMigrations().Any());
        DatabaseInitializer.Initialize(db);
        Assert.Empty(Directory.GetFiles(_directory, "*.before-migrate-*"));
    }

    [Fact]
    public void Code_key_upgrade_preserves_original_codes_and_matches_sqlite_backfill()
    {
        var path = LegacyDatabase("code-keys.db", "AbC");
        using (var original = Context(path))
        {
            foreach (var code in new[] { "Ä", "ä", "Σ", "σ", "é", "e\u0301", "😀", " A " })
                original.Database.ExecuteSqlInterpolated($"""
                    INSERT INTO demo_records (Id,Code,Name,Remark,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy)
                    VALUES ({Guid.NewGuid()}, {code}, 'name', '', 9, '2020-01-01', 'creator', '2021-01-01', 'editor')
                    """);
        }
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        var records = db.Set<DemoRecord>().AsNoTracking().ToArray();
        Assert.Equal(9, records.Length);
        Assert.All(records, record => Assert.Equal(CodeIdentity.Create(record.Code), record.CodeKey));
        Assert.Contains(records, record => record.Code == "AbC");
        Assert.Contains(records, record => record.Code == " A " && record.Version == 9 && record.CreatedBy == "creator");
        Assert.Single(Directory.GetFiles(_directory, "code-keys.db.before-migrate-*"));
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT sql FROM sqlite_master WHERE type='table' AND name='demo_records'";
        Assert.DoesNotContain("NOCASE", (string)command.ExecuteScalar()!);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public void EnsureCreated_database_is_baselined_without_losing_records()
    {
        var path = LegacyDatabase("legacy.db", "KEEP");
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        Assert.Equal(new[] { InitialSchema.MigrationId, NotesEntries.MigrationId, CodeIdentityKeys.MigrationId }, db.Database.GetAppliedMigrations());
        Assert.Equal("KEEP", db.Set<DemoRecord>().Single().Code);
        Assert.Single(Directory.GetFiles(_directory, "legacy.db.before-migrate-*"));
    }

    [Fact]
    public void Six_column_EnsureCreated_database_preserves_values_and_backfills_audit_fields()
    {
        var path = SixColumnDatabase("six.db");
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        var record = Assert.Single(db.Set<DemoRecord>().AsNoTracking().ToList());
        Assert.Equal(Guid.Parse("c8cb0890-cfc9-4e25-8bb8-9b62b0d74a81"), record.Id);
        Assert.Equal("OLD-1", record.Code);
        Assert.Equal("旧数据", record.Name);
        Assert.Equal("保留备注", record.Remark);
        Assert.Equal(7, record.Version);
        Assert.Equal(new DateTime(2024, 2, 29, 23, 59, 58).AddTicks(1234567), record.CreatedAtUtc);
        Assert.Equal(record.CreatedAtUtc, record.UpdatedAtUtc);
        Assert.Equal("unknown", record.CreatedBy);
        Assert.Equal("unknown", record.UpdatedBy);
        Assert.Equal(new[] { InitialSchema.MigrationId, NotesEntries.MigrationId, CodeIdentityKeys.MigrationId }, db.Database.GetAppliedMigrations());
        var safety = Assert.Single(Directory.GetFiles(_directory, "six.db.before-migrate-*"));
        using (var original = Context(safety))
        {
            original.Database.OpenConnection();
            using var command = original.Database.GetDbConnection().CreateCommand();
            command.CommandText = "SELECT count(*) FROM pragma_table_info('demo_records')";
            Assert.Equal(6L, command.ExecuteScalar());
            command.CommandText = "SELECT CreatedAtUtc FROM demo_records";
            Assert.Equal("2024-02-29 23:59:58.1234567", command.ExecuteScalar());
        }
        // 重启不能重复补齐或改写旧行的审计值。
        DatabaseInitializer.Initialize(db);
        Assert.Single(Directory.GetFiles(_directory, "six.db.before-migrate-*"));
        Assert.Throws<SqliteException>(() => Execute(path, """
            INSERT INTO demo_records
                (Id,Code,CodeKey,Name,Remark,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy)
            SELECT 'another-id','old-1',CodeKey,Name,Remark,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy
            FROM demo_records
            """));
    }

    [Theory]
    [InlineData("ALTER TABLE demo_records ADD COLUMN CreatedBy TEXT NOT NULL DEFAULT 'unknown'")]
    [InlineData("DROP INDEX IX_demo_records_Code")]
    [InlineData("CREATE TABLE unrelated (Id INTEGER PRIMARY KEY)")]
    public void Unknown_six_column_variants_are_rejected_without_writing_original(string change)
    {
        var path = SixColumnDatabase("unknown-six.db");
        Execute(path, change);
        var before = Hash(path);
        using var db = Context(path);
        Assert.Throws<InvalidDataException>(() => DatabaseInitializer.Initialize(db));
        Assert.Equal(before, Hash(path));
        Assert.Empty(Directory.GetFiles(_directory, "unknown-six.db.before-migrate-*"));
    }

    [Fact]
    public void External_utf16_database_is_rejected_before_migration_or_copy()
    {
        var path = Path.Combine(_directory, "utf16.db");
        using var db = Context(path);
        db.Database.ExecuteSqlRaw("PRAGMA encoding='UTF-16le';" + InitialSchema.CreateSql);
        var before = Hash(path);
        Assert.Throws<InvalidDataException>(() => DatabaseInitializer.Initialize(db));
        Assert.Equal(before, Hash(path));
        Assert.Empty(Directory.GetFiles(_directory, "utf16.db.before-migrate-*"));
    }

    [Fact]
    public void Six_column_database_with_false_migration_history_is_rejected()
    {
        var path = SixColumnDatabase("false-baseline.db");
        Execute(path, """
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" TEXT NOT NULL CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY,
                "ProductVersion" TEXT NOT NULL);
            INSERT INTO "__EFMigrationsHistory" VALUES ('20261101000000_InitialSchema', '10.0.14');
            """);
        var before = Hash(path);
        using var db = Context(path);
        Assert.Throws<InvalidDataException>(() => DatabaseInitializer.Initialize(db));
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public void Empty_six_column_database_is_upgraded_and_baselined()
    {
        var path = SixColumnDatabase("empty-six.db");
        Execute(path, "DELETE FROM demo_records");
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        Assert.Empty(db.Set<DemoRecord>().ToList());
        Assert.Equal(new[] { InitialSchema.MigrationId, NotesEntries.MigrationId, CodeIdentityKeys.MigrationId }, db.Database.GetAppliedMigrations());
    }

    [Fact]
    public void Full_audit_legacy_database_keeps_existing_audit_values()
    {
        var path = LegacyDatabase("full-audit.db", "KEEP");
        Execute(path, """
            UPDATE demo_records SET CreatedBy='original-creator', UpdatedBy='original-editor',
                CreatedAtUtc='2020-01-01 00:00:00', UpdatedAtUtc='2021-02-03 04:05:06', Version=12
            """);
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        var record = db.Set<DemoRecord>().Single();
        Assert.Equal("original-creator", record.CreatedBy);
        Assert.Equal("original-editor", record.UpdatedBy);
        Assert.Equal(new DateTime(2020, 1, 1), record.CreatedAtUtc);
        Assert.Equal(new DateTime(2021, 2, 3, 4, 5, 6), record.UpdatedAtUtc);
        Assert.Equal(12, record.Version);
    }

    [Fact]
    public void EF_generated_migration_SQL_builds_a_database_accepted_by_the_initializer()
    {
        var path = Path.Combine(_directory, "ef-script.db");
        using var db = Context(path);
        Assert.False(db.Database.HasPendingModelChanges());
        var sql = db.GetService<IMigrator>().GenerateScript("0", InitialSchema.MigrationId,
            MigrationsSqlGenerationOptions.NoTransactions);
        Assert.Contains("\"CreatedBy\"", sql);
        Assert.Contains("COLLATE NOCASE", sql);
        db.Database.ExecuteSqlRaw(sql);
        DatabaseInitializer.Initialize(db);
        Assert.Equal(new[] { InitialSchema.MigrationId, NotesEntries.MigrationId, CodeIdentityKeys.MigrationId }, db.Database.GetAppliedMigrations());
        Assert.Empty(db.Set<DemoRecord>().ToList());
    }

    [Fact]
    public void Unknown_legacy_schema_is_rejected_without_changing_original()
    {
        var path = LegacyDatabase("unknown.db", "KEEP");
        Execute(path, "ALTER TABLE demo_records ADD COLUMN unexpected TEXT");
        var before = Hash(path);
        using var db = Context(path);
        Assert.Throws<InvalidDataException>(() => DatabaseInitializer.Initialize(db));
        Assert.Equal(before, Hash(path));
        Assert.Empty(db.Database.GetAppliedMigrations());
    }

    [Fact]
    public void Newer_schema_is_rejected_without_changing_original()
    {
        var path = Path.Combine(_directory, "newer.db");
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        Execute(path, """INSERT INTO "__EFMigrationsHistory" VALUES ('20990101000000_Future', '99.0.0')""");
        var before = Hash(path);
        Assert.Throws<InvalidDataException>(() => DatabaseInitializer.Initialize(db));
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public void Notes_migration_history_cannot_hide_a_missing_table()
    {
        var path = Path.Combine(_directory, "missing-notes.db");
        using var db = Context(path);
        DatabaseInitializer.Initialize(db);
        Execute(path, """DROP TABLE "notes_entries";""");
        var before = Hash(path);
        Assert.Throws<InvalidDataException>(() => DatabaseInitializer.Initialize(db));
        Assert.Equal(before, Hash(path));
    }

    [Fact]
    public async Task Restore_keeps_original_database_and_settings_in_safety_directory()
    {
        var source = LegacyDatabase("source.db", "RESTORED");
        var settings = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settings, """{"SchemaVersion":1,"Theme":"Dark","Window":{"Width":910,"Height":710},"token":"secret"}""");
        using var provider = Provider(source);
        var backup = Path.Combine(_directory, "backup.zip");
        await Service(provider).BackupToAsync(backup);
        var manifest = BackupService.Inspect(backup);
        Assert.Equal(TimeSpan.Zero, manifest.CreatedUtc.Offset);
        Assert.Equal(2, manifest.Files.Length);
        Assert.All(manifest.Files, file => Assert.Equal(64, file.Sha256.Length));
        var target = LegacyDatabase("target.db", "ORIGINAL");
        File.WriteAllText(settings, """{"SchemaVersion":1,"Theme":"Light"}""");
        Assert.Null(BackupService.StageRestore(backup, target));
        Assert.True(BackupService.ApplyPendingRestore(target));
        using (var restored = Context(target))
        {
            Assert.Equal("RESTORED", restored.Set<DemoRecord>().Single().Code);
            Assert.Equal(new[] { InitialSchema.MigrationId, NotesEntries.MigrationId, CodeIdentityKeys.MigrationId }, restored.Database.GetAppliedMigrations());
        }
        Assert.Contains("\"Width\": 910", File.ReadAllText(settings));
        Assert.DoesNotContain("secret", File.ReadAllText(settings));
        var safety = Assert.Single(Directory.GetDirectories(_directory, "target.db.before-restore-*"));
        using var old = Context(Path.Combine(safety, "database.db"));
        Assert.Equal("ORIGINAL", ReadSingleCode(old));
        Assert.Contains("Light", File.ReadAllText(Path.Combine(safety, "settings.json")));
    }

    [Fact]
    public async Task Corrupt_payload_does_not_replace_existing_pending_backup()
    {
        var source = LegacyDatabase("source.db", "KEEP");
        using var provider = Provider(source);
        var backup = Path.Combine(_directory, "backup.zip");
        var target = Path.Combine(_directory, "target.db");
        await Service(provider).BackupToAsync(backup);
        Assert.Null(BackupService.StageRestore(backup, target));
        var originalPending = Hash(target + ".restore");
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            zip.GetEntry("database.db")!.Delete();
            using var stream = new StreamWriter(zip.CreateEntry("database.db").Open());
            stream.Write("corrupt");
        }
        Assert.NotNull(BackupService.StageRestore(backup, target));
        Assert.Equal(originalPending, Hash(target + ".restore"));
    }

    [Fact]
    public void Invalid_pending_restore_does_not_move_original()
    {
        var target = LegacyDatabase("target.db", "ORIGINAL");
        File.WriteAllText(target + ".restore", "tampered");
        var before = Hash(target);
        Assert.False(BackupService.ApplyPendingRestore(target));
        Assert.Equal(before, Hash(target));
    }

    [Fact]
    public void Interrupted_restore_rolls_back_before_startup()
    {
        var target = LegacyDatabase("target.db", "ORIGINAL");
        var settings = Path.Combine(_directory, "settings.json");
        File.WriteAllText(settings, "original settings");
        var journal = target + ".restore-transaction";
        Directory.CreateDirectory(journal);
        File.Copy(target, Path.Combine(journal, "database.db"));
        File.Copy(settings, Path.Combine(journal, "settings.json"));
        File.WriteAllText(Path.Combine(journal, "ready.json"), """["database.db","settings.json"]""");
        File.WriteAllText(settings, "partially restored settings");
        Execute(target, "DELETE FROM demo_records");
        Assert.False(BackupService.ApplyPendingRestore(target));
        using var restored = Context(target);
        Assert.Equal("ORIGINAL", ReadSingleCode(restored));
        Assert.Equal("original settings", File.ReadAllText(settings));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public async Task Cancellation_preserves_existing_destination()
    {
        var source = LegacyDatabase("source.db", "KEEP");
        using var provider = Provider(source);
        var destination = Path.Combine(_directory, "existing.zip");
        File.WriteAllText(destination, "keep");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Service(provider).BackupToAsync(destination, cancellation.Token));
        Assert.Equal("keep", File.ReadAllText(destination));
        Assert.Empty(Directory.GetDirectories(_directory, "*.work-*"));
    }

    [Fact]
    public async Task Backup_cannot_overwrite_source_database_or_WAL()
    {
        var target = LegacyDatabase("target.db", "ORIGINAL");
        using var provider = Provider(target);
        var before = Hash(target);
        await Assert.ThrowsAsync<IOException>(() => Service(provider).BackupToAsync(target));
        await Assert.ThrowsAsync<IOException>(() => Service(provider).BackupToAsync(target + "-wal"));
        Assert.Equal(before, Hash(target));
    }

    [Fact]
    public async Task Backup_captures_committed_WAL_rows_while_writer_is_open()
    {
        var path = LegacyDatabase("wal.db", "FIRST");
        using (var initialize = Context(path)) DatabaseInitializer.Initialize(initialize);
        using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode=WAL;";
            Assert.Equal("wal", (string)command.ExecuteScalar()!);
        }
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0;";
            command.ExecuteNonQuery();
        }
        // 写连接必须保持打开：本机 SQLite 在最后一个 EF 连接关闭时会 checkpoint 并删除 WAL。
        using var db = Context(path);
        db.Database.OpenConnection();
        db.Add(new DemoRecord { Code = "IN-WAL", Name = "已提交 WAL" });
        db.SaveChanges();
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        using var provider = Provider(path);
        var backup = Path.Combine(_directory, "backup.zip");
        await Service(provider).BackupToAsync(backup);
        using var zip = ZipFile.OpenRead(backup);
        var extracted = Path.Combine(_directory, "copy.db");
        zip.GetEntry("database.db")!.ExtractToFile(extracted);
        using var copy = Context(extracted);
        Assert.Equal(2, copy.Set<DemoRecord>().Count());
    }

    [Fact]
    public async Task Settings_target_failure_preserves_original_database()
    {
        var target = LegacyDatabase("target.db", "ORIGINAL");
        using var provider = Provider(target);
        var backup = Path.Combine(_directory, "backup.zip");
        await Service(provider).BackupToAsync(backup);
        Assert.Null(BackupService.StageRestore(backup, target));
        Directory.CreateDirectory(Path.Combine(_directory, "settings.json"));
        var before = Hash(target);
        Assert.False(BackupService.ApplyPendingRestore(target));
        Assert.Equal(before, Hash(target));
    }

    [Fact]
    public async Task Undeclared_path_traversal_entry_is_rejected()
    {
        var source = LegacyDatabase("source.db", "KEEP");
        using var provider = Provider(source);
        var backup = Path.Combine(_directory, "backup.zip");
        await Service(provider).BackupToAsync(backup);
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
            zip.CreateEntry("../outside");
        Assert.NotNull(BackupService.StageRestore(backup, Path.Combine(_directory, "target.db")));
        Assert.False(File.Exists(Path.Combine(_directory, "outside")));
    }

    [Theory]
    [InlineData("SchemaVersion", "20990101000000_Future")]
    [InlineData("CreatedUtc", "2026-11-01T12:00:00+08:00")]
    public async Task Schema_mismatch_and_non_UTC_manifest_are_rejected(string property, string value)
    {
        var source = LegacyDatabase("source.db", "KEEP");
        using var provider = Provider(source);
        var backup = Path.Combine(_directory, "backup.zip");
        await Service(provider).BackupToAsync(backup);
        using (var zip = ZipFile.Open(backup, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            JsonObject manifest;
            using (var reader = new StreamReader(entry.Open()))
                manifest = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
            manifest[property] = value;
            entry.Delete();
            using var writer = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            writer.Write(manifest.ToJsonString());
        }
        Assert.NotNull(BackupService.StageRestore(backup, Path.Combine(_directory, "target.db")));
    }

    [Fact]
    public void Missing_rollback_copy_stops_startup_and_preserves_transaction_for_recovery()
    {
        var target = LegacyDatabase("target.db", "ORIGINAL");
        var journal = target + ".restore-transaction";
        Directory.CreateDirectory(journal);
        File.WriteAllText(Path.Combine(journal, "ready.json"), """["database.db"]""");
        var before = Hash(target);
        Assert.Throws<FileNotFoundException>(() => BackupService.ApplyPendingRestore(target));
        Assert.Equal(before, Hash(target));
        Assert.True(Directory.Exists(journal));
    }

    // 历史夹具固定为旧六列 DDL，不能用当前模型的 EnsureCreated 模拟旧版本。
    private string SixColumnDatabase(string name)
    {
        var path = Path.Combine(_directory, name);
        Execute(path, """
            CREATE TABLE "demo_records" (
                "Id" TEXT NOT NULL CONSTRAINT "PK_demo_records" PRIMARY KEY,
                "Code" TEXT COLLATE NOCASE NOT NULL,
                "Name" TEXT NOT NULL,
                "Remark" TEXT NOT NULL,
                "Version" INTEGER NOT NULL,
                "CreatedAtUtc" TEXT NOT NULL
            );
            CREATE UNIQUE INDEX "IX_demo_records_Code" ON "demo_records" ("Code");
            INSERT INTO demo_records (Id,Code,Name,Remark,Version,CreatedAtUtc)
            VALUES ('c8cb0890-cfc9-4e25-8bb8-9b62b0d74a81','OLD-1','旧数据','保留备注',7,'2024-02-29 23:59:58.1234567');
            """);
        return path;
    }

    private string LegacyDatabase(string name, string code)
    {
        var path = Path.Combine(_directory, name);
        using var db = Context(path);
        // Freeze the historical full-audit shape instead of using today's model.
        db.Database.ExecuteSqlRaw("""
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
            """);
        // 历史夹具不能通过含 CodeKey 的当前实体写入旧表。
        db.Database.ExecuteSqlInterpolated($"""
            INSERT INTO demo_records (Id,Code,Name,Remark,Version,CreatedAtUtc,CreatedBy,UpdatedAtUtc,UpdatedBy)
            VALUES ({Guid.NewGuid()}, {code}, {code}, '', 0, '2026-11-01 00:00:00', '', '2026-11-01 00:00:00', '')
            """);
        return path;
    }
    private static string ReadSingleCode(AppDbContext db)
    {
        db.Database.OpenConnection();
        using var command = db.Database.GetDbConnection().CreateCommand();
        command.CommandText = "SELECT Code FROM demo_records";
        return (string)command.ExecuteScalar()!;
    }
    private static AppDbContext Context(string path) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()).Options);
    private static ServiceProvider Provider(string path)
    {
        var services = new ServiceCollection();
        services.AddDbContextFactory<AppDbContext>(options => options.UseSqlite(
            new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()));
        return services.BuildServiceProvider();
    }
    private static BackupService Service(ServiceProvider provider) =>
        new(provider.GetRequiredService<IDbContextFactory<AppDbContext>>());
    private static void Execute(string path, string sql)
    {
        using var db = Context(path);
        db.Database.ExecuteSqlRaw(sql);
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
