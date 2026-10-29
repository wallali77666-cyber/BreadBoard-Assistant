// ============================================================================
// FILE: src/4_BreadboardAOI.Intelligence/SpeechTextNormalizer.cs
// PURPOSE: Turns tutor text into something a person would say out loud.
//          The model writes "VCC", "GND", "4.7kΩ", "->" and markdown; a TTS
//          engine reads those as gibberish. This normalizer expands units and
//          abbreviations, strips markup, and splits long sentences into
//          breath-sized clauses so the voice stops sounding like a robot
//          reading a log file.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace BreadboardAOI.Intelligence;

public static class SpeechTextNormalizer
{
    private static readonly Regex MarkdownPattern = new(
        @"[`*_#>\[\]{}|]", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex WhitespacePattern = new(
        @"\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex SentenceSplitPattern = new(
        @"(?<=[.!?])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CommaSplitPattern = new(
        @"(?<=[,;:])\s+", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly (Regex Pattern, string Replacement)[] Replacements =
    {
        // A number glued to a unit is the common case in tutor text. These
        // patterns run first so "5V" becomes "5 volts" before the standalone
        // V rule can touch the separated letters of "V C C".
        (new Regex(@"(?<=\d)V\b", RegexOptions.Compiled), " volts"),
        (new Regex(@"(?<=\d)A\b", RegexOptions.Compiled), " amps"),
        (new Regex(@"(?<=\d)W\b", RegexOptions.Compiled), " watts"),
        (new Regex(@"(?<=\d)\s*mA\b", RegexOptions.Compiled), " milliamps"),
        (new Regex(@"(?<=\d)\s*µA\b", RegexOptions.Compiled), " microamps"),
        (new Regex(@"(?<=\d)\s*uA\b", RegexOptions.Compiled), " microamps"),
        (new Regex(@"(?<=\d)\s*kV\b", RegexOptions.Compiled), " kilovolts"),
        (new Regex(@"(?<=\d)\s*mV\b", RegexOptions.Compiled), " millivolts"),
        (new Regex(@"(?<=\d)\s*ms\b", RegexOptions.Compiled), " milliseconds"),
        (new Regex(@"(?<=\d)\s*MHz\b", RegexOptions.Compiled), " megahertz"),
        (new Regex(@"(?<=\d)\s*kHz\b", RegexOptions.Compiled), " kilohertz"),
        (new Regex(@"(?<=\d)\s*Hz\b", RegexOptions.Compiled), " hertz"),
        (new Regex(@"(?<=\d)\s*pF\b", RegexOptions.Compiled), " picofarads"),
        (new Regex(@"(?<=\d)\s*nF\b", RegexOptions.Compiled), " nanofarads"),
        (new Regex(@"(?<=\d)\s*µF\b", RegexOptions.Compiled), " microfarads"),
        (new Regex(@"(?<=\d)\s*uF\b", RegexOptions.Compiled), " microfarads"),

        // Standalone V before VCC: "VCC" then becomes "V C C" and is left alone.
        // The negative lookahead keeps a second normalization pass from turning
        // the already-spoken "V C C" into "volts C C".
        (new Regex(@"\bV\b(?!\s+C\b)", RegexOptions.Compiled), " volts"),

        // Abbreviations first: these are the ones a TTS engine mangles.
        (new Regex(@"\bVCC\b", RegexOptions.Compiled), "V C C"),
        (new Regex(@"\bGND\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "ground"),
        (new Regex(@"\bDRC\b", RegexOptions.Compiled), "D R C"),
        (new Regex(@"\bERC\b", RegexOptions.Compiled), "E R C"),
        (new Regex(@"\bIC\b", RegexOptions.Compiled), "I C"),
        (new Regex(@"\bLED\b", RegexOptions.Compiled), "L E D"),
        (new Regex(@"\bRGB\b", RegexOptions.Compiled), "R G B"),
        (new Regex(@"\bGPIO\b", RegexOptions.Compiled), "G P I O"),
        (new Regex(@"\bPCB\b", RegexOptions.Compiled), "P C B"),
        (new Regex(@"\bLEDs\b", RegexOptions.Compiled), "L E Ds"),

        // Units, longest symbol first so "kΩ" wins over "Ω".
        (new Regex(@"kΩ", RegexOptions.Compiled), " kilo ohms"),
        (new Regex(@"MΩ", RegexOptions.Compiled), " mega ohms"),
        (new Regex(@"Ω", RegexOptions.Compiled), " ohms"),

        // Symbols and arrows.
        (new Regex(@"\s*->\s*", RegexOptions.Compiled), " to "),
        (new Regex(@"\s*=>\s*", RegexOptions.Compiled), " gives "),
        (new Regex(@"\s*==\s*", RegexOptions.Compiled), " equals "),
        (new Regex(@"\s*[×x]\s*(?=\d)", RegexOptions.Compiled), " by "),
        (new Regex(@"\bvs\.?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), " versus ")
    };

    /// <summary>Rewrites technical shorthand into words that read naturally.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string value = MarkdownPattern.Replace(text, " ");

        foreach ((Regex pattern, string replacement) in Replacements)
            value = pattern.Replace(value, replacement);

        value = value.Replace("  ", " ", StringComparison.Ordinal);
        value = WhitespacePattern.Replace(value, " ").Trim();

        // A spoken fragment without a terminator gets a full stop so the TTS
        // engine applies falling intonation instead of clipping the last word.
        // A clause that already ends in a comma, colon or semicolon is left
        // alone: adding a period there produces the tell-tale "hello,." pause.
        if (value.Length > 0 && NeedsTerminator(value[^1]))
            value += ".";

        return value;
    }

    /// <summary>
    /// True when the last character is a word (or a closing quote/bracket) that
    /// still needs a sentence terminator. Punctuation already carries prosody.
    /// </summary>
    private static bool NeedsTerminator(char value) =>
        char.IsLetterOrDigit(value) || value is ')' or ']' or '"' or '\'';

    /// <summary>
    /// Splits normalized text into breath-sized clauses. Short sentences stay
    /// intact; long ones break at commas so a 40-word sentence is not delivered
    /// in one robotic burst.
    /// </summary>
    public static IReadOnlyList<string> SplitForProsody(string? text, int maxChars = 130, bool normalize = true)
    {
        string value = normalize ? Normalize(text) : text ?? string.Empty;
        if (value.Length == 0)
            return Array.Empty<string>();

        if (maxChars < 40)
            maxChars = 40;

        var chunks = new List<string>(4);
        foreach (string sentence in SentenceSplitPattern.Split(value))
        {
            string trimmed = sentence.Trim();
            if (trimmed.Length == 0)
                continue;

            if (trimmed.Length <= maxChars)
            {
                chunks.Add(trimmed);
                continue;
            }

            var current = new StringBuilder(160);
            foreach (string clause in CommaSplitPattern.Split(trimmed))
            {
                string piece = clause.Trim();
                if (piece.Length == 0)
                    continue;

                if (current.Length > 0 && current.Length + piece.Length + 1 > maxChars)
                {
                    chunks.Add(current.ToString().Trim());
                    current.Clear();
                }

                if (current.Length > 0)
                    current.Append(' ');

                current.Append(piece);
            }

            if (current.Length > 0)
                chunks.Add(current.ToString().Trim());
        }

        return chunks;
    }
}
