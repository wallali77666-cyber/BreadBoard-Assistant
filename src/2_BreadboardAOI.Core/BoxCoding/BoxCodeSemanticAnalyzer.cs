// ============================================================================
// FILE: src/2_BreadboardAOI.Core/BoxCoding/BoxCodeSemanticAnalyzer.cs
// PURPOSE: VS Code-style semantic & logic error detection for Arduino C++ and
//          MicroPython firmware (PWM support, assignment-in-condition,
//          division by zero, unreachable code, tight WDT loops, rapid un-delayed
//          writes, ISR blocking calls, Serial.begin omissions, undeclared/unused/
//          uninitialized variables, arg count mismatches, pin/peripheral conflicts)
//          plus one-click Quick Fix actions.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

internal static partial class BoxCodeSemanticAnalyzer
{
    private static readonly DevBoardAndModuleRuleEngine BoardEngine = new();
    private static readonly HashSet<string> CppBuiltInIdentifiers = new(StringComparer.Ordinal)
    {
        "if", "else", "for", "while", "do", "switch", "case", "default", "break", "continue", "return",
        "void", "int", "long", "short", "float", "double", "bool", "boolean", "char", "byte", "word",
        "unsigned", "signed", "const", "static", "volatile", "inline", "extern", "struct", "class", "enum",
        "typedef", "using", "namespace", "true", "false", "NULL", "nullptr", "sizeof", "auto", "String",
        "uint8_t", "uint16_t", "uint32_t", "int8_t", "int16_t", "int32_t", "size_t",
        "HIGH", "LOW", "INPUT", "OUTPUT", "INPUT_PULLUP", "INPUT_PULLDOWN", "LED_BUILTIN",
        "RISING", "FALLING", "CHANGE", "MSBFIRST", "LSBFIRST", "DEC", "HEX", "OCT", "BIN",
        "A0", "A1", "A2", "A3", "A4", "A5", "A6", "A7", "A8", "A9", "A10", "A11", "A12", "A13", "A14", "A15",
        "D0", "D1", "D2", "D3", "D4", "D5", "D6", "D7", "D8", "D9", "D10", "D11", "D12", "D13",
        "IO0", "IO1", "IO2", "IO3", "IO4", "IO5", "IO6", "IO7", "IO8", "IO9", "IO10", "IO11", "IO12",
        "IO13", "IO14", "IO15", "IO16", "IO17", "IO18", "IO21", "IO33", "IO34", "IO35", "IO36", "IO37", "IO38", "IO39", "IO40",
        "PA0", "PA1", "PA2", "PA3", "PA4", "PA5", "PA6", "PA7", "PA8", "PA9", "PA10", "PA11", "PA12", "PA15",
        "PB0", "PB1", "PB3", "PB4", "PB5", "PB6", "PB7", "PB8", "PB9", "PB10", "PB11", "PB12", "PB13", "PB14", "PB15",
        "PC13", "PC14", "PC15", "SDA", "SCL", "SS", "MOSI", "MISO", "SCK",
        "pinMode", "digitalWrite", "digitalRead", "analogWrite", "analogRead", "analogReference",
        "tone", "noTone", "pulseIn", "pulseInLong", "shiftOut", "shiftIn",
        "delay", "delayMicroseconds", "millis", "micros", "yield",
        "map", "constrain", "min", "max", "abs", "pow", "sqrt", "sq", "sin", "cos", "tan",
        "random", "randomSeed", "bitRead", "bitSet", "bitClear", "bitWrite", "bit",
        "attachInterrupt", "detachInterrupt", "digitalPinToInterrupt", "interrupts", "noInterrupts",
        "Serial", "Serial1", "Serial2", "SerialUSB", "Wire", "SPI", "WiFi", "BLE", "EEPROM", "SD", "F",
        "LiquidCrystal_I2C", "Servo", "DHT", "DHT11", "DHT22", "Adafruit_SSD1306", "CRGB", "FastLED",
        "SSD1306_SWITCHCAPVCC", "SSD1306_EXTERNALVCC", "SSD1306_WHITE", "SSD1306_BLACK", "WHITE", "BLACK",
        "PI", "HALF_PI", "TWO_PI", "DEG_TO_RAD", "RAD_TO_DEG", "isnan", "isinf",
        "printf", "sprintf", "snprintf", "dtostrf", "atoi", "atof", "atol", "strlen", "strcmp",
        "lcd", "servo", "servoMotor", "dht", "oledDisplay", "display", "rtc", "strip", "pixels", "sonar", "motor", "stepper",
        "setup", "loop"
    };

