using System.Linq;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Plinth.App.Views.Controls;

namespace Plinth.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        // 挂载时子控件还没进入可视树,等本轮布局结束后再找数字框。
        AttachedToVisualTree += (_, _) =>
            Dispatcher.UIThread.Post(EnableCompactSpinners, DispatcherPriority.Loaded);
    }

    private void EnableCompactSpinners()
    {
        foreach (var numeric in this.GetVisualDescendants().OfType<NumericUpDown>())
            CompactSpinner.SetIsEnabled(numeric, true);
    }
}
