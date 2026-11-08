using Dock.Model.Controls;
using Dock.Model.Core;
using Dock.Model.Mvvm.Controls;

namespace Plinth.DockDemo;

internal static class DockLayoutValidation
{
    public static void Validate(IRootDock root) => Validate(root, DemoDockFactory.PaneDescriptions);

    public static void Validate(IRootDock root, IReadOnlyDictionary<string, string> descriptions)
    {
        if (root.Id != "root") throw new InvalidDataException("Unexpected layout root.");
        var ancestors = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        var visited = new HashSet<IDockable>(ReferenceEqualityComparer.Instance);
        var panes = new HashSet<string>(StringComparer.Ordinal);
        Visit(root, 0);

        void Visit(IDockable node, int depth)
        {
            if (depth > 64 || !ancestors.Add(node))
                throw new InvalidDataException("Cyclic or overly deep layout.");
            if (visited.Add(node))
            {
                if (visited.Count > 1024) throw new InvalidDataException("Oversized layout.");
                if (node.GetType() != typeof(RootDock) && node.GetType() != typeof(ProportionalDock)
                    && node.GetType() != typeof(ProportionalDockSplitter)
                    && node.GetType() != typeof(ToolDock) && node.GetType() != typeof(DocumentDock)
                    && node.GetType() != typeof(Tool) && node.GetType() != typeof(Document))
                    throw new InvalidDataException("Unsupported dockable type.");
                if (node is Tool or Document)
                {
                    if (!descriptions.ContainsKey(node.Id) || !panes.Add(node.Id)
                        || (node is Tool) != (node.Id is "explorer" or "inspector" or "demo"))
                        throw new InvalidDataException("Unknown or duplicate pane.");
                }
                // Context is runtime-only and rebuilt from our explicit locator.
                node.Context = null;
                if (node is IDock dock)
                {
                    VisitList(dock.VisibleDockables, depth);
                    if (dock.ActiveDockable is { } active && dock.VisibleDockables?.Contains(active) != true)
                        dock.ActiveDockable = dock.VisibleDockables?.FirstOrDefault();
                    if (dock.DefaultDockable is { } initial && dock.VisibleDockables?.Contains(initial) != true)
                        dock.DefaultDockable = dock.ActiveDockable;
                }
                if (node is IRootDock nested)
                {
                    VisitList(nested.HiddenDockables, depth);
                    VisitList(nested.LeftPinnedDockables, depth);
                    VisitList(nested.RightPinnedDockables, depth);
                    VisitList(nested.TopPinnedDockables, depth);
                    VisitList(nested.BottomPinnedDockables, depth);
                    if (nested.PinnedDock is { } pinned) Visit(pinned, depth + 1);
                    if (nested.Windows is { } windows)
                        foreach (var window in windows)
                        {
                            if (window.Layout is not { } layout)
                                throw new InvalidDataException("Floating window has no layout.");
                            Visit(layout, depth + 1);
                        }
                }
            }
            ancestors.Remove(node);
        }

        void VisitList(IEnumerable<IDockable>? list, int depth)
        {
            if (list is not null)
                foreach (var child in list) Visit(child, depth + 1);
        }
    }
}
