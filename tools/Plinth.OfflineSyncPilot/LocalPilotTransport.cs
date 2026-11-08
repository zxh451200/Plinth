using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Plinth.App.Modules.OfflineSync;
using StateMachine = Plinth.App.Modules.OfflineSync.OfflineSyncPilot;

namespace Plinth.OfflineSyncPilot;

/// <summary>Local demonstration authority only. Never registered in the desktop app.</summary>
public sealed class LocalPilotTransport : IPilotTransport
{
    private readonly string connectionString;
    public string Scope { get; }
    public bool LoseResponse { get; init; }

    public LocalPilotTransport(string directory)
    {
        if (!Path.IsPathFullyQualified(directory))
            throw new ArgumentException("An absolute pilot directory is required.");
        var path = Path.Combine(Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory)), "local-server.db");
        Scope = "local-sqlite/" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path))) + "/demo-user";
        connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = path, Pooling = false, DefaultTimeout = 5
        }.ToString();
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken token)
    {
        var connection = new SqliteConnection(connectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync(CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS pilot_records (
                scope TEXT NOT NULL, id TEXT NOT NULL, snapshot TEXT NOT NULL,
                PRIMARY KEY (scope, id));
            CREATE TABLE IF NOT EXISTS pilot_receipts (
                scope TEXT NOT NULL, id TEXT NOT NULL, request TEXT NOT NULL, reply TEXT NOT NULL,
                PRIMARY KEY (scope, id));
            """;
        await command.ExecuteNonQueryAsync(token);
    }

    // BeginTransaction uses an immediate SQLite write transaction: reads, version comparison,
    // record update and terminal receipt commit are serialized across independent processes.
    public async Task<PilotReply> SendAsync(PilotWrite write, CancellationToken cancellationToken)
    {
        if (write.Scope != Scope || write.OperationId == Guid.Empty)
            throw new ArgumentException("Wrong local destination or operation.");
        StateMachine.Validate(write.RecordId, write.ExpectedVersion, write.Value);
        await using var connection = await OpenAsync(cancellationToken);
        using var transaction = connection.BeginTransaction();
        var receipt = await ReceiptAsync(connection, transaction, write, cancellationToken);
        PilotReply reply;
        if (receipt is not null) reply = receipt;
        else
        {
            var current = await ReadAsync(connection, transaction, write.RecordId, cancellationToken);
            reply = current is null || current.Version != write.ExpectedVersion
                ? new(PilotOutcome.Conflict, current)
                : current.Version == int.MaxValue
                    ? new(PilotOutcome.Rejected)
                    : new(PilotOutcome.Applied, new(write.RecordId, current.Version + 1, write.Value));
            if (reply.Outcome == PilotOutcome.Applied)
                await StoreAsync(connection, transaction, reply.Server!, cancellationToken);
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = "INSERT INTO pilot_receipts VALUES ($scope, $id, $request, $reply)";
            insert.Parameters.AddWithValue("$scope", Scope);
            insert.Parameters.AddWithValue("$id", write.OperationId.ToString("D"));
            insert.Parameters.AddWithValue("$request", JsonSerializer.Serialize(write));
            insert.Parameters.AddWithValue("$reply", JsonSerializer.Serialize(reply));
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        transaction.Commit();
        if (LoseResponse) throw new IOException("Simulated response loss; inspect state and reconcile the local receipt.");
        return reply;
    }

    public async Task<PilotReply?> LookupAsync(PilotWrite write, CancellationToken token = default)
    {
        if (write.Scope != Scope) throw new ArgumentException("Wrong local destination.");
        await using var connection = await OpenAsync(token);
        return await ReceiptAsync(connection, null, write, token);
    }

    private async Task<PilotReply?> ReceiptAsync(SqliteConnection connection, SqliteTransaction? transaction,
        PilotWrite write, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT request, reply FROM pilot_receipts WHERE scope=$scope AND id=$id";
        command.Parameters.AddWithValue("$scope", Scope);
        command.Parameters.AddWithValue("$id", write.OperationId.ToString("D"));
        await using var reader = await command.ExecuteReaderAsync(token);
        if (!await reader.ReadAsync(token)) return null; // Absence never proves noncommit.
        if (JsonSerializer.Deserialize<PilotWrite>(reader.GetString(0)) != write)
            throw new InvalidOperationException("Operation ID is already bound to a different payload.");
        return JsonSerializer.Deserialize<PilotReply>(reader.GetString(1))
            ?? throw new InvalidDataException("Invalid local receipt.");
    }

    public async Task<PilotRecord?> ReadAsync(Guid id, CancellationToken token = default)
    {
        await using var connection = await OpenAsync(token);
        return await ReadAsync(connection, null, id, token);
    }

    private async Task<PilotRecord?> ReadAsync(SqliteConnection connection, SqliteTransaction? transaction,
        Guid id, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT snapshot FROM pilot_records WHERE scope=$scope AND id=$id";
        command.Parameters.AddWithValue("$scope", Scope);
        command.Parameters.AddWithValue("$id", id.ToString("D"));
        var json = await command.ExecuteScalarAsync(token) as string;
        return json is null ? null : JsonSerializer.Deserialize<PilotRecord>(json)
            ?? throw new InvalidDataException("Invalid local server record.");
    }

    public async Task<PilotRecord> SeedAsync(Guid id, PilotInput value, CancellationToken token = default)
    {
        StateMachine.Validate(id, 1, value);
        await using var connection = await OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        if (await ReadAsync(connection, transaction, id, token) is not null)
            throw new InvalidOperationException("Record already exists; seed never overwrites.");
        var record = new PilotRecord(id, 1, value);
        await StoreAsync(connection, transaction, record, token);
        transaction.Commit();
        return record;
    }

    // Simulates another actor with compare-and-swap, without touching the client outbox.
    public async Task<PilotRecord> RemoteEditAsync(Guid id, int version, PilotInput value,
        CancellationToken token = default)
    {
        StateMachine.Validate(id, version, value);
        await using var connection = await OpenAsync(token);
        using var transaction = connection.BeginTransaction();
        var current = await ReadAsync(connection, transaction, id, token);
        if (current is null || current.Version != version)
            throw new InvalidOperationException("Local server version changed or record is missing.");
        var next = new PilotRecord(id, checked(version + 1), value);
        await StoreAsync(connection, transaction, next, token);
        transaction.Commit();
        return next;
    }

    private async Task StoreAsync(SqliteConnection connection, SqliteTransaction transaction,
        PilotRecord record, CancellationToken token)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO pilot_records VALUES ($scope, $id, $snapshot)
            ON CONFLICT(scope, id) DO UPDATE SET snapshot=excluded.snapshot
            """;
        command.Parameters.AddWithValue("$scope", Scope);
        command.Parameters.AddWithValue("$id", record.Id.ToString("D"));
        command.Parameters.AddWithValue("$snapshot", JsonSerializer.Serialize(record));
        await command.ExecuteNonQueryAsync(token);
    }
}
