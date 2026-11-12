using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.App.Views;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Design;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Hardware;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class WorkstationAndAuditRegressionTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;
    private static readonly ElectricalRulesChecker Checker = new(Lookup);

    [Theory]
    [InlineData("ultrasonic distance radar with buzzer")]
    [InlineData("DHT11 temperature weather station with LCD")]
    [InlineData("traffic light with red yellow green LEDs")]
    [InlineData("555 timer astable blinker")]
    [InlineData("push button doorbell alarm")]
    public void EndToEnd_ExperimentalLab_PromptToThermalAndAutoFix_PreservesPartIntegrity(string prompt)
    {
        var draft = new CircuitDraft(Lookup);

        ExperimentalLabEngine.BuildCircuitFromPrompt(
            prompt,
            draft,
            out MicrocontrollerBoardType recommendedBoard,
            out List<BoxBlockItem> blocks,
            out string summary).Should().BeTrue(summary);

        recommendedBoard.Should().NotBe(MicrocontrollerBoardType.None);
        blocks.Should().NotBeEmpty();
        draft.Parts.Should().NotBeEmpty();

        DrcStatePacket cleanPacket = Checker.EvaluateBoard(1, 0, draft.BuildDetections());
        cleanPacket.Violations.Should().NotContain(v => v.Severity == DrcSeverity.Critical);

        IReadOnlyList<ThermalHotspot> cleanHotspots =
            ExperimentalLabEngine.ComputeThermalHotspots(draft.Parts, cleanPacket, draft.Matrix);
        cleanHotspots.Should().HaveCount(draft.Parts.Count);
        cleanHotspots.Should().NotContain(h => h.IsOverloaded);

        // Inject a direct unprotected LED fault across rails (triggers TryReplacePins inside TryAutoFixViolations).
        draft.TryAdd(ComponentKind.Led, "TOP_VCC_29", "TOP_GND_29", "RED 2.0V", out DraftPart faultyLed, out string? addErr)
            .Should().BeTrue(addErr);
        int countWithFaultyLed = draft.Parts.Count;

        DrcStatePacket faultPacket = Checker.EvaluateBoard(2, 0, draft.BuildDetections());
        faultPacket.Violations.Should().Contain(v => v.Code == ViolationCode.ErcResistorCurrentLimitLow);

        IReadOnlyList<ThermalHotspot> faultHotspots =
            ExperimentalLabEngine.ComputeThermalHotspots(draft.Parts, faultPacket, draft.Matrix);
        faultHotspots.Single(h => h.TrackId == faultyLed.TrackId).IsOverloaded.Should().BeTrue();

        ExperimentalLabEngine.TryAutoFixViolations(draft, faultPacket, out int fixedCount, out string fixSummary)
            .Should().BeTrue(fixSummary);
        fixedCount.Should().BeGreaterOrEqualTo(1);

        // Exactly one LED (re-wired) + one newly inserted 220R resistor -> net +1 part, NO duplicate ghost LED.
        draft.Parts.Count.Should().Be(countWithFaultyLed + 1);
        draft.Parts.Count(p => p.TrackId == faultyLed.TrackId).Should().Be(1);

        DrcStatePacket afterPacket = Checker.EvaluateBoard(3, 0, draft.BuildDetections());
        afterPacket.Violations.Should().NotContain(v =>
            v.Code == ViolationCode.ErcResistorCurrentLimitLow ||
            v.Code == ViolationCode.ErcVccGndDirectShort);
    }

    [Fact]
    public void EndToEnd_AutoFix_SameStripAndReversedLed_ProducesZeroDuplicatePartsAndSupportsUndo()
    {
        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_8", "A8", "220R", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.Led, "TOP_GND_8", "B8", "RED 2.0V", out DraftPart reversedLed, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.Resistor, "C20", "D20", "220R", out DraftPart shortedResistor, out _).Should().BeTrue();

        DrcStatePacket packet = Checker.EvaluateBoard(1, 0, draft.BuildDetections());
        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcSameStripShort);

        ExperimentalLabEngine.TryAutoFixViolations(draft, packet, out int fixedCount, out _).Should().BeTrue();
        fixedCount.Should().BeGreaterOrEqualTo(2);

        // Part count must stay 3 (no duplicate parts spawned by TryReplacePins).
        draft.Parts.Should().HaveCount(3);
        draft.Parts.Select(p => p.TrackId).Should().OnlyHaveUniqueItems();
        draft.Parts.Single(p => p.TrackId == reversedLed.TrackId).Pins.Should().Equal("B8", "TOP_GND_8");
        draft.Parts.Single(p => p.TrackId == shortedResistor.TrackId).Pins[1].Should().NotBe("D20");

        DrcStatePacket after = Checker.EvaluateBoard(2, 0, draft.BuildDetections());
        after.Violations.Should().NotContain(v =>
            v.Code == ViolationCode.ErcSameStripShort ||
            v.Code == ViolationCode.ErcReversedPolarity);
    }

    [Theory]
    [InlineData(MicrocontrollerBoardType.ArduinoUnoR3R4, BoxCodingLanguage.ArduinoCpp, UsbCableKind.UsbB)]
    [InlineData(MicrocontrollerBoardType.Esp32DevKitV1, BoxCodingLanguage.ArduinoCpp, UsbCableKind.MicroUsb)]
    [InlineData(MicrocontrollerBoardType.Esp32DevKitV1, BoxCodingLanguage.MicroPython, UsbCableKind.MicroUsb)]
    [InlineData(MicrocontrollerBoardType.WemosEsp32S2Mini, BoxCodingLanguage.MicroPython, UsbCableKind.UsbTypeC)]
    [InlineData(MicrocontrollerBoardType.ArduinoMega2560, BoxCodingLanguage.ArduinoCpp, UsbCableKind.UsbB)]
    [InlineData(MicrocontrollerBoardType.Stm32BluePillF103C8, BoxCodingLanguage.ArduinoCpp, UsbCableKind.MicroUsb)]
    public void EndToEnd_BoxCoding_GenerateAnalyzeSimulateAndProgrammerCatalog_AcrossTargetBoards(
        MicrocontrollerBoardType boardType,
        BoxCodingLanguage language,
        UsbCableKind expectedRecommendedCable)
    {
        var blocks = new List<BoxBlockItem>
        {
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.WhenStarted, boardType),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ForeverLoop, boardType),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.ReadUltrasonicCm, boardType),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SetLed, boardType),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.SerialPrint, boardType),
            BoxCodeGenerator.CreateConfiguredBlock(BoxBlockKind.Delay, boardType)
        };

        string code = BoxCodeGenerator.GenerateCode(blocks, boardType, language);
        code.Should().NotBeNullOrWhiteSpace();

        BoxCodeAnalysisReport analysis = BoxCodeErrorAnalyzer.Analyze(code, language, boardType);
        analysis.IsValid.Should().BeTrue(string.Join(" | ", analysis.Diagnostics.Select(d => d.DisplaySummary)));
        analysis.ErrorCount.Should().Be(0);

        UsbCableKind recommended = ProgrammerCatalog.GetRecommendedCable(boardType);
        recommended.Should().Be(expectedRecommendedCable);

        ProgrammerCatalog.IsCableCompatible(recommended, boardType, out bool isNative, out string note)
            .Should().BeTrue();
        isNative.Should().BeTrue();
        note.Should().Contain("Native");

        ProgrammerCatalog.IsCableCompatible(UsbCableKind.None, boardType, out _, out _)
            .Should().BeFalse();

        string portDesc = ProgrammerCatalog.GetBoardPortDescription(boardType, recommended, "COM9");
        portDesc.Should().Contain("COM9");

        VirtualFirmwareRuntimeState simTick0 = VirtualFirmwareSimulator.SimulateFromCode(
            code,
            language,
            boardType,
            recommended,
            tickIndex: 0,
            userSerialInputs: new[] { "PING" });

        simTick0.IsRunning.Should().BeTrue();
        simTick0.BoardType.Should().Be(boardType);
        simTick0.CableKind.Should().Be(recommended);
        simTick0.WaveformSamples.Should().HaveCount(64);
        simTick0.SerialMonitorLines.Should().Contain(l => l.Contains("PING"));
    }

    [Fact]
    public void EndToEnd_RaspberryPiPico_And_JumperBridgedLedDrc_IntegratesWithDraftAndChecker()
    {
        DevBoardAndModuleRuleEngine.TryGetBoardDefinition("PICO", 40, out DevBoardDefinition picoDef)
            .Should().BeTrue();
        picoDef.ModelId.Should().Be("PICO");
        picoDef.OperatingVoltage.Should().BeApproximately(3.3, 0.01);
        picoDef.Is5VTolerant.Should().BeFalse();

        var draft = new CircuitDraft(Lookup);
        draft.TryAdd(ComponentKind.Resistor, "TOP_VCC_4", "A4", "15R", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.JumperWire, "B4", "B14", "SeriesLink", out _, out _).Should().BeTrue();
        draft.TryAdd(ComponentKind.Led, "A14", "TOP_GND_14", "RED 2.0V", out _, out _).Should().BeTrue();

        DrcStatePacket packet = Checker.EvaluateBoard(10, 0, draft.BuildDetections());
        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcResistorCurrentLimitLow);
    }

    [Fact]
    public void VramBudgetMonitor_GetSnapshotAndDispose_DoNotThrow()
    {
        using var monitor = new VramBudgetMonitor();
        VramSnapshot snap = monitor.GetSnapshot();
        snap.IsAvailable.Should().BeTrue();
        snap.TotalBytes.Should().BeGreaterThan(0);
    }

    [Fact]
    public void SkiaArOverlayRenderer_RendersProgrammerUsbCableAndThermalHeatmap_AndDisposesCleanly()
    {
        var draft = new CircuitDraft(Lookup);
        ExperimentalLabEngine.BuildCircuitFromPrompt(
            "ultrasonic distance radar with buzzer",
            draft,
            out MicrocontrollerBoardType boardType,
            out List<BoxBlockItem> blocks,
            out _).Should().BeTrue();

        DrcStatePacket state = Checker.EvaluateBoard(1, 0, draft.BuildDetections());
        IReadOnlyList<ThermalHotspot> hotspots =
            ExperimentalLabEngine.ComputeThermalHotspots(draft.Parts, state, draft.Matrix);

        string code = BoxCodeGenerator.GenerateCode(blocks, boardType, BoxCodingLanguage.ArduinoCpp);
        VirtualFirmwareRuntimeState fwState = VirtualFirmwareSimulator.SimulateFromCode(
            code,
            BoxCodingLanguage.ArduinoCpp,
            boardType,
            UsbCableKind.UsbB,
            tickIndex: 2);

        using var renderer = new SkiaArOverlayRenderer
        {
            ActiveUsbCableKind = UsbCableKind.UsbB,
            IsProgrammerConnected = false,
            IsProgrammerUploading = false,
            ProgrammerUploadProgress = 1.0f,
            ActiveFirmwareState = fwState,
            ActiveThermalHotspots = hotspots,
            ActiveMicrocontrollers = new List<OffBoardMicrocontrollerVisualState>
            {
                new(
                    "MCU_1",
                    boardType,
                    OffBoardDockPlacement.RightMargin,
                    0,
                    null,
                    new HashSet<string>(),
                    new HashSet<string>())
            },
            ActiveDevBoardWires = new List<OffBoardJumperWire>
            {
                new("W1", "5V_RAIL", "MCU1:5V", "BB:TOP_VCC_1", "#EF4444"),
                new("W2", "GND_RAIL", "MCU1:GND_1", "BB:TOP_GND_1", "#1E293B")
            }
        };
        renderer.Layers.ShowHardwareProgrammer = true;
        renderer.Layers.ShowThermalHeatmap = true;

        using var surface = SKSurface.Create(new SKImageInfo(1600, 960));
        renderer.RenderCadViewport(
            surface.Canvas,
            1600,
            960,
            warpedBoardFrame: null,
            drcState: state);

        renderer.LastProgrammerBodyRect.Width.Should().BeGreaterThan(10f);
        renderer.LastProgrammerUploadButtonRect.Should().Be(SKRect.Empty);

        renderer.IsProgrammerConnected = true;
        renderer.RenderCadViewport(
            surface.Canvas,
            1600,
            960,
            warpedBoardFrame: null,
            drcState: state);

        renderer.LastProgrammerBodyRect.Width.Should().BeGreaterThan(10f);
        renderer.LastProgrammerUploadButtonRect.Width.Should().BeGreaterThan(10f);

        byte[] png = renderer.CaptureFullBoardPng(state);

        png.Should().NotBeNullOrEmpty();
        png.Length.Should().BeGreaterThan(1024);
    }

    [Fact]
    public void BoxCodingStudioViewModel_PaletteSearchNestingErrorCheckerAndSendToProgrammer_WorkEndToEnd()
    {
        var vm = new BoxCodingStudioViewModel(draftParts: null, initialBoard: MicrocontrollerBoardType.Esp32DevKitV1);
        vm.Blocks.Should().NotBeEmpty();
        vm.DisplayPaletteBlocks.Count.Should().BeGreaterOrEqualTo(18);

        vm.SearchFilterText = "Ultrasonic";
        vm.DisplayPaletteBlocks.Should().ContainSingle(p => p.Kind == BoxBlockKind.ReadUltrasonicCm);
        vm.SearchFilterText = string.Empty;

        vm.AddBlockCommand.Execute(vm.PaletteBlocks.First(p => p.Kind == BoxBlockKind.MapRange));
        vm.AddBlockCommand.Execute(vm.PaletteBlocks.First(p => p.Kind == BoxBlockKind.PlayToneNote));
        vm.AddBlockCommand.Execute(vm.PaletteBlocks.First(p => p.Kind == BoxBlockKind.RgbLedColor));
        vm.AddBlockCommand.Execute(vm.PaletteBlocks.First(p => p.Kind == BoxBlockKind.ReadUltrasonicCm));

        int countBeforeDuplicate = vm.Blocks.Count;
        vm.DuplicateBlockCommand.Execute(vm.Blocks.Last());
        vm.Blocks.Count.Should().Be(countBeforeDuplicate + 1);

        vm.CheckCodeErrorsCommand.Execute(null);
        vm.HasCodeErrors.Should().BeFalse();

        // Inject a syntax error manually into GeneratedCode and verify live detection + resync
        vm.GeneratedCode += "\nvoid brokenFunc( {\n";
        vm.IsManualCodeEdit.Should().BeTrue();
        vm.CheckCodeErrorsCommand.Execute(null);
        vm.HasCodeErrors.Should().BeTrue();
        vm.CodeDiagnostics.Should().NotBeEmpty();

        vm.ResyncCodeFromBlocksCommand.Execute(null);
        vm.IsManualCodeEdit.Should().BeFalse();
        vm.HasCodeErrors.Should().BeFalse();

        bool programmerEventFired = false;
        vm.SendToProgrammerRequested += (code, lang, board) =>
        {
            programmerEventFired = true;
            code.Should().NotBeNullOrWhiteSpace();
            board.Should().Be(MicrocontrollerBoardType.Esp32DevKitV1);
        };
        vm.SendToProgrammerCommand.Execute(null);
        programmerEventFired.Should().BeTrue();
    }

    [Fact]
    public void MainWorkstationViewModel_And_WpfWindows_SmokeTestOnStaThread_SucceedsWithoutErrors()
    {
        Exception? staException = null;

        var thread = new Thread(() =>
        {
            try
            {
                var dispatcher = Dispatcher.CurrentDispatcher;
                using var vm = new MainWorkstationViewModel(dispatcher);
                vm.Initialize();

                // 1. Select USB cables & connect programmer
                vm.SelectUsbCableCommand.Execute("typec");
                vm.SelectedUsbCableKind.Should().Be(UsbCableKind.UsbTypeC);
                vm.SelectUsbCableCommand.Execute("usbb");
                vm.SelectedUsbCableKind.Should().Be(UsbCableKind.UsbB);

                vm.ConnectUsbCableCommand.Execute(null);
                vm.IsProgrammerCableConnected.Should().BeTrue();
                vm.ConnectedBoardSummary.Should().NotBeNullOrWhiteSpace();

                // 2. Upload firmware to virtual MCU and verify running state + oscilloscope + serial monitor
                ProgrammerUploadResult uploadResult = vm
                    .UploadFirmwareToBoardAsync()
                    .GetAwaiter()
                    .GetResult();
                uploadResult.Success.Should().BeTrue(uploadResult.LogOutput);
                vm.IsVirtualFirmwareRunning.Should().BeTrue();
                vm.OscilloscopeSamples.Should().NotBeEmpty();
                vm.VirtualSerialLog.Should().Contain("System Initialized");

                ProgrammerUploadResult comUploadResult = vm
                    .UploadFirmwareCoreAsync(vm.ActiveFirmwareCode, BoxCodingLanguage.ArduinoCpp, "COM99")
                    .GetAwaiter()
                    .GetResult();
                comUploadResult.Success.Should().BeTrue(comUploadResult.LogOutput);
                comUploadResult.UsedHardwareCli.Should().BeFalse();
                comUploadResult.LogOutput.Should().Contain("virtual execution");

                vm.SerialInputCommandText = "STATUS";
                vm.SendSerialInputCommand.Execute(null);
                vm.VirtualSerialLog.Should().Contain("STATUS");

                // 3. Experimental Lab: Natural language prompt -> circuit synthesis + thermal heatmap + DRC auto-fix
                vm.GenerateCircuitFromPromptCommand.Execute("Traffic light with red yellow green LEDs");
                vm.DraftParts.Should().NotBeEmpty();

                vm.ToggleThermalHeatmapCommand.Execute(null);
                vm.ShowThermalHeatmap.Should().BeTrue();
                vm.ThermalSummaryText.Should().NotBe("Thermal overlay off");

                vm.AutoFixAllViolationsCommand.Execute(null);
                vm.StopVirtualFirmwareCommand.Execute(null);
                vm.IsVirtualFirmwareRunning.Should().BeFalse();

                // 4. Instantiate WPF dialogs/windows on STA to verify XAML resource loading and bindings
                var studioWindow = new BoxCodingStudioWindow(Array.Empty<DraftPart>(), vm.SelectedDevBoardType);
                studioWindow.DataContext.Should().BeOfType<BoxCodingStudioViewModel>();
                studioWindow.Close();

                var uploadDialog = new ProgrammerUploadDialog(vm, vm.ActiveFirmwareCode, BoxCodingLanguage.ArduinoCpp);
                uploadDialog.DataContext.Should().BeSameAs(vm);
                uploadDialog.Close();
            }
            catch (Exception ex)
            {
                staException = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        staException.Should().BeNull();
    }
}