    private static readonly HashSet<string> PythonBuiltInIdentifiers = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class",
        "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global",
        "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield",
        "Exception", "BaseException", "ValueError", "RuntimeError", "OSError", "TypeError",
        "KeyError", "IndexError", "ZeroDivisionError", "AttributeError", "ImportError",
        "NameError", "StopIteration", "KeyboardInterrupt", "MemoryError", "NotImplementedError",
        "print", "len", "range", "int", "float", "str", "bool", "bytes", "bytearray",
        "list", "dict", "set", "tuple", "abs", "min", "max", "sum", "round", "pow",
        "enumerate", "zip", "map", "filter", "sorted", "reversed", "any", "all",
        "isinstance", "type", "input", "open", "chr", "ord", "hex", "bin", "sleep",
        "Pin", "PWM", "ADC", "I2C", "SoftI2C", "SPI", "UART", "Timer", "RTC", "machine", "time", "utime",
        "math", "random", "json", "network", "neopixel", "dht", "ssd1306", "oled", "lcd", "servo", "motor",
        "buzzer", "led", "sensor", "btn", "button", "pot"
    };

    private static readonly Dictionary<string, (int MinArgs, int MaxArgs)> CoreApiArity = new(StringComparer.Ordinal)
    {
        ["pinMode"] = (2, 2),
        ["digitalWrite"] = (2, 2),
        ["digitalRead"] = (1, 1),
        ["analogWrite"] = (2, 2),
        ["analogRead"] = (1, 1),
        ["delay"] = (1, 1),
        ["delayMicroseconds"] = (1, 1),
        ["map"] = (5, 5),
        ["constrain"] = (3, 3),
        ["tone"] = (2, 3),
        ["noTone"] = (1, 1),
        ["attachInterrupt"] = (3, 3)
    };

    public static void AnalyzeSemanticsAndLogic(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        List<BoxCodeDiagnostic> diagnostics)
    {
        CheckConditionAssignments(rawLines, strippedLines, language, diagnostics);
        CheckDivisionByZero(strippedLines, language, diagnostics);
        CheckRapidUnDelayedPinWrites(strippedLines, language, diagnostics);
        CheckInfiniteLoopsWithoutYield(rawLines, strippedLines, language, diagnostics);
        CheckUnreachableCode(strippedLines, language, diagnostics);
        CheckInterruptServiceRoutines(strippedLines, language, diagnostics);
        CheckNonPwmAnalogWrite(strippedLines, language, boardType, diagnostics);
        CheckBuiltinArgumentCounts(strippedLines, language, diagnostics);
        CheckSerialBeginAndPinRoleConflicts(rawLines, strippedLines, language, boardType, diagnostics);
        CheckIdentifiersAndVariableLifecycle(rawLines, strippedLines, language, diagnostics);
    }

    private static void CheckConditionAssignments(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            Match condMatch = language == BoxCodingLanguage.ArduinoCpp
                ? CppConditionRegex().Match(line)
                : PythonConditionRegex().Match(line);
            if (!condMatch.Success)
                continue;

            string condExpr = condMatch.Groups[1].Value;
            Match assignMatch = SingleAssignEqualsRegex().Match(condExpr);
            if (!assignMatch.Success)
                continue;

            string rawLine = rawLines[i];
            int lineNum = i + 1;
            int eqOffsetInAssign = Math.Max(0, assignMatch.Value.IndexOf('='));
            int eqLocalIdx = condMatch.Groups[1].Index + assignMatch.Index + eqOffsetInAssign;
            string replacedRaw = SingleAssignEqualsInLine(rawLine, language);

            diagnostics.Add(new BoxCodeDiagnostic(
                lineNum,
                Math.Max(1, eqLocalIdx + 1),
                BoxCodeDiagnosticSeverity.Warning,
                "BC040",
                "Assignment '=' used inside conditional expression instead of comparison '=='.",
                "Use '==' to compare values without mutating the variable.",
                new BoxCodeQuickFix(
                    "Replace '=' with '==' in condition",
                    lineNum,
                    replacedRaw)));
        }
    }

    private static string SingleAssignEqualsInLine(string rawLine, BoxCodingLanguage language)
    {
        Match condMatch = language == BoxCodingLanguage.ArduinoCpp
            ? CppConditionRegex().Match(rawLine)
            : PythonConditionRegex().Match(rawLine);
        if (!condMatch.Success)
            return rawLine;

        string condExpr = condMatch.Groups[1].Value;
        Match assignMatch = SingleAssignEqualsRegex().Match(condExpr);
        if (!assignMatch.Success)
            return rawLine;

        int absIndex = condMatch.Groups[1].Index + assignMatch.Index + assignMatch.Value.IndexOf('=');
        return string.Concat(rawLine.AsSpan(0, absIndex), "==", rawLine.AsSpan(absIndex + 1));
    }

    private static void CheckDivisionByZero(
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            foreach (Match m in DivByZeroRegex().Matches(line))
            {
                if (language == BoxCodingLanguage.ArduinoCpp && line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                    continue;

                diagnostics.Add(new BoxCodeDiagnostic(
                    i + 1,
                    m.Index + 1,
                    BoxCodeDiagnosticSeverity.Error,
                    "BC041",
                    "Division or modulo by zero literal causes undefined behavior / hardware fault.",
                    "Guard the denominator or use a non-zero divisor."));
            }
        }
    }

    private static void CheckUnreachableCode(
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (language == BoxCodingLanguage.ArduinoCpp)
        {
            var terminatedAtDepth = new Dictionary<int, bool>();
            int braceDepth = 0;

            for (int i = 0; i < strippedLines.Length; i++)
            {
                string trimmed = strippedLines[i].Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                int leadingCloses = 0;
                while (leadingCloses < trimmed.Length && trimmed[leadingCloses] == '}')
                    leadingCloses++;

                for (int c = 0; c < leadingCloses; c++)
                {
                    terminatedAtDepth.Remove(braceDepth);
                    braceDepth = Math.Max(0, braceDepth - 1);
                }

                string remainder = trimmed[leadingCloses..].Trim();
                if (!string.IsNullOrEmpty(remainder) &&
                    !remainder.StartsWith("else", StringComparison.Ordinal) &&
                    !remainder.StartsWith("case ", StringComparison.Ordinal) &&
                    !remainder.StartsWith("default:", StringComparison.Ordinal) &&
                    !remainder.StartsWith('#') &&
                    braceDepth > 0 &&
                    terminatedAtDepth.GetValueOrDefault(braceDepth))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC042",
                        "Unreachable statement detected after return/break/continue in the same block.",
                        "Remove or move this statement before the terminating jump.",
                        new BoxCodeQuickFix("Remove unreachable line", i + 1, string.Empty)));
                    terminatedAtDepth[braceDepth] = false;
                }

                if (CppJumpStatementRegex().IsMatch(remainder))
                    terminatedAtDepth[braceDepth] = true;

                int openBraces = CountCharOutsideQuotes(remainder, '{');
                int closeBraces = CountCharOutsideQuotes(remainder, '}');
                for (int b = 0; b < openBraces; b++)
                    braceDepth++;
                for (int b = 0; b < closeBraces; b++)
                {
                    terminatedAtDepth.Remove(braceDepth);
                    braceDepth = Math.Max(0, braceDepth - 1);
                }
            }
        }
        else
        {
            int terminatedIndent = -1;
            for (int i = 0; i < strippedLines.Length; i++)
            {
                string line = strippedLines[i];
                string trimmed = line.Trim();
                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                int indent = CountLeadingSpaces(line);
                if (terminatedIndent >= 0)
                {
                    if (indent < terminatedIndent ||
                        (indent == terminatedIndent && (trimmed.StartsWith("elif ", StringComparison.Ordinal) ||
                                                        trimmed.StartsWith("else:", StringComparison.Ordinal) ||
                                                        trimmed.StartsWith("except", StringComparison.Ordinal) ||
                                                        trimmed.StartsWith("finally:", StringComparison.Ordinal))))
                    {
                        terminatedIndent = -1;
                    }
                    else if (indent == terminatedIndent)
                    {
                        diagnostics.Add(new BoxCodeDiagnostic(
                            i + 1,
                            indent + 1,
                            BoxCodeDiagnosticSeverity.Warning,
                            "BC042",
                            "Unreachable Python statement after return/break/continue.",
                            "Remove or unindent this unreachable line.",
                            new BoxCodeQuickFix("Remove unreachable line", i + 1, string.Empty)));
                        terminatedIndent = -1;
                    }
                }

                if (PythonJumpStatementRegex().IsMatch(trimmed))
                    terminatedIndent = indent;
            }
        }
    }

    private static void CheckInfiniteLoopsWithoutYield(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            bool isInfiniteHeader = language == BoxCodingLanguage.ArduinoCpp
                ? CppInfiniteLoopRegex().IsMatch(line)
                : PythonInfiniteLoopRegex().IsMatch(line);

            if (!isInfiniteHeader)
                continue;

            if (language == BoxCodingLanguage.ArduinoCpp)
            {
                int depth = CountCharOutsideQuotes(line, '{') - CountCharOutsideQuotes(line, '}');
                int endLine = i;
                bool hasPaceOrExit = LoopBodyHasPaceOrExit(line, isPython: false);

                int j = i + 1;
                if (depth <= 0 && j < strippedLines.Length && strippedLines[j].Contains('{'))
                {
                    depth += CountCharOutsideQuotes(strippedLines[j], '{');
                    endLine = j;
                    j++;
                }

                while (j < strippedLines.Length && depth > 0)
                {
                    string bodyLine = strippedLines[j];
                    if (LoopBodyHasPaceOrExit(bodyLine, isPython: false))
                        hasPaceOrExit = true;
                    depth += CountCharOutsideQuotes(bodyLine, '{') - CountCharOutsideQuotes(bodyLine, '}');
                    endLine = j;
                    j++;
                }

                bool singleLineBrace = line.Contains('{') && line.Contains('}');
                bool singleLineSemicolon = !line.Contains('{') && line.TrimEnd().EndsWith(';');
                if (!hasPaceOrExit && (endLine > i || singleLineBrace || singleLineSemicolon))
                {
                    BoxCodeQuickFix quickFix = singleLineBrace
                        ? new BoxCodeQuickFix(
                            "Insert delay(10); inside loop",
                            i + 1,
                            Regex.Replace(rawLines[i], @"\{\s*\}", "{ delay(10); }"),
                            InsertLineInsteadOfReplace: false)
                        : singleLineSemicolon
                            ? new BoxCodeQuickFix(
                                "Replace ';' with { delay(10); }",
                                i + 1,
                                Regex.Replace(rawLines[i], @";\s*(//.*)?$", " { delay(10); } $1").TrimEnd(),
                                InsertLineInsteadOfReplace: false)
                            : new BoxCodeQuickFix(
                                "Insert delay(10); inside loop",
                                i + 1,
                                "    delay(10);",
                                InsertLineInsteadOfReplace: true,
                                InsertAtLine: i + 2);

                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC043",
                        "Infinite loop runs continuously without delay(), yield(), or break (watchdog reset risk).",
                        "Add delay(10); or yield(); inside the loop body.",
                        quickFix));
                }
            }
            else
            {
                int baseIndent = CountLeadingSpaces(line);
                bool hasBody = false;
                bool hasPaceOrExit = false;

                for (int j = i + 1; j < strippedLines.Length; j++)
                {
                    string body = strippedLines[j];
                    if (string.IsNullOrWhiteSpace(body))
                        continue;

                    int indent = CountLeadingSpaces(body);
                    if (indent <= baseIndent)
                        break;

                    hasBody = true;
                    if (LoopBodyHasPaceOrExit(body, isPython: true))
                        hasPaceOrExit = true;
                }

                if (hasBody && !hasPaceOrExit)
                {
                    string bodyIndent = new string(' ', baseIndent + 4);
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        baseIndent + 1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC043",
                        "Infinite 'while True:' loop has no sleep(), break, or return (CPU lockup / WDT reset risk).",
                        "Add time.sleep_ms(10) inside the loop body.",
                        new BoxCodeQuickFix(
                            "Insert time.sleep_ms(10) in while loop",
                            i + 1,
                            $"{bodyIndent}time.sleep_ms(10)",
                            InsertLineInsteadOfReplace: true,
                            InsertAtLine: i + 2)));
                }
            }
        }
    }

    private static bool LoopBodyHasPaceOrExit(string line, bool isPython)
    {
        if (string.IsNullOrWhiteSpace(line))
            return false;

        if (isPython)
        {
            return line.Contains("sleep", StringComparison.Ordinal) ||
                   Regex.IsMatch(line, @"\b(break|return)\b");
        }

        return line.Contains("delay", StringComparison.Ordinal) ||
               line.Contains("yield", StringComparison.Ordinal) ||
               Regex.IsMatch(line, @"\b(break|return)\b");
    }

    private static void CheckRapidUnDelayedPinWrites(
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        string? prevPin = null;
        string? prevState = null;
        int prevLine = -1;

        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Contains("delay", StringComparison.Ordinal) ||
                line.Contains("sleep", StringComparison.Ordinal))
            {
                prevPin = null;
                prevState = null;
                continue;
            }

            if (language == BoxCodingLanguage.ArduinoCpp)
            {
                Match m = SingleDigitalWriteRegex().Match(line);
                if (m.Success)
                {
                    string pin = BoxCodeErrorAnalyzer.NormalizePinKey(m.Groups[1].Value);
                    string state = m.Groups[2].Value.Trim().ToUpperInvariant();
                    bool high = state is "HIGH" or "1" or "TRUE";
                    bool low = state is "LOW" or "0" or "FALSE";
                    string normalizedState = high ? "H" : low ? "L" : state;

                    if (prevPin == pin && prevState is not null && prevState != normalizedState && (i + 1) - prevLine <= 2)
                    {
                        diagnostics.Add(new BoxCodeDiagnostic(
                            i + 1,
                            1,
                            BoxCodeDiagnosticSeverity.Warning,
                            "BC044",
                            $"Pin {m.Groups[1].Value.Trim()} is toggled back immediately with no delay() between writes (sub-microsecond pulse).",
                            "Insert delay(250); between HIGH and LOW writes so the output pulse is visible.",
                            new BoxCodeQuickFix(
                                "Insert delay(250); before second write",
                                i + 1,
                                "    delay(250);",
                                InsertLineInsteadOfReplace: true,
                                InsertAtLine: i + 1)));
                        prevPin = null;
                        prevState = null;
                        continue;
                    }

                    prevPin = pin;
                    prevState = normalizedState;
                    prevLine = i + 1;
                    continue;
                }
            }
            else
            {
                Match valMatch = SinglePythonPinValueRegex().Match(line);
                if (valMatch.Success)
                {
                    string varName = valMatch.Groups[1].Value;
                    string arg = valMatch.Groups[2].Value.Trim();
                    string normalizedState = (arg == "1" || arg.Equals("True", StringComparison.OrdinalIgnoreCase)) ? "H" : "L";

                    if (prevPin == varName && prevState is not null && prevState != normalizedState && (i + 1) - prevLine <= 2)
                    {
                        int indent = CountLeadingSpaces(strippedLines[i]);
                        diagnostics.Add(new BoxCodeDiagnostic(
                            i + 1,
                            1,
                            BoxCodeDiagnosticSeverity.Warning,
                            "BC044",
                            $"Pin object '{varName}' toggles immediately with no sleep() between writes.",
                            "Insert time.sleep_ms(250) between state changes.",
                            new BoxCodeQuickFix(
                                "Insert time.sleep_ms(250) before second write",
                                i + 1,
                                $"{new string(' ', indent)}time.sleep_ms(250)",
                                InsertLineInsteadOfReplace: true,
                                InsertAtLine: i + 1)));
                        prevPin = null;
                        prevState = null;
                        continue;
                    }

                    prevPin = varName;
                    prevState = normalizedState;
                    prevLine = i + 1;
                    continue;
                }
            }

            if (line.Contains('}') || line.StartsWith("if", StringComparison.Ordinal) || line.StartsWith("else", StringComparison.Ordinal))
            {
                prevPin = null;
                prevState = null;
            }
        }
    }

    private static void CheckInterruptServiceRoutines(
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (language != BoxCodingLanguage.ArduinoCpp)
            return;

        var isrNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string line in strippedLines)
        {
            Match m = AttachInterruptRegex().Match(line);
            if (m.Success)
                isrNames.Add(m.Groups[1].Value.Trim());
        }

        if (isrNames.Count == 0)
            return;

        string? currentIsr = null;
        int depth = 0;

        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (currentIsr is null)
            {
                Match fnMatch = CppAnyFuncHeaderRegex().Match(line.Trim());
                if (fnMatch.Success && isrNames.Contains(fnMatch.Groups[1].Value))
                {
                    currentIsr = fnMatch.Groups[1].Value;
                    depth = CountCharOutsideQuotes(line, '{') - CountCharOutsideQuotes(line, '}');
                }
                continue;
            }

            if (IsrBlockingCallRegex().IsMatch(line))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    i + 1,
                    1,
                    BoxCodeDiagnosticSeverity.Error,
                    "BC045",
                    $"Blocking call (delay / Serial) inside interrupt handler '{currentIsr}()' causes timer freeze or ESP32 WDT crash.",
                    "Set a 'volatile bool' flag inside the ISR and handle Serial/delay inside loop()."));
            }

            depth += CountCharOutsideQuotes(line, '{') - CountCharOutsideQuotes(line, '}');
            if (depth <= 0 && line.Contains('}'))
                currentIsr = null;
        }
    }

    private static void CheckNonPwmAnalogWrite(
        string[] strippedLines,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (language != BoxCodingLanguage.ArduinoCpp || boardType == MicrocontrollerBoardType.None)
            return;

        MicrocontrollerBoardProfile profile = BoardEngine.GetBoardProfile(boardType);
        Dictionary<string, string> pinSymbols = CollectCppPinConstants(strippedLines);

        for (int i = 0; i < strippedLines.Length; i++)
        {
            foreach (Match m in AnalogWriteCallRegex().Matches(strippedLines[i]))
            {
                string rawArg = m.Groups[1].Value.Trim();
                string token = pinSymbols.TryGetValue(rawArg, out string? mapped) ? mapped : rawArg;

                if (!TrySimplePinToken(token, out bool hasNum, out int numPin, out bool isAnalogPrefix))
                    continue;

                McuPinDefinition? mcuPin = BoxCodeErrorAnalyzer.ResolveBoardPin(profile, token, hasNum, numPin, isAnalogPrefix);
                if (mcuPin is { SupportsPwm: false })
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        m.Index + 1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC035",
                        $"Pin '{mcuPin.DisplayLabel}' on {profile.DisplayName} does not support hardware PWM for analogWrite().",
                        boardType == MicrocontrollerBoardType.ArduinoUnoR3R4
                            ? "Move analogWrite() to a hardware PWM pin (~3, ~5, ~6, ~9, ~10, or ~11)."
                            : "Select a board pin flagged with PWM capability."));
                }
            }
        }
    }

    private static void CheckBuiltinArgumentCounts(
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (language != BoxCodingLanguage.ArduinoCpp)
            return;

        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i];
            if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("#", StringComparison.Ordinal))
                continue;

            foreach (Match m in FuncCallStartRegex().Matches(line))
            {
                string fn = m.Groups[1].Value;
                if (!CoreApiArity.TryGetValue(fn, out var arity))
                    continue;

                int openParenIdx = m.Index + m.Length - 1;
                if (!TryExtractBalancedParens(line, openParenIdx, out string argText))
                    continue;

                int argCount = CountArguments(argText);
                if (argCount < arity.MinArgs || argCount > arity.MaxArgs)
                {
                    string expected = arity.MinArgs == arity.MaxArgs
                        ? arity.MinArgs.ToString(CultureInfo.InvariantCulture)
                        : $"{arity.MinArgs}-{arity.MaxArgs}";

                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        m.Index + 1,
                        BoxCodeDiagnosticSeverity.Error,
                        "BC050",
                        $"Function '{fn}()' expects {expected} argument(s), but {argCount} provided.",
                        $"Adjust the arguments passed to {fn}(...)."));
                }
                else if (argCount > 0 && HasStringLiteralArgument(argText))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        m.Index + 1,
                        BoxCodeDiagnosticSeverity.Error,
                        "BC052",
                        $"Argument type mismatch: string literal passed to '{fn}()' which expects numeric/constant values.",
                        $"Remove quotes and pass a numeric pin, state (HIGH/LOW), or integer expression to {fn}(...)."));
                }
            }

            if (line.Contains("Serial.begin(\"\")", StringComparison.Ordinal))
            {
                int sbCol = line.IndexOf("Serial.begin(", StringComparison.Ordinal) + 1;
                diagnostics.Add(new BoxCodeDiagnostic(
                    i + 1,
                    Math.Max(1, sbCol),
                    BoxCodeDiagnosticSeverity.Error,
                    "BC052",
                    "Argument type mismatch: Serial.begin() expects a numeric baud rate (e.g. 115200), not a string.",
                    "Pass an unquoted integer baud rate such as Serial.begin(115200);."));
            }
        }
    }

    private static bool HasStringLiteralArgument(string argText)
    {
        int parenDepth = 0;
        var current = new StringBuilder();
        for (int idx = 0; idx < argText.Length; idx++)
        {
            char c = argText[idx];
            if (c == '(') parenDepth++;
            else if (c == ')') parenDepth = Math.Max(0, parenDepth - 1);

            if (c == ',' && parenDepth == 0)
            {
                if (current.ToString().Trim() == "\"\"")
                    return true;
                current.Clear();
                continue;
            }
            current.Append(c);
        }
        return current.ToString().Trim() == "\"\"";
    }

    private static void CheckSerialBeginAndPinRoleConflicts(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (language != BoxCodingLanguage.ArduinoCpp)
            return;

        bool hasSerialUse = false;
        int firstSerialUseLine = -1;
        bool hasSerialBegin = false;
        bool hasWireBegin = false;

        Dictionary<string, string> pinSymbols = CollectCppPinConstants(strippedLines);
        var inputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var conflictReported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i];
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (line.Contains("Serial.begin(", StringComparison.Ordinal))
                hasSerialBegin = true;
            if (line.Contains("Wire.begin(", StringComparison.Ordinal))
                hasWireBegin = true;

            if (!hasSerialUse && SerialUsageWithoutBeginRegex().IsMatch(line))
            {
                hasSerialUse = true;
                firstSerialUseLine = i + 1;
            }

            foreach (Match m in PinModeModeRegex().Matches(line))
            {
                string rawPin = m.Groups[1].Value.Trim();
                string token = pinSymbols.TryGetValue(rawPin, out string? mapped) ? mapped : rawPin;
                string norm = BoxCodeErrorAnalyzer.NormalizePinKey(token);
                string mode = m.Groups[2].Value.Trim().ToUpperInvariant();
                if (mode.Contains("INPUT", StringComparison.Ordinal))
                    inputPins.Add(norm);
                else if (mode.Contains("OUTPUT", StringComparison.Ordinal))
                    outputPins.Add(norm);
            }

            foreach (Match m in AnalogReadPinRegex().Matches(line))
            {
                string rawPin = m.Groups[1].Value.Trim();
                string token = pinSymbols.TryGetValue(rawPin, out string? mapped) ? mapped : rawPin;
                string norm = BoxCodeErrorAnalyzer.NormalizePinKey(token);
                inputPins.Add(norm);
            }

            foreach (Match m in SingleDigitalWriteRegex().Matches(line))
            {
                string rawPin = m.Groups[1].Value.Trim();
                string token = pinSymbols.TryGetValue(rawPin, out string? mapped) ? mapped : rawPin;
                string norm = BoxCodeErrorAnalyzer.NormalizePinKey(token);

                if (inputPins.Contains(norm) && !outputPins.Contains(norm) && conflictReported.Add(norm))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC051",
                        $"Pin {token} is configured or read as an INPUT/analog sensor, but also written with digitalWrite().",
                        "Separate input and output pins or call pinMode(..., OUTPUT) intentionally."));
                }

                if (hasWireBegin && IsDefaultI2cPin(boardType, norm) && conflictReported.Add("I2C_" + norm))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC051",
                        $"Pin {token} is used by hardware I2C (Wire.begin) while also toggled by digitalWrite().",
                        "Move GPIO digitalWrite() off the I2C bus SDA/SCL pins."));
                }
            }
        }

        if (hasSerialUse && !hasSerialBegin && firstSerialUseLine > 0)
        {
            int insertLine = BoxCodeErrorAnalyzer.FindSetupInsertionLine(strippedLines);
            diagnostics.Add(new BoxCodeDiagnostic(
                firstSerialUseLine,
                1,
                BoxCodeDiagnosticSeverity.Warning,
                "BC046",
                "Serial.print()/read() used without calling Serial.begin(baud) in setup().",
                "Add 'Serial.begin(115200);' inside void setup().",
                new BoxCodeQuickFix(
                    "Insert Serial.begin(115200); in setup()",
                    firstSerialUseLine,
                    "    Serial.begin(115200);",
                    InsertLineInsteadOfReplace: true,
                    InsertAtLine: insertLine)));
        }
    }

    private static bool IsDefaultI2cPin(MicrocontrollerBoardType board, string normPin) => board switch
    {
        MicrocontrollerBoardType.ArduinoUnoR3R4 => normPin is "A4" or "A5" or "SDA" or "SCL" or "18" or "19",
        MicrocontrollerBoardType.ArduinoMega2560 => normPin is "20" or "21" or "SDA" or "SCL",
        MicrocontrollerBoardType.Esp32DevKitV1 => normPin is "21" or "22" or "SDA" or "SCL",
        MicrocontrollerBoardType.WemosEsp32S2Mini => normPin is "8" or "9" or "SDA" or "SCL",
        MicrocontrollerBoardType.Stm32BluePillF103C8 => normPin is "PB6" or "PB7" or "B6" or "B7" or "SDA" or "SCL",
        _ => false
    };

    private static void CheckIdentifiersAndVariableLifecycle(
        string[] rawLines,
        string[] strippedLines,
        BoxCodingLanguage language,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (language == BoxCodingLanguage.ArduinoCpp)
            CheckCppIdentifiersAndLifecycle(rawLines, strippedLines, diagnostics);
        else
            CheckPythonIdentifiersAndLifecycle(strippedLines, diagnostics);
    }

    private static void CheckCppIdentifiersAndLifecycle(
        string[] rawLines,
        string[] strippedLines,
        List<BoxCodeDiagnostic> diagnostics)
    {
        var declared = new Dictionary<string, (int Line, bool IsInitialized, bool IsConstant, bool IsFunctionOrObj)>(StringComparer.Ordinal);
        var usedAfterDecl = new HashSet<string>(StringComparer.Ordinal);
        var flaggedUndeclared = new HashSet<string>(StringComparer.Ordinal);
        var flaggedUninit = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: Collect preprocessor #defines, typedef/object/function names
        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            Match defMatch = CppDefineRegex().Match(line);
            if (defMatch.Success)
                declared[defMatch.Groups[1].Value] = (i + 1, true, true, false);

            Match fnMatch = CppAnyFuncHeaderRegex().Match(line);
            if (fnMatch.Success)
            {
                declared[fnMatch.Groups[1].Value] = (i + 1, true, false, true);
                foreach (Match p in CppParamDeclRegex().Matches(fnMatch.Groups[2].Value))
                    declared[p.Groups[1].Value] = (i + 1, true, false, false);
            }

            Match objMatch = CppLibraryObjectRegex().Match(line);
            if (objMatch.Success)
                declared[objMatch.Groups[1].Value] = (i + 1, true, false, true);
        }

        // Pass 2: Walk statements sequentially to detect undeclared reads, uninitialized reads, and usage
        for (int i = 0; i < strippedLines.Length; i++)
        {
            string rawLine = strippedLines[i];
            string trimmed = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('#'))
                continue;

            // Skip function declaration header parameter tokens (already handled above)
            if (CppAnyFuncHeaderRegex().IsMatch(trimmed) || CppLibraryObjectRegex().IsMatch(trimmed))
                continue;

            Match varDecl = CppVarDeclRegex().Match(trimmed);
            if (varDecl.Success)
            {
                bool isConst = trimmed.StartsWith("const ", StringComparison.Ordinal);
                bool isStringLikeType = trimmed.StartsWith("String ", StringComparison.Ordinal) ||
                    trimmed.StartsWith("const String ", StringComparison.Ordinal) ||
                    trimmed.Contains("char*", StringComparison.Ordinal) ||
                    trimmed.Contains("char *", StringComparison.Ordinal) ||
                    trimmed.Contains('[', StringComparison.Ordinal);
                bool isCharScalar = !isStringLikeType &&
                    Regex.IsMatch(trimmed, @"^(?:const\s+)?char\s+[A-Za-z_]");

                foreach (Match part in CppDeclaratorListItemRegex().Matches(varDecl.Groups[1].Value))
                {
                    string vName = part.Groups[1].Value;
                    bool hasInit = part.Groups[2].Success;
                    if (hasInit)
                    {
                        string rhs = part.Groups[3].Value.Trim();
                        if (!isStringLikeType && rhs == "\"\"" &&
                            (!isCharScalar || rawLines[i].Contains('"', StringComparison.Ordinal)))
                        {
                            diagnostics.Add(new BoxCodeDiagnostic(
                                i + 1,
                                1,
                                BoxCodeDiagnosticSeverity.Error,
                                "BC052",
                                $"Type mismatch: cannot initialize numeric/boolean variable '{vName}' with a string literal.",
                                $"Use 'String {vName} = ...;' for text or assign a numeric/boolean literal."));
                        }
                        ScanCppExpressionIdentifiers(rhs, i + 1, declared, usedAfterDecl, flaggedUndeclared, flaggedUninit, diagnostics);
                    }

                    declared[vName] = (i + 1, hasInit || trimmed.Contains('[') || trimmed.StartsWith("for", StringComparison.Ordinal), isConst, false);
                }

                if (trimmed.StartsWith("for", StringComparison.Ordinal))
                {
                    int semiIdx = trimmed.IndexOf(';');
                    if (semiIdx >= 0 && semiIdx + 1 < trimmed.Length)
                        ScanCppExpressionIdentifiers(trimmed[(semiIdx + 1)..], i + 1, declared, usedAfterDecl, flaggedUndeclared, flaggedUninit, diagnostics);
                }
                continue;
            }

            // Detect assignments x = expr;
            Match assign = CppSimpleAssignRegex().Match(trimmed);
            if (assign.Success)
            {
                string lhs = assign.Groups[1].Value;
                string rhs = assign.Groups[2].Value;
                ScanCppExpressionIdentifiers(rhs, i + 1, declared, usedAfterDecl, flaggedUndeclared, flaggedUninit, diagnostics);

                if (declared.TryGetValue(lhs, out var existing))
                {
                    declared[lhs] = (existing.Line, true, existing.IsConstant, existing.IsFunctionOrObj);
                    usedAfterDecl.Add(lhs);
                }
                else if (!IsKnownCppOrPinToken(lhs) && flaggedUndeclared.Add(lhs))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        i + 1,
                        1,
                        BoxCodeDiagnosticSeverity.Error,
                        "BC048",
                        $"Undeclared identifier '{lhs}' assigned without type declaration.",
                        $"Declare '{lhs}' with a type first (e.g. int {lhs} = 0;)."));
                }
                continue;
            }

            ScanCppExpressionIdentifiers(trimmed, i + 1, declared, usedAfterDecl, flaggedUndeclared, flaggedUninit, diagnostics);
        }

        // Pass 3: Unused non-constant local/global variables (skip #defines / constants / functions / loop counters i, j)
        foreach (var kv in declared)
        {
            if (kv.Value.IsConstant || kv.Value.IsFunctionOrObj)
                continue;
            if (kv.Key.Length == 1 && kv.Key[0] is 'i' or 'j' or 'k')
                continue;
            if (!usedAfterDecl.Contains(kv.Key))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    kv.Value.Line,
                    1,
                    BoxCodeDiagnosticSeverity.Warning,
                    "BC049",
                    $"Variable '{kv.Key}' is declared but its value is never used.",
                    $"Remove '{kv.Key}' if unneeded or reference it in your logic."));
            }
        }
    }

    private static void ScanCppExpressionIdentifiers(
        string expr,
        int lineNum,
        Dictionary<string, (int Line, bool IsInitialized, bool IsConstant, bool IsFunctionOrObj)> declared,
        HashSet<string> usedAfterDecl,
        HashSet<string> flaggedUndeclared,
        HashSet<string> flaggedUninit,
        List<BoxCodeDiagnostic> diagnostics)
    {
        if (string.IsNullOrWhiteSpace(expr))
            return;

        foreach (Match tok in CppIdentRegex().Matches(expr))
        {
            string name = tok.Value;
            // Skip numeric-like suffixes (0xFF caught if starts with 0? CppIdentRegex requires letter/_ start, so check preceding dot or ->)
            if (tok.Index > 0 && (expr[tok.Index - 1] == '.' || (tok.Index > 1 && expr.Substring(tok.Index - 2, 2) == "->")))
                continue;

            // Skip misspelled Arduino API typos already caught by BC012
            if (IsLikelyArduinoApiTypo(name))
                continue;

            // Skip function calls like foo(...) when preceded by object OR library type
            int afterIdx = tok.Index + tok.Length;
            bool followedByParen = afterIdx < expr.Length && expr[afterIdx] == '(';
            if (followedByParen && (IsKnownCppOrPinToken(name) || declared.ContainsKey(name)))
            {
                if (declared.ContainsKey(name))
                    usedAfterDecl.Add(name);
                continue;
            }

            if (declared.TryGetValue(name, out var info))
            {
                usedAfterDecl.Add(name);
                if (!info.IsInitialized && !info.IsFunctionOrObj && lineNum > info.Line && flaggedUninit.Add(name))
                {
                    diagnostics.Add(new BoxCodeDiagnostic(
                        lineNum,
                        tok.Index + 1,
                        BoxCodeDiagnosticSeverity.Warning,
                        "BC049",
                        $"Variable '{name}' may be used before it has been initialized.",
                        $"Initialize '{name}' at declaration (e.g. int {name} = 0;)."));
                }
                continue;
            }

            if (IsKnownCppOrPinToken(name))
                continue;

            // Heuristic: if followed by dot like unknownObj.print(...), allow common sketch peripherals without declaring object headers unless blatantly misspelled
            if (afterIdx < expr.Length && expr[afterIdx] == '.')
                continue;

            if (flaggedUndeclared.Add(name))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    lineNum,
                    tok.Index + 1,
                    BoxCodeDiagnosticSeverity.Error,
                    "BC048",
                    $"Undeclared identifier '{name}'.",
                    $"Declare '{name}' before using it (or check spelling)."));
            }
        }
    }

    private static void CheckPythonIdentifiersAndLifecycle(
        string[] strippedLines,
        List<BoxCodeDiagnostic> diagnostics)
    {
        var defined = new Dictionary<string, int>(StringComparer.Ordinal);
        var used = new HashSet<string>(StringComparer.Ordinal);
        var flagged = new HashSet<string>(StringComparer.Ordinal);

        // Pass 1: collect imports, defs, global assignments, for-targets
        var importBlockLines = new HashSet<int>();
        bool inFromImportParens = false;
        for (int i = 0; i < strippedLines.Length; i++)
        {
            string line = strippedLines[i].Trim();
            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (inFromImportParens)
            {
                importBlockLines.Add(i);
                int closeIdx = line.IndexOf(')');
                string segment = closeIdx >= 0 ? line[..closeIdx] : line;
                foreach (string sym in segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string clean = sym.Split(" as ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
                    if (Regex.IsMatch(clean, @"^[A-Za-z_]\w*$"))
                        defined[clean] = i + 1;
                }
                if (closeIdx >= 0)
                    inFromImportParens = false;
                continue;
            }

            string strippedImport = line.Split('#', 2)[0].Trim();
            if (strippedImport.StartsWith("import ", StringComparison.Ordinal))
            {
                foreach (string rawModule in strippedImport["import ".Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    Match aliasMatch = PythonImportAsRegex().Match("import " + rawModule);
                    if (aliasMatch.Success && aliasMatch.Groups[1].Success)
                    {
                        defined[aliasMatch.Groups[1].Value] = i + 1;
                    }
                    else
                    {
                        int dotIdx = rawModule.IndexOf('.');
                        int spaceIdx = rawModule.IndexOfAny(new[] { ' ', '\t' });
                        int endIdx = dotIdx >= 0 && spaceIdx >= 0
                            ? Math.Min(dotIdx, spaceIdx)
                            : (dotIdx >= 0 ? dotIdx : spaceIdx);
                        string rootModule = endIdx >= 0 ? rawModule[..endIdx] : rawModule;
                        if (!string.IsNullOrEmpty(rootModule) && (char.IsLetter(rootModule[0]) || rootModule[0] == '_'))
                            defined[rootModule] = i + 1;
                    }
                }
            }
            else
            {
                foreach (Match m in PythonImportAsRegex().Matches(line))
                {
                    if (m.Groups[1].Success)
                        defined[m.Groups[1].Value] = i + 1;
                }
            }

            Match fromParenMatch = Regex.Match(strippedImport, @"^from\s+[A-Za-z_][\w\.]*\s+import\s*\((.*)$");
            if (fromParenMatch.Success)
            {
                importBlockLines.Add(i);
                string rest = fromParenMatch.Groups[1].Value;
                int closeIdx = rest.IndexOf(')');
                string segment = closeIdx >= 0 ? rest[..closeIdx] : rest;
                foreach (string sym in segment.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string clean = sym.Split(" as ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
                    if (Regex.IsMatch(clean, @"^[A-Za-z_]\w*$"))
                        defined[clean] = i + 1;
                }
                if (closeIdx < 0)
                    inFromImportParens = true;
            }
            else
            {
                Match fromMatch = PythonFromImportRegex().Match(line);
                if (fromMatch.Success)
                {
                    foreach (string sym in fromMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        string clean = sym.Split(" as ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Last();
                        if (Regex.IsMatch(clean, @"^[A-Za-z_]\w*$"))
                            defined[clean] = i + 1;
                    }
                }
            }

            Match defMatch = PythonDefRegex().Match(line);
            if (defMatch.Success)
            {
                defined[defMatch.Groups[1].Value] = i + 1;
                foreach (string p in defMatch.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string clean = p.Split('=')[0].Trim();
                    if (Regex.IsMatch(clean, @"^[A-Za-z_]\w*$"))
                        defined[clean] = i + 1;
                }
            }

            Match forMatch = PythonForTargetRegex().Match(line);
            if (forMatch.Success)
            {
                foreach (string tok in forMatch.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Regex.IsMatch(tok, @"^[A-Za-z_]\w*$"))
                        defined[tok] = i + 1;
                }
            }

            Match exceptAs = PythonExceptAsRegex().Match(line);
            if (exceptAs.Success)
                defined[exceptAs.Groups[1].Value] = i + 1;
        }

        // Pass 2: walk lines sequentially for assignments vs reads
        for (int i = 0; i < strippedLines.Length; i++)
        {
            if (importBlockLines.Contains(i))
                continue;

            string line = strippedLines[i].Trim();
            if (string.IsNullOrWhiteSpace(line) ||
                line.StartsWith("import ", StringComparison.Ordinal) ||
                line.StartsWith("from ", StringComparison.Ordinal) ||
                line.StartsWith("def ", StringComparison.Ordinal) ||
                line.StartsWith("except ", StringComparison.Ordinal))
            {
                continue;
            }

            Dictionary<string, int> exprScope = defined;
            MatchCollection compMatches = Regex.Matches(line, @"\bfor\s+([A-Za-z_]\w*(?:\s*,\s*[A-Za-z_]\w*)*)\s+in\b");
            if (compMatches.Count > 0 && !line.StartsWith("for ", StringComparison.Ordinal))
            {
                exprScope = new Dictionary<string, int>(defined, StringComparer.Ordinal);
                foreach (Match cm in compMatches)
                {
                    foreach (string tok in cm.Groups[1].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    {
                        if (Regex.IsMatch(tok, @"^[A-Za-z_]\w*$"))
                            exprScope[tok] = i + 1;
                    }
                }
            }

            Match assignMatch = PythonAssignRegex().Match(line);
            if (assignMatch.Success)
            {
                string lhsGroup = assignMatch.Groups[1].Value;
                string rhs = assignMatch.Groups[2].Value;
                ScanPythonExpression(rhs, i + 1, exprScope, used, flagged, diagnostics);
                foreach (string lhs in lhsGroup.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    if (Regex.IsMatch(lhs, @"^[A-Za-z_]\w*$") && !defined.ContainsKey(lhs))
                        defined[lhs] = i + 1;
                }
                continue;
            }

            ScanPythonExpression(line, i + 1, exprScope, used, flagged, diagnostics);
        }
    }

    private static void ScanPythonExpression(
        string expr,
        int lineNum,
        Dictionary<string, int> defined,
        HashSet<string> used,
        HashSet<string> flagged,
        List<BoxCodeDiagnostic> diagnostics)
    {
        foreach (Match tok in CppIdentRegex().Matches(expr))
        {
            string name = tok.Value;
            if (tok.Index > 0 && expr[tok.Index - 1] == '.')
                continue;

            int afterIdx = tok.Index + tok.Length;
            // Python f-string / raw-string / byte-string literal prefix (f"", r"", b"", u"", fr"", rf"")
            if (afterIdx < expr.Length && (expr[afterIdx] == '"' || expr[afterIdx] == '\'') &&
                name.Length <= 2 && name.All(ch => ch is 'f' or 'F' or 'r' or 'R' or 'b' or 'B' or 'u' or 'U'))
            {
                continue;
            }

            // Keyword arg foo(freq=1000)
            if (afterIdx < expr.Length && expr[afterIdx] == '=' && (afterIdx + 1 >= expr.Length || expr[afterIdx + 1] != '='))
                continue;

            if (defined.ContainsKey(name))
            {
                used.Add(name);
                continue;
            }

            if (PythonBuiltInIdentifiers.Contains(name))
                continue;

            if (afterIdx < expr.Length && expr[afterIdx] == '.')
                continue;

            if (flagged.Add(name))
            {
                diagnostics.Add(new BoxCodeDiagnostic(
                    lineNum,
                    tok.Index + 1,
                    BoxCodeDiagnosticSeverity.Error,
                    "BC048",
                    $"NameError risk: '{name}' is not defined in MicroPython scope.",
                    $"Assign or import '{name}' before reading it."));
            }
        }
    }

    private static bool IsKnownCppOrPinToken(string token)
    {
        if (CppBuiltInIdentifiers.Contains(token))
            return true;

        if (Regex.IsMatch(token, @"^(?:D\d+|A\d+|IO\d+|GPIO\d+|P[ABC]\d+)$", RegexOptions.IgnoreCase))
            return true;

        return false;
    }

    private static bool IsLikelyArduinoApiTypo(string token) => token is "pinmode" or "digtalWrite" or "digitalwrite" or
        "digitalread" or "analogwrite" or "analogread" or "Seral" or "seral" or "serial" or "dealy";

    private static Dictionary<string, string> CollectCppPinConstants(string[] strippedLines)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string raw in strippedLines)
        {
            string line = raw.Trim();
            Match def = CppDefineConstRegex().Match(line);
            if (def.Success)
                map[def.Groups[1].Value] = def.Groups[2].Value;

            Match varC = CppConstPinValRegex().Match(line);
            if (varC.Success)
                map[varC.Groups[1].Value] = varC.Groups[2].Value;
        }
        return map;
    }

    private static bool TrySimplePinToken(string raw, out bool hasNum, out int numPin, out bool isAnalogPrefix)
    {
        hasNum = false;
        numPin = -1;
        isAnalogPrefix = false;
        string s = raw.Trim();
        if (string.IsNullOrEmpty(s))
            return false;

        if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out numPin))
        {
            hasNum = true;
            return true;
        }

        if (s.Length > 1 && (s[0] == 'A' || s[0] == 'a') && int.TryParse(s[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out numPin))
        {
            hasNum = true;
            isAnalogPrefix = true;
            return true;
        }

        if (s.StartsWith("GPIO", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[4..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numPin))
        {
            hasNum = true;
            return true;
        }

        if (s.StartsWith("IO", StringComparison.OrdinalIgnoreCase) && int.TryParse(s[2..].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out numPin))
        {
            hasNum = true;
            return true;
        }

        if (s.Length > 1 && (s[0] == 'D' || s[0] == 'd') && int.TryParse(s[1..], NumberStyles.Integer, CultureInfo.InvariantCulture, out numPin))
        {
            hasNum = true;
            return true;
        }

        return Regex.IsMatch(s, @"^(?:P[ABC]\d+|SDA|SCL|LED_BUILTIN)$", RegexOptions.IgnoreCase);
    }

    private static bool TryExtractBalancedParens(string line, int openIdx, out string inside)
    {
        inside = string.Empty;
        if (openIdx < 0 || openIdx >= line.Length || line[openIdx] != '(')
            return false;

        int depth = 0;
        for (int i = openIdx; i < line.Length; i++)
        {
            if (line[i] == '(') depth++;
            else if (line[i] == ')')
            {
                depth--;
                if (depth == 0)
                {
                    inside = line.Substring(openIdx + 1, i - openIdx - 1);
                    return true;
                }
            }
        }
        return false;
    }

    private static int CountArguments(string argText)
    {
        string trimmed = argText.Trim();
        if (trimmed.Length == 0)
            return 0;

        int depth = 0;
        int count = 1;
        foreach (char c in trimmed)
        {
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth = Math.Max(0, depth - 1);
            else if (c == ',' && depth == 0) count++;
        }
        return count;
    }

    private static int CountCharOutsideQuotes(string text, char target)
    {
        int count = 0;
        foreach (char c in text)
        {
            if (c == target) count++;
        }
        return count;
    }

    private static int CountLeadingSpaces(string line)
    {
        int count = 0;
        while (count < line.Length && line[count] == ' ')
            count++;
        return count;
    }

    [GeneratedRegex(@"\b(?:if|while)\s*\(([^)]+)\)")]
    private static partial Regex CppConditionRegex();

    [GeneratedRegex(@"^\s*(?:if|elif|while)\s+(.+?)\s*:")]
    private static partial Regex PythonConditionRegex();

    [GeneratedRegex(@"(?<![=!<>:])\b([A-Za-z_]\w*)\s*=(?!=)")]
    private static partial Regex SingleAssignEqualsRegex();

    [GeneratedRegex(@"(?:(?<!\w)/(?!\s*0\.[0-9]*[1-9])|%)\s*0(?:\.0+)?(?![0-9A-Za-z_xXbB])")]
    private static partial Regex DivByZeroRegex();

    [GeneratedRegex(@"^(?:return\b[^;]*;|break;|continue;)")]
    private static partial Regex CppJumpStatementRegex();

    [GeneratedRegex(@"^(?:return\b|break\b|continue\b)")]
    private static partial Regex PythonJumpStatementRegex();

    [GeneratedRegex(@"\b(?:while\s*\(\s*(?:true|1)\s*\)|for\s*\(\s*;\s*;\s*\))")]
    private static partial Regex CppInfiniteLoopRegex();

    [GeneratedRegex(@"^\s*while\s+(?:True|1)\s*:")]
    private static partial Regex PythonInfiniteLoopRegex();

    [GeneratedRegex(@"^\bdigitalWrite\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)")]
    private static partial Regex SingleDigitalWriteRegex();

    [GeneratedRegex(@"^([A-Za-z_]\w*)\.value\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex SinglePythonPinValueRegex();

    [GeneratedRegex(@"\battachInterrupt\s*\([^,]+,\s*([A-Za-z_]\w*)\s*,")]
    private static partial Regex AttachInterruptRegex();

    [GeneratedRegex(@"\b(?:delay|delayMicroseconds|Serial\.print(?:ln)?)\s*\(")]
    private static partial Regex IsrBlockingCallRegex();

    [GeneratedRegex(@"\banalogWrite\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)")]
    private static partial Regex AnalogWriteCallRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\s*\(")]
    private static partial Regex FuncCallStartRegex();

    [GeneratedRegex(@"\bSerial\.(?:print|println|write|read|available|parseInt)\s*\(")]
    private static partial Regex SerialUsageWithoutBeginRegex();

    [GeneratedRegex(@"\bpinMode\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)")]
    private static partial Regex PinModeModeRegex();

    [GeneratedRegex(@"\banalogRead\s*\(\s*([^)\s]+)\s*\)")]
    private static partial Regex AnalogReadPinRegex();

    [GeneratedRegex(@"^#\s*define\s+([A-Za-z_]\w*)\b")]
    private static partial Regex CppDefineRegex();

    [GeneratedRegex(@"^#\s*define\s+([A-Za-z_]\w*)\s+([A-Za-z0-9_]+)")]
    private static partial Regex CppDefineConstRegex();

    [GeneratedRegex(@"\b(?:const\s+)?(?:int|uint8_t|byte)\s+([A-Za-z_]\w*)\s*=\s*([A-Za-z0-9_]+)\s*;")]
    private static partial Regex CppConstPinValRegex();

    [GeneratedRegex(@"^(?:const\s+)?(?:void|int|long|float|double|bool|boolean|char|byte|String|uint\d+_t|int\d+_t|unsigned(?:\s+\w+)?)\s+([A-Za-z_]\w*)\s*\(([^;]*)\)\s*\{?")]
    private static partial Regex CppAnyFuncHeaderRegex();

    [GeneratedRegex(@"(?:int|long|float|double|bool|byte|char|String|uint\d+_t)\s+([A-Za-z_]\w*)")]
    private static partial Regex CppParamDeclRegex();

    [GeneratedRegex(@"^(?:LiquidCrystal_I2C|Servo|DHT|Adafruit_SSD1306|SoftwareSerial)\s+([A-Za-z_]\w*)")]
    private static partial Regex CppLibraryObjectRegex();

    [GeneratedRegex(@"^(?:for\s*\(\s*)?(?:const\s+|volatile\s+|static\s+|unsigned\s+)*(?:int|long|short|float|double|bool|boolean|char|byte|uint8_t|uint16_t|uint32_t|int8_t|int16_t|int32_t|size_t|String)\s+([^;]+)(?:;|$)")]
    private static partial Regex CppVarDeclRegex();

    [GeneratedRegex(@"([A-Za-z_]\w*)(?:\s*\[[^\]]*\])?\s*(?:(=)\s*([^,;]+))?")]
    private static partial Regex CppDeclaratorListItemRegex();

    [GeneratedRegex(@"^([A-Za-z_]\w*)\s*(?:\+|-|\*|/)?=\s*(.+?)\s*;?$")]
    private static partial Regex CppSimpleAssignRegex();

    [GeneratedRegex(@"\b([A-Za-z_]\w*)\b")]
    private static partial Regex CppIdentRegex();

    [GeneratedRegex(@"(?:^|\s)import\s+[A-Za-z_][\w.]*(?:\s+as\s+([A-Za-z_]\w*))?")]
    private static partial Regex PythonImportAsRegex();

    [GeneratedRegex(@"^from\s+[A-Za-z_][\w.]*\s+import\s+(.+)$")]
    private static partial Regex PythonFromImportRegex();

    [GeneratedRegex(@"^def\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*:")]
    private static partial Regex PythonDefRegex();

    [GeneratedRegex(@"^for\s+([A-Za-z_][\w,\s]*?)\s+in\s+")]
    private static partial Regex PythonForTargetRegex();

    [GeneratedRegex(@"^(?:except\s+[^:]*?\bas|with\s+[^:]*?\bas)\s+([A-Za-z_]\w*)\s*:")]
    private static partial Regex PythonExceptAsRegex();

    [GeneratedRegex(@"^([A-Za-z_][\w,\s]*?)\s*(?:\+|-|\*|/)?=\s*(.+)$")]
    private static partial Regex PythonAssignRegex();
}
