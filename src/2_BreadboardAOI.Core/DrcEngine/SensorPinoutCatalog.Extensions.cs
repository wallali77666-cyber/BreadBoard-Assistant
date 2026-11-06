using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using BreadboardAOI.Core.Contracts;

namespace BreadboardAOI.Core.DrcEngine;

/// <summary>
/// Communication bus architecture used by an expanded breakout sensor module.
/// </summary>
public enum ExpandedSensorProtocol : byte
{
    I2c = 0,
    Spi = 1,
    I2cAndSpi = 2
}

/// <summary>
/// Electrical and signal role of a breakout sensor header pin.
/// </summary>
public enum ExpandedSensorPinRole : byte
{
    PowerSupply = 0,
    Ground = 1,
    I2cScl = 2,
    I2cSda = 3,
    AuxiliaryI2cScl = 4,
    AuxiliaryI2cSda = 5,
    I2cAddressSelect = 6,
    SpiSck = 7,
    SpiMosi = 8,
    SpiMiso = 9,
    SpiChipSelect = 10,
    InterruptOutput = 11,
    ResetOrShutdown = 12,
    HighSideShuntPositive = 13,
    HighSideShuntNegative = 14
}

/// <summary>
/// Immutable, zero-allocation pin specification for a breakout sensor module.
/// </summary>
public sealed record ExpandedSensorPinSpec(
    int PinNumber,
    string Name,
    ExpandedSensorPinRole Role,
    double MaxInputVoltage,
    bool Is5VTolerant,
    string Description,
    IReadOnlyList<string> Aliases)
{
    public bool IsPowerPin => Role == ExpandedSensorPinRole.PowerSupply;

    public bool IsGroundPin => Role == ExpandedSensorPinRole.Ground;
}

