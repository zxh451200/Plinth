using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>原生下拉与菜单弹层的首条目必须贴住上边,选中/悬停底色才不会有顶部留白。</summary>
public sealed class PopupBleedTests
{
    [AvaloniaFact]
    public void Combo_box_rows_start_at_the_popup_top()
    {
        var combo = new ComboBox
        {
            ItemsSource = new[] { "标准", "紧凑", "舒适" },
            SelectedIndex = 0,
            Width = 180,
        };
        var window = new Window { Content = combo, Width = 400, Height = 300 };
        window.Show();
        combo.IsDropDownOpen = true;
        LayOut(window);

        var child = (Border)combo.GetVisualDescendants().OfType<Popup>().First(popup => popup.IsOpen).Child!;
        var first = child.GetVisualDescendants().OfType<ComboBoxItem>().First();
        var origin = first.TranslatePoint(new Point(0, 0), child)!.Value;

        Assert.Equal(child.BorderThickness.Top, origin.Y, 1);
        window.Close();
    }

    [AvaloniaFact]
    public void Menu_items_start_at_the_popup_top()
    {
        var item = new MenuItem { Header = "文件" };
        item.Items.Add(new MenuItem { Header = "新建" });
        var menu = new Menu();
        menu.Items.Add(item);
        var window = new Window { Content = menu, Width = 400, Height = 300 };
        window.Show();
        item.IsSubMenuOpen = true;
        LayOut(window);

        var child = (Border)item.GetVisualDescendants().OfType<Popup>().First(popup => popup.IsOpen).Child!;
        var first = child.GetVisualDescendants().OfType<MenuItem>().First();
        var origin = first.TranslatePoint(new Point(0, 0), child)!.Value;

        Assert.Equal(child.BorderThickness.Top, origin.Y, 1);
        window.Close();
    }

    private static void LayOut(Window window)
    {
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.Measure(new Size(window.Width, window.Height));
        window.Arrange(new Rect(0, 0, window.Width, window.Height));
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
    }
}
