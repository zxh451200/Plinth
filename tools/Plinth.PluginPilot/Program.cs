using Plinth.App.Modules.RuntimePlugins;

// Fixed installation layout. Neither the executable path nor the expected hash comes from argv.
// A trusted operator places the reviewed release and its hash here before running the pilot.
var executable = Path.Combine(AppContext.BaseDirectory, "plugins", "echo",
    OperatingSystem.IsWindows() ? "Plinth.EchoPlugin.exe" : "Plinth.EchoPlugin");
var hashPath = Path.Combine(AppContext.BaseDirectory, "plugins", "echo", "approved.sha256");
if (args.Length != 1)
{
    Console.Error.WriteLine("Usage: Plinth.PluginPilot <text>");
    return 2;
}
using var cancellation = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
try
{
    // This is deployment policy, not a plugin-discovered manifest.
    var hashInfo = new FileInfo(hashPath);
    if (!hashInfo.Exists || hashInfo.Length > 128 ||
        (hashInfo.Attributes & FileAttributes.ReparsePoint) != 0)
    {
        Console.Error.WriteLine(PluginError.Denied);
        return 1;
    }
    var hash = (await File.ReadAllTextAsync(hashPath, cancellation.Token)).Trim();
    var host = new RuntimePluginHost([new("sample.echo", executable, hash, ["echo"])]);
    var result = await host.InvokeAsync("sample.echo", "echo", args[0], cancellation.Token);
    if (!result.Success)
    {
        Console.Error.WriteLine(result.Error);
        return 1;
    }
    // Untrusted output is JSON-escaped before terminal rendering.
    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(result.Output));
    return 0;
}
catch (Exception)
{
    Console.Error.WriteLine(PluginError.Denied);
    return 1;
}
