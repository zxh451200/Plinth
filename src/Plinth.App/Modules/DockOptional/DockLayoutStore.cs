using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Plinth.App.Modules.DockOptional;

/// <summary>
/// Persistence boundary for the optional Dock integration.
/// This type deliberately knows nothing about Dock's model or Avalonia controls.
/// </summary>
public sealed class DockLayoutStore
{
    private const int CurrentSchema = 1;
    private readonly string _path;

    public DockLayoutStore(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new ArgumentException("A layout path is required.", nameof(path));

        _path = path;
    }

    public async Task SaveAsync(string layout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(layout))
            throw new ArgumentException("A serialized layout is required.", nameof(layout));

        using var document = JsonDocument.Parse(layout);
        var envelope = new LayoutEnvelope(CurrentSchema, document.RootElement.Clone());
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        var directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        try
        {
            await File.WriteAllTextAsync(temporary, json, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, _path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public async Task<DockLayoutLoadResult> LoadAsync(
        string fallbackLayout, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(fallbackLayout))
            throw new ArgumentException("A fallback layout is required.", nameof(fallbackLayout));
        using var fallback = JsonDocument.Parse(fallbackLayout);
        cancellationToken.ThrowIfCancellationRequested();

        if (!File.Exists(_path))
            return new(fallbackLayout, true, "missing");

        try
        {
            var json = await File.ReadAllTextAsync(_path, cancellationToken);
            var envelope = JsonSerializer.Deserialize<LayoutEnvelope>(json, JsonOptions);
            if (envelope is null || envelope.Schema != CurrentSchema
                || envelope.Layout.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            {
                return new(fallbackLayout, true, "schema");
            }

            return new DockLayoutLoadResult(
                envelope.Layout.GetRawText(), UsedFallback: false, FallbackReason: null);
        }
        catch (JsonException)
        {
            return new(fallbackLayout, true, "invalid-json");
        }
        catch (IOException)
        {
            return new(fallbackLayout, true, "io");
        }
        catch (UnauthorizedAccessException)
        {
            return new(fallbackLayout, true, "access");
        }
    }

    private sealed record LayoutEnvelope(int Schema, JsonElement Layout);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    };
}

public sealed record DockLayoutLoadResult(
    string Layout,
    bool UsedFallback,
    string? FallbackReason);
