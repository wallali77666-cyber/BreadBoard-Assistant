using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>
/// Electrical and functional classification for an integrated circuit or breakout IC pin.
/// </summary>
public enum ExpandedIcPinRole : byte
{
    PowerVdd = 0,
    GroundVss = 1,
    ResetActiveLow = 2,
    DigitalIo = 3,
    AnalogInput = 4,
    OscillatorClock = 5,
    I2cScl = 6,
    I2cSda = 7,
    SpiClock = 8,
    SpiDataIn = 9,
    SpiDataOut = 10,
    SpiChipSelect = 11,
    DarlingtonBaseInput = 12,
    DarlingtonCollectorOutput = 13,
    FlybackCommonCathode = 14,
    LedSegmentDrive = 15,
    LedDigitSink = 16,
    CurrentSetResistor = 17,
    HardwareAddressSelect = 18,
    InterruptOrAlertOutput = 19,
    TestOrJtag = 20,
    NoConnect = 21
}

/// <summary>
/// Immutable, zero-allocation pin specification for an integrated circuit in the expanded catalog.
/// </summary>
public sealed record ExpandedIcPinSpec(
    int PinNumber,
    string Name,
    ExpandedIcPinRole Role,
    string Description,
    IReadOnlyList<string> Aliases,
    bool Is5VTolerant = true)
{
    public bool IsPowerPin => Role == ExpandedIcPinRole.PowerVdd;

    public bool IsGroundPin => Role == ExpandedIcPinRole.GroundVss;
}

