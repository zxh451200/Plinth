using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plinth.App.Localization;
using Plinth.App.Navigation;
using Plinth.App.Modules.Demo.Services;

namespace Plinth.App.Modules.Demo;

/// <summary>全局搜索的示例数据内容:按编码/名称/备注查前 8 条,命中后回 Demo 页定位记录。</summary>
public sealed class DemoContentSearchProvider(DemoService service) : IContentSearchProvider
{
    public string PageId => DemoModuleRegistration.PageId;

    public async Task<IReadOnlyList<ContentSearchHit>> SearchAsync(string query, CancellationToken token)
    {
        var page = await service.SearchPageAsync(query, 1, 8, "Code", descending: false, token);
        return page.Items
            .Select(record => new ContentSearchHit(
                PageId,
                $"{record.Code} · {record.Name}",
                string.IsNullOrWhiteSpace(record.Remark) ? Loc.Get("demo.col.code") : record.Remark,
                record.Id.ToString("D")))
            .ToList();
    }
}
