using System;
using System.Globalization;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Views;
using Plinth.App.Views.Controls;
using Xunit;

namespace Plinth.App.Tests;

[Collection("Localization")]
public sealed class DateFieldTests
{
    [AvaloniaFact]
    public void Empty_field_uses_its_label_as_hint()
    {
        var field = new DateField { Label = "起始日期" };
        var window = new Window { Content = field, Width = 240, Height = 80 };
        window.Show();

        var value = field.FindControl<TextBlock>("ValueText")!;
        Assert.Equal("起始日期", value.Text);
        Assert.DoesNotContain("<", value.Text ?? string.Empty);
        // 与下拉共用同一套按下/悬停底色。
        Assert.Contains("field", field.GetVisualDescendants().OfType<Button>().First().Classes);

        field.SelectedDate = new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);
        Assert.Equal(
            new DateTimeOffset(2026, 11, 4, 0, 0, 0, TimeSpan.Zero).ToString("d", CultureInfo.CurrentCulture),
            value.Text);

        field.SelectedDate = null;
        Assert.Equal("起始日期", value.Text);
        window.Close();
    }

    [AvaloniaFact]
    public void Capture_log_filters()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var nav = shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.LogsId);
        shell.ViewModel.OpenTabCommand.Execute(nav);
        shell.Layout();
        var output = Path.Combine(Path.GetTempPath(), "plinth-date-filters");
        Directory.CreateDirectory(output);
        shell.Window.CaptureRenderedFrame()!.Save(Path.Combine(output, "plinth-date-filters.png"));

        var fields = shell.PageView<LogView>().GetVisualDescendants().OfType<DateField>().ToList();
        using var writer = new StreamWriter(Path.Combine(output, "plinth-date-filters.txt"));
        foreach (var field in fields)
        {
            writer.WriteLine(
                $"label={field.Label} text={field.FindControl<TextBlock>("ValueText")!.Text} bounds={field.Bounds} iconVisible={field.GetVisualDescendants().OfType<TextBlock>().Count(t => t.Text == "\uE787")}");
        }
    }
}
