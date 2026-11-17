using System.Text;
using SporAkademi.Application.Abstractions.Storage;

namespace SporAkademi.Infrastructure.Storage;

/// <summary>The response headers every provider fixes in a signed link, so a stored file is never interpreted as anything else.</summary>
internal static class DownloadHeaders
{
    public const string CacheControl = "private, no-store";

    /// <summary>RFC 6266: an ASCII fallback name plus the UTF-8 name, so Turkish file names survive.</summary>
    public static string ContentDisposition(DownloadDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(disposition);
        var ascii = new StringBuilder(disposition.FileName.Length);
        foreach (var c in disposition.FileName)
        {
            ascii.Append(c is >= ' ' and <= '~' and not '"' and not '\\' and not ';' and not '%' ? c : '_');
        }

        var kind = disposition.Inline ? "inline" : "attachment";
        return $"{kind}; filename=\"{ascii}\"; filename*=UTF-8''{Uri.EscapeDataString(disposition.FileName)}";
    }
}
