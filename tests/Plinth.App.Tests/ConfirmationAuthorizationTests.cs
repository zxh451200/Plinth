using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Notes.Services;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
#if PLINTH_XLSX
using Plinth.App.Modules.Demo.ViewModels;
#endif
using Xunit;

namespace Plinth.App.Tests;

public sealed class ConfirmationAuthorizationTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-confirm-" + Guid.NewGuid().ToString("N"));

    public ConfirmationAuthorizationTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        _database.Dispose();
        Directory.Delete(_directory, recursive: true);
    }

    public static IEnumerable<object[]> ConfirmationCases()
    {
        foreach (var operation in new[] { "save", "delete", "import", "export", "previewErrors" })
            foreach (var change in new[] { "logout", "switch", "session", "revoke", "stable", "decline", "cancel" })
                yield return [operation, change];
    }

    [Theory]
    [MemberData(nameof(ConfirmationCases))]
    public async Task Demo_rechecks_after_confirmation_before_any_side_effect(string operation, string change)
    {
        var users = new Users();
        var audit = new Audit();
        var permissions = new Permissions(new PermissionService(users, audit));
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, users, audit),
            audit, authenticationEnabled: true, production: true);
        var gate = new ConfirmationGate();
        operations.ConfirmProductionAsync = gate.ConfirmAsync;
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance, operations);
        var record = new DemoRecord { Code = "KEEP", Name = "original" };
        await using (var db = _database.Factory.CreateDbContext())
        {
            db.Set<DemoRecord>().Add(record);
            await db.SaveChangesAsync();
        }
        var source = Path.Combine(_directory, "source.csv");
        var report = Path.Combine(_directory, "report.csv");
        var exports = Path.Combine(_directory, "exports");
        await File.WriteAllTextAsync(source, "code,name,remark\nKEEP,changed,text\nNEW,added,text\n");
        if (change != "stable") await File.WriteAllTextAsync(report, "original report");
        using var cancellation = new CancellationTokenSource();
        Task pending = operation switch
        {
            "save" => service.SaveAsync(new DemoRecord { Code = "NEW", Name = "added" }, cancellation.Token),
            "delete" => service.DeleteAsync(record.Id, record.Version, cancellation.Token),
            "import" => service.ImportCsvAsync(source, cancellation.Token),
            "export" => service.ExportCsvAsync(null, exports, cancellation.Token),
            _ => service.ExportPreviewErrorsAsync(report, [new CsvIssue(2, "TEST", "invalid")], cancellation.Token),
        };
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            Assert.Single(permissions.Correlations);
            Assert.Empty(audit.Events);
            switch (change)
            {
                case "logout": users.CurrentUser = RemoteUserContext.Anonymous; break;
                // Keep the session version unchanged to exercise Subject comparison independently.
                case "switch": users.CurrentUser = users.CurrentUser with { Subject = "bob" }; break;
                case "session": users.SessionVersion++; break;
                case "revoke": users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty }; break;
                case "cancel": cancellation.Cancel(); break;
            }
        }
        finally { gate.Release.TrySetResult(change != "decline"); }

        if (change == "stable")
        {
            await pending;
            var rows = await service.SearchAsync(null);
            Assert.Equal(operation switch { "save" or "import" => 2, "delete" => 0, _ => 1 }, rows.Count);
            if (operation == "import") Assert.Equal("changed", rows.Single(r => r.Code == "KEEP").Name);
            if (operation == "export") Assert.Single(Directory.GetFiles(exports, "*.csv"));
            if (operation == "previewErrors") Assert.Contains("TEST", await File.ReadAllTextAsync(report));
        }
        else
        {
            if (change is "decline" or "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
            var stored = Assert.Single(await service.SearchAsync(null));
            Assert.Equal(record.Id, stored.Id);
            Assert.Equal("KEEP", stored.Code);
            Assert.Equal("original", stored.Name);
            Assert.Equal(record.Version, stored.Version);
            Assert.Equal("original report", await File.ReadAllTextAsync(report));
            Assert.False(Directory.Exists(exports));
            Assert.Equal(2, Directory.GetFiles(_directory).Length);
        }
        Assert.Equal(1, gate.Calls);
        var businessEvent = Assert.Single(audit.Events.Where(e => e.Module == "Demo"));
        Assert.Equal("alice", businessEvent.Actor);
        Assert.Equal(operation, businessEvent.Operation);
        Assert.Equal(change == "stable" ? "success" : change is "decline" or "cancel" ? "cancelled" : "denied",
            businessEvent.Result);
        Assert.Equal(change is "stable" or "revoke" ? 2 : 1, permissions.Correlations.Count);
        Assert.All(permissions.Correlations, id => Assert.Equal(businessEvent.CorrelationId, id));
    }

    [Theory]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task Offline_confirms_without_permissions_and_rechecks_token(
        bool production, bool accepted, bool cancel)
    {
        var users = new Users { CurrentUser = RemoteUserContext.Anonymous };
        var audit = new Audit();
        var permissions = new Permissions(new PermissionService(users, audit));
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, users, audit),
            audit, authenticationEnabled: false, production: production);
        var gate = new ConfirmationGate();
        operations.ConfirmProductionAsync = gate.ConfirmAsync;
        using var cancellation = new CancellationTokenSource();
        var path = Path.Combine(_directory, "offline.csv");
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance, operations);
        var pending = service.ExportPreviewErrorsAsync(path, [], cancellation.Token);
        if (production)
        {
            try
            {
                await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
                if (cancel) cancellation.Cancel();
            }
            finally { gate.Release.TrySetResult(accepted); }
        }
        if (production && (!accepted || cancel))
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else
            Assert.Equal(path, await pending);
        Assert.Equal(!production || (accepted && !cancel), File.Exists(path));
        Assert.Equal(production ? 1 : 0, gate.Calls);
        Assert.Empty(permissions.Correlations);
        Assert.Equal(production && (!accepted || cancel) ? "cancelled" : "success", Assert.Single(audit.Events).Result);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_after_database_commit_keeps_success(bool authentication)
    {
        var users = new Users();
        var audit = new Audit();
        var permissions = new Permissions(new PermissionService(users, audit));
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, users, audit),
            audit, authenticationEnabled: authentication, production: true);
        var confirmations = 0;
        operations.ConfirmProductionAsync = () => { confirmations++; return Task.FromResult(true); };
        using var cancellation = new CancellationTokenSource();
        var result = await operations.ExecuteConfirmedAsync("demo:save", "Demo", "save", async ct =>
        {
            await using var db = _database.Factory.CreateDbContext();
            db.Set<DemoRecord>().Add(new DemoRecord { Code = "COMMITTED", Name = "saved" });
            await db.SaveChangesAsync(ct);
            cancellation.Cancel();
            return 42;
        }, cancellation.Token);
        Assert.Equal(42, result);
        await using var check = _database.Factory.CreateDbContext();
        Assert.Equal("COMMITTED", Assert.Single(check.Set<DemoRecord>()).Code);
        Assert.Equal("success", Assert.Single(audit.Events).Result);
        Assert.Equal(1, confirmations);
        Assert.Equal(authentication ? 2 : 0, permissions.Correlations.Count);
    }

    [Fact]
    public async Task Legacy_authorized_call_without_confirmation_demands_once_and_supports_default_session_version()
    {
        var users = new OfflineUserContext();
        var audit = new Audit();
        var permissions = new AllowPermissions();
        var authorized = new AuthorizedOperation(permissions, users, audit);
        Assert.Equal(42, await authorized.ExecuteAsync("demo:save", "Demo", "save", (_, _) => Task.FromResult(42)));
        Assert.Equal(1, permissions.Calls);
        Assert.Equal(42, await authorized.ExecuteAsync("demo:save", "Demo", "save",
            (_, _) => Task.FromResult(42), confirm: () => Task.FromResult(true)));
        Assert.Equal(3, permissions.Calls);
        Assert.Equal(2, audit.Events.Count);
        Assert.All(audit.Events, e => Assert.Equal("success", e.Result));
    }

    [Fact]
    public async Task Default_offline_demo_save_still_succeeds()
    {
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance);
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "DEFAULT", Name = "offline" }));
        Assert.Equal("DEFAULT", Assert.Single(await service.SearchAsync(null)).Code);
    }

    public static IEnumerable<object[]> OtherServiceCases()
    {
        foreach (var service in new[] { "notes", "logs", "backup" })
            foreach (var change in new[] { "logout", "switch", "session", "revoke", "stable", "decline", "cancel" })
                if (service != "logs" || change != "cancel")
                    yield return [service, change];
    }

    [Theory]
    [MemberData(nameof(OtherServiceCases))]
    public async Task Other_services_recheck_confirmation_before_database_or_file_changes(string service, string change)
    {
        var users = new Users
        {
            CurrentUser = new("alice", ImmutableHashSet<string>.Empty,
                ImmutableHashSet.Create("notes:save", "notes:read", "logs:read", "audit:read", "settings:manage", "backup:export")),
        };
        var audit = new Audit();
        var permissions = new Permissions(new PermissionService(users, audit));
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, users, audit),
            audit, authenticationEnabled: true, production: true);
        var gate = new ConfirmationGate();
        operations.ConfirmProductionAsync = gate.ConfirmAsync;
        var offlineNotes = new NoteService(_database.Factory, new SensitiveOperationService(
            new AuthorizedOperation(permissions, users, audit), audit, authenticationEnabled: false, production: false));
        var original = await offlineNotes.SaveAsync("original", 0);
        audit.Events.Clear();
        var path = Path.Combine(_directory, "old.ndjson");
        await File.WriteAllTextAsync(path, "original log");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        var destination = Path.Combine(_directory, "backup.zip");
        using var cancellation = new CancellationTokenSource();
        Task pending = service switch
        {
            "notes" => new NoteService(_database.Factory, operations).SaveAsync("changed", original.Version, cancellation.Token),
            "logs" => new LogAccessService(permissions, users, true, operations)
                .CleanOldAsync(_directory, TimeSpan.FromDays(30)),
            _ => new BackupService(_database.Factory, operations).BackupToAsync(destination, cancellation.Token),
        };
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            switch (change)
            {
                case "logout": users.CurrentUser = RemoteUserContext.Anonymous; break;
                case "switch": users.CurrentUser = users.CurrentUser with { Subject = "bob" }; break;
                case "session": users.SessionVersion++; break;
                case "revoke": users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty }; break;
                case "cancel": cancellation.Cancel(); break;
            }
        }
        finally { gate.Release.TrySetResult(change != "decline"); }

        if (change == "stable")
            await pending;
        else if (change is "decline" or "cancel")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);

        var businessEvent = Assert.Single(audit.Events.Where(e => e.Module == (service switch
        {
            "notes" => "Notes",
            "logs" => "Logs",
            _ => "Backup",
        })));
        Assert.Equal("alice", businessEvent.Actor);
        Assert.Equal(change == "stable" ? "success" : change is "decline" or "cancel" ? "cancelled" : "denied",
            businessEvent.Result);
        Assert.Equal(1, gate.Calls);
        var stored = (await offlineNotes.LoadAsync())!;
        Assert.Equal(service == "notes" && change == "stable" ? "changed" : "original", stored.Text);
        Assert.Equal(original.Version + (service == "notes" && change == "stable" ? 1 : 0), stored.Version);
        Assert.Equal(service == "backup" && change == "stable", File.Exists(destination));
        if (service == "logs" && change == "stable")
            Assert.False(File.Exists(path));
        else
            Assert.Equal("original log", await File.ReadAllTextAsync(path));
        Assert.Empty(Directory.GetDirectories(_directory, "*.work-*"));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp-*"));
    }

    public static IEnumerable<object[]> SeparatedCases()
    {
        foreach (var boundary in new[] { "before", "dialog", "queue" })
            foreach (var change in new[] { "switch", "session", "revoke", "stable", "decline", "cancel" })
                yield return [boundary, change];
    }

    [Theory]
    [MemberData(nameof(SeparatedCases))]
    public async Task Separated_confirmation_rechecks_each_boundary(string boundary, string change)
    {
        var users = new Users();
        var audit = new Audit();
        var permissions = new Permissions(new PermissionService(users, audit));
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, users, audit),
            audit, authenticationEnabled: true, production: true);
        var gate = new ConfirmationGate();
        operations.ConfirmProductionAsync = gate.ConfirmAsync;
        using var cancellation = new CancellationTokenSource();
        void Change()
        {
            switch (change)
            {
                case "switch": users.CurrentUser = users.CurrentUser with { Subject = "bob" }; break;
                case "session": users.SessionVersion++; break;
                case "revoke": users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty }; break;
                case "cancel": cancellation.Cancel(); break;
            }
        }
        if (boundary == "before") Change();
        var pending = operations.PrepareConfirmedAsync("demo:export", "Demo", "export", cancellation.Token);
        if (!pending.IsCompleted)
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (boundary == "dialog") Change();
            gate.Release.TrySetResult(change != "decline");
        }
        var ran = false;
        async Task Run()
        {
            var confirmation = await pending;
            if (boundary == "queue") Change();
            await confirmation.ExecuteAsync(_ =>
            {
                ran = true;
                return Task.FromResult(42);
            }, cancellation.Token);
        }
        if (change is "cancel" or "decline")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(Run);
        else if (change == "revoke" || (boundary != "before" && (change is "switch" or "session")))
            await Assert.ThrowsAsync<UnauthorizedAccessException>(Run);
        else
            await Run(); // A different user/session before capture is the legitimate initiator.
        Assert.Equal(change == "stable" || (boundary == "before" && (change is "switch" or "session")), ran);
        Assert.Equal(boundary == "before" && (change is "cancel" or "revoke") ? 0 : 1, gate.Calls);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Separated_confirmation_does_not_check_cancellation_after_commit(bool authentication)
    {
        var users = new Users();
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authenticationEnabled: authentication, production: true);
        var calls = 0;
        operations.ConfirmProductionAsync = () => { calls++; return Task.FromResult(true); };
        using var cancellation = new CancellationTokenSource();
        var confirmation = await operations.PrepareConfirmedAsync("demo:export", "Demo", "export", cancellation.Token);
        Assert.Equal(42, await confirmation.ExecuteAsync(_ =>
        {
            cancellation.Cancel();
            return Task.FromResult(42);
        }, cancellation.Token));
        Assert.Equal(1, calls);
        Assert.All(audit.Events, entry => Assert.Equal("success", entry.Result));
    }

    [Theory]
    [InlineData("backup", false)]
    [InlineData("backup", true)]
    [InlineData("restore", true)]
    [InlineData("diagnostics", false)]
    [InlineData("diagnostics", true)]
