using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Plinth.App.Localization;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Services;
using Plinth.App.Modules.Demo.Services;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;
using Plinth.App.Navigation;
using Plinth.App.Modules.Demo.Data;

namespace Plinth.App.Modules.Demo.Views;

public partial class DemoListView : UserControl, IPageSearchTarget
{
    private bool _restoring;
    private bool _openingRecordMenu;
    private int _lifetimeVersion;
    private DemoListViewModel? _selectionViewModel;
    private ContextMenu RecordContextMenu => (ContextMenu)Resources["RecordContextMenu"]!;
    private readonly double[] _defaultWidths = [140, 200, 200, 110, 130, 110, 80, 140, 120];
    public DemoListView()
    {
        InitializeComponent();
        // 先走现有未保存确认，再打开菜单，不能让表格先切行后对旧记录执行操作。
        RecordsTable.AddHandler(PointerPressedEvent, OnRecordPointerPressed, RoutingStrategies.Tunnel);
        // 行内编辑把单元格换成 TextBox，其类处理器会吞掉右键的 ContextRequested；
        // 用 Tunnel 保证右键目标行始终打开记录菜单（§2.1/§2.2），TextBox 不再弹出自身菜单。
        RecordsTable.AddHandler(ContextRequestedEvent, OnRecordContextRequested, RoutingStrategies.Tunnel);
        RecordsTable.SelectionChanged += (_, _) =>
        {
            if (DataContext is DemoListViewModel { IsBatchSelecting: true } vm)
                vm.UpdateBatchSelection(RecordsTable.SelectedItems?.OfType<DemoRecord>().ToArray() ?? []);
        };
        AttachedToVisualTree += (_, _) =>
        {
            TrackSelectionViewModel(DataContext as DemoListViewModel);
            if (DataContext is DemoListViewModel vm) RestoreColumns(vm);
        };
        DetachedFromVisualTree += (_, _) =>
        {
            _lifetimeVersion++;
            RecordContextMenu.Close();
            TrackSelectionViewModel(null);
        };
        foreach (var column in RecordsTable.Columns)
        {
            column.PropertyChanged += (_, e) =>
            {
                if (!_restoring && (e.Property == TableViewColumn.WidthProperty || e.Property == TableViewColumn.IsVisibleProperty))
                    RememberColumns();
            };
        }
    }

    private void TrackSelectionViewModel(DemoListViewModel? vm)
    {
        var changed = !ReferenceEquals(_selectionViewModel, vm);
        if (_selectionViewModel is { } previous)
        {
            previous.PropertyChanged -= OnSelectionStateChanged;
            if (changed) previous.UpdateBatchSelection([]);
        }
        if (changed && (_selectionViewModel?.IsBatchSelecting == true || vm?.IsBatchSelecting == true))
        {
            RecordsTable?.SelectedItems?.Clear();
            vm?.UpdateBatchSelection([]);
        }
        _selectionViewModel = vm;
        if (vm is not null) vm.PropertyChanged += OnSelectionStateChanged;
        ApplySelectionMode();
    }

