using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;

namespace Plinth.App.Modules.RuntimePlugins;

public sealed record AllowedPlugin(string Id, string ExecutablePath, string Sha256, IReadOnlyCollection<string> Operations);

/// <summary>
/// Explicitly constructed, default-deny pilot. Registration must come from trusted host code,
/// never a plugin manifest, IPC request, environment variable or user-supplied command line.
/// </summary>
public sealed class RuntimePluginHost
{
    private readonly Dictionary<string, Registration> _plugins = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly TimeSpan _timeout;
    private const int MaxTextBytes = 16 * 1024;
    private const int MaxStderrBytes = 8 * 1024;

    public RuntimePluginHost(IEnumerable<AllowedPlugin> allowlist, TimeSpan? timeout = null)
    {
        _timeout = timeout ?? TimeSpan.FromSeconds(5);
        if (_timeout < TimeSpan.FromMilliseconds(50) || _timeout > TimeSpan.FromSeconds(30))
            throw new ArgumentOutOfRangeException(nameof(timeout));

        foreach (var entry in allowlist)
        {
            if (string.IsNullOrWhiteSpace(entry.Id) || !Path.IsPathFullyQualified(entry.ExecutablePath) ||
                !string.Equals(Path.GetFullPath(entry.ExecutablePath), entry.ExecutablePath, PathComparison) ||
                entry.Sha256.Length != 64 || !entry.Sha256.All(Uri.IsHexDigit) ||
                entry.Operations.Count == 0 || entry.Operations.Any(string.IsNullOrWhiteSpace))
                throw new ArgumentException("Invalid plugin registration.");
            // Take a snapshot: callers cannot extend a live allowlist by mutating their collection.
            _plugins.Add(entry.Id, new(entry.ExecutablePath, entry.Sha256,
                new HashSet<string>(entry.Operations, StringComparer.Ordinal)));
        }
    }

