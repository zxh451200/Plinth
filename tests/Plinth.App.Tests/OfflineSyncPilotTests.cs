using Microsoft.EntityFrameworkCore;
using Plinth.App.Data;
using Plinth.App.Modules.OfflineSync;
using Xunit;

namespace Plinth.App.Tests;

public sealed class OfflineSyncPilotTests
{
    private static readonly PilotInput Local = new("C1", "offline edit", "local remark");
    private static readonly PilotInput Remote = new("C1", "server edit", "server remark");

    private sealed class Clock : TimeProvider
    {
        private DateTimeOffset now = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(int seconds) => now = now.AddSeconds(seconds);
    }

    private sealed class Transport : IPilotTransport
    {
        public List<PilotWrite> Writes { get; } = [];
        public Func<PilotWrite, CancellationToken, Task<PilotReply>> Handle { get; set; } =
            (write, _) => Task.FromResult(new PilotReply(PilotOutcome.Applied,
                new(write.RecordId, write.ExpectedVersion + 1, write.Value)));
        public Task<PilotReply> SendAsync(PilotWrite write, CancellationToken cancellationToken)
        {
            Writes.Add(write);
            return Handle(write, cancellationToken);
        }
    }

    private sealed class Store : IDisposable
    {
        private readonly string directory = Path.Combine(Path.GetTempPath(), "plinth-offline-" + Guid.NewGuid().ToString("N"));
        public Clock Clock { get; } = new();
        public Transport Transport { get; } = new();
        public Store() => Directory.CreateDirectory(directory);
        public OfflineSyncPilot Open(string scope = "test-api/account-a") => new(
            Path.Combine(directory, "offline-sync-pilot.db"), scope, Transport, Clock);
        public PilotDbContext Database() => new(PilotDatabaseOptions.Create(
            Path.Combine(directory, "offline-sync-pilot.db")));
        public void Dispose() => Directory.Delete(directory, recursive: true);
    }

    [Fact]
    public async Task Offline_draft_and_id_survive_restart_and_success_is_not_sent_again()
    {
        using var store = new Store();
        var first = store.Open();
        await first.InitializeAsync();
        var draft = await first.EditOfflineAsync(Guid.NewGuid(), 3, Local);
        Assert.Empty(store.Transport.Writes);
        var restarted = store.Open();
        await restarted.InitializeAsync();
        Assert.Equal(draft, await restarted.FindAsync(draft.OperationId));
        Assert.Equal(draft, Assert.Single(await restarted.ListAsync()));
        Assert.True(await restarted.SyncOneAsync());
        var write = Assert.Single(store.Transport.Writes);
        Assert.Equal(draft.OperationId, write.OperationId);
        Assert.Equal(3, write.ExpectedVersion);
        Assert.Equal(Local, write.Value);
        Assert.Equal(PilotState.Succeeded, (await store.Open().FindAsync(draft.OperationId))!.State);
        Assert.False(await store.Open().SyncOneAsync());
    }

