using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.ViewModels;

namespace Plinth.App.Modules.Demo.Views;

/// <summary>枚举单元格显示当前界面语言的优先级名称。</summary>
public sealed class PriorityLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DemoPriority priority ? DemoListViewModel.PriorityLabel(priority) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
