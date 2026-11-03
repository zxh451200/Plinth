using System;
using System.Linq;
using System.Text.RegularExpressions;

namespace Plinth.App.Services;

/// <summary>Second-layer display filtering. Export additionally drops all free-form text.</summary>
public static partial class DiagnosticRedactor
{
    internal static bool IsSafeCorrelationId(string? value) => value is { Length: > 0 and <= 128 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.');

    public static string Redact(string text)
    {
        if (text.Length > 32768) text = text[..32768];
        text = Secrets().Replace(text, "[redacted]");
        text = Email().Replace(text, "[email]");
        text = Url().Replace(text, "[url]");
        text = Path().Replace(text, "[path]");
        return Phone().Replace(text, "[number]");
    }

    [GeneratedRegex(@"(?i)(?:bearer\s+[a-z0-9._~+/\-=]+|(?:password|passwd|pwd|token|secret|api[_-]?key|authorization|cookie)\s*[""']?\s*[:=]\s*(?:""[^""]*""|'[^']*'|[^\s,;]+))")]
    private static partial Regex Secrets();
    [GeneratedRegex(@"[\w.+-]+@[\w.-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();
    [GeneratedRegex(@"(?i)https?://[^\s""'<>]+")]
    private static partial Regex Url();
    [GeneratedRegex(@"(?:[A-Za-z]:\\|/)[^\s""'<>]+")]
    private static partial Regex Path();
    [GeneratedRegex(@"(?<!\w)\+?\d[\d ()-]{8,}\d(?!\w)")]
    private static partial Regex Phone();
}
