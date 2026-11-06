// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/PdfSchematicCropper.cs
// PURPOSE: Repairs truncated 250 KB PDF prefixes (Pages 1-4) if needed, scans
//          page text via Docnet.Core for "Typical Application" / "Application
//          Circuit" / "Schematic" anchors, renders the matching page, and crops
//          the schematic diagram region into a self-contained PNG for Qwen3-VL.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace BreadboardAOI.Intelligence;

/// <summary>Result of cropping a "Typical Application" schematic from a datasheet PDF prefix.</summary>
public sealed record SchematicCropResult(
    int PageIndex,
    string MatchedKeyword,
    int Width,
    int Height,
    byte[] PngBytes);

/// <summary>
/// Extracts "Typical Application" schematic regions from partial (250 KB) or full PDF buffers
/// using <see cref="DocLib"/> (Docnet.Core).
/// </summary>
public sealed class PdfSchematicCropper
{
    private static readonly string[] AnchorPhrases =
    [
        "typical application",
        "typical applications",
        "application circuit",
        "application schematic",
        "test circuit",
        "functional diagram",
        "connection diagram",
        "schematic"
    ];

    private static readonly object DocnetGate = new();

    public int RenderWidth { get; init; } = 1080;

    public int RenderHeight { get; init; } = 1440;

    public int MaxPagesToInspect { get; init; } = 4;

    /// <summary>
    /// Scans up to the first 4 pages of <paramref name="pdfBytes"/> via Docnet.Core,
    /// locates the "Typical Application" section, renders that page, and crops the
    /// highest-density schematic region below/around the header into a PNG image.
    /// </summary>
    public SchematicCropResult CropTypicalApplication(byte[] pdfBytes)
    {
        ArgumentNullException.ThrowIfNull(pdfBytes);
        if (pdfBytes.Length == 0)
            throw new ArgumentException("PDF buffer cannot be empty.", nameof(pdfBytes));

        byte[]normalizedPdf = EnsurePdfTrailer(pdfBytes);

        lock (DocnetGate)
        {
            try
            {
                using IDocReader docReader = DocLib.Instance.GetDocReader(
                    normalizedPdf,
                    new PageDimensions(RenderWidth, RenderHeight));

                int pageCount = Math.Min(MaxPagesToInspect, Math.Max(0, docReader.GetPageCount()));
                if (pageCount == 0)
                    return CreateFallbackBlankCrop();

                int selectedPage = 0;
                string matchedPhrase = "Page 1 (Default)";
                int bestRank = int.MaxValue;

                for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
                {
                    using IPageReader pageReader = docReader.GetPageReader(pageIndex);
                    string pageText = pageReader.GetText() ?? string.Empty;
                    if (string.IsNullOrWhiteSpace(pageText))
                        continue;

                    string lower = pageText.ToLowerInvariant();
                    for (int rank = 0; rank < AnchorPhrases.Length; rank++)
                    {
                        if (rank >= bestRank)
                            break;

                        if (lower.Contains(AnchorPhrases[rank], StringComparison.Ordinal))
                        {
                            bestRank = rank;
                            selectedPage = pageIndex;
                            matchedPhrase = AnchorPhrases[rank];
                            break;
                        }
                    }

                    if (bestRank == 0)
                        break;
                }

                using IPageReader targetReader = docReader.GetPageReader(selectedPage);
                int width = targetReader.GetPageWidth();
                int height = targetReader.GetPageHeight();
                byte[] bgra = targetReader.GetImage();

                if (width <= 0 || height <= 0 || bgra is null || bgra.Length < width * height * 4)
                    return CreateFallbackBlankCrop();

                (int cropX, int cropY, int cropW, int cropH) = FindSchematicBoundingBox(
                    targetReader,
                    bgra,
                    width,
                    height,
                    matchedPhrase);

                byte[] croppedBgra = ExtractSubRegionBgra(bgra, width, height, cropX, cropY, cropW, cropH);
                byte[] png = EncodeBgraToPng(croppedBgra, cropW, cropH);

                return new SchematicCropResult(
                    PageIndex: selectedPage,
                    MatchedKeyword: matchedPhrase,
                    Width: cropW,
                    Height: cropH,
                    PngBytes: png);
            }
            catch (Exception)
            {
                return CreateFallbackBlankCrop();
            }
        }
    }

