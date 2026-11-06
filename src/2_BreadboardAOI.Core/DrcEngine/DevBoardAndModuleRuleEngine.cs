using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Linq;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>
/// Supported off-board microcontroller development boards rendered outside the breadboard.
/// </summary>
public enum MicrocontrollerBoardType
{
    None = 0,
    ArduinoUnoR3R4 = 1,
    ArduinoMega2560,
    Esp32DevKitV1,
    WemosEsp32S2Mini,
    Stm32BluePillF103C8
}

/// <summary>
/// Functional and electrical classification for a microcontroller header socket.
/// </summary>
public enum PinElectricalRole
{
    Power5V,
    Power3V3,
    PowerVin,
    PowerVbus,
    PowerIoRef,
    Ground,
    Reset,
    AnalogRef,
    DigitalGpio,
    AnalogInputOnly,
    AnalogAndDigitalGpio,
    UartTx,
    UartRx,
    I2cSda,
    I2cScl,
    SpiSck,
    SpiMiso,
    SpiMosi,
    SpiSs,
    DacOutput,
    UsbDifferential
}

/// <summary>
/// Visual color-coding category for crisp vector labels rendered beside each socket.
/// </summary>
public enum PinColorCategory
{
    PowerRed,
    GroundDark,
    CommCyanTeal,
    GeneralIoWhite
}

/// <summary>
/// Side of the pin header relative to the label placement on the 2D PCB.
/// </summary>
public enum PinLabelAlignment
{
    LeftOfSocket,
    RightOfSocket,
    AboveSocket,
    BelowSocket
}

/// <summary>
/// Severity level for off-board microcontroller Design Rule Check (DRC) violations.
/// </summary>
public enum McuDrcSeverity
{
    Info,
    Warning,
    Error,
    CriticalDamageRisk
}

/// <summary>
/// Immutable specification of a single physical pin socket on an off-board microcontroller.
/// Coordinates (LocalXMm, LocalYMm) are expressed in millimeters relative to the board's top-left corner.
/// </summary>
public sealed record McuPinDefinition(
    string CanonicalId,
    string DisplayLabel,
    PinElectricalRole Role,
    PinColorCategory ColorCategory,
    double NominalVoltage,
    double MaxInputVoltage,
    double MaxSourceCurrentMa,
    bool Is5VTolerant,
    bool IsAnalogOnly,
    bool SupportsPwm,
    float LocalXMm,
    float LocalYMm,
    PinLabelAlignment LabelAlignment,
    IReadOnlyList<string> Aliases,
    int BusChannel = 0);