/// <summary>
/// Immutable, high-structure hardware specification for a breakout sensor module.
/// </summary>
public sealed record ExpandedSensorDefinition(
    string ModelId,
    string DisplayName,
    string Category,
    ExpandedSensorProtocol Protocol,
    int PinCount,
    double MinSupplyVoltage,
    double NominalLogicVoltage,
    double MaxSupplyVoltage,
    bool Is5VCompatible,
    IReadOnlyList<ExpandedSensorPinSpec> Pins,
    IReadOnlyList<string> ModelAliases)
{
    private readonly FrozenDictionary<int, ExpandedSensorPinSpec> _pinsByNumber =
        Pins.ToFrozenDictionary(static p => p.PinNumber);

    private readonly FrozenDictionary<string, ExpandedSensorPinSpec> _pinsByName =
        BuildNameLookup(Pins);

    /// <summary>
    /// All supply voltage pins (VIN, VCC, 3.3V) on the breakout sensor module.
    /// </summary>
    public IReadOnlyList<ExpandedSensorPinSpec> PowerPins { get; } =
        Pins.Where(static p => p.IsPowerPin).ToArray();

    /// <summary>
    /// All ground reference pins (GND) on the breakout sensor module.
    /// </summary>
    public IReadOnlyList<ExpandedSensorPinSpec> GroundPins { get; } =
        Pins.Where(static p => p.IsGroundPin).ToArray();

    /// <summary>
    /// Looks up a pin by 1-based physical header index in O(1) time without allocations.
    /// </summary>
    public ExpandedSensorPinSpec? GetPin(int pinNumber) =>
        _pinsByNumber.TryGetValue(pinNumber, out ExpandedSensorPinSpec? spec) ? spec : null;

    /// <summary>
    /// Looks up a pin by canonical name or alias in O(1) time without allocations.
    /// </summary>
    public ExpandedSensorPinSpec? FindPin(string nameOrAlias)
    {
        if (string.IsNullOrWhiteSpace(nameOrAlias))
            return null;

        return _pinsByName.TryGetValue(nameOrAlias.Trim(), out ExpandedSensorPinSpec? spec) ? spec : null;
    }

    /// <summary>
    /// Converts this expanded breakout sensor definition to the legacy <see cref="SensorDefinition"/> used by <see cref="ElectricalRulesChecker"/>.
    /// </summary>
    public SensorDefinition ToSensorDefinition()
    {
        var sensorPins = Pins.Select(p => new SensorPinSpec(
            p.PinNumber,
            p.Name,
            MapSensorPinRoleToType(p.Role),
            p.Description)).ToArray();

        return new SensorDefinition(
            PartId: ModelId,
            DisplayName: DisplayName,
            PinCount: PinCount,
            Pins: sensorPins,
            NominalVoltage: NominalLogicVoltage,
            MinVoltage: MinSupplyVoltage,
            MaxVoltage: MaxSupplyVoltage,
            Description: DisplayName);
    }

    /// <summary>
    /// Converts this expanded sensor definition to a rich <see cref="ComponentPinoutProfile"/> for interactive visual inspection.
    /// </summary>
    public ComponentPinoutProfile ToComponentPinoutProfile()
    {
        var supplyRange = new VoltageRange(MinSupplyVoltage, NominalLogicVoltage, MaxSupplyVoltage, MaxSupplyVoltage * 1.1, Is5VCompatible);
        var logicRange = supplyRange;

        var pinDefs = Pins.Select(p => new PinDefinition(
            p.PinNumber,
            p.Name,
            MapSensorPinRoleToPinFunction(p.Role),
            supplyRange,
            p.Description)).ToImmutableArray();

        return new ComponentPinoutProfile(
            PartNumber: ModelId,
            Aliases: ModelAliases.ToImmutableArray(),
            Category: Category,
            Description: DisplayName,
            PinCount: PinCount,
            WidthCategory: DipWidthCategory.SingleInLine100Mil,
            RowSpacingInches: 0.1,
            Pin1Marker: Pin1OrientationMarker.SilkscreenPin1SquarePad,
            Pin1MarkerDescription: "Header pin 1 square solder pad / silkscreen label",
            SupplyVoltage: supplyRange,
            LogicVoltage: logicRange,
            Pins: pinDefs,
            CommonWiringTraps: ImmutableArray<BreadboardWiringTrap>.Empty);
    }

    private static SensorPinType MapSensorPinRoleToType(ExpandedSensorPinRole role) => role switch
    {
        ExpandedSensorPinRole.PowerSupply => SensorPinType.PowerVcc,
        ExpandedSensorPinRole.Ground => SensorPinType.Ground,
        ExpandedSensorPinRole.InterruptOutput => SensorPinType.DigitalOutput,
        ExpandedSensorPinRole.SpiMiso => SensorPinType.DigitalOutput,
        ExpandedSensorPinRole.HighSideShuntPositive => SensorPinType.PassiveLead,
        ExpandedSensorPinRole.HighSideShuntNegative => SensorPinType.PassiveLead,
        ExpandedSensorPinRole.I2cScl => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.I2cSda => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.AuxiliaryI2cScl => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.AuxiliaryI2cSda => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.I2cAddressSelect => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.SpiSck => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.SpiMosi => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.SpiChipSelect => SensorPinType.DigitalInput,
        ExpandedSensorPinRole.ResetOrShutdown => SensorPinType.DigitalInput,
        _ => SensorPinType.DigitalInput
    };

    private static PinFunction MapSensorPinRoleToPinFunction(ExpandedSensorPinRole role) => role switch
    {
        ExpandedSensorPinRole.PowerSupply => PinFunction.PowerVcc,
        ExpandedSensorPinRole.Ground => PinFunction.Ground,
        ExpandedSensorPinRole.I2cScl => PinFunction.I2cScl,
        ExpandedSensorPinRole.I2cSda => PinFunction.I2cSda,
        ExpandedSensorPinRole.AuxiliaryI2cScl => PinFunction.I2cScl,
        ExpandedSensorPinRole.AuxiliaryI2cSda => PinFunction.I2cSda,
        ExpandedSensorPinRole.I2cAddressSelect => PinFunction.DigitalInput,
        ExpandedSensorPinRole.SpiSck => PinFunction.SpiSck,
        ExpandedSensorPinRole.SpiMosi => PinFunction.SpiMosi,
        ExpandedSensorPinRole.SpiMiso => PinFunction.SpiMiso,
        ExpandedSensorPinRole.SpiChipSelect => PinFunction.SpiCs,
        ExpandedSensorPinRole.InterruptOutput => PinFunction.DigitalOutput,
        ExpandedSensorPinRole.ResetOrShutdown => PinFunction.ActiveLowReset,
        _ => PinFunction.None
    };

    private static FrozenDictionary<string, ExpandedSensorPinSpec> BuildNameLookup(IReadOnlyList<ExpandedSensorPinSpec> pins)
    {
        var map = new Dictionary<string, ExpandedSensorPinSpec>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < pins.Count; i++)
        {
            ExpandedSensorPinSpec pin = pins[i];
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
/// BME680, MPU6050, VL53L0X, INA219, and RC522 breakout sensors.
/// </summary>
public static class SensorPinoutCatalogExtensions
{
    private static readonly FrozenDictionary<string, ExpandedSensorDefinition> ExpandedSensorRegistry =
        BuildExpandedSensors().ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);

    private static readonly FrozenDictionary<string, ExpandedSensorDefinition> AliasLookupRegistry =
        BuildAliasRegistry(ExpandedSensorRegistry);

    /// <summary>
    /// Returns the read-only dictionary of expanded breakout sensor definitions keyed by canonical model ID.
    /// </summary>
    public static IReadOnlyDictionary<string, ExpandedSensorDefinition> GetExpandedSensors() =>
        ExpandedSensorRegistry;

    /// <summary>
    /// Attempts to resolve an expanded sensor definition by canonical model ID or alias without allocation.
    /// </summary>
    public static bool TryGetExpandedSensor(string? modelIdOrAlias, out ExpandedSensorDefinition definition)
    {
        if (string.IsNullOrWhiteSpace(modelIdOrAlias))
        {
            definition = null!;
            return false;
        }

        string key = modelIdOrAlias.Trim();
        if (ExpandedSensorRegistry.TryGetValue(key, out ExpandedSensorDefinition? found) ||
            AliasLookupRegistry.TryGetValue(key, out found))
        {
            definition = found;
            return true;
        }

        definition = null!;
        return false;
    }

    private static Dictionary<string, ExpandedSensorDefinition> BuildExpandedSensors()
    {
        return new Dictionary<string, ExpandedSensorDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["BME680"] = BuildBme680(),
            ["MPU6050"] = BuildMpu6050(),
            ["VL53L0X"] = BuildVl53L0X(),
            ["INA219"] = BuildIna219(),
            ["RC522"] = BuildRc522()
        };
    }

    private static FrozenDictionary<string, ExpandedSensorDefinition> BuildAliasRegistry(
        FrozenDictionary<string, ExpandedSensorDefinition> primary)
    {
        var map = new Dictionary<string, ExpandedSensorDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach ((string key, ExpandedSensorDefinition def) in primary)
        {
            map[key] = def;
            for (int i = 0; i < def.ModelAliases.Count; i++)
            {
                map[def.ModelAliases[i]] = def;
            }
        }

        return map.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    // =========================================================================
    // 1. BME680: Gas/Temp/Humidity/Pressure (VIN, GND, SCL, SDA, SDO, CS) - 3.3V Logic
    // =========================================================================
    private static ExpandedSensorDefinition BuildBme680()
    {
        ExpandedSensorPinSpec[] pins =
        [
            new(1, "VIN", ExpandedSensorPinRole.PowerSupply, 3.6, false, "3.3V Supply Input (3.3V Logic)", ["VCC", "3V3", "3.3V", "VDD"]),
            new(2, "GND", ExpandedSensorPinRole.Ground, 0.0, true, "System Ground Reference (0V)", ["VSS"]),
            new(3, "SCL", ExpandedSensorPinRole.I2cScl, 3.6, false, "I2C Serial Clock / SPI SCK (3.3V Logic)", ["SCK", "SCLK"]),
            new(4, "SDA", ExpandedSensorPinRole.I2cSda, 3.6, false, "I2C Serial Data / SPI MOSI (3.3V Logic)", ["SDI", "MOSI"]),
            new(5, "SDO", ExpandedSensorPinRole.SpiMiso, 3.6, false, "I2C Address Select (GND=0x76, HIGH=0x77) / SPI MISO", ["ADDR", "MISO"]),
            new(6, "CS", ExpandedSensorPinRole.SpiChipSelect, 3.6, false, "Chip Select (Pull HIGH for I2C, Active-LOW for SPI)", ["CSB", "SS"])
        ];

        return new ExpandedSensorDefinition(
            ModelId: "BME680",
            DisplayName: "Bosch BME680 VOC Gas, Temperature, Humidity & Barometric Pressure Sensor",
            Category: "Environmental Sensor",
            Protocol: ExpandedSensorProtocol.I2cAndSpi,
            PinCount: 6,
            MinSupplyVoltage: 1.71,
            NominalLogicVoltage: 3.3,
            MaxSupplyVoltage: 3.6,
            Is5VCompatible: false,
            Pins: pins,
            ModelAliases: ["CJMCU-680", "GY-BME680", "BME688"]);
    }

    // =========================================================================
    // 2. MPU6050: 6-Axis IMU (VCC, GND, SCL, SDA, XDA, XCL, AD0, INT) - 3.3V/5V compatible
    // =========================================================================
    private static ExpandedSensorDefinition BuildMpu6050()
    {
        ExpandedSensorPinSpec[] pins =
        [
            new(1, "VCC", ExpandedSensorPinRole.PowerSupply, 5.5, true, "Power Supply Input (3.3V to 5V via onboard LDO)", ["VIN", "VDD", "+5V", "3V3"]),
            new(2, "GND", ExpandedSensorPinRole.Ground, 0.0, true, "System Ground Reference (0V)", ["VSS"]),
            new(3, "SCL", ExpandedSensorPinRole.I2cScl, 5.5, true, "Primary I2C Serial Clock Line", ["I2C_SCL"]),
            new(4, "SDA", ExpandedSensorPinRole.I2cSda, 5.5, true, "Primary I2C Serial Data Line", ["I2C_SDA"]),
            new(5, "XDA", ExpandedSensorPinRole.AuxiliaryI2cSda, 3.6, false, "Auxiliary Master I2C Data Line (External Magnetometer)", ["AUX_DA"]),
            new(6, "XCL", ExpandedSensorPinRole.AuxiliaryI2cScl, 3.6, false, "Auxiliary Master I2C Clock Line (External Magnetometer)", ["AUX_CL"]),
            new(7, "AD0", ExpandedSensorPinRole.I2cAddressSelect, 5.5, true, "I2C Slave Address LSB Select (LOW=0x68, HIGH=0x69)", ["ADDR"]),
            new(8, "INT", ExpandedSensorPinRole.InterruptOutput, 3.6, false, "Data Ready / Motion Detection Interrupt Output", ["IRQ"])
        ];

        return new ExpandedSensorDefinition(
            ModelId: "MPU6050",
            DisplayName: "InvenSense MPU6050 6-Axis Accelerometer & Gyroscope IMU (GY-521)",
            Category: "Inertial Measurement Unit",
            Protocol: ExpandedSensorProtocol.I2c,
            PinCount: 8,
            MinSupplyVoltage: 3.0,
            NominalLogicVoltage: 3.3,
            MaxSupplyVoltage: 5.5,
            Is5VCompatible: true,
            Pins: pins,
            ModelAliases: ["GY-521", "MPU-6050", "MPU6050_IMU"]);
    }

    // =========================================================================
    // 3. VL53L0X: Time-of-Flight Distance (VIN, GND, SCL, SDA, XSHUT, GPIO1) - 2.8V-5V compatible
    // =========================================================================
    private static ExpandedSensorDefinition BuildVl53L0X()
    {
        ExpandedSensorPinSpec[] pins =
        [
            new(1, "VIN", ExpandedSensorPinRole.PowerSupply, 5.5, true, "Power Supply Input (2.8V to 5.0V Regulated)", ["VCC", "VDD", "+5V", "3V3"]),
            new(2, "GND", ExpandedSensorPinRole.Ground, 0.0, true, "System Ground Reference (0V)", ["VSS"]),
            new(3, "SCL", ExpandedSensorPinRole.I2cScl, 5.5, true, "I2C Serial Clock Input (Level-Shifted 2.8V–5V)", ["I2C_SCL"]),
            new(4, "SDA", ExpandedSensorPinRole.I2cSda, 5.5, true, "I2C Serial Data Bidirectional Line (Level-Shifted 2.8V–5V)", ["I2C_SDA"]),
            new(5, "XSHUT", ExpandedSensorPinRole.ResetOrShutdown, 5.5, true, "Active-Low Hardware Standby / Shutdown Input", ["SHDN", "SHUTDOWN", "RST"]),
            new(6, "GPIO1", ExpandedSensorPinRole.InterruptOutput, 3.6, false, "Programmable Range Threshold Interrupt Output (Open-Drain)", ["INT", "GPIO"])
        ];

        return new ExpandedSensorDefinition(
            ModelId: "VL53L0X",
            DisplayName: "STMicroelectronics VL53L0X Time-of-Flight Laser Distance Sensor",
            Category: "Optical Distance Sensor",
            Protocol: ExpandedSensorProtocol.I2c,
            PinCount: 6,
            MinSupplyVoltage: 2.8,
            NominalLogicVoltage: 3.3,
            MaxSupplyVoltage: 5.5,
            Is5VCompatible: true,
            Pins: pins,
            ModelAliases: ["GY-VL53L0XV2", "VL53L0XV2", "TOF200C"]);
    }

    // =========================================================================
    // 4. INA219: High-Side Current & Power (VCC, GND, SCL, SDA, VIN+, VIN-) - 3.3V/5V compatible
    // =========================================================================
    private static ExpandedSensorDefinition BuildIna219()
    {
        ExpandedSensorPinSpec[] pins =
        [
            new(1, "VCC", ExpandedSensorPinRole.PowerSupply, 5.5, true, "Chip Logic & Supply Input (3.0V to 5.5V)", ["VIN", "VDD", "+5V", "3V3"]),
            new(2, "GND", ExpandedSensorPinRole.Ground, 0.0, true, "System Ground Reference (0V)", ["VSS"]),
            new(3, "SCL", ExpandedSensorPinRole.I2cScl, 5.5, true, "I2C Serial Clock Line", ["I2C_SCL"]),
            new(4, "SDA", ExpandedSensorPinRole.I2cSda, 5.5, true, "I2C Serial Data Line", ["I2C_SDA"]),
            new(5, "VIN+", ExpandedSensorPinRole.HighSideShuntPositive, 26.0, true, "High-Side Current Sense Shunt Positive Input (Supply Side, 0–26V)", ["IN+", "V+"]),
            new(6, "VIN-", ExpandedSensorPinRole.HighSideShuntNegative, 26.0, true, "High-Side Current Sense Shunt Negative Output (Load Side, 0–26V)", ["IN-", "V-"])
        ];

        return new ExpandedSensorDefinition(
            ModelId: "INA219",
            DisplayName: "Texas Instruments INA219 High-Side DC Current & Power Monitor",
            Category: "Current & Power Sensor",
            Protocol: ExpandedSensorProtocol.I2c,
            PinCount: 6,
            MinSupplyVoltage: 3.0,
            NominalLogicVoltage: 3.3,
            MaxSupplyVoltage: 5.5,
            Is5VCompatible: true,
            Pins: pins,
            ModelAliases: ["GY-INA219", "INA219B", "CJMCU-219"]);
    }

    // =========================================================================
    // 5. RC522: RFID SPI Reader (SDA, SCK, MOSI, MISO, IRQ, GND, RST, 3.3V) - Strict 3.3V
    // =========================================================================
    private static ExpandedSensorDefinition BuildRc522()
    {
        ExpandedSensorPinSpec[] pins =
        [
            new(1, "SDA", ExpandedSensorPinRole.SpiChipSelect, 3.6, false, "SPI Slave Select (SS / CS) Input (Labeled SDA on RC522 silkscreen)", ["SS", "CS", "NSS"]),
            new(2, "SCK", ExpandedSensorPinRole.SpiSck, 3.6, false, "SPI Serial Clock Input (Strict 3.3V Logic)", ["CLK", "SCLK"]),
            new(3, "MOSI", ExpandedSensorPinRole.SpiMosi, 3.6, false, "SPI Master-Out Slave-In Data Line (Strict 3.3V Logic)", ["SDI"]),
            new(4, "MISO", ExpandedSensorPinRole.SpiMiso, 3.6, false, "SPI Master-In Slave-Out Data Line (Strict 3.3V Logic)", ["SDO"]),
            new(5, "IRQ", ExpandedSensorPinRole.InterruptOutput, 3.6, false, "Contactless Tag Detection Interrupt Output", ["INT"]),
            new(6, "GND", ExpandedSensorPinRole.Ground, 0.0, true, "System Ground Reference (0V)", ["VSS"]),
            new(7, "RST", ExpandedSensorPinRole.ResetOrShutdown, 3.6, false, "Reset and Power-Down Input (Strict 3.3V Logic)", ["RESET", "NRSTPD"]),
            new(8, "3.3V", ExpandedSensorPinRole.PowerSupply, 3.6, false, "Strict 3.3V DC Power Supply Input (5V WILL DESTROY MFRC522)", ["3V3", "VCC", "VDD"])
        ];

        return new ExpandedSensorDefinition(
            ModelId: "RC522",
            DisplayName: "NXP MFRC522 13.56MHz RFID SPI Reader Module (Strict 3.3V)",
            Category: "RFID / NFC Transceiver",
            Protocol: ExpandedSensorProtocol.Spi,
            PinCount: 8,
            MinSupplyVoltage: 2.5,
            NominalLogicVoltage: 3.3,
            MaxSupplyVoltage: 3.6,
            Is5VCompatible: false,
            Pins: pins,
            ModelAliases: ["MFRC522", "RFID-RC522", "RC-522"]);
    }
}
