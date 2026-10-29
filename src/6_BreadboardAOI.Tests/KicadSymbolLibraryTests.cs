// ============================================================================
// FILE: src/6_BreadboardAOI.Tests/KicadSymbolLibraryTests.cs
// PURPOSE: Guards the bundled KiCad symbol reader: archive discovery, search,
//          graphics/pin parsing, and the "extends" inheritance used by NE555P.
// ============================================================================

using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.KiCad;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class KicadSymbolLibraryTests
{
    [Fact]
    public void Bundled_archive_is_found_from_the_test_output()
    {
        bool opened = KicadSymbolLibrary.TryOpenDefault(out KicadSymbolLibrary? library, out string? error);

        opened.Should().BeTrue(error);
        library.Should().NotBeNull();
        library!.SymbolCount.Should().BeGreaterThan(20_000, "the full official library is bundled");
        library.Libraries.Should().Contain(new[] { "Device", "Timer", "74xx" });

        library.Dispose();
    }

    [Fact]
    public void Resistor_symbol_parses_body_and_two_pins()
    {
        using KicadSymbolLibrary library = OpenLibrary();
        KicadSymbol? resistor = library.LoadById("Device/R");

        resistor.Should().NotBeNull();
        resistor!.Name.Should().Be("R");
        resistor.Reference.Should().Be("R");
        resistor.Pins.Should().HaveCount(2);
        resistor.Graphics.Should().NotBeEmpty("a resistor has a body rectangle");

        resistor.Pins.Select(pin => pin.Number).Should().BeEquivalentTo(new[] { "1", "2" });
        resistor.Width.Should().BeGreaterThan(0f);
        resistor.Height.Should().BeGreaterThan(0f);
        resistor.Graphics.Should().Contain(graphic => graphic.Kind == KicadGraphicKind.Rectangle);
    }

    [Fact]
    public void Derived_555_inherits_pins_and_graphics_from_its_base()
    {
        using KicadSymbolLibrary library = OpenLibrary();
        KicadSymbol? timer = library.LoadById("Timer/NE555P");

        timer.Should().NotBeNull();
        timer!.BaseName.Should().Be("NE555D", "NE555P is declared with (extends \"NE555D\")");
        timer.Pins.Should().HaveCount(8, "the DIP-8 pinout comes from the base symbol");
        timer.Graphics.Should().NotBeEmpty("the base symbol carries the body");
        timer.Reference.Should().Be("U");
        timer.Pins.Select(pin => pin.Number).Should().Contain(new[] { "1", "4", "8" });
    }

    [Fact]
    public void Search_ranks_exact_names_first()
    {
        using KicadSymbolLibrary library = OpenLibrary();

        IReadOnlyList<KicadSymbolRef> hits = library.Search("NE555P", 10);

        hits.Should().NotBeEmpty();
        hits[0].FullId.Should().Be("Timer/NE555P");
    }

    [Fact]
    public void Search_finds_microcontrollers_and_logic()
    {
        using KicadSymbolLibrary library = OpenLibrary();

        library.Search("ATmega328", 10).Should().Contain(entry => entry.FullId == "MCU_Microchip_ATmega/ATmega328P-P");
        library.Search("74LS00", 10).Should().Contain(entry => entry.FullId == "74xx/74LS00");
        library.Search(string.Empty, 10).Should().NotBeEmpty("an empty query lists starter symbols");
    }

    [Fact]
    public void Unknown_symbol_returns_null_instead_of_throwing()
    {
        using KicadSymbolLibrary library = OpenLibrary();

        library.LoadById("Device/ThisPartDoesNotExist").Should().BeNull();
        library.LoadById("NotALibrary/Whatever").Should().BeNull();
        library.LoadById(null).Should().BeNull();
        library.Contains("Device/R").Should().BeTrue();
        library.Contains("Device/Nope").Should().BeFalse();
    }

    [Fact]
    public void Every_part_kind_has_a_default_symbol_in_the_archive()
    {
        using KicadSymbolLibrary library = OpenLibrary();

        foreach (ComponentKind kind in new[]
                 {
                     ComponentKind.Resistor, ComponentKind.Capacitor, ComponentKind.Led,
                     ComponentKind.IntegratedCircuit, ComponentKind.PushButton
                 })
        {
            string id = KicadSymbolLibrary.DefaultSymbolId(kind);
            library.Contains(id).Should().BeTrue($"{kind} defaults to {id}");
        }
    }

    [Fact]
    public void Eight_pin_dip_loads_all_pins_for_the_schematic_sheet()
    {
        using KicadSymbolLibrary library = OpenLibrary();
        KicadSymbol? gate = library.LoadById("74xx/74LS00");

        gate.Should().NotBeNull();
        gate!.Pins.Should().NotBeEmpty();
        gate.Pins.Should().OnlyHaveUniqueItems(pin => pin.Number);
        gate.Pins.Should().Contain(pin => pin.Number == "1" || pin.Number == "14");
    }

    private static KicadSymbolLibrary OpenLibrary()
    {
        if (!KicadSymbolLibrary.TryOpenDefault(out KicadSymbolLibrary? library, out string? error))
            throw new InvalidOperationException(error);

        return library!;
    }
}
