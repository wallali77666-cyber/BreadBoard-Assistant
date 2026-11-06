# ==============================================================================
# File: Build-GoogleStyle-Installer.ps1
# Purpose: Compiles the full application payload, bundles it into the
#          animated Google/Gemini-style standalone installer (BreadboardAOI_Setup.exe),
#          registers the Windows Installed Apps entry, and deploys locally.
# Usage:   powershell -ExecutionPolicy Bypass -File .\Build-GoogleStyle-Installer.ps1
# ==============================================================================

param(
    [switch]$SkipDeploy
)

$ErrorActionPreference = "Stop"
$ScriptDir = $PSScriptRoot
if (-not $ScriptDir) { $ScriptDir = Get-Location }

Get-Process -Name "BreadboardAOI_Setup", "BreadboardAOI.App" -ErrorAction SilentlyContinue | Stop-Process -Force

$DotnetCmd = "dotnet"
if (Test-Path "C:\Users\DELL\.dotnet\dotnet.exe") {
    $DotnetCmd = "C:\Users\DELL\.dotnet\dotnet.exe"
    $env:PATH = "C:\Users\DELL\.dotnet;$env:PATH"
    $env:DOTNET_ROOT = "C:\Users\DELL\.dotnet"
}

Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "  Breadboard AOI -- Complete Workstation & Installer Packaging    " -ForegroundColor Yellow
Write-Host "=================================================================" -ForegroundColor Cyan

# 1. Publish full application payload
$AppProj = Join-Path $ScriptDir "src\5_BreadboardAOI.App\5_BreadboardAOI.App.csproj"
$AppPayloadDir = Join-Path $ScriptDir "dist\app_payload"
if (Test-Path $AppPayloadDir) { Remove-Item $AppPayloadDir -Recurse -Force }
New-Item -ItemType Directory -Path $AppPayloadDir | Out-Null

Write-Host "`n[1/5] Publishing complete Breadboard AOI Application (win-x64)..." -ForegroundColor Yellow
& $DotnetCmd publish $AppProj -c Release -r win-x64 --self-contained false -o $AppPayloadDir

# 2. Mirror native DLLs into root
$nativeRuntimes = Join-Path $AppPayloadDir "runtimes\win-x64\native"
if (Test-Path $nativeRuntimes) {
    Get-ChildItem -Path $nativeRuntimes -Filter "*.dll" | ForEach-Object {
        $dest = Join-Path $AppPayloadDir $_.Name
        if (-not (Test-Path $dest)) {
            Copy-Item $_.FullName $dest -Force
        }
    }
}

# Copy uninstall script and app icon
Copy-Item (Join-Path $ScriptDir "Uninstall-BreadboardAOI.ps1") (Join-Path $AppPayloadDir "Uninstall-BreadboardAOI.ps1") -Force
Copy-Item (Join-Path $ScriptDir "assets\app.ico") (Join-Path $AppPayloadDir "app.ico") -Force

# 3. Create BreadboardAOI_Complete_Build.zip
$CompleteZip = Join-Path $ScriptDir "BreadboardAOI_Complete_Build.zip"
if (Test-Path $CompleteZip) { Remove-Item $CompleteZip -Force }

Write-Host "`n[2/5] Compressing application payload into portable archive..." -ForegroundColor Yellow
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($AppPayloadDir, $CompleteZip, [System.IO.Compression.CompressionLevel]::Fastest, $false)

# Remove any legacy parts so compiler stays lightweight
Get-ChildItem -Path (Join-Path $ScriptDir "src\7_BreadboardAOI.Installer\Payload.zip.part*") -ErrorAction SilentlyContinue | Remove-Item -Force

# 4. Compile Installer Executable
$InstallerProj = Join-Path $ScriptDir "src\7_BreadboardAOI.Installer\7_BreadboardAOI.Installer.csproj"
$DistInstallerDir = Join-Path $ScriptDir "dist\installer"
if (Test-Path $DistInstallerDir) { Remove-Item $DistInstallerDir -Recurse -Force }
New-Item -ItemType Directory -Path $DistInstallerDir | Out-Null

Write-Host "`n[3/5] Compiling Standalone Animated Installer (BreadboardAOI_Setup.exe)..." -ForegroundColor Yellow
& $DotnetCmd publish $InstallerProj -c Release -r win-x64 --self-contained false -o $DistInstallerDir -p:UseSharedCompilation=false

$ProducedExe = Join-Path $DistInstallerDir "BreadboardAOI_Setup.exe"
if (-not (Test-Path $ProducedExe)) {
    throw "Fatal: Setup executable was not generated at $ProducedExe"
}

$FinalSetupExe = Join-Path $ScriptDir "BreadboardAOI_Setup.exe"
Copy-Item $ProducedExe $FinalSetupExe -Force

# Append payload zip to produce single-file self-extracting executable
Write-Host "Embedding full payload archive into standalone setup executable..." -ForegroundColor Yellow
$zipStream = [System.IO.File]::OpenRead($CompleteZip)
$exeStream = [System.IO.File]::Open($FinalSetupExe, [System.IO.FileMode]::Append, [System.IO.FileAccess]::Write)
try {
    $zipStream.CopyTo($exeStream)
}
finally {
    $zipStream.Dispose()
    $exeStream.Dispose()
}

