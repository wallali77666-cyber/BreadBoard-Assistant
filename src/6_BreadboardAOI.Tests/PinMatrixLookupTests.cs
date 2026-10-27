using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

public sealed class PinMatrixLookupTests
{
    private readonly PinMatrixLookup _lookup = PinMatrixLookup.Shared;

    [Fact]
    public void A15_and_E15_share_strip_115()
    {
        _lookup.TryGetHole('A', 15, out var a15).Should().BeTrue();
        _lookup.TryGetHole('E', 15, out var e15).Should().BeTrue();

        a15.HoleId.Should().Be("A15");
        e15.HoleId.Should().Be("E15");
        a15.StripId.Should().Be(115);
        e15.StripId.Should().Be(115);

        _lookup.TrySnapToHole(a15.Px + 3.2f, a15.Py - 2.1f, out var snappedA).Should().BeTrue();
        snappedA.HoleId.Should().Be("A15");

        _lookup.TrySnapToHole(e15.Px - 4.0f, e15.Py + 1.5f, out var snappedE).Should().BeTrue();
        snappedE.HoleId.Should().Be("E15");
        snappedE.StripId.Should().Be(snappedA.StripId);
    }

    [Fact]
    public void Matrix_contains_830_holes_and_every_centre_snaps_back_exactly()
    {
        _lookup.Count.Should().Be(BreadboardSpec.TotalHoleCount);
        _lookup.Count.Should().Be(830);

        foreach (var hole in _lookup.Holes)
        {
            _lookup.TrySnapToHole(hole.Px, hole.Py, out var snapped).Should().BeTrue();
            snapped.HoleId.Should().Be(hole.HoleId);
            snapped.StripId.Should().Be(hole.StripId);
        }
    }

    [Fact]
    public void Snap_rejects_the_non_conductive_centre_trench()
    {
        // y = 200 is 25 px from both the E row (175) and the F row (225),
        // i.e. far outside the 11.5 px capture radius.
        _lookup.TrySnapToHole(500f, 200f, out _).Should().BeFalse();
        _lookup.TrySnapToHole(-100f, -100f, out _).Should().BeFalse();
    }

    [Fact]
    public void Power_rails_contribute_200_holes_with_reserved_strip_ids()
    {
        int railHoles = 0;
        foreach (var hole in _lookup.Holes)
        {
            if (BreadboardSpec.IsRailStrip(hole.StripId))
                railHoles++;
        }

        railHoles.Should().Be(BreadboardSpec.RailHoleTotal);
        railHoles.Should().Be(200);

        _lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 12, out var vcc).Should().BeTrue();
        vcc.HoleId.Should().Be("TOP_VCC_12");
        vcc.StripId.Should().Be(BreadboardSpec.StripTopVcc);

        _lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 0, out _).Should().BeFalse();
        _lookup.TryGetRailHole(99, 1, out _).Should().BeFalse();
    }

    [Fact]
    public void Nearest_free_hole_to_the_right_of_D15_is_D16()
    {
        _lookup.TryGetHole("D15", out var d15).Should().BeTrue();

        var occupied = new bool[BreadboardSpec.StripCount];
        occupied[d15.StripId] = true;

        _lookup.TryFindNearestFreeHoleInRow(d15, occupied, out var target).Should().BeTrue();
        target.HoleId.Should().Be("D16");
        target.StripId.Should().Be(116);
    }

    [Fact]
    public void GetStripHoles_returns_the_five_shared_tie_points()
    {
        var stripHoles = _lookup.GetStripHoles(115);
        stripHoles.Length.Should().Be(5);
        stripHoles.ToArray().Select(h => h.HoleId)
            .Should().Equal("A15", "B15", "C15", "D15", "E15");
    }

    [Fact]
    public void Target_netlist_ships_with_the_repository()
    {
        string? path = TargetSchematicLoader.ResolveDefaultPath();
        path.Should().NotBeNull();

        var target = TargetSchematicLoader.LoadFromFile(path!);
        target.Name.Should().Be("555_astable_blinker");
        target.Nets.Should().NotBeEmpty();
        target.Components.Should().NotBeEmpty();
    }
}
