using System;
using System.IO;
using System.Linq;
using Plinth.App.Services;
using Xunit;

namespace Plinth.App.Tests;

public sealed class WorkspacePreferencesTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "plinth-layout-" + Guid.NewGuid().ToString("N"));
    private string ConfigPath => Path.Combine(_directory, "workspace.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void Layout_and_demo_preferences_roundtrip_independently()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.True(store.SaveLayout(new WorkspaceLayoutV1
        {
            OpenTabIds = ["demo", "settings"],
            SelectedTabId = "demo",
            SidebarWidth = 320,
            SidebarVisible = false,
            SidePanelVisible = true,
            SidePanelWidth = 360,
            SidePanelTab = 1,
        }));
        Assert.True(store.SaveDemo(new DemoListPreferences
        {
            PageSize = 100,
            SortField = "Name",
            Descending = true,
            Columns = new() { ["Remark"] = new(false, 180) },
        }));
        var loaded = new WorkspacePreferencesService(ConfigPath);
        Assert.Equal(new[] { "demo", "settings" }, loaded.Layout!.OpenTabIds);
        Assert.Equal("demo", loaded.Layout.SelectedTabId);
        Assert.Equal(320, loaded.Layout.SidebarWidth);
        Assert.False(loaded.Layout.SidebarVisible);
        Assert.True(loaded.Layout.SidePanelVisible);
        Assert.Equal(360, loaded.Layout.SidePanelWidth);
        Assert.Equal(1, loaded.Layout.SidePanelTab);
        Assert.Equal(100, loaded.Demo.PageSize);
        Assert.Equal("Name", loaded.Demo.SortField);
        Assert.True(loaded.Demo.Descending);
        Assert.Equal(new DemoColumnPreference(false, 180), loaded.Demo.Columns["Remark"]);
    }

    [Fact]
    public void Sidebar_compact_flag_roundtrips()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.True(store.SaveLayout(new WorkspaceLayoutV1 { SidebarCompact = true, SidebarWidth = 300 }));
        var loaded = new WorkspacePreferencesService(ConfigPath).Layout!;
        Assert.True(loaded.SidebarCompact);
        Assert.Equal(300, loaded.SidebarWidth);
    }

    [Fact]
    public void Legacy_layout_without_sidebar_compact_reads_expanded()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, """{"Layout":{"Version":1,"OpenTabIds":["demo"],"SidebarWidth":300}}""");
        var loaded = new WorkspacePreferencesService(ConfigPath).Layout!;
        Assert.False(loaded.SidebarCompact);
        Assert.Equal(300, loaded.SidebarWidth);
    }

    [Fact]
    public void Corrupt_and_unknown_version_layout_fall_back_safely()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{broken");
        Assert.Null(new WorkspacePreferencesService(ConfigPath).Layout);
        File.WriteAllText(ConfigPath, """{"Layout":{"Version":99}}""");
        Assert.Null(new WorkspacePreferencesService(ConfigPath).Layout);
    }

    [Theory]
    [InlineData("""{"Layout":{"Version":99,"Future":"keep"},"Demo":{"PageSize":200},"Navigation":{"FavoriteIds":["future"]}}""")]
    [InlineData("""{"SchemaVersion":99,"Layout":{"Version":1,"OpenTabIds":["future"]},"Demo":{"PageSize":200},"Future":"keep"}""")]
    [InlineData("""{"schemaVersion":99,"Layout":{"Version":1},"Future":"keep"}""")]
    [InlineData("""{"Version":99,"Layout":{"Version":1},"Future":"keep"}""")]
    public void Unsupported_versions_preserve_whole_file_and_use_defaults_across_saves(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, json);
        var original = File.ReadAllBytes(ConfigPath);
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.Null(store.Layout);
        Assert.Equal(50, store.Demo.PageSize);
        Assert.Empty(store.Navigation.FavoriteIds);
        Assert.Equal(original, File.ReadAllBytes(ConfigPath + ".unsupported"));

        Assert.True(store.SaveDemo(new DemoListPreferences { PageSize = 100 }));
        Assert.True(store.SaveLayout(new WorkspaceLayoutV1 { OpenTabIds = ["demo"] }));
        Assert.True(store.SaveNavigation(new PageNavigationPreferences { FavoriteIds = ["demo"] }));
        var reloaded = new WorkspacePreferencesService(ConfigPath);
        Assert.Equal(new[] { "demo" }, reloaded.Layout!.OpenTabIds);
        Assert.Equal(100, reloaded.Demo.PageSize);
        Assert.Equal(new[] { "demo" }, reloaded.Navigation.FavoriteIds);
        Assert.True(reloaded.SaveLayout(new WorkspaceLayoutV1()));
        Assert.Equal(original, File.ReadAllBytes(ConfigPath + ".unsupported"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Failed_preservation_blocks_all_saves_without_changing_originals(bool directory)
    {
        Directory.CreateDirectory(_directory);
        const string json = """{"Layout":{"Version":99},"Future":"keep"}""";
        File.WriteAllText(ConfigPath, json);
        var backup = ConfigPath + ".unsupported";
        if (directory) Directory.CreateDirectory(backup);
        else File.WriteAllText(backup, "earlier original");
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.False(store.SaveLayout(new WorkspaceLayoutV1()));
        Assert.False(store.SaveDemo(new DemoListPreferences { PageSize = 200 }));
        Assert.False(store.SaveNavigation(new PageNavigationPreferences { FavoriteIds = ["demo"] }));
        Assert.Null(store.Layout);
        Assert.Equal(50, store.Demo.PageSize);
        Assert.Empty(store.Navigation.FavoriteIds);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
        Assert.Null(new WorkspacePreferencesService(ConfigPath).Layout);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
        if (!directory) Assert.Equal("earlier original", File.ReadAllText(backup));
    }

    [Theory]
    [InlineData("""{"Layout":{"Version":1,"OpenTabIds":["demo"]},"Demo":{"PageSize":100}}""")]
    [InlineData("""{"SchemaVersion":1,"Layout":{"Version":1,"OpenTabIds":["demo"]},"Demo":{"PageSize":100}}""")]
    public void Supported_old_files_load_without_preservation_or_rewriting(string json)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, json);
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.Equal(new[] { "demo" }, store.Layout!.OpenTabIds);
        Assert.Equal(100, store.Demo.PageSize);
        Assert.Equal(json, File.ReadAllText(ConfigPath));
        Assert.False(File.Exists(ConfigPath + ".unsupported"));
        Assert.True(store.SaveNavigation(new PageNavigationPreferences { FavoriteIds = ["demo"] }));
        var reloaded = new WorkspacePreferencesService(ConfigPath);
        Assert.Equal(new[] { "demo" }, reloaded.Layout!.OpenTabIds);
        Assert.Equal(100, reloaded.Demo.PageSize);
    }

    [Fact]
    public void Layout_normalizes_width_and_duplicate_ids()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        store.SaveLayout(new WorkspaceLayoutV1
        {
            SidebarWidth = 9999,
            OpenTabIds = ["demo", "demo", ""],
            SidePanelWidth = -1,
            SidePanelTab = 99,
        });
        Assert.Equal(420, store.Layout!.SidebarWidth);
        Assert.Equal(new[] { "demo" }, store.Layout.OpenTabIds);
        Assert.Equal(240, store.Layout.SidePanelWidth);
        Assert.Equal(0, store.Layout.SidePanelTab);
    }

    [Theory]
    [InlineData("""{"SchemaVersion":99,"Layout":{"Version":1},"Future":"keep"}""")]
    [InlineData("""{"Version":99,"Layout":{"Version":1},"Future":"keep"}""")]
    [InlineData("""{"schemaVersion":99,"Future":"keep"}""")]
    public void Backup_normalization_rejects_unknown_top_level_versions(string json)
    {
        Assert.Throws<NotSupportedException>(() => WorkspacePreferencesService.NormalizeForBackup(json));
    }

    [Fact]
    public void Old_layout_defaults_and_backup_panel_fields_are_preserved()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, """{"Layout":{"Version":1}}""");
        var old = new WorkspacePreferencesService(ConfigPath).Layout!;
        Assert.False(old.SidePanelVisible);
        Assert.Equal(300, old.SidePanelWidth);
        var normalized = WorkspacePreferencesService.NormalizeForBackup(
            """{"Layout":{"Version":1,"SidePanelVisible":true,"SidePanelWidth":9999,"SidePanelTab":1},"Secret":"discard"}""");
        File.WriteAllText(ConfigPath, normalized);
        var restored = new WorkspacePreferencesService(ConfigPath).Layout!;
        Assert.True(restored.SidePanelVisible);
        Assert.Equal(480, restored.SidePanelWidth);
        Assert.Equal(1, restored.SidePanelTab);
        Assert.DoesNotContain("Secret", normalized);
    }

    [Fact]
    public void Unwritable_destination_reports_failure_without_throwing()
    {
        Directory.CreateDirectory(_directory);
        var store = new WorkspacePreferencesService(_directory);
        Assert.False(store.SaveLayout(new WorkspaceLayoutV1()));
    }

    [Fact]
    public void Navigation_is_bounded_deduplicated_and_preserved_by_other_preferences()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.True(store.SaveNavigation(new PageNavigationPreferences
        {
            FavoriteIds = ["demo.list", "demo.list", "../secret", "", "shell.settings"],
            RecentIds = Enumerable.Range(0, 30).Select(i => $"page.{i}").ToArray(),
        }));
        Assert.True(store.SaveLayout(new WorkspaceLayoutV1()));
        Assert.True(store.SaveDemo(new DemoListPreferences { PageSize = 100 }));
        var loaded = new WorkspacePreferencesService(ConfigPath);
        Assert.Equal(new[] { "demo.list", "shell.settings" }, loaded.Navigation.FavoriteIds);
        Assert.Equal(WorkspacePreferencesService.MaxRecentPages, loaded.Navigation.RecentIds.Length);
        Assert.Equal("page.0", loaded.Navigation.RecentIds[0]);
        Assert.Equal(100, loaded.Demo.PageSize);
    }

    [Fact]
    public void Failed_navigation_write_does_not_change_committed_memory_or_disk()
    {
        var store = new WorkspacePreferencesService(ConfigPath);
        Assert.True(store.SaveNavigation(new PageNavigationPreferences { FavoriteIds = ["first"] }));
        // Occupy the temporary file path to fail before the atomic replacement.
        Directory.CreateDirectory(ConfigPath + ".tmp");
        Assert.False(store.SaveNavigation(new PageNavigationPreferences { FavoriteIds = ["second"] }));
        Assert.Equal(new[] { "first" }, store.Navigation.FavoriteIds);
        Assert.Equal(new[] { "first" }, new WorkspacePreferencesService(ConfigPath).Navigation.FavoriteIds);
    }

    [Fact]
    public void Backup_keeps_only_known_navigation_fields_and_old_files_default_to_empty()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(ConfigPath, "{}");
        Assert.Empty(new WorkspacePreferencesService(ConfigPath).Navigation.FavoriteIds);
        var json = WorkspacePreferencesService.NormalizeForBackup(
            """{"Navigation":{"FavoriteIds":["demo.list","demo.list"],"RecentIds":null,"Token":"secret"}}""");
        Assert.DoesNotContain("secret", json);
        File.WriteAllText(ConfigPath, json);
        var loaded = new WorkspacePreferencesService(ConfigPath);
        Assert.Equal(new[] { "demo.list" }, loaded.Navigation.FavoriteIds);
        Assert.Empty(loaded.Navigation.RecentIds);
    }
}
