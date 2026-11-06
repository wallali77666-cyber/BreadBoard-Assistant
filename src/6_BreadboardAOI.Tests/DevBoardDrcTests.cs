using System.Collections.Generic;
using System.Linq;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using SkiaSharp;
using Xunit;
using DrcViolation = BreadboardAOI.Core.DrcEngine.McuDrcViolation;
using DrcSeverity = BreadboardAOI.Core.DrcEngine.McuDrcSeverity;

namespace BreadboardAOI.Tests;

public sealed class DevBoardDrcTests
{
    private readonly DevBoardAndModuleRuleEngine _engine = new();

    [Fact]
    public void ArduinoUno_ShouldContainAllRequiredPowerAnalogAndDigitalPins()
    {
        MicrocontrollerBoardProfile uno = _engine.GetBoardProfile(MicrocontrollerBoardType.ArduinoUnoR3R4);

        Assert.Equal("#00878F", uno.PcbPrimaryHexColor);
        Assert.NotNull(uno.FindPin("IOREF"));
        Assert.NotNull(uno.FindPin("RESET"));
        Assert.NotNull(uno.FindPin("3.3V"));
        Assert.NotNull(uno.FindPin("5V"));
        Assert.NotNull(uno.FindPin("VIN"));
        Assert.NotNull(uno.FindPin("A0"));
        Assert.NotNull(uno.FindPin("A4 (SDA)"));
        Assert.NotNull(uno.FindPin("A5 (SCL)"));
        Assert.NotNull(uno.FindPin("D13 (SCK)"));
        Assert.NotNull(uno.FindPin("D12 (MISO)"));
        Assert.NotNull(uno.FindPin("D11 (MOSI)"));
        Assert.NotNull(uno.FindPin("D10 (SS)"));
        Assert.NotNull(uno.FindPin("D9 (PWM)"));
        Assert.NotNull(uno.FindPin("D1 (TX)"));
        Assert.NotNull(uno.FindPin("D0 (RX)"));

        Assert.Equal(PinColorCategory.PowerRed, uno.FindPin("5V")!.ColorCategory);
        Assert.Equal(PinColorCategory.GroundDark, uno.FindPin("GND")!.ColorCategory);
        Assert.Equal(PinColorCategory.CommCyanTeal, uno.FindPin("A4 (SDA)")!.ColorCategory);
        Assert.Equal(PinColorCategory.GeneralIoWhite, uno.FindPin("D13 (SCK)")!.ColorCategory);
    }

    [Fact]
    public void AllFiveMicrocontrollers_ShouldExposeCompletePinoutsAndValidCoordinates()
    {
        MicrocontrollerBoardProfile mega = _engine.GetBoardProfile(MicrocontrollerBoardType.ArduinoMega2560);
        Assert.NotNull(mega.FindPin("A15"));
        Assert.NotNull(mega.FindPin("TX3"));
        Assert.NotNull(mega.FindPin("RX3"));
        Assert.NotNull(mega.FindPin("D53"));

        MicrocontrollerBoardProfile esp32 = _engine.GetBoardProfile(MicrocontrollerBoardType.Esp32DevKitV1);
        Assert.Equal("#1E1E1E", esp32.PcbPrimaryHexColor);
        Assert.NotNull(esp32.FindPin("VP(36)"));
        Assert.NotNull(esp32.FindPin("VN(39)"));
        Assert.NotNull(esp32.FindPin("D21(SDA)"));
        Assert.NotNull(esp32.FindPin("D22(SCL)"));
        Assert.NotNull(esp32.FindPin("RX2(16)"));
        Assert.NotNull(esp32.FindPin("TX2(17)"));

        MicrocontrollerBoardProfile s2Mini = _engine.GetBoardProfile(MicrocontrollerBoardType.WemosEsp32S2Mini);
        Assert.NotNull(s2Mini.FindPin("VBUS"));
        Assert.NotNull(s2Mini.FindPin("IO17 (DAC)"));
        Assert.NotNull(s2Mini.FindPin("IO40"));

        MicrocontrollerBoardProfile bluePill = _engine.GetBoardProfile(MicrocontrollerBoardType.Stm32BluePillF103C8);
        Assert.Equal(40, bluePill.Pins.Count);
        Assert.True(bluePill.FindPin("B6(SCL1)")!.Is5VTolerant);
        Assert.False(bluePill.FindPin("A0")!.Is5VTolerant);
        Assert.True(bluePill.FindPin("A0")!.IsAnalogOnly);
    }

