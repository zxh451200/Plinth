using Avalonia.Controls;
using Avalonia.Input;
using Plinth.App.Navigation;
using Plinth.App.ViewModels;

namespace Plinth.App.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
    }

    /// <summary>
    /// 已经选中的导航项再点一次不会触发 SelectedItem 变化,这里补一次打开;
    /// OpenTab 对同一个 Id 是幂等的,不会重复开标签。
    /// </summary>
    private void OnNavigationTapped(object? sender, TappedEventArgs e)
    {
        if (DataContext is MainViewModel viewModel
            && sender is ListBox { SelectedItem: NavigationItem item })
        {
            viewModel.OpenTabCommand.Execute(item);
        }
    }
}