/// <summary>
/// Complete physical and electrical specification of an off-board microcontroller PCB.
/// </summary>
public sealed record MicrocontrollerBoardProfile(
    MicrocontrollerBoardType BoardType,
    string DisplayName,
    string PcbPrimaryHexColor,
    string PcbSecondaryHexColor,
    float WidthMm,
    float HeightMm,
    double OperatingLogicVoltage,
    double Max3V3RailCurrentMa,
    double Max5VRailCurrentMa,
    double MaxTotalGpioPackageCurrentMa,
    IReadOnlyList<McuPinDefinition> Pins)
{
    public McuPinDefinition? FindPin(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            return null;

        string normalized = query.Trim();
        foreach (McuPinDefinition pin in Pins)
        {
            if (string.Equals(pin.CanonicalId, normalized, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(pin.DisplayLabel, normalized, StringComparison.OrdinalIgnoreCase))
            {
                return pin;
            }

            for (int i = 0; i < pin.Aliases.Count; i++)
            {
                if (string.Equals(pin.Aliases[i], normalized, StringComparison.OrdinalIgnoreCase))
                {
                    return pin;
                }
            }
        }

        return null;
    }
}

/// <summary>
/// Represents a pin endpoint on a placed microcontroller instance in the workspace.
/// </summary>
public sealed record McuPinEndpoint(
    string BoardInstanceId,
    MicrocontrollerBoardType BoardType,
    string PinId);

/// <summary>
/// Represents an external component or breadboard load attached to a net.
/// </summary>
public enum ExternalLoadType
{
    None,
    StandardLed,
    SeriesResistor,
    LogicLevelShifter,
    VoltageDivider,
    I2cPullUpResistor,
    HobbyServoMotor,
    DcMotorOrRelay,
    LowPowerSensor3V3,
    ExternalPowerSupply
}

/// <summary>
/// Describes a breadboard component connected to a shared electrical net.
/// </summary>
public sealed record BreadboardNetComponent(
    string ComponentId,
    ExternalLoadType LoadType,
    double CurrentDrawMa = 0.0,
    double ResistanceOhms = 0.0,
    string? ConnectedBreadboardTiePoint = null,
    double SupplyVoltage = 0.0,
    bool IsPositiveTerminal = false,
    bool IsNegativeTerminal = false);

/// <summary>
/// Represents an electrical net connecting one or more off-board microcontroller pins
/// and optional breadboard components via jumper wires.
/// </summary>
public sealed record OffBoardElectricalNet(
    string NetName,
    IReadOnlyList<McuPinEndpoint> ConnectedMcuPins,
    IReadOnlyList<BreadboardNetComponent> BreadboardComponents,
    IReadOnlyList<string> BreadboardTiePoints);

/// <summary>
/// Detailed off-board DRC violation produced by <see cref="DevBoardAndModuleRuleEngine"/>.
/// </summary>
public sealed record McuDrcViolation(
    string RuleCode,
    McuDrcSeverity Severity,
    string NetName,
    string Title,
    string DetailedMessage,
    string RemediationAdvice,
    IReadOnlyList<McuPinEndpoint> OffendingPins);

// Legacy on-board definitions for backward compatibility
public enum DevBoardPinFunction : byte
{
    Power5V = 0,
    Power3V3 = 1,
    Ground = 2,
    Gpio3V3 = 3,
    Gpio5V = 4,
    InputOnlyGpio3V3 = 5,
    UartTx = 6,
    UartRx = 7,
    I2cSda = 8,
    I2cScl = 9,
    SpiClock = 10,
    Reset = 11
}

public sealed record DevBoardPinSpec(
    int PinNumber,
    string Name,
    DevBoardPinFunction Function,
    string Description);

public sealed record DevBoardDefinition(
    string ModelId,
    string DisplayName,
    int PinCount,
    double OperatingVoltage,
    bool Is5VTolerant,
    IReadOnlyList<DevBoardPinSpec> Pins)
{
    private readonly Dictionary<int, DevBoardPinSpec> _pinMap = new();
    private readonly Dictionary<string, DevBoardPinSpec> _nameMap = new(StringComparer.OrdinalIgnoreCase);

    public DevBoardDefinition(
        string modelId,
        string displayName,
        int pinCount,
        double operatingVoltage,
        bool is5VTolerant,
        IReadOnlyList<DevBoardPinSpec> pins,
        bool initMaps = true) : this(modelId, displayName, pinCount, operatingVoltage, is5VTolerant, pins)
    {
        for (int i = 0; i < pins.Count; i++)
        {
            _pinMap[pins[i].PinNumber] = pins[i];
            _nameMap[pins[i].Name] = pins[i];
        }
    }

    public DevBoardPinSpec? GetPin(int pinNumber) =>
        _pinMap.TryGetValue(pinNumber, out DevBoardPinSpec? spec) ? spec : null;

    public DevBoardPinSpec? GetPinByName(string name) =>
        _nameMap.TryGetValue(name, out DevBoardPinSpec? spec) ? spec : null;
}

/// <summary>
/// Deep electrical rule engine and pinout registry for both on-board and off-board microcontrollers
/// (Arduino Uno R3/R4, Arduino Mega 2560, ESP32 DevKit V1, WEMOS ESP32-S2 Mini, STM32F103C8T6 Blue Pill, Raspberry Pi Pico).
/// </summary>
public class DevBoardAndModuleRuleEngine
{
    public const double MinSafeLedSeriesResistanceOhms = 100.0;

    private static readonly FrozenDictionary<MicrocontrollerBoardType, MicrocontrollerBoardProfile> BoardProfiles =
        BuildBoardProfiles().ToFrozenDictionary();

    private static readonly Dictionary<string, DevBoardDefinition> BoardCatalog = new(StringComparer.OrdinalIgnoreCase);

    static DevBoardAndModuleRuleEngine()
    {
        RegisterEsp32DevKit30();
        RegisterEsp32NodeMcu38();
        RegisterArduinoNano30();
        RegisterRaspberryPiPico40();
    }

    public DevBoardAndModuleRuleEngine()
    {
    }

    // =========================================================================
    // Off-Board Microcontroller Public API
    // =========================================================================

    public MicrocontrollerBoardProfile GetBoardProfile(MicrocontrollerBoardType boardType) =>
        BoardProfiles.TryGetValue(boardType, out var profile) ? profile : BoardProfiles[MicrocontrollerBoardType.None];

    public IEnumerable<MicrocontrollerBoardProfile> GetAllBoardProfiles() =>
        BoardProfiles.Values;

    public McuPinDefinition? ResolvePin(MicrocontrollerBoardType boardType, string pinIdOrLabel) =>
        BoardProfiles.TryGetValue(boardType, out MicrocontrollerBoardProfile? profile)
            ? profile.FindPin(pinIdOrLabel)
            : null;

    public IReadOnlyList<McuDrcViolation> EvaluateCircuit(IEnumerable<OffBoardElectricalNet> nets)
    {
        ArgumentNullException.ThrowIfNull(nets);

        var violations = new List<McuDrcViolation>();
        var netList = nets as IReadOnlyList<OffBoardElectricalNet> ?? nets.ToList();

        var board3V3LoadMap = new Dictionary<string, (MicrocontrollerBoardProfile Profile, double TotalMa, List<McuPinEndpoint> Endpoints)>(
            StringComparer.OrdinalIgnoreCase);

        foreach (OffBoardElectricalNet net in netList)
        {
            var resolvedEndpoints = new List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)>();

            foreach (McuPinEndpoint endpoint in net.ConnectedMcuPins)
            {
                MicrocontrollerBoardProfile board = GetBoardProfile(endpoint.BoardType);
                McuPinDefinition? pin = board.FindPin(endpoint.PinId);
                if (pin is not null)
                {
                    resolvedEndpoints.Add((endpoint, board, pin));
                }
            }

            if (resolvedEndpoints.Count == 0)
                continue;

            CheckDirectPowerShort(net, resolvedEndpoints, violations);
            CheckExternalSupplyOvervoltage(net, resolvedEndpoints, violations);
            CheckCrossBoardOvervoltage(net, resolvedEndpoints, violations);
            CheckGpioDirectRailTie(net, resolvedEndpoints, violations);
            CheckUartProtocolCrossing(net, resolvedEndpoints, violations);
            CheckI2cBusCrossing(net, resolvedEndpoints, violations);
            CheckDirectLedWithoutResistor(net, resolvedEndpoints, violations);
            Check3V3LdoOverloadOnNet(net, resolvedEndpoints, board3V3LoadMap, violations);
            CheckInputOnlyPinDrive(net, resolvedEndpoints, violations);
            CheckEsp32BootStrapping(net, resolvedEndpoints, violations);
            CheckGpioOutputContention(net, resolvedEndpoints, violations);
            CheckMissingI2cPullups(net, resolvedEndpoints, violations);
            CheckVinSupplyVoltage(net, resolvedEndpoints, violations);
        }

        foreach ((string boardInstanceId, var loadInfo) in board3V3LoadMap)
        {
            if (loadInfo.TotalMa > loadInfo.Profile.Max3V3RailCurrentMa)
            {
                bool alreadyFlagged = violations.Any(v =>
                    v.RuleCode == "DRC-3V3-LDO-OVERLOAD" &&
                    v.OffendingPins.Any(p => string.Equals(p.BoardInstanceId, boardInstanceId, StringComparison.OrdinalIgnoreCase)));

                if (!alreadyFlagged)
                {
                    violations.Add(new McuDrcViolation(
                        RuleCode: "DRC-3V3-LDO-OVERLOAD",
                        Severity: McuDrcSeverity.CriticalDamageRisk,
                        NetName: "MULTI_NET_3V3",
                        Title: $"Cumulative 3.3V LDO Overload on {loadInfo.Profile.DisplayName} ({boardInstanceId})",
                        DetailedMessage: $"Total current drawn from the onboard 3.3V LDO regulator of {loadInfo.Profile.DisplayName} is {loadInfo.TotalMa:F0} mA, exceeding its safe thermal rating of {loadInfo.Profile.Max3V3RailCurrentMa:F0} mA.",
                        RemediationAdvice: "Power high-current loads (servos, motors, RF modules) from an external regulated power supply and share only a common GND with the microcontroller.",
                        OffendingPins: loadInfo.Endpoints));
                }
            }
        }

        // Circuit-Wide Check: Missing Common Ground Reference (DRC-FLOATING-GROUND-REF)
        CheckFloatingGroundReference(netList, violations);

        return violations;
    }

    private void CheckFloatingGroundReference(
        IReadOnlyList<OffBoardElectricalNet> netList,
        List<McuDrcViolation> violations)
    {
        // Check if any net contains an external power supply or battery
        bool hasExternalSupply = netList.Any(n => n.BreadboardComponents.Any(c => c.LoadType == ExternalLoadType.ExternalPowerSupply));
        if (!hasExternalSupply)
            return;

        // Check if any MCU pins are active/wired in the circuit
        var allConnectedMcuPins = netList.SelectMany(n => n.ConnectedMcuPins).ToList();
        if (allConnectedMcuPins.Count == 0)
            return;

        // An external power supply exists and MCU pins are present.
        // Check if at least one MCU Ground pin is tied to the common ground (the external supply's negative rail / GND)
        bool commonGroundEstablished = netList.Any(n =>
        {
            bool hasSupplyNegative = n.BreadboardComponents.Any(c => c.LoadType == ExternalLoadType.ExternalPowerSupply && c.IsNegativeTerminal);
            bool hasBreadboardGndRail = string.Equals(n.NetName, "NET_GND", StringComparison.OrdinalIgnoreCase) ||
                                       n.BreadboardTiePoints.Any(h => h.Contains("GND", StringComparison.OrdinalIgnoreCase));

            if (!hasSupplyNegative && !hasBreadboardGndRail)
                return false;

            return n.ConnectedMcuPins.Any(p =>
            {
                var profile = GetBoardProfile(p.BoardType);
                var pinDef = profile.FindPin(p.PinId);
                return pinDef?.Role == PinElectricalRole.Ground;
            });
        });

        if (!commonGroundEstablished)
        {
            var mcuGndPins = allConnectedMcuPins.Where(p =>
            {
                var profile = GetBoardProfile(p.BoardType);
                return profile.FindPin(p.PinId)?.Role == PinElectricalRole.Ground;
            }).ToList();

            var offendingPins = mcuGndPins.Count > 0 ? mcuGndPins : allConnectedMcuPins.Take(1).ToList();

            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-FLOATING-GROUND-REF",
                Severity: McuDrcSeverity.Warning,
                NetName: "ISOLATED_GND",
                Title: "Missing Common Ground Reference (MCU <-> External Supply)",
                DetailedMessage: "An external battery or power supply is connected to the breadboard, but its Ground (0V return) is not tied to the microcontroller Ground. Signals and reference levels will float unpredictably, causing logic malfunctions or parasitic back-powering.",
                RemediationAdvice: "Connect a jumper wire between the MCU GND pin and the breadboard Ground rail sharing the external power supply return.",
                OffendingPins: offendingPins));
        }
    }

    private static void CheckDirectPowerShort(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var powerPins = resolved.Where(r =>
            r.Pin.Role is PinElectricalRole.Power5V or PinElectricalRole.Power3V3 or PinElectricalRole.PowerVin or PinElectricalRole.PowerVbus).ToList();
        var gndPins = resolved.Where(r => r.Pin.Role == PinElectricalRole.Ground).ToList();

        // 1. Direct short between MCU power pin and MCU ground pin on same net
        if (powerPins.Count > 0 && gndPins.Count > 0)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-PWR-GND-SHORT",
                Severity: McuDrcSeverity.CriticalDamageRisk,
                NetName: net.NetName,
                Title: "Direct Power-to-Ground Short Circuit",
                DetailedMessage: $"Net '{net.NetName}' directly shorts power pin '{powerPins[0].Pin.DisplayLabel}' ({powerPins[0].Board.DisplayName}) to Ground '{gndPins[0].Pin.DisplayLabel}'.",
                RemediationAdvice: "Remove the jumper wire shorting the supply rail directly to GND.",
                OffendingPins: powerPins.Concat(gndPins).Select(x => x.Endpoint).ToList()));
        }

        // 2. Direct short between MCU power pin and breadboard GND rail / negative battery terminal
        bool hasExternalGnd = string.Equals(net.NetName, "NET_GND", StringComparison.OrdinalIgnoreCase) ||
            net.BreadboardTiePoints.Any(h => h.Contains("GND", StringComparison.OrdinalIgnoreCase)) ||
            net.BreadboardComponents.Any(c => c.IsNegativeTerminal);

        if (powerPins.Count > 0 && gndPins.Count == 0 && hasExternalGnd)
        {
            string gndTarget = net.BreadboardComponents.FirstOrDefault(c => c.IsNegativeTerminal)?.ComponentId
                ?? net.BreadboardTiePoints.FirstOrDefault(h => h.Contains("GND", StringComparison.OrdinalIgnoreCase))
                ?? "Breadboard Ground Rail (0V)";

            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-PWR-GND-SHORT",
                Severity: McuDrcSeverity.CriticalDamageRisk,
                NetName: net.NetName,
                Title: "Direct Power-to-Ground Short Circuit",
                DetailedMessage: $"Net '{net.NetName}' directly shorts power pin '{powerPins[0].Pin.DisplayLabel}' ({powerPins[0].Board.DisplayName}) to Ground ({gndTarget}).",
                RemediationAdvice: "Remove the jumper wire shorting the power supply rail directly to GND to prevent destroying the voltage regulator or battery.",
                OffendingPins: powerPins.Select(x => x.Endpoint).ToList()));
        }

        // 3. Direct short between MCU ground pin and breadboard VCC rail / positive battery terminal
        bool hasExternalVccOrSupply = net.NetName.StartsWith("NET_SUPPLY", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(net.NetName, "NET_VCC", StringComparison.OrdinalIgnoreCase) ||
            net.BreadboardTiePoints.Any(h => h.Contains("VCC", StringComparison.OrdinalIgnoreCase)) ||
            net.BreadboardComponents.Any(c => c.IsPositiveTerminal);

        if (gndPins.Count > 0 && powerPins.Count == 0 && hasExternalVccOrSupply)
        {
            string vccTarget = net.BreadboardComponents.FirstOrDefault(c => c.IsPositiveTerminal)?.ComponentId
                ?? net.BreadboardTiePoints.FirstOrDefault(h => h.Contains("VCC", StringComparison.OrdinalIgnoreCase))
                ?? "Breadboard VCC Rail";

            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-PWR-GND-SHORT",
                Severity: McuDrcSeverity.CriticalDamageRisk,
                NetName: net.NetName,
                Title: "Direct Ground-to-Power Supply Short Circuit",
                DetailedMessage: $"Net '{net.NetName}' connects Ground pin '{gndPins[0].Pin.DisplayLabel}' ({gndPins[0].Board.DisplayName}) directly to power supply ({vccTarget}).",
                RemediationAdvice: "Remove the jumper wire connecting GND directly to a positive power supply rail.",
                OffendingPins: gndPins.Select(x => x.Endpoint).ToList()));
        }

        // 4. 5V rail tied directly into 3.3V rail
        var rail5V = powerPins.Where(r => r.Pin.NominalVoltage >= 4.8).ToList();
        var rail3V3 = powerPins.Where(r => Math.Abs(r.Pin.NominalVoltage - 3.3) < 0.2).ToList();
        if (rail5V.Count > 0 && rail3V3.Count > 0)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-5V-TO-3V3-RAIL-SHORT",
                Severity: McuDrcSeverity.CriticalDamageRisk,
                NetName: net.NetName,
                Title: "5V Rail Tied Directly to 3.3V Rail",
                DetailedMessage: $"Net '{net.NetName}' ties 5V supply pin '{rail5V[0].Pin.DisplayLabel}' ({rail5V[0].Board.DisplayName}) directly into 3.3V regulator pin '{rail3V3[0].Pin.DisplayLabel}' ({rail3V3[0].Board.DisplayName}), back-feeding the 3.3V rail.",
                RemediationAdvice: "Never connect 5V directly to a 3.3V pin. Route 5V into VIN/VBUS if feeding an onboard LDO, or keep the 5V and 3.3V rails isolated.",
                OffendingPins: rail5V.Concat(rail3V3).Select(x => x.Endpoint).ToList()));
        }
    }

    private static void CheckExternalSupplyOvervoltage(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var extSupply = net.BreadboardComponents.FirstOrDefault(c =>
            c.LoadType == ExternalLoadType.ExternalPowerSupply && c.IsPositiveTerminal);

        double extVoltage = extSupply?.SupplyVoltage ?? 0.0;
        if (extVoltage <= 0.0 && net.NetName.StartsWith("NET_SUPPLY_", StringComparison.OrdinalIgnoreCase))
        {
            string numPart = net.NetName.Substring("NET_SUPPLY_".Length).TrimEnd('V', 'v');
            if (double.TryParse(numPart, System.Globalization.CultureInfo.InvariantCulture, out double parsedV))
                extVoltage = parsedV;
        }

        if (extVoltage <= 0.0)
            return;

        bool hasLevelShifterOrDivider = net.BreadboardComponents.Any(c =>
            c.LoadType is ExternalLoadType.LogicLevelShifter or ExternalLoadType.VoltageDivider);

        foreach (var target in resolved)
        {
            if (target.Pin.Role == PinElectricalRole.Ground)
                continue;

            if (target.Pin.Role == PinElectricalRole.PowerVin)
            {
                if (extVoltage > target.Pin.MaxInputVoltage)
                {
                    violations.Add(new McuDrcViolation(
                        RuleCode: "DRC-VIN-SUPPLY-EXCEEDED",
                        Severity: McuDrcSeverity.CriticalDamageRisk,
                        NetName: net.NetName,
                        Title: $"VIN Voltage Exceeded ({extVoltage:F1}V > {target.Pin.MaxInputVoltage:F1}V Max)",
                        DetailedMessage: $"External supply voltage {extVoltage:F1}V exceeds the maximum VIN rating of {target.Pin.MaxInputVoltage:F1}V for {target.Board.DisplayName}.",
                        RemediationAdvice: $"Supply between {target.Board.OperatingLogicVoltage + 2.0:F1}V and {target.Pin.MaxInputVoltage:F1}V into VIN.",
                        OffendingPins: new[] { target.Endpoint }));
                }
                continue;
            }

            if (target.Pin.Role == PinElectricalRole.Power3V3)
            {
                if (extVoltage > 3.6)
                {
                    violations.Add(new McuDrcViolation(
                        RuleCode: "DRC-OVERVOLTAGE-INTO-3V3-RAIL",
                        Severity: McuDrcSeverity.CriticalDamageRisk,
                        NetName: net.NetName,
                        Title: $"External Supply Overvoltage into 3.3V Rail ({extVoltage:F1}V -> {target.Pin.DisplayLabel})",
                        DetailedMessage: $"Net '{net.NetName}' connects an external {extVoltage:F1}V supply directly to 3.3V pin '{target.Pin.DisplayLabel}' on {target.Board.DisplayName}. The onboard 3.3V regulator output cannot withstand reverse/over-voltage and will burn out.",
                        RemediationAdvice: "Route unregulated power (>3.3V) into VIN or through an external step-down voltage regulator (e.g. LM7805/LD33V).",
                        OffendingPins: new[] { target.Endpoint }));
                }
                continue;
            }

            if (target.Pin.Role == PinElectricalRole.Power5V)
            {
                if (extVoltage > 5.5)
                {
                    violations.Add(new McuDrcViolation(
                        RuleCode: "DRC-OVERVOLTAGE-INTO-5V-RAIL",
                        Severity: McuDrcSeverity.CriticalDamageRisk,
                        NetName: net.NetName,
                        Title: $"External Supply Overvoltage into 5V Rail ({extVoltage:F1}V -> {target.Pin.DisplayLabel})",
                        DetailedMessage: $"Net '{net.NetName}' connects an external {extVoltage:F1}V supply directly to 5V pin '{target.Pin.DisplayLabel}' on {target.Board.DisplayName}, exceeding the absolute maximum 5.5V rating.",
                        RemediationAdvice: "Route external supplies (>5.5V) into VIN so the onboard regulator safely steps down the voltage.",
                        OffendingPins: new[] { target.Endpoint }));
                }
                continue;
            }

            if (!hasLevelShifterOrDivider && extVoltage > target.Pin.MaxInputVoltage)
            {
                violations.Add(new McuDrcViolation(
                    RuleCode: "DRC-OVERVOLTAGE-EXTERNAL-SUPPLY",
                    Severity: McuDrcSeverity.CriticalDamageRisk,
                    NetName: net.NetName,
                    Title: $"External Supply Overvoltage into GPIO ({extVoltage:F1}V into {target.Pin.DisplayLabel})",
                    DetailedMessage: $"Net '{net.NetName}' feeds an external {extVoltage:F1}V supply directly into pin '{target.Pin.DisplayLabel}' on {target.Board.DisplayName}. Pin max rating is {target.Pin.MaxInputVoltage:F1}V. This will blow the internal ESD clamping diodes and destroy the MCU pin.",
                    RemediationAdvice: "Do not connect raw power supplies to GPIO pins. Use a resistive voltage divider or voltage regulator to step down the signal to a safe voltage level.",
                    OffendingPins: new[] { target.Endpoint }));
            }
        }
    }

    private static void CheckGpioDirectRailTie(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        bool isGndRail = string.Equals(net.NetName, "NET_GND", StringComparison.OrdinalIgnoreCase) ||
            net.BreadboardTiePoints.Any(h => h.Contains("GND", StringComparison.OrdinalIgnoreCase)) ||
            net.BreadboardComponents.Any(c => c.IsNegativeTerminal);

        bool isPowerRail = net.NetName.StartsWith("NET_SUPPLY", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(net.NetName, "NET_VCC", StringComparison.OrdinalIgnoreCase) ||
            net.BreadboardTiePoints.Any(h => h.Contains("VCC", StringComparison.OrdinalIgnoreCase)) ||
            net.BreadboardComponents.Any(c => c.IsPositiveTerminal);

        if (!isGndRail && !isPowerRail)
            return;

        bool hasSeriesResistor = net.BreadboardComponents.Any(c =>
            c.LoadType == ExternalLoadType.SeriesResistor && c.ResistanceOhms >= 100.0);

        if (hasSeriesResistor)
            return;

        foreach (var r in resolved)
        {
            if (r.Pin.Role is PinElectricalRole.DigitalGpio or PinElectricalRole.AnalogAndDigitalGpio)
            {
                if (isGndRail)
                {
                    violations.Add(new McuDrcViolation(
                        RuleCode: "DRC-GPIO-DIRECT-GND-TIE",
                        Severity: McuDrcSeverity.Warning,
                        NetName: net.NetName,
                        Title: $"Unbuffered 0Ω Tie from GPIO '{r.Pin.DisplayLabel}' to GND",
                        DetailedMessage: $"Pin '{r.Pin.DisplayLabel}' on {r.Board.DisplayName} is tied directly to the Ground rail without a pull-down or series resistor. If configured in firmware as OUTPUT HIGH, it will produce a direct power short and destroy the pin driver.",
                        RemediationAdvice: "If used as an active-low input, use the internal pull-up resistor or add an external 10kΩ pull-down resistor. If driving a load, ensure a current-limiting resistor is in series.",
                        OffendingPins: new[] { r.Endpoint }));
                }
                else if (isPowerRail)
                {
                    violations.Add(new McuDrcViolation(
                        RuleCode: "DRC-GPIO-DIRECT-VCC-TIE",
                        Severity: McuDrcSeverity.Warning,
                        NetName: net.NetName,
                        Title: $"Unbuffered 0Ω Tie from GPIO '{r.Pin.DisplayLabel}' to VCC Rail",
                        DetailedMessage: $"Pin '{r.Pin.DisplayLabel}' on {r.Board.DisplayName} is tied directly to the VCC rail without a pull-up or series resistor. If configured in firmware as OUTPUT LOW, it will short VCC to Ground through the output transistor.",
                        RemediationAdvice: "Add a 10kΩ pull-up resistor or current-limiting series resistor.",
                        OffendingPins: new[] { r.Endpoint }));
                }
            }
        }
    }

    private static void CheckCrossBoardOvervoltage(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        bool hasLevelShifter = net.BreadboardComponents.Any(c =>
            c.LoadType is ExternalLoadType.LogicLevelShifter or ExternalLoadType.VoltageDivider);

        if (hasLevelShifter)
            return;

        var fiveVoltSources = resolved.Where(r =>
            r.Pin.NominalVoltage >= 4.8 &&
            r.Pin.Role is not PinElectricalRole.Ground and not PinElectricalRole.AnalogInputOnly and not PinElectricalRole.Reset)
            .ToList();

        if (fiveVoltSources.Count == 0)
            return;

        foreach (var target in resolved)
        {
            if (target.Pin.Role is PinElectricalRole.Ground or PinElectricalRole.PowerVin or PinElectricalRole.PowerVbus or PinElectricalRole.Power5V)
                continue;

            bool isNonTolerant3V3Pin = !target.Pin.Is5VTolerant && target.Pin.MaxInputVoltage < 4.5;
            if (!isNonTolerant3V3Pin)
                continue;

            var source = fiveVoltSources.FirstOrDefault(s =>
                !string.Equals(s.Endpoint.BoardInstanceId, target.Endpoint.BoardInstanceId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(s.Pin.CanonicalId, target.Pin.CanonicalId, StringComparison.OrdinalIgnoreCase));

            if (source.Pin is null)
                continue;

            string targetSpecNote = target.Board.BoardType == MicrocontrollerBoardType.Stm32BluePillF103C8 && target.Pin.IsAnalogOnly
                ? $"Pin '{target.Pin.DisplayLabel}' on {target.Board.DisplayName} is an Analog-capable non-FT pin strictly limited to {target.Pin.MaxInputVoltage:F1}V max."
                : $"Pin '{target.Pin.DisplayLabel}' on {target.Board.DisplayName} is a 3.3V-only CMOS input (max {target.Pin.MaxInputVoltage:F1}V) and is NOT 5V-tolerant.";

            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-OVERVOLTAGE-5V-TO-3V3",
                Severity: McuDrcSeverity.CriticalDamageRisk,
                NetName: net.NetName,
                Title: $"Cross-Board 5V Overvoltage into 3.3V Pin ({source.Board.DisplayName} -> {target.Board.DisplayName})",
                DetailedMessage: $"5V signal/supply from {source.Board.DisplayName} pin '{source.Pin.DisplayLabel}' ({source.Pin.NominalVoltage:F1}V) is wired directly to {target.Board.DisplayName} pin '{target.Pin.DisplayLabel}'. {targetSpecNote} Applying 5V will forward-bias ESD clamping diodes and permanently damage the MCU.",
                RemediationAdvice: "Insert a bi-directional logic level shifter (e.g., BSS138 module) or a 1kΩ / 2kΩ resistive voltage divider on the breadboard between the 5V pin and the 3.3V pin.",
                OffendingPins: new[] { source.Endpoint, target.Endpoint }));
        }
    }

    private static void CheckUartProtocolCrossing(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var uartPins = resolved.Where(r => r.Pin.Role is PinElectricalRole.UartTx or PinElectricalRole.UartRx).ToList();
        if (uartPins.Count < 2)
            return;

        var txPins = uartPins.Where(r => r.Pin.Role == PinElectricalRole.UartTx).ToList();
        var rxPins = uartPins.Where(r => r.Pin.Role == PinElectricalRole.UartRx).ToList();

        if (txPins.Count >= 2 && rxPins.Count == 0)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-UART-TX-TX",
                Severity: McuDrcSeverity.Error,
                NetName: net.NetName,
                Title: "UART TX-to-TX Contention Error",
                DetailedMessage: $"Net '{net.NetName}' connects UART Transmit pin '{txPins[0].Pin.DisplayLabel}' ({txPins[0].Board.DisplayName}) directly to UART Transmit pin '{txPins[1].Pin.DisplayLabel}' ({txPins[1].Board.DisplayName}). Both push-pull drivers will fight each other and communication will fail.",
                RemediationAdvice: "Cross the UART lines so that TX on the transmitter board connects to RX on the receiver board (and RX connects to TX).",
                OffendingPins: txPins.Select(x => x.Endpoint).ToList()));
        }
        else if (rxPins.Count >= 2 && txPins.Count == 0)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-UART-RX-RX",
                Severity: McuDrcSeverity.Error,
                NetName: net.NetName,
                Title: "UART RX-to-RX Crossover Error",
                DetailedMessage: $"Net '{net.NetName}' connects UART Receive pin '{rxPins[0].Pin.DisplayLabel}' ({rxPins[0].Board.DisplayName}) to UART Receive pin '{rxPins[1].Pin.DisplayLabel}' ({rxPins[1].Board.DisplayName}) with no TX driver on the line.",
                RemediationAdvice: "Connect UART RX to the opposite device's UART TX pin.",
                OffendingPins: rxPins.Select(x => x.Endpoint).ToList()));
        }
    }

    private static void CheckI2cBusCrossing(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var sdaPins = resolved.Where(r => r.Pin.Role == PinElectricalRole.I2cSda).ToList();
        var sclPins = resolved.Where(r => r.Pin.Role == PinElectricalRole.I2cScl).ToList();

        if (sdaPins.Count > 0 && sclPins.Count > 0)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-I2C-CROSS",
                Severity: McuDrcSeverity.Error,
                NetName: net.NetName,
                Title: "I2C Bus SDA/SCL Crossed",
                DetailedMessage: $"Net '{net.NetName}' crosses I2C Data pin '{sdaPins[0].Pin.DisplayLabel}' ({sdaPins[0].Board.DisplayName}) with I2C Clock pin '{sclPins[0].Pin.DisplayLabel}' ({sclPins[0].Board.DisplayName}).",
                RemediationAdvice: "I2C is a shared bus, not a crossover pair: connect SDA to SDA and SCL to SCL across all connected boards.",
                OffendingPins: sdaPins.Concat(sclPins).Select(x => x.Endpoint).ToList()));
        }
    }

    private static void CheckDirectLedWithoutResistor(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var leds = net.BreadboardComponents.Where(c => c.LoadType == ExternalLoadType.StandardLed).ToList();
        if (leds.Count == 0)
            return;

        bool hasAdequateSeriesResistor = net.BreadboardComponents.Any(c =>
            c.LoadType == ExternalLoadType.SeriesResistor && c.ResistanceOhms >= MinSafeLedSeriesResistanceOhms) ||
            leds.All(l => l.ResistanceOhms >= MinSafeLedSeriesResistanceOhms);

        if (hasAdequateSeriesResistor)
            return;

        var drivingPins = resolved.Where(r =>
            r.Pin.Role is not PinElectricalRole.Ground and not PinElectricalRole.Reset and not PinElectricalRole.AnalogInputOnly)
            .ToList();

        foreach (var driver in drivingPins)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-LED-NO-RESISTOR",
                Severity: McuDrcSeverity.Error,
                NetName: net.NetName,
                Title: $"Direct LED Connection Without Series Resistor on {driver.Pin.DisplayLabel}",
                DetailedMessage: $"LED ({leds[0].ComponentId}) on net '{net.NetName}' is connected directly to {driver.Board.DisplayName} pin '{driver.Pin.DisplayLabel}' without a current-limiting series resistor. Unclamped current will exceed the pin's {driver.Pin.MaxSourceCurrentMa:F0} mA max rating and burn out the LED or GPIO driver.",
                RemediationAdvice: $"Place a 220Ω–470Ω series resistor on the breadboard between '{driver.Pin.DisplayLabel}' and the LED anode.",
                OffendingPins: new[] { driver.Endpoint }));
        }
    }

    private static void Check3V3LdoOverloadOnNet(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        Dictionary<string, (MicrocontrollerBoardProfile Profile, double TotalMa, List<McuPinEndpoint> Endpoints)> board3V3LoadMap,
        List<McuDrcViolation> violations)
    {
        var pins3V3 = resolved.Where(r => r.Pin.Role == PinElectricalRole.Power3V3).ToList();
        if (pins3V3.Count == 0)
            return;

        double totalNetLoadMa = 0.0;
        bool hasServoOrMotor = false;

        foreach (BreadboardNetComponent comp in net.BreadboardComponents)
        {
            if (comp.LoadType is ExternalLoadType.HobbyServoMotor or ExternalLoadType.DcMotorOrRelay)
            {
                hasServoOrMotor = true;
                double effectiveDraw = comp.CurrentDrawMa > 0 ? comp.CurrentDrawMa : 500.0;
                totalNetLoadMa += effectiveDraw;
            }
            else if (comp.CurrentDrawMa > 0)
            {
                totalNetLoadMa += comp.CurrentDrawMa;
            }
        }

        foreach (var railPin in pins3V3)
        {
            if (!board3V3LoadMap.TryGetValue(railPin.Endpoint.BoardInstanceId, out var entry))
            {
                entry = (railPin.Board, 0.0, new List<McuPinEndpoint>());
            }

            entry.TotalMa += totalNetLoadMa;
            entry.Endpoints.Add(railPin.Endpoint);
            board3V3LoadMap[railPin.Endpoint.BoardInstanceId] = entry;

            if (hasServoOrMotor || totalNetLoadMa > railPin.Board.Max3V3RailCurrentMa)
            {
                violations.Add(new McuDrcViolation(
                    RuleCode: "DRC-3V3-LDO-OVERLOAD",
                    Severity: McuDrcSeverity.CriticalDamageRisk,
                    NetName: net.NetName,
                    Title: $"Excessive Load on 3.3V LDO Rail ({railPin.Board.DisplayName} '{railPin.Pin.DisplayLabel}')",
                    DetailedMessage: $"Net '{net.NetName}' draws {totalNetLoadMa:F0} mA{(hasServoOrMotor ? " (high-current servo/motor inductive load)" : "")} from the 3.3V LDO pin '{railPin.Pin.DisplayLabel}' on {railPin.Board.DisplayName}, which is rated for at most {railPin.Board.Max3V3RailCurrentMa:F0} mA. This will cause brownout resets or thermal destruction of the onboard 3.3V LDO.",
                    RemediationAdvice: "Never power servos or motors from a microcontroller's 3.3V LDO output. Use an external 5V/6V supply for the servo and tie its GND to the microcontroller GND.",
                    OffendingPins: new[] { railPin.Endpoint }));
            }
        }
    }

    private static void CheckInputOnlyPinDrive(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var inputOnlyPins = resolved.Where(r =>
            r.Pin.Role == PinElectricalRole.AnalogInputOnly || r.Pin.IsAnalogOnly).ToList();

        if (inputOnlyPins.Count == 0)
            return;

        bool hasLoad = net.BreadboardComponents.Any(c =>
            c.LoadType is ExternalLoadType.StandardLed or ExternalLoadType.HobbyServoMotor or ExternalLoadType.DcMotorOrRelay);

        var drivingSources = resolved.Where(r =>
            r.Pin.Role is PinElectricalRole.Power5V or PinElectricalRole.Power3V3 or PinElectricalRole.PowerVin or PinElectricalRole.PowerVbus or PinElectricalRole.DigitalGpio or PinElectricalRole.AnalogAndDigitalGpio or PinElectricalRole.UartTx or PinElectricalRole.DacOutput).ToList();

        foreach (var inputPin in inputOnlyPins)
        {
            if (hasLoad && drivingSources.Count == 0)
            {
                violations.Add(new McuDrcViolation(
                    RuleCode: "DRC-INPUT-ONLY-PIN-DRIVE",
                    Severity: McuDrcSeverity.Error,
                    NetName: net.NetName,
                    Title: $"Input-Only Pin Cannot Drive Load ({inputPin.Board.DisplayName} '{inputPin.Pin.DisplayLabel}')",
                    DetailedMessage: $"Pin '{inputPin.Pin.DisplayLabel}' on {inputPin.Board.DisplayName} (e.g. ESP32 GPIO34-39) is strictly an input-only line (GPI) with no output driver transistors or internal pull-ups. It cannot source or sink current to drive an LED or external actuator.",
                    RemediationAdvice: "Move the connection to a standard bidirectional GPIO pin (e.g., D2, D4, D5, D18) that supports digital output mode.",
                    OffendingPins: new[] { inputPin.Endpoint }));
            }
        }
    }

    private static void CheckEsp32BootStrapping(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var esp32Pins = resolved.Where(r =>
            r.Board.BoardType is MicrocontrollerBoardType.Esp32DevKitV1 or MicrocontrollerBoardType.WemosEsp32S2Mini).ToList();

        if (esp32Pins.Count == 0)
            return;

        var powerOrGnd = resolved.Where(r =>
            r.Pin.Role is PinElectricalRole.Power5V or PinElectricalRole.Power3V3 or PinElectricalRole.PowerVin or PinElectricalRole.Ground).ToList();

        bool hasDirectRail = powerOrGnd.Count > 0;
        bool isGnd = powerOrGnd.Any(p => p.Pin.Role == PinElectricalRole.Ground);
        bool isHigh = powerOrGnd.Any(p => p.Pin.Role is PinElectricalRole.Power5V or PinElectricalRole.Power3V3 or PinElectricalRole.PowerVin);

        foreach (var item in esp32Pins)
        {
            string id = item.Pin.CanonicalId.ToUpperInvariant();
            if ((id == "D12" || id == "12" || id == "GPIO12") && hasDirectRail && isHigh)
            {
                violations.Add(new McuDrcViolation(
                    RuleCode: "DRC-ESP32-BOOT-STRAPPING",
                    Severity: McuDrcSeverity.Warning,
                    NetName: net.NetName,
                    Title: $"ESP32 Strapping Pin GPIO12 Pulled HIGH at Boot ({item.Board.DisplayName})",
                    DetailedMessage: $"Pin '{item.Pin.DisplayLabel}' is tied directly to a HIGH logic rail ({net.NetName}). On ESP32, pulling GPIO12 (MTDI) high at power-up forces internal SPI flash voltage (VDD_SDIO) to 1.8V, crashing 3.3V flash memory on boot.",
                    RemediationAdvice: "Avoid hardwiring GPIO12 directly to 3.3V or 5V. If used as an input, ensure it is not pulled high during power-up or reset.",
                    OffendingPins: new[] { item.Endpoint }));
            }
            else if ((id == "D0" || id == "0" || id == "GPIO0") && hasDirectRail && isGnd)
            {
                violations.Add(new McuDrcViolation(
                    RuleCode: "DRC-ESP32-BOOT-STRAPPING",
                    Severity: McuDrcSeverity.Warning,
                    NetName: net.NetName,
                    Title: $"ESP32 Strapping Pin GPIO0 Tied to Ground ({item.Board.DisplayName})",
                    DetailedMessage: $"Pin '{item.Pin.DisplayLabel}' is tied directly to Ground ({net.NetName}). On ESP32, holding GPIO0 low during reset forces ROM bootloader / flashing mode, preventing user application firmware from booting.",
                    RemediationAdvice: "Use a momentary tactile pushbutton with a pull-up resistor to pull GPIO0 low only when flashing firmware, or select a non-strapping GPIO.",
                    OffendingPins: new[] { item.Endpoint }));
            }
        }
    }

    private static void CheckGpioOutputContention(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var sameBoardPins = resolved.Where(r =>
            r.Pin.Role is PinElectricalRole.DigitalGpio or PinElectricalRole.AnalogAndDigitalGpio or PinElectricalRole.SpiSs or PinElectricalRole.SpiMosi or PinElectricalRole.SpiSck &&
            !r.Pin.IsAnalogOnly).GroupBy(p => p.Endpoint.BoardInstanceId);

        foreach (var group in sameBoardPins)
        {
            var distinct = group.GroupBy(p => p.Pin.CanonicalId).Select(g => g.First()).ToList();
            if (distinct.Count >= 2)
            {
                violations.Add(new McuDrcViolation(
                    RuleCode: "DRC-GPIO-OUTPUT-CONTENTION",
                    Severity: McuDrcSeverity.Warning,
                    NetName: net.NetName,
                    Title: $"Potential GPIO Output Contention on {distinct[0].Board.DisplayName}",
                    DetailedMessage: $"Net '{net.NetName}' connects multiple digital GPIO outputs ({string.Join(", ", distinct.Select(d => $"'{d.Pin.DisplayLabel}'"))}) on the same board together. If both pins are driven in opposite directions (one HIGH and one LOW), a direct short will occur, causing excessive current draw and GPIO silicon damage.",
                    RemediationAdvice: "Avoid bridging two GPIO pins on the same board together unless one is configured as high-impedance input or open-drain.",
                    OffendingPins: distinct.Select(d => d.Endpoint).ToList()));
            }
        }
    }

    private static void CheckMissingI2cPullups(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var i2cPins = resolved.Where(r => r.Pin.Role is PinElectricalRole.I2cSda or PinElectricalRole.I2cScl).ToList();
        if (i2cPins.Count == 0)
            return;

        bool hasPullup = net.BreadboardComponents.Any(c =>
            c.LoadType == ExternalLoadType.I2cPullUpResistor ||
            (c.LoadType == ExternalLoadType.SeriesResistor && c.ResistanceOhms >= 1000 && c.ResistanceOhms <= 15000));

        if (!hasPullup && (i2cPins.Count >= 2 || net.BreadboardComponents.Any(c => c.LoadType == ExternalLoadType.LowPowerSensor3V3)))
        {
            var sample = i2cPins[0];
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-MISSING-I2C-PULLUPS",
                Severity: McuDrcSeverity.Warning,
                NetName: net.NetName,
                Title: $"I2C Bus Missing Pull-Up Resistor ({sample.Pin.DisplayLabel})",
                DetailedMessage: $"Net '{net.NetName}' carries I2C line '{sample.Pin.DisplayLabel}' but has no pull-up resistor detected. I2C is an open-drain bus: without external 2.2kΩ–4.7kΩ pull-up resistors to VCC, signal edges will be rounded or fail to rise, locking the bus in high-impedance.",
                RemediationAdvice: "Add a 4.7kΩ pull-up resistor between the I2C line and the 3.3V (or 5V) supply rail on the breadboard.",
                OffendingPins: i2cPins.Select(x => x.Endpoint).ToList()));
        }
    }

    private static void CheckVinSupplyVoltage(
        OffBoardElectricalNet net,
        List<(McuPinEndpoint Endpoint, MicrocontrollerBoardProfile Board, McuPinDefinition Pin)> resolved,
        List<McuDrcViolation> violations)
    {
        var vinPins = resolved.Where(r => r.Pin.Role == PinElectricalRole.PowerVin &&
            r.Board.BoardType is MicrocontrollerBoardType.ArduinoUnoR3R4 or MicrocontrollerBoardType.ArduinoMega2560).ToList();

        if (vinPins.Count == 0)
            return;

        var supply5V = resolved.Where(r => r.Pin.Role == PinElectricalRole.Power5V).ToList();
        if (supply5V.Count > 0)
        {
            violations.Add(new McuDrcViolation(
                RuleCode: "DRC-VIN-UNDERVOLTAGE",
                Severity: McuDrcSeverity.Warning,
                NetName: net.NetName,
                Title: "5V Supply Fed into Arduino VIN Pin (Dropout Undervoltage)",
                DetailedMessage: $"Net '{net.NetName}' feeds 5V directly into the VIN pin of {vinPins[0].Board.DisplayName}. The onboard linear regulator (AMS1117 / NCP1117) has a dropout voltage of ~1.2V to 1.5V; feeding 5V into VIN results in only ~3.5V–3.8V on the 5V rail, causing erratic ATmega operation and brownout resets.",
                RemediationAdvice: "Feed regulated 5V directly into the '5V' header pin instead of VIN, or supply at least 7.0V to VIN if using an external power source.",
                OffendingPins: vinPins.Concat(supply5V).Select(x => x.Endpoint).ToList()));
        }
    }

    private static Dictionary<MicrocontrollerBoardType, MicrocontrollerBoardProfile> BuildBoardProfiles()
    {
        return new Dictionary<MicrocontrollerBoardType, MicrocontrollerBoardProfile>
        {
            [MicrocontrollerBoardType.None] = BuildNoneProfile(),
            [MicrocontrollerBoardType.ArduinoUnoR3R4] = BuildArduinoUnoProfile(),
            [MicrocontrollerBoardType.ArduinoMega2560] = BuildArduinoMegaProfile(),
            [MicrocontrollerBoardType.Esp32DevKitV1] = BuildEsp32DevKitV1Profile(),
            [MicrocontrollerBoardType.WemosEsp32S2Mini] = BuildWemosEsp32S2MiniProfile(),
            [MicrocontrollerBoardType.Stm32BluePillF103C8] = BuildStm32BluePillProfile()
        };
    }

    private static MicrocontrollerBoardProfile BuildNoneProfile()
    {
        return new MicrocontrollerBoardProfile(
            BoardType: MicrocontrollerBoardType.None,
            DisplayName: "None (Standalone)",
            PcbPrimaryHexColor: "#1E1E1E",
            PcbSecondaryHexColor: "#141416",
            WidthMm: 0f,
            HeightMm: 0f,
            OperatingLogicVoltage: 5.0,
            Max3V3RailCurrentMa: 0,
            Max5VRailCurrentMa: 0,
            MaxTotalGpioPackageCurrentMa: 0,
            Pins: Array.Empty<McuPinDefinition>());
    }

    private static MicrocontrollerBoardProfile BuildArduinoUnoProfile()
    {
        var pins = new List<McuPinDefinition>
        {
            new("IOREF", "IOREF", PinElectricalRole.PowerIoRef, PinColorCategory.PowerRed, 5.0, 5.5, 50, true, false, false, 18.00f, 50.8f, PinLabelAlignment.BelowSocket, ["IO_REF"]),
            new("RESET", "RESET", PinElectricalRole.Reset, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 5, true, false, false, 20.54f, 50.8f, PinLabelAlignment.BelowSocket, ["RST", "NRST"]),
            new("3.3V", "3.3V", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, 3.3, 3.6, 150, false, false, false, 23.08f, 50.8f, PinLabelAlignment.BelowSocket, ["3V3"]),
            new("5V", "5V", PinElectricalRole.Power5V, PinColorCategory.PowerRed, 5.0, 5.5, 500, true, false, false, 25.62f, 50.8f, PinLabelAlignment.BelowSocket, ["+5V", "VCC"]),
            new("GND_1", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 28.16f, 50.8f, PinLabelAlignment.BelowSocket, ["GND", "GND1"]),
            new("GND_2", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 30.70f, 50.8f, PinLabelAlignment.BelowSocket, ["GND2"]),
            new("VIN", "VIN", PinElectricalRole.PowerVin, PinColorCategory.PowerRed, 9.0, 12.0, 800, true, false, false, 33.24f, 50.8f, PinLabelAlignment.BelowSocket, ["V_IN"]),

            new("A0", "A0", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 38.32f, 50.8f, PinLabelAlignment.BelowSocket, ["D14", "ADC0"]),
            new("A1", "A1", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 40.86f, 50.8f, PinLabelAlignment.BelowSocket, ["D15", "ADC1"]),
            new("A2", "A2", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 43.40f, 50.8f, PinLabelAlignment.BelowSocket, ["D16", "ADC2"]),
            new("A3", "A3", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 45.94f, 50.8f, PinLabelAlignment.BelowSocket, ["D17", "ADC3"]),
            new("A4", "A4 (SDA)", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 48.48f, 50.8f, PinLabelAlignment.BelowSocket, ["A4(SDA)", "SDA_A4", "D18"]),
            new("A5", "A5 (SCL)", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 51.02f, 50.8f, PinLabelAlignment.BelowSocket, ["A5(SCL)", "SCL_A5", "D19"]),

            new("SCL", "SCL", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 15.46f, 2.54f, PinLabelAlignment.AboveSocket, ["I2C_SCL"]),
            new("SDA", "SDA", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 18.00f, 2.54f, PinLabelAlignment.AboveSocket, ["I2C_SDA"]),
            new("AREF", "AREF", PinElectricalRole.AnalogRef, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 5, true, false, false, 20.54f, 2.54f, PinLabelAlignment.AboveSocket, ["A_REF"]),
            new("GND_3", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 23.08f, 2.54f, PinLabelAlignment.AboveSocket, ["GND3", "GND_DIG"]),
            new("D13", "D13 (SCK)", PinElectricalRole.SpiSck, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 25.62f, 2.54f, PinLabelAlignment.AboveSocket, ["13", "SCK", "D13(SCK)", "LED_BUILTIN"]),
            new("D12", "D12 (MISO)", PinElectricalRole.SpiMiso, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 28.16f, 2.54f, PinLabelAlignment.AboveSocket, ["12", "MISO", "D12(MISO)"]),
            new("D11", "D11 (MOSI)", PinElectricalRole.SpiMosi, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, true, 30.70f, 2.54f, PinLabelAlignment.AboveSocket, ["11", "MOSI", "D11(MOSI)"]),
            new("D10", "D10 (SS)", PinElectricalRole.SpiSs, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, true, 33.24f, 2.54f, PinLabelAlignment.AboveSocket, ["10", "SS", "D10(SS)"]),
            new("D9", "D9 (PWM)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, true, 35.78f, 2.54f, PinLabelAlignment.AboveSocket, ["9", "D9(PWM)"]),
            new("D8", "D8", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 38.32f, 2.54f, PinLabelAlignment.AboveSocket, ["8"]),

            new("D7", "D7", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 42.40f, 2.54f, PinLabelAlignment.AboveSocket, ["7"]),
            new("D6", "D6 (PWM)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, true, 44.94f, 2.54f, PinLabelAlignment.AboveSocket, ["6", "D6(PWM)"]),
            new("D5", "D5 (PWM)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, true, 47.48f, 2.54f, PinLabelAlignment.AboveSocket, ["5", "D5(PWM)"]),
            new("D4", "D4", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 50.02f, 2.54f, PinLabelAlignment.AboveSocket, ["4"]),
            new("D3", "D3 (PWM)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, true, 52.56f, 2.54f, PinLabelAlignment.AboveSocket, ["3", "D3(PWM)"]),
            new("D2", "D2", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 40, true, false, false, 55.10f, 2.54f, PinLabelAlignment.AboveSocket, ["2"]),
            new("D1", "D1 (TX)", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 57.64f, 2.54f, PinLabelAlignment.AboveSocket, ["1", "TX", "TX0", "D1(TX)"]),
            new("D0", "D0 (RX)", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 60.18f, 2.54f, PinLabelAlignment.AboveSocket, ["0", "RX", "RX0", "D0(RX)"])
        };

        return new MicrocontrollerBoardProfile(
            BoardType: MicrocontrollerBoardType.ArduinoUnoR3R4,
            DisplayName: "Arduino Uno R3 / R4",
            PcbPrimaryHexColor: "#00878F",
            PcbSecondaryHexColor: "#00979D",
            WidthMm: 68.6f,
            HeightMm: 53.3f,
            OperatingLogicVoltage: 5.0,
            Max3V3RailCurrentMa: 150.0,
            Max5VRailCurrentMa: 500.0,
            MaxTotalGpioPackageCurrentMa: 200.0,
            Pins: pins);
    }

    private static MicrocontrollerBoardProfile BuildArduinoMegaProfile()
    {
        var pins = new List<McuPinDefinition>
        {
            new("IOREF", "IOREF", PinElectricalRole.PowerIoRef, PinColorCategory.PowerRed, 5.0, 5.5, 50, true, false, false, 18.00f, 50.8f, PinLabelAlignment.BelowSocket, ["IO_REF"]),
            new("RESET", "RESET", PinElectricalRole.Reset, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 5, true, false, false, 20.54f, 50.8f, PinLabelAlignment.BelowSocket, ["RST"]),
            new("3.3V", "3.3V", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, 3.3, 3.6, 150, false, false, false, 23.08f, 50.8f, PinLabelAlignment.BelowSocket, ["3V3"]),
            new("5V", "5V", PinElectricalRole.Power5V, PinColorCategory.PowerRed, 5.0, 5.5, 800, true, false, false, 25.62f, 50.8f, PinLabelAlignment.BelowSocket, ["+5V"]),
            new("GND_1", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 28.16f, 50.8f, PinLabelAlignment.BelowSocket, ["GND", "GND1"]),
            new("GND_2", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 30.70f, 50.8f, PinLabelAlignment.BelowSocket, ["GND2"]),
            new("VIN", "VIN", PinElectricalRole.PowerVin, PinColorCategory.PowerRed, 9.0, 12.0, 800, true, false, false, 33.24f, 50.8f, PinLabelAlignment.BelowSocket, ["V_IN"])
        };

        for (int a = 0; a <= 15; a++)
        {
            float x = 38.32f + (a * 2.54f) + (a >= 8 ? 2.54f : 0f);
            pins.Add(new McuPinDefinition(
                CanonicalId: $"A{a}",
                DisplayLabel: $"A{a}",
                Role: PinElectricalRole.AnalogAndDigitalGpio,
                ColorCategory: PinColorCategory.GeneralIoWhite,
                NominalVoltage: 5.0,
                MaxInputVoltage: 5.5,
                MaxSourceCurrentMa: 40.0,
                Is5VTolerant: true,
                IsAnalogOnly: false,
                SupportsPwm: false,
                LocalXMm: x,
                LocalYMm: 50.8f,
                LabelAlignment: PinLabelAlignment.BelowSocket,
                Aliases: [$"ADC{a}"]));
        }

        pins.Add(new("SCL", "SCL", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 15.46f, 2.54f, PinLabelAlignment.AboveSocket, ["I2C_SCL"]));
        pins.Add(new("SDA", "SDA", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 18.00f, 2.54f, PinLabelAlignment.AboveSocket, ["I2C_SDA"]));
        pins.Add(new("AREF", "AREF", PinElectricalRole.AnalogRef, PinColorCategory.GeneralIoWhite, 5.0, 5.5, 5, true, false, false, 20.54f, 2.54f, PinLabelAlignment.AboveSocket, ["A_REF"]));
        pins.Add(new("GND_3", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 23.08f, 2.54f, PinLabelAlignment.AboveSocket, ["GND3"]));

        for (int d = 13; d >= 2; d--)
        {
            int idx = 13 - d;
            float x = 25.62f + (idx * 2.54f) + (d <= 7 ? 1.5f : 0f);
            pins.Add(new McuPinDefinition(
                CanonicalId: $"D{d}",
                DisplayLabel: $"D{d} (PWM)",
                Role: d == 13 ? PinElectricalRole.SpiSck : PinElectricalRole.DigitalGpio,
                ColorCategory: PinColorCategory.GeneralIoWhite,
                NominalVoltage: 5.0,
                MaxInputVoltage: 5.5,
                MaxSourceCurrentMa: 40.0,
                Is5VTolerant: true,
                IsAnalogOnly: false,
                SupportsPwm: true,
                LocalXMm: x,
                LocalYMm: 2.54f,
                LabelAlignment: PinLabelAlignment.AboveSocket,
                Aliases: [$"{d}", $"D{d}(PWM)"]));
        }

        pins.Add(new("D1", "TX0", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 58.60f, 2.54f, PinLabelAlignment.AboveSocket, ["TX0", "D1 (TX0)", "TX"], 0));
        pins.Add(new("D0", "RX0", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 61.14f, 2.54f, PinLabelAlignment.AboveSocket, ["RX0", "D0 (RX0)", "RX"], 0));

        pins.Add(new("D14", "TX3", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 65.20f, 2.54f, PinLabelAlignment.AboveSocket, ["TX3", "D14 (TX3)"], 3));
        pins.Add(new("D15", "RX3", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 67.74f, 2.54f, PinLabelAlignment.AboveSocket, ["RX3", "D15 (RX3)"], 3));
        pins.Add(new("D16", "TX2", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 70.28f, 2.54f, PinLabelAlignment.AboveSocket, ["TX2", "D16 (TX2)"], 2));
        pins.Add(new("D17", "RX2", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 72.82f, 2.54f, PinLabelAlignment.AboveSocket, ["RX2", "D17 (RX2)"], 2));
        pins.Add(new("D18", "TX1", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 75.36f, 2.54f, PinLabelAlignment.AboveSocket, ["TX1", "D18 (TX1)"], 1));
        pins.Add(new("D19", "RX1", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 77.90f, 2.54f, PinLabelAlignment.AboveSocket, ["RX1", "D19 (RX1)"], 1));
        pins.Add(new("D20", "SDA", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 80.44f, 2.54f, PinLabelAlignment.AboveSocket, ["D20 (SDA)", "SDA20"]));
        pins.Add(new("D21", "SCL", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, 5.0, 5.5, 40, true, false, false, 82.98f, 2.54f, PinLabelAlignment.AboveSocket, ["D21 (SCL)", "SCL21"]));

        pins.Add(new("5V_REAR1", "5V", PinElectricalRole.Power5V, PinColorCategory.PowerRed, 5.0, 5.5, 800, true, false, false, 94.00f, 4.50f, PinLabelAlignment.LeftOfSocket, ["5V_R1"]));
        pins.Add(new("5V_REAR2", "5V", PinElectricalRole.Power5V, PinColorCategory.PowerRed, 5.0, 5.5, 800, true, false, false, 96.54f, 4.50f, PinLabelAlignment.RightOfSocket, ["5V_R2"]));

        for (int row = 0; row < 16; row++)
        {
            int evenPin = 22 + (row * 2);
            int oddPin = evenPin + 1;
            float y = 7.04f + (row * 2.54f);

            pins.Add(new McuPinDefinition(
                CanonicalId: $"D{evenPin}",
                DisplayLabel: $"D{evenPin}",
                Role: evenPin == 50 ? PinElectricalRole.SpiMiso : evenPin == 52 ? PinElectricalRole.SpiSck : PinElectricalRole.DigitalGpio,
                ColorCategory: PinColorCategory.GeneralIoWhite,
                NominalVoltage: 5.0,
                MaxInputVoltage: 5.5,
                MaxSourceCurrentMa: 40.0,
                Is5VTolerant: true,
                IsAnalogOnly: false,
                SupportsPwm: evenPin is >= 44 and <= 46,
                LocalXMm: 94.00f,
                LocalYMm: y,
                LabelAlignment: PinLabelAlignment.LeftOfSocket,
                Aliases: [$"{evenPin}"]));

            pins.Add(new McuPinDefinition(
                CanonicalId: $"D{oddPin}",
                DisplayLabel: $"D{oddPin}",
                Role: oddPin == 51 ? PinElectricalRole.SpiMosi : oddPin == 53 ? PinElectricalRole.SpiSs : PinElectricalRole.DigitalGpio,
                ColorCategory: PinColorCategory.GeneralIoWhite,
                NominalVoltage: 5.0,
                MaxInputVoltage: 5.5,
                MaxSourceCurrentMa: 40.0,
                Is5VTolerant: true,
                IsAnalogOnly: false,
                SupportsPwm: oddPin == 45,
                LocalXMm: 96.54f,
                LocalYMm: y,
                LabelAlignment: PinLabelAlignment.RightOfSocket,
                Aliases: [$"{oddPin}"]));
        }

        pins.Add(new("GND_REAR1", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 94.00f, 47.68f, PinLabelAlignment.LeftOfSocket, ["GND_R1"]));
        pins.Add(new("GND_REAR2", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 1000, true, false, false, 96.54f, 47.68f, PinLabelAlignment.RightOfSocket, ["GND_R2"]));

        return new MicrocontrollerBoardProfile(
            BoardType: MicrocontrollerBoardType.ArduinoMega2560,
            DisplayName: "Arduino Mega 2560",
            PcbPrimaryHexColor: "#00878F",
            PcbSecondaryHexColor: "#00979D",
            WidthMm: 101.52f,
            HeightMm: 53.3f,
            OperatingLogicVoltage: 5.0,
            Max3V3RailCurrentMa: 150.0,
            Max5VRailCurrentMa: 800.0,
            MaxTotalGpioPackageCurrentMa: 400.0,
            Pins: pins);
    }

    private static MicrocontrollerBoardProfile BuildEsp32DevKitV1Profile()
    {
        string[] leftLabels =
        [
            "EN", "VP(36)", "VN(39)", "D34", "D35", "D32", "D33", "D25", "D26", "D27", "D14", "D12", "D13", "GND", "VIN"
        ];
        string[] rightLabels =
        [
            "3V3", "GND", "D15", "D2", "D4", "RX2(16)", "TX2(17)", "D5", "D18", "D19", "D21(SDA)", "RX0(3)", "TX0(1)", "D22(SCL)", "D23"
        ];

        var pins = new List<McuPinDefinition>();

        for (int i = 0; i < leftLabels.Length; i++)
        {
            string label = leftLabels[i];
            float x = 7.62f + (i * 2.54f);
            (string id, PinElectricalRole role, PinColorCategory color, double vNom, double vMax, bool inputOnly, string[] aliases) = label switch
            {
                "EN" => ("EN", PinElectricalRole.Reset, PinColorCategory.GeneralIoWhite, 3.3, 3.6, false, new[] { "RST", "ENABLE" }),
                "VP(36)" => ("VP", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, 3.3, 3.6, true, new[] { "GPIO36", "D36", "36", "SVP" }),
                "VN(39)" => ("VN", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, 3.3, 3.6, true, new[] { "GPIO39", "D39", "39", "SVN" }),
                "D34" => ("D34", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, 3.3, 3.6, true, new[] { "GPIO34", "34" }),
                "D35" => ("D35", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, 3.3, 3.6, true, new[] { "GPIO35", "35" }),
                "GND" => ("GND_L", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, false, new[] { "GND", "GND1" }),
                "VIN" => ("VIN", PinElectricalRole.PowerVin, PinColorCategory.PowerRed, 5.0, 12.0, false, new[] { "5V", "V_IN" }),
                _ => (label, PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, false, new[] { $"GPIO{label[1..]}", label[1..] })
            };

            pins.Add(new McuPinDefinition(
                CanonicalId: id,
                DisplayLabel: label,
                Role: role,
                ColorCategory: color,
                NominalVoltage: vNom,
                MaxInputVoltage: vMax,
                MaxSourceCurrentMa: 40.0,
                Is5VTolerant: id == "VIN",
                IsAnalogOnly: inputOnly,
                SupportsPwm: !inputOnly && role == PinElectricalRole.AnalogAndDigitalGpio,
                LocalXMm: x,
                LocalYMm: 2.54f,
                LabelAlignment: PinLabelAlignment.AboveSocket,
                Aliases: aliases));
        }

        for (int i = 0; i < rightLabels.Length; i++)
        {
            string label = rightLabels[i];
            float x = 7.62f + (i * 2.54f);
            (string id, PinElectricalRole role, PinColorCategory color, double vNom, double vMax, int busCh, string[] aliases) = label switch
            {
                "3V3" => ("3V3", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, 3.3, 3.6, 0, new[] { "3.3V", "VCC" }),
                "GND" => ("GND_R", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, 0, new[] { "GND2" }),
                "RX2(16)" => ("RX2", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 3.3, 3.6, 2, new[] { "D16", "GPIO16", "16" }),
                "TX2(17)" => ("TX2", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 3.3, 3.6, 2, new[] { "D17", "GPIO17", "17" }),
                "D21(SDA)" => ("D21", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, 3.3, 3.6, 0, new[] { "SDA", "GPIO21", "21" }),
                "RX0(3)" => ("RX0", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, 3.3, 3.6, 0, new[] { "RX", "D3", "GPIO3", "3" }),
                "TX0(1)" => ("TX0", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, 3.3, 3.6, 0, new[] { "TX", "D1", "GPIO1", "1" }),
                "D22(SCL)" => ("D22", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, 3.3, 3.6, 0, new[] { "SCL", "GPIO22", "22" }),
                _ => (label, PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, 0, new[] { $"GPIO{label[1..]}", label[1..] })
            };

            pins.Add(new McuPinDefinition(
                CanonicalId: id,
                DisplayLabel: label,
                Role: role,
                ColorCategory: color,
                NominalVoltage: vNom,
                MaxInputVoltage: vMax,
                MaxSourceCurrentMa: role == PinElectricalRole.Power3V3 ? 250.0 : 40.0,
                Is5VTolerant: false,
                IsAnalogOnly: false,
                SupportsPwm: role is PinElectricalRole.AnalogAndDigitalGpio or PinElectricalRole.DigitalGpio,
                LocalXMm: x,
                LocalYMm: 25.4f,
                LabelAlignment: PinLabelAlignment.BelowSocket,
                Aliases: aliases,
                BusChannel: busCh));
        }

        return new MicrocontrollerBoardProfile(
            BoardType: MicrocontrollerBoardType.Esp32DevKitV1,
            DisplayName: "ESP32 DevKit V1 (30-Pin)",
            PcbPrimaryHexColor: "#1E1E1E",
            PcbSecondaryHexColor: "#141416",
            WidthMm: 51.5f,
            HeightMm: 28.0f,
            OperatingLogicVoltage: 3.3,
            Max3V3RailCurrentMa: 250.0,
            Max5VRailCurrentMa: 500.0,
            MaxTotalGpioPackageCurrentMa: 250.0,
            Pins: pins);
    }

    private static MicrocontrollerBoardProfile BuildWemosEsp32S2MiniProfile()
    {
        var pins = new List<McuPinDefinition>(32);

        // Real WEMOS ESP32-S2 Mini hardware layout per official pinout specifications:
        // Dual staggered 2x8 rows on Left and Right sides of the vertical PCB.
        // Left Outer Row (8 pins): EN, IO3, IO5, IO7, IO9, IO11, IO12, 3V3
        var leftOuter = new (string id, string label, PinElectricalRole role, PinColorCategory color, double vNom, double vMax, string[] aliases)[]
        {
            ("RST", "RST", PinElectricalRole.Reset, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["EN", "RESET"]),
            ("IO3", "IO3", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO3", "3"]),
            ("IO5", "IO5", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO5", "5"]),
            ("IO7", "IO7", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO7", "7"]),
            ("IO9", "IO9 (SCL)", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, 3.3, 3.6, ["IO9", "SCL", "GPIO9"]),
            ("IO11", "IO11", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO11", "11"]),
            ("IO12", "IO12", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO12", "12"]),
            ("3V3", "3V3", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, 3.3, 3.6, ["3.3V", "VCC"])
        };

        for (int i = 0; i < leftOuter.Length; i++)
        {
            var p = leftOuter[i];
            pins.Add(new McuPinDefinition(
                CanonicalId: p.id,
                DisplayLabel: p.label,
                Role: p.role,
                ColorCategory: p.color,
                NominalVoltage: p.vNom,
                MaxInputVoltage: p.vMax,
                MaxSourceCurrentMa: p.role == PinElectricalRole.Power3V3 ? 250.0 : 40.0,
                Is5VTolerant: false,
                IsAnalogOnly: false,
                SupportsPwm: p.role is PinElectricalRole.AnalogAndDigitalGpio or PinElectricalRole.DigitalGpio,
                LocalXMm: 2.00f,
                LocalYMm: 6.35f + (i * 2.54f),
                LabelAlignment: PinLabelAlignment.LeftOfSocket,
                Aliases: p.aliases));
        }

        // Left Inner Row (8 pins): IO1, IO2, IO4, IO6, IO8, IO10, IO13, IO14
        var leftInner = new (string id, string label, PinElectricalRole role, PinColorCategory color, double vNom, double vMax, string[] aliases)[]
        {
            ("IO1", "IO1", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO1", "1"]),
            ("IO2", "IO2", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO2", "2"]),
            ("IO4", "IO4", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO4", "4"]),
            ("IO6", "IO6", PinElectricalRole.AnalogAndDigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO6", "6"]),
            ("IO8", "IO8 (SDA)", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, 3.3, 3.6, ["IO8", "SDA", "GPIO8"]),
            ("IO10", "IO10", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO10", "10"]),
            ("IO13", "IO13", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO13", "13"]),
            ("IO14", "IO14", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO14", "14"])
        };

        for (int i = 0; i < leftInner.Length; i++)
        {
            var p = leftInner[i];
            pins.Add(new McuPinDefinition(
                CanonicalId: p.id,
                DisplayLabel: p.label,
                Role: p.role,
                ColorCategory: p.color,
                NominalVoltage: p.vNom,
                MaxInputVoltage: p.vMax,
                MaxSourceCurrentMa: 40.0,
                Is5VTolerant: false,
                IsAnalogOnly: false,
                SupportsPwm: true,
                LocalXMm: 4.54f,
                LocalYMm: 6.35f + (i * 2.54f),
                LabelAlignment: PinLabelAlignment.LeftOfSocket,
                Aliases: p.aliases));
        }

        // Right Inner Row (8 pins): IO40, IO38, IO36, IO34, IO21, IO17, GND, IO15
        var rightInner = new (string id, string label, PinElectricalRole role, PinColorCategory color, double vNom, double vMax, string[] aliases)[]
        {
            ("IO40", "IO40", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO40", "40"]),
            ("IO38", "IO38", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO38", "38"]),
            ("IO36", "IO36", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO36", "36"]),
            ("IO34", "IO34", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO34", "34"]),
            ("IO21", "IO21", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO21", "21"]),
            ("IO17", "IO17 (DAC)", PinElectricalRole.DacOutput, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["IO17", "DAC", "DAC1", "GPIO17", "IO17 (DAC)"]),
            ("GND", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, ["GND", "GND1"]),
            ("IO15", "IO15", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO15", "15"])
        };

        for (int i = 0; i < rightInner.Length; i++)
        {
            var p = rightInner[i];
            pins.Add(new McuPinDefinition(
                CanonicalId: p.id,
                DisplayLabel: p.label,
                Role: p.role,
                ColorCategory: p.color,
                NominalVoltage: p.vNom,
                MaxInputVoltage: p.vMax,
                MaxSourceCurrentMa: p.role == PinElectricalRole.Ground ? 1000.0 : 40.0,
                Is5VTolerant: false,
                IsAnalogOnly: false,
                SupportsPwm: p.role is PinElectricalRole.AnalogAndDigitalGpio or PinElectricalRole.DigitalGpio,
                LocalXMm: 20.86f,
                LocalYMm: 6.35f + (i * 2.54f),
                LabelAlignment: PinLabelAlignment.RightOfSocket,
                Aliases: p.aliases));
        }

        // Right Outer Row (8 pins): IO39, IO37, IO35, IO33, IO18, IO16, GND_2, VBUS
        var rightOuter = new (string id, string label, PinElectricalRole role, PinColorCategory color, double vNom, double vMax, string[] aliases)[]
        {
            ("IO39", "IO39", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO39", "39"]),
            ("IO37", "IO37", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO37", "37"]),
            ("IO35", "IO35", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO35", "35"]),
            ("IO33", "IO33", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO33", "33"]),
            ("IO18", "IO18 (DAC)", PinElectricalRole.DacOutput, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["IO18", "DAC2", "GPIO18", "IO18 (DAC)"]),
            ("IO16", "IO16", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, 3.3, 3.6, ["GPIO16", "16"]),
            ("GND_2", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, 0.0, 0.0, ["GND", "GND2"]),
            ("VBUS", "VBUS", PinElectricalRole.PowerVbus, PinColorCategory.PowerRed, 5.0, 5.5, ["5V", "VCC", "VBUS", "V_BUS"])
        };

        for (int i = 0; i < rightOuter.Length; i++)
        {
            var p = rightOuter[i];
            pins.Add(new McuPinDefinition(
                CanonicalId: p.id,
                DisplayLabel: p.label,
                Role: p.role,
                ColorCategory: p.color,
                NominalVoltage: p.vNom,
                MaxInputVoltage: p.vMax,
                MaxSourceCurrentMa: p.role == PinElectricalRole.PowerVbus ? 500.0 : p.role == PinElectricalRole.Ground ? 1000.0 : 40.0,
                Is5VTolerant: p.role == PinElectricalRole.PowerVbus,
                IsAnalogOnly: false,
                SupportsPwm: p.role is PinElectricalRole.AnalogAndDigitalGpio or PinElectricalRole.DigitalGpio,
                LocalXMm: 23.40f,
                LocalYMm: 6.35f + (i * 2.54f),
                LabelAlignment: PinLabelAlignment.RightOfSocket,
                Aliases: p.aliases));
        }

        return new MicrocontrollerBoardProfile(
            BoardType: MicrocontrollerBoardType.WemosEsp32S2Mini,
            DisplayName: "WEMOS ESP32-S2 Mini",
            PcbPrimaryHexColor: "#3B1E6D",
            PcbSecondaryHexColor: "#4A2382",
            WidthMm: 25.4f,
            HeightMm: 34.3f,
            OperatingLogicVoltage: 3.3,
            Max3V3RailCurrentMa: 250.0,
            Max5VRailCurrentMa: 500.0,
            MaxTotalGpioPackageCurrentMa: 200.0,
            Pins: pins);
    }

    private static MicrocontrollerBoardProfile BuildStm32BluePillProfile()
    {
        var topRowSpecs = new (string Id, string Label, PinElectricalRole Role, PinColorCategory Color, bool Is5VTol, bool AnalogOnly, string[] Aliases)[]
        {
            ("B12", "B12 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB12", "B12"]),
            ("B13", "B13 (FT)", PinElectricalRole.SpiSck, PinColorCategory.GeneralIoWhite, true, false, ["PB13", "B13"]),
            ("B14", "B14 (FT)", PinElectricalRole.SpiMiso, PinColorCategory.GeneralIoWhite, true, false, ["PB14", "B14"]),
            ("B15", "B15 (FT)", PinElectricalRole.SpiMosi, PinColorCategory.GeneralIoWhite, true, false, ["PB15", "B15"]),
            ("A8", "A8 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PA8", "A8"]),
            ("A9", "A9(TX1)", PinElectricalRole.UartTx, PinColorCategory.CommCyanTeal, true, false, ["PA9", "TX1", "A9"]),
            ("A10", "A10(RX1)", PinElectricalRole.UartRx, PinColorCategory.CommCyanTeal, true, false, ["PA10", "RX1", "A10"]),
            ("A11", "A11(USB-)", PinElectricalRole.UsbDifferential, PinColorCategory.CommCyanTeal, true, false, ["PA11", "USB-", "A11"]),
            ("A12", "A12(USB+)", PinElectricalRole.UsbDifferential, PinColorCategory.CommCyanTeal, true, false, ["PA12", "USB+", "A12"]),
            ("A15", "A15 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PA15", "A15"]),
            ("B3", "B3 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB3", "B3"]),
            ("B4", "B4 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB4", "B4"]),
            ("B5", "B5 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB5", "B5"]),
            ("B6", "B6(SCL1)", PinElectricalRole.I2cScl, PinColorCategory.CommCyanTeal, true, false, ["PB6", "SCL1", "SCL", "B6"]),
            ("B7", "B7(SDA1)", PinElectricalRole.I2cSda, PinColorCategory.CommCyanTeal, true, false, ["PB7", "SDA1", "SDA", "B7"]),
            ("B8", "B8 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB8", "B8"]),
            ("B9", "B9 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB9", "B9"]),
            ("5V", "5V", PinElectricalRole.Power5V, PinColorCategory.PowerRed, true, false, ["+5V", "VCC5"]),
            ("GND_1", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, true, false, ["GND", "GND1"]),
            ("3V3_1", "3V3", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, false, false, ["3.3V", "3V3"])
        };

        var bottomRowSpecs = new (string Id, string Label, PinElectricalRole Role, PinColorCategory Color, bool Is5VTol, bool AnalogOnly, string[] Aliases)[]
        {
            ("VBAT", "VBAT", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, false, false, ["V_BAT"]),
            ("C13", "C13", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, false, false, ["PC13"]),
            ("C14", "C14", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, false, false, ["PC14"]),
            ("C15", "C15", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, false, false, ["PC15"]),
            ("A0", "A0", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA0", "ADC0"]),
            ("A1", "A1", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA1", "ADC1"]),
            ("A2", "A2", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA2", "ADC2"]),
            ("A3", "A3", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA3", "ADC3"]),
            ("A4", "A4", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA4", "ADC4"]),
            ("A5", "A5", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA5", "ADC5"]),
            ("A6", "A6", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA6", "ADC6"]),
            ("A7", "A7", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PA7", "ADC7"]),
            ("B0", "B0", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PB0", "ADC8"]),
            ("B1", "B1", PinElectricalRole.AnalogInputOnly, PinColorCategory.GeneralIoWhite, false, true, ["PB1", "ADC9"]),
            ("B10", "B10 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB10", "B10"]),
            ("B11", "B11 (FT)", PinElectricalRole.DigitalGpio, PinColorCategory.GeneralIoWhite, true, false, ["PB11", "B11"]),
            ("NRST", "NRST", PinElectricalRole.Reset, PinColorCategory.GeneralIoWhite, false, false, ["RESET", "RST"]),
            ("3V3_2", "3V3", PinElectricalRole.Power3V3, PinColorCategory.PowerRed, false, false, ["3V3_B"]),
            ("GND_2", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, true, false, ["GND2"]),
            ("GND_3", "GND", PinElectricalRole.Ground, PinColorCategory.GroundDark, true, false, ["GND3"])
        };

        var pins = new List<McuPinDefinition>(40);

        for (int i = 0; i < topRowSpecs.Length; i++)
        {
            var spec = topRowSpecs[i];
            float x = 2.54f + (i * 2.54f);
            double vNom = spec.Role == PinElectricalRole.Power5V ? 5.0 : spec.Role == PinElectricalRole.Ground ? 0.0 : 3.3;
            double vMax = spec.Is5VTol ? 5.5 : 3.6;

            pins.Add(new McuPinDefinition(
                CanonicalId: spec.Id,
                DisplayLabel: spec.Label,
                Role: spec.Role,
                ColorCategory: spec.Color,
                NominalVoltage: vNom,
                MaxInputVoltage: vMax,
                MaxSourceCurrentMa: spec.Role == PinElectricalRole.Power3V3 ? 150.0 : 25.0,
                Is5VTolerant: spec.Is5VTol,
                IsAnalogOnly: spec.AnalogOnly,
                SupportsPwm: true,
                LocalXMm: x,
                LocalYMm: 2.54f,
                LabelAlignment: PinLabelAlignment.AboveSocket,
                Aliases: spec.Aliases,
                BusChannel: 1));
        }

        for (int i = 0; i < bottomRowSpecs.Length; i++)
        {
            var spec = bottomRowSpecs[i];
            float x = 2.54f + (i * 2.54f);
            double vNom = spec.Role == PinElectricalRole.Ground ? 0.0 : 3.3;
            double vMax = spec.Is5VTol ? 5.5 : 3.6;

            pins.Add(new McuPinDefinition(
                CanonicalId: spec.Id,
                DisplayLabel: spec.Label,
                Role: spec.Role,
                ColorCategory: spec.Color,
                NominalVoltage: vNom,
                MaxInputVoltage: vMax,
                MaxSourceCurrentMa: spec.Role == PinElectricalRole.Power3V3 ? 150.0 : 25.0,
                Is5VTolerant: spec.Is5VTol,
                IsAnalogOnly: spec.AnalogOnly,
                SupportsPwm: !spec.AnalogOnly,
                LocalXMm: x,
                LocalYMm: 20.32f,
                LabelAlignment: PinLabelAlignment.BelowSocket,
                Aliases: spec.Aliases,
                BusChannel: 1));
        }

        return new MicrocontrollerBoardProfile(
            BoardType: MicrocontrollerBoardType.Stm32BluePillF103C8,
            DisplayName: "STM32F103C8T6 \"Blue Pill\"",
            PcbPrimaryHexColor: "#0D47A1",
            PcbSecondaryHexColor: "#1565C0",
            WidthMm: 53.34f,
            HeightMm: 22.86f,
            OperatingLogicVoltage: 3.3,
            Max3V3RailCurrentMa: 150.0,
            Max5VRailCurrentMa: 500.0,
            MaxTotalGpioPackageCurrentMa: 150.0,
            Pins: pins);
    }

    // =========================================================================
    // Legacy On-Board DevBoard Rule Engine Implementation
    // =========================================================================

    private static void RegisterEsp32DevKit30()
    {
        var pins = new List<DevBoardPinSpec>
        {
            new(1, "EN", DevBoardPinFunction.Reset, "Enable / Reset Input"),
            new(2, "GPIO36", DevBoardPinFunction.InputOnlyGpio3V3, "SENSOR_VP (Input Only, No Pullup/Down)"),
            new(3, "GPIO39", DevBoardPinFunction.InputOnlyGpio3V3, "SENSOR_VN (Input Only, No Pullup/Down)"),
            new(4, "GPIO34", DevBoardPinFunction.InputOnlyGpio3V3, "GPI 34 (Input Only, No Pullup/Down)"),
            new(5, "GPIO35", DevBoardPinFunction.InputOnlyGpio3V3, "GPI 35 (Input Only, No Pullup/Down)"),
            new(6, "GPIO32", DevBoardPinFunction.Gpio3V3, "GPIO 32 / ADC4 / Touch 9"),
            new(7, "GPIO33", DevBoardPinFunction.Gpio3V3, "GPIO 33 / ADC5 / Touch 8"),
            new(8, "GPIO25", DevBoardPinFunction.Gpio3V3, "GPIO 25 / DAC1"),
            new(9, "GPIO26", DevBoardPinFunction.Gpio3V3, "GPIO 26 / DAC2"),
            new(10, "GPIO27", DevBoardPinFunction.Gpio3V3, "GPIO 27 / Touch 7"),
            new(11, "GPIO14", DevBoardPinFunction.Gpio3V3, "GPIO 14 / HSPI_CLK"),
            new(12, "GPIO12", DevBoardPinFunction.Gpio3V3, "GPIO 12 / HSPI_MISO"),
            new(13, "GPIO13", DevBoardPinFunction.Gpio3V3, "GPIO 13 / HSPI_MOSI"),
            new(14, "GND", DevBoardPinFunction.Ground, "System Ground 0V"),
            new(15, "VIN", DevBoardPinFunction.Power5V, "External 5V DC Supply Input"),

            new(16, "3V3", DevBoardPinFunction.Power3V3, "+3.3V LDO Output Power"),
            new(17, "GND", DevBoardPinFunction.Ground, "System Ground 0V"),
            new(18, "GPIO15", DevBoardPinFunction.Gpio3V3, "GPIO 15 / HSPI_CS"),
            new(19, "GPIO2", DevBoardPinFunction.Gpio3V3, "GPIO 2 / Onboard LED"),
            new(20, "GPIO4", DevBoardPinFunction.Gpio3V3, "GPIO 4 / Touch 0"),
            new(21, "GPIO16", DevBoardPinFunction.UartRx, "RX2 (UART2 Receive)"),
            new(22, "GPIO17", DevBoardPinFunction.UartTx, "TX2 (UART2 Transmit)"),
            new(23, "GPIO5", DevBoardPinFunction.Gpio3V3, "GPIO 5 / VSPI_CS"),
            new(24, "GPIO18", DevBoardPinFunction.SpiClock, "GPIO 18 / VSPI_CLK"),
            new(25, "GPIO19", DevBoardPinFunction.Gpio3V3, "GPIO 19 / VSPI_MISO"),
            new(26, "GPIO21", DevBoardPinFunction.I2cSda, "SDA (I2C Data Line)"),
            new(27, "GPIO3", DevBoardPinFunction.UartRx, "RX0 (UART0 Console RX)"),
            new(28, "GPIO1", DevBoardPinFunction.UartTx, "TX0 (UART0 Console TX)"),
            new(29, "GPIO22", DevBoardPinFunction.I2cScl, "SCL (I2C Clock Line)"),
            new(30, "GPIO23", DevBoardPinFunction.Gpio3V3, "GPIO 23 / VSPI_MOSI")
        };

        var def = new DevBoardDefinition("ESP32", "ESP32 DevKit V1 (30-Pin)", 30, 3.3, is5VTolerant: false, pins, true);
        BoardCatalog["ESP32"] = def;
        BoardCatalog["ESP32-DEVKIT-V1"] = def;
        BoardCatalog["ESP32_30P"] = def;
        BoardCatalog["ESP-WROOM-32"] = def;
    }

    private static void RegisterEsp32NodeMcu38()
    {
        var pins = new List<DevBoardPinSpec>(38);
        for (int i = 1; i <= 38; i++)
        {
            string name = $"P{i}";
            DevBoardPinFunction fn = DevBoardPinFunction.Gpio3V3;
            if (i == 1) { name = "3V3"; fn = DevBoardPinFunction.Power3V3; }
            else if (i == 2) { name = "EN"; fn = DevBoardPinFunction.Reset; }
            else if (i == 3) { name = "GPIO36"; fn = DevBoardPinFunction.InputOnlyGpio3V3; }
            else if (i == 4) { name = "GPIO39"; fn = DevBoardPinFunction.InputOnlyGpio3V3; }
            else if (i == 5) { name = "GPIO34"; fn = DevBoardPinFunction.InputOnlyGpio3V3; }
            else if (i == 6) { name = "GPIO35"; fn = DevBoardPinFunction.InputOnlyGpio3V3; }
            else if (i == 14 || i == 19 || i == 38) { name = "GND"; fn = DevBoardPinFunction.Ground; }
            else if (i == 20) { name = "VIN"; fn = DevBoardPinFunction.Power5V; }
            else if (i == 27) { name = "GPIO16"; fn = DevBoardPinFunction.UartRx; }
            else if (i == 28) { name = "GPIO17"; fn = DevBoardPinFunction.UartTx; }
            else if (i == 33) { name = "GPIO21"; fn = DevBoardPinFunction.I2cSda; }
            else if (i == 36) { name = "GPIO22"; fn = DevBoardPinFunction.I2cScl; }
            else if (i == 34) { name = "GPIO1"; fn = DevBoardPinFunction.UartTx; }
            else if (i == 35) { name = "GPIO3"; fn = DevBoardPinFunction.UartRx; }

            pins.Add(new DevBoardPinSpec(i, name, fn, name));
        }

        var def = new DevBoardDefinition("ESP32_38P", "NodeMCU-32S ESP32 (38-Pin)", 38, 3.3, is5VTolerant: false, pins, true);
        BoardCatalog["ESP32_38P"] = def;
        BoardCatalog["NODEMCU-32S"] = def;
    }

    private static void RegisterArduinoNano30()
    {
        var pins = new List<DevBoardPinSpec>
        {
            new(1, "D1/TX", DevBoardPinFunction.UartTx, "UART TX Output (5V Logic)"),
            new(2, "D0/RX", DevBoardPinFunction.UartRx, "UART RX Input (5V Logic)"),
            new(3, "RESET", DevBoardPinFunction.Reset, "Active-Low Reset"),
            new(4, "GND", DevBoardPinFunction.Ground, "System Ground"),
            new(5, "D2", DevBoardPinFunction.Gpio5V, "Digital Pin 2 (5V)"),
            new(6, "D3", DevBoardPinFunction.Gpio5V, "Digital Pin 3 (PWM, 5V)"),
            new(7, "D4", DevBoardPinFunction.Gpio5V, "Digital Pin 4 (5V)"),
            new(8, "D5", DevBoardPinFunction.Gpio5V, "Digital Pin 5 (PWM, 5V)"),
            new(9, "D6", DevBoardPinFunction.Gpio5V, "Digital Pin 6 (PWM, 5V)"),
            new(10, "D7", DevBoardPinFunction.Gpio5V, "Digital Pin 7 (5V)"),
            new(11, "D8", DevBoardPinFunction.Gpio5V, "Digital Pin 8 (5V)"),
            new(12, "D9", DevBoardPinFunction.Gpio5V, "Digital Pin 9 (PWM, 5V)"),
            new(13, "D10", DevBoardPinFunction.Gpio5V, "Digital Pin 10 (SS/PWM, 5V)"),
            new(14, "D11", DevBoardPinFunction.Gpio5V, "Digital Pin 11 (MOSI/PWM, 5V)"),
            new(15, "D12", DevBoardPinFunction.Gpio5V, "Digital Pin 12 (MISO, 5V)"),

            new(16, "D13", DevBoardPinFunction.Gpio5V, "Digital Pin 13 (SCK/LED, 5V)"),
            new(17, "3V3", DevBoardPinFunction.Power3V3, "3.3V Output from FTDI/CH340"),
            new(18, "AREF", DevBoardPinFunction.Gpio5V, "Analog Reference"),
            new(19, "A0", DevBoardPinFunction.Gpio5V, "Analog Input 0"),
            new(20, "A1", DevBoardPinFunction.Gpio5V, "Analog Input 1"),
            new(21, "A2", DevBoardPinFunction.Gpio5V, "Analog Input 2"),
            new(22, "A3", DevBoardPinFunction.Gpio5V, "Analog Input 3"),
            new(23, "A4", DevBoardPinFunction.I2cSda, "I2C SDA / Analog Input 4"),
            new(24, "A5", DevBoardPinFunction.I2cScl, "I2C SCL / Analog Input 5"),
            new(25, "A6", DevBoardPinFunction.Gpio5V, "Analog Input 6"),
            new(26, "A7", DevBoardPinFunction.Gpio5V, "Analog Input 7"),
            new(27, "5V", DevBoardPinFunction.Power5V, "+5.0V Regulated Rail"),
            new(28, "RESET", DevBoardPinFunction.Reset, "Active-Low Reset"),
            new(29, "GND", DevBoardPinFunction.Ground, "System Ground"),
            new(30, "VIN", DevBoardPinFunction.Power5V, "Raw Unregulated DC Input (+7V to +12V)")
        };

        var def = new DevBoardDefinition("ARDUINO_NANO", "Arduino Nano v3 (ATmega328P)", 30, 5.0, is5VTolerant: true, pins, true);
        BoardCatalog["ARDUINO_NANO"] = def;
        BoardCatalog["NANO"] = def;
        BoardCatalog["ARDUINO"] = def;
    }

    private static void RegisterRaspberryPiPico40()
    {
        var pins = new List<DevBoardPinSpec>(40);
        for (int i = 1; i <= 40; i++)
        {
            string name = $"P{i}";
            DevBoardPinFunction fn = DevBoardPinFunction.Gpio3V3;
            if (i == 3 || i == 8 || i == 13 || i == 18 || i == 23 || i == 28 || i == 38)
            {
                name = "GND"; fn = DevBoardPinFunction.Ground;
            }
            else if (i == 36) { name = "3V3"; fn = DevBoardPinFunction.Power3V3; }
            else if (i == 39) { name = "VSYS"; fn = DevBoardPinFunction.Power5V; }
            else if (i == 40) { name = "VBUS"; fn = DevBoardPinFunction.Power5V; }
            else if (i == 1) { name = "GP0_TX"; fn = DevBoardPinFunction.UartTx; }
            else if (i == 2) { name = "GP1_RX"; fn = DevBoardPinFunction.UartRx; }
            else if (i == 6) { name = "GP4_SDA"; fn = DevBoardPinFunction.I2cSda; }
            else if (i == 7) { name = "GP5_SCL"; fn = DevBoardPinFunction.I2cScl; }

            pins.Add(new DevBoardPinSpec(i, name, fn, name));
        }

        var def = new DevBoardDefinition("PICO", "Raspberry Pi Pico (RP2040)", 40, 3.3, is5VTolerant: false, pins, true);
        BoardCatalog["PICO"] = def;
        BoardCatalog["RP2040"] = def;
    }

    public static bool TryGetBoardDefinition(string? labelOrSymbol, int pinCount, out DevBoardDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(labelOrSymbol))
        {
            string clean = labelOrSymbol.Trim();
            int slash = clean.LastIndexOf('/');
            if (slash >= 0)
                clean = clean[(slash + 1)..];

            int space = clean.IndexOf(' ');
            if (space > 0)
                clean = clean[..space];

            if (BoardCatalog.TryGetValue(clean, out definition!))
                return true;

            foreach (var pair in BoardCatalog)
            {
                if (clean.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    definition = pair.Value;
                    return true;
                }
            }

            // If an explicit IC or sensor definition exists for this part name, do not fall back to a dev board!
            if (IcPinoutCatalog.TryGetIc(clean, out _) ||
                SensorPinoutCatalog.TryGetSensorOrBoard(clean, out _) ||
                IcPinoutCatalogExtensions.TryGetExpandedIc(clean, out _) ||
                SensorPinoutCatalogExtensions.TryGetExpandedSensor(clean, out _))
            {
                definition = null!;
                return false;
            }
        }

        if (pinCount == 30)
        {
            definition = BoardCatalog["ESP32"];
            return true;
        }

        if (pinCount == 38)
        {
            definition = BoardCatalog["ESP32_38P"];
            return true;
        }

        if (pinCount == 40)
        {
            definition = BoardCatalog["PICO"];
            return true;
        }

        definition = null!;
        return false;
    }

    public static bool IsDevBoard(SnappedComponent component)
    {
        string? label = component.RawDetection.DecodedValueLabel ?? component.RawDetection.SymbolId;

        // If this part is explicitly defined in our IC or Sensor catalogs (e.g. PIC16F877A, MSP430G2553, ATmega328P),
        // it belongs to the IC or Sensor DRC engine, not the DevBoard engine.
        if (!string.IsNullOrWhiteSpace(label))
        {
            if (IcPinoutCatalog.TryGetIc(label, out _) ||
                IcPinoutCatalogExtensions.TryGetExpandedIc(label, out _) ||
                SensorPinoutCatalog.TryGetSensorOrBoard(label, out _) ||
                SensorPinoutCatalogExtensions.TryGetExpandedSensor(label, out _))
            {
                return false;
            }
        }

        if (component.RawDetection.Kind == ComponentKind.IntegratedCircuit)
            return false;

        if (component.RawDetection.Kind == ComponentKind.Microcontroller)
            return TryGetBoardDefinition(label, component.PinCount, out _);

        if (component.PinCount >= 20 && TryGetBoardDefinition(label, component.PinCount, out _))
            return true;

        return false;
    }

    public static void CheckDevBoardDrc(
        SnappedComponent board,
        IReadOnlyList<SnappedComponent> allComponents,
        List<BreadboardAOI.Core.Contracts.DrcViolation> violations,
        PinMatrixLookup matrix,
        Func<int, int> findRootNet,
        Func<int, string> formatNetName,
        bool[] stripOccupied,
        int[] stripDegree)
    {
        string boardName = board.RawDetection.DecodedValueLabel ?? board.RawDetection.SymbolId ?? "DEV_BOARD";
        int pinCount = board.PinCount;

        if (!TryGetBoardDefinition(boardName, pinCount, out DevBoardDefinition def))
            return;

        // Rule 1: Same-strip shorts across opposing pins
        for (int a = 0; a < pinCount; a++)
        {
            HoleCoord holeA = board.GetHole(a);
            if (string.IsNullOrEmpty(holeA.HoleId)) continue;

            for (int b = a + 1; b < pinCount; b++)
            {
                HoleCoord holeB = board.GetHole(b);
                if (string.IsNullOrEmpty(holeB.HoleId) || holeA.StripId != holeB.StripId)
                    continue;

                DevBoardPinSpec? pinA = def.GetPin(a + 1);
                DevBoardPinSpec? pinB = def.GetPin(b + 1);

                matrix.TryFindNearestFreeHoleInRow(holeB, stripOccupied, out HoleCoord suggested);

                violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                    Code: ViolationCode.ErcSameStripShort,
                    Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                    ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                    OffendingStripId: holeB.StripId,
                    BadHole: holeB,
                    SuggestedTargetHole: suggested,
                    TechnicalMessage: $"Dev Board '{def.DisplayName}' pin {b + 1} ({pinB?.Name ?? $"P{b+1}"}) at {holeB.HoleId} shares conductive strip {holeB.StripId} with pin {a + 1} ({pinA?.Name ?? $"P{a+1}"}) at {holeA.HoleId}. Board must straddle the center divider trench between rows E and F!"));
            }
        }

        // Rule 2: Power and Ground integrity
        DevBoardPinSpec? vccSpec = null;
        DevBoardPinSpec? gndSpec = null;

        for (int p = 0; p < def.Pins.Count; p++)
        {
            if (def.Pins[p].Function == DevBoardPinFunction.Power5V && vccSpec == null)
                vccSpec = def.Pins[p];
            else if (def.Pins[p].Function == DevBoardPinFunction.Ground && gndSpec == null)
                gndSpec = def.Pins[p];
        }

        if (vccSpec != null && gndSpec != null)
        {
            HoleCoord vccHole = board.GetHole(vccSpec.PinNumber - 1);
            HoleCoord gndHole = board.GetHole(gndSpec.PinNumber - 1);

            if (!string.IsNullOrEmpty(vccHole.HoleId) && !string.IsNullOrEmpty(gndHole.HoleId))
            {
                int vccRoot = findRootNet(vccHole.StripId);
                int gndRoot = findRootNet(gndHole.StripId);

                if (vccRoot == gndRoot && vccRoot >= 0)
                {
                    violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                        Code: ViolationCode.ErcVccGndDirectShort,
                        Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                        ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                        OffendingStripId: vccHole.StripId,
                        BadHole: vccHole,
                        SuggestedTargetHole: gndHole,
                        TechnicalMessage: $"Direct power short on '{def.DisplayName}': Supply input {vccSpec.Name} at {vccHole.HoleId} is shorted directly to GND at {gndHole.HoleId}! Disconnect power immediately to protect the board."));
                }
            }
        }

        // Rule 3: 5V-to-3.3V Overvoltage on GPIOs
        if (!def.Is5VTolerant)
        {
            for (int p = 0; p < def.Pins.Count; p++)
            {
                DevBoardPinSpec pin = def.Pins[p];
                if (pin.Function is not (DevBoardPinFunction.Gpio3V3 or DevBoardPinFunction.InputOnlyGpio3V3
                    or DevBoardPinFunction.UartRx or DevBoardPinFunction.I2cSda or DevBoardPinFunction.I2cScl))
                {
                    continue;
                }

                HoleCoord pinHole = board.GetHole(pin.PinNumber - 1);
                if (string.IsNullOrEmpty(pinHole.HoleId))
                    continue;

                int root = findRootNet(pinHole.StripId);
                string netName = formatNetName(root);

                bool isTiedTo5V = root == BreadboardSpec.StripTopVcc || root == BreadboardSpec.StripBotVcc
                    || netName == "NET_VCC" || IsNetDrivenBy5V(root, allComponents, findRootNet);

                if (isTiedTo5V)
                {
                    violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                        Code: ViolationCode.ErcVoltageOverload,
                        Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                        ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                        OffendingStripId: pinHole.StripId,
                        BadHole: pinHole,
                        SuggestedTargetHole: default,
                        TechnicalMessage: $"5V Overvoltage Overload on '{def.DisplayName}' pin {pin.PinNumber} ({pin.Name}) at {pinHole.HoleId}: Connected to 5V rail '{netName}'. {def.ModelId} GPIOs operate at 3.3V max (NOT 5V-tolerant)! A voltage divider or logic level shifter is required to avoid silicon burnout."));
                }
            }
        }

        // Rule 4: Input-Only Pins
        for (int p = 0; p < def.Pins.Count; p++)
        {
            DevBoardPinSpec pin = def.Pins[p];
            if (pin.Function != DevBoardPinFunction.InputOnlyGpio3V3)
                continue;

            HoleCoord pinHole = board.GetHole(pin.PinNumber - 1);
            if (string.IsNullOrEmpty(pinHole.HoleId))
                continue;

            int root = findRootNet(pinHole.StripId);

            if (IsDrivingOutputLoad(root, allComponents, findRootNet, out string? loadPart))
            {
                violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                    Code: ViolationCode.ErcInputOnlyViolation,
                    Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                    ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                    OffendingStripId: pinHole.StripId,
                    BadHole: pinHole,
                    SuggestedTargetHole: default,
                    TechnicalMessage: $"Invalid output configuration on '{def.DisplayName}' pin {pin.PinNumber} ({pin.Name}) at {pinHole.HoleId}: Pin is input-only (GPI) without internal output drivers or pullup/pulldown resistors. It cannot drive output load '{loadPart}'!"));
            }
        }

        // Rule 4b: Boot Strapping Pin Conflicts (ESP32 GPIO 0, 2, 12, 15)
        if (def.ModelId.Contains("ESP32", StringComparison.OrdinalIgnoreCase))
        {
            for (int p = 0; p < def.Pins.Count; p++)
            {
                DevBoardPinSpec pin = def.Pins[p];
                if (pin.Name is "GPIO12" or "MTDI" or "GPIO0" or "BOOT")
                {
                    HoleCoord pinHole = board.GetHole(pin.PinNumber - 1);
                    if (string.IsNullOrEmpty(pinHole.HoleId))
                        continue;

                    int root = findRootNet(pinHole.StripId);
                    string netName = formatNetName(root);
                    bool isTiedToVcc = root == BreadboardSpec.StripTopVcc || root == BreadboardSpec.StripBotVcc || netName == "NET_VCC";
                    bool isTiedToGnd = root == BreadboardSpec.StripTopGnd || root == BreadboardSpec.StripBotGnd || netName == "NET_GND";

                    if ((pin.Name is "GPIO12" or "MTDI") && isTiedToVcc)
                    {
                        violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                            Code: ViolationCode.ErcBootStrappingConflict,
                            Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                            ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                            OffendingStripId: pinHole.StripId,
                            BadHole: pinHole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Strapping pin conflict on ESP32 pin {pin.PinNumber} ({pin.Name}) at {pinHole.HoleId}: Strapping pin GPIO12 is tied hard to VCC. This selects 1.8V SPI flash voltage and will cause boot failure or crash!"));
                    }
                    else if ((pin.Name is "GPIO0" or "BOOT") && isTiedToGnd)
                    {
                        violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                            Code: ViolationCode.ErcBootStrappingConflict,
                            Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Warning,
                            ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                            OffendingStripId: pinHole.StripId,
                            BadHole: pinHole,
                            SuggestedTargetHole: default,
                            TechnicalMessage: $"Strapping pin warning on ESP32 pin {pin.PinNumber} ({pin.Name}) at {pinHole.HoleId}: GPIO0 is tied hard to GND, holding the ESP32 in ROM download bootloader mode instead of executing user sketch."));
                    }
                }
            }
        }

        // Rule 5: Protocol Crossover Verification
        CheckProtocolCrossover(board, def, allComponents, violations, findRootNet);
    }

    private static bool IsNetDrivenBy5V(
        int rootNet,
        IReadOnlyList<SnappedComponent> components,
        Func<int, int> findRootNet)
    {
        for (int i = 0; i < components.Count; i++)
        {
            SnappedComponent c = components[i];
            if (c.RawDetection.Kind == ComponentKind.VoltageRegulator)
            {
                if (c.PinCount >= 3 && findRootNet(c.GetHole(2).StripId) == rootNet)
                    return true;
            }
            else if (c.RawDetection.Kind == ComponentKind.Battery && c.RawDetection.DecodedValueLabel?.Contains("9V") == true)
            {
                if (findRootNet(c.Pin1Hole.StripId) == rootNet)
                    return true;
            }
        }

        return false;
    }

    private static bool IsDrivingOutputLoad(
        int rootNet,
        IReadOnlyList<SnappedComponent> components,
        Func<int, int> findRootNet,
        out string? loadPart)
    {
        loadPart = null;
        for (int i = 0; i < components.Count; i++)
        {
            SnappedComponent c = components[i];
            if (c.RawDetection.Kind is ComponentKind.Led or ComponentKind.Buzzer)
            {
                if (findRootNet(c.Pin1Hole.StripId) == rootNet || findRootNet(c.Pin2Hole.StripId) == rootNet)
                {
                    loadPart = $"{c.RawDetection.Kind} #{c.RawDetection.TrackId}";
                    return true;
                }
            }
            else if (c.RawDetection.Kind == ComponentKind.Transistor && c.PinCount >= 3)
            {
                if (findRootNet(c.GetHole(1).StripId) == rootNet)
                {
                    loadPart = $"Transistor Base #{c.RawDetection.TrackId}";
                    return true;
                }
            }
        }
        return false;
    }

    private static void CheckProtocolCrossover(
        SnappedComponent board,
        DevBoardDefinition def,
        IReadOnlyList<SnappedComponent> allComponents,
        List<BreadboardAOI.Core.Contracts.DrcViolation> violations,
        Func<int, int> findRootNet)
    {
        for (int p = 0; p < def.Pins.Count; p++)
        {
            DevBoardPinSpec pin = def.Pins[p];
            HoleCoord hole = board.GetHole(pin.PinNumber - 1);
            if (string.IsNullOrEmpty(hole.HoleId)) continue;

            int root = findRootNet(hole.StripId);

            for (int i = 0; i < allComponents.Count; i++)
            {
                SnappedComponent other = allComponents[i];
                if (other.RawDetection.TrackId == board.RawDetection.TrackId)
                    continue;

                if (pin.Function == DevBoardPinFunction.UartTx)
                {
                    if (IsPinMatchingFunction(other, root, DevBoardPinFunction.UartTx, findRootNet, out string? otherPinName, out HoleCoord badTarget))
                    {
                        violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                            Code: ViolationCode.ErcProtocolCrossoverError,
                            Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                            ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: badTarget,
                            TechnicalMessage: $"UART Protocol Crossover Error: TX pin '{pin.Name}' at {hole.HoleId} is wired directly to another TX pin '{otherPinName}' at {badTarget.HoleId}. UART requires crossed connections: TX must connect to RX, and RX must connect to TX!"));
                    }
                }
                else if (pin.Function == DevBoardPinFunction.UartRx)
                {
                    if (IsPinMatchingFunction(other, root, DevBoardPinFunction.UartRx, findRootNet, out string? otherPinName, out HoleCoord badTarget))
                    {
                        violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                            Code: ViolationCode.ErcProtocolCrossoverError,
                            Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                            ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: badTarget,
                            TechnicalMessage: $"UART Protocol Crossover Error: RX pin '{pin.Name}' at {hole.HoleId} is wired directly to another RX pin '{otherPinName}' at {badTarget.HoleId}. Connect RX to TX!"));
                    }
                }
                else if (pin.Function == DevBoardPinFunction.I2cSda)
                {
                    if (IsPinMatchingFunction(other, root, DevBoardPinFunction.I2cScl, findRootNet, out string? otherPinName, out HoleCoord badTarget))
                    {
                        violations.Add(new BreadboardAOI.Core.Contracts.DrcViolation(
                            Code: ViolationCode.ErcProtocolCrossoverError,
                            Severity: BreadboardAOI.Core.Contracts.DrcSeverity.Critical,
                            ComponentId: $"{def.ModelId}_{board.RawDetection.TrackId}",
                            OffendingStripId: hole.StripId,
                            BadHole: hole,
                            SuggestedTargetHole: badTarget,
                            TechnicalMessage: $"I2C Bus Mismatch: Data line SDA '{pin.Name}' at {hole.HoleId} is connected to Clock line SCL '{otherPinName}' at {badTarget.HoleId}. I2C lines must match directly: SDA to SDA, and SCL to SCL!"));
                    }
                }
            }
        }
    }

    private static bool IsPinMatchingFunction(
        SnappedComponent component,
        int targetRootNet,
        DevBoardPinFunction targetFunction,
        Func<int, int> findRootNet,
        out string? pinName,
        out HoleCoord targetHole)
    {
        pinName = null;
        targetHole = default;

        string? partName = component.RawDetection.DecodedValueLabel ?? component.RawDetection.SymbolId;
        if (TryGetBoardDefinition(partName, component.PinCount, out DevBoardDefinition otherDef))
        {
            for (int p = 0; p < otherDef.Pins.Count; p++)
            {
                DevBoardPinSpec spec = otherDef.Pins[p];
                if (spec.Function == targetFunction)
                {
                    HoleCoord h = component.GetHole(spec.PinNumber - 1);
                    if (!string.IsNullOrEmpty(h.HoleId) && findRootNet(h.StripId) == targetRootNet)
                    {
                        pinName = $"{otherDef.ModelId} {spec.Name}";
                        targetHole = h;
                        return true;
                    }
                }
            }
        }

        if (SensorPinoutCatalog.TryGetDefinition(partName, component.PinCount, out SensorDefinition sensorDef))
        {
            for (int p = 0; p < sensorDef.Pins.Count; p++)
            {
                var sPin = sensorDef.Pins[p];
                bool matches = false;
                if (targetFunction == DevBoardPinFunction.I2cScl && sPin.Name.Equals("SCL", StringComparison.OrdinalIgnoreCase))
                    matches = true;
                else if (targetFunction == DevBoardPinFunction.I2cSda && sPin.Name.Equals("SDA", StringComparison.OrdinalIgnoreCase))
                    matches = true;
                else if (targetFunction == DevBoardPinFunction.UartTx && sPin.Name.Equals("TX", StringComparison.OrdinalIgnoreCase))
                    matches = true;
                else if (targetFunction == DevBoardPinFunction.UartRx && sPin.Name.Equals("RX", StringComparison.OrdinalIgnoreCase))
                    matches = true;

                if (matches)
                {
                    HoleCoord h = component.GetHole(sPin.PinNumber - 1);
                    if (!string.IsNullOrEmpty(h.HoleId) && findRootNet(h.StripId) == targetRootNet)
                    {
                        pinName = $"{sensorDef.DisplayName} {sPin.Name}";
                        targetHole = h;
                        return true;
                    }
                }
            }
        }

        return false;
    }
}
