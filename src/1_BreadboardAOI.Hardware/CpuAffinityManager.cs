// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/CpuAffinityManager.cs
// PURPOSE: Pins hot-path threads and processes to physical CPU cores and locks
//          critical memory into RAM via Win32 VirtualLock.
//          Target workstation (Intel Core i5-13600K):
//            - P-Cores:  0x00FFF (cores 0..11, 12 logical threads) for llama-server
//                        (Qwen3-VL MoE + Kokoro TTS) + VirtualLock mlock.
//            - E-Cores:  0xFF000 (cores 12..19, 8 logical threads) for Vision/DRC
//                        (60 FPS optical capture, ArUco homography, YOLO & KiCad DRC).
//          Supports dual GPU hardware tiers:
//            - RTX 3060 12GB: offload 22 MoE experts to CPU (--n-cpu-moe 22)
//            - RTX 5050 8GB:  offload 34 MoE experts to CPU (--n-cpu-moe 34)
// ============================================================================

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace BreadboardAOI.Hardware;

/// <summary>Hardware GPU profile tier targeting Qwen3-VL MoE offload settings.</summary>
public enum GpuHardwareTier
{
    /// <summary>NVIDIA GeForce RTX 3060 12GB VRAM (Target Workstation). Requires --n-cpu-moe 22.</summary>
    Rtx3060_12Gb = 0,

    /// <summary>NVIDIA GeForce RTX 5050 8GB VRAM (Next-Gen Compact). Requires --n-cpu-moe 34.</summary>
    Rtx5050_8Gb = 1,

    /// <summary>Generic or custom GPU tier with dynamically computed budget.</summary>
    Custom = 2
}

/// <summary>MoE offload and memory specifications for a GPU hardware profile.</summary>
public readonly record struct GpuMoEProfile(
    GpuHardwareTier Tier,
    string ModelName,
    ulong TotalVramBytes,
    int CpuMoeOffloadCount,
    string CpuMoeArgument,
    ulong RecommendedGpuMoEBudgetBytes,
    string Description)
{
    public double TotalVramGigabytes => TotalVramBytes / 1024d / 1024d / 1024d;

    public double RecommendedGpuMoEGigabytes => RecommendedGpuMoEBudgetBytes / 1024d / 1024d / 1024d;

    /// <summary>Default profile for NVIDIA GeForce RTX 3060 12GB: --n-cpu-moe 22.</summary>
    public static GpuMoEProfile Rtx3060 { get; } = new(
        Tier: GpuHardwareTier.Rtx3060_12Gb,
        ModelName: "NVIDIA GeForce RTX 3060 (12GB)",
        TotalVramBytes: 12UL * 1024UL * 1024UL * 1024UL,
        CpuMoeOffloadCount: 22,
        CpuMoeArgument: "--n-cpu-moe 22",
        RecommendedGpuMoEBudgetBytes: 10_600UL * 1024UL * 1024UL,
        Description: "RTX 3060 12GB: 22 MoE experts offloaded to CPU RAM; fits Qwen3-VL within 10.6 GB VRAM budget.");

    /// <summary>Default profile for NVIDIA GeForce RTX 5050 8GB: --n-cpu-moe 34.</summary>
    public static GpuMoEProfile Rtx5050 { get; } = new(
        Tier: GpuHardwareTier.Rtx5050_8Gb,
        ModelName: "NVIDIA GeForce RTX 5050 (8GB)",
        TotalVramBytes: 8UL * 1024UL * 1024UL * 1024UL,
        CpuMoeOffloadCount: 34,
        CpuMoeArgument: "--n-cpu-moe 34",
        RecommendedGpuMoEBudgetBytes: 6_600UL * 1024UL * 1024UL,
        Description: "RTX 5050 8GB: 34 MoE experts offloaded to CPU RAM; fits Qwen3-VL within 6.6 GB VRAM budget.");
}

