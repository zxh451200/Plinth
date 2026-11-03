using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper.Configuration;
using System.Security.Cryptography;

namespace Plinth.App.Modules.Demo.Services;

public sealed record CsvIssue(long Row, string Code, string Message);
public sealed record CsvReadResult(IReadOnlyList<IReadOnlyList<string>> Rows, IReadOnlyList<CsvIssue> Errors)
{
    public IReadOnlyList<long> RowNumbers { get; init; } = [];
}
public sealed record CsvEncodingPreview(string? Encoding, bool NeedsChoice, IReadOnlyList<string> Candidates,
    CsvReadResult? Content)
{
    public string Sha256 { get; init; } = "";
}

/// <summary>Bounded CsvHelper reader. No numeric coercion; identifiers retain all digits.</summary>
public static class CsvReader
{
    public const int MaxBytes = 5 * 1024 * 1024;
    public const int MaxRows = 5001;
    public const int MaxFieldLength = 32768;

    static CsvReader() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // Compatibility for existing DemoService; syntax errors are never silently accepted.
    public static List<List<string>> Parse(string text) => Parse(text, CancellationToken.None);

    public static List<List<string>> Parse(string text, CancellationToken cancellationToken)
    {
        if (text.Length > MaxBytes) throw new InvalidDataException("CSV text exceeds size limit.");
        using var reader = new StringReader(text.TrimStart('\uFEFF'));
        var result = Read(reader, cancellationToken: cancellationToken);
        if (result.Errors.Count > 0)
            throw new InvalidDataException($"CSV row {result.Errors[0].Row}: {result.Errors[0].Code}");
        return result.Rows.Select(x => x.ToList()).ToList();
    }

    /// <summary>Preview only; candidate selection is explicit for non-UTF8 input.</summary>
    public static async Task<CsvEncodingPreview> PreviewAsync(string path, string? encodingName = null,
        string delimiter = ",", CancellationToken cancellationToken = default, IProgress<double?>? progress = null)
    {
        await using var file = OpenFile(path, cancellationToken);
        var (sha256, prefix) = await HashFileAsync(file, cancellationToken, progress);
        var (encoding, bom) = DetectBom(prefix);
        if (encodingName is not null)
        {
            var chosen = StrictEncoding(encodingName);
            if (encoding is not null && chosen.CodePage != encoding.CodePage)
                throw new InvalidDataException("Selected encoding conflicts with BOM.");
            encoding = chosen;
        }
        var automaticUtf8 = encoding is null;
        encoding ??= new UTF8Encoding(false, true);
        CsvReadResult content;
        try
        {
            content = await ReadContentAsync(file, encoding, bom, delimiter, true, sha256,
                cancellationToken, progress);
        }
        catch (DecoderFallbackException) when (automaticUtf8)
        {
            await VerifyFileAsync(file, sha256, cancellationToken, progress);
            return new(null, true, ["utf-8", "gb18030", "gbk"], null) { Sha256 = sha256 };
        }
        // Even valid UTF8 can be mojibake. The caller must show the sample and allow switching.
        var selectedName = encoding.CodePage switch
        {
            936 => "gbk",
            54936 => "gb18030",
            1200 => "utf-16",
            1201 => "utf-16be",
            _ => "utf-8",
        };
        IReadOnlyList<string> candidates = bom > 0 ? [selectedName] : ["utf-8", "gb18030", "gbk"];
        return new(selectedName, encodingName is null && bom == 0, candidates, content)
        { Sha256 = sha256 };
    }

    public static async Task<CsvReadResult> ReadFileAsync(string path, string encodingName,
        string delimiter = ",", CancellationToken cancellationToken = default, string? expectedSha256 = null,
        IProgress<double?>? progress = null)
    {
        await using var file = OpenFile(path, cancellationToken);
        var (sha256, prefix) = await HashFileAsync(file, cancellationToken, progress);
        if (expectedSha256 is not null &&
            !string.Equals(sha256, expectedSha256, StringComparison.Ordinal))
            throw new InvalidDataException("CSV_CHANGED_AFTER_PREVIEW");
        var (bomEncoding, bom) = DetectBom(prefix);
        var encoding = StrictEncoding(encodingName);
        if (bomEncoding is not null && encoding.CodePage != bomEncoding.CodePage)
            throw new InvalidDataException("Selected encoding conflicts with BOM.");
        return await ReadContentAsync(file, encoding, bom, delimiter, false, sha256, cancellationToken, progress);
    }

