using System.Text.Json;
using System.Text.Json.Serialization;
using Plinth.App.Modules.OfflineSync;
using Plinth.OfflineSyncPilot;
using StateMachine = Plinth.App.Modules.OfflineSync.OfflineSyncPilot;

return await PilotCli.RunAsync(args, Console.Out, Console.Error);

namespace Plinth.OfflineSyncPilot
{
    public static class PilotCli
    {
        private const string Usage = """
            Local SQLite demonstration only; no HTTP, credentials or production account.
            Usage: <absolute-directory> <command> [arguments]
              seed <record-id> <code> <name> <remark>
              server <record-id>
              remote-edit <record-id> <expected-version> <code> <name> <remark>
              edit <record-id> <expected-version> <code> <name> <remark> [previous-operation-id]
              list [skip] [take]
              show <operation-id>
              sync [lose-response]              Sends at most one due Pending operation.
              accept <conflict-operation-id>    Accepts saved server snapshot/deletion.
              merge <conflict-operation-id> <code> <name> <remark>
              reconcile <uncertain-operation-id>  Uses an exact local terminal receipt only.
              discard <failed-operation-id>
            Exit codes: 0 success, 1 execution failure (query state before retry), 2 usage error.
            """;
        private static readonly JsonSerializerOptions Json = new()
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
        {
            if (args.Length == 1 && args[0] == "--help")
            {
                await output.WriteLineAsync(Usage);
                return 0;
            }
            if (!ValidArguments(args))
            {
                await error.WriteLineAsync(Usage);
                return 2;
            }
            try
            {
                if (!Path.IsPathFullyQualified(args[0])) throw new ArgumentException("Use an absolute directory.");
                var directory = Path.GetFullPath(args[0]);
                Directory.CreateDirectory(directory);
                var transport = new LocalPilotTransport(directory)
                {
                    LoseResponse = args[1] == "sync" && args.Length == 3
                };
                var pilot = new StateMachine(Path.Combine(directory, "offline-sync-pilot.db"),
                    transport.Scope, transport);
                // Opening/listing/editing the client must not require a server file.
                await pilot.InitializeAsync();
                object? result;
                switch (args[1])
                {
                    case "seed":
                        await transport.InitializeAsync();
                        result = await transport.SeedAsync(Id(2), Input(3));
                        break;
                    case "server":
                        result = await transport.ReadAsync(Id(2));
                        break;
                    case "remote-edit":
                        result = await transport.RemoteEditAsync(Id(2), int.Parse(args[3]), Input(4));
                        break;
                    case "edit":
                        result = await pilot.EditOfflineAsync(Id(2), int.Parse(args[3]), Input(4),
                            args.Length == 8 ? Id(7) : null);
                        break;
                    case "list":
                        result = await pilot.ListAsync(args.Length > 2 ? int.Parse(args[2]) : 0,
                            args.Length > 3 ? int.Parse(args[3]) : 50);
                        break;
                    case "show":
                        result = await RequiredAsync();
                        break;
                    case "sync":
                        result = new { Sent = await pilot.SyncOneAsync() };
                        break;
                    case "accept":
                        result = await pilot.ResolveConflictAsync(Id(2), null);
                        break;
                    case "merge":
                        result = await pilot.ResolveConflictAsync(Id(2), Input(3));
                        break;
                    case "reconcile":
                        var operation = await RequiredAsync();
                        var reply = await transport.LookupAsync(new(operation.OperationId, transport.Scope,
                            operation.RecordId, operation.ExpectedVersion, operation.Local))
                            ?? throw new InvalidOperationException("No terminal receipt; leave Uncertain unchanged.");
                        await pilot.ReconcileAsync(operation.OperationId, reply,
                            "local-receipt/" + operation.OperationId.ToString("D"));
                        result = await RequiredAsync();
                        break;
                    case "discard":
                        await pilot.DiscardFailedAsync(Id(2));
                        result = await RequiredAsync();
                        break;
                    default: throw new ArgumentException("Unknown command.");
                }
                await output.WriteLineAsync(JsonSerializer.Serialize(result, Json));
                return 0;

                Guid Id(int index) => Guid.Parse(args[index]);
                PilotInput Input(int index) => new(args[index], args[index + 1], args[index + 2]);
                async Task<PilotOperation> RequiredAsync() => await pilot.FindAsync(Id(2))
                    ?? throw new KeyNotFoundException("Unknown scoped operation.");
            }
            catch (Exception ex)
            {
                // Do not print raw exception bodies or persist them in the outbox.
                await error.WriteLineAsync($"Command failed ({ex.GetType().Name}). Inspect list/show before retrying. " +
                    "Uncertain requires an exact terminal receipt; missing receipt does not permit resend.");
                return 1;
            }
        }

        private static bool ValidArguments(string[] args) => args.Length >= 2 && (args[1] switch
        {
            "seed" => args.Length == 6,
            "server" or "show" or "accept" or "reconcile" or "discard" => args.Length == 3,
            "remote-edit" => args.Length == 7,
            "edit" => args.Length is 7 or 8,
            "list" => args.Length is >= 2 and <= 4,
            "sync" => args.Length == 2 || (args.Length == 3 && args[2] == "lose-response"),
            "merge" => args.Length == 6,
            _ => false
        });
    }
}
