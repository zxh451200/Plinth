using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Serilog;

namespace Plinth.App.Services;

public sealed record WorkspaceLayoutV1
{
    public int Version { get; init; } = 1;
    public string[] OpenTabIds { get; init; } = [];
    public string? SelectedTabId { get; init; }
    public bool SidebarVisible { get; init; } = true;
    public double SidebarWidth { get; init; } = 260;
    public bool SidebarCompact { get; init; }
    public bool SidePanelVisible { get; init; }
    public double SidePanelWidth { get; init; } = 300;
    public int SidePanelTab { get; init; }
}

public sealed record DemoColumnPreference(bool Visible, double Width);

public sealed record PageNavigationPreferences
{
    public string[] FavoriteIds { get; init; } = [];
    public string[] RecentIds { get; init; } = [];
}

public sealed record DemoListPreferences
{
    public const int MaxSearchTextLength = 500;
    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        init => _searchText = (value ?? string.Empty) is var text && text.Length > MaxSearchTextLength
            ? text[..MaxSearchTextLength] : text;
    }
    public int PageSize { get; init; } = 50;
    public string SortField { get; init; } = "Code";
    public bool Descending { get; init; }
    public Dictionary<string, DemoColumnPreference> Columns { get; init; } = [];
}

/// <summary>独立的非秘密配置文件，不写 SettingsService。临时文件替换避免半写入。</summary>
public sealed class WorkspacePreferencesService
{
    private readonly string _path;
    private Preferences _current;
    private bool _saveBlocked;

    public WorkspacePreferencesService() : this(Path.Combine(AppPaths.DataDirectory, "workspace-preferences.json")) { }

    public WorkspacePreferencesService(string path)
    {
        _path = path;
        try
        {
            _current = new();
            if (File.Exists(path))
            {
                var json = File.ReadAllText(path);
                ValidateVersions(json);
                _current = JsonSerializer.Deserialize<Preferences>(json) ?? new();
            }
        }
        catch (NotSupportedException)
        {
            Log.Warning("Unsupported workspace version; preserving original and using defaults.");
            PreserveOriginal(".unsupported");
            _current = new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            Log.Warning("Workspace preferences could not be read; preserving original and using defaults.");
            PreserveOriginal(".bad");
            _current = new();
        }
    }

    public WorkspaceLayoutV1? Layout => _current.Layout is { Version: 1 } layout
        ? layout with
        {
            OpenTabIds = (layout.OpenTabIds ?? []).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToArray(),
            SidebarWidth = double.IsFinite(layout.SidebarWidth) ? Math.Clamp(layout.SidebarWidth, 180, 420) : 260,
            SidePanelWidth = NormalizeSidePanelWidth(layout.SidePanelWidth),
            SidePanelTab = layout.SidePanelTab == 1 ? 1 : 0,
        }
        : null;

    public DemoListPreferences Demo => _current.Demo ?? new();
    public const int MaxFavorites = 20;
    public const int MaxRecentPages = 10;
    public PageNavigationPreferences Navigation => NormalizeNavigation(_current.Navigation);

    public bool SaveLayout(WorkspaceLayoutV1 layout)
    {
        return Save(_current with { Layout = layout });
    }

    public bool SaveDemo(DemoListPreferences demo)
    {
        return Save(_current with { Demo = demo });
    }

    public bool SaveNavigation(PageNavigationPreferences navigation) =>
        Save(_current with { Navigation = NormalizeNavigation(navigation) });

