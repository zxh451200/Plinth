using System;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Microsoft.Extensions.Logging;
using Plinth.App.Localization;

namespace Plinth.App.Services;

public sealed class BrandingOptions
{
    public string ProductName { get; set; } = AppInfo.ProductName;
    public string Description { get; set; } = "";
    public string SupportText { get; set; } = "";
    public string SupportUrl { get; set; } = "";
    public string LogoFile { get; set; } = "";
    public string IconFile { get; set; } = "";
    public string AccentColor { get; set; } = "";

    public BrandingOptions Normalize() => new()
    {
        ProductName = Text(ProductName, 100, AppInfo.ProductName),
        Description = Text(Description, 500, Loc.Get("about.description")),
        SupportText = Text(SupportText, 200, ""),
        LogoFile = AssetName(LogoFile, ".png"),
        IconFile = AssetName(IconFile, ".ico"),
        AccentColor = Regex.IsMatch(AccentColor ?? "", @"^#[0-9a-fA-F]{6}\z")
            ? AccentColor!.ToUpperInvariant() : "",
        SupportUrl = Uri.TryCreate(SupportUrl, UriKind.Absolute, out var url)
            && url.Scheme == Uri.UriSchemeHttps && url.UserInfo.Length == 0
            && !string.IsNullOrWhiteSpace(url.Host) ? url.AbsoluteUri : "",
    };

    private static string Text(string? value, int limit, string fallback) =>
        string.IsNullOrWhiteSpace(value) || value.Trim().Length > limit ? fallback : value.Trim();

    // Deployment assets live next to branding.json; no URLs or arbitrary paths.
    private static string AssetName(string? value, string extension) =>
        value is not null && value.Length <= 100
        && Regex.IsMatch(value, @"^[A-Za-z0-9][A-Za-z0-9_-]*" + Regex.Escape(extension) + @"\z",
            RegexOptions.IgnoreCase) ? value : "";
}

public sealed class BrandingService : IDisposable
{
    public BrandingOptions Current { get; }
    public Bitmap? Logo { get; private set; }

    public BrandingService(ILogger<BrandingService> logger)
    {
        var options = new BrandingOptions();
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "branding.json");
            if (File.Exists(path))
                options = JsonSerializer.Deserialize<BrandingOptions>(File.ReadAllText(path),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? options;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            logger.LogWarning("Branding configuration could not be read; using defaults");
        }
        Current = options.Normalize();
        if (Current.LogoFile.Length > 0)
        {
            try
            {
                using var stream = OpenAsset(Current.LogoFile);
                Logo = Bitmap.DecodeToWidth(stream, 256);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                logger.LogWarning("Brand logo could not be loaded; using the product name");
            }
        }
    }

    public void ApplyAccent(Application application)
    {
        if (!Color.TryParse(Current.AccentColor, out var color)) return;
        application.Resources["PlinthAccentBrush"] = new SolidColorBrush(color);
        foreach (var style in application.Styles)
        {
            if (style is not FluentTheme fluent) continue;
            fluent.Palettes[ThemeVariant.Light] = new ColorPaletteResources { Accent = color };
            fluent.Palettes[ThemeVariant.Dark] = new ColorPaletteResources { Accent = color };
        }
    }

    public WindowIcon CreateWindowIcon()
    {
        if (Current.IconFile.Length > 0)
        {
            try
            {
                using var stream = OpenAsset(Current.IconFile);
                return new WindowIcon(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                or ArgumentException or NotSupportedException or InvalidOperationException)
            {
                // A broken optional brand asset must not prevent startup.
            }
        }
        using var fallback = AssetLoader.Open(new Uri("avares://Plinth.App/Assets/plinth.ico"));
        return new WindowIcon(fallback);
    }

    private static Stream OpenAsset(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, name);
        if (new FileInfo(path).Length > 2 * 1024 * 1024)
            throw new InvalidDataException("Brand assets must be at most 2 MiB.");
        return File.OpenRead(path);
    }

    public void Dispose() => Logo?.Dispose();
}
