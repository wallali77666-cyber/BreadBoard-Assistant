# ==============================================================================
# File: Setup-ArgonWebBridge.ps1
# Purpose: Automates packing src/ files for the 4 Gemini Web (Argon) tabs and
#          unpacking their responses directly back into src/ for OpenCode CLI.
# ==============================================================================

$BridgeDir = Join-Path $PSScriptRoot "_SwarmBridge"
if (-not (Test-Path $BridgeDir)) { New-Item -ItemType Directory -Path $BridgeDir -Force | Out-Null }

function Pack-AgentBundle([string]$BundleName, [string[]]$ProjectFolders, [string]$RolePrompt) {
    $sb = [System.Text.StringBuilder]::new()
    [void]$sb.AppendLine($RolePrompt)
    [void]$sb.AppendLine("`r`nCRITICAL OUTPUT FORMAT RULE:")
    [void]$sb.AppendLine("Output EVERY updated or created file using this exact header format so our automated parser can write it to disk:")
    [void]$sb.AppendLine("===FILE: src/ProjectFolder/FileName.cs===")
    [void]$sb.AppendLine('```csharp')
    [void]$sb.AppendLine('// Full C# 12 production code here (zero placeholders)')
    [void]$sb.AppendLine('```')
    [void]$sb.AppendLine("`r`n--- CURRENT WORKSPACE FILES IN YOUR SCOPE ---")

    foreach ($folder in $ProjectFolders) {
        $fullFolder = Join-Path $PSScriptRoot $folder
        if (Test-Path $fullFolder) {
            Get-ChildItem -Path $fullFolder -Recurse -File -Include *.cs,*.csproj,*.xaml,*.json,*.gbnf |
                Where-Object { $_.FullName -notmatch '[\\/](bin|obj)[\\/]' } | ForEach-Object {
                    $rel = $_.FullName.Substring($PSScriptRoot.Length).TrimStart('\', '/').Replace('\', '/')
                    [void]$sb.AppendLine("`r`n===EXISTING FILE: $rel===")
                    [void]$sb.AppendLine([System.IO.File]::ReadAllText($_.FullName))
                }
        }
    }

    $outPath = Join-Path $BridgeDir "$BundleName`_INPUT.txt"
    [System.IO.File]::WriteAllText($outPath, $sb.ToString(), [System.Text.Encoding]::UTF8)

    $replyPath = Join-Path $BridgeDir "$BundleName`_OUTPUT.txt"
    if (-not (Test-Path $replyPath)) {
        [System.IO.File]::WriteAllText($replyPath, "", [System.Text.Encoding]::UTF8)
    }
    Write-Host "  [+] Packed $outPath" -ForegroundColor Cyan
}

# 1. Pack Argon-1 (Core + Tests)
Pack-AgentBundle "ARGON_1" @("src\2_BreadboardAOI.Core", "src\6_BreadboardAOI.Tests") @"
You are Argon-1 (Core Electrical, PinMatrix & KiCad 1.27mm Compiler Lead).
Upgrade and complete all files in src/2_BreadboardAOI.Core and src/6_BreadboardAOI.Tests:
1. PinMatrixLookup.cs: O(1) Euclidean snapping for all 830 breadboard holes in 1000x500 space.
2. FastImageToKicadEngine.cs: OpenCV morphological wire skeletonization + Union-Find netlist extraction + Sugiyama hierarchical placement + Manhattan L1 orthogonal routing with (junction ...) dots + exact 1.27mm grid snapping & Y-axis inversion. Outputs standalone .kicad_sch (with embedded lib_symbols) and 2D SVG.
3. ElectricalRulesChecker.cs & DevBoardAndModuleRuleEngine.cs: Detect same-strip shorts, VCC-GND shorts, 5V-to-3.3V GPIO overloads (ESP32), input-only pins (GPIO34-39), and UART/I2C crossover errors.
4. Unit tests in src/6_BreadboardAOI.Tests verifying ERC and netlist generation.
"@

# 2. Pack Argon-2 (Vision)
Pack-AgentBundle "ARGON_2" @("src\3_BreadboardAOI.Vision") @"
You are Argon-2 (Computer Vision, YOLO-Pose & Temporal Wire Tracker Lead).
Upgrade and complete all files in src/3_BreadboardAOI.Vision:
1. CameraCaptureService.cs & HomographyCalibrator.cs: Lock USB webcam manual focus/exposure, detect 4 ArUco markers, and warp to 1000x500 in <2.5ms.
2. HandMotionGate.cs: Use Cv2.AbsDiff to detect hands; trigger frame lock and 22KB WebP encoding after 250ms of stillness.
3. YoloPoseInferenceEngine.cs: ONNX Runtime GPU inference for component boxes, wire lead tips, IC notches, and linear pin interpolation (Pin 1 to Pin N) for off-board sensor headers (HC-SR04, OLED).
4. WireInsertionTracker.cs: Temporal delta wire endpoint pairing across consecutive hand-settled frames so messy/overlapping wires pair deterministically, with HSV color fallback on cold start.
5. PaddleIcPartNumberOcr.cs: Read laser-etched IC markings in <12ms via Sdcb.PaddleOCR.
"@

# 3. Pack Argon-3 (Intelligence)
Pack-AgentBundle "ARGON_3" @("src\4_BreadboardAOI.Intelligence") @"
You are Argon-3 (Alldatasheet Scraper, Qwen3-VL MoE & Voice Synthesis Lead).
Upgrade and complete all files in src/4_BreadboardAOI.Intelligence:
1. AlldatasheetRangeFetcher.cs & PdfSchematicCropper.cs: Check SQLite cache first; on miss, fetch first 250KB (Pages 1-4) of PDF from alldatasheet.com via HTTP Range headers (for 2 Mbps connections) and crop "Typical Application" schematics via Docnet.Core.
2. QwenMoEService.cs: Query llama-server (port 8081) with datasheet_netlist.gbnf enforced to extract Golden Netlist JSON in <120 tokens.
3. VoiceSynthesisEngine.cs: Stream sentence chunks into SherpaOnnx (Kokoro-82M) and encode to 16kbps Opus (2 KB/s) via Concentus.
"@

# 4. Pack Argon-4 (Hardware + Installer)
Pack-AgentBundle "ARGON_4" @("src\1_BreadboardAOI.Hardware", "src\7_BreadboardAOI.Installer") @"
You are Argon-4 (Hardware Affinity, Memory Lock & Single-File Installer Lead).
Upgrade and complete all files in src/1_BreadboardAOI.Hardware and src/7_BreadboardAOI.Installer:
1. CpuAffinityManager.cs & PinnedFrameRingBuffer.cs: P/Invoke SetThreadAffinityMask (P-Cores 0x00FFF for llama-server + VirtualLock mlock; E-Cores 0xFF000 for Vision/DRC). Support both RTX 3060 12GB (--n-cpu-moe 22) and RTX 5050 8GB (--n-cpu-moe 34).
2. 7_BreadboardAOI.Installer: Enhanced dark-mode Google/Gemini-style WPF installer (no phase labels, custom dark segmented buttons, 60FPS procedural breadboard/AI telemetry HUD, antigravity-aoi:// URI registration, and antigravity.link.json output).
"@

# 5. Create the Unpacker Script that OpenCode will run to apply Argon's outputs
$UnpackerCode = @'
$RootDir = [System.IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$AllowedExts = @('.cs', '.csproj', '.xaml', '.props', '.json', '.gbnf', '.ps1', '.md', '.sln', '.dru')
$HeaderPattern = '(?m)^===FILE:[ \t]*([^\r\n=]+?)[ \t]*===[ \t]*\r?\n([\s\S]*?)(?=^===FILE:[ \t]*[^\r\n=]+?[ \t]*===[ \t]*\r?$|\z)'

function Get-CodeBody([string]$Body) {
    $lines = [System.Collections.Generic.List[string]]::new([string[]]($Body -split "\r?\n"))
    while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[0])) { $lines.RemoveAt(0) }
    while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1])) { $lines.RemoveAt($lines.Count - 1) }

    if ($lines.Count -gt 0 -and $lines[0].TrimStart().StartsWith('```')) { $lines.RemoveAt(0) }
    if ($lines.Count -gt 0 -and $lines[$lines.Count - 1].Trim() -eq '```') { $lines.RemoveAt($lines.Count - 1) }

    while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[0])) { $lines.RemoveAt(0) }
    while ($lines.Count -gt 0 -and [string]::IsNullOrWhiteSpace($lines[$lines.Count - 1])) { $lines.RemoveAt($lines.Count - 1) }

    return (($lines -join "`r`n") + "`r`n")
}

