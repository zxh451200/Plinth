using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Navigation;

/// <summary>全局搜索(Ctrl+K)的一条内容命中:打开 PageId 页面,Payload 语义由页面自定。</summary>
public sealed record ContentSearchHit(string PageId, string Title, string Detail, string? Payload = null);

/// <summary>
/// 模块贡献的内容搜索:让 Ctrl+K 不只搜页面标题,也搜模块自己的数据。
/// 实现要自带上限并容忍取消;抛错会被隔离成"本模块无结果",不影响其他模块。
/// </summary>
public interface IContentSearchProvider
{
    string PageId { get; }

    Task<IReadOnlyList<ContentSearchHit>> SearchAsync(string query, CancellationToken token);
}

/// <summary>页面接收"从全局搜索打开并定位"的请求;Content 是页面 ViewModel。</summary>
public interface IOpenRequestTarget
{
    void HandleOpenRequest(string? payload);
}
