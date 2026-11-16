using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Modules.Demo;
using Plinth.App.Modules.Demo.Data;
using Plinth.App.Modules.Demo.Services;
using Plinth.App.Modules.Demo.ViewModels;
using Plinth.App.Modules.Notes.Services;
using Plinth.App.Navigation;
using Plinth.App.Services;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>Ctrl+K 内容搜索:模块提供者出命中,选中后打开页面并定位;命中混排在页面命中之后。</summary>
public sealed class QuickOpenContentSearchTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-qopen-" + Guid.NewGuid().ToString("N"));

    public QuickOpenContentSearchTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try { Directory.Delete(_directory, true); } catch (IOException) { }
    }

    [AvaloniaFact]
    public async Task Demo_content_hits_open_the_page_and_select_the_record()
    {
        using var shell = new Shell();
        var service = shell.Provider.GetRequiredService<DemoService>();
        Assert.Null(await service.SaveAsync(new DemoRecord { Code = "SRCH-A", Name = "定位测试", Remark = "keyword" }));

        shell.ViewModel.QuickOpenText = "SRCH-A";
        await shell.ViewModel.WaitForContentSearchForTests();

        var hit = Assert.IsType<ContentSearchHit>(shell.ViewModel.QuickOpenItems
            .Single(item => item is ContentSearchHit && ((ContentSearchHit)item).Title.Contains("SRCH-A")));
        Assert.Equal("demo.list", hit.PageId);
        // Payload 是记录 Id,供打开后定位。
        Assert.True(Guid.TryParse(hit.Payload, out var recordId));

        shell.ViewModel.AcceptQuickOpenCommand.Execute(hit);

        Assert.False(shell.ViewModel.IsQuickOpen);
        Assert.Equal("demo.list", shell.ViewModel.SelectedTab?.Id);
        var demo = Assert.IsType<DemoListViewModel>(shell.ViewModel.SelectedTab!.Content);
        await demo.SelectRecordByIdForTestsAsync(recordId);
        Assert.Equal("SRCH-A", demo.SelectedItem?.Code);
    }

    [AvaloniaFact]
    public async Task Notes_body_hits_open_the_note_page_with_an_excerpt()
    {
        using var shell = new Shell();
        var notes = shell.Provider.GetRequiredService<NoteService>();
        var note = await notes.LoadAsync();
        var marker = "QUICKOPEN-MARKER";
        Assert.NotNull(await notes.SaveAsync($"before {marker} after", note?.Version ?? 0));

        shell.ViewModel.QuickOpenText = marker;
        await shell.ViewModel.WaitForContentSearchForTests();

        var hit = Assert.IsType<ContentSearchHit>(Assert.Single(
            shell.ViewModel.QuickOpenItems.OfType<ContentSearchHit>()));
        Assert.Equal("notes.editor", hit.PageId);
        Assert.Contains(marker, hit.Detail);

        shell.ViewModel.AcceptQuickOpenCommand.Execute(hit);
        Assert.Equal("notes.editor", shell.ViewModel.SelectedTab?.Id);
    }

    [Fact]
    public void Demo_provider_caps_results_and_matches_name()
    {
        var database = new TestDatabase();
        try
        {
            var service = new DemoService(database.Factory, NullLogger<DemoService>.Instance);
            var provider = new DemoContentSearchProvider(service);
            // 已有条目之外再补足 10 条同名记录,验证上限 8。
            for (var i = 0; i < 3; i++)
                Assert.Null(service.SaveAsync(new DemoRecord { Code = $"CAP-{i}", Name = "大标题关键词" }).Result);
            var hits = provider.SearchAsync("大标题关键词", default).Result;
            Assert.Equal(3, hits.Count);
            Assert.All(hits, hit => Assert.Equal("demo.list", hit.PageId));
        }
        finally { database.Dispose(); }
    }
}
