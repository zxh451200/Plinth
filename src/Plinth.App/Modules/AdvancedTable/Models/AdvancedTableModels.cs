using System.Collections.Generic;

namespace Plinth.App.Modules.AdvancedTable.Models;

public sealed record AdvancedTableRow(string Id, string Group, string Label, int Quantity, decimal Amount);
public sealed record AdvancedTableGroupSummary(string Group, int RowCount, int Quantity, decimal Amount);
public sealed record AdvancedTableQuery(string? Search, string? Group, int Page, int PageSize)
{
    public long Skip => ((long)Page - 1) * PageSize;
}
public sealed record AdvancedTablePage(
    IReadOnlyList<AdvancedTableRow> Rows,
    IReadOnlyList<AdvancedTableGroupSummary> Groups,
    int TotalRows, int FilteredRows, int Page, int PageSize,
    decimal FilteredAmount, int FilteredQuantity)
{
    public int PageCount => FilteredRows == 0 ? 0 : (FilteredRows - 1) / PageSize + 1;
}
public interface IAdvancedTableRepository
{
    Task<AdvancedTablePage> ReadPageAsync(AdvancedTableQuery query, CancellationToken cancellationToken = default);
    IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(
        string? search, string? group, int batchSize, CancellationToken cancellationToken = default);
}

/// <summary>
/// Optional write contract for a real host snapshot. TableView remains read-only;
/// callers edit through an explicit form and publish a new page after the update.
/// </summary>
public interface IAdvancedTableEditableRepository
{
    Task UpdateAsync(AdvancedTableRow row, CancellationToken cancellationToken = default);
}
