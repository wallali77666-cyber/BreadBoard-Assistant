// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/DevBoardSensorWiringGuideViewModel.cs
// PURPOSE: ViewModel for the DevBoard-Sensor Wiring Guide & Interface Tutor Studio.
//          Binds dev boards, sensors, interactive filtering, step-by-step
//          pin connection maps, level shifting rules, and verification code.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class DevBoardSensorWiringGuideViewModel : ObservableObject
{
    private readonly IReadOnlyList<DraftPart> _draftParts;

    [ObservableProperty]
    private MicrocontrollerBoardType _selectedBoardType = MicrocontrollerBoardType.ArduinoUnoR3R4;

    [ObservableProperty]
    private SensorInterfaceSpec? _selectedSensor;

    [ObservableProperty]
    private string _searchQuery = string.Empty;

    [ObservableProperty]
    private WiringGuideDocument? _currentGuide;

    [ObservableProperty]
    private string _statusMessage = "Select a Dev Board and Sensor to generate connection instructions.";

    public ObservableCollection<SensorInterfaceSpec> FilteredSensors { get; } = new();

    public IReadOnlyList<MicrocontrollerBoardType> AvailableBoards { get; } = new[]
    {
        MicrocontrollerBoardType.ArduinoUnoR3R4,
        MicrocontrollerBoardType.ArduinoMega2560,
        MicrocontrollerBoardType.Esp32DevKitV1,
        MicrocontrollerBoardType.WemosEsp32S2Mini,
        MicrocontrollerBoardType.Stm32BluePillF103C8
    };

    public IReadOnlyList<SensorInterfaceSpec> AllSensors { get; }

    public DevBoardSensorWiringGuideViewModel(
        IReadOnlyList<DraftPart>? draftParts = null,
        MicrocontrollerBoardType initialBoard = MicrocontrollerBoardType.ArduinoUnoR3R4)
    {
        _draftParts = draftParts ?? Array.Empty<DraftPart>();
        if (initialBoard != MicrocontrollerBoardType.None)
            SelectedBoardType = initialBoard;

        AllSensors = WiringGuideService.GetAvailableSensors();
        FilterSensors();

        if (_draftParts.Count > 0)
        {
            SelectedSensor = WiringGuideService.AutoDetectSensorFromParts(_draftParts);
            StatusMessage = $"Auto-detected sensor '{SelectedSensor?.DisplayName}' from active breadboard circuit!";
        }
        else
        {
            SelectedSensor = AllSensors.FirstOrDefault();
        }

        RefreshGuide();
    }

    partial void OnSelectedBoardTypeChanged(MicrocontrollerBoardType value) => RefreshGuide();

    partial void OnSelectedSensorChanged(SensorInterfaceSpec? value) => RefreshGuide();

    partial void OnSearchQueryChanged(string value) => FilterSensors();

    private void FilterSensors()
    {
        FilteredSensors.Clear();
        string q = SearchQuery.Trim();
        var matches = string.IsNullOrWhiteSpace(q)
            ? AllSensors
            : AllSensors.Where(s =>
                s.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.Category.Contains(q, StringComparison.OrdinalIgnoreCase) ||
                s.SensorId.Contains(q, StringComparison.OrdinalIgnoreCase));

        foreach (var m in matches)
            FilteredSensors.Add(m);
    }

    [RelayCommand]
    public void RefreshGuide()
    {
        if (SelectedSensor == null)
            return;

        CurrentGuide = WiringGuideService.GenerateGuide(SelectedSensor.SensorId, SelectedBoardType);
        StatusMessage = $"Wiring guide generated for {SelectedSensor.DisplayName} on {CurrentGuide.BoardName}.";
    }

    [RelayCommand]
    public void AutoDetectCircuit()
    {
        if (_draftParts.Count == 0)
        {
            StatusMessage = "No components placed in circuit draft to auto-detect.";
            return;
        }

        var detected = WiringGuideService.AutoDetectSensorFromParts(_draftParts);
        if (detected != null)
        {
            SelectedSensor = detected;
            RefreshGuide();
            StatusMessage = $"Matched sensor '{detected.DisplayName}' from breadboard parts!";
        }
    }

    [RelayCommand]
    public void CopyMarkdownGuide()
    {
        if (CurrentGuide == null)
            return;

        try
        {
            Clipboard.SetText(CurrentGuide.FormattedMarkdown);
            StatusMessage = "📋 Full wiring documentation copied to clipboard (Markdown format)!";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy guide: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CopyTestCode()
    {
        if (CurrentGuide == null)
            return;

        try
        {
            Clipboard.SetText(CurrentGuide.Sensor.MinimalTestCodeIno);
            StatusMessage = "⚡ Arduino C++ test code copied to clipboard! Paste into Arduino IDE.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy test code: {ex.Message}";
        }
    }

    [RelayCommand]
    public void CopyTestCodePy()
    {
        if (CurrentGuide == null)
            return;

        try
        {
            Clipboard.SetText(CurrentGuide.Sensor.MinimalTestCodePy);
            StatusMessage = "🐍 MicroPython test code copied to clipboard! Paste into Thonny.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to copy test code: {ex.Message}";
        }
    }

    public Action<MicrocontrollerBoardType, SensorInterfaceSpec?>? RequestDockToLeftPanel { get; set; }

    [RelayCommand]
    public void DockToLeftPanel()
    {
        RequestDockToLeftPanel?.Invoke(SelectedBoardType, SelectedSensor);
    }
}
