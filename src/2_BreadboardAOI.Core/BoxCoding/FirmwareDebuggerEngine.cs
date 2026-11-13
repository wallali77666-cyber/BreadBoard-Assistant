// ============================================================================
// FILE: src/2_BreadboardAOI.Core/BoxCoding/FirmwareDebuggerEngine.cs
// PURPOSE: Interactive VS Code-style step-through debugger engine for Arduino
//          C++ and MicroPython sketches (Breakpoints, Continue, Pause, Step Over,
//          Step Into, Reset, Call Stack, Variable Watch, Pin Watch, and Serial).
// ============================================================================

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.Core.BoxCoding;

public sealed record DebugStackFrame(
    string FunctionName,
    int CallSiteLine,
    int CurrentLine)
{
    public int Line => CurrentLine;
    public string Summary => $"call L{CallSiteLine}";
}

public sealed record DebugVariableWatch(
    string Name,
    string TypeName,
    string Value,
    bool ChangedThisStep)
{
    public string ValueDisplay => ChangedThisStep ? $"{Value} *" : Value;
}

public sealed record DebugPinWatchState(
    string PinName,
    string Mode,
    bool IsHigh,
    int PwmDuty,
    int AnalogMv)
{
    public string StateText => PwmDuty > 0 ? $"PWM {PwmDuty}" : (IsHigh ? "HIGH" : "LOW");
}

public sealed record FirmwareDebugSnapshot(
    int CurrentLine,
    bool IsActive,
    bool IsPaused,
    bool IsHalted,
    int StepCount,
    int LoopIterations,
    int? HitBreakpointLine,
    string StatusText,
    IReadOnlyList<DebugStackFrame> CallStack,
    IReadOnlyList<DebugVariableWatch> Variables,
    IReadOnlyList<DebugPinWatchState> PinStates,
    IReadOnlyList<string> SerialLines);

public sealed partial class FirmwareDebuggerSession
{
    private readonly List<string> _lines = new();
    private readonly HashSet<int> _breakpoints = new();
    private readonly Dictionary<string, (string Type, double NumVal, string? StrVal, bool IsString)> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _changedVars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DebugPinWatchState> _pins = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inputDigitalOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, int> _inputAnalogOverrides = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (int HeaderLine, int BodyStartLine, int BodyEndLine, List<string> ParamNames)> _functions = new(StringComparer.Ordinal);
    private readonly List<string> _serialLines = new();

    private sealed class LoopBlock
    {
        public int HeaderLine { get; init; }
        public int EndLine { get; init; }
        public string CondExpr { get; init; } = string.Empty;
        public string IncrExpr { get; init; } = string.Empty;
        public int Iterations { get; set; }
    }

    private sealed class ConditionalBlock
    {
        public int EndLine { get; init; }
        public bool BranchTaken { get; init; }
    }

    private sealed class ExecFrame
    {
        public required string FunctionName { get; init; }
        public int CallSiteLine { get; init; }
        public int BodyStartLine { get; init; }
        public int BodyEndLine { get; init; }
        public int NextLine { get; set; }
        public Stack<LoopBlock> ActiveLoops { get; } = new();
        public Stack<ConditionalBlock> ActiveConditionals { get; } = new();
    }

    private readonly Stack<ExecFrame> _frames = new();
    private BoxCodingLanguage _language = BoxCodingLanguage.ArduinoCpp;
    private MicrocontrollerBoardType _boardType = MicrocontrollerBoardType.ArduinoUnoR3R4;
    private int _stepCount;
    private int _loopIterations;
    private bool _isActive;
    private bool _isPaused;
    private bool _isHalted;
    private bool _ranSetup;
    private int _pythonMainLoopHeaderLine = -1;
    private int _pythonMainLoopEndLine = -1;
    private string _statusText = "Debugger ready — click Debug/Step or toggle breakpoints in the gutter.";

    public IReadOnlyCollection<int> Breakpoints => _breakpoints;

    public void LoadCode(
        string? code,
        BoxCodingLanguage language,
        MicrocontrollerBoardType boardType)
    {
        _language = language;
        _boardType = boardType;
        _lines.Clear();
        if (!string.IsNullOrEmpty(code))
        {
            string norm = code.Replace("\r\n", "\n").Replace('\r', '\n');
            _lines.AddRange(norm.Split('\n'));
        }

        IndexFunctions();
        Reset();
    }

    public bool ToggleBreakpoint(int lineNumber)
    {
        if (lineNumber <= 0)
            return false;

        if (_breakpoints.Remove(lineNumber))
            return false;

        _breakpoints.Add(lineNumber);
        return true;
    }

    public void SetBreakpoints(IEnumerable<int> lines)
    {
        _breakpoints.Clear();
        foreach (int line in lines)
        {
            if (line > 0)
                _breakpoints.Add(line);
        }
    }

    public void SetSimulatedPinInput(string pinName, bool isHigh, int? analogRaw = null)
    {
        if (string.IsNullOrWhiteSpace(pinName))
            return;

        string key = BoxCodeErrorAnalyzer.NormalizePinKey(pinName);
        _inputDigitalOverrides[key] = isHigh ? 1 : 0;
        if (analogRaw.HasValue)
            _inputAnalogOverrides[key] = analogRaw.Value;
    }