    /// <summary>
    /// Appends a minimal PDF trailer if the 250 KB HTTP Range slice truncated the file
    /// mid-stream so PDFium / Docnet.Core can still parse Pages 1-4 cleanly.
    /// </summary>
    public static byte[] EnsurePdfTrailer(byte[] rawPdf)
    {
        ArgumentNullException.ThrowIfNull(rawPdf);
        if (rawPdf.Length == 0)
            return rawPdf;

        ReadOnlySpan<byte> tail = rawPdf.AsSpan(Math.Max(0, rawPdf.Length - 128));
        if (tail.IndexOf("%%EOF"u8) >= 0)
            return rawPdf;

        byte[] suffix = "\nendstream\nendobj\ntrailer\n<< /Root 1 0 R >>\n%%EOF\n"u8.ToArray();
        byte[] combined = new byte[rawPdf.Length + suffix.Length];
        Buffer.BlockCopy(rawPdf, 0, combined, 0, rawPdf.Length);
        Buffer.BlockCopy(suffix, 0, combined, rawPdf.Length, suffix.Length);
        return combined;
    }

    private static (int X, int Y, int W, int H) FindSchematicBoundingBox(
        IPageReader pageReader,
        byte[] bgra,
        int width,
        int height,
        string matchedPhrase)
    {
        int anchorY = -1;
        try
        {
            IEnumerable<Character>? characters = pageReader.GetCharacters();
            if (characters is not null)
            {
                var sb = new StringBuilder(512);
                var charBoxes = new List<BoundBox>(512);

                foreach (Character ch in characters)
                {
                    sb.Append(char.ToLowerInvariant(ch.Char));
                    charBoxes.Add(ch.Box);
                }

                string full = sb.ToString();
                int idx = full.IndexOf(matchedPhrase.ToLowerInvariant(), StringComparison.Ordinal);
                if (idx >= 0 && idx < charBoxes.Count)
                {
                    anchorY = Math.Clamp(charBoxes[idx].Top, 0, height - 1);
                }
            }
        }
        catch
        {
            // Character layout metadata is optional; ink density heuristic below still works.
        }

        int searchTop = anchorY >= 0
            ? Math.Max(0, anchorY - (int)(height * 0.03))
            : (int)(height * 0.12);
        int searchBottom = anchorY >= 0
            ? Math.Min(height, anchorY + (int)(height * 0.58))
            : (int)(height * 0.88);

        if (searchBottom - searchTop < 128)
        {
            searchTop = 0;
            searchBottom = height;
        }

        int marginX = (int)(width * 0.05);
        int minX = width - marginX;
        int maxX = marginX;
        int minY = searchBottom;
        int maxY = searchTop;
        int darkPixels = 0;

        for (int y = searchTop; y < searchBottom; y += 2)
        {
            int rowOffset = y * width * 4;
            for (int x = marginX; x < width - marginX; x += 2)
            {
                int idx = rowOffset + (x * 4);
                byte b = bgra[idx];
                byte g = bgra[idx + 1];
                byte r = bgra[idx + 2];
                byte a = bgra[idx + 3];

                // PDFium renders transparent background as 0 alpha or white.
                if (a > 32 && (r < 235 || g < 235 || b < 235))
                {
                    darkPixels++;
                    if (x < minX) minX = x;
                    if (x > maxX) maxX = x;
                    if (y < minY) minY = y;
                    if (y > maxY) maxY = y;
                }
            }
        }

        if (darkPixels < 50 || maxX <= minX || maxY <= minY)
        {
            int fallbackW = Math.Max(64, (int)(width * 0.85));
            int fallbackH = Math.Max(64, Math.Min(height - searchTop, (int)(height * 0.50)));
            return ((width - fallbackW) / 2, searchTop, fallbackW, fallbackH);
        }

        int pad = 20;
        minX = Math.Max(0, minX - pad);
        minY = Math.Max(0, minY - pad);
        maxX = Math.Min(width, maxX + pad);
        maxY = Math.Min(height, maxY + pad);

        return (minX, minY, Math.Max(32, maxX - minX), Math.Max(32, maxY - minY));
    }

