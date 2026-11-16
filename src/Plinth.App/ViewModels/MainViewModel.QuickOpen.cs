using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Plinth.App.Navigation;

namespace Plinth.App.ViewModels;

public partial class MainViewModel
{
    // 页面命中在前,内容命中在后;两类都可能是 object,模板按类型区分。
    public ObservableCollection<object> QuickOpenItems { get; } = [];

    [ObservableProperty]
    public partial bool IsQuickOpen { get; set; }

    [ObservableProperty]
    public partial string QuickOpenText { get; set; } = "";

    [ObservableProperty]
    public partial object? QuickOpenSelection { get; set; }

    private CancellationTokenSource? _contentSearchCts;
    private int _contentSearchVersion;
    private Task? _contentSearchTask;

    partial void OnQuickOpenTextChanged(string value) => RefreshQuickOpen();

    [RelayCommand]
    private void OpenQuickOpen()
    {
        if (_released) return;
        QuickOpenText = "";
        RefreshQuickOpen();
        QuickOpenSelection = QuickOpenItems.FirstOrDefault();
        IsQuickOpen = true;
    }

    [RelayCommand]
    private void CloseQuickOpen() => IsQuickOpen = false;

    [RelayCommand]
    private void AcceptQuickOpen(object? item = null)
    {
        switch (item ?? QuickOpenSelection)
        {
            case NavigationItem target:
                OpenTab(target);
                break;
            case ContentSearchHit hit:
                OpenContentHit(hit);
                break;
        }
        IsQuickOpen = false;
    }

    private void RefreshQuickOpen()
    {
        var query = QuickOpenText.Trim();
        var matches = MenuItems.Where(item =>
            query.Length == 0 || item.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        QuickOpenItems.Clear();
        foreach (var item in matches) QuickOpenItems.Add(item);
        if (QuickOpenSelection is null || !QuickOpenItems.Contains(QuickOpenSelection))
            QuickOpenSelection = QuickOpenItems.FirstOrDefault();

        // 内容搜索是异步的:页面命中立刻显示,模块命中到了再补上。
        RefreshContentSearch(query);
    }

    /// <summary>逐字符触发查询:每次取消上一次,只让最新一次的结果上屏。</summary>
    private void RefreshContentSearch(string query)
    {
        var version = ++_contentSearchVersion;
        _contentSearchCts?.Cancel();
        if (_contentSearchProviders.Length == 0 || query.Length == 0)
        {
            _contentSearchCts = null;
            return;
        }
        var cts = new CancellationTokenSource();
        _contentSearchCts = cts;
        _contentSearchTask = SearchContentAsync(query, version, cts.Token);
    }

    private async Task SearchContentAsync(string query, int version, CancellationToken token)
    {
        IReadOnlyList<ContentSearchHit>[] results;
        try
        {
            // 单个提供者抛错隔离成空结果:一个模块的查询失败不能拖垮全局搜索。
            var searches = _contentSearchProviders.Select(async provider =>
            {
                try { return await provider.SearchAsync(query, token); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { return (IReadOnlyList<ContentSearchHit>)[]; }
            });
            results = await Task.WhenAll(searches);
            token.ThrowIfCancellationRequested();
        }
        catch (OperationCanceledException)
        {
            return; // 被更新的查询取代
        }
        catch (Exception)
        {
            return;
        }
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (_released || version != _contentSearchVersion) return;
            for (var i = QuickOpenItems.Count - 1; i >= 0; i--)
                if (QuickOpenItems[i] is ContentSearchHit) QuickOpenItems.RemoveAt(i);
            foreach (var hit in results.SelectMany(result => result))
                QuickOpenItems.Add(hit);
            if (QuickOpenSelection is null && QuickOpenItems.Count > 0)
                QuickOpenSelection = QuickOpenItems[0];
        });
    }

    private void OpenContentHit(ContentSearchHit hit)
    {
        var item = MenuItems.FirstOrDefault(nav => nav.Id == hit.PageId);
        if (item is null) return;
        OpenTab(item);
        var tab = Tabs.FirstOrDefault(tab => tab.Id == hit.PageId);
        (tab?.Content as IOpenRequestTarget)?.HandleOpenRequest(hit.Payload);
    }

    /// <summary>测试用:等待最近一次内容搜索结束,保证断言确定性。</summary>
    internal Task WaitForContentSearchForTests() => _contentSearchTask ?? Task.CompletedTask;
}
