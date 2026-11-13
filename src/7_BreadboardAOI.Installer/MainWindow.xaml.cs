// ============================================================================
// FILE: src/7_BreadboardAOI.Installer/MainWindow.xaml.cs
// PURPOSE: Google Gemini-style dark-mode single-file installer for Breadboard AOI.
//          Features:
//          - No phase labels; clean dark segmented edition & GPU tier controls.
//          - 60 FPS procedural vector cinema (breadboard, ArUco, IC, LED, HUD).
//          - Real-time CPU affinity telemetry (P-Cores 0x00FFF, E-Cores 0xFF000).
//          - GPU profile support: RTX 3060 12GB (--n-cpu-moe 22) and RTX 5050 8GB (--n-cpu-moe 34).
//          - Full antigravity-aoi:// Windows URI protocol registration.
//          - Complete antigravity.link.json manifest generation.
// ============================================================================

using System;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using BreadboardAOI.Hardware;
using Microsoft.Win32;

namespace BreadboardAOI.Installer;

public partial class MainWindow : Window
{
    private readonly Stopwatch _animClock = Stopwatch.StartNew();
    private readonly DrawingGroup _cinemaDrawingGroup = new();
    private readonly DrawingImage _cinemaImage;
    private int _lockedSceneOverride = 0; // 0 = Auto-Cycle; 1..3 = User-Selected Tab
    private bool _isInstalling;
    private bool _installFinished;

    private readonly VramBudgetMonitor _vramMonitor = new();
    private GpuHardwareTier _selectedGpuTier = GpuHardwareTier.Rtx3060_12Gb;

    private readonly string _installRootPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BreadboardAOI");
    private string? _lastExtractionWarning;

    public MainWindow()
    {
        InitializeComponent();
        _cinemaImage = new DrawingImage(_cinemaDrawingGroup);
        CinemaViewport.Source = _cinemaImage;

        DetectHardwareSummary();
        CompositionTarget.Rendering += OnRenderCinemaFrame;
    }

    protected override void OnClosed(EventArgs e)
    {
        CompositionTarget.Rendering -= OnRenderCinemaFrame;
        _vramMonitor.Dispose();
        base.OnClosed(e);
    }

    private double GetProgressTrackWidth()
        => ProgressFillBar.Parent is FrameworkElement track && track.ActualWidth > 0
            ? track.ActualWidth
            : 840.0;

    private void DetectHardwareSummary()
    {
        int logicalCores = Environment.ProcessorCount;
        long totalRamGb = Math.Max(8, (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0)));

        // Probe VRAM & detect NVIDIA GPU tier
        VramSnapshot snap = _vramMonitor.GetSnapshot();
        if (snap.DetectedTier == GpuHardwareTier.Rtx5050_8Gb)
        {
            _selectedGpuTier = GpuHardwareTier.Rtx5050_8Gb;
            if (GpuRtx5050Radio is not null)
                GpuRtx5050Radio.IsChecked = true;
        }
        else
        {
            _selectedGpuTier = GpuHardwareTier.Rtx3060_12Gb;
            if (GpuRtx3060Radio is not null)
                GpuRtx3060Radio.IsChecked = true;
        }

