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

## Implementation status

- [x] **Phase 1** — solution scaffolding, data contracts, CPU affinity, pinned triple frame buffer, NVML monitor.
- [x] **Phase 2** — 830-point PinMatrix, Union-Find ERC/DRC engine, target 555 netlist + ratsnest, unit tests.
- [ ] **Phase 3** — OpenCV homography, hand gate, YOLO pose inference.
- [ ] **Phase 4** — Qwen3-VL MoE service + Kokoro voice engine.
- [ ] **Phase 5** — Spectrum-dark WPF workstation, SkiaSharp CAD viewport, demo-mode fault injection.

## Coordinate space

Camera frames are warped (homography) into a fixed **1000 × 500 px** virtual board. Terminal strips are 63 columns × 5 rows per half (`A..E` upper strip IDs `101..163`, `F..J` lower strip IDs `201..263`); four power rails use strip IDs `1..4`. 630 terminal holes + 200 rail holes = **830 tie points**.
