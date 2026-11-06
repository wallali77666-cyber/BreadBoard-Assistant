// ============================================================================
// FILE: src/5_BreadboardAOI.App/Views/BoxCodingStudioWindow.xaml.cs
// PURPOSE: Code-behind for the Scratch-Style Visual Box Coding Studio window.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Windows;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;

namespace BreadboardAOI.App.Views;

public partial class BoxCodingStudioWindow : Window
{
    public BoxCodingStudioViewModel ViewModel { get; }

    public BoxCodingStudioWindow() : this(null, MicrocontrollerBoardType.ArduinoUnoR3R4)
    {
    }

    public BoxCodingStudioWindow(
        IReadOnlyList<DraftPart>? draftParts,
        MicrocontrollerBoardType initialBoard = MicrocontrollerBoardType.ArduinoUnoR3R4)
    {
        InitializeComponent();
        ViewModel = new BoxCodingStudioViewModel(draftParts, initialBoard);
        DataContext = ViewModel;
    }
}
