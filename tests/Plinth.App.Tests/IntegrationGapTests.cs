using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Data;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class IntegrationGapTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-gaps-" + Guid.NewGuid().ToString("N"));
    private readonly string _settingsPath = Path.Combine(AppPaths.DataDirectory, "settings.json");
    private readonly byte[]? _previousSettings;

    public IntegrationGapTests()
    {
        Directory.CreateDirectory(_directory);
        Directory.CreateDirectory(AppPaths.DataDirectory);
        _previousSettings = File.Exists(_settingsPath) ? File.ReadAllBytes(_settingsPath) : null;
        File.Delete(_settingsPath);
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, true);
        if (Directory.Exists(_settingsPath)) Directory.Delete(_settingsPath);
        if (_previousSettings is null) File.Delete(_settingsPath);
        else File.WriteAllBytes(_settingsPath, _previousSettings);
    }

    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Events { get; } = [];
        public void Write(AuditEvent entry) => Events.Add(entry);
    }

    private sealed class User(params string[] permissions) : IRemoteUserContext
    {
        public RemoteUserContext CurrentUser => new("subject-test", ImmutableHashSet<string>.Empty,
            permissions.ToImmutableHashSet(StringComparer.Ordinal));
    }

    private static SensitiveOperationService Operations(Audit audit, bool remote, bool production = false,
        params string[] permissions)
    {
        var user = new User(permissions);
        return new(new AuthorizedOperation(new PermissionService(user, audit), user, audit), audit, remote, production);
    }

    [Fact]
    public async Task Export_crosses_page_boundaries_without_missing_or_duplicating_rows()
    {
        using var database = new TestDatabase();
        await using (var db = database.Factory.CreateDbContext())
        {
            db.Set<DemoRecord>().AddRange(Enumerable.Range(0, 451).Select(i =>
                new DemoRecord { Code = $"A{i:D5}", Name = $"name,{i}", Remark = "=1+1" }));
            await db.SaveChangesAsync();
        }
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
        var path = await service.ExportCsvAsync("A", _directory);
        var result = await CsvReader.ReadFileAsync(path, "utf-8");
        Assert.Empty(result.Errors);
        Assert.Equal(452, result.Rows.Count);
        Assert.Equal(451, result.Rows.Skip(1).Select(r => r[0]).Distinct().Count());
        Assert.Equal("A00000", result.Rows[1][0]);
        Assert.Equal("A00450", result.Rows[^1][0]);
        Assert.Equal("=1+1", result.Rows[1][2]);
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Async_writer_cancellation_preserves_destination_and_cleans_temporary_file()
    {
        var path = Path.Combine(_directory, "keep.csv");
        await File.WriteAllTextAsync(path, "original");
        using var cancellation = new CancellationTokenSource();
        async IAsyncEnumerable<IReadOnlyList<string>> Rows()
        {
            yield return new[] { "first" };
            await Task.Yield();
            cancellation.Cancel();
            yield return new[] { "second" };
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CsvWriter.WriteFileAsync(path, ["header"], Rows(), cancellation.Token));
        Assert.Equal("original", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task Preview_hash_covers_unshown_rows_and_rejects_changed_file_before_database_write()
    {
        using var database = new TestDatabase();
        var path = Path.Combine(_directory, "import.csv");
        var text = "code,name,remark\n" + string.Join("\n",
            Enumerable.Range(0, 30).Select(i => $"A{i},name,remark"));
        await File.WriteAllTextAsync(path, text);
        var preview = await CsvReader.PreviewAsync(path, "utf-8");
        Assert.Equal(21, preview.Content!.Rows.Count);
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
        await File.WriteAllTextAsync(path, text + "\nZ,changed,last");
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            service.ImportCsvAsync(path, "utf-8", ",", [0, 1, 2], expectedSha256: preview.Sha256));
        Assert.Empty(await service.SearchAsync(null));
        await File.WriteAllTextAsync(path, text);
        Assert.True((await service.ImportCsvAsync(path, "utf-8", ",", [0, 1, 2],
            expectedSha256: preview.Sha256)).Succeeded);
        Assert.Equal(30, (await service.SearchAsync(null)).Count);
    }

    [Fact]
    public async Task Preview_errors_can_be_exported_without_importing_bad_csv()
    {
        using var database = new TestDatabase();
        var path = Path.Combine(_directory, "bad.csv");
        await File.WriteAllTextAsync(path, "code,name,remark\nA,name\n");
        var preview = await CsvReader.PreviewAsync(path, "utf-8");
        Assert.NotEmpty(preview.Content!.Errors);
        using var vm = new Plinth.App.Modules.Demo.ViewModels.DemoListViewModel(
            new DemoService(database.Factory, NullLogger<DemoService>.Instance),
            NullLogger<Plinth.App.Modules.Demo.ViewModels.DemoListViewModel>.Instance);
        var report = Path.Combine(_directory, "errors.csv");
        await vm.ExportPreviewErrorsAsync(report, preview.Content.Errors);
        Assert.Contains("COLUMN_COUNT", await File.ReadAllTextAsync(report));
        Assert.Empty(await new DemoService(database.Factory, NullLogger<DemoService>.Instance).SearchAsync(null));
    }

    [Fact]
    public async Task Save_and_delete_enforce_service_permissions_and_audit_business_rejections()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var offline = new DemoService(database.Factory, NullLogger<DemoService>.Instance, Operations(audit, false));
        var record = new DemoRecord { Code = "A", Name = "name" };
        Assert.Null(await offline.SaveAsync(record));
        Assert.NotNull(await offline.SaveAsync(new DemoRecord { Code = "", Name = "invalid" }));
        Assert.Contains(audit.Events, e => e.Operation == "save" && e.Result == "rejected");
        var denied = new DemoService(database.Factory, NullLogger<DemoService>.Instance, Operations(audit, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.SaveAsync(new DemoRecord { Code = "B", Name = "denied" }));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.DeleteAsync(record.Id, record.Version));
        Assert.Single(await offline.SearchAsync(null));
        var allowed = new DemoService(database.Factory, NullLogger<DemoService>.Instance,
            Operations(audit, true, false, "demo:delete"));
        Assert.Null(await allowed.DeleteAsync(record.Id, record.Version));
        Assert.Contains(audit.Events, e => e.Operation == "delete" && e.Result == "success" && e.Actor == "subject-test");
        Assert.DoesNotContain(audit.Events, e => e.Operation == "save" && e.Actor == "subject-test" && e.Result == "success");
    }

    [Fact]
    public async Task Settings_denial_and_production_cancellation_preserve_persisted_values()
    {
        var audit = new Audit();
        var service = new SettingsService(NullLogger<SettingsService>.Instance, Operations(audit, true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => service.ChangeAsync("RemoteEnabled", true));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() =>
            service.ChangeAsync(nameof(AppSettings.Density), DensityService.Compact));
        Assert.Equal(DensityService.Standard, service.Current.Density);
        Assert.False(service.Current.Remote.Enabled);
        Assert.False(File.Exists(_settingsPath));
        var operations = Operations(audit, true, true, "settings:manage");
        operations.ConfirmProductionAsync = () => Task.FromResult(false);
        service = new SettingsService(NullLogger<SettingsService>.Instance, operations);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ChangeAsync("RemoteEnabled", true));
        Assert.False(service.Current.Remote.Enabled);
        Assert.False(File.Exists(_settingsPath));
        Assert.Contains(audit.Events, e => e.Module == "Settings" && e.Result == "cancelled");
        operations.ConfirmProductionAsync = () => Task.FromResult(true);
        await service.ChangeAsync("RemoteEnabled", true);
        Assert.True(new SettingsService(NullLogger<SettingsService>.Instance).Current.Remote.Enabled);
        Assert.Contains(audit.Events, e => e.Module == "Settings" && e.Result == "success");
    }

    [Fact]
    public async Task Settings_page_reports_persistence_failure_and_reverts_input()
    {
        var audit = new Audit();
        var service = new SettingsService(NullLogger<SettingsService>.Instance, Operations(audit, false));
        Directory.CreateDirectory(_settingsPath);
        var vm = new SettingsViewModel(service);
        vm.RemoteEnabled = true;
        await vm.PendingChange;
        Assert.False(vm.RemoteEnabled);
        Assert.False(service.Current.Remote.Enabled);
        Assert.NotEqual("已保存", vm.StatusText);
        Assert.Contains(audit.Events, e => e.Module == "Settings" && e.Result == "failed");
        Assert.Empty(Directory.GetFiles(AppPaths.DataDirectory, "settings.json.*.tmp"));
    }

    private sealed class Factory(string path) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<AppDbContext>().UseSqlite($"Data Source={path}").Options);
    }

    [Fact]
    public async Task Csv_service_denies_direct_calls_before_reading_or_creating_files()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var denied = new DemoService(database.Factory, NullLogger<DemoService>.Instance, Operations(audit, true));
        var missing = Path.Combine(_directory, "missing.csv");
        var destination = Path.Combine(_directory, "denied-exports");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ImportCsvAsync(missing));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ExportCsvAsync(null, destination));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => denied.ExportPreviewErrorsAsync(missing, []));
        Assert.False(Directory.Exists(destination));
        Assert.False(File.Exists(missing));
        Assert.Empty(await denied.SearchAsync(null));
        Assert.Contains(audit.Events, entry => entry.Operation == "import" && entry.Result == "denied");
        Assert.Contains(audit.Events, entry => entry.Operation == "export" && entry.Result == "denied");
    }

    [Fact]
    public async Task Csv_service_confirms_once_and_audits_validation_rejections()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var operations = Operations(audit, true, true, "demo:import", "demo:export");
        var confirmations = 0;
        var confirm = false;
        operations.ConfirmProductionAsync = () => { confirmations++; return Task.FromResult(confirm); };
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance, operations);
        var source = Path.Combine(_directory, "source.csv");
        var destination = Path.Combine(_directory, "exports");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportCsvAsync(source));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ExportCsvAsync(null, destination));
        Assert.Equal(2, confirmations);
        Assert.False(Directory.Exists(destination));
        confirm = true;
        await File.WriteAllTextAsync(source, "code,name,remark\nA,allowed,text\n");
        Assert.True((await service.ImportCsvAsync(source)).Succeeded);
        Assert.Equal(3, confirmations);
        Assert.True(File.Exists(await service.ExportCsvAsync(null, destination)));
        Assert.Equal(4, confirmations);
        await File.WriteAllTextAsync(source, "code,name,remark\nA,,invalid\n");
        Assert.False((await service.ImportCsvAsync(source)).Succeeded);
        Assert.Contains(audit.Events, entry => entry.Operation == "import" && entry.Result == "rejected");
        Assert.Contains(audit.Events, entry => entry.Operation == "import" && entry.Result == "cancelled");
    }

    [Fact]
    public async Task Manual_log_cleanup_uses_saved_retention_instead_of_thirty_days()
    {
        var settings = new SettingsService(NullLogger<SettingsService>.Instance, Operations(new Audit(), false));
        await settings.ChangeAsync(nameof(AppSettings.LogRetentionDays), 7);
        Assert.Equal(7, SettingsService.ReadLogRetention().Days);
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var old = Path.Combine(AppPaths.LogDirectory, $"gap-old-{Guid.NewGuid():N}.ndjson");
        var recent = Path.Combine(AppPaths.LogDirectory, $"gap-recent-{Guid.NewGuid():N}.ndjson");
        try
        {
            await File.WriteAllTextAsync(old, "");
            await File.WriteAllTextAsync(recent, "");
            File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-10));
            File.SetLastWriteTimeUtc(recent, DateTime.UtcNow.AddDays(-2));
            var page = new LogViewModel(new FolderService(NullLogger<FolderService>.Instance));
            await page.CleanOldCommand.ExecuteAsync(null);
            Assert.False(File.Exists(old));
            Assert.True(File.Exists(recent));
            page.PageClosed();
        }
        finally
        {
            File.Delete(old);
            File.Delete(recent);
        }
    }

    [Fact]
    public void Old_restore_journal_does_not_delete_unmanaged_workspace_preferences()
    {
        var database = Path.Combine(_directory, "legacy.db");
        var workspace = Path.Combine(_directory, "workspace-preferences.json");
        var journal = database + ".restore-transaction";
        Directory.CreateDirectory(journal);
        File.WriteAllText(workspace, """{"Demo":{"PageSize":100}}""");
        File.WriteAllText(Path.Combine(journal, "ready.json"), "[]");
        Assert.False(BackupService.ApplyPendingRestore(database));
        Assert.Equal("""{"Demo":{"PageSize":100}}""", File.ReadAllText(workspace));
        Assert.False(Directory.Exists(journal));
    }

    [Fact]
    public async Task Production_save_and_delete_cancellation_leave_database_unchanged()
    {
        using var database = new TestDatabase();
        var audit = new Audit();
        var operations = Operations(audit, true, true, "demo:save", "demo:delete");
        operations.ConfirmProductionAsync = () => Task.FromResult(true);
        var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance, operations);
        var record = new DemoRecord { Code = "KEEP", Name = "original" };
        Assert.Null(await service.SaveAsync(record));
        operations.ConfirmProductionAsync = () => Task.FromResult(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAsync(new DemoRecord { Code = "BLOCK", Name = "cancelled" }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.DeleteAsync(record.Id, record.Version));
        Assert.Single(await service.SearchAsync(null));
        Assert.Contains(audit.Events, e => e.Operation == "save" && e.Result == "cancelled");
        Assert.Contains(audit.Events, e => e.Operation == "delete" && e.Result == "cancelled");
    }

    [Fact]
    public async Task Zip_roundtrip_restores_sanitized_workspace_and_schema_two_settings()
    {
        var sourceDir = Path.Combine(_directory, "source");
        var targetDir = Path.Combine(_directory, "target");
        Directory.CreateDirectory(sourceDir);
        Directory.CreateDirectory(targetDir);
        var factory = new Factory(Path.Combine(sourceDir, "plinth.db"));
        using (var db = factory.CreateDbContext()) DatabaseInitializer.Initialize(db);
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "settings.json"),
            """{"SchemaVersion":1,"Theme":"Dark","Density":"Compact","Region":"en-US","TimeZoneId":"UTC","AccessToken":"do-not-copy"}""");
        await File.WriteAllTextAsync(Path.Combine(sourceDir, "workspace-preferences.json"),
            """{"Layout":{"Version":1,"OpenTabIds":["shell.overview"],"SelectedTabId":"shell.overview","SidebarWidth":300},"Demo":{"PageSize":100},"Navigation":{"FavoriteIds":["shell.overview"],"RecentIds":["shell.overview"]},"Token":"do-not-copy"}""");
        var zipPath = Path.Combine(_directory, "backup.zip");
        await new BackupService(factory).BackupToAsync(zipPath);
        Assert.Contains(BackupService.Inspect(zipPath).Files, f => f.Path == "workspace-preferences.json");
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            using var settings = new StreamReader(zip.GetEntry("settings.json")!.Open());
            var json = await settings.ReadToEndAsync();
            using var document = JsonDocument.Parse(json);
            Assert.Equal(2, document.RootElement.GetProperty("SchemaVersion").GetInt32());
            Assert.Equal("en-US", document.RootElement.GetProperty("Region").GetString());
            Assert.Equal("UTC", document.RootElement.GetProperty("TimeZoneId").GetString());
            Assert.Equal(DensityService.Compact, document.RootElement.GetProperty("Density").GetString());
            Assert.DoesNotContain("do-not-copy", json);
            using var workspace = new StreamReader(zip.GetEntry("workspace-preferences.json")!.Open());
            Assert.DoesNotContain("do-not-copy", await workspace.ReadToEndAsync());
        }
        var targetDb = Path.Combine(targetDir, "plinth.db");
        var oldWorkspace = """{"Layout":{"Version":1,"SidebarWidth":210}}""";
        await File.WriteAllTextAsync(Path.Combine(targetDir, "workspace-preferences.json"), oldWorkspace);
        Assert.Null(BackupService.StageRestore(zipPath, targetDb));
        Assert.True(BackupService.ApplyPendingRestore(targetDb));
        var restoredSettings = JsonSerializer.Deserialize<AppSettings>(
            await File.ReadAllTextAsync(Path.Combine(targetDir, "settings.json")))!;
        Assert.Equal("en-US", restoredSettings.Region);
        Assert.Equal("UTC", restoredSettings.TimeZoneId);
        Assert.Equal(DensityService.Compact, restoredSettings.Density);
        var restored = new WorkspacePreferencesService(Path.Combine(targetDir, "workspace-preferences.json"));
        Assert.Equal(300, restored.Layout!.SidebarWidth);
        Assert.Equal(100, restored.Demo.PageSize);
        Assert.Equal(new[] { "shell.overview" }, restored.Navigation.FavoriteIds);
        Assert.Equal(new[] { "shell.overview" }, restored.Navigation.RecentIds);
        Assert.Equal(oldWorkspace, await File.ReadAllTextAsync(Path.Combine(
            Directory.GetDirectories(targetDir, "plinth.db.before-restore-*").Single(), "workspace-preferences.json")));
    }

    [Fact]
    public void Workspace_backup_rejects_future_layout_and_drops_unknown_columns()
    {
        Assert.Throws<NotSupportedException>(() =>
            WorkspacePreferencesService.NormalizeForBackup("""{"Layout":{"Version":99}}"""));
        var json = WorkspacePreferencesService.NormalizeForBackup(
            """{"Demo":{"Columns":{"Secret":{"Visible":true,"Width":100},"Code":{"Visible":true,"Width":5000}}},"Token":"do-not-copy"}""");
        Assert.DoesNotContain("Secret", json);
        Assert.DoesNotContain("do-not-copy", json);
        using var document = JsonDocument.Parse(json);
        Assert.Equal(2000, document.RootElement.GetProperty("Demo")
            .GetProperty("Columns").GetProperty("Code").GetProperty("Width").GetDouble());
    }
}
