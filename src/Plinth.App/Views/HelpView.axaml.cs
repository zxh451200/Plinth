using Avalonia.Controls;
using Plinth.App.Navigation;

namespace Plinth.App.Views;

public partial class HelpView : UserControl, IPageSearchTarget
{
    public HelpView() => InitializeComponent();
    public void FocusSearch() => HelpSearch.Focus();
}
