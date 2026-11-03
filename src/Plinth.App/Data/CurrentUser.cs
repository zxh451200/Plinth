using System;

namespace Plinth.App.Data;

/// <summary>
/// "当前是谁在操作",审计字段用它填写。底座没有登录功能,默认取操作系统的登录名。
/// 二开接入自己的登录后,在启动时替换一次:<c>CurrentUser.Resolve = () =&gt; mySession.UserName;</c>
/// </summary>
public static class CurrentUser
{
    public static Func<string> Resolve { get; set; } = () => Environment.UserName;

    public static string Name => Resolve();
}
