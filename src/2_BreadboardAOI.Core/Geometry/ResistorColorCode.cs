// ============================================================================
// FILE: src/2_BreadboardAOI.Core/Geometry/ResistorColorCode.cs
// PURPOSE: Decodes resistance strings (220R, 330, 1k, 4k7, 4.7k, 10k, 1M, etc.)
//          into standard 4-band EIA color code indexes:
//          0:Black, 1:Brown, 2:Red, 3:Orange, 4:Yellow, 5:Green,
//          6:Blue, 7:Violet, 8:Grey, 9:White, 10:Gold, 11:Silver.
// ============================================================================

using System;
using System.Globalization;

namespace BreadboardAOI.Core.Geometry;

public static class ResistorColorCode
{
    public static readonly string[] BandHexColors =
    [
        "#111111", // 0: Black
        "#794022", // 1: Brown
        "#D32F2F", // 2: Red
        "#FF7010", // 3: Orange
        "#FFD600", // 4: Yellow
        "#2E7D32", // 5: Green
        "#1976D2", // 6: Blue
        "#8E24AA", // 7: Violet
        "#757575", // 8: Grey
        "#FFFFFF", // 9: White
        "#CFB53B", // 10: Gold (±5%)
        "#C0C0C0"  // 11: Silver (±10%)
    ];

    public static readonly string[] BandColorNames =
    [
        "Black", "Brown", "Red", "Orange", "Yellow",
        "Green", "Blue", "Violet", "Grey", "White",
        "Gold (±5%)", "Silver (±10%)"
    ];

    /// <summary>
    /// Parses a resistance string (e.g. "220R", "330", "1k", "4k7", "10k", "1M") into Ohms.
    /// </summary>
    public static bool TryParseOhms(string? value, out double ohms)
    {
        ohms = 0;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string text = value.Trim().ToUpperInvariant()
            .Replace("OHMS", string.Empty, StringComparison.Ordinal)
            .Replace("OHM", string.Empty, StringComparison.Ordinal)
            .Replace("Ω", string.Empty, StringComparison.Ordinal);

        int paren = text.IndexOf('(');
        if (paren >= 0)
            text = text[..paren].Trim();

        text = text.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (text.Length == 0)
            return false;

        int cursor = 0;
        int integerPart = 0;
        while (cursor < text.Length && char.IsDigit(text[cursor]))
        {
            integerPart = integerPart * 10 + (text[cursor] - '0');
            cursor++;
        }

        if (cursor == 0 && text[0] != '.' && text[0] != ',')
            return false;

        double magnitude = integerPart;
        if (cursor < text.Length)
        {
            char marker = text[cursor];
            if (marker is 'R' or 'K' or 'M')
            {
                cursor++;

                int fractionDigits = 0;
                double fraction = 0;
                while (cursor < text.Length && char.IsDigit(text[cursor]) && fractionDigits < 3)
                {
                    fraction = fraction * 10 + (text[cursor] - '0');
                    fractionDigits++;
                    cursor++;
                }

                for (int i = 0; i < fractionDigits; i++)
                    fraction /= 10;

                magnitude += fraction;

                magnitude *= marker switch
                {
                    'K' => 1_000d,
                    'M' => 1_000_000d,
                    _ => 1d
                };
            }
            else if (marker is '.' or ',')
            {
                cursor++;
                int fractionDigits = 0;
                double fraction = 0;
                while (cursor < text.Length && char.IsDigit(text[cursor]) && fractionDigits < 3)
                {
                    fraction = fraction * 10 + (text[cursor] - '0');
                    fractionDigits++;
                    cursor++;
                }

                for (int i = 0; i < fractionDigits; i++)
                    fraction /= 10;

                magnitude += fraction;

                if (cursor < text.Length && text[cursor] is 'K' or 'M')
                {
                    magnitude *= text[cursor] == 'K' ? 1_000d : 1_000_000d;
                    cursor++;
                }
            }
        }

        if (!double.IsFinite(magnitude) || magnitude <= 0d)
            return false;

        ohms = magnitude;
        return true;
    }

    /// <summary>
    /// Decodes resistance values like "220R", "330", "1K", "4K7", "4.7K", "10K", "1M"
    /// into four band indexes in [0..11].
    /// </summary>
    public static bool TryDecodeBands(string? value, Span<int> bands)
    {
        if (bands.Length < 4 || !TryParseOhms(value, out double magnitude))
            return false;

        int exponent = 0;
        while (magnitude >= 100d && exponent < 9)
        {
            magnitude /= 10d;
            exponent++;
        }

        while (magnitude < 9.95d && exponent > -2)
        {
            magnitude *= 10d;
            exponent--;
        }

        int digits = (int)Math.Round(magnitude);
        if (digits >= 100)
        {
            digits = (int)Math.Round(digits / 10d);
            exponent++;
        }

        if (digits < 10)
            return false;

        int multiplier = exponent switch
        {
            -1 => 10, // Gold (x0.1)
            -2 => 11, // Silver (x0.01)
            _ => Math.Clamp(exponent, 0, 9)
        };

        bands[0] = digits / 10;
        bands[1] = digits % 10;
        bands[2] = multiplier;
        bands[3] = 10; // Gold (±5% tolerance)
        return true;
    }
}
