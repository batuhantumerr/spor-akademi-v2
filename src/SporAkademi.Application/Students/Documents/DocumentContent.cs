using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using SporAkademi.Domain.SharedKernel;
using SporAkademi.Domain.Students;

namespace SporAkademi.Application.Students.Documents;

/// <summary>An upload that passed inspection: the detected format, a safe display name and the bytes to store.</summary>
public sealed record InspectedDocument(DocumentFormat Format, string FileName, byte[] Content);

/// <summary>
/// Decides what an uploaded file really is and what of it is stored (DEC-322). Nothing the client says about the
/// file is trusted: the format comes from the magic bytes and a structural parse, the declared content type is
/// ignored, and the name is only display data. Images are re-written without metadata (EXIF/GPS, XMP, IPTC,
/// comments, text chunks) and without anything after their end marker; PDFs with active content are refused.
/// </summary>
public static class DocumentContent
{
    private static readonly byte[] PdfMagic = "%PDF-"u8.ToArray();
    private static readonly SearchValues<byte> PdfTrailingWhitespace = SearchValues.Create("\r\n \t\0"u8);
    private static readonly byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>PDF names that make a viewer run code, start programs or carry other files.</summary>
    private static readonly HashSet<string> ActivePdfNames = new(StringComparer.Ordinal)
    {
        "JavaScript", "JS", "Launch", "EmbeddedFile", "EmbeddedFiles", "RichMedia", "XFA", "SubmitForm", "ImportData", "GoToE",
    };

    /// <summary>PNG chunks kept: the image itself and colour information. Text, EXIF and time chunks are dropped.</summary>
    private static readonly HashSet<string> KeptPngChunks = new(StringComparer.Ordinal)
    {
        "IHDR", "PLTE", "IDAT", "IEND", "tRNS", "gAMA", "cHRM", "sRGB", "iCCP", "sBIT", "bKGD", "pHYs",
    };

    /// <summary>WebP chunks kept: image data, alpha, animation and colour profile. EXIF and XMP are dropped.</summary>
    private static readonly HashSet<string> KeptWebPChunks = new(StringComparer.Ordinal)
    {
        "VP8 ", "VP8L", "VP8X", "ALPH", "ANIM", "ANMF", "ICCP",
    };

    public static Result<InspectedDocument> Inspect(DocumentType type, string? fileName, byte[] content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.Length == 0)
        {
            return Result.Failure<InspectedDocument>(DocumentErrors.Empty);
        }

        if (content.Length > DocumentRules.MaxBytes(type))
        {
            return Result.Failure<InspectedDocument>(DocumentErrors.TooLarge);
        }

        if (Detect(content) is not { } format || !DocumentRules.Accepts(type, format))
        {
            return Result.Failure<InspectedDocument>(DocumentErrors.UnsupportedType);
        }

        if (!ExtensionMatches(fileName, format))
        {
            return Result.Failure<InspectedDocument>(DocumentErrors.ExtensionMismatch);
        }

        var cleaned = format switch
        {
            DocumentFormat.Pdf => CheckPdf(content),
            DocumentFormat.Jpeg => StripJpeg(content),
            DocumentFormat.Png => StripPng(content),
            DocumentFormat.WebP => StripWebP(content),
            _ => Result.Failure<byte[]>(DocumentErrors.UnsupportedType),
        };

