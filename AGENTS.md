# AGENTS.md — BreadboardAOI Master Engineering Specification (For DeepSeek / OpenCode)

## 1. Your Role & Operational Mandate
You are the **Principal C# Implementation Agent** operating inside OpenCode under a Senior Systems Architect. You are building **`BreadboardAOI`**, a native Windows (.NET 8) **Automated Optical Inspection (AOI)** and **Real-Time Circuit Verification Workstation** for solderless breadboards.

### Core Workflow Rules for DeepSeek in OpenCode:
1. **Never Hallucinate Packages or APIs:** Use ONLY the locked NuGet packages and versions defined in Section 3.
2. **Compile & Verify Every Step:** After creating or editing any file, always run `dotnet build --configuration Release` via the terminal tool to verify `0 Warnings, 0 Errors` before moving to the next task.
3. **Respect the Dual-Loop Hardware Split:** Never block the 60 FPS Vision/DRC loop with LLM or TTS calls. Always communicate between the Fast Reflex Loop and the Cognitive MoE Loop using `System.Threading.Channels`.
4. **Zero-Allocation Hot Path:** Inside the 60 FPS loop (`Camera -> Homography -> YOLO -> PinMatrix -> DRC -> SkiaSharp`), do NOT allocate new heap objects (`new List<>()`, LINQ `.Select()`, or boxed structs) per frame. Pre-allocate buffers or use `Span<T>`, `ArrayPool<T>`, and `readonly record struct`.

---

## 2. Target Hardware & Hybrid CPU/GPU Allocation Strategy
This software is engineered for a specific local workstation. Every thread and memory buffer must be tuned to these exact physical limits:

* **CPU:** Intel Core i5-13600K (Hybrid Architecture: **6 P-Cores / 12 Threads @ 5.1 GHz** + **8 E-Cores / 8 Threads @ 3.9 GHz** = 20 Logical Processors).
  * **Logical Processors `0..11` (P-Cores, Affinity Mask `0x00FFF`):** Dedicated EXCLUSIVELY to `Qwen3-VL-30B-A3B` AVX2 matrix math (DDR4 offloaded experts) and `SherpaOnnx` neural TTS synthesis.
  * **Logical Processors `12..17` (E-Cores, Affinity Mask `0x3F000`):** Dedicated to 60 FPS `OpenCvSharp4` camera capture, ArUco homography warping, and `YOLOv11-Pose` pre/post-processing.
  * **Logical Processors `18..19` (E-Cores, Affinity Mask `0xC0000`):** Dedicated to the sub-millisecond `ElectricalRulesChecker` (KiCad DRC engine) and the 60 FPS `SkiaSharp` CAD UI thread.
* **GPU:** NVIDIA GeForce RTX 3060 (**12 GB GDDR6 VRAM** @ 360 GB/s).
  * **VRAM Budget:**
    * `YOLOv11s-Pose` (FP16 TensorRT/CUDA): **~450 MB**
    * `SkiaSharp` DirectX/OpenGL SwapChain: **~350 MB**
    * `Qwen3-VL-30B-A3B-Instruct` (`Q4_K_M` GGUF + `mmproj-f16.gguf` + `q8_0` KV Cache): **~10,600 MB** (`--n-gpu-layers 99 --n-cpu-moe 22`)
    * OS Reserve Buffer: **~600 MB**
* **System RAM:** **32 GB DDR4-3200 MHz** (~51.2 GB/s dual-channel).
  * Holds **~9.4 GB** of offloaded Sparse MoE routed experts from `Qwen3-VL-30B-A3B` pinned in physical RAM via `--mlock` / Win32 `VirtualLock` so Windows never pages weights to the NVMe SSD.
  * Because `Qwen3-VL-30B-A3B` activates only **3B parameters per token**, the i5-13600K P-Cores only stream ~1.0–1.5 GB of weights from DDR4 per token, sustaining **18–22 tokens/sec**.

---

## 3. Manager-Locked Tech Stack & NuGet Packages
When generating `.csproj` files, use these exact libraries and versions. Do NOT substitute them with older wrappers (like `Emgu.CV` or `System.Drawing.Common`).