Get-ChildItem -Path $PSScriptRoot -Filter "ARGON_*_OUTPUT.txt" | ForEach-Object {
    $raw = [System.IO.File]::ReadAllText($_.FullName)
    if ([string]::IsNullOrWhiteSpace($raw)) { return }

    $matches = [regex]::Matches($raw, $HeaderPattern)
    foreach ($m in $matches) {
        $relPath = $m.Groups[1].Value.Trim().Replace('/', '\')
        $code    = Get-CodeBody $m.Groups[2].Value
        if ([string]::IsNullOrWhiteSpace($code)) {
            Write-Warning "  [Skipped] Empty body: $relPath"
            continue
        }

        $fullOut = [System.IO.Path]::GetFullPath((Join-Path $RootDir $relPath))
        if (-not $fullOut.StartsWith($RootDir + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
            Write-Warning "  [Skipped] Path escapes workspace: $relPath"
            continue
        }
        if ($AllowedExts -notcontains [System.IO.Path]::GetExtension($fullOut).ToLowerInvariant()) {
            Write-Warning "  [Skipped] Extension not allowed: $relPath"
            continue
        }

        $outDir = Split-Path $fullOut -Parent
        if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir -Force | Out-Null }
        [System.IO.File]::WriteAllText($fullOut, $code, [System.Text.Encoding]::UTF8)
        Write-Host "  [Applied] $relPath" -ForegroundColor Green
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path $BridgeDir "Apply-ArgonOutputs.ps1"), $UnpackerCode, [System.Text.Encoding]::UTF8)

Write-Host "`n[READY] Open '_SwarmBridge' folder. Upload/Paste ARGON_1..4_INPUT.txt into your 4 Gemini Web tabs!" -ForegroundColor Yellow