    public FirmwareDebugSnapshot Reset()
    {
        _vars.Clear();
        _changedVars.Clear();
        _pins.Clear();
        _serialLines.Clear();
        _frames.Clear();
        _stepCount = 0;
        _loopIterations = 0;
        _ranSetup = false;
        _isHalted = false;
        _isActive = _lines.Count > 0;
        _isPaused = _isActive;

        SeedGlobalDeclarations();

        if (_lines.Count == 0)
        {
            _statusText = "No code loaded in editor.";
            return CaptureSnapshot(hitBreakpoint: null);
        }

        if (_language == BoxCodingLanguage.ArduinoCpp)
        {
            if (_functions.TryGetValue("setup", out var setupFn))
            {
                int first = FindNextExecutableLine(setupFn.BodyStartLine, setupFn.BodyEndLine);
                _frames.Push(new ExecFrame
                {
                    FunctionName = "setup()",
                    CallSiteLine = setupFn.HeaderLine,
                    BodyStartLine = setupFn.BodyStartLine,
                    BodyEndLine = setupFn.BodyEndLine,
                    NextLine = first > 0 ? first : setupFn.BodyEndLine + 1
                });
                _statusText = "Debugger reset at entry of setup().";
            }
            else if (_functions.TryGetValue("loop", out var loopFn))
            {
                _ranSetup = true;
                int first = FindNextExecutableLine(loopFn.BodyStartLine, loopFn.BodyEndLine);
                _frames.Push(new ExecFrame
                {
                    FunctionName = "loop()",
                    CallSiteLine = loopFn.HeaderLine,
                    BodyStartLine = loopFn.BodyStartLine,
                    BodyEndLine = loopFn.BodyEndLine,
                    NextLine = first > 0 ? first : loopFn.BodyEndLine + 1
                });
                _statusText = "Debugger reset at entry of loop().";
            }
            else
            {
                _isHalted = true;
                _statusText = "Add void setup() / void loop() to step through Arduino firmware.";
            }
        }
        else
        {
            int first = FindNextExecutableLine(1, _lines.Count);
            if (first > 0)
            {
                _frames.Push(new ExecFrame
                {
                    FunctionName = "<module>",
                    CallSiteLine = 1,
                    BodyStartLine = 1,
                    BodyEndLine = _lines.Count,
                    NextLine = first
                });
                _statusText = $"Debugger reset at line {first}.";
            }
            else
            {
                _isHalted = true;
                _statusText = "No executable Python lines found.";
            }
        }

        return CaptureSnapshot(hitBreakpoint: null);
    }

    public FirmwareDebugSnapshot Pause()
    {
        _isPaused = true;
        _statusText = _frames.Count > 0
            ? $"Paused at line {_frames.Peek().NextLine}."
            : "Paused.";
        return CaptureSnapshot(hitBreakpoint: null);
    }

    public FirmwareDebugSnapshot StepInto()
    {
        if (!_isActive || _isHalted || _frames.Count == 0)
            return Reset();

        _changedVars.Clear();
        int? hitBp = ExecuteSingleInstruction(allowStepInto: true);
        _isPaused = true;
        return CaptureSnapshot(hitBp);
    }

    public FirmwareDebugSnapshot StepOver()
    {
        if (!_isActive || _isHalted || _frames.Count == 0)
            return Reset();

        _changedVars.Clear();
        int targetDepth = _frames.Count;
        int? hitBp = ExecuteSingleInstruction(allowStepInto: true);

        int guard = 0;
        while (_frames.Count > targetDepth && !_isHalted && guard < 120)
        {
            guard++;
            int curLine = _frames.Peek().NextLine;
            if (_breakpoints.Contains(curLine))
            {
                hitBp = curLine;
                _statusText = $"Hit breakpoint at line {curLine}.";
                break;
            }
            hitBp = ExecuteSingleInstruction(allowStepInto: true);
        }

        _isPaused = true;
        return CaptureSnapshot(hitBp);
    }

    public FirmwareDebugSnapshot Continue(int maxSteps = 200)
    {
        if (!_isActive || _isHalted || _frames.Count == 0)
            return Reset();

        _changedVars.Clear();
        int? hitBp = null;
        int startLoopIter = _loopIterations;
        int stepsRun = 0;

        while (stepsRun < maxSteps && !_isHalted && _frames.Count > 0)
        {
            int curLine = _frames.Peek().NextLine;
            if (stepsRun > 0 && _breakpoints.Contains(curLine))
            {
                hitBp = curLine;
                _isPaused = true;
                _statusText = $"Paused on breakpoint at line {curLine}.";
                break;
            }

            hitBp = ExecuteSingleInstruction(allowStepInto: true);
            stepsRun++;

            if (hitBp.HasValue)
            {
                _isPaused = true;
                break;
            }

            if (_loopIterations > startLoopIter && _breakpoints.Count == 0 && stepsRun >= 6)
            {
                _isPaused = true;
                _statusText = $"Completed loop iteration {_loopIterations} (no breakpoints set).";
                break;
            }
        }

        if (hitBp is null && stepsRun >= maxSteps)
        {
            _isPaused = true;
            _statusText = $"Paused after {maxSteps} debug steps (cycle limit).";
        }

        return CaptureSnapshot(hitBp);
    }

    private int? ExecuteSingleInstruction(bool allowStepInto)
    {
        if (_frames.Count == 0)
        {
            _isHalted = true;
            _statusText = "Execution halted.";
            return null;
        }

        ExecFrame frame = _frames.Peek();
        int lineNum = FindNextExecutableLine(frame.NextLine, frame.BodyEndLine);
        if (lineNum <= 0 || lineNum > frame.BodyEndLine)
        {
            AdvanceOutOfFrame();
            return null;
        }

        string lineText = StripComment(_lines[lineNum - 1], _language == BoxCodingLanguage.MicroPython).Trim();
        _stepCount++;

        // Handle user function call if Step Into requested
        if (allowStepInto && TryMatchUserCall(lineText, out string calledFunc, out List<double> argVals))
        {
            if (_functions.TryGetValue(calledFunc, out var fn))
            {
                for (int p = 0; p < fn.ParamNames.Count && p < argVals.Count; p++)
                {
                    SetNumericVar(fn.ParamNames[p], "int", argVals[p]);
                }

                frame.NextLine = AdvancePastLine(lineNum, frame);
                int firstBody = FindNextExecutableLine(fn.BodyStartLine, fn.BodyEndLine);
                _frames.Push(new ExecFrame
                {
                    FunctionName = $"{calledFunc}()",
                    CallSiteLine = lineNum,
                    BodyStartLine = fn.BodyStartLine,
                    BodyEndLine = fn.BodyEndLine,
                    NextLine = firstBody > 0 ? firstBody : fn.BodyEndLine + 1
                });
                _statusText = $"Stepped into {calledFunc}() at line {(_frames.Peek().NextLine)}.";
                return null;
            }
        }

        // Handle structured control flow (if / else if / else / for / while / break / continue)
        if (TryExecuteControlFlowLine(lineNum, lineText, frame, out int jumpNextLine, out string? cfStatus))
        {
            if (jumpNextLine <= 0 || jumpNextLine > frame.BodyEndLine)
            {
                AdvanceOutOfFrame();
            }
            else
            {
                frame.NextLine = jumpNextLine;
                _statusText = cfStatus ?? $"Stepped L{lineNum} -> L{jumpNextLine} ({frame.FunctionName}).";
            }
            return null;
        }

        ExecuteLineEffects(lineText);

        // Return exits current call frame
        if (Regex.IsMatch(lineText, @"^return\b"))
        {
            AdvanceOutOfFrame();
            return null;
        }

        int nextLine = AdvancePastLine(lineNum, frame);
        if (nextLine <= 0 || nextLine > frame.BodyEndLine)
        {
            AdvanceOutOfFrame();
        }
        else
        {
            frame.NextLine = nextLine;
            _statusText = $"Executed L{lineNum} · Next: L{nextLine} ({frame.FunctionName}).";
        }

        return null;
    }