    private void OnSelectionStateChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(DemoListViewModel.ListPreferences)
            && _selectionViewModel is { } vm) RestoreColumns(vm);
        if (e.PropertyName == nameof(DemoListViewModel.IsBatchSelecting)) ApplySelectionMode();
        if (e.PropertyName == nameof(DemoListViewModel.IsLoading)
            && _selectionViewModel is { IsBatchSelecting: true, IsLoading: true })
            RecordsTable.SelectedItems?.Clear();
        if (e.PropertyName == nameof(DemoListViewModel.CanManageBatch))
            RecordsTable.IsEnabled = _selectionViewModel is not { IsBatchSelecting: true, CanManageBatch: false };
    }

    private void ApplySelectionMode()
    {
        if (RecordsTable is null) return;
        RecordContextMenu.Close();
        var batch = _selectionViewModel?.IsBatchSelecting == true;
        var mode = batch ? SelectionMode.Multiple | SelectionMode.Toggle : SelectionMode.Single;
        if (RecordsTable.SelectionMode != mode)
        {
            RecordsTable.SelectedItems?.Clear();
            RecordsTable.SelectionMode = mode;
        }
        RecordsTable.IsEnabled = !batch || _selectionViewModel!.CanManageBatch;
    }

    private void OnSelectPageClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DemoListViewModel { IsBatchSelecting: true, CanManageBatch: true })
            RecordsTable.SelectAll();
    }

    private void OnClearBatchClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DemoListViewModel { IsBatchSelecting: true, CanManageBatch: true })
            RecordsTable.SelectedItems?.Clear();
    }

    private async void OnCopyBatchClick(object? sender, RoutedEventArgs e) => await CopyBatchAsync();

    private async void OnExportPdfClick(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not DemoListViewModel vm || TopLevel.GetTopLevel(this) is not { } topLevel) return;
            var file = await topLevel.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("demo.pdf.pickTitle"),
                DefaultExtension = "pdf",
                SuggestedFileName = $"demo-{DateTime.Now:yyyyMMdd-HHmmss}.pdf",
                FileTypeChoices = [new FilePickerFileType("PDF") { Patterns = ["*.pdf"] }],
                ShowOverwritePrompt = true,
            });
            if (file?.TryGetLocalPath() is { } path) await vm.ExportPdfAsync(path);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (DataContext is DemoListViewModel vm) vm.StatusText = Loc.Get("demo.status.pdfFailed");
        }
        catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
    }

    private async void OnPrintPdfClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DemoListViewModel vm) await vm.PrintPdfAsync();
    }

    private async Task CopyBatchAsync()
    {
        if (DataContext is not DemoListViewModel vm) return;
        try
        {
            if (vm.GetBatchCopyText() is not { } text) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard)
            {
                vm.StatusText = Loc.Get("demo.list.copyFailed");
                return;
            }
            await clipboard.SetTextAsync(text);
        }
        catch (Exception) { vm.StatusText = Loc.Get("demo.list.copyFailed"); }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        // 键盘上下文菜单键：Avalonia 12 的表格不会为 Shift+F10 自动抛出 ContextRequested。
        if ((e.Key == Key.F10 && e.KeyModifiers == KeyModifiers.Shift) || e.Key == Key.Apps)
        {
            if (DataContext is DemoListViewModel { SelectedItem: { } record })
            {
                e.Handled = true;
                _ = OpenRecordMenuAsync(record, fromPointer: false);
            }
            return;
        }
        // 文本输入继续由文本框处理；批量复制只作用于当前表格。
        if (e.Key != Key.C || e.KeyModifiers != KeyModifiers.Control
            || DataContext is not DemoListViewModel { IsBatchSelecting: true }
            || e.Source is not Visual visual) return;
        var ancestors = visual.GetVisualAncestors().Prepend(visual).ToArray();
        if (ancestors.Any(item => item is TextBox) || !ancestors.Contains(RecordsTable)) return;
        e.Handled = true;
        _ = CopyBatchAsync();
    }

    private void OnRecordPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(RecordsTable).Properties.IsRightButtonPressed)
            e.Handled = true;
    }

    private async void OnRecordContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        e.Handled = true;
        var source = e.Source as Visual;
        var row = source as TableViewRow ?? source?.GetVisualAncestors().OfType<TableViewRow>().FirstOrDefault();
        var fromPointer = e.TryGetPosition(RecordsTable, out _);
        var record = fromPointer ? row?.DataContext as DemoRecord
            : DataContext is DemoListViewModel { SelectedItem: { } selected } ? selected : null;
        // 空白处不复用上一行；键盘菜单键则针对当前选择。
        if (record is null) return;
        await OpenRecordMenuAsync(record, fromPointer);
    }

    private async Task OpenRecordMenuAsync(DemoRecord record, bool fromPointer)
    {
        RecordContextMenu.Close();
        if (_openingRecordMenu || DataContext is not DemoListViewModel vm || vm.IsBatchSelecting
            || vm.IsSaving || vm.IsLoading) return;
        _openingRecordMenu = true;
        try
        {
            if (!await vm.SelectRecordAsync(record) || !ReferenceEquals(DataContext, vm)
                || TopLevel.GetTopLevel(this) is null || !vm.CanUseSelectedRecord
                || vm.SelectedItem?.Id != record.Id) return;
            vm.RefreshRecordActions();
            RecordContextMenu.DataContext = vm;
            RecordContextMenu.Placement = fromPointer ? PlacementMode.Pointer : PlacementMode.Bottom;
            // 确认中若保存刷新了列表，原行容器可能已被回收。
            RecordContextMenu.Open(RecordsTable);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
        finally { _openingRecordMenu = false; }
    }

    private void OnEditRecordClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DemoListViewModel { CanUseSelectedRecord: true, HasEditPermission: true })
        {
            RecordContextMenu.Close();
            EditCodeBox.Focus();
        }
    }

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        _lifetimeVersion++;
        base.OnDataContextChanged(e);
        TrackSelectionViewModel(DataContext as DemoListViewModel);
        if (Resources.TryGetResource("RecordContextMenu", ActualThemeVariant, out var resource)
            && resource is ContextMenu menu)
        {
            menu.Close();
            menu.DataContext = null;
        }
        if (DataContext is not DemoListViewModel vm || RecordsTable is null) return;
        RestoreColumns(vm);
    }

    private void RestoreColumns(DemoListViewModel vm)
    {
        _restoring = true;
        try
        {
            ColumnMenu.Children.Clear();
            for (var i = 0; i < RecordsTable.Columns.Count && i < vm.ColumnKeys.Length; i++)
            {
                var column = RecordsTable.Columns[i];
                var saved = vm.ListPreferences.Columns?.GetValueOrDefault(vm.ColumnKeys[i]);
                column.IsVisible = saved?.Visible ?? true;
                column.Width = new GridLength(saved is not null && double.IsFinite(saved.Width)
                    ? Math.Clamp(saved.Width, 40, 2000) : _defaultWidths[i]);
                var toggle = new CheckBox { Content = column.Header, IsChecked = column.IsVisible };
                toggle.PropertyChanged += (_, change) =>
                {
                    if (change.Property == ToggleButton.IsCheckedProperty)
                        column.IsVisible = toggle.IsChecked == true;
                };
                ColumnMenu.Children.Add(toggle);
            }
        }
        finally { _restoring = false; }
    }

    private void RememberColumns()
    {
        if (DataContext is not DemoListViewModel vm) return;
        var columns = new Dictionary<string, DemoColumnPreference>();
        for (var i = 0; i < RecordsTable.Columns.Count && i < vm.ColumnKeys.Length; i++)
        {
            var column = RecordsTable.Columns[i];
            var width = column.Width.IsAbsolute ? column.Width.Value : column.ActualWidth;
            columns[vm.ColumnKeys[i]] = new DemoColumnPreference(column.IsVisible,
                double.IsFinite(width) ? Math.Clamp(width, 40, 2000) : _defaultWidths[i]);
        }
        vm.SaveColumns(columns);
    }

    private async void OnCopyClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is DemoListViewModel { CanUseSelectedRecord: true, SelectedItem: { } record } vm
            && TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
        {
            try { await clipboard.SetTextAsync($"{record.Code}\t{record.Name}\t{record.Remark}"); }
            catch (Exception) { vm.StatusText = Loc.Get("demo.list.copyFailed"); }
        }
    }

    /// <summary>选择文件属于界面操作,放在代码后置;选好之后的导入逻辑都在 ViewModel 和 Service 里。</summary>
    private async void OnImportClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not DemoListViewModel viewModel || TopLevel.GetTopLevel(this) is not { } topLevel)
        {
            return;
        }

        var version = _lifetimeVersion;
        bool IsCurrent() => version == _lifetimeVersion && ReferenceEquals(DataContext, viewModel)
            && ReferenceEquals(TopLevel.GetTopLevel(this), topLevel)
            && (topLevel is not Window window || window.IsVisible);
        try
        {
            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.Get("demo.import.pickTitle"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
            });

            if (!IsCurrent()) return;
            if (files.Count == 1 && files[0].TryGetLocalPath() is { } path)
            {
                if (topLevel is not Window owner) return;
                await ShowCsvPreviewAsync(owner, viewModel, path);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (IsCurrent())
                viewModel.StatusText = Loc.Get("demo.status.importFailed");
        }
        catch (Exception ex) { UnhandledErrorReporter.Report(ex); }
    }

    internal async Task ShowCsvPreviewAsync(Window owner, DemoListViewModel vm, string path,
        Func<string, string?, string, CancellationToken, Task<CsvEncodingPreview>>? readPreview = null,
        Func<Window, Task<string?>>? pickReportPath = null)
    {
        readPreview ??= (file, name, separator, token) => CsvReader.PreviewAsync(file, name, separator, token);
        pickReportPath ??= async window =>
        {
            var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.Get("csv.exportErrors"),
                DefaultExtension = "csv",
                SuggestedFileName = "csv-preview-errors.csv",
                FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }],
                ShowOverwritePrompt = false,
            });
            return file?.TryGetLocalPath();
        };
        var pageVersion = _lifetimeVersion;
        bool PageIsCurrent() => pageVersion == _lifetimeVersion && ReferenceEquals(DataContext, vm)
            && ReferenceEquals(TopLevel.GetTopLevel(this), owner) && owner.IsVisible;
        if (!PageIsCurrent()) return;
        using var cancellation = new CancellationTokenSource();
        var dialog = new Window
        {
            Title = Loc.Get("csv.preview"),
            Width = 760,
            Height = 580,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        var closed = false;
        bool IsCurrent() => !closed && !cancellation.IsCancellationRequested && PageIsCurrent();
        var panel = new StackPanel { Margin = new Thickness(16), Spacing = 8 };
        var encoding = new ComboBox { ItemsSource = new[] { "utf-8", "gb18030", "gbk", "utf-16", "utf-16be" }, SelectedIndex = 0 };
        var delimiter = new ComboBox { ItemsSource = new[] { ",", ";", "\\t", "|" }, SelectedIndex = 0 };
        var mappings = new[] { new NumericUpDown { Minimum = 1, Maximum = 256, Value = 1 },
            new NumericUpDown { Minimum = 1, Maximum = 256, Value = 2 },
            new NumericUpDown { Minimum = 1, Maximum = 256, Value = 3 } };
        var sample = new TextBox { IsReadOnly = true, AcceptsReturn = true, MinHeight = 140, TextWrapping = TextWrapping.Wrap };
        var choice = new CheckBox { Content = Loc.Get("csv.confirm") };
        var preview = new Button { Content = Loc.Get("csv.preview") };
        var import = new Button { Content = Loc.Get("demo.import"), IsEnabled = false };
        var exportErrors = new Button { Content = Loc.Get("csv.exportErrors"), IsEnabled = false };
        IReadOnlyList<CsvIssue> previewErrors = [];
        string? previewSha256 = null;
        var revision = 0;
        string? previewEncoding = null;
        string? previewDelimiter = null;
        int[]? previewMapping = null;
        (string Encoding, string Delimiter, int[] Mapping, string Sha256)? confirmed = null;
        dialog.Closed += (_, _) =>
        {
            closed = true;
            revision++;
            cancellation.Cancel();
        };
        string Delimiter() => delimiter.SelectedIndex == 2 ? "\t" : delimiter.SelectedItem?.ToString() ?? ",";
        void Invalidate()
        {
            if (!IsCurrent()) return;
            choice.IsChecked = false;
            import.IsEnabled = false;
            previewEncoding = null;
            previewSha256 = null;
            previewErrors = [];
            exportErrors.IsEnabled = false;
            revision++;
        }
        encoding.SelectionChanged += (_, _) => Invalidate();
        delimiter.SelectionChanged += (_, _) => Invalidate();
        foreach (var mapping in mappings) mapping.ValueChanged += (_, _) => Invalidate();
        preview.Click += async (_, _) =>
        {
            if (!IsCurrent()) return;
            Invalidate();
            var requestRevision = revision;
            try
            {
                var selectedEncoding = encoding.SelectedItem?.ToString() ?? "utf-8";
                var selectedDelimiter = Delimiter();
                var selectedMapping = mappings.Select(m => (int)(m.Value ?? 1) - 1).ToArray();
                var result = await readPreview(path, selectedEncoding, selectedDelimiter, cancellation.Token);
                if (!IsCurrent() || requestRevision != revision) return;
                var mapped = result.Content is null ? null : CsvReader.MapAndValidate(result.Content,
                    selectedMapping, [typeof(string), typeof(string), typeof(string)], cancellation.Token);
                sample.Text = mapped is null ? Loc.Get("csv.failed") :
                    string.Join("\n", mapped.Rows.Select(r => string.Join(" | ", r)));
                previewErrors = mapped?.Errors ?? [new CsvIssue(0, "ENCODING", Loc.Get("csv.failed"))];
                exportErrors.IsEnabled = previewErrors.Count > 0;
                if (mapped is null || mapped.Errors.Count > 0)
                {
                    sample.Text += "\n" + Loc.Get("csv.failed");
                    return;
                }
                previewEncoding = selectedEncoding;
                previewDelimiter = selectedDelimiter;
                previewMapping = selectedMapping;
                previewSha256 = result.Sha256;
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (!IsCurrent() || requestRevision != revision) return;
                sample.Text = Loc.Get("csv.failed");
                previewErrors = [new CsvIssue(0, "PREVIEW_FAILED", Loc.Get("csv.failed"))];
                exportErrors.IsEnabled = true;
            }
        };
        exportErrors.Click += async (_, _) =>
        {
            if (!IsCurrent()) return;
            var errors = previewErrors.ToArray();
            var requestRevision = revision;
            try
            {
                var reportPath = await pickReportPath(dialog);
                if (IsCurrent() && dialog.IsVisible && requestRevision == revision && reportPath is not null)
                    await vm.ExportPreviewErrorsAsync(reportPath, errors);
            }
            catch (OperationCanceledException) { }
            catch (Exception)
            {
                if (IsCurrent() && requestRevision == revision) sample.Text = Loc.Get("csv.failed");
            }
        };
        choice.PropertyChanged += (_, change) =>
        {
            if (IsCurrent() && change.Property == ToggleButton.IsCheckedProperty)
                import.IsEnabled = choice.IsChecked == true && previewEncoding is not null;
        };
        import.Click += (_, _) =>
        {
            if (!IsCurrent() || confirmed is not null || choice.IsChecked != true
                || previewEncoding is null || previewDelimiter is null
                || previewMapping is null || previewSha256 is null) return;
            // Closed invalidates pending reads, but must not invalidate the accepted import.
            confirmed = (previewEncoding, previewDelimiter, previewMapping.ToArray(), previewSha256);
            dialog.Close(true);
        };
        panel.Children.Add(new TextBlock { Text = Loc.Get("csv.encoding") });
        panel.Children.Add(encoding);
        panel.Children.Add(new TextBlock { Text = Loc.Get("csv.delimiter") });
        panel.Children.Add(delimiter);
        panel.Children.Add(new TextBlock { Text = Loc.Get("csv.mapping") });
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (var mapping in mappings) row.Children.Add(mapping);
        panel.Children.Add(row);
        panel.Children.Add(preview);
        panel.Children.Add(exportErrors);
        panel.Children.Add(sample);
        panel.Children.Add(choice);
        panel.Children.Add(import);
        dialog.Content = new ScrollViewer { Content = panel };
        // Closing or replacing the page also ends this local preview, never global work.
        void EndPreview(object? sender, EventArgs args)
        {
            if (!closed) dialog.Close();
        }
        void DetachPreview(object? sender, VisualTreeAttachmentEventArgs args) => EndPreview(sender, args);
        DataContextChanged += EndPreview;
        DetachedFromVisualTree += DetachPreview;
        owner.Closed += EndPreview;
        try
        {
            var result = dialog.ShowDialog<bool>(owner);
            // BOM determines the initial choice; explicit preview and confirmation remain required.
            var initialRevision = revision;
            try
            {
                var initial = await readPreview(path, null, ",", cancellation.Token);
                if (IsCurrent() && initialRevision == revision && initial.Encoding is { } detected)
                    encoding.SelectedItem = detected;
            }
            catch (OperationCanceledException) { }
            catch (Exception) { if (IsCurrent() && initialRevision == revision) sample.Text = Loc.Get("csv.failed"); }
            if (await result && PageIsCurrent() && confirmed is { } snapshot)
                await vm.ImportFromAsync(path, snapshot.Encoding, snapshot.Delimiter, snapshot.Mapping, snapshot.Sha256);
        }
        finally
        {
            DataContextChanged -= EndPreview;
            DetachedFromVisualTree -= DetachPreview;
            owner.Closed -= EndPreview;
            if (!closed) dialog.Close();
            cancellation.Cancel();
        }
    }
}
