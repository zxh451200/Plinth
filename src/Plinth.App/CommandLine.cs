using System.IO;
using Plinth.App.Localization;

namespace Plinth.App;

/// <summary>只处理启动前参数；null 表示继续启动桌面，不读取用户配置。</summary>
public static class CommandLine
{
    public static int? Handle(string[] args, TextWriter output, TextWriter error)
    {
        if (args.Length == 0) return null;
        try
        {
            if (args.Length == 1 && args[0] == "--version")
            {
                output.WriteLine($"{AppInfo.ProductName} {AppInfo.Version}");
                return 0;
            }

            // 不回显原始参数，避免将凭据或控制字符写入终端。
            error.WriteLine(Loc.Get("cli.invalidArguments"));
            return 2;
        }
        catch (IOException)
        {
            // 管道已关闭或输出不可写时，以失败码退出，不再启动窗口。
            return 1;
        }
    }
}
