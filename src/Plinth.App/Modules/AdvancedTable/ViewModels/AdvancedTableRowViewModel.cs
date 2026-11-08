using Plinth.App.Modules.AdvancedTable.Models;
using Plinth.App.Localization;

namespace Plinth.App.Modules.AdvancedTable.ViewModels;

public enum AdvancedTableRowKind { Data, GroupHeader, GroupSubtotal, GrandTotal }

public sealed class AdvancedTableRowViewModel
{
    private AdvancedTableRowViewModel(AdvancedTableRowKind kind, string id, string group,
        string label, int quantity, decimal amount, int rowCount)
    {
        Kind = kind; Id = id; Group = group; Label = label;
        Quantity = quantity; Amount = amount; RowCount = rowCount;
    }

    public AdvancedTableRowKind Kind { get; }
    public string Id { get; }
    public string Group { get; }
    public string Label { get; }
    public int Quantity { get; }
    public decimal Amount { get; }
    public int RowCount { get; }
    public bool IsSubtotal => Kind == AdvancedTableRowKind.GroupSubtotal;
    public bool IsGrandTotal => Kind == AdvancedTableRowKind.GrandTotal;
    public string RowCountText => Kind == AdvancedTableRowKind.GroupHeader ? "" : RowCount.ToString("N0");
    public string QuantityText => Kind == AdvancedTableRowKind.GroupHeader ? "" : Quantity.ToString("N0");
    public string AmountText => Kind == AdvancedTableRowKind.GroupHeader ? "" : Amount.ToString("N2");
    public string KindText => Kind switch
    {
        AdvancedTableRowKind.GroupHeader => Loc.Get("advanced.kindGroup"),
        AdvancedTableRowKind.GroupSubtotal => Loc.Get("advanced.kindSubtotal"),
        AdvancedTableRowKind.GrandTotal => Loc.Get("advanced.kindTotal"),
        _ => Loc.Get("advanced.kindDetail"),
    };

    public static AdvancedTableRowViewModel Header(string group) =>
        new(AdvancedTableRowKind.GroupHeader, string.Empty, group, Loc.Format("advanced.groupLabel", group), 0, 0, 0);

    public static AdvancedTableRowViewModel Data(AdvancedTableRow row) =>
        new(AdvancedTableRowKind.Data, row.Id, row.Group, row.Label, row.Quantity, row.Amount, 1);
    public static AdvancedTableRowViewModel Subtotal(AdvancedTableGroupSummary summary) =>
        new(AdvancedTableRowKind.GroupSubtotal, string.Empty, summary.Group,
            Loc.Format("advanced.subtotalLabel", summary.Group), summary.Quantity, summary.Amount, summary.RowCount);
    public static AdvancedTableRowViewModel Total(int rowCount, int quantity, decimal amount) =>
        new(AdvancedTableRowKind.GrandTotal, string.Empty, string.Empty,
            Loc.Get("advanced.totalLabel"), quantity, amount, rowCount);

    internal AdvancedTableRowViewModel Relocalize() => Kind switch
    {
        AdvancedTableRowKind.GroupHeader => Header(Group),
        AdvancedTableRowKind.GroupSubtotal => Subtotal(new(Group, RowCount, Quantity, Amount)),
        AdvancedTableRowKind.GrandTotal => Total(RowCount, Quantity, Amount),
        _ => this,
    };
}
