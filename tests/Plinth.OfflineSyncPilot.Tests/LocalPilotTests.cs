using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Serialization;
using Plinth.App.Modules.OfflineSync;
using Plinth.OfflineSyncPilot;
using Xunit;
using StateMachine = Plinth.App.Modules.OfflineSync.OfflineSyncPilot;

namespace Plinth.OfflineSyncPilot.Tests;

public sealed class LocalPilotTests : IDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "plinth-local-sync-" + Guid.NewGuid().ToString("N"));
    private static readonly PilotInput Local = new("C1", "offline", "local");
    private static readonly PilotInput Remote = new("C1", "remote", "server");
    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter() }
    };

    public LocalPilotTests() => Directory.CreateDirectory(directory);
    public void Dispose() => Directory.Delete(directory, true);
    private LocalPilotTransport Server() => new(directory);
    private StateMachine Client(IPilotTransport? server = null) => new(
        Path.Combine(directory, "offline-sync-pilot.db"), Server().Scope, server ?? Server());

    [Fact]
    public async Task Offline_cli_does_not_open_server_and_bad_usage_has_no_side_effects()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, await PilotCli.RunAsync([directory, "sync", "anything"], output, error));
        Assert.Empty(Directory.GetFiles(directory));
        var id = Guid.NewGuid().ToString();
        Assert.Equal(0, await PilotCli.RunAsync([directory, "edit", id, "1", "C1", "offline", ""], output, error));
        Assert.False(File.Exists(Path.Combine(directory, "local-server.db")));
        Assert.Equal(PilotState.Pending, Assert.Single(await Client().ListAsync()).State);
    }

    [Fact]
    public async Task Local_authority_serializes_version_checks_and_binds_receipts_to_exact_payload()
    {
        var server = Server();
        await server.InitializeAsync();
        var id = Guid.NewGuid();
        await server.SeedAsync(id, Remote);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.SeedAsync(id, Local));
        var first = new PilotWrite(Guid.NewGuid(), server.Scope, id, 1, Local);
        var second = first with { OperationId = Guid.NewGuid(), Value = Remote };
        var replies = await Task.WhenAll(
            Task.Run(() => Server().SendAsync(first, default)),
            Task.Run(() => Server().SendAsync(second, default)));
        Assert.Single(replies.Where(x => x.Outcome == PilotOutcome.Applied));
        Assert.Single(replies.Where(x => x.Outcome == PilotOutcome.Conflict));
        Assert.Equal(2, (await server.ReadAsync(id))!.Version);
        Assert.Equal(replies[0], await Server().SendAsync(first, default));
        Assert.Equal(replies[0], await Server().LookupAsync(first));
        Assert.Equal(2, (await server.ReadAsync(id))!.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => server.LookupAsync(first with { Value = new("C2", "other", "") }));
        await Assert.ThrowsAsync<ArgumentException>(() => server.SendAsync(first with { Scope = "other" }, default));
    }

    [Fact]
    public async Task Missing_receipt_does_not_unlock_an_uncertain_operation()
    {
        var server = Server();
        await server.InitializeAsync();
        var pilot = Client(new FailingTransport());
        await pilot.InitializeAsync();
        var draft = await pilot.EditOfflineAsync(Guid.NewGuid(), 1, Local);
        // Failure before a receipt exists still cannot prove that a request was never committed.
        await Assert.ThrowsAsync<IOException>(() => pilot.SyncOneAsync());
        Assert.Null(await server.LookupAsync(new(draft.OperationId, server.Scope, draft.RecordId, 1, Local)));
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(1, await PilotCli.RunAsync(
            [directory, "reconcile", draft.OperationId.ToString()], output, error));
        Assert.Equal(PilotState.Uncertain, (await Client().FindAsync(draft.OperationId))!.State);
        Assert.False(await Client().SyncOneAsync());
    }

    private sealed class FailingTransport : IPilotTransport
    {
        public Task<PilotReply> SendAsync(PilotWrite write, CancellationToken cancellationToken) =>
            throw new IOException("No terminal receipt.");
    }

    [Fact]
    public async Task Accepting_a_real_local_conflict_preserves_both_snapshots_without_writing()
    {
        var server = Server();
        await server.InitializeAsync();
        var id = Guid.NewGuid();
        await server.SeedAsync(id, Remote);
        var client = Client();
        await client.InitializeAsync();
        var draft = await client.EditOfflineAsync(id, 1, Local);
        var changed = await server.RemoteEditAsync(id, 1, Remote with { Name = "new server" });
        await client.SyncOneAsync();
        Assert.Null(await Client().ResolveConflictAsync(draft.OperationId, null));
        var archived = (await Client().FindAsync(draft.OperationId))!;
        Assert.Equal(Local, archived.Local);
        Assert.Equal(changed, archived.Server);
        Assert.Equal("accepted-server", archived.Resolution);
        Assert.False(await Client().SyncOneAsync());
        Assert.Equal(changed, await server.ReadAsync(id));
    }

    [Fact]
    public async Task Separate_cli_processes_restore_send_conflict_merge_and_reconcile_lost_response()
    {
        var id = Guid.NewGuid().ToString();
        await Cli(0, "seed", id, "C1", "initial", "");
        var draft = Operation(await Cli(0, "edit", id, "1", "C1", "offline", "local"));
        Assert.Equal(draft, Operation(await Cli(0, "show", draft.OperationId.ToString())));
        await Cli(0, "remote-edit", id, "1", "C1", "remote", "server");
        await Cli(0, "sync");
        var conflict = Operation(await Cli(0, "show", draft.OperationId.ToString()));
        Assert.Equal(PilotState.Conflict, conflict.State);
        Assert.Equal("offline", conflict.Local.Name);
        Assert.Equal("remote", conflict.Server!.Value.Name);
        var merged = Operation(await Cli(0, "merge", draft.OperationId.ToString(), "C1", "merged", "explicit"));
        Assert.NotEqual(draft.OperationId, merged.OperationId);
        Assert.Equal(2, merged.ExpectedVersion);
        await Cli(1, "sync", "lose-response");
        var uncertain = Operation(await Cli(0, "show", merged.OperationId.ToString()));
        Assert.Equal(PilotState.Uncertain, uncertain.State);
        Assert.Equal(1, uncertain.Attempts);
        using (var noSend = JsonDocument.Parse(await Cli(0, "sync")))
            Assert.False(noSend.RootElement.GetProperty("Sent").GetBoolean());
        var resolved = Operation(await Cli(0, "reconcile", merged.OperationId.ToString()));
        Assert.Equal(PilotState.Succeeded, resolved.State);
        Assert.Equal(3, resolved.Server!.Version);
        Assert.Equal("merged", resolved.Server.Value.Name);
        using var server = JsonDocument.Parse(await Cli(0, "server", id));
        Assert.Equal(3, server.RootElement.GetProperty("Version").GetInt32());
    }

    private static PilotOperation Operation(string json) => JsonSerializer.Deserialize<PilotOperation>(json, Json)!;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Killed_sync_process_keeps_uncertainty_until_exact_terminal_receipt(bool committed)
    {
        var id = Guid.NewGuid().ToString();
        await Cli(0, "seed", id, "C1", "remote", "server");
        var draft = Operation(await Cli(0, "edit", id, "1", "C1", "offline", "local"));
        var pipeName = "plinth-sync-" + Guid.NewGuid().ToString("N");
        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1,
            PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var start = StartInfo(typeof(LocalPilotTests).Assembly.Location,
            "-method", typeof(ProcessTerminationFixture).FullName + ".Run");
        start.Environment[ProcessTerminationFixture.DirectoryVariable] = directory;
        start.Environment[ProcessTerminationFixture.PipeVariable] = pipeName;
        start.Environment[ProcessTerminationFixture.StageVariable] = committed ? "after-commit" : "before-send";
        await using (var child = new ChildProcess(start))
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            // A pipe handshake, not timing or a log line, proves which boundary was reached.
            await child.WaitForBoundaryAsync(pipe.WaitForConnectionAsync(deadline.Token));
            using var reader = new StreamReader(pipe, leaveOpen: true);
            var ready = await child.WaitForBoundaryAsync(reader.ReadLineAsync(deadline.Token).AsTask());
            Assert.Equal(draft.OperationId.ToString("D"), ready);
            Assert.False(child.HasExited);

            // Read using independent connections while the child cannot return from SendAsync.
            var uncertain = (await Client().FindAsync(draft.OperationId))!;
            Assert.Equal(draft with { State = PilotState.Uncertain, Attempts = 1 }, uncertain);
            var write = new PilotWrite(draft.OperationId, Server().Scope, draft.RecordId,
                draft.ExpectedVersion, draft.Local);
            var receipt = await Server().LookupAsync(write);
            if (committed)
            {
                Assert.Equal(new PilotReply(PilotOutcome.Applied, new(draft.RecordId, 2, Local)), receipt);
                Assert.Equal(Local, (await Server().ReadAsync(draft.RecordId))!.Value);
            }
            else
            {
                Assert.Null(receipt);
                Assert.Equal(Remote, (await Server().ReadAsync(draft.RecordId))!.Value);
            }
            Assert.Equal(committed ? 2 : 1, (await Server().ReadAsync(draft.RecordId))!.Version);
            await child.KillAsync();
            Assert.True(child.HasExited);
            Assert.NotEqual(0, (await child.CompleteAsync()).ExitCode);
        }

        // Every recovery command is a fresh OS process using the same files and scope.
        var restored = Operation(await Cli(0, "show", draft.OperationId.ToString()));
        Assert.Equal(draft with { State = PilotState.Uncertain, Attempts = 1 }, restored);
        using (var noSend = JsonDocument.Parse(await Cli(0, "sync")))
            Assert.False(noSend.RootElement.GetProperty("Sent").GetBoolean());
        await Cli(1, "edit", id, "1", "C1", "overwrite", "", draft.OperationId.ToString());
        await Cli(1, "discard", draft.OperationId.ToString());
        var recovered = await Cli(committed ? 0 : 1, "reconcile", draft.OperationId.ToString());
        if (committed)
        {
            var resolved = Operation(recovered);
            Assert.Equal(PilotState.Succeeded, resolved.State);
            Assert.Equal(draft.OperationId, resolved.OperationId);
            Assert.Equal(draft.Local, resolved.Local);
            Assert.Equal(1, resolved.Attempts);
            Assert.Equal(new PilotRecord(draft.RecordId, 2, Local), resolved.Server);
            Assert.Equal("local-receipt/" + draft.OperationId.ToString("D"), resolved.Resolution);
        }
        else
            Assert.Equal(restored, Operation(await Cli(0, "show", draft.OperationId.ToString())));
        Assert.Single(await Client().ListAsync());
        using (var noSend = JsonDocument.Parse(await Cli(0, "sync")))
            Assert.False(noSend.RootElement.GetProperty("Sent").GetBoolean());
        using var server = JsonDocument.Parse(await Cli(0, "server", id));
        Assert.Equal(committed ? 2 : 1, server.RootElement.GetProperty("Version").GetInt32());
    }

    private async Task<string> Cli(int exitCode, params string[] arguments)
    {
        await using var child = new ChildProcess(StartInfo(typeof(PilotCli).Assembly.Location,
            new[] { directory }.Concat(arguments).ToArray()));
        var (actualExit, text, errors) = await child.CompleteAsync();
        Assert.True(actualExit == exitCode, $"Exit {actualExit}, expected {exitCode}: {errors}\n{text}");
        return text;
    }

    private static ProcessStartInfo StartInfo(string assembly, params string[] arguments)
    {
        // Use this test project's dependency graph/runtime; each command is a fresh OS process.
        var testAssembly = typeof(LocalPilotTests).Assembly.Location;
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var arg in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            assembly
        }.Concat(arguments))
            start.ArgumentList.Add(arg);
        // Parent fixture activation must never leak into other child invocations.
        start.Environment.Remove(ProcessTerminationFixture.DirectoryVariable);
        start.Environment.Remove(ProcessTerminationFixture.PipeVariable);
        start.Environment.Remove(ProcessTerminationFixture.StageVariable);
        return start;
    }

    private sealed class ChildProcess : IAsyncDisposable
    {
        private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
        private readonly Process process;
        private readonly Task<string> output;
        private readonly Task<string> error;
        private readonly Task exited;

        public ChildProcess(ProcessStartInfo start)
        {
            process = Process.Start(start) ?? throw new InvalidOperationException("Child did not start.");
            output = process.StandardOutput.ReadToEndAsync();
            error = process.StandardError.ReadToEndAsync();
            exited = process.WaitForExitAsync();
        }

        public bool HasExited => process.HasExited;

        public async Task WaitForBoundaryAsync(Task boundary)
        {
            if (await Task.WhenAny(boundary, exited) == exited)
            {
                var (_, text, errors) = await CompleteAsync();
                throw new InvalidOperationException($"Child exited before boundary: {errors}\n{text}");
            }
            await boundary;
        }

        public async Task<T> WaitForBoundaryAsync<T>(Task<T> boundary)
        {
            await WaitForBoundaryAsync((Task)boundary);
            return await boundary;
        }

        public async Task<(int ExitCode, string Output, string Error)> CompleteAsync()
        {
            // Bound pipe drains too: waiting only for the process can still hang on inherited handles.
            await Task.WhenAll(exited, output, error).WaitAsync(Timeout);
            return (process.ExitCode, await output, await error);
        }

        public async Task KillAsync()
        {
            try { process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) when (process.HasExited) { }
            await CompleteAsync();
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!process.HasExited) await KillAsync();
                else await CompleteAsync();
            }
            finally { process.Dispose(); }
        }
    }
}

