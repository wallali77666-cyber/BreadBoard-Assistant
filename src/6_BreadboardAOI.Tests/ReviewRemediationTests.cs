using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BreadboardAOI.Core.Contracts;
using BreadboardAOI.Core.DrcEngine;
using BreadboardAOI.Core.Geometry;
using BreadboardAOI.Hardware;
using BreadboardAOI.Intelligence.Remote;
using FluentAssertions;
using Xunit;

namespace BreadboardAOI.Tests;

/// <summary>
/// Regression coverage for the senior-review remediation: ring-buffer lifetime,
/// DRC determinism/thread safety, power-short naming, pairing policy, encrypted
/// tokens and netlist diagnostics.
/// </summary>
public sealed class ReviewRemediationTests
{
    private static readonly PinMatrixLookup Lookup = PinMatrixLookup.Shared;

    // ---- DRC: deterministic topology hash ---------------------------------

    [Fact]
    public void Flipping_a_resistor_keeps_the_same_topology_hash()
    {
        Lookup.TryGetHole("A15", out HoleCoord a15).Should().BeTrue();
        Lookup.TryGetHole("D15", out HoleCoord d15).Should().BeTrue();

        DetectedComponent forward = Create(ComponentKind.Resistor, a15, d15);
        DetectedComponent rotated = Create(ComponentKind.Resistor, d15, a15);

        var checker = new ElectricalRulesChecker(Lookup);
        int firstHash = checker.EvaluateBoard(1, 0, new[] { forward }).StateHash;
        int secondHash = checker.EvaluateBoard(2, 0, new[] { rotated }).StateHash;

        secondHash.Should().Be(firstHash);
    }

    [Fact]
    public void Flipping_an_led_changes_the_topology_hash()
    {
        Lookup.TryGetHole("A15", out HoleCoord a15).Should().BeTrue();
        Lookup.TryGetHole("D16", out HoleCoord d16).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        int firstHash = checker.EvaluateBoard(1, 0, new[] { Create(ComponentKind.Led, a15, d16) }).StateHash;
        int secondHash = checker.EvaluateBoard(2, 0, new[] { Create(ComponentKind.Led, d16, a15) }).StateHash;

        secondHash.Should().NotBe(firstHash, "an LED is polarized, so reversing it is a different board");
    }

    // ---- DRC: power short must not masquerade as a healthy VCC ------------

