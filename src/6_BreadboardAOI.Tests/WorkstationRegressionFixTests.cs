using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows.Threading;
using BreadboardAOI.App.Rendering;
using BreadboardAOI.App.ViewModels;
using BreadboardAOI.Core.BoxCoding;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using FluentAssertions;
using SkiaSharp;
using Xunit;

namespace BreadboardAOI.Tests;

public class WorkstationRegressionFixTests
{
    private static OffBoardMicrocontrollerVisualState Mcu(
        string id,
        MicrocontrollerBoardType type,
        OffBoardDockPlacement dock,
        int slot) =>
        new(id, type, dock, slot, string.Empty, new HashSet<string>(), new HashSet<string>());

    [Fact]
    public void SkiaRenderer_BottomMarginDual_MegaAndS2Mini_And_MidAirDrag_RenderCleanly()
    {
        // 1. BottomMargin dual (Mega + Uno) targeting MCU_1 Mega:
        //    Programmer chassis & right vertical cable corridor must clear MCU_2 (Uno) right edge completely.
        using (var renderer = new SkiaArOverlayRenderer())
        using (var bitmap = new SKBitmap(1600, 980, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var canvas = new SKCanvas(bitmap))
        using (var dummyBoard = new SKBitmap(1000, 500, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            renderer.ActiveMicrocontrollers = new[]
            {
                Mcu("MCU_1", MicrocontrollerBoardType.ArduinoMega2560, OffBoardDockPlacement.BottomMargin, 0),
                Mcu("MCU_2", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.BottomMargin, 1)
            };
            renderer.ActiveUsbCableKind = UsbCableKind.UsbB;
            renderer.IsProgrammerConnected = true;
            renderer.TargetProgrammerBoardInstanceId = "MCU_1";
            renderer.ActiveBoardPortSummaryText = "COM8 · 115200";

            renderer.RenderCadViewport(canvas, 1600, 980, dummyBoard, DrcStatePacket.Empty, Array.Empty<RatsnestVector>());
            canvas.Flush();

            renderer.LastProgrammerBodyRect.Left.Should().BeGreaterThan(
                renderer.LastBoardUsbReceptacles["MCU_2"].UsbRect.Right + 200f,
                "Programmer chassis and vertical cable corridor must sit cleanly outside BottomMargin MCU_2 Uno");
        }

        // 2. BottomMargin dual (S2 Mini + Uno) targeting MCU_1 S2 Mini (vertical USB-C):
        using (var renderer = new SkiaArOverlayRenderer())
        using (var bitmap = new SKBitmap(1600, 980, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var canvas = new SKCanvas(bitmap))
        using (var dummyBoard = new SKBitmap(1000, 500, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            renderer.ActiveMicrocontrollers = new[]
            {
                Mcu("MCU_1", MicrocontrollerBoardType.WemosEsp32S2Mini, OffBoardDockPlacement.BottomMargin, 0),
                Mcu("MCU_2", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.BottomMargin, 1)
            };
            renderer.ActiveUsbCableKind = UsbCableKind.UsbTypeC;
            renderer.IsProgrammerConnected = true;
            renderer.TargetProgrammerBoardInstanceId = "MCU_1";

            renderer.RenderCadViewport(canvas, 1600, 980, dummyBoard, DrcStatePacket.Empty, Array.Empty<RatsnestVector>());
            canvas.Flush();

            renderer.LastProgrammerJackRect.Left.Should().BeLessThan(renderer.LastProgrammerBodyRect.Left);
        }

        // 3. Dual RightMargin boards with mid-air free drag preview:
        using (var renderer = new SkiaArOverlayRenderer())
        using (var bitmap = new SKBitmap(1600, 980, SKColorType.Bgra8888, SKAlphaType.Premul))
        using (var canvas = new SKCanvas(bitmap))
        using (var dummyBoard = new SKBitmap(1000, 500, SKColorType.Bgra8888, SKAlphaType.Premul))
        {
            renderer.ActiveMicrocontrollers = new[]
            {
                Mcu("MCU_1", MicrocontrollerBoardType.ArduinoUnoR3R4, OffBoardDockPlacement.RightMargin, 0),
                Mcu("MCU_2", MicrocontrollerBoardType.WemosEsp32S2Mini, OffBoardDockPlacement.RightMargin, 1)
            };
            renderer.PendingCableDragKind = UsbCableKind.UsbTypeC;
            renderer.HoveredUsbReceptacleBoardId = string.Empty;
            renderer.CableDragCursorCanvas = new SKPoint(1260f, 540f);
            renderer.TargetProgrammerBoardInstanceId = "MCU_1";

            renderer.RenderCadViewport(canvas, 1600, 980, dummyBoard, DrcStatePacket.Empty, Array.Empty<RatsnestVector>());
            canvas.Flush();

            renderer.LastCableRackCardRects.Should().HaveCount(3);
        }
    }

    [Fact]
    public void BoxCodingStudioViewModel_UpdateProgrammerConnectionState_PropagatesMismatchWarning()
    {
        var vm = new BoxCodingStudioViewModel();

        vm.IsProgrammerCableConnected.Should().BeFalse();
        vm.IsCableMismatchWarning.Should().BeFalse();
        vm.GlobalConnectionBadgeText.Should().Be("DISCONNECTED");

        vm.UpdateProgrammerConnectionState(isConnected: false, badgeText: null, isMismatchWarning: true);
        vm.IsProgrammerCableConnected.Should().BeFalse();
        vm.IsCableMismatchWarning.Should().BeTrue();
        vm.GlobalConnectionBadgeText.Should().Be("PLUG MISMATCH");

        vm.UpdateProgrammerConnectionState(isConnected: true, badgeText: "CONNECTED · LIVE", isMismatchWarning: true);
        vm.IsProgrammerCableConnected.Should().BeTrue();
        vm.IsCableMismatchWarning.Should().BeFalse();
        vm.GlobalConnectionBadgeText.Should().Be("CONNECTED · LIVE");
    }

    [Fact]
    public async Task MainWorkstationViewModel_SerialRevisions_DistinguishFailedUploadFromUserSerialTraffic()
    {
        using var vm = new MainWorkstationViewModel(Dispatcher.CurrentDispatcher);

        vm.SelectedDevBoardType = MicrocontrollerBoardType.ArduinoUnoR3R4;
        vm.SelectedUsbCableKind = UsbCableKind.UsbB;
        vm.ConnectUsbCable();
        vm.IsGlobalBoardConnected.Should().BeTrue();

        const string validSketch = """
            void setup() {
                Serial.begin(115200);
                pinMode(13, OUTPUT);
            }
            void loop() {
                digitalWrite(13, HIGH);
                Serial.println("TICK");
                delay(100);
            }
            """;

        ProgrammerUploadResult ok = await vm.UploadFirmwareWithOptionsAsync(
            validSketch,
            BoxCodingLanguage.ArduinoCpp,
            ProgrammerIdeCatalog.VirtualSimPortDisplay,
            verifyOnly: false);
        ok.Success.Should().BeTrue();
        vm.IsVirtualFirmwareRunning.Should().BeTrue();

        long extRevAfterStart = vm.ExternalSerialTrafficRevision;
        long serRevAfterStart = vm.VirtualSerialRevision;

        // Subsequent failed upload must advance VirtualSerialRevision for [COMPILE FAILED]
        // without advancing ExternalSerialTrafficRevision, keeping prior sim ticks distinguishable.
        ProgrammerUploadResult failed = await vm.UploadFirmwareWithOptionsAsync(
            "void setup() { ",
            BoxCodingLanguage.ArduinoCpp,
            ProgrammerIdeCatalog.VirtualSimPortDisplay,
            verifyOnly: false);
        failed.Success.Should().BeFalse();
        vm.VirtualSerialRevision.Should().BeGreaterThan(serRevAfterStart);
        vm.ExternalSerialTrafficRevision.Should().Be(extRevAfterStart);

        // User-initiated serial command or clear advances ExternalSerialTrafficRevision.
        vm.SerialInputCommandText = "PING";
        vm.SendSerialInput();
        vm.ExternalSerialTrafficRevision.Should().BeGreaterThan(extRevAfterStart);

        long extRevBeforeClear = vm.ExternalSerialTrafficRevision;
        vm.ClearSerialMonitor();
        vm.ExternalSerialTrafficRevision.Should().BeGreaterThan(extRevBeforeClear);
        vm.VirtualSerialLog.Should().Contain("Buffer cleared");
    }
}
