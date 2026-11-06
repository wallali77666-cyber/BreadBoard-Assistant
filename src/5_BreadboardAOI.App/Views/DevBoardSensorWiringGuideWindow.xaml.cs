// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/DevBoardSensorWiringGuideWindow.xaml.cs
// PURPOSE: Code-behind for the DevBoard & Sensor Wiring Guide Studio window.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.Views;

public partial class DevBoardSensorWiringGuideWindow : Window
{
    public DevBoardSensorWiringGuideViewModel ViewModel { get; }

    public DevBoardSensorWiringGuideWindow() : this(null, MicrocontrollerBoardType.ArduinoUnoR3R4)
    {
    }

    public DevBoardSensorWiringGuideWindow(
        IReadOnlyList<DraftPart>? draftParts,
        MicrocontrollerBoardType initialBoard = MicrocontrollerBoardType.ArduinoUnoR3R4,
        Action<MicrocontrollerBoardType, SensorInterfaceSpec?>? onDock = null)
    {
        InitializeComponent();
        ViewModel = new DevBoardSensorWiringGuideViewModel(draftParts, initialBoard);
        if (onDock != null)
        {
            ViewModel.RequestDockToLeftPanel = (board, sensor) =>
            {
                onDock(board, sensor);
                Close();
            };
        }
        DataContext = ViewModel;
    }
}
