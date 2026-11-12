using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

public enum BoxCodeDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

public sealed record BoxCodeDiagnostic(
    int Line,
    int Column,
    BoxCodeDiagnosticSeverity Severity,
    string Code,
    string Message,
    string? Suggestion = null)
{
    public string SeverityLabel => Severity switch
    {
        BoxCodeDiagnosticSeverity.Error => "ERROR",
        BoxCodeDiagnosticSeverity.Warning => "WARN",
        _ => "INFO"
    };

    public string DisplaySummary => $"L{Line}:{Column} [{Code}] {Message}" +
        (string.IsNullOrWhiteSpace(Suggestion) ? string.Empty : $" (Fix: {Suggestion})");
}

public sealed record SimulatedPinAction(
    string PinName,
    bool IsOutput,
    bool IsPwm,
    bool IsDigitalHigh,
    int PwmDuty,
    string? LcdLine1 = null,
    string? LcdLine2 = null,
    string? SerialText = null,
    int BuzzerHz = 0,
    int DelayMs = 0);

public sealed record BoxCodeAnalysisReport(
    bool IsValid,
    int ErrorCount,
    int WarningCount,
    IReadOnlyList<BoxCodeDiagnostic> Diagnostics,
    IReadOnlyList<SimulatedPinAction> ExtractedActions)
{
    public string StatusSummary => ErrorCount > 0
        ? $"{ErrorCount} error(s), {WarningCount} warning(s) detected"
        : WarningCount > 0
            ? $"Code valid with {WarningCount} warning(s)"
            : "0 errors · Code ready for upload";
}

/// <summary>Deterministic static syntax, hardware-pin semantic analyzer, and action extractor for C++ and MicroPython.</summary>
public static partial class BoxCodeErrorAnalyzer
{
    private static readonly DevBoardAndModuleRuleEngine BoardRules = new();

    private static readonly Dictionary<string, string> MisspelledCppApis = new(StringComparer.Ordinal)
    {
        ["digtalWrite"] = "digitalWrite",
        ["digitalwirte"] = "digitalWrite",
        ["digitalWrtie"] = "digitalWrite",
        ["digitalwrite"] = "digitalWrite",
        ["digtalRead"] = "digitalRead",
        ["digitalread"] = "digitalRead",
        ["pinmode"] = "pinMode",
        ["pinMod"] = "pinMode",
        ["pinMode_"] = "pinMode",
        ["analogwrite"] = "analogWrite",
        ["anlogWrite"] = "analogWrite",
        ["analogWrtie"] = "analogWrite",
        ["analogread"] = "analogRead",
        ["Seral"] = "Serial",
        ["dlay"] = "delay",
        ["dealy"] = "delay",
        ["lcdprint"] = "lcd.print"
    };

    public static BoxCodeAnalysisReport Analyze(
        string? code,
        TargetProgrammingLanguage language,
        MicrocontrollerBoardType boardType,
        IReadOnlyCollection<string>? wiredMcuPins = null) =>
        Analyze(code, (BoxCodingLanguage)language, boardType, wiredMcuPins);

    public static BoxCodeAnalysisReport Analyze(
        string? code,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        IReadOnlyCollection<string>? wiredMcuPins = null)
    {
        var diagnostics = new List<BoxCodeDiagnostic>();
        var actions = new List<SimulatedPinAction>();

        if (string.IsNullOrWhiteSpace(code))
        {
            diagnostics.Add(new BoxCodeDiagnostic(
                1, 1, BoxCodeDiagnosticSeverity.Error, "BC001",
                "Code editor is empty.",
                "Add or generate firmware blocks before verifying."));
            return BuildReport(diagnostics, actions);
        }

        string normalized = code.Replace("\r\n", "\n").Replace('\r', '\n');
        string[] rawLines = normalized.Split('\n');

        // 1. Strip comments & check strings + delimiters per line
        var strippedLines = StripCommentsStringsAndCheckDelimiters(
            rawLines, language == BoxCodingLanguage.MicroPython, diagnostics);

        // 2. Language-specific syntax rules
        if (language == BoxCodingLanguage.ArduinoCpp)
        {
            AnalyzeArduinoCppSyntax(strippedLines, diagnostics);
        }
        else
        {
            AnalyzeMicroPythonSyntax(rawLines, strippedLines, diagnostics);
        }

        // 3. Hardware & board pin semantic rules (BC030..BC034)
        AnalyzeHardwarePins(rawLines, strippedLines, language, boardType, wiredMcuPins, diagnostics);

        // 4. Extract runtime actions for simulator
        ExtractSimulatedActions(rawLines, strippedLines, language, actions);

        return BuildReport(diagnostics, actions);
    }

    private static BoxCodeAnalysisReport BuildReport(
        List<BoxCodeDiagnostic> diagnostics,
        List<SimulatedPinAction> actions)
    {
        int errors = diagnostics.Count(d => d.Severity == BoxCodeDiagnosticSeverity.Error);
        int warnings = diagnostics.Count(d => d.Severity == BoxCodeDiagnosticSeverity.Warning);
        return new BoxCodeAnalysisReport(
            IsValid: errors == 0,
            ErrorCount: errors,
            WarningCount: warnings,
            Diagnostics: diagnostics,
            ExtractedActions: actions);
    }