#if PLINTH_XLSX
    [InlineData("xlsx", false)]
    [InlineData("xlsx", true)]
#endif
    public async Task Queued_VM_export_keeps_running_after_close_but_rejects_new_session(string kind, bool changeSession)
    {
        var users = new Users
        {
            CurrentUser = new("alice", ImmutableHashSet<string>.Empty,
                ImmutableHashSet.Create("backup:export", "backup:restore", "diagnostics:export", "demo:export", "demo:read")),
        };
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authenticationEnabled: true, production: true);
        var calls = 0;
        operations.ConfirmProductionAsync = () => { calls++; return Task.FromResult(true); };
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = Enumerable.Range(0, 2)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)).ToArray();
        for (var i = 0; i < started.Length; i++)
        {
            var signal = started[i];
            tasks.Enqueue("blocker", "Test", async (_, token) =>
            {
                signal.TrySetResult(true);
                await release.Task.WaitAsync(token);
            });
        }
        await using (var db = _database.Factory.CreateDbContext())
        {
            db.Set<DemoRecord>().Add(new DemoRecord { Code = "EXPORT", Name = "example" });
            await db.SaveChangesAsync();
        }
        try
        {
            await Task.WhenAll(started.Select(s => s.Task)).WaitAsync(TimeSpan.FromSeconds(5));
            var destination = Path.Combine(_directory, kind + ".zip");
            Task exporting;
            if (kind is "backup" or "restore")
            {
                var vm = new BackupViewModel(new BackupService(_database.Factory, operations),
                    NullLogger<BackupViewModel>.Instance, tasks);
                if (kind == "backup") exporting = vm.BackupToAsync(destination);
                else
                {
                    SetRestoreSelection(vm, destination);
                    exporting = vm.ConfirmRestoreCommand.ExecuteAsync(null);
                }
                vm.PageClosed();
            }
            else if (kind == "diagnostics")
            {
                var vm = new TaskDiagnosticsViewModel(tasks,
                    new DiagnosticService(new LogAccessService(authenticationEnabled: false)), operations)
                {
                    IncludeLogs = false,
                };
                await vm.PreviewCommand.ExecuteAsync(null);
                vm.Confirmed = true;
                exporting = vm.ExportAsync(destination);
                vm.PageClosed();
                vm.Dispose();
            }
#if PLINTH_XLSX
            else
            {
                var vm = new SpreadsheetViewModel(new SpreadsheetService(),
                    new DemoService(_database.Factory, NullLogger<DemoService>.Instance, operations),
                    tasks, operations, NullLogger<SpreadsheetViewModel>.Instance);
                exporting = vm.ExportToAsync(destination);
                vm.Dispose();
            }
#else
            else throw new InvalidOperationException();
#endif
            var item = Assert.Single(tasks.Snapshot().Where(t => t.Module != "Test"));
            Assert.Equal(WorkStatus.Waiting, item.Status);
            if (changeSession) users.SessionVersion++;
            release.TrySetResult(true);
            await exporting.WaitAsync(TimeSpan.FromSeconds(10));
            await tasks.WaitAsync(item.Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(changeSession ? WorkStatus.Failed : WorkStatus.Succeeded,
                tasks.Snapshot().Single(t => t.Id == item.Id).Status);
            Assert.Equal(!changeSession, kind == "xlsx"
                ? Directory.Exists(destination) && Directory.GetFiles(destination, "*.xlsx").Length == 1
                : File.Exists(destination));
            Assert.Equal(1, calls);
        }
        finally
        {
            release.TrySetResult(true);
            await tasks.StopAsync();
        }
    }

    [Theory]
    [InlineData("backup")]
    [InlineData("restore")]
    [InlineData("diagnostics")]
