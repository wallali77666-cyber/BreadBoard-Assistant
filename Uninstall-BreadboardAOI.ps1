[CmdletBinding()]
param([switch]$Quiet)

# Stop any active BreadboardAOI processes
Get-Process -Name "BreadboardAOI.App", "BreadboardAOI_Setup", "BreadboardAOI_Full_Setup", "llama-server" -ErrorAction SilentlyContinue | Stop-Process -Force

# Remove Desktop and Start Menu Shortcuts
$desktopLnk = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)) "Breadboard AOI.lnk"
if (Test-Path $desktopLnk) { Remove-Item $desktopLnk -Force -ErrorAction SilentlyContinue }

$startLnk = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::Programs)) "Breadboard AOI.lnk"
if (Test-Path $startLnk) { Remove-Item $startLnk -Force -ErrorAction SilentlyContinue }

# Unregister URI protocol and Windows Uninstall entry
Remove-Item -Path "HKCU:\Software\Classes\antigravity-aoi" -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\BreadboardAOI" -Recurse -Force -ErrorAction SilentlyContinue

# Target directory
$installDir = Split-Path -Parent $MyInvocation.MyCommand.Path

# Show user confirmation if not quiet
if (-not $Quiet) {
    try {
        Add-Type -AssemblyName PresentationFramework
        [System.Windows.MessageBox]::Show("Breadboard AOI Workstation has been successfully uninstalled from your computer.", "Breadboard AOI Uninstalled", [System.Windows.MessageBoxButton]::OK, [System.Windows.MessageBoxImage]::Information) | Out-Null
    } catch { }
}

# Self-delete install directory after process exits (ping avoids timeout stdin issues in hidden cmd)
if (Test-Path $installDir) {
    Start-Process -FilePath "cmd.exe" -ArgumentList "/c ping -n 3 127.0.0.1 >nul & rmdir /s /q `"$installDir`"" -WindowStyle Hidden
}

