using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CsvHelper.Configuration;

namespace Plinth.App.Modules.Demo.Services;

public static class CsvWriter
{
    public static string Build(IReadOnlyList<string> header, IEnumerable<IReadOnlyList<string>> rows,
        CancellationToken cancellationToken = default, string delimiter = ",")
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        Write(output, header, rows, delimiter, cancellationToken);
        return output.ToString();
    }

    private static void Write(TextWriter output, IReadOnlyList<string> header,
        IEnumerable<IReadOnlyList<string>> rows, string delimiter, CancellationToken token)
    {
        CsvReader.ValidateDelimiter(delimiter);
        using var csv = new CsvHelper.CsvWriter(output, CreateConfiguration(delimiter), leaveOpen: true);
        Append(header);
        foreach (var row in rows) Append(row);
        void Append(IReadOnlyList<string> row)
        {
            token.ThrowIfCancellationRequested();
            foreach (var cell in row)
            {
                token.ThrowIfCancellationRequested();
                csv.WriteField(Protect(cell));
            }
            csv.NextRecord();
        }
    }

    public static async Task WriteFileAsync(string path, IReadOnlyList<string> header,
        IEnumerable<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default,
        string delimiter = ",")
    {
        await WriteFileWithHashAsync(path, header, rows, cancellationToken, delimiter).ConfigureAwait(false);
    }

    /// <summary>Hashes the serialized UTF-8 bytes, including the BOM, as they are written.</summary>
    public static async Task<string> WriteFileWithHashAsync(string path, IReadOnlyList<string> header,
        IEnumerable<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default,
        string delimiter = ",")
    {
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var sha256 = await Task.Run(() =>
            {
                using var hash = SHA256.Create();
                using var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                using var hashed = new CryptoStream(file, hash, CryptoStreamMode.Write, leaveOpen: true);
                using (var output = new StreamWriter(hashed, new UTF8Encoding(true), 1024, leaveOpen: true))
                    Write(output, header, rows, delimiter, cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                hashed.FlushFinalBlock();
                return Convert.ToHexString(hash.Hash!);
            }, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: false);
            return sha256;
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    public static async Task WriteFileAsync(string path, IReadOnlyList<string> header,
        IAsyncEnumerable<IReadOnlyList<string>> rows, CancellationToken cancellationToken = default,
        string delimiter = ",")
    {
        CsvReader.ValidateDelimiter(delimiter);
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new StreamWriter(temp, false, new UTF8Encoding(true)))
            using (var csv = new CsvHelper.CsvWriter(output, CreateConfiguration(delimiter), leaveOpen: true))
            {
                async Task Append(IReadOnlyList<string> row)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    foreach (var cell in row) csv.WriteField(Protect(cell));
                    await csv.NextRecordAsync();
                }
                await Append(header);
                await foreach (var row in rows.WithCancellation(cancellationToken)) await Append(row);
                await output.FlushAsync(cancellationToken);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temp, path, overwrite: false);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public static Task WriteErrorReportAsync(string path, IEnumerable<CsvIssue> issues,
        CancellationToken cancellationToken = default) =>
        WriteFileAsync(path, ["Row", "Code", "Message"],
            issues.Select(x => (IReadOnlyList<string>)[x.Row.ToString(CultureInfo.InvariantCulture), x.Code, x.Message]),
            cancellationToken);

    public static string Escape(string value)
    {
        using var output = new StringWriter(CultureInfo.InvariantCulture);
        using (var csv = new CsvHelper.CsvWriter(output, CreateConfiguration(","), leaveOpen: true))
            csv.WriteField(Protect(value));
        return output.ToString();
    }

    // CsvHelper 按 NewLine("\r\n") 判断是否需要引号，只含 "\n" 的字段会被原样写出并破坏行结构。
    private static CsvConfiguration CreateConfiguration(string delimiter) => new(CultureInfo.InvariantCulture)
    {
        Delimiter = delimiter,
        NewLine = "\r\n",
        ShouldQuote = args => args.Field.Contains('\r') || args.Field.Contains('\n')
            || args.Field.Contains('"') || args.Field.Contains(delimiter[0]),
    };

    private static string Protect(string value)
    {
        // Spreadsheet programs may trim leading whitespace before interpreting formulas.
        var trimmed = value.AsSpan().TrimStart();
        // Escape a literal leading apostrophe too, so import does not confuse it
        // with the prefix added here for formula protection.
        return (value.Length > 0 && value[0] is '\'' or '\t' or '\r' or '\n') ||
               (trimmed.Length > 0 && trimmed[0] is '=' or '+' or '-' or '@') ? "'" + value : value;
    }
}
