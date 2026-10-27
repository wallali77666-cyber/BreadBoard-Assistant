// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/CpuAffinityManager.cs
// PURPOSE: Pins hot-path threads to physical cores and locks critical memory
//          into RAM. On the target i5-13600K it uses the fixed P/E-core masks;
//          on any other Windows PC it derives safe masks from
//          Environment.ProcessorCount and never throws when a mask is rejected.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace BreadboardAOI.Hardware;

/// <summary>Logical roles used by the dual-loop pipeline.</summary>
public enum AffinityRole
{
    /// <summary>Qwen3-VL MoE + Kokoro TTS (P-Cores on the target workstation).</summary>
    Ai = 0,

    /// <summary>60 FPS capture, homography and YOLO (E-Cores on the target workstation).</summary>
    Vision = 1,

    /// <summary>DRC engine + SkiaSharp UI thread (E-Cores on the target workstation).</summary>
    DrcUi = 2
}

/// <summary>Resolved affinity masks for the machine the process is running on.</summary>
public readonly record struct AffinityPlan(
    int LogicalProcessorCount,
    bool IsTargetWorkstation,
    nuint AiMask,
    nuint VisionMask,
    nuint DrcUiMask,
    string Description);

/// <summary>
/// Win32 thread-affinity + memory-lock helpers.
/// <para>
/// Target workstation (20 logical processors or more): the locked i5-13600K split
/// <c>AI = 0x00FFF</c>, <c>Vision = 0x3F000</c>, <c>DRC/UI = 0xC0000</c>.
/// </para>
/// <para>
/// Any smaller machine: masks are computed inside
/// <c>(1 &lt;&lt; Environment.ProcessorCount) - 1</c>, with the lower half assigned to
/// AI and the upper half split between Vision and DRC/UI.
/// </para>
/// </summary>
public static class CpuAffinityManager
{
    /// <summary>Logical processor count at or above which the fixed 20-thread hybrid masks apply.</summary>
    public const int HybridLogicalProcessorThreshold = 20;

    // Locked masks for the i5-13600K workstation (0..11 P, 12..17 vision E, 18..19 DRC/UI E).
    public const nuint PCoresMask = 0x00FFF;
    public const nuint ECoresVisionMask = 0x3F000;
    public const nuint ECoresDrcUiMask = 0xC0000;

    public const int PCoreLogicalCount = 12;
    public const int VisionECoreCount = 6;
    public const int DrcUiECoreCount = 2;

    private static readonly Lazy<AffinityPlan> PlanLazy = new(BuildPlan, isThreadSafe: true);

