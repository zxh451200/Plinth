using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.VisualTree;
using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;
using Plinth.App.Modules.DockOptional;
using Plinth.DockDemo;
using Xunit;

[assembly: AvaloniaTestApplication(typeof(Plinth.DockDemo.Tests.TestAppBuilder))]
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Plinth.DockDemo.Tests;

public static class TestAppBuilder
{
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<DockDemoApp>().UseHeadless(new AvaloniaHeadlessPlatformOptions());
}

public sealed class DockUiTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"plinth-dock-ui-{Guid.NewGuid():N}");
    private DockLayoutStore Store => new(Path.Combine(_directory, "layout.json"));

    [AvaloniaFact]
    public void Real_control_attaches_to_factory_and_detaches_without_leaking_registration()
    {
        using var workspace = DockWorkspace.CreateDefault();
        // 与 DockDemoWindow.Attach 一致：先挂到树上再赋 Factory/Layout，
        // DockControl 内部订阅依赖控件已进入可视树。
        var dock = new DockControl
        {
            InitializeFactory = false,
            InitializeLayout = false,
        };
        var window = new Window { Content = dock, Width = 900, Height = 600 };
        try
        {
            window.Show();
            window.UpdateLayout();
            dock.Factory = workspace.Factory;
            dock.Layout = workspace.Layout;
            window.UpdateLayout();
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            window.UpdateLayout();
            Assert.Same(workspace.Factory, workspace.Layout.Factory);
            Assert.Contains(dock, workspace.Factory.DockControls);
            Assert.True(dock.Bounds.Width > 0);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(),
                text => text.Text?.Contains("欢迎使用 Plinth Dock 示例", StringComparison.Ordinal) == true);
            dock.Layout = null;
            dock.Factory = null;
            Assert.DoesNotContain(dock, workspace.Factory.DockControls);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Vendor_serializer_round_trip_restores_order_and_runtime_contexts()
    {
        using (var first = DockWorkspace.CreateDefault())
        {
            var documents = Documents(first);
            var last = documents.VisibleDockables![1];
            documents.VisibleDockables.RemoveAt(1);
            documents.VisibleDockables.Insert(0, last);
            documents.ActiveDockable = last;
            await first.SaveAsync(Store);
        }
        using var second = await DockWorkspace.OpenAsync(Store);
        Assert.Null(second.RecoveryReason);
        Assert.Equal("guide", Documents(second).VisibleDockables![0].Id);
        Assert.Equal("guide", Documents(second).ActiveDockable!.Id);
        Assert.Same(second.Factory, Documents(second).Factory);
        Assert.Same(second.Factory.GetPane("guide"), Documents(second).ActiveDockable!.Context);
    }

    [AvaloniaFact]
    public async Task Syntactically_valid_but_invalid_vendor_payload_falls_back()
    {
        await Store.SaveAsync("{}");
        using var workspace = await DockWorkspace.OpenAsync(Store);
        Assert.Equal("dock-layout", workspace.RecoveryReason);
        Assert.Equal(2, Documents(workspace).VisibleDockables!.Count);
    }

    [AvaloniaFact]
    public async Task Unknown_pane_in_a_real_serialized_layout_falls_back()
    {
        using (var first = DockWorkspace.CreateDefault())
        {
            Documents(first).VisibleDockables![0].Id = "unsupported-pane";
            await first.SaveAsync(Store);
        }
        using var restored = await DockWorkspace.OpenAsync(Store);
        Assert.Equal("dock-layout", restored.RecoveryReason);
        Assert.Equal("welcome", Documents(restored).VisibleDockables![0].Id);
    }

    [AvaloniaFact]
    public async Task Cyclic_vendor_tree_is_rejected_before_initialization()
    {
        using (var first = DockWorkspace.CreateDefault())
        {
            first.Layout.VisibleDockables!.Add(first.Layout);
            await first.SaveAsync(Store);
            first.Layout.VisibleDockables.Remove(first.Layout);
        }
        using var restored = await DockWorkspace.OpenAsync(Store);
        Assert.Equal("dock-layout", restored.RecoveryReason);
        Assert.Equal(2, Documents(restored).VisibleDockables!.Count);
    }

    [AvaloniaFact]
    public void Successful_close_releases_context_but_a_veto_preserves_it()
    {
        using var workspace = DockWorkspace.CreateDefault();
        var document = Documents(workspace).VisibleDockables![0];
        var pane = workspace.Factory.GetPane(document.Id);
        void Veto(object? sender, Dock.Model.Core.Events.DockableClosingEventArgs args) => args.Cancel = true;
        workspace.Factory.DockableClosing += Veto;
        workspace.Factory.CloseDockable(document);
        Assert.False(pane.IsDisposed);
        Assert.Contains(document, Documents(workspace).VisibleDockables!);
        workspace.Factory.DockableClosing -= Veto;
        workspace.Factory.CloseDockable(document);
        Assert.True(pane.IsDisposed);
        Assert.DoesNotContain(document, Documents(workspace).VisibleDockables!);
        workspace.Dispose();
        Assert.All(DemoDockFactory.PaneDescriptions.Keys,
            id => Assert.True(workspace.Factory.GetPane(id).IsDisposed));
    }

    [AvaloniaFact]
    public async Task Cancellation_propagates_instead_of_opening_a_fallback_workspace()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            DockWorkspace.OpenAsync(Store, new CancellationToken(true)));
    }

    [AvaloniaFact]
    public async Task Floating_window_layout_is_serialized_and_exited_on_disposal()
    {
        var workspace = DockWorkspace.CreateDefault();
        var document = Documents(workspace).VisibleDockables![0];
        var window = workspace.Factory.CreateWindowFrom(document);
        Assert.NotNull(window);
        // Move using Dock's API, as UI docking would; do not construct a custom floating engine.
        workspace.Factory.RemoveDockable(document, true);
        workspace.Factory.AddWindow(workspace.Layout, window);
        var host = Assert.IsType<HostWindow>(window.Host);
        var closed = false;
        host.Closed += (_, _) => closed = true;
        try
        {
            window.Width = 480;
            window.Height = 320;
            window.Present(false);
            Assert.True(host.IsVisible);
            await workspace.SaveAsync(Store);
            var json = await Store.LoadAsync("{}");
            var layout = new DockSerializer().Deserialize<IRootDock>(json.Layout);
            Assert.NotNull(layout?.Windows);
            Assert.Single(layout.Windows);
        }
        finally
        {
            workspace.Dispose();
            workspace.Dispose();
        }
        Assert.True(closed);
        Assert.False(host.IsVisible);
        Assert.DoesNotContain(host, workspace.Factory.HostWindows);
        Assert.All(DemoDockFactory.PaneDescriptions.Keys,
            id => Assert.True(workspace.Factory.GetPane(id).IsDisposed));
        using var restored = await DockWorkspace.OpenAsync(Store);
        Assert.Null(restored.RecoveryReason);
        var restoredWindow = Assert.Single(restored.Layout.Windows!);
        var restoredHost = Assert.IsType<HostWindow>(restoredWindow.Host);
        Assert.True(restoredHost.IsVisible);
        restored.Dispose();
        Assert.False(restoredHost.IsVisible);
    }

    private static IDocumentDock Documents(DockWorkspace workspace) =>
        (IDocumentDock)workspace.Factory.FindDockable(workspace.Layout, node => node is IDocumentDock)!;

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
