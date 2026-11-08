using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Modules.DockOptional;
using Xunit;

namespace Plinth.App.Tests;

public sealed class DockOptionalBoundaryTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"plinth-dock-{Guid.NewGuid():N}");
    private string LayoutPath => Path.Combine(_directory, "dock-layout.json");
    private const string Fallback = """{"active":"default"}""";

    [Fact]
    public async Task Round_trip_and_replacement_preserve_vendor_payload()
    {
        var store = new DockLayoutStore(LayoutPath);
        await store.SaveAsync("""{"active":"first"}""");
        await store.SaveAsync("""{"active":"second","panes":["left","center"]}""");
        var result = await store.LoadAsync(Fallback);
        Assert.False(result.UsedFallback);
        using var json = JsonDocument.Parse(result.Layout);
        Assert.Equal("second", json.RootElement.GetProperty("active").GetString());
        Assert.Equal(2, json.RootElement.GetProperty("panes").GetArrayLength());
    }

    [Theory]
    [InlineData("{broken", "invalid-json")]
    [InlineData("""{"schema":99,"layout":{}}""", "schema")]
    [InlineData("""{"schema":1,"layout":null}""", "schema")]
    public async Task Broken_envelope_returns_the_supplied_fallback_without_overwriting_file(
        string contents, string reason)
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(LayoutPath, contents);
        var result = await new DockLayoutStore(LayoutPath).LoadAsync(Fallback);
        Assert.True(result.UsedFallback);
        Assert.Equal(Fallback, result.Layout);
        Assert.Equal(reason, result.FallbackReason);
        Assert.Equal(contents, await File.ReadAllTextAsync(LayoutPath));
    }

    [Fact]
    public async Task Missing_layout_returns_fallback()
    {
        var result = await new DockLayoutStore(LayoutPath).LoadAsync(Fallback);
        Assert.True(result.UsedFallback);
        Assert.Equal(Fallback, result.Layout);
    }

    [Fact]
    public async Task Invalid_save_and_cancellation_do_not_replace_good_layout()
    {
        var store = new DockLayoutStore(LayoutPath);
        await store.SaveAsync(Fallback);
        var before = await File.ReadAllTextAsync(LayoutPath);
        await Assert.ThrowsAnyAsync<JsonException>(() => store.SaveAsync("{broken"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.SaveAsync("""{"active":"new"}""", new CancellationToken(true)));
        Assert.Equal(before, await File.ReadAllTextAsync(LayoutPath));
    }

    [Fact]
    public void Close_releases_all_resources_once_even_when_one_throws()
    {
        var order = new List<int>();
        var session = new DockOptionalSession();
        var first = session.Own(new Resource(() => order.Add(1)));
        session.Own(first);
        session.Own(new Resource(() => { order.Add(2); throw new InvalidOperationException(); }));
        session.Own(new Resource(() => order.Add(3)));
        Assert.Throws<AggregateException>(session.Dispose);
        session.Dispose();
        Assert.Equal(new[] { 3, 2, 1 }, order);
        Assert.Throws<ObjectDisposedException>(() => session.Own(first));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }

    private sealed class Resource(Action dispose) : IDisposable
    {
        public void Dispose() => dispose();
    }
}