    private int AdvancePastLine(int executedLine, ExecFrame frame)
        => AdvanceFromIndex(executedLine + 1, frame);

    private int AdvanceFromIndex(int candidateStart, ExecFrame frame)
    {
        bool isPython = _language == BoxCodingLanguage.MicroPython;
        int nextLine = candidateStart > 0 && candidateStart <= frame.BodyEndLine
            ? FindNextExecutableLine(candidateStart, frame.BodyEndLine)
            : -1;

        while (true)
        {
            // Skip remaining else-if / else chain if we just exited a taken if / else-if branch
            while (frame.ActiveConditionals.Count > 0)
            {
                ConditionalBlock topCond = frame.ActiveConditionals.Peek();
                bool exitedAtSameBraceElse = nextLine == topCond.EndLine &&
                    !isPython &&
                    nextLine > 0 &&
                    nextLine <= _lines.Count &&
                    IsElseOrElseIfLine(StripComment(_lines[nextLine - 1], false).Trim(), false);

                if (!(nextLine <= 0 || nextLine > topCond.EndLine || exitedAtSameBraceElse))
                    break;

                ConditionalBlock condBlock = frame.ActiveConditionals.Pop();
                if (condBlock.BranchTaken && nextLine > 0 && nextLine <= frame.BodyEndLine)
                {
                    while (nextLine > 0 && nextLine <= frame.BodyEndLine)
                    {
                        string peekText = StripComment(_lines[nextLine - 1], isPython).Trim();
                        if (!IsElseOrElseIfLine(peekText, isPython))
                            break;

                        int chainEnd = FindBlockEndLine(nextLine, frame.BodyEndLine);
                        nextLine = NextCandidateAfterBlockEnd(nextLine, chainEnd, frame.BodyEndLine, isPython);
                    }
                }
            }

            // Re-evaluate active loop when control exits the loop body
            if (frame.ActiveLoops.Count > 0 &&
                (nextLine <= 0 || nextLine > frame.ActiveLoops.Peek().EndLine))
            {
                LoopBlock loop = frame.ActiveLoops.Peek();
                if (!string.IsNullOrWhiteSpace(loop.IncrExpr))
                    ExecuteLineEffects(loop.IncrExpr);

                loop.Iterations++;
                bool keepLooping = loop.Iterations < 256 &&
                    (string.IsNullOrWhiteSpace(loop.CondExpr) || EvalCondition(loop.CondExpr));

                if (keepLooping)
                {
                    int bodyStart = FindNextExecutableLine(loop.HeaderLine + 1, loop.EndLine);
                    if (bodyStart > 0 && bodyStart <= loop.EndLine)
                        return bodyStart;
                    nextLine = loop.EndLine + 1;
                    continue;
                }

                frame.ActiveLoops.Pop();
                nextLine = FindNextExecutableLine(loop.EndLine + 1, frame.BodyEndLine);
                continue;
            }

            break;
        }

        if (isPython && _pythonMainLoopHeaderLine > 0 && frame.FunctionName == "<module>")
        {
            if (nextLine <= 0 || nextLine > _pythonMainLoopEndLine)
            {
                _loopIterations++;
                return FindNextExecutableLine(_pythonMainLoopHeaderLine + 1, _pythonMainLoopEndLine);
            }
        }

        return nextLine;
    }

