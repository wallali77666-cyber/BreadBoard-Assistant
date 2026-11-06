# ==============================================================================
# File: Sync-NewFiles-And-Replan.ps1 (Compatible with PowerShell 5.1 & PowerShell 7)
# Purpose: Scans project files, indexes symbols, and establishes zero-bandwidth
#          code sync via HardLink, SymLink, or Lightweight Code Mirror.
# ==============================================================================

param(
    [string]$Source9GbPath = $PSScriptRoot,
    [string]$GhostWorkspacePath = (Join-Path (Split-Path $PSScriptRoot -Parent) "BreadboardAOI_Ghost_400KB")
)

$ErrorActionPreference = "Stop"
Write-Host "=== Scanning for New Files & Re-Planning Swarm Topology ===" -ForegroundColor Cyan
Write-Host "  Source Path: $Source9GbPath"
Write-Host "  Ghost Path : $GhostWorkspacePath"

# Avoid self-referencing (PowerShell 5.1 compatible check without '?.' operator)
$resolvedSource = (Resolve-Path $Source9GbPath).Path.TrimEnd('\')
$resolvedGhostObj = Resolve-Path $GhostWorkspacePath -ErrorAction SilentlyContinue
$resolvedGhost = if ($null -ne $resolvedGhostObj) { $resolvedGhostObj.Path.TrimEnd('\') } else { "" }
$IsSameDir = [string]::Equals($resolvedSource, $resolvedGhost, [System.StringComparison]::OrdinalIgnoreCase)

if (-not $IsSameDir -and -not (Test-Path $GhostWorkspacePath)) {
    New-Item -ItemType Directory -Path $GhostWorkspacePath -Force | Out-Null
}

$AllowedCodeExts = @(".cs", ".xaml", ".csproj", ".sln", ".props", ".py", ".cpp", ".h", ".json", ".jsonc", ".gbnf", ".ps1", ".md", ".dru", ".kicad_sch", ".kicad_sym")
$BlockedDirs     = @("bin", "obj", ".vs", ".git", "packages", "node_modules", "_SingleExeBuildTemp", "Dist_SingleExe", "BreadboardAOI_Ghost_400KB")

$CodeFilesIndex  = [System.Collections.Generic.List[object]]::new()
$HeavyAssetsList = [System.Collections.Generic.List[object]]::new()
$LinkedCount     = 0

Get-ChildItem -Path $Source9GbPath -Recurse -File -Force | ForEach-Object {
    $File = $_
    $RelPath = $File.FullName.Substring($Source9GbPath.Length).TrimStart('\', '/')
    $TopFolder = ($RelPath -split '[\\/]' | Select-Object -First 1)

    if ($BlockedDirs -contains $TopFolder -or $RelPath -match '(^|[\\/])(bin|obj|\.vs|\.git)[\\/]') {
        return
    }

    $IsCode = ($AllowedCodeExts -contains $File.Extension.ToLower()) -and ($File.Length -lt 524288)

    if ($IsCode) {
        if (-not $IsSameDir) {
            $GhostTarget = Join-Path $GhostWorkspacePath $RelPath
            $GhostDir = Split-Path $GhostTarget -Parent
            if (-not (Test-Path $GhostDir)) { New-Item -ItemType Directory -Path $GhostDir -Force | Out-Null }

            if (-not (Test-Path $GhostTarget)) {
                # Fallback sequence: HardLink -> SymbolicLink -> Lightweight Copy
                $linkCreated = $false
                try {
                    New-Item -ItemType HardLink -Path $GhostTarget -Value $File.FullName -ErrorAction Stop | Out-Null
                    $linkCreated = $true
                } catch {
                    try {
                        New-Item -ItemType SymbolicLink -Path $GhostTarget -Value $File.FullName -ErrorAction Stop | Out-Null
                        $linkCreated = $true
                    } catch {
                        Copy-Item -Path $File.FullName -Destination $GhostTarget -Force
                        $linkCreated = $true
                    }
                }
                if ($linkCreated) { $LinkedCount++ }
            }
        }

        # Extract namespace & class symbols for quick agent reference
        $Symbols = @()
        if ($File.Extension -eq ".cs") {
            try {
                $RawText = [System.IO.File]::ReadAllText($File.FullName)
                $Matches = [regex]::Matches($RawText, '(?:namespace\s+([\w\.]+))|(?:(?:public|internal|sealed|abstract|partial)\s+(?:class|record|struct|interface|enum)\s+(\w+))')
                foreach ($m in $Matches) {
                    if ($m.Groups[1].Success) { $Symbols += "ns:$($m.Groups[1].Value)" }
                    if ($m.Groups[2].Success) { $Symbols += "type:$($m.Groups[2].Value)" }
                }
            } catch { }
        }

        $CodeFilesIndex.Add([PSCustomObject]@{
            path          = $RelPath.Replace('\', '/')
            size_kb       = [Math]::Round($File.Length / 1KB, 1)
            last_modified = $File.LastWriteTimeUtc.ToString("yyyy-MM-dd HH:mm:ss")
            symbols       = ($Symbols | Select-Object -Unique) -join ", "
        })
    } else {
        $HeavyAssetsList.Add([PSCustomObject]@{
            path    = $RelPath.Replace('\', '/')
            size_mb = [Math]::Round($File.Length / 1MB, 2)
            ext     = $File.Extension.ToLower()
        })
    }
}

# 1. Write LIVE_WORKSPACE_MAP.json
$WorkspaceMap = [PSCustomObject]@{
    scan_timestamp_utc = (Get-Date).ToUniversalTime().ToString("o")
    total_code_files   = $CodeFilesIndex.Count
    newly_linked_files = $LinkedCount
    code_files         = $CodeFilesIndex
    heavy_assets       = $HeavyAssetsList
}
$MapJson = $WorkspaceMap | ConvertTo-Json -Depth 5
[System.IO.File]::WriteAllText((Join-Path $Source9GbPath "LIVE_WORKSPACE_MAP.json"), $MapJson)
if (-not $IsSameDir) {
    [System.IO.File]::WriteAllText((Join-Path $GhostWorkspacePath "LIVE_WORKSPACE_MAP.json"), $MapJson)
}

# 2. Build live dynamic tree list for AGENTS.md
$TreeLines = $CodeFilesIndex | ForEach-Object {
    $SymInfo = if ($_.symbols) { "  # [$($_.symbols)]" } else { "" }
    "- `$($_.path)` ($($_.size_kb) KB, Modified: $($_.last_modified))$SymInfo"
}
$TreeMarkdown = $TreeLines -join "`r`n"

# 3. Write Updated AGENTS.md
$DynamicAgentsMd = @"
# Breadboard AOI — Swarm Master Specification (`AGENTS.md`)

## 1. 9GB ZERO-DATA INVARIANTS (STRICT)
1. Never run `read_file`, `cat`, or base64 reads on `*.gguf`, `*.onnx`, `*.engine`, `*.zip`, `*.db`, or `models/**`.
2. Inspect `@PROJECT_9GB_MANIFEST.json` and `@LIVE_WORKSPACE_MAP.json` for paths and sizes of heavy binary assets.
3. Edit files in-place using existing namespaces (`BreadboardAOI.*`).

## 2. DISCOVERED CODE TREE ($($CodeFilesIndex.Count) Files)
$TreeMarkdown

## 3. ADAPTIVE TEAM OWNERSHIP MATRIX
- **Argon-1 (Core & KiCad Compiler):** Owns `src/2_BreadboardAOI.Core/` (1.27mm Sugiyama/Manhattan routing, PinMatrix, Disjoint-Set netlists, `.kicad_sch` generator).
- **Argon-2 (Vision & Wire Tracker):** Owns `src/3_BreadboardAOI.Vision/` (OpenCV ArUco warp, 250ms stillness gate, YOLOv11-Pose, PaddleOCR, `WireInsertionTracker.cs`).
- **Argon-3 (Datasheet & MoE Brain):** Owns `src/4_BreadboardAOI.Intelligence/` (Alldatasheet 250KB Range fetcher, Qwen3-VL GBNF service, DevBoard GPIO rules, Kokoro Opus audio).
- **Argon-4 (Hardware & Installer):** Owns `src/1_BreadboardAOI.Hardware/` (P/E-Core affinity masks, memory locking) and `src/7_BreadboardAOI.Installer/` (single-file installer).
- **Flash 3.8 Antigravity (UI & Pipeline Coordinator):** Owns `src/5_BreadboardAOI.App/` (SkiaSharp 60FPS AR canvas, WebSocket, Named Pipe IPC).
- **OpenCode DeepSeek V4 (Terminal Build Gate):** Audits `src/6_BreadboardAOI.Tests/`, manages solution build, and runs `dotnet build -c Release`.
"@

[System.IO.File]::WriteAllText((Join-Path $Source9GbPath "AGENTS.md"), $DynamicAgentsMd)
if (-not $IsSameDir) {
    [System.IO.File]::WriteAllText((Join-Path $GhostWorkspacePath "AGENTS.md"), $DynamicAgentsMd)
}

Write-Host "`n[SUCCESS] Re-Plan Complete!" -ForegroundColor Green
Write-Host "  -> Indexed Code Files: $($CodeFilesIndex.Count)"
Write-Host "  -> Processed Links   : $LinkedCount"
Write-Host "  -> Updated LIVE_WORKSPACE_MAP.json and AGENTS.md" -ForegroundColor Yellow