using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Plinth.App.Modules.Demo.Views;

/// <summary>
/// TableView cells receive their row as the data context. Only the selected row
/// may show the existing form draft; all other rows remain read-only.
/// </summary>
public sealed class InlineEditingVisibilityConverter : IMultiValueConverter
{
    public object Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var row = values.Count > 0 ? values[0] : null;
        var selected = values.Count > 1 ? values[1] : null;
        var canEdit = values.Count > 2 && values[2] is true;
        var isSelected = row is not null && selected is not null && ReferenceEquals(row, selected);
        var editor = string.Equals(parameter?.ToString(), "editor", StringComparison.Ordinal);
        return editor ? canEdit && isSelected : !canEdit || !isSelected;
    }
}
