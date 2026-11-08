using System.Collections.Generic;
using System.IO;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.DependencyInjection;
using Plinth.App.Modules.AdvancedTable;
using Plinth.App.Modules.AdvancedTable.Models;
using Plinth.App.Modules.AdvancedTable.Services;
using Plinth.App.Modules.AdvancedTable.ViewModels;
using Plinth.App.Modules.AdvancedTable.Views;
using Plinth.App.Navigation;
using Xunit;

namespace Plinth.App.Tests;

public sealed class AdvancedTableTests
{
    [Fact]
    public async Task Page_contract_returns_bounded_rows_and_scope_totals()
    {
        var repository = new AdvancedTableRepository(Enumerable.Range(1, 120)
            .Select(i => new AdvancedTableRow($"ID-{i}", i % 2 == 0 ? "A" : "B",
                $"Item {i}", 1, i)).ToArray());

        var result = await repository.ReadPageAsync(new AdvancedTableQuery(null, "A", 2, 10));

        Assert.Equal(60, result.FilteredRows);
        Assert.Equal(10, result.Rows.Count);
        Assert.Equal(60, result.FilteredQuantity);
        Assert.Equal(3660m, result.FilteredAmount);
        Assert.Equal(6, result.PageCount);
        Assert.Equal(60, Assert.Single(result.Groups).RowCount);
    }

    [Fact]
    public async Task View_model_keeps_detail_rows_bounded_and_publishes_totals()
    {
        var rows = Enumerable.Range(1, 120)
            .Select(i => new AdvancedTableRow($"ID-{i}", i % 2 == 0 ? "A" : "B",
                $"Item {i}", 2, 10)).ToArray();
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository(rows));

        await vm.InitializeAsync();

