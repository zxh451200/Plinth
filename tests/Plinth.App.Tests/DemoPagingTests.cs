using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DemoPagingTests : IDisposable
{
    private readonly TestDatabase _database = new();
    private DemoService Service => new(_database.Factory, NullLogger<DemoService>.Instance);
    public void Dispose() => _database.Dispose();

    private async Task SeedAsync()
    {
        await using var db = await _database.Factory.CreateDbContextAsync();
        db.AddRange(Enumerable.Range(0, 61).Select(i =>
            new DemoRecord { Code = $"A{i:D3}", Name = "Same name", Remark = i % 2 == 0 ? "Even" : "Odd" }));
        await db.SaveChangesAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Equal_sort_values_have_stable_nonoverlapping_pages(bool descending)
    {
        await SeedAsync();
        var first = await Service.SearchPageAsync(null, 1, 25, "Name", descending);
        var second = await Service.SearchPageAsync(null, 2, 25, "Name", descending);
        var repeated = await Service.SearchPageAsync(null, 1, 25, "Name", descending);
        Assert.Equal(61, first.TotalCount);
        Assert.Equal(25, first.Items.Count);
        Assert.Empty(first.Items.Select(r => r.Id).Intersect(second.Items.Select(r => r.Id)));
        Assert.Equal(first.Items.Select(r => r.Id), repeated.Items.Select(r => r.Id));
    }

    [Fact]
    public async Task Invalid_page_and_sort_are_bounded_and_descending_sort_is_applied()
    {
        await SeedAsync();
        var page = await Service.SearchPageAsync(null, int.MaxValue, 25, "Code", true);
        Assert.Equal(3, page.Page);
        Assert.Equal(11, page.Items.Count);
        Assert.Equal("A010", page.Items[0].Code);
        var fallback = await Service.SearchPageAsync(null, -1, int.MaxValue, "unknown");
        Assert.Equal(1, fallback.Page);
        Assert.Equal(200, fallback.PageSize);
        Assert.Equal("A000", fallback.Items[0].Code);
    }

    [Fact]
    public async Task Filter_total_and_page_are_consistent_and_cancelled_queries_stop()
    {
        await SeedAsync();
        var page = await Service.SearchPageAsync("A000", 3, 25);
        Assert.Equal(1, page.TotalCount);
        Assert.Equal(1, page.Page);
        Assert.Single(page.Items);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service.SearchPageAsync(null, cancellationToken: cts.Token));
    }

    [Fact]
    public async Task View_model_keeps_only_current_page_and_filter_resets_page()
    {
        await SeedAsync();
        using var vm = new DemoListViewModel(Service, NullLogger<DemoListViewModel>.Instance);
        vm.PageSize = 25;
        await vm.LoadAsync();
        Assert.Equal(25, vm.Items.Count);
        Assert.Equal(61, vm.TotalCount);
        await vm.NextPageCommand.ExecuteAsync(null);
        Assert.Equal(2, vm.Page);
        vm.SearchText = "A060";
        await vm.LoadAsync();
        Assert.Equal(1, vm.Page);
        Assert.Single(vm.Items);
        vm.SearchText = "No match";
        await vm.LoadAsync();
        Assert.Equal(1, vm.PageCount);
        Assert.True(vm.IsEmpty);
    }
}