    /// <summary>Masks valid for the current machine. Computed once and cached.</summary>
    public static AffinityPlan Plan => PlanLazy.Value;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessorNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualLock(IntPtr lpAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualUnlock(IntPtr lpAddress, nuint dwSize);

    private static AffinityPlan BuildPlan()
    {
        int processors = Math.Max(1, Environment.ProcessorCount);

        if (processors >= HybridLogicalProcessorThreshold)
        {
            return new AffinityPlan(
                processors,
                IsTargetWorkstation: true,
                AiMask: PCoresMask,
                VisionMask: ECoresVisionMask,
                DrcUiMask: ECoresDrcUiMask,
                Description: $"Target hybrid CPU ({processors} logical): AI 0x{PCoresMask:X}, Vision 0x{ECoresVisionMask:X}, DRC/UI 0x{ECoresDrcUiMask:X}.");
        }

        nuint fullMask = processors >= 64 ? nuint.MaxValue : (nuint)((1UL << processors) - 1);
        nuint aiMask;
        nuint visionMask;
        nuint drcUiMask;

        if (processors <= 2)
        {
            // Too few logical CPUs to split without starving a loop; every role may use all of them.
            aiMask = fullMask;
            visionMask = fullMask;
            drcUiMask = fullMask;
        }
        else
        {
            int aiBits = processors / 2;
            int remaining = processors - aiBits;
            int drcUiBits = Math.Max(1, remaining / 4);
            int visionBits = Math.Max(1, remaining - drcUiBits);

            aiMask = LowBits(aiBits);
            visionMask = LowBits(visionBits) << aiBits;
            drcUiMask = fullMask & ~(aiMask | visionMask);
        }

        return new AffinityPlan(
            processors,
            IsTargetWorkstation: false,
            AiMask: aiMask,
            VisionMask: visionMask,
            DrcUiMask: drcUiMask,
            Description: $"Portable CPU ({processors} logical): AI 0x{aiMask:X}, Vision 0x{visionMask:X}, DRC/UI 0x{drcUiMask:X}.");
    }

    private static nuint LowBits(int bitCount) =>
        bitCount >= 64 ? nuint.MaxValue : (nuint)((1UL << bitCount) - 1);

    /// <summary>
    /// Pins the calling thread to <paramref name="affinityMask"/> via Win32
    /// <c>SetThreadAffinityMask</c>. Never throws: returns false with the Win32
    /// error when the OS rejects the mask (restricted process affinity, a CPU
    /// group that does not contain those cores, or a container).
    /// </summary>
    public static bool TryPinCurrentThread(nuint affinityMask, out int win32Error)
    {
        if (affinityMask == 0)
        {
            win32Error = 87; // ERROR_INVALID_PARAMETER
            return false;
        }

        if (!OperatingSystem.IsWindows())
        {
            win32Error = 50; // ERROR_NOT_SUPPORTED
            return false;
        }

        try
        {
            nuint previousMask = SetThreadAffinityMask(GetCurrentThread(), affinityMask);
            if (previousMask == 0)
            {
                win32Error = Marshal.GetLastPInvokeError();
                return false;
            }

            win32Error = 0;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException
                                       or EntryPointNotFoundException
                                       or BadImageFormatException
                                       or SEHException
                                       or MarshalDirectiveException)
        {
            win32Error = ex.HResult;
            return false;
        }
    }

    /// <summary>Pins the calling thread using the resolved mask for <paramref name="role"/>.</summary>
    public static bool TryPinCurrentThread(AffinityRole role, out int win32Error) =>
        TryPinCurrentThread(MaskFor(role), out win32Error);

    /// <summary>Legacy name kept for the orchestration code; now resolves the portable AI mask.</summary>
    public static bool PinCurrentThreadToPCores() => TryPinCurrentThread(Plan.AiMask, out _);

    /// <summary>Pins to the resolved vision mask (fixed 0x3F000 on the target workstation).</summary>
    public static bool PinCurrentThreadToVisionECores() => TryPinCurrentThread(Plan.VisionMask, out _);

    /// <summary>Pins to the resolved DRC/UI mask (fixed 0xC0000 on the target workstation).</summary>
    public static bool PinCurrentThreadToDrcUiECores() => TryPinCurrentThread(Plan.DrcUiMask, out _);

    public static nuint MaskFor(AffinityRole role) => role switch
    {
        AffinityRole.Ai => Plan.AiMask,
        AffinityRole.Vision => Plan.VisionMask,
        AffinityRole.DrcUi => Plan.DrcUiMask,
        _ => Plan.AiMask
    };

    /// <summary>Logical processor the calling thread is currently scheduled on, or -1 when unavailable.</summary>
    public static int CurrentProcessorIndex
    {
        get
        {
            if (!OperatingSystem.IsWindows())
                return -1;

            try
            {
                return (int)GetCurrentProcessorNumber();
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
            {
                return -1;
            }
        }
    }

    /// <summary>
    /// Locks a native memory range into physical RAM so Windows can never page
    /// Qwen3-VL MoE expert weights (or frame buffers) to the NVMe SSD.
    /// Fails gracefully when the process working-set quota is smaller than requested.
    /// </summary>
    public static bool TryLockMemory(IntPtr address, nuint byteCount)
    {
        if (!OperatingSystem.IsWindows() || address == IntPtr.Zero || byteCount == 0)
            return false;

        try
        {
            return VirtualLock(address, byteCount);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return false;
        }
    }

    /// <summary>Releases a range previously locked with <see cref="TryLockMemory"/>.</summary>
    public static bool TryUnlockMemory(IntPtr address, nuint byteCount)
    {
        if (!OperatingSystem.IsWindows() || address == IntPtr.Zero || byteCount == 0)
            return false;

        try
        {
            return VirtualUnlock(address, byteCount);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            return false;
        }
    }
}