    private bool Save(Preferences candidate)
    {
        if (_saveBlocked)
        {
            Log.Warning("Workspace save blocked because the original file could not be preserved.");
            return false;
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path))!);
            File.WriteAllText(_path + ".tmp", JsonSerializer.Serialize(candidate));
            File.Move(_path + ".tmp", _path, overwrite: true);
            _current = candidate;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning("Workspace preferences could not be saved.");
            return false;
        }
        finally
        {
            try { File.Delete(_path + ".tmp"); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warning("无法清理工作区临时文件");
            }
        }
    }

    private void PreserveOriginal(string suffix)
    {
        try
        {
            File.Move(_path, _path + suffix, overwrite: false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _saveBlocked = true;
            Log.Warning("Workspace original could not be preserved; saving is blocked.");
        }
    }

    private static void ValidateVersions(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Object) return;
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (property.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase)
                || property.Name.Equals("Version", StringComparison.OrdinalIgnoreCase))
                ValidateVersion(property.Value);
            if (property.Name.Equals("Layout", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind == JsonValueKind.Object)
            {
                foreach (var field in property.Value.EnumerateObject())
                    if (field.Name.Equals("Version", StringComparison.OrdinalIgnoreCase)
                        || field.Name.Equals("SchemaVersion", StringComparison.OrdinalIgnoreCase))
                        ValidateVersion(field.Value);
            }
        }
    }

    private static void ValidateVersion(JsonElement version)
    {
        if (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var value) || value != 1)
            throw new NotSupportedException("Unsupported workspace schema.");
    }

    public sealed record Preferences
    {
        public WorkspaceLayoutV1? Layout { get; init; }
        public DemoListPreferences? Demo { get; init; }
        public PageNavigationPreferences? Navigation { get; init; }
    }

    /// <summary>Backup contract: only known layout and list fields, never arbitrary JSON fields.</summary>
    public static string NormalizeForBackup(string json)
    {
        ValidateVersions(json);
        var preferences = JsonSerializer.Deserialize<Preferences>(json)
            ?? throw new InvalidDataException("Workspace preferences are empty.");
        var layout = preferences.Layout;
        if (layout is not null)
        {
            if (layout.Version != 1) throw new NotSupportedException("Unsupported workspace schema.");
            var tabs = layout.OpenTabIds ?? [];
            if (tabs.Length > 100 || tabs.Any(id => string.IsNullOrWhiteSpace(id) || id.Length > 128
                || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not '.' and not '_' and not '-')))
                throw new InvalidDataException("Invalid workspace page identifiers.");
            layout = layout with
            {
                OpenTabIds = tabs.Distinct().ToArray(),
                SelectedTabId = tabs.Contains(layout.SelectedTabId) ? layout.SelectedTabId : null,
                SidebarWidth = double.IsFinite(layout.SidebarWidth) ? Math.Clamp(layout.SidebarWidth, 180, 420) : 260,
                SidePanelWidth = NormalizeSidePanelWidth(layout.SidePanelWidth),
                SidePanelTab = layout.SidePanelTab == 1 ? 1 : 0,
            };
        }
        var demo = preferences.Demo;
        if (demo is not null)
        {
            string[] fields = ["Code", "Name", "Remark", "UpdatedAt", "UpdatedBy"];
            demo = demo with
            {
                SearchText = demo.SearchText,
                PageSize = demo.PageSize is 25 or 50 or 100 or 200 ? demo.PageSize : 50,
                SortField = fields.Contains(demo.SortField) ? demo.SortField : "Code",
                Columns = (demo.Columns ?? []).Where(c => fields.Contains(c.Key) && c.Value is not null)
                    .ToDictionary(c => c.Key, c => c.Value with
                    {
                        Width = double.IsFinite(c.Value.Width) ? Math.Clamp(c.Value.Width, 40, 2000) : 140,
                    }),
            };
        }
        return JsonSerializer.Serialize(new Preferences
        {
            Layout = layout,
            Demo = demo,
            Navigation = NormalizeNavigation(preferences.Navigation),
        });
    }

    private static PageNavigationPreferences NormalizeNavigation(PageNavigationPreferences? navigation) => new()
    {
        FavoriteIds = NormalizePageIds(navigation?.FavoriteIds, MaxFavorites),
        RecentIds = NormalizePageIds(navigation?.RecentIds, MaxRecentPages),
    };

    private static string[] NormalizePageIds(string[]? ids, int maximum) =>
        (ids ?? []).Where(id => !string.IsNullOrWhiteSpace(id) && id.Length <= 128
            && id.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-'))
            .Distinct(StringComparer.Ordinal).Take(maximum).ToArray();

    private static double NormalizeSidePanelWidth(double width) =>
        double.IsFinite(width) ? Math.Clamp(width, 240, 480) : 300;
}
