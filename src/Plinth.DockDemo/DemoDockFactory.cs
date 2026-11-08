using Dock.Avalonia.Controls;
using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm;
using Dock.Model.Mvvm.Controls;
using Plinth.App.Modules.DockOptional;

namespace Plinth.DockDemo;

/// <summary>Only application layout/content; Dock supplies every docking operation.</summary>
public sealed class DemoDockFactory : Factory, IDisposable
{
    private readonly DockOptionalSession _session = new();
    private readonly Dictionary<string, DockPane> _panes = new(StringComparer.Ordinal);
    private readonly IReadOnlyDictionary<string, string> _descriptions;
    private readonly Func<string, DockPane> _paneFactory;
    private readonly HashSet<IDockBusinessPane> _closeChecks = [];
    private readonly HashSet<IDockable> _approvedCloses = [];

    public DemoDockFactory() : this(PaneDescriptions, id => new DockPane(PaneDescriptions[id])) { }

    public DemoDockFactory(IReadOnlyDictionary<string, string> descriptions, Func<string, DockPane> paneFactory)
    {
        _descriptions = descriptions;
        _paneFactory = paneFactory;
        HostWindowLocator = new()
        {
            [nameof(IDockWindow)] = () => new HostWindow(),
        };
        ContextLocator = new();
        DockableClosing += HandleDockableClosing;
        foreach (var id in _descriptions.Keys)
        {
            var pane = _session.Own(_paneFactory(id));
            _panes.Add(id, pane);
            ContextLocator[id] = () => pane;
        }
    }

    public static IReadOnlyDictionary<string, string> PaneDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["explorer"] = "资源浏览器\n\n拖动标题栏可停靠到其他边缘或浮动。拖动分隔条可调整宽度。",
            ["inspector"] = "属性检查器\n\n可与浏览器合并为工具页签，也可拖出成为独立浮窗。",
            ["welcome"] = "欢迎使用 Plinth Dock 示例\n\n拖动文档页签可重排、拆分或浮动。布局由 Dock.Avalonia 管理。",
            ["guide"] = "布局说明\n\n点击“保存布局”后重启可恢复布局；“重置布局”会恢复默认窗格。窗格内容为只读示例。",
        };

    public DockPane GetPane(string id) => _panes[id];
    public DemoDockFactory CreateSibling() => new(_descriptions, _paneFactory);
    public IReadOnlyDictionary<string, string> Descriptions => _descriptions;
    public IEnumerable<IDockBusinessPane> BusinessPanes => _panes.Values.OfType<IDockBusinessPane>();

    public async Task<bool> TryLeaveBusinessPanesAsync()
    {
        foreach (var pane in BusinessPanes)
            if (!await pane.TryLeaveAsync()) return false;
        return true;
    }

    public override IRootDock CreateLayout()
    {
        if (_descriptions.ContainsKey("demo") && _descriptions.ContainsKey("notes"))
            return CreateBusinessLayout();

        var explorer = new Tool { Id = "explorer", Title = "资源浏览器", CanClose = true };
        var inspector = new Tool { Id = "inspector", Title = "属性", CanClose = true };
        var welcome = new Document { Id = "welcome", Title = "欢迎", CanClose = true };
        var guide = new Document { Id = "guide", Title = "布局说明", CanClose = true };
        var tools = new ToolDock
        {
            Id = "tools",
            Title = "工具",
            Proportion = 0.25,
            Alignment = Alignment.Left,
            ActiveDockable = explorer,
            VisibleDockables = CreateList<IDockable>(explorer, inspector),
        };
        var documents = new DocumentDock
        {
            Id = "documents",
            Title = "文档",
            IsCollapsable = false,
            ActiveDockable = welcome,
            VisibleDockables = CreateList<IDockable>(welcome, guide),
        };
        var split = new ProportionalDock
        {
            Id = "workspace",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(tools, new ProportionalDockSplitter(), documents),
        };
        var root = CreateRootDock();
        root.Id = "root";
        root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(split);
        root.ActiveDockable = split;
        root.DefaultDockable = split;
        return root;
    }

    private IRootDock CreateBusinessLayout()
    {
        var demo = new Tool { Id = "demo", Title = "Demo", CanClose = true };
        var notes = new Document { Id = "notes", Title = "Notes", CanClose = true };
        var tools = new ToolDock
        {
            Id = "tools",
            Title = "工具",
            Proportion = 0.35,
            Alignment = Alignment.Left,
            ActiveDockable = demo,
            VisibleDockables = CreateList<IDockable>(demo)
        };
        var documents = new DocumentDock
        {
            Id = "documents",
            Title = "业务页面",
            IsCollapsable = false,
            ActiveDockable = notes,
            VisibleDockables = CreateList<IDockable>(notes)
        };
        var split = new ProportionalDock
        {
            Id = "workspace",
            Orientation = Orientation.Horizontal,
            VisibleDockables = CreateList<IDockable>(tools, new ProportionalDockSplitter(), documents)
        };
        var root = CreateRootDock();
        root.Id = "root"; root.IsCollapsable = false;
        root.VisibleDockables = CreateList<IDockable>(split);
        root.ActiveDockable = split; root.DefaultDockable = split;
        return root;
    }

    // OnClose is a veto hook. Dispose only after Dock confirms a successful close.
    private void HandleDockableClosing(object? sender, Dock.Model.Core.Events.DockableClosingEventArgs args)
    {
        var dockable = args.Dockable;
        if (dockable?.Context is IDockBusinessPane pane && !_approvedCloses.Remove(dockable))
        {
            args.Cancel = true;
            if (_closeChecks.Add(pane)) _ = CompleteBusinessCloseAsync(dockable, pane);
        }
    }

    private async Task CompleteBusinessCloseAsync(IDockable dockable, IDockBusinessPane pane)
    {
        try
        {
            if (await pane.TryLeaveAsync())
            {
                _approvedCloses.Add(dockable);
                CloseDockable(dockable);
            }
        }
        finally { _closeChecks.Remove(pane); }
    }

    public override void OnDockableClosed(IDockable? dockable)
    {
        if (dockable?.Context is DockPane pane) pane.Dispose();
        base.OnDockableClosed(dockable);
    }

    public override void OnWindowClosed(IDockWindow? window)
    {
        // A native floating window may close its whole root without individual pane events.
        if (window?.Layout is { } root)
            foreach (var dockable in Find(root, _ => true))
                if (dockable.Context is DockPane pane) pane.Dispose();
        base.OnWindowClosed(window);
    }

    public void Dispose()
    {
        DockableClosing -= HandleDockableClosing;
        ContextLocator?.Clear();
        HostWindowLocator?.Clear();
        _session.Dispose();
    }
}

public interface IDockBusinessPane : IDisposable
{
    Task<bool> TryLeaveAsync();
}

public interface IDockPaneView
{
    Avalonia.Controls.Control View { get; }
}

public class DockPane(string text) : IDisposable
{
    public string Text { get; } = text;
    public bool IsDisposed { get; private set; }
    public virtual void Dispose() => IsDisposed = true;
}
