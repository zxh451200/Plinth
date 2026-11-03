using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Plinth.App.Data;
using Serilog;

namespace Plinth.App.Services;

/// <summary>
/// SQLite 一致性副本 + 设置（包括 Window 布局）的 ZIP 备份。
/// 恢复只在启动打开数据库前执行：校验、升级候选、安全副本、带回滚日志的替换。
/// </summary>
public sealed class BackupService(IDbContextFactory<AppDbContext> dbFactory, SensitiveOperationService? operations = null)
{
    private readonly SensitiveOperationService _operations = operations ??
        SensitiveOperationService.CreateDefault(Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
    private const string DatabaseEntry = "database.db";
    private const string ManifestEntry = "manifest.json";
    private static readonly string[] PreferenceFiles = ["settings.json", "workspace-preferences.json"];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly object RestoreLock = new();

    public sealed record BackupFile(string Path, long Size, string Sha256);
    public sealed record BackupManifest(int FormatVersion, string ApplicationVersion, string SchemaVersion,
        DateTimeOffset CreatedUtc, BackupFile[] Files);
    public sealed record BackupEstimate(string Path, long EstimatedBytes);
    public sealed record BackupPreview(string Destination, BackupEstimate[] Files, string[] Excluded)
    {
        public long EstimatedBytes => Files.Sum(file => file.EstimatedBytes);
    }

    /// <summary>只读取源文件元数据；不打开 SQLite、不 checkpoint、不生成副本或临时文件。</summary>
    public async Task<BackupPreview> PreviewBackupAsync(string destination, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await _operations.ExecuteAsync("backup:export", "Backup", "backup.previewCreate", async token =>
        {
            await using var db = await dbFactory.CreateDbContextAsync(token);
            var source = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
            destination = ValidateDestination(destination, source);
            var files = new List<BackupEstimate>();
            token.ThrowIfCancellationRequested();
            files.Add(new(DatabaseEntry, new FileInfo(source).Length));
            foreach (var name in PreferenceFiles)
            {
                token.ThrowIfCancellationRequested();
                var file = new FileInfo(Path.Combine(Path.GetDirectoryName(source)!, name));
                if (!file.Exists) continue;
                if (file.Length > 4 * 1024 * 1024) throw new InvalidDataException("设置文件过大。");
                files.Add(new(name, file.Length));
            }
            token.ThrowIfCancellationRequested();
            return new BackupPreview(destination, files.ToArray(),
                ["logs", "credentials/tokens", "SQLite WAL/SHM/journal files", "pending restores", "other files"]);
        }, cancellationToken);
    }

    private static string ValidateDestination(string destination, string source)
    {
        destination = Path.GetFullPath(destination);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (new[] { source, source + "-wal", source + "-shm", source + "-journal", PendingPath(source) }
            .Concat(PreferenceFiles.Select(name => Path.Combine(Path.GetDirectoryName(source)!, name)))
            .Any(path => string.Equals(destination, path, comparison)))
            throw new IOException("备份目标不能覆盖当前数据库或设置。");
        if (File.Exists(destination) || Directory.Exists(destination))
            throw new IOException("备份目标已存在。");
        return destination;
    }

    /// <summary>返回实际文件、大小、校验状态；同时验证 schema 和数据库完整性。</summary>
    public static BackupManifest Inspect(string backupPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(backupPath))!,
            ".inspect-" + Guid.NewGuid().ToString("N"));
        using var source = File.OpenRead(backupPath);
        return InspectFromStream(source, directory, cancellationToken);
    }

    private static BackupManifest InspectFromStream(Stream source, string directory, CancellationToken cancellationToken)
    {
        try { return ExtractValidatedStream(source, directory, cancellationToken); }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    internal static (BackupManifest Manifest, byte[] Hash) PreviewRestore(
        string backupPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var source = File.OpenRead(backupPath);
        var directory = Path.Combine(AppPaths.DataDirectory, ".restore-preview-" + Guid.NewGuid().ToString("N"));
        return PreviewRestoreFromStream(source, directory, cancellationToken);
    }

    // 哈希和清单都读取同一份私有快照，不能分别按原路径打开，避免预览与确认对象错配。
    internal static (BackupManifest Manifest, byte[] Hash) PreviewRestoreFromStream(
        Stream source, string directory, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        try
        {
            using var snapshot = new FileStream(Path.Combine(directory, "backup.zip"),
                FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            CopyWithCancellation(source, snapshot, cancellationToken);
            snapshot.Position = 0;
            var hash = HashWithCancellation(snapshot, cancellationToken);
            snapshot.Position = 0;
            var manifest = InspectFromStream(snapshot, Path.Combine(directory, "contents"), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            return (manifest, hash);
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    public async Task BackupToAsync(string destination, CancellationToken cancellationToken = default,
        IProgress<double?>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await _operations.ExecuteConfirmedAsync("backup:export", "Backup", "backup.create", async token =>
        {
            await Task.Run(() => BackupCoreAsync(destination, token, progress), token);
            return true;
        }, cancellationToken);
    }

    internal SensitiveOperationService Operations => _operations;

    internal Task<SensitiveOperationService.Confirmation> ConfirmOperationAsync()
        => _operations.PrepareConfirmedAsync("backup:export", "Backup", "backup.create");

    // 调用者在 UI 线程确认；工作线程仍须重新授权，不能凭预览授予导出权限。
    internal async Task BackupAfterConfirmationAsync(string destination,
        SensitiveOperationService.Confirmation confirmation, CancellationToken cancellationToken,
        IProgress<double?>? progress = null)
    {
        await confirmation.ExecuteAsync(async token =>
        {
            await Task.Run(() => BackupCoreAsync(destination, token, progress), token);
            return true;
        }, cancellationToken);
    }

    private async Task BackupCoreAsync(string destination, CancellationToken cancellationToken, IProgress<double?>? progress)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var source = Path.GetFullPath(db.Database.GetDbConnection().DataSource);
        destination = ValidateDestination(destination, source);
        cancellationToken.ThrowIfCancellationRequested();
        var work = destination + ".work-" + Guid.NewGuid().ToString("N");
        var temp = destination + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(work);
        try
        {
            var sourceDirectory = Path.GetDirectoryName(source)!;

            cancellationToken.ThrowIfCancellationRequested();
            progress?.Report(null); // SQLite 快照和完整性检查没有可用的总工作量。
            var copy = Path.Combine(work, DatabaseEntry);
            DatabaseInitializer.CreateConsistentCopy(source, copy);
            var schema = DatabaseInitializer.ValidateCompatible(copy);
            var names = new List<string> { DatabaseEntry };
            foreach (var name in PreferenceFiles)
            {
                var preference = Path.Combine(sourceDirectory, name);
                if (!File.Exists(preference)) continue;
                if (new FileInfo(preference).Length > 4 * 1024 * 1024)
                    throw new InvalidDataException("设置文件过大。");
                // 使用明确的设置契约，未知字段（例如手工写入的 token）不会进入备份。
                var content = NormalizePreference(name, await File.ReadAllTextAsync(preference, cancellationToken));
                await File.WriteAllTextAsync(Path.Combine(work, name), content, cancellationToken);
                names.Add(name);
            }
            var files = names.Select(name => Describe(work, name, cancellationToken)).ToArray();
            var manifest = new BackupManifest(1, AppInfo.Version, schema, DateTimeOffset.UtcNow, files);
            await File.WriteAllTextAsync(Path.Combine(work, ManifestEntry),
                JsonSerializer.Serialize(manifest, JsonOptions), cancellationToken);
            await WriteArchiveAsync(work, temp, names.Append(ManifestEntry).ToArray(), cancellationToken, progress);
            progress?.Report(null); // 压缩数据已写完，最后发布单独处理。
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, destination, overwrite: false);
        }
        finally
        {
            File.Delete(temp);
            Directory.Delete(work, recursive: true);
        }
    }

    private static async Task WriteArchiveAsync(string directory, string destination, string[] names,
        CancellationToken token, IProgress<double?>? progress)
    {
        var total = names.Sum(name => new FileInfo(Path.Combine(directory, name)).Length);
        long copied = 0;
        var buffer = new byte[81920];
        await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None,
            bufferSize: 81920, useAsync: true);
        using var archive = new ZipArchive(target, ZipArchiveMode.Create, leaveOpen: true);
        progress?.Report(total == 0 ? null : 0);
        foreach (var name in names)
        {
            token.ThrowIfCancellationRequested();
            await using var source = File.OpenRead(Path.Combine(directory, name));
            await using var entry = archive.CreateEntry(name, CompressionLevel.Optimal).Open();
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), token)) > 0)
            {
                await entry.WriteAsync(buffer.AsMemory(0, count), token);
                copied += count;
                progress?.Report(total == 0 ? null : (double)copied / total);
            }
        }
        token.ThrowIfCancellationRequested();
    }

    /// <summary>复制后校验实际待恢复内容；无 manifest 的旧裸 SQLite 文件不能确定来源，明确拒绝。</summary>
    public static string? StageRestore(string backupPath, string? databasePath = null, byte[]? expectedHash = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        databasePath ??= AppPaths.DatabasePath;
        lock (RestoreLock)
        {
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var source = File.OpenRead(backupPath);
                StageRestoreFromStream(source, databasePath, expectedHash, cancellationToken);
                return null;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (IsRestoreError(ex))
            {
                Log.Error(ex, "准备恢复备份失败:{Path}", backupPath);
                return "无法准备恢复：" + ex.Message;
            }
        }
    }

    private static void StageRestoreFromStream(Stream source, string databasePath, byte[]? expectedHash,
        CancellationToken cancellationToken)
    {
        var temp = PendingPath(databasePath) + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                CopyWithCancellation(source, output, cancellationToken);
            if (expectedHash is not null)
            {
                using var staged = File.OpenRead(temp);
                if (!CryptographicOperations.FixedTimeEquals(HashWithCancellation(staged, cancellationToken), expectedHash))
                    throw new InvalidDataException("Backup changed after preview.");
            }
            Inspect(temp, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // 提交边界：替换成功后不再检查取消，不能把已发布的恢复报告为取消。
            File.Move(temp, PendingPath(databasePath), overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    /// <summary>
    /// 普通失败恢复原数据；若回滚也失败则抛异常阻止启动，并保留安全副本和日志。
    /// 启动中断留下的事务下次先回滚；本方法只能在应用尚未打开数据库和设置前调用。
    /// </summary>
    public static bool ApplyPendingRestore(string? databasePath = null)
    {
        databasePath = Path.GetFullPath(databasePath ?? AppPaths.DatabasePath);
        lock (RestoreLock)
        {
            var journal = databasePath + ".restore-transaction";
            RecoverInterrupted(databasePath, journal);
            var pending = PendingPath(databasePath);
            if (!File.Exists(pending)) return false;
            var candidate = databasePath + ".restore-candidate-" + Guid.NewGuid().ToString("N");
            var ready = false;
            try
            {
                ExtractValidated(pending, candidate);
                var newDatabase = Path.Combine(candidate, DatabaseEntry);
                using (var db = DatabaseInitializer.CreateContext(newDatabase))
                    DatabaseInitializer.Initialize(db);
                DatabaseInitializer.ValidateIntegrity(newDatabase);
                DatabaseInitializer.Checkpoint(newDatabase);

                var parent = Path.GetDirectoryName(databasePath)!;
                if (PreferenceFiles.Any(name => Directory.Exists(Path.Combine(parent, name))))
                    throw new IOException("设置目标被目录占用，已取消恢复。");
                Directory.CreateDirectory(journal);
                var originalFiles = new List<string>();
                if (File.Exists(databasePath))
                {
                    DatabaseInitializer.CreateConsistentCopy(databasePath, Path.Combine(journal, DatabaseEntry));
                    DatabaseInitializer.ValidateIntegrity(Path.Combine(journal, DatabaseEntry));
                    // 不删除 WAL：必须先成功 checkpoint，busy 时保持原库。
                    DatabaseInitializer.Checkpoint(databasePath);
                    originalFiles.Add(DatabaseEntry);
                }
                else if (File.Exists(databasePath + "-wal") || File.Exists(databasePath + "-shm"))
                    throw new IOException("存在孤立 SQLite 日志，拒绝覆盖，请先检查原数据。");

                foreach (var name in PreferenceFiles)
                {
                    var original = Path.Combine(parent, name);
                    if (!File.Exists(original)) continue;
                    File.Copy(original, Path.Combine(journal, name));
                    originalFiles.Add(name);
                }
                // ready 写入成功之前绝不替换任何文件。
                WriteMarker(Path.Combine(journal, "managed.json"), JsonSerializer.Serialize(PreferenceFiles));
                WriteMarker(Path.Combine(journal, "ready.json"), JsonSerializer.Serialize(originalFiles));
                ready = true;
                foreach (var name in PreferenceFiles)
                {
                    var restored = Path.Combine(candidate, name);
                    var target = Path.Combine(parent, name);
                    if (File.Exists(restored)) AtomicCopy(restored, target);
                    else File.Delete(target);
                }
                File.Move(newDatabase, databasePath, overwrite: true);
                WriteMarker(Path.Combine(journal, "committed"), "ok");
            }
            catch (Exception ex) when (IsRestoreError(ex))
            {
                Log.Error(ex, "恢复失败，保留并恢复原数据:{Path}", databasePath);
                if (ready) Rollback(databasePath, journal); // 失败则向上传播，不能带着混合数据启动。
                ArchiveJournal(databasePath, journal);
                return false;
            }
            finally
            {
                if (Directory.Exists(candidate)) Directory.Delete(candidate, recursive: true);
            }
            // 提交后清理失败不能触发回滚；下次启动识别 committed。
            try
            {
                File.Delete(pending);
                ArchiveJournal(databasePath, journal);
            }
            catch (Exception ex) when (IsRestoreError(ex))
            {
                Log.Warning(ex, "恢复成功，清理恢复事务失败");
            }
            Log.Information("已恢复数据库和设置，原数据安全副本保留:{Path}", databasePath);
            return true;
        }
    }

    public static bool HasPendingRestore(string? databasePath = null) =>
        File.Exists(PendingPath(databasePath ?? AppPaths.DatabasePath));

    private static string PendingPath(string databasePath) => databasePath + ".restore";

    private static BackupManifest ExtractValidated(string path, string directory,
        CancellationToken cancellationToken = default)
    {
        using var source = File.OpenRead(path);
        return ExtractValidatedStream(source, directory, cancellationToken);
    }

    private static BackupManifest ExtractValidatedStream(Stream source, string directory,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(directory);
        using var zip = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException("备份缺少 manifest；不支持直接恢复裸数据库。");
        if (entry.Length > 1024 * 1024) throw new InvalidDataException("备份 manifest 过大。");
        using var stream = entry.Open();
        var manifest = JsonSerializer.Deserialize<BackupManifest>(stream)
            ?? throw new InvalidDataException("备份 manifest 为空。");
        cancellationToken.ThrowIfCancellationRequested();
        if (manifest.FormatVersion != 1 || string.IsNullOrWhiteSpace(manifest.ApplicationVersion) ||
            string.IsNullOrWhiteSpace(manifest.SchemaVersion) || manifest.CreatedUtc == default ||
            manifest.CreatedUtc.Offset != TimeSpan.Zero || manifest.Files is null)
            throw new InvalidDataException("备份格式、版本或 UTC 时间无效。");
        var allowed = PreferenceFiles.Append(DatabaseEntry).ToArray();
        if (manifest.Files.Any(file => file is null || !allowed.Contains(file.Path, StringComparer.Ordinal) ||
            file.Size < 0 || file.Size > 16L * 1024 * 1024 * 1024 || file.Sha256 is null || file.Sha256.Length != 64) ||
            manifest.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() != manifest.Files.Length ||
            manifest.Files.Count(file => file.Path == DatabaseEntry) != 1 ||
            zip.Entries.Count != manifest.Files.Length + 1 ||
            zip.Entries.Select(item => item.FullName).Distinct(StringComparer.Ordinal).Count() != zip.Entries.Count)
            throw new InvalidDataException("备份文件清单无效或包含未声明文件。");
        foreach (var file in manifest.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var payload = zip.GetEntry(file.Path) ?? throw new InvalidDataException("备份缺少文件：" + file.Path);
            if (payload.Length != file.Size) throw new InvalidDataException("备份文件大小不符：" + file.Path);
            if (PreferenceFiles.Contains(file.Path, StringComparer.Ordinal) && file.Size > 4 * 1024 * 1024)
                throw new InvalidDataException("备份设置文件过大。");
            var target = Path.Combine(directory, file.Path); // 仅允许上面的固定名称，不能路径穿越。
            using (var input = payload.Open())
            using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[81920];
                long copied = 0;
                int count;
                while (true)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    count = input.Read(buffer, 0, buffer.Length);
                    cancellationToken.ThrowIfCancellationRequested();
                    if (count == 0) break;
                    copied += count;
                    if (copied > file.Size)
                        throw new InvalidDataException("解压数据超过声明大小：" + file.Path);
                    output.Write(buffer, 0, count);
                }
                if (copied != file.Size)
                    throw new InvalidDataException("解压数据大小不符：" + file.Path);
            }
            if (!string.Equals(Describe(directory, file.Path, cancellationToken).Sha256, file.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("备份 SHA-256 校验失败：" + file.Path);
            if (PreferenceFiles.Contains(file.Path, StringComparer.Ordinal))
            {
                var json = File.ReadAllText(target);
                cancellationToken.ThrowIfCancellationRequested();
                var normalized = NormalizePreference(file.Path, json);
                cancellationToken.ThrowIfCancellationRequested();
                File.WriteAllText(target, normalized);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var schema = DatabaseInitializer.ValidateCompatible(Path.Combine(directory, DatabaseEntry));
        cancellationToken.ThrowIfCancellationRequested();
        if (schema != manifest.SchemaVersion)
            throw new InvalidDataException("manifest schema 版本与数据库不一致。");
        return manifest;
    }

    private static BackupFile Describe(string directory, string name, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(directory, name);
        using var stream = File.OpenRead(path);
        return new BackupFile(name, stream.Length, Convert.ToHexString(HashWithCancellation(stream, cancellationToken)));
    }

    private static void CopyWithCancellation(Stream input, Stream output, CancellationToken cancellationToken)
    {
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) return;
            output.Write(buffer, 0, count);
        }
    }

    private static byte[] HashWithCancellation(Stream input, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = input.Read(buffer, 0, buffer.Length);
            cancellationToken.ThrowIfCancellationRequested();
            if (count == 0) return hash.GetHashAndReset();
            hash.AppendData(buffer, 0, count);
        }
    }

    private static string NormalizePreference(string name, string json)
    {
        if (name == "workspace-preferences.json") return WorkspacePreferencesService.NormalizeForBackup(json);
        if (name != "settings.json") throw new InvalidDataException("Unknown preference file.");
        var settings = JsonSerializer.Deserialize<AppSettings>(json)
            ?? throw new InvalidDataException("Empty settings.");
        SettingsService.Upgrade(settings);
        return SerializeSettings(settings);
    }

    private static string SerializeSettings(AppSettings settings) => JsonSerializer.Serialize(new
    {
        settings.SchemaVersion,
        settings.Theme,
        settings.Density,
        settings.Language,
        settings.Region,
        settings.TimeZoneId,
        settings.UpdateFeedUrl,
        settings.Window,
        settings.LogRetentionDays,
        settings.LogRetainedFileCount,
        Remote = settings.Remote is null ? null : new
        {
            settings.Remote.Enabled,
            settings.Remote.BaseUrl,
            settings.Remote.HealthPath,
        },
    }, JsonOptions);

    private static void AtomicCopy(string source, string target)
    {
        var temp = target + ".replace-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(source, temp);
            File.Move(temp, target, overwrite: true);
        }
        finally { File.Delete(temp); }
    }

    private static void WriteMarker(string path, string content)
    {
        var temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(content);
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, path);
    }

    private static void Rollback(string databasePath, string journal)
    {
        var originals = JsonSerializer.Deserialize<string[]>(File.ReadAllText(Path.Combine(journal, "ready.json")))
            ?? throw new InvalidDataException("恢复事务日志损坏，停止启动。");
        // Journals created by older versions did not manage workspace preferences.
        var managedPath = Path.Combine(journal, "managed.json");
        string[] managed = File.Exists(managedPath)
            ? JsonSerializer.Deserialize<string[]>(File.ReadAllText(managedPath))
                ?? throw new InvalidDataException("Invalid managed preference list.")
            : ["settings.json"];
        if (managed.Distinct(StringComparer.Ordinal).Count() != managed.Length ||
            managed.Any(name => !PreferenceFiles.Contains(name, StringComparer.Ordinal)))
            throw new InvalidDataException("Invalid managed preference list.");
        var known = managed.Prepend(DatabaseEntry).ToArray();
        if (originals.Any(name => !known.Contains(name, StringComparer.Ordinal)) ||
            originals.Distinct(StringComparer.Ordinal).Count() != originals.Length)
            throw new InvalidDataException("恢复事务文件清单损坏，停止启动。");
        // 回滚前先检查所有原文件；不能先替换数据库才发现设置副本已经丢失。
        foreach (var name in originals)
            if (!File.Exists(Path.Combine(journal, name)))
                throw new FileNotFoundException("恢复安全副本缺失，停止启动。", Path.Combine(journal, name));
        if (originals.Contains(DatabaseEntry))
            DatabaseInitializer.ValidateIntegrity(Path.Combine(journal, DatabaseEntry));
        foreach (var name in managed.Prepend(DatabaseEntry))
        {
            var target = name == DatabaseEntry ? databasePath : Path.Combine(Path.GetDirectoryName(databasePath)!, name);
            if (originals.Contains(name)) AtomicCopy(Path.Combine(journal, name), target);
            else File.Delete(target);
        }
    }

    private static void RecoverInterrupted(string databasePath, string journal)
    {
        if (!Directory.Exists(journal)) return;
        if (!File.Exists(Path.Combine(journal, "committed")) && File.Exists(Path.Combine(journal, "ready.json")))
            Rollback(databasePath, journal);
        if (File.Exists(Path.Combine(journal, "committed")))
            File.Delete(PendingPath(databasePath));
        ArchiveJournal(databasePath, journal);
    }

    private static void ArchiveJournal(string databasePath, string journal)
    {
        if (Directory.Exists(journal))
            Directory.Move(journal, databasePath + ".before-restore-" + DateTime.UtcNow.ToString("yyyyMMddTHHmmssZ") +
                "-" + Guid.NewGuid().ToString("N"));
    }

    private static bool IsRestoreError(Exception ex) =>
        ex is IOException or InvalidDataException or UnauthorizedAccessException or SqliteException or JsonException
            or InvalidOperationException or NotSupportedException;
}
