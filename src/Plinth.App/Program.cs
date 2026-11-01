using System;
using Avalonia;
using Avalonia.Logging;
using Plinth.App.Diagnostics;
using Serilog;
using Serilog.Formatting.Compact;

namespace Plinth.App;

internal static class Program
{
    /// <summary>日志文件上限:单文件 10 MB,按天滚动并滚动时切分,最多保留 10 个(§11.3)。</summary>
    private const long LogFileSizeLimitBytes = 10L * 1024 * 1024;

    private const int RetainedLogFileCount = 10;

    [STAThread]
    public static void Main(string[] args)
    {
        AppPaths.EnsureCreated();
        Log.Logger = CreateLogger();
        Log.Information("应用启动,版本 {Version},运行时 {Framework}", AppInfo.Version, AppInfo.FrameworkDescription);

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用启动失败");
            throw;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp()
    {
        // 替代模板的 .LogToTrace():Avalonia 自家日志也进 Serilog(§4.6.1、§11.3)
        Logger.Sink = new AvaloniaSerilogSink(Log.Logger);

        return AppBuilder.Configure<App>()
            .UsePlatformDetect()
#if DEBUG
            .WithDeveloperTools()
#endif
            ;
    }

    private static Serilog.ILogger CreateLogger() => new LoggerConfiguration()
        .MinimumLevel.Information()
        .Enrich.FromLogContext()
        .WriteTo.File(
            new CompactJsonFormatter(),
            AppPaths.LogFilePath,
            rollingInterval: RollingInterval.Day,
            fileSizeLimitBytes: LogFileSizeLimitBytes,
            rollOnFileSizeLimit: true,
            retainedFileCountLimit: RetainedLogFileCount)
        .CreateLogger();
}