    public async Task<PluginResult> InvokeAsync(
        string pluginId, string operation, string input, CancellationToken cancellationToken = default)
    {
        if (pluginId is null || operation is null || !_plugins.TryGetValue(pluginId, out var entry) ||
            !entry.Operations.Contains(operation))
            return new(PluginError.Denied);
        if (input is null || Encoding.UTF8.GetByteCount(input) > MaxTextBytes)
            return new(PluginError.InvalidInput);

        using var deadline = new CancellationTokenSource(_timeout);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        var token = lifetime.Token;
        Process? process = null;
        Task? exchange = null;
        Task? stderr = null;
        var entered = false;
        var started = false;
        var result = new PluginResult(PluginError.StartFailed);
        try
        {
            await _gate.WaitAsync(token);
            entered = true;
            try
            {
                if (!await ValidateExecutableAsync(entry, token))
                    return new(PluginError.InvalidExecutable);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new(PluginError.InvalidExecutable);
            }

            var requestId = Guid.NewGuid().ToString("N");
            byte[] payload;
            try { payload = PluginProtocol.Encode(new PluginRequest(PluginProtocol.Version, requestId, operation, input)); }
            catch (InvalidDataException) { return new(PluginError.InvalidInput); }

            var start = new ProcessStartInfo
            {
                FileName = entry.Path,
                WorkingDirectory = Path.GetDirectoryName(entry.Path)!,
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            // No arguments, shell, PATH lookup or inherited application credentials.
            start.Environment.Clear();
            if (OperatingSystem.IsWindows())
            {
                var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                start.Environment["SystemRoot"] = windows;
                start.Environment["WINDIR"] = windows;
            }
            process = new Process { StartInfo = start };
            token.ThrowIfCancellationRequested();
            started = process.Start();
            if (!started)
                return new(PluginError.StartFailed);

            stderr = DrainStderrAsync(process.StandardError.BaseStream, token);
            var responseTask = ExchangeAsync(process, payload, token);
            exchange = responseTask;
            // Fail immediately on either pipe fault; WhenAll alone could wait for a hung peer.
            var first = await Task.WhenAny(responseTask, stderr).WaitAsync(token);
            await first;
            // The first task is already complete. Await both directly so cancellation cannot
            // abandon a faulting WhenAll task in addition to the pipe tasks observed below.
            await responseTask.WaitAsync(token);
            await stderr.WaitAsync(token);
            await process.WaitForExitAsync(token);
            var response = await responseTask;
            if (response.Version != PluginProtocol.Version || response.RequestId != requestId ||
                (response.Success && response.Output is null) ||
                (response.Output is not null && Encoding.UTF8.GetByteCount(response.Output) > MaxTextBytes))
                result = new(PluginError.ProtocolViolation);
            else if (process.ExitCode != 0 || !response.Success)
                result = new(PluginError.PluginFailed);
            else
                result = new(PluginError.None, response.Output);
        }
        catch (OperationCanceledException)
        {
            result = new(cancellationToken.IsCancellationRequested ? PluginError.Cancelled : PluginError.TimedOut);
        }
        catch (Exception ex) when (ex is InvalidDataException or System.Text.Json.JsonException or EndOfStreamException)
        {
            result = new(PluginError.ProtocolViolation);
        }
        catch (Exception)
        {
            // Do not disclose local paths, payloads, platform errors or plugin-provided diagnostics.
            result = new(PluginError.StartFailed);
        }
        finally
        {
            lifetime.Cancel();
            if (process is not null)
            {
                try
                {
                    if (started && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                        await process.WaitForExitAsync(cleanup.Token);
                    }
                }
                catch (InvalidOperationException) { /* Process.Start failed: no process to reap. */ }
                catch (Exception) { result = new(PluginError.CleanupFailed); }
                finally
                {
                    if (started)
                    {
                        try { process.StandardInput.Dispose(); } catch (Exception) { }
                        try { process.StandardOutput.Dispose(); } catch (Exception) { }
                        try { process.StandardError.Dispose(); } catch (Exception) { }
                    }
                    process.Dispose();
                }
            }
            // Observe pipe tasks after cancellation and disposal; never leave unobserved exceptions.
            var exchangeStopped = await ObserveAsync(exchange);
            var stderrStopped = await ObserveAsync(stderr);
            if (!exchangeStopped || !stderrStopped) result = new(PluginError.CleanupFailed);
            if (entered) _gate.Release();
        }
        return result;
    }

    private static async Task<PluginResponse> ExchangeAsync(Process process, byte[] payload, CancellationToken token)
    {
        await PluginProtocol.WriteAsync(process.StandardInput.BaseStream, payload, token);
        process.StandardInput.Close();
        var response = await PluginProtocol.ReadAsync(process.StandardOutput.BaseStream, token);
        // Exactly one frame; EOF and process exit are part of the protocol.
        if (await process.StandardOutput.BaseStream.ReadAsync(new byte[1], token) != 0)
            throw new InvalidDataException();
        return PluginProtocol.Decode<PluginResponse>(response);
    }

    private static async Task DrainStderrAsync(Stream stream, CancellationToken token)
    {
        var buffer = new byte[1024];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, token)) != 0)
        {
            total += read;
            if (total > MaxStderrBytes) throw new InvalidDataException();
        }
    }

    private static async Task<bool> ValidateExecutableAsync(Registration entry, CancellationToken token)
    {
        // Reject links/junctions at every level, including the executable itself.
        for (string? path = entry.Path; path is not null; path = Path.GetDirectoryName(path))
        {
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        }
        var extension = Path.GetExtension(entry.Path);
        if (OperatingSystem.IsWindows() ? !extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) : extension.Length != 0)
            return false;
        await using var file = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read,
            4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var magic = new byte[4];
        if (await file.ReadAsync(magic, token) != 4) return false;
        if (OperatingSystem.IsWindows()
            ? magic[0] != 'M' || magic[1] != 'Z'
            : !(magic[0] == 0x7f && magic[1] == 'E' && magic[2] == 'L' && magic[3] == 'F'))
            return false;
        file.Position = 0;
        var hash = Convert.ToHexString(await SHA256.HashDataAsync(file, token));
        return string.Equals(hash, entry.Hash, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ObserveAsync(Task? task)
    {
        if (task is null) return true;
        try { await task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException)
        {
            _ = task.ContinueWith(t => { _ = t.Exception; },
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted |
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            return false;
        }
        catch (Exception) { /* Host-owned result only; no external diagnostics. */ }
        return true;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    private sealed record Registration(string Path, string Hash, HashSet<string> Operations);
}