### Global Project Properties (`Directory.Build.props`)
```xml
<Project>
  <PropertyGroup>
    <TargetFramework>net8.0-windows</TargetFramework>
    <LangVersion>12.0</LangVersion>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <PlatformTarget>x64</PlatformTarget>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <TieredPGO>true</TieredPGO>
    <Optimize>true</Optimize>
  </PropertyGroup>
</Project>
```

### Approved NuGet Matrix per Project
| Project | Package ID | Version | Purpose |
| :--- | :--- | :--- | :--- |
| **`1_BreadboardAOI.Hardware`** | *(Pure Win32 P/Invoke)* | N/A | `kernel32.dll` (`SetThreadAffinityMask`, `VirtualLock`), `nvml.dll` |
| **`2_BreadboardAOI.Core`** | `System.Text.Json` | `8.0.5` | Zero-dependency PinMatrix, Union-Find Netlist, KiCad ERC/DRC Engine |
| **`3_BreadboardAOI.Vision`** | `OpenCvSharp4` | `4.10.0.20240616` | Camera capture, ArUco detection, `WarpPerspective`, `AbsDiff` hand gate |
| **`3_BreadboardAOI.Vision`** | `OpenCvSharp4.runtime.win` | `4.10.0.20240616` | Native C++ AVX2 OpenCV binaries for Windows x64 |
| **`3_BreadboardAOI.Vision`** | `Microsoft.ML.OnnxRuntime.Gpu` | `1.19.2` | Runs `yolov11s_breadboard_pose_fp16.onnx` on RTX 3060 CUDA/TensorRT |
| **`4_BreadboardAOI.Intelligence`** | `org.k2fsa.sherpa.onnx` | `1.10.30` | Ultra-fast local CPU neural TTS (`Kokoro-82M` / `Piper` ONNX) |
| **`4_BreadboardAOI.Intelligence`** | `NAudio` | `2.2.1` | Low-latency `WaveOutEvent` audio playback (<10ms short alarms + TTS stream) |
| **`5_BreadboardAOI.App`** | `SkiaSharp` | `2.88.8` | Hardware-accelerated 2D CAD overlay rendering (X-Ray strips, Ratsnest) |
| **`5_BreadboardAOI.App`** | `SkiaSharp.Views.WPF` | `2.88.8` | Hosts `SKElement` / `SKGLElement` inside the WPF Workstation UI |
| **`5_BreadboardAOI.App`** | `CommunityToolkit.Mvvm` | `8.3.2` | Source-generated, high-performance MVVM bindings (`[ObservableProperty]`) |
| **`6_BreadboardAOI.Tests`** | `xunit` & `FluentAssertions` | `2.9.2` / `6.12.1` | Automated unit testing for the PinMatrix and KiCad DRC engine |

> **Note on `Qwen3-VL-30B-A3B` Integration:** Instead of fighting out-of-date C# GGUF bindings for brand-new Qwen3-VL vision projectors, `QwenMoEService.cs` manages the official CUDA-compiled `llama-server.exe` binary as a persistent background child process pinned to P-Cores (`0x00FFF`) and communicates with it over local loopback HTTP SSE (`[http://127.0.0.1:8089/v1/chat/completions](http://127.0.0.1:8089/v1/chat/completions)`) using `SocketsHttpHandler`.

---

## 4. Solution File Structure (`BreadboardAOI.sln`)
Maintain this exact directory tree:

