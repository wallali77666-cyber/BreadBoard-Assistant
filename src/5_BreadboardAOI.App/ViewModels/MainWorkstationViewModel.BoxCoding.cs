// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.BoxCoding.cs
// PURPOSE: Partial class for launching the Scratch-Style Visual Box Coding
//          Studio for microcontrollers and sensors directly from the workstation.
// ============================================================================

using System;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.App.Views;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    private BoxCodingStudioWindow? _boxCodingStudioWindow;

    // ---- 16x2 I2C Character LCD CAD AR Simulation --------------------------
    [ObservableProperty]
    private bool _showLcdSimulation = true;

    [ObservableProperty]
    private string _lcdSimulatedRow0 = "Breadboard AOI";

    [ObservableProperty]
    private string _lcdSimulatedRow1 = "LCD 1602 Active";

    partial void OnShowLcdSimulationChanged(bool value)
    {
        _renderer.Layers.ShowLcdSimulation = value;
        StatusLine = $"LCD 1602 AR Simulation {(value ? "ENABLED (Blue Backlit Matrix)" : "DISABLED (Turned Off Screen)")}.";
    }

    [RelayCommand]
    public void ToggleLcdSimulation()
    {
        ShowLcdSimulation = !ShowLcdSimulation;
    }

    [RelayCommand]
    public void OpenBoxCodingStudio()
    {
        try
        {
            if (_boxCodingStudioWindow is { IsLoaded: true })
            {
                _boxCodingStudioWindow.Activate();
                return;
            }

            var parts = _draft.Parts.ToList();
            var targetBoard = SelectedDevBoardType == MicrocontrollerBoardType.None
                ? MicrocontrollerBoardType.ArduinoUnoR3R4
                : SelectedDevBoardType;

            _boxCodingStudioWindow = new BoxCodingStudioWindow(parts, targetBoard)
            {
                Owner = Application.Current?.MainWindow
            };

            if (_boxCodingStudioWindow.DataContext is BoxCodingStudioViewModel boxVm)
            {
                boxVm.LcdSimulatedTextUpdated += (r0, r1) =>
                {
                    LcdSimulatedRow0 = r0;
                    LcdSimulatedRow1 = r1;
                    _renderer.Layers.LcdSimulatedRow0 = r0;
                    _renderer.Layers.LcdSimulatedRow1 = r1;
                };

                if (!string.IsNullOrEmpty(boxVm.LcdSimulatedRow0))
                {
                    LcdSimulatedRow0 = boxVm.LcdSimulatedRow0;
                    LcdSimulatedRow1 = boxVm.LcdSimulatedRow1;
                    _renderer.Layers.LcdSimulatedRow0 = boxVm.LcdSimulatedRow0;
                    _renderer.Layers.LcdSimulatedRow1 = boxVm.LcdSimulatedRow1;
                }
            }

            _boxCodingStudioWindow.Closed += (_, _) => _boxCodingStudioWindow = null;
            _boxCodingStudioWindow.Show();
        }
        catch (Exception ex)
        {
            StatusLine = $"Unable to launch Box Coding Studio: {ex.Message}";
        }
    }
}
