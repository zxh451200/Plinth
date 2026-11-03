using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Services;

public sealed record DiagnosticOptions(LogFilter Filter, bool IncludeLogs = true, bool IncludeRuntime = true);
public sealed record DiagnosticFile(string Name, long Bytes, string Fields, string Redaction);
public sealed record DiagnosticPreview(Guid Id, IReadOnlyList<DiagnosticFile> Files,
    IReadOnlyList<string> Excluded, bool Truncated, int InvalidLines);

/// <summary>
/// Only exports sanitized structured metadata. No original logs, arbitrary files, settings, dumps,
/// messages, templates, exception text, paths or business properties enter the archive.
/// Preview freezes the exact bytes; confirmation applies to this snapshot, not a later file reread.
/// </summary>
public sealed class DiagnosticService(LogAccessService? access = null)
{
    private readonly LogAccessService _access = access ?? new LogAccessService();
    private readonly object _gate = new();
    private Guid _previewId;
    private Dictionary<string, byte[]>? _payload;
    private string _previewSubject = "";
    private bool _previewIncludesLogs;
    private static readonly HashSet<string> Levels = ["Verbose", "Debug", "Information", "Warning", "Error", "Fatal", "Critical"];

    public Task<DiagnosticPreview> PreviewAsync(string logDirectory, DiagnosticOptions options,
        CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        _access.Demand("diagnostics:export");
        var subject = _access.CurrentSubject;
        if (options.IncludeLogs && options.Filter.EventKind == LogEventKind.SecurityAudit)
            throw new UnauthorizedAccessException("security.audit_export_excluded");
        var payload = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        var files = new List<DiagnosticFile>();
        var result = options.IncludeLogs
            ? QueryLogs(logDirectory, options.Filter, cancellationToken)
            : new LogQueryResult([], 0, false);
        if (options.IncludeLogs)
        {
            // Canonical values only. Free text can contain business data that regex cannot identify.
            var lines = result.Entries.Select(e => JsonSerializer.Serialize(new
            {
                Time = e.Time.ToUniversalTime().ToString("O"),
                Level = Levels.Contains(e.Level) ? e.Level : "Information",
                TaskId = CanonicalId(e.TaskId),
                CorrelationId = CanonicalId(e.CorrelationId),
            }));
            payload["logs.ndjson"] = Encoding.UTF8.GetBytes(string.Join("\n", lines));
            files.Add(new("logs.ndjson", payload["logs.ndjson"].Length,
                "Time, Level, TaskId, CorrelationId", "metadata-only"));
        }
        if (options.IncludeRuntime)
        {
            payload["runtime.json"] = JsonSerializer.SerializeToUtf8Bytes(new
            {
                Runtime = Environment.Version.ToString(),
                Platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsLinux() ? "Linux" : "Other",
            });
            files.Add(new("runtime.json", payload["runtime.json"].Length, "Runtime, Platform", "metadata-only"));
        }
        if (payload.Count == 0) throw new InvalidOperationException("Select at least one category.");
        var id = Guid.NewGuid();
        DemandSnapshotAccess(subject, options.IncludeLogs);
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _previewId = id;
            _payload = payload;
            _previewSubject = subject;
            _previewIncludesLogs = options.IncludeLogs;
        }
        return new DiagnosticPreview(id, files.AsReadOnly(),
            Array.AsReadOnly(new[] { "database", "settings", "credentials", "attachments", "raw dumps",
                "message/template/stack/business fields", "non-GUID identifiers", "security audit events" }), result.Truncated, result.InvalidLines);
    }, cancellationToken);

    public async Task ExportAsync(Guid previewId, bool confirmed, string destination,
        CancellationToken cancellationToken = default)
    {
        if (!confirmed) throw new InvalidOperationException("Explicit preview confirmation is required.");
        Dictionary<string, byte[]> payload;
        string subject;
        bool includesLogs;
        lock (_gate)
        {
            if (_payload is null || previewId != _previewId)
                throw new InvalidOperationException("Preview expired; generate a new preview.");
            payload = _payload;
            subject = _previewSubject;
            includesLogs = _previewIncludesLogs;
        }
        DemandSnapshotAccess(subject, includesLogs);
        var temp = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                using var zip = new ZipArchive(output, ZipArchiveMode.Create, leaveOpen: true);
                foreach (var (name, bytes) in payload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    await using var entry = zip.CreateEntry(name).Open();
                    await entry.WriteAsync(bytes, cancellationToken);
                }
            }
            // Reopen the actual archive. Check paths, sizes, exact snapshot bytes and strict schema.
            await ScanArchiveAsync(temp, payload, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            DemandSnapshotAccess(subject, includesLogs);
            File.Move(temp, destination, overwrite: false);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private void DemandSnapshotAccess(string subject, bool includesLogs)
    {
        _access.Demand("diagnostics:export");
        if (includesLogs) _access.DemandRead(LogEventKind.Technical);
        if (subject != _access.CurrentSubject)
            throw new UnauthorizedAccessException("security.subject_changed");
    }

    private LogQueryResult QueryLogs(string directory, LogFilter filter, CancellationToken token)
    {
        if (filter.EventKind.HasValue) return _access.Query(directory, filter, 5000, token);
        return _access.Query(directory, filter with
        { EventKind = LogEventKind.Technical, IncludeTaskEvents = true }, 5000, token);
    }

    private static async Task ScanArchiveAsync(string path, Dictionary<string, byte[]> expected, CancellationToken token)
    {
        using var zip = ZipFile.OpenRead(path);
        if (zip.Entries.Count != expected.Count) throw new InvalidDataException("Unexpected archive entries.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in zip.Entries)
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(entry.FullName) || !expected.TryGetValue(entry.FullName, out var bytes) ||
                entry.Length != bytes.Length || entry.Length > 2 * 1024 * 1024)
                throw new InvalidDataException("Unexpected diagnostic content.");
            await using var stream = entry.Open();
            using var memory = new MemoryStream();
            await stream.CopyToAsync(memory, token);
            if (!memory.ToArray().AsSpan().SequenceEqual(bytes))
                throw new InvalidDataException("Diagnostic content changed.");
            var text = Encoding.UTF8.GetString(bytes);
            if (entry.FullName == "logs.ndjson")
            {
                foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (root.EnumerateObject().Count() != 4 ||
                        !DateTimeOffset.TryParse(root.GetProperty("Time").GetString(), out _) ||
                        !Levels.Contains(root.GetProperty("Level").GetString() ?? "") ||
                        !IsCanonicalId(root.GetProperty("TaskId").GetString()) ||
                        !IsCanonicalId(root.GetProperty("CorrelationId").GetString()))
                        throw new InvalidDataException("Diagnostic metadata failed scan.");
                }
            }
            else
            {
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.EnumerateObject().Count() != 2 ||
                    !Version.TryParse(root.GetProperty("Runtime").GetString(), out _) ||
                    root.GetProperty("Platform").GetString() is not ("Windows" or "Linux" or "Other"))
                    throw new InvalidDataException("Runtime metadata failed scan.");
            }
        }
    }

    private static string CanonicalId(string value) => Guid.TryParse(value, out var id) ? id.ToString("D") : "";
    private static bool IsCanonicalId(string? value) => value == "" ||
        (Guid.TryParseExact(value, "D", out var id) && id.ToString("D") == value);
}
