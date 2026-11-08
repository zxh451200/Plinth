using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 表格表面:表头要是一条完整的带。Fluent 默认在表头外面套一层灰底 Border(上下左右各露一条),
/// 又在每列尾部画 1px 竖线,这两样在卡片表里都是脏线。
/// </summary>
[Collection("Localization")]
public sealed class TableSurfaceTests
{
    [AvaloniaFact]
    public void Header_band_is_one_continuous_strip_without_column_dividers()
    {
        Loc.Initialize(Loc.DefaultLanguage);
        using var shell = new Shell();
        var nav = shell.ViewModel.NavigationItems.Single(item => item.Id == ShellNavigation.TaskDiagnosticsId);
        shell.ViewModel.OpenTabCommand.Execute(nav);
        shell.Layout();

        var presenter = shell.Window.GetVisualDescendants()
            .First(visual => visual.GetType().Name == "TableViewColumnHeadersPresenter");
        var band = presenter.GetVisualAncestors().First();
        Assert.Equal(band.Bounds.Width, presenter.Bounds.Width, 1);
        Assert.Equal(band.Bounds.Height, presenter.Bounds.Height, 1);

        var resizers = shell.Window.GetVisualDescendants().OfType<Thumb>().Where(thumb => thumb.Name == "PART_Resizer").ToList();
        Assert.NotEmpty(resizers);
        Assert.All(resizers, resizer =>
        {
            Assert.True(resizer.IsVisible);
            Assert.True(resizer.IsHitTestVisible);
            Assert.Equal(0d, resizer.Opacity);
        });
        Assert.All(shell.Window.GetVisualDescendants().OfType<TableView>(),
            table => Assert.True(table.CanUserResizeColumns));
    }
}
