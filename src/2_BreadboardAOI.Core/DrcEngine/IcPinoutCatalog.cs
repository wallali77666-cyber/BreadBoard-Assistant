// ============================================================================
// FILE: src/2_BreadboardAOI.Core/DrcEngine/IcPinoutCatalog.cs
// PURPOSE: Pinout specifications and power pin assignments for standard ICs
//          (Timers, Op-Amps, Logic Gates, Counters, Drivers, Microcontrollers)
//          used by the KiCad DRC engine to detect IC trench shorts, power shorts,
//          reversed polarity, and floating power pins.
// ============================================================================

using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>
/// Package physical width classification for breadboard trench straddling and row-span DRC validation.
/// </summary>
public enum DipWidthCategory
{
    /// <summary>Standard 0.3 inch (300 mil / 7.62 mm) narrow DIP straddling the standard breadboard center ravine.</summary>
    Narrow300Mil,

    /// <summary>Wide 0.6 inch (600 mil / 15.24 mm) DIP or devboard spanning 6 breadboard holes across the ravine.</summary>
    Wide600Mil,

    /// <summary>Extra-wide 0.9 to 1.0 inch (900-1000 mil / 22.86-25.4 mm) devboard (e.g., ESP32 DevKit) leaving 0-1 free column per side on standard breadboards.</summary>
    ExtraWide900Mil,

    /// <summary>Single-in-line (SIP) 0.1 inch (100 mil / 2.54 mm) pitch package or TO-220/TO-92 inline header.</summary>
    SingleInLine100Mil
}

/// <summary>
/// Physical orientation fiducial marker used by AOI vision algorithms to locate Pin 1.
/// </summary>
public enum Pin1OrientationMarker
{
    HalfMoonNotch,
    EmbossedDot,
    NotchAndDot,
    BeveledPackageEdge,
    MetalHeatsinkTabBack,
    FlatFaceFront,
    SilkscreenPin1SquarePad,
    UsbConnectorTopEdge
}

/// <summary>
/// Electrical and logical pin capabilities used by the Design Rule Check (DRC) engine.
/// </summary>
[Flags]
public enum PinFunction
{
    None              = 0,
    PowerVcc          = 1 << 0,
    PowerVdd          = 1 << 1,
    MotorSupplyVs     = 1 << 2,
    Ground            = 1 << 3,
    DigitalInput      = 1 << 4,
    DigitalOutput     = 1 << 5,
    DigitalBidirectional = DigitalInput | DigitalOutput,
    AnalogInput       = 1 << 6,
    AnalogOutput      = 1 << 7,
    I2cSda            = 1 << 8,
    I2cScl            = 1 << 9,
    SpiMosi           = 1 << 10,
    SpiMiso           = 1 << 11,
    SpiSck            = 1 << 12,
    SpiCs             = 1 << 13,
    UartTx            = 1 << 14,
    UartRx            = 1 << 15,
    PwmOutput         = 1 << 16,
    ClockInput        = 1 << 17,
    ActiveLowReset    = 1 << 18,
    ActiveHighReset   = 1 << 19,
    ActiveLowEnable   = 1 << 20,
    ActiveHighEnable  = 1 << 21,
    OpenCollectorOut  = 1 << 22,
    RegulatedVoltageOut = 1 << 23,
    VoltageReference  = 1 << 24,
    OneWireData       = 1 << 25,
    NoConnect         = 1 << 26
}

/// <summary>
/// Severity level for breadboard wiring traps evaluated during automated optical and schematic DRC.
/// </summary>
public enum WiringTrapSeverity
{
    Info,
    Warning,
    CriticalDamageRisk,
    LogicFailure
}

/// <summary>
/// Represents a voltage operating window in Volts DC.
/// </summary>
public readonly record struct VoltageRange(
    double MinVolts,
    double NominalVolts,
    double MaxVolts,
    double AbsoluteMaxVolts,
    bool IsFiveVoltTolerant = true);

/// <summary>
/// Defines a single physical pin on an IC, sensor breakout, or microcontroller development board.
/// </summary>
public sealed record PinDefinition(
    int PinNumber,
    string Name,
    PinFunction Functions,
    VoltageRange? OperatingVoltage = null,
    string ElectricalNotes = "");

/// <summary>
/// Describes a known real-world breadboard wiring mistake and how the DRC engine detects/mitigates it.
/// </summary>
public sealed record BreadboardWiringTrap(
    string TrapCode,
    string Title,
    WiringTrapSeverity Severity,
    ImmutableArray<int> AffectedPins,
    string Description,
    string DrcMitigationRule);

/// <summary>
/// Complete physical, electrical, and DRC profile for a breadboardable component.
/// </summary>
public sealed record ComponentPinoutProfile(
    string PartNumber,
    ImmutableArray<string> Aliases,
    string Category,
    string Description,
    int PinCount,
    DipWidthCategory WidthCategory,
    double RowSpacingInches,
    Pin1OrientationMarker Pin1Marker,
    string Pin1MarkerDescription,
    VoltageRange SupplyVoltage,
    VoltageRange LogicVoltage,
    ImmutableArray<PinDefinition> Pins,
    ImmutableArray<BreadboardWiringTrap> CommonWiringTraps);

/// <summary>
/// Legacy IC definition for DRC engine power-pin and trench-short verification.
/// </summary>
public sealed record IcDefinition(
    string PartNumber,
    int PinCount,
    int PrimaryVccPin,            // 1-based index (e.g. 8 for 555)
    int PrimaryGndPin,            // 1-based index (e.g. 1 for 555)
    IReadOnlyList<int> AllVccPins,
    IReadOnlyList<int> AllGndPins,
    string Description,
    IReadOnlyDictionary<int, string>? PinNames = null
);

/// <summary>
/// Production catalog of 25 popular through-hole Digital Logic, Analog, Power, Converter, and Driver ICs.
/// </summary>
public static class IcPinoutCatalog
{
    private static readonly VoltageRange Ttl5V = new(4.5, 5.0, 5.5, 7.0, true);
    private static readonly VoltageRange HcCmosWide = new(2.0, 5.0, 6.0, 7.0, true);
    private static readonly VoltageRange Cd4000BSeries = new(3.0, 5.0, 15.0, 18.0, true);

    public static FrozenDictionary<string, ComponentPinoutProfile> All { get; } = CreateCatalog();

    public static bool TryGetIc(string partNumberOrAlias, out ComponentPinoutProfile? profile)
    {
        if (string.IsNullOrWhiteSpace(partNumberOrAlias))
        {
            profile = null;
            return false;
        }

        if (All.TryGetValue(partNumberOrAlias.Trim(), out profile))
            return true;

        if (IcPinoutCatalogExtensions.TryGetExpandedIc(partNumberOrAlias, out ExpandedIcDefinition? exp))
        {
            profile = exp.ToComponentPinoutProfile();
            return true;
        }

        profile = null;
        return false;
    }

    /// <summary>
    /// Looks up the IC definition by part name, KiCad symbol id, or pin count fallback.
    /// Provides 100% backward compatibility for ElectricalRulesChecker.
    /// </summary>
    public static bool TryGetDefinition(string? partNameOrSymbol, int pinCount, out IcDefinition definition)
    {
        if (!string.IsNullOrWhiteSpace(partNameOrSymbol))
        {
            string clean = partNameOrSymbol.Trim();
            int slash = clean.LastIndexOf('/');
            if (slash >= 0)
                clean = clean[(slash + 1)..];

            int space = clean.IndexOf(' ');
            if (space > 0)
                clean = clean[..space];

            if (All.TryGetValue(clean, out ComponentPinoutProfile? profile))
            {
                definition = ProfileToIcDefinition(profile);
                return true;
            }

            if (IcPinoutCatalogExtensions.TryGetExpandedIc(clean, out ExpandedIcDefinition? expDef))
            {
                definition = expDef.ToIcDefinition();
                return true;
            }

            foreach (var pair in All)
            {
                if (clean.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    definition = ProfileToIcDefinition(pair.Value);
                    return true;
                }
            }

            foreach (var pair in IcPinoutCatalogExtensions.GetExpandedIcs())
            {
                if (clean.Contains(pair.Key, StringComparison.OrdinalIgnoreCase))
                {
                    definition = pair.Value.ToIcDefinition();
                    return true;
                }
            }
        }

        // Generic fallback by standard DIP pin count
        switch (pinCount)
        {
            case 8:
                definition = new IcDefinition("DIP-8", 8, 8, 1, [8], [1, 4], "Generic DIP-8 IC");
                return true;
            case 14:
                definition = new IcDefinition("DIP-14", 14, 14, 7, [14], [7], "Generic DIP-14 IC");
                return true;
            case 16:
                definition = new IcDefinition("DIP-16", 16, 16, 8, [16], [8], "Generic DIP-16 IC");
                return true;
            case 28:
                definition = new IcDefinition("DIP-28", 28, 7, 8, [7, 20], [8, 22], "Generic DIP-28 IC");
                return true;
            default:
                if (pinCount >= 4)
                {
                    int vcc = pinCount;
                    int gnd = (pinCount / 2);
                    definition = new IcDefinition($"DIP-{pinCount}", pinCount, vcc, gnd, [vcc], [gnd], $"Generic DIP-{pinCount} IC");
                    return true;
                }
                definition = null!;
                return false;
        }
    }

    private static IcDefinition ProfileToIcDefinition(ComponentPinoutProfile p)
    {
        int vcc = 0;
        int gnd = 0;
        var allVcc = new List<int>();
        var allGnd = new List<int>();
        var names = new Dictionary<int, string>();

        for (int i = 0; i < p.Pins.Length; i++)
        {
            var pin = p.Pins[i];
            names[pin.PinNumber] = pin.Name;
            if (pin.Functions.HasFlag(PinFunction.PowerVcc) || pin.Functions.HasFlag(PinFunction.PowerVdd) || pin.Functions.HasFlag(PinFunction.MotorSupplyVs))
            {
                allVcc.Add(pin.PinNumber);
                if (vcc == 0) vcc = pin.PinNumber;
            }
            if (pin.Functions.HasFlag(PinFunction.Ground))
            {
                allGnd.Add(pin.PinNumber);
                if (gnd == 0) gnd = pin.PinNumber;
            }
        }

        if (vcc == 0) vcc = p.PinCount;
        if (gnd == 0) gnd = p.PinCount / 2;
        if (allVcc.Count == 0) allVcc.Add(vcc);
        if (allGnd.Count == 0) allGnd.Add(gnd);

        return new IcDefinition(
            PartNumber: p.PartNumber,
            PinCount: p.PinCount,
            PrimaryVccPin: vcc,
            PrimaryGndPin: gnd,
            AllVccPins: allVcc,
            AllGndPins: allGnd,
            Description: p.Description,
            PinNames: names);
    }