/// <summary>Logical roles used by the dual-loop Breadboard AOI pipeline.</summary>
public enum AffinityRole
{
    /// <summary>Qwen3-VL MoE + Kokoro TTS (P-Cores: 0x00FFF on the target workstation).</summary>
    Ai = 0,

    /// <summary>60 FPS capture, homography and YOLO (E-Cores: 0x3F000 or 0xFF000).</summary>
    Vision = 1,

    /// <summary>DRC engine + SkiaSharp UI thread (E-Cores: 0xC0000 or 0xFF000).</summary>
    DrcUi = 2,

    /// <summary>Unified Vision + DRC engine thread group (E-Cores: 0xFF000 on target workstation).</summary>
    VisionDrc = 3
}

/// <summary>Resolved affinity masks for the machine the process is running on.</summary>
public readonly record struct AffinityPlan(
    int LogicalProcessorCount,
    bool IsTargetWorkstation,
    nuint AiMask,
    nuint VisionMask,
    nuint DrcUiMask,
    nuint ECoresMask,
    string Description,
    string CpuModel = "");

/// <summary>Runtime state recorded for one affinity role.</summary>
public readonly record struct AffinityRoleStatus(
    bool Pinned,
    int Win32Error,
    int LastProcessorIndex)
{
    /// <summary>True when a pin was never attempted for this role.</summary>
    public bool IsUnknown => !Pinned && Win32Error == 0;
}

/// <summary>
/// Restores the previous affinity mask when disposed. Created by
/// <see cref="CpuAffinityManager.PinCurrentThreadFor"/> and used only around
/// synchronous work so the set/restore pair stays on one OS thread.
/// </summary>
public readonly struct AffinityScope : IDisposable
{
    private readonly nuint _previousMask;
    private readonly bool _restore;

    internal AffinityScope(nuint previousMask, bool restore)
    {
        _previousMask = previousMask;
        _restore = restore;
    }

    public bool Pinned => _restore;

    public void Dispose()
    {
        if (_restore)
            CpuAffinityManager.RestoreCurrentThreadAffinity(_previousMask);
    }
}

/// <summary>
/// Win32 thread-affinity + memory-lock helpers.
/// <para>
/// Target workstation (20 logical processors, e.g. i5-13600K):
/// - P-Cores: <c>0x00FFF</c> (cores 0..11) for llama-server + TTS
/// - E-Cores: <c>0xFF000</c> (cores 12..19) for Vision/DRC (<c>0x3F000</c> for Vision, <c>0xC0000</c> for DRC/UI)
/// </para>
/// <para>
/// Portable systems: masks are derived proportionally from <c>Environment.ProcessorCount</c>.
/// </para>
/// </summary>
public static class CpuAffinityManager
{
    /// <summary>Logical processor count at or above which the fixed 20-thread hybrid masks apply.</summary>
    public const int HybridLogicalProcessorThreshold = 20;

    // Locked masks for the i5-13600K workstation:
    // Cores 0..11: P-Cores (12 logical threads)
    public const nuint PCoresMask = 0x00FFF;

    // Cores 12..19: E-Cores (8 logical threads)
    public const nuint ECoresVisionMask = 0x3F000;    // Cores 12..17 (6 E-cores)
    public const nuint ECoresDrcUiMask = 0xC0000;     // Cores 18..19 (2 E-cores)
    public const nuint ECoresVisionDrcMask = 0xFF000; // Cores 12..19 (All 8 E-cores for Vision/DRC)

    public const int PCoreLogicalCount = 12;
    public const int VisionECoreCount = 6;
    public const int DrcUiECoreCount = 2;
    public const int TotalECoreCount = 8;

    private static readonly Lazy<AffinityPlan> PlanLazy = new(BuildPlan, isThreadSafe: true);

    /// <summary>Masks valid for the current machine. Computed once and cached.</summary>
    public static AffinityPlan Plan => PlanLazy.Value;

