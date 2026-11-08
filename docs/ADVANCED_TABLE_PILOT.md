# Advanced table pilot

The independent `AdvancedTable` module demonstrates a table paging contract
without changing existing Demo files. This is a local synthetic / explicitly
imported CSV pilot, not a database integration or remote performance result.

- `IAdvancedTableRepository.ReadPageAsync` returns only the requested page.
- Search and group filtering define an explicit scope before paging.
- Real group headings enclose each group's current-page detail fragment.
  Each subtotal and the grand total cover the complete applied scope, including
  rows outside the current page. A group may span pages; the UI says so explicitly.
- CSV export streams detail rows from the applied scope, excluding summary rows.
  Editing the filter inputs does not change paging or export until Apply succeeds.
  CSV has a UTF-8 BOM, invariant decimal numbers, escaped quotes and formula
  neutralization for text. Export publishes a unique final file only after
  completion; failure/cancellation removes the partial file.
- The view uses Avalonia `TableView`; it does not add deprecated `DataGrid` or
  a custom grid engine.

The shared composition root wires this pilot behind the independent trusted
deployment flag `EnableAdvancedTablePilot`, which defaults to `false`.
Set it in deployment.json or use `PLINTH_EnableAdvancedTablePilot=true`
to expose the synthetic-data pilot independently of `IncludeSampleModules`:

```csharp
using Plinth.App.Modules.AdvancedTable;
// Inside AddPlinthModules:
if (deployment.EnableAdvancedTablePilot)
    services.AddAdvancedTableModule();
```

The conventional ViewModel/View namespace mapping works with the existing
ViewLocator. No ViewLocator edit or extra package is needed.

The default repository owns 240 deterministic synthetic rows in three groups.
The local adapter allows at most 10,000 source rows and 16 groups, sorts by
ordinal group then unique ID, and never materializes a complete filtered result.
It scans the fixed source once for counts/sums, retaining at most 200 requested
details and 16 summaries. The ViewModel requests 50 details and retains at most
83 display rows (50 details + 16 headings + 16 summaries + total). Export yields
one row at a time, observing cancellation per row and yielding every 50 rows.

Page numbering starts at 1. Page size must be 1..200. An empty filter result
has zero pages; an out-of-range page returns empty detail rows with accurate
scope metadata. The UI prevents advancing past the last page. Counts and
aggregates describe the same immutable source snapshot as the details.

An API/EF adapter must implement filtering, aggregation and deterministic paging
at the source, preserve scope consistency across export pages, bound group
cardinality, and enforce authorization. The local pilot never accesses business
records unless the user explicitly selects a CSV snapshot and makes no production
scalability claim. The page uses the existing `Loc` / `{loc:T ...}` bilingual
resources and refreshes generated scope/status/summary text when language changes.

The shell owns page disposal. Closing a page cancels reads, import and export; this
pilot does not enqueue background tasks in the task centre.

Validation:

```sh
dotnet test /home/work/data/xxx/Plinth/tests/Plinth.App.Tests/Plinth.App.Tests.csproj --filter FullyQualifiedName~AdvancedTableTests
```

The implementation environment has no `dotnet` executable, so compilation,
the test suite and Avalonia headless interaction remain unverified. This
desktop Avalonia view has no browser URL and cannot be validated by browser-use.

## Optional real CSV snapshot

The existing `AppDbContext` discovers DemoRecord and Note mappings. DemoRecord
has Code/Name/Remark and Note has Code/Text; their shared ID, Version and audit
fields are not business quantities or amounts. There is no honest mapping to
this report's numeric measures. No database adapter or fabricated measure was
added. The architecture baseline requires database/API grouping and aggregation
at the server when such a business source exists; this bounded local import is
not a replacement for that requirement.

`AdvancedTableCsvRepository.ImportAsync` is an explicit real-data alternative.
The host supplies a readable stream that it has authorized. The adapter never
opens arbitrary paths or URLs. It leaves the stream open, validates the entire
input, and only returns a repository on success. There is no auto-discovery,
fallback to synthetic rows, file watcher, database write, or automatic replacement
of a running page's source. The parameterless module registration still selects
the original 240-row synthetic pilot.

To use the import in an independent host (choose exactly one registration):

```csharp
// The host checks access to a trusted local file before opening it.
await using var input = File.OpenRead(authorizedCsvPath);
var snapshot = await AdvancedTableCsvRepository.ImportAsync(input, cancellationToken);
services.AddAdvancedTableModule(snapshot,
    readPermission: hostReadPermission,
    exportPermission: hostExportPermission,
    importPermission: hostImportPermission); // instead of the parameterless call
```

