using System;
using System.Runtime.InteropServices;

namespace Plinth.App.Services;

/// <summary>
/// 启动失败时主窗口还不存在，ErrorDialog 用不了；Windows 交互会话用原生消息框展示安全摘要，
/// 其余环境写 stderr。文本只含资源文案、错误码和关联 ID，不带原始异常。
/// </summary>
public static class StartupErrorReporter
{
    public static string BuildText(TaskError error) =>
        error.Message + Environment.NewLine + UnhandledErrorPresentation.Format(error);

    public static void Show(TaskError error)
    {
        var text = BuildText(error);
        if (OperatingSystem.IsWindows() && Environment.UserInteractive)
        {
            MessageBoxW(IntPtr.Zero, text, AppInfo.ProductName, 0x10);
            return;
        }
        Console.Error.WriteLine(text);
    }

    [DllImport("user32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern int MessageBoxW(IntPtr window, string text, string caption, uint type);
}
