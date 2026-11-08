using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Plinth.App.Data;

namespace Plinth.App.Modules.OfflineSync;

/// <summary>Explicitly invoked update-only pilot; no production registration or HTTP adapter.</summary>
public sealed class OfflineSyncPilot
{
    public const int MaxAttempts = 5;
    private readonly DbContextOptions<PilotDbContext> options;
    private readonly string scope;
    private readonly IPilotTransport transport;
    private readonly TimeProvider clock;

    public OfflineSyncPilot(string databasePath, string scope, IPilotTransport transport, TimeProvider? clock = null)
    {
        if (!Path.IsPathFullyQualified(databasePath) || Path.GetFileName(databasePath) != "offline-sync-pilot.db")
            throw new ArgumentException("Use an absolute path to offline-sync-pilot.db.", nameof(databasePath));
        if (string.IsNullOrWhiteSpace(scope) || scope.Length > 300)
            throw new ArgumentException("A destination and account scope is required.", nameof(scope));
        this.scope = scope;
        this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
        this.clock = clock ?? TimeProvider.System;
        options = PilotDatabaseOptions.Create(databasePath);
    }

    private PilotDbContext Open() => new(options);
    private long Now => clock.GetUtcNow().UtcDateTime.Ticks;

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var db = Open();
        await db.Database.EnsureCreatedAsync(token);
        // Never reset Uncertain on restart. No background work is started here.
    }

    /// <summary>Draft and outbox are one durable row. A changed payload gets a new operation ID.</summary>
    public async Task<PilotOperation> EditOfflineAsync(Guid recordId, int expectedVersion, PilotInput value,
        Guid? previousOperationId = null, CancellationToken token = default)
    {
        Validate(recordId, expectedVersion, value);
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var key = ActiveKey(recordId);
        var previous = await db.Outbox.SingleOrDefaultAsync(x => x.ActiveKey == key, token);
        if (previous is null ? previousOperationId is not null :
            previous.OperationId != previousOperationId || previous.State != PilotState.Pending
            || previous.ExpectedVersion != expectedVersion)
            throw new InvalidOperationException("Stale draft or unresolved write; resolve explicitly first.");
        var next = NewEntry(recordId, expectedVersion, value);
        if (previous is not null)
        {
            Retire(previous, "superseded", next.OperationId);
            await db.SaveChangesAsync(token);
        }
        db.Outbox.Add(next);
        await db.SaveChangesAsync(token);
        await transaction.CommitAsync(token);
        return Snapshot(next);
    }

    public async Task<PilotOperation?> FindAsync(Guid operationId, CancellationToken token = default)
    {
        await using var db = Open();
        var entry = await db.Outbox.AsNoTracking()
            .SingleOrDefaultAsync(x => x.Scope == scope && x.OperationId == operationId, token);
        return entry is null ? null : Snapshot(entry);
    }

    public async Task<IReadOnlyList<PilotOperation>> ListAsync(int skip = 0, int take = 50,
        CancellationToken token = default)
    {
        if (skip < 0 || take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        await using var db = Open();
        var rows = await db.Outbox.AsNoTracking().Where(x => x.Scope == scope)
            .OrderBy(x => x.CreatedTicks).ThenBy(x => x.OperationId).Skip(skip).Take(take).ToListAsync(token);
        return rows.Select(Snapshot).ToArray();
    }

    /// <summary>At most one due write per call. No unbounded retry loop.</summary>
    public async Task<bool> SyncOneAsync(CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        await using var db = Open();
        var now = Now;
        var entry = await db.Outbox.AsNoTracking()
            .Where(x => x.Scope == scope && x.State == PilotState.Pending && x.NextAttemptTicks <= now)
            .OrderBy(x => x.CreatedTicks).ThenBy(x => x.OperationId).FirstOrDefaultAsync(token);
        if (entry is null) return false;
        // Atomic claim across processes. Persist uncertainty BEFORE any network call.
        var changed = await db.Outbox.Where(x => x.OperationId == entry.OperationId
                && x.State == PilotState.Pending && x.Revision == entry.Revision)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(x => x.State, PilotState.Uncertain)
                .SetProperty(x => x.Attempts, x => x.Attempts + 1)
                .SetProperty(x => x.Revision, x => x.Revision + 1), token);
        if (changed == 0) return false;
        entry.State = PilotState.Uncertain;
        entry.Attempts++;
        entry.Revision++;
        token.ThrowIfCancellationRequested();
        // Any exception/cancellation or process crash leaves durable Uncertain.
        // Do not store exception text, credentials or untrusted error response bodies.
        var reply = await transport.SendAsync(new(entry.OperationId, scope, entry.RecordId,
            entry.ExpectedVersion, ReadInput(entry.LocalJson)), token);
        db.Attach(entry);
        ApplyReply(entry, reply);
        entry.Revision++;
        // A received outcome is saved even if cancellation arrived meanwhile.
        // Save failure leaves the previous durable Uncertain state.
        await db.SaveChangesAsync(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Caller supplies an authoritative TERMINAL result for this exact operation,
    /// including any still-running request. Missing lookup/timeout is not evidence.
    /// evidenceReference is a non-secret audit/receipt reference, not a response body.
    /// </summary>
    public async Task ReconcileAsync(Guid operationId, PilotReply authoritativeResult,
        string evidenceReference, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(evidenceReference) || evidenceReference.Length > 200
            || authoritativeResult.Outcome == PilotOutcome.Unknown)
            throw new ArgumentException("An authoritative terminal result and evidence reference are required.");
        await using var db = Open();
        var entry = await RequiredAsync(db, operationId, token);
        if (entry.State != PilotState.Uncertain) throw new InvalidOperationException("Write is not uncertain.");
        ApplyReply(entry, authoritativeResult);
        if (entry.State == PilotState.Uncertain) throw new ArgumentException("Invalid authoritative result.");
        entry.Resolution = evidenceReference;
        entry.Revision++;
        await db.SaveChangesAsync(token);
    }

    /// <summary>Null accepts server/deletion. A merge gets a new ID and the conflict's server version.</summary>
    public async Task<PilotOperation?> ResolveConflictAsync(Guid operationId, PilotInput? merged,
        CancellationToken token = default)
    {
        await using var db = Open();
        await using var transaction = await db.Database.BeginTransactionAsync(token);
        var entry = await RequiredAsync(db, operationId, token);
        if (entry.State != PilotState.Conflict) throw new InvalidOperationException("Write is not conflicted.");
        PilotOutboxEntry? next = null;
        if (merged is not null)
        {
            var server = ReadServer(entry.ServerJson)
                ?? throw new InvalidOperationException("Deleted records cannot be updated; accept deletion explicitly.");
            Validate(entry.RecordId, server.Version, merged);
            next = NewEntry(entry.RecordId, server.Version, merged);
        }
        Retire(entry, merged is null ? "accepted-server" : "merged", next?.OperationId);
        await db.SaveChangesAsync(token);
        if (next is not null)
        {
            db.Outbox.Add(next);
            await db.SaveChangesAsync(token);
        }
        await transaction.CommitAsync(token);
        return next is null ? null : Snapshot(next);
    }

    public async Task DiscardFailedAsync(Guid operationId, CancellationToken token = default)
    {
        await using var db = Open();
        var entry = await RequiredAsync(db, operationId, token);
        if (entry.State is not (PilotState.Exhausted or PilotState.Rejected))
            throw new InvalidOperationException("Only confirmed failed writes may be discarded.");
        Retire(entry, "discarded-confirmed-failure");
        await db.SaveChangesAsync(token);
    }

    private async Task<PilotOutboxEntry> RequiredAsync(PilotDbContext db, Guid id, CancellationToken token) =>
        await db.Outbox.SingleOrDefaultAsync(x => x.OperationId == id && x.Scope == scope, token)
        ?? throw new KeyNotFoundException("Unknown scoped operation.");

    private void ApplyReply(PilotOutboxEntry entry, PilotReply? reply)
    {
        if (reply is null || !Enum.IsDefined(reply.Outcome)) return;
        if (reply.Outcome is PilotOutcome.Applied or PilotOutcome.Conflict)
        {
            if (reply.Server is { } server && (!Valid(server.Value) || server.Id != entry.RecordId
                || server.Version <= 0)) return;
            if (reply.Outcome == PilotOutcome.Applied
                && (reply.Server is null || reply.Server.Version <= entry.ExpectedVersion)) return;
        }
        switch (reply.Outcome)
        {
            case PilotOutcome.Applied:
                entry.State = PilotState.Succeeded;
                entry.ActiveKey = null;
                entry.ServerJson = JsonSerializer.Serialize(reply.Server);
                break;
            case PilotOutcome.Conflict:
                entry.State = PilotState.Conflict;
                entry.ServerJson = reply.Server is null ? null : JsonSerializer.Serialize(reply.Server);
                break;
            case PilotOutcome.DefinitelyNotAppliedTransient:
                entry.State = entry.Attempts >= MaxAttempts ? PilotState.Exhausted : PilotState.Pending;
                entry.NextAttemptTicks = Now + TimeSpan.FromSeconds(Math.Min(8, 1 << Math.Min(entry.Attempts - 1, 3))).Ticks;
                break;
            case PilotOutcome.Rejected:
                entry.State = PilotState.Rejected;
                break;
        }
    }

    private string ActiveKey(Guid id) => scope + "/" + id.ToString("D");
    private PilotOutboxEntry NewEntry(Guid id, int version, PilotInput value) => new()
    {
        OperationId = Guid.NewGuid(),
        Scope = scope,
        RecordId = id,
        ExpectedVersion = version,
        LocalJson = JsonSerializer.Serialize(value),
        ActiveKey = ActiveKey(id),
        State = PilotState.Pending,
        CreatedTicks = Now,
        NextAttemptTicks = Now,
        Revision = 1
    };

    private static void Retire(PilotOutboxEntry entry, string resolution, Guid? successorId = null)
    {
        entry.State = PilotState.Resolved;
        entry.ActiveKey = null;
        entry.Resolution = resolution;
        entry.SuccessorId = successorId;
        entry.Revision++;
    }

    private static PilotInput ReadInput(string json) => JsonSerializer.Deserialize<PilotInput>(json)
        ?? throw new InvalidDataException("Invalid persisted pilot input.");
    private static PilotRecord? ReadServer(string? json) =>
        json is null ? null : JsonSerializer.Deserialize<PilotRecord>(json);
    private static PilotOperation Snapshot(PilotOutboxEntry entry) => new(entry.OperationId,
        entry.RecordId, entry.ExpectedVersion, ReadInput(entry.LocalJson), entry.State, entry.Attempts,
        new DateTime(entry.NextAttemptTicks, DateTimeKind.Utc), ReadServer(entry.ServerJson),
        entry.SuccessorId, entry.Resolution);

    internal static void Validate(Guid id, int version, PilotInput value)
    {
        if (id == Guid.Empty || version <= 0 || !Valid(value)) throw new ArgumentException("Invalid pilot update.");
    }
    private static bool Valid(PilotInput? value) => value is not null
        && Text(value.Code, 50, true) && Text(value.Name, 200, true) && Text(value.Remark, 500, false);
    private static bool Text(string? value, int max, bool required)
    {
        if (value is null || value.Length > max || (required && string.IsNullOrWhiteSpace(value))) return false;
        for (var i = 0; i < value.Length; i++)
        {
            if (char.IsHighSurrogate(value[i]))
            {
                if (++i >= value.Length || !char.IsLowSurrogate(value[i])) return false;
            }
            else if (char.IsLowSurrogate(value[i]) || value[i] == '\0') return false;
        }
        return true;
    }
}
