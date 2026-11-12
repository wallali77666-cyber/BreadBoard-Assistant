// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/ProgrammerUploadDialog.xaml.cs
// PURPOSE: Spectrum-Dark dialog for inspecting MCU/USB programmer specs,
//          choosing a target COM/VIRTUAL-SIM port, validating firmware with
//          BoxCodeErrorAnalyzer, and flashing onto the virtual/connected MCU.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.DrcEngine;
using Microsoft.Win32;

namespace BreadboardAOI.App.Views;

public partial class ProgrammerUploadDialog : Window
{
    private const string VirtualSimPortOption = "VIRTUAL-SIM (Live Breadboard Execution)";

    private readonly MainWorkstationViewModel _viewModel;
    private bool _suppressUiEvents;

    public ProgrammerUploadDialog(
        MainWorkstationViewModel viewModel,
        string initialCode,
        BoxCodingLanguage initialLanguage)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        _viewModel = viewModel;
        DataContext = _viewModel;

        InitializeComponent();

        _suppressUiEvents = true;
        PopulatePorts();
        LanguageComboBox.Items.Add("Arduino C++ (.ino)");
        LanguageComboBox.Items.Add("MicroPython (.py)");
        _suppressUiEvents = false;

        LoadFirmwareContext(initialCode, initialLanguage);
        ConsoleOutputBox.Text = "[READY] Select port & press UPLOAD TO BOARD to run pre-flight check + flash.\n";
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    public void LoadFirmwareContext(string code, BoxCodingLanguage language)
    {
        _suppressUiEvents = true;
        try
        {
            LanguageComboBox.SelectedIndex = language == BoxCodingLanguage.MicroPython ? 1 : 0;
            CodeEditorBox.Text = code ?? string.Empty;
        }
        finally
        {
            _suppressUiEvents = false;
        }

        RefreshHardwareContext();
        RunStaticAnalysis();
    }

    private void PopulatePorts()
    {
        PortComboBox.Items.Clear();
        PortComboBox.Items.Add(VirtualSimPortOption);

        var discoveredPorts = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? serialKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (serialKey is not null)
            {
                foreach (string valueName in serialKey.GetValueNames())
                {
                    if (serialKey.GetValue(valueName) is string portName && !string.IsNullOrWhiteSpace(portName))
                        discoveredPorts.Add(portName.Trim());
                }
            }
        }
        catch
        {
            // Registry access may be restricted under hardened environments; VIRTUAL-SIM remains available.
        }

        foreach (string port in discoveredPorts)
            PortComboBox.Items.Add($"{port} (Hardware USB-UART)");

        string currentTarget = _viewModel.SelectedProgrammerComPort;
        int matchIndex = 0;
        for (int i = 0; i < PortComboBox.Items.Count; i++)
        {
            string itemText = PortComboBox.Items[i]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(currentTarget)
                && itemText.StartsWith(currentTarget.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
        }

        PortComboBox.SelectedIndex = matchIndex;
    }

    private BoxCodingLanguage GetSelectedLanguage()
        => LanguageComboBox.SelectedIndex == 1
            ? BoxCodingLanguage.MicroPython
            : BoxCodingLanguage.ArduinoCpp;

    private string GetSelectedPort()
        => PortComboBox.SelectedItem?.ToString() ?? VirtualSimPortOption;

    private void RefreshHardwareContext()
    {
        MicrocontrollerBoardType board = _viewModel.SelectedDevBoardType == MicrocontrollerBoardType.None
            ? MicrocontrollerBoardType.ArduinoUnoR3R4
            : _viewModel.SelectedDevBoardType;

        MicrocontrollerBoardProfile profile = _viewModel.Renderer.McuRuleEngine.GetBoardProfile(board);
        UsbCableSpec cableSpec = ProgrammerCatalog.GetCableSpec(_viewModel.SelectedUsbCableKind);
        ProgrammerCatalog.IsCableCompatible(_viewModel.SelectedUsbCableKind, board, out bool isNative, out string compatNote);

        BoardSummaryText.Text =
            $"{profile.DisplayName} · {profile.OperatingLogicVoltage:0.#}V Logic · {profile.Pins.Count} Pins · Cable: {cableSpec.DisplayName}";

        CompatibilityBadgeText.Text = isNative
            ? $"✓ NATIVE {cableSpec.ShortTag}"
            : $"⚡ {cableSpec.ShortTag} ADAPTER";
        CompatibilityBadgeBorder.BorderBrush = new SolidColorBrush(
            isNative ? Color.FromRgb(0x10, 0xB9, 0x81) : Color.FromRgb(0xF5, 0x9E, 0x0B));
        CompatibilityBadgeText.Foreground = new SolidColorBrush(
            isNative ? Color.FromRgb(0x6E, 0xE7, 0xB7) : Color.FromRgb(0xFD, 0xE6, 0x8A));

        PortDetailText.Text =
            $"{ProgrammerCatalog.GetBoardPortDescription(board, _viewModel.SelectedUsbCableKind, GetSelectedPort())}  |  {compatNote}";
    }