    private static string[] StripCommentsStringsAndCheckDelimiters(
        string[] rawLines,
        bool isPython,
        List<BoxCodeDiagnostic> diagnostics)
    {
        var stripped = new string[rawLines.Length];
        var delimStack = new Stack<(char Ch, int Line, int Col)>();
        bool inCppBlockComment = false;
        char? pythonTripleQuote = null;

        for (int lineIdx = 0; lineIdx < rawLines.Length; lineIdx++)
        {
            int lineNum = lineIdx + 1;
            string line = rawLines[lineIdx];
            var sb = new StringBuilder(line.Length);

            int i = 0;
            while (i < line.Length)
            {
                char c = line[i];

                if (inCppBlockComment)
                {
                    if (c == '*' && i + 1 < line.Length && line[i + 1] == '/')
                    {
                        inCppBlockComment = false;
                        sb.Append("  ");
                        i += 2;
                        continue;
                    }
                    sb.Append(' ');
                    i++;
                    continue;
                }

                if (isPython && pythonTripleQuote.HasValue)
                {
                    char tq = pythonTripleQuote.Value;
                    if (c == tq && i + 2 < line.Length && line[i + 1] == tq && line[i + 2] == tq)
                    {
                        pythonTripleQuote = null;
                        sb.Append("\"\"");
                        i += 3;
                        continue;
                    }
                    sb.Append(' ');
                    i++;
                    continue;
                }

                // Comments
                if (!isPython && c == '/' && i + 1 < line.Length && line[i + 1] == '/')
                    break;
                if (!isPython && c == '/' && i + 1 < line.Length && line[i + 1] == '*')
                {
                    inCppBlockComment = true;
                    sb.Append("  ");
                    i += 2;
                    continue;
                }
                if (isPython && c == '#')
                    break;

                // Python triple quotes
                if (isPython && (c == '"' || c == '\'') && i + 2 < line.Length && line[i + 1] == c && line[i + 2] == c)
                {
                    pythonTripleQuote = c;
                    sb.Append("\"\"");
                    i += 3;
                    continue;
                }

                // String / char literals
                if (c == '"' || c == '\'')
                {
                    char quote = c;
                    int openCol = i + 1;
                    bool closed = false;
                    i++;
                    while (i < line.Length)
                    {
                        if (line[i] == '\\')
                        {
                            i += 2;
                            continue;
                        }
                        if (line[i] == quote)
                        {
                            closed = true;
                            i++;
                            break;
                        }
                        i++;
                    }

                    sb.Append("\"\"");
                    if (!closed)
                    {
                        diagnostics.Add(new BoxCodeDiagnostic(
                            lineNum, openCol, BoxCodeDiagnosticSeverity.Error, "BC003",
                            $"Unclosed string literal ({quote}).",
                            $"Add closing {quote} before end of line."));
                    }
                    continue;
                }

                // Delimiters
                if (c is '{' or '(' or '[')
                {
                    delimStack.Push((c, lineNum, i + 1));
                }
                else if (c is '}' or ')' or ']')
                {
                    char expectedOpen = c switch { '}' => '{', ')' => '(', _ => '[' };
                    if (delimStack.Count == 0 || delimStack.Peek().Ch != expectedOpen)
                    {
                        diagnostics.Add(new BoxCodeDiagnostic(
                            lineNum, i + 1, BoxCodeDiagnosticSeverity.Error, "BC002",
                            $"Unmatched closing '{c}'.",
                            $"Remove extra '{c}' or add matching '{expectedOpen}'."));
                    }
                    else
                    {
                        delimStack.Pop();
                    }
                }

                sb.Append(c);
                i++;
            }

            stripped[lineIdx] = sb.ToString();
        }

        while (delimStack.Count > 0)
        {
            var (ch, line, col) = delimStack.Pop();
            char expectedClose = ch switch { '{' => '}', '(' => ')', _ => ']' };
            diagnostics.Add(new BoxCodeDiagnostic(
                line, col, BoxCodeDiagnosticSeverity.Error, "BC002",
                $"Unclosed '{ch}' delimiter.",
                $"Add closing '{expectedClose}'."));
        }

        return stripped;
    }

