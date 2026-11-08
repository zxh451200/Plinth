using Plinth.App.Modules.RuntimePlugins;

// The sample has no dependency on the desktop app, DI container, database or user settings.
using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
try
{
    var input = Console.OpenStandardInput();
    var request = PluginProtocol.Decode<PluginRequest>(await PluginProtocol.ReadAsync(input, deadline.Token));
    if (request.Version != PluginProtocol.Version || request.Operation != "echo" ||
        request.RequestId is null || request.RequestId.Length != 32 || request.Input is null ||
        System.Text.Encoding.UTF8.GetByteCount(request.Input) > 16 * 1024 ||
        await input.ReadAsync(new byte[1], deadline.Token) != 0)
        return 2;
    var response = new PluginResponse(PluginProtocol.Version, request.RequestId, true, request.Input);
    await PluginProtocol.WriteAsync(Console.OpenStandardOutput(), PluginProtocol.Encode(response), deadline.Token);
    return 0;
}
catch (Exception)
{
    // stdout is protocol only. Do not print stack traces, request data or paths.
    return 2;
}
