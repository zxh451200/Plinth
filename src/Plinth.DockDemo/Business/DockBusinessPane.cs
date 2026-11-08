using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Plinth.App.Navigation;

namespace Plinth.DockDemo;

/// <summary>Dock context backed by a real Plinth page.</summary>
public sealed class DockBusinessPane : DockPane, IDockBusinessPane, IDockPaneView
{
    private readonly IUnsavedChanges? _closeGuard;
    private readonly IDisposable? _disposable;

    public DockBusinessPane(string id, string title, Control view, object viewModel)
        : base(id + "\n" + title)
    {
        Id = id;
        Title = title;
        View = view;
        ViewModel = viewModel;
        _closeGuard = viewModel as IUnsavedChanges;
        _disposable = viewModel as IDisposable;
    }

    public string Id { get; }
    public string Title { get; }
    public Control View { get; }
    public object ViewModel { get; }
    public Task<bool> TryLeaveAsync() => _closeGuard?.TryLeaveAsync() ?? Task.FromResult(true);

    public override void Dispose()
    {
        if (IsDisposed) return;
        _disposable?.Dispose();
        base.Dispose();
    }
}