    private static FrozenDictionary<string, ComponentPinoutProfile> CreateCatalog()
    {
        var profiles = new ComponentPinoutProfile[]
        {
            Build74HC595(),
            Build74HC165(),
            BuildCD4017(),
            BuildCD4026(),
            Build74HC00(),
            Build74HC04(),
            Build74HC08(),
            Build74HC32(),
            Build74HC86(),
            Build74HC138(),
            Build74HC4051(),
            BuildCD4511(),
            BuildNE555(),
            BuildLM358(),
            BuildLM324(),
            BuildLM393(),
            BuildLM386(),
            BuildLM317(),
            BuildL7805(),
            BuildMCP3008(),
            BuildPCF8574(),
            BuildULN2003A(),
            BuildL293D(),
            BuildATmega328P(),
            BuildMAX7219()
        };

        var dict = new Dictionary<string, ComponentPinoutProfile>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in profiles)
        {
            dict[p.PartNumber] = p;
            foreach (var alias in p.Aliases)
            {
                dict[alias] = p;
            }
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 1. 74HC595 - 8-Bit Serial-In, Parallel-Out Shift Register with Latch
    // =========================================================================
    private static ComponentPinoutProfile Build74HC595() => new(
        PartNumber: "74HC595",
        Aliases: ["SN74HC595N", "74HC595N", "MM74HC595"],
        Category: "Digital IC - Shift Register",
        Description: "8-Bit Serial-In, Parallel-Out Shift Register with 3-State Output Latches",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge and embossed dot next to Pin 1 (QB).",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "QB (Q1)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 1"),
            new(2,  "QC (Q2)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 2"),
            new(3,  "QD (Q3)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 3"),
            new(4,  "QE (Q4)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 4"),
            new(5,  "QF (Q5)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 5"),
            new(6,  "QG (Q6)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 6"),
            new(7,  "QH (Q7)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 7"),
            new(8,  "GND",           PinFunction.Ground,        null,       "0V ground reference"),
            new(9,  "QH' (Q7S)",     PinFunction.DigitalOutput | PinFunction.SpiMiso, HcCmosWide, "Serial daisy-chain output to next 74HC595 SER pin"),
            new(10, "SRCLR_N (MR_N)",PinFunction.DigitalInput | PinFunction.ActiveLowReset, HcCmosWide, "Active-LOW shift register clear; MUST be tied HIGH (VCC) for normal operation"),
            new(11, "SRCLK (SH_CP)", PinFunction.DigitalInput | PinFunction.ClockInput | PinFunction.SpiSck, HcCmosWide, "Shift register clock input (rising-edge triggered)"),
            new(12, "RCLK (ST_CP)",  PinFunction.DigitalInput | PinFunction.ClockInput | PinFunction.SpiCs, HcCmosWide, "Storage latch clock input (rising-edge transfers shift register to output latches)"),
            new(13, "OE_N",          PinFunction.DigitalInput | PinFunction.ActiveLowEnable, HcCmosWide, "Active-LOW output enable; tie to GND to enable outputs or drive with PWM for dimming"),
            new(14, "SER (DS)",      PinFunction.DigitalInput | PinFunction.SpiMosi, HcCmosWide, "Serial data input"),
            new(15, "QA (Q0)",       PinFunction.DigitalOutput, HcCmosWide, "Parallel data output bit 0 (note: located on Pin 15, opposite QB-QH)"),
            new(16, "VCC",           PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V, typically 3.3V or 5.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC595-TRAP-01", "Floating SRCLR_N (Pin 10) or OE_N (Pin 13)", WiringTrapSeverity.LogicFailure, [10, 13],
                "Beginners leave Pin 10 (SRCLR_N) or Pin 13 (OE_N) floating. Floating CMOS inputs pick up 50/60Hz mains hum, randomly clearing the shift register or tri-stating all outputs.",
                "Require Pin 10 tied to VCC (or MCU GPIO) and Pin 13 tied to GND (or MCU PWM GPIO)."),
            new("74HC595-TRAP-02", "Capacitor Placed Directly on ST_CP (Pin 12) to GND", WiringTrapSeverity.CriticalDamageRisk, [12],
                "Some outdated tutorials advise putting a 100nF capacitor on the latch pin (Pin 12) to ground to 'stop flicker', causing high peak currents into the MCU GPIO pin. The 100nF decoupling capacitor belongs across Pin 16 (VCC) and Pin 8 (GND).",
                "Flag any capacitor connected between Pin 11/12 and GND; verify 100nF ceramic decoupling cap is across Pin 16 and Pin 8."),
            new("74HC595-TRAP-03", "Exceeding 70mA Total Package ICC Limit", WiringTrapSeverity.CriticalDamageRisk, [1, 2, 3, 4, 5, 6, 7, 15, 16],
                "Driving 8 LEDs simultaneously at 15-20mA each (120-160mA total) exceeds the 74HC595 absolute maximum 70mA total VCC/GND current rating.",
                "Verify LED series resistors are >= 470 Ohm at 5V (<= 6-8mA per LED) when all 8 outputs are active.")
        ]);

