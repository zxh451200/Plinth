using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Security.Claims;
using Microsoft.Extensions.Logging;

namespace Plinth.App.Security;

public sealed record RemoteUserContext(
    string Subject,
    ImmutableHashSet<string> Roles,
    ImmutableHashSet<string> Permissions)
{
    public static RemoteUserContext Anonymous { get; } = new("",
        ImmutableHashSet<string>.Empty, ImmutableHashSet<string>.Empty);

    public bool IsAuthenticated => Subject.Length != 0;

    public static RemoteUserContext FromPrincipal(ClaimsPrincipal? user, string roleClaim, string permissionClaim)
    {
        var subject = user?.FindFirst("sub")?.Value;
        if (user?.Identity?.IsAuthenticated != true || string.IsNullOrWhiteSpace(subject))
            return Anonymous;
        return new RemoteUserContext(subject,
            user.FindAll(roleClaim).Select(c => c.Value).ToImmutableHashSet(StringComparer.Ordinal),
            user.FindAll(permissionClaim).Select(c => c.Value).ToImmutableHashSet(StringComparer.Ordinal));
    }
}

public interface IRemoteUserContext
{
    RemoteUserContext CurrentUser { get; }
    /// <summary>登录／注销边界递增；普通令牌刷新不改变会话版本。</summary>
    long SessionVersion => 0;
}

public sealed record AuditEvent(DateTimeOffset Timestamp, string Actor, string Module, string Operation,
    string Result, string CorrelationId);

public interface ISecurityAudit
{
    void Write(AuditEvent entry);
}

/// <summary>Dedicated category/event id; retention, access control and server-side auditing belong to the host.</summary>
public sealed class SecurityAudit(ILogger<SecurityAudit> logger) : ISecurityAudit
{
    public void Write(AuditEvent entry)
    {
        using var scope = logger.BeginScope(new Dictionary<string, object> { ["EventKind"] = "SecurityAudit" });
        logger.LogInformation(new EventId(4100, "SecurityAudit"),
            "SecurityAudit {Timestamp} {Actor} {Module} {Operation} {Result} {CorrelationId}",
            entry.Timestamp, entry.Actor, entry.Module, entry.Operation, entry.Result, entry.CorrelationId);
    }
}

public interface IPermissionService
{
    bool HasPermission(string permission);
    void Demand(string permission, string correlationId);
}

/// <summary>UI/use-case checks only. The API must independently authorize every operation.</summary>
public sealed class PermissionService : IPermissionService
{
    private readonly IRemoteUserContext _users;
    private readonly ISecurityAudit _audit;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _rolePermissions;

    public PermissionService(IRemoteUserContext users, ISecurityAudit audit,
        IReadOnlyDictionary<string, string[]>? rolePermissions = null)
    {
        _users = users;
        _audit = audit;
        // Snapshot a trusted deployment mapping; users cannot grant their own roles/permissions.
        _rolePermissions = (rolePermissions ?? new Dictionary<string, string[]>())
            .ToImmutableDictionary(x => x.Key,
                x => x.Value.ToImmutableHashSet(StringComparer.Ordinal), StringComparer.Ordinal);
    }

    public bool HasPermission(string permission)
    {
        var user = _users.CurrentUser;
        return IsPermissionId(permission) && user.IsAuthenticated
            && (user.Permissions.Contains(permission)
                || user.Roles.Any(r => _rolePermissions.TryGetValue(r, out var grants) && grants.Contains(permission)));
    }

    public void Demand(string permission, string correlationId)
    {
        if (HasPermission(permission)) return;
        _audit.Write(new AuditEvent(DateTimeOffset.UtcNow, _users.CurrentUser.Subject,
            "authorization", IsPermissionId(permission) ? permission : "invalid_permission",
            "denied", correlationId));
        throw new UnauthorizedAccessException("security.permission_denied");
    }

    private static bool IsPermissionId(string value) =>
        !string.IsNullOrWhiteSpace(value) && value.Length <= 128 && value.Contains(':')
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is ':' or '.' or '_' or '-');
}