    private bool TryExecuteControlFlowLine(
        int lineNum,
        string lineText,
        ExecFrame frame,
        out int nextLine,
        out string? status)
    {
        nextLine = -1;
        status = null;
        bool isPython = _language == BoxCodingLanguage.MicroPython;

        if (Regex.IsMatch(lineText, @"^break\b\s*;?$") && frame.ActiveLoops.Count > 0)
        {
            LoopBlock loop = frame.ActiveLoops.Pop();
            nextLine = AdvanceFromIndex(loop.EndLine + 1, frame);
            status = $"Break out of loop at L{lineNum}.";
            return true;
        }

        if (Regex.IsMatch(lineText, @"^continue\b\s*;?$") && frame.ActiveLoops.Count > 0)
        {
            LoopBlock loop = frame.ActiveLoops.Peek();
            nextLine = AdvanceFromIndex(loop.EndLine + 1, frame);
            status = $"Continue loop iteration at L{lineNum}.";
            return true;
        }

        if (!isPython)
        {
            Match elseIfMatch = Regex.Match(lineText, @"^(?:\}\s*)?else\s+if\s*\((.+)\)\s*\{?\s*$");
            if (elseIfMatch.Success)
            {
                bool cond = EvalCondition(elseIfMatch.Groups[1].Value);
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                if (cond)
                {
                    frame.ActiveConditionals.Push(new ConditionalBlock { EndLine = endLine, BranchTaken = true });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(NextCandidateAfterBlockEnd(lineNum, endLine, frame.BodyEndLine, false), frame);
                    status = $"else if ({elseIfMatch.Groups[1].Value.Trim()}) -> true (entering L{nextLine}).";
                }
                else
                {
                    nextLine = AdvanceFromIndex(NextCandidateAfterBlockEnd(lineNum, endLine, frame.BodyEndLine, false), frame);
                    status = $"else if ({elseIfMatch.Groups[1].Value.Trim()}) -> false (skipped to L{nextLine}).";
                }
                return true;
            }

            if (Regex.IsMatch(lineText, @"^(?:\}\s*)?else\s*\{?\s*$"))
            {
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(NextCandidateAfterBlockEnd(lineNum, endLine, frame.BodyEndLine, false), frame);
                status = $"Entering else branch at L{nextLine}.";
                return true;
            }

            Match ifMatch = Regex.Match(lineText, @"^if\s*\((.+)\)\s*\{?\s*$");
            if (ifMatch.Success)
            {
                bool cond = EvalCondition(ifMatch.Groups[1].Value);
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                if (cond)
                {
                    frame.ActiveConditionals.Push(new ConditionalBlock { EndLine = endLine, BranchTaken = true });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(NextCandidateAfterBlockEnd(lineNum, endLine, frame.BodyEndLine, false), frame);
                    status = $"if ({ifMatch.Groups[1].Value.Trim()}) -> true (entering L{nextLine}).";
                }
                else
                {
                    nextLine = AdvanceFromIndex(NextCandidateAfterBlockEnd(lineNum, endLine, frame.BodyEndLine, false), frame);
                    status = $"if ({ifMatch.Groups[1].Value.Trim()}) -> false (skipped branch).";
                }
                return true;
            }

            Match forMatch = Regex.Match(lineText, @"^for\s*\(\s*([^;]*);\s*([^;]*);\s*([^)]*)\)\s*\{?\s*$");
            if (forMatch.Success)
            {
                string initExpr = forMatch.Groups[1].Value.Trim();
                string condExpr = forMatch.Groups[2].Value.Trim();
                string incrExpr = forMatch.Groups[3].Value.Trim();
                if (!string.IsNullOrWhiteSpace(initExpr))
                    ExecuteLineEffects(initExpr);

                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                bool enter = string.IsNullOrWhiteSpace(condExpr) || EvalCondition(condExpr);
                if (enter)
                {
                    frame.ActiveLoops.Push(new LoopBlock
                    {
                        HeaderLine = lineNum,
                        EndLine = endLine,
                        CondExpr = condExpr,
                        IncrExpr = incrExpr,
                        Iterations = 0
                    });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(endLine + 1, frame);
                    status = $"Entering for-loop at L{lineNum}.";
                }
                else
                {
                    nextLine = AdvanceFromIndex(endLine + 1, frame);
                    status = $"for-loop condition false -> skipped.";
                }
                return true;
            }

            Match whileMatch = Regex.Match(lineText, @"^while\s*\((.+)\)\s*\{?\s*$");
            if (whileMatch.Success)
            {
                string condExpr = whileMatch.Groups[1].Value.Trim();
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                if (EvalCondition(condExpr))
                {
                    frame.ActiveLoops.Push(new LoopBlock
                    {
                        HeaderLine = lineNum,
                        EndLine = endLine,
                        CondExpr = condExpr,
                        IncrExpr = string.Empty,
                        Iterations = 0
                    });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(endLine + 1, frame);
                    status = $"Entering while ({condExpr}) at L{lineNum}.";
                }
                else
                {
                    nextLine = AdvanceFromIndex(endLine + 1, frame);
                    status = $"while ({condExpr}) -> false (skipped).";
                }
                return true;
            }
        }
        else
        {
            Match pyElif = Regex.Match(lineText, @"^elif\s+(.+?)\s*:\s*$");
            if (pyElif.Success)
            {
                bool cond = EvalCondition(pyElif.Groups[1].Value);
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                if (cond)
                {
                    frame.ActiveConditionals.Push(new ConditionalBlock { EndLine = endLine, BranchTaken = true });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(endLine + 1, frame);
                    status = $"elif {pyElif.Groups[1].Value.Trim()} -> True.";
                }
                else
                {
                    nextLine = AdvanceFromIndex(endLine + 1, frame);
                    status = $"elif {pyElif.Groups[1].Value.Trim()} -> False (skipped).";
                }
                return true;
            }

            if (Regex.IsMatch(lineText, @"^else\s*:\s*$"))
            {
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(endLine + 1, frame);
                status = $"Entering else branch at L{nextLine}.";
                return true;
            }

            Match pyIf = Regex.Match(lineText, @"^if\s+(.+?)\s*:\s*$");
            if (pyIf.Success)
            {
                bool cond = EvalCondition(pyIf.Groups[1].Value);
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                if (cond)
                {
                    frame.ActiveConditionals.Push(new ConditionalBlock { EndLine = endLine, BranchTaken = true });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(endLine + 1, frame);
                    status = $"if {pyIf.Groups[1].Value.Trim()} -> True.";
                }
                else
                {
                    nextLine = AdvanceFromIndex(endLine + 1, frame);
                    status = $"if {pyIf.Groups[1].Value.Trim()} -> False (skipped).";
                }
                return true;
            }

            Match pyWhile = Regex.Match(lineText, @"^while\s+(.+?)\s*:\s*$");
            if (pyWhile.Success && lineNum != _pythonMainLoopHeaderLine)
            {
                string condExpr = pyWhile.Groups[1].Value.Trim();
                int endLine = FindBlockEndLine(lineNum, frame.BodyEndLine);
                if (EvalCondition(condExpr))
                {
                    frame.ActiveLoops.Push(new LoopBlock
                    {
                        HeaderLine = lineNum,
                        EndLine = endLine,
                        CondExpr = condExpr,
                        IncrExpr = string.Empty,
                        Iterations = 0
                    });
                    int bodyFirst = FindNextExecutableLine(lineNum + 1, endLine);
                    nextLine = bodyFirst > 0 ? bodyFirst : AdvanceFromIndex(endLine + 1, frame);
                    status = $"Entering while {condExpr} at L{lineNum}.";
                }
                else
                {
                    nextLine = AdvanceFromIndex(endLine + 1, frame);
                    status = $"while {condExpr} -> False (skipped).";
                }
                return true;
            }
        }

        return false;
    }

    private int NextCandidateAfterBlockEnd(int headerLine, int endLine, int frameEndLine, bool isPython)
    {
        if (!isPython && endLine > headerLine && endLine <= _lines.Count &&
            IsElseOrElseIfLine(StripComment(_lines[endLine - 1], false).Trim(), false))
        {
            return endLine;
        }
        return FindNextExecutableLine(endLine + 1, frameEndLine);
    }

    private static bool IsElseOrElseIfLine(string trimmed, bool isPython)
        => isPython
            ? Regex.IsMatch(trimmed, @"^(?:elif\b|else\s*:)")
            : Regex.IsMatch(trimmed, @"^(?:\}\s*)?else\b");

