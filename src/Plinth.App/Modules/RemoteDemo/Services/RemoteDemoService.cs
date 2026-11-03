using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Modules.RemoteDemo.Data;
using Plinth.App.Remote;
using Plinth.App.Security;
using Plinth.App.Services;

namespace Plinth.App.Modules.RemoteDemo.Services;

public sealed class RemoteDemoService(RemoteApiClient api, AuthorizedOperation authorized,
    SensitiveOperationService sensitive, IRemoteUserContext users)
{
    public const int MaxCodeLength = 50, MaxNameLength = 200, MaxRemarkLength = 500, MaxSearchLength = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task<RemoteDemoPage> SearchPageAsync(string search = "", int page = 1, int pageSize = 50,
        string sortField = "Code", bool descending = false, CancellationToken cancellationToken = default)
    {
        if (page < 1 || pageSize is < 1 or > 200 || sortField is not ("Code" or "Name")
            || !ValidText(search, MaxSearchLength, false))
            throw new ArgumentException("remoteDemo.invalidQuery");
        var path = "demo-records?page=" + page.ToString(CultureInfo.InvariantCulture)
            + "&pageSize=" + pageSize.ToString(CultureInfo.InvariantCulture)
            + "&search=" + Uri.EscapeDataString(search) + "&sortField=" + sortField
            + "&descending=" + (descending ? "true" : "false");
        return ExecuteAsync("demo:read", "read", false, async (actor, id, ct) =>
        {
            using var response = await api.SendAsync(HttpMethod.Get, path, cancellationToken: ct, operationCorrelationId: id);
            CheckActor(actor, id);
            var result = await ReadJsonAsync(response, id, root =>
            {
                RequireObject(root, "page", "pageSize", "totalCount", "items");
                var actualPage = root.GetProperty("page").GetInt32();
                var actualSize = root.GetProperty("pageSize").GetInt32();
                var total = root.GetProperty("totalCount").GetInt32();
                if (actualPage != page || actualSize != pageSize || total < 0)
                    throw new JsonException();
                var items = root.GetProperty("items");
                if (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > pageSize)
                    throw new JsonException();
                var expectedCount = (int)Math.Min(pageSize, Math.Max(0L, total - ((long)page - 1) * pageSize));
                if (items.GetArrayLength() != expectedCount) throw new JsonException();
                var records = items.EnumerateArray().Select(ReadRecord).ToArray();
                if (records.Select(r => r.Id).Distinct().Count() != records.Length) throw new JsonException();
                return new RemoteDemoPage(page, pageSize, total, records);
            }, ct);
            CheckActor(actor, id);
            return result;
        }, cancellationToken);
    }

    public Task<RemoteDemoRecord> CreateAsync(RemoteDemoInput input, CancellationToken cancellationToken = default) =>
        SaveAsync(null, null, input, cancellationToken);

    public Task<RemoteDemoRecord> UpdateAsync(Guid id, int expectedVersion, RemoteDemoInput input,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(id, expectedVersion);
        return SaveAsync(id, expectedVersion, input, cancellationToken);
    }

    private Task<RemoteDemoRecord> SaveAsync(Guid? recordId, int? version, RemoteDemoInput input, CancellationToken token)
    {
        if (!ValidInput(input)) throw new ArgumentException("remoteDemo.invalidInput");
        return ExecuteAsync("demo:save", recordId is null ? "create" : "update", true, async (actor, id, ct) =>
        {
            using var body = new StringContent(JsonSerializer.Serialize(input, JsonOptions), Encoding.UTF8, "application/json");
            using var response = await api.SendAsync(recordId is null ? HttpMethod.Post : HttpMethod.Put,
                recordId is null ? "demo-records" : "demo-records/" + recordId.Value.ToString("D"),
                body, ct, id, version);
            CheckActor(actor, id);
            var record = await ReadJsonAsync(response, id, ReadRecord, ct);
            if (recordId is not null && (record.Id != recordId || record.Version <= version!.Value))
                throw RemoteApiClient.Failure("remoteDemo.invalidResponse", Correlation(response, id));
            CheckActor(actor, id);
            return record;
        }, token);
    }

    public Task<bool> DeleteAsync(Guid recordId, int expectedVersion, CancellationToken cancellationToken = default)
    {
        ValidateIdentity(recordId, expectedVersion);
        return ExecuteAsync("demo:delete", "delete", true, async (actor, id, ct) =>
        {
            using var response = await api.SendAsync(HttpMethod.Delete, "demo-records/" + recordId.ToString("D"),
                cancellationToken: ct, operationCorrelationId: id, expectedVersion: expectedVersion);
            CheckActor(actor, id);
            ct.ThrowIfCancellationRequested();
            if (response.StatusCode != HttpStatusCode.NoContent)
                throw RemoteApiClient.Failure("remoteDemo.invalidResponse", Correlation(response, id));
            return true;
        }, cancellationToken);
    }

    private Task<T> ExecuteAsync<T>(string permission, string operation, bool write,
        Func<string, string, CancellationToken, Task<T>> action, CancellationToken token)
    {
        var actor = users.CurrentUser.Subject;
        var session = users.SessionVersion;
        return authorized.ExecuteAsync(permission, "remoteDemo", operation, async (id, ct) =>
        {
            CheckActor(actor, id, write, session);
            ct.ThrowIfCancellationRequested();
            try
            {
                var result = await action(actor, id, ct);
                ct.ThrowIfCancellationRequested();
                CheckActor(actor, id, write, session);
                return result;
            }
            catch (OperationCanceledException) when (write)
            {
                throw new RemoteApiException(new RemoteApiError("remoteDemo.writeUncertain",
                    "remoteDemo.writeUncertain.message", "remote.suggestion.verify_result", false, id));
            }
            catch (RemoteApiException ex) when (write && ex.Error.Code == "remoteDemo.invalidResponse")
            {
                throw new RemoteApiException(ex.Error with { SuggestionKey = "remote.suggestion.verify_result" });
            }
            finally { CheckActor(actor, id, write, session); }
        }, token, confirm: write ? sensitive.ConfirmAsync : null);
    }

    private void CheckActor(string actor, string id, bool write = false, long? session = null)
    {
        if (string.IsNullOrWhiteSpace(actor) || !string.Equals(actor, users.CurrentUser.Subject, StringComparison.Ordinal)
            || (session is not null && session.Value != users.SessionVersion))
            throw new RemoteApiException(new RemoteApiError("remoteDemo.actorChanged",
                "remoteDemo.actorChanged.message",
                write ? "remote.suggestion.verify_result" : "remote.suggestion.reload", false, id));
    }

    public static bool ValidInput(RemoteDemoInput? input) => input is not null
        && ValidText(input.Code, MaxCodeLength, true) && ValidText(input.Name, MaxNameLength, true)
        && ValidText(input.Remark, MaxRemarkLength, false);

    public static bool ValidText(string? value, int max, bool required)
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

    private static void ValidateIdentity(Guid id, int version)
    {
        if (id == Guid.Empty || version <= 0) throw new ArgumentException("remoteDemo.invalidIdentity");
    }

    private static RemoteDemoRecord ReadRecord(JsonElement root)
    {
        RequireObject(root, "id", "code", "name", "remark", "version");
        var idText = root.GetProperty("id").GetString();
        if (!Guid.TryParseExact(idText, "D", out var id) || id == Guid.Empty) throw new JsonException();
        var input = new RemoteDemoInput(root.GetProperty("code").GetString()!,
            root.GetProperty("name").GetString()!, root.GetProperty("remark").GetString()!);
        var version = root.GetProperty("version").GetInt32();
        if (!ValidInput(input) || version <= 0) throw new JsonException();
        return new(id, input.Code, input.Name, input.Remark, version);
    }

    private static void RequireObject(JsonElement root, params string[] fields)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new JsonException();
        var names = root.EnumerateObject().Select(p => p.Name).ToArray();
        if (names.Length != fields.Length || names.Distinct(StringComparer.Ordinal).Count() != fields.Length
            || fields.Any(f => !root.TryGetProperty(f, out _))) throw new JsonException();
    }

    private static string Correlation(HttpResponseMessage response, string fallback) =>
        response.Headers.TryGetValues("X-Correlation-ID", out var ids) ? ids.First() : fallback;

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response, string id,
        Func<JsonElement, T> read, CancellationToken token)
    {
        try
        {
            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (mediaType is null || !(mediaType.Equals("application/json", StringComparison.OrdinalIgnoreCase)
                || mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)))
                throw new JsonException();
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(token));
            return read(document.RootElement);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException or OverflowException
                                   or DecoderFallbackException)
        {
            throw RemoteApiClient.Failure("remoteDemo.invalidResponse", Correlation(response, id));
        }
    }
}
