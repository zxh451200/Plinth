using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class LogAccessTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-access-" + Guid.NewGuid().ToString("N"));
    private readonly Users _users = new();
    private readonly Audit _audit = new();

    public LogAccessTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Theory]
    [InlineData("\"EventKind\":\"SecurityAudit\",\"TaskId\":\"task\"", LogEventKind.SecurityAudit)]
    [InlineData("\"EventId\":{\"Id\":4100}", LogEventKind.SecurityAudit)]
    [InlineData("\"EventId\":{\"Name\":\"SecurityAudit\"}", LogEventKind.SecurityAudit)]
    [InlineData("\"@mt\":\"SecurityAudit {Actor}\",\"Actor\":\"user\"", LogEventKind.SecurityAudit)]
    [InlineData("\"EventKind\":\"Task\"", LogEventKind.Task)]
    [InlineData("\"TaskId\":\"legacy-task\"", LogEventKind.Task)]
    [InlineData("\"EventId\":{\"Id\":\"wrong-type\"}", LogEventKind.Technical)]
    public void Classifies_current_and_legacy_events_with_audit_precedence(string fields, LogEventKind expected)
    {
        var entry = LogReader.Parse("{\"@t\":\"2026-11-01T08:00:00Z\"," + fields + "}");
        Assert.NotNull(entry);
        Assert.Equal(expected, entry.EventKind);
    }

    [Fact]
    public void Grants_separate_audit_from_operational_queries_and_fail_closed_without_identity()
    {
        WriteMixed();
        var access = Access();
        _users.Set("one", "logs:read");
        Assert.Equal(LogEventKind.Technical, Assert.Single(access.Query(_directory, new()).Entries).EventKind);
        Assert.Equal(2, access.Query(_directory, new(IncludeTaskEvents: true)).Entries.Count);
        Assert.Equal(LogEventKind.Task, Assert.Single(access.Query(_directory, new(EventKind: LogEventKind.Task)).Entries).EventKind);
        Assert.Throws<UnauthorizedAccessException>(() => access.Query(_directory, new(EventKind: LogEventKind.SecurityAudit)));
        Assert.Throws<UnauthorizedAccessException>(() => access.DemandRawAccess());

        _users.Set("one", "audit:read");
        Assert.Equal(LogEventKind.SecurityAudit, Assert.Single(access.Query(_directory,
            new(EventKind: LogEventKind.SecurityAudit, IncludeTaskEvents: true)).Entries).EventKind);
        Assert.Throws<UnauthorizedAccessException>(() => access.Query(_directory, new()));
        Assert.Throws<UnauthorizedAccessException>(() => new LogAccessService(authenticationEnabled: true)
            .Query(Path.Combine(_directory, "missing"), new()));
        _users.CurrentUser = RemoteUserContext.Anonymous;
        Assert.False(access.CanRead(LogEventKind.SecurityAudit));
    }

    [Fact]
    public async Task Cleanup_requires_all_grants_and_production_confirmation_and_records_result()
    {
        var path = WriteMixed();
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        _users.Set("one", "logs:read", "settings:manage");
        var permissions = new PermissionService(_users, _audit);
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, _users, _audit),
            _audit, authenticationEnabled: true, production: true);
        var access = new LogAccessService(permissions, _users, true, operations);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.CleanOldAsync(_directory, TimeSpan.FromDays(30)));
        Assert.True(File.Exists(path));
        _users.Set("one", "logs:read", "audit:read", "settings:manage");
        operations.ConfirmProductionAsync = () => Task.FromResult(false);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => access.CleanOldAsync(_directory, TimeSpan.FromDays(30)));
        Assert.True(File.Exists(path));
        Assert.Contains(_audit.Events, e => e.Operation == "cleanup" && e.Result == "cancelled");
        operations.ConfirmProductionAsync = () => Task.FromResult(true);
        Assert.Equal(1, await access.CleanOldAsync(_directory, TimeSpan.FromDays(30)));
        Assert.False(File.Exists(path));
        Assert.Contains(_audit.Events, e => e.Operation == "cleanup" && e.Result == "success");
    }

    [Fact]
    public async Task Account_change_during_cleanup_confirmation_prevents_deletion()
    {
        var path = WriteMixed();
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        _users.Set("one", "logs:read", "audit:read", "settings:manage");
        var permissions = new PermissionService(_users, _audit);
        var operations = new SensitiveOperationService(new AuthorizedOperation(permissions, _users, _audit),
            _audit, true, true)
        {
            ConfirmProductionAsync = () =>
            {
                _users.Set("two", "logs:read", "audit:read", "settings:manage");
                return Task.FromResult(true);
            },
        };
        var access = new LogAccessService(permissions, _users, true, operations);
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => access.CleanOldAsync(_directory, TimeSpan.FromDays(30)));
        Assert.True(File.Exists(path));
    }

    [Fact]
    public void Folder_entry_rejects_logs_and_their_parent_without_raw_grants()
    {
        _users.Set("one", "logs:read");
        var folders = new FolderService(NullLogger<FolderService>.Instance, Access());
        Assert.False(folders.TryOpen(AppPaths.LogDirectory));
        Assert.False(folders.TryOpen(Path.GetDirectoryName(AppPaths.LogDirectory)!));
        Assert.False(folders.TryOpen(Path.Combine(AppPaths.LogDirectory, "child")));
        Assert.Equal(3, _audit.Events.Count(e => e.Result == "denied"));
    }

    [Fact]
    public async Task Diagnostic_preview_and_export_recheck_grants_and_account_and_exclude_audit()
    {
        WriteMixed();
        var diagnostics = new DiagnosticService(Access());
        _users.Set("one", "logs:read");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.PreviewAsync(_directory, new(new())));
        _users.Set("one", "diagnostics:export");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.PreviewAsync(_directory, new(new())));
        var runtime = await diagnostics.PreviewAsync(_directory, new(new(), IncludeLogs: false));
        Assert.Single(runtime.Files);
        _users.Set("one", "logs:read", "diagnostics:export");
        var preview = await diagnostics.PreviewAsync(_directory, new(new()));
        var target = Path.Combine(_directory, "bundle.zip");
        _users.Set("one", "diagnostics:export");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.ExportAsync(preview.Id, true, target));
        _users.Set("two", "logs:read", "diagnostics:export");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.ExportAsync(preview.Id, true, target));
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        _users.Set("one", "logs:read", "diagnostics:export", "audit:read");
        await diagnostics.ExportAsync(preview.Id, true, target);
        using var archive = ZipFile.OpenRead(target);
        using var reader = new StreamReader(archive.GetEntry("logs.ndjson")!.Open());
        var lines = (await reader.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, lines.Length);
        Assert.DoesNotContain(lines, line => line.Contains("2026-11-01T10:00:00"));
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.PreviewAsync(_directory,
            new(new(EventKind: LogEventKind.SecurityAudit))));
    }

    [AvaloniaFact]
    public async Task Log_page_clears_stale_details_and_copy_on_revocation_and_account_change()
    {
        var id = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(AppPaths.LogDirectory);
        var path = Path.Combine(AppPaths.LogDirectory, $"access-{id}.ndjson");
        await File.WriteAllTextAsync(path, JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["@t"] = DateTimeOffset.UtcNow,
            ["@mt"] = "Context",
            ["CorrelationId"] = id,
            ["EventKind"] = "SecurityAudit",
        }));
        var access = Access();
        _users.Set("one", "audit:read");
        var vm = new LogViewModel(new FolderService(NullLogger<FolderService>.Instance, access), access);
        try
        {
            await vm.OpenQueryAsync(new(CorrelationId: id, EventKind: LogEventKind.SecurityAudit));
            vm.SelectedEntry = Assert.Single(vm.Entries);
            Assert.Equal(id, vm.GetCopyText(true));
            _users.Set("one");
            Assert.Null(vm.GetCopyText(false));
            Assert.Empty(vm.Entries);
            Assert.Null(vm.SelectedEntry);
            _users.Set("two", "audit:read");
            var loading = vm.LoadAsync();
            vm.RefreshAccess(); // A legitimate new-account query must not be cancelled by the timer.
            await loading;
            vm.SelectedEntry = Assert.Single(vm.Entries);
            _users.Set("three", "audit:read");
            vm.RefreshAccess();
            Assert.Empty(vm.Entries);
            Assert.Null(vm.GetCopyText(false));
            await vm.OpenQueryAsync(new(CorrelationId: id));
            Assert.Empty(vm.Entries);
        }
        finally
        {
            vm.PageClosed();
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Revocation_at_final_archive_check_removes_temporary_file()
    {
        WriteMixed();
        _users.Set("one", "logs:read", "diagnostics:export");
        var permissions = new ObservedPermissions(new PermissionService(_users, _audit));
        var diagnostics = new DiagnosticService(new LogAccessService(permissions, _users, true));
        var preview = await diagnostics.PreviewAsync(_directory, new(new()));
        var exportChecks = 0;
        permissions.BeforeDemand = permission =>
        {
            if (permission == "diagnostics:export" && ++exportChecks == 2)
                _users.Set("one", "diagnostics:export");
        };
        var target = Path.Combine(_directory, "revoked.zip");
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => diagnostics.ExportAsync(preview.Id, true, target));
        Assert.Equal(2, exportChecks);
        Assert.False(File.Exists(target));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public void Cleanup_guard_failure_is_not_swallowed_as_a_file_permission_error()
    {
        var path = WriteMixed();
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddDays(-60));
        Assert.Throws<UnauthorizedAccessException>(() => LogReader.DeleteOlderThan(_directory,
            TimeSpan.FromDays(30), () => throw new UnauthorizedAccessException("revoked")));
        Assert.True(File.Exists(path));
    }

    private LogAccessService Access() => new(new PermissionService(_users, _audit), _users, true);

    private string WriteMixed()
    {
        var path = Path.Combine(_directory, "mixed.ndjson");
        File.WriteAllLines(path,
        [
            """{"@t":"2026-11-01T08:00:00Z","@mt":"Technical"}""",
            """{"@t":"2026-11-01T09:00:00Z","@mt":"Task","EventKind":"Task"}""",
            """{"@t":"2026-11-01T10:00:00Z","@mt":"SecurityAudit {Actor}","Actor":"private","EventKind":"SecurityAudit","TaskId":"task"}""",
        ]);
        return path;
    }

    private sealed class Users : IRemoteUserContext
    {
        public RemoteUserContext CurrentUser { get; set; } = RemoteUserContext.Anonymous;
        public void Set(string subject, params string[] grants) =>
            CurrentUser = new(subject, ImmutableHashSet<string>.Empty, grants.ToImmutableHashSet(StringComparer.Ordinal));
    }

    private sealed class Audit : ISecurityAudit
    {
        public List<AuditEvent> Events { get; } = [];
        public void Write(AuditEvent entry) => Events.Add(entry);
    }

    private sealed class ObservedPermissions(IPermissionService inner) : IPermissionService
    {
        public Action<string>? BeforeDemand { get; set; }
        public bool HasPermission(string permission) => inner.HasPermission(permission);
        public void Demand(string permission, string correlationId)
        {
            BeforeDemand?.Invoke(permission);
            inner.Demand(permission, correlationId);
        }
    }
}
