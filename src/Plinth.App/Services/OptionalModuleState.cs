using System;
using System.Reflection;
using Plinth.App.ViewModels;
using Serilog;

namespace Plinth.App.Services;

/// <summary>
/// State for the host's single explicitly optional module. Metadata comes from the
/// composition root, never deployment input. This is not a registry or module loader.
/// </summary>
public sealed class OptionalModuleState(string moduleId, string moduleName, string pageId)
{
    public string ModuleId { get; } = moduleId;
    public string ModuleName { get; } = moduleName;
    public string PageId { get; } = pageId;
    public const string ErrorCode = "OPTIONAL_MODULE_FAILED";
    public string? CorrelationId { get; private set; }
    public string? Operation { get; private set; }
    public bool Failed => CorrelationId is not null;
    internal event Action? FailedChanged;

    internal void Fail(bool registration) => Fail(registration ? "Registration" : "PageConstruction");

    internal void FailViewConstruction() => Fail("ViewConstruction");

    private void Fail(string operation)
    {
        if (Failed) return;
        Operation = operation;
        CorrelationId = Guid.NewGuid().ToString("D");
        // Deliberate allowlist: never retain/log exception text, Data, URLs, credentials,
        // arbitrary exception types or paths (including inner exceptions).
        Log.Error("Optional module disabled. Module {Module}, ErrorCode {ErrorCode}, Operation {Operation}, CorrelationId {CorrelationId}",
            ModuleId, ErrorCode, Operation, CorrelationId);
        FailedChanged?.Invoke();
    }

    internal static bool IsRecoverable(Exception exception)
    {
        // Inspect reflection wrappers for filtering only; excluded failures propagate unchanged.
        while (exception is TargetInvocationException { InnerException: { } inner })
            exception = inner;
        return exception is not (OutOfMemoryException or OperationCanceledException);
    }

    internal ViewModelBase CreatePage(Action prepareCore, Func<ViewModelBase> createModulePage)
    {
        if (Failed) throw new OptionalModuleUnavailableException();
        // The composition root explicitly enumerates the module's shared dependencies.
        // Their lazy DI failures must propagate unchanged, outside the optional catch.
        prepareCore();
        try
        {
            var page = createModulePage();
            page.OptionalModule = this;
            return page;
        }
        catch (Exception ex) when (IsRecoverable(ex))
        {
            Fail(registration: false);
            throw new OptionalModuleUnavailableException();
        }
    }
}

// A safe signal only; do not attach the original exception as InnerException.
internal sealed class OptionalModuleUnavailableException : Exception
{
    public OptionalModuleUnavailableException() : base(OptionalModuleState.ErrorCode) { }
}
