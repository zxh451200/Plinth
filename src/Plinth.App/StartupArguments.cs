using System;

namespace Plinth.App;

/// <summary>
/// 只识别系统通知使用的固定 URI；其它参数仍交给 CommandLine 拒绝，不把外部输入当命令执行。
/// </summary>
public static class StartupArguments
{
    public const string TaskCenterUri = "plinth://tasks";

    public static (ActivationIntent Intent, string[] Args) Parse(string[] args)
    {
        if (args.Length == 1 && string.Equals(args[0], TaskCenterUri, StringComparison.OrdinalIgnoreCase))
        {
            return (ActivationIntent.TaskCenter, []);
        }

        return (ActivationIntent.Show, args);
    }
}
