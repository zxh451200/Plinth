# AdvancedTable capability boundary

Avalonia 12.1.4 `TableView` is used here as the read-only surface. The checked API used by this module exposes columns, templates, selection and column width/visibility, but no cell editor or frozen-column property. The project baseline therefore keeps editing in an explicit form.

The form edits only the existing `Quantity` and `Amount` fields. It validates the same integer, range and two-decimal constraints as the CSV adapter, calls the optional `IAdvancedTableEditableRepository` contract, and reloads the applied page after a successful update. Synthetic and imported snapshots implement that contract; host repositories remain read-only unless they explicitly implement it and provide an edit permission.

Frozen columns are not implemented. A two-table synchronized layout would not provide the required row virtualization, selection, sorting, paging, or edit semantics. No replacement grid dependency was added because the repository baseline rejects deprecated/archived grid choices and no maintained, license-approved control was available in the locked dependency set. A future frozen-column requirement needs a separate control and license/maintenance review.
