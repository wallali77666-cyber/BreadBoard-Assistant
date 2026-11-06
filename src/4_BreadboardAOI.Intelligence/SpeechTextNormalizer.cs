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

    /// <summary>
    /// Conversational contractions. A tutor that says "do not" and "it is" in
    /// every sentence sounds like a manual; the same advice with contractions
    /// sounds like a person talking. Technical identifiers are untouched.
    /// </summary>
    private static readonly (Regex Pattern, string Replacement)[] Contractions =
    {
        (new Regex(@"\bdo not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "don't"),
        (new Regex(@"\bdoes not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "doesn't"),
        (new Regex(@"\bdid not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "didn't"),
        (new Regex(@"\bis not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "isn't"),
        (new Regex(@"\bare not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "aren't"),
        (new Regex(@"\bwas not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "wasn't"),
        (new Regex(@"\bwere not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "weren't"),
        (new Regex(@"\bcan not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "can't"),
        (new Regex(@"\bcannot\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "can't"),
        (new Regex(@"\bcould not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "couldn't"),
        (new Regex(@"\bshould not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "shouldn't"),
        (new Regex(@"\bwould not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "wouldn't"),
        (new Regex(@"\bwill not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "won't"),
        (new Regex(@"\bhave not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "haven't"),
        (new Regex(@"\bhas not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "hasn't"),
        (new Regex(@"\bhad not\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "hadn't"),
        (new Regex(@"\bIt is\b", RegexOptions.Compiled), "It's"),
        (new Regex(@"\bThat is\b", RegexOptions.Compiled), "That's"),
        (new Regex(@"\bThere is\b", RegexOptions.Compiled), "There's"),
        (new Regex(@"\bHere is\b", RegexOptions.Compiled), "Here's"),
        (new Regex(@"\bWhat is\b", RegexOptions.Compiled), "What's"),
        (new Regex(@"\bYou are\b", RegexOptions.Compiled), "You're"),
        (new Regex(@"\bWe are\b", RegexOptions.Compiled), "We're"),
        (new Regex(@"\bThey are\b", RegexOptions.Compiled), "They're"),
        (new Regex(@"\bit is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "it's"),
        (new Regex(@"\bthat is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "that's"),
        (new Regex(@"\bthere is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "there's"),
        (new Regex(@"\bhere is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "here's"),
        (new Regex(@"\bwhat is\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "what's"),
        (new Regex(@"\byou are\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "you're"),
        (new Regex(@"\bwe are\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "we're"),
        (new Regex(@"\bthey are\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "they're"),
        (new Regex(@"\bI am\b", RegexOptions.Compiled), "I'm"),
        (new Regex(@"\bI will\b", RegexOptions.Compiled), "I'll"),
        (new Regex(@"\blet us\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), "let's"),
        (new Regex(@"\be\.g\.", RegexOptions.Compiled | RegexOptions.IgnoreCase), "for example"),
        (new Regex(@"\bi\.e\.", RegexOptions.Compiled | RegexOptions.IgnoreCase), "that is")
    };

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
        (new Regex(@"\bvs\.?\b", RegexOptions.Compiled | RegexOptions.IgnoreCase), " versus "),
        (new Regex(@"(?<=\d)%", RegexOptions.Compiled), " percent"),
        (new Regex(@"\s*&\s*", RegexOptions.Compiled), " and ")
    };

    /// <summary>Rewrites technical shorthand into words that read naturally.</summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        string value = MarkdownPattern.Replace(text, " ");

        foreach ((Regex pattern, string replacement) in Replacements)
            value = pattern.Replace(value, replacement);

        // Contractions come last so they see clean prose rather than symbols.
        foreach ((Regex pattern, string replacement) in Contractions)
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
    /// Splits prose into complete sentences, preserving the terminal punctuation.
    /// Used by the SAPI writer to give each sentence its own intonation contour.
    /// </summary>
    public static IReadOnlyList<string> SplitSentences(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return Array.Empty<string>();

        var sentences = new List<string>(4);
        foreach (string sentence in SentenceSplitPattern.Split(text))
        {
            string trimmed = sentence.Trim();
            if (trimmed.Length > 0)
                sentences.Add(trimmed);
        }

        return sentences;
    }

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
