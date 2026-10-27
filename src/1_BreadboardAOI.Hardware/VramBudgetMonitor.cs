// ============================================================================
// FILE: src/1_BreadboardAOI.Hardware/VramBudgetMonitor.cs
// PURPOSE: Live RTX 3060 12 GB VRAM telemetry via nvml.dll so the orchestrator
//          can refuse to load Qwen3-VL if YOLO + Skia swap-chain already ate the
//          headroom. Degrades gracefully on machines without an NVIDIA driver.
// ============================================================================

using System;
using System.Runtime.InteropServices;

namespace BreadboardAOI.Hardware;

/// <summary>Point-in-time VRAM reading from NVML.</summary>
public readonly record struct VramSnapshot(
    bool IsAvailable,
    ulong TotalBytes,
    ulong FreeBytes,
    ulong UsedBytes,
    string? Diagnostic)
{
    public double UsedGigabytes => IsAvailable ? UsedBytes / 1024d / 1024d / 1024d : 0d;

    public double TotalGigabytes => IsAvailable ? TotalBytes / 1024d / 1024d / 1024d : 0d;
}

/// <summary>NVML wrapper with the fixed VRAM budget of the target RTX 3060 workstation.</summary>
public sealed class VramBudgetMonitor : IDisposable
{
    private const string NvmlLibrary = "nvml.dll";

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
    private string? _lastError;

    /// <summary>True once <c>nvmlInit_v2</c> + GPU handle acquisition succeeded.</summary>
    public bool IsAvailable => _initialized && _device != IntPtr.Zero;

    /// <summary>Human-readable GPU name reported by NVML, when available.</summary>
    public string DeviceName { get; private set; } = "NVIDIA GPU (undetected)";

    /// <summary>Last NVML/driver error, or null when everything is healthy.</summary>
    public string? LastError => _lastError;

    /// <summary>Attempts to initialize NVML. Safe to call repeatedly; failures are cached.</summary>
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
            _lastError = "nvml.dll was not found. NVIDIA driver or NVSMI path missing.";
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
    }

    /// <summary>Reads current VRAM usage. Never throws; returns IsAvailable=false on failure.</summary>
    public bool TryGetSnapshot(out VramSnapshot snapshot)
    {
        if (!TryInitialize())
        {
            snapshot = new VramSnapshot(false, 0, 0, 0, _lastError);
            return false;
        }

        try
        {
            int status = NvmlDeviceGetMemoryInfo(_device, out NvmlMemory memory);
            if (status != 0)
            {
                _lastError = $"nvmlDeviceGetMemoryInfo failed with status {status}.";
                snapshot = new VramSnapshot(false, 0, 0, 0, _lastError);
                return false;
            }

            snapshot = new VramSnapshot(true, memory.Total, memory.Free, memory.Used, null);
            _lastError = null;
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
        {
            _lastError = ex.Message;
            snapshot = new VramSnapshot(false, 0, 0, 0, _lastError);
            return false;
        }
    }

    /// <summary>
    /// True when the Qwen3-VL budget would still fit after the YOLO, Skia and OS
    /// reservations. Used before spawning llama-server.exe.
    /// </summary>
    public bool CanLoadQwen(out ulong projectedFreeAfterLoad)
    {
        projectedFreeAfterLoad = 0;
        if (!TryGetSnapshot(out VramSnapshot snapshot) || !snapshot.IsAvailable)
            return false;

        ulong overhead = YuoPoseModelBudgetBytes + SkiaSwapChainBudgetBytes + OsReserveBytes;
        ulong required = overhead + QwenMoEBudgetBytes;
        projectedFreeAfterLoad = snapshot.TotalBytes > required ? snapshot.TotalBytes - required : 0;
        return snapshot.TotalBytes >= required;
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