```text
BreadboardAOI/
├── AGENTS.md                                  # This specification file
├── Directory.Build.props                      # Global .NET 8 x64 + AVX2 + Unsafe configuration
├── BreadboardAOI.sln                          # Master Solution
├── assets/
│   ├── models/                                # Placeholders/paths for YOLO ONNX, Qwen3-VL GGUF, Kokoro ONNX
│   ├── audio_reflex/                          # Pre-generated WAV files for <10ms short-circuit alarms
│   └── schematics/
│       └── target_555_blinker_netlist.json    # Reference KiCad-style target netlist
└── src/
    ├── 1_BreadboardAOI.Hardware/
    │   ├── 1_BreadboardAOI.Hardware.csproj
    │   ├── CpuAffinityManager.cs              # P/Invoke SetThreadAffinityMask (P-Cores vs E-Cores)
    │   ├── PinnedFrameRingBuffer.cs           # Lock-free 3-slot NativeMemory ring buffer for 1080p frames
    │   └── VramBudgetMonitor.cs               # Queries nvml.dll for live RTX 3060 12GB VRAM telemetry
    │
    ├── 2_BreadboardAOI.Core/
    │   ├── 2_BreadboardAOI.Core.csproj
    │   ├── Contracts/
    │   │   └── AoiDataContracts.cs            # Immutable structs: HoleCoord, DetectedComponent, DrcViolation, DrcStatePacket
    │   ├── Geometry/
    │   │   ├── BreadboardSpec.cs              # 830-tie-point physical constants (63 cols, rows A-E & F-J, 4 rails)
    │   │   └── PinMatrixLookup.cs             # O(1) spatial hash grid snapping (px, py) in 1000x500 space to HoleCoord
    │   └── DrcEngine/
    │       ├── ElectricalRulesChecker.cs      # Union-Find jumper merger + KiCad ERC/DRC rules
    │       └── TargetSchematicLoader.cs       # Loads target JSON netlist and computes Ratsnest air-wire vectors
    │
    ├── 3_BreadboardAOI.Vision/
    │   ├── 3_BreadboardAOI.Vision.csproj
    │   ├── CameraCaptureService.cs            # 60 FPS E-Core frame grabber into PinnedFrameRingBuffer
    │   ├── HomographyCalibrator.cs            # ArUco 4-corner detection + Cv2.WarpPerspective to 1000x500
    │   ├── HandMotionGate.cs                  # Cv2.AbsDiff motion blocker; fires after 250ms of board stability
    │   ├── YoloPoseInferenceEngine.cs         # ONNX Runtime GPU inference outputting box + Lead1/Lead2 keypoints
    │   └── MicroCropExtractor.cs              # Slices 256x256 PNG around faulty/new components for fast Qwen prefill
    │
    ├── 4_BreadboardAOI.Intelligence/
    │   ├── 4_BreadboardAOI.Intelligence.csproj
    │   ├── QwenMoEService.cs                  # Launches & streams from llama-server.exe on P-Cores (0x00FFF)
    │   ├── DiagnosticPromptBuilder.cs         # Combines DRC JSON + 256x256 crop into a concise 2-sentence tutor prompt
    │   ├── SentenceStreamSplitter.cs          # Yields complete sentences on '.', '!', '?' from incoming token stream
    │   └── VoiceSynthesisEngine.cs            # SherpaOnnx (Kokoro) + NAudio WaveOutEvent + <10ms emergency WAV reflex
    │
    ├── 5_BreadboardAOI.App/
    │   ├── 5_BreadboardAOI.App.csproj
    │   ├── App.xaml & App.xaml.cs
    │   ├── Themes/
    │   │   └── SpectrumDarkTheme.xaml         # Adobe Spectrum / Visual Studio Dark workstation dictionary
    │   ├── Orchestration/
    │   │   └── InspectionPipelineCoordinator.cs # Dual-Loop Channel coordinator (60 FPS E-Core vs Async P-Core MoE)
    │   ├── Rendering/
    │   │   └── SkiaArOverlayRenderer.cs       # 60 FPS CAD viewport: Pasteboard, rulers, X-Ray strips, Ratsnest arrows
    │   ├── ViewModels/
    │   │   └── MainWorkstationViewModel.cs    # Binds telemetry, layer toggles, DRC table, and live voice transcript
    │   └── Views/
    │       └── MainWindow.xaml & .cs          # 4-Zone Docking Workstation UI
    │
    └── 6_BreadboardAOI.Tests/
        ├── 6_BreadboardAOI.Tests.csproj
        ├── PinMatrixLookupTests.cs            # Verifies coordinate snapping accuracy across all 830 holes
        └── ElectricalRulesCheckerTests.cs     # Verifies VCC-GND shorts, Same-Strip shorts, and Floating Pins
```