/// <summary>
/// Immutable, high-structure hardware specification for a DIP IC or multi-pin IC breakout.
/// </summary>
public sealed record ExpandedIcDefinition(
    string PartNumber,
    string DisplayName,
    string Manufacturer,
    string PackageType,
    int PinCount,
    double MinSupplyVoltage,
    double NominalSupplyVoltage,
    double MaxSupplyVoltage,
    bool Is5VTolerant,
    IReadOnlyList<ExpandedIcPinSpec> Pins,
    IReadOnlyList<string> PartAliases)
{
    private readonly FrozenDictionary<int, ExpandedIcPinSpec> _pinsByNumber =
        Pins.ToFrozenDictionary(static p => p.PinNumber);

    private readonly FrozenDictionary<string, ExpandedIcPinSpec> _pinsByName =
        BuildNameLookup(Pins);

    /// <summary>
    /// All power supply pins (VDD, DVCC, V+) on the integrated circuit.
    /// </summary>
    public IReadOnlyList<ExpandedIcPinSpec> PowerPins { get; } =
        Pins.Where(static p => p.IsPowerPin).ToArray();

    /// <summary>
    /// All ground reference pins (VSS, DVSS, GND, E) on the integrated circuit.
    /// </summary>
    public IReadOnlyList<ExpandedIcPinSpec> GroundPins { get; } =
        Pins.Where(static p => p.IsGroundPin).ToArray();

    /// <summary>
    /// Looks up a pin by 1-based physical pin number in O(1) time without allocations.
    /// </summary>
    public ExpandedIcPinSpec? GetPin(int pinNumber) =>
        _pinsByNumber.TryGetValue(pinNumber, out ExpandedIcPinSpec? spec) ? spec : null;

    /// <summary>
    /// Looks up a pin by canonical name or alias in O(1) time without allocations.
    /// </summary>
    public ExpandedIcPinSpec? FindPin(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
            return null;

        return _pinsByName.TryGetValue(nameOrAlias.Trim(), out ExpandedIcPinSpec? spec) ? spec : null;
    }

    /// <summary>
    /// Converts this expanded IC definition to the legacy <see cref="IcDefinition"/> used by <see cref="ElectricalRulesChecker"/>.
    /// </summary>
    public IcDefinition ToIcDefinition()
    {
        var vccPins = Pins.Where(p => p.IsPowerPin || p.Role == ExpandedIcPinRole.FlybackCommonCathode)
                          .Select(p => p.PinNumber).ToArray();
        var gndPins = GroundPins.Select(p => p.PinNumber).ToArray();
        var pinNames = Pins.ToDictionary(p => p.PinNumber, p => p.Name);

        int primaryVcc = vccPins.Length > 0 ? vccPins[0] : (FindPin("COM")?.PinNumber ?? PinCount);
        int primaryGnd = gndPins.Length > 0 ? gndPins[0] : (PinCount / 2);

        return new IcDefinition(
            PartNumber: PartNumber,
            PinCount: PinCount,
            PrimaryVccPin: primaryVcc,
            PrimaryGndPin: primaryGnd,
            AllVccPins: vccPins.Length > 0 ? vccPins : [primaryVcc],
            AllGndPins: gndPins.Length > 0 ? gndPins : [primaryGnd],
            Description: DisplayName,
            PinNames: pinNames);
    }

    /// <summary>
    /// Converts this expanded IC definition to a rich <see cref="ComponentPinoutProfile"/> for interactive visual inspection.
    /// </summary>
    public ComponentPinoutProfile ToComponentPinoutProfile()
    {
        bool isBreakout = PackageType.StartsWith("BREAKOUT", StringComparison.OrdinalIgnoreCase) ||
                          PackageType.StartsWith("SIP", StringComparison.OrdinalIgnoreCase) ||
                          PackageType.StartsWith("HEADER", StringComparison.OrdinalIgnoreCase);

        DipWidthCategory width = isBreakout
            ? DipWidthCategory.SingleInLine100Mil
            : PinCount switch
            {
                >= 40 => DipWidthCategory.Wide600Mil,
                _ => DipWidthCategory.Narrow300Mil
            };

        double rowSpacing = width switch
        {
            DipWidthCategory.SingleInLine100Mil => 0.1,
            DipWidthCategory.Wide600Mil => 0.6,
            _ => 0.3
        };

        Pin1OrientationMarker marker = isBreakout
            ? Pin1OrientationMarker.SilkscreenPin1SquarePad
            : Pin1OrientationMarker.HalfMoonNotch;

        string markerDesc = isBreakout
            ? "Header pin 1 square solder pad / silkscreen label"
            : "Standard DIP pin 1 half-moon index notch";

        var supplyRange = new VoltageRange(MinSupplyVoltage, NominalSupplyVoltage, MaxSupplyVoltage, MaxSupplyVoltage * 1.1, Is5VTolerant);
        var logicRange = supplyRange;

        var pinDefs = Pins.Select(p => new PinDefinition(
            p.PinNumber,
            p.Name,
            MapIcPinRoleToFunction(p.Role),
            supplyRange,
            p.Description)).ToImmutableArray();

        return new ComponentPinoutProfile(
            PartNumber: PartNumber,
            Aliases: PartAliases.ToImmutableArray(),
            Category: isBreakout ? "Breakout Module" : "Integrated Circuit",
            Description: DisplayName,
            PinCount: PinCount,
            WidthCategory: width,
            RowSpacingInches: rowSpacing,
            Pin1Marker: marker,
            Pin1MarkerDescription: markerDesc,
            SupplyVoltage: supplyRange,
            LogicVoltage: logicRange,
            Pins: pinDefs,
            CommonWiringTraps: ImmutableArray<BreadboardWiringTrap>.Empty);
    }

    private static PinFunction MapIcPinRoleToFunction(ExpandedIcPinRole role) => role switch
    {
        ExpandedIcPinRole.PowerVdd => PinFunction.PowerVdd,
        ExpandedIcPinRole.GroundVss => PinFunction.Ground,
        ExpandedIcPinRole.ResetActiveLow => PinFunction.ActiveLowReset,
        ExpandedIcPinRole.DigitalIo => PinFunction.DigitalBidirectional,
        ExpandedIcPinRole.AnalogInput => PinFunction.AnalogInput,
        ExpandedIcPinRole.OscillatorClock => PinFunction.ClockInput,
        ExpandedIcPinRole.I2cScl => PinFunction.I2cScl,
        ExpandedIcPinRole.I2cSda => PinFunction.I2cSda,
        ExpandedIcPinRole.SpiClock => PinFunction.SpiSck,
        ExpandedIcPinRole.SpiDataIn => PinFunction.SpiMosi,
        ExpandedIcPinRole.SpiDataOut => PinFunction.SpiMiso,
        ExpandedIcPinRole.SpiChipSelect => PinFunction.SpiCs,
        ExpandedIcPinRole.DarlingtonBaseInput => PinFunction.DigitalInput,
        ExpandedIcPinRole.DarlingtonCollectorOutput => PinFunction.OpenCollectorOut,
        ExpandedIcPinRole.FlybackCommonCathode => PinFunction.PowerVcc,
        ExpandedIcPinRole.LedSegmentDrive => PinFunction.DigitalOutput,
        ExpandedIcPinRole.LedDigitSink => PinFunction.OpenCollectorOut,
        ExpandedIcPinRole.CurrentSetResistor => PinFunction.AnalogInput,
        ExpandedIcPinRole.HardwareAddressSelect => PinFunction.DigitalInput,
        ExpandedIcPinRole.InterruptOrAlertOutput => PinFunction.DigitalOutput,
        ExpandedIcPinRole.NoConnect => PinFunction.NoConnect,
        _ => PinFunction.None
    };

    private static FrozenDictionary<string, ExpandedIcPinSpec> BuildNameLookup(IReadOnlyList<ExpandedIcPinSpec> pins)
    {
        var map = new Dictionary<string, ExpandedIcPinSpec>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pins.Count; i++)
        {
            ExpandedIcPinSpec pin = pins[i];
            map.TryAdd(pin.Name, pin);
            for (int a = 0; a < pin.Aliases.Count; a++)
            {
                map.TryAdd(pin.Aliases[a], pin);
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }
}

/// <summary>
/// High-structure catalog extensions providing zero-allocation lookup definitions for
/// PIC16F877A, MSP430G2553, ULN2003A, MCP23017, MAX7219, and ADS1115.
/// </summary>
public static class IcPinoutCatalogExtensions
{
    private static readonly FrozenDictionary<string, ExpandedIcDefinition> ExpandedIcRegistry =
        BuildExpandedIcs().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, ExpandedIcDefinition> AliasLookupRegistry =
        BuildAliasRegistry(ExpandedIcRegistry);

    /// <summary>
    /// Returns the read-only dictionary of expanded integrated circuits keyed by canonical part number.
    /// </summary>
    public static IReadOnlyDictionary<string, ExpandedIcDefinition> GetExpandedIcs() =>
        ExpandedIcRegistry;

    /// <summary>
    /// Attempts to resolve an expanded IC definition by canonical part number or alias without allocation.
    /// </summary>
    public static bool TryGetExpandedIc(string? partNumberOrAlias, out ExpandedIcDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(partNumberOrAlias))
        {
            definition = null!;
            return false;
        }

        string key = partNumberOrAlias.Trim();
        if (ExpandedIcRegistry.TryGetValue(key, out ExpandedIcDefinition? found) ||
            AliasLookupRegistry.TryGetValue(key, out found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    private static Dictionary<string, ExpandedIcDefinition> BuildExpandedIcs()
    {
        return new Dictionary<string, ExpandedIcDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["PIC16F877A"] = BuildPic16F877A(),
            ["MSP430G2553"] = BuildMsp430G2553(),
            ["ULN2003A"] = BuildUln2003A(),
            ["MCP23017"] = BuildMcp23017(),
            ["MAX7219"] = BuildMax7219(),
            ["ADS1115"] = BuildAds1115()
        };
    }

    private static FrozenDictionary<string, ExpandedIcDefinition> BuildAliasRegistry(
        FrozenDictionary<string, ExpandedIcDefinition> primary)
    {
        var map = new Dictionary<string, ExpandedIcDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ExpandedIcDefinition def) in primary)
        {
            map[key] = def;
            for (int i = 0; i < def.PartAliases.Count; i++)
            {
                map[def.PartAliases[i]] = def;
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 1. PIC16F877A: 40-pin DIP, Microchip 8-bit MCU
    //    (VDD on pins 11 & 32, VSS on 12 & 31, MCLR on 1)
    // =========================================================================
    private static ExpandedIcDefinition BuildPic16F877A()
    {
        ExpandedIcPinSpec[] pins =
        [
            new(1, "MCLR", ExpandedIcPinRole.ResetActiveLow, "Master Clear (Active-Low Reset) / VPP Programming Voltage", ["MCLR/VPP", "VPP", "nMCLR"]),
            new(2, "RA0", ExpandedIcPinRole.AnalogInput, "Port A Bit 0 / Analog Input AN0", ["RA0/AN0", "AN0"]),
            new(3, "RA1", ExpandedIcPinRole.AnalogInput, "Port A Bit 1 / Analog Input AN1", ["RA1/AN1", "AN1"]),
            new(4, "RA2", ExpandedIcPinRole.AnalogInput, "Port A Bit 2 / AN2 / VREF- / CVREF", ["RA2/AN2", "AN2", "VREF-"]),
            new(5, "RA3", ExpandedIcPinRole.AnalogInput, "Port A Bit 3 / AN3 / VREF+", ["RA3/AN3", "AN3", "VREF+"]),
            new(6, "RA4", ExpandedIcPinRole.DigitalIo, "Port A Bit 4 / T0CKI / C1OUT (Open-Drain Output)", ["RA4/T0CKI", "T0CKI"]),
            new(7, "RA5", ExpandedIcPinRole.AnalogInput, "Port A Bit 5 / AN4 / SPI Slave Select (SS) / C2OUT", ["RA5/AN4", "AN4", "SS"]),
            new(8, "RE0", ExpandedIcPinRole.AnalogInput, "Port E Bit 0 / RD / Analog Input AN5", ["RE0/AN5", "AN5", "RD"]),
            new(9, "RE1", ExpandedIcPinRole.AnalogInput, "Port E Bit 1 / WR / Analog Input AN6", ["RE1/AN6", "AN6", "WR"]),
            new(10, "RE2", ExpandedIcPinRole.AnalogInput, "Port E Bit 2 / CS / Analog Input AN7", ["RE2/AN7", "AN7", "CS"]),
            new(11, "VDD", ExpandedIcPinRole.PowerVdd, "Positive Supply (+5.0V VDD, Primary Left Bank)", ["VDD1", "VCC", "+5V"]),
            new(12, "VSS", ExpandedIcPinRole.GroundVss, "Ground Reference (0V VSS, Primary Left Bank)", ["VSS1", "GND", "GND1"]),
            new(13, "OSC1", ExpandedIcPinRole.OscillatorClock, "Oscillator Crystal Input / External Clock Input", ["OSC1/CLKI", "CLKI"]),
            new(14, "OSC2", ExpandedIcPinRole.OscillatorClock, "Oscillator Crystal Output / Clock Output", ["OSC2/CLKO", "CLKO"]),
            new(15, "RC0", ExpandedIcPinRole.DigitalIo, "Port C Bit 0 / T1OSO / T1CKI", ["RC0/T1CKI", "T1CKI"]),
            new(16, "RC1", ExpandedIcPinRole.DigitalIo, "Port C Bit 1 / T1OSI / CCP2", ["RC1/CCP2", "CCP2"]),
            new(17, "RC2", ExpandedIcPinRole.DigitalIo, "Port C Bit 2 / Capture-Compare-PWM 1 (CCP1)", ["RC2/CCP1", "CCP1"]),
            new(18, "RC3", ExpandedIcPinRole.I2cScl, "Port C Bit 3 / SPI SCK / I2C SCL", ["RC3/SCK/SCL", "SCK", "SCL"]),
            new(19, "RD0", ExpandedIcPinRole.DigitalIo, "Port D Bit 0 / Parallel Slave Port Bit 0", ["RD0/PSP0", "PSP0"]),
            new(20, "RD1", ExpandedIcPinRole.DigitalIo, "Port D Bit 1 / Parallel Slave Port Bit 1", ["RD1/PSP1", "PSP1"]),
            new(21, "RD2", ExpandedIcPinRole.DigitalIo, "Port D Bit 2 / Parallel Slave Port Bit 2", ["RD2/PSP2", "PSP2"]),
            new(22, "RD3", ExpandedIcPinRole.DigitalIo, "Port D Bit 3 / Parallel Slave Port Bit 3", ["RD3/PSP3", "PSP3"]),
            new(23, "RC4", ExpandedIcPinRole.I2cSda, "Port C Bit 4 / SPI SDI / I2C SDA", ["RC4/SDI/SDA", "SDI", "SDA"]),
            new(24, "RC5", ExpandedIcPinRole.SpiDataOut, "Port C Bit 5 / SPI SDO", ["RC5/SDO", "SDO"]),
            new(25, "RC6", ExpandedIcPinRole.DigitalIo, "Port C Bit 6 / USART Asynchronous TX / Synchronous CK", ["RC6/TX/CK", "TX"]),
            new(26, "RC7", ExpandedIcPinRole.DigitalIo, "Port C Bit 7 / USART Asynchronous RX / Synchronous DT", ["RC7/RX/DT", "RX"]),
            new(27, "RD4", ExpandedIcPinRole.DigitalIo, "Port D Bit 4 / Parallel Slave Port Bit 4", ["RD4/PSP4", "PSP4"]),
            new(28, "RD5", ExpandedIcPinRole.DigitalIo, "Port D Bit 5 / Parallel Slave Port Bit 5", ["RD5/PSP5", "PSP5"]),
            new(29, "RD6", ExpandedIcPinRole.DigitalIo, "Port D Bit 6 / Parallel Slave Port Bit 6", ["RD6/PSP6", "PSP6"]),
            new(30, "RD7", ExpandedIcPinRole.DigitalIo, "Port D Bit 7 / Parallel Slave Port Bit 7", ["RD7/PSP7", "PSP7"]),
            new(31, "VSS", ExpandedIcPinRole.GroundVss, "Ground Reference (0V VSS, Secondary Right Bank)", ["VSS2", "GND2"]),
            new(32, "VDD", ExpandedIcPinRole.PowerVdd, "Positive Supply (+5.0V VDD, Secondary Right Bank)", ["VDD2", "VCC2"]),
            new(33, "RB0", ExpandedIcPinRole.DigitalIo, "Port B Bit 0 / External Interrupt Input (INT)", ["RB0/INT", "INT"]),
            new(34, "RB1", ExpandedIcPinRole.DigitalIo, "Port B Bit 1 Bidirectional I/O", ["PB1"]),
            new(35, "RB2", ExpandedIcPinRole.DigitalIo, "Port B Bit 2 Bidirectional I/O", ["PB2"]),
            new(36, "RB3", ExpandedIcPinRole.DigitalIo, "Port B Bit 3 / Low-Voltage ICSP Programming Enable (PGM)", ["RB3/PGM", "PGM"]),
            new(37, "RB4", ExpandedIcPinRole.DigitalIo, "Port B Bit 4 Interrupt-on-Change Pin", ["PB4"]),
            new(38, "RB5", ExpandedIcPinRole.DigitalIo, "Port B Bit 5 Interrupt-on-Change Pin", ["PB5"]),
            new(39, "RB6", ExpandedIcPinRole.DigitalIo, "Port B Bit 6 / ICSP Programming Clock (PGC)", ["RB6/PGC", "PGC"]),
            new(40, "RB7", ExpandedIcPinRole.DigitalIo, "Port B Bit 7 / ICSP Programming Data (PGD)", ["RB7/PGD", "PGD"])
        ];

        return new ExpandedIcDefinition(
            PartNumber: "PIC16F877A",
            DisplayName: "Microchip PIC16F877A 8-Bit CMOS Flash MCU (40-Pin PDIP)",
            Manufacturer: "Microchip Technology",
            PackageType: "DIP-40",
            PinCount: 40,
            MinSupplyVoltage: 2.0,
            NominalSupplyVoltage: 5.0,
            MaxSupplyVoltage: 5.5,
            Is5VTolerant: true,
            Pins: pins,
            PartAliases: ["PIC16F877A-I/P", "PIC16F877", "16F877A"]);
    }

    // =========================================================================
    // 2. MSP430G2553: 20-pin DIP, TI ultra-low-power MCU
    //    (DVCC pin 1, DVSS pin 20, TEST pin 17, RST pin 16)
    // =========================================================================
    private static ExpandedIcDefinition BuildMsp430G2553()
    {
        ExpandedIcPinSpec[] pins =
        [
            new(1, "DVCC", ExpandedIcPinRole.PowerVdd, "Digital Supply Voltage (+1.8V to +3.6V, Nominal 3.3V)", ["VCC", "VDD", "3V3"], Is5VTolerant: false),
            new(2, "P1.0", ExpandedIcPinRole.AnalogInput, "Port 1.0 / TA0CLK / ACLK / ADC10 A0 / Comparator CA0", ["A0", "P1_0", "CA0"], Is5VTolerant: false),
            new(3, "P1.1", ExpandedIcPinRole.DigitalIo, "Port 1.1 / TA0.0 / USCI_A0 UART RXD / SPI SOMI / A1", ["UCA0RXD", "RXD", "A1", "P1_1"], Is5VTolerant: false),
            new(4, "P1.2", ExpandedIcPinRole.DigitalIo, "Port 1.2 / TA0.1 / USCI_A0 UART TXD / SPI SIMO / A2", ["UCA0TXD", "TXD", "A2", "P1_2"], Is5VTolerant: false),
            new(5, "P1.3", ExpandedIcPinRole.AnalogInput, "Port 1.3 / ADC10CLK / VREF- / A3 / CA3", ["A3", "VREF-", "P1_3"], Is5VTolerant: false),
            new(6, "P1.4", ExpandedIcPinRole.AnalogInput, "Port 1.4 / SMCLK / VREF+ / A4 / JTAG TCK", ["A4", "VREF+", "SMCLK", "TCK", "P1_4"], Is5VTolerant: false),
            new(7, "P1.5", ExpandedIcPinRole.SpiClock, "Port 1.5 / TA0.0 / UCB0CLK / A5 / JTAG TMS", ["UCB0CLK", "A5", "TMS", "P1_5"], Is5VTolerant: false),
            new(8, "P2.0", ExpandedIcPinRole.DigitalIo, "Port 2.0 / Timer1_A3 TA1.0", ["P2_0", "TA1.0"], Is5VTolerant: false),
            new(9, "P2.1", ExpandedIcPinRole.DigitalIo, "Port 2.1 / Timer1_A3 TA1.1", ["P2_1", "TA1.1"], Is5VTolerant: false),
            new(10, "P2.2", ExpandedIcPinRole.DigitalIo, "Port 2.2 / Timer1_A3 TA1.1", ["P2_2"], Is5VTolerant: false),
            new(11, "P2.3", ExpandedIcPinRole.DigitalIo, "Port 2.3 / Timer1_A3 TA1.0", ["P2_3"], Is5VTolerant: false),
            new(12, "P2.4", ExpandedIcPinRole.DigitalIo, "Port 2.4 / Timer1_A3 TA1.2", ["P2_4", "TA1.2"], Is5VTolerant: false),
            new(13, "P2.5", ExpandedIcPinRole.DigitalIo, "Port 2.5 / Timer1_A3 TA1.2", ["P2_5"], Is5VTolerant: false),
            new(14, "P1.6", ExpandedIcPinRole.I2cScl, "Port 1.6 / TA0.1 / I2C SCL / SPI SOMI / A6 / TDI", ["SCL", "UCB0SCL", "A6", "P1_6"], Is5VTolerant: false),
            new(15, "P1.7", ExpandedIcPinRole.I2cSda, "Port 1.7 / I2C SDA / SPI SIMO / A7 / TDO", ["SDA", "UCB0SDA", "A7", "P1_7"], Is5VTolerant: false),
            new(16, "RST", ExpandedIcPinRole.ResetActiveLow, "Active-Low Reset / Non-Maskable Interrupt / Spy-Bi-Wire TDIO", ["nRST", "RST/NMI", "SBWTDIO", "RESET"], Is5VTolerant: false),
            new(17, "TEST", ExpandedIcPinRole.TestOrJtag, "Test Mode Enable for JTAG Pins / Spy-Bi-Wire Clock", ["TEST/SBWTCK", "SBWTCK"], Is5VTolerant: false),
            new(18, "XOUT", ExpandedIcPinRole.OscillatorClock, "Crystal Oscillator Output / Port 2.7", ["P2.7", "P2_7"], Is5VTolerant: false),
            new(19, "XIN", ExpandedIcPinRole.OscillatorClock, "Crystal Oscillator Input / Port 2.6 / TA0.1", ["P2.6", "P2_6"], Is5VTolerant: false),
            new(20, "DVSS", ExpandedIcPinRole.GroundVss, "Digital Ground Reference (0V DVSS)", ["GND", "VSS"], Is5VTolerant: true)
        ];

        return new ExpandedIcDefinition(
            PartNumber: "MSP430G2553",
            DisplayName: "Texas Instruments MSP430G2553 16-Bit Ultra-Low-Power MCU (20-Pin DIP)",
            Manufacturer: "Texas Instruments",
            PackageType: "DIP-20",
            PinCount: 20,
            MinSupplyVoltage: 1.8,
            NominalSupplyVoltage: 3.3,
            MaxSupplyVoltage: 3.6,
            Is5VTolerant: false,
            Pins: pins,
            PartAliases: ["MSP430G2553IN20", "MSP430G2553IPW20", "MSP430"]);
    }

    // =========================================================================
    // 3. ULN2003A: 16-pin DIP, Darlington array
    //    (Inputs 1..7, Outputs 16..10, GND 8, COM 9)
    // =========================================================================
    private static ExpandedIcDefinition BuildUln2003A()
    {
        ExpandedIcPinSpec[] pins =
        [
            new(1, "IN1", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 1 Base Input (5V TTL/CMOS)", ["1B", "B1", "INPUT1"]),
            new(2, "IN2", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 2 Base Input (5V TTL/CMOS)", ["2B", "B2", "INPUT2"]),
            new(3, "IN3", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 3 Base Input (5V TTL/CMOS)", ["3B", "B3", "INPUT3"]),
            new(4, "IN4", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 4 Base Input (5V TTL/CMOS)", ["4B", "B4", "INPUT4"]),
            new(5, "IN5", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 5 Base Input (5V TTL/CMOS)", ["5B", "B5", "INPUT5"]),
            new(6, "IN6", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 6 Base Input (5V TTL/CMOS)", ["6B", "B6", "INPUT6"]),
            new(7, "IN7", ExpandedIcPinRole.DarlingtonBaseInput, "Darlington Channel 7 Base Input (5V TTL/CMOS)", ["7B", "B7", "INPUT7"]),
            new(8, "GND", ExpandedIcPinRole.GroundVss, "Common Darlington Emitter Ground Reference (0V)", ["E", "EMITTER", "VSS"]),
            new(9, "COM", ExpandedIcPinRole.FlybackCommonCathode, "Common Freewheeling Flyback Diode Cathode (Inductive Load V+)", ["COMMON", "VDD", "VCC_LOAD"]),
            new(10, "OUT7", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 7 Open-Collector Sink Output (500mA / 50V)", ["7C", "C7", "OUTPUT7"]),
            new(11, "OUT6", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 6 Open-Collector Sink Output (500mA / 50V)", ["6C", "C6", "OUTPUT6"]),
            new(12, "OUT5", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 5 Open-Collector Sink Output (500mA / 50V)", ["5C", "C5", "OUTPUT5"]),
            new(13, "OUT4", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 4 Open-Collector Sink Output (500mA / 50V)", ["4C", "C4", "OUTPUT4"]),
            new(14, "OUT3", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 3 Open-Collector Sink Output (500mA / 50V)", ["3C", "C3", "OUTPUT3"]),
            new(15, "OUT2", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 2 Open-Collector Sink Output (500mA / 50V)", ["2C", "C2", "OUTPUT2"]),
            new(16, "OUT1", ExpandedIcPinRole.DarlingtonCollectorOutput, "Darlington Channel 1 Open-Collector Sink Output (500mA / 50V)", ["1C", "C1", "OUTPUT1"])
        ];

        return new ExpandedIcDefinition(
            PartNumber: "ULN2003A",
            DisplayName: "ULN2003A 7-Channel High-Voltage High-Current Darlington Array (16-Pin DIP)",
            Manufacturer: "Texas Instruments / STMicroelectronics",
            PackageType: "DIP-16",
            PinCount: 16,
            MinSupplyVoltage: 3.3,
            NominalSupplyVoltage: 5.0,
            MaxSupplyVoltage: 50.0,
            Is5VTolerant: true,
            Pins: pins,
            PartAliases: ["ULN2003", "ULN2003AN", "ULN2003APG"]);
    }

    // =========================================================================
    // 4. MCP23017: 28-pin DIP, I2C 16-bit IO Expander
    //    (VDD 9, VSS 10, SCL 12, SDA 13, RESET 18)
    // =========================================================================
    private static ExpandedIcDefinition BuildMcp23017()
    {
        ExpandedIcPinSpec[] pins =
        [
            new(1, "GPB0", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 0", ["PB0", "B0"]),
            new(2, "GPB1", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 1", ["PB1", "B1"]),
            new(3, "GPB2", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 2", ["PB2", "B2"]),
            new(4, "GPB3", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 3", ["PB3", "B3"]),
            new(5, "GPB4", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 4", ["PB4", "B4"]),
            new(6, "GPB5", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 5", ["PB5", "B5"]),
            new(7, "GPB6", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 6", ["PB6", "B6"]),
            new(8, "GPB7", ExpandedIcPinRole.DigitalIo, "Port B Bidirectional GPIO Pin 7", ["PB7", "B7"]),
            new(9, "VDD", ExpandedIcPinRole.PowerVdd, "Positive Supply Voltage (+1.8V to +5.5V)", ["VCC", "+5V", "3V3"]),
            new(10, "VSS", ExpandedIcPinRole.GroundVss, "System Ground Reference (0V)", ["GND"]),
            new(11, "NC1", ExpandedIcPinRole.NoConnect, "No Internal Connection (I2C Variant)", ["NC", "CS_NC"]),
            new(12, "SCL", ExpandedIcPinRole.I2cScl, "I2C Serial Clock Input (Up to 1.7 MHz)", ["I2C_SCL", "SCK"]),
            new(13, "SDA", ExpandedIcPinRole.I2cSda, "I2C Serial Data Bidirectional Line", ["I2C_SDA"]),
            new(14, "NC2", ExpandedIcPinRole.NoConnect, "No Internal Connection (I2C Variant)", ["SO_NC"]),
            new(15, "A0", ExpandedIcPinRole.HardwareAddressSelect, "I2C Hardware Address Bit 0 (Must be externally biased to VDD or VSS)", ["ADDR0"]),
            new(16, "A1", ExpandedIcPinRole.HardwareAddressSelect, "I2C Hardware Address Bit 1 (Must be externally biased to VDD or VSS)", ["ADDR1"]),
            new(17, "A2", ExpandedIcPinRole.HardwareAddressSelect, "I2C Hardware Address Bit 2 (Must be externally biased to VDD or VSS)", ["ADDR2"]),
            new(18, "RESET", ExpandedIcPinRole.ResetActiveLow, "Active-Low Hardware Reset Input (Pull High to VDD for normal operation)", ["nRESET", "RST"]),
            new(19, "INTB", ExpandedIcPinRole.InterruptOrAlertOutput, "Port B Interrupt Output (Configurable Active-High/Low or Open-Drain)", ["INT_B"]),
            new(20, "INTA", ExpandedIcPinRole.InterruptOrAlertOutput, "Port A Interrupt Output (Configurable Active-High/Low or Open-Drain)", ["INT_A"]),
            new(21, "GPA0", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 0", ["PA0", "A_0"]),
            new(22, "GPA1", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 1", ["PA1", "A_1"]),
            new(23, "GPA2", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 2", ["PA2", "A_2"]),
            new(24, "GPA3", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 3", ["PA3", "A_3"]),
            new(25, "GPA4", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 4", ["PA4", "A_4"]),
            new(26, "GPA5", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 5", ["PA5", "A_5"]),
            new(27, "GPA6", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 6", ["PA6", "A_6"]),
            new(28, "GPA7", ExpandedIcPinRole.DigitalIo, "Port A Bidirectional GPIO Pin 7", ["PA7", "A_7"])
        ];

        return new ExpandedIcDefinition(
            PartNumber: "MCP23017",
            DisplayName: "Microchip MCP23017 16-Bit I2C I/O Expander (28-Pin SPDIP)",
            Manufacturer: "Microchip Technology",
            PackageType: "DIP-28",
            PinCount: 28,
            MinSupplyVoltage: 1.8,
            NominalSupplyVoltage: 5.0,
            MaxSupplyVoltage: 5.5,
            Is5VTolerant: true,
            Pins: pins,
            PartAliases: ["MCP23017-E/SP", "MCP23017-I/SP"]);
    }

    // =========================================================================
    // 5. MAX7219: 24-pin DIP, Serially interfaced LED driver
    //    (DIN 1, CLK 13, LOAD/CS 12, V+ 19, GND 4 & 9)
    // =========================================================================
    private static ExpandedIcDefinition BuildMax7219()
    {
        ExpandedIcPinSpec[] pins =
        [
            new(1, "DIN", ExpandedIcPinRole.SpiDataIn, "Serial Data Input (16-Bit Shift Register Input)", ["MOSI", "DATA_IN", "SDI"]),
            new(2, "DIG0", ExpandedIcPinRole.LedDigitSink, "Digit 0 Cathode Sink Driver", ["DIG_0", "D0"]),
            new(3, "DIG4", ExpandedIcPinRole.LedDigitSink, "Digit 4 Cathode Sink Driver", ["DIG_4", "D4"]),
            new(4, "GND", ExpandedIcPinRole.GroundVss, "Ground Reference Pin 4 (Both GND pins 4 and 9 must be connected)", ["GND1", "VSS1"]),
            new(5, "DIG6", ExpandedIcPinRole.LedDigitSink, "Digit 6 Cathode Sink Driver", ["DIG_6", "D6"]),
            new(6, "DIG2", ExpandedIcPinRole.LedDigitSink, "Digit 2 Cathode Sink Driver", ["DIG_2", "D2"]),
            new(7, "DIG3", ExpandedIcPinRole.LedDigitSink, "Digit 3 Cathode Sink Driver", ["DIG_3", "D3"]),
            new(8, "DIG7", ExpandedIcPinRole.LedDigitSink, "Digit 7 Cathode Sink Driver", ["DIG_7", "D7"]),
            new(9, "GND", ExpandedIcPinRole.GroundVss, "Ground Reference Pin 9 (Both GND pins 4 and 9 must be connected)", ["GND2", "VSS2"]),
            new(10, "DIG5", ExpandedIcPinRole.LedDigitSink, "Digit 5 Cathode Sink Driver", ["DIG_5", "D5"]),
            new(11, "DIG1", ExpandedIcPinRole.LedDigitSink, "Digit 1 Cathode Sink Driver", ["DIG_1", "D1"]),
            new(12, "LOAD/CS", ExpandedIcPinRole.SpiChipSelect, "Load Data / Chip Select Input (Latches data on rising edge)", ["LOAD", "CS", "SS"]),
            new(13, "CLK", ExpandedIcPinRole.SpiClock, "Serial Clock Input (Up to 10 MHz)", ["SCK", "SCLK"]),
            new(14, "SEGA", ExpandedIcPinRole.LedSegmentDrive, "Segment A Anode Source Driver", ["SEG_A", "A"]),
            new(15, "SEGF", ExpandedIcPinRole.LedSegmentDrive, "Segment F Anode Source Driver", ["SEG_F", "F"]),
            new(16, "SEGB", ExpandedIcPinRole.LedSegmentDrive, "Segment B Anode Source Driver", ["SEG_B", "B"]),
            new(17, "SEGG", ExpandedIcPinRole.LedSegmentDrive, "Segment G Anode Source Driver", ["SEG_G", "G"]),
            new(18, "ISET", ExpandedIcPinRole.CurrentSetResistor, "Peak Segment Current Set Input (Connect resistor to V+)", ["RSET", "I_SET"]),
            new(19, "V+", ExpandedIcPinRole.PowerVdd, "Positive Supply Voltage (+4.0V to +5.5V, Nominal 5.0V)", ["VCC", "VDD", "+5V"]),
            new(20, "SEGC", ExpandedIcPinRole.LedSegmentDrive, "Segment C Anode Source Driver", ["SEG_C", "C"]),
            new(21, "SEGE", ExpandedIcPinRole.LedSegmentDrive, "Segment E Anode Source Driver", ["SEG_E", "E"]),
            new(22, "SEGDP", ExpandedIcPinRole.LedSegmentDrive, "Decimal Point Anode Source Driver", ["SEG_DP", "DP"]),
            new(23, "SEGD", ExpandedIcPinRole.LedSegmentDrive, "Segment D Anode Source Driver", ["SEG_D", "D"]),
            new(24, "DOUT", ExpandedIcPinRole.SpiDataOut, "Serial Data Output for Daisy-Chaining Multiple MAX7219s", ["SDO", "DATA_OUT"])
        ];

        return new ExpandedIcDefinition(
            PartNumber: "MAX7219",
            DisplayName: "Analog Devices / Maxim MAX7219 8-Digit LED Display Driver (24-Pin DIP)",
            Manufacturer: "Analog Devices / Maxim Integrated",
            PackageType: "DIP-24",
            PinCount: 24,
            MinSupplyVoltage: 4.0,
            NominalSupplyVoltage: 5.0,
            MaxSupplyVoltage: 5.5,
            Is5VTolerant: true,
            Pins: pins,
            PartAliases: ["MAX7219CNG", "MAX7219ENG", "MAX7221"]);
    }

    // =========================================================================
    // 6. ADS1115: 10-pin breakout, 16-bit I2C ADC
    //    (VDD 1, GND 2, SCL 3, SDA 4, ADDR 5, ALRT 6, A0..A3)
    // =========================================================================
    private static ExpandedIcDefinition BuildAds1115()
    {
        ExpandedIcPinSpec[] pins =
        [
            new(1, "VDD", ExpandedIcPinRole.PowerVdd, "Positive Supply Input (+2.0V to +5.5V)", ["VCC", "VIN", "+5V", "3V3"]),
            new(2, "GND", ExpandedIcPinRole.GroundVss, "System Ground Reference (0V)", ["VSS"]),
            new(3, "SCL", ExpandedIcPinRole.I2cScl, "I2C Serial Clock Input", ["I2C_SCL"]),
            new(4, "SDA", ExpandedIcPinRole.I2cSda, "I2C Serial Data Bidirectional Line", ["I2C_SDA"]),
            new(5, "ADDR", ExpandedIcPinRole.HardwareAddressSelect, "I2C Slave Address Select Pin (GND=0x48, VDD=0x49, SDA=0x4A, SCL=0x4B)", ["ADDRESS", "ADR"]),
            new(6, "ALRT", ExpandedIcPinRole.InterruptOrAlertOutput, "Comparator Alert / Conversion Ready Open-Drain Output", ["ALERT", "RDY", "ALERT/RDY"]),
            new(7, "A0", ExpandedIcPinRole.AnalogInput, "Analog Input Channel 0 (Single-Ended or Differential AIN0)", ["AIN0", "CH0"]),
            new(8, "A1", ExpandedIcPinRole.AnalogInput, "Analog Input Channel 1 (Single-Ended or Differential AIN1)", ["AIN1", "CH1"]),
            new(9, "A2", ExpandedIcPinRole.AnalogInput, "Analog Input Channel 2 (Single-Ended or Differential AIN2)", ["AIN2", "CH2"]),
            new(10, "A3", ExpandedIcPinRole.AnalogInput, "Analog Input Channel 3 (Single-Ended or Differential AIN3)", ["AIN3", "CH3"])
        ];

        return new ExpandedIcDefinition(
            PartNumber: "ADS1115",
            DisplayName: "Texas Instruments ADS1115 16-Bit 860SPS I2C ADC Breakout (10-Pin)",
            Manufacturer: "Texas Instruments",
            PackageType: "BREAKOUT-10",
            PinCount: 10,
            MinSupplyVoltage: 2.0,
            NominalSupplyVoltage: 3.3,
            MaxSupplyVoltage: 5.5,
            Is5VTolerant: true,
            Pins: pins,
            PartAliases: ["ADS1115IDGSR", "ADS1015", "GY-ADS1115"]);
    }
}
