using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Security;

/// <summary>
/// Business modules wrap sensitive use cases (export, restore, settings changes) with this service.
/// The callback receives the same correlation id that should be passed to the remote API.
/// </summary>
public sealed class AuthorizedOperation(
    IPermissionService permissions,
    IRemoteUserContext users,
    ISecurityAudit audit)
{
    internal Action CaptureSessionValidation()
    {
        var subject = users.CurrentUser.Subject;
        var version = users.SessionVersion;
        return () =>
        {
            if (!string.Equals(subject, users.CurrentUser.Subject, StringComparison.Ordinal)
                || version != users.SessionVersion)
                throw new UnauthorizedAccessException("security.permission_denied");
        };
    }

    public async Task<T> ExecuteAsync<T>(string permission, string module, string operation,
        Func<string, CancellationToken, Task<T>> action, CancellationToken cancellationToken = default,
        Func<T, bool>? succeeded = null, Func<Task<bool>>? confirm = null,
        Action? validateSession = null)
    {
        if (!IsIdentifier(module) || !IsIdentifier(operation))
            throw new ArgumentException("security.invalid_operation");
        var correlationId = Guid.NewGuid().ToString("N");
        var actor = users.CurrentUser.Subject;
        var sessionVersion = confirm is not null ? users.SessionVersion : 0;
        var result = "failed";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            validateSession?.Invoke();
            permissions.Demand(permission, correlationId);
            if (confirm is not null)
            {
                if (!await confirm()) throw new OperationCanceledException(cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                if (!string.Equals(actor, users.CurrentUser.Subject, StringComparison.Ordinal)
                    || sessionVersion != users.SessionVersion)
                    throw new UnauthorizedAccessException("security.permission_denied");
                validateSession?.Invoke();
                permissions.Demand(permission, correlationId);
            }
            var value = await action(correlationId, cancellationToken);
            result = succeeded is null || succeeded(value) ? "success" : "rejected";
            return value;
        }
        catch (UnauthorizedAccessException)
        {
            result = "denied";
            throw;
        }
        catch (OperationCanceledException)
        {
            result = "cancelled";
            throw;
        }
        finally
        {
            audit.Write(new AuditEvent(DateTimeOffset.UtcNow, actor, module, operation, result, correlationId));
        }
    }

    private static bool IsIdentifier(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');
}
