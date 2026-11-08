using System;
using System.Collections.Generic;
using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Demo.Views;
using Plinth.App.Modules.Notes.ViewModels;
using Plinth.App.Modules.Notes.Views;

namespace Plinth.DockDemo;

/// <summary>Optional adapter; the host supplies its existing service provider.</summary>
public sealed class DockBusinessWorkspaceLauncher
{
    public static IReadOnlyDictionary<string, string> PaneDescriptions { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["demo"] = "Demo 数据页",
            ["notes"] = "Notes 编辑页",
        };

    private readonly IServiceProvider _services;
    public DockBusinessWorkspaceLauncher(IServiceProvider services) =>
        _services = services ?? throw new ArgumentNullException(nameof(services));

    public DemoDockFactory CreateFactory() => new(PaneDescriptions, CreatePane);

    public DockDemoWindow CreateWindow(string layoutPath) =>
        new(layoutPath, CreateFactory);

    public DockPane CreatePane(string id) => id switch
    {
        "demo" => CreateDemo(),
        "notes" => CreateNotes(),
        _ => throw new KeyNotFoundException($"Unknown business pane '{id}'."),
    };

    private DockBusinessPane CreateDemo()
    {
        var vm = _services.GetRequiredService<DemoListViewModel>();
        return new("demo", "Demo", new DemoListView { DataContext = vm }, vm);
    }

    private DockBusinessPane CreateNotes()
    {
        var vm = _services.GetRequiredService<NoteViewModel>();
        return new("notes", "Notes", new NoteView { DataContext = vm }, vm);
    }
}