This overload is confined to the module. The main shared registration and CI
are unchanged. Import errors are `InvalidDataException` with a validation reason
but without echoing cell contents; cancellation propagates as
`OperationCanceledException`. A failed import returns no partially usable
repository. The caller owns error presentation and keeps any previous snapshot.
The existing host/API integration entry point remains available. The page now also
has an explicit **Import CSV snapshot** button, described below.

### Explicit page import

The button uses the current top-level's native `StorageProvider.OpenFilePickerAsync`
(one `*.csv` selection), then `IStorageFile.OpenReadAsync`. It never converts the
selection to an arbitrary filesystem path or fetches a URL. The selected handles
and opened stream are disposed, including on failure or a late picker response.
There is no additional CSV parser: the page calls
`AdvancedTableCsvRepository.ImportAsync`, preserving the exact byte/row/group,
UTF-8, header, quoting, text and numeric validation below. Parsing runs off the UI
thread with the linked cancellation token.

The page prepares the operation through the existing `SensitiveOperationService`
before opening the picker, including the existing production confirmation. The
authenticated path fails closed with no permission mapping by default. A trusted
host integrating an authenticated business use case must explicitly supply an
existing, appropriate permission as the view model's `importPermission` constructor
argument together with its operation service, plus a `readPermission` mapping
before the picker can open. These are integration parameters,
not a new production permission, role grant or deployment setting. The default
module registration does not supply one. Offline import remains a local operation
with the service's existing audit behavior; file selection is not proof of business
or tenant authorization. The captured session and permission are checked again
before parsing and immediately before publishing the new snapshot.

Only after full import validation, stream disposal and a successful initial page
read does the page replace its own repository reference. It does not replace the
DI singleton or another page's snapshot. Success rebuilds groups, clears both
filter inputs and applied filters, and displays page 1 of the complete imported
scope, including correct empty-snapshot behavior. Paging, totals and export then
use that same imported snapshot. Failure, rejection or cancellation preserves the
previous rows, groups, page, totals and applied scope, including unapplied input
edits, provided the previous snapshot is still authorized for the same session.
Identity/claim changes or read denial invalidate protected state instead.
Dismissing the picker also preserves the prior status. Errors are localized,
do not echo cell values, paths or raw exceptions, and never fall back to synthetic
data. The page distinguishes synthetic, imported and host-provided sources.

During import the existing busy state blocks other operations and a **Cancel
import** command cancels the linked operation token. Detachment or DataContext
replacement invalidates the view generation and cancels its page token; shell
disposal also cancels the view model lifetime. After the native dialog returns,
the view checks the captured generation, DataContext, top-level, window visibility
and token before opening the file. An old selection is discarded even if the view
is subsequently reattached. Cancellation cannot forcibly close a native dialog:
the busy operation unwinds when the dialog returns, but its result cannot publish.
Late page cancellation does not overwrite the previous status.

`EnableAdvancedTablePilot` remains default `false`; no default deployment enablement,
database mapping, task-centre job, persistence or new production grant was added.

CSV contract:

- UTF-8, optional BOM, at most 8 MiB including BOM. The limit applies to
  non-seekable streams too, with at most one byte read beyond the limit.
- Exact header `Id,Group,Label,Quantity,Amount`; exactly five fields per record.
  Commas, doubled quotes and newlines inside quoted fields are supported.
  Record endings are LF or CRLF. Malformed quotes, blank records and extra
  columns are rejected. A header-only file is a valid empty snapshot.
- At most 10,000 detail rows and 16 ordinal, case-sensitive groups. IDs are
  ordinal unique, at most 128 UTF-16 code units; groups are at most 128 and
  labels at most 1,024. All are required and must have no surrounding whitespace.
  Controls are rejected except embedded CR/LF/tab in labels.
- Quantity is an invariant signed integer in [-100000, 100000]. Amount is an
  invariant signed decimal in [-1000000000000, 1000000000000] with at most two
  fractional digits. No exponent, thousands separators, plus signs, padded
  numbers, locale conversion, rounding or inferred values. Negative values
  permit explicit adjustments; the host must establish consistent units and
  currency before combining records. These bounds make every subset sum safe.

Example schema only (the host supplies actual authorized values):

```csv
Id,Group,Label,Quantity,Amount
```

An imported repository owns an immutable sorted copy, ordered by ordinal Group
then Id. Search, group filtering, page metadata, full-scope group/grand totals
and streamed export all use that same snapshot. It reuses the bounded local
query engine: page sizes 1..200, at most 16 summaries, and no full filtered-result
materialization. The initial import deliberately holds the bounded file and
parsed rows in memory; it makes no large-data/server-performance claim.
Export retains UTF-8 BOM, two-digit invariant amounts, formula neutralization,
per-row cancellation and partial-file cleanup. Spreadsheet formula
neutralization intentionally adds an apostrophe to dangerous text; importing an
export does not remove it or promise byte-for-byte text round trips.

