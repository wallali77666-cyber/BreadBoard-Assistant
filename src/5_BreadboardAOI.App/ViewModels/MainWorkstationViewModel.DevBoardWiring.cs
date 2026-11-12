// ============================================================================
// FILE: src/5_BreadboardAOI.App/ViewModels/MainWorkstationViewModel.DevBoardWiring.cs
// PURPOSE: Partial class for DevBoard pin selection, interactive jumper wiring,
//          power rail hookups, and MCU DRC rule evaluation.
// ============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Globalization;
using BreadboardAOI.Core.Geometry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.App.Rendering;

namespace BreadboardAOI.App.ViewModels;

public sealed partial class MainWorkstationViewModel
{
    [RelayCommand]
    public void SelectDevBoardType(string? boardName)
    {
        if (string.IsNullOrWhiteSpace(boardName))
            return;

        var newType = boardName.Trim().ToLowerInvariant() switch
        {
            "none" or "remove" or "empty" => MicrocontrollerBoardType.None,
            "mega" or "mega2560" => MicrocontrollerBoardType.ArduinoMega2560,
            "esp32" or "devkit" or "esp32devkit" => MicrocontrollerBoardType.Esp32DevKitV1,
            "s2" or "s2mini" or "wemos" => MicrocontrollerBoardType.WemosEsp32S2Mini,
            "stm32" or "bluepill" or "cortex" => MicrocontrollerBoardType.Stm32BluePillF103C8,
            _ => MicrocontrollerBoardType.ArduinoUnoR3R4
        };

        if (SelectedDevBoardType != newType)
        {
            RecordUndoSnapshot($"Switch Dev Board to {boardName}");
            SelectedDevBoardType = newType;
            UpdateDevBoardState();
        }
    }

    [RelayCommand]
    public void RemoveDevBoard()
    {
        RecordUndoSnapshot("Remove Dev Board");
        SelectedDevBoardType = MicrocontrollerBoardType.None;
        IsDevBoardDocked = false;
        ClearDevBoardWires();
        UpdateDevBoardState();
    }

    [RelayCommand]
    public void ToggleDevBoardDocked()
    {
        RecordUndoSnapshot(IsDevBoardDocked ? "Undock Dev Board" : "Dock Dev Board");
        IsDevBoardDocked = !IsDevBoardDocked;
        UpdateDevBoardState();
    }

    [RelayCommand]
    public void SetDevBoardPlacement(string? placement)
    {
        SelectedDevBoardPlacement = string.Equals(placement, "right", StringComparison.OrdinalIgnoreCase)
            ? OffBoardDockPlacement.RightMargin
            : OffBoardDockPlacement.BottomMargin;

        UpdateDevBoardState();
    }

    public ObservableCollection<OffBoardJumperWire> ActiveDevBoardWires { get; } = new();

    [ObservableProperty]
    private string _selectedMcuPinToWire = string.Empty;

    [ObservableProperty]
    private string _selectedBreadboardHoleToWire = "TOP_VCC_1";

    [ObservableProperty]
    private string _selectedDevBoardWireColor = "AUTO (By Role)";

    public IReadOnlyList<string> DevBoardWireColors { get; } = new[]
    {
        "AUTO (By Role)", "RED", "BLACK", "BLUE", "GREEN", "YELLOW", "ORANGE", "PURPLE", "CYAN", "WHITE"
    };

    public static string WireColorToHex(string colorName) => (colorName ?? "AUTO").ToUpperInvariant() switch
    {
        "RED" => "#E53935",
        "BLACK" => "#1E1E24",
        "BLUE" => "#1E88E5",
        "GREEN" => "#43A047",
        "YELLOW" => "#FBC02D",
        "ORANGE" => "#FB8C00",
        "PURPLE" => "#8E24AA",
        "CYAN" => "#00BCD4",
        "WHITE" => "#ECEFF1",
        _ => "#1E88E5"
    };

