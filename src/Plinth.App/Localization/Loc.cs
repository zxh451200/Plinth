using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Globalization;
using System.Linq;
using System.Resources;
using Avalonia.Threading;
using Serilog;

[assembly: NeutralResourcesLanguage("en")]

namespace Plinth.App.Localization;

/// <summary>
/// 界面文字(多语言)入口(规范 §9)。文字放在 Resources/Strings.resx(英文)和 Strings.zh-CN.resx(中文)里,
/// 代码里写 Loc.Get("nav.overview"),XAML 里写 {loc:T nav.overview}。
/// 切换语言会通知所有订阅者,已打开的窗口和页面立即刷新,不需要重启。
/// </summary>
public static class Loc
{
    public const string DefaultLanguage = "zh-CN";

    public static IReadOnlyList<string> SupportedLanguages { get; } = ["zh-CN", "en-US"];

    private static readonly ResourceManager Manager = new("Plinth.App.Resources.Strings", typeof(Loc).Assembly);

    private static readonly ConcurrentDictionary<string, bool> ReportedMissing = new();

    // 弱引用订阅:页面/窗口的生命周期由界面控制,语言是静态服务,不能把已关闭的对象拖住。
    private static readonly List<WeakReference<Action>> Subscribers = [];

    private static CultureInfo _culture = CreateCulture(DefaultLanguage);

    /// <summary>当前界面语言对应的 CultureInfo。</summary>
    public static CultureInfo Culture => _culture;

    /// <summary>启动时在界面创建之前调用;切换语言用 <see cref="SetLanguage"/>。
    /// language 不认识或为空时用默认语言。</summary>
    public static void Initialize(string? language) => SetLanguage(language);

    /// <summary>切换界面语言并通知已打开的界面;语言相同时不做任何事。</summary>
    public static void SetLanguage(string? language)
    {
        var name = language is not null && SupportedLanguages.Contains(language) ? language : DefaultLanguage;
        var culture = CreateCulture(name);
        if (string.Equals(culture.Name, _culture.Name, StringComparison.OrdinalIgnoreCase)) return;
        _culture = culture;
        // 地区/时区名称等 .NET 显示名按 UI 语言本地化；业务数字和日期仍用用户选的地区格式。
        CultureInfo.DefaultThreadCurrentUICulture = _culture;
        CultureInfo.CurrentUICulture = _culture;
        PublishLanguageChanged();
    }

    /// <summary>订阅语言变化;投递方不持有强引用,订阅者失效后自动清理。</summary>
    public static void Subscribe(Action handler)
    {
        lock (Subscribers) Subscribers.Add(new WeakReference<Action>(handler));
    }

    public static void Unsubscribe(Action handler)
    {
        lock (Subscribers)
            Subscribers.RemoveAll(reference =>
                !reference.TryGetTarget(out var target) || target == handler);
    }

    private static void PublishLanguageChanged()
    {
        Action[] handlers;
        lock (Subscribers)
        {
            var live = new List<Action>(Subscribers.Count);
            var dead = new List<WeakReference<Action>>();
            foreach (var reference in Subscribers)
            {
                if (reference.TryGetTarget(out var handler)) live.Add(handler);
                else dead.Add(reference);
            }
            foreach (var reference in dead) Subscribers.Remove(reference);
            handlers = live.ToArray();
        }
        // 界面绑定只能在 UI 线程更新;非 UI 线程发起切换时投递过去。
        if (Dispatcher.UIThread.CheckAccess()) Invoke(handlers);
        else Dispatcher.UIThread.Post(() => Invoke(handlers));
    }

    private static void Invoke(Action[] handlers)
    {
        foreach (var handler in handlers)
        {
            try { handler(); }
            catch (Exception ex) { Log.Warning(ex, "语言切换后刷新界面失败"); }
        }
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
