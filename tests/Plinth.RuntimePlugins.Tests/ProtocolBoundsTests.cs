using System.Buffers.Binary;
using System.Text.Json;
using Plinth.App.Modules.RuntimePlugins;
using Xunit;

namespace Plinth.RuntimePlugins.Tests;

public sealed class ProtocolBoundsTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65537)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_lengths_are_rejected_before_payload_read(int length)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, length);
        using var stream = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
        {
            await PluginProtocol.ReadAsync(stream, CancellationToken.None);
        });
    }

    [Fact]
    public async Task Maximum_frame_round_trips_and_writer_rejects_larger_frames()
    {
        using var stream = new MemoryStream();
        var payload = new byte[PluginProtocol.MaxFrameBytes];
        await PluginProtocol.WriteAsync(stream, payload, CancellationToken.None);
        stream.Position = 0;
        Assert.Equal(payload, await PluginProtocol.ReadAsync(stream, CancellationToken.None));
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            PluginProtocol.WriteAsync(stream, new byte[PluginProtocol.MaxFrameBytes + 1], CancellationToken.None));
    }

    [Fact]
    public void Unknown_response_fields_are_rejected()
    {
        Assert.Throws<JsonException>(() => PluginProtocol.Decode<PluginResponse>(
            """{"version":1,"requestId":"id","success":true,"output":"ok","command":"run"}"""u8.ToArray()));
    }

    [Theory]
    [InlineData("""{"requestId":"id","success":true,"output":"ok"}""")]
    [InlineData("""{"version":1,"success":true,"output":"ok"}""")]
    [InlineData("""{"version":1,"requestId":"id","output":"ok"}""")]
    [InlineData("""{"version":1,"requestId":"id","success":false}""")]
    public void Missing_response_fields_are_protocol_errors(string json)
    {
        Assert.Throws<JsonException>(() => PluginProtocol.Decode<PluginResponse>(
            System.Text.Encoding.UTF8.GetBytes(json)));
    }

    [Theory]
    [InlineData("""{"requestId":"id","operation":"echo","input":"ok"}""")]
    [InlineData("""{"version":1,"operation":"echo","input":"ok"}""")]
    [InlineData("""{"version":1,"requestId":"id","input":"ok"}""")]
    [InlineData("""{"version":1,"requestId":"id","operation":"echo"}""")]
    public void Missing_request_fields_are_protocol_errors(string json)
    {
        Assert.Throws<JsonException>(() => PluginProtocol.Decode<PluginRequest>(
            System.Text.Encoding.UTF8.GetBytes(json)));
    }
}