        Assert.Equal(50, vm.Rows.Count(row => row.Kind == AdvancedTableRowKind.Data));
        Assert.Equal(2, vm.Rows.Count(row => row.IsSubtotal));
        var total = Assert.Single(vm.Rows.Where(row => row.IsGrandTotal));
        Assert.Equal(120, total.RowCount);
        Assert.Equal(240, total.Quantity);
        Assert.Equal(1200m, total.Amount);
    }

    [Fact]
    public async Task Filtered_export_stream_reads_only_the_explicit_scope()
    {
        var rows = Enumerable.Range(1, 120)
            .Select(i => new AdvancedTableRow($"ID-{i}", i % 2 == 0 ? "A" : "B",
                $"Item {i}", 1, 1)).ToArray();
        var repository = new AdvancedTableRepository(rows);
        var exported = new List<AdvancedTableRow>();
        await foreach (var row in repository.ReadFilteredAsync("Item", "B", 17))
            exported.Add(row);

        Assert.Equal(60, exported.Count);
        Assert.All(exported, row => Assert.Equal("B", row.Group));
    }

    [Fact]
    public async Task Empty_and_out_of_range_pages_preserve_scope_metadata()
    {
        var repository = new AdvancedTableRepository();
        var empty = await repository.ReadPageAsync(new("does-not-exist", null, 1, 50));
        Assert.Empty(empty.Rows);
        Assert.Empty(empty.Groups);
        Assert.Equal(0, empty.PageCount);
        Assert.Equal(0m, empty.FilteredAmount);
        var beyond = await repository.ReadPageAsync(new(null, null, int.MaxValue, 200));
        Assert.Empty(beyond.Rows);
        Assert.Equal(240, beyond.FilteredRows);
        Assert.Equal(2, beyond.PageCount);
    }

    [Theory]
    [InlineData(0, 50)]
    [InlineData(1, 0)]
    [InlineData(1, 201)]
    public async Task Invalid_paging_is_rejected(int page, int size)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new AdvancedTableRepository().ReadPageAsync(new(null, null, page, size)));
    }

    [Theory]
    [InlineData("", "A", "Label")]
    [InlineData("ID", "", "Label")]
    [InlineData("ID", "A", " label")]
    [InlineData("ID", "A", "bad\u0001label")]
    public async Task Editing_preserves_snapshot_text_constraints(string id, string group, string label)
    {
        var repository = new AdvancedTableRepository();
        await Assert.ThrowsAnyAsync<ArgumentException>(() => repository.UpdateAsync(
            new AdvancedTableRow(id, group, label, 1, 1m)));
    }

    [Fact]
    public async Task Grouped_page_boundaries_never_duplicate_or_skip_detail_rows()
    {
        var repository = new AdvancedTableRepository();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        for (var page = 1; page <= 5; page++)
        {
            var result = await repository.ReadPageAsync(new(null, null, page, 50));
            Assert.InRange(result.Rows.Count, 1, 50);
            Assert.Equal(240, result.Groups.Sum(group => group.RowCount));
            Assert.Equal(result.FilteredAmount, result.Groups.Sum(group => group.Amount));
            foreach (var row in result.Rows) Assert.True(ids.Add(row.Id));
        }
        Assert.Equal(240, ids.Count);
    }

    [Fact]
    public async Task Cancellation_is_observed_during_read_and_export()
    {
        using var cancellation = new CancellationTokenSource();
        var repository = new AdvancedTableRepository();
        await using var iterator = repository.ReadFilteredAsync(null, null, 50, cancellation.Token)
            .GetAsyncEnumerator();
        Assert.True(await iterator.MoveNextAsync());
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { await iterator.MoveNextAsync(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            repository.ReadPageAsync(new(null, null, 1, 50), cancellation.Token));
    }

    [Fact]
    public async Task Csv_writer_escapes_text_and_exports_whole_filtered_scope_with_invariant_numbers()
    {
        var rows = Enumerable.Range(1, 60).Select(i =>
            new AdvancedTableRow($"ID-{i:000}", "A", "  =SUM(1,2)\"test\"", 2, 1.25m)).ToArray();
        var repository = new AdvancedTableRepository(rows);
        using var writer = new StringWriter();
        await AdvancedTableCsvExporter.WriteAsync(repository, null, "A", writer, CancellationToken.None);
        var lines = writer.ToString().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(61, lines.Length);
        Assert.All(lines.Skip(1), line =>
        {
            Assert.Contains("\"'  =SUM(1,2)\"\"test\"\"\"", line);
            Assert.EndsWith(",2,1.25", line);
        });
    }

    [Fact]
    public async Task Cancelled_file_export_leaves_no_final_or_partial_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-advanced-{Guid.NewGuid():N}");
        try
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                AdvancedTableCsvExporter.ExportAsync(new AdvancedTableRepository(), null, null,
                    directory, cancellation.Token));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Export_publication_guard_runs_after_close_and_rejection_cleans_only_partial(bool cancel)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var existing = Path.Combine(directory, "existing.csv");
        await File.WriteAllTextAsync(existing, "keep");
        using var cancellation = new CancellationTokenSource();
        var guarded = false;
        try
        {
            var pending = AdvancedTableCsvExporter.ExportAsync(new AdvancedTableRepository(), null, null,
                directory, cancellation.Token, publish =>
                {
                    var partial = Assert.Single(Directory.GetFiles(directory, "*.partial"));
                    Assert.Equal(241, File.ReadAllLines(partial).Length);
                    using (File.Open(partial, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
                    Assert.Single(Directory.GetFiles(directory, "*.csv"));
                    guarded = true;
                    if (!cancel) throw new UnauthorizedAccessException();
                    cancellation.Cancel();
                    publish();
                    return Task.CompletedTask;
                });
            if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
            Assert.True(guarded);
            Assert.Equal(existing, Assert.Single(Directory.GetFiles(directory)));
            Assert.Equal("keep", await File.ReadAllTextAsync(existing));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Paging_and_export_use_applied_scope_until_filter_is_applied()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository());
        await vm.InitializeAsync();
        vm.SearchText = "does-not-exist";
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Page);
        Assert.Equal(240, vm.FilteredRows);
        Assert.Equal(50, vm.Rows.Count(row => row.Kind == AdvancedTableRowKind.Data));
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-advanced-{Guid.NewGuid():N}");
        try
        {
            await vm.ExportToAsync(directory);
            Assert.Equal(241, File.ReadAllLines(Assert.Single(Directory.GetFiles(directory))).Length);
            await vm.ApplyFilterCommand.ExecuteAsync(null);
            Assert.Equal(1, vm.Page);
            Assert.Equal(0, vm.FilteredRows);
            Assert.False(vm.NextPageCommand.CanExecute(null));
            Assert.Single(vm.Rows);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [AvaloniaFact]
    public async Task Standalone_module_resolves_and_view_binds_table_and_filter()
    {
        using var provider = new ServiceCollection().AddAdvancedTableModule().BuildServiceProvider();
        var navigation = Assert.Single(provider.GetServices<NavigationItem>());
        using var vm = Assert.IsType<AdvancedTableViewModel>(navigation.CreateViewModel());
        await vm.InitializeAsync();
        var view = new AdvancedTableView { DataContext = vm };
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            var table = view.FindControl<TableView>("PilotTable")!;
            Assert.Same(vm.Rows, table.ItemsSource);
            Assert.Equal(7, table.Columns.Count);
            vm.SelectedGroup = "North";
            await vm.ApplyFilterCommand.ExecuteAsync(null);
            Assert.Equal(80, vm.FilteredRows);
            Assert.All(vm.Rows.Where(row => row.Kind == AdvancedTableRowKind.Data),
                row => Assert.Equal("North", row.Group));
            Assert.Equal(80, Assert.Single(vm.Rows.Where(row => row.IsSubtotal)).RowCount);
            Assert.Same(vm.ApplyFilterCommand, view.FindControl<Button>("ApplyButton")!.Command);
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Apply_after_initial_read_failure_restores_group_choices_and_export()
    {
        using var vm = new AdvancedTableViewModel(new FailFirstReadRepository());
        await vm.InitializeAsync();
        Assert.Empty(vm.Groups);
        Assert.False(vm.ExportCommand.CanExecute(null));

        vm.SearchText = "Pilot";
        vm.SelectedGroup = "North";
        await vm.ApplyFilterCommand.ExecuteAsync(null);

        Assert.Contains("Central", vm.Groups);
        Assert.Contains("North", vm.Groups);
        Assert.Contains("South", vm.Groups);
        Assert.Equal(80, vm.FilteredRows);
        Assert.All(vm.Rows.Where(row => row.Kind == AdvancedTableRowKind.Data),
            row => Assert.Equal("North", row.Group));
        Assert.True(vm.ExportCommand.CanExecute(null));
    }

    [AvaloniaFact]
    public void View_initializes_when_data_context_arrives_after_attachment()
    {
        using var vm = new AdvancedTableViewModel(new AdvancedTableRepository());
        var view = new AdvancedTableView();
        var window = new Window { Content = view, Width = 1100, Height = 800 };
        try
        {
            window.Show();
            view.DataContext = vm;
            Assert.Equal(240, vm.FilteredRows);
            Assert.True(vm.ExportCommand.CanExecute(null));
        }
        finally { window.Close(); }
    }

    [Fact]
    public async Task Navigation_page_lifetime_is_owned_by_shell_instead_of_root_container()
    {
        using var provider = new ServiceCollection().AddAdvancedTableModule().BuildServiceProvider();
        var navigation = Assert.Single(provider.GetServices<NavigationItem>());
        using var vm = Assert.IsType<AdvancedTableViewModel>(navigation.CreateViewModel());
        await vm.InitializeAsync();

        provider.Dispose();

        // Root disposal must not dispose a page constructed for the shell.
        Assert.True(vm.ApplyFilterCommand.CanExecute(null));
        vm.Dispose();
        Assert.False(vm.ApplyFilterCommand.CanExecute(null));
    }

    [Fact]
    public async Task Failure_after_first_export_row_cleans_up_the_partial_file()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"plinth-advanced-{Guid.NewGuid():N}");
        try
        {
            await Assert.ThrowsAsync<IOException>(() => AdvancedTableCsvExporter.ExportAsync(
                new FailingExportRepository(), null, null, directory));
            Assert.Empty(Directory.GetFiles(directory));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task Closing_page_cancels_pending_load_and_does_not_publish_late_data()
    {
        var repository = new DelayedRepository();
        using var vm = new AdvancedTableViewModel(repository);
        var pending = vm.InitializeAsync();
        Assert.True(vm.IsBusy);
        Assert.False(vm.ApplyFilterCommand.CanExecute(null));
        vm.Dispose();
        Assert.True(repository.Token.IsCancellationRequested);
        repository.Release.SetResult(await new AdvancedTableRepository().ReadPageAsync(new(null, null, 1, 50)));
        await pending;
        Assert.Empty(vm.Rows);
        Assert.False(vm.ExportCommand.CanExecute(null));
    }

    private sealed class DelayedRepository : IAdvancedTableRepository
    {
        public TaskCompletionSource<AdvancedTablePage> Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public CancellationToken Token { get; private set; }
        public Task<AdvancedTablePage> ReadPageAsync(AdvancedTableQuery query,
            CancellationToken cancellationToken = default)
        {
            Token = cancellationToken;
            return Release.Task; // Deliberately non-cooperative to exercise the publication guard.
        }

        public IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(string? search, string? group,
            int batchSize, CancellationToken cancellationToken = default) =>
            new AdvancedTableRepository().ReadFilteredAsync(search, group, batchSize, cancellationToken);
    }

    private sealed class FailFirstReadRepository : IAdvancedTableRepository
    {
        private readonly AdvancedTableRepository _inner = new();
        private bool _fail = true;

        public Task<AdvancedTablePage> ReadPageAsync(AdvancedTableQuery query,
            CancellationToken cancellationToken = default)
        {
            if (!_fail) return _inner.ReadPageAsync(query, cancellationToken);
            _fail = false;
            return Task.FromException<AdvancedTablePage>(new IOException("Test initial read failure"));
        }

        public IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(string? search, string? group,
            int batchSize, CancellationToken cancellationToken = default) =>
            _inner.ReadFilteredAsync(search, group, batchSize, cancellationToken);
    }

    private sealed class FailingExportRepository : IAdvancedTableRepository
    {
        public Task<AdvancedTablePage> ReadPageAsync(AdvancedTableQuery query,
            CancellationToken cancellationToken = default) =>
            new AdvancedTableRepository().ReadPageAsync(query, cancellationToken);

        public async IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(string? search, string? group,
            int batchSize,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new("ID", "A", "First row", 1, 1m);
            await Task.Yield();
            throw new IOException("Test export failure");
        }
    }
}