    [Fact]
    public async Task Editing_pending_draft_is_atomic_retains_history_and_rejects_stale_editor()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var old = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        await Assert.ThrowsAsync<InvalidOperationException>(() => pilot.EditOfflineAsync(old.RecordId, 1, Remote));
        var next = await pilot.EditOfflineAsync(old.RecordId, 1, Remote, old.OperationId);
        Assert.NotEqual(old.OperationId, next.OperationId);
        var retained = (await pilot.FindAsync(old.OperationId))!;
        Assert.Equal(PilotState.Resolved, retained.State);
        Assert.Equal(Local, retained.Local);
        Assert.Equal(next.OperationId, retained.SuccessorId);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pilot.EditOfflineAsync(old.RecordId, 1, Local, old.OperationId));
        Assert.Equal(2, (await pilot.ListAsync()).Count);
        await pilot.SyncOneAsync();
        Assert.Equal(next.OperationId, Assert.Single(store.Transport.Writes).OperationId);
    }

    [Fact]
    public async Task Confirmed_noncommit_has_persisted_bounded_backoff_and_stable_operation_id()
    {
        using var store = new Store();
        store.Transport.Handle = (_, _) => Task.FromResult(new PilotReply(PilotOutcome.DefinitelyNotAppliedTransient));
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        int[] delays = [1, 2, 4, 8];
        for (var attempt = 1; attempt <= OfflineSyncPilot.MaxAttempts; attempt++)
        {
            Assert.True(await pilot.SyncOneAsync());
            var current = (await pilot.FindAsync(draft.OperationId))!;
            Assert.Equal(attempt, current.Attempts);
            pilot = store.Open();
            await pilot.InitializeAsync();
            Assert.False(await pilot.SyncOneAsync());
            if (attempt < OfflineSyncPilot.MaxAttempts)
            {
                Assert.Equal(PilotState.Pending, current.State);
                Assert.Equal(store.Clock.GetUtcNow().UtcDateTime.AddSeconds(delays[attempt - 1]), current.NextAttemptUtc);
                store.Clock.Advance(delays[attempt - 1]);
            }
            else Assert.Equal(PilotState.Exhausted, current.State);
        }
        store.Clock.Advance(86400);
        Assert.False(await pilot.SyncOneAsync());
        Assert.Equal(5, store.Transport.Writes.Count);
        Assert.All(store.Transport.Writes, write => Assert.Equal(draft.OperationId, write.OperationId));
        await pilot.DiscardFailedAsync(draft.OperationId);
        Assert.Equal("discarded-confirmed-failure", (await pilot.FindAsync(draft.OperationId))!.Resolution);
    }

    [Fact]
    public async Task Committed_but_response_lost_is_never_retried_without_idempotency_confirmation()
    {
        using var store = new Store();
        PilotRecord? committed = null;
        store.Transport.Handle = async (write, _) =>
        {
            // Independently reopen the DB while SendAsync is running: claim is already durable.
            Assert.Equal(PilotState.Uncertain, (await store.Open().FindAsync(write.OperationId))!.State);
            committed = new(write.RecordId, 2, write.Value);
            throw new HttpRequestException("response lost after commit");
        };
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        await Assert.ThrowsAsync<HttpRequestException>(() => pilot.SyncOneAsync());
        pilot = store.Open();
        await pilot.InitializeAsync();
        store.Clock.Advance(86400);
        Assert.False(await pilot.SyncOneAsync());
        Assert.Equal(PilotState.Uncertain, (await pilot.FindAsync(draft.OperationId))!.State);
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pilot.EditOfflineAsync(draft.RecordId, 1, Remote, draft.OperationId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => pilot.DiscardFailedAsync(draft.OperationId));
        await Assert.ThrowsAsync<ArgumentException>(() => pilot.ReconcileAsync(draft.OperationId,
            new(PilotOutcome.Unknown), "lookup-not-found-is-not-proof"));
        await pilot.ReconcileAsync(draft.OperationId, new(PilotOutcome.Applied, committed), "verified-receipt-1");
        Assert.Equal(PilotState.Succeeded, (await pilot.FindAsync(draft.OperationId))!.State);
        Assert.False(await pilot.SyncOneAsync());
        Assert.Single(store.Transport.Writes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Unknown_or_invalid_success_stays_uncertain_after_restart(bool invalidSuccess)
    {
        using var store = new Store();
        store.Transport.Handle = (write, _) => Task.FromResult(invalidSuccess
            ? new PilotReply(PilotOutcome.Applied, new(write.RecordId, write.ExpectedVersion, Local))
            : new PilotReply(PilotOutcome.Unknown));
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        Assert.True(await pilot.SyncOneAsync());
        var restarted = store.Open();
        await restarted.InitializeAsync();
        Assert.False(await restarted.SyncOneAsync());
        Assert.Equal(PilotState.Uncertain, (await restarted.FindAsync(draft.OperationId))!.State);
        // Only explicit authoritative noncommit reconciliation may enable another attempt.
        await restarted.ReconcileAsync(draft.OperationId,
            new(PilotOutcome.DefinitelyNotAppliedTransient), "terminal-noncommit-receipt");
        Assert.False(await restarted.SyncOneAsync());
        store.Clock.Advance(1);
        Assert.True(await restarted.SyncOneAsync());
        Assert.All(store.Transport.Writes, write => Assert.Equal(draft.OperationId, write.OperationId));
    }

    [Fact]
    public async Task Conflict_keeps_both_snapshots_and_explicit_merge_uses_server_version_and_new_id()
    {
        using var store = new Store();
        store.Transport.Handle = (write, _) => Task.FromResult(new PilotReply(PilotOutcome.Conflict,
            new(write.RecordId, 9, Remote)));
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 2, Local);
        await pilot.SyncOneAsync();
        pilot = store.Open();
        var conflict = (await pilot.FindAsync(draft.OperationId))!;
        Assert.Equal(PilotState.Conflict, conflict.State);
        Assert.Equal(Local, conflict.Local);
        Assert.Equal(Remote, conflict.Server!.Value);
        Assert.False(await pilot.SyncOneAsync());
        var merged = (await pilot.ResolveConflictAsync(draft.OperationId, Local with { Remark = "merged" }))!;
        Assert.Equal(9, merged.ExpectedVersion);
        Assert.NotEqual(draft.OperationId, merged.OperationId);
        Assert.Equal(Local, (await pilot.FindAsync(draft.OperationId))!.Local);
        Assert.Equal(Remote, (await pilot.FindAsync(draft.OperationId))!.Server!.Value);
        store.Transport.Handle = (write, _) => Task.FromResult(new PilotReply(PilotOutcome.Applied,
            new(write.RecordId, 10, write.Value)));
        await pilot.SyncOneAsync();
        Assert.Equal(9, store.Transport.Writes.Last().ExpectedVersion);
        Assert.Equal(PilotState.Succeeded, (await pilot.FindAsync(merged.OperationId))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Accepting_server_or_deletion_does_not_send_a_write(bool deleted)
    {
        using var store = new Store();
        store.Transport.Handle = (write, _) => Task.FromResult(new PilotReply(PilotOutcome.Conflict,
            deleted ? null : new(write.RecordId, 3, Remote)));
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        await pilot.SyncOneAsync();
        if (deleted)
            await Assert.ThrowsAsync<InvalidOperationException>(() => pilot.ResolveConflictAsync(draft.OperationId, Local));
        Assert.Null(await pilot.ResolveConflictAsync(draft.OperationId, null));
        Assert.Equal(Local, (await pilot.FindAsync(draft.OperationId))!.Local);
        Assert.False(await pilot.SyncOneAsync());
        Assert.Single(store.Transport.Writes);
    }

    [Fact]
    public async Task Independent_workers_cannot_send_the_same_claimed_operation()
    {
        using var store = new Store();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Transport.Handle = async (write, _) =>
        {
            entered.SetResult();
            await release.Task;
            return new(PilotOutcome.Applied, new(write.RecordId, 2, Local));
        };
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        var first = pilot.SyncOneAsync();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(await store.Open().SyncOneAsync());
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                store.Open().EditOfflineAsync(draft.RecordId, 1, Remote, draft.OperationId));
        }
        finally { release.TrySetResult(); }
        Assert.True(await first);
        Assert.Single(store.Transport.Writes);
    }

    [Fact]
    public async Task Simultaneous_workers_atomically_claim_only_once()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sends = 0;
        store.Transport.Handle = async (write, _) =>
        {
            Interlocked.Increment(ref sends);
            entered.TrySetResult();
            await release.Task;
            return new(PilotOutcome.Applied, new(write.RecordId, 2, Local));
        };
        var workers = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            await start.Task;
            return await store.Open().SyncOneAsync();
        })).ToArray();
        start.SetResult();
        bool[] results;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(PilotState.Uncertain, (await store.Open().FindAsync(draft.OperationId))!.State);
        }
        finally
        {
            release.TrySetResult();
            results = await Task.WhenAll(workers).WaitAsync(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(1, results.Count(sent => sent));
        Assert.Equal(1, sends);
        Assert.Equal(PilotState.Succeeded, (await store.Open().FindAsync(draft.OperationId))!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Invalid_conflict_stays_uncertain_and_cannot_be_resolved_as_a_conflict(bool wrongId)
    {
        using var store = new Store();
        store.Transport.Handle = (write, _) => Task.FromResult(new PilotReply(PilotOutcome.Conflict,
            new(wrongId ? Guid.NewGuid() : write.RecordId, wrongId ? 2 : 0, Remote)));
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        Assert.True(await pilot.SyncOneAsync());
        pilot = store.Open();
        await pilot.InitializeAsync();
        Assert.Equal(PilotState.Uncertain, (await pilot.FindAsync(draft.OperationId))!.State);
        Assert.False(await pilot.SyncOneAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pilot.ResolveConflictAsync(draft.OperationId, null));
        await Assert.ThrowsAsync<ArgumentException>(() => pilot.ReconcileAsync(draft.OperationId,
            new(PilotOutcome.Conflict, new(Guid.NewGuid(), 2, Remote)), "invalid-receipt"));
        Assert.Equal(PilotState.Uncertain, (await pilot.FindAsync(draft.OperationId))!.State);
    }

    [Fact]
    public async Task Cancellation_before_send_does_not_claim_but_cancellation_inside_send_remains_uncertain()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pilot.SyncOneAsync(cancelled.Token));
        Assert.Equal(PilotState.Pending, (await pilot.FindAsync(draft.OperationId))!.State);
        store.Transport.Handle = (_, _) => throw new OperationCanceledException();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pilot.SyncOneAsync());
        Assert.Equal(PilotState.Uncertain, (await store.Open().FindAsync(draft.OperationId))!.State);
        Assert.False(await store.Open().SyncOneAsync());
    }

    [Fact]
    public async Task Account_and_destination_scope_isolation_and_business_model_boundary()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        var other = store.Open("other-api/account-b");
        Assert.Null(await other.FindAsync(draft.OperationId));
        Assert.Empty(await other.ListAsync());
        Assert.False(await other.SyncOneAsync());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => other.ReconcileAsync(draft.OperationId,
            new(PilotOutcome.Rejected), "receipt"));
        Assert.Empty(store.Transport.Writes);
        await using var business = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite("Data Source=:memory:").Options);
        Assert.DoesNotContain(business.Model.GetEntityTypes(), x => x.ClrType == typeof(PilotOutboxEntry));
    }

    [Fact]
    public async Task Failed_replacement_insert_rolls_back_retirement_of_old_draft()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        await using (var db = store.Database())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_insert BEFORE INSERT ON offline_pilot_outbox_v1 BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() =>
            pilot.EditOfflineAsync(draft.RecordId, 1, Remote, draft.OperationId));
        Assert.Equal(draft, await store.Open().FindAsync(draft.OperationId));
        Assert.Single(await pilot.ListAsync());
    }

    [Fact]
    public async Task Local_save_failure_after_remote_commit_preserves_uncertainty_and_blocks_replay()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        await using (var db = store.Database())
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER fail_result BEFORE UPDATE ON offline_pilot_outbox_v1 WHEN NEW.ServerJson IS NOT NULL BEGIN SELECT RAISE(ABORT, 'injected'); END;");
        await Assert.ThrowsAsync<DbUpdateException>(() => pilot.SyncOneAsync());
        pilot = store.Open();
        await pilot.InitializeAsync();
        Assert.Equal(PilotState.Uncertain, (await pilot.FindAsync(draft.OperationId))!.State);
        store.Clock.Advance(86400);
        Assert.False(await pilot.SyncOneAsync());
        Assert.Single(store.Transport.Writes);
        await using (var db = store.Database())
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER fail_result;");
        await pilot.ReconcileAsync(draft.OperationId,
            new(PilotOutcome.Applied, new(draft.RecordId, 2, Local)), "verified-after-local-failure");
        Assert.Equal(PilotState.Succeeded, (await pilot.FindAsync(draft.OperationId))!.State);
    }

    [Fact]
    public async Task Late_original_result_cannot_overwrite_authoritative_reconciliation()
    {
        using var store = new Store();
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Transport.Handle = async (_, _) =>
        {
            // Server has committed; only delivery of the original response is delayed.
            committed.SetResult();
            await release.Task;
            return new(PilotOutcome.Unknown);
        };
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        var original = pilot.SyncOneAsync();
        try
        {
            await committed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await store.Open().ReconcileAsync(draft.OperationId,
                new(PilotOutcome.Applied, new(draft.RecordId, 2, Local)), "authoritative-terminal-result");
        }
        finally { release.TrySetResult(); }
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => original);
        var final = (await store.Open().FindAsync(draft.OperationId))!;
        Assert.Equal(PilotState.Succeeded, final.State);
        Assert.Equal("authoritative-terminal-result", final.Resolution);
        Assert.False(await pilot.SyncOneAsync());
    }

    [Fact]
    public async Task Received_success_is_persisted_even_when_caller_cancels_before_local_save()
    {
        using var store = new Store();
        using var cancellation = new CancellationTokenSource();
        store.Transport.Handle = (write, _) =>
        {
            cancellation.Cancel();
            return Task.FromResult(new PilotReply(PilotOutcome.Applied, new(write.RecordId, 2, Local)));
        };
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        Assert.True(await pilot.SyncOneAsync(cancellation.Token));
        Assert.Equal(PilotState.Succeeded, (await store.Open().FindAsync(draft.OperationId))!.State);
        Assert.False(await store.Open().SyncOneAsync());
    }

    [Fact]
    public async Task Invalid_input_and_rejection_preserve_existing_draft()
    {
        using var store = new Store();
        var pilot = store.Open();
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        await Assert.ThrowsAsync<ArgumentException>(() => pilot.EditOfflineAsync(draft.RecordId, 1,
            Local with { Name = "\ud800" }, draft.OperationId));
        Assert.Equal(draft, await pilot.FindAsync(draft.OperationId));
        store.Transport.Handle = (_, _) => Task.FromResult(new PilotReply(PilotOutcome.Rejected));
        await pilot.SyncOneAsync();
        Assert.False(await pilot.SyncOneAsync());
        Assert.Equal(PilotState.Rejected, (await pilot.FindAsync(draft.OperationId))!.State);
        await pilot.DiscardFailedAsync(draft.OperationId);
        Assert.Equal(Local, (await pilot.FindAsync(draft.OperationId))!.Local);
    }
}
