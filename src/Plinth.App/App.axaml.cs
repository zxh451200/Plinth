using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Plinth.App.Data;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Plinth.App.Views;
using Serilog;

namespace Plinth.App;

public partial class App : Application
{
    private ServiceProvider? _services;
    private TrayIcon? _tray;

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
            desktop.Exit += (_, _) => DisposeServices();
        }

        services.AddPlinthModules();
        _services = services.BuildServiceProvider();
        var formatSettings = _services.GetRequiredService<SettingsService>().Current;
        FormattingService.Initialize(formatSettings.Region, formatSettings.TimeZoneId);
        var users = _services.GetRequiredService<Security.IRemoteUserContext>();
        CurrentUser.Resolve = () => users.CurrentUser.IsAuthenticated
            ? users.CurrentUser.Subject : Environment.UserName;

        // 先把数据库准备好再开窗口。失败会让程序明确地启动失败,不带着坏数据库继续运行
        _services.EnsureDatabase();

        // 按用户保存的设置应用主题
        _services.GetRequiredService<SettingsService>().ApplyTheme();
        DensityService.Apply(formatSettings.Density);
        _services.GetRequiredService<SettingsService>().Save();
        var branding = _services.GetRequiredService<BrandingService>();
        branding.ApplyAccent(this);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktopLifetime)
        {
            // 模板原本直接 new MainViewModel,这里改为从容器解析(§4.6.1 偏差表)
            var mainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>(),
                Icon = branding.CreateWindowIcon(),
            };
            desktopLifetime.MainWindow = mainWindow;
            _services.GetRequiredService<SensitiveOperationService>().ConfirmProductionAsync =
                mainWindow.ConfirmProductionAsync;
            desktopLifetime.ShutdownRequested += (_, e) =>
            {
                if (mainWindow.CloseApproved) return;
                e.Cancel = true;
                Dispatcher.UIThread.Post(mainWindow.Close);
            };

            // 恢复上次关闭时的窗口位置和大小,关闭时再记下来
            var settings = _services.GetRequiredService<SettingsService>();
            WindowPlacementStore.Restore(mainWindow, settings);
            mainWindow.Closing += (_, _) => WindowPlacementStore.Remember(mainWindow, settings);

            CreateTrayIcon(desktopLifetime, mainWindow, _services.GetRequiredService<BrandingService>());

            // 用户又启动了一次程序时,把已有的窗口带到前台
            if (Program.SingleInstance is { } singleInstance)
            {
                singleInstance.ActivationRequested += () => Dispatcher.UIThread.Post(() => BringToFront(desktopLifetime.MainWindow));
            }
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// 系统托盘图标:点一下显示窗口,右键菜单有「显示窗口」和「退出」。
    /// 关闭窗口仍然是退出程序(没有"最小化到托盘"),托盘只是个快捷入口。
    /// 有的 Linux 桌面没有托盘,创建失败只写日志,不影响启动。
    /// </summary>
    private void CreateTrayIcon(
        IClassicDesktopStyleApplicationLifetime desktop,
        Window mainWindow,
        BrandingService branding)
    {
        try
        {
            var show = new NativeMenuItem(Loc.Get("tray.show"));
            show.Click += (_, _) => BringToFront(mainWindow);
            var exit = new NativeMenuItem(Loc.Get("tray.exit"));
            exit.Click += (_, _) => mainWindow.Close();

            _tray = new TrayIcon
            {
                Icon = branding.CreateWindowIcon(),
                ToolTipText = branding.Current.ProductName,
                Menu = [show, new NativeMenuItemSeparator(), exit],
            };
            _tray.Clicked += (_, _) => BringToFront(mainWindow);
            TrayIcon.SetIcons(this, [_tray]);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "创建托盘图标失败,继续不带托盘运行");
        }
    }

    private static void BringToFront(Window? window)
    {
        if (window is null)
        {
            return;
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        window.Activate();
    }

    private void DisposeServices()
    {
        _tray?.Dispose();
        _tray = null;
        _services?.Dispose();
        _services = null;
    }

    /// <summary>
    /// 三个异常钩子本地落盘,不自动上报(§11.3)。原生层崩溃只能尽力而为。
    /// </summary>
    private static void RegisterUnhandledExceptionHandlers()
    {
        // 标记为已处理:程序继续运行,用户看到提示框,详情在日志里
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            e.Handled = true;
            UnhandledErrorReporter.Report(e.Exception);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error(e.Exception, "未观察到的 Task 异常");
            e.SetObserved();
        };

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Fatal(e.ExceptionObject as Exception, "AppDomain 未处理异常");
    }
}
