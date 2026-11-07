using System.Buffers.Binary;
using System.Text;

namespace SporAkademi.Testing;

/// <summary>
/// Small synthetic files for the document tests (P2-F6): structurally valid PDF, JPEG, PNG and WebP with metadata
/// that must be stripped, and hostile files (wrong magic bytes, SVG/HTML disguised as images, active PDFs).
/// Linked into the unit and integration test projects. Every "secret" is a marker string, never real data.
/// </summary>
public static class TestFiles
{
    public const string MetadataMarker = "GPS-37.0N-SECRET";
    public const string TrailerMarker = "<script>alert('polyglot')</script>";

    public static byte[] Pdf(string body = "") => Ascii(
        "%PDF-1.7\n" +
        "1 0 obj\n<< /Type /Catalog /Pages 2 0 R " + body + " >>\nendobj\n" +
        "2 0 obj\n<< /Type /Pages /Kids [] /Count 0 >>\nendobj\n" +
        "trailer\n<< /Root 1 0 R >>\n%%EOF\n");

    public static byte[] PdfWithJavaScript() => Pdf("/OpenAction << /S /JavaScript /JS (app.alert(1)) >>");

    /// <summary><c>#61</c> is 'a': the name decodes to /JavaScript.</summary>
    public static byte[] PdfWithEscapedJavaScript() => Pdf("/OpenAction << /S /J#61vaScript /JS (x) >>");

    public static byte[] PdfWithLaunch() => Pdf("/OpenAction << /S /Launch /F (cmd.exe) >>");

    public static byte[] PdfWithAppendedHtml() => [.. Pdf(), .. Ascii("<html><body>" + TrailerMarker + "</body></html>")];

    /// <summary>JFIF + EXIF (with a GPS marker) + comment + a frame and one scan; optional bytes after the end marker.</summary>
    public static byte[] Jpeg(bool withTrailer = false, int padTo = 0)
    {
        var bytes = new List<byte> { 0xFF, 0xD8 };
        Segment(bytes, 0xE0, [.. Ascii("JFIF\0"), 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00]);
        Segment(bytes, 0xE1, [.. Ascii("Exif\0\0"), .. Ascii(MetadataMarker)]);
        Segment(bytes, 0xFE, Ascii("comment " + MetadataMarker));
        Segment(bytes, 0xDB, [0x00, .. Enumerable.Repeat((byte)1, 64)]);
        Segment(bytes, 0xC0, [0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00]);
        Segment(bytes, 0xC4, [0x00, .. new byte[16]]);
        Segment(bytes, 0xDA, [0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        bytes.AddRange([0x12, 0x34, 0xFF, 0x00, 0x56, 0xFF, 0xD0, 0x78]);
        if (padTo > bytes.Count + 2)
        {
            bytes.AddRange(Enumerable.Repeat((byte)0x55, padTo - bytes.Count - 2));
        }

        bytes.AddRange([0xFF, 0xD9]);
        if (withTrailer)
        {
            bytes.AddRange(Ascii(TrailerMarker));
        }

        return [.. bytes];
    }

    /// <summary>IHDR + a text chunk and an eXIf chunk (markers) + IDAT + IEND; optional bytes after IEND.</summary>
    public static byte[] Png(bool withTrailer = false)
    {
        var bytes = new List<byte> { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };
        Chunk(bytes, "IHDR", [0, 0, 0, 1, 0, 0, 0, 1, 8, 2, 0, 0, 0]);
        Chunk(bytes, "tEXt", Ascii("Comment\0" + MetadataMarker));
        Chunk(bytes, "eXIf", Ascii("MM\0*" + MetadataMarker));
        Chunk(bytes, "IDAT", [0x78, 0x9C, 0x63, 0xF8, 0xCF, 0xC0, 0x00, 0x00, 0x03, 0x01, 0x01, 0x00]);
        Chunk(bytes, "IEND", []);
        if (withTrailer)
        {
            bytes.AddRange(Ascii(TrailerMarker));
        }

        return [.. bytes];
    }

    /// <summary>VP8X (EXIF flag set) + VP8L image + EXIF chunk with a marker.</summary>
    public static byte[] WebP()
    {
        var body = new List<byte>();
        RiffChunk(body, "VP8X", [0x08, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        RiffChunk(body, "VP8L", [0x2F, 0x00, 0x00, 0x00, 0x00]);
        RiffChunk(body, "EXIF", Ascii(MetadataMarker));
        var bytes = new List<byte>();
        bytes.AddRange(Ascii("RIFF"));
        bytes.AddRange(LittleEndian((uint)(body.Count + 4)));
        bytes.AddRange(Ascii("WEBP"));
        bytes.AddRange(body);
        return [.. bytes];
    }

    public static byte[] Svg() => Ascii("<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>");

    public static byte[] Html() => Ascii("<!DOCTYPE html><html><body><script>alert(1)</script></body></html>");

    /// <summary>A Windows executable header.</summary>
    public static byte[] Executable() => [0x4D, 0x5A, 0x90, 0x00, 0x03, 0x00, 0x00, 0x00];

    /// <summary>A GIF (not accepted for any type).</summary>
    public static byte[] Gif() => Ascii("GIF89a\u0001\0\u0001\0\0\0\0;");

    public static bool Contains(byte[] haystack, string needle) =>
        haystack.AsSpan().IndexOf(Encoding.ASCII.GetBytes(needle)) >= 0;

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);

    private static void Segment(List<byte> bytes, byte marker, byte[] payload)
    {
        bytes.AddRange([0xFF, marker]);
        var length = new byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(length, (ushort)(payload.Length + 2));
        bytes.AddRange(length);
        bytes.AddRange(payload);
    }

    private static void Chunk(List<byte> bytes, string type, byte[] data)
    {
        var length = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        bytes.AddRange(length);
        bytes.AddRange(Ascii(type));
        bytes.AddRange(data);
        bytes.AddRange([0, 0, 0, 0]); // CRC is not checked by the inspector
    }

    private static void RiffChunk(List<byte> bytes, string fourCc, byte[] data)
    {
        bytes.AddRange(Ascii(fourCc));
        bytes.AddRange(LittleEndian((uint)data.Length));
        bytes.AddRange(data);
        if (data.Length % 2 == 1)
        {
            bytes.Add(0);
        }
    }

    private static byte[] LittleEndian(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, value);
        return bytes;
    }
}
