using System.Buffers.Binary;
using System.Diagnostics;
using Plinth.App.Modules.RuntimePlugins;

// Deliberately hostile test peer. Never deploy or register this executable in the application.
if (args.Length == 1 && args[0] == "--child")
{
    await Task.Delay(Timeout.Infinite);
    return 0;
}
var request = PluginProtocol.Decode<PluginRequest>(
    await PluginProtocol.ReadAsync(Console.OpenStandardInput(), CancellationToken.None));

// 测试轮询端与写端存在共享冲突窗口；短暂碰撞时重试，避免夹具先于协议失败。
async Task WritePidAsync(string path, string content)
{
    for (var attempt = 0; ; attempt++)
    {
        try { await File.WriteAllTextAsync(path, content); return; }
        catch (IOException) when (attempt < 50) { await Task.Delay(20); }
    }
}

var output = Console.OpenStandardOutput();
var response = new PluginResponse(PluginProtocol.Version, request.RequestId, true, request.Input);
switch (request.Operation)
{
    case "hang":
        await WritePidAsync(request.Input, Environment.ProcessId.ToString());
        await Task.Delay(Timeout.Infinite);
        break;
    case "response-hang":
        await PluginProtocol.WriteAsync(output, PluginProtocol.Encode(response), CancellationToken.None);
        await WritePidAsync(request.Input, Environment.ProcessId.ToString());
        await Task.Delay(Timeout.Infinite);
        break;
    case "tree":
        using (var child = Process.Start(new ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            ArgumentList = { "--child" }
        })!)
        {
            await WritePidAsync(request.Input, $"{Environment.ProcessId},{child.Id}");
            await Task.Delay(Timeout.Infinite);
        }
        break;
    case "oversize":
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, PluginProtocol.MaxFrameBytes + 1);
        await output.WriteAsync(header);
        break;
    case "truncated":
        await output.WriteAsync(new byte[] { 1, 0, 0, 0 });
        break;
    case "malformed":
        await PluginProtocol.WriteAsync(output, "secret=/private/token"u8.ToArray(), CancellationToken.None);
        break;
    case "version":
        response = response with { Version = 99 };
        break;
    case "id":
        response = response with { RequestId = "different" };
        break;
    case "failed":
        response = response with { Success = false, Output = "secret=/private/token" };
        break;
    case "missing-success":
        await PluginProtocol.WriteAsync(output,
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
            {
                version = PluginProtocol.Version,
                requestId = request.RequestId,
                output = "ok"
            }), CancellationToken.None);
        return 0;
    case "exit":
        await PluginProtocol.WriteAsync(output, PluginProtocol.Encode(response), CancellationToken.None);
        return 7;
    case "stderr":
        await Console.OpenStandardError().WriteAsync(new byte[PluginProtocol.MaxFrameBytes]);
        await Task.Delay(Timeout.Infinite);
        break;
    case "extra":
        await PluginProtocol.WriteAsync(output, PluginProtocol.Encode(response), CancellationToken.None);
        await output.WriteAsync(new byte[] { 42 });
        return 0;
    case "large-output":
        response = response with { Output = new string('x', 16 * 1024 + 1) };
        break;
    case "environment":
        response = response with { Output = Environment.GetEnvironmentVariable("PLINTH_PLUGIN_TEST_SECRET") ?? "absent" };
        break;
}
if (request.Operation is "oversize" or "truncated" or "malformed") return 0;
await PluginProtocol.WriteAsync(output, PluginProtocol.Encode(response), CancellationToken.None);
return 0;