---

## 5. Non-Negotiable Architecture & Design Laws

### Law 1: How the PinMatrix + KiCad DRC Engine Must Work
1. **Warped Coordinate Space (`1000 x 500` pixels):**
   * Top Power Rails: `StripTopVcc = 1` (`Y = 25`), `StripTopGnd = 2` (`Y = 45`).
   * Bottom Power Rails: `StripBotVcc = 3` (`Y = 455`), `StripBotGnd = 4` (`Y = 475`).
   * Terminal Strips: 63 columns (`Col 1..63`, spaced evenly from `X = 40` to `X = 960`, pitch $\approx 14.83\text{ px}$).
   * Upper Half (`Rows A..E`, `Y = 95..175`): Each 5-hole vertical column (`A1..E1` through `A63..E63`) shares a single internal metal clip (`StripId = 101..163`).
   * Center IC Trench: `Y = 175..225` (non-conductive divider).
   * Lower Half (`Rows F..J`, `Y = 225..305`): Each 5-hole vertical column (`F1..J1` through `F63..J63`) shares a single internal metal clip (`StripId = 201..263`).
2. **Union-Find Netlist Resolution:**
   * Every `JumperWire` detected between `Hole1` and `Hole2` calls `UnionStrips(Hole1.StripId, Hole2.StripId)`.
   * Always keep Power Rail IDs (`1..4`) as the canonical root when merging sets so any strip connected to power via a chain of jumpers automatically resolves to `NET_VCC` or `NET_GND`.
3. **The 4 Mandatory DRC Rules:**
   * `ErcVccGndDirectShort (100)` [`Critical`]: `FindRootNet(StripTopVcc) == FindRootNet(StripTopGnd)` (or bottom rails). Must fire the hardware WAV alarm in `< 10 ms`.
   * `ErcSameStripShort (101)` [`Critical`]: A non-jumper component has `FindRootNet(Pin1Hole.StripId) == FindRootNet(Pin2Hole.StripId)`. Compute `SuggestedTargetHole` as the nearest adjacent unpopulated strip in the same row (`Col + 1`) so `SkiaArOverlayRenderer` draws the green corrective Ratsnest arrow.
   * `ErcFloatingLead (102)` [`Warning`]: A non-jumper component lead sits on a resolved net where total connected terminals across the entire board `== 1`.
   * `DrcSchematicNetMismatch (200)` [`Warning`]: Component connectivity graph does not match `target_555_blinker_netlist.json`.

### Law 2: Strict Workstation UI Specification (NO "AI Design" Tropes)
The application must look and feel like **Adobe InDesign / Visual Studio 2022 / KiCad**—an industrial engineering workstation:
* **NO** purple/blue gradients, **NO** glowing neon buttons, **NO** rounded `16px` chat bubbles, **NO** consumer "AI assistant" avatars.
* **Color Tokens (`SpectrumDarkTheme.xaml`):**
  * `PasteboardBrush`: `#141414` (Matte dark surround behind the 1000x500 breadboard canvas)
  * `PanelBackgroundBrush`: `#1E1E1E` (Dock panels)
  * `ToolbarSurfaceBrush`: `#252526` (Top command bar & panel headers)
  * `BorderBrush`: `#333337` (`1px` crisp structural borders everywhere; `CornerRadius="0"` or `"2"`)
  * `AccentBlueBrush`: `#007ACC` (Active tool selection & focus border)
  * `DrcPassBrush`: `#2D9D78` | `DrcWarningBrush`: `#E68619` | `DrcCriticalBrush`: `#D7373F`
* **Typography:**
  * Panel Headers & Menus: `Segoe UI`, `11px` SemiBold, Uppercase tracking for dock headers (`NETLIST & LAYERS`, `DRC VIOLATIONS`, `PIN INSPECTOR`).
  * Coordinates, Net IDs & Telemetry: `Cascadia Mono` or `Consolas`, `11px`.