    // =========================================================================
    // 2. 74HC165 - 8-Bit Parallel-In, Serial-Out Shift Register
    // =========================================================================
    private static ComponentPinoutProfile Build74HC165() => new(
        PartNumber: "74HC165",
        Aliases: ["SN74HC165N", "74HC165N", "MM74HC165"],
        Category: "Digital IC - Shift Register",
        Description: "8-Bit Parallel-Load Shift Register (Parallel-In, Serial-Out)",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is SH/LD_N (PL_N).",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "SH/LD_N (PL_N)", PinFunction.DigitalInput | PinFunction.ActiveLowEnable, HcCmosWide, "Asynchronous parallel load input (LOW = load parallel inputs A-H, HIGH = shift)"),
            new(2,  "CLK (CP)",       PinFunction.DigitalInput | PinFunction.ClockInput | PinFunction.SpiSck, HcCmosWide, "Clock input for shifting data on rising edge"),
            new(3,  "E (D4)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input E (Bit 4)"),
            new(4,  "F (D5)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input F (Bit 5)"),
            new(5,  "G (D6)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input G (Bit 6)"),
            new(6,  "H (D7)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input H (Bit 7)"),
            new(7,  "QH_N (Q7_N)",    PinFunction.DigitalOutput, HcCmosWide, "Complementary (inverted) serial output from final stage"),
            new(8,  "GND",            PinFunction.Ground,        null,       "0V ground reference"),
            new(9,  "QH (Q7)",        PinFunction.DigitalOutput | PinFunction.SpiMiso, HcCmosWide, "True serial data output to MCU MISO / digital input"),
            new(10, "SER (DS)",       PinFunction.DigitalInput | PinFunction.SpiMosi, HcCmosWide, "Serial data input for daisy-chaining multiple 74HC165 chips; tie to GND if unused"),
            new(11, "A (D0)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input A (Bit 0)"),
            new(12, "B (D1)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input B (Bit 1)"),
            new(13, "C (D2)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input C (Bit 2)"),
            new(14, "D (D3)",         PinFunction.DigitalInput,  HcCmosWide, "Parallel data input D (Bit 3)"),
            new(15, "CLK_INH (CE_N)", PinFunction.DigitalInput | PinFunction.ActiveLowEnable, HcCmosWide, "Clock痕inhibit (active-LOW clock enable); tie to GND if always enabled"),
            new(16, "VCC",            PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC165-TRAP-01", "Floating Parallel Inputs Without Pull-Up/Pull-Down Resistors", WiringTrapSeverity.LogicFailure, [3, 4, 5, 6, 11, 12, 13, 14],
                "Pushbuttons connected to parallel inputs A-H without 10k pull-up or pull-down resistors leave inputs floating when buttons are open, producing erratic bit reads.",
                "Verify every parallel input (Pins 3-6, 11-14) connected to a switch also has a pull-up or pull-down resistor (4.7k-47k Ohm)."),
            new("74HC165-TRAP-02", "Floating CLK_INH (Pin 15) or SER (Pin 10)", WiringTrapSeverity.LogicFailure, [10, 15],
                "Leaving Pin 15 (CLK_INH) floating inhibits shifting randomly when Pin 15 drifts HIGH.",
                "Require Pin 15 tied to GND (or MCU GPIO) and Pin 10 tied to GND (or upstream QH).")
        ]);

    // =========================================================================
    // 3. CD4017 - Decade Counter / Divider with 10 Decoded Outputs
    // =========================================================================
    private static ComponentPinoutProfile BuildCD4017() => new(
        PartNumber: "CD4017",
        Aliases: ["CD4017BE", "HEF4017BP", "74HC4017", "MC14017B"],
        Category: "Digital IC - Counter",
        Description: "5-Stage Johnson Decade Counter with 10 Decoded Active-HIGH Outputs",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Decoded Output Q5.",
        SupplyVoltage: Cd4000BSeries,
        LogicVoltage: Cd4000BSeries,
        Pins:
        [
            new(1,  "Q5",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 5"),
            new(2,  "Q1",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 1"),
            new(3,  "Q0",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 0 (HIGH after reset)"),
            new(4,  "Q2",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 2"),
            new(5,  "Q6",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 6"),
            new(6,  "Q7",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 7"),
            new(7,  "Q3",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 3"),
            new(8,  "VSS (GND)",     PinFunction.Ground,        null,          "0V ground reference"),
            new(9,  "Q8",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 8"),
            new(10, "Q4",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 4"),
            new(11, "Q9",            PinFunction.DigitalOutput, Cd4000BSeries, "Decoded output 9"),
            new(12, "CO (CARRY_OUT)",PinFunction.DigitalOutput, Cd4000BSeries, "Carry out (HIGH for counts 0-4, LOW for counts 5-9)"),
            new(13, "CLK_INH (EN_N)",PinFunction.DigitalInput | PinFunction.ActiveLowEnable, Cd4000BSeries, "Clock Inhibit (tie LOW to GND to allow counting on CLK rising edges)"),
            new(14, "CLK",           PinFunction.DigitalInput | PinFunction.ClockInput, Cd4000BSeries, "Clock input (advances count on LOW-to-HIGH transition when Pin 13 is LOW)"),
            new(15, "RESET",         PinFunction.DigitalInput | PinFunction.ActiveHighReset, Cd4000BSeries, "Active-HIGH Reset (tie LOW to GND for normal counting; pulse HIGH to reset to Q0)"),
            new(16, "VDD",           PinFunction.PowerVdd,      Cd4000BSeries, "Positive supply voltage (3.0V to 15.0V)")
        ],
        CommonWiringTraps:
        [
            new("CD4017-TRAP-01", "Non-Sequential Physical Pin Ordering of Q0-Q9", WiringTrapSeverity.LogicFailure, [1, 2, 3, 4, 5, 6, 7, 9, 10, 11],
                "Outputs Q0 through Q9 are NOT arranged sequentially around the DIP package (Q0=Pin 3, Q1=Pin 2, Q2=Pin 4, Q3=Pin 7, Q4=Pin 10, Q5=Pin 1, Q6=Pin 5, Q7=Pin 6, Q8=Pin 9, Q9=Pin 11). Wiring LEDs in physical pin order scrambles chaser sequences.",
                "Cross-check LED chaser physical order against decoded output mapping [3, 2, 4, 7, 10, 1, 5, 6, 9, 11]."),
            new("CD4017-TRAP-02", "Floating RESET (Pin 15) or CLK_INH (Pin 13)", WiringTrapSeverity.LogicFailure, [13, 15],
                "Both Pin 13 (Clock Inhibit) and Pin 15 (Active-HIGH Reset) must be tied to VSS/GND for continuous counting. Leaving either floating freezes the counter on Q0.",
                "Require both Pin 13 and Pin 15 to have a path to GND (or valid driver/RC reset network).")
        ]);

    // =========================================================================
    // 4. CD4026 - Decade Counter / Divider with 7-Segment Display Outputs
    // =========================================================================
    private static ComponentPinoutProfile BuildCD4026() => new(
        PartNumber: "CD4026",
        Aliases: ["CD4026BE", "HEF4026BP"],
        Category: "Digital IC - Counter & Display Driver",
        Description: "Decade Counter with Decoded 7-Segment Common-Cathode Display Outputs",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is CLK.",
        SupplyVoltage: Cd4000BSeries,
        LogicVoltage: Cd4000BSeries,
        Pins:
        [
            new(1,  "CLK",           PinFunction.DigitalInput | PinFunction.ClockInput, Cd4000BSeries, "Clock input (increments count on rising edge)"),
            new(2,  "CLK_INH",       PinFunction.DigitalInput | PinFunction.ActiveLowEnable, Cd4000BSeries, "Clock Inhibit (tie LOW to GND to enable counting)"),
            new(3,  "DEI (DISP_EN)", PinFunction.DigitalInput | PinFunction.ActiveHighEnable, Cd4000BSeries, "Display Enable In (MUST be tied HIGH to VDD to illuminate segments a-g)"),
            new(4,  "DEO",           PinFunction.DigitalOutput, Cd4000BSeries, "Display Enable Out for ripple blanking chain"),
            new(5,  "CO (CARRY_OUT)",PinFunction.DigitalOutput, Cd4000BSeries, "Carry Out (divides clock by 10 for cascading next digit)"),
            new(6,  "SEG_F",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output F"),
            new(7,  "SEG_G",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output G"),
            new(8,  "VSS (GND)",     PinFunction.Ground,        null,          "0V ground reference"),
            new(9,  "SEG_D",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output D"),
            new(10, "SEG_A",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output A"),
            new(11, "SEG_E",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output E"),
            new(12, "SEG_B",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output B"),
            new(13, "SEG_C",         PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output C"),
            new(14, "UNGATED_C",     PinFunction.DigitalOutput, Cd4000BSeries, "Ungated C-segment output (not blanked by DEI; used for divide-by-12 clocks)"),
            new(15, "MR (RESET)",    PinFunction.DigitalInput | PinFunction.ActiveHighReset, Cd4000BSeries, "Master Reset (active-HIGH; tie LOW to VSS for normal operation)"),
            new(16, "VDD",           PinFunction.PowerVdd,      Cd4000BSeries, "Positive supply voltage (3.0V to 15.0V)")
        ],
        CommonWiringTraps:
        [
            new("CD4026-TRAP-01", "Display Blanked Because DEI (Pin 3) Is Left Low or Floating", WiringTrapSeverity.LogicFailure, [3],
                "Pin 3 (Display Enable In) is active-HIGH. If tied to GND or left floating, the internal counter increments but all 7-segment outputs remain LOW (dark).",
                "Require Pin 3 (DEI) to be tied HIGH to VDD."),
            new("CD4026-TRAP-02", "Using Common-Anode Display Instead of Common-Cathode", WiringTrapSeverity.LogicFailure, [6, 7, 9, 10, 11, 12, 13],
                "CD4026 drives active-HIGH segment outputs intended for a Common-Cathode 7-segment display.",
                "Verify connected 7-segment display common pin is tied to GND (Common-Cathode), not VDD.")
        ]);

    // =========================================================================
    // 5. 74HC00 - Quad 2-Input NAND Gate
    // =========================================================================
    private static ComponentPinoutProfile Build74HC00() => new(
        PartNumber: "74HC00",
        Aliases: ["SN74HC00N", "74LS00", "SN74LS00N", "74HCT00"],
        Category: "Digital IC - Logic Gate",
        Description: "Quadruple 2-Input Positive-NAND Gates",
        PinCount: 14,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is 1A.",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "1A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input A"),
            new(2,  "1B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input B"),
            new(3,  "1Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 1 NAND output"),
            new(4,  "2A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input A"),
            new(5,  "2B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input B"),
            new(6,  "2Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 2 NAND output"),
            new(7,  "GND", PinFunction.Ground,        null,       "0V ground reference"),
            new(8,  "3Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 3 NAND output"),
            new(9,  "3A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input A"),
            new(10, "3B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input B"),
            new(11, "4Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 4 NAND output"),
            new(12, "4A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input A"),
            new(13, "4B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input B"),
            new(14, "VCC", PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC00-TRAP-01", "Unused CMOS Gate Inputs Left Floating", WiringTrapSeverity.Warning, [1, 2, 4, 5, 9, 10, 12, 13],
                "Unused inputs on 74HC CMOS gates oscillate around Vcc/2, causing shoot-through current and heating.",
                "Warn if any unused gate's input pins (A, B) are unconnected; recommend tying unused inputs to GND or VCC.")
        ]);

    // =========================================================================
    // 6. 74HC04 - Hex Inverter
    // =========================================================================
    private static ComponentPinoutProfile Build74HC04() => new(
        PartNumber: "74HC04",
        Aliases: ["SN74HC04N", "74LS04", "74HCT04", "CD4069"],
        Category: "Digital IC - Logic Gate",
        Description: "Hex Inverter (6 Independent NOT Gates)",
        PinCount: 14,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Inverter 1 Input (1A).",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "1A",  PinFunction.DigitalInput,  HcCmosWide, "Inverter 1 input"),
            new(2,  "1Y",  PinFunction.DigitalOutput, HcCmosWide, "Inverter 1 output (~1A)"),
            new(3,  "2A",  PinFunction.DigitalInput,  HcCmosWide, "Inverter 2 input"),
            new(4,  "2Y",  PinFunction.DigitalOutput, HcCmosWide, "Inverter 2 output (~2A)"),
            new(5,  "3A",  PinFunction.DigitalInput,  HcCmosWide, "Inverter 3 input"),
            new(6,  "3Y",  PinFunction.DigitalOutput, HcCmosWide, "Inverter 3 output (~3A)"),
            new(7,  "GND", PinFunction.Ground,        null,       "0V ground reference"),
            new(8,  "4Y",  PinFunction.DigitalOutput, HcCmosWide, "Inverter 4 output (~4A)"),
            new(9,  "4A",  PinFunction.DigitalInput,  HcCmosWide, "Inverter 4 input"),
            new(10, "5Y",  PinFunction.DigitalOutput, HcCmosWide, "Inverter 5 output (~5A)"),
            new(11, "5A",  PinFunction.DigitalInput,  HcCmosWide, "Inverter 5 input"),
            new(12, "6Y",  PinFunction.DigitalOutput, HcCmosWide, "Inverter 6 output (~6A)"),
            new(13, "6A",  PinFunction.DigitalInput,  HcCmosWide, "Inverter 6 input"),
            new(14, "VCC", PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC04-TRAP-01", "Right-Side Pin Order Reversal (Pins 8-13)", WiringTrapSeverity.LogicFailure, [8, 9, 10, 11, 12, 13],
                "On the left side (Pins 1-6), inputs precede outputs (1A=1, 1Y=2). On the right side (Pins 8-13), outputs precede inputs going counter-clockwise (4Y=8, 4A=9). Students often drive output pins 8, 10, or 12 as if they were inputs.",
                "Flag any signal driver connected to outputs 4Y (Pin 8), 5Y (Pin 10), or 6Y (Pin 12)."),
            new("74HC04-TRAP-02", "Floating Unused Inverter Inputs", WiringTrapSeverity.Warning, [1, 3, 5, 9, 11, 13],
                "High-gain CMOS inverters left with floating inputs self-oscillate at several MHz and consume high supply current.",
                "Warn if any inverter input (1A-6A) is left floating.")
        ]);

    // =========================================================================
    // 7. 74HC08 - Quad 2-Input AND Gate
    // =========================================================================
    private static ComponentPinoutProfile Build74HC08() => new(
        PartNumber: "74HC08",
        Aliases: ["SN74HC08N", "74LS08", "SN74LS08N", "74HCT08"],
        Category: "Digital IC - Logic Gate",
        Description: "Quadruple 2-Input Positive-AND Gates",
        PinCount: 14,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is 1A.",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "1A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input A"),
            new(2,  "1B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input B"),
            new(3,  "1Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 1 AND output"),
            new(4,  "2A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input A"),
            new(5,  "2B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input B"),
            new(6,  "2Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 2 AND output"),
            new(7,  "GND", PinFunction.Ground,        null,       "0V ground reference"),
            new(8,  "3Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 3 AND output"),
            new(9,  "3A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input A"),
            new(10, "3B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input B"),
            new(11, "4Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 4 AND output"),
            new(12, "4A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input A"),
            new(13, "4B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input B"),
            new(14, "VCC", PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC08-TRAP-01", "Right-Side Gate Output vs Input Confusion (Pins 8, 11)", WiringTrapSeverity.LogicFailure, [8, 9, 10, 11, 12, 13],
                "For Gates 3 and 4 on the right side of the chip, outputs 3Y (Pin 8) and 4Y (Pin 11) sit below their respective inputs (9/10 and 12/13).",
                "Verify signals drive Pins 9/10 and 12/13 and read from Pins 8 and 11."),
            new("74HC08-TRAP-02", "Floating Unused Inputs", WiringTrapSeverity.Warning, [1, 2, 4, 5, 9, 10, 12, 13],
                "Unused CMOS inputs must not float.",
                "Tie unused gate inputs to GND or VCC.")
        ]);

    // =========================================================================
    // 8. 74HC32 - Quad 2-Input OR Gate
    // =========================================================================
    private static ComponentPinoutProfile Build74HC32() => new(
        PartNumber: "74HC32",
        Aliases: ["SN74HC32N", "74LS32", "SN74LS32N", "74HCT32"],
        Category: "Digital IC - Logic Gate",
        Description: "Quadruple 2-Input Positive-OR Gates",
        PinCount: 14,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is 1A.",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "1A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input A"),
            new(2,  "1B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input B"),
            new(3,  "1Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 1 OR output"),
            new(4,  "2A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input A"),
            new(5,  "2B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input B"),
            new(6,  "2Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 2 OR output"),
            new(7,  "GND", PinFunction.Ground,        null,       "0V ground reference"),
            new(8,  "3Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 3 OR output"),
            new(9,  "3A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input A"),
            new(10, "3B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input B"),
            new(11, "4Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 4 OR output"),
            new(12, "4A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input A"),
            new(13, "4B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input B"),
            new(14, "VCC", PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC32-TRAP-01", "Floating Second Input on Active OR Gate Held High by Noise", WiringTrapSeverity.LogicFailure, [1, 2, 4, 5, 9, 10, 12, 13],
                "If one input of an active OR gate is driven and the partner input is left floating, the floating input often drifts HIGH, locking the OR gate output permanently HIGH.",
                "Flag any OR gate with a connected output (1Y-4Y) where either of its two inputs is unconnected.")
        ]);

    // =========================================================================
    // 9. 74HC86 - Quad 2-Input XOR Gate
    // =========================================================================
    private static ComponentPinoutProfile Build74HC86() => new(
        PartNumber: "74HC86",
        Aliases: ["SN74HC86N", "74LS86", "SN74LS86N", "74HCT86", "CD4070"],
        Category: "Digital IC - Logic Gate",
        Description: "Quadruple 2-Input Exclusive-OR (XOR) Gates",
        PinCount: 14,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is 1A.",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "1A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input A"),
            new(2,  "1B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 1 input B"),
            new(3,  "1Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 1 XOR output"),
            new(4,  "2A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input A"),
            new(5,  "2B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 2 input B"),
            new(6,  "2Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 2 XOR output"),
            new(7,  "GND", PinFunction.Ground,        null,       "0V ground reference"),
            new(8,  "3Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 3 XOR output"),
            new(9,  "3A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input A"),
            new(10, "3B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 3 input B"),
            new(11, "4Y",  PinFunction.DigitalOutput, HcCmosWide, "Gate 4 XOR output"),
            new(12, "4A",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input A"),
            new(13, "4B",  PinFunction.DigitalInput,  HcCmosWide, "Gate 4 input B"),
            new(14, "VCC", PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC86-TRAP-01", "74LS86 vs CD4070 Pinout Compatibility Trap", WiringTrapSeverity.LogicFailure, [3, 4, 10, 11],
                "While 74HC86 uses standard 74-series gate pinout (Outputs on 3, 6, 8, 11), the CD4070 CMOS quad XOR places outputs on Pins 3, 4, 10, 11. Substituting CD4070 into a 74HC86 breadboard layout shorts Gate 2 and Gate 3 outputs.",
                "Verify part marking on IC top surface matches 74x86 pinout before applying power.")
        ]);

    // =========================================================================
    // 10. 74HC138 - 3-to-8 Line Decoder / Demultiplexer (Active-LOW Outputs)
    // =========================================================================
    private static ComponentPinoutProfile Build74HC138() => new(
        PartNumber: "74HC138",
        Aliases: ["SN74HC138N", "74LS138", "74HCT138"],
        Category: "Digital IC - Decoder / Demultiplexer",
        Description: "3-to-8 Line Decoder / Demultiplexer with Inverting (Active-LOW) Outputs",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Select Input A0.",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "A0 (A)",    PinFunction.DigitalInput,  HcCmosWide, "Address select input 0 (LSB)"),
            new(2,  "A1 (B)",    PinFunction.DigitalInput,  HcCmosWide, "Address select input 1"),
            new(3,  "A2 (C)",    PinFunction.DigitalInput,  HcCmosWide, "Address select input 2 (MSB)"),
            new(4,  "E1_N (G2A)",PinFunction.DigitalInput | PinFunction.ActiveLowEnable, HcCmosWide, "Active-LOW enable input 1 (tie to GND)"),
            new(5,  "E2_N (G2B)",PinFunction.DigitalInput | PinFunction.ActiveLowEnable, HcCmosWide, "Active-LOW enable input 2 (tie to GND)"),
            new(6,  "E3 (G1)",   PinFunction.DigitalInput | PinFunction.ActiveHighEnable, HcCmosWide, "Active-HIGH enable input 3 (tie to VCC)"),
            new(7,  "Y7_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 7"),
            new(8,  "GND",       PinFunction.Ground,        null,       "0V ground reference"),
            new(9,  "Y6_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 6"),
            new(10, "Y5_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 5"),
            new(11, "Y4_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 4"),
            new(12, "Y3_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 3"),
            new(13, "Y2_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 2"),
            new(14, "Y1_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 1"),
            new(15, "Y0_N",      PinFunction.DigitalOutput, HcCmosWide, "Active-LOW decoded output 0"),
            new(16, "VCC",       PinFunction.PowerVcc,      HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC138-TRAP-01", "Triple Enable Polarity Mismatch (Pins 4, 5, 6)", WiringTrapSeverity.LogicFailure, [4, 5, 6],
                "74HC138 requires E1_N (Pin 4) = LOW, E2_N (Pin 5) = LOW, AND E3 (Pin 6) = HIGH simultaneously. Tying all three enable pins to VCC or all three to GND disables the chip (all Y0_N..Y7_N stay HIGH).",
                "Verify Pin 4 and Pin 5 are LOW while Pin 6 is HIGH."),
            new("74HC138-TRAP-02", "Assuming Active-HIGH Outputs Instead of Active-LOW", WiringTrapSeverity.LogicFailure, [7, 9, 10, 11, 12, 13, 14, 15],
                "All 8 outputs (Y0_N..Y7_N) are active-LOW: the selected channel goes LOW (0V) while the other 7 stay HIGH.",
                "Info check when LEDs are wired cathode-to-GND instead of current-sinking from VCC.")
        ]);

    // =========================================================================
    // 11. 74HC4051 - 8-Channel Analog Multiplexer / Demultiplexer
    // =========================================================================
    private static ComponentPinoutProfile Build74HC4051() => new(
        PartNumber: "74HC4051",
        Aliases: ["CD4051", "CD4051BE", "SN74HC4051N"],
        Category: "Analog / Mixed-Signal IC - Multiplexer",
        Description: "8-Channel Single-Pole Octal-Throw Analog Multiplexer / Demultiplexer",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Analog Channel Y4.",
        SupplyVoltage: HcCmosWide,
        LogicVoltage: HcCmosWide,
        Pins:
        [
            new(1,  "Y4",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 4"),
            new(2,  "Y6",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 6"),
            new(3,  "Z (COM)",PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Common analog/digital input/output"),
            new(4,  "Y7",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 7"),
            new(5,  "Y5",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 5"),
            new(6,  "E_N (INH)",PinFunction.DigitalInput | PinFunction.ActiveLowEnable, HcCmosWide, "Active-LOW enable (inhibit); tie to GND to enable channel routing"),
            new(7,  "VEE",   PinFunction.Ground, null, "Negative analog supply voltage; MUST be tied to GND (Pin 8) for single-supply 0-5V signals"),
            new(8,  "GND",   PinFunction.Ground, null, "0V digital ground reference"),
            new(9,  "S2 (C)",PinFunction.DigitalInput, HcCmosWide, "Digital channel select input 2 (MSB)"),
            new(10, "S1 (B)",PinFunction.DigitalInput, HcCmosWide, "Digital channel select input 1"),
            new(11, "S0 (A)",PinFunction.DigitalInput, HcCmosWide, "Digital channel select input 0 (LSB)"),
            new(12, "Y3",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 3"),
            new(13, "Y0",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 0"),
            new(14, "Y1",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 1"),
            new(15, "Y2",    PinFunction.AnalogInput | PinFunction.AnalogOutput | PinFunction.DigitalBidirectional, HcCmosWide, "Independent analog/digital channel 2"),
            new(16, "VCC",   PinFunction.PowerVcc, HcCmosWide, "Positive supply voltage (2.0V to 6.0V)")
        ],
        CommonWiringTraps:
        [
            new("74HC4051-TRAP-01", "Leaving VEE (Pin 7) Floating in Single-Supply Circuits", WiringTrapSeverity.LogicFailure, [7, 8],
                "Pin 7 (VEE) is the negative analog switch substrate rail. Leaving Pin 7 unconnected causes severe analog distortion and switch latch-up. For unipolar 0V-5V signals, Pin 7 must be shorted to Pin 8 (GND).",
                "Require Pin 7 (VEE) to be connected to GND (Pin 8) or a valid negative supply rail.")
        ]);

    // =========================================================================
    // 12. CD4511 - BCD-to-7-Segment Latch/Decoder/Driver
    // =========================================================================
    private static ComponentPinoutProfile BuildCD4511() => new(
        PartNumber: "CD4511",
        Aliases: ["CD4511BE", "HEF4511BP", "MC14511B"],
        Category: "Digital IC - Display Driver",
        Description: "BCD-to-7-Segment Latch / Decoder / Bipolar NPN High-Current Driver (Common-Cathode)",
        PinCount: 16,
        WidthCategory: DipWidthCategory.Narrow300Mil,
        RowSpacingInches: 0.3,
        Pin1Marker: Pin1OrientationMarker.NotchAndDot,
        Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is BCD Input B (D1).",
        SupplyVoltage: Cd4000BSeries,
        LogicVoltage: Cd4000BSeries,
        Pins:
        [
            new(1,  "B (D1)",   PinFunction.DigitalInput,  Cd4000BSeries, "BCD input B (weight 2)"),
            new(2,  "C (D2)",   PinFunction.DigitalInput,  Cd4000BSeries, "BCD input C (weight 4)"),
            new(3,  "LT_N",     PinFunction.DigitalInput | PinFunction.ActiveLowEnable, Cd4000BSeries, "Lamp Test (active-LOW); MUST be tied HIGH to VDD for normal operation"),
            new(4,  "BI_N",     PinFunction.DigitalInput | PinFunction.ActiveLowEnable, Cd4000BSeries, "Blanking Input (active-LOW); MUST be tied HIGH to VDD to turn display on"),
            new(5,  "LE (ST)",  PinFunction.DigitalInput | PinFunction.ActiveLowEnable, Cd4000BSeries, "Latch Enable / Strobe (LOW = transparent, HIGH = latched); tie to GND for real-time decoding"),
            new(6,  "D (D3)",   PinFunction.DigitalInput,  Cd4000BSeries, "BCD input D (weight 8, MSB)"),
            new(7,  "A (D0)",   PinFunction.DigitalInput,  Cd4000BSeries, "BCD input A (weight 1, LSB)"),
            new(8,  "VSS (GND)",PinFunction.Ground,        null,          "0V ground reference"),
            new(9,  "SEG_E",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output E"),
            new(10, "SEG_D",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output D"),
            new(11, "SEG_C",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output C"),
            new(12, "SEG_B",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output B"),
            new(13, "SEG_A",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output A"),
            new(14, "SEG_G",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output G"),
            new(15, "SEG_F",    PinFunction.DigitalOutput, Cd4000BSeries, "7-segment output F"),
            new(16, "VDD",      PinFunction.PowerVdd,      Cd4000BSeries, "Positive supply voltage (3.0V to 15.0V)")
        ],
        CommonWiringTraps:
        [
            new("CD4511-TRAP-01", "Control Pins LT_N (Pin 3), BI_N (Pin 4), and LE (Pin 5) Tied Together", WiringTrapSeverity.LogicFailure, [3, 4, 5],
                "LT_N (Pin 3) and BI_N (Pin 4) must be tied HIGH to VDD, whereas LE (Pin 5) must be tied LOW to GND for transparent operation. Tying all three to GND forces Lamp Test (all 8s), while tying all three to VDD freezes the latch.",
                "Require Pins 3 and 4 tied HIGH (VDD) and Pin 5 tied LOW (GND) or driven by an MCU GPIO."),
            new("CD4511-TRAP-02", "Missing Current-Limiting Resistors on Segment Outputs (Pins 9-15)", WiringTrapSeverity.CriticalDamageRisk, [9, 10, 11, 12, 13, 14, 15],
                "Unlike CD4026, CD4511 uses bipolar NPN emitter-follower outputs capable of sourcing 25mA+ without internal current limiting. Direct connection to a 7-segment LED burns out segments.",
                "Require series current-limiting resistors (220 Ohm - 1k Ohm) on every connected segment pin (Pins 9-15).")
        ]);

    // =========================================================================
    // 13. NE555 - Precision Timer IC
    // =========================================================================
    private static ComponentPinoutProfile BuildNE555()
    {
        var supply = new VoltageRange(4.5, 5.0, 16.0, 18.0, true);
        return new(
            PartNumber: "NE555",
            Aliases: ["NE555P", "LM555", "LM555CN", "SA555", "NA555", "TLC555", "LMC555"],
            Category: "Analog & Mixed-Signal IC - Timer",
            Description: "Precision Bipolar / CMOS Single Timer for Monostable and Astable Oscillation",
            PinCount: 8,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge and circular dimple next to Pin 1 (GND).",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1, "GND",       PinFunction.Ground,        null,   "0V ground reference"),
                new(2, "TRIG",      PinFunction.AnalogInput,   supply, "Trigger comparator input (< 1/3 VCC sets flip-flop and drives OUT HIGH)"),
                new(3, "OUT",       PinFunction.DigitalOutput | PinFunction.PwmOutput, supply, "Push-pull timer output (totem-pole up to 200mA on bipolar NE555)"),
                new(4, "RESET_N",   PinFunction.DigitalInput | PinFunction.ActiveLowReset, supply, "Active-LOW reset; MUST be tied to VCC (Pin 8) if not used"),
                new(5, "CTRL (CV)", PinFunction.AnalogInput | PinFunction.VoltageReference, supply, "Control Voltage (2/3 VCC internal divider tap); bypass to GND via 10nF ceramic cap"),
                new(6, "THRES",     PinFunction.AnalogInput,   supply, "Threshold comparator input (> 2/3 VCC resets flip-flop and drives OUT LOW)"),
                new(7, "DISCH",     PinFunction.OpenCollectorOut, supply, "Open-collector discharge transistor output for timing capacitor"),
                new(8, "VCC",       PinFunction.PowerVcc,      supply, "Positive supply voltage (4.5V to 16.0V for bipolar NE555; 2.0V to 15.0V for CMOS TLC555)")
            ],
            CommonWiringTraps:
            [
                new("NE555-TRAP-01", "Direct Connection of DISCH (Pin 7) to VCC Without Series Resistor RA", WiringTrapSeverity.CriticalDamageRisk, [7, 8],
                    "Pin 7 is an open-collector NPN transistor to GND. Connecting Pin 7 directly to VCC (or shorting a potentiometer across Pin 7 and Pin 8 to 0 Ohms) shorts VCC to GND through the internal discharge transistor when the timer state flips LOW, destroying the IC.",
                    "Require at least 470 Ohm series resistance between Pin 7 (DISCH) and Pin 8 (VCC)."),
                new("NE555-TRAP-02", "Floating RESET_N (Pin 4)", WiringTrapSeverity.LogicFailure, [4],
                    "Leaving Pin 4 (RESET_N) unconnected allows noise below 0.7V to randomly reset the oscillator.",
                    "Require Pin 4 tied to VCC (Pin 8) or driven by a valid digital signal."),
                new("NE555-TRAP-03", "Reversed Polarity on Electrolytic Timing Capacitor at Pin 2/6", WiringTrapSeverity.CriticalDamageRisk, [1, 2, 6],
                    "Installing the electrolytic timing capacitor with its negative lead on Pin 2/6 and positive lead on GND causes leakage and prevents the capacitor voltage from reaching the 2/3 VCC threshold.",
                    "Verify timing capacitor positive terminal is on the Pin 2/6 node and negative stripe is on GND.")
            ]);
    }

    // =========================================================================
    // 14. LM358 - Dual Operational Amplifier
    // =========================================================================
    private static ComponentPinoutProfile BuildLM358()
    {
        var supply = new VoltageRange(3.0, 5.0, 32.0, 32.0, true);
        return new(
            PartNumber: "LM358",
            Aliases: ["LM358P", "LM358N", "TL072", "MCP6002", "NE5532"],
            Category: "Analog IC - Operational Amplifier",
            Description: "Dual Low-Power Single-Supply Operational Amplifier (Ground-Sensing Input)",
            PinCount: 8,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Output A (1OUT).",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1, "1OUT",     PinFunction.AnalogOutput, supply, "Op-Amp 1 output (swings 0V to VCC - 1.5V)"),
                new(2, "1IN-",     PinFunction.AnalogInput,  supply, "Op-Amp 1 inverting input"),
                new(3, "1IN+",     PinFunction.AnalogInput,  supply, "Op-Amp 1 non-inverting input"),
                new(4, "GND (V-)", PinFunction.Ground,       null,   "Negative supply or 0V ground"),
                new(5, "2IN+",     PinFunction.AnalogInput,  supply, "Op-Amp 2 non-inverting input"),
                new(6, "2IN-",     PinFunction.AnalogInput,  supply, "Op-Amp 2 inverting input"),
                new(7, "2OUT",     PinFunction.AnalogOutput, supply, "Op-Amp 2 output"),
                new(8, "VCC (V+)", PinFunction.PowerVcc,     supply, "Positive supply voltage (3.0V to 32.0V)")
            ],
            CommonWiringTraps:
            [
                new("LM358-TRAP-01", "Expecting Rail-to-Rail Output Swing Up to VCC", WiringTrapSeverity.LogicFailure, [1, 7, 8],
                    "LM358 is NOT a rail-to-rail output op-amp. Its upper output saturates at approximately (VCC - 1.5V), i.e., ~3.5V on a 5V rail and ~1.8V on a 3.3V rail.",
                    "Warn when LM358 is powered from 3.3V or expected to drive 5V CMOS logic thresholds without level shifting."),
                new("LM358-TRAP-02", "Unused Op-Amp Section Left completely Floating", WiringTrapSeverity.Warning, [5, 6, 7],
                    "Leaving an unused op-amp channel with floating inputs causes saturation or high-frequency oscillation that couples into the active channel.",
                    "Recommend wiring unused op-amp as a unity-gain follower (2OUT tied to 2IN-) with 2IN+ tied to a mid-supply reference or GND.")
            ]);
    }

    // =========================================================================
    // 15. LM324 - Quad Operational Amplifier
    // =========================================================================
    private static ComponentPinoutProfile BuildLM324()
    {
        var supply = new VoltageRange(3.0, 5.0, 32.0, 32.0, true);
        return new(
            PartNumber: "LM324",
            Aliases: ["LM324N", "LM324AN", "TL074", "MCP6004"],
            Category: "Analog IC - Operational Amplifier",
            Description: "Quadruple Low-Power Single-Supply Operational Amplifier",
            PinCount: 14,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Output 1 (1OUT).",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1,  "1OUT",     PinFunction.AnalogOutput, supply, "Op-Amp 1 output"),
                new(2,  "1IN-",     PinFunction.AnalogInput,  supply, "Op-Amp 1 inverting input"),
                new(3,  "1IN+",     PinFunction.AnalogInput,  supply, "Op-Amp 1 non-inverting input"),
                new(4,  "VCC (V+)", PinFunction.PowerVcc,     supply, "Positive supply voltage (Note: center-left Pin 4!)"),
                new(5,  "2IN+",     PinFunction.AnalogInput,  supply, "Op-Amp 2 non-inverting input"),
                new(6,  "2IN-",     PinFunction.AnalogInput,  supply, "Op-Amp 2 inverting input"),
                new(7,  "2OUT",     PinFunction.AnalogOutput, supply, "Op-Amp 2 output"),
                new(8,  "3OUT",     PinFunction.AnalogOutput, supply, "Op-Amp 3 output"),
                new(9,  "3IN-",     PinFunction.AnalogInput,  supply, "Op-Amp 3 inverting input"),
                new(10, "3IN+",     PinFunction.AnalogInput,  supply, "Op-Amp 3 non-inverting input"),
                new(11, "GND (V-)", PinFunction.Ground,       null,   "Negative supply or 0V ground (Note: center-right Pin 11!)"),
                new(12, "4IN+",     PinFunction.AnalogInput,  supply, "Op-Amp 4 non-inverting input"),
                new(13, "4IN-",     PinFunction.AnalogInput,  supply, "Op-Amp 4 inverting input"),
                new(14, "4OUT",     PinFunction.AnalogOutput, supply, "Op-Amp 4 output")
            ],
            CommonWiringTraps:
            [
                new("LM324-TRAP-01", "Non-Corner Power Pins (VCC on Pin 4, GND on Pin 11)", WiringTrapSeverity.CriticalDamageRisk, [1, 4, 7, 11, 14],
                    "Unlike 14-pin logic ICs (which use Pin 14 = VCC, Pin 7 = GND), the LM324 places VCC on Pin 4 and GND on Pin 11. Applying power to Pins 14 and 7 drives power directly into Op-Amp 4 Output (Pin 14) and Op-Amp 2 Output (Pin 7)!",
                    "CRITICAL: Flag any power rail connected to Pin 7 or Pin 14; enforce VCC on Pin 4 and GND on Pin 11.")
            ]);
    }

    // =========================================================================
    // 16. LM393 - Dual Differential Comparator
    // =========================================================================
    private static ComponentPinoutProfile BuildLM393()
    {
        var supply = new VoltageRange(2.0, 5.0, 36.0, 36.0, true);
        return new(
            PartNumber: "LM393",
            Aliases: ["LM393P", "LM393N", "LM2903"],
            Category: "Analog IC - Voltage Comparator",
            Description: "Dual Independent Precision Voltage Comparator with Open-Collector Outputs",
            PinCount: 8,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Comparator 1 Output (1OUT).",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1, "1OUT",     PinFunction.DigitalOutput | PinFunction.OpenCollectorOut, supply, "Comparator 1 open-collector output (REQUIRES external pull-up resistor)"),
                new(2, "1IN-",     PinFunction.AnalogInput,  supply, "Comparator 1 inverting input"),
                new(3, "1IN+",     PinFunction.AnalogInput,  supply, "Comparator 1 non-inverting input"),
                new(4, "GND",      PinFunction.Ground,       null,   "0V ground reference"),
                new(5, "2IN+",     PinFunction.AnalogInput,  supply, "Comparator 2 non-inverting input"),
                new(6, "2IN-",     PinFunction.AnalogInput,  supply, "Comparator 2 inverting input"),
                new(7, "2OUT",     PinFunction.DigitalOutput | PinFunction.OpenCollectorOut, supply, "Comparator 2 open-collector output (REQUIRES external pull-up resistor)"),
                new(8, "VCC",      PinFunction.PowerVcc,     supply, "Positive supply voltage (2.0V to 36.0V)")
            ],
            CommonWiringTraps:
            [
                new("LM393-TRAP-01", "Missing Pull-Up Resistor on Open-Collector Output (Pins 1, 7)", WiringTrapSeverity.LogicFailure, [1, 7],
                    "LM393 shares the exact same pinout as the LM358 op-amp, so users expect push-pull outputs. However, LM393 outputs are open-collector NPN transistors that can only sink current to GND. Without a pull-up resistor (typically 1k-10k Ohm to VCC), the output never goes HIGH.",
                    "Require an external pull-up resistor to VCC (or MCU internal pull-up) on Pin 1 and/or Pin 7.")
            ]);
    }

    // =========================================================================
    // 17. LM386 - Low-Voltage Audio Power Amplifier
    // =========================================================================
    private static ComponentPinoutProfile BuildLM386()
    {
        var supply = new VoltageRange(4.0, 9.0, 12.0, 15.0, true);
        return new(
            PartNumber: "LM386",
            Aliases: ["LM386N-1", "LM386N-3", "LM386N-4", "JRC386D"],
            Category: "Analog IC - Audio Amplifier",
            Description: "Low-Voltage Audio Power Amplifier with Internal 20x to 200x Gain Biasing",
            PinCount: 8,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is GAIN.",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1, "GAIN_1",  PinFunction.AnalogInput,  supply, "Gain setting pin 1 (open = 20x gain; 10uF cap between Pin 1 and Pin 8 = 200x gain)"),
                new(2, "IN-",     PinFunction.AnalogInput,  supply, "Inverting audio input (typically tied to GND)"),
                new(3, "IN+",     PinFunction.AnalogInput,  supply, "Non-inverting audio input (typically fed via 10k volume potentiometer)"),
                new(4, "GND",     PinFunction.Ground,       null,   "0V ground reference"),
                new(5, "VOUT",    PinFunction.AnalogOutput, supply, "Speaker output (requires 220uF-470uF DC-blocking coupling capacitor + Zobel network)"),
                new(6, "VS (VCC)",PinFunction.PowerVcc,     supply, "Positive power supply (4.0V to 12.0V; Note: Pin 6, NOT Pin 8!)"),
                new(7, "BYPASS",  PinFunction.AnalogInput,  supply, "Internal 15k voltage divider bypass (connect 100nF-10uF capacitor to GND for power rail noise rejection)"),
                new(8, "GAIN_8",  PinFunction.AnalogInput,  supply, "Gain setting pin 8 (do NOT connect VCC here!)")
            ],
            CommonWiringTraps:
            [
                new("LM386-TRAP-01", "Applying VCC to Pin 8 Instead of Pin 6", WiringTrapSeverity.CriticalDamageRisk, [6, 8],
                    "Because most 8-pin DIP ICs (NE555, LM358, LM393) use Pin 8 for VCC, beginners wire 9V or 5V directly to Pin 8 on the LM386. On the LM386, Pin 8 is GAIN and Pin 6 is VS (VCC)!",
                    "CRITICAL: Flag any power rail connected to Pin 8; enforce VS supply on Pin 6."),
                new("LM386-TRAP-02", "Speaker Connected Directly to Pin 5 Without DC-Blocking Capacitor", WiringTrapSeverity.CriticalDamageRisk, [5],
                    "Pin 5 biases to VS/2 DC (~2.5V or ~4.5V). Connecting an 8-Ohm speaker directly from Pin 5 to GND draws 300-500mA continuous DC current, overheating both the LM386 and the speaker voice coil.",
                    "Require a series coupling capacitor (100uF-470uF, positive lead toward Pin 5) between Pin 5 and the speaker.")
            ]);
    }

    // =========================================================================
    // 18. LM317 - 3-Terminal Adjustable Positive Linear Voltage Regulator
    // =========================================================================
    private static ComponentPinoutProfile BuildLM317()
    {
        var vinRange = new VoltageRange(4.2, 12.0, 40.0, 40.0, true);
        var voutRange = new VoltageRange(1.25, 5.0, 37.0, 37.0, true);
        return new(
            PartNumber: "LM317",
            Aliases: ["LM317T", "LM317LZ"],
            Category: "Power IC - Adjustable Linear Voltage Regulator",
            Description: "1.25V to 37V 1.5A Adjustable 3-Terminal Positive Voltage Regulator (TO-220)",
            PinCount: 3,
            WidthCategory: DipWidthCategory.SingleInLine100Mil,
            RowSpacingInches: 0.1,
            Pin1Marker: Pin1OrientationMarker.MetalHeatsinkTabBack,
            Pin1MarkerDescription: "Looking at front plastic face with metal heatsink tab at the back and leads pointing down: Left=Pin 1 (ADJ), Center=Pin 2 (VOUT / Tab), Right=Pin 3 (VIN).",
            SupplyVoltage: vinRange,
            LogicVoltage: voutRange,
            Pins:
            [
                new(1, "ADJ",  PinFunction.AnalogInput | PinFunction.VoltageReference, voutRange, "Adjustment terminal (maintains 1.25V nominal reference between VOUT and ADJ)"),
                new(2, "VOUT", PinFunction.RegulatedVoltageOut, voutRange, "Regulated positive voltage output (internally connected to metal TO-220 heatsink tab)"),
                new(3, "VIN",  PinFunction.PowerVcc, vinRange, "Unregulated DC voltage input (must be >= VOUT + 2.5V dropout margin)")
            ],
            CommonWiringTraps:
            [
                new("LM317-TRAP-01", "Confusing LM317 Pinout (ADJ-VOUT-VIN) with L7805 Pinout (VIN-GND-VOUT)", WiringTrapSeverity.CriticalDamageRisk, [1, 2, 3],
                    "L78xx fixed regulators have Pin 1 = VIN, Pin 2 = GND, Pin 3 = VOUT. The LM317 has Pin 1 = ADJ, Pin 2 = VOUT, Pin 3 = VIN! Plugging an LM317 into a 7805 footprint grounds VOUT and feeds VIN into ADJ.",
                    "Verify VIN is connected to Pin 3 (Right pin), VOUT is taken from Pin 2 (Center pin), and R1 (typically 240 Ohm) connects between Pin 2 and Pin 1."),
                new("LM317-TRAP-02", "Grounding the TO-220 Metal Heatsink Tab", WiringTrapSeverity.CriticalDamageRisk, [2],
                    "On the LM317, the metal TO-220 tab is electrically connected to Pin 2 (VOUT), NOT Ground! Shorting the tab to ground shorts the regulator output.",
                    "Warn if the heatsink tab is tied to GND.")
            ]);
    }

    // =========================================================================
    // 19. L7805 - 5.0V Fixed Positive Linear Voltage Regulator
    // =========================================================================
    private static ComponentPinoutProfile BuildL7805()
    {
        var vinRange = new VoltageRange(7.0, 9.0, 25.0, 35.0, true);
        return new(
            PartNumber: "L7805",
            Aliases: ["LM7805", "L7805CV", "MC7805", "UA7805", "7805"],
            Category: "Power IC - Fixed Linear Voltage Regulator",
            Description: "Fixed 5.0V 1.5A 3-Terminal Positive Linear Voltage Regulator (TO-220)",
            PinCount: 3,
            WidthCategory: DipWidthCategory.SingleInLine100Mil,
            RowSpacingInches: 0.1,
            Pin1Marker: Pin1OrientationMarker.MetalHeatsinkTabBack,
            Pin1MarkerDescription: "Looking at front plastic face with metal heatsink tab at the back and leads pointing down: Left=Pin 1 (VIN), Center=Pin 2 (GND / Tab), Right=Pin 3 (VOUT).",
            SupplyVoltage: vinRange,
            LogicVoltage: Ttl5V,
            Pins:
            [
                new(1, "VIN",  PinFunction.PowerVcc, vinRange, "Unregulated DC input (7.0V minimum for dropout margin; 9V-12V recommended)"),
                new(2, "GND",  PinFunction.Ground,   null,     "0V common ground reference (connected to metal TO-220 tab)"),
                new(3, "VOUT", PinFunction.RegulatedVoltageOut, Ttl5V, "Regulated +5.0V DC output")
            ],
            CommonWiringTraps:
            [
                new("L7805-TRAP-01", "Insufficient Input Voltage (< 7.0V Dropout Margin)", WiringTrapSeverity.LogicFailure, [1, 3],
                    "L7805 is a standard bipolar regulator with a ~2.0V dropout voltage. Feeding 5V or 6V (e.g. 4x AA batteries) into Pin 1 produces an unstable ~3.8V-4.3V output.",
                    "Require VIN >= 7.0V on Pin 1."),
                new("L7805-TRAP-02", "Missing Input (0.33uF) and Output (0.1uF) Stability Capacitors", WiringTrapSeverity.Warning, [1, 2, 3],
                    "Without decoupling capacitors close to the pins across VIN-GND and VOUT-GND, the 7805 can oscillate at high frequencies on long breadboard jumper leads.",
                    "Recommend decoupling capacitors from Pin 1 to Pin 2 and Pin 3 to Pin 2."),
                new("L7805-TRAP-03", "Plugging All 3 Pins Into the Same 5-Hole Breadboard Terminal Strip Row", WiringTrapSeverity.CriticalDamageRisk, [1, 2, 3],
                    "Orienting the TO-220 package parallel to a single 5-hole breadboard row shorts VIN, GND, and VOUT together instantly.",
                    "Ensure Pins 1, 2, and 3 occupy three distinct terminal strip rows.")
            ]);
    }

    // =========================================================================
    // 20. MCP3008 - 8-Channel 10-Bit SPI ADC
    // =========================================================================
    private static ComponentPinoutProfile BuildMCP3008()
    {
        var supply = new VoltageRange(2.7, 3.3, 5.5, 6.5, true);
        return new(
            PartNumber: "MCP3008",
            Aliases: ["MCP3008-I/P", "MCP3208"],
            Category: "Analog & Mixed-Signal IC - ADC",
            Description: "8-Channel 10-Bit SAR Analog-to-Digital Converter with SPI Serial Interface",
            PinCount: 16,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Analog Channel CH0.",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1,  "CH0",        PinFunction.AnalogInput, supply, "Analog input channel 0 (0V to VREF)"),
                new(2,  "CH1",        PinFunction.AnalogInput, supply, "Analog input channel 1 (0V to VREF)"),
                new(3,  "CH2",        PinFunction.AnalogInput, supply, "Analog input channel 2 (0V to VREF)"),
                new(4,  "CH3",        PinFunction.AnalogInput, supply, "Analog input channel 3 (0V to VREF)"),
                new(5,  "CH4",        PinFunction.AnalogInput, supply, "Analog input channel 4 (0V to VREF)"),
                new(6,  "CH5",        PinFunction.AnalogInput, supply, "Analog input channel 5 (0V to VREF)"),
                new(7,  "CH6",        PinFunction.AnalogInput, supply, "Analog input channel 6 (0V to VREF)"),
                new(8,  "CH7",        PinFunction.AnalogInput, supply, "Analog input channel 7 (0V to VREF)"),
                new(9,  "DGND",       PinFunction.Ground,      null,   "Digital ground reference (MUST be connected to AGND)"),
                new(10, "CS_N/SHDN",  PinFunction.DigitalInput | PinFunction.SpiCs | PinFunction.ActiveLowEnable, supply, "SPI Chip Select (active-LOW) / Shutdown input"),
                new(11, "DIN (MOSI)", PinFunction.DigitalInput | PinFunction.SpiMosi, supply, "SPI Serial Data In (connect to MCU MOSI)"),
                new(12, "DOUT (MISO)",PinFunction.DigitalOutput | PinFunction.SpiMiso, supply, "SPI Serial Data Out (connect to MCU MISO)"),
                new(13, "CLK (SCK)",  PinFunction.DigitalInput | PinFunction.SpiSck | PinFunction.ClockInput, supply, "SPI Serial Clock input"),
                new(14, "AGND",       PinFunction.Ground,      null,   "Analog ground reference (MUST be connected to DGND)"),
                new(15, "VREF",       PinFunction.AnalogInput | PinFunction.VoltageReference, supply, "Analog voltage reference input (typically tied to VDD; MUST NOT be left floating!)"),
                new(16, "VDD",        PinFunction.PowerVdd,    supply, "Positive supply voltage (2.7V to 5.5V)")
            ],
            CommonWiringTraps:
            [
                new("MCP3008-TRAP-01", "Leaving VREF (Pin 15) or AGND (Pin 14) Unconnected", WiringTrapSeverity.LogicFailure, [9, 14, 15, 16],
                    "Users often connect VDD (Pin 16) and DGND (Pin 9) but forget VREF (Pin 15) and AGND (Pin 14), causing the ADC to return all 1023s or random noise.",
                    "Require both Pin 9 (DGND) and Pin 14 (AGND) tied to Ground, and Pin 15 (VREF) connected to a reference voltage <= VDD (usually tied to VDD)."),
                new("MCP3008-TRAP-02", "Powering MCP3008 VDD with 5V When Connected to a 3.3V-Only MCU (Raspberry Pi / RP2040 / ESP32)", WiringTrapSeverity.CriticalDamageRisk, [12, 16],
                    "When VDD (Pin 16) is 5.0V, DOUT (Pin 12) drives 5.0V logic into the MCU's MISO pin, damaging non-5V-tolerant 3.3V GPIOs on Raspberry Pi, Pico, or ESP32.",
                    "Flag if MCP3008 VDD is 5V while SPI DOUT (Pin 12) connects directly to a 3.3V-only MCU without a level shifter.")
            ]);
    }

    // =========================================================================
    // 21. PCF8574 - 8-Bit I2C Quasi-Bidirectional I/O Expander
    // =========================================================================
    private static ComponentPinoutProfile BuildPCF8574()
    {
        var supply = new VoltageRange(2.5, 3.3, 6.0, 7.0, true);
        return new(
            PartNumber: "PCF8574",
            Aliases: ["PCF8574P", "PCF8574N", "PCF8574AP"],
            Category: "Digital IC - I2C I/O Expander",
            Description: "Remote 8-Bit I2C Quasi-Bidirectional I/O Expander with Interrupt Output",
            PinCount: 16,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Address Select A0.",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1,  "A0",    PinFunction.DigitalInput, supply, "I2C hardware address bit 0 (tie to VDD or VSS; do NOT float)"),
                new(2,  "A1",    PinFunction.DigitalInput, supply, "I2C hardware address bit 1 (tie to VDD or VSS; do NOT float)"),
                new(3,  "A2",    PinFunction.DigitalInput, supply, "I2C hardware address bit 2 (tie to VDD or VSS; do NOT float)"),
                new(4,  "P0",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 0"),
                new(5,  "P1",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 1"),
                new(6,  "P2",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 2"),
                new(7,  "P3",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 3"),
                new(8,  "VSS",   PinFunction.Ground,       null,   "0V ground reference"),
                new(9,  "P4",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 4"),
                new(10, "P5",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 5"),
                new(11, "P6",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 6"),
                new(12, "P7",    PinFunction.DigitalBidirectional, supply, "Quasi-bidirectional I/O port bit 7"),
                new(13, "INT_N", PinFunction.DigitalOutput | PinFunction.OpenCollectorOut, supply, "Open-drain active-LOW interrupt output (requires pull-up if used)"),
                new(14, "SCL",   PinFunction.I2cScl | PinFunction.DigitalInput, supply, "I2C Serial Clock line (requires 2.2k-10k pull-up to VDD)"),
                new(15, "SDA",   PinFunction.I2cSda | PinFunction.DigitalBidirectional, supply, "I2C Serial Data line (requires 2.2k-10k pull-up to VDD)"),
                new(16, "VDD",   PinFunction.PowerVdd,     supply, "Positive supply voltage (2.5V to 6.0V)")
            ],
            CommonWiringTraps:
            [
                new("PCF8574-TRAP-01", "Floating Address Pins A0, A1, A2 (Pins 1-3)", WiringTrapSeverity.LogicFailure, [1, 2, 3],
                    "PCF8574 address pins have no internal pull-ups. Leaving A0-A2 floating causes the I2C slave address (0x20-0x27 for PCF8574, 0x38-0x3F for PCF8574A) to jump randomly mid-transaction.",
                    "Require Pins 1, 2, and 3 to be explicitly tied to either VDD or VSS."),
                new("PCF8574-TRAP-02", "Attempting to Source High Current from Quasi-Bidirectional Pins P0-P7", WiringTrapSeverity.LogicFailure, [4, 5, 6, 7, 9, 10, 11, 12],
                    "Pins P0-P7 can only source ~100uA when HIGH (weak internal current source) but can sink up to 25mA when LOW. LEDs must be wired active-LOW (anode to VDD via resistor, cathode to P0-P7).",
                    "Warn if an LED anode is driven from P0-P7 with cathode to GND.")
            ]);
    }

    // =========================================================================
    // 22. ULN2003A - 7-Channel High-Voltage High-Current Darlington Array
    // =========================================================================
    private static ComponentPinoutProfile BuildULN2003A()
    {
        var inputRange = new VoltageRange(0.0, 5.0, 30.0, 30.0, true);
        var loadRange = new VoltageRange(0.0, 12.0, 50.0, 50.0, true);
        return new(
            PartNumber: "ULN2003A",
            Aliases: ["ULN2003", "ULN2003AN", "ULN2003APG"],
            Category: "Power & Driver IC - Darlington Transistor Array",
            Description: "7-Channel 50V 500mA NPN Darlington Sink Driver with Integrated Freewheeling Clamp Diodes",
            PinCount: 16,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Base Input 1B.",
            SupplyVoltage: loadRange,
            LogicVoltage: inputRange,
            Pins:
            [
                new(1,  "1B (IN1)",  PinFunction.DigitalInput,      inputRange, "Channel 1 input (internal 2.7k series base resistor for 3.3V/5V CMOS/TTL)"),
                new(2,  "2B (IN2)",  PinFunction.DigitalInput,      inputRange, "Channel 2 input"),
                new(3,  "3B (IN3)",  PinFunction.DigitalInput,      inputRange, "Channel 3 input"),
                new(4,  "4B (IN4)",  PinFunction.DigitalInput,      inputRange, "Channel 4 input"),
                new(5,  "5B (IN5)",  PinFunction.DigitalInput,      inputRange, "Channel 5 input"),
                new(6,  "6B (IN6)",  PinFunction.DigitalInput,      inputRange, "Channel 6 input"),
                new(7,  "7B (IN7)",  PinFunction.DigitalInput,      inputRange, "Channel 7 input"),
                new(8,  "E (GND)",   PinFunction.Ground,            null,       "Common Emitter ground (MUST connect to both MCU GND and Load Supply GND)"),
                new(9,  "COM",       PinFunction.MotorSupplyVs,     loadRange,  "Common cathode node of internal flyback diodes (connect to positive inductive load supply +VM)"),
                new(10, "7C (OUT7)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 7 open-collector sink output"),
                new(11, "6C (OUT6)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 6 open-collector sink output"),
                new(12, "5C (OUT5)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 5 open-collector sink output"),
                new(13, "4C (OUT4)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 4 open-collector sink output"),
                new(14, "3C (OUT3)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 3 open-collector sink output"),
                new(15, "2C (OUT2)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 2 open-collector sink output"),
                new(16, "1C (OUT1)", PinFunction.OpenCollectorOut,  loadRange,  "Channel 1 open-collector sink output")
            ],
            CommonWiringTraps:
            [
                new("ULN2003A-TRAP-01", "Leaving COM (Pin 9) Disconnected When Driving Relays or Stepper Motors (28BYJ-48)", WiringTrapSeverity.CriticalDamageRisk, [9],
                    "Pin 9 connects the cathodes of all 7 internal flyback diodes to the positive motor/relay supply (+VM). Leaving Pin 9 unconnected leaves inductive kickback unclamped (> 50V spikes).",
                    "Warn if inductive loads are connected to 1C-7C without Pin 9 tied to the positive load supply rail."),
                new("ULN2003A-TRAP-02", "Expecting Outputs 1C-7C to Source Positive Voltage", WiringTrapSeverity.LogicFailure, [10, 11, 12, 13, 14, 15, 16],
                    "ULN2003A is a low-side current sink driver only. Loads must be connected between +V_LOAD and the output pin (1C-7C), never between 1C-7C and GND.",
                    "Flag any load wired between 1C-7C and GND.")
            ]);
    }

    // =========================================================================
    // 23. L293D - Quadruple Half-H DC / Stepper Motor Driver IC
    // =========================================================================
    private static ComponentPinoutProfile BuildL293D()
    {
        var logicVss = new VoltageRange(4.5, 5.0, 7.0, 36.0, true);
        var motorVs = new VoltageRange(4.5, 12.0, 36.0, 36.0, true);
        return new(
            PartNumber: "L293D",
            Aliases: ["L293DN", "L293NE", "SN754410", "SN754410NE"],
            Category: "Power & Driver IC - H-Bridge Motor Driver",
            Description: "Quadruple Half-H Driver (600mA/Channel) with Internal Clamp Diodes for Bidirectional DC/Stepper Motors",
            PinCount: 16,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Enable 1,2 (1,2EN).",
            SupplyVoltage: motorVs,
            LogicVoltage: logicVss,
            Pins:
            [
                new(1,  "1,2EN",     PinFunction.DigitalInput | PinFunction.PwmOutput | PinFunction.ActiveHighEnable, logicVss, "Enable driver channels 1 and 2 (active-HIGH or PWM speed control)"),
                new(2,  "1A (IN1)",  PinFunction.DigitalInput,  logicVss, "Driver 1 logic input"),
                new(3,  "1Y (OUT1)", PinFunction.DigitalOutput, motorVs,  "Driver 1 motor output"),
                new(4,  "GND_1",     PinFunction.Ground,        null,     "Ground and thermal heatsink tab pin 1"),
                new(5,  "GND_2",     PinFunction.Ground,        null,     "Ground and thermal heatsink tab pin 2"),
                new(6,  "2Y (OUT2)", PinFunction.DigitalOutput, motorVs,  "Driver 2 motor output"),
                new(7,  "2A (IN2)",  PinFunction.DigitalInput,  logicVss, "Driver 2 logic input"),
                new(8,  "VCC2 (VS)", PinFunction.MotorSupplyVs, motorVs,  "Motor power supply voltage (4.5V to 36.0V)"),
                new(9,  "3,4EN",     PinFunction.DigitalInput | PinFunction.PwmOutput | PinFunction.ActiveHighEnable, logicVss, "Enable driver channels 3 and 4 (active-HIGH or PWM speed control)"),
                new(10, "3A (IN3)",  PinFunction.DigitalInput,  logicVss, "Driver 3 logic input"),
                new(11, "3Y (OUT3)", PinFunction.DigitalOutput, motorVs,  "Driver 3 motor output"),
                new(12, "GND_3",     PinFunction.Ground,        null,     "Ground and thermal heatsink tab pin 3"),
                new(13, "GND_4",     PinFunction.Ground,        null,     "Ground and thermal heatsink tab pin 4"),
                new(14, "4Y (OUT4)", PinFunction.DigitalOutput, motorVs,  "Driver 4 motor output"),
                new(15, "4A (IN4)",  PinFunction.DigitalInput,  logicVss, "Driver 4 logic input"),
                new(16, "VCC1 (VSS)",PinFunction.PowerVcc,      logicVss, "5V internal logic supply voltage (4.5V to 7.0V)")
            ],
            CommonWiringTraps:
            [
                new("L293D-TRAP-01", "Swapping Logic Supply VCC1 (Pin 16) and Motor Supply VCC2 (Pin 8)", WiringTrapSeverity.CriticalDamageRisk, [8, 16],
                    "Pin 16 (VCC1/VSS in ST datasheet) is the 5V logic supply, while Pin 8 (VCC2/VS) is the high-voltage motor supply (up to 36V). Because CMOS chips use 'VSS' to mean Ground, students either ground Pin 16 or feed 12V-24V into Pin 16 and 5V into Pin 8.",
                    "Verify Pin 16 is connected to +5V logic rail and Pin 8 is connected to the motor battery/supply (+4.5V to +36V)."),
                new("L293D-TRAP-02", "Leaving Enable Pins 1,2EN (Pin 1) or 3,4EN (Pin 9) Floating", WiringTrapSeverity.LogicFailure, [1, 9],
                    "Drivers 1-2 and 3-4 remain tri-stated unless their respective enable pin (Pin 1 or Pin 9) is driven HIGH or with PWM.",
                    "Require Pin 1 (when using 1Y/2Y) and Pin 9 (when using 3Y/4Y) to be tied to 5V or an MCU GPIO."),
                new("L293D-TRAP-03", "Missing Common Ground Between Motor Battery and MCU", WiringTrapSeverity.LogicFailure, [4, 5, 8, 12, 13, 16],
                    "Using a separate 9V/12V battery for Pin 8 (VCC2) without tying its negative terminal to the breadboard GND (Pins 4, 5, 12, 13) prevents motor current return and logic reference.",
                    "Verify all 4 center ground pins (4, 5, 12, 13) share a common ground net with both MCU GND and Motor Supply negative.")
            ]);
    }

    // =========================================================================
    // 24. ATmega328P-PU - 8-Bit AVR Microcontroller (DIP-28 Bare Chip)
    // =========================================================================
    private static ComponentPinoutProfile BuildATmega328P()
    {
        var supply = new VoltageRange(1.8, 5.0, 5.5, 6.0, true);
        return new(
            PartNumber: "ATMEGA328P-PU",
            Aliases: ["ATMEGA328P", "ATMEGA328", "ATMEGA168P", "ATMEGA8A-PU"],
            Category: "Microcontroller IC - 8-Bit AVR",
            Description: "28-Pin Narrow DIP 8-Bit AVR RISC Microcontroller (Arduino Uno Core IC)",
            PinCount: 28,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is PC6 (RESET_N).",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1,  "PC6 (RESET_N)",      PinFunction.DigitalInput | PinFunction.ActiveLowReset, supply, "Active-LOW Reset (connect 10k pull-up resistor to VCC)"),
                new(2,  "PD0 (RXD / D0)",     PinFunction.DigitalBidirectional | PinFunction.UartRx, supply, "UART Receiver / Arduino Digital Pin 0"),
                new(3,  "PD1 (TXD / D1)",     PinFunction.DigitalBidirectional | PinFunction.UartTx, supply, "UART Transmitter / Arduino Digital Pin 1"),
                new(4,  "PD2 (INT0 / D2)",    PinFunction.DigitalBidirectional, supply, "External Interrupt 0 / Arduino Digital Pin 2"),
                new(5,  "PD3 (INT1 / D3~)",   PinFunction.DigitalBidirectional | PinFunction.PwmOutput, supply, "External Interrupt 1 / OC2B PWM / Arduino Digital Pin 3"),
                new(6,  "PD4 (D4)",           PinFunction.DigitalBidirectional, supply, "Arduino Digital Pin 4"),
                new(7,  "VCC",                PinFunction.PowerVcc,             supply, "Digital positive supply voltage (1.8V-5.5V; 5.0V required for 16MHz clock)"),
                new(8,  "GND_1",              PinFunction.Ground,               null,   "Digital ground reference (MUST tie to Pin 22 AGND)"),
                new(9,  "PB6 (XTAL1)",        PinFunction.ClockInput | PinFunction.DigitalBidirectional, supply, "Crystal oscillator input 1 (connect 16MHz crystal + 22pF cap to GND)"),
                new(10, "PB7 (XTAL2)",        PinFunction.ClockInput | PinFunction.DigitalBidirectional, supply, "Crystal oscillator output 2 (connect 16MHz crystal + 22pF cap to GND)"),
                new(11, "PD5 (D5~)",          PinFunction.DigitalBidirectional | PinFunction.PwmOutput, supply, "OC0B PWM / Arduino Digital Pin 5"),
                new(12, "PD6 (D6~)",          PinFunction.DigitalBidirectional | PinFunction.PwmOutput, supply, "OC0A PWM / Arduino Digital Pin 6"),
                new(13, "PD7 (D7)",           PinFunction.DigitalBidirectional, supply, "Arduino Digital Pin 7"),
                new(14, "PB0 (D8)",           PinFunction.DigitalBidirectional, supply, "Arduino Digital Pin 8"),
                new(15, "PB1 (D9~)",          PinFunction.DigitalBidirectional | PinFunction.PwmOutput, supply, "OC1A PWM / Arduino Digital Pin 9"),
                new(16, "PB2 (SS_N / D10~)",  PinFunction.DigitalBidirectional | PinFunction.SpiCs | PinFunction.PwmOutput, supply, "SPI Slave Select / OC1B PWM / Arduino Digital Pin 10"),
                new(17, "PB3 (MOSI / D11~)",  PinFunction.DigitalBidirectional | PinFunction.SpiMosi | PinFunction.PwmOutput, supply, "SPI MOSI / OC2A PWM / Arduino Digital Pin 11"),
                new(18, "PB4 (MISO / D12)",   PinFunction.DigitalBidirectional | PinFunction.SpiMiso, supply, "SPI MISO / Arduino Digital Pin 12"),
                new(19, "PB5 (SCK / D13)",    PinFunction.DigitalBidirectional | PinFunction.SpiSck, supply, "SPI SCK / Built-in LED / Arduino Digital Pin 13"),
                new(20, "AVCC",               PinFunction.PowerVcc,             supply, "Supply voltage for ADC and Port C; MUST be connected to VCC even if ADC is unused!"),
                new(21, "AREF",               PinFunction.AnalogInput | PinFunction.VoltageReference, supply, "Analog Reference pin for ADC (decouple to GND with 100nF cap; do NOT tie directly to VCC if using internal 1.1V ref)"),
                new(22, "GND_2 (AGND)",       PinFunction.Ground,               null,   "Analog/Port C ground reference (MUST be connected to Pin 8 GND)"),
                new(23, "PC0 (ADC0 / A0)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional, supply, "ADC Channel 0 / Arduino Analog Pin A0"),
                new(24, "PC1 (ADC1 / A1)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional, supply, "ADC Channel 1 / Arduino Analog Pin A1"),
                new(25, "PC2 (ADC2 / A2)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional, supply, "ADC Channel 2 / Arduino Analog Pin A2"),
                new(26, "PC3 (ADC3 / A3)",    PinFunction.AnalogInput | PinFunction.DigitalBidirectional, supply, "ADC Channel 3 / Arduino Analog Pin A3"),
                new(27, "PC4 (ADC4/SDA/A4)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.I2cSda, supply, "I2C SDA / ADC Channel 4 / Arduino Analog Pin A4"),
                new(28, "PC5 (ADC5/SCL/A5)",  PinFunction.AnalogInput | PinFunction.DigitalBidirectional | PinFunction.I2cScl, supply, "I2C SCL / ADC Channel 5 / Arduino Analog Pin A5")
            ],
            CommonWiringTraps:
            [
                new("ATMEGA328P-TRAP-01", "Leaving AVCC (Pin 20) or AGND (Pin 22) Unconnected", WiringTrapSeverity.CriticalDamageRisk, [7, 8, 20, 22],
                    "AVCC (Pin 20) powers all of Port C (A0-A5 and I2C) in addition to the ADC. Leaving Pin 20 or Pin 22 unconnected causes latch-up through internal protection diodes and erratic MCU resets.",
                    "Require both Pin 7 (VCC) and Pin 20 (AVCC) connected to +V, and both Pin 8 and Pin 22 connected to GND."),
                new("ATMEGA328P-TRAP-02", "Tying AREF (Pin 21) Directly to 5V VCC", WiringTrapSeverity.CriticalDamageRisk, [21],
                    "Tying Pin 21 (AREF) directly to 5V VCC is dangerous because calling analogReference(INTERNAL) shorts the internal 1.1V bandgap reference to the external 5V rail, destroying the AREF multiplexer.",
                    "Warn if Pin 21 is hardwired to VCC; recommend a 100nF decoupling capacitor from Pin 21 to GND instead.")
            ]);
    }

    // =========================================================================
    // 25. MAX7219 - 8-Digit LED Display / 8x8 Matrix Driver
    // =========================================================================
    private static ComponentPinoutProfile BuildMAX7219()
    {
        var supply = new VoltageRange(4.0, 5.0, 5.5, 6.0, true);
        return new(
            PartNumber: "MAX7219",
            Aliases: ["MAX7219CNG", "MAX7219ENG", "MAX7221", "MAX7221CNG"],
            Category: "Digital IC - LED Matrix & 7-Segment Driver",
            Description: "Serially Interfaced 8-Digit Common-Cathode LED Display / 64-LED Matrix Driver",
            PinCount: 24,
            WidthCategory: DipWidthCategory.Narrow300Mil,
            RowSpacingInches: 0.3,
            Pin1Marker: Pin1OrientationMarker.NotchAndDot,
            Pin1MarkerDescription: "Half-moon notch at top edge; Pin 1 is Serial Data Input (DIN).",
            SupplyVoltage: supply,
            LogicVoltage: supply,
            Pins:
            [
                new(1,  "DIN",       PinFunction.DigitalInput | PinFunction.SpiMosi, supply, "Serial data input (16-bit shift register)"),
                new(2,  "DIG_0",     PinFunction.DigitalOutput, supply, "Digit 0 cathode sink driver"),
                new(3,  "DIG_4",     PinFunction.DigitalOutput, supply, "Digit 4 cathode sink driver"),
                new(4,  "GND_1",     PinFunction.Ground,        null,   "Ground reference 1 (both Pins 4 and 9 MUST be grounded)"),
                new(5,  "DIG_6",     PinFunction.DigitalOutput, supply, "Digit 6 cathode sink driver"),
                new(6,  "DIG_2",     PinFunction.DigitalOutput, supply, "Digit 2 cathode sink driver"),
                new(7,  "DIG_3",     PinFunction.DigitalOutput, supply, "Digit 3 cathode sink driver"),
                new(8,  "DIG_7",     PinFunction.DigitalOutput, supply, "Digit 7 cathode sink driver"),
                new(9,  "GND_2",     PinFunction.Ground,        null,   "Ground reference 2 (both Pins 4 and 9 MUST be grounded)"),
                new(10, "DIG_5",     PinFunction.DigitalOutput, supply, "Digit 5 cathode sink driver"),
                new(11, "DIG_1",     PinFunction.DigitalOutput, supply, "Digit 1 cathode sink driver"),
                new(12, "LOAD (CS)", PinFunction.DigitalInput | PinFunction.SpiCs, supply, "Latch data on rising edge (SPI Chip Select)"),
                new(13, "CLK",       PinFunction.DigitalInput | PinFunction.SpiSck | PinFunction.ClockInput, supply, "Serial clock input (up to 10MHz)"),
                new(14, "SEG_A",     PinFunction.DigitalOutput, supply, "Segment A current-regulated source driver"),
                new(15, "SEG_F",     PinFunction.DigitalOutput, supply, "Segment F current-regulated source driver"),
                new(16, "SEG_B",     PinFunction.DigitalOutput, supply, "Segment B current-regulated source driver"),
                new(17, "SEG_G",     PinFunction.DigitalOutput, supply, "Segment G current-regulated source driver"),
                new(18, "ISET",      PinFunction.AnalogInput,   supply, "Peak segment current programming pin (connect 10k-68k resistor from Pin 18 to VCC Pin 19)"),
                new(19, "V+ (VCC)",  PinFunction.PowerVcc,      supply, "Positive 5.0V supply voltage"),
                new(20, "SEG_C",     PinFunction.DigitalOutput, supply, "Segment C current-regulated source driver"),
                new(21, "SEG_E",     PinFunction.DigitalOutput, supply, "Segment E current-regulated source driver"),
                new(22, "SEG_DP",    PinFunction.DigitalOutput, supply, "Segment Decimal Point (DP) current-regulated source driver"),
                new(23, "SEG_D",     PinFunction.DigitalOutput, supply, "Segment D current-regulated source driver"),
                new(24, "DOUT",      PinFunction.DigitalOutput | PinFunction.SpiMiso, supply, "Serial data output for daisy-chaining multiple MAX7219 chips")
            ],
            CommonWiringTraps:
            [
                new("MAX7219-TRAP-01", "Missing or Shorted R_SET Resistor Between ISET (Pin 18) and VCC (Pin 19)", WiringTrapSeverity.CriticalDamageRisk, [18, 19],
                    "Pin 18 (ISET) sets the peak LED segment current (approx. 100x the current flowing into Pin 18 from VCC). Connecting Pin 18 directly to VCC or to GND either destroys the LEDs/chip or turns the display completely off. Requires a resistor >= 9.53k Ohm (typically 10k-33k Ohm) between Pin 18 and Pin 19.",
                    "Require a resistor (9.5k-100k Ohm) connected between Pin 18 (ISET) and Pin 19 (V+)."),
                new("MAX7219-TRAP-02", "Only One Ground Pin Connected (Pin 4 or Pin 9)", WiringTrapSeverity.LogicFailure, [4, 9],
                    "Because MAX7219 sinks up to 320mA of multiplexed digit current, both Pin 4 and Pin 9 must be connected to GND along with a 10uF electrolytic + 100nF ceramic bypass capacitor close to Pin 19.",
                    "Require both Pin 4 and Pin 9 tied to GND.")
            ]);
    }
}
