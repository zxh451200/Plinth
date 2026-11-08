using System.IO;
using System.Linq;
using System.Security;
using System.Threading;
using System.Threading.Tasks;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Plinth.App.Data;
using Plinth.App.Localization;
using Plinth.App.Services;

namespace Plinth.App;

/// <summary>启动前命令；版本查询不读用户配置，升级备份及校验显式读取部署和数据配置。</summary>
public static class CommandLine
{
    public static int? Handle(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0) return null;
        try
        {
            if (args.Length == 1 && args[0] == "--version")
            {
                output.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
                return 0;
            }

            var inspect = args.Length == 3 && args[0] == "--inspect-backup-before-upgrade";
            if ((args.Length == 2 && args[0] == "--backup-before-upgrade") || inspect)
            {
                if (!IsUnambiguousLocalPath(args[1]) ||
                    string.IsNullOrWhiteSpace(Path.GetExtension(args[1])) ||
                    !string.Equals(Path.GetExtension(args[1]), ".zip", System.StringComparison.OrdinalIgnoreCase))
                {
                    error.WriteLine(Loc.Get("cli.invalidArguments"));
                    return 2;
                }

                var dataDirectory = Path.GetFullPath(AppPaths.DataDirectory)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                    Path.DirectorySeparatorChar;
                var destination = Path.GetFullPath(args[1]);
                if (destination.StartsWith(dataDirectory,
                        OperatingSystem.IsWindows() ? System.StringComparison.OrdinalIgnoreCase : System.StringComparison.Ordinal))
                {
                    error.WriteLine("升级前备份必须保存到数据目录之外。");
                    return 2;
                }

                if (DatabaseConfiguration.Current.IsServer)
                {
                    error.WriteLine("升级前备份流程仅支持本地 SQLite 数据库。");
                    return 8;
                }

                RejectLinkedPath(destination);
                if (inspect)
                {
                    // Only inspect an existing archive. Never open, migrate or export the live database.
                    // The report is a new file in the helper directory, not an arbitrary overwrite target.
                    if (!IsUnambiguousLocalPath(args[2]))
                    {
                        error.WriteLine(Loc.Get("cli.invalidArguments"));
                        return 2;
                    }
                    var report = Path.GetFullPath(args[2]);
                    if (!string.Equals(Path.GetDirectoryName(report),
                            Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory),
                            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                        return 2;
                    RejectLinkedPath(report);
                    var operations = SensitiveOperationService.CreateDefault(
                        Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);
                    var manifest = operations.ExecuteConfirmedAsync("backup:export", "Backup", "backup.inspectUpgrade",
                        _ => Task.FromResult(BackupService.Inspect(destination))).GetAwaiter().GetResult();
                    // Do not render arbitrary manifest text as installer instructions or control characters.
                    static string Display(string value) =>
                        new(value.Take(120).Select(c => c is >= ' ' and <= '~' ? c : '?').ToArray());
                    using var stream = new FileStream(report, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    using var writer = new StreamWriter(stream, Encoding.Unicode);
                    writer.WriteLine($"Application: {Display(manifest.ApplicationVersion)}");
                    writer.WriteLine($"Schema: {Display(manifest.SchemaVersion)}");
                    writer.WriteLine($"UTC: {manifest.CreatedUtc:O}; Files: {manifest.Files.Length}; Bytes: {manifest.Files.Sum(f => f.Size)}");
                    return 0;
                }
                RejectLinkedPath(AppPaths.DatabasePath);
                RejectLinkedPath(AppPaths.DatabasePath + "-wal");
                RejectLinkedPath(AppPaths.DatabasePath + "-shm");
                RejectLinkedPath(Path.Combine(AppPaths.DataDirectory, "settings.json"));
                RejectLinkedPath(Path.Combine(AppPaths.DataDirectory, "workspace-preferences.json"));
                var applicationDirectory = Path.GetFullPath(AppContext.BaseDirectory);
                if (destination.StartsWith(applicationDirectory,
                        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new IOException("Backup destination is inside application directory.");
                // 与普通启动使用同一数据目录互斥量；不通知或激活已有窗口。
                using var instance = new Mutex(true, $@"Local\Plinth.{InstanceActivator.InstanceId}", out var first);
                if (!first) return 8;
                try
                {
                    var service = new BackupService(new CommandLineDbContextFactory());
                    service.BackupToAsync(destination, CancellationToken.None)
                        .GetAwaiter().GetResult();
                    BackupService.Inspect(destination);
                }
                finally { instance.ReleaseMutex(); }
                return 0;
            }

            // 不回显原始参数，避免将凭据或控制字符写入终端。
            error.WriteLine(Loc.Get("cli.invalidArguments"));
            return 2;
        }
        catch (IOException)
        {
            return args.Length > 0 && args[0] is "--backup-before-upgrade" or "--inspect-backup-before-upgrade" ? 8 : 1;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException
            or InvalidOperationException or NotSupportedException or Microsoft.Data.Sqlite.SqliteException
            or OperationCanceledException or System.Text.Json.JsonException or ArgumentException
            or System.TypeInitializationException or SecurityException)
        {
            // 不回显路径、连接串或底层异常；安装器只需要一个不可绕过的失败码。
            try { error.WriteLine("升级前备份创建或校验失败。"); }
            catch (IOException) { }
            return 8;
        }
    }

    private static bool IsUnambiguousLocalPath(string path) =>
        Path.IsPathFullyQualified(path) && !path.Any(char.IsControl) &&
        (!OperatingSystem.IsWindows() ||
            (path.Length >= 3 && char.IsAsciiLetter(path[0]) && path[1] == ':' &&
                !path.Skip(2).Contains(':') &&
                !path.Split('\\', '/').Any(part => part.EndsWith('.') || part.EndsWith(' '))));

    private static void RejectLinkedPath(string path)
    {
        for (string? current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Linked paths are not supported for upgrade backup.");
        }
    }

    private sealed class CommandLineDbContextFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => DatabaseInitializer.CreateContext(AppPaths.DatabasePath);

        public Task<AppDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CreateDbContext());
        }
    }
}
