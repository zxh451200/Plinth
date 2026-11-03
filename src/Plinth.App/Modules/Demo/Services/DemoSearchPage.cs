using System.Collections.Generic;
using Plinth.App.Modules.Demo.Data;

namespace Plinth.App.Modules.Demo.Services;

public sealed record DemoSearchPage(IReadOnlyList<DemoRecord> Items, int TotalCount, int Page, int PageSize);