    private static void AnalyzeArduinoCppSyntax(
        string[] strippedLines,
        List<BoxCodeDiagnostic> diagnostics)
    {
        string allStripped = string.Join('\n', strippedLines);

        bool hasSetup = SetupDeclRegex().IsMatch(allStripped);
        bool hasLoop = LoopDeclRegex().IsMatch(allStripped);
        if (!hasSetup || !hasLoop)
        {
            diagnostics.Add(new BoxCodeDiagnostic(
                1, 1, BoxCodeDiagnosticSeverity.Error, "BC010",
                "Arduino C++ sketch must define both void setup() and void loop().",
                "Ensure both void setup() { ... } and void loop() { ... } functions are present."));
        }

        // Check misspelled Arduino APIs (BC012)
        for (int lineIdx = 0; lineIdx < strippedLines.Length; lineIdx++)
        {
            string line = strippedLines[lineIdx];
            if (string.IsNullOrWhiteSpace(line)) continue;

            foreach (Match m in IdentifierTokenRegex().Matches(line))
            {
                string tok = m.Value;
                if (MisspelledCppApis.TryGetValue(tok, out string? fixedApi))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineIdx + 1, m.Index + 1, BoxCodeDiagnosticSeverity.Error, "BC012",
                        $"Possible misspelled Arduino API '{tok}'.",
                        $"Did you mean '{fixedApi}'?"));
                }
                else if (tok == "serial")
                {
                    int after = m.Index + tok.Length;
                    if (after < line.Length && line[after] == '.')
                    {
                        diagnostics.Add(new BoxCodeDiagnostic(
                            lineIdx + 1, m.Index + 1, BoxCodeDiagnosticSeverity.Error, "BC012",
                            "Arduino 'serial' must be capitalized as 'Serial'.",
                            "Replace 'serial' with 'Serial'."));
                    }
                }
            }
        }

        // Check missing semicolons (BC011)
        int parenDepth = 0;
        for (int lineIdx = 0; lineIdx < strippedLines.Length; lineIdx++)
        {
            string raw = strippedLines[lineIdx];
            string trimmed = raw.Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            int parenAfterLine = parenDepth + CountCharDelta(trimmed, '(', ')');

            if (trimmed.StartsWith('#'))
            {
                parenDepth = Math.Max(0, parenAfterLine);
                continue;
            }

            char last = trimmed[^1];
            if (last is ';' or '{' or '}' or ',' or ':' or '\\')
            {
                parenDepth = Math.Max(0, parenAfterLine);
                continue;
            }

            if (parenAfterLine > 0)
            {
                parenDepth = parenAfterLine;
                continue;
            }

            parenDepth = 0;

            string nextNonEmpty = string.Empty;
            for (int n = lineIdx + 1; n < strippedLines.Length; n++)
            {
                string candidate = strippedLines[n].Trim();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    nextNonEmpty = candidate;
                    break;
                }
            }

            if (nextNonEmpty.StartsWith('{') ||
                nextNonEmpty.StartsWith('.') ||
                nextNonEmpty.StartsWith("&&") ||
                nextNonEmpty.StartsWith("||") ||
                nextNonEmpty.StartsWith('?') ||
                nextNonEmpty.StartsWith(':'))
            {
                continue;
            }

            if (CppControlOrFuncHeaderRegex().IsMatch(trimmed) ||
                trimmed == "else" ||
                trimmed == "do")
            {
                continue;
            }

            // Function signature declaration spanning before '{' or multiline args
            if (CppFunctionSignatureRegex().IsMatch(trimmed))
                continue;

            diagnostics.Add(new BoxCodeDiagnostic(
                lineIdx + 1, Math.Max(1, raw.TrimEnd().Length), BoxCodeDiagnosticSeverity.Error, "BC011",
                $"Missing semicolon ';' at end of statement on line {lineIdx + 1}.",
                "Add ';' at the end of the statement."));
        }
    }

    private static void AnalyzeMicroPythonSyntax(
        string[] rawLines,
        string[] strippedLines,
        List<BoxCodeDiagnostic> diagnostics)
    {
        bool flaggedCppMode = false;
        int parenDepth = 0;

        for (int lineIdx = 0; lineIdx < strippedLines.Length; lineIdx++)
        {
            string trimmed = strippedLines[lineIdx].Trim();
            if (string.IsNullOrWhiteSpace(trimmed))
                continue;

            int lineNum = lineIdx + 1;

            // BC022: C++ syntax in MicroPython mode
            if (!flaggedCppMode &&
                (CppInPythonRegex().IsMatch(trimmed) ||
                 trimmed.EndsWith(';') ||
                 trimmed.StartsWith("#include", StringComparison.Ordinal) ||
                 trimmed.StartsWith("#define", StringComparison.Ordinal)))
            {
                flaggedCppMode = true;
                diagnostics.Add(new BoxCodeDiagnostic(
                    lineNum, 1, BoxCodeDiagnosticSeverity.Error, "BC022",
                    "C++ syntax detected while MicroPython mode is selected.",
                    "Use MicroPython syntax (Pin/PWM, colons, indentation) or switch language to Arduino C++."));
            }

            // BC020: Missing colon on control/function header
            if (parenDepth == 0 && PythonSuiteStartRegex().IsMatch(trimmed) && !trimmed.EndsWith(':'))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    lineNum, Math.Max(1, trimmed.Length), BoxCodeDiagnosticSeverity.Error, "BC020",
                    "Python control/function header is missing trailing ':'.",
                    "Add ':' at the end of the line."));
            }

            // BC021: Tabs+spaces mixing or invalid indent width + missing suite body
            string raw = rawLines[lineIdx];
            if (parenDepth == 0)
            {
                int wsLen = 0;
                bool hasSpace = false;
                bool hasTab = false;
                while (wsLen < raw.Length && (raw[wsLen] == ' ' || raw[wsLen] == '\t'))
                {
                    if (raw[wsLen] == ' ') hasSpace = true;
                    if (raw[wsLen] == '\t') hasTab = true;
                    wsLen++;
                }

                if (hasSpace && hasTab)
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineNum, 1, BoxCodeDiagnosticSeverity.Error, "BC021",
                        "Mixed tabs and spaces in Python indentation.",
                        "Use consistent 4-space indentation."));
                }
                else if (hasSpace && wsLen % 2 != 0)
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineNum, 1, BoxCodeDiagnosticSeverity.Error, "BC021",
                        $"Irregular Python indentation ({wsLen} spaces).",
                        "Indent blocks using multiples of 4 spaces."));
                }

                // Check suite body when line ends with ':' (e.g. "while True:", NOT inline "if lcd: lcd.clear()")
                if (trimmed.EndsWith(':'))
                {
                    int currentIndent = GetVisualIndentWidth(raw);
                    bool foundNext = false;
                    int nextIndent = -1;
                    int nextLineNum = lineNum + 1;
                    for (int n = lineIdx + 1; n < strippedLines.Length; n++)
                    {
                        if (!string.IsNullOrWhiteSpace(strippedLines[n]))
                        {
                            foundNext = true;
                            nextIndent = GetVisualIndentWidth(rawLines[n]);
                            nextLineNum = n + 1;
                            break;
                        }
                    }

                    if (!foundNext || nextIndent <= currentIndent)
                    {
                        diagnostics.Add(new BoxCodeDiagnostic(
                            nextLineNum, 1, BoxCodeDiagnosticSeverity.Error, "BC021",
                            "Expected an indented code block after ':' statement.",
                            "Indent the following line(s) by 4 spaces or add 'pass'."));
                    }
                }
            }

            parenDepth = Math.Max(0, parenDepth + CountCharDelta(trimmed, '(', ')') + CountCharDelta(trimmed, '[', ']'));
        }
    }

    private static void AnalyzeHardwarePins(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        IReadOnlyCollection<string>? wiredMcuPins,
        List<BoxCodeDiagnostic> diagnostics)
    {
        var symbolMap = BuildPinSymbolMap(strippedLines);
        var setupOutputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pinModeCalls = new List<(string Token, string Mode, int Line, int Col)>();
        var pinUsages = new List<(string Token, bool IsOutput, bool RequiresPinModeCheck, int Line, int Col)>();

        bool inSetup = false;
        int setupBraceDepth = 0;

        for (int lineIdx = 0; lineIdx < strippedLines.Length; lineIdx++)
        {
            string line = strippedLines[lineIdx];
            if (string.IsNullOrWhiteSpace(line)) continue;
            int lineNum = lineIdx + 1;

            if (language == BoxCodingLanguage.ArduinoCpp)
            {
                if (SetupDeclRegex().IsMatch(line))
                {
                    inSetup = true;
                    setupBraceDepth = CountCharDelta(line, '{', '}');
                }
                else if (inSetup)
                {
                    setupBraceDepth += CountCharDelta(line, '{', '}');
                    if (setupBraceDepth <= 0)
                        inSetup = false;
                }

                foreach (Match m in CppPinModeRegex().Matches(line))
                {
                    string pinTok = ResolvePinSymbol(m.Groups[1].Value, symbolMap);
                    string mode = m.Groups[2].Value.Trim().ToUpperInvariant();
                    bool isOut = mode.Contains("OUTPUT");
                    pinModeCalls.Add((pinTok, mode, lineNum, m.Index + 1));
                    pinUsages.Add((pinTok, isOut, false, lineNum, m.Index + 1));

                    if (inSetup && isOut)
                        setupOutputPins.Add(NormalizePinKey(pinTok));
                }

                foreach (Match m in CppServoAttachRegex().Matches(line))
                {
                    string pinTok = ResolvePinSymbol(m.Groups[1].Value, symbolMap);
                    pinUsages.Add((pinTok, true, false, lineNum, m.Index + 1));
                    if (inSetup)
                        setupOutputPins.Add(NormalizePinKey(pinTok));
                }

                foreach (Match m in CppDigitalWriteRegex().Matches(line))
                {
                    string pinTok = ResolvePinSymbol(m.Groups[1].Value, symbolMap);
                    pinUsages.Add((pinTok, true, !inSetup, lineNum, m.Index + 1));
                }

                foreach (Match m in CppAnalogWriteOrToneRegex().Matches(line))
                {
                    string pinTok = ResolvePinSymbol(m.Groups[1].Value, symbolMap);
                    pinUsages.Add((pinTok, true, false, lineNum, m.Index + 1));
                }

                foreach (Match m in CppReadPinRegex().Matches(line))
                {
                    string pinTok = ResolvePinSymbol(m.Groups[1].Value, symbolMap);
                    pinUsages.Add((pinTok, false, false, lineNum, m.Index + 1));
                }
            }
            else
            {
                foreach (Match m in PythonPinCtorRegex().Matches(line))
                {
                    string pinTok = ResolvePinSymbol(m.Groups[1].Value, symbolMap);
                    string mode = m.Groups[2].Success ? m.Groups[2].Value.ToUpperInvariant() : string.Empty;
                    bool isOut = mode.Contains("OUT") ||
                        line.Contains("PWM(", StringComparison.Ordinal);
                    pinUsages.Add((pinTok, isOut, false, lineNum, m.Index + 1));
                }
            }
        }

        // Fallback: if no explicit void setup() detected cleanly, still record global pinMode calls
        if (setupOutputPins.Count == 0)
        {
            foreach (var pm in pinModeCalls.Where(p => p.Mode.Contains("OUTPUT")))
                setupOutputPins.Add(NormalizePinKey(pm.Token));
        }

        var profile = boardType != MicrocontrollerBoardType.None
            ? BoardRules.GetBoardProfile(boardType)
            : null;

        var warnedMissingPinMode = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var warnedUnwired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        HashSet<string>? wiredNormalized = wiredMcuPins is { Count: > 0 }
            ? wiredMcuPins.Select(NormalizePinKey).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : null;

        foreach (var (token, isOutput, requiresPinModeCheck, lineNum, col) in pinUsages)
        {
            if (string.IsNullOrWhiteSpace(token) || IsNonPinToken(token))
                continue;

            bool hasNum = TryExtractNumericPin(token, out int numPin, out bool isAnalogPrefix);

            // Board-specific rules (BC030, BC031, BC032)
            if (boardType != MicrocontrollerBoardType.None)
            {
                bool isEsp = boardType is MicrocontrollerBoardType.Esp32DevKitV1 or MicrocontrollerBoardType.WemosEsp32S2Mini;

                if (isEsp && hasNum && !isAnalogPrefix && numPin is >= 6 and <= 11)
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineNum, col, BoxCodeDiagnosticSeverity.Error, "BC031",
                        $"GPIO {numPin} is reserved for internal SPI flash on ESP32.",
                        "Use a safe general-purpose GPIO such as 2, 4, 5, 13, 18, 19, 21, 22, 23, 25-27, or 32-33."));
                    continue;
                }

                McuPinDefinition? mcuPin = profile is not null ? ResolveBoardPin(profile, token, hasNum, numPin, isAnalogPrefix) : null;
                bool isEspInputOnlyNum = boardType == MicrocontrollerBoardType.Esp32DevKitV1 && hasNum &&
                    (numPin is 34 or 35 or 36 or 39);

                if (isOutput && (isEspInputOnlyNum || (mcuPin is { IsAnalogOnly: true })))
                {
                    string displayPin = hasNum ? numPin.ToString(CultureInfo.InvariantCulture) : token;
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineNum, col, BoxCodeDiagnosticSeverity.Error, "BC030",
                        $"GPIO {displayPin} on ESP32 is input-only and cannot drive outputs or PWM.",
                        "Move this output to a bidirectional GPIO pin (e.g., GPIO 2, 4, 5, 12-19, 21-27, 32-33)."));
                    continue;
                }

                if (IsOutOfRangeForBoard(boardType, token, hasNum, numPin, isAnalogPrefix, mcuPin))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineNum, col, BoxCodeDiagnosticSeverity.Error, "BC032",
                        $"Pin '{token}' is outside the valid GPIO range for {profile?.DisplayName ?? boardType.ToString()}.",
                        "Select a valid pin header printed on the active development board."));
                    continue;
                }
            }

            string normKey = NormalizePinKey(token);

            // BC033: digitalWrite without pinMode(pin, OUTPUT) in setup()
            if (language == BoxCodingLanguage.ArduinoCpp &&
                requiresPinModeCheck &&
                !setupOutputPins.Contains(normKey) &&
                warnedMissingPinMode.Add(normKey))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    lineNum, col, BoxCodeDiagnosticSeverity.Warning, "BC033",
                    $"Pin {token} is driven with digitalWrite() but pinMode({token}, OUTPUT) was not found in setup().",
                    $"Add 'pinMode({token}, OUTPUT);' inside void setup()."));
            }

            // BC034: Pin not present in wiredMcuPins
            if (wiredNormalized != null &&
                !wiredNormalized.Contains(normKey) &&
                warnedUnwired.Add(normKey))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    lineNum, col, BoxCodeDiagnosticSeverity.Warning, "BC034",
                    $"Pin {token} is used in firmware but is not connected in the current breadboard wiring.",
                    $"Route a jumper wire from MCU pin {token} to the breadboard component or adjust the pin number."));
            }
        }
    }

    private static void ExtractSimulatedActions(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        List<SimulatedPinAction> actions)
    {
        var symbolMap = BuildPinSymbolMap(strippedLines);
        var pyVarPinMap = new Dictionary<string, string>(StringComparer.Ordinal);
        int currentLcdRow = 0;

        if (language == BoxCodingLanguage.MicroPython)
        {
            foreach (string s in strippedLines)
            {
                Match pm = PythonVarPinBindRegex().Match(s);
                if (pm.Success)
                {
                    pyVarPinMap[pm.Groups[1].Value] = ResolvePinSymbol(pm.Groups[2].Value, symbolMap);
                }
            }
        }

        for (int idx = 0; idx < strippedLines.Length; idx++)
        {
            string stripped = strippedLines[idx];
            string raw = rawLines[idx];
            if (string.IsNullOrWhiteSpace(stripped)) continue;

            if (language == BoxCodingLanguage.ArduinoCpp)
            {
                foreach (Match m in CppDigitalWriteRegex().Matches(stripped))
                {
                    string pin = NormalizePinKey(ResolvePinSymbol(m.Groups[1].Value, symbolMap));
                    string state = m.Groups[2].Value.Trim().ToUpperInvariant();
                    bool high = state == "HIGH" || state == "1" || state == "TRUE";
                    actions.Add(new SimulatedPinAction(pin, IsOutput: true, IsPwm: false, IsDigitalHigh: high, PwmDuty: high ? 255 : 0));
                }

                foreach (Match m in CppAnalogWriteRegex().Matches(stripped))
                {
                    string pin = NormalizePinKey(ResolvePinSymbol(m.Groups[1].Value, symbolMap));
                    int duty = int.TryParse(m.Groups[2].Value.Trim(), out int v) ? Math.Clamp(v, 0, 255) : 128;
                    actions.Add(new SimulatedPinAction(pin, IsOutput: true, IsPwm: true, IsDigitalHigh: duty > 0, PwmDuty: duty));
                }

                foreach (Match m in CppToneRegex().Matches(stripped))
                {
                    string pin = NormalizePinKey(ResolvePinSymbol(m.Groups[1].Value, symbolMap));
                    int hz = int.TryParse(m.Groups[2].Value.Trim(), out int f) ? Math.Max(0, f) : 1000;
                    actions.Add(new SimulatedPinAction(pin, IsOutput: true, IsPwm: false, IsDigitalHigh: hz > 0, PwmDuty: 0, BuzzerHz: hz));
                }

                foreach (Match m in CppLcdSetCursorRegex().Matches(stripped))
                {
                    if (int.TryParse(m.Groups[2].Value.Trim(), out int row))
                        currentLcdRow = Math.Clamp(row, 0, 1);
                }

                foreach (Match m in RawLcdPrintRegex().Matches(raw))
                {
                    string text = CleanQuotedPayload(m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(text))
                    {
                        actions.Add(new SimulatedPinAction(
                            "I2C_LCD", IsOutput: true, IsPwm: false, IsDigitalHigh: true, PwmDuty: 0,
                            LcdLine1: currentLcdRow == 0 ? text : null,
                            LcdLine2: currentLcdRow == 1 ? text : null));
                    }
                }

                foreach (Match m in RawSerialPrintRegex().Matches(raw))
                {
                    string text = CleanQuotedPayload(m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(text))
                        actions.Add(new SimulatedPinAction("SERIAL", IsOutput: false, IsPwm: false, IsDigitalHigh: false, PwmDuty: 0, SerialText: text));
                }

                foreach (Match m in CppDelayRegex().Matches(stripped))
                {
                    if (int.TryParse(m.Groups[1].Value.Trim(), out int ms) && ms > 0)
                        actions.Add(new SimulatedPinAction("WAIT", IsOutput: false, IsPwm: false, IsDigitalHigh: false, PwmDuty: 0, DelayMs: ms));
                }
            }
            else
            {
                foreach (Match m in PythonPinValueRegex().Matches(stripped))
                {
                    string varName = m.Groups[1].Value;
                    string arg = m.Groups[2].Value.Trim();
                    string pin = pyVarPinMap.TryGetValue(varName, out string? mapped) ? NormalizePinKey(mapped) : varName;
                    if (varName is "trig" or "echo") continue;
                    bool high = arg == "1" || arg.Equals("True", StringComparison.OrdinalIgnoreCase);
                    actions.Add(new SimulatedPinAction(pin, IsOutput: true, IsPwm: false, IsDigitalHigh: high, PwmDuty: high ? 255 : 0));
                }

                foreach (Match m in PythonPwmDutyRegex().Matches(stripped))
                {
                    string varName = m.Groups[1].Value;
                    string pin = pyVarPinMap.TryGetValue(varName, out string? mapped) ? NormalizePinKey(mapped) : varName;
                    if (int.TryParse(m.Groups[2].Value.Trim(), out int rawDuty))
                    {
                        int duty = stripped.Contains("duty_u16", StringComparison.Ordinal)
                            ? Math.Clamp((int)Math.Round(rawDuty / 257.0), 0, 255)
                            : Math.Clamp(rawDuty, 0, 255);
                        actions.Add(new SimulatedPinAction(pin, IsOutput: true, IsPwm: true, IsDigitalHigh: duty > 0, PwmDuty: duty));
                    }
                }

                foreach (Match m in PythonBuzzerFreqRegex().Matches(stripped))
                {
                    string varName = m.Groups[1].Value;
                    string pin = pyVarPinMap.TryGetValue(varName, out string? mapped) ? NormalizePinKey(mapped) : varName;
                    if (int.TryParse(m.Groups[2].Value.Trim(), out int hz))
                        actions.Add(new SimulatedPinAction(pin, IsOutput: true, IsPwm: false, IsDigitalHigh: hz > 0, PwmDuty: 0, BuzzerHz: hz));
                }

                foreach (Match m in PythonLcdMoveToRegex().Matches(stripped))
                {
                    if (int.TryParse(m.Groups[2].Value.Trim(), out int row))
                        currentLcdRow = Math.Clamp(row, 0, 1);
                }

                foreach (Match m in RawPythonLcdPutstrRegex().Matches(raw))
                {
                    string text = CleanQuotedPayload(m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(text))
                    {
                        actions.Add(new SimulatedPinAction(
                            "I2C_LCD", IsOutput: true, IsPwm: false, IsDigitalHigh: true, PwmDuty: 0,
                            LcdLine1: currentLcdRow == 0 ? text : null,
                            LcdLine2: currentLcdRow == 1 ? text : null));
                    }
                }

                foreach (Match m in RawPythonPrintRegex().Matches(raw))
                {
                    string text = CleanQuotedPayload(m.Groups[1].Value);
                    if (!string.IsNullOrEmpty(text))
                        actions.Add(new SimulatedPinAction("SERIAL", IsOutput: false, IsPwm: false, IsDigitalHigh: false, PwmDuty: 0, SerialText: text));
                }

                foreach (Match m in PythonSleepMsRegex().Matches(stripped))
                {
                    if (int.TryParse(m.Groups[1].Value.Trim(), out int ms) && ms > 0)
                        actions.Add(new SimulatedPinAction("WAIT", IsOutput: false, IsPwm: false, IsDigitalHigh: false, PwmDuty: 0, DelayMs: ms));
                }
            }
        }
    }

    private static Dictionary<string, string> BuildPinSymbolMap(string[] strippedLines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in strippedLines)
        {
            Match defM = CppDefinePinRegex().Match(line);
            if (defM.Success)
            {
                map[defM.Groups[1].Value] = defM.Groups[2].Value.Trim();
                continue;
            }

            Match constM = CppConstPinRegex().Match(line);
            if (constM.Success)
            {
                map[constM.Groups[1].Value] = constM.Groups[2].Value.Trim();
            }
        }
        return map;
    }

    private static string ResolvePinSymbol(string rawToken, Dictionary<string, string> symbolMap)
    {
        string tok = rawToken.Trim();
        for (int depth = 0; depth < 3 && symbolMap.TryGetValue(tok, out string? mapped); depth++)
            tok = mapped;
        return tok;
    }

    private static bool IsNonPinToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return true;
        if (token.Contains('(') || token.Contains(')') || token.Contains(' ') || token.Contains('"'))
            return true;

        // Ignore unresolved symbolic variable identifiers except standard pin names (D0..D53, A0..A15, PA0.., PB0.., PC13.., IO0.., GPIO0.., numbers)
        if (char.IsDigit(token[0])) return !int.TryParse(token, out _);
        if (PinTokenPatternRegex().IsMatch(token)) return false;
        return true;
    }

    private static bool TryExtractNumericPin(string token, out int pinNum, out bool isAnalogPrefix)
    {
        pinNum = -1;
        isAnalogPrefix = false;
        string t = token.Trim();
        if (int.TryParse(t, NumberStyles.Integer, CultureInfo.InvariantCulture, out pinNum))
            return true;

        if (t.StartsWith("GPIO", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(t[4..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pinNum))
            return true;

        if (t.StartsWith("IO", StringComparison.OrdinalIgnoreCase) &&
            int.TryParse(t[2..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out pinNum))
            return true;

        if (t.Length > 1 && (t[0] == 'D' || t[0] == 'd') &&
            int.TryParse(t[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out pinNum))
            return true;

        if (t.Length > 1 && (t[0] == 'A' || t[0] == 'a') &&
            int.TryParse(t[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out pinNum))
        {
            isAnalogPrefix = true;
            return true;
        }

        return false;
    }

    private static McuPinDefinition? ResolveBoardPin(
        MicrocontrollerBoardProfile profile,
        string token,
        bool hasNum,
        int numPin,
        bool isAnalogPrefix)
    {
        McuPinDefinition? found = profile.FindPin(token);
        if (found != null) return found;

        if (hasNum && !isAnalogPrefix)
        {
            found = profile.FindPin("D" + numPin)
                ?? profile.FindPin("GPIO" + numPin)
                ?? profile.FindPin("IO" + numPin)
                ?? profile.FindPin(numPin.ToString(CultureInfo.InvariantCulture));
        }

        return found;
    }

    private static bool IsOutOfRangeForBoard(
        MicrocontrollerBoardType boardType,
        string token,
        bool hasNum,
        int numPin,
        bool isAnalogPrefix,
        McuPinDefinition? mcuPin)
    {
        switch (boardType)
        {
            case MicrocontrollerBoardType.ArduinoUnoR3R4:
                if (isAnalogPrefix) return numPin < 0 || numPin > 5;
                if (hasNum) return numPin < 0 || numPin > 19;
                return mcuPin == null;

            case MicrocontrollerBoardType.ArduinoMega2560:
                if (isAnalogPrefix) return numPin < 0 || numPin > 15;
                if (hasNum) return numPin < 0 || numPin > 53;
                return mcuPin == null;

            case MicrocontrollerBoardType.Esp32DevKitV1:
                if (hasNum && !isAnalogPrefix) return numPin < 0 || numPin > 39 || mcuPin == null;
                return mcuPin == null;

            case MicrocontrollerBoardType.WemosEsp32S2Mini:
                if (hasNum && !isAnalogPrefix) return numPin < 0 || numPin > 40 || mcuPin == null;
                return mcuPin == null;

            case MicrocontrollerBoardType.Stm32BluePillF103C8:
                if (mcuPin != null) return false;
                if (hasNum && !isAnalogPrefix) return numPin < 0 || numPin > 47;
                return true;

            default:
                return false;
        }
    }

    private static string NormalizePinKey(string raw)
    {
        string s = raw.Trim();
        if (s.StartsWith("GPIO ", StringComparison.OrdinalIgnoreCase))
            s = s[5..].Trim();
        else if (s.StartsWith("GPIO", StringComparison.OrdinalIgnoreCase))
            s = s[4..].Trim();
        else if (s.StartsWith("IO", StringComparison.OrdinalIgnoreCase) && s.Length > 2 && char.IsDigit(s[2]))
            s = s[2..].Trim();
        else if ((s.StartsWith('D') || s.StartsWith('d')) && s.Length > 1 && char.IsDigit(s[1]))
            s = s[1..];
        return s.ToUpperInvariant();
    }

    private static string CleanQuotedPayload(string expr)
    {
        string s = expr.Trim();
        if (s.StartsWith("F(", StringComparison.Ordinal) && s.EndsWith(')'))
            s = s[2..^1].Trim();
        if ((s.StartsWith("f\"") || s.StartsWith("f'")) && s.Length >= 3)
            s = s[1..];
        if ((s.StartsWith('"') && s.EndsWith('"')) || (s.StartsWith('\'') && s.EndsWith('\'')))
            s = s[1..^1];
        return s.Replace("\\\"", "\"");
    }

    private static int CountCharDelta(string s, char open, char close)
    {
        int d = 0;
        foreach (char c in s)
        {
            if (c == open) d++;
            else if (c == close) d--;
        }
        return d;
    }

    private static int GetVisualIndentWidth(string rawLine)
    {
        int w = 0;
        foreach (char c in rawLine)
        {
            if (c == ' ') w++;
            else if (c == '\t') w += 4;
            else break;
        }
        return w;
    }

    [GeneratedRegex(@"\bvoid\s+setup\s*\(")]
    private static partial Regex SetupDeclRegex();

    [GeneratedRegex(@"\bvoid\s+loop\s*\(")]
    private static partial Regex LoopDeclRegex();

    [GeneratedRegex(@"\b[A-Za-z_][A-Za-z0-9_]*\b")]
    private static partial Regex IdentifierTokenRegex();

    [GeneratedRegex(@"^(if|else\s+if|for|while|switch|catch)\s*\(")]
    private static partial Regex CppControlOrFuncHeaderRegex();

    [GeneratedRegex(@"^(?:const\s+)?(?:void|int|long|float|double|bool|char|String|uint\d+_t|int\d+_t|unsigned(?:\s+\w+)?)\s+[A-Za-z_]\w*\s*\([^;]*\)\s*$")]
    private static partial Regex CppFunctionSignatureRegex();

    [GeneratedRegex(@"\b(?:void\s+(?:setup|loop)\s*\(|(?:digitalWrite|digitalRead|pinMode|analogWrite|analogRead)\s*\(|Serial\.(?:begin|println|print)\s*\(|(?:int|float|double|bool)\s+[A-Za-z_]\w*\s*=)")]
    private static partial Regex CppInPythonRegex();

    [GeneratedRegex(@"^(?:def|class|if|elif|else|while|for|try|except|finally|with)\b")]
    private static partial Regex PythonSuiteStartRegex();

    [GeneratedRegex(@"^(?:#\s*define)\s+([A-Za-z_]\w*)\s+([A-Za-z0-9_]+)")]
    private static partial Regex CppDefinePinRegex();

    [GeneratedRegex(@"\b(?:const\s+)?(?:int|uint8_t|byte)\s+([A-Za-z_]\w*)\s*=\s*([A-Za-z0-9_]+)\s*;")]
    private static partial Regex CppConstPinRegex();

    [GeneratedRegex(@"\bpinMode\s*\(\s*([^,\s)]+)\s*,\s*([^)]+)\)")]
    private static partial Regex CppPinModeRegex();

    [GeneratedRegex(@"\b\w+\.attach\s*\(\s*([^,\s)]+)")]
    private static partial Regex CppServoAttachRegex();

    [GeneratedRegex(@"\bdigitalWrite\s*\(\s*([^,\s)]+)\s*,\s*([^)]+)\)")]
    private static partial Regex CppDigitalWriteRegex();

    [GeneratedRegex(@"\banalogWrite\s*\(\s*([^,\s)]+)\s*,\s*([^)]+)\)")]
    private static partial Regex CppAnalogWriteRegex();

    [GeneratedRegex(@"\b(?:analogWrite|tone)\s*\(\s*([^,\s)]+)\s*,")]
    private static partial Regex CppAnalogWriteOrToneRegex();

    [GeneratedRegex(@"\btone\s*\(\s*([^,\s)]+)\s*,\s*([^,\s)]+)")]
    private static partial Regex CppToneRegex();

    [GeneratedRegex(@"\b(?:digitalRead|analogRead)\s*\(\s*([^,\s)]+)\s*\)")]
    private static partial Regex CppReadPinRegex();

    [GeneratedRegex(@"\bPin\s*\(\s*([^,\s)]+)\s*(?:,\s*([^)]+))?\)")]
    private static partial Regex PythonPinCtorRegex();

    [GeneratedRegex(@"^([A-Za-z_]\w*)\s*=\s*(?:ADC|PWM)?\s*\(?\s*Pin\s*\(\s*([^,\s)]+)")]
    private static partial Regex PythonVarPinBindRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\.value\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex PythonPinValueRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\.duty(?:_u16)?\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex PythonPwmDutyRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\.freq\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex PythonBuzzerFreqRegex();

    [GeneratedRegex(@"\blcd\.setCursor\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)")]
    private static partial Regex CppLcdSetCursorRegex();

    [GeneratedRegex(@"\blcd\.move_to\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)")]
    private static partial Regex PythonLcdMoveToRegex();

    [GeneratedRegex(@"\bdelay\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex CppDelayRegex();

    [GeneratedRegex(@"\btime\.sleep_ms\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex PythonSleepMsRegex();

    [GeneratedRegex(@"(?:lcd|oledDisplay)\.print(?:ln)?\s*\(\s*(F\(""(?:[^""\\]|\\.)*""\)|""(?:[^""\\]|\\.)*"")\s*(?:,\s*\d+\s*)?\)")]
    private static partial Regex RawLcdPrintRegex();

    [GeneratedRegex(@"\bSerial\.print(?:ln)?\s*\(\s*(F\(""(?:[^""\\]|\\.)*""\)|""(?:[^""\\]|\\.)*"")\s*\)")]
    private static partial Regex RawSerialPrintRegex();

    [GeneratedRegex(@"\blcd\.putstr\s*\(\s*(f?""(?:[^""\\]|\\.)*""|f?'(?:[^'\\]|\\.)*')\s*\)")]
    private static partial Regex RawPythonLcdPutstrRegex();

    [GeneratedRegex(@"\bprint\s*\(\s*(f?""(?:[^""\\]|\\.)*""|f?'(?:[^'\\]|\\.)*')")]
    private static partial Regex RawPythonPrintRegex();

    [GeneratedRegex(@"^(?:D\d+|A\d+|P[ABC]\d+|GPIO\s*\d+|IO\d+|\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex PinTokenPatternRegex();
}
