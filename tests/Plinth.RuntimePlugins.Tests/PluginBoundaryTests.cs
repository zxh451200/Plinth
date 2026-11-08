using System.Diagnostics;
using System.Security.Cryptography;
using Plinth.App.Modules.RuntimePlugins;
using Xunit;
namespace Plinth.RuntimePlugins.Tests;

public sealed class PluginBoundaryTests
{
    private static string Peer(string folder, string name) => Path.Combine(AppContext.BaseDirectory,
        folder, name + (OperatingSystem.IsWindows() ? ".exe" : ""));
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static RuntimePluginHost Host(string operation, TimeSpan? timeout = null)
    {
        var path = Peer("fixture", "Plinth.PluginFixture");
        return new([new("fixture", path, Hash(path), [operation, "echo"])], timeout);
    }
    [Fact]
    public async Task Sample_round_trips_unicode_and_shell_metacharacters()
    {
        var path = Peer("sample", "Plinth.EchoPlugin");
        var host = new RuntimePluginHost([new("echo", path, Hash(path), ["echo"])]);
        const string text = "你好 $(touch should-not-exist) & | quoted";
        var result = await host.InvokeAsync("echo", "echo", text);
        Assert.True(result.Success, result.Error.ToString());
        Assert.Equal(text, result.Output);
    }
    [Fact]
    public async Task Default_deny_input_limit_and_precancel()
    {
        Assert.Equal(PluginError.Denied, (await new RuntimePluginHost([]).InvokeAsync("x", "echo", "")).Error);
        var host = Host("echo");
        Assert.Equal(PluginError.Denied, (await host.InvokeAsync("fixture", "other", "")).Error);
        Assert.Equal(PluginError.InvalidInput, (await host.InvokeAsync("fixture", "echo", new string('x', 16385))).Error);
        Assert.Equal(PluginError.Cancelled, (await host.InvokeAsync("fixture", "echo", "", new CancellationToken(true))).Error);
    }
    [Theory]
    [InlineData("version", PluginError.ProtocolViolation)]
    [InlineData("id", PluginError.ProtocolViolation)]
    [InlineData("oversize", PluginError.ProtocolViolation)]
    [InlineData("truncated", PluginError.ProtocolViolation)]
    [InlineData("malformed", PluginError.ProtocolViolation)]
    [InlineData("extra", PluginError.ProtocolViolation)]
    [InlineData("large-output", PluginError.ProtocolViolation)]
    [InlineData("stderr", PluginError.ProtocolViolation)]
    [InlineData("exit", PluginError.PluginFailed)]
    [InlineData("failed", PluginError.PluginFailed)]
    [InlineData("missing-success", PluginError.ProtocolViolation)]
    public async Task Hostile_peer_errors_are_redacted(string operation, PluginError expected)
    {
        var result = await Host(operation).InvokeAsync("fixture", operation, "secret=/private/token");
        Assert.Equal(expected, result.Error);
        Assert.Null(result.Output);
        Assert.DoesNotContain("secret", result.ToString());
    }
    [Fact]
    public async Task Hash_mismatch_missing_file_and_relative_path_fail_closed()
    {
        var path = Peer("fixture", "Plinth.PluginFixture");
        foreach (var candidate in new[] { path, path + "missing" })
        {
            var host = new RuntimePluginHost([new("fixture", candidate, new string('0', 64), ["echo"])]);
            Assert.Equal(PluginError.InvalidExecutable, (await host.InvokeAsync("fixture", "echo", "")).Error);
        }
        Assert.Throws<ArgumentException>(() => new RuntimePluginHost([new("x", "relative.exe", new string('0', 64), ["echo"])]));
    }
    [Fact]
    public async Task Registration_copies_operation_allowlist()
    {
        var path = Peer("fixture", "Plinth.PluginFixture");
        var operations = new List<string> { "echo" };
        var host = new RuntimePluginHost([new("fixture", path, Hash(path), operations)]);
        operations.Add("hang");
        Assert.Equal(PluginError.Denied, (await host.InvokeAsync("fixture", "hang", "")).Error);
    }
    [Fact]
    public async Task Symlink_is_rejected()
    {
        if (OperatingSystem.IsWindows()) return; // Windows needs separate symlink privilege.
        var path = Peer("fixture", "Plinth.PluginFixture");
        var link = Path.Combine(Path.GetDirectoryName(path)!, Guid.NewGuid().ToString("N"));
        File.CreateSymbolicLink(link, path);
        try
        {
            var host = new RuntimePluginHost([new("x", link, Hash(path), ["echo"])]);
            Assert.Equal(PluginError.InvalidExecutable, (await host.InvokeAsync("x", "echo", "")).Error);
        }
        finally { File.Delete(link); }
    }
    [Theory]
    [InlineData(false, "hang")]
    [InlineData(true, "hang")]
    [InlineData(true, "tree")]
    [InlineData(false, "response-hang")]
    [InlineData(true, "response-hang")]
    public async Task Timeout_and_cancel_reap_processes(bool cancel, string operation)
    {
        var file = Path.Combine(Path.GetTempPath(), $"plinth-plugin-{Guid.NewGuid():N}");
        using var cts = new CancellationTokenSource();
        var host = Host(operation, TimeSpan.FromSeconds(3));
        var invocation = host.InvokeAsync("fixture", operation, file, cts.Token);
        int[] pids = [];
        try
        {
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(2))
            {
                if (File.Exists(file))
                {
                    // 写端(File.WriteAllTextAsync 的独享窗口)与轮询读端在 Windows 上会共享冲突，碰撞时继续轮询。
                    string? text;
                    try { text = await File.ReadAllTextAsync(file); }
                    catch (IOException) { await Task.Delay(20); continue; }
                    if (text.Length > 0 && text.Split(',').All(x => int.TryParse(x, out _)))
                    { pids = text.Split(',').Select(int.Parse).ToArray(); break; }
                }
                await Task.Delay(20);
            }
            Assert.NotEmpty(pids);
            if (cancel) cts.Cancel();
            var result = await invocation.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(cancel ? PluginError.Cancelled : PluginError.TimedOut, result.Error);
            foreach (var pid in pids)
            {
                var watchExit = Stopwatch.StartNew();
                while (IsRunning(pid) && watchExit.Elapsed < TimeSpan.FromSeconds(2)) await Task.Delay(20);
                Assert.False(IsRunning(pid), $"Process {pid} survived cleanup.");
            }
            var subsequent = await host.InvokeAsync("fixture", "echo", "after cleanup");
            Assert.True(subsequent.Success, subsequent.Error.ToString());
            Assert.Equal("after cleanup", subsequent.Output);
        }
        finally
        {
            cts.Cancel(); await invocation;
            foreach (var pid in pids)
            {
                try { using var process = Process.GetProcessById(pid); if (!process.HasExited) process.Kill(true); }
                catch (ArgumentException) { }
            }
            File.Delete(file);
        }
    }
    [Fact]
    public async Task Environment_secrets_are_not_inherited()
    {
        Environment.SetEnvironmentVariable("PLINTH_PLUGIN_TEST_SECRET", "private-token");
        try
        {
            var result = await Host("environment").InvokeAsync("fixture", "environment", "");
            Assert.True(result.Success, result.Error.ToString());
            Assert.Equal("absent", result.Output);
        }
        finally { Environment.SetEnvironmentVariable("PLINTH_PLUGIN_TEST_SECRET", null); }
    }
    private static bool IsRunning(int pid)
    {
        try { using var process = Process.GetProcessById(pid); return !process.HasExited; }
        catch (ArgumentException) { return false; }
    }
}
