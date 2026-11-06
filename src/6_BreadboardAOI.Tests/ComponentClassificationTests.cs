using System;
using System.Collections.Generic;
using System.Linq;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class ComponentClassificationTests
{
    [Theory]
    [InlineData("220R", 2, 2, 1, 10)]       // Red-Red-Brown-Gold = 220 Ohm
    [InlineData("220", 2, 2, 1, 10)]
    [InlineData("220 ohm", 2, 2, 1, 10)]
    [InlineData("220 Ω", 2, 2, 1, 10)]
    [InlineData("330R", 3, 3, 1, 10)]       // Orange-Orange-Brown-Gold = 330 Ohm
    [InlineData("470R", 4, 7, 1, 10)]       // Yellow-Violet-Brown-Gold = 470 Ohm
    [InlineData("1K", 1, 0, 2, 10)]         // Brown-Black-Red-Gold = 1k Ohm
    [InlineData("1kΩ", 1, 0, 2, 10)]
    [InlineData("2K2", 2, 2, 2, 10)]       // Red-Red-Red-Gold = 2.2k Ohm
    [InlineData("4K7", 4, 7, 2, 10)]       // Yellow-Violet-Red-Gold = 4.7k Ohm
    [InlineData("4.7K", 4, 7, 2, 10)]
    [InlineData("10K", 1, 0, 3, 10)]       // Brown-Black-Orange-Gold = 10k Ohm
    [InlineData("47K", 4, 7, 3, 10)]       // Yellow-Violet-Orange-Gold = 47k Ohm
    [InlineData("100K", 1, 0, 4, 10)]      // Brown-Black-Yellow-Gold = 100k Ohm
    [InlineData("1M", 1, 0, 5, 10)]        // Brown-Black-Green-Gold = 1M Ohm
    [InlineData("10R", 1, 0, 0, 10)]       // Brown-Black-Black-Gold = 10 Ohm
    [InlineData("1R", 1, 0, 10, 10)]       // Brown-Black-Gold-Gold = 1 Ohm (gold multiplier x0.1)
    public void Resistor_value_decodes_to_exact_eia_colour_bands(string value, int b1, int b2, int mult, int tol)
    {
        Span<int> bands = stackalloc int[4];
        bool success = ResistorColorCode.TryDecodeBands(value, bands);

        success.Should().BeTrue();
        bands[0].Should().Be(b1, "band 1 is first significant digit");
        bands[1].Should().Be(b2, "band 2 is second significant digit");
        bands[2].Should().Be(mult, "band 3 is multiplier");
        bands[3].Should().Be(tol, "band 4 is tolerance (gold = 10)");
    }

    [Fact]
    public void Jumper_colors_palette_contains_8_standard_wire_colors()
    {
        IReadOnlyList<JumperColorOption> colors = ComponentCatalog.StandardJumperColors;
        colors.Should().HaveCount(8);

        colors.Should().Contain(c => c.Code == "RED");
        colors.Should().Contain(c => c.Code == "BLACK");
        colors.Should().Contain(c => c.Code == "BLUE");
        colors.Should().Contain(c => c.Code == "YELLOW");
        colors.Should().Contain(c => c.Code == "GREEN");
        colors.Should().Contain(c => c.Code == "ORANGE");
        colors.Should().Contain(c => c.Code == "PURPLE");
        colors.Should().Contain(c => c.Code == "WHITE");
    }

    [Fact]
    public void Ic_types_catalog_contains_standard_hobbyist_and_logic_chips()
    {
        IReadOnlyList<PartPreset> ics = ComponentCatalog.StandardIcs;

        ics.Should().Contain(p => p.Value == "NE555P" && p.PinCount == 8);
        ics.Should().Contain(p => p.Value == "LM358" && p.PinCount == 8);
        ics.Should().Contain(p => p.Value == "74LS00" && p.PinCount == 14);
        ics.Should().Contain(p => p.Value == "74LS04" && p.PinCount == 14);
        ics.Should().Contain(p => p.Value == "CD4017BE" && p.PinCount == 16);
        ics.Should().Contain(p => p.Value == "L293D" && p.PinCount == 16);
        ics.Should().Contain(p => p.Value == "ATmega328P" && p.PinCount == 28);
    }

    [Fact]
    public void Capacitor_presets_format_values_in_farads()
    {
        IReadOnlyList<PartPreset> caps = ComponentCatalog.StandardCapacitors;

        // Ceramic (pF / nF)
        caps.Should().Contain(p => p.Value == "100pF");
        caps.Should().Contain(p => p.Value == "1nF");
        caps.Should().Contain(p => p.Value == "10nF");
        caps.Should().Contain(p => p.Value == "100nF");

        // Electrolytic (uF)
        caps.Should().Contain(p => p.Value == "1uF");
        caps.Should().Contain(p => p.Value == "10uF");
        caps.Should().Contain(p => p.Value == "100uF");
        caps.Should().Contain(p => p.Value == "1000uF");
    }

    [Fact]
    public void Diode_presets_contain_switching_rectifier_schottky_and_zener()
    {
        IReadOnlyList<PartPreset> diodes = ComponentCatalog.StandardDiodes;
        diodes.Should().Contain(d => d.Value == "1N4148");
        diodes.Should().Contain(d => d.Value == "1N4007");
        diodes.Should().Contain(d => d.Value == "1N5819");
        diodes.Should().Contain(d => d.Value == "3.3V ZENER");
        diodes.Should().Contain(d => d.Value == "5.1V ZENER");
        diodes.Should().Contain(d => d.Value == "12V ZENER");
    }

    [Fact]
    public void Transistor_presets_contain_npn_pnp_and_mosfets()
    {
        IReadOnlyList<PartPreset> bjtAndFet = ComponentCatalog.StandardTransistors;
        bjtAndFet.Should().Contain(t => t.Value == "2N3904" && t.PinCount == 3);
        bjtAndFet.Should().Contain(t => t.Value == "2N2222" && t.PinCount == 3);
        bjtAndFet.Should().Contain(t => t.Value == "BC547" && t.PinCount == 3);
        bjtAndFet.Should().Contain(t => t.Value == "2N3906" && t.PinCount == 3);
        bjtAndFet.Should().Contain(t => t.Value == "2N7000" && t.PinCount == 3);
        bjtAndFet.Should().Contain(t => t.Value == "BS170" && t.PinCount == 3);
    }

    [Fact]
    public void Potentiometer_and_regulator_catalogs_contain_standard_parts()
    {
        IReadOnlyList<PartPreset> pots = ComponentCatalog.StandardPotentiometers;
        pots.Should().Contain(p => p.Value == "10K_TRIM" && p.PinCount == 3);
        pots.Should().Contain(p => p.Value == "1K_TRIM" && p.PinCount == 3);
        pots.Should().Contain(p => p.Value == "10K_POT" && p.PinCount == 3);

        IReadOnlyList<PartPreset> regs = ComponentCatalog.StandardRegulators;
        regs.Should().Contain(r => r.Value == "L7805" && r.PinCount == 3);
        regs.Should().Contain(r => r.Value == "LD1117V33" && r.PinCount == 3);
        regs.Should().Contain(r => r.Value == "LM317T" && r.PinCount == 3);
    }

    [Fact]
    public void Common_sensors_catalog_contains_optical_temperature_ultrasonic_and_environmental()
    {
        IReadOnlyList<PartPreset> sensors = ComponentCatalog.StandardSensors;

        // Optical
        sensors.Should().Contain(s => s.Value == "LDR GL5528" && s.PinCount == 2);
        sensors.Should().Contain(s => s.Value == "BPW34" && s.PinCount == 2);
        sensors.Should().Contain(s => s.Value == "PT334-6B" && s.PinCount == 2);

        // Temperature & Humidity
        sensors.Should().Contain(s => s.Value == "NTC 10K" && s.PinCount == 2);
        sensors.Should().Contain(s => s.Value == "TMP36" && s.PinCount == 3);
        sensors.Should().Contain(s => s.Value == "DS18B20" && s.PinCount == 3);
        sensors.Should().Contain(s => s.Value == "DHT11" && s.PinCount == 4);
        sensors.Should().Contain(s => s.Value == "DHT22" && s.PinCount == 4);

        // Motion & Distance
        sensors.Should().Contain(s => s.Value == "HC-SR04" && s.PinCount == 4);
        sensors.Should().Contain(s => s.Value == "HC-SR501" && s.PinCount == 3);
        sensors.Should().Contain(s => s.Value == "A3144" && s.PinCount == 3);
        sensors.Should().Contain(s => s.Value == "SW-520D" && s.PinCount == 2);

        // Gas & Audio
        sensors.Should().Contain(s => s.Value == "MQ-2" && s.PinCount == 4);
        sensors.Should().Contain(s => s.Value == "MIC" && s.PinCount == 2);
        sensors.Should().Contain(s => s.Value == "KY-038" && s.PinCount == 4);
    }
}