    private BoxCodeAnalysisReport RunStaticAnalysis()
    {
        string code = CodeEditorBox.Text ?? string.Empty;
        BoxCodingLanguage language = GetSelectedLanguage();
        MicrocontrollerBoardType board = _viewModel.SelectedDevBoardType == MicrocontrollerBoardType.None
            ? MicrocontrollerBoardType.ArduinoUnoR3R4
            : _viewModel.SelectedDevBoardType;

        var wiredPins = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var wire in _viewModel.ActiveDevBoardWires)
        {
            AddPin(wire.SourceEndpointKey, wiredPins);
            AddPin(wire.TargetEndpointKey, wiredPins);
        }

        BoxCodeAnalysisReport report = BoxCodeErrorAnalyzer.Analyze(code, language, board, wiredPins);

        int lineCount = string.IsNullOrEmpty(code) ? 0 : code.Split('\n').Length;
        CodeStatsText.Text = $"{language} · {lineCount} lines · {code.Length} chars · {report.ExtractedActions.Count} action(s)";

        DiagnosticsListBox.ItemsSource = report.Diagnostics;

        if (!report.IsValid)
        {
            DiagnosticsHeaderText.Text = $"STATIC CHECK FAILED: {report.ErrorCount} ERROR(S), {report.WarningCount} WARNING(S)";
            DiagnosticsHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
        }
        else if (report.WarningCount > 0)
        {
            DiagnosticsHeaderText.Text = $"STATIC CHECK PASSED WITH {report.WarningCount} WARNING(S)";
            DiagnosticsHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
        else
        {
            DiagnosticsHeaderText.Text = "STATIC CHECK CLEAN (0 ERRORS, 0 WARNINGS)";
            DiagnosticsHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
        }

        return report;

        static void AddPin(string key, HashSet<string> target)
        {
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith("MCU", StringComparison.OrdinalIgnoreCase))
                return;

            int colon = key.LastIndexOf(':');
            if (colon >= 0 && colon < key.Length - 1)
                target.Add(key.Substring(colon + 1));
        }
    }

    private void OnPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _viewModel.SelectedProgrammerComPort = GetSelectedPort();
        RefreshHardwareContext();
    }

    private void OnLanguageSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _viewModel.ActiveFirmwareLanguage = GetSelectedLanguage();
        RunStaticAnalysis();
    }

    private void OnCodeEditorTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _viewModel.ActiveFirmwareCode = CodeEditorBox.Text ?? string.Empty;
        RunStaticAnalysis();
    }

    private void OnSelectUsbCClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectUsbCable("typec");
        RefreshHardwareContext();
    }

    private void OnSelectUsbBClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectUsbCable("usbb");
        RefreshHardwareContext();
    }

    private void OnSelectMicroUsbClick(object sender, RoutedEventArgs e)
    {
        _viewModel.SelectUsbCable("micro");
        RefreshHardwareContext();
    }

    private void OnReanalyzeClick(object sender, RoutedEventArgs e)
    {
        RunStaticAnalysis();
    }

    private void OnOpenBoxCodingStudioClick(object sender, RoutedEventArgs e)
    {
        _viewModel.OpenBoxCodingStudio();
    }

    private async void OnUploadToBoardClick(object sender, RoutedEventArgs e)
    {
        UploadNowButton.IsEnabled = false;
        try
        {
            string code = CodeEditorBox.Text ?? string.Empty;
            BoxCodingLanguage lang = GetSelectedLanguage();
            string port = GetSelectedPort();

            UploadStageText.Text = "[1/4] VERIFYING SYNTAX & PIN RULES...";
            UploadProgressBar.Value = 20;
            UploadPercentText.Text = "20%";
            ConsoleOutputBox.Text = "[1/4] Starting upload pipeline...\n";

            await System.Windows.Threading.Dispatcher.Yield();
            UploadProgressBar.Value = 50;
            UploadPercentText.Text = "50%";
            UploadStageText.Text = "[2/4] COMPILING & CONNECTING OVER USB...";

            ProgrammerUploadResult result = await _viewModel
                .UploadFirmwareCoreAsync(code, lang, port)
                .ConfigureAwait(true);

            DiagnosticsListBox.ItemsSource = result.CodeAnalysis.Diagnostics;
            ConsoleOutputBox.Text = result.LogOutput;
            ConsoleOutputBox.ScrollToEnd();

            if (result.Success)
            {
                UploadProgressBar.Value = 100;
                UploadPercentText.Text = "100%";
                UploadStageText.Text = $"✓ LIVE ON {result.BoardDisplayName}";
                UploadStageText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            }
            else
            {
                UploadProgressBar.Value = 0;
                UploadPercentText.Text = "ERR";
                UploadStageText.Text = $"✕ UPLOAD BLOCKED ({result.CodeAnalysis.ErrorCount} ERROR(S))";
                UploadStageText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }

            RefreshHardwareContext();
        }
        finally
        {
            UploadNowButton.IsEnabled = true;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void TryEnableDarkTitleBar()
    {
        IntPtr handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero)
            return;

        int enabled = 1;
        try
        {
            if (DwmSetWindowAttribute(handle, 20, ref enabled, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref enabled, sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
