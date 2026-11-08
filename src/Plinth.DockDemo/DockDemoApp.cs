using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Themes.Fluent;
using Dock.Avalonia.Themes.Fluent;
using Dock.Model.Mvvm.Controls;
using Dock.Settings;

namespace Plinth.DockDemo;

public sealed class DockDemoApp : Application
{
    public override void Initialize()
    {
        // The demo saves before closing floating windows. Its window owns this sequence.
        DockSettings.CloseFloatingWindowsOnMainWindowClose = false;
        Styles.Add(new FluentTheme());
        Styles.Add(new DockFluentTheme());
        // Application templates also reach the library-created floating HostWindows.
        DataTemplates.Add(new FuncDataTemplate<Document>((model, _) => PaneView(model?.Context)));
        DataTemplates.Add(new FuncDataTemplate<Tool>((model, _) => PaneView(model?.Context)));
    }

    private static Control PaneView(object? context) => context is IDockPaneView business ? business.View : new TextBlock
    {
        Text = context is DockPane pane ? pane.Text : "窗格上下文不可用",
        Margin = new Thickness(16),
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
    };

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Plinth", "DockDemo");
            desktop.MainWindow = new DockDemoWindow(Path.Combine(directory, "layout.json"));
        }
        base.OnFrameworkInitializationCompleted();
    }
}
