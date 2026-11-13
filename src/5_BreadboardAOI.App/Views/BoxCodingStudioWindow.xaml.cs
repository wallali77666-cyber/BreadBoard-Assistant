// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/BoxCodingStudioWindow.xaml.cs
// PURPOSE: Code-behind for the Scratch-Style Visual Box Coding Studio window,
//          hosting VS Code-style interactive line/breakpoint/diagnostic gutter,
//          Problems jump-to-line caret navigation, and scroll synchronization.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.Views;

public sealed record CodeGutterLineItem(
    int LineNumber,
    bool HasBreakpoint,
    bool IsCurrentDebugLine,
    BoxCodeDiagnosticSeverity? TopSeverity,
    string GutterTooltip)
{
    private static readonly SolidColorBrush BgDefault = Freeze(Color.FromRgb(0x11, 0x13, 0x1D));
    private static readonly SolidColorBrush BgCurrent = Freeze(Color.FromRgb(0x2D, 0x2A, 0x12));
    private static readonly SolidColorBrush BgError = Freeze(Color.FromRgb(0x2B, 0x12, 0x15));
    private static readonly SolidColorBrush NumNormal = Freeze(Color.FromRgb(0x64, 0x74, 0x8B));
    private static readonly SolidColorBrush NumActive = Freeze(Color.FromRgb(0xFA, 0xCC, 0x15));
    private static readonly SolidColorBrush DotError = Freeze(Color.FromRgb(0xEF, 0x44, 0x44));
    private static readonly SolidColorBrush DotWarn = Freeze(Color.FromRgb(0xF5, 0x9E, 0x0B));
    private static readonly SolidColorBrush DotInfo = Freeze(Color.FromRgb(0x38, 0xBD, 0xF8));

    public Visibility BreakpointVisibility => HasBreakpoint && !IsCurrentDebugLine ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CurrentLineVisibility => IsCurrentDebugLine ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DiagnosticVisibility => TopSeverity.HasValue ? Visibility.Visible : Visibility.Collapsed;

    public Brush RowBackgroundBrush => IsCurrentDebugLine
        ? BgCurrent
        : TopSeverity == BoxCodeDiagnosticSeverity.Error
            ? BgError
            : BgDefault;

    public Brush LineNumberBrush => IsCurrentDebugLine ? NumActive : NumNormal;

    public Brush DiagnosticDotBrush => TopSeverity switch
    {
        BoxCodeDiagnosticSeverity.Error => DotError,
        BoxCodeDiagnosticSeverity.Warning => DotWarn,
        _ => DotInfo
    };

    private static SolidColorBrush Freeze(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

public partial class BoxCodingStudioWindow : Window
{
    public BoxCodingStudioViewModel ViewModel { get; }

    public BoxCodingStudioWindow() : this(null, MicrocontrollerBoardType.ArduinoUnoR3R4, null)
    {
    }

    public BoxCodingStudioWindow(
        IReadOnlyList<DraftPart>? draftParts,
        MicrocontrollerBoardType initialBoard = MicrocontrollerBoardType.ArduinoUnoR3R4,
        IReadOnlyCollection<string>? wiredMcuPins = null)
    {
        InitializeComponent();
        ViewModel = new BoxCodingStudioViewModel(draftParts, initialBoard, wiredMcuPins);
        DataContext = ViewModel;

        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        ViewModel.CodeDiagnostics.CollectionChanged += OnGutterSourceCollectionChanged;
        ViewModel.DebugBreakpoints.CollectionChanged += OnGutterSourceCollectionChanged;
        ViewModel.EditorCaretJumpRequested += JumpEditorToLineColumn;
        Closed += OnWindowClosed;

        RebuildEditorGutter();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        ViewModel.CodeDiagnostics.CollectionChanged -= OnGutterSourceCollectionChanged;
        ViewModel.DebugBreakpoints.CollectionChanged -= OnGutterSourceCollectionChanged;
        ViewModel.EditorCaretJumpRequested -= JumpEditorToLineColumn;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(BoxCodingStudioViewModel.GeneratedCode)
            or nameof(BoxCodingStudioViewModel.DebugCurrentLine))
        {
            RebuildEditorGutter();
        }
    }

    private void OnGutterSourceCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildEditorGutter();
    }

    private void RebuildEditorGutter()
    {
        string text = ViewModel.GeneratedCode ?? string.Empty;
        int lineCount = 1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineCount++;
        }

        var bpSet = new HashSet<int>(ViewModel.DebugBreakpoints);
        var diagByLine = ViewModel.CodeDiagnostics
            .GroupBy(d => Math.Max(1, d.Line))
            .ToDictionary(
                g => g.Key,
                g => (
                    Severity: (BoxCodeDiagnosticSeverity?)g.Max(x => x.Severity),
                    Tooltip: string.Join("\n", g.Select(x => x.DisplaySummary))));

        int activeDebugLine = ViewModel.DebugCurrentLine;
        var rows = new List<CodeGutterLineItem>(lineCount);
        for (int line = 1; line <= lineCount; line++)
        {
            bool hasDiag = diagByLine.TryGetValue(line, out var diagEntry);
            bool hasBp = bpSet.Contains(line);
            string tooltip = hasDiag
                ? $"Line {line} — Click to toggle breakpoint\n{diagEntry.Tooltip}"
                : $"Line {line} — Click to toggle breakpoint";

            rows.Add(new CodeGutterLineItem(
                LineNumber: line,
                HasBreakpoint: hasBp,
                IsCurrentDebugLine: activeDebugLine == line,
                TopSeverity: hasDiag ? diagEntry.Severity : null,
                GutterTooltip: tooltip));
        }

        EditorGutterList.ItemsSource = rows;
    }

    private void OnGutterLineMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: CodeGutterLineItem item })
        {
            ViewModel.ToggleBreakpointAtLine(item.LineNumber);
            e.Handled = true;
        }
    }

    private void OnCodeEditorScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        GutterScrollViewer.ScrollToVerticalOffset(e.VerticalOffset);
    }

    private void JumpEditorToLineColumn(int line, int column)
    {
        string code = FirmwareCodeEditorBox.Text ?? string.Empty;
        int targetLine = Math.Max(1, line);
        int targetCol = Math.Max(1, column);

        int currentLine = 1;
        int index = 0;
        while (index < code.Length && currentLine < targetLine)
        {
            if (code[index] == '\n')
                currentLine++;
            index++;
        }

        int lineEnd = index;
        while (lineEnd < code.Length && code[lineEnd] != '\n' && code[lineEnd] != '\r')
            lineEnd++;

        int caretIndex = Math.Min(lineEnd, index + (targetCol - 1));
        FirmwareCodeEditorBox.Focus();
        FirmwareCodeEditorBox.CaretIndex = Math.Clamp(caretIndex, 0, code.Length);
        FirmwareCodeEditorBox.ScrollToLine(Math.Max(0, targetLine - 1));
    }

    private void OnBlockCardPreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: BoxBlockItem block })
        {
            ViewModel.SelectedBlock = block;
        }
    }
}
