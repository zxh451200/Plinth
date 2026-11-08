using System;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using Avalonia.Media;
using Plinth.App.Localization;

namespace Plinth.App.Views.Controls;

/// <summary>
/// 紧凑日期选择:自绘月历弹层,替代 Fluent CalendarDatePicker——后者模板里写死了灰底灰格,样式改不动。
/// </summary>
public partial class DateField : UserControl
{
    public static readonly StyledProperty<DateTimeOffset?> SelectedDateProperty =
        AvaloniaProperty.Register<DateField, DateTimeOffset?>(
            nameof(SelectedDate), defaultBindingMode: BindingMode.TwoWay);

    public static readonly StyledProperty<string> LabelProperty =
        AvaloniaProperty.Register<DateField, string>(nameof(Label), string.Empty);

    public static readonly StyledProperty<string> PlaceholderProperty =
        AvaloniaProperty.Register<DateField, string>(nameof(Placeholder), string.Empty);

    private DateTime _month = DateTime.Today;

    public DateField()
    {
        InitializeComponent();
        PopupCard.Background = this.TryFindResource("PlinthPanelBrush", out var panel) && panel is IBrush brush
            ? brush
            : Brushes.White;
        PopupCard.BorderBrush = this.TryFindResource("PlinthBorderBrush", out var border) && border is IBrush borderBrush
            ? borderBrush
            : Brushes.LightGray;
        PopupCard.BorderThickness = new Thickness(1);
        BuildWeekdayHeader();
        BuildDays();
        UpdateValueText();
    }

    public DateTimeOffset? SelectedDate
    {
        get => GetValue(SelectedDateProperty);
        set => SetValue(SelectedDateProperty, value);
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

    private void OnFieldClick(object? sender, RoutedEventArgs e)
    {
        _month = (SelectedDate ?? DateTimeOffset.Now).Date;
        BuildDays();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == SelectedDateProperty) UpdateValueText();
        if (change.Property == LabelProperty)
        {
            AutomationProperties.SetName(FieldButton, Label);
            UpdateValueText();
        }
    }

    private void UpdateValueText()
    {
        // 没选日期时用字段名当提示(起始日期/截止日期),别把格式串丢给用户看。
        var hint = !string.IsNullOrWhiteSpace(Placeholder)
            ? Placeholder
            : !string.IsNullOrWhiteSpace(Label) ? Label : Loc.Get("date.placeholder");
        ValueText.Text = SelectedDate is { } date
            ? date.ToString("d", CultureInfo.CurrentCulture)
            : hint;
        ValueText.Classes.Set("placeholder", SelectedDate is null);
    }

    private void BuildWeekdayHeader()
    {
        WeekdayHeader.Children.Clear();
        var names = CultureInfo.CurrentCulture.DateTimeFormat.ShortestDayNames;
        for (var i = 0; i < 7; i++)
        {
            var day = (DayOfWeek)(((int)DayOfWeek.Monday + i) % 7);
            var label = new TextBlock { Text = names[(int)day], Classes = { "weekday" } };
            Grid.SetColumn(label, i);
            WeekdayHeader.Children.Add(label);
        }
    }

    private void BuildDays()
    {
        MonthText.Text = _month.ToString("Y", CultureInfo.CurrentCulture);
        DaysGrid.Children.Clear();
        var first = new DateTime(_month.Year, _month.Month, 1);
        var start = first.AddDays(-(((int)first.DayOfWeek + 6) % 7));
        var today = DateTime.Today;
        for (var i = 0; i < 42; i++)
        {
            var date = start.AddDays(i);
            var button = new Button { Content = date.Day, Tag = date, Classes = { "day" } };
            AutomationProperties.SetName(button, date.ToString("d", CultureInfo.CurrentCulture));
            if (date.Month != _month.Month) button.Classes.Add("other");
            if (date == today) button.Classes.Add("today");
            if (SelectedDate is { } selected && selected.Date == date) button.Classes.Add("selected");
            button.Click += OnDayClick;
            DaysGrid.Children.Add(button);
        }
    }

    private void OnDayClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: DateTime date }) return;
        SelectedDate = new DateTimeOffset(date);
        Close();
    }

    private void OnPreviousMonth(object? sender, RoutedEventArgs e) => ShiftMonth(-1);

    private void OnNextMonth(object? sender, RoutedEventArgs e) => ShiftMonth(1);

    private void ShiftMonth(int months)
    {
        _month = _month.AddMonths(months);
        BuildDays();
    }

    private void OnToday(object? sender, RoutedEventArgs e)
    {
        SelectedDate = new DateTimeOffset(DateTime.Today);
        Close();
    }

    private void OnClear(object? sender, RoutedEventArgs e)
    {
        SelectedDate = null;
        Close();
    }

    private void Close() => FieldButton.Flyout?.Hide();
}
