using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Plinth.App.Modules.AdvancedTable.Models;

namespace Plinth.App.Modules.AdvancedTable.Services;

/// <summary>
/// Explicit, bounded local import. The caller supplies an authorized stream;
/// this adapter does not open paths, fetch URLs or infer business measures.
/// A repository is published only after the entire snapshot passes validation.
/// </summary>
public sealed class AdvancedTableCsvRepository : IAdvancedTableRepository, IAdvancedTableEditableRepository
{
    public const int MaxBytes = 8 * 1024 * 1024;
    public const int MaxRows = 10000;
    public const int MaxGroups = 16;
    private readonly AdvancedTableRepository _snapshot;

    private AdvancedTableCsvRepository(IReadOnlyList<AdvancedTableRow> rows) =>
        _snapshot = new AdvancedTableRepository(rows);

    /// <summary>Reads UTF-8 (optional BOM), leaving the caller's stream open.</summary>
    public static async Task<AdvancedTableCsvRepository> ImportAsync(
        Stream source, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Read at most one byte beyond the limit, including on non-seekable streams.
            var read = await source.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, MaxBytes + 1L - bytes.Length)),
                cancellationToken);
            if (read == 0) break;
            if (bytes.Length + read > MaxBytes) throw Invalid("File exceeds 8 MiB.");
            bytes.Write(buffer, 0, read);
        }

        string text;
        try { text = new UTF8Encoding(false, true).GetString(bytes.GetBuffer(), 0, (int)bytes.Length); }
        catch (DecoderFallbackException) { throw Invalid("File must be valid UTF-8."); }
        if (text.StartsWith('\uFEFF')) text = text[1..];
        var rows = new List<AdvancedTableRow>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var groups = new HashSet<string>(StringComparer.Ordinal);
        var position = 0;
        var header = ReadRecord(text, ref position, cancellationToken);
        if (header is null || !header.SequenceEqual(new[] { "Id", "Group", "Label", "Quantity", "Amount" }))
            throw Invalid("Expected header Id,Group,Label,Quantity,Amount.");
        while (ReadRecord(text, ref position, cancellationToken) is { } fields)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (rows.Count == MaxRows) throw Invalid("At most 10000 data rows are allowed.");
            if (fields.Length != 5) throw Invalid("Each record must have exactly five fields.");
            ValidateText(fields[0], 128, "Id", allowNewlines: false);
            ValidateText(fields[1], 128, "Group", allowNewlines: false);
            ValidateText(fields[2], 1024, "Label", allowNewlines: true);
            if (!ids.Add(fields[0])) throw Invalid("Duplicate Id (ordinal comparison).");
            groups.Add(fields[1]);
            if (groups.Count > MaxGroups) throw Invalid("At most 16 groups are allowed.");
            // Conservative per-row bounds keep every possible filtered sum representable.
            if (!Regex.IsMatch(fields[3], @"\A-?[0-9]+\z")
                || !int.TryParse(fields[3], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var quantity)
                || quantity is < -100000 or > 100000)
                throw Invalid("Quantity must be an integer between -100000 and 100000.");
            if (!Regex.IsMatch(fields[4], @"\A-?[0-9]+(?:\.[0-9]{1,2})?\z")
                || !decimal.TryParse(fields[4], NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out var amount)
                || amount is < -1000000000000m or > 1000000000000m)
                throw Invalid("Amount must be within +/-1000000000000 with at most two decimal places.");
            rows.Add(new(fields[0], fields[1], fields[2], quantity, amount));
        }
        cancellationToken.ThrowIfCancellationRequested();
        return new AdvancedTableCsvRepository(rows);
    }

    public Task<AdvancedTablePage> ReadPageAsync(
        AdvancedTableQuery query, CancellationToken cancellationToken = default) =>
        _snapshot.ReadPageAsync(query, cancellationToken);

    public IAsyncEnumerable<AdvancedTableRow> ReadFilteredAsync(
        string? search, string? group, int batchSize, CancellationToken cancellationToken = default) =>
        _snapshot.ReadFilteredAsync(search, group, batchSize, cancellationToken);

    public Task UpdateAsync(AdvancedTableRow row, CancellationToken cancellationToken = default) =>
        _snapshot.UpdateAsync(row, cancellationToken);

    private static void ValidateText(string value, int limit, string name, bool allowNewlines)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > limit
            || value != value.Trim()
            || value.Any(c => char.IsControl(c) && !(allowNewlines && c is '\r' or '\n' or '\t')))
            throw Invalid($"{name} is empty, too long, padded or contains unsupported control characters.");
    }

    // Strict CSV: quoted commas/newlines/doubled quotes; LF or CRLF record endings.
    // No silent truncation, ignored columns, empty records or recovery from malformed quotes.
    private static string[]? ReadRecord(string text, ref int position, CancellationToken token)
    {
        if (position == text.Length) return null;
        var fields = new List<string>(5);
        while (true)
        {
            var value = new StringBuilder();
            var quoted = position < text.Length && text[position] == '"';
            if (quoted) position++;
            var closed = !quoted;
            while (position < text.Length)
            {
                token.ThrowIfCancellationRequested();
                var c = text[position];
                if (quoted)
                {
                    position++;
                    if (c == '"')
                    {
                        if (position < text.Length && text[position] == '"') position++;
                        else { closed = true; break; }
                    }
                }
                else
                {
                    if (c is ',' or '\r' or '\n') break;
                    if (c == '"') throw Invalid("Unexpected quote in unquoted field.");
                    position++;
                }
                value.Append(c);
                if (value.Length > 1024) throw Invalid("Field exceeds 1024 characters.");
            }
            if (!closed) throw Invalid("Unterminated quoted field.");
            fields.Add(value.ToString());
            if (fields.Count > 5) throw Invalid("Too many columns.");
            if (position == text.Length) return fields.ToArray();
            var separator = text[position++];
            if (separator == ',') continue;
            if (separator == '\n') return fields.ToArray();
            if (separator == '\r' && position < text.Length && text[position++] == '\n')
                return fields.ToArray();
            throw Invalid("Expected comma, LF or CRLF after field.");
        }
    }

    private static InvalidDataException Invalid(string message) => new($"Invalid advanced-table CSV: {message}");
}