    private static byte[] ExtractSubRegionBgra(
        byte[] sourceBgra,
        int sourceWidth,
        int sourceHeight,
        int cropX,
        int cropY,
        int cropW,
        int cropH)
    {
        byte[] dest = new byte[cropW * cropH * 4];
        for (int row = 0; row < cropH; row++)
        {
            int srcY = Math.Clamp(cropY + row, 0, sourceHeight - 1);
            for (int col = 0; col < cropW; col++)
            {
                int srcX = Math.Clamp(cropX + col, 0, sourceWidth - 1);
                int srcIdx = ((srcY * sourceWidth) + srcX) * 4;
                int dstIdx = ((row * cropW) + col) * 4;

                byte a = sourceBgra[srcIdx + 3];
                if (a < 16)
                {
                    // Composite transparent PDF page background onto white paper
                    dest[dstIdx] = 255;
                    dest[dstIdx + 1] = 255;
                    dest[dstIdx + 2] = 255;
                    dest[dstIdx + 3] = 255;
                }
                else
                {
                    dest[dstIdx] = sourceBgra[srcIdx];
                    dest[dstIdx + 1] = sourceBgra[srcIdx + 1];
                    dest[dstIdx + 2] = sourceBgra[srcIdx + 2];
                    dest[dstIdx + 3] = 255;
                }
            }
        }

        return dest;
    }

    /// <summary>
    /// Minimal, zero-dependency PNG encoder (8-bit RGBA, RFC 2083 + zlib stream) so
    /// cropped schematics never depend on OS-specific GDI+ / System.Drawing binaries.
    /// </summary>
    public static byte[] EncodeBgraToPng(byte[] bgra, int width, int height)
    {
        ArgumentNullException.ThrowIfNull(bgra);
        using var output = new MemoryStream(width * height + 256);

        // PNG signature
        output.Write([137, 80, 78, 71, 13, 10, 26, 10]);

        // IHDR
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[0..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = 8;  // Bit depth: 8
        ihdr[9] = 6;  // Color type: RGBA
        ihdr[10] = 0; // Compression: Deflate
        ihdr[11] = 0; // Filter: Standard
        ihdr[12] = 0; // Interlace: None
        WritePngChunk(output, "IHDR"u8, ihdr);

        // IDAT
        using (var compressed = new MemoryStream())
        {
            using (var zlib = new ZLibStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
            {
                int rowStride = width * 4;
                byte[] scanline = new byte[1 + rowStride];
                scanline[0] = 0; // Filter type 0 (None)

                for (int y = 0; y < height; y++)
                {
                    int rowStart = y * rowStride;
                    for (int x = 0; x < width; x++)
                    {
                        int src = rowStart + (x * 4);
                        int dst = 1 + (x * 4);
                        scanline[dst] = bgra[src + 2];     // R
                        scanline[dst + 1] = bgra[src + 1]; // G
                        scanline[dst + 2] = bgra[src];     // B
                        scanline[dst + 3] = bgra[src + 3]; // A
                    }

                    zlib.Write(scanline, 0, scanline.Length);
                }
            }

            WritePngChunk(output, "IDAT"u8, compressed.ToArray());
        }

        // IEND
        WritePngChunk(output, "IEND"u8, ReadOnlySpan<byte>.Empty);
        return output.ToArray();
    }

    private static void WritePngChunk(Stream stream, ReadOnlySpan<byte> chunkType, ReadOnlySpan<byte> data)
    {
        Span<byte> lenBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lenBytes, (uint)data.Length);
        stream.Write(lenBytes);
        stream.Write(chunkType);
        if (!data.IsEmpty)
            stream.Write(data);

        uint crc = ComputePngCrc(chunkType, data);
        Span<byte> crcBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
        stream.Write(crcBytes);
    }

    private static uint ComputePngCrc(ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFFFFFFu;
        crc = UpdateCrc(crc, type);
        crc = UpdateCrc(crc, data);
        return crc ^ 0xFFFFFFFFu;
    }

    private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
        {
            uint c = (crc ^ b) & 0xFFu;
            for (int k = 0; k < 8; k++)
                c = (c & 1u) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
            crc = (crc >> 8) ^ c;
        }
        return crc;
    }

    private static SchematicCropResult CreateFallbackBlankCrop()
    {
        const int w = 256;
        const int h = 256;
        byte[] bgra = new byte[w * h * 4];
        Array.Fill(bgra, (byte)255);
        return new SchematicCropResult(0, "Fallback", w, h, EncodeBgraToPng(bgra, w, h));
    }
}