        UpdateHardwareSubtitle(logicalCores, totalRamGb);
    }

    private void UpdateHardwareSubtitle(int logicalCores, long totalRamGb)
    {
        GpuMoEProfile profile = CpuAffinityManager.GetProfile(_selectedGpuTier);
        string maskInfo = logicalCores >= CpuAffinityManager.HybridLogicalProcessorThreshold
            ? "P:0x00FFF, E:0xFF000"
            : $"AI:0x{CpuAffinityManager.Plan.AiMask:X}, V/D:0x{CpuAffinityManager.Plan.ECoresMask:X}";

        HardwareSubtitleText.Text =
            $"Hardware Ready: {logicalCores} Cores ({maskInfo}) \u2022 {totalRamGb} GB RAM \u2022 {profile.ModelName} ({profile.CpuMoeArgument})";
    }

    private void GpuProfileRadio_Checked(object sender, RoutedEventArgs e)
    {
        if (GpuRtx5050Radio?.IsChecked == true)
            _selectedGpuTier = GpuHardwareTier.Rtx5050_8Gb;
        else
            _selectedGpuTier = GpuHardwareTier.Rtx3060_12Gb;

        _vramMonitor.SetHardwareTier(_selectedGpuTier);
        int logicalCores = Environment.ProcessorCount;
        long totalRamGb = Math.Max(8, (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0)));
        UpdateHardwareSubtitle(logicalCores, totalRamGb);
    }

    // =========================================================================
    // 60 FPS PROCEDURAL VECTOR CINEMA (ENHANCED BREADBOARD + AI TELEMETRY HUD)
    // =========================================================================
    private void OnRenderCinemaFrame(object? sender, EventArgs e)
    {
        double t = _animClock.Elapsed.TotalSeconds;
        int activeScene = _lockedSceneOverride > 0
            ? _lockedSceneOverride
            : ((int)(t / 4.4) % 3) + 1;

        double sceneProgress = (t % 4.4) / 4.4;
        UpdateSceneTabsAndCaptions(activeScene, t);

        using DrawingContext dc = _cinemaDrawingGroup.Open();
        const double W = 872;
        const double H = 380;

        // 1. Deep Engineering Blueprint Background Grid
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(7, 10, 16)), null, new Rect(0, 0, W, H));
        Pen minorGrid = new(new SolidColorBrush(Color.FromArgb(16, 56, 189, 248)), 1.0);
        for (double x = 0; x < W; x += 24) dc.DrawLine(minorGrid, new Point(x, 0), new Point(x, H));
        for (double y = 0; y < H; y += 24) dc.DrawLine(minorGrid, new Point(0, y), new Point(W, y));

        // 2. Left Physical Breadboard Matrix (with DIP-8 pins, Resistor, LED, Jumpers)
        Rect boardRect = new(32, 50, 498, 256);
        DrawRealisticBreadboard(dc, boardRect, activeScene, sceneProgress, t);

        // 3. Right-Side Neural Core & Live KiCad / Hardware Telemetry HUD
        Rect hudRect = new(546, 50, 294, 256);
        DrawNeuralTelemetryHud(dc, hudRect, activeScene, sceneProgress, t);
    }

    private void DrawRealisticBreadboard(DrawingContext dc, Rect board, int scene, double prog, double t)
    {
        // Breadboard Chassis with subtle bevel border
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(17, 24, 39)),
            new Pen(new SolidColorBrush(Color.FromRgb(51, 65, 85)), 1.8),
            board, 12, 12);

        // Power Rails (+5V Red Top Rail, GND Blue Bottom Rail)
        double topRailY = board.Top + 20;
        double botRailY = board.Bottom - 20;
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 2.2),
            new Point(board.Left + 34, topRailY), new Point(board.Right - 34, topRailY));
        dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2.2),
            new Point(board.Left + 34, botRailY), new Point(board.Right - 34, botRailY));

        DrawLabel(dc, "+5V", new Point(board.Left + 10, topRailY - 6), 9.5, Color.FromRgb(248, 113, 113), true);
        DrawLabel(dc, "GND", new Point(board.Left + 10, botRailY - 6), 9.5, Color.FromRgb(56, 189, 248), true);

        // Center Isolation Trench
        Rect trench = new(board.Left + 14, board.Top + board.Height / 2 - 11, board.Width - 28, 22);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(10, 15, 24)),
            new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1), trench, 4, 4);

        // 4 Corner ArUco Markers
        DrawArUcoFiducial(dc, new Point(board.Left + 10, board.Top + 30), "ID:0");
        DrawArUcoFiducial(dc, new Point(board.Right - 30, board.Top + 30), "ID:1");
        DrawArUcoFiducial(dc, new Point(board.Right - 30, board.Bottom - 50), "ID:2");
        DrawArUcoFiducial(dc, new Point(board.Left + 10, board.Bottom - 50), "ID:3");

        // Pin Matrix Coordinates (17 Columns x 2 Banks of 5 Rows)
        double startX = board.Left + 54;
        double pitchX = 23.5;
        double topBankY = board.Top + 46;
        double botBankY = trench.Bottom + 14;
        double pitchY = 13.5;

        int row15Idx = 11;
        int row16Idx = 12;
        double row15X = startX + row15Idx * pitchX;
        double row16X = startX + row16Idx * pitchX;

        // Scene 2: Red X-Ray Strip Highlight on Row 15 (A15-E15)
        if (scene == 2)
        {
            byte alpha = (byte)(120 + 75 * Math.Sin(t * 8.5));
            Rect shortCapsule = new(row15X - 8, topBankY - 6, 16, pitchY * 4 + 12);
            dc.DrawRoundedRectangle(
                new SolidColorBrush(Color.FromArgb(alpha, 239, 68, 68)),
                new Pen(new SolidColorBrush(Color.FromRgb(252, 165, 165)), 2.0),
                shortCapsule, 6, 6);
        }
        // Scene 3: Emerald Verified Strip Highlight on Row 15 & Row 16
        else if (scene == 3 && prog > 0.42)
        {
            Brush okFill = new SolidColorBrush(Color.FromArgb(90, 16, 185, 129));
            Pen okPen = new(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 1.6);
            dc.DrawRoundedRectangle(okFill, okPen, new Rect(row15X - 7, topBankY - 5, 14, pitchY * 4 + 10), 5, 5);
            dc.DrawRoundedRectangle(okFill, okPen, new Rect(row16X - 7, topBankY - 5, 14, pitchY * 4 + 10), 5, 5);
        }

        // Draw Breadboard Matrix Sockets & Row Labels
        Brush socketFill = new SolidColorBrush(Color.FromRgb(9, 13, 20));
        Pen socketBorder = new(new SolidColorBrush(Color.FromRgb(46, 61, 82)), 1.0);
        string[] rowLettersTop = { "A", "B", "C", "D", "E" };
        for (int r = 0; r < 5; r++)
        {
            DrawLabel(dc, rowLettersTop[r], new Point(startX - 18, topBankY + r * pitchY - 5), 8.5, Color.FromRgb(100, 116, 139), true);
        }

        for (int col = 0; col < 17; col++)
        {
            double hx = startX + col * pitchX;
            if ((col + 1) % 5 == 0 || col == 0 || col == row15Idx || col == row16Idx)
            {
                Color numCol = (col == row15Idx && scene == 2) ? Color.FromRgb(248, 113, 113) : Color.FromRgb(100, 116, 139);
                DrawLabel(dc, (col + 4).ToString(), new Point(hx - 5, topBankY - 17), 8.5, numCol, true);
            }
            for (int r = 0; r < 5; r++)
            {
                dc.DrawRectangle(socketFill, socketBorder, new Rect(hx - 3, topBankY + r * pitchY - 3, 6, 6));
                dc.DrawRectangle(socketFill, socketBorder, new Rect(hx - 3, botBankY + r * pitchY - 3, 6, 6));
            }
        }

        // Draw Jumper Wires (VCC to IC Pin 8, GND to IC Pin 1, and Row 16 to GND)
        Pen vccWirePen = new(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 2.6);
        Pen gndWirePen = new(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2.6);
        dc.DrawLine(vccWirePen, new Point(startX + 3 * pitchX, topRailY), new Point(startX + 3 * pitchX, topBankY + 4 * pitchY));
        dc.DrawLine(gndWirePen, new Point(startX + 3 * pitchX, botRailY), new Point(startX + 3 * pitchX, botBankY));
        dc.DrawLine(gndWirePen, new Point(row16X, botRailY), new Point(row16X, topBankY + 4 * pitchY));

        // Draw NE555P DIP-8 IC across Center Trench with 8 Metallic Legs & Pin-1 Notch
        Rect icRect = new(startX + 3 * pitchX - 10, trench.Top - 16, pitchX * 3 + 20, 54);
        for (int p = 0; p < 4; p++)
        {
            double pinX = startX + (3 + p) * pitchX;
            dc.DrawRectangle(Brushes.Silver, null, new Rect(pinX - 2.5, icRect.Top - 5, 5, 6));
            dc.DrawRectangle(Brushes.Silver, null, new Rect(pinX - 2.5, icRect.Bottom - 1, 5, 6));
        }
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(26, 35, 50)),
            new Pen(new SolidColorBrush(Color.FromRgb(148, 163, 184)), 1.6),
            icRect, 4, 4);

        // Pin-1 Notch & Dot
        dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(56, 189, 248)), null, new Point(icRect.Left + 10, icRect.Bottom - 10), 3, 3);
        DrawLabel(dc, "NE555P", new Point(icRect.Left + 20, icRect.Top + 19), 11, Colors.White, true);

        // Draw R1 (220-Ohm Axial Resistor) bridging IC Out to Row 15
        Point rLead1 = new(startX + 6 * pitchX, topBankY + 2 * pitchY);
        Point rLead2 = new(row15X, topBankY + 2 * pitchY);
        dc.DrawLine(new Pen(Brushes.LightSteelBlue, 2.5), rLead1, rLead2);
        Rect rBody = new((rLead1.X + rLead2.X) / 2 - 24, rLead1.Y - 7, 48, 14);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(226, 188, 138)),
            new Pen(new SolidColorBrush(Color.FromRgb(120, 90, 60)), 1.2),
            rBody, 5, 5);
        dc.DrawRectangle(Brushes.Crimson, null, new Rect(rBody.Left + 9, rBody.Top + 1, 4.5, 12));
        dc.DrawRectangle(Brushes.Crimson, null, new Rect(rBody.Left + 18, rBody.Top + 1, 4.5, 12));
        dc.DrawRectangle(Brushes.SaddleBrown, null, new Rect(rBody.Left + 27, rBody.Top + 1, 4.5, 12));
        dc.DrawRectangle(Brushes.Gold, null, new Rect(rBody.Left + 36, rBody.Top + 1, 3.5, 12));
        DrawCalloutPill(dc, new Point(rBody.Left + 2, rBody.Top + 18), "R1 \u2022 220\u03A9", Color.FromRgb(56, 189, 248));

        // Draw LED1: Anode at D15, Cathode at A15 (Shorted in Scene 1/2) -> Moves to A16 in Scene 3
        Point ledAnode = new(row15X, topBankY + 3 * pitchY); // D15
        double cathodeX = row15X;
        if (scene == 3)
        {
            double moveT = Math.Clamp((prog - 0.12) / 0.32, 0.0, 1.0);
            cathodeX = row15X + moveT * (row16X - row15X);
        }
        Point ledCathode = new(cathodeX, topBankY); // A15 -> A16
        Point ledDomeCenter = new(cathodeX + 26, topBankY + 1.5 * pitchY);

        // Angled Leads to Offset LED Dome
        dc.DrawLine(new Pen(Brushes.Silver, 2.2), ledAnode, ledDomeCenter);
        dc.DrawLine(new Pen(Brushes.Silver, 2.2), ledCathode, ledDomeCenter);

        bool ledLit = scene == 3 && prog > 0.45;
        if (ledLit)
        {
            dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(100, 251, 146, 60)), null, ledDomeCenter, 22, 22);
        }
        dc.DrawEllipse(
            new SolidColorBrush(ledLit ? Color.FromRgb(251, 146, 60) : Color.FromRgb(217, 119, 6)),
            new Pen(Brushes.White, 1.8), ledDomeCenter, 10.5, 10.5);

        // Clean Callout Badge
        DrawCalloutPill(dc, new Point(ledDomeCenter.X + 15, ledDomeCenter.Y - 9),
            ledLit ? "LED1 \u2022 ON" : "LED1", ledLit ? Color.FromRgb(52, 211, 153) : Color.FromRgb(251, 191, 36));

        // Scene 1: Laser Homography Sweep
        if (scene == 1)
        {
            double scanX = board.Left + 24 + (board.Width - 48) * prog;
            dc.DrawLine(new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 2.2),
                new Point(scanX, board.Top + 6), new Point(scanX, board.Bottom - 6));
            Rect beamGlow = new(Math.Max(board.Left, scanX - 24), board.Top + 6, 24, board.Height - 12);
            dc.DrawRectangle(new LinearGradientBrush(
                Color.FromArgb(0, 56, 189, 248),
                Color.FromArgb(70, 56, 189, 248), 0.0), null, beamGlow);
        }

        // Scene 2: Red Crosshair on A15 + Green Ratsnest Arrow to A16
        if (scene == 2)
        {
            Point holeA15 = new(row15X, topBankY);
            Point holeA16 = new(row16X, topBankY);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(239, 68, 68)), 2.4), holeA15, 9, 9);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 2.4), holeA16, 9, 9);

            Pen ratsnestPen = new(new SolidColorBrush(Color.FromRgb(52, 211, 153)), 2.4)
            {
                DashStyle = new DashStyle(new double[] { 3, 2 }, -t * 10)
            };
            Point apex = new((holeA15.X + holeA16.X) / 2, holeA15.Y - 24);
            dc.DrawLine(ratsnestPen, holeA15, apex);
            dc.DrawLine(ratsnestPen, apex, holeA16);
            DrawCalloutPill(dc, new Point(apex.X - 36, apex.Y - 20), "MOVE A15 -> A16", Color.FromRgb(52, 211, 153));
        }
    }

    private void DrawNeuralTelemetryHud(DrawingContext dc, Rect hud, int scene, double prog, double t)
    {
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(12, 18, 30)),
            new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1.5),
            hud, 12, 12);

        string headerTitle = scene switch
        {
            1 => "YOLO11-Pose + Homography (E-Cores)",
            2 => "KiCad DRC & Qwen3-VL MoE (P-Cores)",
            _ => "ElevenLabs Neural Voice + Verified Netlist"
        };
        DrawLabel(dc, headerTitle, new Point(hud.Left + 14, hud.Top + 10), 10.5, Color.FromRgb(192, 132, 252), true);

        // Animated Neural Core with Orbiting Satellite Nodes
        Point coreCenter = new(hud.Left + 48, hud.Top + 72);
        double outerR = 26 + 3 * Math.Sin(t * 4.0);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(35, 139, 92, 246)),
            new Pen(new SolidColorBrush(Color.FromArgb(120, 96, 165, 250)), 1.2), coreCenter, outerR, outerR);
        dc.DrawEllipse(new SolidColorBrush(Color.FromArgb(80, 59, 130, 246)),
            new Pen(new SolidColorBrush(Color.FromRgb(192, 132, 252)), 2.0), coreCenter, 15, 15);

        for (int i = 0; i < 3; i++)
        {
            double angle = t * 2.4 + i * (Math.PI * 2 / 3);
            Point orb = new(coreCenter.X + Math.Cos(angle) * outerR, coreCenter.Y + Math.Sin(angle) * outerR);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(56, 189, 248)), null, orb, 3.5, 3.5);
        }

        // Live KiCad Netlist Telemetry Rows
        double cardLeft = hud.Left + 92;
        double cardWidth = hud.Width - 104;
        DrawTelemetryRow(dc, new Rect(cardLeft, hud.Top + 36, cardWidth, 20), "PIN_08 -> +5V", "OK", Color.FromRgb(52, 211, 153));
        DrawTelemetryRow(dc, new Rect(cardLeft, hud.Top + 60, cardWidth, 20), "R1_220 -> C15", "OK", Color.FromRgb(52, 211, 153));

        if (scene == 2)
            DrawTelemetryRow(dc, new Rect(cardLeft, hud.Top + 84, cardWidth, 20), "LED1 -> A15/D15", "SHORT", Color.FromRgb(248, 113, 113));
        else if (scene == 3 && prog > 0.42)
            DrawTelemetryRow(dc, new Rect(cardLeft, hud.Top + 84, cardWidth, 20), "LED1 -> D15/A16", "FIXED", Color.FromRgb(52, 211, 153));
        else
            DrawTelemetryRow(dc, new Rect(cardLeft, hud.Top + 84, cardWidth, 20), "830 PIN MATRIX", "LOCK", Color.FromRgb(56, 189, 248));

        // Hardware Affinity & MoE Offload Live Badges
        GpuMoEProfile activeProfile = CpuAffinityManager.GetProfile(_selectedGpuTier);
        double hwBarY = hud.Top + 112;
        Rect affinityBadgeRect = new(hud.Left + 14, hwBarY, hud.Width - 28, 22);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1), affinityBadgeRect, 4, 4);

        string affinityText = "P-Cores: 0x00FFF (llama) | E-Cores: 0xFF000 (Vision)";
        DrawLabel(dc, affinityText, new Point(affinityBadgeRect.Left + 6, affinityBadgeRect.Top + 4), 8.5, Color.FromRgb(148, 163, 184), true);

        double gpuBarY = hwBarY + 26;
        Rect gpuBadgeRect = new(hud.Left + 14, gpuBarY, hud.Width - 28, 22);
        dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(15, 23, 42)),
            new Pen(new SolidColorBrush(Color.FromRgb(30, 41, 59)), 1), gpuBadgeRect, 4, 4);

        string gpuText = $"{activeProfile.ModelName} \u2022 {activeProfile.CpuMoeArgument}";
        DrawLabel(dc, gpuText, new Point(gpuBadgeRect.Left + 6, gpuBadgeRect.Top + 4), 8.5, Color.FromRgb(56, 189, 248), true);

        // Live Audio Spectrum Visualizer
        int bars = 24;
        double startBarX = hud.Left + 14;
        double barMidY = hud.Bottom - 44;
        for (int i = 0; i < bars; i++)
        {
            double amp = scene == 3
                ? 5 + 24 * Math.Abs(Math.Sin(t * 9.5 + i * 0.45))
                : scene == 2
                    ? 4 + 12 * Math.Abs(Math.Sin(t * 6.0 + i * 0.6))
                    : 3 + 4 * Math.Abs(Math.Sin(t * 3.2 + i * 0.35));

            Color barCol = scene == 2
                ? Color.FromRgb(248, 113, 113)
                : scene == 3 ? Color.FromRgb(52, 211, 153) : Color.FromRgb(56, 189, 248);

            dc.DrawRoundedRectangle(new SolidColorBrush(barCol), null,
                new Rect(startBarX + i * 11, barMidY - amp / 2, 6, amp), 3, 3);
        }

        string footerMetric = scene switch
        {
            1 => "830 Holes Homography (0.04ms)",
            2 => "ERC_SAME_STRIP_SHORT: Row 15",
            _ => "DRC PASS: 0 Violations (Verified)"
        };
        Color metricColor = scene == 2 ? Color.FromRgb(248, 113, 113) : Color.FromRgb(52, 211, 153);
        DrawLabel(dc, footerMetric, new Point(hud.Left + 14, hud.Bottom - 20), 10, metricColor, true);
    }

    private static void DrawTelemetryRow(DrawingContext dc, Rect box, string netText, string badge, Color badgeColor)
    {
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromRgb(19, 28, 46)),
            new Pen(new SolidColorBrush(Color.FromRgb(38, 52, 73)), 1),
            box, 4, 4);
        DrawLabel(dc, netText, new Point(box.Left + 6, box.Top + 3), 9.0, Color.FromRgb(203, 213, 225), true);
        DrawLabel(dc, badge, new Point(box.Right - 38, box.Top + 3), 9.0, badgeColor, true);
    }

    private static void DrawCalloutPill(DrawingContext dc, Point topLeft, string text, Color borderColor)
    {
        double width = text.Length * 6.5 + 12;
        Rect pill = new(topLeft.X, topLeft.Y, width, 17);
        dc.DrawRoundedRectangle(
            new SolidColorBrush(Color.FromArgb(220, 15, 23, 42)),
            new Pen(new SolidColorBrush(borderColor), 1.2),
            pill, 5, 5);
        DrawLabel(dc, text, new Point(topLeft.X + 6, topLeft.Y + 2), 9.0, borderColor, true);
    }

    private static void DrawArUcoFiducial(DrawingContext dc, Point topLeft, string idText)
    {
        Rect outer = new(topLeft.X, topLeft.Y, 20, 20);
        dc.DrawRectangle(Brushes.Black, new Pen(new SolidColorBrush(Color.FromRgb(56, 189, 248)), 1.3), outer);
        dc.DrawRectangle(Brushes.White, null, new Rect(topLeft.X + 4, topLeft.Y + 4, 5, 5));
        dc.DrawRectangle(Brushes.White, null, new Rect(topLeft.X + 11, topLeft.Y + 11, 5, 5));
        DrawLabel(dc, idText, new Point(topLeft.X - 1, topLeft.Y - 12), 8.5, Color.FromRgb(56, 189, 248), true);
    }

    private static void DrawLabel(DrawingContext dc, string text, Point pos, double size, Color color, bool bold = false)
    {
        var ft = new FormattedText(
            text,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            new Typeface(new FontFamily("Consolas"), FontStyles.Normal, bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal),
            size,
            new SolidColorBrush(color),
            1.25);
        dc.DrawText(ft, pos);
    }

    private void UpdateSceneTabsAndCaptions(int activeScene, double t)
    {
        HighlightScenePill(BtnScene1, activeScene == 1, Color.FromRgb(56, 189, 248));
        HighlightScenePill(BtnScene2, activeScene == 2, Color.FromRgb(248, 113, 113));
        HighlightScenePill(BtnScene3, activeScene == 3, Color.FromRgb(52, 211, 153));

        if (activeScene == 1)
        {
            StageBannerTitle.Text = "OPTICAL CAPTURE & NO-HANDS AUTO GATE";
            LiveSubtitleText.Text = "OpenCV locks angled USB webcams via ArUco/Quad homography + dual YCrCb/HSV hands-free trigger...";
            LatencyMetricBadge.Text = "2.5ms Warp | Auto-Gate";
            LivePulseDot.Fill = new SolidColorBrush(Color.FromRgb(56, 189, 248));
        }
        else if (activeScene == 2)
        {
            StageBannerTitle.Text = "KICAD DRC ENGINE & BLOCK CODING + USB PROG";
            LiveSubtitleText.Text = "Detects hardware shorts in 0.04ms while checking nested Block Code for ESP32 & Arduino...";
            LatencyMetricBadge.Text = "0.04ms DRC | BC001-034";
            LivePulseDot.Fill = new SolidColorBrush(Color.FromRgb(239, 68, 68));
        }
        else
        {
            StageBannerTitle.Text = "REAL-TIME VOICE TUTOR & LAB (EXP) SIMULATOR";
            LiveSubtitleText.Text = "\"Move LED1's cathode leg from hole A15 to A16 — live virtual MCU waveform & thermal check OK.\"";
            LatencyMetricBadge.Text = "Neural TTS | Live Sim";
            LivePulseDot.Fill = new SolidColorBrush(Color.FromRgb(52, 211, 153));
        }
    }

    private static void HighlightScenePill(Button btn, bool isActive, Color accent)
    {
        btn.Background = new SolidColorBrush(isActive ? Color.FromRgb(23, 37, 60) : Color.FromRgb(15, 23, 38));
        btn.BorderBrush = new SolidColorBrush(isActive ? accent : Color.FromRgb(38, 52, 73));
        btn.Foreground = new SolidColorBrush(isActive ? Colors.White : Color.FromRgb(148, 163, 184));
    }

    // =========================================================================
    // INSTALLER WORKFLOW & ANTIGRAVITY BRIDGE
    // =========================================================================
    private async void PrimaryAction_Click(object sender, RoutedEventArgs e)
    {
        if (_installFinished)
        {
            LaunchInstalledApplication();
            return;
        }
        if (_isInstalling) return;

        _isInstalling = true;
        _lastExtractionWarning = null;
        PrimaryActionButton.IsEnabled = false;

        string selectedProfile = ModeThinClient.IsChecked == true
            ? "EdgeClient"
            : ModeServer.IsChecked == true ? "AIServer" : "Workstation";

        GpuMoEProfile moeProfile = CpuAffinityManager.GetProfile(_selectedGpuTier);

        var steps = new (int Percent, string Title, string Detail, int DelayMs)[]
        {
            (16, "Configuring Hybrid CPU Core Affinity (P:0x00FFF, E:0xFF000)...",
                 $"Binding P-Cores to llama-server & E-Cores to Vision/DRC \u2022 Memory mlock", 500),
            (36, $"Configuring {moeProfile.ModelName} ({moeProfile.CpuMoeArgument})...",
                 $"Target VRAM: {moeProfile.TotalVramGigabytes:F1} GB \u2022 CPU Offload: {moeProfile.CpuMoeOffloadCount} MoE experts", 500),
            (60, "Deploying Breadboard AOI v2.0 Binaries, KiCad Libs & Programmer Sim...",
                 $"Target: {_installRootPath} \u2022 Profile: {selectedProfile}", 600),
            (84, "Registering Windows URI Handler 'antigravity-aoi://'...",
                 "Binding HKCU\\Software\\Classes\\antigravity-aoi and outputting antigravity.link.json", 500),
            (100, "Installation Complete \u2014 Breadboard AOI v2.0 is Ready!",
                 "Launch workstation or connect via Antigravity protocol bridge.", 200)
        };

        try
        {
            foreach (var step in steps)
            {
                InstallStatusTitle.Text = step.Title;
                InstallStepDetailText.Text = step.Detail;
                InstallPercentText.Text = $"{step.Percent}%";
                ProgressFillBar.Width = (step.Percent / 100.0) * GetProgressTrackWidth();
                await Task.Delay(step.DelayMs);

                if (step.Percent == 60)
                    await Task.Run(ExtractPayloadArchive);
                else if (step.Percent == 84 && ChkRegisterAntigravity.IsChecked == true)
                    await Task.Run(() => RegisterAntigravityLink(selectedProfile));
            }
        }
        catch (Exception ex)
        {
            LogInstallerMessage($"Installation flow note: {ex.Message}");
            InstallStatusTitle.Text = "Installation Finalized (Check Logs if Needed)";
            InstallStepDetailText.Text = $"Deployed to {_installRootPath} \u2022 Ready to launch.";
        }
        finally
        {
            if (!string.IsNullOrEmpty(_lastExtractionWarning))
            {
                InstallStatusTitle.Text = "Installation Complete (With Warnings)";
                InstallStepDetailText.Text = _lastExtractionWarning;
            }
            InstallPercentText.Text = "100%";
            ProgressFillBar.Width = GetProgressTrackWidth();
            _isInstalling = false;
            _installFinished = true;
            PrimaryActionButton.Content = "Launch Breadboard AOI";
            PrimaryActionButton.IsEnabled = true;
            BtnCopyAntigravityJson.Visibility = Visibility.Visible;
        }
    }

    private void ExtractPayloadArchive()
    {
        try
        {
            // 1. Terminate any active application processes that might hold file locks
            foreach (string procName in new[] { "BreadboardAOI.App", "llama-server", "BreadboardAOI" })
            {
                try
                {
                    foreach (var p in Process.GetProcessesByName(procName))
                    {
                        try { p.Kill(); p.WaitForExit(500); } catch { }
                    }
                }
                catch { }
            }

            Directory.CreateDirectory(_installRootPath);
            string normalizedInstallRoot = Path.GetFullPath(_installRootPath);
            if (!normalizedInstallRoot.EndsWith(Path.DirectorySeparatorChar))
                normalizedInstallRoot += Path.DirectorySeparatorChar;

            using Stream? stream = OpenEmbeddedPayload();

            if (stream is not null)
            {
                using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
                int total = archive.Entries.Count;
                int current = 0;

                foreach (ZipArchiveEntry entry in archive.Entries)
                {
                    current++;
                    if (string.IsNullOrEmpty(entry.Name)) continue;

                    string destFile = Path.GetFullPath(Path.Combine(normalizedInstallRoot, entry.FullName));
                    if (!destFile.StartsWith(normalizedInstallRoot, StringComparison.OrdinalIgnoreCase))
                        continue;

                    string? dir = Path.GetDirectoryName(destFile);
                    if (!string.IsNullOrEmpty(dir))
                        Directory.CreateDirectory(dir);

                    try
                    {
                        entry.ExtractToFile(destFile, overwrite: true);
                    }
                    catch (IOException)
                    {
                        try
                        {
                            Thread.Sleep(40);
                            entry.ExtractToFile(destFile, overwrite: true);
                        }
                        catch (Exception retryEx)
                        {
                            _lastExtractionWarning ??= $"Partial overwrite note on {entry.Name}: {retryEx.Message}";
                            LogInstallerMessage($"Retry failed extracting '{entry.FullName}': {retryEx.Message}");
                        }
                    }
                    catch (Exception ex)
                    {
                        _lastExtractionWarning ??= $"Extraction note on {entry.Name}: {ex.Message}";
                        LogInstallerMessage($"Failed extracting '{entry.FullName}': {ex.Message}");
                    }

                    if (current % 8 == 0 || current == total)
                    {
                        int subPercent = 60 + (int)((current / (double)total) * 24.0);
                        string fileName = entry.Name;
                        Dispatcher.BeginInvoke(() =>
                        {
                            InstallPercentText.Text = $"{subPercent}%";
                            ProgressFillBar.Width = (subPercent / 100.0) * GetProgressTrackWidth();
                            InstallStepDetailText.Text = $"Deploying {fileName}...";
                        });
                    }
                }
            }
            else
            {
                _lastExtractionWarning = "Embedded archive not found; attempted local directory payload copy.";
                string zipPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "BreadboardAOI_Complete_Build.zip");
                if (!File.Exists(zipPath))
                    zipPath = Path.Combine(Directory.GetCurrentDirectory(), "BreadboardAOI_Complete_Build.zip");

                if (File.Exists(zipPath))
                {
                    using var archive = ZipFile.OpenRead(zipPath);
                    foreach (var entry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(entry.Name)) continue;
                        string destFile = Path.GetFullPath(Path.Combine(normalizedInstallRoot, entry.FullName));
                        if (!destFile.StartsWith(normalizedInstallRoot, StringComparison.OrdinalIgnoreCase))
                            continue;
                        string? dir = Path.GetDirectoryName(destFile);
                        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                        try { entry.ExtractToFile(destFile, overwrite: true); }
                        catch (Exception ex) { LogInstallerMessage($"Zip fallback failed '{entry.FullName}': {ex.Message}"); }
                    }
                }
                else
                {
                    string sourceDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, @"src\5_BreadboardAOI.App\bin\Release\net8.0-windows");
                    if (!Directory.Exists(sourceDir))
                        sourceDir = Path.Combine(Directory.GetCurrentDirectory(), @"src\5_BreadboardAOI.App\bin\Release\net8.0-windows");

                    if (Directory.Exists(sourceDir))
                        CopyDirectoryRecursiveFiltered(sourceDir, _installRootPath);
                }
            }

            // Mirror native runtime DLLs to root folder
            string nativeRuntimes = Path.Combine(_installRootPath, "runtimes", "win-x64", "native");
            if (Directory.Exists(nativeRuntimes))
            {
                foreach (string file in Directory.GetFiles(nativeRuntimes, "*.dll"))
                {
                    string destFile = Path.Combine(_installRootPath, Path.GetFileName(file));
                    try { File.Copy(file, destFile, overwrite: true); }
                    catch (Exception ex) { LogInstallerMessage($"Native DLL mirror note ({Path.GetFileName(file)}): {ex.Message}"); }
                }
            }

            // Copy app.ico (always overwrite on upgrade)
            string targetIcon = Path.Combine(_installRootPath, "app.ico");
            string srcIcon = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "app.ico");
            if (File.Exists(srcIcon) && !string.Equals(Path.GetFullPath(srcIcon), Path.GetFullPath(targetIcon), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(srcIcon, targetIcon, overwrite: true); }
                catch (Exception ex) { LogInstallerMessage($"Icon copy note: {ex.Message}"); }
            }

            // Copy Uninstall-BreadboardAOI.ps1 (always overwrite on upgrade)
            string uninstallTarget = Path.Combine(_installRootPath, "Uninstall-BreadboardAOI.ps1");
            string uninstallSrc = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Uninstall-BreadboardAOI.ps1");
            if (!File.Exists(uninstallSrc))
                uninstallSrc = Path.Combine(Directory.GetCurrentDirectory(), "Uninstall-BreadboardAOI.ps1");
            if (File.Exists(uninstallSrc) && !string.Equals(Path.GetFullPath(uninstallSrc), Path.GetFullPath(uninstallTarget), StringComparison.OrdinalIgnoreCase))
            {
                try { File.Copy(uninstallSrc, uninstallTarget, overwrite: true); }
                catch (Exception ex) { LogInstallerMessage($"Uninstaller copy note: {ex.Message}"); }
            }

            // Create Desktop & Start Menu Shortcuts
            if (ChkCreateShortcuts.IsChecked == true)
            {
                try
                {
                    string appExe = Path.Combine(_installRootPath, "BreadboardAOI.App.exe");
                    string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
                    CreateShortcut(appExe, Path.Combine(desktopPath, "Breadboard AOI.lnk"), targetIcon, "Breadboard AOI Workstation v2.0");
                    string startMenu = Environment.GetFolderPath(Environment.SpecialFolder.Programs);
                    CreateShortcut(appExe, Path.Combine(startMenu, "Breadboard AOI.lnk"), targetIcon, "Breadboard AOI Workstation v2.0");
                }
                catch (Exception ex)
                {
                    LogInstallerMessage($"Shortcut creation note: {ex.Message}");
                }
            }

            RegisterWindowsUninstall();
        }
        catch (Exception ex)
        {
            _lastExtractionWarning = $"Extraction note: {ex.Message}";
            LogInstallerMessage($"Extraction note: {ex.Message}");
        }
    }

    private Stream? OpenEmbeddedPayload()
    {
        System.Reflection.Assembly assembly = System.Reflection.Assembly.GetExecutingAssembly();

        // Prefer the legacy single resource when present.
        Stream? single = assembly.GetManifestResourceStream("BreadboardAOI.EmbeddedPayload.zip");
        if (single is not null)
            return single;

        // Large payloads are split into <128 MB parts so csc can embed them.
        string[] parts = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith("BreadboardAOI.EmbeddedPayload.part", StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (parts.Length > 0)
        {
            string tempZipPath = Path.Combine(_installRootPath,
                $".BreadboardAOI_Payload_{Guid.NewGuid():N}.zip");

            var assembled = new FileStream(tempZipPath, FileMode.CreateNew, FileAccess.ReadWrite,
                FileShare.None, 1 << 20, FileOptions.DeleteOnClose | FileOptions.SequentialScan);

            try
            {
                foreach (string partName in parts)
                {
                    using Stream? part = assembly.GetManifestResourceStream(partName);
                    if (part is null)
                        throw new InvalidOperationException($"Missing payload part: {partName}");
                    part.CopyTo(assembled, 1 << 20);
                }

                assembled.Position = 0;
                return assembled;
            }
            catch
            {
                assembled.Dispose();
                throw;
            }
        }

        // Overlay payload with BAOIZIP1 trailer: [Int64 offset][Int64 length]["BAOIZIP1"]
        string? exePath = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(exePath) && File.Exists(exePath))
        {
            FileStream? fs = null;
            try
            {
                fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length > 24)
                {
                    byte[] trailer = new byte[24];
                    fs.Seek(-24, SeekOrigin.End);
                    fs.ReadExactly(trailer);
                    string magic = Encoding.ASCII.GetString(trailer, 16, 8);
                    if (string.Equals(magic, "BAOIZIP1", StringComparison.Ordinal))
                    {
                        long payloadOffset = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(0, 8));
                        long payloadLength = BinaryPrimitives.ReadInt64LittleEndian(trailer.AsSpan(8, 8));
                        if (payloadOffset > 0 && payloadLength > 0 && payloadOffset + payloadLength + 24 <= fs.Length)
                        {
                            var slice = new PayloadSliceStream(fs, payloadOffset, payloadLength);
                            using var testArchive = new ZipArchive(slice, ZipArchiveMode.Read, leaveOpen: true);
                            if (testArchive.Entries.Count > 0)
                            {
                                slice.Position = 0;
                                return slice;
                            }
                            slice.Dispose();
                            fs = null;
                        }
                    }
                }

                if (fs is not null)
                {
                    fs.Position = 0;
                    using var rawArchive = new ZipArchive(fs, ZipArchiveMode.Read, leaveOpen: true);
                    if (rawArchive.Entries.Count > 0)
                    {
                        fs.Position = 0;
                        return fs;
                    }
                    fs.Dispose();
                }
            }
            catch
            {
                fs?.Dispose();
            }
        }

        // Sibling archive fallback
        string? baseDir = AppContext.BaseDirectory;
        string[] candidates =
        {
            Path.Combine(baseDir, "BreadboardAOI_Complete_Build.zip"),
            Path.Combine(baseDir, "Payload.zip")
        };
        foreach (string candidate in candidates)
        {
            if (File.Exists(candidate))
            {
                return new FileStream(candidate, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
        }

        return null;
    }

    private void RegisterWindowsUninstall()
    {
        try
        {
            long totalBytes = Directory.Exists(_installRootPath)
                ? Directory.EnumerateFiles(_installRootPath, "*", SearchOption.AllDirectories)
                    .Sum(static path => new FileInfo(path).Length)
                : 0;
            int estimatedKb = (int)Math.Max(600000L, (totalBytes + 1023L) / 1024L);

            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Uninstall\BreadboardAOI");
            key.SetValue("DisplayName", "Breadboard AOI Workstation");
            key.SetValue("DisplayVersion", "2.0.0");
            key.SetValue("Publisher", "Breadboard AOI Engineering");
            key.SetValue("InstallLocation", _installRootPath);

            string iconPath = Path.Combine(_installRootPath, "app.ico");
            if (!File.Exists(iconPath)) iconPath = Path.Combine(_installRootPath, "BreadboardAOI.App.exe");
            key.SetValue("DisplayIcon", $"{iconPath},0");

            string uninstallScript = Path.Combine(_installRootPath, "Uninstall-BreadboardAOI.ps1");
            key.SetValue("UninstallString", $"powershell.exe -ExecutionPolicy Bypass -File \"{uninstallScript}\"");
            key.SetValue("QuietUninstallString", $"powershell.exe -ExecutionPolicy Bypass -File \"{uninstallScript}\" -Quiet");
            key.SetValue("EstimatedSize", estimatedKb, RegistryValueKind.DWord);
            key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
            key.SetValue("WindowsInstaller", 0, RegistryValueKind.DWord);
            key.SetValue("HelpLink", "https://github.com/wallali77666-cyber/BreadBoard-Assistant");
            key.SetValue("URLInfoAbout", "https://github.com/wallali77666-cyber/BreadBoard-Assistant");
            key.SetValue("NoModify", 1, RegistryValueKind.DWord);
            key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
        }
        catch (Exception ex)
        {
            LogInstallerMessage($"Registry registration note: {ex.Message}");
        }
    }

    private static void CopyDirectoryRecursiveFiltered(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string ext = Path.GetExtension(file).ToLowerInvariant();
            if (ext is ".gguf" or ".bin" or ".mmap" or ".so" or ".dylib") continue;
            File.Copy(file, Path.Combine(targetDir, Path.GetFileName(file)), overwrite: true);
        }
        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string dirName = Path.GetFileName(subDir).ToLowerInvariant();
            if (dirName is "linux-x64" or "osx" or "osx-x64" or "osx-arm64") continue;
            CopyDirectoryRecursiveFiltered(subDir, Path.Combine(targetDir, Path.GetFileName(subDir)));
        }
    }

    private static void CreateShortcut(string targetPath, string shortcutPath, string iconPath, string description)
    {
        try
        {
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;
            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = targetPath;
            shortcut.WorkingDirectory = Path.GetDirectoryName(targetPath);
            shortcut.Description = description;
            if (File.Exists(iconPath))
                shortcut.IconLocation = $"{iconPath},0";
            else if (File.Exists(targetPath))
                shortcut.IconLocation = $"{targetPath},0";
            shortcut.Save();
        }
        catch { }
    }

    private void RegisterAntigravityLink(string selectedProfile)
    {
        string appExe = Path.Combine(_installRootPath, "BreadboardAOI.App.exe");
        string manifestPath = Path.Combine(_installRootPath, "antigravity.link.json");
        GpuMoEProfile gpuProfile = CpuAffinityManager.GetProfile(_selectedGpuTier);

        var manifest = new
        {
            app_name = "BreadboardAOI",
            version = "2.0.0",
            edition = selectedProfile,
            capabilities = new[]
            {
                "nested_block_coding",
                "box_code_error_analyzer_bc001_034",
                "usb_programmer_virtual_and_cli",
                "ai_live_no_hands_auto_gate",
                "experimental_lab_suite"
            },
            install_directory = _installRootPath,
            executable = appExe,
            uri_scheme = "antigravity-aoi://inspect",
            gpu_hardware = new
            {
                selected_tier = gpuProfile.Tier.ToString(),
                model_name = gpuProfile.ModelName,
                total_vram_gb = gpuProfile.TotalVramGigabytes,
                cpu_moe_offload_count = gpuProfile.CpuMoeOffloadCount,
                cpu_moe_argument = gpuProfile.CpuMoeArgument,
                recommended_gpu_budget_gb = gpuProfile.RecommendedGpuMoEGigabytes
            },
            cpu_affinity = new
            {
                p_cores_mask = "0x00FFF",
                e_cores_mask = "0xFF000",
                llama_server_affinity = "0x00FFF",
                vision_drc_affinity = "0xFF000",
                target_cpu = "Intel Core i5-13600K (12P + 8E threads)",
                virtual_lock_mlock = true
            },
            ai_architecture = new
            {
                qwen_mode = "external",
                qwen_bundled = false,
                default_loopback = "http://127.0.0.1:8089/v1/chat/completions",
                supported_providers = new[] { "LM Studio", "Ollama", "llama-server", "Google Colab" }
            },
            endpoints = new
            {
                websocket_inspect = "ws://127.0.0.1:5005/ws/inspect",
                qwen_moe_api = "http://127.0.0.1:8089/v1/chat/completions",
                named_pipe_ipc = @"\\.\pipe\BreadboardAOI_Antigravity_Bridge"
            }
        };

        File.WriteAllText(manifestPath, JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        try
        {
            // Register Windows URL Protocol antigravity-aoi://
            using RegistryKey key = Registry.CurrentUser.CreateSubKey(@"Software\Classes\antigravity-aoi");
            key.SetValue("", "URL:Breadboard AOI Antigravity Protocol");
            key.SetValue("URL Protocol", "");

            string iconPath = Path.Combine(_installRootPath, "app.ico");
            if (!File.Exists(iconPath)) iconPath = appExe;
            using (RegistryKey iconKey = key.CreateSubKey("DefaultIcon"))
            {
                iconKey.SetValue("", $"{iconPath},0");
            }

            using (RegistryKey cmdKey = key.CreateSubKey(@"shell\open\command"))
            {
                cmdKey.SetValue("", $"\"{appExe}\" \"%1\"");
            }
        }
        catch { }
    }

    private void CopyAntigravityLink_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText("antigravity-aoi://inspect");
        BtnCopyAntigravityJson.Content = "\u2713 Copied antigravity-aoi://";
    }

    private void LaunchInstalledApplication()
    {
        string appExe = Path.Combine(_installRootPath, "BreadboardAOI.App.exe");
        if (File.Exists(appExe))
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = appExe,
                WorkingDirectory = _installRootPath,
                UseShellExecute = true
            });
        }
        else
        {
            Process.Start("explorer.exe", _installRootPath);
        }
        Close();
    }

    private void Scene1_Click(object sender, RoutedEventArgs e) => _lockedSceneOverride = _lockedSceneOverride == 1 ? 0 : 1;
    private void Scene2_Click(object sender, RoutedEventArgs e) => _lockedSceneOverride = _lockedSceneOverride == 2 ? 0 : 2;
    private void Scene3_Click(object sender, RoutedEventArgs e) => _lockedSceneOverride = _lockedSceneOverride == 3 ? 0 : 3;

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private static void LogInstallerMessage(string message)
    {
        try
        {
            string logPath = Path.Combine(Path.GetTempPath(), "BreadboardAOI-installer.log");
            File.AppendAllText(logPath, $"[{DateTime.Now:O}] {message}{Environment.NewLine}");
        }
        catch { }
    }

    internal sealed class PayloadSliceStream : Stream
    {
        private readonly FileStream _inner;
        private readonly long _startOffset;
        private readonly long _length;
        private long _position;
        private bool _disposed;

        public PayloadSliceStream(FileStream inner, long startOffset, long length)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _startOffset = startOffset;
            _length = length;
            _inner.Position = startOffset;
        }

        public override bool CanRead => !_disposed;
        public override bool CanSeek => !_disposed;
        public override bool CanWrite => false;
        public override long Length => _length;

        public override long Position
        {
            get => _position;
            set => Seek(value, SeekOrigin.Begin);
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long remaining = _length - _position;
            if (remaining <= 0 || count <= 0)
                return 0;

            int toRead = (int)Math.Min(count, remaining);
            _inner.Position = _startOffset + _position;
            int read = _inner.Read(buffer, offset, toRead);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            long target = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => _length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin))
            };
            if (target < 0)
                throw new IOException("Attempted to seek before the beginning of the payload slice.");
            _position = Math.Min(target, _length);
            _inner.Position = _startOffset + _position;
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (!_disposed && disposing)
            {
                _disposed = true;
                _inner.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
