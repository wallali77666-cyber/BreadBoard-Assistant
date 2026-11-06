// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/VramBudgetMonitor.cs
// PURPOSE: Live VRAM telemetry via nvml.dll for NVIDIA RTX 3060 12GB and
//          NVIDIA RTX 5050 8GB workstations. Enables the orchestrator to verify
//          safe headroom before launching llama-server with Qwen3-VL MoE.
//          RTX 3060 12GB: --n-cpu-moe 22 (10.6 GB GPU budget, 22 CPU experts)
//          RTX 5050 8GB:  --n-cpu-moe 34 (6.6 GB GPU budget, 34 CPU experts)
// ============================================================================

using System;
using System.IO;
using System.Runtime.InteropServices;

namespace BreadboardAOI.Hardware;

/// <summary>Point-in-time VRAM reading. <see cref="IsMocked"/> marks synthetic values used when no NVIDIA GPU is present.</summary>
public readonly record struct VramSnapshot(
    bool IsAvailable,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    string? Diagnostic,
    bool IsMocked = false,
    GpuHardwareTier DetectedTier = GpuHardwareTier.Rtx3060_12Gb)
{
    /// <summary>True only when the values came from a real NVML device.</summary>
    public bool IsRealGpu => IsAvailable && !IsMocked;

    public double UsedGigabytes => IsAvailable ? UsedBytes / 1024d / 1024d / 1024d : 0d;

    public double TotalGigabytes => IsAvailable ? TotalBytes / 1024d / 1024d / 1024d : 0d;

    public double FreeGigabytes => IsAvailable ? FreeBytes / 1024d / 1024d / 1024d : 0d;
}

/// <summary>
/// NVML telemetry wrapper with dual-tier budget profiling for both
/// RTX 3060 12GB and RTX 5050 8GB workstations.
/// </summary>
public sealed class VramBudgetMonitor : IDisposable
{
    private const string NvmlLibrary = "nvml.dll";

    /// <summary>Standard NVSMI install location checked when plain <c>nvml.dll</c> is not on the DLL search path.</summary>
    private const string NvmlFallbackPath = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll";

    public const ulong Megabyte = 1024UL * 1024UL;
    public const ulong Gigabyte = 1024UL * Megabyte;

    // Shared Vision, UI, and OS Headroom Reservations
    public const ulong YoloPoseModelBudgetBytes = 450UL * Megabyte;
    public const ulong SkiaSwapChainBudgetBytes = 350UL * Megabyte;
    public const ulong OsReserveBytes = 600UL * Megabyte;

    // RTX 3060 12GB Profile Constants (--n-cpu-moe 22)
    public const ulong Rtx3060TotalVramBytes = 12UL * Gigabyte;
    public const ulong Rtx3060QwenMoEBudgetBytes = 10_600UL * Megabyte;
    public const int Rtx3060CpuMoeCount = 22;

    // RTX 5050 8GB Profile Constants (--n-cpu-moe 34)
    public const ulong Rtx5050TotalVramBytes = 8UL * Gigabyte;
    public const ulong Rtx5050QwenMoEBudgetBytes = 6_600UL * Megabyte;
    public const ulong TargetTotalVramBytes = Rtx3060TotalVramBytes;
    public const int Rtx5050CpuMoeCount = 34;

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory
    {
        public ulong Total;
        public ulong Free;
        public ulong Used;
    }

    [DllImport(NvmlLibrary, EntryPoint = "nvmlInit_v2")]
    private static extern int NvmlInit();

    [DllImport(NvmlLibrary, EntryPoint = "nvmlShutdown")]
    private static extern int NvmlShutdown();

