using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Plinth.App.Localization;
using Plinth.App.Modules.AdvancedTable.ViewModels;

namespace Plinth.App.Modules.AdvancedTable.Views;

public partial class AdvancedTableView : UserControl
{
    private readonly Func<TopLevel, Task<IReadOnlyList<IStorageFile>>>? _pickFiles;
    private CancellationTokenSource _pageCancellation = new();
    private int _pageVersion;
    private bool _picking;

    public AdvancedTableView() : this(null) { }

    internal AdvancedTableView(Func<TopLevel, Task<IReadOnlyList<IStorageFile>>>? pickFiles)
    {
        _pickFiles = pickFiles;
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
        DetachedFromVisualTree += (_, _) => InvalidatePage();
        DataContextChanged += async (_, _) =>
        {
            InvalidatePage();
            if (TopLevel.GetTopLevel(this) is not null && DataContext is AdvancedTableViewModel viewModel)
                await viewModel.InitializeAsync();
        };
    }

    private void InvalidatePage()
    {
        _pageVersion++;
        _pageCancellation.Cancel();
        _pageCancellation.Dispose();
        _pageCancellation = new CancellationTokenSource();
    }

    internal Task PickerCompletion { get; private set; } = Task.CompletedTask;

    private void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (!_picking) PickerCompletion = PickAsync();
    }

    private async Task PickAsync()
    {
        if (_picking || DataContext is not AdvancedTableViewModel { CanInteract: true } vm ||
            TopLevel.GetTopLevel(this) is not { } top) return;
        var version = _pageVersion;
        var token = _pageCancellation.Token;
        bool IsCurrent() => version == _pageVersion && ReferenceEquals(DataContext, vm)
            && ReferenceEquals(TopLevel.GetTopLevel(this), top)
            && (top is not Window host || host.IsVisible);
        _picking = true;
        try
        {
            await vm.ImportFromPickerAsync(async (ct, validateSelection) =>
            {
                ct.ThrowIfCancellationRequested();
                if (!IsCurrent()) return null;
                IReadOnlyList<IStorageFile> files;
                try { files = await (_pickFiles is { } pick ? pick(top) : PickNativeAsync(top)); }
                catch (Exception) when (!IsCurrent()) { return null; }
                Stream? source = null;
                try
                {
                    try
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!IsCurrent()) return null;
                        if (files.Count != 1) return null;
                        validateSelection();
                        source = await files[0].OpenReadAsync();
                    }
                    finally
                    {
                        Exception? failure = null;
                        foreach (var file in files)
                        {
                            try { file.Dispose(); }
                            catch (Exception ex) { failure ??= ex; }
                        }
                        if (failure is not null)
                            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
                    }
                    // Opening can finish after the page closes. Transfer ownership only
                    // after storage handles close successfully and the page is still current.
                    ct.ThrowIfCancellationRequested();
                    if (!IsCurrent()) return null;
                    var selected = source;
                    source = null;
                    return selected;
                }
                finally
                {
                    if (source is not null) await source.DisposeAsync();
                }
            }, token);
        }
        finally { _picking = false; }
    }

    private static Task<IReadOnlyList<IStorageFile>> PickNativeAsync(TopLevel top)
    {
        if (top.StorageProvider is not { CanOpen: true } storage)
            throw new InvalidOperationException("File picker unavailable.");
        return storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("advanced.import"),
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType(Loc.Get("advanced.csvType")) { Patterns = ["*.csv"] }],
        });
    }

    private async void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (DataContext is AdvancedTableViewModel viewModel)
            await viewModel.InitializeAsync();
    }
}