$ExeLength = (Get-Item $FinalSetupExe).Length
$ExeSizeMb = [math]::Round($ExeLength / 1MB, 2)
Write-Host ('[+] Produced Standalone Installer: {0} [{1} MB]' -f $FinalSetupExe, $ExeSizeMb) -ForegroundColor Green

if ($SkipDeploy) {
    Write-Host "`n[4/5] Local deployment skipped (-SkipDeploy)." -ForegroundColor Yellow
    Write-Host "[5/5] Windows registration skipped (-SkipDeploy)." -ForegroundColor Yellow
    Write-Host "`n=================================================================" -ForegroundColor Cyan
    Write-Host "  BUILD COMPLETE! Installer and portable zip are ready.          " -ForegroundColor Green
    Write-Host "=================================================================" -ForegroundColor Cyan
    Write-Host "Installer: $FinalSetupExe ($ExeSizeMb MB)" -ForegroundColor Green
    Write-Host "Portable:  $CompleteZip" -ForegroundColor Green
    return
}

# 5. Direct deployment to %LOCALAPPDATA%\BreadboardAOI and Register Windows Uninstall
$InstallRoot = Join-Path $env:LOCALAPPDATA "BreadboardAOI"
Write-Host "`n[4/5] Deploying directly to local workstation: $InstallRoot ..." -ForegroundColor Yellow
if (-not (Test-Path $InstallRoot)) { New-Item -ItemType Directory -Path $InstallRoot -Force | Out-Null }
Copy-Item -Path "$AppPayloadDir\*" -Destination $InstallRoot -Recurse -Force

# Register Windows Uninstall in Settings > Apps > Installed apps
Write-Host "`n[5/5] Registering in Windows Settings (Installed Apps)..." -ForegroundColor Yellow
$regKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BreadboardAOI"
if (-not (Test-Path $regKey)) { New-Item -Path $regKey -Force | Out-Null }
Set-ItemProperty -Path $regKey -Name "DisplayName" -Value "Breadboard AOI Workstation"
Set-ItemProperty -Path $regKey -Name "DisplayVersion" -Value "1.0.0"
Set-ItemProperty -Path $regKey -Name "Publisher" -Value "Breadboard AOI Engineering"
Set-ItemProperty -Path $regKey -Name "InstallLocation" -Value $InstallRoot
Set-ItemProperty -Path $regKey -Name "DisplayIcon" -Value "$InstallRoot\app.ico,0"
Set-ItemProperty -Path $regKey -Name "UninstallString" -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallRoot\Uninstall-BreadboardAOI.ps1`""
Set-ItemProperty -Path $regKey -Name "QuietUninstallString" -Value "powershell.exe -ExecutionPolicy Bypass -File `"$InstallRoot\Uninstall-BreadboardAOI.ps1`" -Quiet"
Set-ItemProperty -Path $regKey -Name "EstimatedSize" -Value 260000 -Type DWord
Set-ItemProperty -Path $regKey -Name "InstallDate" -Value (Get-Date -Format "yyyyMMdd")
Set-ItemProperty -Path $regKey -Name "WindowsInstaller" -Value 0 -Type DWord
Set-ItemProperty -Path $regKey -Name "HelpLink" -Value "https://github.com/wallali77666-cyber/BreadBoard-Assistant"
Set-ItemProperty -Path $regKey -Name "NoModify" -Value 1 -Type DWord
Set-ItemProperty -Path $regKey -Name "NoRepair" -Value 1 -Type DWord

# Update Shortcuts
$desktopLnk = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) "Breadboard AOI.lnk"
$startLnk = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) "Breadboard AOI.lnk"
$wsh = New-Object -ComObject WScript.Shell
$sc1 = $wsh.CreateShortcut($desktopLnk)
$sc1.TargetPath = "$InstallRoot\BreadboardAOI.App.exe"
$sc1.WorkingDirectory = $InstallRoot
$sc1.IconLocation = "$InstallRoot\app.ico,0"
$sc1.Description = "Breadboard AOI Workstation"
$sc1.Save()

$sc2 = $wsh.CreateShortcut($startLnk)
$sc2.TargetPath = "$InstallRoot\BreadboardAOI.App.exe"
$sc2.WorkingDirectory = $InstallRoot
$sc2.IconLocation = "$InstallRoot\app.ico,0"
$sc2.Description = "Breadboard AOI Workstation"
$sc2.Save()

Write-Host "`n=================================================================" -ForegroundColor Cyan
Write-Host "  BUILD COMPLETE! All files packaged & Windows Registered!       " -ForegroundColor Green
Write-Host "=================================================================" -ForegroundColor Cyan
Write-Host "Installer: $FinalSetupExe ($ExeSizeMb MB)" -ForegroundColor Green
Write-Host "Portable:  $CompleteZip" -ForegroundColor Green
Write-Host "App Path:   $InstallRoot\BreadboardAOI.App.exe" -ForegroundColor Green