    [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int NvmlDeviceGetHandleByIndex(uint index, out IntPtr device);

    [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int NvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport(NvmlLibrary, EntryPoint = "nvmlDeviceGetName")]
    private static extern int NvmlDeviceGetName(IntPtr device, byte[] name, uint length);

    private IntPtr _device;
    private bool _initialized;
    private bool _disposed;
    private bool _nativeLibraryPreloaded;
    private string? _lastError;
    private GpuHardwareTier? _manualTierOverride;

    /// <summary>True once <c>nvmlInit_v2</c> + GPU handle acquisition succeeded.</summary>
    public bool IsAvailable => _initialized && _device != IntPtr.Zero;

    /// <summary>True when a real NVIDIA GPU is being monitored (false when running on the mock profile).</summary>
    public bool HasRealGpu => IsAvailable;

    private static readonly (string Name, ulong VramBytes) SystemGpuInfo = QuerySystemGpu();

    /// <summary>True if a real GPU (Intel, AMD, Qualcomm, etc.) was detected via Windows registry/system.</summary>
    public bool IsSystemGpuDetected => !string.IsNullOrEmpty(SystemGpuInfo.Name) && !SystemGpuInfo.Name.Contains("No GPU", StringComparison.OrdinalIgnoreCase);

    /// <summary>System GPU name (e.g. Intel(R) Iris(R) Xe Graphics) detected when NVIDIA NVML is not present.</summary>
    public string SystemGpuName => SystemGpuInfo.Name;

    /// <summary>System GPU dedicated/adapter RAM in bytes (e.g. 2 GB for Iris Xe).</summary>
    public ulong SystemVramBytes => SystemGpuInfo.VramBytes;

    /// <summary>Human-readable GPU name reported by NVML, or active system GPU.</summary>
    public string DeviceName { get; private set; } = SystemGpuInfo.Name;

    /// <summary>Last NVML/driver error, or null when everything is healthy.</summary>
    public string? LastError => _lastError;

    /// <summary>Current active hardware tier (auto-detected or manually selected).</summary>
    public GpuHardwareTier ActiveTier => _manualTierOverride ?? DetectedTier;

    /// <summary>Auto-detected hardware tier based on device name and VRAM size.</summary>
    public GpuHardwareTier DetectedTier { get; private set; } = GpuHardwareTier.Rtx3060_12Gb;

    /// <summary>Number of MoE experts to offload to CPU (22 for RTX 3060, 34 for RTX 5050).</summary>
    public int CpuMoeCount => ActiveTier == GpuHardwareTier.Rtx5050_8Gb ? Rtx5050CpuMoeCount : Rtx3060CpuMoeCount;

    /// <summary>llama-server CLI argument snippet for CPU MoE offloading.</summary>
    public string LlamaServerCpuMoeArg => ActiveTier == GpuHardwareTier.Rtx5050_8Gb ? "--n-cpu-moe 34" : "--n-cpu-moe 22";

    /// <summary>Active MoE hardware profile.</summary>
    public GpuMoEProfile ActiveProfile => CpuAffinityManager.GetProfile(ActiveTier);

    /// <summary>Override detected tier with an explicit tier (e.g. from installer UI).</summary>
    public void SetHardwareTier(GpuHardwareTier tier)
    {
        _manualTierOverride = tier;
    }

    /// <summary>
    /// Attempts to initialize NVML. Safe to call repeatedly; failures are cached.
    /// Never throws when the NVIDIA driver is missing.
    /// </summary>
    public bool TryInitialize()
    {
        if (_disposed)
            return false;

        if (IsAvailable)
            return true;

        if (_lastError is not null)
            return false;

        try
        {
            EnsureNvmlProbePath();
            int status = NvmlInit();
            if (status != 0)
            {
                _lastError = $"nvmlInit_v2 failed with status {status}. Is the NVIDIA driver installed?";
                return false;
            }

            _initialized = true;

            status = NvmlDeviceGetHandleByIndex(0, out IntPtr device);
            if (status != 0 || device == IntPtr.Zero)
            {
                _lastError = $"nvmlDeviceGetHandleByIndex_v2 failed with status {status}.";
                TryShutdown();
                return false;
            }

            _device = device;
            DeviceName = ReadDeviceName(device) ?? "NVIDIA GPU";

            // Inspect device memory to auto-classify tier
            if (NvmlDeviceGetMemoryInfo(_device, out NvmlMemory mem) == 0)
            {
                DetectedTier = CpuAffinityManager.DetectGpuProfile(DeviceName, mem.Total).Tier;
            }

            return true;
        }
        catch (DllNotFoundException)
        {
            _lastError = "nvml.dll was not found. NVIDIA driver or NVSMI path missing; using mock VRAM telemetry.";
            return false;
        }
        catch (EntryPointNotFoundException ex)
        {
            _lastError = $"NVML entry point missing: {ex.Message}";
            return false;
        }
        catch (BadImageFormatException ex)
        {
            _lastError = $"nvml.dll has the wrong architecture: {ex.Message}";
            return false;
        }
        catch (Exception ex) when (ex is SEHException or TypeInitializationException or InvalidOperationException)
        {
            _lastError = $"NVML initialization failed: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// Reads current VRAM usage. Returns false when the reading is synthetic
    /// (<see cref="VramSnapshot.IsMocked"/> is then true). Never throws.
    /// </summary>
    public bool TryGetSnapshot(out VramSnapshot snapshot)
    {
        if (!TryInitialize())
        {
            snapshot = CreateMockSnapshot(_lastError, ActiveTier);
            return false;
        }

        try
        {
            int status = NvmlDeviceGetMemoryInfo(_device, out NvmlMemory memory);
            if (status != 0)
            {
                _lastError = $"nvmlDeviceGetMemoryInfo failed with status {status}.";
                snapshot = CreateMockSnapshot(_lastError, ActiveTier);
                return false;
            }

            snapshot = new VramSnapshot(true, memory.Total, memory.Free, memory.Used, null, false, ActiveTier);
            _lastError = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            _lastError = ex.Message;
            snapshot = CreateMockSnapshot(_lastError, ActiveTier);
            return false;
        }
    }

    /// <summary>
    /// Always returns a usable snapshot: real NVML numbers when a GPU is present,
    /// otherwise a simulated profile (12 GB for 3060, 8 GB for 5050) so UI never shows blanks.
    /// </summary>
    public VramSnapshot GetSnapshot()
    {
        TryGetSnapshot(out VramSnapshot snapshot);
        return snapshot;
    }

    /// <summary>
    /// True when the Qwen3-VL MoE budget will fit given the active GPU hardware profile.
    /// For RTX 3060 12GB: checks headroom against 10.6 GB MoE budget (--n-cpu-moe 22).
    /// For RTX 5050 8GB: checks headroom against 6.6 GB MoE budget (--n-cpu-moe 34).
    /// </summary>
    public bool CanLoadQwen(out ulong projectedFreeAfterLoad)
    {
        projectedFreeAfterLoad = 0;
        if (!TryGetSnapshot(out VramSnapshot snapshot) || !snapshot.IsRealGpu)
            return false;

        ulong overhead = YoloPoseModelBudgetBytes + SkiaSwapChainBudgetBytes + OsReserveBytes;
        ulong moeBudget = ActiveTier == GpuHardwareTier.Rtx5050_8Gb
            ? Rtx5050QwenMoEBudgetBytes
            : Rtx3060QwenMoEBudgetBytes;

        ulong required = overhead + moeBudget;
        projectedFreeAfterLoad = snapshot.TotalBytes > required ? snapshot.TotalBytes - required : 0;
        return snapshot.TotalBytes >= required;
    }

    private static VramSnapshot CreateMockSnapshot(string? diagnostic, GpuHardwareTier tier)
    {
        ulong total = tier == GpuHardwareTier.Rtx5050_8Gb ? Rtx5050TotalVramBytes : Rtx3060TotalVramBytes;
        return new VramSnapshot(
            IsAvailable: true,
            TotalBytes: total,
            FreeBytes: total,
            UsedBytes: 0,
            Diagnostic: diagnostic ?? (SystemGpuInfo.VramBytes > 0
                ? $"GPU: {SystemGpuInfo.Name} · {SystemGpuInfo.VramBytes / 1024d / 1024d / 1024d:0.0} GB VRAM · DIRECTX 12 / iGPU"
                : "No NVIDIA GPU detected; simulated VRAM telemetry."),
            IsMocked: true,
            DetectedTier: tier);
    }

    private void EnsureNvmlProbePath()
    {
        if (_nativeLibraryPreloaded || !OperatingSystem.IsWindows())
            return;

        _nativeLibraryPreloaded = true;

        try
        {
            if (!NativeLibrary.TryLoad(NvmlLibrary, out _) && File.Exists(NvmlFallbackPath))
            {
                NativeLibrary.TryLoad(NvmlFallbackPath, out _);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            // Best-effort probe
        }
    }

    private static string? ReadDeviceName(IntPtr device)
    {
        byte[] buffer = new byte[96];
        int status = NvmlDeviceGetName(device, buffer, (uint)buffer.Length);
        if (status != 0)
            return null;

        int length = Array.IndexOf(buffer, (byte)0);
        if (length < 0)
            length = buffer.Length;

        return System.Text.Encoding.ASCII.GetString(buffer, 0, length);
    }

    private void TryShutdown()
    {
        if (!_initialized)
            return;

        try
        {
            NvmlShutdown();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            // Driver disappeared mid-run
        }

        _initialized = false;
        _device = IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;
        TryShutdown();
    }

    /// <summary>
    /// Probes Windows Registry / VideoController for the active system GPU when NVML is not present.
    /// Returns the exact GPU name (e.g. "Intel(R) Iris(R) Xe Graphics") and adapter memory in bytes.
    /// </summary>
    public static (string Name, ulong VramBytes) QuerySystemGpu()
    {
        if (!OperatingSystem.IsWindows())
            return ("DirectX / iGPU Path", 0);

        try
        {
            using var videoClass = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}");
            if (videoClass != null)
            {
                foreach (string subKeyName in videoClass.GetSubKeyNames())
                {
                    if (subKeyName.Length == 4 && char.IsDigit(subKeyName[0]))
                    {
                        using var subKey = videoClass.OpenSubKey(subKeyName);
                        if (subKey == null) continue;

                        string? driverDesc = subKey.GetValue("DriverDesc") as string;
                        if (string.IsNullOrWhiteSpace(driverDesc) ||
                            driverDesc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
                            driverDesc.Contains("Remote", StringComparison.OrdinalIgnoreCase))
                            continue;

                        ulong vram = 0;
                        object? qwMem = subKey.GetValue("HardwareInformation.qwMemorySize");
                        if (qwMem is long l && l > 0) vram = (ulong)l;
                        else if (qwMem is ulong ul) vram = ul;

                        if (vram == 0)
                        {
                            object? mem = subKey.GetValue("HardwareInformation.MemorySize");
                            if (mem is byte[] b && b.Length >= 4)
                                vram = BitConverter.ToUInt32(b, 0);
                            else if (mem is int i && i > 0)
                                vram = (ulong)i;
                        }

                        if (vram == 0)
                        {
                            object? ram = subKey.GetValue("AdapterRAM");
                            if (ram is int r && r > 0) vram = (ulong)r;
                            else if (ram is long lr && lr > 0) vram = (ulong)lr;
                        }

                        return (driverDesc.Trim(), vram);
                    }
                }
            }
        }
        catch
        {
            // Registry probe failed
        }

        return ("DirectX Compatible GPU", 0);
    }
}
