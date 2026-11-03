using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Resources;
using Serilog;

[assembly: NeutralResourcesLanguage("en")]

namespace Plinth.App.Localization;

/// <summary>
/// 界面文字(多语言)入口(规范 §9)。文字放在 Resources/Strings.resx(英文)和 Strings.zh-CN.resx(中文)里,
/// 代码里写 Loc.Get("nav.overview"),XAML 里写 {loc:T nav.overview}。
/// 语言在程序启动时定一次,改语言要重启程序才生效(设置页会提示)。
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "zh-CN";

    public static IReadOnlyList<string> SupportedLanguages { get; } = ["zh-CN", "en-US"];

    private static readonly ResourceManager Manager = new("Plinth.App.Resources.Strings", typeof(Loc).Assembly);

    private static readonly ConcurrentDictionary<string, bool> ReportedMissing = new();

    private static CultureInfo _culture = CreateCulture(DefaultLanguage);

    /// <summary>程序启动时调用一次,在界面创建之前。language 不认识或为空时用默认语言。</summary>
    public static void Initialize(string? language)
    {
        var name = language is not null && SupportedLanguages.Contains(language) ? language : DefaultLanguage;
        _culture = CreateCulture(name);
    }

    /// <summary>按 key 取当前语言的文字。找不到时返回 [key],界面上一眼能看出漏了,同时写一条警告日志(每个 key 只写一次)。</summary>
    public static string Get(string key)
    {
        var text = Manager.GetString(key, _culture);
        if (text is not null)
        {
            return text;
        }

        if (ReportedMissing.TryAdd(key, true))
        {
            Log.Warning("缺少多语言文字:{Key}", key);
        }

        return $"[{key}]";
    }

    /// <summary>取带 {0} 占位符的文字并填入参数。</summary>
    public static string Format(string key, params object?[] args) =>
        string.Format(_culture, Get(key), args);

    private static CultureInfo CreateCulture(string name)
    {
        try
        {
            return new CultureInfo(name);
        }
        catch (CultureNotFoundException)
        {
            // 系统没有安装区域数据(例如精简的容器)时,退回不变区域,让资源回落到默认(英文)文字,不让程序启动失败
            return CultureInfo.InvariantCulture;
        }
    }
}