The local snapshot contains only data the host already authorized for the
consumer. It adds no tenant/user permission system: do not share the registered
singleton across consumers with different access rights. Authorize/subset the
input before import and keep its lifetime within that authorized session.

### Authenticated read, display and export boundaries

`readPermission`, `exportPermission` and `importPermission` are independent,
explicit host mappings to existing permissions. No permission IDs or role grants
are installed by this module. The explicit repository registration accepts these
named arguments and passes them to both DI-created and navigation-created pages.
The default registration still creates only the deterministic synthetic pilot.
Without a read mapping, authenticated host/CSV snapshots are denied before their
first repository read. A valid read grant alone does not authorize export; missing
or denied export mapping is rejected before enumeration or file creation.

The host must supply its real `SensitiveOperationService`, backed by the same
user context and permission service as the authenticated session. The production
`RemoteAuthService` also supplies `IIdentitySessionEvents`; custom mutable host
contexts must publish that event for login, logout, account switch and claim
changes. Static contexts cannot provide immediate display invalidation.
The fallback service remains fail-closed in authenticated deployments.

Each protected snapshot is bound to the subject, session version, roles and
permission set captured when its page is constructed or a fresh import is
authorized. Reads check this binding and the current read grant before querying
and before publishing rows/groups/totals. Session events cancel the active
operation immediately and dispatch display invalidation to the UI thread.
Invalidation clears rows, group choices, filter inputs, applied scope, counts,
page metadata and prior status (including export paths). Restoring a grant or
logging back in cannot revive that old snapshot: recreate the page with an
authorized host snapshot or perform a fresh authorized import. An ordinary
refresh with unchanged identity/claims does not invalidate the snapshot.

Protected exports use the existing production confirmation and audit path, then
revalidate the captured session, read binding and export grant at execution.
After the writer has closed, the same checks run again around the final file
move. Revocation, cancellation or failure leaves no final CSV and removes the
partial file. A separate export denial preserves still-authorized display state.
The low-level CSV adapter/exporter remain host-authorized building blocks; their
direct callers must enforce their own session/source scope.

Offline operation remains local and audited as before. The known synthetic
repository remains usable without business permission mappings even when
authentication is enabled. Imported CSV and all other host repositories are
treated as business snapshots; a wrapper around synthetic data is not assumed
safe. No shared plugin registration was changed.

Regression coverage in `AdvancedTableCsvTests` now includes absent read/export
mappings, independent import/read grants, explicit navigation mapping, claim and
identity invalidation, noncooperative late reads, export revocation during
confirmation and after the last row, fresh-import recovery, and synthetic
availability. The current environment still has no `dotnet` executable; these
added C#/Avalonia cases require execution in an SDK-equipped environment.
For this boundary increment, `check_source_contracts.py` passed with zero errors
and `integration_checks.py` passed (498 bilingual keys, 440 referenced keys,
37 XML files). These checks do not establish C# compilation or desktop behavior.

Additional validation:

```sh
dotnet test /home/work/data/xxx/Plinth/tests/Plinth.App.Tests/Plinth.App.Tests.csproj --filter 'FullyQualifiedName~AdvancedTable'
```

`AdvancedTableCsvTests` covers strict parsing and UTF-8, quoted multilingual
multiline data, numeric/text/row/group/byte limits (including a non-seekable
stream), safe maximum sums, snapshot isolation, ordinal paging, totals/export
scope agreement, explicit versus default registration, cancellation, formula
neutralization and file cleanup. Additional view-model and Avalonia headless tests
exercise the actual import button, snapshot replacement/isolation, paging/export
after import, malformed/byte/row/group-limit failure retention, empty imports,
dismissal, read and picker cancellation, authorization/production rejection,
session revalidation, late detach/DataContext/close responses, handle disposal,
duplicate clicks and bilingual refresh.

For this increment, source-contract and integration static checks were actually
executed successfully. Integration reported 498 bilingual resource keys, 440
referenced keys and 37 XML files. The current environment still has no `dotnet`;
these executable C#/Avalonia tests have not been compiled or run here. Static
checks do not replace compilation, native-dialog interaction or Windows acceptance.
The Python gate regression suite also ran: 103 tests, 9 skipped; SQLite smoke:
14 passed. `git diff --check` passed. The release lock check was actually attempted
and remains blocked by the pre-existing missing
`samples/Plinth.EchoPlugin/packages.lock.json`; no lockfile was fabricated.