    [Fact]
    public void Vcc_gnd_bridge_names_the_merged_net_as_short_and_keeps_healthy_rails_named()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 10, out HoleCoord vcc).Should().BeTrue();
        Lookup.TryGetRailHole(BreadboardSpec.StripTopGnd, 10, out HoleCoord gnd).Should().BeTrue();

        var checker = new ElectricalRulesChecker(Lookup);
        DrcStatePacket packet = checker.EvaluateBoard(3, 0, new[] { Create(ComponentKind.JumperWire, vcc, gnd) });

        packet.Violations.Should().Contain(v => v.Code == ViolationCode.ErcVccGndDirectShort);

        checker.GetResolvedNetName(BreadboardSpec.StripTopVcc).Should().Be("NET_VCC_GND_SHORT");
        checker.GetResolvedNetName(BreadboardSpec.StripTopGnd).Should().Be("NET_VCC_GND_SHORT");

        // The healthy bottom rails were not part of the bridge, so they keep their names.
        checker.GetResolvedNetName(BreadboardSpec.StripBotVcc).Should().Be("NET_VCC");
        checker.GetResolvedNetName(BreadboardSpec.StripBotGnd).Should().Be("NET_GND");
    }

    // ---- DRC: concurrent evaluation ---------------------------------------

    [Fact]
    public async Task Concurrent_evaluations_do_not_corrupt_the_union_find_state()
    {
        Lookup.TryGetRailHole(BreadboardSpec.StripTopVcc, 12, out HoleCoord vcc).Should().BeTrue();
        Lookup.TryGetHole("A12", out HoleCoord a12).Should().BeTrue();
        Lookup.TryGetHole("A15", out HoleCoord a15).Should().BeTrue();
        Lookup.TryGetHole("D15", out HoleCoord d15).Should().BeTrue();

        DetectedComponent[] detections =
        [
            Create(ComponentKind.JumperWire, vcc, a12),
            Create(ComponentKind.Resistor, a15, d15, trackId: 2)
        ];

        var checker = new ElectricalRulesChecker(Lookup);

        Task<int>[] workers = Enumerable.Range(0, 6).Select(index => Task.Run(() =>
        {
            int hash = 0;
            for (int iteration = 0; iteration < 60; iteration++)
                hash = checker.EvaluateBoard(index * 1000 + iteration, 0, detections).StateHash;

            return hash;
        })).ToArray();

        int[] hashes = await Task.WhenAll(workers);

        hashes.Should().OnlyContain(hash => hash == hashes[0], "identical input must hash identically");
        checker.GetResolvedNetName(112).Should().Be("NET_VCC");
    }

    // ---- ring buffer lifetime --------------------------------------------

    [Fact]
    public void Double_dispose_is_safe_and_leases_are_rejected_afterwards()
    {
        var buffer = new PinnedFrameRingBuffer(64, 32, bytesPerPixel: 4);

        buffer.TryBeginWrite(out FrameWriteLease lease).Should().BeTrue();
        lease.Span.Length.Should().Be(64 * 32 * 4);
        lease.Publish();

        buffer.Dispose();
        buffer.Invoking(b => b.Dispose()).Should().NotThrow();
        buffer.Invoking(b => b.TryBeginWrite(out _)).Should().Throw<ObjectDisposedException>();
    }

    [Fact]
    public async Task Publish_and_read_stress_never_tears_a_frame()
    {
        const int width = 32;
        const int height = 16;
        var buffer = new PinnedFrameRingBuffer(width, height, bytesPerPixel: 4);
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(750));

        Task producer = Task.Run(() =>
        {
            byte value = 1;
            while (!stop.IsCancellationRequested)
            {
                if (!buffer.TryBeginWrite(out FrameWriteLease lease))
                    continue;

                lease.Span.Fill(value);
                lease.Publish();
                value = value == 255 ? (byte)1 : (byte)(value + 1);
            }
        });

        Task consumer = Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                if (!buffer.TryReadLatest(out FrameReadLease lease))
                    continue;

                using (lease)
                {
                    ReadOnlySpan<byte> frame = lease.Span;
                    if (frame.IsEmpty)
                        continue;

                    // Every published frame is filled with one uniform value.
                    byte first = frame[0];
                    for (int i = 1; i < frame.Length; i += 997)
                        frame[i].Should().Be(first, "a torn frame would mix two generations");
                }
            }
        });

        await Task.WhenAll(producer, consumer).WaitAsync(TimeSpan.FromSeconds(10));
        buffer.Dispose();
    }

    // ---- pairing policy ---------------------------------------------------

    [Theory]
    [InlineData("BBAOI1|https://calm-otter.trycloudflare.com|key", true)]
    [InlineData("BBAOI1|http://127.0.0.1:8089|key", true)]
    [InlineData("BBAOI1|http://192.168.1.10:8080|key", false)]
    [InlineData("BBAOI1|http://evil.example.com|key", false)]
    public void Pair_codes_require_tls_except_on_loopback(string paste, bool accepted)
    {
        bool ok = ColabPairCode.TryParse(paste, out RemoteTutorTarget target, out string? rejection);

        ok.Should().Be(accepted);
        if (accepted)
        {
            target.Should().NotBeNull();
            rejection.Should().BeNull();
        }
        else
        {
            target.Should().BeNull();
            rejection.Should().NotBeNullOrWhiteSpace();
        }
    }

    [Theory]
    [InlineData("https://calm-otter.trycloudflare.com", true)]
    [InlineData("https://127.0.0.1:8443", true)]
    [InlineData("http://127.0.0.1:1234", true)]
    [InlineData("http://localhost:11434", true)]
    [InlineData("http://10.0.0.5:8080", false)]
    [InlineData("http://example.com", false)]
    [InlineData("ftp://example.com", false)]
    public void Secure_endpoint_policy_is_https_or_loopback(string url, bool expected)
    {
        ColabEndpoints.IsSecureEndpoint(url).Should().Be(expected);
    }

    // ---- token store encryption ------------------------------------------

    [Fact]
    public void Saved_grant_is_encrypted_at_rest_and_round_trips()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-token-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "google-session.json");

        try
        {
            var store = new GoogleTokenStore(path);
            var grant = new GoogleTokenSet
            {
                AccessToken = "ya29.access-token-value",
                RefreshToken = "1//refresh-token-value",
                ExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1),
                Scope = "https://www.googleapis.com/auth/drive.file",
                TokenType = "Bearer"
            };

            store.Save(grant).Should().BeTrue();
            File.Exists(path).Should().BeTrue();

            byte[] raw = File.ReadAllBytes(path);
            string asText = Encoding.UTF8.GetString(raw);
            asText.Should().NotContain("refresh-token-value");
            asText.Should().NotContain("access-token-value");

            if (OperatingSystem.IsWindows())
                asText.Should().StartWith("BBAOIDP1", "DPAPI ciphertext carries a magic header");

            GoogleTokenSet? loaded = store.Load();
            loaded.Should().NotBeNull();
            loaded!.AccessToken.Should().Be(grant.AccessToken);
            loaded.RefreshToken.Should().Be(grant.RefreshToken);
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Legacy_plaintext_grant_still_loads()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-token-" + Guid.NewGuid().ToString("N"));
        string path = Path.Combine(directory, "google-session.json");

        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllText(
                path,
                """
                {
                  "accessToken": "legacy-access",
                  "refreshToken": "legacy-refresh",
                  "tokenType": "Bearer"
                }
                """);

            GoogleTokenSet? loaded = new GoogleTokenStore(path).Load();

            loaded.Should().NotBeNull();
            loaded!.AccessToken.Should().Be("legacy-access");
            loaded.RefreshToken.Should().Be("legacy-refresh");
        }
        finally
        {
            if (Directory.Exists(directory))
                Directory.Delete(directory, recursive: true);
        }
    }

    // ---- netlist loader ---------------------------------------------------

    [Fact]
    public void Try_load_from_a_directory_path_returns_false_instead_of_throwing()
    {
        string directory = Path.Combine(Path.GetTempPath(), "bbaoi-netlist-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);

        try
        {
            bool ok = TargetSchematicLoader.TryLoadFromFile(directory, out TargetSchematic? schematic, out string? error);

            ok.Should().BeFalse();
            schematic.Should().BeNull();
            error.Should().NotBeNullOrWhiteSpace();
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Test_points_and_multi_pin_components_are_kept_in_pin_order()
    {
        const string json = """
            {
              "name": "partial",
              "nets": [ { "name": "NET_VCC", "holes": [ "A1" ] } ],
              "components": [
                { "ref": "TP1", "kind": "Unknown", "pin1": "A1" },
                { "ref": "R1", "kind": "Resistor", "pin1": "A1", "pin2": "B1" },
                { "ref": "U1", "kind": "IntegratedCircuit",
                  "pins": [ "E20", "E21", "E22", "E23", "F23", "F22", "F21", "F20" ] },
                { "ref": "X9", "kind": "FluxCapacitor", "pin1": "C1", "pin2": "D1" }
              ]
            }
            """;

        TargetSchematic schematic = TargetSchematicLoader.Parse(json);

        schematic.Components.Should().HaveCount(4);

        TargetComponent testPoint = schematic.Components.Single(c => c.Reference == "TP1");
        testPoint.IsTestPoint.Should().BeTrue();
        testPoint.PinCount.Should().Be(1);
        testPoint.Pin1HoleId.Should().Be("A1");
        testPoint.Pin2HoleId.Should().Be("A1");

        TargetComponent dip = schematic.Components.Single(c => c.Reference == "U1");
        dip.PinCount.Should().Be(8);
        dip.PinHoleIds.Should().Equal("E20", "E21", "E22", "E23", "F23", "F22", "F21", "F20");
        dip.Pin1HoleId.Should().Be("E20");
        dip.Pin2HoleId.Should().Be("E21");

        schematic.Components.Single(c => c.Reference == "R1").PinHoleIds.Should().Equal("A1", "B1");

        // An unknown symbol is still loaded (it can be shown to the operator) but
        // its name is reported so a typo in the netlist is visible.
        schematic.HasWarnings.Should().BeTrue();
        schematic.Warnings.Should().Contain(w => w.Contains("FluxCapacitor", StringComparison.Ordinal));
        schematic.Warnings.Should().NotContain(w => w.Contains("TP1", StringComparison.Ordinal));
    }

    [Fact]
    public void A_component_without_any_pin_hole_is_reported_and_skipped()
    {
        const string json = """
            { "name": "empty", "components": [ { "ref": "R9", "kind": "Resistor" } ] }
            """;

        TargetSchematic schematic = TargetSchematicLoader.Parse(json);

        schematic.Components.Should().BeEmpty();
        schematic.Warnings.Should().Contain(w => w.Contains("R9", StringComparison.Ordinal));
    }

    // ---- helpers ----------------------------------------------------------

    private static DetectedComponent Create(ComponentKind kind, HoleCoord lead1, HoleCoord lead2, int trackId = 1)
    {
        float minX = MathF.Min(lead1.Px, lead2.Px) - 6f;
        float minY = MathF.Min(lead1.Py, lead2.Py) - 6f;
        float width = MathF.Abs(lead2.Px - lead1.Px) + 12f;
        float height = MathF.Abs(lead2.Py - lead1.Py) + 12f;

        return new DetectedComponent(
            TrackId: trackId,
            Kind: kind,
            Confidence: 0.97f,
            BoxX: minX,
            BoxY: minY,
            BoxW: width,
            BoxH: height,
            Lead1Px: lead1.Px,
            Lead1Py: lead1.Py,
            Lead2Px: lead2.Px,
            Lead2Py: lead2.Py,
            DecodedValueLabel: null);
    }
}
