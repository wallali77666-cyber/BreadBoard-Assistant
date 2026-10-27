// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/CpuAffinityManager.cs
// PURPOSE: Pins hot-path threads to the physical cores of the i5-13600K and
//          locks critical model/frame memory into physical RAM (no page file).
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace BreadboardAOI.Hardware;

/// <summary>
/// Win32 thread-affinity + memory-lock helpers for the fixed workstation topology:
/// <list type="bullet">
///   <item><description>Logical 0..11  (P-Cores)  = Qwen3-VL MoE + Kokoro TTS.</description></item>
///   <item><description>Logical 12..17 (E-Cores)  = 60 FPS capture, homography, YOLO pose.</description></item>
///   <item><description>Logical 18..19 (E-Cores)  = DRC engine + SkiaSharp CAD UI.</description></item>
/// </list>
/// </summary>
public static class CpuAffinityManager
{
    /// <summary>P-Core logical processors 0..11 (mask 0x0000_0FFF).</summary>
    public const nuint PCoresMask = 0x00FFF;

    /// <summary>Vision E-Core logical processors 12..17 (mask 0x0003_F000).</summary>
    public const nuint ECoresVisionMask = 0x3F000;

    /// <summary>DRC + UI E-Core logical processors 18..19 (mask 0x000C_0000).</summary>
    public const nuint ECoresDrcUiMask = 0xC0000;

    public const int PCoreLogicalCount = 12;
    public const int VisionECoreCount = 6;
    public const int DrcUiECoreCount = 2;

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

    /// <summary>
    /// Pins the calling thread to <paramref name="affinityMask"/>.
    /// Returns false (and the Win32 error) instead of throwing when the mask is not
    /// available on the current machine, so the app still runs on non-hybrid CPUs.
    /// </summary>
    public static bool TryPinCurrentThread(nuint affinityMask, out int win32Error)
    {
        if (!OperatingSystem.IsWindows() || affinityMask == 0)
        {
            win32Error = affinityMask == 0 ? 87 /* ERROR_INVALID_PARAMETER */ : -1;
            return false;
        }

        nuint previousMask = SetThreadAffinityMask(GetCurrentThread(), affinityMask);
        if (previousMask == 0)
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        return true;
    }

    /// <summary>Pins the calling thread to the 12 P-Core logical processors (0x00FFF).</summary>
    public static bool PinCurrentThreadToPCores() => TryPinCurrentThread(PCoresMask, out _);

    /// <summary>Pins the calling thread to the 6 vision E-Core logical processors (0x3F000).</summary>
    public static bool PinCurrentThreadToVisionECores() => TryPinCurrentThread(ECoresVisionMask, out _);

    /// <summary>Pins the calling thread to the 2 DRC/UI E-Core logical processors (0xC0000).</summary>
    public static bool PinCurrentThreadToDrcUiECores() => TryPinCurrentThread(ECoresDrcUiMask, out _);

    /// <summary>Logical processor the calling thread is currently scheduled on, or -1 off Windows.</summary>
    public static int CurrentProcessorIndex => OperatingSystem.IsWindows() ? (int)GetCurrentProcessorNumber() : -1;

    /// <summary>
    /// Locks a native memory range into physical RAM so Windows can never page
    /// Qwen3-VL MoE expert weights (or frame buffers) to the NVMe SSD.
    /// Fails gracefully when the process working-set quota is smaller than requested.
    /// </summary>
    public static bool TryLockMemory(IntPtr address, nuint byteCount)
    {
        if (!OperatingSystem.IsWindows() || address == IntPtr.Zero || byteCount == 0)
            return false;

        return VirtualLock(address, byteCount);
    }

    /// <summary>Releases a range previously locked with <see cref="TryLockMemory"/>.</summary>
    public static bool TryUnlockMemory(IntPtr address, nuint byteCount)
    {
        if (!OperatingSystem.IsWindows() || address == IntPtr.Zero || byteCount == 0)
            return false;

        return VirtualUnlock(address, byteCount);
    }
}