#if PLINTH_XLSX
    [InlineData("xlsx")]
#endif
    public async Task VM_confirmation_rejects_session_change_before_enqueue(string kind)
    {
        var users = new Users
        {
            CurrentUser = new("alice", ImmutableHashSet<string>.Empty,
                ImmutableHashSet.Create("backup:export", "backup:restore", "diagnostics:export", "demo:export")),
        };
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authenticationEnabled: true, production: true);
        var gate = new ConfirmationGate();
        operations.ConfirmProductionAsync = gate.ConfirmAsync;
        var tasks = new TaskService(NullLogger<TaskService>.Instance);
        var destination = Path.Combine(_directory, "not-created");
        Task pending;
        if (kind is "backup" or "restore")
        {
            var vm = new BackupViewModel(new BackupService(_database.Factory, operations),
                NullLogger<BackupViewModel>.Instance, tasks);
            if (kind == "backup") pending = vm.BackupToAsync(destination);
            else
            {
                // Supply an already selected restore object. A denial must occur before opening it.
                SetRestoreSelection(vm, destination);
                pending = vm.ConfirmRestoreCommand.ExecuteAsync(null);
            }
        }
        else if (kind == "diagnostics")
        {
            var vm = new TaskDiagnosticsViewModel(tasks,
                new DiagnosticService(new LogAccessService(authenticationEnabled: false)), operations)
            {
                IncludeLogs = false,
            };
            await vm.PreviewCommand.ExecuteAsync(null);
            vm.Confirmed = true;
            pending = vm.ExportAsync(destination);
        }
