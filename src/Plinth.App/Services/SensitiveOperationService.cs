using System;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Security;
using Microsoft.Extensions.Logging;

namespace Plinth.App.Services;

public sealed class OfflineUserContext : IRemoteUserContext
{
    public RemoteUserContext CurrentUser => RemoteUserContext.Anonymous;
}

/// <summary>Offline operations remain local; configured identity deployments require explicit grants.</summary>
public sealed class SensitiveOperationService(AuthorizedOperation authorized, ISecurityAudit audit,
    bool? authenticationEnabled = null, bool? production = null)
{
    internal bool AuthenticationEnabled =>
        authenticationEnabled ?? DeploymentConfiguration.Current.Authentication.Enabled;
    internal Plinth.App.Auth.IIdentitySessionEvents? SessionEvents => authorized.SessionEvents;
    internal Action CaptureSnapshotValidation() => AuthenticationEnabled
        ? authorized.CaptureSnapshotValidation() : () => { };
    internal void Demand(string permission)
    {
        if (AuthenticationEnabled) authorized.Demand(permission);
    }

    public Func<Task<bool>>? ConfirmProductionAsync { get; set; }

    public async Task<bool> ConfirmAsync() => !(production ?? DeploymentConfiguration.Current.IsProduction)
        || (ConfirmProductionAsync is not null && await ConfirmProductionAsync());

    // Created on the caller's UI context; the queue only revalidates and executes.
    public async Task<Confirmation> PrepareConfirmedAsync(string permission, string module, string operation,
        CancellationToken token = default)
    {
        var validate = authorized.CaptureSessionValidation();
        await ExecuteCoreAsync(permission, module, operation + ".confirm",
            _ => Task.FromResult(true), token, null,
            ConfirmAsync, validate);
        return new Confirmation(this, permission, module, operation, validate);
    }

    public sealed class Confirmation
    {
        private readonly SensitiveOperationService _owner;
        private readonly string _permission;
        private readonly string _module;
        private readonly string _operation;
        private readonly Action _validate;

        internal Confirmation(SensitiveOperationService owner, string permission, string module,
            string operation, Action validate)
        {
            _owner = owner;
            _permission = permission;
            _module = module;
            _operation = operation;
            _validate = validate;
        }

        public Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> action,
            CancellationToken token = default, Func<T, bool>? succeeded = null)
            => _owner.ExecuteCoreAsync(_permission, _module, _operation, action, token, succeeded, null, _validate);
    }

    public Task<T> ExecuteAsync<T>(string permission, string module, string operation,
        Func<CancellationToken, Task<T>> action, CancellationToken token = default,
        Func<T, bool>? succeeded = null)
        => ExecuteCoreAsync(permission, module, operation, action, token, succeeded, null);

    public Task<T> ExecuteConfirmedAsync<T>(string permission, string module, string operation,
        Func<CancellationToken, Task<T>> action, CancellationToken token = default,
        Func<T, bool>? succeeded = null)
        => ExecuteCoreAsync(permission, module, operation, action, token, succeeded, ConfirmAsync);

    private async Task<T> ExecuteCoreAsync<T>(string permission, string module, string operation,
        Func<CancellationToken, Task<T>> action, CancellationToken token,
        Func<T, bool>? succeeded, Func<Task<bool>>? confirm, Action? validateSession = null)
    {
        if (authenticationEnabled ?? DeploymentConfiguration.Current.Authentication.Enabled)
            return await authorized.ExecuteAsync(permission, module, operation, (_, ct) => action(ct), token,
                succeeded, confirm, validateSession);
        var actor = Data.CurrentUser.Name;
        var result = "failed";
        try
        {
            token.ThrowIfCancellationRequested();
            validateSession?.Invoke();
            if (confirm is not null)
            {
                if (!await confirm()) throw new OperationCanceledException(token);
                token.ThrowIfCancellationRequested();
                validateSession?.Invoke();
            }
            var value = await action(token);
            result = succeeded is null || succeeded(value) ? "success" : "rejected";
            return value;
        }
        catch (UnauthorizedAccessException) { result = "denied"; throw; }
        catch (OperationCanceledException) { result = "cancelled"; throw; }
        finally
        {
            audit.Write(new AuditEvent(DateTimeOffset.UtcNow, actor, module, operation,
                result, Guid.NewGuid().ToString("N")));
        }
    }

    // Legacy direct construction stays offline-capable but fails closed when identity is enabled.
    public static SensitiveOperationService CreateDefault(ILogger logger)
    {
        var audit = new HostAudit(logger);
        var users = new OfflineUserContext();
        return new(new AuthorizedOperation(new PermissionService(users, audit), users, audit), audit);
    }

    private sealed class HostAudit(ILogger logger) : ISecurityAudit
    {
        public void Write(AuditEvent entry)
        {
            using var scope = logger.BeginScope(new System.Collections.Generic.Dictionary<string, object>
            { ["EventKind"] = "SecurityAudit" });
            logger.LogInformation(new EventId(4100, "SecurityAudit"),
                "SecurityAudit {Actor} {Module} {Operation} {Result} {CorrelationId}",
                entry.Actor, entry.Module, entry.Operation, entry.Result, entry.CorrelationId);
        }
    }
}
