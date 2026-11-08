using System.Globalization;
using System.Text;
using Plinth.App.Modules.AdvancedTable.Models;

namespace Plinth.App.Modules.AdvancedTable.Services;

public static class AdvancedTableCsvExporter
{
    public static async Task<string> ExportAsync(IAdvancedTableRepository repository,
        string? search, string? group, string directory, CancellationToken cancellationToken = default,
        Func<Action, Task>? publishAsync = null)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"advanced-table-{Guid.NewGuid():N}.csv");
        var temporary = path + ".partial";
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write,
                FileShare.None, 4096, useAsync: true))
            await using (var writer = new StreamWriter(stream, new UTF8Encoding(true)))
                await WriteAsync(repository, search, group, writer, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            void Publish()
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Move(temporary, path);
            }
            if (publishAsync is null) Publish();
            else await publishAsync(Publish);
            return path;
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    internal static async Task WriteAsync(IAdvancedTableRepository repository,
        string? search, string? group, TextWriter writer, CancellationToken token)
    {
        await writer.WriteLineAsync("Id,Group,Label,Quantity,Amount".AsMemory(), token);
        await foreach (var row in repository.ReadFilteredAsync(search, group, 50, token))
        {
            token.ThrowIfCancellationRequested();
            var line = string.Join(",", Csv(row.Id), Csv(row.Group), Csv(row.Label),
                row.Quantity.ToString(CultureInfo.InvariantCulture),
                row.Amount.ToString("F2", CultureInfo.InvariantCulture));
            await writer.WriteLineAsync(line.AsMemory(), token);
        }
    }

    // Quoting alone does not stop spreadsheet formula execution.
    private static string Csv(string value)
    {
        var first = value.TrimStart();
        if (first.Length > 0 && (first[0] is '=' or '+' or '-' or '@'
            || value[0] is '\t' or '\r' or '\n')) value = "'" + value;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }
}