* **CAD Viewport Features (`SkiaArOverlayRenderer.cs`):**
  * Top horizontal pixel/column ruler (`Cols 1..63`) and left vertical row ruler (`+, -, A..E, F..J, +, -`) rendered in `#252526` with `#888888` ticks.
  * Toggleable layers from the Left Dock: `Show Pin Matrix Dots`, `Show X-Ray Metal Clips`, `Show KiCad Ratsnest Arrows`, `Show Component Boxes`.

---

## 6. Phased Execution Plan & Task List for OpenCode

Execute these 5 phases in exact sequential order. Do not skip ahead until the current phase builds cleanly and passes its verification gate.

### PHASE 1: Solution Scaffolding, Data Contracts & Hardware Isolation
* **Task 1.1:** Create `Directory.Build.props`, `BreadboardAOI.sln`, and all 6 `.csproj` files with the exact NuGet packages from Section 3.
* **Task 1.2:** Implement `src/2_BreadboardAOI.Core/Contracts/AoiDataContracts.cs` containing `ComponentKind`, `DrcSeverity`, `ViolationCode`, `HoleCoord`, `DetectedComponent`, `SnappedComponent`, `DrcViolation`, and `DrcStatePacket`.
* **Task 1.3:** Implement `src/1_BreadboardAOI.Hardware/CpuAffinityManager.cs` (Win32 `SetThreadAffinityMask` with `PCoresMask = 0x00FFF`, `ECoresVisionMask = 0x3F000`, `ECoresDrcUiMask = 0xC0000`) and `PinnedFrameRingBuffer.cs` (unmanaged 3-slot triple buffer using `NativeMemory.AlignedAlloc`).
* **Verification Gate 1:** Run `dotnet build` and verify all 6 projects compile with `0 errors`.

### PHASE 2: The 830-Point PinMatrix & Deterministic KiCad DRC Engine
* **Task 2.1:** Implement `BreadboardSpec.cs` and `PinMatrixLookup.cs` in `2_BreadboardAOI.Core/Geometry/`. Generate all 830 holes (`A1..E63`, `F1..J63`, plus top/bottom power rails) in `1000x500` coordinate space and provide an O(1) / fast spatial lookup `TrySnapToHole(float px, float py, out HoleCoord hole, float maxRadius = 11.5f)`.
* **Task 2.2:** Implement `ElectricalRulesChecker.cs` and `TargetSchematicLoader.cs` in `2_BreadboardAOI.Core/DrcEngine/`. Implement Union-Find jumper merging, `ErcVccGndDirectShort`, `ErcSameStripShort` (with automatic adjacent empty strip lookup for `SuggestedTargetHole`), `ErcFloatingLead`, and `DrcSchematicNetMismatch`.
* **Task 2.3:** Create unit tests in `6_BreadboardAOI.Tests/PinMatrixLookupTests.cs` and `ElectricalRulesCheckerTests.cs`. Test:
  1. Snapping pixel coordinates to `E15` and `A15` and verifying both share `StripId = 115`.
  2. Bridging `StripTopVcc` to `A12` via a `JumperWire` and verifying `Strip 112` resolves to `NET_VCC`.
  3. Placing an LED with both legs in `A15` and `D15` and verifying `ErcSameStripShort` is emitted with `SuggestedTargetHole = D16` in `< 0.1 ms`.
* **Verification Gate 2:** Run `dotnet test` and confirm 100% of unit tests pass.

