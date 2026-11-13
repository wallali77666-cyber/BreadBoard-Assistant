// ============================================================================
// FILE: src/5_BreadboardAOI.App/Orchestration/HardwareSerialMonitorService.cs
// PURPOSE: Real System.IO.Ports hardware COM port enumeration, live USB-UART
//          serial monitor Rx/Tx streaming, DTR/RTS board reset, and registry
//          friendly-name enrichment for Arduino/ESP32/STM32 boards.
// ============================================================================

using System;
using System.Collections.Generic;
using System.IO.Ports;
using System.Linq;
using BreadboardAOI.Core.BoxCoding;
using Microsoft.Win32;

namespace BreadboardAOI.App.Orchestration;

public sealed class HardwareSerialMonitorService : IDisposable
{
    private SerialPort? _port;
    private readonly object _sync = new();

    public bool IsOpen
    {
        get
        {
            lock (_sync)
                return _port is { IsOpen: true };
        }
    }

    public string? ActivePortName
    {
        get
        {
            lock (_sync)
                return _port is { IsOpen: true } p ? p.PortName : null;
        }
    }

    public int ActiveBaudRate
    {
        get
        {
            lock (_sync)
                return _port?.BaudRate ?? 115200;
        }
    }

    public event Action<string>? LineReceived;
    public event Action<string>? MonitorStatusChanged;

    public static IReadOnlyList<PhysicalComPortEntry> DiscoverAvailablePorts()
    {
        var results = new List<PhysicalComPortEntry>
        {
            new(ProgrammerIdeCatalog.VirtualSimPortDisplay, ProgrammerIdeCatalog.VirtualSimPortDisplay, IsVirtualSimulator: true)
        };

        var seenPorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var friendlyHints = ReadSerialCommRegistryHints();

        try
        {
            foreach (string portName in SerialPort.GetPortNames().OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            {
                string normalized = portName.Trim().ToUpperInvariant();
                if (string.IsNullOrWhiteSpace(normalized) || !seenPorts.Add(normalized))
                    continue;

                string hint = friendlyHints.TryGetValue(normalized, out string? devPath) && !string.IsNullOrWhiteSpace(devPath)
                    ? $"{normalized} (Hardware USB-UART · {devPath})"
                    : $"{normalized} (Hardware USB-UART)";

                results.Add(new PhysicalComPortEntry(normalized, hint, IsVirtualSimulator: false));
            }
        }
        catch
        {
            // Fallback to Registry-discovered ports below
        }

        foreach (var kv in friendlyHints.OrderBy(k => k.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (seenPorts.Add(kv.Key))
            {
                results.Add(new PhysicalComPortEntry(
                    kv.Key,
                    $"{kv.Key} (Hardware USB-UART · {kv.Value})",
                    IsVirtualSimulator: false));
            }
        }

        return results;
    }

    public bool TryOpen(string? portDisplayOrName, int baudRate, out string statusMessage)
    {
        string? rawPort = ProgrammerIdeCatalog.ExtractRawComPort(portDisplayOrName);
        if (string.IsNullOrWhiteSpace(rawPort))
        {
            statusMessage = "Virtual Simulator selected — no physical COM port to open.";
            return false;
        }

        Close();

        lock (_sync)
        {
            try
            {
                var sp = new SerialPort(rawPort, baudRate > 0 ? baudRate : 115200, Parity.None, 8, StopBits.One)
                {
                    ReadTimeout = 500,
                    WriteTimeout = 1000,
                    NewLine = "\n",
                    DtrEnable = false,
                    RtsEnable = false
                };
                sp.DataReceived += OnSerialDataReceived;
                sp.ErrorReceived += OnSerialErrorReceived;
                sp.Open();
                _port = sp;
                statusMessage = $"Hardware Serial Monitor OPEN on {rawPort} @ {sp.BaudRate} baud.";
                MonitorStatusChanged?.Invoke(statusMessage);
                return true;
            }
            catch (Exception ex)
            {
                _port?.Dispose();
                _port = null;
                statusMessage = $"Failed to open {rawPort}: {ex.Message}";
                MonitorStatusChanged?.Invoke(statusMessage);
                return false;
            }
        }
    }

    public void Close()
    {
        SerialPort? existing;
        lock (_sync)
        {
            existing = _port;
            _port = null;
        }

        if (existing is not null)
        {
            try
            {
                existing.DataReceived -= OnSerialDataReceived;
                existing.ErrorReceived -= OnSerialErrorReceived;
                if (existing.IsOpen)
                    existing.Close();
            }
            catch
            {
                // Best-effort serial handle close
            }
            finally
            {
                existing.Dispose();
            }

            MonitorStatusChanged?.Invoke("Hardware Serial Monitor disconnected.");
        }
    }

    public bool TrySendLine(string text, out string errorMessage)
    {
        lock (_sync)
        {
            if (_port is not { IsOpen: true })
            {
                errorMessage = "Hardware serial port is not open.";
                return false;
            }

            try
            {
                _port.WriteLine(text ?? string.Empty);
                errorMessage = string.Empty;
                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }
    }

    public bool PulseDtrReset(out string message)
    {
        lock (_sync)
        {
            if (_port is not { IsOpen: true })
            {
                message = "Open hardware serial port first to pulse DTR reset.";
                return false;
            }

            try
            {
                _port.DtrEnable = false;
                _port.RtsEnable = false;
                System.Threading.Thread.Sleep(60);
                _port.DtrEnable = true;
                _port.RtsEnable = true;
                message = $"DTR/RTS reset pulse sent to {_port.PortName}.";
                return true;
            }
            catch (Exception ex)
            {
                message = $"DTR reset failed: {ex.Message}";
                return false;
            }
        }
    }

    private void OnSerialDataReceived(object sender, SerialDataReceivedEventArgs e)
    {
        if (sender is not SerialPort sp || !sp.IsOpen)
            return;

        try
        {
            string chunk = sp.ReadExisting();
            if (string.IsNullOrEmpty(chunk))
                return;

            string[] lines = chunk.Replace("\r\n", "\n").Split('\n');
            foreach (string raw in lines)
            {
                if (!string.IsNullOrEmpty(raw))
                    LineReceived?.Invoke(raw);
            }
        }
        catch
        {
            // Ignore transient read abort during port close
        }
    }

    private void OnSerialErrorReceived(object sender, SerialErrorReceivedEventArgs e)
    {
        MonitorStatusChanged?.Invoke($"Serial hardware error: {e.EventType}");
    }

    private static Dictionary<string, string> ReadSerialCommRegistryHints()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            using RegistryKey? key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
            if (key is null)
                return map;

            foreach (string valueName in key.GetValueNames())
            {
                string? port = key.GetValue(valueName)?.ToString()?.Trim();
                if (string.IsNullOrWhiteSpace(port))
                    continue;

                string shortDev = valueName;
                int slash = shortDev.LastIndexOf('\\');
                if (slash >= 0 && slash < shortDev.Length - 1)
                    shortDev = shortDev[(slash + 1)..];

                map[port.ToUpperInvariant()] = shortDev;
            }
        }
        catch
        {
            // Registry access may be restricted on non-Windows or locked environments
        }

        return map;
    }

    public void Dispose() => Close();
}
