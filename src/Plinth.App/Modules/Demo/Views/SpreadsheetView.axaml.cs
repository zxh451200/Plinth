#if PLINTH_XLSX
using System;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Platform.Storage;
using Plinth.App.Modules.Demo.ViewModels;

namespace Plinth.App.Modules.Demo.Views;

/// <summary>ViewLocator-compatible wrapper; only native storage pickers live in code-behind.</summary>
public sealed class SpreadsheetView : UserControl
{
    private bool _picking;
    private int _lifetimeVersion;
    private readonly Func<TopLevel, bool, Task<string?>>? _pickPath;

    public SpreadsheetView() : this(null) { }

    // Tests control the native dialog response while exercising the real view and buttons.
    internal SpreadsheetView(Func<TopLevel, bool, Task<string?>>? pickPath)
    {
        _pickPath = pickPath;
        var page = (UserControl)AvaloniaXamlLoader.Load(
            new Uri("avares://Plinth.App/Modules/Demo/Views/SpreadsheetView.axaml"));
        Content = page;
        var labels = new SpreadsheetLabels();
        var table = page.FindControl<TableView>("PreviewTable")!;
        string[] headers = ["Row", "Code", "Name", "Remark"];
        for (var i = 0; i < headers.Length; i++) table.Columns[i].Header = labels[headers[i]];
        page.FindControl<Button>("ChooseFileButton")!.Click += (_, _) =>
        {
            if (!_picking) PickerCompletion = PickAsync(false);
        };
        page.FindControl<Button>("ExportButton")!.Click += (_, _) =>
        {
            if (!_picking) PickerCompletion = PickAsync(true);
        };
        DetachedFromVisualTree += (_, _) => _lifetimeVersion++;
    }

    internal Task PickerCompletion { get; private set; } = Task.CompletedTask;

    protected override void OnDataContextChanged(EventArgs e)
    {
        _lifetimeVersion++;
        base.OnDataContextChanged(e);
    }

    private async Task PickAsync(bool folder)
    {
        if (_picking || DataContext is not SpreadsheetViewModel { CanInteract: true } vm
            || TopLevel.GetTopLevel(this) is not { } top) return;
        var version = _lifetimeVersion;
        bool IsCurrent() => version == _lifetimeVersion && ReferenceEquals(DataContext, vm)
            && ReferenceEquals(TopLevel.GetTopLevel(this), top)
            && (top is not Window host || host.IsVisible) && vm.CanInteract;
        if (!IsCurrent()) return;
        _picking = true;
        try
        {
            var path = await (_pickPath is { } pick
                ? pick(top, folder) : PickNativePathAsync(top, vm, folder));
            if (!IsCurrent() || path is null) return;
            if (folder) await vm.ExportToAsync(path);
            else await vm.PreviewFromAsync(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { if (IsCurrent()) vm.ReportPickerFailure(); }
        finally { _picking = false; }
    }

    private static async Task<string?> PickNativePathAsync(TopLevel top, SpreadsheetViewModel vm, bool folder)
    {
        if (folder)
        {
            if (top.StorageProvider is not { CanPickFolder: true } storage)
                throw new InvalidOperationException("Folder picker unavailable.");
            var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                Title = vm.Labels["ChooseFolder"], AllowMultiple = false,
            });
            if (folders.Count != 1) return null;
            using var selected = folders[0];
            return selected.TryGetLocalPath()
                ?? throw new InvalidOperationException("Folder has no local path.");
        }
        else
        {
            if (top.StorageProvider is not { CanOpen: true } storage)
                throw new InvalidOperationException("File picker unavailable.");
            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = vm.Labels["ChooseFile"], AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("XLSX") { Patterns = ["*.xlsx"] }],
            });
            if (files.Count != 1) return null;
            using var selected = files[0];
            return selected.TryGetLocalPath()
                ?? throw new InvalidOperationException("File has no local path.");
        }
    }
}
#endif
