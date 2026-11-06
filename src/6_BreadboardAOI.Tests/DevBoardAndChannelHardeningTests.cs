// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/DevBoardAndChannelHardeningTests.cs
// PURPOSE: Verifies channel concurrency hardening, multi-threaded packet dispatch,
//          and DevBoard partial class integration.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class DevBoardAndChannelHardeningTests
{
    [Fact]
    public async Task Channel_MultiWriterConcurrentPublish_DoesNotThrow()
    {
        // Verifies the fix for SingleWriter = false on BoundedChannelOptions(1)
        var channel = Channel.CreateBounded<DrcStatePacket>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false // hardened setting
        });

        int writtenCount = 0;
        using var barrier = new Barrier(4);

        var tasks = new Task[4];
        for (int t = 0; t < 4; t++)
        {
            int writerId = t;
            tasks[t] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < 50; i++)
                {
                    var packet = new DrcStatePacket
                    {
                        FrameNumber = writerId * 1000 + i,
                        YoloInferenceMs = 1.5,
                        DrcComputeMs = 0.8,
                        StateHash = writerId * 1000 + i,
                        Components = Array.Empty<SnappedComponent>(),
                        Violations = Array.Empty<DrcViolation>()
                    };

                    if (channel.Writer.TryWrite(packet))
                    {
                        Interlocked.Increment(ref writtenCount);
                    }
                }
            });
        }

        await Task.WhenAll(tasks);
        channel.Writer.Complete();

        // The channel held at most 1 item (DropOldest); reader should be able to read the latest without error
        Assert.True(writtenCount > 0);
        bool readOk = channel.Reader.TryRead(out DrcStatePacket? latest);
        Assert.True(readOk);
        Assert.NotNull(latest);
    }

    [Fact]
    public void McuRuleEngine_Esp32InputOnlyPins_FlaggedCorrectly()
    {
        var engine = new DevBoardAndModuleRuleEngine();
        var profile = engine.GetBoardProfile(MicrocontrollerBoardType.Esp32DevKitV1);

        // Verify GPIO 34, 35, 36, 39 are marked as AnalogInputOnly or IsAnalogOnly
        var gpio34 = profile.FindPin("D34") ?? profile.FindPin("GPIO34");
        var gpio35 = profile.FindPin("D35") ?? profile.FindPin("GPIO35");
        var gpio36 = profile.FindPin("VP") ?? profile.FindPin("GPIO36");
        var gpio39 = profile.FindPin("VN") ?? profile.FindPin("GPIO39");

        Assert.NotNull(gpio34);
        Assert.NotNull(gpio35);
        Assert.NotNull(gpio36);
        Assert.NotNull(gpio39);

        Assert.True(gpio34.Role == PinElectricalRole.AnalogInputOnly || gpio34.IsAnalogOnly);
        Assert.True(gpio35.Role == PinElectricalRole.AnalogInputOnly || gpio35.IsAnalogOnly);
        Assert.True(gpio36.Role == PinElectricalRole.AnalogInputOnly || gpio36.IsAnalogOnly);
        Assert.True(gpio39.Role == PinElectricalRole.AnalogInputOnly || gpio39.IsAnalogOnly);
    }

    [Fact]
    public void McuRuleEngine_UnoOperatingVoltage_Is5Volts()
    {
        var engine = new DevBoardAndModuleRuleEngine();
        var profile = engine.GetBoardProfile(MicrocontrollerBoardType.ArduinoUnoR3R4);

        Assert.Equal(5.0, profile.OperatingLogicVoltage);
        Assert.Equal("Arduino Uno R3 / R4", profile.DisplayName);
    }

    [Fact]
    public void McuRuleEngine_Stm32BluePill_Has5VTolerantPins()
    {
        var engine = new DevBoardAndModuleRuleEngine();
        var profile = engine.GetBoardProfile(MicrocontrollerBoardType.Stm32BluePillF103C8);

        Assert.Equal(3.3, profile.OperatingLogicVoltage);
        var pb12 = profile.FindPin("PB12");
        Assert.NotNull(pb12);
        Assert.True(pb12.Is5VTolerant);
    }
}