#if PLINTH_XLSX
        else
        {
            var vm = new SpreadsheetViewModel(new SpreadsheetService(),
                new DemoService(_database.Factory, NullLogger<DemoService>.Instance, operations),
                tasks, operations, NullLogger<SpreadsheetViewModel>.Instance);
            pending = vm.ExportToAsync(destination);
        }
#else
        else throw new InvalidOperationException();
#endif
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            users.SessionVersion++;
        }
        finally { gate.Release.TrySetResult(true); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(tasks.Snapshot());
        Assert.False(File.Exists(destination));
        Assert.False(Directory.Exists(destination));
        Assert.Equal(1, gate.Calls);
        Assert.Contains(audit.Events, entry => entry.Result == "denied");
        await tasks.StopAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Closing_backup_page_during_confirmation_never_starts_file_work(bool queued)
    {
        var users = new Users();
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new AllowPermissions(), users, audit),
            audit, authenticationEnabled: false, production: true);
        var gate = new ConfirmationGate();
        operations.ConfirmProductionAsync = gate.ConfirmAsync;
        var tasks = queued ? new TaskService(NullLogger<TaskService>.Instance) : null;
        var vm = new BackupViewModel(new BackupService(_database.Factory, operations),
            NullLogger<BackupViewModel>.Instance, tasks);
        var destination = Path.Combine(_directory, "closed.zip");
        var pending = vm.BackupToAsync(destination);
        try
        {
            await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            vm.PageClosed();
        }
        finally { gate.Release.TrySetResult(true); }
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(File.Exists(destination));
        Assert.Empty(Directory.GetFileSystemEntries(_directory));
        if (tasks is not null)
        {
            Assert.Empty(tasks.Snapshot());
            await tasks.StopAsync();
        }
        Assert.Equal(1, gate.Calls);
        Assert.DoesNotContain(audit.Events, entry => entry.Operation == "backup.create");
    }

    [Fact]
    public async Task Confirmed_error_report_preserves_an_existing_destination()
    {
        var users = new Users();
        var audit = new Audit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new AllowPermissions(), users, audit),
            audit, authenticationEnabled: false, production: true);
        var confirmations = 0;
        operations.ConfirmProductionAsync = () => { confirmations++; return Task.FromResult(true); };
        var service = new DemoService(_database.Factory, NullLogger<DemoService>.Instance, operations);
        var destination = Path.Combine(_directory, "existing.csv");
        await File.WriteAllTextAsync(destination, "original report");
        await Assert.ThrowsAsync<IOException>(() => service.ExportPreviewErrorsAsync(
            destination, [new CsvIssue(2, "TEST", "invalid")]));
        Assert.Equal("original report", await File.ReadAllTextAsync(destination));
        Assert.Single(Directory.GetFiles(_directory));
        Assert.Equal(1, confirmations);
        Assert.Equal("failed", Assert.Single(audit.Events).Result);
    }

    private static void SetRestoreSelection(BackupViewModel vm, string path)
    {
        typeof(BackupViewModel).GetField("_restorePath",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(vm, path);
        vm.RestoreConfirmed = true;
    }

    private sealed class Users : IRemoteUserContext
    {
        public RemoteUserContext CurrentUser { get; set; } = new("alice", ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create(StringComparer.Ordinal, "demo:save", "demo:delete", "demo:import", "demo:export"));
        public long SessionVersion { get; set; } = 1;
    }

    private sealed class Permissions(IPermissionService inner) : IPermissionService
    {
        public List<string> Correlations { get; } = [];
        public bool HasPermission(string permission) => inner.HasPermission(permission);
        public void Demand(string permission, string correlationId)
        {
            Correlations.Add(correlationId);
            inner.Demand(permission, correlationId);
        }
    }

    private sealed class AllowPermissions : IPermissionService
    {
        public int Calls { get; private set; }
        public bool HasPermission(string permission) => true;
        public void Demand(string permission, string correlationId) => Calls++;
    }

    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Events { get; } = [];
        public void Write(AuditEvent entry) => Events.Add(entry);
    }

    private sealed class ConfirmationGate
    {
        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public Task<bool> ConfirmAsync()
        {
            Calls++;
            Entered.TrySetResult(true);
            return Release.Task;
        }
    }
}
