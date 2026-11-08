using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Serializer.SystemTextJson;
using Plinth.App.Modules.DockOptional;

namespace Plinth.DockDemo;

/// <summary>One factory, layout and content lifetime per workspace. Use on the UI thread.</summary>
public sealed class DockWorkspace : IDisposable
{
    private readonly DockSerializer _serializer;
    private bool _disposed;
    private bool _initializationStarted;

    private DockWorkspace(DemoDockFactory factory)
    {
        // Load the model assembly and its generated contracts before the serializer
        // snapshots DockJsonMetadata's registered resolvers.
        Factory = factory;
        _serializer = new DockSerializer();
    }

    public DemoDockFactory Factory { get; }
    public IRootDock Layout { get; private set; } = null!;
    public string? RecoveryReason { get; private set; }

    public static DockWorkspace CreateDefault() => CreateDefault(new DemoDockFactory());

    public static DockWorkspace CreateDefault(DemoDockFactory factory)
    {
        var workspace = new DockWorkspace(factory);
        try
        {
            workspace.Layout = workspace.Factory.CreateLayout();
            workspace._initializationStarted = true;
            workspace.Factory.InitLayout(workspace.Layout);
            return workspace;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public static Task<DockWorkspace> OpenAsync(DockLayoutStore store, CancellationToken cancellationToken = default) =>
        OpenAsync(store, new DemoDockFactory(), cancellationToken);

    public static async Task<DockWorkspace> OpenAsync(
        DockLayoutStore store, DemoDockFactory factory, CancellationToken cancellationToken = default)
    {
        var workspace = new DockWorkspace(factory);
        try
        {
            var fallback = workspace._serializer.Serialize<IRootDock>(workspace.Factory.CreateLayout());
            var loaded = await store.LoadAsync(fallback, cancellationToken);
            if (loaded.UsedFallback)
            {
                workspace.Layout = workspace.Factory.CreateLayout();
                workspace.RecoveryReason = loaded.FallbackReason;
            }
            else
            {
                workspace.Layout = workspace._serializer.Deserialize<IRootDock>(loaded.Layout)
                    ?? throw new InvalidDataException("No root layout.");
                DockLayoutValidation.Validate(workspace.Layout, workspace.Factory.Descriptions);
                // The primary window belongs to this process; only floating windows are restored.
                workspace.Layout.Window = null;
            }
            cancellationToken.ThrowIfCancellationRequested();
            workspace._initializationStarted = true;
            workspace.Factory.InitLayout(workspace.Layout);
            return workspace;
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Never reuse contexts/floating hosts from a failed restore in the fallback.
            workspace.Dispose();
            var fallback = CreateDefault(factory.CreateSibling());
            fallback.RecoveryReason = "dock-layout";
            return fallback;
        }
        catch
        {
            workspace.Dispose();
            throw;
        }
    }

    public Task<bool> TryLeaveBusinessPanesAsync() => Factory.TryLeaveBusinessPanesAsync();

    public Task SaveAsync(DockLayoutStore store, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Layout.Windows is { } windows)
            foreach (var window in windows.ToArray()) window.Save();
        Layout.DefaultDockable = Layout.ActiveDockable;
        // Snapshot on the UI thread before yielding to file I/O.
        return store.SaveAsync(_serializer.Serialize<IRootDock>(Layout), cancellationToken);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_initializationStarted && Layout is { } root && root.ExitWindows.CanExecute(null))
                root.ExitWindows.Execute(null);
        }
        finally { Factory.Dispose(); }
    }
}
