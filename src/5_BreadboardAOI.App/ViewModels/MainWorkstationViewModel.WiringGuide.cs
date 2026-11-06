// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.WiringGuide.cs
// PURPOSE: Partial class for launching, docking, minimizing, and synchronizing
//          the DevBoard & Sensor Wiring Guide Studio with the Left Dock panel.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.App.Views;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    private DevBoardSensorWiringGuideWindow? _wiringGuideWindow;

    // ---- Live Sensor & Component Pin Labels (Toggleable) -------------------
    [ObservableProperty]
    private bool _showSensorPinLabels = true;

    partial void OnShowSensorPinLabelsChanged(bool value)
    {
        _renderer.Layers.ShowSensorPinLabels = value;
        StatusLine = $"AR Sensor Pin Labels {(value ? "ENABLED (VCC, GND, TRIG, ECHO...)" : "DISABLED")}.";
    }

    [RelayCommand]
    public void ToggleSensorPinLabels()
    {
        ShowSensorPinLabels = !ShowSensorPinLabels;
    }

    // ---- Left Panel Wiring Guide Docking State -----------------------------
    private bool _syncingDockState;

    [ObservableProperty]
    private bool _isWiringGuideDocked;

    [ObservableProperty]
    private WiringGuideDocument? _dockedWiringGuide;

    [ObservableProperty]
    private SensorInterfaceSpec? _selectedDockedSensor;

    [ObservableProperty]
    private MicrocontrollerBoardType _selectedDockedBoard = MicrocontrollerBoardType.ArduinoUnoR3R4;

    public ObservableCollection<SensorInterfaceSpec> DockedSensors { get; } = new();

    public IReadOnlyList<MicrocontrollerBoardType> AvailableDockedBoards { get; } = new[]
    {
        MicrocontrollerBoardType.ArduinoUnoR3R4,
        MicrocontrollerBoardType.ArduinoMega2560,
        MicrocontrollerBoardType.Esp32DevKitV1,
        MicrocontrollerBoardType.WemosEsp32S2Mini,
        MicrocontrollerBoardType.Stm32BluePillF103C8
    };

    partial void OnIsWiringGuideDockedChanged(bool value)
    {
        if (_syncingDockState)
            return;

        if (value)
            OpenWiringGuideDock();
        else
            CloseWiringGuideDock();
    }

    partial void OnSelectedDockedSensorChanged(SensorInterfaceSpec? value)
    {
        RefreshDockedWiringGuide();
    }

    partial void OnSelectedDockedBoardChanged(MicrocontrollerBoardType value)
    {
        RefreshDockedWiringGuide();
    }

    [RelayCommand]
    public void OpenWiringGuideDock()
    {
        EnsureDockedSensorsPopulated();

        if (SelectedDockedSensor == null)
        {
            var detected = WiringGuideService.AutoDetectSensorFromParts(_draft.Parts);
            SelectedDockedSensor = detected ?? DockedSensors.FirstOrDefault();
        }

        if (SelectedDevBoardType != MicrocontrollerBoardType.None)
        {
            SelectedDockedBoard = SelectedDevBoardType;
        }

        RefreshDockedWiringGuide();

        _syncingDockState = true;
        try
        {
            IsWiringGuideDocked = true;
        }
        finally
        {
            _syncingDockState = false;
        }

        IsLeftDockVisible = true;
        LeftDockWidth = Math.Max(LeftDockWidth, 340);
        SelectedLeftTabIndex = 5; // WIRING tab index

        StatusLine = "DevBoard & Sensor Wiring Guide docked to Left Panel.";
    }

    [RelayCommand]
    public void CloseWiringGuideDock()
    {
        _syncingDockState = true;
        try
        {
            IsWiringGuideDocked = false;
        }
        finally
        {
            _syncingDockState = false;
        }

        if (SelectedLeftTabIndex == 5)
            SelectedLeftTabIndex = 0; // Return to SCENE or previous tab

        StatusLine = "Left Panel Wiring Guide dock closed.";
    }

    [RelayCommand]
    public void UndockWiringGuideToPopup()
    {
        CloseWiringGuideDock();
        OpenWiringGuideStudio();
    }

    public void DockWiringGuideFromPopup(MicrocontrollerBoardType board, SensorInterfaceSpec? sensor)
    {
        EnsureDockedSensorsPopulated();
        SelectedDockedBoard = board;
        if (sensor != null)
            SelectedDockedSensor = sensor;

        OpenWiringGuideDock();
    }

    [RelayCommand]
    public void AutoDetectDockedSensor()
    {
        EnsureDockedSensorsPopulated();
        var detected = WiringGuideService.AutoDetectSensorFromParts(_draft.Parts);
        if (detected != null)
        {
            SelectedDockedSensor = detected;
            StatusLine = $"Auto-detected '{detected.DisplayName}' from active breadboard draft.";
        }
        else
        {
            StatusLine = "No recognizable sensor breakout found in current draft.";
        }
    }

    [RelayCommand]
    public void RefreshDockedWiringGuide()
    {
        if (SelectedDockedSensor == null)
            return;

        DockedWiringGuide = WiringGuideService.GenerateGuide(SelectedDockedSensor.SensorId, SelectedDockedBoard);
    }

    [RelayCommand]
    public void CopyDockedCode()
    {
        if (DockedWiringGuide == null)
            return;

        try
        {
            Clipboard.SetText(DockedWiringGuide.Sensor.MinimalTestCodeIno);
            StatusLine = $"Copied test code for {DockedWiringGuide.Sensor.DisplayName} to clipboard!";
        }
        catch (Exception ex)
        {
            StatusLine = $"Clipboard copy failed: {ex.Message}";
        }
    }

    private void EnsureDockedSensorsPopulated()
    {
        if (DockedSensors.Count == 0)
        {
            foreach (var s in WiringGuideService.GetAvailableSensors())
            {
                DockedSensors.Add(s);
            }
        }
    }

    [RelayCommand]
    public void OpenWiringGuideStudio()
    {
        try
        {
            if (_wiringGuideWindow is { IsLoaded: true })
            {
                _wiringGuideWindow.Activate();
                return;
            }

            var parts = _draft.Parts.ToList();
            var targetBoard = SelectedDevBoardType == MicrocontrollerBoardType.None
                ? MicrocontrollerBoardType.ArduinoUnoR3R4
                : SelectedDevBoardType;

            _wiringGuideWindow = new DevBoardSensorWiringGuideWindow(
                parts,
                targetBoard,
                (board, sensor) => DockWiringGuideFromPopup(board, sensor))
            {
                Owner = Application.Current?.MainWindow
            };

            _wiringGuideWindow.Closed += (_, _) => _wiringGuideWindow = null;
            _wiringGuideWindow.Show();
        }
        catch (Exception ex)
        {
            StatusLine = $"Unable to launch Wiring Guide Studio: {ex.Message}";
        }
    }
}