    public static string ResolveWireColor(string chosenColor, McuPinDefinition? pin, string? holeId)
    {
        if (string.Equals(chosenColor, "AUTO (By Role)", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(chosenColor, "AUTO", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrWhiteSpace(chosenColor))
        {
            if (pin is not null)
            {
                string id = pin.CanonicalId.ToUpperInvariant();
                if (id.Contains("GND")) return "#1E1E24";
                if (id.Contains("5V") || id.Contains("3V3") || id.Contains("3.3V") || id.Contains("VIN") || id.Contains("VCC") || id.Contains("VBUS") || id.Contains("IOREF")) return "#E53935";
                if (id.StartsWith("A") && id.Length <= 3) return "#FBC02D"; // Analog yellow
                if (id.Contains("SDA") || id.Contains("SCL")) return "#00BCD4"; // I2C cyan
                if (id.Contains("MOSI") || id.Contains("MISO") || id.Contains("SCK")) return "#43A047"; // SPI green
                if (id.Contains("TX") || id.Contains("RX")) return "#8E24AA"; // UART purple
                if (id.StartsWith("D") && int.TryParse(id.AsSpan(1), out int pinNum))
                {
                    return (pinNum % 6) switch
                    {
                        0 => "#1E88E5", // Blue
                        1 => "#43A047", // Green
                        2 => "#FB8C00", // Orange
                        3 => "#8E24AA", // Purple
                        4 => "#00BCD4", // Cyan
                        _ => "#E91E63"  // Magenta
                    };
                }
            }

            if (!string.IsNullOrEmpty(holeId))
            {
                string hid = holeId.ToUpperInvariant();
                if (hid.Contains("VCC")) return "#E53935";
                if (hid.Contains("GND")) return "#1E1E24";
            }

            return "#1E88E5";
        }

        return WireColorToHex(chosenColor);
    }

    [RelayCommand]
    public void WirePowerRails()
    {
        RecordUndoSnapshot($"Wire {SelectedDevBoardType} power rails");
        MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(SelectedDevBoardType);
        string vccPin = profile.BoardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 => "5V",
            MicrocontrollerBoardType.ArduinoMega2560 => "5V",
            MicrocontrollerBoardType.Esp32DevKitV1 => "3V3",
            MicrocontrollerBoardType.WemosEsp32S2Mini => "3V3",
            MicrocontrollerBoardType.Stm32BluePillF103C8 => "3V3",
            _ => "5V"
        };
        string gndPin = profile.BoardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 => "GND_1",
            MicrocontrollerBoardType.ArduinoMega2560 => "GND_1",
            MicrocontrollerBoardType.Esp32DevKitV1 => "GND",
            MicrocontrollerBoardType.WemosEsp32S2Mini => "GND",
            MicrocontrollerBoardType.Stm32BluePillF103C8 => "GND",
            _ => "GND"
        };

        // Replace every previous power-rail wire, including stale wires left
        // behind after switching to a board with different rail pin ids.
        var existing = ActiveDevBoardWires.Where(w =>
            string.Equals(w.NetName, "NET_VCC", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(w.NetName, "NET_GND", StringComparison.OrdinalIgnoreCase) ||
            IsPowerRailEndpoint(w.TargetEndpointKey) ||
            IsPowerRailEndpoint(w.SourceEndpointKey)).ToList();
        foreach (var w in existing)
            ActiveDevBoardWires.Remove(w);

        ActiveDevBoardWires.Add(new OffBoardJumperWire(
            WireId: $"W_VCC_{Guid.NewGuid():N}"[..10],
            NetName: "NET_VCC",
            SourceEndpointKey: $"MCU_1:{vccPin}",
            TargetEndpointKey: "BB:TOP_VCC_1",
            InsulationHexColor: "#E53935"));

        ActiveDevBoardWires.Add(new OffBoardJumperWire(
            WireId: $"W_GND_{Guid.NewGuid():N}"[..10],
            NetName: "NET_GND",
            SourceEndpointKey: $"MCU_1:{gndPin}",
            TargetEndpointKey: "BB:TOP_GND_1",
            InsulationHexColor: "#212121"));

        EvaluateDevBoardCircuit();
        StatusLine = $"DEV BOARD: Wired {profile.DisplayName} power rails ({vccPin} -> TOP_VCC_1, {gndPin} -> TOP_GND_1).";
    }

    private static bool IsPowerRailEndpoint(string key) =>
        key.EndsWith("TOP_VCC_1", StringComparison.OrdinalIgnoreCase) ||
        key.EndsWith("TOP_GND_1", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    public void WireSensor()
    {
        MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(SelectedDevBoardType);

        // Prefer a three-pin module so power, ground and signal each get their
        // own lead. Fall back to any sensor, then any placed part.
        DraftPartRow? targetPart = DraftParts.FirstOrDefault(p => p.Part.PinCount >= 3 && IsSensorLike(p.Display))
            ?? DraftParts.FirstOrDefault(p => IsSensorLike(p.Display))
            ?? DraftParts.FirstOrDefault(p => p.Part.PinCount >= 3)
            ?? DraftParts.FirstOrDefault();

        if (targetPart is null)
        {
            StatusLine = "DEV BOARD: Place a sensor or LED on the breadboard first, then click WIRE SENSOR.";
            return;
        }

        RecordUndoSnapshot($"Wire {profile.DisplayName} to {targetPart.Display}");

        // Repeated clicks should replace the previous auto-wiring, not stack
        // another set of parallel jumpers on top of it.
        var staleSensorWires = ActiveDevBoardWires
            .Where(w => w.NetName.StartsWith("NET_SENSOR_", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var wire in staleSensorWires)
            ActiveDevBoardWires.Remove(wire);

        string vccPin = profile.BoardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560 ? "5V" : "3V3";
        string gndPin = profile.BoardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560 ? "GND_1" : "GND";
        string signalPin = profile.BoardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 => "D2",
            MicrocontrollerBoardType.ArduinoMega2560 => "D2",
            MicrocontrollerBoardType.Esp32DevKitV1 => "D4",
            MicrocontrollerBoardType.WemosEsp32S2Mini => "IO4",
            MicrocontrollerBoardType.Stm32BluePillF103C8 => "A1",
            _ => "D2"
        };
        string analogPin = profile.BoardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 => "A0",
            MicrocontrollerBoardType.ArduinoMega2560 => "A0",
            MicrocontrollerBoardType.Esp32DevKitV1 => "D34",
            MicrocontrollerBoardType.WemosEsp32S2Mini => "IO1",
            MicrocontrollerBoardType.Stm32BluePillF103C8 => "A0",
            _ => "A0"
        };

        string lead1 = targetPart.Part.Pin1HoleId;
        string lead2 = targetPart.Part.PinCount > 1 ? targetPart.Part.Pins[1] : lead1;
        string signalLead = targetPart.Part.PinCount > 2 ? targetPart.Part.Pins[2] : string.Empty;

        ActiveDevBoardWires.Add(new OffBoardJumperWire(
            WireId: $"W_SNS_V_{Guid.NewGuid():N}"[..10],
            NetName: "NET_SENSOR_PWR",
            SourceEndpointKey: $"MCU_1:{vccPin}",
            TargetEndpointKey: $"BB:{lead1}",
            InsulationHexColor: "#E53935"));

        ActiveDevBoardWires.Add(new OffBoardJumperWire(
            WireId: $"W_SNS_G_{Guid.NewGuid():N}"[..10],
            NetName: "NET_SENSOR_GND",
            SourceEndpointKey: $"MCU_1:{gndPin}",
            TargetEndpointKey: $"BB:{lead2}",
            InsulationHexColor: "#212121"));

        string sigChoice = targetPart.Display.Contains("POT", StringComparison.OrdinalIgnoreCase) ||
                           targetPart.Display.Contains("LDR", StringComparison.OrdinalIgnoreCase) ||
                           targetPart.Display.Contains("TMP36", StringComparison.OrdinalIgnoreCase)
                           ? analogPin : signalPin;

        if (!string.IsNullOrEmpty(signalLead))
        {
            ActiveDevBoardWires.Add(new OffBoardJumperWire(
                WireId: $"W_SNS_S_{Guid.NewGuid():N}"[..10],
                NetName: "NET_SENSOR_SIG",
                SourceEndpointKey: $"MCU_1:{sigChoice}",
                TargetEndpointKey: $"BB:{signalLead}",
                InsulationHexColor: "#1E88E5"));
        }

        EvaluateDevBoardCircuit();
        StatusLine = string.IsNullOrEmpty(signalLead)
            ? $"DEV BOARD: Wired {targetPart.Display} power only (2-pin part). Add a signal or series wire manually."
            : $"DEV BOARD: Connected {profile.DisplayName} to {targetPart.Display} (PWR:{vccPin}, GND:{gndPin}, SIG:{sigChoice} -> {signalLead}).";
    }

    private static bool IsSensorLike(string display) =>
        display.Contains("SENSOR", StringComparison.OrdinalIgnoreCase) ||
        display.Contains("POT", StringComparison.OrdinalIgnoreCase) ||
        display.Contains("LED", StringComparison.OrdinalIgnoreCase) ||
        display.Contains("DHT", StringComparison.OrdinalIgnoreCase) ||
        display.Contains("HC-SR04", StringComparison.OrdinalIgnoreCase) ||
        display.Contains("LDR", StringComparison.OrdinalIgnoreCase) ||
        display.Contains("TMP36", StringComparison.OrdinalIgnoreCase);

    [RelayCommand]
    public void AddDevBoardJumperWire()
    {
        if (string.IsNullOrWhiteSpace(SelectedMcuPinToWire) || string.IsNullOrWhiteSpace(SelectedBreadboardHoleToWire))
        {
            StatusLine = "DEV BOARD: Select an MCU pin and enter a breadboard hole (e.g. E15 or TOP_VCC_1).";
            return;
        }

        string pinInput = SelectedMcuPinToWire.Trim();
        McuPinDefinition? pinDefinition = SelectedDevBoardPins.FirstOrDefault(p =>
            string.Equals(p.CanonicalId, pinInput, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(p.DisplayLabel, pinInput, StringComparison.OrdinalIgnoreCase));

        if (pinDefinition is null)
        {
            StatusLine = $"DEV BOARD: '{pinInput}' is not a pin on {_renderer.McuRuleEngine.GetBoardProfile(SelectedDevBoardType).DisplayName}.";
            return;
        }

        string holeInput = SelectedBreadboardHoleToWire.Trim();
        if (holeInput.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            holeInput = holeInput.Substring(3);

        if (!PinMatrixLookup.Shared.TryGetHole(holeInput, out HoleCoord hole))
        {
            StatusLine = $"DEV BOARD: '{holeInput}' is not a valid breadboard tie point.";
            return;
        }

        string pin = pinDefinition.CanonicalId;
        string holeId = hole.HoleId;
        string color = ResolveWireColor(SelectedDevBoardWireColor, pinDefinition, holeId);
        string net = pin.Contains("GND", StringComparison.OrdinalIgnoreCase) ? "NET_GND" :
                     pin.Contains("5V", StringComparison.OrdinalIgnoreCase) || pin.Contains("3V3", StringComparison.OrdinalIgnoreCase) || pin.Contains("VCC", StringComparison.OrdinalIgnoreCase) ? "NET_VCC" :
                     $"NET_{pin}";

        RecordUndoSnapshot($"Wire {pinDefinition.DisplayLabel} to {holeId}");

        // Clicking ADD twice with the same endpoints replaces the wire instead
        // of drawing two jumpers on top of each other.
        var duplicate = ActiveDevBoardWires.FirstOrDefault(w =>
            string.Equals(w.SourceEndpointKey, $"MCU_1:{pin}", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(w.TargetEndpointKey, $"BB:{holeId}", StringComparison.OrdinalIgnoreCase));
        if (duplicate is not null)
            ActiveDevBoardWires.Remove(duplicate);

        var wire = new OffBoardJumperWire(
            WireId: $"WIRE_{Guid.NewGuid():N}"[..12],
            NetName: net,
            SourceEndpointKey: $"MCU_1:{pin}",
            TargetEndpointKey: $"BB:{holeId}",
            InsulationHexColor: color);

        ActiveDevBoardWires.Add(wire);
        EvaluateDevBoardCircuit();
        StatusLine = $"DEV BOARD: Added wire from {pinDefinition.DisplayLabel} to {holeId}.";
    }

    [RelayCommand]
    public void RemoveDevBoardJumperWire(OffBoardJumperWire? wire)
    {
        if (wire is not null)
        {
            RecordUndoSnapshot($"Remove dev board wire {wire.NetName}");
            if (ActiveDevBoardWires.Remove(wire))
            {
                EvaluateDevBoardCircuit();
                StatusLine = "DEV BOARD: Removed jumper wire.";
            }
        }
    }

    [RelayCommand]
    public void ClearDevBoardWires()
    {
        if (ActiveDevBoardWires.Count > 0)
        {
            RecordUndoSnapshot("Clear dev board wires");
            ActiveDevBoardWires.Clear();
            EvaluateDevBoardCircuit();
            StatusLine = "DEV BOARD: Cleared all dev board jumper wires.";
        }
    }

    public void EvaluateDevBoardCircuit()
    {
        _renderer.ActiveDevBoardWires = ActiveDevBoardWires.ToList();

        IReadOnlyList<McuDrcViolation> violations = Array.Empty<McuDrcViolation>();
        if (ActiveDevBoardWires.Count > 0)
        {
            var dsu = new DisjointSet();
            MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(SelectedDevBoardType);

            // 1. Union breadboard jumper wires (plain wires between tie points)
            foreach (var part in _draft.Parts)
            {
                if (part.Kind == ComponentKind.JumperWire && part.Pins.Length >= 2)
                {
                    string s1 = GetHoleStripKey(part.Pins[0]);
                    string s2 = GetHoleStripKey(part.Pins[1]);
                    dsu.Union(s1, s2);
                }
            }

            // 2. Union each active dev board jumper wire
            foreach (var wire in ActiveDevBoardWires)
            {
                string k1 = NormalizeWireEndpointKey(wire.SourceEndpointKey);
                string k2 = NormalizeWireEndpointKey(wire.TargetEndpointKey);
                dsu.Union(k1, k2);
            }

            // 3. Collect endpoints and breadboard tie points into their root components
            var rootMcuPins = new Dictionary<string, List<McuPinEndpoint>>(StringComparer.OrdinalIgnoreCase);
            var rootHoles = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);

            foreach (var wire in ActiveDevBoardWires)
            {
                RegisterWireEndpoint(wire.SourceEndpointKey, dsu, rootMcuPins, rootHoles);
                RegisterWireEndpoint(wire.TargetEndpointKey, dsu, rootMcuPins, rootHoles);
            }

            // 3b. Register roots for all placed breadboard components (e.g. Batteries, power sources)
            // so isolated power supplies and peripheral networks are evaluated by the rule engine even before jumpered
            foreach (var part in DraftParts)
            {
                foreach (string pin in part.Part.Pins)
                {
                    string stripKey = GetHoleStripKey(pin);
                    string pinRoot = dsu.Find(stripKey);
                    if (!rootHoles.TryGetValue(pinRoot, out var hSet))
                    {
                        hSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                        rootHoles[pinRoot] = hSet;
                    }
                    hSet.Add(pin);
                }
            }

            // 4. Build OffBoardElectricalNet for each connected root
            var nets = new List<OffBoardElectricalNet>();
            var allRoots = rootMcuPins.Keys.Concat(rootHoles.Keys).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

            foreach (string root in allRoots)
            {
                var mcuEndpoints = rootMcuPins.TryGetValue(root, out var mPins) ? mPins : new List<McuPinEndpoint>();
                var holes = rootHoles.TryGetValue(root, out var hSet) ? hSet.ToList() : new List<string>();

                var bbComponents = new List<BreadboardNetComponent>();
                foreach (var part in DraftParts)
                {
                    string? matchedHole = part.Part.Pins.FirstOrDefault(pin =>
                    {
                        string stripKey = GetHoleStripKey(pin);
                        return string.Equals(dsu.Find(stripKey), root, StringComparison.OrdinalIgnoreCase) ||
                               holes.Any(h => string.Equals(h, pin, StringComparison.OrdinalIgnoreCase));
                    });

                    if (matchedHole is null)
                        continue;

                    ExternalLoadType loadType = ExternalLoadType.None;
                    double resistanceOhms = 0.0;
                    double currentDrawMa = 5.0;
                    double supplyVoltage = 0.0;
                    bool isPosTerminal = false;
                    bool isNegTerminal = false;

                    switch (part.Part.Kind)
                    {
                        case ComponentKind.Battery:
                            loadType = ExternalLoadType.ExternalPowerSupply;
                            double batteryV = 9.0;
                            if (!string.IsNullOrWhiteSpace(part.Part.Value))
                            {
                                string valClean = part.Part.Value.Trim().TrimEnd('V', 'v');
                                if (double.TryParse(valClean, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsedBattV))
                                    batteryV = parsedBattV;
                            }
                            bool isLead1 = string.Equals(matchedHole, part.Part.Pin1HoleId, StringComparison.OrdinalIgnoreCase)
                                || (part.Part.Pins.Length > 0 && string.Equals(matchedHole, part.Part.Pins[0], StringComparison.OrdinalIgnoreCase));
                            isPosTerminal = isLead1;
                            isNegTerminal = !isLead1;
                            supplyVoltage = isPosTerminal ? batteryV : 0.0;
                            currentDrawMa = 0.0;
                            break;

                        case ComponentKind.Led:
                            loadType = ExternalLoadType.StandardLed;
                            currentDrawMa = 20.0;
                            break;

                        case ComponentKind.Resistor:
                            loadType = ExternalLoadType.SeriesResistor;
                            if (BreadboardAOI.Core.Geometry.ResistorColorCode.TryParseOhms(part.Part.Value, out double parsedOhms))
                                resistanceOhms = parsedOhms;
                            else
                                resistanceOhms = 220.0;
                            break;

                        case ComponentKind.Buzzer:
                            loadType = ExternalLoadType.DcMotorOrRelay;
                            currentDrawMa = 35.0;
                            break;

                        case ComponentKind.Sensor:
                            string sensorName = part.Part.Value?.ToUpperInvariant() ?? string.Empty;
                            if (sensorName.Contains("SERVO", StringComparison.Ordinal) || sensorName.Contains("MOTOR", StringComparison.Ordinal))
                            {
                                loadType = ExternalLoadType.HobbyServoMotor;
                                currentDrawMa = 250.0;
                            }
                            else
                            {
                                loadType = profile.OperatingLogicVoltage <= 3.3 ? ExternalLoadType.LowPowerSensor3V3 : ExternalLoadType.None;
                                currentDrawMa = 10.0;
                            }
                            break;

                        case ComponentKind.Potentiometer:
                            loadType = ExternalLoadType.SeriesResistor;
                            resistanceOhms = 10000.0;
                            break;
                    }

                    bbComponents.Add(new BreadboardNetComponent(
                        part.Display,
                        loadType,
                        CurrentDrawMa: currentDrawMa,
                        ResistanceOhms: resistanceOhms,
                        ConnectedBreadboardTiePoint: matchedHole,
                        SupplyVoltage: supplyVoltage,
                        IsPositiveTerminal: isPosTerminal,
                        IsNegativeTerminal: isNegTerminal));
                }

                // Determine net name
                string netName = ResolveConsolidatedNetName(root, mcuEndpoints, holes, bbComponents);

                nets.Add(new OffBoardElectricalNet(
                    NetName: netName,
                    ConnectedMcuPins: mcuEndpoints,
                    BreadboardComponents: bbComponents,
                    BreadboardTiePoints: holes));
            }

            // Synchronize active wire NetNames to consolidated net names
            var endpointToNetName = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var net in nets)
            {
                foreach (var p in net.ConnectedMcuPins)
                {
                    endpointToNetName[$"{p.BoardInstanceId}:{p.PinId}"] = net.NetName;
                    endpointToNetName[p.PinId] = net.NetName;
                }
                foreach (var h in net.BreadboardTiePoints)
                {
                    endpointToNetName[h] = net.NetName;
                    endpointToNetName[$"BB:{h}"] = net.NetName;
                }
            }

            for (int i = 0; i < ActiveDevBoardWires.Count; i++)
            {
                var wire = ActiveDevBoardWires[i];
                string? resolvedNet = null;
                if (endpointToNetName.TryGetValue(wire.SourceEndpointKey, out string? sNet))
                    resolvedNet = sNet;
                else if (endpointToNetName.TryGetValue(wire.TargetEndpointKey, out string? tNet))
                    resolvedNet = tNet;

                if (resolvedNet != null && !string.Equals(wire.NetName, resolvedNet, StringComparison.OrdinalIgnoreCase))
                {
                    ActiveDevBoardWires[i] = wire with { NetName = resolvedNet };
                }
            }

            violations = _renderer.McuRuleEngine.EvaluateCircuit(nets);
        }

        _renderer.ActiveDevBoardViolations = violations;
        SyncDevBoardVisualState(violations);
        RefreshDevBoardViolationRows(violations);

        if (violations.Count > 0)
            StatusLine = $"DRC ALERT ({violations.Count}): {violations[0].Title}";
    }

    private static string DetermineDefaultNetName(McuPinDefinition pin1, McuPinDefinition? pin2)
    {
        string p1 = pin1.CanonicalId;
        string? p2 = pin2?.CanonicalId;

        if (p1.Contains("GND", StringComparison.OrdinalIgnoreCase) || (p2 != null && p2.Contains("GND", StringComparison.OrdinalIgnoreCase)))
            return "NET_GND";

        if (p1.Contains("5V", StringComparison.OrdinalIgnoreCase) || p1.Contains("3V3", StringComparison.OrdinalIgnoreCase) || p1.Contains("VCC", StringComparison.OrdinalIgnoreCase) ||
            (p2 != null && (p2.Contains("5V", StringComparison.OrdinalIgnoreCase) || p2.Contains("3V3", StringComparison.OrdinalIgnoreCase) || p2.Contains("VCC", StringComparison.OrdinalIgnoreCase))))
            return "NET_VCC";

        return $"NET_{p1}";
    }

    private static string GetHoleStripKey(string holeId)
    {
        string trimmed = holeId.Trim();
        if (trimmed.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(3);

        if (PinMatrixLookup.Shared.TryGetHole(trimmed, out HoleCoord hc))
        {
            if (hc.StripId == BreadboardSpec.StripTopVcc)
                return "RAIL_TOP_VCC";
            if (hc.StripId == BreadboardSpec.StripBotVcc)
                return "RAIL_BOT_VCC";
            if (hc.StripId == BreadboardSpec.StripTopGnd)
                return "RAIL_TOP_GND";
            if (hc.StripId == BreadboardSpec.StripBotGnd)
                return "RAIL_BOT_GND";
            return $"STRIP_{hc.StripId}";
        }
        return $"HOLE_{trimmed.ToUpperInvariant()}";
    }

    private string NormalizeWireEndpointKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
            return string.Empty;

        string trimmed = key.Trim();
        if (trimmed.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            return GetHoleStripKey(trimmed.Substring(3));
        if (PinMatrixLookup.Shared.TryGetHole(trimmed, out _))
            return GetHoleStripKey(trimmed);

        string instance = "MCU_1";
        string pin = trimmed;
        int colon = trimmed.LastIndexOf(':');
        if (colon >= 0)
        {
            instance = trimmed.Substring(0, colon);
            pin = trimmed.Substring(colon + 1);
        }

        var boardState = _renderer.ActiveMicrocontrollers.FirstOrDefault(m => string.Equals(m.BoardInstanceId, instance, StringComparison.OrdinalIgnoreCase));
        MicrocontrollerBoardType bType = boardState?.BoardType ?? SelectedDevBoardType;
        MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(bType);
        McuPinDefinition? pinDef = profile.FindPin(pin);
        string canonicalPin = pinDef?.CanonicalId ?? pin.ToUpperInvariant();

        return $"{instance.ToUpperInvariant()}:{canonicalPin}";
    }

    private void RegisterWireEndpoint(
        string key,
        DisjointSet dsu,
        Dictionary<string, List<McuPinEndpoint>> rootMcuPins,
        Dictionary<string, HashSet<string>> rootHoles)
    {
        string norm = NormalizeWireEndpointKey(key);
        string root = dsu.Find(norm);

        string trimmed = key.Trim();
        if (trimmed.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
        {
            string hole = trimmed.Substring(3);
            if (!rootHoles.TryGetValue(root, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rootHoles[root] = set;
            }
            set.Add(hole);
        }
        else if (PinMatrixLookup.Shared.TryGetHole(trimmed, out _))
        {
            if (!rootHoles.TryGetValue(root, out var set))
            {
                set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                rootHoles[root] = set;
            }
            set.Add(trimmed);
        }
        else
        {
            string instance = "MCU_1";
            string pin = trimmed;
            int colon = trimmed.LastIndexOf(':');
            if (colon >= 0)
            {
                instance = trimmed.Substring(0, colon);
                pin = trimmed.Substring(colon + 1);
            }

            var boardState = _renderer.ActiveMicrocontrollers.FirstOrDefault(m => string.Equals(m.BoardInstanceId, instance, StringComparison.OrdinalIgnoreCase));
            MicrocontrollerBoardType bType = boardState?.BoardType ?? SelectedDevBoardType;
            MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(bType);
            McuPinDefinition? pinDef = profile.FindPin(pin);
            string canonicalPin = pinDef?.CanonicalId ?? pin;

            if (!rootMcuPins.TryGetValue(root, out var list))
            {
                list = new List<McuPinEndpoint>();
                rootMcuPins[root] = list;
            }

            if (!list.Any(p => string.Equals(p.BoardInstanceId, instance, StringComparison.OrdinalIgnoreCase) && string.Equals(p.PinId, canonicalPin, StringComparison.OrdinalIgnoreCase)))
            {
                list.Add(new McuPinEndpoint(instance, bType, canonicalPin));
            }
        }
    }

    public static bool MatchesMcuEndpoint(string key, string instanceId, McuPinDefinition pin)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string trimmed = key.Trim();
        string canonical = pin.CanonicalId;
        string mcuKey = $"{instanceId}:{canonical}";

        if (string.Equals(trimmed, mcuKey, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, canonical, StringComparison.OrdinalIgnoreCase))
            return true;

        int colon = trimmed.LastIndexOf(':');
        string pinPart = colon >= 0 ? trimmed.Substring(colon + 1) : trimmed;

        if (string.Equals(pinPart, canonical, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(pinPart, pin.DisplayLabel, StringComparison.OrdinalIgnoreCase))
            return true;

        foreach (var alias in pin.Aliases)
        {
            if (string.Equals(pinPart, alias, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static bool MatchesBreadboardEndpoint(string key, string holeId)
    {
        if (string.IsNullOrWhiteSpace(key))
            return false;

        string trimmed = key.Trim();
        if (string.Equals(trimmed, holeId, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(trimmed, $"BB:{holeId}", StringComparison.OrdinalIgnoreCase))
            return true;

        if (trimmed.StartsWith("BB:", StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed.Substring(3);

        return string.Equals(trimmed, holeId, StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveConsolidatedNetName(
        string root,
        List<McuPinEndpoint> mcuEndpoints,
        List<string> holes,
        List<BreadboardNetComponent> bbComponents)
    {
        var posSupply = bbComponents.FirstOrDefault(c => c.IsPositiveTerminal && c.SupplyVoltage > 0.0);
        if (posSupply is not null)
            return $"NET_SUPPLY_{posSupply.SupplyVoltage:0.#}V";

        if (bbComponents.Any(c => c.IsNegativeTerminal))
            return "NET_GND";

        if (root.Contains("GND", StringComparison.OrdinalIgnoreCase) ||
            holes.Any(h => h.Contains("GND", StringComparison.OrdinalIgnoreCase)) ||
            mcuEndpoints.Any(p => p.PinId.Contains("GND", StringComparison.OrdinalIgnoreCase)))
            return "NET_GND";

        if (root.Contains("VCC", StringComparison.OrdinalIgnoreCase) ||
            holes.Any(h => h.Contains("VCC", StringComparison.OrdinalIgnoreCase)) ||
            mcuEndpoints.Any(p => p.PinId.Contains("5V", StringComparison.OrdinalIgnoreCase) || p.PinId.Contains("3V3", StringComparison.OrdinalIgnoreCase) || p.PinId.Contains("VCC", StringComparison.OrdinalIgnoreCase)))
            return "NET_VCC";

        if (mcuEndpoints.Count > 0)
            return $"NET_{mcuEndpoints[0].PinId}";

        if (holes.Count > 0)
            return $"NET_{holes[0]}";

        return $"NET_{root}";
    }

    private sealed class DisjointSet
    {
        private readonly Dictionary<string, string> _parent = new(StringComparer.OrdinalIgnoreCase);

        public string Find(string x)
        {
            if (!_parent.TryGetValue(x, out string? p))
            {
                _parent[x] = x;
                return x;
            }
            if (!string.Equals(p, x, StringComparison.OrdinalIgnoreCase))
            {
                _parent[x] = Find(p);
            }
            return _parent[x];
        }

        public void Union(string a, string b)
        {
            string rootA = Find(a);
            string rootB = Find(b);
            if (!string.Equals(rootA, rootB, StringComparison.OrdinalIgnoreCase))
            {
                _parent[rootA] = rootB;
            }
        }
    }

    /// <summary>
    /// Pushes connected and violating pin ids into the renderer so the sockets
    /// light up as soon as a jumper is added or a rule fires.
    /// </summary>
    private void SyncDevBoardVisualState(IReadOnlyList<McuDrcViolation> violations)
    {
        if (!IsDevBoardDocked)
        {
            _renderer.ActiveMicrocontrollers = Array.Empty<OffBoardMicrocontrollerVisualState>();
            return;
        }

        var connected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (OffBoardJumperWire wire in ActiveDevBoardWires)
        {
            CollectMcuPin(wire.SourceEndpointKey, connected);
            CollectMcuPin(wire.TargetEndpointKey, connected);
        }

        var violating = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (McuDrcViolation violation in violations)
        {
            foreach (McuPinEndpoint endpoint in violation.OffendingPins)
            {
                if (string.Equals(endpoint.BoardInstanceId, "MCU_1", StringComparison.OrdinalIgnoreCase))
                    violating.Add(endpoint.PinId);
            }
        }

        _renderer.ActiveMicrocontrollers = new[]
        {
            new OffBoardMicrocontrollerVisualState(
                "MCU_1",
                SelectedDevBoardType,
                SelectedDevBoardPlacement,
                0,
                null,
                connected,
                violating)
        };
    }

    private static void CollectMcuPin(string key, HashSet<string> pins)
    {
        if (string.IsNullOrWhiteSpace(key))
            return;

        string trimmed = key.Trim();
        if (!trimmed.StartsWith("MCU", StringComparison.OrdinalIgnoreCase))
            return;

        int colon = trimmed.LastIndexOf(':');
        if (colon >= 0 && colon < trimmed.Length - 1)
            pins.Add(trimmed.Substring(colon + 1));
    }

    /// <summary>
    /// Replaces only the dev-board rows in the DRC list, leaving camera-derived
    /// rows intact and keeping the counters accurate across re-evaluations.
    /// </summary>
    private void RefreshDevBoardViolationRows(IReadOnlyList<McuDrcViolation> violations)
    {
        for (int i = Violations.Count - 1; i >= 0; i--)
        {
            if (Violations[i].IsDevBoard)
                Violations.RemoveAt(i);
        }

        for (int i = 0; i < violations.Count; i++)
            Violations.Insert(0, new ViolationRow(violations[i]));

        CriticalCount = Violations.Count(v => v.IsCritical);
        WarningCount = Violations.Count - CriticalCount;
        ViolationSummary = string.Create(
            CultureInfo.InvariantCulture,
            $"{CriticalCount} CRITICAL  ·  {WarningCount} WARNING");

        if (Violations.Count == 0)
            return;

        if (CriticalCount > 0)
        {
            DrcStatus = CriticalCount == 1 ? "1 CRITICAL SHORT" : $"{CriticalCount} CRITICAL SHORTS";
            DrcSeverityKey = "Critical";
        }
        else if (WarningCount > 0)
        {
            DrcStatus = WarningCount == 1 ? "1 WARNING" : $"{WarningCount} WARNINGS";
            DrcSeverityKey = "Warning";
        }
        else
        {
            DrcStatus = "DRC PASS · NETLIST CLEAN";
            DrcSeverityKey = "Pass";
        }
    }

    public void UpdateDevBoardState()
    {
        MicrocontrollerBoardProfile profile = _renderer.McuRuleEngine.GetBoardProfile(SelectedDevBoardType);
        DevBoardSummary = $"{profile.DisplayName}: {profile.OperatingLogicVoltage:0.#}V Logic, {profile.Pins.Count} Pins.";
        DevBoardStrappingNotes = SelectedDevBoardType switch
        {
            MicrocontrollerBoardType.ArduinoUnoR3R4 => "VIN: 7-12V DC. 5V Rail max 500mA. 3.3V Rail max 150mA. ATmega328P 16MHz.",
            MicrocontrollerBoardType.ArduinoMega2560 => "54 Digital I/O, 16 Analog Inputs, 4 Hardware UARTs. 5V Logic. ATmega2560 16MHz.",
            MicrocontrollerBoardType.Esp32DevKitV1 => "3.3V Logic (NOT 5V tolerant!). Strapping pins: GPIO0, 2, 12, 15. ADC2 unavailable when WiFi active.",
            MicrocontrollerBoardType.WemosEsp32S2Mini => "3.3V Logic. Native USB Type-C OTG. Ultra-compact breadboard companion.",
            MicrocontrollerBoardType.Stm32BluePillF103C8 => "3.3V Logic. Main GPIOs are 5V-Tolerant (except PB0, PB1, PA0-PA7). ARM Cortex-M3 72MHz.",
            _ => string.Empty
        };

        // EvaluateDevBoardCircuit also publishes the docked board and its
        // connected/violating pin rings, so there is one source of truth.
        if (SelectedDevBoardPins.Count > 0 && (string.IsNullOrEmpty(SelectedMcuPinToWire) || !SelectedDevBoardPins.Any(p => p.CanonicalId == SelectedMcuPinToWire)))
        {
            SelectedMcuPinToWire = SelectedDevBoardPins[0].CanonicalId;
        }
        EvaluateDevBoardCircuit();
        OnPropertyChanged(nameof(SelectedDevBoardPins));
        OnPropertyChanged(nameof(ActiveDevBoardName));
        OnDevBoardTypeChangedForProgrammer(SelectedDevBoardType);
        BuilderHint = IsDevBoardDocked
            ? $"Dev Board docked: {profile.DisplayName} outside breadboard. Pins labeled beside sockets."
            : $"Dev Board {profile.DisplayName} undocked from viewport.";
    }
}
