using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Plinth.App.Views;
using Serilog;

namespace Plinth.App.Services;

/// <summary>
/// UI 线程上没人处理的异常:写日志,并弹一个提示框,程序继续运行(不让用户一点按钮就整个闪退)。
/// 同一时间只弹一个框;提示框自己出错时只写日志,不会无限循环。
/// </summary>
public static class UnhandledErrorReporter
{
    private static bool _showing;

    public static void Report(Exception exception)
    {
        var error = UnhandledErrorPresentation.Create();
        Log.Error(exception, "UI unhandled error. ErrorCode {ErrorCode}, CorrelationId {CorrelationId}",
            error.Code, error.CorrelationId);

        if (_showing || Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime { MainWindow: { } owner })
        {
            return;
        }

        _showing = true;
        try
        {
            var dialog = new ErrorDialog(error);
            dialog.Closed += (_, _) => _showing = false;
            dialog.Show(owner);
        }
        catch (Exception ex)
        {
            _showing = false;
            Log.Error(ex, "Error dialog failed. ErrorCode {ErrorCode}, CorrelationId {CorrelationId}",
                error.Code, error.CorrelationId);
        }
    }
}
