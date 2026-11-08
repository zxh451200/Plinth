using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;

namespace Plinth.App.Services;

/// <summary>把生成的 PDF 交给系统默认处理程序的 print 动作；不引入打印 SDK。</summary>
public static class PdfPrinter
{
    internal static ProcessStartInfo BuildStartInfo(string path) =>
        new(path) { UseShellExecute = true, Verb = "print" };

    public static bool TryPrint(string path, out string error)
    {
        error = string.Empty;
        if (!OperatingSystem.IsWindows())
        {
            error = "unsupported";
            return false;
        }

        if (!File.Exists(path))
        {
            error = "missing";
            return false;
        }

        try
        {
            using var process = Process.Start(BuildStartInfo(path));
            return true;
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or IOException)
        {
            error = ex.GetType().Name;
            return false;
        }
    }
}