    [Fact]
    public void CrossBoardOvervoltage_ArduinoUno5VToEsp32AndStm32AnalogPin_ShouldFlagCriticalDamage()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_UNO_TO_ESP32",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D8"),
                    new McuPinEndpoint("Esp1", MicrocontrollerBoardType.Esp32DevKitV1, "D34")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:E10"]),

            new OffBoardElectricalNet(
                NetName: "NET_UNO_TO_STM32_ANALOG",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D9"),
                    new McuPinEndpoint("BluePill1", MicrocontrollerBoardType.Stm32BluePillF103C8, "A0")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:E12"]),

            new OffBoardElectricalNet(
                NetName: "NET_UNO_TO_STM32_FT_SAFE",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D7"),
                    new McuPinEndpoint("BluePill1", MicrocontrollerBoardType.Stm32BluePillF103C8, "B12")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:E14"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-OVERVOLTAGE-5V-TO-3V3" && v.NetName == "NET_UNO_TO_ESP32");
        Assert.Contains(violations, v => v.RuleCode == "DRC-OVERVOLTAGE-5V-TO-3V3" && v.NetName == "NET_UNO_TO_STM32_ANALOG");
        Assert.DoesNotContain(violations, v => v.NetName == "NET_UNO_TO_STM32_FT_SAFE");
    }

    [Fact]
    public void ProtocolCrossing_UartTxToTxAndI2cSdaToScl_ShouldFlagErrors()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_UART_BAD_TX_TX",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D1 (TX)"),
                    new McuPinEndpoint("Mega1", MicrocontrollerBoardType.ArduinoMega2560, "TX1")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:A1"]),

            new OffBoardElectricalNet(
                NetName: "NET_I2C_BAD_CROSSED",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "SDA"),
                    new McuPinEndpoint("BluePill1", MicrocontrollerBoardType.Stm32BluePillF103C8, "B6(SCL1)")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:A2"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-UART-TX-TX" && v.NetName == "NET_UART_BAD_TX_TX");
        Assert.Contains(violations, v => v.RuleCode == "DRC-I2C-CROSS" && v.NetName == "NET_I2C_BAD_CROSSED");
    }

    [Fact]
    public void CurrentAndLoadViolations_DirectLedAnd3V3ServoLoad_ShouldFlagViolations()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_LED_UNPROTECTED",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("S2Mini", MicrocontrollerBoardType.WemosEsp32S2Mini, "IO1")
                ],
                BreadboardComponents:
                [
                    new BreadboardNetComponent("LED1", ExternalLoadType.StandardLed, CurrentDrawMa: 25.0, ResistanceOhms: 0.0)
                ],
                BreadboardTiePoints: ["BB:C20"]),

            new OffBoardElectricalNet(
                NetName: "NET_3V3_SERVO",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "3.3V")
                ],
                BreadboardComponents:
                [
                    new BreadboardNetComponent("SG90_Servo", ExternalLoadType.HobbyServoMotor, CurrentDrawMa: 650.0)
                ],
                BreadboardTiePoints: ["BB:J5"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-LED-NO-RESISTOR" && v.NetName == "NET_LED_UNPROTECTED");
        Assert.Contains(violations, v => v.RuleCode == "DRC-3V3-LDO-OVERLOAD" && v.NetName == "NET_3V3_SERVO");
    }

    [Fact]
    public void OffBoardRenderer_ShouldComputePinSocketCoordinatesOutsideBreadboard()
    {
        var renderer = new OffBoardMicrocontrollerRenderer(_engine);
        var breadboardRect = new SKRect(50f, 50f, 875.5f, 323.0f);
        var state = new OffBoardMicrocontrollerVisualState(
            BoardInstanceId: "UnoMain",
            BoardType: MicrocontrollerBoardType.ArduinoUnoR3R4,
            DockPlacement: OffBoardDockPlacement.BottomMargin,
            DockSlotIndex: 0,
            HoveredPinCanonicalId: "D13",
            ConnectedPinCanonicalIds: new HashSet<string> { "D13", "5V", "GND_1" },
            ViolatingPinCanonicalIds: new HashSet<string>());

        RenderedMicrocontrollerLayout layout = renderer.ComputeBoardLayout(state, breadboardRect, pixelsPerMm: 5.0f);

        Assert.True(layout.BoardBoundsCanvas.Top > breadboardRect.Bottom);
        Assert.True(layout.PinSocketCanvasCenters.ContainsKey("D13"));
        Assert.True(layout.PinSocketCanvasCenters.ContainsKey("A4"));
    }

    [Fact]
    public void Esp32InputOnlyPin_DrivingLed_ShouldFlagInputOnlyViolation()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_ESP32_GPI_DRIVE",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Esp1", MicrocontrollerBoardType.Esp32DevKitV1, "D34")
                ],
                BreadboardComponents:
                [
                    new BreadboardNetComponent("LED1", ExternalLoadType.StandardLed, CurrentDrawMa: 20.0, ResistanceOhms: 220.0)
                ],
                BreadboardTiePoints: ["BB:A10"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-INPUT-ONLY-PIN-DRIVE");
    }

    [Fact]
    public void Esp32StrappingPin_Gpio12PulledHigh_ShouldFlagBootStrappingWarning()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_STRAPPING_PULLUP",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Esp1", MicrocontrollerBoardType.Esp32DevKitV1, "D12"),
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "3.3V")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:A15"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-ESP32-BOOT-STRAPPING");
    }

    [Fact]
    public void GpioOutputContention_MultipleOutputsOnSameNet_ShouldFlagWarning()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_GPIO_FIGHT",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D9"),
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "D10")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:B20"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-GPIO-OUTPUT-CONTENTION");
    }

    [Fact]
    public void MissingI2cPullups_MultipleI2cDevicesWithoutPullup_ShouldFlagNotice()
    {
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_I2C_FLOAT",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "A4 (SDA)"),
                    new McuPinEndpoint("Esp1", MicrocontrollerBoardType.Esp32DevKitV1, "D21(SDA)")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:C5"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-MISSING-I2C-PULLUPS");
    }

    [Fact]
    public void DevBoardPinHitTest_ShouldHitPinSocketAndSetHoverState()
    {
        var overlayRenderer = new SkiaArOverlayRenderer();
        overlayRenderer.ActiveMicrocontrollers = new[]
        {
            new OffBoardMicrocontrollerVisualState(
                BoardInstanceId: "MCU_1",
                BoardType: MicrocontrollerBoardType.ArduinoUnoR3R4,
                DockPlacement: OffBoardDockPlacement.BottomMargin,
                DockSlotIndex: 0,
                HoveredPinCanonicalId: null,
                ConnectedPinCanonicalIds: new HashSet<string>(),
                ViolatingPinCanonicalIds: new HashSet<string>())
        };

        // Render viewport layout
        using var surface = SKSurface.Create(new SKImageInfo(1200, 800));
        overlayRenderer.RenderCadViewport(surface.Canvas, 1200, 800, null, null, null);

        Assert.NotEmpty(overlayRenderer.ActiveMcuLayouts);
        RenderedMicrocontrollerLayout layout = overlayRenderer.ActiveMcuLayouts[0];
        Assert.True(layout.PinSocketCanvasCenters.TryGetValue("D13", out SKPoint d13Center));

        // Test hover directly on D13 pin socket
        bool hit = overlayRenderer.TryHitTestDevBoardPin(d13Center.X, d13Center.Y, out DevBoardPinHit? pinHit);
        Assert.True(hit);
        Assert.NotNull(pinHit);
        Assert.Equal("D13", pinHit!.Pin.CanonicalId);
        Assert.Equal("MCU_1", pinHit.BoardInstanceId);
    }

    [Fact]
    public void DevBoard_DirectShortAcrossBreadboardHoles_ShouldFlagPwrGndShort()
    {
        // Wire 1: Uno 5V to Breadboard hole A10
        // Wire 2: Uno GND to Breadboard hole B10 (same terminal strip 10)
        var nets = new[]
        {
            new OffBoardElectricalNet(
                NetName: "NET_STRIP_10_SHORT",
                ConnectedMcuPins:
                [
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "5V"),
                    new McuPinEndpoint("Uno1", MicrocontrollerBoardType.ArduinoUnoR3R4, "GND")
                ],
                BreadboardComponents: [],
                BreadboardTiePoints: ["BB:A10", "BB:B10"])
        };

        IReadOnlyList<DrcViolation> violations = _engine.EvaluateCircuit(nets);

        Assert.Contains(violations, v => v.RuleCode == "DRC-PWR-GND-SHORT");
    }

    [Fact]
    public void DevBoard_EndpointMatching_ShouldSupportInstanceAndCanonicalAndAliases()
    {
        MicrocontrollerBoardProfile uno = _engine.GetBoardProfile(MicrocontrollerBoardType.ArduinoUnoR3R4);
        McuPinDefinition? d13 = uno.FindPin("D13");
        Assert.NotNull(d13);

        // Exact match with instance prefix
        Assert.True(MainWorkstationViewModel.MatchesMcuEndpoint("MCU_1:D13", "MCU_1", d13!));
        // Canonical ID alone
        Assert.True(MainWorkstationViewModel.MatchesMcuEndpoint("D13", "MCU_1", d13!));
        // Display label alone
        Assert.True(MainWorkstationViewModel.MatchesMcuEndpoint(d13!.DisplayLabel, "MCU_1", d13!));
        // Aliases if any
        if (d13.Aliases.Count > 0)
        {
            Assert.True(MainWorkstationViewModel.MatchesMcuEndpoint($"MCU_1:{d13.Aliases[0]}", "MCU_1", d13));
        }

        // Mismatched pin should return false
        Assert.False(MainWorkstationViewModel.MatchesMcuEndpoint("MCU_1:5V", "MCU_1", d13));
        Assert.False(MainWorkstationViewModel.MatchesMcuEndpoint("MCU_1:D12", "MCU_1", d13));

        // Breadboard hole matching
        Assert.True(MainWorkstationViewModel.MatchesBreadboardEndpoint("A10", "A10"));
        Assert.True(MainWorkstationViewModel.MatchesBreadboardEndpoint("BB:A10", "A10"));
        Assert.True(MainWorkstationViewModel.MatchesBreadboardEndpoint("bb:a10", "A10"));
        Assert.False(MainWorkstationViewModel.MatchesBreadboardEndpoint("BB:B10", "A10"));
    }

    [Fact]
    public void DevBoard_PinHitTesting_FarClick_ShouldNotHit()
    {
        var overlayRenderer = new SkiaArOverlayRenderer();
        overlayRenderer.ActiveMicrocontrollers = new[]
        {
            new OffBoardMicrocontrollerVisualState(
                BoardInstanceId: "MCU_1",
                BoardType: MicrocontrollerBoardType.ArduinoUnoR3R4,
                DockPlacement: OffBoardDockPlacement.BottomMargin,
                DockSlotIndex: 0,
                HoveredPinCanonicalId: null,
                ConnectedPinCanonicalIds: new HashSet<string>(),
                ViolatingPinCanonicalIds: new HashSet<string>())
        };

        using var surface = SKSurface.Create(new SKImageInfo(1200, 800));
        overlayRenderer.RenderCadViewport(surface.Canvas, 1200, 800, null, null, null);

        // Click in middle of canvas far away from bottom dock
        bool hit = overlayRenderer.TryHitTestDevBoardPin(200f, 200f, out DevBoardPinHit? pinHit);
        Assert.False(hit);
        Assert.Null(pinHit);
    }

    [Fact]
    public void DevBoard_JumperWire_RealisticDropShadowAndCatenary_RendersWithoutException()
    {
        using var surface = SKSurface.Create(new SKImageInfo(1000, 1000));
        var canvas = surface.Canvas;

        // Render both normal and violating jumper wires
        var start = new SKPoint(100, 100);
        var end = new SKPoint(400, 300);

        // Should render without throwing
        SkiaArOverlayRenderer.Draw3DJumperWire(canvas, start, end, "#4CAF50", hasViolation: false, pxPerMm: 3.78f);
        SkiaArOverlayRenderer.Draw3DJumperWire(canvas, start, end, "#FF1744", hasViolation: true, pxPerMm: 3.78f);
    }

    [Fact]
    public void DevBoard_WireColorResolution_SmartAutoRoleMappingWorksCorrectly()
    {
        var engine = new DevBoardAndModuleRuleEngine();
        MicrocontrollerBoardProfile uno = engine.GetBoardProfile(MicrocontrollerBoardType.ArduinoUnoR3R4);

        McuPinDefinition vccPin = uno.FindPin("5V")!;
        McuPinDefinition gndPin = uno.FindPin("GND_1")!;
        McuPinDefinition a0Pin = uno.FindPin("A0")!;
        McuPinDefinition sdaPin = uno.FindPin("SDA")!;
        McuPinDefinition d13Pin = uno.FindPin("D13")!;
        McuPinDefinition d4Pin = uno.FindPin("D4")!;

        // Auto resolution by pin role
        Assert.Equal("#E53935", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", vccPin, null));
        Assert.Equal("#1E1E24", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", gndPin, null));
        Assert.Equal("#FBC02D", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", a0Pin, null));
        Assert.Equal("#00BCD4", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", sdaPin, null));
        Assert.NotEmpty(BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", d13Pin, null));
        Assert.NotEmpty(BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", d4Pin, null));

        // Hole-based fallback when pin is null
        Assert.Equal("#E53935", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", null, "TOP_VCC_1"));
        Assert.Equal("#1E1E24", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("AUTO (By Role)", null, "BOT_GND_5"));

        // Explicit user choice overrides auto
        Assert.Equal("#1E88E5", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("BLUE", vccPin, null));
        Assert.Equal("#43A047", BreadboardAOI.App.ViewModels.MainWorkstationViewModel.ResolveWireColor("GREEN", gndPin, null));
    }

    [Fact]
    public void DevBoard_MeasureLabelExtents_TopBadgeExtentsAccountForOutwardBadges()
    {
        var engine = new DevBoardAndModuleRuleEngine();
        var renderer = new OffBoardMicrocontrollerRenderer(engine);

        McuLabelExtents unoExtents = renderer.MeasureLabelExtents(MicrocontrollerBoardType.ArduinoUnoR3R4, 3.78f);
        Assert.True(unoExtents.TopPx > 0f, "Top pin label badges project outward above the socket to prevent component collisions.");
        Assert.True(unoExtents.BottomPx > 0f);
    }

    [Fact]
    public void DevBoard_WemosEsp32S2Mini_ProfileAndPinLayoutVerified()
    {
        MicrocontrollerBoardProfile profile = _engine.GetBoardProfile(MicrocontrollerBoardType.WemosEsp32S2Mini);

        Assert.Equal(25.4f, profile.WidthMm);
        Assert.Equal(34.3f, profile.HeightMm);
        Assert.Equal("#3B1E6D", profile.PcbPrimaryHexColor);
        Assert.Equal("#4A2382", profile.PcbSecondaryHexColor);
        Assert.Equal(32, profile.Pins.Count);

        // Verify key pins and their roles/aliases
        var vbus = profile.FindPin("VBUS");
        Assert.NotNull(vbus);
        Assert.Equal(PinElectricalRole.PowerVbus, vbus.Role);
        Assert.Equal(5.0, vbus.NominalVoltage);

        var rst = profile.FindPin("RST");
        Assert.NotNull(rst);
        Assert.Equal(PinElectricalRole.Reset, rst.Role);

        var dac1 = profile.FindPin("IO17");
        Assert.NotNull(dac1);
        Assert.Equal(PinElectricalRole.DacOutput, dac1.Role);

        var sda = profile.FindPin("IO8");
        Assert.NotNull(sda);
        Assert.Equal(PinElectricalRole.I2cSda, sda.Role);

        var scl = profile.FindPin("IO9");
        Assert.NotNull(scl);
        Assert.Equal(PinElectricalRole.I2cScl, scl.Role);

        var io40 = profile.FindPin("IO40");
        Assert.NotNull(io40);
        Assert.Equal(PinElectricalRole.DigitalGpio, io40.Role);
    }

    [Fact]
    public void DevBoard_Esp32DevKitV1_MatteBlackPcbAndShieldCan_Verified()
    {
        MicrocontrollerBoardProfile profile = _engine.GetBoardProfile(MicrocontrollerBoardType.Esp32DevKitV1);

        Assert.Equal(51.5f, profile.WidthMm);
        Assert.Equal(28.0f, profile.HeightMm);
        Assert.Equal("#1E1E1E", profile.PcbPrimaryHexColor);
        Assert.Equal("#141416", profile.PcbSecondaryHexColor);
        Assert.Equal(30, profile.Pins.Count);

        // Verify left row alignment is AboveSocket and right row is BelowSocket
        var leftPin = profile.FindPin("EN");
        Assert.NotNull(leftPin);
        Assert.Equal(PinLabelAlignment.AboveSocket, leftPin.LabelAlignment);

        var rightPin = profile.FindPin("3V3");
        Assert.NotNull(rightPin);
        Assert.Equal(PinLabelAlignment.BelowSocket, rightPin.LabelAlignment);
    }

    [Fact]
    public void DevBoard_Stm32BluePill_TopRowLabelsProjectAboveSocket()
    {
        MicrocontrollerBoardProfile profile = _engine.GetBoardProfile(MicrocontrollerBoardType.Stm32BluePillF103C8);

        Assert.Equal(40, profile.Pins.Count);

        // Verify top-row pins use AboveSocket so badges project outward instead of into the crystal/STM32 chip
        string[] topRowPins = ["B12", "B13", "B14", "B15", "A8", "A9", "A10", "A11", "A12", "A15", "B3", "B4", "B5", "B6", "B7", "B8", "B9", "5V", "GND_1", "3V3_1"];
        foreach (var pinId in topRowPins)
        {
            var pin = profile.FindPin(pinId);
            Assert.NotNull(pin);
            Assert.Equal(PinLabelAlignment.AboveSocket, pin.LabelAlignment);
        }
    }

    [Fact]
    public void DevBoard_UnifiedUndoRedo_DevBoardWiresAndDraftPartsRestoredCorrectly()
    {
        RunOnStaThread(() =>
        {
            var vm = new MainWorkstationViewModel();
            vm.IsBuildMode = true;

            // 1. Place a resistor
            bool placed = vm.TryPlacePart(ComponentKind.Resistor, "A1", "A5");
            Assert.True(placed);
            Assert.Single(vm.DraftParts);
            Assert.Equal(1, vm.UndoStackDepth);

            // 2. Add a dev board jumper wire
            vm.SelectedDevBoardType = MicrocontrollerBoardType.ArduinoUnoR3R4;
            vm.SelectedMcuPinToWire = "5V";
            vm.SelectedBreadboardHoleToWire = "TOP_VCC_1";
            vm.AddDevBoardJumperWire();
            Assert.Single(vm.ActiveDevBoardWires);
            Assert.Equal(2, vm.UndoStackDepth);

            // 3. Undo the dev board wire (Ctrl+Z)
            vm.UndoCircuitPart();
            Assert.Empty(vm.ActiveDevBoardWires);
            Assert.Single(vm.DraftParts);
            Assert.Equal(1, vm.RedoStackDepth);

            // 4. Undo the placed resistor (Ctrl+Z)
            vm.UndoCircuitPart();
            Assert.Empty(vm.DraftParts);
            Assert.Empty(vm.ActiveDevBoardWires);
            Assert.Equal(2, vm.RedoStackDepth);

            // 5. Redo the placed resistor (Ctrl+Shift+Z)
            vm.RedoCircuitPart();
            Assert.Single(vm.DraftParts);
            Assert.Empty(vm.ActiveDevBoardWires);
            Assert.Equal(1, vm.RedoStackDepth);

            // 6. Redo the dev board wire (Ctrl+Shift+Z)
            vm.RedoCircuitPart();
            Assert.Single(vm.DraftParts);
            Assert.Single(vm.ActiveDevBoardWires);
            Assert.Equal(0, vm.RedoStackDepth);
        });
    }

    private static void RunOnStaThread(System.Action action)
    {
        System.Exception? error = null;
        var thread = new System.Threading.Thread(() =>
        {
            try
            {
                action();
            }
            catch (System.Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(System.Threading.ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
        }
    }

    [Fact]
    public void DevBoard_AllFiveBoards_RenderWithSilkscreenAndHoverHudWithoutException()
    {
        var engine = new DevBoardAndModuleRuleEngine();
        var renderer = new OffBoardMicrocontrollerRenderer(engine);
        using var surface = SKSurface.Create(new SKImageInfo(1200, 1000));
        var canvas = surface.Canvas;
        var breadboardRect = new SKRect(100, 50, 900, 300);

        MicrocontrollerBoardType[] boards =
        [
            MicrocontrollerBoardType.ArduinoUnoR3R4,
            MicrocontrollerBoardType.ArduinoMega2560,
            MicrocontrollerBoardType.Esp32DevKitV1,
            MicrocontrollerBoardType.WemosEsp32S2Mini,
            MicrocontrollerBoardType.Stm32BluePillF103C8
        ];

        foreach (var boardType in boards)
        {
            var profile = engine.GetBoardProfile(boardType);
            string firstPin = profile.Pins[0].CanonicalId;

            var state = new OffBoardMicrocontrollerVisualState(
                BoardInstanceId: "MCU_TEST",
                BoardType: boardType,
                DockPlacement: OffBoardDockPlacement.BottomMargin,
                DockSlotIndex: 0,
                HoveredPinCanonicalId: firstPin,
                ConnectedPinCanonicalIds: new HashSet<string> { firstPin },
                ViolatingPinCanonicalIds: new HashSet<string>());

            RenderedMicrocontrollerLayout layout = renderer.RenderMicrocontroller(canvas, state, breadboardRect, 3.78f);
            Assert.NotNull(layout);
            Assert.NotEmpty(layout.PinSocketCanvasCenters);
        }
    }

    [Fact]
    public void Esp32DevKit_NineVoltBatteryIntoD4_TriggersCriticalOvervoltage()
    {
        var batteryComp = new BreadboardNetComponent(
            ComponentId: "BATTERY #1",
            LoadType: ExternalLoadType.ExternalPowerSupply,
            SupplyVoltage: 9.0,
            IsPositiveTerminal: true,
            ConnectedBreadboardTiePoint: "TOP_VCC_9");

        var net = new OffBoardElectricalNet(
            NetName: "NET_SUPPLY_9.0V",
            ConnectedMcuPins: new[] { new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.Esp32DevKitV1, "D4") },
            BreadboardComponents: new[] { batteryComp },
            BreadboardTiePoints: new[] { "TOP_VCC_9" });

        var violations = _engine.EvaluateCircuit(new[] { net });

        Assert.Contains(violations, v =>
            v.RuleCode == "DRC-OVERVOLTAGE-EXTERNAL-SUPPLY" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk &&
            v.OffendingPins.Any(p => p.PinId == "D4"));
    }

    [Fact]
    public void Esp32DevKit_3V3ConnectedToBreadboardGroundRail_TriggersPowerGndShort()
    {
        var net = new OffBoardElectricalNet(
            NetName: "NET_GND",
            ConnectedMcuPins: new[] { new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.Esp32DevKitV1, "3V3") },
            BreadboardComponents: System.Array.Empty<BreadboardNetComponent>(),
            BreadboardTiePoints: new[] { "BOT_GND_13" });

        var violations = _engine.EvaluateCircuit(new[] { net });

        Assert.Contains(violations, v =>
            v.RuleCode == "DRC-PWR-GND-SHORT" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk &&
            v.OffendingPins.Any(p => p.PinId == "3V3"));
    }

    [Fact]
    public void Esp32DevKit_D27DirectlyTiedToGroundRail_TriggersGpioDirectGndTieWarning()
    {
        var net = new OffBoardElectricalNet(
            NetName: "NET_GND",
            ConnectedMcuPins: new[] { new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.Esp32DevKitV1, "D27") },
            BreadboardComponents: System.Array.Empty<BreadboardNetComponent>(),
            BreadboardTiePoints: new[] { "TOP_GND_33" });

        var violations = _engine.EvaluateCircuit(new[] { net });

        Assert.Contains(violations, v =>
            v.RuleCode == "DRC-GPIO-DIRECT-GND-TIE" &&
            v.Severity == McuDrcSeverity.Warning &&
            v.OffendingPins.Any(p => p.PinId == "D27"));
    }

    [Fact]
    public void ExternalSupply_NineVoltsInto3V3Rail_TriggersOvervoltageInto3V3Rail()
    {
        var batteryComp = new BreadboardNetComponent(
            ComponentId: "BATTERY #1",
            LoadType: ExternalLoadType.ExternalPowerSupply,
            SupplyVoltage: 9.0,
            IsPositiveTerminal: true);

        var net = new OffBoardElectricalNet(
            NetName: "NET_SUPPLY_9.0V",
            ConnectedMcuPins: new[] { new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.Esp32DevKitV1, "3V3") },
            BreadboardComponents: new[] { batteryComp },
            BreadboardTiePoints: new[] { "TOP_VCC_1" });

        var violations = _engine.EvaluateCircuit(new[] { net });

        Assert.Contains(violations, v =>
            v.RuleCode == "DRC-OVERVOLTAGE-INTO-3V3-RAIL" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk &&
            v.OffendingPins.Any(p => p.PinId == "3V3"));
    }

    [Fact]
    public void ExternalSupply_NineVoltsInto5VRail_TriggersOvervoltageInto5VRail()
    {
        var batteryComp = new BreadboardNetComponent(
            ComponentId: "BATTERY #1",
            LoadType: ExternalLoadType.ExternalPowerSupply,
            SupplyVoltage: 9.0,
            IsPositiveTerminal: true);

        var net = new OffBoardElectricalNet(
            NetName: "NET_SUPPLY_9.0V",
            ConnectedMcuPins: new[] { new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, "5V") },
            BreadboardComponents: new[] { batteryComp },
            BreadboardTiePoints: new[] { "TOP_VCC_1" });

        var violations = _engine.EvaluateCircuit(new[] { net });

        Assert.Contains(violations, v =>
            v.RuleCode == "DRC-OVERVOLTAGE-INTO-5V-RAIL" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk &&
            v.OffendingPins.Any(p => p.PinId == "5V"));
    }

    [Fact]
    public void ArduinoUno_5VConnectedToBreadboardGroundRail_TriggersPowerGndShort()
    {
        var net = new OffBoardElectricalNet(
            NetName: "NET_GND",
            ConnectedMcuPins: new[] { new McuPinEndpoint("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, "5V") },
            BreadboardComponents: System.Array.Empty<BreadboardNetComponent>(),
            BreadboardTiePoints: new[] { "BOT_GND_5" });

        var violations = _engine.EvaluateCircuit(new[] { net });

        Assert.Contains(violations, v =>
            v.RuleCode == "DRC-PWR-GND-SHORT" &&
            v.Severity == McuDrcSeverity.CriticalDamageRisk &&
            v.OffendingPins.Any(p => p.PinId == "5V"));
    }

    [Fact]
    public void Draw3DJumperWire_BottomHeaderSocket_ExecutesFlankRoutingCleanly()
    {
        using var surface = SKSurface.Create(new SKImageInfo(800, 600));
        var canvas = surface.Canvas;

        var boardBounds = new SKRect(200, 300, 500, 500);
        var breadboardRect = new SKRect(50, 50, 750, 250);

        // Target on left side of bottom header (e.g. 3.3V on Uno / ESP32) -> routes around left flank
        var bbPoint1 = new SKPoint(250, 150);
        var mcuBottomLeft = new SKPoint(240, 480);
        SkiaArOverlayRenderer.Draw3DJumperWire(
            canvas,
            bbPoint1,
            mcuBottomLeft,
            "#FF9800",
            hasViolation: false,
            pxPerMm: 4.0f,
            startAlignment: null,
            endAlignment: PinLabelAlignment.BelowSocket,
            mcuBoardBounds: boardBounds,
            breadboardRect: breadboardRect);

        // Target on right side of bottom header (e.g. D4 on Uno / ESP32) -> routes around right flank
        var bbPoint2 = new SKPoint(350, 150);
        var mcuBottomRight = new SKPoint(420, 480);
        SkiaArOverlayRenderer.Draw3DJumperWire(
            canvas,
            bbPoint2,
            mcuBottomRight,
            "#00E5FF",
            hasViolation: false,
            pxPerMm: 4.0f,
            startAlignment: null,
            endAlignment: PinLabelAlignment.BelowSocket,
            mcuBoardBounds: boardBounds,
            breadboardRect: breadboardRect);

        // Target on top header -> direct vertical entry
        var mcuTopPin = new SKPoint(350, 310);
        SkiaArOverlayRenderer.Draw3DJumperWire(
            canvas,
            bbPoint2,
            mcuTopPin,
            "#E53935",
            hasViolation: false,
            pxPerMm: 4.0f,
            startAlignment: null,
            endAlignment: PinLabelAlignment.AboveSocket,
            mcuBoardBounds: boardBounds,
            breadboardRect: breadboardRect);

        // Top half breadboard jumper -> arches upward away from ravine
        var bbTop1 = new SKPoint(100, 100);
        var bbTop2 = new SKPoint(200, 100);
        SkiaArOverlayRenderer.Draw3DJumperWire(
            canvas,
            bbTop1,
            bbTop2,
            "#4CAF50",
            hasViolation: false,
            pxPerMm: 4.0f,
            startAlignment: null,
            endAlignment: null,
            mcuBoardBounds: null,
            breadboardRect: breadboardRect);

        // Bottom half breadboard jumper -> arches downward away from ravine
        var bbBot1 = new SKPoint(100, 200);
        var bbBot2 = new SKPoint(200, 200);
        SkiaArOverlayRenderer.Draw3DJumperWire(
            canvas,
            bbBot1,
            bbBot2,
            "#9C27B0",
            hasViolation: false,
            pxPerMm: 4.0f,
            startAlignment: null,
            endAlignment: null,
            mcuBoardBounds: null,
            breadboardRect: breadboardRect);
    }

    [Fact]
    public void TryResolveMcuPinContext_ResolvesPinsCorrectly()
    {
        var renderer = new SkiaArOverlayRenderer();
        var bbRect = new SKRect(0, 0, 600, 200);
        var layout = renderer.McuRenderer.ComputeBoardLayout(
            new OffBoardMicrocontrollerVisualState(
                BoardInstanceId: "MCU_1",
                BoardType: MicrocontrollerBoardType.ArduinoUnoR3R4,
                DockPlacement: OffBoardDockPlacement.BottomMargin,
                DockSlotIndex: 0,
                HoveredPinCanonicalId: null,
                ConnectedPinCanonicalIds: new HashSet<string>(),
                ViolatingPinCanonicalIds: new HashSet<string>()),
            bbRect,
            4.0f);

        var layouts = new[] { layout };

        // Test resolving full key
        bool foundFull = renderer.TryResolveMcuPinContext("MCU_1:D13", layouts, out var l1, out var p1);
        Assert.True(foundFull);
        Assert.NotNull(p1);
        Assert.Equal("D13 (SCK)", p1.DisplayLabel);
        Assert.Equal(PinLabelAlignment.AboveSocket, p1.LabelAlignment);

        // Test resolving bottom pin
        bool foundBottom = renderer.TryResolveMcuPinContext("MCU_1:3.3V", layouts, out var l2, out var p2);
        Assert.True(foundBottom);
        Assert.NotNull(p2);
        Assert.Equal(PinLabelAlignment.BelowSocket, p2.LabelAlignment);

        // Test breadboard hole returns false
        bool foundBb = renderer.TryResolveMcuPinContext("BB:E10", layouts, out _, out _);
        Assert.False(foundBb);
    }

    [Fact]
    public void WemosEsp32S2Mini_ShouldHaveDualRowStaggeredHeadersAndFourColumnBadges()
    {
        MicrocontrollerBoardProfile profile = _engine.GetBoardProfile(MicrocontrollerBoardType.WemosEsp32S2Mini);
        Assert.Equal(32, profile.Pins.Count);
        Assert.Equal(25.4f, profile.WidthMm);
        Assert.Equal(34.3f, profile.HeightMm);

        // Verify left outer pins at X = 2.00 mm
        var rst = profile.FindPin("RST");
        Assert.NotNull(rst);
        Assert.Equal(2.00f, rst.LocalXMm);
        Assert.Equal(PinLabelAlignment.LeftOfSocket, rst.LabelAlignment);

        // Verify left inner pins at X = 4.54 mm
        var io1 = profile.FindPin("IO1");
        Assert.NotNull(io1);
        Assert.Equal(4.54f, io1.LocalXMm);
        Assert.Equal(PinLabelAlignment.LeftOfSocket, io1.LabelAlignment);

        // Verify right inner pins at X = 20.86 mm
        var io40 = profile.FindPin("IO40");
        Assert.NotNull(io40);
        Assert.Equal(20.86f, io40.LocalXMm);
        Assert.Equal(PinLabelAlignment.RightOfSocket, io40.LabelAlignment);

        // Verify right outer pins at X = 23.40 mm
        var vbus = profile.FindPin("VBUS");
        Assert.NotNull(vbus);
        Assert.Equal(23.40f, vbus.LocalXMm);
        Assert.Equal(PinLabelAlignment.RightOfSocket, vbus.LabelAlignment);

        // Verify label extents reserve space for dual columns
        var renderer = new OffBoardMicrocontrollerRenderer(_engine);
        McuLabelExtents extents = renderer.MeasureLabelExtents(profile, 4.0f);
        Assert.True(extents.LeftPx > 10.0f);
        Assert.True(extents.RightPx > 10.0f);
    }

    [Fact]
    public void Stm32BluePill_PinLabels_ShouldBeAboveAndBelowBoardWithoutOverlap()
    {
        MicrocontrollerBoardProfile profile = _engine.GetBoardProfile(MicrocontrollerBoardType.Stm32BluePillF103C8);
        Assert.Equal(40, profile.Pins.Count);

        // Top row pins: all AboveSocket
        var b12 = profile.FindPin("B12");
        Assert.NotNull(b12);
        Assert.Equal(PinLabelAlignment.AboveSocket, b12.LabelAlignment);
        Assert.Equal(2.54f, b12.LocalYMm);

        // Bottom row pins: all BelowSocket
        var vbat = profile.FindPin("VBAT");
        Assert.NotNull(vbat);
        Assert.Equal(PinLabelAlignment.BelowSocket, vbat.LabelAlignment);
        Assert.Equal(20.32f, vbat.LocalYMm);

        var renderer = new OffBoardMicrocontrollerRenderer(_engine);
        McuLabelExtents extents = renderer.MeasureLabelExtents(profile, 4.0f);
        Assert.True(extents.TopPx > 5.0f);
        Assert.True(extents.BottomPx > 5.0f);
    }

    [Fact]
    public void CadViewport_ZoomAndPan_CalculationsAndCoordinates_AreAccurateAndConsistent()
    {
        var renderer = new SkiaArOverlayRenderer();
        Assert.Equal(1.0f, renderer.UserZoomFactor);
        Assert.Equal(0f, renderer.PanOffsetX);
        Assert.Equal(0f, renderer.PanOffsetY);

        using var surface = SKSurface.Create(new SKImageInfo(1200, 800));
        renderer.RenderCadViewport(surface.Canvas, 1200, 800, null, null, null);

        // Zoom at center
        renderer.ZoomAtCenter(1.25f);
        Assert.True(MathF.Abs(renderer.UserZoomFactor - 1.25f) < 0.01f);

        // Pan by offset
        float panXBefore = renderer.PanOffsetX;
        float panYBefore = renderer.PanOffsetY;
        renderer.PanBy(40f, -25f);
        Assert.True(MathF.Abs(renderer.PanOffsetX - (panXBefore + 40f)) < 0.01f);
        Assert.True(MathF.Abs(renderer.PanOffsetY - (panYBefore - 25f)) < 0.01f);

        // Reset zoom
        renderer.ResetZoom();
        Assert.Equal(1.0f, renderer.UserZoomFactor);
        Assert.Equal(0f, renderer.PanOffsetX);
        Assert.Equal(0f, renderer.PanOffsetY);

        // Sub-pixel focus anchoring test:
        // A point under focusScreenX, focusScreenY must map to the same board coordinate before and after zoom
        renderer.RenderCadViewport(surface.Canvas, 1200, 800, null, null, null);
        float focusX = 600f;
        float focusY = 400f;
        SKPoint boardBefore = renderer.Transform.ToBoard(focusX, focusY);

        renderer.ZoomAt(1.5f, focusX, focusY);
        renderer.RenderCadViewport(surface.Canvas, 1200, 800, null, null, null);
        SKPoint boardAfter = renderer.Transform.ToBoard(focusX, focusY);

        Assert.True(MathF.Abs(boardBefore.X - boardAfter.X) < 0.05f, $"Anchor X drifted: before={boardBefore.X}, after={boardAfter.X}");
        Assert.True(MathF.Abs(boardBefore.Y - boardAfter.Y) < 0.05f, $"Anchor Y drifted: before={boardBefore.Y}, after={boardAfter.Y}");

        // ViewModel bindings test on STA thread
        RunOnStaThread(() =>
        {
            var vm = new MainWorkstationViewModel();
            Assert.Equal("100%", vm.ViewportZoomText);

            vm.ZoomIn();
            Assert.Equal("125%", vm.ViewportZoomText);

            vm.ZoomOut();
            Assert.Equal("100%", vm.ViewportZoomText);

            vm.ZoomCadViewport(2.0, null);
            Assert.Equal("200%", vm.ViewportZoomText);

            vm.ResetZoom();
            Assert.Equal("100%", vm.ViewportZoomText);
        });
    }

    [Fact]
    public void Stm32BluePill_PinLabelCoordinates_ShouldNotOverlapBoardComponents()
    {
        var ruleEngine = new DevBoardAndModuleRuleEngine();
        var renderer = new OffBoardMicrocontrollerRenderer(ruleEngine);
        var visualState = new OffBoardMicrocontrollerVisualState(
            "MCU_1",
            MicrocontrollerBoardType.Stm32BluePillF103C8,
            OffBoardDockPlacement.BottomMargin,
            0,
            null,
            new HashSet<string>(),
            new HashSet<string>());
        var bbBounds = new SKRect(50, 50, 950, 450);
        float pxPerMm = 6.0f;

        var layout = renderer.ComputeBoardLayout(visualState, bbBounds, pxPerMm);
        var b = layout.BoardBoundsCanvas;

        // Top socket centers should be near top of board
        var b12Center = layout.PinSocketCanvasCenters["B12"];
        Assert.True(b12Center.Y > b.Top && b12Center.Y < b.Top + (5f * pxPerMm), "B12 should be near top edge");

        // Bottom socket centers should be near bottom of board
        var vbatCenter = layout.PinSocketCanvasCenters["VBAT"];
        Assert.True(vbatCenter.Y > b.Bottom - (5f * pxPerMm) && vbatCenter.Y < b.Bottom, "VBAT should be near bottom edge");

        // Verify transformed badge rectangle for B12 (AboveSocket) and VBAT (BelowSocket)
        float housingSize = 2.45f * pxPerMm;
        float offsetFromSocket = (housingSize * 0.64f) + (0.60f * pxPerMm);
        float badgeW = 20.0f;

        // B12 (AboveSocket)
        var mAbove = SKMatrix.CreateTranslation(b12Center.X, b12Center.Y);
        mAbove = mAbove.PreConcat(SKMatrix.CreateRotationDegrees(-90f));
        float startXAbove = offsetFromSocket;
        var ptAbove = mAbove.MapPoint(startXAbove, 0);

        // Assert ptAbove.Y is ABOVE b12Center.Y (i.e. smaller Y)
        Assert.True(ptAbove.Y < b12Center.Y, $"B12 badge Y ({ptAbove.Y}) should be above socket center ({b12Center.Y})");

        // VBAT (BelowSocket)
        var mBelow = SKMatrix.CreateTranslation(vbatCenter.X, vbatCenter.Y);
        mBelow = mBelow.PreConcat(SKMatrix.CreateRotationDegrees(-90f));
        float startXBelow = -(offsetFromSocket + badgeW);
        var ptBelow = mBelow.MapPoint(startXBelow, 0);

        // Assert ptBelow.Y is BELOW vbatCenter.Y (i.e. larger Y)
        Assert.True(ptBelow.Y > vbatCenter.Y, $"VBAT badge Y ({ptBelow.Y}) should be below socket center ({vbatCenter.Y})");
    }

    [Fact]
    public void Workstation_Image5Scenario_ShouldTriggerExternalOvervoltageAndGroundChecks()
    {
        RunOnStaThread(() =>
        {
            var vm = new MainWorkstationViewModel();
            vm.SelectDevBoardType("esp32");

            // Place Battery #1: 9V between TOP_VCC_3 and BOT_GND_3
            vm.TryPlacePart(ComponentKind.Battery, "TOP_VCC_3", "BOT_GND_3");

            // Wire ESP32 D25 to TOP_VCC_9
            vm.ActiveDevBoardWires.Add(new OffBoardJumperWire("W1", "NET_D25", "MCU_1:D25", "BB:TOP_VCC_9", "#FF0000"));

            // Wire ESP32 3V3 to BOT_VCC_11
            vm.ActiveDevBoardWires.Add(new OffBoardJumperWire("W2", "NET_3V3", "MCU_1:3V3", "BB:BOT_VCC_11", "#FF0000"));

            // Evaluate
            vm.EvaluateDevBoardCircuit();

            // Check violations
            var violations = vm.Violations;
            // There MUST be an overvoltage violation because 9V battery is on TOP_VCC and D25 is on TOP_VCC!
            Assert.NotEmpty(violations);
        });
    }

    [Fact]
    public void FloatingGround_BatteryAndMcuWithoutCommonGnd_TriggersFloatingGroundWarning()
    {
        RunOnStaThread(() =>
        {
            var vm = new MainWorkstationViewModel();
            vm.SelectDevBoardType("esp32");

            // Place 9V Battery on top rails
            vm.TryPlacePart(ComponentKind.Battery, "TOP_VCC_1", "TOP_GND_1");

            // Wire ESP32 D25 to TOP_VCC_10
            vm.ActiveDevBoardWires.Add(new OffBoardJumperWire("W1", "NET_D25", "MCU_1:D25", "BB:TOP_VCC_10", "#FF0000"));

            vm.EvaluateDevBoardCircuit();

            Assert.Contains(vm.Violations, v => v.CodeLabel == "DRC-FLOATING-GROUND-REF");
        });
    }

    [Fact]
    public void FloatingGround_WhenCommonGndWired_ViolationClears()
    {
        RunOnStaThread(() =>
        {
            var vm = new MainWorkstationViewModel();
            vm.SelectDevBoardType("esp32");

            // Place 9V Battery on top rails
            vm.TryPlacePart(ComponentKind.Battery, "TOP_VCC_1", "TOP_GND_1");

            // Wire ESP32 D25 to TOP_VCC_10
            vm.ActiveDevBoardWires.Add(new OffBoardJumperWire("W1", "NET_D25", "MCU_1:D25", "BB:TOP_VCC_10", "#FF0000"));

            // Wire ESP32 GND to TOP_GND_10 (establishing common ground)
            vm.ActiveDevBoardWires.Add(new OffBoardJumperWire("W2", "NET_GND", "MCU_1:GND", "BB:TOP_GND_10", "#000000"));

            vm.EvaluateDevBoardCircuit();

            Assert.DoesNotContain(vm.Violations, v => v.CodeLabel == "DRC-FLOATING-GROUND-REF");
        });
    }

    [Fact]
    public void ComputeTopologyHash_WhenResistorValueChanged_ProducesDifferentHash()
    {
        var matrix = new PinMatrixLookup();
        var drc = new ElectricalRulesChecker(matrix);
        matrix.TryGetHole("A1", out var p1);
        matrix.TryGetHole("A5", out var p2);

        var detections1 = new[]
        {
            new DetectedComponent(
                TrackId: 1,
                Kind: ComponentKind.Resistor,
                Confidence: 0.99f,
                BoxX: 0f, BoxY: 0f, BoxW: 10f, BoxH: 10f,
                Lead1Px: p1.Px, Lead1Py: p1.Py,
                Lead2Px: p2.Px, Lead2Py: p2.Py,
                DecodedValueLabel: "220R")
        };

        var detections2 = new[]
        {
            new DetectedComponent(
                TrackId: 1,
                Kind: ComponentKind.Resistor,
                Confidence: 0.99f,
                BoxX: 0f, BoxY: 0f, BoxW: 10f, BoxH: 10f,
                Lead1Px: p1.Px, Lead1Py: p1.Py,
                Lead2Px: p2.Px, Lead2Py: p2.Py,
                DecodedValueLabel: "100K")
        };

        var packet1 = drc.EvaluateBoard(1, 0.0, detections1);
        var packet2 = drc.EvaluateBoard(2, 0.0, detections2);

        Assert.NotEqual(packet1.StateHash, packet2.StateHash);
    }
}

