using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Security;

namespace Plinth.App.Services;

/// <summary>应用内日志用例入口；底层 LogReader 只负责解析，不承担本机文件安全隔离。</summary>
public sealed class LogAccessService(IPermissionService? permissions = null, IRemoteUserContext? users = null,
    bool? authenticationEnabled = null, SensitiveOperationService? operations = null)
{
    private readonly SensitiveOperationService _operations = operations
        ?? SensitiveOperationService.CreateDefault(NullLogger<LogAccessService>.Instance);
    private bool AuthenticationEnabled => authenticationEnabled ?? DeploymentConfiguration.Current.Authentication.Enabled;
    public string CurrentSubject => AuthenticationEnabled ? users?.CurrentUser.Subject ?? "" : "";

    public bool CanRead(LogEventKind kind) => !AuthenticationEnabled
        || permissions?.HasPermission(PermissionFor(kind)) == true;

    private static string PermissionFor(LogEventKind kind) => kind switch
    {
        LogEventKind.Technical or LogEventKind.Task => "logs:read",
        LogEventKind.SecurityAudit => "audit:read",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public void Demand(string permission)
    {
        if (!AuthenticationEnabled) return;
        if (permissions is null) throw new UnauthorizedAccessException("security.permission_denied");
        permissions.Demand(permission, Guid.NewGuid().ToString("N"));
    }

    public void DemandRead(LogEventKind kind) => Demand(PermissionFor(kind));

    public void DemandRawAccess()
    {
        Demand("logs:read");
        Demand("audit:read");
    }

    public LogQueryResult Query(string directory, LogFilter filter, int limit = LogReader.DefaultMaxEntries,
        CancellationToken cancellationToken = default)
    {
        var kind = filter.EventKind ?? LogEventKind.Technical;
        DemandRead(kind);
        var subject = CurrentSubject;
        cancellationToken.ThrowIfCancellationRequested();
        var result = LogReader.Query(directory, filter with { EventKind = kind }, limit, cancellationToken);
        DemandRead(kind);
        if (subject != CurrentSubject) throw new UnauthorizedAccessException("security.subject_changed");
        return result;
    }

    public Task<int> CleanOldAsync(string directory, TimeSpan age)
    {
        DemandRawAccess();
        Demand("settings:manage");
        var subject = CurrentSubject;
        return _operations.ExecuteConfirmedAsync("settings:manage", "Logs", "cleanup", async token =>
        {
            DemandRawAccess();
            Demand("settings:manage");
            if (subject != CurrentSubject) throw new UnauthorizedAccessException("security.subject_changed");
            return await Task.Run(() =>
            {
                token.ThrowIfCancellationRequested();
                return LogReader.DeleteOlderThan(directory, age, () =>
                {
                    token.ThrowIfCancellationRequested();
                    DemandRawAccess();
                    Demand("settings:manage");
                    if (subject != CurrentSubject)
                        throw new UnauthorizedAccessException("security.subject_changed");
                });
            }, token);
        });
    }
}
