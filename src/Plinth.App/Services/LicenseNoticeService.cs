using System.Security;
using System.Text;

namespace Plinth.App.Services;

public enum LicenseNoticeStatus
{
    Loaded,
    Missing,
    TooLarge,
    Unreadable
}

public sealed record LicenseNoticeResult(LicenseNoticeStatus Status, string Text = "");

/// <summary>Reads the shipped notice as plain UTF-8 text, never as executable markup.</summary>
public sealed class LicenseNoticeService
{
    public const string FileName = "THIRD_PARTY_NOTICES.md";
    public const int MaximumBytes = 2 * 1024 * 1024;
    private readonly string _directory;
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public LicenseNoticeService(string? directory = null) =>
        _directory = directory ?? AppContext.BaseDirectory;

    /// <summary>Cancellation propagates; file errors return no content or exception details.</summary>
    public async Task<LicenseNoticeResult> ReadAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var stream = new FileStream(
                Path.Combine(_directory, FileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 8192,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            if (stream.Length > MaximumBytes)
                return new(LicenseNoticeStatus.TooLarge);

            // Length is only an early check. The read itself is capped even if a writer
            // keeps appending; one extra byte detects an over-limit file.
            var bytes = new byte[MaximumBytes + 1];
            var count = 0;
            while (count < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(
                    bytes.AsMemory(count, Math.Min(8192, bytes.Length - count)), cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0) break;
                count += read;
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (count > MaximumBytes)
                return new(LicenseNoticeStatus.TooLarge);

            var offset = count >= 3 && bytes[0] == 0xef && bytes[1] == 0xbb && bytes[2] == 0xbf ? 3 : 0;
            var text = Utf8.GetString(bytes, offset, count - offset);
            cancellationToken.ThrowIfCancellationRequested();
            return new(LicenseNoticeStatus.Loaded, text);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(LicenseNoticeStatus.Missing);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                   or SecurityException or ArgumentException or NotSupportedException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return new(LicenseNoticeStatus.Unreadable);
        }
    }
}
