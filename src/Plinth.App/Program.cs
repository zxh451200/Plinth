using System;
using System.IO;
using System.Threading;
using Avalonia;
using Avalonia.Logging;
using Plinth.App.Diagnostics;
using Serilog;
using Serilog.Formatting.Compact;
using Serilog.Debugging;
using Plinth.App.Localization;
using Plinth.App.Services;

namespace Plinth.App;

internal static class Program
{
    /// <summary>日志文件上限:单文件 10 MB,按天滚动并滚动时切分,最多保留 10 个(§11.3)。</summary>
    private const long LogFileSizeLimitBytes = 10L * 1024 * 1024;

    private static int RetainedLogFileCount => SettingsService.ReadLogRetention().Files;

    /// <summary>
    /// 负责接收"第二次启动"的通知。界面创建后由 App 订阅它,把窗口带到前台。只在第一个进程里有值。
    /// </summary>
    internal static InstanceActivator? SingleInstance { get; private set; }

    /// <summary>首个实例启动时要打开的受控入口;系统通知点击只允许任务中心。</summary>
    internal static ActivationIntent StartupIntent { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        (StartupIntent, args) = StartupArguments.Parse(args);

        // 必须先于单实例、数据目录、日志、恢复和 Avalonia，供无显示器环境查询。
        if (args.Length > 0)
        {
            CommandLineConsole.AttachParentIfNeeded();
            return CommandLine.Handle(args, Console.Out, Console.Error) ?? 2;
        }

        // 版本查询不访问安装锁；普通启动在打开数据和 UI 之前持有只读句柄直到退出。
        FileStream? installationLease = null;
        if (OperatingSystem.IsWindows())
        {
            try { installationLease = InstallationGuard.Acquire(AppContext.BaseDirectory); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                InstallationGuard.ShowUnavailable();
                return 3;
            }
        }
        using var installationLifetime = installationLease;

        // 默认只允许同时运行一个程序,避免两个进程同时写同一个 SQLite 数据库(规范 §15.3)
        using var singleInstanceLock = new Mutex(initiallyOwned: true, $@"Local\Plinth.{InstanceActivator.InstanceId}", out var isFirstInstance);
        if (!isFirstInstance)
        {
            // 已经有一个在运行了:通知它把窗口带到前台（系统通知点击还要打开任务中心），自己直接退出
            InstanceActivator.TryNotifyRunningInstance(StartupIntent);
            return 0;
        }

        TaskCompletionNotifier.Enabled = true;

        var fallback = new LogWriteFallback(Console.Error);
        SelfLog.Enable(fallback.ReportFailure);
        Log.Logger = new LoggerConfiguration().WriteTo.Sink(fallback).CreateLogger();
        try
        {
            SingleInstance = new InstanceActivator();
            SingleInstance.StartListening();

            // 数据不可用仍中止启动；仅日志目录故障允许降级。
            Directory.CreateDirectory(AppPaths.DataDirectory);
            var bootstrapLogger = Log.Logger;
            Log.Logger = CreateLogger(fallback);
            (bootstrapLogger as IDisposable)?.Dispose();

            // 界面语言在启动时定一次(读设置文件里的语言),改语言要重启才生效
            Loc.Initialize(SettingsService.ReadLanguage());

            // 上次在"备份与恢复"页选了恢复备份的话,现在、在数据库被打开之前换上
            BackupService.ApplyPendingRestore();
            Loc.Initialize(SettingsService.ReadLanguage());

            // 替代模板的 .LogToTrace():Avalonia 自家日志也进 Serilog(§4.6.1、§11.3)。
            // 必须在 Log.Logger 就位之后装,且不能放进 BuildAvaloniaApp:设计器会直接调那个方法。
            Logger.Sink = new AvaloniaSerilogSink();

            Log.Information("应用启动,版本 {Version},运行时 {Framework}", AppInfo.Version, AppInfo.FrameworkDescription);

            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "应用启动失败");
            throw;
        }
        finally
        {
            try { SingleInstance?.Dispose(); }
            finally
            {
                SingleInstance = null;
                try { Log.CloseAndFlush(); }
                catch (Exception) { fallback.ReportFailure(); }
                finally { SelfLog.Disable(); }
            }
        }
    }

    // Avalonia configuration, don't remove; also used by visual designer.
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
#if DEBUG
        .WithDeveloperTools()
#endif
        ;

    private static Serilog.ILogger CreateLogger(LogWriteFallback fallback)
    {
        var minimum = DeploymentConfiguration.Current.TechnicalLogLevel;
        // 审计使用 Information，不能被较高的技术日志阈值丢弃。
        var pipelineMinimum = minimum < Serilog.Events.LogEventLevel.Information
            ? minimum : Serilog.Events.LogEventLevel.Information;
        var configuration = new LoggerConfiguration()
            .MinimumLevel.Is(pipelineMinimum)
            .Enrich.FromLogContext()
            .WriteTo.Sink(fallback);
        try
        {
            Directory.CreateDirectory(AppPaths.LogDirectory);
            var fileLogger = new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.File(
                new CompactJsonFormatter(),
                AppPaths.LogFilePath,
                rollingInterval: RollingInterval.Day,
                fileSizeLimitBytes: LogFileSizeLimitBytes,
                rollOnFileSizeLimit: true,
                retainedFileCountLimit: RetainedLogFileCount,
                retainedFileTimeLimit: TimeSpan.FromDays(SettingsService.ReadLogRetention().Days))
                .CreateLogger();
            return configuration.WriteTo.Sink(new RedactingLogSink(fileLogger, minimum)).CreateLogger();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or System.Security.SecurityException)
        {
            fallback.ReportFailure();
            return new LoggerConfiguration()
                .MinimumLevel.Is(pipelineMinimum)
                .Enrich.FromLogContext()
                .WriteTo.Sink(fallback)
                .CreateLogger();
        }
    }
}
