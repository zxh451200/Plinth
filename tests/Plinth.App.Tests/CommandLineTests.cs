using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Localization;
using Xunit;

namespace Plinth.App.Tests;

public sealed class CommandLineTests
{
    [Fact]
    public void No_arguments_continue_to_the_desktop_without_output()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Null(CommandLine.Handle([], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Fact]
    public void Version_uses_the_same_assembly_version_as_the_desktop()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(0, CommandLine.Handle(["--version"], output, error));
        Assert.Equal($"{AppInfo.ProductName} {AppInfo.Version}{output.NewLine}", output.ToString());
        Assert.Equal("", error.ToString());
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("--Version")]
    [InlineData("--version=1")]
    [InlineData("")]
    [InlineData("token=private\n\u001b[31m")]
    public void Unknown_arguments_use_stderr_without_echoing_input(string argument)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        Assert.Equal(2, CommandLine.Handle([argument], output, error));
        Assert.Equal("", output.ToString());
        Assert.Equal(Loc.Get("cli.invalidArguments") + error.NewLine, error.ToString());
    }

    [Fact]
    public void Version_does_not_ignore_extra_or_duplicate_arguments()
    {
        foreach (var args in new[] { new[] { "--version", "--unknown" }, new[] { "--version", "--version" } })
        {
            using var output = new StringWriter();
            using var error = new StringWriter();
            Assert.Equal(2, CommandLine.Handle(args, output, error));
            Assert.Equal("", output.ToString());
            Assert.NotEmpty(error.ToString());
        }
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("--unknown")]
    public void Closed_output_pipe_returns_failure_instead_of_throwing(string argument)
    {
        using var broken = new BrokenWriter();
        Assert.Equal(1, CommandLine.Handle([argument], broken, broken));
    }

    [Theory]
    [InlineData("--version", 0)]
    [InlineData("--unknown", 2)]
    public async Task Real_entry_point_exits_without_a_display_or_creating_a_data_directory(string argument, int expectedCode)
    {
        var dataDirectory = Path.Combine(Path.GetTempPath(), "plinth-cli-" + Guid.NewGuid().ToString("N"));
        var testAssembly = typeof(CommandLineTests).Assembly.Location;
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        // 用测试宿主的依赖清单运行应用真实入口，不启动测试宿主自身。
        foreach (var value in new[]
        {
            "exec", "--runtimeconfig", Path.ChangeExtension(testAssembly, ".runtimeconfig.json"),
            "--depsfile", Path.ChangeExtension(testAssembly, ".deps.json"),
            typeof(AppInfo).Assembly.Location, argument,
        })
            start.ArgumentList.Add(value);
        start.Environment["PLINTH_DATA_DIR"] = dataDirectory;
        start.Environment.Remove("DISPLAY");
        start.Environment.Remove("WAYLAND_DISPLAY");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start CLI test process.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await process.WaitForExitAsync(timeout.Token);
            Assert.Equal(expectedCode, process.ExitCode);
            if (expectedCode == 0)
            {
                Assert.Equal($"{AppInfo.ProductName} {AppInfo.Version}", (await output).Trim());
                Assert.Equal("", await error);
            }
            else
            {
                Assert.Equal("", await output);
                Assert.Contains("--version", await error);
            }
            Assert.False(Directory.Exists(dataDirectory));
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
            if (Directory.Exists(dataDirectory)) Directory.Delete(dataDirectory, recursive: true);
        }
    }

    private sealed class BrokenWriter : StringWriter
    {
        public override void WriteLine(string? value) => throw new IOException("Pipe closed.");
    }
}
