using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
        // 容器回收时会用 null 调模板,这里必须空安全。
        OptionsList.ItemTemplate = new FuncDataTemplate<ISelectOption>((option, _) =>
        {
            var title = new TextBlock
            {
                Text = option?.Title ?? string.Empty,
                TextTrimming = TextTrimming.CharacterEllipsis,
                VerticalAlignment = VerticalAlignment.Center,
            };
            // 勾选标记由 ListBoxItem:selected 的样式点亮,模板里不绑状态。
            var check = new TextBlock { Text = "\uE73E", Classes = { "check" } };
            var row = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                Margin = new Thickness(12, 0),
            };
            row.Children.Add(title);
            row.Children.Add(check);
            Grid.SetColumn(check, 1);
            return row;
        });
        OptionsPopup.Opened += OnPopupOpened;
        OptionsPopup.Closed += OnPopupClosed;
        // 选中只在“点选/回车”时提交:上下键浏览不应该顺手把设置改掉。
        OptionsList.PointerReleased += OnOptionPointerReleased;
        OptionsList.KeyDown += OnOptionKeyDown;
        SearchBox.KeyDown += OnSearchKeyDown;
        // 隧道路由先于 Button 自带的回车/空格处理,否则回车会被当成一次点击把弹层关掉。
        FieldButton.AddHandler(KeyDownEvent, OnFieldKeyDown, RoutingStrategies.Tunnel);
        FieldButton.LostFocus += OnFieldLostFocus;
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

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        SyncPopupWidth();
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
        if (OptionsPopup.IsOpen)
        {
            ClosePopup();
            return;
        }
        OpenPopup();
    }

    private void OpenPopup()
    {
        SyncPopupWidth();
        SetSearchTextSilently(string.Empty);
        ApplyFilter();
        OptionsPopup.IsOpen = true;
    }

    /// <summary>弹层宽度跟字段走;在展开前就定好,避免打开瞬间重排导致的“跳一下”。</summary>
    private void SyncPopupWidth()
    {
        if (Bounds.Width <= 0) return;
        var width = Math.Max(240, Bounds.Width);
        PopupCard.MinWidth = width;
        PopupCard.Width = width;
    }

    private void OnPopupOpened(object? sender, EventArgs e)
    {
        FieldButton.Classes.Set("open", true);
        // 焦点留在字段上:方向键/回车由字段驱动,弹层不抢键盘。
        // 可搜索列表才把焦点送进搜索框,否则没法直接打字过滤。
        if (Searchable)
        {
            Dispatcher.UIThread.Post(() =>
            {
                SearchBox.Focus();
                SearchBox.CaretIndex = SearchBox.Text?.Length ?? 0;
            }, DispatcherPriority.Background);
        }
        // 长列表(时区)默认停在中段而不是顶部:等布局完成再滚。
        Dispatcher.UIThread.Post(() =>
        {
            if (OptionsList.SelectedIndex >= 0)
                OptionsList.ScrollIntoView(OptionsList.SelectedIndex);
        }, DispatcherPriority.Background);
    }

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        FieldButton.Classes.Set("open", false);
    }

    /// <summary>焦点离开字段本身不算收起;只有去了浮层外的其它控件才关闭。</summary>
    private void OnFieldLostFocus(object? sender, RoutedEventArgs e)
    {
        if (!OptionsPopup.IsOpen) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (OptionsPopup.IsOpen && !SearchBox.IsFocused && !OptionsList.IsKeyboardFocusWithin)
                ClosePopup(returnFocus: false);
        }, DispatcherPriority.Background);
    }

    private void OnFieldKeyDown(object? sender, KeyEventArgs e)
    {
        if (!OptionsPopup.IsOpen)
        {
            if (e.Key is not (Key.Down or Key.Up or Key.F4)) return;
            OpenPopup();
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Enter:
                CommitSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePopup();
                e.Handled = true;
                break;
        }
    }

    /// <summary>键盘浏览只移动列表选中项,不提交;回车才生效。</summary>
    private void MoveSelection(int delta)
    {
        var count = OptionsList.ItemCount;
        if (count == 0) return;
        var index = OptionsList.SelectedIndex;
        index = index < 0 ? 0 : (index + delta + count) % count;
        OptionsList.SelectedIndex = index;
        OptionsList.ScrollIntoView(index);
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                CommitSelection();
                e.Handled = true;
                break;
            // 搜索框里上下键移动高亮,不离开输入框,边打字边挑。
            case Key.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case Key.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePopup();
                e.Handled = true;
                break;
        }
    }

    private void OnOptionKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
            case Key.Space:
                CommitSelection();
                e.Handled = true;
                break;
            case Key.Escape:
                ClosePopup();
                e.Handled = true;
                break;
        }
    }

    private void OnOptionPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        // 点在滚动条空白处不算选;只有落在选项行上才提交。
        if (e.Source is not Visual source
            || source.FindAncestorOfType<ListBoxItem>(includeSelf: true) is null) return;
        CommitSelection();
    }

    private void CommitSelection()
    {
        if (OptionsList.SelectedItem is not { } option) return;
        SelectedItem = option;
        ClosePopup();
    }

    private void ClosePopup(bool returnFocus = true)
    {
        OptionsPopup.IsOpen = false;
        // 选完把焦点还给字段,键盘连续操作时不用再 Tab 回来。
        if (returnFocus) FieldButton.Focus();
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
        OptionsList.ItemsSource = matches;
        // 当前值不在结果里时预选首条,过滤后直接回车就能选中。
        OptionsList.SelectedItem = matches.FirstOrDefault(item => ReferenceEquals(item, SelectedItem))
                                   ?? matches.FirstOrDefault();

        EmptyText.IsVisible = matches.Count == 0;
    }
}
