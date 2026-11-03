using Avalonia;

namespace Plinth.App.Services;

/// <summary>Shared spacing only; font sizes, column widths and user window layout stay independent.</summary>
public static class DensityService
{
    public const string Standard = "Standard";
    public const string Compact = "Compact";
    public const string Comfortable = "Comfortable";

    public static bool IsSupported(string? value) => value is Standard or Compact or Comfortable;

    public static void Apply(string density)
    {
        if (Application.Current is not { } app) return;
        var compact = density == Compact;
        var comfortable = density == Comfortable;
        var resources = app.Resources;
        resources["PlinthPageMargin"] = new Thickness(compact ? 12 : comfortable ? 24 : 16);
        resources["PlinthPanelPadding"] = new Thickness(compact ? 12 : comfortable ? 20 : 16);
        resources["PlinthSectionSpacing"] = compact ? 8d : comfortable ? 18d : 12d;
        resources["PlinthControlHeight"] = compact ? 28d : comfortable ? 40d : 32d;
        resources["PlinthControlPadding"] = new Thickness(12, compact ? 3 : comfortable ? 9 : 5);
        resources["PlinthRowHeight"] = compact ? 28d : comfortable ? 44d : 34d;
        resources["PlinthRowPadding"] = new Thickness(8, compact ? 2 : comfortable ? 8 : 4);
        resources["PlinthToolbarPadding"] = new Thickness(8, compact ? 4 : comfortable ? 10 : 6);
        resources["PlinthNavigationPadding"] = new Thickness(10, compact ? 4 : comfortable ? 11 : 7);
    }
}