    /// <summary>Exact CPU Model Name detected from the system (e.g. "11th Gen Intel(R) Core(TM) i5-1145G7 @ 2.60GHz").</summary>
    public static string CpuModelName { get; } = DetectCpuModelName();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nuint SetThreadAffinityMask(IntPtr hThread, nuint dwThreadAffinityMask);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentProcessorNumber();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualLock(IntPtr lpAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualUnlock(IntPtr lpAddress, nuint dwSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessWorkingSetSize(IntPtr hProcess, nuint dwMinimumWorkingSetSize, nuint dwMaximumWorkingSetSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessWorkingSetSize(IntPtr hProcess, out nuint lpMinimumWorkingSetSize, out nuint lpMaximumWorkingSetSize);

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
                ECoresMask: ECoresVisionDrcMask,
                Description: $"Target hybrid CPU ({processors} logical): AI P-Cores 0x{PCoresMask:X}, Vision E-Cores 0x{ECoresVisionMask:X}, DRC/UI 0x{ECoresDrcUiMask:X}, Full E-Cores 0x{ECoresVisionDrcMask:X}.",
                CpuModel: CpuModelName);
        }

        nuint fullMask = processors >= 64 ? nuint.MaxValue : (nuint)((1UL << processors) - 1);
        nuint aiMask;
        nuint visionMask;
        nuint drcUiMask;
        nuint eCoresMask;

        if (processors <= 2)
        {
            // Too few logical CPUs to split without starving a loop; every role may use all of them.
            aiMask = fullMask;
            visionMask = fullMask;
            drcUiMask = fullMask;
            eCoresMask = fullMask;
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
            if (drcUiMask == 0)
                drcUiMask = visionMask;

            eCoresMask = visionMask | drcUiMask;
        }

        return new AffinityPlan(
            processors,
            IsTargetWorkstation: false,
            AiMask: aiMask,
            VisionMask: visionMask,
            DrcUiMask: drcUiMask,
            ECoresMask: eCoresMask,
            Description: $"Portable CPU ({processors} logical): AI 0x{aiMask:X}, Vision 0x{visionMask:X}, DRC/UI 0x{drcUiMask:X}, Vision/DRC 0x{eCoresMask:X}.",
            CpuModel: CpuModelName);
    }

