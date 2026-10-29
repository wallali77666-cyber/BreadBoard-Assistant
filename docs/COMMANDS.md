# BreadboardAOI - literal commands

This file records the exact commands used to restore, build, test, run and
verify the workstation. Copy a block into **PowerShell** at the repository root.

```powershell
cd "D:\Breadboard assistant"
```

The machine has more than one .NET installation. Every command below uses the
SDK that built this repository:

```powershell
$dotnet = "C:\Users\DELL\.dotnet\dotnet.exe"
```

---

## 1. Restore

Run once after a project file or package reference changes. This may contact
NuGet; it is not needed for normal builds.

```powershell
& $dotnet restore BreadboardAOI.sln --ignore-failed-sources
```

Expected result: `7 of 7 projects` restored or up-to-date.

---

## 2. Build (Release)

Close the workstation first: Windows locks `BreadboardAOI.App.exe` while it is
running. The normal loop never restores again.

```powershell
& $dotnet build BreadboardAOI.sln --configuration Release --no-restore
```

Expected result:

```text
0 Warning(s)
0 Error(s)
Build succeeded.
```

The application executable is:

```text
src\5_BreadboardAOI.App\bin\Release\net8.0-windows\BreadboardAOI.App.exe
```

---

## 3. Test

```powershell
& $dotnet test BreadboardAOI.sln --configuration Release --no-build
```

Expected result:

```text
Passed! - Failed: 0, Passed: 236, Skipped: 0, Total: 236
```

Run one test class or one test by name:

```powershell
& $dotnet test src\6_BreadboardAOI.Tests\6_BreadboardAOI.Tests.csproj `
  --configuration Release --no-build `
  --filter "FullyQualifiedName~ElectricalRulesCheckerTests"
```

```powershell
& $dotnet test src\6_BreadboardAOI.Tests\6_BreadboardAOI.Tests.csproj `
  --configuration Release --no-build `
  --filter "DisplayName~SameStrip"
```

BMO voice processing, pronunciation cleanup and SAPI SSML wrapper:

```powershell
& $dotnet test src\6_BreadboardAOI.Tests\6_BreadboardAOI.Tests.csproj `
  --configuration Release --no-build `
  --filter "FullyQualifiedName~BmoVoiceTests"
```

Piper preference parsing, CLI arguments, WAV ingestion and fallback:

```powershell
& $dotnet test src\6_BreadboardAOI.Tests\6_BreadboardAOI.Tests.csproj `
  --configuration Release --no-build `
  --filter "FullyQualifiedName~PiperTtsTests"
```

---

## 4. Run the workstation

```powershell
& $dotnet run --project src\5_BreadboardAOI.App\5_BreadboardAOI.App.csproj --configuration Release
```

Or start the already-built executable without a console window:

```powershell
Start-Process -FilePath "D:\Breadboard assistant\src\5_BreadboardAOI.App\bin\Release\net8.0-windows\BreadboardAOI.App.exe" `
  -WorkingDirectory "D:\Breadboard assistant"
```

Stop it before rebuilding:

```powershell
Get-Process -Name "BreadboardAOI.App" -ErrorAction SilentlyContinue |
  ForEach-Object { $_.CloseMainWindow() | Out-Null }
```

---

## 5. Boot switches (demo / QA / screenshot)

Set an environment variable in the same PowerShell session before launching.

```powershell
# Replay a still photo as the camera frame during startup (no part extraction).
$env:BREADBOARD_IMAGE_PATH = "C:\photos\breadboard.jpg"

# Preselect a demo fault: 555, floating, short, valid.
$env:BREADBOARD_START_DEMO = "short"

# Open the Adobe-style VIEW layout panel at boot.
$env:BREADBOARD_START_VIEW = "1"

# Start with a saved preset: workstation, canvas, inspect or builder.
$env:BREADBOARD_START_LAYOUT = "builder"
```

Clear them afterwards:

```powershell
Remove-Item Env:BREADBOARD_IMAGE_PATH, Env:BREADBOARD_START_DEMO,
  Env:BREADBOARD_START_VIEW, Env:BREADBOARD_START_LAYOUT -ErrorAction SilentlyContinue
```

### Workspace layout file

The VIEW panel saves panel visibility, dock widths and the active left tab
automatically. Inspect or reset it with:

```powershell
$layout = Join-Path $env:LOCALAPPDATA "BreadboardAOI\workspace-layout.json"
Get-Content $layout

