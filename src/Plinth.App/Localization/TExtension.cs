using System;

namespace Plinth.App.Localization;

/// <summary>
/// XAML 里取多语言文字:Text="{loc:T demo.new}"(顶部加 xmlns:loc="using:Plinth.App.Localization")。
/// 没有继承任何基类,Avalonia 的 XAML 加载器按 ProvideValue 方法的形状认它。
/// </summary>
public sealed class TExtension(string key)
{
    public string Key { get; } = key;

    public object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}
