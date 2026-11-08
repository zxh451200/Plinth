using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Localization;
using Plinth.App.Data;
using Plinth.App.Services;
using Microsoft.Data.Sqlite;
using System.IO.Compression;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_continue_to_the_desktop_without_output()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Null(CommandLine.Handle([], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Version_uses_the_same_assembly_version_as_the_desktop()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandLine.Handle(["--version"], output, error));
        Assert.Equal($"{AppInfo.ProductName} {AppInfo.Version}{output.NewLine}", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--Version")]
    [InlineData("--version=1")]
    [InlineData("")]
    [InlineData("token=private\n\u001b[31m")]
    public void Unknown_arguments_use_stderr_without_echoing_input(string argument)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Handle([argument], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal(Loc.Get("cli.invalidArguments") + error.NewLine, error.ToString());
    }

    [Fact]
    public void Version_does_not_ignore_extra_or_duplicate_arguments()
    {
        foreach (var args in new[] { new[] { "--version", "--unknown" }, new[] { "--version", "--version" } })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, CommandLine.Handle(args, output, error));
            Assert.Equal("", output.ToString());
            Assert.NotEmpty(error.ToString());
        }
    }

    [Fact]
    public void Upgrade_backup_rejects_missing_relative_wrong_extension_and_extra_arguments()
    {
        foreach (var args in new[]
        {
            new[] { "--backup-before-upgrade" },
            new[] { "--backup-before-upgrade", "relative.zip" },
            new[] { "--backup-before-upgrade", Path.Combine(Path.GetTempPath(), "backup.db") },
            new[] { "--backup-before-upgrade", Path.Combine(Path.GetTempPath(), "backup.zip"), "--force" },
            new[] { "--inspect-backup-before-upgrade" },
            new[] { "--inspect-backup-before-upgrade", Path.Combine(Path.GetTempPath(), "backup.zip") },
            new[] { "--inspect-backup-before-upgrade", "relative.zip", "report.txt" },
            new[] { "--inspect-backup-before-upgrade", Path.Combine(Path.GetTempPath(), "backup.zip"), "report.txt", "--force" },
        })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, CommandLine.Handle(args, output, error));
            Assert.Equal("", output.ToString());
        }
    }

    [Theory]
    [InlineData(@"\\server\share\backup.zip")]
    [InlineData("//server/share/backup.zip")]
    [InlineData(@"C:\backup.zip:stream.zip")]
    [InlineData(@"C:\backup. \backup.zip")]
    public void Upgrade_backup_rejects_ambiguous_windows_destinations(string destination)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Handle(["--backup-before-upgrade", destination], output, error));
        Assert.Equal("", output.ToString());
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--unknown")]
    public void Closed_output_pipe_returns_failure_instead_of_throwing(string argument)
    {
        using var broken = new BrokenWriter();
        Assert.Equal(1, CommandLine.Handle([argument], broken, broken));
    }

    [Theory]
    [InlineData("--version", 0)]
    [InlineData("--unknown", 2)]
    public async Task Real_entry_point_exits_without_a_display_or_creating_a_data_directory(string argument, int expectedCode)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "plinth-cli-" + Guid.NewGuid().ToString("N"));
        var testAssembly = typeof(CommandLineTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 用测试宿主的依赖清单运行应用真实入口，不启动测试宿主自身。
        foreach (var value in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            typeof(AppInfo).Assembly.Location, argument,
        })
            start.ArgumentList.Add(value);
        start.Environment["PLINTH_DATA_DIR"] = dataDirectory;
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI test process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(expectedCode, process.ExitCode);
            if (expectedCode == 0)
            {
                Assert.Equal($"{AppInfo.ProductName} {AppInfo.Version}", (await output).Trim());
                Assert.Equal("", await error);
            }
            else
            {
                Assert.Equal("", await output);
                Assert.Contains("--version", await error);
            }
            Assert.False(Directory.Exists(dataDirectory));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    [Theory]
    [InlineData("success", 0)]
    [InlineData("existing", 8)]
    [InlineData("missing", 8)]
    [InlineData("invalid", 8)]
    [InlineData("production", 8)]
    [InlineData("authentication", 8)]
    [InlineData("inside", 2)]
    [InlineData("running", 8)]
    [InlineData("server", 8)]
    [InlineData("installation-lock", 0)]
    [InlineData("application-directory", 8)]
    [InlineData("linked-target", 8)]
    [InlineData("user-server", 8)]
    [InlineData("invalid-database-config", 8)]
    [InlineData("inspect-success", 0)]
    [InlineData("inspect-no-live", 0)]
    [InlineData("inspect-bad-report", 2)]
    [InlineData("inspect-missing", 8)]
    [InlineData("inspect-invalid", 8)]
    [InlineData("inspect-no-manifest", 8)]
    [InlineData("inspect-hash", 8)]
    [InlineData("inspect-schema", 8)]
    [InlineData("inspect-production", 8)]
    [InlineData("inspect-authentication", 8)]
    [InlineData("inspect-server", 8)]
    [InlineData("inspect-user-server", 8)]
    [InlineData("inspect-existing-report", 8)]
    [InlineData("inspect-invalid-database-config", 8)]
    [InlineData("inspect-directory-database-config", 8)]
    [InlineData("inspect-report-ads", 2)]
    [InlineData("inspect-report-control", 2)]
    [InlineData("inspect-summary-controls", 0)]
    public async Task Upgrade_backup_real_entry_point_preserves_sources_and_fails_closed(string scenario, int expected)
    {
        var inspect = scenario.StartsWith("inspect-", StringComparison.Ordinal);
        if (inspect) scenario = scenario["inspect-".Length..];
        var root = Path.Combine(Path.GetTempPath(), "plinth-upgrade-" + Guid.NewGuid().ToString("N"));
        var data = Path.Combine(root, "data");
        Directory.CreateDirectory(data);
        var source = Path.Combine(data, "plinth.db");
        var target = scenario == "inside" ? Path.Combine(data, "backup.zip")
            : scenario == "application-directory"
                ? Path.Combine(AppContext.BaseDirectory, "upgrade-" + Guid.NewGuid().ToString("N") + ".zip")
                : Path.Combine(root, "backup.zip");
        var installationLockPath = Path.Combine(AppContext.BaseDirectory, InstallationGuard.FileName);
        var createdInstallationLock = scenario == "installation-lock" && !File.Exists(installationLockPath);
        var report = Path.Combine(scenario == "bad-report" ? root : AppContext.BaseDirectory,
            "inspect-report-" + Guid.NewGuid().ToString("N") + ".txt");
        if (scenario == "report-ads")
        {
            if (!OperatingSystem.IsWindows()) { Directory.Delete(root, recursive: true); return; }
            report += ":stream.txt";
        }
        if (scenario == "report-control") report += "\n";
        try
        {
            if (scenario != "missing")
            {
                using var db = DatabaseInitializer.CreateContext(source);
                DatabaseInitializer.Initialize(db);
            }
            // Keep a WAL connection alive: the committed row is not checkpointed into the main file.
            using var wal = scenario == "missing" ? null : DatabaseInitializer.Open(source, readOnly: false);
            if (wal is not null)
            {
                using var write = wal.CreateCommand();
                // Use the model to avoid coupling this fixture to physical column names.
                write.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0;";
                write.ExecuteNonQuery();
                using var db = DatabaseInitializer.CreateContext(source);
                db.Add(new Plinth.App.Modules.Demo.Data.DemoRecord { Code = "CLI", Name = "snapshot" });
                db.SaveChanges();
            }
            if (scenario == "invalid") File.WriteAllText(Path.Combine(data, "settings.json"), "{");
            if (scenario == "user-server")
                File.WriteAllText(Path.Combine(data, "database.json"),
                    """{"Provider":"PostgreSql","ConnectionString":"Host=127.0.0.1;Database=unused;Username=test"}""");
            if (scenario == "invalid-database-config")
                File.WriteAllText(Path.Combine(data, "database.json"), "{");
            if (scenario == "directory-database-config")
                Directory.CreateDirectory(Path.Combine(data, "database.json"));
            if (scenario == "existing") File.WriteAllText(target, "keep");
            if (inspect && scenario != "missing")
            {
                // A ZIP produced with the same format as the historical desktop; no CLI support required.
                var snapshot = Path.Combine(root, "database.db");
                DatabaseInitializer.CreateConsistentCopy(source, snapshot);
                var schema = DatabaseInitializer.ValidateCompatible(snapshot);
                var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(snapshot)));
                var manifest = new BackupService.BackupManifest(1,
                    scenario == "summary-controls" ? "0.0.1\r\n\u202e\u2028\u2029\u001b$INSTDIR" : "0.0.1",
                    scenario == "schema" ? "unknown" : schema, DateTimeOffset.UtcNow,
                    [new("database.db", new FileInfo(snapshot).Length, scenario == "hash" ? new string('0', 64) : hash)]);
                using (var archive = ZipFile.Open(target, ZipArchiveMode.Create))
                {
                    archive.CreateEntryFromFile(snapshot, "database.db");
                    if (scenario != "no-manifest")
                    {
                        using var writer = new StreamWriter(archive.CreateEntry("manifest.json").Open());
                        writer.Write(System.Text.Json.JsonSerializer.Serialize(manifest));
                    }
                }
                if (scenario == "invalid") File.WriteAllText(target, "not a zip");
                if (scenario == "existing-report") File.WriteAllText(report, "keep");
                if (scenario == "no-live")
                {
                    wal!.Dispose();
                    SqliteConnection.ClearAllPools();
                    File.Delete(source);
                }
            }
            var archiveBefore = inspect && File.Exists(target) ? File.ReadAllBytes(target) : null;
            var databaseBefore = inspect && File.Exists(source) ? ReadShared(source) : null;
            if (scenario == "linked-target")
            {
                var linked = Path.Combine(root, "linked");
                try { Directory.CreateSymbolicLink(linked, data); }
                catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
                {
                    // Windows accounts without symbolic-link privileges cannot exercise this case.
                    return;
                }
                target = Path.Combine(linked, "backup.zip");
            }
            // The CLI tests named-object existence. Keep its handle alive without thread ownership:
            // the continuation after WaitForExitAsync may run on another thread.
            using var instance = scenario == "running"
                ? new Mutex(false, $@"Local\Plinth.{InstanceActivator.ComputeInstanceId(data)}") : null;
            var assembly = typeof(CommandLineTests).Assembly.Location;
            var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var value in new[]
            {
                "exec", "--runtimeconfig", Path.ChangeExtension(assembly, ".runtimeconfig.json"),
                "--depsfile", Path.ChangeExtension(assembly, ".deps.json"), typeof(AppInfo).Assembly.Location,
                inspect ? "--inspect-backup-before-upgrade" : "--backup-before-upgrade", target,
            }) start.ArgumentList.Add(value);
            if (inspect) start.ArgumentList.Add(report);
            start.Environment["PLINTH_DATA_DIR"] = data;
            start.Environment["PLINTH_Profile"] = scenario == "production" ? "Production" : "Test";
            start.Environment["PLINTH_Authentication__Enabled"] = scenario == "authentication" ? "true" : "false";
            if (scenario == "authentication")
            {
                // A valid identity deployment must reach authorization, not fail configuration validation.
                start.Environment["PLINTH_Authentication__Authority"] = "https://identity.example.test";
                start.Environment["PLINTH_Authentication__ClientId"] = "plinth-cli-test";
                start.Environment["PLINTH_Authentication__RedirectUri"] = "http://127.0.0.1:49152/callback/";
            }
            start.Environment["PLINTH_Database__Provider"] = scenario == "server" ? "PostgreSql" : "Sqlite";
            // Exercise the same data-directory configuration used by desktop startup.
            if (scenario == "user-server") start.Environment.Remove("PLINTH_Database__Provider");
            start.Environment.Remove("PLINTH_Database__ConnectionString");
            if (scenario == "server")
                start.Environment["PLINTH_Database__ConnectionString"] = "Host=127.0.0.1;Database=unused;Username=test";
            start.Environment.Remove("DISPLAY");
            start.Environment.Remove("WAYLAND_DISPLAY");
            using var installationLock = scenario == "installation-lock"
                ? new FileStream(installationLockPath, FileMode.OpenOrCreate, FileAccess.Read, FileShare.Delete)
                : null;
            if (scenario == "installation-lock" && OperatingSystem.IsWindows())
                Assert.Throws<IOException>(() => InstallationGuard.Acquire(AppContext.BaseDirectory));
            using var process = Process.Start(start)!;
            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(timeout.Token);
                Assert.Equal(expected, process.ExitCode);
                Assert.Equal("", await stdout);
                Assert.DoesNotContain(root, await stderr);
                if (inspect)
                {
                    if (archiveBefore is not null) Assert.Equal(archiveBefore, File.ReadAllBytes(target));
                    if (databaseBefore is not null) Assert.Equal(databaseBefore, ReadShared(source));
                    if (expected == 0)
                    {
                        var info = File.ReadAllText(report, System.Text.Encoding.Unicode);
                        Assert.Contains("Application: 0.0.1", info);
                        Assert.Contains("Schema:", info);
                        Assert.Contains("UTC:", info);
                        Assert.Contains("Files: 1", info);
                        Assert.Equal(3, File.ReadAllLines(report, System.Text.Encoding.Unicode).Length);
                        Assert.DoesNotContain('\u202e', info);
                        Assert.DoesNotContain('\u2028', info);
                        Assert.DoesNotContain('\u2029', info);
                        Assert.DoesNotContain('\u001b', info);
                    }
                    else if (scenario == "existing-report") Assert.Equal("keep", File.ReadAllText(report));
                    else Assert.False(File.Exists(report));
                }
                if (expected == 0)
                {
                    BackupService.Inspect(target);
                    var extracted = Path.Combine(root, "extracted");
                    ZipFile.ExtractToDirectory(target, extracted);
                    using var copy = DatabaseInitializer.Open(Path.Combine(extracted, "database.db"));
                    using var count = copy.CreateCommand();
                    count.CommandText = "SELECT count(*) FROM demo_records WHERE Code='CLI'";
                    Assert.Equal(1L, (long)count.ExecuteScalar()!);
                }
                else if (scenario == "existing") Assert.Equal("keep", File.ReadAllText(target));
                else if (!inspect) Assert.False(File.Exists(target));
                Assert.Empty(Directory.GetDirectories(root, "*.work-*"));
                Assert.Empty(Directory.GetFiles(root, "*.tmp-*"));
                Assert.Equal(scenario != "missing" && scenario != "no-live", File.Exists(source));
                Assert.False(Directory.Exists(Path.Combine(data, "logs")));
            }
            finally
            {
                if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        finally
        {
            if (createdInstallationLock) File.Delete(installationLockPath);
            if (scenario == "application-directory") File.Delete(target);
            // Invalid Windows path spellings must never be opened, even by fixture cleanup.
            if (scenario is not ("report-ads" or "report-control")) File.Delete(report);
            SqliteConnection.ClearAllPools();
            Directory.Delete(root, recursive: true);
        }
    }

    // 活跃 WAL 连接持有主库写句柄；File.ReadAllBytes 的共享模式会冲突，改用共享读取原始字节。
    private static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("Pipe closed.");
    }
}