# Reset to the default workstation layout.
Remove-Item $layout -ErrorAction SilentlyContinue
```

The file is created on first launch and flushed when the window closes. A
missing file is safe: the next launch writes the defaults.

### BMO voice profile

Piper, Kokoro and SAPI are selectable at runtime from **VIEW → VOICE ENGINE**
(`AUTO`, `BMO PIPER`, `KOKORO`, `SAPI`) with **TEST VOICE** to audition the result.
The choice is saved to `%LOCALAPPDATA%\BreadboardAOI\voice-settings.json`:

```powershell
$voice = Join-Path $env:LOCALAPPDATA 'BreadboardAOI\voice-settings.json'
Get-Content $voice -Raw
Remove-Item $voice -ErrorAction SilentlyContinue   # back to AUTO
```

`BREADBOARD_VOICE_BACKEND` wins over the saved file for the session:

```powershell
$env:BREADBOARD_VOICE_BACKEND = "auto"     # piper -> kokoro -> sapi
$env:BREADBOARD_VOICE_BACKEND = "piper"    # BMO Piper, falls back if files are missing
$env:BREADBOARD_VOICE_BACKEND = "kokoro"   # Kokoro-82M with the BMO DSP
$env:BREADBOARD_VOICE_BACKEND = "sapi"     # Windows SAPI with SSML pitch
```

Install the BMO Piper voice (optional, user-supplied; the app never downloads it):

```powershell
# 1. Unzip the Windows Piper release from
#    https://github.com/rhasspy/piper/releases into:
#    assets\models\piper\   (piper.exe + DLLs + espeak-ng-data)
# 2. Put the voice beside it:
#    assets\models\piper\bmo.onnx
#    assets\models\piper\bmo.onnx.json
# 3. Launch, VIEW -> BMO PIPER -> TEST VOICE.
```

Paths can be overridden without touching the drop folder:

```powershell
$env:BREADBOARD_VOICE_PIPER_EXE = "D:\voices\piper\piper.exe"
$env:BREADBOARD_VOICE_PIPER_MODEL = "D:\voices\piper\bmo.onnx"
$env:BREADBOARD_VOICE_PIPER_CONFIG = "D:\voices\piper\bmo.onnx.json"
$env:BREADBOARD_VOICE_PIPER_SPEAKER = "-1"          # -1 omits --speaker
$env:BREADBOARD_VOICE_PIPER_LENGTH_SCALE = "1.0"    # >1 is slower
$env:BREADBOARD_VOICE_PIPER_NOISE_SCALE = "0.667"
$env:BREADBOARD_VOICE_PIPER_NOISE_W = "0.8"
$env:BREADBOARD_VOICE_PIPER_SENTENCE_SILENCE_MS = "200"
```

Kokoro uses the bright `af_sky` speaker (#10) automatically; SAPI selects the
brightest installed voice (Zira on most PCs). Tune or neutralise it with:

```powershell
$env:BREADBOARD_VOICE_BMO = "1"
$env:BREADBOARD_VOICE_PITCH = "1.10"
$env:BREADBOARD_VOICE_SPEAKER = "-1"
$env:BREADBOARD_VOICE_SENTENCE_PAUSE_MS = "150"
$env:BREADBOARD_VOICE_CLAUSE_PAUSE_MS = "55"

# Neutral voice, no pitch lift.
$env:BREADBOARD_VOICE_BMO = "0"
```

Clear them afterwards:

```powershell
Remove-Item Env:BREADBOARD_VOICE_BMO, Env:BREADBOARD_VOICE_PITCH,
  Env:BREADBOARD_VOICE_SPEAKER, Env:BREADBOARD_VOICE_SENTENCE_PAUSE_MS,
  Env:BREADBOARD_VOICE_CLAUSE_PAUSE_MS, Env:BREADBOARD_VOICE_BACKEND,
  Env:BREADBOARD_VOICE_PIPER_EXE, Env:BREADBOARD_VOICE_PIPER_MODEL,
  Env:BREADBOARD_VOICE_PIPER_CONFIG, Env:BREADBOARD_VOICE_PIPER_SPEAKER,
  Env:BREADBOARD_VOICE_PIPER_LENGTH_SCALE, Env:BREADBOARD_VOICE_PIPER_NOISE_SCALE,
  Env:BREADBOARD_VOICE_PIPER_NOISE_W,
  Env:BREADBOARD_VOICE_PIPER_SENTENCE_SILENCE_MS -ErrorAction SilentlyContinue
```

---

## 6. Photo replay (LOAD IMAGE)

From the UI:

1. Press **LOAD IMAGE**, or drop a `.png`, `.jpg`, `.bmp`, `.webp`, `.tif` on
   the viewport.
2. The picture replaces the camera as the frame source. It is letterboxed into
   the 1280 x 720 capture frame, rectified with the four ArUco markers (or fitted
   to the board rectangle) and passed through the live homography / YOLO / DRC
   path.
3. The 2D board and the parts remain the editable surface. Enable
   **SCENE -> Show camera / photo underlay** to see the photo behind the vector
   board.

There is no photo-to-shapes conversion, import dialog or automatic part
extraction in the application.

---

## 7. Release publish (only after approval)

This creates a self-contained Windows x64 folder and zip. Do not run it from a
background agent without the operator's approval.

```powershell
& $dotnet publish src\5_BreadboardAOI.App\5_BreadboardAOI.App.csproj `
  --configuration Release `
  --runtime win-x64 `
  --self-contained true `
  --output artifacts\BreadboardAOI-win-x64

Compress-Archive -Path artifacts\BreadboardAOI-win-x64\* `
  -DestinationPath artifacts\BreadboardAOI-win-x64.zip -Force
```

The published app keeps `assets\kicad\kicad-symbols-master.zip` next to the
executable. Model weights remain optional and are never required to start.

---

## 8. Useful one-liners

```powershell
# Current branch and uncommitted files.
git status --short

# Last three commits.
git log --oneline -3

# Find every project in the solution.
& $dotnet sln BreadboardAOI.sln list

# Confirm the workstation assembly was produced.
Get-Item "src\5_BreadboardAOI.App\bin\Release\net8.0-windows\BreadboardAOI.App.dll"

# Tail the app's crash log, if one was written.
Get-Content (Join-Path $env:TEMP "BreadboardAOI-error.log") -Tail 40
```