    private int FindBlockEndLine(int headerLine1Based, int frameEndLine1Based)
    {
        bool isPython = _language == BoxCodingLanguage.MicroPython;
        if (!isPython)
        {
            int depth = 0;
            bool opened = false;
            int limit = Math.Min(frameEndLine1Based, _lines.Count);
            for (int ln = headerLine1Based; ln <= limit; ln++)
            {
                string t = StripComment(_lines[ln - 1], isPython: false);
                if (ln == headerLine1Based)
                {
                    t = t.TrimStart();
                    if (t.StartsWith('}'))
                        t = t[1..];
                    if (!t.Contains('{') && t.TrimEnd().EndsWith(';'))
                        return ln;
                }

                for (int ci = 0; ci < t.Length; ci++)
                {
                    char ch = t[ci];
                    if (ch == '{')
                    {
                        opened = true;
                        depth++;
                    }
                    else if (ch == '}')
                    {
                        depth--;
                        if (opened && depth <= 0)
                            return ln;
                    }
                }
            }
            return Math.Min(limit, headerLine1Based + 1);
        }
        else
        {
            int baseIndent = GetLineIndent(_lines[headerLine1Based - 1]);
            int lastSuiteLine = headerLine1Based;
            int limit = Math.Min(frameEndLine1Based, _lines.Count);
            for (int ln = headerLine1Based + 1; ln <= limit; ln++)
            {
                string t = StripComment(_lines[ln - 1], isPython: true);
                if (string.IsNullOrWhiteSpace(t))
                    continue;
                int indent = GetLineIndent(_lines[ln - 1]);
                if (indent <= baseIndent)
                    break;
                lastSuiteLine = ln;
            }
            return lastSuiteLine;
        }
    }

    private static int GetLineIndent(string rawLine)
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

    private bool EvalCondition(string rawExpr)
    {
        string expr = StripOuterParens(rawExpr.Trim());
        if (string.IsNullOrEmpty(expr))
            return false;

        if (TrySplitTopLevelLogical(expr, "||", "or", out string leftOr, out string rightOr))
            return EvalCondition(leftOr) || EvalCondition(rightOr);

        if (TrySplitTopLevelLogical(expr, "&&", "and", out string leftAnd, out string rightAnd))
            return EvalCondition(leftAnd) && EvalCondition(rightAnd);

        if (expr.StartsWith('!') && !expr.StartsWith("!=", StringComparison.Ordinal))
            return !EvalCondition(expr[1..]);
        if (expr.StartsWith("not ", StringComparison.Ordinal))
            return !EvalCondition(expr[4..]);

        Match cmp = Regex.Match(expr, @"^(.+?)\s*(==|!=|<=|>=|<|>)\s*(.+)$");
        if (cmp.Success)
        {
            double l = EvalNumericExpr(cmp.Groups[1].Value);
            double r = EvalNumericExpr(cmp.Groups[3].Value);
            return cmp.Groups[2].Value switch
            {
                "==" => Math.Abs(l - r) < 1e-6,
                "!=" => Math.Abs(l - r) >= 1e-6,
                "<=" => l <= r + 1e-9,
                ">=" => l >= r - 1e-9,
                "<" => l < r - 1e-9,
                ">" => l > r + 1e-9,
                _ => false
            };
        }

        return Math.Abs(EvalNumericExpr(expr)) > 1e-9;
    }