    public static CsvReadResult Read(TextReader input, string delimiter = ",", int maxRows = MaxRows,
        bool preview = false, CancellationToken cancellationToken = default)
    {
        ValidateDelimiter(delimiter);
        if (maxRows is < 1 or > MaxRows) throw new ArgumentOutOfRangeException(nameof(maxRows));
        var rows = new List<IReadOnlyList<string>>();
        var errors = new List<CsvIssue>();
        var rowNumbers = new List<long>();
        long physicalRow = 1;
        bool bad = false;
        var configuration = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HasHeaderRecord = false,
            Delimiter = delimiter,
            IgnoreBlankLines = true,
            MaxFieldSize = MaxFieldLength,
            ExceptionMessagesContainRawData = false,
            BadDataFound = _ => bad = true,
        };
        using var parser = new CsvHelper.CsvParser(input, configuration, leaveOpen: true);
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                bad = false;
                physicalRow = parser.RawRow + 1;
                if (!parser.Read()) break;
                var record = parser.Record ?? [];
                var raw = parser.RawRecord.TrimStart('\r', '\n');
                var lineBreaks = 0;
                for (var i = 0; i < raw.Length; i++)
                {
                    if (raw[i] == '\n' || (raw[i] == '\r' && (i + 1 == raw.Length || raw[i + 1] != '\n')))
                        lineBreaks++;
                }
                physicalRow = parser.RawRow - lineBreaks +
                    (raw.EndsWith('\n') || raw.EndsWith('\r') ? 1 : 0);
                if (rows.Count == maxRows)
                {
                    if (!preview) errors.Add(new(physicalRow, "ROW_LIMIT", "CSV row limit exceeded."));
                    break;
                }
                if (bad) errors.Add(new(physicalRow, "SYNTAX", "Invalid CSV quoting."));
                // CsvHelper 的 MaxFieldSize 只在缓冲区增长到阈值时才抛；边界值仍可能漏过。
                if (record.Any(field => field.Length > MaxFieldLength))
                    errors.Add(new(physicalRow, "FIELD_LIMIT", "CSV field length limit exceeded."));
                if (record.Length > 256) errors.Add(new(physicalRow, "COLUMN_LIMIT", "Too many columns."));
                if (rows.Count > 0 && record.Length != rows[0].Count)
                    errors.Add(new(physicalRow, "COLUMN_COUNT", "Column count differs from header."));
                rows.Add(record.Select(Unprotect).ToArray());
                rowNumbers.Add(physicalRow);
                if (preview && rows.Count == maxRows) break;
                if (errors.Count >= 100) break; // whole result is invalid; never import the remaining prefix
            }
        }
        catch (CsvHelper.CsvHelperException)
        {
            errors.Add(new(physicalRow, "PARSE_FAILED", "CSV syntax or field size is invalid."));
        }
        return new(rows.AsReadOnly(), errors.AsReadOnly()) { RowNumbers = rowNumbers.AsReadOnly() };
    }

    /// <summary>Explicit source-column mapping; null means text, other types only validate, never coerce.</summary>
    public static CsvReadResult MapAndValidate(CsvReadResult source, IReadOnlyList<int> columns,
        IReadOnlyList<Type?> types, CancellationToken cancellationToken = default)
    {
        if (columns.Count != types.Count || columns.Any(i => i < 0))
            throw new ArgumentException("Invalid column mapping.");
        var errors = source.Errors.ToList();
        var mapped = new List<IReadOnlyList<string>>();
        for (var row = 0; row < source.Rows.Count; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var physicalRow = row < source.RowNumbers.Count ? source.RowNumbers[row] : row + 1;
            var values = columns.Select(i => i < source.Rows[row].Count ? source.Rows[row][i] : "").ToArray();
            mapped.Add(values);
            if (row == 0) continue;
            for (var col = 0; col < columns.Count; col++)
            {
                if (columns[col] >= source.Rows[row].Count)
                    errors.Add(new(physicalRow, "MISSING_COLUMN", $"Missing column {columns[col] + 1}."));
                else if (!ValidType(values[col], types[col]))
                    errors.Add(new(physicalRow, "TYPE", $"Invalid value type in column {columns[col] + 1}."));
            }
        }
        return new(mapped.AsReadOnly(), errors.AsReadOnly()) { RowNumbers = source.RowNumbers };
    }

    private static bool ValidType(string value, Type? type) =>
        type is null || type == typeof(string) ||
        (type == typeof(decimal) && decimal.TryParse(value, NumberStyles.Number, CultureInfo.InvariantCulture, out _)) ||
        (type == typeof(long) && long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _)) ||
        (type == typeof(DateTimeOffset) && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out _));

    private static string Unprotect(string value)
    {
        if (value.Length <= 1 || value[0] != '\'') return value;
        var tail = value.AsSpan(1);
        var trimmed = tail.TrimStart();
        return tail[0] is '\'' or '\t' or '\r' or '\n' ||
               (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') ? value[1..] : value;
    }

    internal static void ValidateDelimiter(string value)
    {
        if (value is not ("," or ";" or "\t" or "|"))
            throw new ArgumentException("Unsupported delimiter.", nameof(value));
    }

    private static Encoding StrictEncoding(string name) => name.ToLowerInvariant() switch
    {
        "utf-8" => new UTF8Encoding(false, true),
        "utf-16" => new UnicodeEncoding(false, false, true),
        "utf-16be" => new UnicodeEncoding(true, false, true),
        "gb18030" or "gbk" => Encoding.GetEncoding(name, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback),
        _ => throw new ArgumentException("Unsupported encoding.", nameof(name)),
    };

    private static (Encoding? Encoding, int Bom) DetectBom(byte[] bytes)
    {
        if (bytes.AsSpan().StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(false, true), 3);
        // Reject UTF32 rather than mistake its prefix for UTF16.
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 }) ||
            bytes.AsSpan().StartsWith(new byte[] { 0, 0, 0xFE, 0xFF }))
            throw new InvalidDataException("UTF32 CSV is not supported.");
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, false, true), 2);
        if (bytes.AsSpan().StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, false, true), 2);
        return (null, 0);
    }

    private static FileStream OpenFile(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
    }

    // Each pass reports its actual bytes / opening length; progress restarts for a new pass.
    private static async Task<(string Sha256, byte[] Prefix)> HashFileAsync(FileStream file,
        CancellationToken token, IProgress<double?>? progress)
    {
        file.Position = 0;
        using var input = new CheckedReadStream(file, token, progress);
        var block = new byte[8192];
        var prefix = new byte[4];
        var prefixLength = 0;
        int read;
        while ((read = await input.ReadAsync(block.AsMemory(), token)) > 0)
        {
            var take = Math.Min(read, prefix.Length - prefixLength);
            block.AsSpan(0, take).CopyTo(prefix.AsSpan(prefixLength));
            prefixLength += take;
        }
        return (input.FinishHash(), prefix[..prefixLength]);
    }

    private static async Task VerifyFileAsync(FileStream file, string expected, CancellationToken token,
        IProgress<double?>? progress)
    {
        var (actual, _) = await HashFileAsync(file, token, progress);
        if (!string.Equals(actual, expected, StringComparison.Ordinal))
            throw new InvalidDataException("CSV_CHANGED_AFTER_PREVIEW");
    }

    private static async Task<CsvReadResult> ReadContentAsync(FileStream file, Encoding encoding, int bom,
        string delimiter, bool preview, string expected, CancellationToken token, IProgress<double?>? progress)
    {
        file.Position = 0;
        using var input = new CheckedReadStream(file, token, progress);
        // Hash the BOM too. Encodings have no preamble so StreamReader cannot strip a second BOM,
        // even with detection disabled; nor may it auto-detect a permissive replacement encoding.
        for (var i = 0; i < bom; i++)
            if (input.ReadByte() < 0) throw new InvalidDataException("CSV_CHANGED_AFTER_PREVIEW");
        using var reader = new StreamReader(input, encoding, detectEncodingFromByteOrderMarks: false,
            bufferSize: 4096, leaveOpen: true);
        var result = Read(reader, delimiter, preview ? 21 : MaxRows, preview, token);
        // Preview/row/error limits bound retained records, not strict decoding or the hash.
        var chars = new char[4096];
        while (await reader.ReadAsync(chars.AsMemory(), token) > 0) { }
        token.ThrowIfCancellationRequested();
        if (!string.Equals(input.FinishHash(), expected, StringComparison.Ordinal))
            throw new InvalidDataException("CSV_CHANGED_AFTER_PREVIEW");
        // FileShare.Read denies cooperative writers. Rehash the same handle as well for Linux
        // writers that ignore sharing, including changes to bytes StreamReader already buffered.
        await VerifyFileAsync(file, expected, token, progress);
        token.ThrowIfCancellationRequested();
        return result;
    }

    /// <summary>One bounded read pass over the open file; owns only its incremental hash.</summary>
    private sealed class CheckedReadStream : Stream
    {
        private readonly FileStream _file;
        private readonly CancellationToken _token;
        private readonly IProgress<double?>? _progress;
        private readonly IncrementalHash _hash;
        private readonly long _length;
        private long _bytes;

        public CheckedReadStream(FileStream file, CancellationToken token, IProgress<double?>? progress)
        {
            _file = file;
            _token = token;
            _progress = progress;
            _length = file.Length;
            Check();
            _progress?.Report(_length == 0 ? null : 0);
            _token.ThrowIfCancellationRequested();
            _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        }

        private void Check()
        {
            _token.ThrowIfCancellationRequested();
            if (_file.Length > MaxBytes || _bytes > MaxBytes)
                throw new InvalidDataException("CSV file exceeds size limit.");
            if (_file.Length != _length)
                throw new InvalidDataException("CSV_CHANGED_AFTER_PREVIEW");
        }

        private int Record(ReadOnlySpan<byte> bytes)
        {
            _bytes += bytes.Length;
            Check();
            _hash.AppendData(bytes);
            if (_length > 0) _progress?.Report((double)_bytes / _length);
            _token.ThrowIfCancellationRequested();
            return bytes.Length;
        }

        public string FinishHash()
        {
            Check();
            if (_bytes != _length) throw new InvalidDataException("CSV_CHANGED_AFTER_PREVIEW");
            return Convert.ToHexString(_hash.GetHashAndReset());
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
        public override int Read(Span<byte> buffer)
        {
            Check();
            var read = _file.Read(buffer);
            return Record(buffer[..read]);
        }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Check();
            cancellationToken.ThrowIfCancellationRequested();
            var read = await _file.ReadAsync(buffer, _token);
            return Record(buffer.Span[..read]);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) _hash.Dispose();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _bytes; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
