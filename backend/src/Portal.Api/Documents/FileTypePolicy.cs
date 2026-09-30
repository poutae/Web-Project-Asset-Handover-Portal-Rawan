using System.Text;

namespace Portal.Api.Documents;

/// <summary>
/// What may be uploaded. The extension must be on the allow-list AND the file's leading bytes must match
/// what that extension claims, so a renamed executable or an HTML page is refused. The content type is
/// always assigned here, never taken from the client. Active content (HTML, SVG, scripts) is not accepted.
/// </summary>
public static class FileTypePolicy
{
    public const int HeaderLength = 8192;

    private sealed record Rule(string ContentType, Func<byte[], int, bool> Matches);

    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = new("application/pdf", (h, n) => StartsWith(h, n, "%PDF-"u8)),
        [".png"] = new("image/png", (h, n) => StartsWith(h, n, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A])),
        [".jpg"] = new("image/jpeg", (h, n) => StartsWith(h, n, [0xFF, 0xD8, 0xFF])),
        [".jpeg"] = new("image/jpeg", (h, n) => StartsWith(h, n, [0xFF, 0xD8, 0xFF])),
        [".gif"] = new("image/gif", (h, n) => StartsWith(h, n, "GIF87a"u8) || StartsWith(h, n, "GIF89a"u8)),
        [".webp"] = new("image/webp", (h, n) => n >= 12 && StartsWith(h, n, "RIFF"u8) && h.AsSpan(8, 4).SequenceEqual("WEBP"u8)),
        [".zip"] = new("application/zip", (h, n) => StartsWith(h, n, "PK\x03\x04"u8)),
        [".docx"] = new("application/vnd.openxmlformats-officedocument.wordprocessingml.document", (h, n) => StartsWith(h, n, "PK\x03\x04"u8)),
        [".xlsx"] = new("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", (h, n) => StartsWith(h, n, "PK\x03\x04"u8)),
        [".pptx"] = new("application/vnd.openxmlformats-officedocument.presentationml.presentation", (h, n) => StartsWith(h, n, "PK\x03\x04"u8)),
        [".txt"] = new("text/plain; charset=utf-8", IsUtf8Text),
        [".md"] = new("text/plain; charset=utf-8", IsUtf8Text),
        [".csv"] = new("text/csv; charset=utf-8", IsUtf8Text),
        [".json"] = new("application/json", IsUtf8Text),
    };

    public static IReadOnlyCollection<string> AllowedExtensions => Rules.Keys;

    /// <summary>Returns the server-assigned content type, or null if the file must be refused.</summary>
    public static string? Validate(string fileName, byte[] header, int headerLength)
    {
        var extension = Path.GetExtension(fileName);
        return Rules.TryGetValue(extension, out var rule) && rule.Matches(header, headerLength) ? rule.ContentType : null;
    }

    /// <summary>A safe display and download name: no path, no control characters, bounded length.</summary>
    public static string SanitizeFileName(string? raw)
    {
        var name = Path.GetFileName((raw ?? string.Empty).Replace('\\', '/'));
        var cleaned = new string(name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?')).ToArray()).Trim(' ', '.');
        if (cleaned.Length > 200)
        {
            var extension = Path.GetExtension(cleaned);
            cleaned = cleaned[..(200 - extension.Length)] + extension;
        }

        return cleaned;
    }

    private static bool StartsWith(byte[] header, int length, ReadOnlySpan<byte> prefix) =>
        length >= prefix.Length && header.AsSpan(0, prefix.Length).SequenceEqual(prefix);

    private static bool IsUtf8Text(byte[] header, int length)
    {
        var span = header.AsSpan(0, length);
        if (span.Contains((byte)0))
        {
            return false;
        }

        // A file cut mid-character at the end of the sample is still text; only reject real bad bytes.
        var decoder = new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder();
        try
        {
            decoder.GetCharCount(span, flush: false);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }
}