    private static string StripOuterParens(string s)
    {
        while (s.Length >= 2 && s[0] == '(' && s[^1] == ')')
        {
            int depth = 0;
            bool wrapped = true;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '(') depth++;
                else if (s[i] == ')') depth--;
                if (depth == 0 && i < s.Length - 1)
                {
                    wrapped = false;
                    break;
                }
            }
            if (!wrapped) break;
            s = s[1..^1].Trim();
        }
        return s;
    }

    private static bool TrySplitTopLevelLogical(string expr, string symOp, string wordOp, out string left, out string right)
    {
        int depth = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (c == '(') depth++;
            else if (c == ')') depth = Math.Max(0, depth - 1);
            if (depth != 0) continue;

            if (i + symOp.Length <= expr.Length && string.Compare(expr, i, symOp, 0, symOp.Length, StringComparison.Ordinal) == 0)
            {
                left = expr[..i];
                right = expr[(i + symOp.Length)..];
                return true;
            }
            if (i > 0 && i + wordOp.Length < expr.Length &&
                !char.IsLetterOrDigit(expr[i - 1]) &&
                !char.IsLetterOrDigit(expr[i + wordOp.Length]) &&
                string.Compare(expr, i, wordOp, 0, wordOp.Length, StringComparison.Ordinal) == 0)
            {
                left = expr[..i];
                right = expr[(i + wordOp.Length)..];
                return true;
            }
        }
        left = right = string.Empty;
        return false;
    }

    private void AdvanceOutOfFrame()
    {
        if (_frames.Count == 0)
            return;

        ExecFrame finished = _frames.Pop();

        if (_frames.Count > 0)
        {
            _statusText = $"Returned from {finished.FunctionName} to {_frames.Peek().FunctionName} L{_frames.Peek().NextLine}.";
            return;
        }

        if (_language == BoxCodingLanguage.ArduinoCpp)
        {
            if (!_ranSetup)
                _ranSetup = true;
            else if (finished.FunctionName == "loop()")
                _loopIterations++;

            if (_functions.TryGetValue("loop", out var loopFn))
            {
                int first = FindNextExecutableLine(loopFn.BodyStartLine, loopFn.BodyEndLine);
                _frames.Push(new ExecFrame
                {
                    FunctionName = "loop()",
                    CallSiteLine = loopFn.HeaderLine,
                    BodyStartLine = loopFn.BodyStartLine,
                    BodyEndLine = loopFn.BodyEndLine,
                    NextLine = first > 0 ? first : loopFn.BodyEndLine + 1
                });
                _statusText = finished.FunctionName == "setup()"
                    ? "setup() complete — entering loop()."
                    : $"loop() iteration {_loopIterations} — restarting at L{(_frames.Peek().NextLine)}.";
                return;
            }
        }

        _isHalted = true;
        _isPaused = false;
        _statusText = "Firmware execution completed.";
    }

    private void ExecuteLineEffects(string lineText)
    {
        if (string.IsNullOrWhiteSpace(lineText))
            return;

        if (_language == BoxCodingLanguage.ArduinoCpp)
        {
            // pinMode(pin, mode)
            Match pm = Regex.Match(lineText, @"\bpinMode\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)");
            if (pm.Success)
            {
                string pin = ResolvePinToken(pm.Groups[1].Value.Trim());
                string mode = pm.Groups[2].Value.Trim().ToUpperInvariant();
                var cur = _pins.GetValueOrDefault(pin) ?? new DebugPinWatchState(pin, mode, false, 0, 0);
                _pins[pin] = cur with { Mode = mode };
            }

            // digitalWrite(pin, val)
            Match dw = Regex.Match(lineText, @"\bdigitalWrite\s*\(\s*([^,\s)]+)\s*,\s*([^)\s]+)\s*\)");
            if (dw.Success)
            {
                string pin = ResolvePinToken(dw.Groups[1].Value.Trim());
                bool isHigh = EvalNumericExpr(dw.Groups[2].Value.Trim()) > 0.5;
                var cur = _pins.GetValueOrDefault(pin) ?? new DebugPinWatchState(pin, "OUTPUT", false, 0, 0);
                _pins[pin] = cur with
                {
                    IsHigh = isHigh,
                    PwmDuty = isHigh ? 255 : 0,
                    AnalogMv = isHigh ? (_boardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560 ? 5000 : 3300) : 0
                };
            }

            // analogWrite(pin, duty)
            Match aw = Regex.Match(lineText, @"\banalogWrite\s*\(\s*([^,\s)]+)\s*,\s*([^)]+)\)");
            if (aw.Success)
            {
                string pin = ResolvePinToken(aw.Groups[1].Value.Trim());
                int duty = Math.Clamp((int)Math.Round(EvalNumericExpr(aw.Groups[2].Value.Trim())), 0, 255);
                var cur = _pins.GetValueOrDefault(pin) ?? new DebugPinWatchState(pin, "PWM", false, 0, 0);
                int mv = (int)Math.Round((duty / 255.0) * (_boardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560 ? 5000 : 3300));
                _pins[pin] = cur with { Mode = "PWM", IsHigh = duty > 0, PwmDuty = duty, AnalogMv = mv };
            }

            // Serial.print / Serial.println
            Match ser = Regex.Match(lineText, @"\bSerial\.print(?:ln)?\s*\(\s*(.+?)\s*\)\s*;?");
            if (ser.Success)
            {
                string formatted = FormatPrintableArg(ser.Groups[1].Value.Trim());
                _serialLines.Add(formatted);
                if (_serialLines.Count > 120)
                    _serialLines.RemoveAt(0);
            }

            // Variable declaration or increment / assignment
            Match decl = Regex.Match(lineText, @"^(?:const\s+|volatile\s+|static\s+)?(int|long|float|double|bool|boolean|byte|uint\d+_t|String)\s+([A-Za-z_]\w*)\s*(?:=\s*(.+?))?\s*;?$");
            if (decl.Success)
            {
                string typeName = decl.Groups[1].Value;
                string name = decl.Groups[2].Value;
                if (typeName == "String")
                {
                    string sv = decl.Groups[3].Success ? FormatPrintableArg(decl.Groups[3].Value) : string.Empty;
                    SetStringVar(name, typeName, sv);
                }
                else
                {
                    double val = decl.Groups[3].Success ? EvalNumericExpr(decl.Groups[3].Value) : 0;
                    SetNumericVar(name, typeName, val);
                }
                return;
            }

            Match incDec = Regex.Match(lineText, @"^([A-Za-z_]\w*)(\+\+|--)\s*;?$");
            if (incDec.Success)
            {
                string name = incDec.Groups[1].Value;
                double delta = incDec.Groups[2].Value == "++" ? 1 : -1;
                double curVal = _vars.TryGetValue(name, out var v) ? v.NumVal : 0;
                SetNumericVar(name, v.Type ?? "int", curVal + delta);
                return;
            }

            Match assign = Regex.Match(lineText, @"^([A-Za-z_]\w*)\s*(\+|-|\*|/)?=\s*(.+?)\s*;?$");
            if (assign.Success)
            {
                string name = assign.Groups[1].Value;
                string op = assign.Groups[2].Value;
                double rhs = EvalNumericExpr(assign.Groups[3].Value);
                double cur = _vars.TryGetValue(name, out var v) ? v.NumVal : 0;
                double updated = op switch
                {
                    "+" => cur + rhs,
                    "-" => cur - rhs,
                    "*" => cur * rhs,
                    "/" => Math.Abs(rhs) > 1e-9 ? cur / rhs : cur,
                    _ => rhs
                };
                SetNumericVar(name, string.IsNullOrEmpty(v.Type) ? "int" : v.Type, updated);
            }
        }
        else
        {
            // MicroPython Pin binding: var = Pin(2, Pin.OUT)
            Match pinBind = Regex.Match(lineText, @"^([A-Za-z_]\w*)\s*=\s*(?:ADC|PWM)?\s*\(?\s*Pin\s*\(\s*([^,\s)]+)");
            if (pinBind.Success)
            {
                string varName = pinBind.Groups[1].Value;
                string pin = ResolvePinToken(pinBind.Groups[2].Value.Trim());
                SetStringVar(varName, "Pin", pin);
                string mode = lineText.Contains("PWM", StringComparison.Ordinal) ? "PWM"
                    : lineText.Contains("ADC", StringComparison.Ordinal) ? "ADC"
                    : lineText.Contains("IN", StringComparison.Ordinal) ? "INPUT" : "OUTPUT";
                _pins[pin] = new DebugPinWatchState(pin, mode, false, 0, 0);
                return;
            }

            Match valCall = Regex.Match(lineText, @"^([A-Za-z_]\w*)\.value\s*\(\s*([^)\s]+)\s*\)");
            if (valCall.Success && _vars.TryGetValue(valCall.Groups[1].Value, out var pinVar) && pinVar.StrVal is { } pinKey)
            {
                bool high = EvalNumericExpr(valCall.Groups[2].Value) > 0.5;
                var cur = _pins.GetValueOrDefault(pinKey) ?? new DebugPinWatchState(pinKey, "OUTPUT", false, 0, 0);
                _pins[pinKey] = cur with { IsHigh = high, PwmDuty = high ? 255 : 0, AnalogMv = high ? 3300 : 0 };
                return;
            }

            Match pyPrint = Regex.Match(lineText, @"^print\s*\(\s*(.+?)\s*\)$");
            if (pyPrint.Success)
            {
                _serialLines.Add(FormatPrintableArg(pyPrint.Groups[1].Value.Trim()));
                if (_serialLines.Count > 120)
                    _serialLines.RemoveAt(0);
                return;
            }

            Match pyAssign = Regex.Match(lineText, @"^([A-Za-z_]\w*)\s*(\+|-|\*|/)?=\s*(.+)$");
            if (pyAssign.Success)
            {
                string name = pyAssign.Groups[1].Value;
                string op = pyAssign.Groups[2].Value;
                string rhsText = pyAssign.Groups[3].Value.Trim();
                if ((rhsText.StartsWith('"') && rhsText.EndsWith('"')) || (rhsText.StartsWith('\'') && rhsText.EndsWith('\'')))
                {
                    SetStringVar(name, "str", rhsText[1..^1]);
                }
                else
                {
                    double rhs = EvalNumericExpr(rhsText);
                    double cur = _vars.TryGetValue(name, out var v) ? v.NumVal : 0;
                    double updated = op switch
                    {
                        "+" => cur + rhs,
                        "-" => cur - rhs,
                        "*" => cur * rhs,
                        "/" => Math.Abs(rhs) > 1e-9 ? cur / rhs : cur,
                        _ => rhs
                    };
                    SetNumericVar(name, "number", updated);
                }
            }
        }
    }

    private void IndexFunctions()
    {
        _functions.Clear();
        _pythonMainLoopHeaderLine = -1;
        _pythonMainLoopEndLine = -1;

        if (_language == BoxCodingLanguage.ArduinoCpp)
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                string text = StripComment(_lines[i], false).Trim();
                Match m = Regex.Match(text, @"^(?:const\s+)?(?:void|int|long|float|double|bool|boolean|byte|String)\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*\{?");
                if (!m.Success)
                    continue;

                string fnName = m.Groups[1].Value;
                var paramNames = new List<string>();
                foreach (string p in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    string lastTok = p.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? string.Empty;
                    if (Regex.IsMatch(lastTok, @"^[A-Za-z_]\w*$"))
                        paramNames.Add(lastTok);
                }

                int openLine = i + 1;
                int depth = CountChar(text, '{') - CountChar(text, '}');
                int j = i + 1;
                if (depth == 0 && j < _lines.Count && _lines[j].Contains('{'))
                {
                    openLine = j + 1;
                    depth = 1;
                    j++;
                }

                int endLine = openLine;
                while (j < _lines.Count && depth > 0)
                {
                    string b = StripComment(_lines[j], false);
                    depth += CountChar(b, '{') - CountChar(b, '}');
                    endLine = j + 1;
                    j++;
                }

                _functions[fnName] = (i + 1, openLine + 1, Math.Max(openLine + 1, endLine - 1), paramNames);
            }
        }
        else
        {
            for (int i = 0; i < _lines.Count; i++)
            {
                string raw = StripComment(_lines[i], true);
                string trimmed = raw.Trim();
                if (trimmed.StartsWith("while True:", StringComparison.Ordinal) ||
                    trimmed.StartsWith("while 1:", StringComparison.Ordinal))
                {
                    int baseIndent = raw.TakeWhile(c => c == ' ').Count();
                    int endLine = i + 1;
                    for (int j = i + 1; j < _lines.Count; j++)
                    {
                        string next = StripComment(_lines[j], true);
                        if (string.IsNullOrWhiteSpace(next))
                            continue;
                        if (next.TakeWhile(c => c == ' ').Count() <= baseIndent)
                            break;
                        endLine = j + 1;
                    }

                    _pythonMainLoopHeaderLine = i + 1;
                    _pythonMainLoopEndLine = endLine;
                    continue;
                }

                Match defM = Regex.Match(trimmed, @"^def\s+([A-Za-z_]\w*)\s*\(([^)]*)\)\s*:");
                if (defM.Success)
                {
                    int baseIndent = raw.TakeWhile(c => c == ' ').Count();
                    int endLine = i + 1;
                    for (int j = i + 1; j < _lines.Count; j++)
                    {
                        string next = StripComment(_lines[j], true);
                        if (string.IsNullOrWhiteSpace(next))
                            continue;
                        if (next.TakeWhile(c => c == ' ').Count() <= baseIndent)
                            break;
                        endLine = j + 1;
                    }

                    var pNames = defM.Groups[2].Value
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(p => p.Split('=')[0].Trim())
                        .Where(p => Regex.IsMatch(p, @"^[A-Za-z_]\w*$"))
                        .ToList();
                    _functions[defM.Groups[1].Value] = (i + 1, i + 2, Math.Max(i + 2, endLine), pNames);
                }
            }
        }
    }

    private void SeedGlobalDeclarations()
    {
        for (int i = 0; i < _lines.Count; i++)
        {
            string trimmed = StripComment(_lines[i], _language == BoxCodingLanguage.MicroPython).Trim();
            if (trimmed.StartsWith("void setup", StringComparison.Ordinal) ||
                trimmed.StartsWith("void loop", StringComparison.Ordinal) ||
                trimmed.StartsWith("def ", StringComparison.Ordinal) ||
                trimmed.StartsWith("while ", StringComparison.Ordinal))
            {
                break;
            }

            Match def = Regex.Match(trimmed, @"^#\s*define\s+([A-Za-z_]\w*)\s+([A-Za-z0-9_]+)");
            if (def.Success)
            {
                double val = EvalNumericExpr(def.Groups[2].Value);
                SetNumericVar(def.Groups[1].Value, "#define", val);
                continue;
            }

            if (_language == BoxCodingLanguage.ArduinoCpp)
            {
                Match decl = Regex.Match(trimmed, @"^(?:const\s+)?(int|long|float|double|bool|byte|uint\d+_t)\s+([A-Za-z_]\w*)\s*=\s*([^;]+);?$");
                if (decl.Success)
                    SetNumericVar(decl.Groups[2].Value, decl.Groups[1].Value, EvalNumericExpr(decl.Groups[3].Value));
            }
        }
        _changedVars.Clear();
    }

    private int FindNextExecutableLine(int startLine1Based, int endLine1Based)
    {
        for (int line = Math.Max(1, startLine1Based); line <= Math.Min(endLine1Based, _lines.Count); line++)
        {
            string trimmed = StripComment(_lines[line - 1], _language == BoxCodingLanguage.MicroPython).Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed is "{" or "}")
                continue;

            if (_language == BoxCodingLanguage.MicroPython &&
                (trimmed.StartsWith("def ", StringComparison.Ordinal) ||
                 trimmed.StartsWith("while True:", StringComparison.Ordinal) ||
                 trimmed.StartsWith("while 1:", StringComparison.Ordinal)))
            {
                if (trimmed.StartsWith("def ", StringComparison.Ordinal) &&
                    _functions.Values.FirstOrDefault(f => f.HeaderLine == line) is { BodyEndLine: > 0 } defMeta)
                {
                    line = defMeta.BodyEndLine;
                    continue;
                }
                continue;
            }

            return line;
        }
        return -1;
    }

    private bool TryMatchUserCall(string lineText, out string fnName, out List<double> args)
    {
        fnName = string.Empty;
        args = new List<double>();

        Match m = Regex.Match(lineText, @"^([A-Za-z_]\w*)\s*\(([^)]*)\)\s*;?$");
        if (!m.Success)
            return false;

        string name = m.Groups[1].Value;
        if (!_functions.ContainsKey(name))
            return false;

        fnName = name;
        foreach (string arg in m.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            args.Add(EvalNumericExpr(arg));

        return true;
    }

    private string ResolvePinToken(string token)
    {
        if (_vars.TryGetValue(token, out var v))
        {
            return v.IsString && !string.IsNullOrEmpty(v.StrVal)
                ? v.StrVal
                : ((int)Math.Round(v.NumVal)).ToString(CultureInfo.InvariantCulture);
        }
        return BoxCodeErrorAnalyzer.NormalizePinKey(token);
    }

    private double EvalNumericExpr(string expr)
    {
        string s = expr.Trim();
        if (string.IsNullOrEmpty(s))
            return 0;

        if (s.Equals("HIGH", StringComparison.Ordinal) || s.Equals("true", StringComparison.OrdinalIgnoreCase))
            return 1;
        if (s.Equals("LOW", StringComparison.Ordinal) || s.Equals("false", StringComparison.OrdinalIgnoreCase))
            return 0;

        Match dr = Regex.Match(s, @"^digitalRead\s*\(\s*([^)]+)\s*\)$");
        if (dr.Success)
        {
            string pin = ResolvePinToken(dr.Groups[1].Value);
            return _inputDigitalOverrides.GetValueOrDefault(pin, 0);
        }

        Match ar = Regex.Match(s, @"^analogRead\s*\(\s*([^)]+)\s*\)$");
        if (ar.Success)
        {
            string pin = ResolvePinToken(ar.Groups[1].Value);
            return _inputAnalogOverrides.GetValueOrDefault(pin, 512);
        }

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double num))
            return num;

        if (_vars.TryGetValue(s, out var variable) && !variable.IsString)
            return variable.NumVal;

        Match bin = Regex.Match(s, @"^(.+?)\s*(\+|-|\*|/|%)\s*(.+)$");
        if (bin.Success)
        {
            double l = EvalNumericExpr(bin.Groups[1].Value);
            double r = EvalNumericExpr(bin.Groups[3].Value);
            return bin.Groups[2].Value switch
            {
                "+" => l + r,
                "-" => l - r,
                "*" => l * r,
                "/" => Math.Abs(r) > 1e-9 ? l / r : 0,
                "%" => Math.Abs(r) > 1e-9 ? l % r : 0,
                _ => l
            };
        }

        return 0;
    }

    private string FormatPrintableArg(string rawArg)
    {
        if (rawArg.StartsWith("F(", StringComparison.Ordinal) && rawArg.EndsWith(')'))
            rawArg = rawArg[2..^1].Trim();
        if ((rawArg.StartsWith('"') && rawArg.EndsWith('"')) || (rawArg.StartsWith('\'') && rawArg.EndsWith('\'')))
            return rawArg[1..^1];
        if (_vars.TryGetValue(rawArg, out var v))
            return v.IsString ? (v.StrVal ?? string.Empty) : FormatNumber(v.NumVal);
        return FormatNumber(EvalNumericExpr(rawArg));
    }

    private void SetNumericVar(string name, string type, double val)
    {
        string formatted = FormatNumber(val);
        _vars[name] = (type, val, null, false);
        _changedVars[name] = formatted;
    }

    private void SetStringVar(string name, string type, string val)
    {
        _vars[name] = (type, 0, val, true);
        _changedVars[name] = val;
    }

    private FirmwareDebugSnapshot CaptureSnapshot(int? hitBreakpoint)
    {
        int currentLine = _frames.Count > 0 ? _frames.Peek().NextLine : 0;
        var callStack = _frames
            .Select(f => new DebugStackFrame(f.FunctionName, f.CallSiteLine, f.NextLine))
            .ToList();

        var vars = _vars
            .OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase)
            .Select(k => new DebugVariableWatch(
                k.Key,
                k.Value.Type,
                k.Value.IsString ? $"\"{k.Value.StrVal}\"" : FormatNumber(k.Value.NumVal),
                _changedVars.ContainsKey(k.Key)))
            .ToList();

        var pins = _pins.Values
            .OrderBy(p => p.PinName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new FirmwareDebugSnapshot(
            CurrentLine: currentLine,
            IsActive: _isActive,
            IsPaused: _isPaused,
            IsHalted: _isHalted,
            StepCount: _stepCount,
            LoopIterations: _loopIterations,
            HitBreakpointLine: hitBreakpoint,
            StatusText: _statusText,
            CallStack: callStack,
            Variables: vars,
            PinStates: pins,
            SerialLines: _serialLines.ToList());
    }

    private static string FormatNumber(double value) =>
        Math.Abs(value - Math.Round(value)) < 1e-6
            ? ((long)Math.Round(value)).ToString(CultureInfo.InvariantCulture)
            : value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string StripComment(string line, bool isPython)
    {
        int idx = line.IndexOf(isPython ? "#" : "//", StringComparison.Ordinal);
        return idx >= 0 ? line[..idx] : line;
    }

    private static int CountChar(string s, char ch)
    {
        int c = 0;
        foreach (char ch2 in s)
            if (ch2 == ch) c++;
        return c;
    }
}