### PHASE 3: OpenCV Homography, Hand Motion Gate & YOLOv11-Pose Engine
* **Task 3.1:** Implement `HomographyCalibrator.cs` using `OpenCvSharp4`. Detect 4 corner ArUco markers (`PredefinedDictionaryName.Dict4X4_50`, IDs `0, 1, 2, 3`), compute the $3 \times 3$ perspective transform, and warp incoming camera frames into a pre-allocated `1000x500` `Mat`. Include a fallback Synthetic Test Pattern generator so the app runs on machines without a physical camera attached.
* **Task 3.2:** Implement `HandMotionGate.cs` using `Cv2.AbsDiff` and non-zero pixel count. Lock the frame and emit `IsBoardStable = true` only after motion stays below threshold for `250 ms`.
* **Task 3.3:** Implement `YoloPoseInferenceEngine.cs` using `Microsoft.ML.OnnxRuntime.Gpu` (`SessionOptions.AppendExecutionProvider_CUDA(0)` with CPU fallback if model file is missing) and `MicroCropExtractor.cs` (crops a `256x256` PNG around any component with a violation).
* **Verification Gate 3:** Run `dotnet build` and verify zero unmanaged memory leaks or compiler warnings.

### PHASE 4: Qwen3-VL-30B-A3B MoE Service & Low-Latency Voice Engine
* **Task 4.1:** Implement `QwenMoEService.cs` in `4_BreadboardAOI.Intelligence`.
  * Launch `llama-server.exe` pinned to `CpuAffinityManager.PCoresMask (0x00FFF)` with arguments:
    `--model "assets/models/Qwen3-VL-30B-A3B-Instruct-Q4_K_M.gguf" --mmproj "assets/models/mmproj-Qwen3-VL-30B-f16.gguf" --n-gpu-layers 99 --n-cpu-moe 22 --flash-attn --cache-type-k q8_0 --cache-type-v q8_0 --ctx-size 4096 --threads 6 --threads-batch 12 --mlock --port 8089`
  * Stream multimodal chat completions (sending the `256x256` base64 PNG crop + structured DRC JSON report) via `IAsyncEnumerable<string>`.
* **Task 4.2:** Implement `DiagnosticPromptBuilder.cs`, `SentenceStreamSplitter.cs`, and `VoiceSynthesisEngine.cs`.
  * `VoiceSynthesisEngine` must provide `PlayEmergencyReflexAlarm()` (`< 10 ms` pre-loaded WAV playback via `NAudio`) and `SpeakSentenceAsync(string sentence)` (using `SherpaOnnx` `OfflineTts` on P-Cores).
* **Verification Gate 4:** Build `4_BreadboardAOI.Intelligence` and verify graceful handling when model files are not yet downloaded on disk.

### PHASE 5: Adobe Spectrum / Visual Studio Dark Workstation UI & SkiaSharp CAD Viewport
* **Task 5.1:** Create `Themes/SpectrumDarkTheme.xaml` implementing the `#141414` pasteboard, `#1E1E1E` panels, `#252526` headers, and `1px #333337` borders.
* **Task 5.2:** Complete `SkiaArOverlayRenderer.cs` with:
  * Top & Left CAD rulers showing pixel ticks and breadboard column/row labels (`1..63`, `A..J`).
  * Optional green PinMatrix calibration dots on all 830 holes.
  * Translucent red X-Ray strip capsules (`#D7373F`) over shorted 5-hole conductive strips.
  * Precision CAD crosshairs on offending holes and dashed green KiCad Ratsnest arrows (`#2D9D78`) pointing to `SuggestedTargetHole`.
  * Interactive mouse-hover pin inspector (snaps mouse cursor `(X, Y)` to `PinMatrixLookup` and highlights the entire 5-hole strip under the cursor).
* **Task 5.3:** Build `InspectionPipelineCoordinator.cs`, `MainWorkstationViewModel.cs`, and `MainWindow.xaml`.
  * Add a **"Simulate Faulty Circuit (Demo Mode)"** toggle button in the top toolbar that injects realistic `DetectedComponent[]` scenarios (1: Same-Strip LED Short on Row 15, 2: Direct VCC-GND Jumper Short, 3: Floating Resistor Lead, 4: Valid 555 Timer Circuit) so the entire DRC engine, SkiaSharp CAD overlay, and Qwen3-VL prompt inspector can be tested immediately upon launch.
* **Verification Gate 5:** Run `dotnet test` and `dotnet build --configuration Release`. Ensure all tests pass and the WPF executable builds cleanly.