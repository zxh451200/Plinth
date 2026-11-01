using System;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Serilog;

namespace Plinth.App;

public partial class App : Application
{
    private ServiceProvider? _services;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted()
    {
        RegisterUnhandledExceptionHandlers();

        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders().AddSerilog(Log.Logger, dispose: false));

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            // 实例注册:退出命令要用到 lifetime,容器不负责释放它
            services.AddSingleton<IClassicDesktopStyleApplicationLifetime>(desktop);
            desktop.ShutdownRequested += (_, _) => DisposeServices();
        }

        services.AddPlinthModules();
        _services = services.BuildServiceProvider();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            // 模板原本直接 new MainViewModel,这里改为从容器解析(§4.6.1 偏差表)
            desktopLifetime.MainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
            };
        }

        base.OnFrameworkInitializationCompleted();
    }

    private void DisposeServices()
    {
        _services?.Dispose();
        _services = null;
    }

    /// <summary>
    /// 三个异常钩子本地落盘,不自动上报(§11.3)。原生层崩溃只能尽力而为。
    /// </summary>
    private static void RegisterUnhandledExceptionHandlers()
    {
        Dispatcher.UIThread.UnhandledException += (_, e) =>
            Log.Fatal(e.Exception, "UI 线程未处理异常");

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "未观察到的 Task 异常");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "AppDomain 未处理异常");
    }
}
