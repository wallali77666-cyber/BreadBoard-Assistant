// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/VramBudgetMonitor.cs
// PURPOSE: Live RTX 3060 12 GB VRAM telemetry via nvml.dll so the orchestrator
//          can refuse to load Qwen3-VL if YOLO + Skia swap-chain already ate the
//          headroom. On machines without an NVIDIA driver it reports a clearly
//          flagged mock 12 GB profile instead of throwing, so the UI and the
//          Qwen pre-flight check keep working everywhere.
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
    bool IsMocked = false)
{
    /// <summary>True only when the values came from a real NVML device.</summary>
    public bool IsRealGpu => IsAvailable && !IsMocked;

    public double UsedGigabytes => IsAvailable ? UsedBytes / 1024d / 1024d / 1024d : 0d;

    public double TotalGigabytes => IsAvailable ? TotalBytes / 1024d / 1024d / 1024d : 0d;

    public double FreeGigabytes => IsAvailable ? FreeBytes / 1024d / 1024d / 1024d : 0d;
}

/// <summary>NVML wrapper with the fixed VRAM budget of the target RTX 3060 workstation.</summary>
public sealed class VramBudgetMonitor : IDisposable
{
    private const string NvmlLibrary = "nvml.dll";

    /// <summary>Standard NVSMI install location checked when plain <c>nvml.dll</c> is not on the DLL search path.</summary>
    private const string NvmlFallbackPath = @"C:\Program Files\NVIDIA Corporation\NVSMI\nvml.dll";

    public const ulong Megabyte = 1024UL * 1024UL;
    public const ulong Gigabyte = 1024UL * Megabyte;

    public const ulong YuoPoseModelBudgetBytes = 450UL * Megabyte;
    public const ulong SkiaSwapChainBudgetBytes = 350UL * Megabyte;
    public const ulong QwenMoEBudgetBytes = 10_600UL * Megabyte;
    public const ulong OsReserveBytes = 600UL * Megabyte;
    public const ulong TargetTotalVramBytes = 12UL * Gigabyte;

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

    /// <summary>True once <c>nvmlInit_v2</c> + GPU handle acquisition succeeded.</summary>
    public bool IsAvailable => _initialized && _device != IntPtr.Zero;

    /// <summary>True when a real NVIDIA GPU is being monitored (false when running on the mock profile).</summary>
    public bool HasRealGpu => IsAvailable;

    /// <summary>Human-readable GPU name reported by NVML, when available.</summary>
    public string DeviceName { get; private set; } = "No NVIDIA GPU detected";

    /// <summary>Last NVML/driver error, or null when everything is healthy.</summary>
    public string? LastError => _lastError;

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
            snapshot = CreateMockSnapshot(_lastError);
            return false;
        }

        try
        {
            int status = NvmlDeviceGetMemoryInfo(_device, out NvmlMemory memory);
            if (status != 0)
            {
                _lastError = $"nvmlDeviceGetMemoryInfo failed with status {status}.";
                snapshot = CreateMockSnapshot(_lastError);
                return false;
            }

            snapshot = new VramSnapshot(true, memory.Total, memory.Free, memory.Used, null);
            _lastError = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or SEHException)
        {
            _lastError = ex.Message;
            snapshot = CreateMockSnapshot(_lastError);
            return false;
        }
    }

    /// <summary>
    /// Always returns a usable snapshot: real NVML numbers when a GPU is present,
    /// otherwise a flagged 12 GB mock profile so the dashboard never shows blanks.
    /// </summary>
    public VramSnapshot GetSnapshot()
    {
        TryGetSnapshot(out VramSnapshot snapshot);
        return snapshot;
    }

    /// <summary>
    /// True when the Qwen3-VL budget would still fit after the YOLO, Skia and OS
    /// reservations. Returns false (never throws) when no real GPU is present.
    /// Used before spawning llama-server.exe.
    /// </summary>
    public bool CanLoadQwen(out ulong projectedFreeAfterLoad)
    {
        projectedFreeAfterLoad = 0;
        if (!TryGetSnapshot(out VramSnapshot snapshot) || !snapshot.IsRealGpu)
            return false;

        ulong overhead = YuoPoseModelBudgetBytes + SkiaSwapChainBudgetBytes + OsReserveBytes;
        ulong required = overhead + QwenMoEBudgetBytes;
        projectedFreeAfterLoad = snapshot.TotalBytes > required ? snapshot.TotalBytes - required : 0;
        return snapshot.TotalBytes >= required;
    }

    private static VramSnapshot CreateMockSnapshot(string? diagnostic) =>
        new(
            IsAvailable: true,
            TotalBytes: TargetTotalVramBytes,
            FreeBytes: TargetTotalVramBytes,
            UsedBytes: 0,
            Diagnostic: diagnostic ?? "No NVIDIA GPU detected; VRAM telemetry is simulated.",
            IsMocked: true);

    /// <summary>
    /// Resolves nvml.dll from the standard NVSMI folder when it is not on PATH.
    /// Called once before the first P/Invoke so laptops with only integrated GPUs fail fast.
    /// </summary>
    private void EnsureNvmlProbePath()
    {
        if (_nativeLibraryPreloaded || !OperatingSystem.IsWindows())
            return;

        _nativeLibraryPreloaded = true;

        try
        {
            if (!NativeLibrary.TryLoad(NvmlLibrary, out _)
                && File.Exists(NvmlFallbackPath))
            {
                NativeLibrary.TryLoad(NvmlFallbackPath, out _);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or InvalidOperationException)
        {
            // Probing is best-effort; the P/Invoke below will surface the real error.
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
            // Driver disappeared mid-run; nothing left to release.
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
}
