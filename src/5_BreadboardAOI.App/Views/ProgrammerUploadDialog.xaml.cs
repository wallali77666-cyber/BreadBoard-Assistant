// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/ProgrammerUploadDialog.xaml.cs
// PURPOSE: Spectrum-Dark Arduino IDE-style dialog for inspecting MCU/USB
//          programmer specs, choosing a physical COM or VIRTUAL-SIM port,
//          configuring board variant/FQBN/baud/flash/programmer options,
//          verifying/compiling firmware, flashing onto hardware or virtual MCU,
//          and operating the live hardware serial monitor.
// ============================================================================

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using BreadboardAOI.App.Orchestration;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.Views;

public partial class ProgrammerUploadDialog : Window
{
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
        _lastSyncedSerialRevision = _viewModel.VirtualSerialRevision;
        _failedAtExternalSerialRevision = _viewModel.ExternalSerialTrafficRevision;
        ConsoleOutputBox.Text = "[READY] Select port & Arduino IDE options, verify/compile, or press SEND / UPLOAD.\n";

        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Closed += (_, _) => _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        TryEnableDarkTitleBar();
    }

    private bool _isUploadWorkflowActive;
    private bool _hasFailedUploadLogInConsole;
    private long _lastSyncedSerialRevision;
    private long _failedAtExternalSerialRevision;

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainWorkstationViewModel.IsProgrammerCableConnected)
            or nameof(MainWorkstationViewModel.SelectedUsbCableKind)
            or nameof(MainWorkstationViewModel.IsUsbAdapterEnabled)
            or nameof(MainWorkstationViewModel.TargetProgrammerBoardInstanceId)
            or nameof(MainWorkstationViewModel.EffectiveFqbnPreview)
            or nameof(MainWorkstationViewModel.IsHardwareSerialOpen))
        {
            RefreshHardwareContext();
        }
        else if ((e.PropertyName == nameof(MainWorkstationViewModel.VirtualSerialLog)
                  || e.PropertyName == nameof(MainWorkstationViewModel.VirtualSerialRevision))
            && !_isUploadWorkflowActive
            && _viewModel.VirtualSerialRevision != _lastSyncedSerialRevision
            && !string.IsNullOrEmpty(_viewModel.VirtualSerialLog))
        {
            bool externalTrafficArrived = _viewModel.ExternalSerialTrafficRevision != _failedAtExternalSerialRevision;
            if (!_hasFailedUploadLogInConsole || externalTrafficArrived)
            {
                _hasFailedUploadLogInConsole = false;
                _lastSyncedSerialRevision = _viewModel.VirtualSerialRevision;
                ConsoleOutputBox.Text = _viewModel.VirtualSerialLog;
                ConsoleOutputBox.ScrollToEnd();
            }
        }
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
        _viewModel.RefreshProgrammerComPorts();

        PortComboBox.Items.Clear();
        PortComboBox.Items.Add(ProgrammerIdeCatalog.VirtualSimPortDisplay);

        IReadOnlyList<PhysicalComPortEntry> discovered = HardwareSerialMonitorService.DiscoverAvailablePorts();
        foreach (PhysicalComPortEntry entry in discovered)
            PortComboBox.Items.Add(entry.DisplayLabel);

        string currentTarget = _viewModel.SelectedProgrammerComPort;
        string rawTarget = ProgrammerIdeCatalog.ExtractRawComPort(currentTarget) ?? string.Empty;
        int matchIndex = 0;
        for (int i = 0; i < PortComboBox.Items.Count; i++)
        {
            string itemText = PortComboBox.Items[i]?.ToString() ?? string.Empty;
            if (!string.IsNullOrWhiteSpace(rawTarget)
                && string.Equals(ProgrammerIdeCatalog.ExtractRawComPort(itemText), rawTarget, StringComparison.OrdinalIgnoreCase))
            {
                matchIndex = i;
                break;
            }
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
        => PortComboBox.SelectedItem?.ToString() ?? ProgrammerIdeCatalog.VirtualSimPortDisplay;

    private MicrocontrollerBoardType GetTargetBoard()
        => _viewModel.GetEffectiveTargetBoard();

    private void RefreshHardwareContext()
    {
        MicrocontrollerBoardType board = GetTargetBoard();
        MicrocontrollerBoardProfile profile = _viewModel.Renderer.McuRuleEngine.GetBoardProfile(board);
        UsbCableSpec cableSpec = ProgrammerCatalog.GetCableSpec(_viewModel.SelectedUsbCableKind);
        bool isCompatible = ProgrammerCatalog.IsCableCompatible(
            _viewModel.SelectedUsbCableKind,
            board,
            _viewModel.IsUsbAdapterEnabled,
            out bool isNative,
            out string compatNote);

        BoardSummaryText.Text =
            $"{profile.DisplayName} ({_viewModel.TargetProgrammerBoardInstanceId}) · {profile.OperatingLogicVoltage:0.#}V Logic · Cable: {cableSpec.DisplayName}";

        if (!_viewModel.IsProgrammerCableConnected)
        {
            CompatibilityBadgeText.Text = "○ CABLE UNPLUGGED";
            CompatibilityBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
            CompatibilityBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
        }
        else if (isNative)
        {
            CompatibilityBadgeText.Text = $"✓ NATIVE {cableSpec.ShortTag}";
            CompatibilityBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xC8));
            CompatibilityBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xC8));
        }
        else if (isCompatible)
        {
            CompatibilityBadgeText.Text = $"⚡ {cableSpec.ShortTag} ADAPTER";
            CompatibilityBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0x9E, 0x0B));
            CompatibilityBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xFD, 0xE6, 0x8A));
        }
        else
        {
            CompatibilityBadgeText.Text = $"⚠ {cableSpec.ShortTag} MISMATCH";
            CompatibilityBadgeBorder.BorderBrush = new SolidColorBrush(Color.FromRgb(0xEF, 0x44, 0x44));
            CompatibilityBadgeText.Foreground = new SolidColorBrush(Color.FromRgb(0xFC, 0xA5, 0xA5));
        }

        PortDetailText.Text =
            $"{ProgrammerCatalog.GetBoardPortDescription(board, _viewModel.SelectedUsbCableKind, GetSelectedPort())}  |  {compatNote}";
    }

    private BoxCodeAnalysisReport RunStaticAnalysis()
    {
        string code = CodeEditorBox.Text ?? string.Empty;
        BoxCodingLanguage language = GetSelectedLanguage();
        MicrocontrollerBoardType board = GetTargetBoard();
        HashSet<string> wiredPins = _viewModel.ExtractWiredMcuPins().ToHashSet(StringComparer.OrdinalIgnoreCase);

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
    }

    private void OnPortSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressUiEvents)
            return;

        _viewModel.SelectedProgrammerComPort = GetSelectedPort();
        RefreshHardwareContext();
    }

    private void OnRefreshPortsClick(object sender, RoutedEventArgs e)
    {
        _suppressUiEvents = true;
        try
        {
            PopulatePorts();
        }
        finally
        {
            _suppressUiEvents = false;
        }

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

    private async void OnVerifyOnlyClick(object sender, RoutedEventArgs e)
    {
        await RunUploadWorkflowAsync(verifyOnly: true).ConfigureAwait(true);
    }

    private async void OnUploadToBoardClick(object sender, RoutedEventArgs e)
    {
        await RunUploadWorkflowAsync(verifyOnly: false).ConfigureAwait(true);
    }

    private async System.Threading.Tasks.Task RunUploadWorkflowAsync(bool verifyOnly)
    {
        _isUploadWorkflowActive = true;
        _hasFailedUploadLogInConsole = false;
        UploadNowButton.IsEnabled = false;
        VerifyNowButton.IsEnabled = false;
        try
        {
            string code = CodeEditorBox.Text ?? string.Empty;
            BoxCodingLanguage lang = GetSelectedLanguage();
            string port = GetSelectedPort();

            UploadStageText.Text = verifyOnly
                ? "[1/3] VERIFYING SYNTAX & COMPILING..."
                : "[1/4] VERIFYING SYNTAX & PIN RULES...";
            UploadStageText.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            UploadProgressBar.Value = 20;
            UploadPercentText.Text = "20%";
            ConsoleOutputBox.Text = verifyOnly
                ? "[1/3] Starting Arduino / MicroPython verify pipeline...\n"
                : "[1/4] Starting hardware upload pipeline...\n";

            await System.Windows.Threading.Dispatcher.Yield();
            UploadProgressBar.Value = 50;
            UploadPercentText.Text = "50%";
            UploadStageText.Text = verifyOnly
                ? "[2/3] RUNNING TOOLCHAIN VERIFICATION..."
                : "[2/4] COMPILING & FLASHING OVER USB...";

            ProgrammerUploadResult result = await _viewModel
                .UploadFirmwareWithOptionsAsync(code, lang, port, verifyOnly)
                .ConfigureAwait(true);

            var combinedDiags = new List<BoxCodeDiagnostic>();
            if (result.CodeAnalysis is not null)
                combinedDiags.AddRange(result.CodeAnalysis.Diagnostics);
            foreach (BoxCodeDiagnostic cliDiag in _viewModel.LastCliCompileDiagnostics)
            {
                if (!combinedDiags.Contains(cliDiag))
                    combinedDiags.Add(cliDiag);
            }
            DiagnosticsListBox.ItemsSource = combinedDiags;
            int totalErrors = combinedDiags.Count(d => d.Severity == BoxCodeDiagnosticSeverity.Error);
            int totalWarnings = combinedDiags.Count(d => d.Severity == BoxCodeDiagnosticSeverity.Warning);
            if (totalErrors > 0)
            {
                DiagnosticsHeaderText.Text = $"CHECK FAILED: {totalErrors} ERROR(S), {totalWarnings} WARNING(S)";
                DiagnosticsHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }
            else if (totalWarnings > 0)
            {
                DiagnosticsHeaderText.Text = $"CHECK PASSED WITH {totalWarnings} WARNING(S)";
                DiagnosticsHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
            }
            else if (result.CodeAnalysis is not null)
            {
                DiagnosticsHeaderText.Text = "STATIC & CLI CHECK CLEAN (0 ERRORS, 0 WARNINGS)";
                DiagnosticsHeaderText.Foreground = new SolidColorBrush(Color.FromRgb(0x34, 0xD3, 0x99));
            }

            _hasFailedUploadLogInConsole = !result.Success;
            _lastSyncedSerialRevision = _viewModel.VirtualSerialRevision;
            _failedAtExternalSerialRevision = _viewModel.ExternalSerialTrafficRevision;
            ConsoleOutputBox.Text = result.LogOutput;
            ConsoleOutputBox.ScrollToEnd();

            if (result.Success)
            {
                UploadProgressBar.Value = 100;
                UploadPercentText.Text = "100%";
                UploadStageText.Text = verifyOnly
                    ? $"✓ VERIFIED CLEAN · {result.BoardDisplayName}"
                    : result.UsedHardwareCli
                        ? $"✓ FLASHED PHYSICALLY ON {result.TargetPort}"
                        : $"✓ LIVE ON {result.BoardDisplayName}";
                UploadStageText.Foreground = new SolidColorBrush(Color.FromRgb(0x00, 0xFF, 0xC8));
            }
            else
            {
                UploadProgressBar.Value = 0;
                UploadPercentText.Text = "ERR";
                UploadStageText.Text = totalErrors > 0
                    ? $"✕ BLOCKED ({totalErrors} ERROR(S))"
                    : "✕ PHYSICAL UPLOAD FAILED — CHECK CABLE / PORT / TOOLCHAIN";
                UploadStageText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            }

            RefreshHardwareContext();
        }
        finally
        {
            _isUploadWorkflowActive = false;
            UploadNowButton.IsEnabled = true;
            VerifyNowButton.IsEnabled = true;
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