// Launched through the xUnit executable's generated entry point with an exact -method filter.
// This wrapper is compiled only into the test assembly; the CLI/state machine has no fault switch.
public sealed class ProcessTerminationFixture
{
    internal const string DirectoryVariable = "PLINTH_TEST_SYNC_DIRECTORY";
    internal const string PipeVariable = "PLINTH_TEST_SYNC_PIPE";
    internal const string StageVariable = "PLINTH_TEST_SYNC_STAGE";

    [Fact]
    public async Task Run()
    {
        var directory = Environment.GetEnvironmentVariable(DirectoryVariable);
        if (directory is null) return; // Ordinary test discovery does not activate the child fixture.
        var pipeName = Environment.GetEnvironmentVariable(PipeVariable)
            ?? throw new InvalidOperationException("Missing test pipe.");
        var stage = Environment.GetEnvironmentVariable(StageVariable);
        if (stage is not ("before-send" or "after-commit"))
            throw new InvalidOperationException("Invalid test boundary.");
        var server = new LocalPilotTransport(directory);
        var pilot = new StateMachine(Path.Combine(directory, "offline-sync-pilot.db"),
            server.Scope, new PausedTransport(server, pipeName, stage == "after-commit"));
        await pilot.InitializeAsync();
        Assert.True(await pilot.SyncOneAsync());
    }

    private sealed class PausedTransport(LocalPilotTransport server, string pipeName, bool commit) : IPilotTransport
    {
        public async Task<PilotReply> SendAsync(PilotWrite write, CancellationToken cancellationToken)
        {
            // Claim has committed before entry. When requested, also commit the actual server
            // transaction and receipt, but never deliver its reply to the client state machine.
            if (commit) await server.SendAsync(write, cancellationToken);
            using var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            await pipe.ConnectAsync(30000, cancellationToken);
            using var writer = new StreamWriter(pipe, leaveOpen: true) { AutoFlush = true };
            await writer.WriteLineAsync(write.OperationId.ToString("D"));
            using var reader = new StreamReader(pipe, leaveOpen: true);
            // Parent kills this OS process while it is blocked. EOF is a fixture failure,
            // never permission to return a reply or to send a previously unsent request.
            await reader.ReadLineAsync(cancellationToken);
            throw new InvalidOperationException("Parent disconnected without terminating the fixture.");
        }
    }
}
