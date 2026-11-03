using System.Collections.Generic;
using Plinth.App.Services;

namespace Plinth.App.Modules.Demo.Services;

/// <summary>导入结果。Errors 不为空表示没有导入任何数据(整批取消)。</summary>
public sealed record ImportResult(int Added, int Updated, IReadOnlyList<string> Errors)
{
    public string? ReportPath { get; init; }
    public int? DiscoveredErrorRows { get; init; }
    public bool ErrorRowCountComplete { get; init; }
    public bool Succeeded => Errors.Count == 0;

    public static ImportResult Failed(string error) => new(0, 0, [error]);

    public TaskOutcome ToTaskOutcome() => Succeeded ? TaskOutcome.Import(Added, Updated)
        : TaskOutcome.Rejected(DiscoveredErrorRows, ErrorRowCountComplete, ReportPath);
}
