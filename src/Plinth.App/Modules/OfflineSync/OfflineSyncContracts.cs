namespace Plinth.App.Modules.OfflineSync;

public sealed record PilotInput(string Code, string Name, string Remark);
public sealed record PilotRecord(Guid Id, int Version, PilotInput Value);

public enum PilotState { Pending, Uncertain, Conflict, Succeeded, Exhausted, Rejected, Resolved }
public enum PilotOutcome { Applied, Conflict, DefinitelyNotAppliedTransient, Rejected, Unknown }

/// <summary>
/// DefinitelyNotAppliedTransient requires positive evidence that no write committed.
/// Rejected likewise confirms noncommit, but is a terminal failure with no retry.
/// A timeout, cancellation, malformed response, HTTP 5xx or missing lookup result is Unknown.
/// Conflict contains the authoritative current record, or null for a deleted record.
/// </summary>
public sealed record PilotReply(PilotOutcome Outcome, PilotRecord? Server = null);

public sealed record PilotWrite(Guid OperationId, string Scope, Guid RecordId, int ExpectedVersion, PilotInput Value);

/// <summary>
/// Pilot-only boundary. No production HTTP adapter is registered.
/// Implementations must validate the destination, actor/session, permission and confirmation
/// on every attempt, atomically compare ExpectedVersion, and disable hidden write retries.
/// OperationId is stable, but is NOT proof of server-side idempotency.
/// </summary>
public interface IPilotTransport
{
    Task<PilotReply> SendAsync(PilotWrite write, CancellationToken cancellationToken);
}

public sealed record PilotOperation(
    Guid OperationId, Guid RecordId, int ExpectedVersion, PilotInput Local,
    PilotState State, int Attempts, DateTime NextAttemptUtc, PilotRecord? Server,
    Guid? SuccessorId, string? Resolution);
