using System.Buffers.Binary;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Plinth.App.Modules.RuntimePlugins;

/// <summary>One request / one response per process. No host object or service crosses this boundary.</summary>
public static class PluginProtocol
{
    public const int Version = 1;
    public const int MaxFrameBytes = 64 * 1024;
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8
    };

    public static byte[] Encode<T>(T value)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (payload.Length is 0 or > MaxFrameBytes)
            throw new InvalidDataException();
        return payload;
    }

    public static T Decode<T>(byte[] payload) =>
        JsonSerializer.Deserialize<T>(payload, Json) ?? throw new InvalidDataException();

    public static async Task WriteAsync(Stream stream, byte[] payload, CancellationToken cancellationToken)
    {
        if (payload.Length is 0 or > MaxFrameBytes)
            throw new InvalidDataException();
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    public static async Task<byte[]> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        var header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrameBytes)
            throw new InvalidDataException();
        var payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        return payload;
    }
}

public sealed record PluginRequest(int Version, string RequestId, string Operation, string Input);
public sealed record PluginResponse(int Version, string RequestId, bool Success, string? Output);

// Only these host-owned codes may be shown or logged. Never attach child stderr or exception text.
public enum PluginError
{
    None, Denied, InvalidInput, InvalidExecutable, StartFailed, ProtocolViolation,
    PluginFailed, TimedOut, Cancelled, CleanupFailed
}

public sealed record PluginResult(PluginError Error, string? Output = null)
{
    public bool Success => Error == PluginError.None;
}
