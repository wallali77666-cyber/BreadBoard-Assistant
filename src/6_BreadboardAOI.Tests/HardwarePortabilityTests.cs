using System;
using BreadboardAOI.Hardware;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class HardwarePortabilityTests
{
    [Fact]
    public void Affinity_plan_only_sets_bits_that_exist_on_this_machine()
    {
        AffinityPlan plan = CpuAffinityManager.Plan;
        int processors = Math.Max(1, Environment.ProcessorCount);

        plan.LogicalProcessorCount.Should().Be(processors);

        ulong fullMask = processors >= 64 ? ulong.MaxValue : (1UL << processors) - 1;

        foreach (nuint mask in new[] { plan.AiMask, plan.VisionMask, plan.DrcUiMask })
        {
            mask.Should().NotBe(0, "every role must retain at least one logical processor");
            ((ulong)mask & ~fullMask).Should().Be(0, "masks may never reference non-existent CPUs");
        }

        if (processors >= CpuAffinityManager.HybridLogicalProcessorThreshold)
        {
            plan.IsTargetWorkstation.Should().BeTrue();
            plan.AiMask.Should().Be(CpuAffinityManager.PCoresMask);
            plan.VisionMask.Should().Be(CpuAffinityManager.ECoresVisionMask);
            plan.DrcUiMask.Should().Be(CpuAffinityManager.ECoresDrcUiMask);
        }
        else
        {
            plan.IsTargetWorkstation.Should().BeFalse();
        }
    }

    [Fact]
    public void Affinity_plan_covers_every_logical_processor_it_manages()
    {
        AffinityPlan plan = CpuAffinityManager.Plan;
        if (Environment.ProcessorCount < 4)
            return;

        int coveredProcessors = Math.Min(Environment.ProcessorCount, CpuAffinityManager.HybridLogicalProcessorThreshold);
        ulong expected = (1UL << coveredProcessors) - 1;
        ulong union = (ulong)(plan.AiMask | plan.VisionMask | plan.DrcUiMask);

        union.Should().Be(expected, "the three loops together should own every managed logical processor");
    }

    [Fact]
    public void Pinning_never_throws_and_rejects_an_empty_mask()
    {
        CpuAffinityManager.TryPinCurrentThread((nuint)0, out int error).Should().BeFalse();
        error.Should().NotBe(0);

        // Even when the OS rejects the mask, the call must return false instead of throwing.
        Action pin = () => CpuAffinityManager.TryPinCurrentThread(CpuAffinityManager.Plan.AiMask, out _);
        pin.Should().NotThrow();
    }

    [Fact]
    public void Vram_monitor_reports_usable_numbers_on_any_machine()
    {
        using var monitor = new VramBudgetMonitor();
        VramSnapshot snapshot = monitor.GetSnapshot();

        snapshot.IsAvailable.Should().BeTrue();
        snapshot.TotalBytes.Should().BeGreaterThan(0);
        snapshot.IsMocked.Should().Be(!monitor.HasRealGpu);

        if (snapshot.IsMocked)
        {
            snapshot.TotalBytes.Should().Be(VramBudgetMonitor.TargetTotalVramBytes);
            snapshot.IsRealGpu.Should().BeFalse();
            monitor.CanLoadQwen(out _).Should().BeFalse("mock VRAM must never green-light the MoE model");
        }
    }
}
