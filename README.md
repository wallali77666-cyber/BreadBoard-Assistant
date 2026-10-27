# BreadboardAOI

Native Windows (.NET 8) **Automated Optical Inspection (AOI)** and **real-time circuit verification workstation** for solderless breadboards.

The workstation runs a 60 FPS "reflex" loop (camera → homography → YOLOv11-Pose → 830-point PinMatrix → KiCad-style ERC/DRC → SkiaSharp overlay) and an asynchronous "cognitive" loop (Qwen3-VL-30B-A3B + Kokoro TTS) connected by a drop-oldest `Channel<T>` so the vision loop never blocks on inference or speech.

## Solution layout

| Project | Purpose |
| :--- | :--- |
| `src/1_BreadboardAOI.Hardware` | Win32 thread affinity (`SetThreadAffinityMask`), `VirtualLock`-ed triple frame buffer, NVML VRAM budget monitor. |
| `src/2_BreadboardAOI.Core` | Immutable data contracts, 830-hole `BreadboardSpec` + O(1) `PinMatrixLookup`, Union-Find `ElectricalRulesChecker`, target-netlist loader + ratsnest. |
| `src/3_BreadboardAOI.Vision` | OpenCV homography/ArUco calibration, hand-motion gate, ONNX Runtime GPU YOLOv11-Pose, micro-crop extractor. |
| `src/4_BreadboardAOI.Intelligence` | `llama-server.exe` Qwen3-VL client, prompt builder, sentence splitter, Sherpa-ONNX Kokoro TTS + NAudio reflex alarms. |
| `src/5_BreadboardAOI.App` | WPF/SkiaSharp workstation shell, Spectrum-dark theme, dual-loop coordinator, MVVM view model. |
| `src/6_BreadboardAOI.Tests` | xUnit + FluentAssertions coverage for the PinMatrix and DRC engine. |

## Prerequisites

- Windows 10/11 x64
- .NET SDK 8.0 (verified with 8.0.427)
- NVIDIA GPU + driver for the Phase 3+ CUDA path (the app degrades to CPU / synthetic frames without one)
- Model files dropped into `assets/models/` (see that folder's README) — none are committed

## Build & test

```powershell
dotnet restore BreadboardAOI.sln
dotnet build BreadboardAOI.sln --configuration Release
dotnet test src/6_BreadboardAOI.Tests/6_BreadboardAOI.Tests.csproj --configuration Release
```

## Run the workstation

```powershell
dotnet run --project src/5_BreadboardAOI.App/5_BreadboardAOI.App.csproj --configuration Release
```

The workstation boots with **Demo Mode ON** and the **synthetic camera**, so the full pipeline (830-pin CAD sheet, DRC engine, ratsnest arrows and spoken tutor) is testable on any Windows PC with no hardware attached. Use the toolbar to cycle to a real USB camera, replay the latest diagnostic, test the emergency alarm or mute the voice.

### Camera, synthetic board or uploaded photo

The camera button cycles **Synthetic → Camera 0 → Camera 1**. **LOAD IMAGE** — or dropping a
`.png` / `.jpg` / `.bmp` / `.webp` / `.tif` file onto the CAD viewport — replays a still photo
through the same homography → YOLO → DRC path. Photos are letterboxed into the 1280 × 720
capture frame, so an ordinary phone photo keeps its aspect ratio. To boot straight into an
image:

```powershell
$env:BREADBOARD_IMAGE_PATH = "C:\photos\breadboard.jpg"
```

## Remote AI on Colab (optional)

The Qwen3-VL tutor can run in the operator's own Google Colab runtime instead of this PC.
The app signs in with Google (OAuth 2.0 + PKCE on an ephemeral `127.0.0.1` loopback), writes
a one-time session key to Drive, and the notebook (`assets/colab/BreadboardAOI_Qwen.ipynb`)
reads it, starts `llama-server`, then publishes a Cloudflare tunnel URL back to Drive. The
tutor routes **Colab → local `llama-server` → rule-based tutor**, so the workstation always
has a fallback.

1. Create an OAuth **Desktop app** client in [Google Cloud Console](https://console.cloud.google.com/apis/credentials) and enable the Google Drive API.
2. Open the **REMOTE AI** tab, paste the client ID (secret optional), press **LINK GOOGLE**, then **PUBLISH SESSION**.
3. Press **OPEN NOTEBOOK** to run it in Colab, then **REFRESH ENDPOINT** (the app also polls every 15 s).

No Google account is required: **APPLY MANUAL ENDPOINT** accepts any tunnel URL plus its bearer
key. See `assets/colab/README.md` for the OAuth setup, Drive flow and release-asset splitting.

## Environment variables

| Variable | Default | Purpose |
| :--- | :--- | :--- |
| `BREADBOARD_IMAGE_PATH` | — | Boot with a still photo instead of a camera. |
| `BREADBOARD_MUTE_VOICE` | `0` | Start with the tutor muted. |
| `BREADBOARD_USE_COLAB` | `0` | Prefer the linked Colab endpoint at boot. |
| `BREADBOARD_COLAB_ENDPOINT` | — | Manual tunnel URL (no Google/Drive link needed). |
| `BREADBOARD_COLAB_KEY` | — | Bearer key for `BREADBOARD_COLAB_ENDPOINT`. |
| `BREADBOARD_GOOGLE_CLIENT_ID` / `_CLIENT_SECRET` | — | OAuth client instead of the in-app fields. |
| `BREADBOARD_QWEN_*` | see `QwenMoEOptions` | Local `llama-server` model paths, port, layers. |
| `BREADBOARD_VOICE_*` | see `VoiceSynthesisEngine` | Kokoro model / voice paths. |

## Model downloads (GitHub only)

Model weights are deliberately **not** committed. To avoid third-party CDN traffic, pull them from this repository's GitHub Releases instead of Hugging Face or other mirrors:

```powershell
gh release download models-v1 `
  --repo wallali77666-cyber/BreadBoard-Assistant `
  --dir assets/models
```

No model is required to start the workstation: Demo Mode, the synthetic camera and the rule-based offline tutor are the default path. Missing YOLO / Qwen / Kokoro files are reported in the Hardware dock and degrade gracefully.

## Implementation status

- [x] **Phase 1** — solution scaffolding, data contracts, CPU affinity, pinned triple frame buffer, NVML monitor.
- [x] **Phase 2** — 830-point PinMatrix, Union-Find ERC/DRC engine, target 555 netlist + ratsnest, unit tests.
- [x] **Phase 3** — OpenCV homography, hand gate, YOLO pose inference.
- [x] **Phase 4** — Qwen3-VL MoE service + Kokoro voice engine with offline rule-based tutor and SAPI fallback.
- [x] **Phase 5** — Spectrum-dark WPF workstation, SkiaSharp CAD viewport, demo-mode fault injection.
- [x] **Phase 6** — Google-linked Colab Qwen3-VL runtime, still-image input (`LOAD IMAGE`, drag & drop, `BREADBOARD_IMAGE_PATH`), dock tabs.

## Coordinate space

Camera frames are warped (homography) into a fixed **1000 × 500 px** virtual board. Terminal strips are 63 columns × 5 rows per half (`A..E` upper strip IDs `101..163`, `F..J` lower strip IDs `201..263`); four power rails use strip IDs `1..4`. 630 terminal holes + 200 rail holes = **830 tie points**.