        return cleaned.IsFailure
            ? Result.Failure<InspectedDocument>(cleaned.Error)
            : Result.Success(new InspectedDocument(format, SafeFileName(fileName, type, format), cleaned.Value));
    }

    /// <summary>The format by magic bytes only; <c>null</c> for anything else (SVG, HTML, executables, archives...).</summary>
    public static DocumentFormat? Detect(ReadOnlySpan<byte> content)
    {
        if (content.StartsWith(PdfMagic))
        {
            return DocumentFormat.Pdf;
        }

        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
        {
            return DocumentFormat.Jpeg;
        }

        if (content.StartsWith(PngMagic))
        {
            return DocumentFormat.Png;
        }

        if (content.Length >= 12 && content[..4].SequenceEqual("RIFF"u8) && content[8..12].SequenceEqual("WEBP"u8))
        {
            return DocumentFormat.WebP;
        }

        return null;
    }

    /// <summary>
    /// The display name: no directories, no control or markup characters, at most
    /// <see cref="StudentDocument.FileNameMaxLength"/> characters, always ending in the detected format's extension.
    /// </summary>
    public static string SafeFileName(string? fileName, DocumentType type, DocumentFormat format)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..].Normalize(NormalizationForm.FormC);
        var extension = Path.GetExtension(name);
        if (DocumentRules.Extensions(format).Contains(extension.ToLowerInvariant()))
        {
            name = name[..^extension.Length];
        }

        var safe = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            safe.Append(char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '(' or ')' ? c : '_');
        }

        var stem = string.Join(' ', safe.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim('.', ' ', '_');
        var canonical = DocumentRules.Extension(format);
        var maxStem = StudentDocument.FileNameMaxLength - canonical.Length;
        if (stem.Length > maxStem)
        {
            // Never cut a surrogate pair in half.
            var cut = char.IsLowSurrogate(stem[maxStem]) ? maxStem - 1 : maxStem;
            stem = stem[..cut].TrimEnd('.', ' ');
        }

        if (stem.Length == 0)
        {
            stem = type == DocumentType.Photo ? "fotograf" : "saglik-raporu";
        }

        return stem + canonical;
    }

    /// <summary>A name with an extension must carry one of the format's extensions (<c>photo.jpg.html</c> is refused).</summary>
    private static bool ExtensionMatches(string? fileName, DocumentFormat format)
    {
        var name = (fileName ?? string.Empty).Replace('\\', '/');
        var extension = Path.GetExtension(name[(name.LastIndexOf('/') + 1)..]).ToLowerInvariant();
        return extension.Length == 0 || DocumentRules.Extensions(format).Contains(extension);
    }

    private static Result<byte[]> Unsupported() => Result.Failure<byte[]>(DocumentErrors.UnsupportedType);

    /// <summary>
    /// A PDF must end with <c>%%EOF</c> (nothing appended after it but whitespace) and may not name active content.
    /// Names are compared after decoding <c>#xx</c> escapes, so <c>/J#61vaScript</c> is caught too. Content inside
    /// compressed object streams is not inspected (a limit of this check, see DEC-322).
    /// </summary>
    private static Result<byte[]> CheckPdf(byte[] content)
    {
        var tail = content.AsSpan(Math.Max(0, content.Length - 1024));
        var eof = tail.LastIndexOf("%%EOF"u8);
        if (eof < 0 || tail[(eof + 5)..].ContainsAnyExcept(PdfTrailingWhitespace))
        {
            return Unsupported();
        }

        for (var i = 0; i < content.Length; i++)
        {
            if (content[i] != (byte)'/')
            {
                continue;
            }

            var name = new StringBuilder();
            var j = i + 1;
            while (j < content.Length && !IsPdfDelimiter(content[j]) && name.Length < 64)
            {
                if (content[j] == (byte)'#' && j + 2 < content.Length && IsHex(content[j + 1]) && IsHex(content[j + 2]))
                {
                    name.Append((char)Convert.ToByte(Encoding.ASCII.GetString(content, j + 1, 2), 16));
                    j += 3;
                }
                else
                {
                    name.Append((char)content[j]);
                    j++;
                }
            }

            if (ActivePdfNames.Contains(name.ToString()))
            {
                return Result.Failure<byte[]>(DocumentErrors.ActiveContent);
            }

            i = j - 1;
        }

        return Result.Success(content);
    }

    private static bool IsPdfDelimiter(byte b) =>
        b is (byte)' ' or (byte)'\r' or (byte)'\n' or (byte)'\t' or (byte)'\f' or 0
            or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'/' or (byte)'%';

    private static bool IsHex(byte b) => b is >= (byte)'0' and <= (byte)'9' or >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

    /// <summary>
    /// Re-writes the JPEG segment by segment: keeps JFIF (APP0), the ICC profile (APP2) and Adobe colour (APP14);
    /// drops EXIF/XMP (APP1), MPF and FlashPix (other APP2), IPTC (APP13), other application segments and comments; stops at the end marker.
    /// A file without a frame, a scan or an end marker is not a JPEG.
    /// </summary>
    private static Result<byte[]> StripJpeg(byte[] content)
    {
        using var output = new MemoryStream(content.Length);
        output.Write([0xFF, 0xD8]);
        var position = 2;
        var sawFrame = false;
        var sawScan = false;

        while (true)
        {
            if (position >= content.Length || content[position] != 0xFF)
            {
                return Unsupported();
            }

            while (position < content.Length && content[position] == 0xFF)
            {
                position++; // fill bytes
            }

            if (position >= content.Length)
            {
                return Unsupported();
            }

            var marker = content[position++];
            if (marker == 0xD9)
            {
                if (!sawFrame || !sawScan)
                {
                    return Unsupported();
                }

                output.Write([0xFF, 0xD9]);
                return Result.Success(output.ToArray());
            }

            if (marker is 0x00 or 0xD8 or (>= 0xD0 and <= 0xD7) or 0x01)
            {
                return Unsupported(); // stray marker outside a scan
            }

            if (position + 2 > content.Length)
            {
                return Unsupported();
            }

            var length = BinaryPrimitives.ReadUInt16BigEndian(content.AsSpan(position));
            if (length < 2 || position + length > content.Length)
            {
                return Unsupported();
            }

            var segmentEnd = position + length;
            var payload = content.AsSpan(position + 2, length - 2);
            var keep = marker switch
            {
                0xE0 => payload.StartsWith("JFIF\0"u8) || payload.StartsWith("JFXX\0"u8),
                0xE2 => payload.StartsWith("ICC_PROFILE\0"u8),
                0xEE => payload.StartsWith("Adobe"u8),
                >= 0xE1 and <= 0xEF => false,
                0xFE => false,
                _ => true,
            };
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                sawFrame = true;
            }

            if (keep)
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(content, position, length);
            }

            position = segmentEnd;
            if (marker != 0xDA)
            {
                continue;
            }

            // Entropy-coded data: copy up to the next real marker (not a stuffed 0x00, a restart marker or fill).
            sawScan = true;
            var scanStart = position;
            while (position + 1 < content.Length
                && !(content[position] == 0xFF && content[position + 1] is not 0x00 and not 0xFF and not (>= 0xD0 and <= 0xD7)))
            {
                position++;
            }

            if (position + 1 >= content.Length)
            {
                return Unsupported();
            }

            output.Write(content, scanStart, position - scanStart);
        }
    }

    /// <summary>
    /// Re-writes the PNG keeping only image and colour chunks; an unknown critical chunk, a chunk running past the
    /// end or a missing IEND makes it invalid; anything after IEND is dropped.
    /// </summary>
    private static Result<byte[]> StripPng(byte[] content)
    {
        using var output = new MemoryStream(content.Length);
        output.Write(PngMagic);
        var position = PngMagic.Length;
        var first = true;

        while (position + 12 <= content.Length)
        {
            var length = BinaryPrimitives.ReadUInt32BigEndian(content.AsSpan(position));
            if (length > int.MaxValue || position + 12 + (long)length > content.Length)
            {
                return Unsupported();
            }

            var chunkType = Encoding.ASCII.GetString(content, position + 4, 4);
            if (!chunkType.All(char.IsAsciiLetter))
            {
                return Unsupported();
            }

            if (first != (chunkType == "IHDR"))
            {
                return Unsupported();
            }

            first = false;
            var total = 12 + (int)length;
            var critical = char.IsUpper(chunkType[0]);
            if (KeptPngChunks.Contains(chunkType))
            {
                output.Write(content, position, total);
            }
            else if (critical)
            {
                return Unsupported();
            }

            position += total;
            if (chunkType == "IEND")
            {
                return Result.Success(output.ToArray());
            }
        }

        return Unsupported();
    }

    /// <summary>
    /// Re-writes the WebP RIFF container keeping only image, alpha, animation and colour chunks, clears the EXIF/XMP
    /// flags of the extended header, and drops anything after the declared RIFF size.
    /// </summary>
    private static Result<byte[]> StripWebP(byte[] content)
    {
        var riffSize = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(4));
        if (riffSize < 4 || riffSize > content.Length - 8L)
        {
            return Unsupported();
        }

        var end = 8 + (int)riffSize;
        using var body = new MemoryStream(end);
        var position = 12;
        var first = true;
        var sawImage = false;

        while (position < end)
        {
            if (position + 8 > end)
            {
                return Unsupported();
            }

            var fourCc = Encoding.ASCII.GetString(content, position, 4);
            var size = BinaryPrimitives.ReadUInt32LittleEndian(content.AsSpan(position + 4));
            var padded = size + (size & 1);
            if (position + 8L + padded > end)
            {
                return Unsupported();
            }

            if (first && fourCc is not ("VP8 " or "VP8L" or "VP8X"))
            {
                return Unsupported();
            }

            first = false;
            sawImage |= fourCc is "VP8 " or "VP8L" or "ANMF";
            var total = 8 + (int)padded;
            if (KeptWebPChunks.Contains(fourCc))
            {
                var chunk = content.AsSpan(position, total).ToArray();
                if (fourCc == "VP8X" && size >= 1)
                {
                    chunk[8] &= unchecked((byte)~0x0C); // EXIF (0x08) and XMP (0x04) flags
                }

                body.Write(chunk);
            }

            position += total;
        }

        if (!sawImage)
        {
            return Unsupported();
        }

        using var output = new MemoryStream((int)body.Length + 12);
        output.Write("RIFF"u8);
        Span<byte> sizeBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(sizeBytes, (uint)(body.Length + 4));
        output.Write(sizeBytes);
        output.Write("WEBP"u8);
        body.Position = 0;
        body.CopyTo(output);
        return Result.Success(output.ToArray());
    }
}