    private static string DetectCpuModelName()
    {
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
                var name = key?.GetValue("ProcessorNameString") as string;
                if (!string.IsNullOrWhiteSpace(name))
                    return name.Trim();
            }
            catch
            {
                // Registry probe failed, fall back
            }
        }

        string? env = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
        return !string.IsNullOrWhiteSpace(env) ? env.Trim() : "x86_64 Processor";
    }

    private static nuint LowBits(int bitCount) =>
        bitCount >= 64 ? nuint.MaxValue : (nuint)((1UL << bitCount) - 1);

    /// <summary>
    /// Pins the calling thread to <paramref name="affinityMask"/> via Win32 SetThreadAffinityMask.
    /// Never throws: returns false with the Win32 error code when rejected.
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
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException or MarshalDirectiveException)
        {
            win32Error = ex.HResult;
            return false;
        }
    }

    private static readonly object RoleSync = new();
    private static readonly bool[] _rolePinned = new bool[4];
    private static readonly int[] _roleErrors = new int[4];
    private static int _aiCore = -1;
    private static int _visionCore = -1;
    private static int _drcUiCore = -1;
    private static int _visionDrcCore = -1;

    /// <summary>Pins the calling thread using the resolved mask for <paramref name="role"/>.</summary>
    public static bool TryPinCurrentThread(AffinityRole role, out int win32Error)
    {
        bool pinned = TryPinCurrentThread(MaskFor(role), out win32Error);
        RecordRolePin(role, pinned, win32Error);
        if (pinned)
            ReportProcessor(role);
        return pinned;
    }

    /// <summary>Replaces the current thread affinity mask and returns the previous mask.</summary>
    public static bool TryExchangeCurrentThreadAffinity(nuint newMask, out nuint previousMask, out int win32Error)
    {
        previousMask = 0;
        if (newMask == 0)
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
            nuint prev = SetThreadAffinityMask(GetCurrentThread(), newMask);
            if (prev == 0)
            {
                win32Error = Marshal.GetLastPInvokeError();
                return false;
            }

            previousMask = prev;
            win32Error = 0;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException or MarshalDirectiveException)
        {
            win32Error = ex.HResult;
            return false;
        }
    }

    /// <summary>
    /// Scopes thread affinity for synchronous operations (e.g. Kokoro TTS synthesis or KiCad DRC).
    /// Restores the previous thread mask when disposed.
    /// </summary>
    public static AffinityScope PinCurrentThreadFor(AffinityRole role)
    {
        nuint previous = 0;
        bool pinned = TryExchangeCurrentThreadAffinity(MaskFor(role), out previous, out int error);
        if (pinned)
            ReportProcessor(role);
        return new AffinityScope(previous, pinned);
    }

    internal static void RestoreCurrentThreadAffinity(nuint previousMask)
    {
        if (previousMask != 0 && OperatingSystem.IsWindows())
        {
            try
            {
                SetThreadAffinityMask(GetCurrentThread(), previousMask);
            }
            catch (Exception)
            {
                // Best-effort restore
            }
        }
    }

    /// <summary>
    /// Pins an external child process (like llama-server.exe or piper.exe) to the specified role's cores.
    /// </summary>
    public static bool TryPinProcess(Process process, AffinityRole role, out int win32Error)
    {
        ArgumentNullException.ThrowIfNull(process);

        if (!OperatingSystem.IsWindows())
        {
            win32Error = 50;
            return false;
        }

        try
        {
            nuint mask = MaskFor(role);
            process.ProcessorAffinity = (IntPtr)(nint)mask;
            RecordRolePin(role, true, 0);
            win32Error = 0;
            return true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            win32Error = ex.HResult != 0 ? ex.HResult : 1;
            RecordRolePin(role, false, win32Error);
            return false;
        }
    }

    /// <summary>
    /// Pins llama-server.exe to P-Cores (mask 0x00FFF on the target workstation) to guarantee
    /// maximum single-thread instruction throughput for Qwen3-VL MoE prompt processing and generation.
    /// </summary>
    public static bool TryPinLlamaServer(Process llamaProcess, out int win32Error) =>
        TryPinProcess(llamaProcess, AffinityRole.Ai, out win32Error);

    /// <summary>
    /// Pins Vision, Homography, and DRC pipelines to E-Cores (mask 0xFF000 on the target workstation).
    /// </summary>
    public static bool TryPinVisionDrc(Process visionProcess, out int win32Error) =>
        TryPinProcess(visionProcess, AffinityRole.VisionDrc, out win32Error);

    public static void RecordRolePin(AffinityRole role, bool pinned, int win32Error)
    {
        int index = (int)role;
        if (index >= 0 && index < 4)
        {
            lock (RoleSync)
            {
                _rolePinned[index] = pinned;
                _roleErrors[index] = win32Error;
            }
        }
    }

    public static void ReportProcessor(AffinityRole role)
    {
        int index = CurrentProcessorIndex;
        if (index >= 0)
            SetLastProcessor(role, index);
    }

    public static void ReportProcessor(AffinityRole role, int processorIndex)
    {
        if (processorIndex >= 0)
            SetLastProcessor(role, processorIndex);
    }

    private static void SetLastProcessor(AffinityRole role, int index)
    {
        switch (role)
        {
            case AffinityRole.Ai: Volatile.Write(ref _aiCore, index); break;
            case AffinityRole.Vision: Volatile.Write(ref _visionCore, index); break;
            case AffinityRole.DrcUi: Volatile.Write(ref _drcUiCore, index); break;
            case AffinityRole.VisionDrc: Volatile.Write(ref _visionDrcCore, index); break;
        }
    }

    private static int LastProcessor(AffinityRole role) => role switch
    {
        AffinityRole.Ai => Volatile.Read(ref _aiCore),
        AffinityRole.Vision => Volatile.Read(ref _visionCore),
        AffinityRole.DrcUi => Volatile.Read(ref _drcUiCore),
        AffinityRole.VisionDrc => Volatile.Read(ref _visionDrcCore),
        _ => -1
    };

    public static AffinityRoleStatus GetRoleStatus(AffinityRole role)
    {
        int index = (int)role;
        if (index < 0 || index >= 4)
            return new AffinityRoleStatus(false, 0, -1);

        lock (RoleSync)
        {
            return new AffinityRoleStatus(_rolePinned[index], _roleErrors[index], LastProcessor(role));
        }
    }

    public static string DescribeRole(AffinityRole role) => role switch
    {
        AffinityRole.Ai => "AI / LLAMA-SERVER (P-CORES: 0x00FFF)",
        AffinityRole.Vision => "VISION / YOLO (E-CORES: 0x3F000)",
        AffinityRole.DrcUi => "DRC / UI (E-CORES: 0xC0000)",
        AffinityRole.VisionDrc => "VISION & DRC UNIFIED (E-CORES: 0xFF000)",
        _ => role.ToString()
    };

    public static string DescribeWork(AffinityRole role) => role switch
    {
        AffinityRole.Ai => "Qwen3-VL MoE + Kokoro/Piper TTS (llama-server)",
        AffinityRole.Vision => "60 FPS Cam + Warp + YOLOv11s-Pose",
        AffinityRole.DrcUi => "KiCad DRC Rules + SkiaSharp Viewport",
        AffinityRole.VisionDrc => "60 FPS Camera + YOLO + Homography + KiCad DRC",
        _ => string.Empty
    };

    public static int CoreCount(nuint mask) =>
        BitOperations.PopCount((ulong)mask);

    public static string DescribeMaskCores(nuint mask, int maxProcessors = 64)
    {
        if (mask == 0) return "NONE";
        ulong bits = (ulong)mask;
        int limit = Math.Min(64, maxProcessors);
        var ranges = new List<string>();
        int start = -1;
        for (int i = 0; i <= limit; i++)
        {
            bool set = i < limit && ((bits & (1UL << i)) != 0);
            if (set)
            {
                if (start < 0) start = i;
            }
            else
            {
                if (start >= 0)
                {
                    int end = i - 1;
                    ranges.Add(start == end ? $"{start}" : $"{start}..{end}");
                    start = -1;
                }
            }
        }
        return ranges.Count > 0 ? string.Join(", ", ranges) : "NONE";
    }

    /// <summary>Pins calling thread to P-Cores (mask 0x00FFF on target workstation) for AI / llama-server work.</summary>
    public static bool PinCurrentThreadToPCores() => TryPinCurrentThread(Plan.AiMask, out _);

    /// <summary>Pins calling thread to E-Cores (mask 0xFF000 on target workstation) for Vision/DRC pipelines.</summary>
    public static bool PinCurrentThreadToECores() => TryPinCurrentThread(Plan.ECoresMask, out _);

    /// <summary>Pins to the dedicated vision mask (0x3F000 on target workstation).</summary>
    public static bool PinCurrentThreadToVisionECores() => TryPinCurrentThread(Plan.VisionMask, out _);

    /// <summary>Pins to the dedicated DRC/UI mask (0xC0000 on target workstation).</summary>
    public static bool PinCurrentThreadToDrcUiECores() => TryPinCurrentThread(Plan.DrcUiMask, out _);

    public static nuint MaskFor(AffinityRole role) => role switch
    {
        AffinityRole.Ai => Plan.AiMask,
        AffinityRole.Vision => Plan.VisionMask,
        AffinityRole.DrcUi => Plan.DrcUiMask,
        AffinityRole.VisionDrc => Plan.ECoresMask,
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
    /// Locks a native memory range into physical RAM via Win32 VirtualLock so Windows
    /// can never page Qwen3-VL MoE weights or high-speed camera frame buffers to disk.
    /// Automatically expands the process working-set quota if needed.
    /// </summary>
    public static bool TryLockMemory(IntPtr address, nuint byteCount)
    {
        if (!OperatingSystem.IsWindows() || address == IntPtr.Zero || byteCount == 0)
            return false;

        try
        {
            if (VirtualLock(address, byteCount))
                return true;

            int error = Marshal.GetLastPInvokeError();
            // ERROR_WORKING_SET_QUOTA = 1453 (0x5AD)
            if (error == 1453)
            {
                if (EnsureWorkingSetQuota(byteCount) && VirtualLock(address, byteCount))
                    return true;
            }

            return false;
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

    /// <summary>
    /// Expands the process minimum and maximum working set quota to accommodate native memory locks.
    /// </summary>
    public static bool EnsureWorkingSetQuota(nuint additionalBytes)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            IntPtr process = GetCurrentProcess();
            if (!GetProcessWorkingSetSize(process, out nuint minSize, out nuint maxSize))
                return false;

            nuint newMin = minSize + additionalBytes + (nuint)(32UL * 1024UL * 1024UL); // +32MB safety margin
            nuint newMax = Math.Max(maxSize, newMin + (nuint)(64UL * 1024UL * 1024UL));

            return SetProcessWorkingSetSize(process, newMin, newMax);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Resolves the GPU MoE Profile for the requested tier.
    /// Returns RTX 3060 (--n-cpu-moe 22) or RTX 5050 (--n-cpu-moe 34).
    /// </summary>
    public static GpuMoEProfile GetProfile(GpuHardwareTier tier) => tier switch
    {
        GpuHardwareTier.Rtx5050_8Gb => GpuMoEProfile.Rtx5050,
        _ => GpuMoEProfile.Rtx3060
    };

    /// <summary>
    /// Detects GPU tier based on GPU device name and total VRAM reported by NVML or DirectX.
    /// </summary>
    public static GpuMoEProfile DetectGpuProfile(string? deviceName, ulong totalVramBytes)
    {
        if (!string.IsNullOrWhiteSpace(deviceName))
        {
            string upper = deviceName.ToUpperInvariant();
            if (upper.Contains("5050") || upper.Contains("4060") || (totalVramBytes > 0 && totalVramBytes < 10UL * 1024UL * 1024UL * 1024UL))
            {
                return GpuMoEProfile.Rtx5050;
            }
        }

        if (totalVramBytes > 0 && totalVramBytes < 10UL * 1024UL * 1024UL * 1024UL)
            return GpuMoEProfile.Rtx5050;

        return GpuMoEProfile.Rtx3060;
    }

    /// <summary>
    /// Formats the complete command line arguments for llama-server.exe including
    /// the exact --n-cpu-moe parameter (22 for RTX 3060 12GB, 34 for RTX 5050 8GB).
    /// </summary>
    public static string BuildLlamaServerCommandLine(
        GpuHardwareTier tier,
        string modelPath,
        string mmprojPath,
        int port = 8089,
        int contextLength = 4096,
        int gpuLayers = 99)
    {
        GpuMoEProfile profile = GetProfile(tier);
        var sb = new StringBuilder();
        sb.Append($"-m \"{modelPath}\" ");
        if (!string.IsNullOrWhiteSpace(mmprojPath))
            sb.Append($"--mmproj \"{mmprojPath}\" ");

        sb.Append($"--port {port} ");
        sb.Append($"-c {contextLength} ");
        sb.Append($"-ngl {gpuLayers} ");
        sb.Append($"{profile.CpuMoeArgument} ");
        sb.Append("--host 127.0.0.1 ");
        sb.Append("--mlock ");
        sb.Append("-t 12 "); // Bind 12 P-Core threads
        return sb.ToString().TrimEnd();
    }
}
