using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Reflection;
using System.Collections.Immutable;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Localization;
using Plinth.App.Security;
using Plinth.App.Services;
using Plinth.App.Modules.AdvancedTable;
using Plinth.App.Modules.AdvancedTable.Models;
using Plinth.App.Modules.AdvancedTable.Services;
using Plinth.App.Modules.AdvancedTable.ViewModels;
using Plinth.App.Modules.AdvancedTable.Views;
using Plinth.App.Navigation;
using Xunit;

namespace Plinth.App.Tests;

public sealed class AdvancedTableCsvTests
{
    private const string Header = "Id,Group,Label,Quantity,Amount\n";

    private static async Task<AdvancedTableCsvRepository> Import(string text)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(text));
        var repository = await AdvancedTableCsvRepository.ImportAsync(stream);
        Assert.True(stream.CanRead);
        return repository;
    }

    [Fact]
    public async Task Quoted_utf8_snapshot_has_stable_pages_and_identical_export_scope()
    {
        var repository = await Import("\uFEFF" + Header +
            "b,B,Other,7,20.00\r\n" +
            "z,A,\"商品, \"\"一\"\"\n明细\",2,1.25\r\n" +
            "a,A,商品二,-1,-0.25\r\n");
        var first = await repository.ReadPageAsync(new("商品", "A", 1, 1));
        var second = await repository.ReadPageAsync(new("商品", "A", 2, 1));
        Assert.Equal("a", Assert.Single(first.Rows).Id);
        Assert.Equal("z", Assert.Single(second.Rows).Id);
        Assert.Equal("商品, \"一\"\n明细", second.Rows[0].Label);
        Assert.Equal(3, first.TotalRows);
        Assert.Equal(2, first.FilteredRows);
        Assert.Equal(1, first.FilteredQuantity);
        Assert.Equal(1m, first.FilteredAmount);
        Assert.Equal(first.Groups.Single(), second.Groups.Single());
        var exported = new List<AdvancedTableRow>();
        await foreach (var row in repository.ReadFilteredAsync("商品", "A", 1)) exported.Add(row);
        Assert.Equal(new[] { first.Rows[0], second.Rows[0] }, exported);
        Assert.Equal(first.FilteredAmount, exported.Sum(row => row.Amount));
        Assert.Empty((await repository.ReadPageAsync(new("商品", "A", int.MaxValue, 200))).Rows);
    }

    [Theory]
    [InlineData("Id,Group,Label,Amount,Quantity\nx,A,X,1,1")]
    [InlineData(Header + "x,A,X,1,1,extra")]
    [InlineData(Header + "x,A,X,1")]
    [InlineData(Header + "\n")]
    [InlineData(Header + "x,A,\"unfinished,1,1")]
    [InlineData(Header + "x,A,\"X\"junk,1,1")]
    [InlineData(Header + "x,A,X\"bad,1,1")]
    [InlineData(Header + "x,A,X,1,1\r")]
    [InlineData(Header + "x, A,X,1,1")]
    [InlineData(Header + "x,A, ,1,1")]
    [InlineData(Header + "x,A,X,1,1\nx,B,Y,2,2")]
    [InlineData(Header + "x,A,X,100001,1")]
    [InlineData(Header + "x,A,X,-100001,1")]
    [InlineData(Header + "x,A,X,1.0,1")]
    [InlineData(Header + "x,A,X,1,1.001")]
    [InlineData(Header + "x,A,X,1,1e2")]
    [InlineData(Header + "x,A,X,1,1000000000001")]
    [InlineData(Header + "x,A,X,1, 1")]
    [InlineData(Header + "x,A,X,1,NaN")]
    [InlineData(Header + "x,A,X\0,1,1")]
    public async Task Invalid_input_is_rejected_atomically(string text) =>
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(text));

    [Fact]
    public async Task Text_and_group_limits_are_enforced()
    {
        foreach (var row in new[]
        {
            $"{new string('x', 129)},A,X,1,1",
            $"x,{new string('A', 129)},X,1,1",
            $"x,A,{new string('X', 1025)},1,1",
            string.Join("\n", Enumerable.Range(0, 17).Select(i => $"{i},G{i},X,1,1"))
        })
            await Assert.ThrowsAsync<InvalidDataException>(() => Import(Header + row));
    }

    [Fact]
    public async Task Maximum_snapshot_sums_are_safe_and_next_row_is_rejected()
    {
        var data = Header + string.Join("\n", Enumerable.Range(0, AdvancedTableCsvRepository.MaxRows)
            .Select(i => $"{i},A,X,100000,1000000000000"));
        var repository = await Import(data);
        var page = await repository.ReadPageAsync(new(null, null, 1, 200));
        Assert.Equal(200, page.Rows.Count);
        Assert.Equal(1000000000, page.FilteredQuantity);
        Assert.Equal(10000000000000000m, page.FilteredAmount);
        await Assert.ThrowsAsync<InvalidDataException>(() => Import(data + "\nextra,A,X,1,1"));
    }

    [Fact]
    public async Task Nonseekable_byte_limit_and_invalid_utf8_are_rejected()
    {
        using var oversized = new NonSeekableStream(new byte[AdvancedTableCsvRepository.MaxBytes + 100]);
        await Assert.ThrowsAsync<InvalidDataException>(() => AdvancedTableCsvRepository.ImportAsync(oversized));
        Assert.Equal(AdvancedTableCsvRepository.MaxBytes + 1, oversized.BytesRead);
        using var invalid = new MemoryStream(new byte[] { 0xC3, 0x28 });
        await Assert.ThrowsAsync<InvalidDataException>(() => AdvancedTableCsvRepository.ImportAsync(invalid));
    }

    [Fact]
    public async Task Cancellation_after_first_import_chunk_keeps_caller_stream_open()
    {
        using var cancellation = new CancellationTokenSource();
        using var input = new NonSeekableStream(Encoding.UTF8.GetBytes(Header + "x,A,X,1,1"),
            cancellation.Cancel);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdvancedTableCsvRepository.ImportAsync(input, cancellation.Token));
        Assert.True(input.CanRead);
        Assert.True(input.BytesRead > 0);
    }

    [Fact]
    public async Task Cancellation_after_first_imported_export_row_removes_partial_file()
    {
        var repository = await Import(Header + "x,A,X,1,1\ny,A,Y,2,2");
        using var cancellation = new CancellationTokenSource();
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-csv-mid-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AdvancedTableCsvExporter.ExportAsync(
                    new CancelAfterRowRepository(repository, cancellation), null, null, directory, cancellation.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Header_only_import_is_empty_and_imports_are_independent_snapshots()
    {
        var empty = await Import(Header);
        var page = await empty.ReadPageAsync(new(null, null, 1, 50));
        Assert.Empty(page.Groups);
        Assert.Equal(0, page.TotalRows);
        var original = await Import(Header + "x,A,X,1,1");
        _ = await Import(Header + "y,B,Y,2,2");
        Assert.Equal("x", Assert.Single((await original.ReadPageAsync(new(null, null, 1, 50))).Rows).Id);
    }

    [Fact]
    public async Task Explicit_registration_uses_import_and_default_stays_synthetic()
    {
        var repository = await Import(Header + "real,A,From file,3,4.50");
        using var provider = new ServiceCollection().AddAdvancedTableModule(repository).BuildServiceProvider();
        Assert.Same(repository, provider.GetRequiredService<IAdvancedTableRepository>());
        using var vm = Assert.IsType<AdvancedTableViewModel>(
            Assert.Single(provider.GetServices<NavigationItem>()).CreateViewModel());
        await vm.InitializeAsync();
        Assert.Equal(1, vm.TotalRows);
        using var defaultProvider = new ServiceCollection().AddAdvancedTableModule().BuildServiceProvider();
        Assert.Equal(240, (await defaultProvider.GetRequiredService<IAdvancedTableRepository>()
            .ReadPageAsync(new(null, null, 1, 50))).TotalRows);
    }

    [Fact]
    public async Task Cancellation_during_import_read_and_file_export_preserves_snapshot_and_cleans_files()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(Header));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            AdvancedTableCsvRepository.ImportAsync(stream, cancelled.Token));

        var repository = await Import(Header + "x,A,=SUM(1),1,2.50\ny,A,Y,2,3.50");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ReadPageAsync(new(null, null, 1, 50), cancelled.Token));
        using var duringExport = new CancellationTokenSource();
        await using var iterator = repository.ReadFilteredAsync(null, null, 1, duringExport.Token)
            .GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        duringExport.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-csv-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AdvancedTableCsvExporter.ExportAsync(repository, null, null, directory, cancelled.Token));
            Assert.Empty(Directory.GetFiles(directory));
            var path = await AdvancedTableCsvExporter.ExportAsync(repository, null, null, directory);
            Assert.Contains("\"'=SUM(1)\"", await File.ReadAllTextAsync(path));
            var encoded = await File.ReadAllBytesAsync(path);
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, encoded.Take(3).ToArray());
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        Assert.Equal(2, (await repository.ReadPageAsync(new(null, null, 1, 50))).FilteredRows);
    }

    private static SensitiveOperationService Operations(bool authentication = false, bool production = false,
        TestUsers? users = null)
    {
        users ??= new TestUsers();
        var audit = new TestAudit();
        return new(new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authentication, production);
    }

    private static Task<Stream?> Select(string text) =>
        Task.FromResult<Stream?>(new MemoryStream(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public async Task Page_import_replaces_only_its_snapshot_and_resets_scope_for_paging_and_export()
    {
        var original = new AdvancedTableRepository();
        using var vm = new AdvancedTableViewModel(original, Operations());
        using var other = new AdvancedTableViewModel(original, Operations());
        await vm.InitializeAsync();
        await other.InitializeAsync();
        vm.SelectedGroup = "North";
        await vm.ApplyFilterCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);

        var csv = Header + string.Join("\n", Enumerable.Range(0, 60).Select(i => $"{i},Imported,商品,2,1.25"));
        var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        await vm.ImportFromPickerAsync(_ => Task.FromResult<Stream?>(stream));

        Assert.False(stream.CanRead);
        Assert.Equal(1, vm.Page);
        Assert.Equal(60, vm.TotalRows);
        Assert.Equal(60, vm.FilteredRows);
        Assert.Equal(new[] { "", "Imported" }, vm.Groups);
        Assert.Equal("", vm.SearchText);
        Assert.Equal("", vm.SelectedGroup);
        Assert.Equal(Loc.Format("advanced.imported", 60), vm.TotalText);
        Assert.Equal(60, Assert.Single(vm.Rows.Where(row => row.IsSubtotal)).RowCount);
        Assert.Equal(75m, Assert.Single(vm.Rows.Where(row => row.IsGrandTotal)).Amount);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(10, vm.Rows.Count(row => row.Kind == AdvancedTableRowKind.Data));
        Assert.Equal(240, other.TotalRows);
        Assert.Equal(240, (await original.ReadPageAsync(new(null, null, 1, 50))).TotalRows);

        var directory = Path.Combine(Path.GetTempPath(), $"plinth-import-page-{Guid.NewGuid():N}");
        try
        {
            // Unapplied edits must not change the export scope of the new snapshot.
            vm.SearchText = "missing";
            await vm.ExportToAsync(directory);
            Assert.Equal(61, File.ReadAllLines(Assert.Single(Directory.GetFiles(directory))).Length);
            await vm.ApplyFilterCommand.ExecuteAsync(null);
            Assert.Equal(0, vm.FilteredRows);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("bytes")]
    [InlineData("rows")]
    [InlineData("groups")]
    [InlineData("io")]
    public async Task Page_import_failure_keeps_previous_imported_snapshot_and_applied_scope(string failure)
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        var originalCsv = Header + string.Join("\n", Enumerable.Range(0, 70).Select(i => $"{i},A,Original,1,2"));
        await vm.ImportFromPickerAsync(_ => Select(originalCsv));
        vm.SelectedGroup = "A";
        await vm.ApplyFilterCommand.ExecuteAsync(null);
        await vm.NextPageCommand.ExecuteAsync(null);
        vm.SearchText = "unapplied";
        var before = vm.Rows.ToArray();
        var scope = vm.ScopeText;
        var total = vm.TotalText;
        var input = failure switch
        {
            "bytes" => new string('x', AdvancedTableCsvRepository.MaxBytes + 1),
            "rows" => Header + string.Join("\n", Enumerable.Range(0, 10001).Select(i => $"{i},A,X,1,1")),
            "groups" => Header + string.Join("\n", Enumerable.Range(0, 17).Select(i => $"{i},G{i},X,1,1")),
            _ => Header + "bad",
        };

        await vm.ImportFromPickerAsync(_ => failure == "io"
            ? Task.FromException<Stream?>(new IOException("secret file path"))
            : Select(input));

        Assert.Equal(before, vm.Rows.ToArray());
        Assert.Equal(scope, vm.ScopeText);
        Assert.Equal(total, vm.TotalText);
        Assert.Equal("unapplied", vm.SearchText);
        Assert.Equal("A", vm.SelectedGroup);
        Assert.Equal(2, vm.Page);
        Assert.Equal(new[] { "", "A" }, vm.Groups);
        Assert.Equal(Loc.Get(failure == "io" ? "advanced.failed" : "advanced.invalid"), vm.StatusText);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [Fact]
    public async Task Empty_import_succeeds_and_dismissing_picker_preserves_status()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        await vm.ImportFromPickerAsync(_ => Select(Header));
        Assert.Equal(0, vm.TotalRows);
        Assert.Single(vm.Rows);
        Assert.Equal(new[] { "" }, vm.Groups);
        Assert.True(vm.ExportCommand.CanExecute(null));
        var status = vm.StatusText;
        await vm.ImportFromPickerAsync(_ => Task.FromResult<Stream?>(null));
        Assert.Equal(status, vm.StatusText);
        Assert.Equal(Loc.Format("advanced.imported", 0), vm.TotalText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancel_or_dispose_during_noncooperative_picker_never_publishes(bool dispose)
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        await vm.InitializeAsync();
        var before = vm.Rows.ToArray();
        var release = new TaskCompletionSource<Stream?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = vm.ImportFromPickerAsync(_ => release.Task);
        Assert.True(vm.IsBusy);
        Assert.False(vm.CanInteract);
        Assert.True(vm.CancelImportCommand.CanExecute(null));
        if (dispose) vm.Dispose();
        else vm.CancelImportCommand.Execute(null);
        var source = new MemoryStream(Encoding.UTF8.GetBytes(Header + "new,B,New,1,1"));
        release.SetResult(source);
        await pending;
        Assert.False(source.CanRead);
        Assert.Equal(before, vm.Rows.ToArray());
        Assert.Equal(240, vm.TotalRows);
        if (!dispose) Assert.True(vm.CanInteract);
    }

    [Fact]
    public async Task Cancellation_during_read_preserves_snapshot_and_disposes_stream()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        await vm.InitializeAsync();
        var before = vm.Rows.ToArray();
        using var source = new NonSeekableStream(Encoding.UTF8.GetBytes(Header + "new,B,New,1,1"),
            () => vm.CancelImportCommand.Execute(null));
        await vm.ImportFromPickerAsync(_ => Task.FromResult<Stream?>(source));
        Assert.Equal(before, vm.Rows.ToArray());
        Assert.Equal(Loc.Get("advanced.cancelled"), vm.StatusText);
    }

    [Fact]
    public async Task Stream_disposal_failure_does_not_commit_validated_snapshot()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        await vm.InitializeAsync();
        var before = vm.Rows.ToArray();
        await vm.ImportFromPickerAsync(_ => Task.FromResult<Stream?>(new FailingDisposeStream()));
        Assert.Equal(before, vm.Rows.ToArray());
        Assert.Equal(240, vm.TotalRows);
        Assert.Equal(Loc.Get("advanced.failed"), vm.StatusText);
    }

    [Fact]
    public async Task Authentication_without_explicit_permission_mapping_denies_before_picker()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations(authentication: true));
        await vm.InitializeAsync();
        var selected = false;
        await vm.ImportFromPickerAsync(_ => { selected = true; return Select(Header); });
        Assert.False(selected);
        Assert.Equal(240, vm.TotalRows);
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [Fact]
    public async Task Rejected_production_confirmation_never_opens_picker()
    {
        var operations = Operations(production: true);
        operations.ConfirmProductionAsync = () => Task.FromResult(false);
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), operations);
        await vm.ImportFromPickerAsync(_ => throw new InvalidOperationException("Picker must not open"));
        Assert.Equal(Loc.Get("advanced.cancelled"), vm.StatusText);
        Assert.Empty(vm.Rows);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Production_confirmation_revalidates_session_before_opening_picker(bool changeSession)
    {
        var users = new TestUsers();
        var operations = Operations(authentication: true, production: true, users: users);
        var confirmations = 0;
        operations.ConfirmProductionAsync = () =>
        {
            confirmations++;
            if (changeSession) users.SessionVersion++;
            return Task.FromResult(true);
        };
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), operations, "test:csv",
            readPermission: "test:csv");
        await vm.InitializeAsync();
        var selections = 0;
        await vm.ImportFromPickerAsync(_ =>
        {
            selections++;
            return Select(Header + "new,A,New,1,1");
        });
        Assert.Equal(1, confirmations);
        Assert.Equal(changeSession ? 0 : 1, selections);
        Assert.Equal(changeSession ? 240 : 1, vm.TotalRows);
        Assert.Equal(Loc.Get(changeSession ? "advanced.denied" : "advanced.importSuccess"), vm.StatusText);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mapped_permission_is_revalidated_before_snapshot_publication(bool changeSession)
    {
        var users = new TestUsers();
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users), "test:csv", readPermission: "test:csv");
        await vm.InitializeAsync();
        using var source = new NonSeekableStream(Encoding.UTF8.GetBytes(Header + "new,A,New,1,1"),
            () => { if (changeSession) users.SessionVersion++; });
        await vm.ImportFromPickerAsync(_ => Task.FromResult<Stream?>(source));
        Assert.Equal(changeSession ? 240 : 1, vm.TotalRows);
        Assert.Equal(Loc.Get(changeSession ? "advanced.denied" : "advanced.importSuccess"), vm.StatusText);
    }

    [Fact]
    public async Task Permission_revocation_during_import_denies_publication()
    {
        var users = new TestUsers();
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users), "test:csv", readPermission: "test:csv");
        await vm.InitializeAsync();
        using var source = new NonSeekableStream(Encoding.UTF8.GetBytes(Header + "new,A,New,1,1"),
            () => users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty });
        await vm.ImportFromPickerAsync(_ => Task.FromResult<Stream?>(source));
        Assert.Equal(240, vm.TotalRows);
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [AvaloniaTheory]
    [InlineData("detach")]
    [InlineData("context")]
    [InlineData("close")]
    [InlineData("reattach")]
    public async Task Late_native_selection_is_disposed_without_opening_or_changing_page(string transition)
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        using var replacement = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        var release = new TaskCompletionSource<IReadOnlyList<IStorageFile>>();
        var selected = DispatchProxy.Create<IStorageFile, StorageFileProxy>();
        var file = (StorageFileProxy)(object)selected;
        var view = new AdvancedTableView(_ => release.Task) { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            var before = vm.Rows.ToArray();
            var status = vm.StatusText;
            var button = view.FindControl<Button>("ImportButton")!;
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(vm.IsBusy);
            button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            if (transition is "detach" or "reattach")
            {
                window.Content = null;
                if (transition == "reattach") window.Content = view;
            }
            else if (transition == "context") view.DataContext = replacement;
            else window.Close();
            release.SetResult(new[] { selected });
            await view.PickerCompletion;
            Assert.Equal(0, file.OpenCount);
            Assert.Equal(1, file.DisposeCount);
            Assert.Equal(before, vm.Rows.ToArray());
            Assert.Equal(status, vm.StatusText);
            Assert.Equal(240, vm.TotalRows);
        }
        finally { window.Close(); }
    }

    [AvaloniaFact]
    public async Task Import_button_opens_selected_stream_and_updates_localized_scope()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        var selected = DispatchProxy.Create<IStorageFile, StorageFileProxy>();
        var file = (StorageFileProxy)(object)selected;
        var calls = 0;
        var view = new AdvancedTableView(_ =>
        {
            calls++;
            return Task.FromResult<IReadOnlyList<IStorageFile>>(new[] { selected });
        })
        { DataContext = vm };
        var window = new Window { Content = view };
        var language = Loc.Culture.Name;
        try
        {
            window.Show();
            view.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.PickerCompletion;
            Assert.Equal(1, calls);
            Assert.Equal(1, file.OpenCount);
            Assert.Equal(1, file.DisposeCount);
            Assert.Equal(1, vm.TotalRows);
            foreach (var lang in new[] { "en-US", "zh-CN" })
            {
                Loc.SetLanguage(lang);
                vm.OnUiLanguageChanged();
                Assert.Equal(Loc.Get("advanced.importSuccess"), vm.StatusText);
                Assert.Equal(Loc.Format("advanced.imported", 1), vm.TotalText);
                Assert.Equal(Loc.Get("advanced.totalLabel"), vm.Rows.Last().Label);
                Assert.Equal(Loc.Get("advanced.import"), view.FindControl<Button>("ImportButton")!.Content);
            }
        }
        finally { Loc.SetLanguage(language); window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Opened_stream_is_disposed_when_handle_release_fails_or_page_closes(bool close)
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        var selected = DispatchProxy.Create<IStorageFile, StorageFileProxy>();
        var file = (StorageFileProxy)(object)selected;
        var release = new TaskCompletionSource<Stream>();
        file.OpenAsync = () => release.Task;
        file.ThrowOnDispose = !close;
        var view = new AdvancedTableView(_ =>
            Task.FromResult<IReadOnlyList<IStorageFile>>(new[] { selected }))
        { DataContext = vm };
        var window = new Window { Content = view };
        using var source = new MemoryStream(Encoding.UTF8.GetBytes(Header + "new,A,New,1,1"));
        try
        {
            window.Show();
            var before = vm.Rows.ToArray();
            view.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(1, file.OpenCount);
            if (close) window.Close();
            release.SetResult(source);
            await view.PickerCompletion;
            Assert.False(source.CanRead);
            Assert.Equal(1, file.DisposeCount);
            Assert.Equal(before, vm.Rows.ToArray());
            Assert.Equal(240, vm.TotalRows);
            if (!close) Assert.Equal(Loc.Get("advanced.failed"), vm.StatusText);
        }
        finally { window.Close(); }
    }

    [AvaloniaTheory]
    [InlineData("session")]
    [InlineData("import")]
    [InlineData("read")]
    public async Task Native_selection_revalidates_before_open_even_without_identity_notification(string change)
    {
        var users = new TestUsers
        {
            CurrentUser = new("tester", ImmutableHashSet<string>.Empty,
                ImmutableHashSet.Create("test:import", "test:read")),
        };
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users),
            importPermission: "test:import", readPermission: "test:read");
        var release = new TaskCompletionSource<IReadOnlyList<IStorageFile>>();
        var selected = DispatchProxy.Create<IStorageFile, StorageFileProxy>();
        var file = (StorageFileProxy)(object)selected;
        var view = new AdvancedTableView(_ => release.Task) { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            view.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(vm.IsBusy);
            if (change == "session") users.SessionVersion++;
            else users.CurrentUser = users.CurrentUser with
            {
                Permissions = users.CurrentUser.Permissions.Remove("test:" + change),
            };
            release.SetResult(new[] { selected });
            await view.PickerCompletion;
            Assert.Equal(0, file.OpenCount);
            Assert.Equal(1, file.DisposeCount);
            Assert.Equal(240, vm.TotalRows);
            Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
            Assert.False(vm.IsBusy);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Revoked_read_grant_after_picker_disposes_stream_without_reading_it()
    {
        var users = new TestUsers
        {
            CurrentUser = new("tester", ImmutableHashSet<string>.Empty,
                ImmutableHashSet.Create("test:import", "test:read")),
        };
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users),
            importPermission: "test:import", readPermission: "test:read");
        await vm.InitializeAsync();
        using var source = new NonSeekableStream(Encoding.UTF8.GetBytes(Header + "private,A,Private,1,1"));
        await vm.ImportFromPickerAsync(_ =>
        {
            users.CurrentUser = users.CurrentUser with
            {
                Permissions = users.CurrentUser.Permissions.Remove("test:read"),
            };
            return Task.FromResult<Stream?>(source);
        });
        Assert.Equal(0, source.BytesRead);
        Assert.False(source.CanRead);
        Assert.Equal(240, vm.TotalRows);
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [AvaloniaFact]
    public async Task Unexpected_multiple_selection_releases_every_handle_when_one_dispose_fails()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(), Operations());
        var first = DispatchProxy.Create<IStorageFile, StorageFileProxy>();
        var second = DispatchProxy.Create<IStorageFile, StorageFileProxy>();
        var firstFile = (StorageFileProxy)(object)first;
        var secondFile = (StorageFileProxy)(object)second;
        firstFile.ThrowOnDispose = true;
        var view = new AdvancedTableView(_ =>
            Task.FromResult<IReadOnlyList<IStorageFile>>(new[] { first, second }))
        { DataContext = vm };
        var window = new Window { Content = view };
        try
        {
            window.Show();
            view.FindControl<Button>("ImportButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await view.PickerCompletion;
            Assert.Equal(0, firstFile.OpenCount);
            Assert.Equal(0, secondFile.OpenCount);
            Assert.Equal(1, firstFile.DisposeCount);
            Assert.Equal(1, secondFile.DisposeCount);
            Assert.Equal(240, vm.TotalRows);
            Assert.Equal(Loc.Get("advanced.failed"), vm.StatusText);
            Assert.False(vm.IsBusy);
        }
        finally { window.Close(); }
    }

    public class StorageFileProxy : DispatchProxy
    {
        public int OpenCount { get; private set; }
        public int DisposeCount { get; private set; }
        public Func<Task<Stream>>? OpenAsync { get; set; }
        public bool ThrowOnDispose { get; set; }
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name == nameof(IStorageFile.OpenReadAsync))
            {
                OpenCount++;
                if (OpenAsync is not null) return OpenAsync();
                return Task.FromResult<Stream>(new MemoryStream(Encoding.UTF8.GetBytes(Header + "new,A,商品,2,3")));
            }
            if (targetMethod?.Name == nameof(IDisposable.Dispose))
            {
                DisposeCount++;
                if (ThrowOnDispose) throw new IOException("Test storage handle disposal failure");
                return null;
            }
            throw new NotSupportedException(targetMethod?.Name);
        }
    }

    [Fact]
    public async Task Authenticated_business_snapshot_without_read_mapping_is_never_read()
    {
        var users = new TestUsers();
        var repository = new BoundaryRepository();
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true, users: users));
        await vm.InitializeAsync();
        Assert.Equal(0, repository.Reads);
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Groups);
        Assert.Equal(0, vm.TotalRows);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [Fact]
    public async Task Import_permission_does_not_substitute_for_read_permission()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true), importPermission: "test:csv");
        var picked = false;
        await vm.ImportFromPickerAsync(_ => { picked = true; return Select(Header); });
        Assert.False(picked);
        Assert.Empty(vm.Rows);
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("test:missing")]
    public async Task Read_grant_does_not_substitute_for_export_mapping(string? exportPermission)
    {
        var repository = new BoundaryRepository();
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true),
            readPermission: "test:csv", exportPermission: exportPermission);
        await vm.InitializeAsync();
        Assert.Equal(1, vm.TotalRows);
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-denied-export-{Guid.NewGuid():N}");
        await vm.ExportToAsync(directory);
        Assert.False(Directory.Exists(directory));
        Assert.Equal(0, repository.Exports);
        Assert.Equal(1, vm.TotalRows); // Export denial alone does not revoke a valid read grant.
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [AvaloniaTheory]
    [InlineData("subject")]
    [InlineData("session")]
    [InlineData("permission")]
    [InlineData("role")]
    public async Task Identity_or_claim_change_clears_all_protected_display_and_never_rebinds_old_snapshot(string change)
    {
        var users = new TestUsers();
        var repository = new BoundaryRepository();
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true, users: users),
            readPermission: "test:csv", exportPermission: "test:csv");
        await vm.InitializeAsync();
        vm.SearchText = "sensitive input";
        var original = users.CurrentUser;
        if (change == "subject") users.CurrentUser = original with { Subject = "other" };
        if (change == "session") users.SessionVersion++;
        if (change == "permission") users.CurrentUser = original with { Permissions = ImmutableHashSet<string>.Empty };
        if (change == "role") users.CurrentUser = original with { Roles = ImmutableHashSet.Create("changed") };
        users.Notify();
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Groups);
        Assert.Equal(0, vm.TotalRows);
        Assert.Equal(0, vm.FilteredRows);
        Assert.Equal(0, vm.PageCount);
        Assert.Equal("", vm.SearchText);
        Assert.False(vm.ExportCommand.CanExecute(null));
        users.CurrentUser = original;
        users.Notify();
        await vm.InitializeAsync();
        Assert.Equal(1, repository.Reads);
        Assert.Empty(vm.Rows);
        vm.OnUiLanguageChanged();
        Assert.Empty(vm.Rows);
    }

    [AvaloniaFact]
    public async Task Identity_change_cancels_noncooperative_read_and_blocks_late_publication()
    {
        var users = new TestUsers();
        var repository = new BoundaryRepository();
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        repository.BeforeRead = () => release.Task;
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true, users: users),
            readPermission: "test:csv");
        var pending = vm.InitializeAsync();
        users.SessionVersion++;
        users.Notify();
        Assert.True(repository.ReadToken.IsCancellationRequested);
        release.SetResult(true);
        await pending;
        Assert.Empty(vm.Rows);
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_revalidates_after_confirmation_and_before_final_file_publication(bool duringConfirmation)
    {
        var users = new TestUsers();
        var operations = Operations(authentication: true, production: true, users: users);
        var repository = new BoundaryRepository();
        void Revoke() => users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty };
        operations.ConfirmProductionAsync = () =>
        {
            if (duringConfirmation) Revoke();
            return Task.FromResult(true);
        };
        if (!duringConfirmation) repository.AfterExport = Revoke;
        using var vm = new AdvancedTableViewModel(repository, operations,
            readPermission: "test:csv", exportPermission: "test:csv");
        await vm.InitializeAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-revoked-export-{Guid.NewGuid():N}");
        try
        {
            await vm.ExportToAsync(directory);
            Assert.Equal(duringConfirmation ? 0 : 1, repository.Exports);
            Assert.True(!Directory.Exists(directory) || Directory.GetFiles(directory).Length == 0);
            Assert.Empty(vm.Rows);
            Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Explicit_host_mapping_reaches_navigation_page_and_exports_business_snapshot()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Operations(authentication: true));
        services.AddAdvancedTableModule(new BoundaryRepository(),
            readPermission: "test:csv", exportPermission: "test:csv");
        using var provider = services.BuildServiceProvider();
        using var vm = Assert.IsType<AdvancedTableViewModel>(
            Assert.Single(provider.GetServices<NavigationItem>()).CreateViewModel());
        await vm.InitializeAsync();
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-mapped-export-{Guid.NewGuid():N}");
        try
        {
            await vm.ExportToAsync(directory);
            Assert.Equal(2, File.ReadAllLines(Assert.Single(Directory.GetFiles(directory))).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Read_rechecks_permission_before_publishing_even_without_notification()
    {
        var users = new TestUsers();
        var repository = new BoundaryRepository
        {
            BeforeRead = () =>
            {
                users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty };
                return Task.CompletedTask;
            }
        };
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true, users: users),
            readPermission: "test:csv");
        await vm.InitializeAsync();
        Assert.Equal(1, repository.Reads);
        Assert.Empty(vm.Rows);
        Assert.Empty(vm.Groups);
        Assert.Equal(0, vm.TotalRows);
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [AvaloniaFact]
    public async Task Identity_event_during_export_cancels_enumeration_and_cleans_files()
    {
        var users = new TestUsers();
        var repository = new BoundaryRepository();
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true, users: users),
            readPermission: "test:csv", exportPermission: "test:csv");
        await vm.InitializeAsync();
        repository.AfterExport = () =>
        {
            users.SessionVersion++;
            users.Notify();
        };
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-event-export-{Guid.NewGuid():N}");
        try
        {
            await vm.ExportToAsync(directory);
            Assert.True(repository.ExportToken.IsCancellationRequested);
            Assert.Empty(Directory.GetFiles(directory));
            Assert.Empty(vm.Rows);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task Imported_snapshot_is_protected_and_recovery_requires_fresh_authorized_import()
    {
        var users = new TestUsers();
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users), importPermission: "test:csv",
            readPermission: "test:csv", exportPermission: "test:csv");
        await vm.ImportFromPickerAsync(_ => Select(Header + "old,A,Old identity,1,1"));
        Assert.Equal(1, vm.TotalRows);
        users.SessionVersion++;
        users.Notify();
        Assert.Empty(vm.Rows);
        await vm.InitializeAsync();
        Assert.Empty(vm.Rows);
        await vm.ImportFromPickerAsync(_ => Select(Header + "new,B,New identity,2,2"));
        Assert.Equal("new", Assert.Single(vm.Rows.Where(row => row.Kind == AdvancedTableRowKind.Data)).Id);
        Assert.Equal(new[] { "", "B" }, vm.Groups);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public async Task Authenticated_default_synthetic_pilot_remains_usable_without_business_mappings()
    {
        var users = new TestUsers();
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users));
        await vm.InitializeAsync();
        users.SessionVersion++;
        users.Notify();
        Assert.Equal(240, vm.TotalRows);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Page);
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-synthetic-{Guid.NewGuid():N}");
        try
        {
            await vm.ExportToAsync(directory);
            Assert.Equal(241, File.ReadAllLines(Assert.Single(Directory.GetFiles(directory))).Length);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaTheory]
    [InlineData("read")]
    [InlineData("export")]
    [InlineData("import")]
    public async Task Unchanged_identity_refresh_does_not_cancel_pending_operation(string operation)
    {
        var users = new TestUsers();
        var repository = new BoundaryRepository();
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true, users: users),
            importPermission: "test:csv", readPermission: "test:csv", exportPermission: "test:csv");
        void Refresh()
        {
            // UserInfo publishes a new immutable object even when claims are unchanged.
            users.CurrentUser = users.CurrentUser with { };
            users.Notify();
        }
        if (operation == "read")
        {
            repository.BeforeRead = () => { Refresh(); return Task.CompletedTask; };
            await vm.InitializeAsync();
            Assert.False(repository.ReadToken.IsCancellationRequested);
        }
        else if (operation == "import")
        {
            await vm.ImportFromPickerAsync(token =>
            {
                Refresh();
                Assert.False(token.IsCancellationRequested);
                return Select(Header + "imported,A,Imported,1,1");
            });
        }
        else
        {
            await vm.InitializeAsync();
            repository.AfterExport = Refresh;
            var directory = Path.Combine(Path.GetTempPath(), $"plinth-refresh-export-{Guid.NewGuid():N}");
            try
            {
                await vm.ExportToAsync(directory);
                Assert.False(repository.ExportToken.IsCancellationRequested);
                Assert.EndsWith(".csv", Assert.Single(Directory.GetFiles(directory)));
            }
            finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        }
        Assert.Equal(1, vm.TotalRows);
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Session_or_claim_change_during_import_from_synthetic_data_cancels_new_operation(bool changeClaims)
    {
        var users = new TestUsers();
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users), importPermission: "test:csv",
            readPermission: "test:csv");
        await vm.InitializeAsync();
        var observedCancellation = false;
        await vm.ImportFromPickerAsync(token =>
        {
            if (changeClaims)
                users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty };
            else
                users.SessionVersion++;
            users.Notify();
            observedCancellation = token.IsCancellationRequested;
            return Select(Header + "private,A,Private,1,1");
        });
        Assert.True(observedCancellation);
        Assert.Equal(240, vm.TotalRows);
        Assert.DoesNotContain(vm.Rows, row => row.Id == "private");
    }

    [Fact]
    public async Task Concrete_repository_with_supplied_business_rows_is_not_synthetic()
    {
        var repository = new AdvancedTableRepository(
            new[] { new AdvancedTableRow("private", "A", "Business", 1, 1m) });
        Assert.False(repository.IsSynthetic);
        using var vm = new AdvancedTableViewModel(repository, Operations(authentication: true));
        await vm.InitializeAsync();
        Assert.Empty(vm.Rows);
        Assert.False(vm.ExportCommand.CanExecute(null));
        Assert.Equal(Loc.Get("advanced.denied"), vm.StatusText);
    }

    [Fact]
    public async Task Disposing_page_releases_identity_event_subscription()
    {
        var users = new TestUsers();
        var vm = new AdvancedTableViewModel(new AdvancedTableRepository(),
            Operations(authentication: true, users: users));
        try
        {
            await vm.InitializeAsync();
            Assert.Equal(1, users.SubscriberCount);
            vm.Dispose();
            vm.Dispose();
            Assert.Equal(0, users.SubscriberCount);
            users.Notify();
        }
        finally { vm.Dispose(); }
    }

    private sealed class BoundaryRepository : IAdvancedTableRepository
    {
        private readonly AdvancedTableRepository _inner = new(
            new[] { new AdvancedTableRow("business", "Private", "Protected label", 1, 2m) });
        public int Reads { get; private set; }
        public int Exports { get; private set; }
        public CancellationToken ReadToken { get; private set; }
        public CancellationToken ExportToken { get; private set; }
        public Func<Task>? BeforeRead { get; set; }
        public Action? AfterExport { get; set; }
        public async Task<AdvancedTablePage> ReadPageAsync(AdvancedTableQuery query,
            CancellationToken cancellationToken = default)
        {
            Reads++;
            ReadToken = cancellationToken;
            if (BeforeRead is not null) await BeforeRead();
            // Deliberately ignore cancellation to exercise the caller's final guard.
            return await _inner.ReadPageAsync(query);
        }
        public async IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(string? search, string? group,
            int batchSize, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Exports++;
            ExportToken = cancellationToken;
            await foreach (var row in _inner.ReadFilteredAsync(search, group, batchSize, cancellationToken))
                yield return row;
            AfterExport?.Invoke();
        }
    }

    private sealed class TestUsers : IRemoteUserContext, Plinth.App.Auth.IIdentitySessionEvents
    {
        public RemoteUserContext CurrentUser { get; set; } = new("tester",
            ImmutableHashSet<string>.Empty, ImmutableHashSet.Create("test:csv"));
        public long SessionVersion { get; set; }
        public event EventHandler? SessionChanged;
        public int SubscriberCount => SessionChanged?.GetInvocationList().Length ?? 0;
        public void Notify() => SessionChanged?.Invoke(this, EventArgs.Empty);
    }

    private sealed class TestAudit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }

    private sealed class FailingDisposeStream() : MemoryStream(Encoding.UTF8.GetBytes(Header + "new,A,New,1,1"))
    {
        public override ValueTask DisposeAsync()
        {
            base.Dispose();
            return ValueTask.FromException(new IOException("Test stream disposal failure"));
        }
    }

    private sealed class CancelAfterRowRepository(
        IAdvancedTableRepository inner, CancellationTokenSource cancellation) : IAdvancedTableRepository
    {
        public Task<AdvancedTablePage> ReadPageAsync(AdvancedTableQuery query, CancellationToken cancellationToken = default) =>
            inner.ReadPageAsync(query, cancellationToken);

        public async IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(
            string? search, string? group, int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await foreach (var row in inner.ReadFilteredAsync(search, group, batchSize, cancellationToken))
            {
                yield return row;
                cancellation.Cancel(); // Resume only after the exporter writes the first detail.
            }
        }
    }

    private sealed class NonSeekableStream(byte[] bytes, Action? afterRead = null) : Stream
    {
        private readonly MemoryStream _inner = new(bytes);
        public int BytesRead { get; private set; }
        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var count = await _inner.ReadAsync(buffer, cancellationToken);
            BytesRead += count;
            afterRead?.Invoke();
            return count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
