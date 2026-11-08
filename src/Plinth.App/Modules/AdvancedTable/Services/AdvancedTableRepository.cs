using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Plinth.App.Modules.AdvancedTable.Models;

namespace Plinth.App.Modules.AdvancedTable.Services;

/// <summary>
/// Fixed, local pilot dataset. No database or remote records are read.
/// Production adapters must aggregate and page at the source.
/// </summary>
public sealed class AdvancedTableRepository : IAdvancedTableRepository, IAdvancedTableEditableRepository
{
    private AdvancedTableRow[] _rows;
    internal bool IsSynthetic { get; }
    public AdvancedTableRepository() : this(CreatePilotRows()) { IsSynthetic = true; }
    internal AdvancedTableRepository(IReadOnlyList<AdvancedTableRow> rows)
    {
        if (rows.Count > 10000) throw new ArgumentOutOfRangeException(nameof(rows));
        if (rows.Select(row => row.Group).Distinct(StringComparer.Ordinal).Take(17).Count() > 16)
            throw new ArgumentException("Pilot supports at most 16 groups.", nameof(rows));
        if (rows.Select(row => row.Id).Distinct(StringComparer.Ordinal).Count() != rows.Count)
            throw new ArgumentException("Row IDs must be unique.", nameof(rows));
        _rows = rows.OrderBy(row => row.Group, StringComparer.Ordinal)
            .ThenBy(row => row.Id, StringComparer.Ordinal).ToArray();
    }

    public Task UpdateAsync(AdvancedTableRow row, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateText(row.Id, 128, nameof(row.Id));
        ValidateText(row.Group, 128, nameof(row.Group));
        ValidateText(row.Label, 1024, nameof(row.Label), allowNewlines: true);
        if (row.Quantity is < -100000 or > 100000)
            throw new ArgumentOutOfRangeException(nameof(row), "Quantity is outside the validated range.");
        if (row.Amount is < -1000000000000m or > 1000000000000m
            || decimal.Round(row.Amount, 2) != row.Amount)
            throw new ArgumentOutOfRangeException(nameof(row), "Amount is outside the validated range.");
        var index = Array.FindIndex(_rows, existing => existing.Id == row.Id);
        if (index < 0) throw new KeyNotFoundException($"Row '{row.Id}' was not found.");
        _rows[index] = row;
        return Task.CompletedTask;
    }

    private static void ValidateText(string value, int limit, string name, bool allowNewlines = false)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit || value != value.Trim()
            || value.Any(c => char.IsControl(c) && !(allowNewlines && c is '\r' or '\n' or '\t')))
            throw new ArgumentException($"{name} is empty, too long, padded or contains unsupported control characters.", name);
    }

    public Task<AdvancedTablePage> ReadPageAsync(
        AdvancedTableQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (query.Page < 1) throw new ArgumentOutOfRangeException(nameof(query.Page));
        if (query.PageSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(query.PageSize));
        var rows = new List<AdvancedTableRow>(query.PageSize);
        var groups = new Dictionary<string, AdvancedTableGroupSummary>(StringComparer.Ordinal);
        var count = 0;
        var quantity = 0;
        var amount = 0m;
        foreach (var row in Filter(_rows, query.Search, query.Group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (count >= query.Skip && rows.Count < query.PageSize) rows.Add(row);
            count++;
            quantity = checked(quantity + row.Quantity);
            amount += row.Amount;
            groups.TryGetValue(row.Group, out var previous);
            groups[row.Group] = new(row.Group, (previous?.RowCount ?? 0) + 1,
                checked((previous?.Quantity ?? 0) + row.Quantity), (previous?.Amount ?? 0) + row.Amount);
        }
        return Task.FromResult(new AdvancedTablePage(
            rows.ToArray(), groups.Values.OrderBy(group => group.Group, StringComparer.Ordinal).ToArray(),
            _rows.Length, count, query.Page, query.PageSize, amount, quantity));
    }

    public async IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(
        string? search, string? group, int batchSize,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (batchSize is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(batchSize));
        var emitted = 0;
        foreach (var row in Filter(_rows, search, group))
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return row;
            if (++emitted % batchSize == 0) await Task.Yield();
        }
    }

    private static IEnumerable<AdvancedTableRow> Filter(
        IEnumerable<AdvancedTableRow> rows, string? search, string? group)
    {
        var text = search?.Trim();
        var selectedGroup = group?.Trim();
        return rows.Where(row => string.IsNullOrEmpty(selectedGroup)
                || string.Equals(row.Group, selectedGroup, StringComparison.Ordinal))
            .Where(row => string.IsNullOrEmpty(text)
                || row.Id.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.Label.Contains(text, StringComparison.OrdinalIgnoreCase)
                || row.Group.Contains(text, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<AdvancedTableRow> CreatePilotRows()
    {
        var groups = new[] { "North", "Central", "South" };
        var rows = new List<AdvancedTableRow>(240);
        for (var i = 1; i <= 240; i++)
            rows.Add(new($"AT-{i:0000}", groups[(i - 1) % 3], $"Pilot item {i:0000}",
                (i % 9) + 1, decimal.Round(12.5m + (i % 17) * 3.25m, 2)));
        return rows;
    }
}
