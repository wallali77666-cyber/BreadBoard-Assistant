// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.AiVision.cs
// PURPOSE: Partial class for camera source cycling, still image/video loading,
//          AI Live Studio inspection window, KiCad/SVG export, and datasheet lookups.
// ============================================================================

using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.App.Orchestration;
using BreadboardAOI.Vision;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    [RelayCommand]
    private void CycleCamera()
    {
        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is null)
            return;

        CameraSource current = coordinator.ActiveCameraSource;
        CameraSource next = current switch
        {
            CameraSource.Synthetic => CameraSource.Camera0,
            CameraSource.Camera0 => CameraSource.Camera1,
            CameraSource.Camera1 => CameraSource.Camera2,
            CameraSource.Camera2 => CameraSource.Camera3,
            _ => CameraSource.Synthetic
        };

        for (int i = 0; i < CameraOptions.Count; i++)
        {
            if (CameraOptions[i].Source != next)
                continue;

            SelectedCamera = CameraOptions[i];
            break;
        }

        // Clean CAD board: transparent underlay disabled
        ShowCameraUnderlay = false;
    }

    /// <summary>
    /// Loads a still photo and replays it as the camera frame source. Used by the
    /// toolbar dialog, viewport drag &amp; drop and <c>BREADBOARD_IMAGE_PATH</c>.
    /// Opens the AI Live Studio so Qwen can inspect the uploaded photo.
    /// </summary>
    public void LoadImage(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
            return;

        LoadedImagePath = path;
        _pendingImagePath = path;
        ShowCameraUnderlay = false;

        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is not null)
        {
            coordinator.SetImageSource(path);
            CameraLabel = $"CAMERA: {coordinator.CameraStatus}";
            StatusLine = $"Still image loaded: {Path.GetFileName(path)}. AI Live Studio opened.";
        }
        else
        {
            StatusLine = $"Still image queued: {Path.GetFileName(path)}.";
        }

        OpenAiVisionInspector(path);
    }

    [RelayCommand]
    private void LoadImageFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open breadboard photo",
            Filter = "Breadboard images|*.png;*.jpg;*.jpeg;*.bmp;*.webp;*.tif;*.tiff|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            LoadImage(dialog.FileName);
    }

    [RelayCommand]
    private void LoadVideoFile()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Open breadboard inspection video",
            Filter = "Video files|*.mp4;*.avi;*.mov;*.mkv;*.webm|All files|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog() == true)
            LoadVideo(dialog.FileName);
    }

    public void LoadVideo(string path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path))
            return;

        LoadedImagePath = path;
        _pendingImagePath = path;
        ShowCameraUnderlay = false;

        InspectionPipelineCoordinator? coordinator = _coordinator;
        if (coordinator is not null)
        {
            coordinator.SetVideoSource(path);
            CameraLabel = $"CAMERA: {coordinator.CameraStatus}";
            StatusLine = $"Video loaded: {Path.GetFileName(path)}. Replaying live at 60 FPS.";
        }
        else
        {
            StatusLine = $"Video queued: {Path.GetFileName(path)}.";
        }

        OpenAiVisionInspector(path);
    }

    private BreadboardAOI.App.Views.AiVisionInspectionWindow? _aiVisionWindow;

    [RelayCommand]
    public void OpenAiVisionInspector() => OpenAiVisionInspector(LoadedImagePath);

    public void OpenAiVisionInspector(string? imagePath)
    {
        _dispatcher.Invoke(() =>
        {
            if (_coordinator is null)
                return;

            if (_aiVisionWindow is null || !_aiVisionWindow.IsLoaded)
            {
                var studio = new BreadboardAOI.App.Views.AiVisionInspectionWindow(_coordinator, imagePath)
                {
                    Owner = System.Windows.Application.Current.MainWindow,
                    LocalProviderLink = LinkLocalProviderAsync
                };
                studio.Closed += (_, _) => _aiVisionWindow = null;
                _aiVisionWindow = studio;
                studio.Show();
            }
            else
            {
                _aiVisionWindow.UpdateInspectionData(
                    imagePath ?? LoadedImagePath,
                    _coordinator.LatestPacket,
                    _coordinator.LatestRatsnest);
                if (_aiVisionWindow.WindowState == System.Windows.WindowState.Minimized)
                    _aiVisionWindow.WindowState = System.Windows.WindowState.Normal;
                _aiVisionWindow.Activate();
            }
        });
    }

    [ObservableProperty]
    private string _ocrResultText = string.Empty;

    /// <summary>
    /// Exports the active breadboard circuit netlist and layout to a standalone KiCad 6/7/8 .kicad_sch file.
    /// </summary>
    [RelayCommand]
    private async Task ExportKicadAsync()
    {
        var coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "EXPORT: pipeline is not running.";
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export KiCad Schematic (.kicad_sch)",
            Filter = "KiCad Schematic (*.kicad_sch)|*.kicad_sch",
            FileName = "breadboard_circuit.kicad_sch",
            DefaultExt = ".kicad_sch"
        };

        if (saveDialog.ShowDialog() != true)
            return;

        string path = saveDialog.FileName;
        bool ok = await Task.Run(() => coordinator.ExportKicadSch(path));
        if (ok)
            StatusLine = $"Exported KiCad schematic: {Path.GetFileName(path)}";
        else
            StatusLine = "Export KiCad failed (no active circuit or components placed).";
    }

    /// <summary>
    /// Exports the active breadboard circuit to an interactive 2D SVG vector diagram.
    /// </summary>
    [RelayCommand]
    private async Task ExportSvgAsync()
    {
        var coordinator = _coordinator;
        if (coordinator is null)
        {
            StatusLine = "EXPORT: pipeline is not running.";
            return;
        }

        var saveDialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Export 2D SVG Schematic (.svg)",
            Filter = "SVG Diagram (*.svg)|*.svg",
            FileName = "breadboard_circuit.svg",
            DefaultExt = ".svg"
        };

        if (saveDialog.ShowDialog() != true)
            return;

        string path = saveDialog.FileName;
        bool ok = await Task.Run(() => coordinator.ExportSvg(path));
        if (ok)
            StatusLine = $"Exported SVG diagram: {Path.GetFileName(path)}";
        else
            StatusLine = "Export SVG failed (no active circuit or components placed).";
    }

    /// <summary>
    /// Queries the datasheet for the currently selected part.
    /// </summary>
    [RelayCommand]
    private void LookupDatasheet()
    {
        string? partName = SelectedDraftPart?.Part.Value;
        if (string.IsNullOrWhiteSpace(partName) || partName == "--")
            partName = SelectedDraftPart?.Part.SymbolId;
        if (string.IsNullOrWhiteSpace(partName))
            partName = SelectedPartOption?.Label ?? "Component";

        BuilderHint = $"[DATASHEET] Querying '{partName}'... [ARGON-3: Local 250KB range fetcher pending integration]";
        try
        {
            string cleanName = partName.Split('/', ' ', '(', ')', '_').FirstOrDefault(s => s.Length > 2) ?? partName;
            string url = $"https://www.alldatasheet.com/view.jsp?Searchword={Uri.EscapeDataString(cleanName)}";
            Process.Start(new ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true
            })?.Dispose();
            StatusLine = $"Opened datasheet search for {cleanName}";
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            StatusLine = $"Datasheet lookup error: {ex.Message}";
        }
    }

    /// <summary>
    /// Triggers OCR reading on selected component or camera crop.
    /// </summary>
    [RelayCommand]
    private void RunOcr()
    {
        BuilderHint = "[OCR] Scanning component text... [ARGON-2: PaddleOCR engine pending integration]";
        OcrResultText = "[ARGON-2] PaddleOCR not available in this build.";
        StatusLine = "OCR: Engine pending Argon-2 vision integration.";
    }

}