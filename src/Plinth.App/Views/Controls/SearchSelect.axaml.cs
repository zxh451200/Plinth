using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Media;
using Avalonia.Interactivity;
using Plinth.App.Services;

namespace Plinth.App.Views.Controls;

/// <summary>
/// 可搜索下拉:列表几百条时先过滤再选,避免在长列表里翻找。选项需实现 <see cref="ISelectOption"/>。
/// </summary>
public partial class SearchSelect : UserControl
{
    public static readonly StyledProperty<IEnumerable?> ItemsProperty =
        AvaloniaProperty.Register<SearchSelect, IEnumerable?>(nameof(Items));

    public static readonly StyledProperty<object?> SelectedItemProperty =
        AvaloniaProperty.Register<SearchSelect, object?>(
            nameof(SelectedItem), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<SearchSelect, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<SearchSelect, string>(nameof(Placeholder), string.Empty);

    public static readonly StyledProperty<bool> SearchableProperty =
        AvaloniaProperty.Register<SearchSelect, bool>(nameof(Searchable), true);

    private bool _syncing;

    public SearchSelect()
    {
        InitializeComponent();
        SearchBox.IsVisible = Searchable;
        // 弹层在独立根里,DynamicResource 的主题画刷取不到,这里从控件资源链兜底;
        // 阴影在弹层里会被祖先裁剪,所以用 1px 细边定住边界。
        PopupCard.Background = this.TryFindResource("PlinthPanelBrush", out var panel) && panel is IBrush brush
            ? brush
            : Brushes.White;
        PopupCard.BorderBrush = this.TryFindResource("PlinthBorderBrush", out var border) && border is IBrush borderBrush
            ? borderBrush
            : Brushes.LightGray;
        PopupCard.BorderThickness = new Thickness(1);
        // 容器回收时会用 null 调模板,这里必须空安全。
        OptionsList.ItemTemplate = new FuncDataTemplate<ISelectOption>((option, _) => new TextBlock
        {
            Text = option?.Title ?? string.Empty,
            TextTrimming = Avalonia.Media.TextTrimming.CharacterEllipsis,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
            Margin = new Thickness(12, 0),
        });
        // 用属性变化而不是 TextChanged:后者只在用户键入时触发,程序赋值不会。
        SearchBox.PropertyChanged += (_, args) =>
        {
            if (args.Property == TextBox.TextProperty && !_syncing) ApplyFilter();
        };
    }

    public IEnumerable? Items
    {
        get => GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    public object? SelectedItem
    {
        get => GetValue(SelectedItemProperty);
        set => SetValue(SelectedItemProperty, value);
    }

    public string Label
    {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public string Placeholder
    {
        get => GetValue(PlaceholderProperty);
        set => SetValue(PlaceholderProperty, value);
    }

    /// <summary>短列表可以关掉搜索框,复用同一套弹层外观。</summary>
    public bool Searchable
    {
        get => GetValue(SearchableProperty);
        set => SetValue(SearchableProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ItemsProperty) ApplyFilter();
        if (change.Property == SelectedItemProperty) UpdateValueText();
        if (change.Property == LabelProperty) AutomationProperties.SetName(FieldButton, Label);
        if (change.Property == SearchableProperty) SearchBox.IsVisible = Searchable;
    }

    private void UpdateValueText()
    {
        var text = (SelectedItem as ISelectOption)?.Title;
        ValueText.Text = string.IsNullOrEmpty(text) ? Placeholder : text;
        ValueText.Classes.Set("placeholder", string.IsNullOrEmpty(text));
        ToolTip.SetTip(this, string.IsNullOrEmpty(text) ? null : text);
    }

    private void OnFieldClick(object? sender, RoutedEventArgs e)
    {
        // 弹层固定成字段宽度:不随当前可见项的最长文字变化,滚动/过滤时窗口不会忽宽忽窄。
        var width = Math.Max(240, Bounds.Width);
        PopupCard.MinWidth = width;
        PopupCard.Width = width;
        SetSearchTextSilently(string.Empty);
        ApplyFilter();
    }

    private void SetSearchTextSilently(string text)
    {
        _syncing = true;
        try { SearchBox.Text = text; }
        finally { _syncing = false; }
    }

    /// <summary>按标题过滤,空查询返回全部;供下拉与单元测试共用同一套规则。</summary>
    internal static List<ISelectOption> Filter(IEnumerable? items, string? query)
    {
        var text = query?.Trim() ?? string.Empty;
        return (items ?? Array.Empty<object>()).OfType<ISelectOption>()
            .Where(option => text.Length == 0
                             || option.Title.Contains(text, StringComparison.CurrentCultureIgnoreCase))
            .ToList();
    }

    private void ApplyFilter()
    {
        var query = SearchBox.Text?.Trim() ?? string.Empty;
        var matches = Filter(Items, query).Cast<object>().ToList();

        // 每次过滤整体替换 ItemsSource:原地增删会让回收的容器错位、首行文字重叠。
        _syncing = true;
        try
        {
            OptionsList.ItemsSource = matches;
            OptionsList.SelectedItem = matches.FirstOrDefault(item => ReferenceEquals(item, SelectedItem));
        }
        finally
        {
            _syncing = false;
        }

        EmptyText.IsVisible = matches.Count == 0;
    }

    private void OnOptionSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || OptionsList.SelectedItem is not { } option) return;
        SelectedItem = option;
        FieldButton.Flyout?.Hide();
    }
}
