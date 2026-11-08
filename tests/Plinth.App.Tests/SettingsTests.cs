using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Headless.XUnit;
using Microsoft.Extensions.Logging.Abstractions;
using Plinth.App.Localization;
using Plinth.App.Services;
using Plinth.App.Security;
using Plinth.App.ViewModels;
using Xunit;

namespace Plinth.App.Tests;

/// <summary>
/// 设置文件的读写(§3.4):文件坏了、被手改时,程序要平稳地用默认值,不能启动失败,也不能丢掉用户的原文件。
/// 测试共用同一个临时数据目录,所以每个测试前后都清掉 settings.json。
/// </summary>
[Collection("Localization")]
public sealed class SettingsTests : IDisposable
{
    private static string SettingsPath => Path.Combine(AppPaths.DataDirectory, "settings.json");

    public SettingsTests() => Clean();

    public void Dispose() => Clean();

    private static void Clean()
    {
        Directory.CreateDirectory(AppPaths.DataDirectory);
        foreach (var path in new[] { SettingsPath, SettingsPath + ".bad", SettingsPath + ".unsupported", SettingsPath + ".tmp" })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static SettingsService Create() => new(NullLogger<SettingsService>.Instance);

    [Fact]
    public void Without_a_file_the_defaults_are_used()
    {
        Assert.Equal(SettingsService.ThemeSystem, Create().Current.Theme);
    }

    [Fact]
    public void Settings_are_saved_and_read_back()
    {
        var first = Create();
        first.Current.Theme = SettingsService.ThemeDark;
        first.Save();

        Assert.Equal(SettingsService.ThemeDark, Create().Current.Theme);
        Assert.False(File.Exists(SettingsPath + ".tmp"), "保存完不该留下临时文件");
    }

    [Fact]
    public void A_corrupt_file_is_kept_as_bad_and_the_defaults_are_used()
    {
        File.WriteAllText(SettingsPath, "{ this is not json");

        var service = Create();

        Assert.Equal(SettingsService.ThemeSystem, service.Current.Theme);
        Assert.True(File.Exists(SettingsPath + ".bad"), "损坏的原文件应该被改名保留,而不是被删掉");
        service.Save();
        Assert.Equal("{ this is not json", File.ReadAllText(SettingsPath + ".bad"));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task Unsupported_schema_uses_defaults_and_preserves_original_across_saves(int version)
    {
        var json = $$$"""{"SchemaVersion":{{{version}}},"Theme":"Dark","Language":"en-US","LogRetentionDays":90,"Future":{"Value":"keep"}}""";
        File.WriteAllText(SettingsPath, json);
        var original = File.ReadAllBytes(SettingsPath);

        Assert.Throws<NotSupportedException>(() => SettingsService.Upgrade(new AppSettings { SchemaVersion = version }));
        Assert.Null(SettingsService.ReadLanguage());
        Assert.Equal((30, 10), SettingsService.ReadLogRetention());
        Assert.Equal(original, File.ReadAllBytes(SettingsPath));
        var service = Create();
        Assert.Equal(2, service.Current.SchemaVersion);
        Assert.Equal(SettingsService.ThemeSystem, service.Current.Theme);
        Assert.Equal(Plinth.App.Localization.Loc.DefaultLanguage, service.Current.Language);
        Assert.Equal(30, service.Current.LogRetentionDays);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath + ".unsupported"));

        service.Save();
        await service.ChangeAsync(nameof(AppSettings.Theme), SettingsService.ThemeLight);
        Create().Save();
        Assert.Equal(SettingsService.ThemeLight, Create().Current.Theme);
        Assert.Equal(original, File.ReadAllBytes(SettingsPath + ".unsupported"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Failed_preservation_blocks_both_save_paths_and_keeps_both_originals(bool directory)
    {
        const string json = """{"SchemaVersion":3,"Future":"keep"}""";
        File.WriteAllText(SettingsPath, json);
        var backup = SettingsPath + ".unsupported";
        if (directory) Directory.CreateDirectory(backup);
        else File.WriteAllText(backup, "earlier original");
        try
        {
            var service = Create();
            Assert.Equal(SettingsService.ThemeSystem, service.Current.Theme);
            Assert.Throws<IOException>(() => service.Save());
            await Assert.ThrowsAsync<IOException>(() =>
                service.ChangeAsync(nameof(AppSettings.Theme), SettingsService.ThemeDark));
            Assert.Equal(SettingsService.ThemeSystem, service.Current.Theme);
            Assert.Equal(json, File.ReadAllText(SettingsPath));
            Assert.Equal(SettingsService.ThemeSystem, Create().Current.Theme);
            Assert.Equal(json, File.ReadAllText(SettingsPath));
            if (!directory) Assert.Equal("earlier original", File.ReadAllText(backup));
        }
        finally
        {
            if (directory) Directory.Delete(backup);
        }
    }

    [Fact]
    public void Schema_one_migrates_without_quarantining_or_rewriting_on_load()
    {
        const string json = """{"SchemaVersion":1,"Theme":"Dark","Language":"en-US","LogRetentionDays":90}""";
        File.WriteAllText(SettingsPath, json);
        var service = Create();
        Assert.Equal(2, service.Current.SchemaVersion);
        Assert.Equal(SettingsService.ThemeDark, service.Current.Theme);
        Assert.Equal("en-US", SettingsService.ReadLanguage());
        Assert.Equal((30, 10), SettingsService.ReadLogRetention());
        Assert.Equal(json, File.ReadAllText(SettingsPath));
        Assert.False(File.Exists(SettingsPath + ".unsupported"));
        service.Save();
        Assert.Equal(SettingsService.ThemeDark, Create().Current.Theme);
        Assert.Equal(30, Create().Current.LogRetentionDays);
    }

    [Fact]
    public void An_unknown_theme_falls_back_to_the_default()
    {
        File.WriteAllText(SettingsPath, """{"SchemaVersion":1,"Theme":"Neon"}""");

        Assert.Equal(SettingsService.ThemeSystem, Create().Current.Theme);
    }

    [AvaloniaFact]
    public async Task Choosing_a_theme_on_the_settings_page_saves_it()
    {
        var service = Create();
        var page = new SettingsViewModel(service);
        Assert.Equal(SettingsService.ThemeSystem, page.SelectedTheme.Key);

        page.SelectedTheme = page.Themes.Single(option => option.Key == SettingsService.ThemeDark);
        await page.PendingChange;

        Assert.Equal(SettingsService.ThemeDark, service.Current.Theme);
        Assert.Equal(SettingsService.ThemeDark, Create().Current.Theme);
        Assert.Equal("已保存", page.StatusText);
    }

    [Fact]
    public void Region_options_list_each_region_once_per_language()
    {
        var page = new SettingsViewModel(Create());
        var duplicates = page.Regions
            .Where(option => option.Key.Length > 0)
            .GroupBy(option => RegionIdentity(option.Key), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => string.Join(" / ", group.Select(option => option.Key)))
            .ToArray();

        Assert.Empty(duplicates);
        // 中文按地区各留一条(中国、台湾、香港、澳门、新加坡),不再出现简繁两份。
        var chinese = page.Regions.Where(option => option.Key.StartsWith("zh-", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(chinese.Length, chinese.Select(option => RegionIdentity(option.Key)).Distinct().Count());
    }

    [Fact]
    public void Region_titles_follow_the_interface_language()
    {
        Loc.Initialize("zh-CN");
        try
        {
            var anguilla = new SettingsViewModel(Create()).Regions.Single(option => option.Key == "en-AI");
            Assert.Contains("英语", anguilla.Title, StringComparison.Ordinal);
            Assert.DoesNotContain("English (Anguilla)", anguilla.Title, StringComparison.Ordinal);
        }
        finally
        {
            Loc.Initialize(Loc.DefaultLanguage);
        }
    }

    private static string RegionIdentity(string name)
    {
        var parts = name.Split('-');
        return parts.Length < 2 ? name : $"{parts[0]}-{parts[^1]}";
    }

    [AvaloniaFact]
    public void The_settings_page_starts_on_the_saved_theme()
    {
        File.WriteAllText(SettingsPath, """{"SchemaVersion":1,"Theme":"Light"}""");

        var page = new SettingsViewModel(Create());

        Assert.Equal(SettingsService.ThemeLight, page.SelectedTheme.Key);
    }

    [AvaloniaFact]
    public async Task Closing_waits_for_all_changes_and_sync_save_does_not_block()
    {
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var enteredSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        var service = CreateWithConfirmation(() =>
        {
            if (++confirmations == 1) return first.Task;
            enteredSecond.TrySetResult(true);
            return second.Task;
        });
        var page = new SettingsViewModel(service);
        page.RemoteEnabled = true;
        page.RemoteBaseUrl = "https://example.com/";
        var closing = page.TryLeaveAsync();
        try
        {
            Assert.True(page.IsLeaving);
            Assert.False(closing.IsCompleted);
            Assert.Throws<InvalidOperationException>(() => service.Save());
            first.SetResult(true);
            await enteredSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(closing.IsCompleted);
            second.SetResult(true);
            Assert.True(await closing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(page.IsSaving);
            Assert.False(page.IsLeaving);
            Assert.True(service.Current.Remote.Enabled);
            Assert.Equal("https://example.com/", Create().Current.Remote.BaseUrl);
            service.Save();
        }
        finally
        {
            first.TrySetResult(true);
            second.TrySetResult(true);
            await page.PendingChange;
        }
    }

    [AvaloniaFact]
    public async Task Failed_save_blocks_close_once_and_restores_committed_values()
    {
        var confirmation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = CreateWithConfirmation(() => confirmation.Task);
        var page = new SettingsViewModel(service);
        page.RemoteEnabled = true;
        var closing = page.TryLeaveAsync();
        // A directory at the target file path forces the real atomic replacement to fail.
        Directory.CreateDirectory(SettingsPath);
        try
        {
            confirmation.SetResult(true);
            Assert.False(await closing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.False(service.Current.Remote.Enabled);
            Assert.False(page.RemoteEnabled);
            Assert.False(page.IsLeaving);
            Assert.Equal(Plinth.App.Localization.Loc.Get("settings.saveFailed"), page.StatusText);
            Assert.True(await page.TryLeaveAsync());
        }
        finally
        {
            confirmation.TrySetResult(true);
            await page.PendingChange;
            Directory.Delete(SettingsPath);
        }
    }

    private static SettingsService CreateWithConfirmation(Func<Task<bool>> confirm)
    {
        var audit = new TestAudit();
        var users = new OfflineUserContext();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit),
            audit, authenticationEnabled: false, production: true)
        {
            ConfirmProductionAsync = confirm,
        };
        return new SettingsService(NullLogger<SettingsService>.Instance, operations);
    }

    [Theory]
    [InlineData("logout")]
    [InlineData("switch")]
    [InlineData("session")]
    [InlineData("revoke")]
    [InlineData("decline")]
    [InlineData("cancel")]
    [InlineData("stable")]
    public async Task Management_changes_recheck_session_and_permission_before_persisting(string change)
    {
        const string original = """{"SchemaVersion":2,"LogRetentionDays":30}""";
        File.WriteAllText(SettingsPath, original);
        var users = new SettingsUsers();
        var audit = new TestAudit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit), audit, true, true);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmations = 0;
        operations.ConfirmProductionAsync = () =>
        {
            confirmations++;
            entered.TrySetResult(true);
            return release.Task;
        };
        var service = new SettingsService(NullLogger<SettingsService>.Instance, operations);
        using var cancellation = new System.Threading.CancellationTokenSource();
        var pending = service.ChangeAsync(nameof(AppSettings.LogRetentionDays), 90, cancellation.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(pending.IsCompleted);
            switch (change)
            {
                case "logout": users.CurrentUser = RemoteUserContext.Anonymous; break;
                case "switch": users.CurrentUser = users.CurrentUser with { Subject = "bob" }; break;
                case "session": users.SessionVersion++; break;
                case "revoke": users.CurrentUser = users.CurrentUser with { Permissions = ImmutableHashSet<string>.Empty }; break;
                case "cancel": cancellation.Cancel(); break;
            }
        }
        finally { release.TrySetResult(change != "decline"); }
        if (change == "stable")
        {
            await pending;
            Assert.Equal(90, Create().Current.LogRetentionDays);
        }
        else
        {
            if (change is "decline" or "cancel")
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
            else
                await Assert.ThrowsAsync<UnauthorizedAccessException>(() => pending);
            Assert.Equal(30, service.Current.LogRetentionDays);
            Assert.Equal(original, File.ReadAllText(SettingsPath));
        }
        Assert.Equal(1, confirmations);
        Assert.Empty(Directory.GetFiles(AppPaths.DataDirectory, "settings.json.*.tmp"));
    }

    [AvaloniaTheory]
    [InlineData(nameof(AppSettings.Theme), SettingsService.ThemeDark)]
    [InlineData(nameof(AppSettings.Language), "en-US")]
    [InlineData(nameof(AppSettings.Region), "en-US")]
    [InlineData(nameof(AppSettings.TimeZoneId), "UTC")]
    [InlineData(nameof(AppSettings.Density), DensityService.Compact)]
    public async Task Appearance_changes_in_production_do_not_confirm(string field, string value)
    {
        var users = new SettingsUsers
        {
            CurrentUser = new("alice", ImmutableHashSet<string>.Empty, ImmutableHashSet.Create("settings:appearance")),
        };
        var audit = new TestAudit();
        var operations = new SensitiveOperationService(
            new AuthorizedOperation(new PermissionService(users, audit), users, audit), audit, true, true)
        {
            ConfirmProductionAsync = () => throw new InvalidOperationException("Appearance must not prompt."),
        };
        var service = new SettingsService(NullLogger<SettingsService>.Instance, operations);
        try
        {
            await service.ChangeAsync(field, value);
            var stored = Create().Current;
            Assert.Equal(value, field switch
            {
                nameof(AppSettings.Theme) => stored.Theme,
                nameof(AppSettings.Language) => stored.Language,
                nameof(AppSettings.Region) => stored.Region,
                nameof(AppSettings.TimeZoneId) => stored.TimeZoneId,
                _ => stored.Density,
            });
        }
        finally
        {
            DensityService.Apply(DensityService.Standard);
            Avalonia.Application.Current!.RequestedThemeVariant = Avalonia.Styling.ThemeVariant.Default;
        }
    }

    private sealed class SettingsUsers : IRemoteUserContext
    {
        public RemoteUserContext CurrentUser { get; set; } = new("alice", ImmutableHashSet<string>.Empty,
            ImmutableHashSet.Create("settings:manage"));
        public long SessionVersion { get; set; } = 1;
    }

    [AvaloniaFact]
    public async Task Regional_choices_persist_independently_of_language_and_are_restored()
    {
        var service = Create();
        var language = service.Current.Language;
        var page = new SettingsViewModel(service);
        page.SelectedRegion = page.Regions.First(option => option.Key == "en-US");
        await page.PendingChange;
        page.SelectedTimeZone = page.TimeZones.First(option => option.Key == TimeZoneInfo.Utc.Id);
        await page.PendingChange;
        Assert.Equal(language, service.Current.Language);
        var reloaded = Create();
        Assert.Equal("en-US", reloaded.Current.Region);
        Assert.Equal(TimeZoneInfo.Utc.Id, reloaded.Current.TimeZoneId);
        var reopened = new SettingsViewModel(reloaded);
        Assert.Equal("en-US", reopened.SelectedRegion.Key);
        Assert.Equal(TimeZoneInfo.Utc.Id, reopened.SelectedTimeZone.Key);
        var format = new DisplayFormat(reloaded.Current.Region, reloaded.Current.TimeZoneId);
        Assert.EndsWith("+00:00", format.FormatInstant(DateTimeOffset.Parse("2026-01-01T12:00:00Z")));
        Assert.Equal("1,234.50", format.FormatNumber(1234.5m));
    }

    [Fact]
    public async Task Invalid_format_edits_do_not_replace_committed_settings()
    {
        var service = Create();
        await service.ChangeAsync(nameof(AppSettings.Region), "en-US");
        await Assert.ThrowsAnyAsync<ArgumentException>(() =>
            service.ChangeAsync(nameof(AppSettings.Region), "en"));
        await Assert.ThrowsAsync<TimeZoneNotFoundException>(() =>
            service.ChangeAsync(nameof(AppSettings.TimeZoneId), "Plinth/DoesNotExist"));
        Assert.Equal("en-US", Create().Current.Region);
        Assert.Equal(string.Empty, Create().Current.TimeZoneId);
    }

    private sealed class TestAudit : ISecurityAudit
    {
        public void Write(AuditEvent entry) { }
    }

    [AvaloniaFact]
    public async Task Density_changes_existing_controls_and_survives_reopening()
    {
        var service = Create();
        var page = new SettingsViewModel(service);
        var button = new Avalonia.Controls.Button { Content = "Density" };
        var window = new Avalonia.Controls.Window { Content = button };
        window.Show();
        try
        {
            page.SelectedDensity = page.Densities.Single(x => x.Key == DensityService.Compact);
            await page.PendingChange;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var compactHeight = button.MinHeight;
            page.SelectedDensity = page.Densities.Single(x => x.Key == DensityService.Comfortable);
            await page.PendingChange;
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            Assert.True(button.MinHeight > compactHeight);
            Assert.Equal(DensityService.Comfortable, new SettingsViewModel(Create()).SelectedDensity.Key);
            await Assert.ThrowsAsync<ArgumentException>(() =>
                service.ChangeAsync(nameof(AppSettings.Density), "Unsupported"));
            Assert.Equal(DensityService.Comfortable, Create().Current.Density);
        }
        finally
        {
            window.Close();
            DensityService.Apply(DensityService.Standard);
        }
    }

    [AvaloniaFact]
    public async Task Failed_density_save_keeps_existing_resources_and_selection()
    {
        DensityService.Apply(DensityService.Standard);
        var page = new SettingsViewModel(Create());
        var previous = Avalonia.Application.Current!.Resources["PlinthControlHeight"];
        Directory.CreateDirectory(SettingsPath);
        try
        {
            page.SelectedDensity = page.Densities.Single(x => x.Key == DensityService.Compact);
            await page.PendingChange;
            Assert.Equal(DensityService.Standard, page.SelectedDensity.Key);
            Assert.Equal(previous, Avalonia.Application.Current.Resources["PlinthControlHeight"]);
            Assert.False(await page.TryLeaveAsync());
        }
        finally { Directory.Delete(SettingsPath); }
    }

    [Theory]
    [InlineData("""{"SchemaVersion":2}""")]
    [InlineData("""{"SchemaVersion":2,"Density":"Future"}""")]
    [InlineData("""{"SchemaVersion":2,"Density":null}""")]
    public void Old_or_unknown_density_uses_standard(string json)
    {
        File.WriteAllText(SettingsPath, json);
        Assert.Equal(DensityService.Standard, Create().Current.Density);
    }
}
