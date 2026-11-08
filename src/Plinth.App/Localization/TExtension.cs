using System;
using System.ComponentModel;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;

namespace Plinth.App.Localization;

/// <summary>
/// XAML 里取多语言文字:Text="{loc:T demo.new}"(顶部加 xmlns:loc="using:Plinth.App.Localization")。
/// 返回一条绑定到语言版本号的单向绑定,切换语言后所有文字立即重新取值。
/// </summary>
public sealed class TExtension(string key)
{
    public string Key { get; } = key;

    public object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Source = LocalizedTextSource.Instance,
        Path = nameof(LocalizedTextSource.Version),
        Mode = BindingMode.OneWay,
        Converter = new LocalizedTextConverter(Key),
    };
}

/// <summary>语言版本号:切换语言时自增,驱动全部 {loc:T} 绑定刷新。</summary>
public sealed class LocalizedTextSource : INotifyPropertyChanged
{
    public static LocalizedTextSource Instance { get; } = new();

    private int _version;

    // 静态字段持住委托:Loc 只存弱引用,没有强引用时回调可能被回收。
    private static readonly Action BumpHandler = Instance.Bump;

    static LocalizedTextSource() => Loc.Subscribe(BumpHandler);

    public int Version => _version;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Bump()
    {
        _version++;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Version)));
    }
}

internal sealed class LocalizedTextConverter(string key) : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => Loc.Get(key);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
