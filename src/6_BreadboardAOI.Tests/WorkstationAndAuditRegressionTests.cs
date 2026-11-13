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

                // 1. Disconnected upload must stay blocked without silently connecting the USB cable
                vm.DisconnectUsbCable();
                vm.IsProgrammerCableConnected.Should().BeFalse();
                vm.IsGlobalBoardConnected.Should().BeFalse();
                ProgrammerUploadResult blockedWhileDisconnected = vm
                    .UploadFirmwareCoreAsync(vm.ActiveFirmwareCode, BoxCodingLanguage.ArduinoCpp, ProgrammerIdeCatalog.VirtualSimPortDisplay)
                    .GetAwaiter()
                    .GetResult();
                blockedWhileDisconnected.Success.Should().BeFalse();
                vm.IsProgrammerCableConnected.Should().BeFalse("upload must not silently connect cable behind user's back");

                // 2. Select USB cables & connect programmer manually
                vm.SelectUsbCableCommand.Execute("typec");
                vm.SelectedUsbCableKind.Should().Be(UsbCableKind.UsbTypeC);
                vm.SelectUsbCableCommand.Execute("usbb");
                vm.SelectedUsbCableKind.Should().Be(UsbCableKind.UsbB);

                vm.ConnectUsbCableCommand.Execute(null);
                vm.IsProgrammerCableConnected.Should().BeTrue();
                vm.IsGlobalBoardConnected.Should().BeTrue();
                vm.GlobalConnectionBadgeText.Should().Contain("CONNECTED");
                vm.ConnectedBoardSummary.Should().NotBeNullOrWhiteSpace();

                // 3. Upload firmware to virtual MCU and verify running state + oscilloscope + serial monitor
                ProgrammerUploadResult uploadResult = vm
                    .UploadFirmwareToBoardAsync()
                    .GetAwaiter()
                    .GetResult();
                uploadResult.Success.Should().BeTrue(uploadResult.LogOutput);
                vm.IsVirtualFirmwareRunning.Should().BeTrue();
                vm.OscilloscopeSamples.Should().NotBeEmpty();
                vm.VirtualSerialLog.Should().Contain("System Initialized");

                // 4. Physical COM upload failure must be reported honestly unless Virtual Fallback is explicitly opted into
                vm.AllowVirtualFallbackOnPhysicalPort.Should().BeFalse();
                ProgrammerUploadResult comFailResult = vm
                    .UploadFirmwareCoreAsync(vm.ActiveFirmwareCode, BoxCodingLanguage.ArduinoCpp, "COM99 (Non-Existent Hardware)")
                    .GetAwaiter()
                    .GetResult();
                comFailResult.Success.Should().BeFalse(comFailResult.LogOutput);
                comFailResult.TargetPort.Should().Be("COM99");
                comFailResult.LogOutput.Should().Contain("PHYSICAL UPLOAD");

                vm.AllowVirtualFallbackOnPhysicalPort = true;
                ProgrammerUploadResult comFallbackResult = vm
                    .UploadFirmwareCoreAsync(vm.ActiveFirmwareCode, BoxCodingLanguage.ArduinoCpp, "COM99")
                    .GetAwaiter()
                    .GetResult();
                comFallbackResult.Success.Should().BeTrue(comFallbackResult.LogOutput);
                comFallbackResult.UsedHardwareCli.Should().BeFalse();
                comFallbackResult.LogOutput.Should().Contain("virtual execution");
                vm.AllowVirtualFallbackOnPhysicalPort = false;

                // 5. Cross-mode manual cable placement & right-click unplug work even when IsBuildMode == false
                vm.IsBuildMode = false;
                vm.StartCablePlacementFromRack(UsbCableKind.UsbB, 50f, 50f);
                vm.PendingCableDragKind.Should().Be(UsbCableKind.UsbB);
                vm.CompleteCablePlacementOnBoard("MCU_1", UsbCableKind.UsbB);
                vm.IsProgrammerCableConnected.Should().BeTrue();

                // Right-click on active plug/jack unplugs across non-Build modes
                using (var surface = SKSurface.Create(new SKImageInfo(1600, 960)))
                {
                    vm.Render(surface.Canvas, 1600, 960);
                    SKRect jackRect = vm.Renderer.LastProgrammerJackRect;
                    jackRect.Width.Should().BeGreaterThan(2f);
                    vm.TryHandleProgrammerViewportAction(jackRect.MidX, jackRect.MidY, secondary: true).Should().BeTrue();
                    vm.IsProgrammerCableConnected.Should().BeFalse();
                }

                vm.SerialInputCommandText = "STATUS";
                vm.SendSerialInputCommand.Execute(null);
                vm.VirtualSerialLog.Should().Contain("STATUS");

                // 6. Experimental Lab: Natural language prompt -> circuit synthesis + thermal heatmap + DRC auto-fix
                vm.GenerateCircuitFromPromptCommand.Execute("Traffic light with red yellow green LEDs");
                vm.DraftParts.Should().NotBeEmpty();

                vm.ToggleThermalHeatmapCommand.Execute(null);
                vm.ShowThermalHeatmap.Should().BeTrue();
                vm.ThermalSummaryText.Should().NotBe("Thermal overlay off");

                vm.AutoFixAllViolationsCommand.Execute(null);
                vm.StopVirtualFirmwareCommand.Execute(null);
                vm.IsVirtualFirmwareRunning.Should().BeFalse();

                // 7. Instantiate WPF dialogs/windows on STA to verify XAML resource loading and bindings
                var studioWindow = new BoxCodingStudioWindow(Array.Empty<DraftPart>(), vm.SelectedDevBoardType, vm.ExtractWiredMcuPins());
                studioWindow.DataContext.Should().BeOfType<BoxCodingStudioViewModel>();
                studioWindow.Close();

                var uploadDialog = new ProgrammerUploadDialog(vm, vm.ActiveFirmwareCode, BoxCodingLanguage.ArduinoCpp);
                uploadDialog.DataContext.Should().BeSameAs(vm);
                uploadDialog.Close();

                if (System.Windows.Application.Current is null)
                {
                    var wpfApp = new System.Windows.Application
                    {
                        ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
                    };
                    wpfApp.Resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
                    {
                        Source = new Uri("/BreadboardAOI.App;component/Themes/SpectrumDarkTheme.xaml", UriKind.Relative)
                    });
                }

                var mainWindow = new MainWindow();
                mainWindow.DataContext.Should().BeOfType<MainWorkstationViewModel>();
                mainWindow.Close();
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

    [Fact]
    public void CadRenderer_25DCableRack_VerticalS2MiniReceptacle_And_DualBoardTargeting_Work()
    {
        var rules = new DevBoardAndModuleRuleEngine();
        var offBoardRenderer = new OffBoardMicrocontrollerRenderer(rules);
        var breadboardBounds = new SKRect(100f, 100f, 900f, 400f);

        RenderedMicrocontrollerLayout unoLayout = offBoardRenderer.ComputeBoardLayout(
            new OffBoardMicrocontrollerVisualState(
                "MCU_1",
                MicrocontrollerBoardType.ArduinoUnoR3R4,
                OffBoardDockPlacement.BottomMargin,
                DockSlotIndex: 0,
                HoveredPinCanonicalId: null,
                ConnectedPinCanonicalIds: new HashSet<string>(),
                ViolatingPinCanonicalIds: new HashSet<string>()),
            breadboardBounds,
            pixelsPerMm: 4f);

        RenderedMicrocontrollerLayout s2Layout = offBoardRenderer.ComputeBoardLayout(
            new OffBoardMicrocontrollerVisualState(
                "MCU_2",
                MicrocontrollerBoardType.WemosEsp32S2Mini,
                OffBoardDockPlacement.BottomMargin,
                DockSlotIndex: 1,
                HoveredPinCanonicalId: null,
                ConnectedPinCanonicalIds: new HashSet<string>(),
                ViolatingPinCanonicalIds: new HashSet<string>()),
            breadboardBounds,
            pixelsPerMm: 4f);

        unoLayout.IsUsbReceptacleVertical.Should().BeFalse();
        unoLayout.NativeUsbKind.Should().Be(UsbCableKind.UsbB);
        unoLayout.UsbReceptacleRectCanvas.Width.Should().BeGreaterThan(5f);

        s2Layout.IsUsbReceptacleVertical.Should().BeTrue("Wemos ESP32-S2 Mini bottom Type-C port accepts vertical cable insertion");
        s2Layout.NativeUsbKind.Should().Be(UsbCableKind.UsbTypeC);
        s2Layout.UsbReceptacleRectCanvas.Width.Should().BeGreaterThan(5f);

        var renderer = new SkiaArOverlayRenderer
        {
            ActiveUsbCableKind = UsbCableKind.UsbTypeC,
            IsProgrammerConnected = true,
            TargetProgrammerBoardInstanceId = "MCU_2",
            ActiveMicrocontrollers = new[]
            {
                new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.BottomMargin, 0, null, new HashSet<string>(), new HashSet<string>()),
                new OffBoardMicrocontrollerVisualState("MCU_2", MicrocontrollerBoardType.WemosEsp32S2Mini, OffBoardDockPlacement.BottomMargin, 1, null, new HashSet<string>(), new HashSet<string>())
            }
        };
        renderer.Layers.ShowHardwareProgrammer = true;

        using var surface = SKSurface.Create(new SKImageInfo(1600, 960));
        renderer.RenderCadViewport(surface.Canvas, 1600, 960, warpedBoardFrame: null, drcState: null);

        renderer.LastCableRackBodyRect.Width.Should().BeGreaterThan(10f);
        renderer.LastProgrammerSendButtonRect.Width.Should().BeGreaterThan(10f);
        renderer.LastProgrammerJackRect.Width.Should().BeGreaterThan(2f);

        // Connected plug/jack hit-test works
        renderer.TryHitTestConnectedCableOrPlug(renderer.LastProgrammerJackRect.MidX, renderer.LastProgrammerJackRect.MidY)
            .Should().BeTrue();
    }

    [Fact]
    public void BoxCodingStudioViewModel_WiredPinsPropagation_QuickFix_And_DebuggerCommands_Work()
    {
        var studioVm = new BoxCodingStudioViewModel(
            draftParts: null,
            initialBoard: MicrocontrollerBoardType.ArduinoUnoR3R4,
            wiredMcuPins: new[] { "12" });

        studioVm.GeneratedCode = """
            void setup() {
                Serial.begin(115200);
                pinMode(12, OUTPUT);
                pinMode(13, OUTPUT);
            }
            void loop() {
                int a = 2;
                if (a = 2) {
                    digitalWrite(12, HIGH);
                    digitalWrite(13, HIGH);
                }
                delay(100);
            }
            """;

        studioVm.CodeDiagnostics.Should().Contain(d => d.Code == "BC034" && d.Message.Contains("13"));
        studioVm.CodeDiagnostics.Should().NotContain(d => d.Code == "BC034" && d.Message.Contains("12"));

        // Propagating wired pin 13 eliminates the unwired warning
        studioVm.UpdateWiredMcuPins(new[] { "12", "13" });
        studioVm.CodeDiagnostics.Should().NotContain(d => d.Code == "BC034");

        // JumpToDiagnosticCommand fires EditorCaretJumpRequested
        int jumpedLine = -1;
        studioVm.EditorCaretJumpRequested += (line, _) => jumpedLine = line;
        var fixable = studioVm.CodeDiagnostics.First(d => d.Code == "BC040" && d.HasQuickFix);
        studioVm.JumpToDiagnosticCommand.Execute(fixable);
        jumpedLine.Should().Be(fixable.Line);

        // ApplyQuickFixCommand repairs if (a = 2) -> if (a == 2)
        studioVm.ApplyQuickFixCommand.Execute(fixable);
        studioVm.GeneratedCode.Should().Contain("if (a == 2)");
        studioVm.CodeDiagnostics.Should().NotContain(d => d.Code == "BC040");

        // Interactive Debugger commands work via ViewModel
        studioVm.ToggleBreakpointAtLine(9);
        studioVm.DebugBreakpoints.Should().Contain(9);
        studioVm.DebugStartOrContinueCommand.Execute(null);
        studioVm.DebugCurrentLine.Should().Be(9);
        studioVm.DebugStepOverCommand.Execute(null);
        studioVm.DebugWatchPins.Should().Contain(p => p.PinName == "12" && p.IsHigh);
        studioVm.DebugResetCommand.Execute(null);
    }

    [Fact]
    public void MicroPython_PhysicalComUpload_FailsHonestly_And_AdapterMismatchBlocksUpload()
    {
        Exception? staException = null;
        var thread = new Thread(() =>
        {
            try
            {
                using var vm = new MainWorkstationViewModel(Dispatcher.CurrentDispatcher);
                vm.Initialize();

                // Plug mismatch: Type-C on Arduino Uno with Adapter OFF must block upload
                vm.IsUsbAdapterEnabled = false;
                vm.SelectUsbCableCommand.Execute("typec");
                vm.ConnectUsbCableCommand.Execute(null);
                vm.IsCableMismatchWarning.Should().BeTrue();
                vm.IsGlobalBoardConnected.Should().BeFalse();

                ProgrammerUploadResult mismatchRes = vm
                    .UploadFirmwareWithOptionsAsync(
                        "from machine import Pin\n",
                        BoxCodingLanguage.MicroPython,
                        ProgrammerIdeCatalog.VirtualSimPortDisplay,
                        verifyOnly: false)
                    .GetAwaiter()
                    .GetResult();
                mismatchRes.Success.Should().BeFalse();

                // Enable adapter + native Micro-USB for ESP32 & test honest MicroPython physical COM failure
                vm.SelectDevBoardTypeCommand.Execute("esp32");
                vm.SelectUsbCableCommand.Execute("micro");
                vm.ConnectUsbCableCommand.Execute(null);
                vm.IsGlobalBoardConnected.Should().BeTrue();

                ProgrammerUploadResult pyComResult = vm
                    .UploadFirmwareWithOptionsAsync(
                        "from machine import Pin\np = Pin(2, Pin.OUT)\np.value(1)\n",
                        BoxCodingLanguage.MicroPython,
                        "COM99 (ESP32 MicroPython)",
                        verifyOnly: false)
                    .GetAwaiter()
                    .GetResult();
                pyComResult.Success.Should().BeFalse();
                pyComResult.TargetPort.Should().Be("COM99");
                pyComResult.LogOutput.Should().Contain("PHYSICAL UPLOAD FAILED");
                pyComResult.LogOutput.Should().NotContain("Virtual execution started");

                // Verify-only with a physical COM port selected must NOT attempt physical COM flash or fail on missing mpremote
                ProgrammerUploadResult pyVerifyOnCom = vm
                    .UploadFirmwareWithOptionsAsync(
                        "from machine import Pin\np = Pin(2, Pin.OUT)\np.value(1)\n",
                        BoxCodingLanguage.MicroPython,
                        "COM99 (ESP32 MicroPython)",
                        verifyOnly: true)
                    .GetAwaiter()
                    .GetResult();
                pyVerifyOnCom.Success.Should().BeTrue(pyVerifyOnCom.LogOutput);
                pyVerifyOnCom.LogOutput.Should().NotContain("mpremote connect COM99");
                pyVerifyOnCom.LogOutput.Should().NotContain("PHYSICAL UPLOAD FAILED");

                // Dual-board BottomMargin and RightMargin layouts never overlap even for Mega + Uno
                var rules = new DevBoardAndModuleRuleEngine();
                var mcuRenderer = new OffBoardMicrocontrollerRenderer(rules);
                var bbBounds = new SKRect(100f, 100f, 760f, 320f);
                var dualBottom = mcuRenderer.ComputeBoardLayouts(
                    new[]
                    {
                        new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.ArduinoMega2560, OffBoardDockPlacement.BottomMargin, 0, null, new HashSet<string>(), new HashSet<string>()),
                        new OffBoardMicrocontrollerVisualState("MCU_2", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.BottomMargin, 1, null, new HashSet<string>(), new HashSet<string>())
                    },
                    bbBounds,
                    pixelsPerMm: 4f);
                dualBottom.Should().HaveCount(2);
                dualBottom[0].BoardBoundsCanvas.IntersectsWith(dualBottom[1].BoardBoundsCanvas).Should().BeFalse("dual BottomMargin boards must never overlap");
                dualBottom[1].BoardBoundsCanvas.Left.Should().BeGreaterThan(dualBottom[0].BoardBoundsCanvas.Right);

                var dualRight = mcuRenderer.ComputeBoardLayouts(
                    new[]
                    {
                        new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.RightMargin, 0, null, new HashSet<string>(), new HashSet<string>()),
                        new OffBoardMicrocontrollerVisualState("MCU_2", MicrocontrollerBoardType.WemosEsp32S2Mini, OffBoardDockPlacement.RightMargin, 1, null, new HashSet<string>(), new HashSet<string>())
                    },
                    bbBounds,
                    pixelsPerMm: 4f);
                dualRight.Should().HaveCount(2);
                dualRight[0].BoardBoundsCanvas.IntersectsWith(dualRight[1].BoardBoundsCanvas).Should().BeFalse("dual RightMargin boards must never overlap");
                dualRight[1].BoardBoundsCanvas.Top.Should().BeGreaterThan(dualRight[0].BoardBoundsCanvas.Bottom);
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

    [Fact]
    public void CadRenderer_HoverTooltip_UpdatesAndClearsForProgrammerJackUsbPortAndCableRack()
    {
        var renderer = new SkiaArOverlayRenderer
        {
            ActiveUsbCableKind = UsbCableKind.UsbB,
            IsProgrammerConnected = false,
            TargetProgrammerBoardInstanceId = "MCU_1",
            ActiveMicrocontrollers = new[]
            {
                new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.BottomMargin, 0, null, new HashSet<string>(), new HashSet<string>())
            }
        };
        renderer.Layers.ShowHardwareProgrammer = true;
        renderer.Layers.ShowHoverInspector = true;

        using var surface = SKSurface.Create(new SKImageInfo(1600, 960));
        renderer.RenderCadViewport(surface.Canvas, 1600, 960, warpedBoardFrame: null, drcState: null);

        // Hover Programmer USB jack -> non-breadboard hover state active, tooltip renders cleanly
        renderer.UpdateHover(renderer.LastProgrammerJackRect.MidX, renderer.LastProgrammerJackRect.MidY).Should().BeTrue();
        renderer.HoveredProgrammerJack.Should().BeTrue();
        renderer.IsHoveringPin.Should().BeFalse();
        renderer.RenderCadViewport(surface.Canvas, 1600, 960, warpedBoardFrame: null, drcState: null);

        // Moving onto empty pasteboard clears programmer/cable hover flags cleanly
        renderer.UpdateHover(2f, 2f).Should().BeFalse();
        renderer.HoveredProgrammerJack.Should().BeFalse();
        renderer.HoveredCableRackKind.Should().Be(UsbCableKind.None);
        renderer.HoveredUsbReceptacleBoardId.Should().BeNull();
    }

    [Fact]
    public void OffBoardRenderer_WemosS2Mini_BottomCaptionExtentsClearUsbCReceptacleAndCable()
    {
        var mcuRenderer = new OffBoardMicrocontrollerRenderer(new DevBoardAndModuleRuleEngine());
        float pxPerMm = 6.0f;
        McuLabelExtents s2Extents = mcuRenderer.MeasureLabelExtents(MicrocontrollerBoardType.WemosEsp32S2Mini, pxPerMm);

        s2Extents.BottomPx.Should().BeGreaterThanOrEqualTo(10.5f * pxPerMm,
            "Wemos S2 Mini bottom USB-C receptacle + plugged ANKER 100W barrel + cable turn require dedicated bottom clearance before the board caption.");

        var renderer = new SkiaArOverlayRenderer
        {
            ActiveUsbCableKind = UsbCableKind.UsbTypeC,
            IsProgrammerConnected = true,
            TargetProgrammerBoardInstanceId = "MCU_1",
            ActiveMicrocontrollers = new[]
            {
                new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.WemosEsp32S2Mini, OffBoardDockPlacement.RightMargin, 0, null, new HashSet<string>(), new HashSet<string>())
            }
        };
        renderer.Layers.ShowHardwareProgrammer = true;

        using var surface = SKSurface.Create(new SKImageInfo(1600, 980));
        renderer.RenderCadViewport(surface.Canvas, 1600, 980, warpedBoardFrame: null, drcState: null);

        renderer.LastProgrammerBodyRect.IsEmpty.Should().BeFalse();
        renderer.LastProgrammerUploadButtonRect.IsEmpty.Should().BeFalse();
        renderer.LastCableRackBodyRect.IsEmpty.Should().BeFalse();
        renderer.TryHitTestConnectedCableOrPlug(renderer.LastProgrammerJackRect.MidX - 4f, renderer.LastProgrammerJackRect.MidY)
            .Should().BeTrue();
    }

    [Fact]
    public void SkiaRenderer_UnoUsbB_S2MiniAnker_And_DualBoardDragPreview_RenderWithoutOverlapOrExceptions()
    {
        var renderer = new SkiaArOverlayRenderer
        {
            TargetProgrammerBoardInstanceId = "MCU_1",
            ActiveUsbCableKind = UsbCableKind.UsbB,
            IsProgrammerConnected = true,
            ActiveMicrocontrollers = new[]
            {
                new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.RightMargin, 0, null, new HashSet<string>(), new HashSet<string>())
            }
        };
        renderer.Layers.ShowHardwareProgrammer = true;

        using var surface = SKSurface.Create(new SKImageInfo(1600, 980));
        renderer.RenderCadViewport(surface.Canvas, 1600, 980, warpedBoardFrame: null, drcState: null);
        renderer.LastProgrammerSendButtonRect.Width.Should().BeGreaterThan(20f);

        // Dual-board + interactive drag preview over MCU_2 vertical S2 Mini port
        renderer.IsProgrammerConnected = false;
        renderer.ActiveMicrocontrollers = new[]
        {
            new OffBoardMicrocontrollerVisualState("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.RightMargin, 0, null, new HashSet<string>(), new HashSet<string>()),
            new OffBoardMicrocontrollerVisualState("MCU_2", MicrocontrollerBoardType.WemosEsp32S2Mini, OffBoardDockPlacement.RightMargin, 1, null, new HashSet<string>(), new HashSet<string>())
        };
        renderer.RenderCadViewport(surface.Canvas, 1600, 980, warpedBoardFrame: null, drcState: null);

        renderer.PendingCableDragKind = UsbCableKind.UsbTypeC;
        renderer.CableDragCursorCanvas = new SKPoint(renderer.LastProgrammerJackRect.MidX - 80f, renderer.LastProgrammerJackRect.MidY + 120f);
        renderer.HoveredUsbReceptacleBoardId = "MCU_2";
        renderer.RenderCadViewport(surface.Canvas, 1600, 980, warpedBoardFrame: null, drcState: null);

        renderer.LastCableRackBodyRect.IsEmpty.Should().BeFalse();
        renderer.LastProgrammerUploadButtonRect.Should().Be(SKRect.Empty);
        renderer.LastProgrammerSendButtonRect.IsEmpty.Should().BeFalse();
    }
}
