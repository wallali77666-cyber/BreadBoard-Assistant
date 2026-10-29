# BreadboardAOI

Native Windows (.NET 8) **Automated Optical Inspection (AOI)** and **real-time circuit verification workstation** for solderless breadboards.

The workstation runs a 60 FPS "reflex" loop (camera → homography → YOLOv11-Pose → 830-point PinMatrix → KiCad-style ERC/DRC → SkiaSharp overlay) and an asynchronous "cognitive" loop (Qwen3-VL-30B-A3B + BMO voice over Piper or Kokoro) connected by a drop-oldest `Channel<T>` so the vision loop never blocks on inference or speech.

## Solution layout

| Project | Purpose |
| :--- | :--- |
| `src/1_BreadboardAOI.Hardware` | Win32 thread affinity (`SetThreadAffinityMask`), `VirtualLock`-ed triple frame buffer, NVML VRAM budget monitor. |
| `src/2_BreadboardAOI.Core` | Immutable data contracts, 830-hole `BreadboardSpec` + O(1) `PinMatrixLookup`, Union-Find `ElectricalRulesChecker`, target-netlist loader + ratsnest, DIP/3-pin footprint placer, bundled KiCad symbol reader. |
| `src/3_BreadboardAOI.Vision` | OpenCV homography/ArUco calibration, hand-motion gate, ONNX Runtime GPU YOLOv11-Pose, micro-crop extractor for the tutor. |
| `src/4_BreadboardAOI.Intelligence` | `llama-server.exe` Qwen3-VL client, prompt builder, sentence splitter, BMO Piper + Sherpa-ONNX Kokoro TTS, NAudio reflex alarms. |
| `src/5_BreadboardAOI.App` | WPF/SkiaSharp workstation shell, Spectrum-dark theme, dual-loop coordinator, realistic 2D part glyphs, MVVM view model. |
| `src/6_BreadboardAOI.Tests` | xUnit + FluentAssertions coverage for the PinMatrix, DRC engine, pairing, voice and review regressions. |

The workstation is deliberately **2D-only**: the CAD viewport is the simulated
breadboard with realistic parts, X-ray clips, DRC markers and ratsnest arrows.
There is no schematic sheet, no view-mode switch and no `.kicad_sch` export; the
bundled KiCad library is still used to choose a footprint id for builder parts.

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

The suite currently has **236 tests** (PinMatrix snapping, DRC rules, target
netlist, ring buffer, pairing security, lens correction, hand-gate exposure
normalisation, BMO voice shaping, Piper CLI/WAV contract). `dotnet build` must report
**0 warnings / 0 errors**; the release artifact is `dotnet publish` on the
`5_BreadboardAOI.App` project.

The exact local commands (SDK path, test filters, run/stop, QA switches, publish)
are listed verbatim in [`docs/COMMANDS.md`](docs/COMMANDS.md).

## Run the workstation

```powershell
dotnet run --project src/5_BreadboardAOI.App/5_BreadboardAOI.App.csproj --configuration Release
```

The workstation boots with **Demo Mode ON** and the **synthetic camera**, so the full pipeline (830-pin CAD sheet, DRC engine, ratsnest arrows and spoken tutor) is testable on any Windows PC with no hardware attached. Use the toolbar to cycle to a real USB camera, replay the latest diagnostic, test the emergency alarm or mute the voice.

### Camera, synthetic board or uploaded photo

The camera button cycles **Synthetic → Camera 0 → Camera 1**. **LOAD IMAGE** — or dropping a
`.png` / `.jpg` / `.bmp` / `.webp` / `.tif` file onto the CAD viewport — replays a still photo
through the same homography → YOLO → DRC path as a camera. Photos are letterboxed into the
1280 × 720 capture frame, so an ordinary phone photo keeps its aspect ratio. No part extraction
runs on the picture: the 2D parts come from the builder, the demo catalogue or the live AOI
detector. To boot straight into an image:

```powershell
$env:BREADBOARD_IMAGE_PATH = "C:\photos\breadboard.jpg"
```

### 2D board viewport

The CAD viewport is the simulated breadboard, painted from the live
`DrcStatePacket`: 830 calibration dots, translucent X-ray clips, realistic part
glyphs, DRC crosshairs and KiCad ratsnest arrows. The toolbar groups the commands
into **INSPECT** (demo / X-ray), **IMAGE** (camera / load) and
**DIAGNOSE** (replay / alarm / mute), while the ruler, layer toggles and dock
layout behave like a CAD sheet.

The builder's footprint picker reads symbols straight from
`assets/kicad/kicad-symbols-master.zip` — an unmodified **28 MB** copy of the
official KiCad symbol libraries (22,910 symbols, 223 libraries), licensed
**CC-BY-SA 4.0** with the KiCad library design exception. It is read in place at
runtime; nothing is extracted or modified. See
`assets/kicad/NOTICE.md` and `assets/kicad/LICENSE-kicad.md`. The zip is part of the
release artifact, so a fresh checkout does not need a separate symbol download.

### Photo replay (no shape conversion)

**LOAD IMAGE**, a drop on the viewport, or `BREADBOARD_IMAGE_PATH` replaces the
camera with a still photo so the live homography/YOLO path can be exercised
without hardware. The vector board stays the editable surface; the picture is
available as the *Show camera / photo underlay* layer in **SCENE**. There is no
image→shapes converter, no import dialog and no automatic part extraction.

### Workspace layout (VIEW tab)

The left dock's **VIEW** tab turns the shell into an Adobe-style workspace:

* **PRESETS** — `WORKSTATION` (both docks), `WIDE CANVAS` (both hidden),
  `INSPECT FOCUS` (right dock only) and `BUILDER FOCUS` (BUILDER dock only).
* **PANELS** — independent `LEFT DOCK` / `RIGHT DOCK` visibility.
* **DOCK WIDTHS** — 200–460 px for the left dock and 240–520 px for the right,
  with the current value shown in pixels.
* **RESET LAYOUT** — back to the default workstation arrangement.

Changes are debounced for 700 ms and written to
`%LOCALAPPDATA%\BreadboardAOI\workspace-layout.json`; the file is also created on
first launch and flushed on window close. **PANELS** in the toolbar restores both
docks and opens VIEW, so `WIDE CANVAS` can never trap the layout panel off-screen.
Boot directly into it with `BREADBOARD_START_VIEW=1`, and apply a preset at boot
with `BREADBOARD_START_LAYOUT=builder` (or `canvas`, `inspect`, `workstation`).

## Remote AI on Colab (optional)

The Qwen3-VL tutor can run in the operator's own Google Colab runtime instead of this PC.
Vision, homography, the PinMatrix and the KiCad DRC engine stay local at 60 FPS; only the
diagnosis is offloaded. The tutor routes **Colab → local `llama-server` → rule-based tutor**,
so the workstation always has a fallback.

### One-paste pairing (default, no Google account)

1. Open `assets/colab/BreadboardAOI_Qwen.ipynb` in Colab, choose **Runtime → Change runtime type → T4 GPU**, then **Run all**.
2. Cell 5 prints one line: `BBAOI1|https://<tunnel>.trycloudflare.com|<key>`.
3. In the workstation open **AI ROUTING**, paste that line into **PAIR CODE**, press **CONNECT**.

The endpoint and key are stored under `%LOCALAPPDATA%\BreadboardAOI\remote-endpoint.json`, so the
next start reconnects automatically. **DISCONNECT** forgets it. `BBAOI1|url|key`, `url#key`,
`url?key=...` and a bare tunnel URL are all accepted.

### Drive auto-connect (optional)

Set `USE_DRIVE = True` in notebook cell 1 and link a Google account instead of pasting:

1. Create an OAuth **Desktop app** client in [Google Cloud Console](https://console.cloud.google.com/apis/credentials) and enable the Google Drive API.
2. In **AI ROUTING**, paste the client ID (secret optional), press **LINK GOOGLE**, then **PUBLISH SESSION**.
3. Press **OPEN NOTEBOOK** to run it in Colab; the app polls Drive every 15 s, or press **REFRESH ENDPOINT**.

See `assets/colab/README.md` for OAuth details, the Drive session format and model hosting.

## Local AI on this PC (LM Studio / Ollama)

The cognitive loop also accepts any OpenAI-compatible server running on loopback.
Open **AI ROUTING**, press **LM STUDIO** (`http://127.0.0.1:1234`) or **OLLAMA**
(`http://127.0.0.1:11434`). The app lists the models from `/v1/models`, picks the
first loaded one and routes the tutor there, labelled `LOCAL AI · 127.0.0.1:1234`.

Nothing is installed, downloaded or launched: start the provider yourself, load a
vision model and press the button. If nothing answers, the workstation reports it
and keeps the local `llama-server` / rule-based tutor. Vision, homography and the
DRC engine always stay in this process at 60 FPS.

## BMO-style tutor voice

The tutor speaks in a bright, close, lightly warbling "little robot" voice. Three
engines can produce it, and the `VIEW` dock's **VOICE ENGINE** buttons switch
between them at runtime:

* **BMO PIPER** — the local Rhasspy Piper CLI (`piper.exe`) driving a Piper/VITS
  `bmo.onnx` voice, the same engine the reference `rogeriochaves/bmo` project
  uses. Piper reads `bmo.onnx.json` next to the model, so a community BMO voice
  works without conversion or an extra NuGet package. The model already carries
  the character, so this path is played without the DSP pitch lift.
* **KOKORO** — Kokoro-82M through Sherpa-ONNX. Picks the youthful `af_sky`
  speaker (#10) when the export has it (otherwise the model's first voice), lifts
  pitch and formants by about 1.6 semitones, adds a subtle 5.1 Hz warble, a
  presence shelf and soft saturation, then normalises and fades the complete
  paragraph.
* **SAPI** — the brightest installed Windows voice (Zira on most PCs) through an
  SSML `+16%` pitch / `+6%` rate prosody wrapper. If a voice rejects SSML, the
  engine retries plain speech automatically.
* **AUTO** — tries BMO Piper, then Kokoro, then SAPI. Every explicit choice also
  falls back, so a missing model never leaves the workstation silent when SAPI
  exists. The dock shows the active engine and the exact missing file.
* **TEST VOICE** — speaks “Hi! I'm BMO. Let's check your circuit together.” with
  the active engine.

### Installing the BMO Piper voice

1. Download the Windows Piper build from
   <https://github.com/rhasspy/piper/releases> (Piper 0.0.x) and unzip the whole
   folder into `assets/models/piper/`, so `piper.exe`, its DLLs and its
   `espeak-ng-data/` folder sit together.
2. Put a Piper BMO voice next to it as `bmo.onnx` plus its `bmo.onnx.json`.
   Community sources include the `brenpoly/be-more-agent` `v1.0-voice` release
   and the PiperHub `bmo-adventure-time-piper-tts` model.
3. Launch the app, open `VIEW`, click **BMO PIPER**, then **TEST VOICE**.

No file is downloaded by the application. If any of the three files is absent the
button still works: the engine falls back and the dock prints the expected path.

The choice is remembered in `%LOCALAPPDATA%\BreadboardAOI\voice-settings.json`.
`BREADBOARD_VOICE_BACKEND` overrides it at boot.

* **Fluency** — clauses are joined into one playback stream with
  punctuation-aware pauses: about 150 ms after a full stop, 55 ms after a comma,
  and 65 ms after an unpunctuated clause. The audio device is not restarted per
  sentence, and each clause gets a click-free fade.
* **Graceful degradation** — no Piper files, no Kokoro files and no SAPI voice
  leaves the transcript working and the alarm available; nothing on the vision or
  DRC loop waits for speech.

The voice is configurable without a rebuild:

| Variable | Default | Effect |
| :--- | :--- | :--- |
| `BREADBOARD_VOICE_BACKEND` | `auto` | `auto`, `piper` / `bmo`, `kokoro` or `sapi`. Locks the choice for the session. |
| `BREADBOARD_VOICE_BMO` | `1` | Set to `0` for a neutral, unprocessed voice. |
| `BREADBOARD_VOICE_PITCH` | `1.10` | Pitch/formant multiplier; `1.0` is neutral, `1.2` is very bright. |
| `BREADBOARD_VOICE_SPEAKER` | `-1` | Kokoro speaker id; `-1` auto-selects the BMO profile. |
| `BREADBOARD_VOICE_SPEED` | `1.0` | Kokoro speaking-rate multiplier before the pitch lift. |
| `BREADBOARD_VOICE_CLAUSE_PAUSE_MS` | `55` | Pause after a comma or unpunctuated clause. |
| `BREADBOARD_VOICE_SENTENCE_PAUSE_MS` | `150` | Pause after a full stop, question or exclamation mark. |
| `BREADBOARD_VOICE_PIPER_EXE` | `assets/models/piper/piper.exe` | Piper executable. |
| `BREADBOARD_VOICE_PIPER_MODEL` | `assets/models/piper/bmo.onnx` | Piper BMO voice model. |
| `BREADBOARD_VOICE_PIPER_CONFIG` | `<model>.json` | Optional explicit voice JSON. |
| `BREADBOARD_VOICE_PIPER_SPEAKER` | `-1` | Piper speaker id; `-1` omits `--speaker` for single-voice models. |
| `BREADBOARD_VOICE_PIPER_LENGTH_SCALE` | `1.0` | Piper speed; above 1 speaks more slowly. |
| `BREADBOARD_VOICE_PIPER_NOISE_SCALE` / `_NOISE_W` | `0.667` / `0.8` | Piper expressiveness / phoneme duration variation. |
| `BREADBOARD_VOICE_PIPER_SENTENCE_SILENCE_MS` | `200` | Silence Piper inserts between its own sentence splits. |

## X-ray circuit builder

Press **BUILD MODE** (toolbar or the **BUILDER** dock). The camera and demo
detections are replaced by a hand-built circuit that the same
`ElectricalRulesChecker` judges in the 60 FPS loop:

1. Pick a part in the palette.
2. Choose its **FOOTPRINT**: 2 leads, a 3-pin row, or a DIP from 8 to 40 pins.
3. Left click its first tie point. A two-lead part asks for a second click; a
   DIP or 3-pin part is placed whole from that one click. You can also type
   labels such as `A15`, `D15` or `TOP_VCC_12` and press **PLACE PART**.
4. Right click a hole to remove the last part touching it.

The dock lists every placed part with its value and symbol, the resolved
netlist and the current DRC hint. **LOAD SELECTED DEMO** copies the scenario
chosen in **SCENE** into the builder; **LOAD 555 EXAMPLE** loads the reference
astable, which passes every rule. **X-RAY** reveals all 126 conductive clips
behind the board.

### Realistic part symbols

Every part is drawn as the object you would actually push into the board, not as
a coloured rectangle. The layer is on by default (**LAYERS → Show realistic part
symbols**); **Show CAD detection boxes** keeps the old YOLO rectangles available
for calibration work.

| Part | What is drawn |
| :--- | :--- |
| Resistor | beige body with four colour bands decoded from the value (`220R`, `4k7`, `10k`, `1M`) |
| LED | 5 mm dome in its own colour (red/green/blue/yellow/orange/white), cathode flat, emission arrows, glass highlight |
| Capacitor | radial can with a printed negative stripe |
| IC | notched DIP outline drawn with its real pin count, pin-1 dot, pin numbers and one lead dot per pin |
| 3-pin part | TO-92 / regulator body with three legs reaching three adjacent strips |
| Push button | square base with a round corrugated actuator |
| Jumper wire | bowed wire with a socket plug at each tie point |

Labels alternate above and below the body by `TrackId` and sit on a paper chip,
so neighbouring parts never overlap their text. Symbols are drawn in local
space, scale-corrected, and reuse pre-created paints and paths — the 60 FPS loop
still allocates nothing per frame.

### ICs, DIPs and 3-pin parts

**FOOTPRINT** offers `2-LEAD`, `3-PIN ROW` and DIP packages from 8 to 40 pins.
A package is placed whole from a single click on pin 1:

* **DIP** — pin 1 lands on the inner row of the half you clicked (`E` or `F`),
  then the package walks up the remaining rows and back along the other half
  across the centre trench. Clicking `E10` with DIP-8 gives
  `E10 E11 E12 E13 F13 F12 F11 F10`, so pins 1–4 and 5–8 sit in separate clips.
* **3-pin row** — three adjacent columns in the clicked row (`B12 B13 B14`),
  which is how a TO-92 or a small regulator module actually sits.

Placement is refused with a message instead of guessing when the package would
run past column 63, start on a power rail, or reuse a hole. Every pin keeps its
pin number in package order, the board glyph shows the real pin count, and the
DRC engine checks the package pin by pin.

Any KiCad symbol can be attached to a part, including symbols that are not DIPs.
The symbol id selects the part's footprint metadata, while the board keeps a DIP
adapter footprint of the chosen pin count.

### Part values and KiCad symbols

Every part carries a printed value and a symbol id:

* **QUICK PRESET / IC TYPE** fills value, symbol and pin count in one click.
  Resistors offer `220R`, `330R`, `1K`, `4K7`, `10K`, `100K`; capacitors offer
  ceramic and electrolytic values; the IC list offers `NE555P` (DIP-8),
  `LM358` (DIP-8), `74LS00` (DIP-14), `CD4017` (DIP-16) and `ATmega328P`
  (DIP-28). The fields below stay editable for a custom part.
* **VALUE** applies to the next placed part (`4K7`, `10uF`, `NE555P`); blank uses
  the kind default (`Resistor`, `Capacitor`, `LED`, `IC`, `SW`). Resistor band
  colours are decoded from this text.
* **KICAD SYMBOL / FOOTPRINT (OPTIONAL)** accepts `Library/Symbol`, or **BROWSE**
  opens a searchable list of the whole bundled library. **CLEAR** returns to the
  default.
* Select a row in **PLACED PARTS** to load its value and symbol into
  **SELECTED PART VALUE / SYMBOL**, edit them and press **APPLY VALUE / SYMBOL**.

Defaults: `Device/R`, `Device/C_Polarized`, `Device/LED`, `Timer/NE555P`,
`Switch/SW_Push`, and `Connector_Generic/Conn_01x03` for a 3-pin module.

## DRC rule set

| Code | Severity | Rule |
| :--- | :--- | :--- |
| 100 | critical | a VCC rail is bridged to a GND rail (names the offending jumper) |
| 101 | critical | both leads of a part sit in one metal clip (suggests the next free hole) |
| 102 | warning | a terminal is the only connection on its net |
| 104 | warning | a jumper whose two ends already share one clip |
| 105 | warning | more than three leads forced into one hole |
| 200 | warning | a target net is split across two nets (open net) |
| 202 | warning | two different target nets are bridged together |

`ErcReversedPolarity` (103) is declared for a future polarity check but is not
emitted by the engine today, so it is absent from the table.

The tutor answers with exactly three spoken sentences (what is wrong, why it
matters, how to fix it) and the **TUTOR / DIAGNOSTIC** dock shows the time to
first token and the total turn time for the last answer.

## Hardening in v0.9.0

The v0.9 batch adds multi-pin packages and the 2D-only workstation. The senior
review findings that affect them are fixed and covered by tests:

* **Multi-pin packages** — `DetectedComponent` carries pins 3..N in
  `ExtraLeads` and `SnappedComponent` exposes every pin via `ExtraPins` /
  `GetHole(i)`, so a DIP or 3-pin module is checked pin by pin instead of being
  reduced to its first two leads. `FootprintPlacer` generates the real hole order
  (across the centre trench for DIPs) and refuses overlaps.
* **Target netlist** — multi-pin (`"pins": [...]`) and single-pin (test point)
  entries are loaded in pin order instead of being skipped; a component with no
  hole is still reported. `TryLoadFromFile` catches access errors (they are not
  `IOException`s).
* **2D-only viewport** — the schematic sheet, its renderer and the `.kicad_sch`
  compiler are gone, so there is one canvas and one hit-test path. A stale demo
  `EMERGENCY:` banner clears as soon as the live DRC packet has no critical
  fault.

The earlier review remediation is unchanged:

* **Frame ring buffer** — the constructor can no longer leak a slot on a partial
  allocation, `Dispose` is atomic (no double free), and it waits briefly for an
  in-flight lease before releasing native memory.
* **DRC engine** — `EvaluateBoard` and its accessors are serialised, so a UI
  preview can never corrupt the 60 FPS reflex loop's Union-Find arrays. The
  topology hash canonicalises symmetric parts, so a flipped resistor no longer
  re-fires the tutor. A VCC↔GND bridge now resolves to `NET_VCC_GND_SHORT`
  instead of pretending the ground rail is VCC.
* **Pairing** — a pair code, manual URL or Drive session may only use `https://`;
  plain `http://` is accepted just for `127.0.0.1` / `localhost`. Private,
  link-local, CGNAT and cloud-metadata hosts are refused, so a pasted code cannot
  aim the camera feed at a LAN service. Known tunnel domains are recognised; a
  public custom domain is still accepted (the operator pasted it) but the status
  line labels it **custom host** and a paste that uses one asks for confirmation
  before any frame leaves the machine.
* **OAuth grant** — tokens are encrypted at rest with Windows DPAPI
  (`crypt32.dll`, current-user scope). Files written by older builds still load
  and are re-encrypted on the next save.
* **Pipeline** — the reflex and tutor loops report a fault to the status bar and
  keep retrying instead of dying silently behind a frozen viewport.
* **Vision** — YOLO inference is serialised (shared input tensor), and a
  homography whose determinant is near zero is rejected before warping.
* **Hand gate** — the motion thumbnail is min/max-normalised before `AbsDiff`, so
  auto-exposure or a global shadow shift no longer looks like a hand and the
  250 ms stability latch still fires. A local change (a hand entering) is still
  detected; tests cover both cases.
* **Lens correction** — `HomographyCalibrator.SetLensDistortion(cameraMatrix,
  distortionCoefficients)` pre-computes `InitUndistortRectifyMap` / `Remap` maps
  and applies them before ArUco detection and the perspective warp, so a
  barrel-distorted outer rail no longer drifts outside the 11.5 px snap radius.
  Calibration is reset when the intrinsics change. There is no in-app chessboard
  wizard yet: capture the intrinsics once (OpenCV `calibrateCamera`) and pass
  them in.

Known limitations, deliberately deferred:

* **Live detection** relies on the YOLO pose model. Without it the camera path
  produces no new parts; the builder and demo catalogue remain fully usable.
* **The 2D board is synthetic.** Parts are drawn as realistic glyphs on the
  calibrated board space; there is no schematic sheet, photo→shapes importer or
  netlist export.
* **Lens correction is API-only.** `SetLensDistortion` applies captured
  intrinsics, but the workstation does not yet walk the operator through a
  chessboard capture, so the default camera path is still raw + homography.
  The AprilTag / TensorRT / colour-classifier ideas from the review are future
  work rather than bug fixes.

## Environment variables

| Variable | Default | Purpose |
| :--- | :--- | :--- |
| `BREADBOARD_IMAGE_PATH` | — | Boot with a still photo instead of a camera. |
| `BREADBOARD_START_VIEW` | `0` | Open the left dock's VIEW layout panel at boot. |
| `BREADBOARD_START_LAYOUT` | — | Apply a VIEW preset at boot (`workstation`, `canvas`, `inspect`, `builder`). |
| `BREADBOARD_START_DEMO` | — | Preselect a demo scenario by keyword (`555`, `floating`, `short`). |
| `BREADBOARD_MUTE_VOICE` | `0` | Start with the tutor muted. |
| `BREADBOARD_USE_COLAB` | `0` | Prefer the linked Colab endpoint at boot. |
| `BREADBOARD_COLAB_ENDPOINT` | — | Manual tunnel URL (no Google/Drive link needed). |
| `BREADBOARD_COLAB_KEY` | — | Bearer key for `BREADBOARD_COLAB_ENDPOINT`. |
| `BREADBOARD_GOOGLE_CLIENT_ID` / `_CLIENT_SECRET` | — | OAuth client instead of the in-app fields. |
| `BREADBOARD_QWEN_*` | see `QwenMoEOptions` | Local `llama-server` model paths, port, layers. |
| `BREADBOARD_VOICE_*` | see `VoiceSynthesisEngine` | Piper / Kokoro model paths and voice parameters. |
| `BREADBOARD_VOICE_BACKEND` | `auto` | `auto`, `piper`, `kokoro` or `sapi`; also persisted by the VIEW panel. |
| `BREADBOARD_VOICE_CLAUSE_PAUSE_MS` | `55` | Silence inserted after a comma or unpunctuated clause. |
| `BREADBOARD_VOICE_SENTENCE_PAUSE_MS` | `150` | Silence inserted after a full stop. |
| `BREADBOARD_VOICE_BMO` / `_PITCH` / `_SPEAKER` | `1` / `1.10` / `-1` | BMO character, pitch lift and Kokoro speaker id. |
| `BREADBOARD_VOICE_PIPER_EXE` / `_MODEL` | `assets/models/piper/...` | Piper executable and BMO voice model. |

## Model downloads (GitHub only)

Model weights are deliberately **not** committed. To avoid third-party CDN traffic, pull them from this repository's GitHub Releases instead of Hugging Face or other mirrors:

```powershell
gh release download models-v1 `
  --repo wallali77666-cyber/BreadBoard-Assistant `
  --dir assets/models
```

No model is required to start the workstation: Demo Mode, the synthetic camera and the rule-based offline tutor are the default path. Missing YOLO / Qwen / Piper / Kokoro files are reported in the Hardware dock and degrade gracefully.

## Implementation status

- [x] **Phase 1** — solution scaffolding, data contracts, CPU affinity, pinned triple frame buffer, NVML monitor.
- [x] **Phase 2** — 830-point PinMatrix, Union-Find ERC/DRC engine, target 555 netlist + ratsnest, unit tests.
- [x] **Phase 3** — OpenCV homography, hand gate, YOLO pose inference.
- [x] **Phase 4** — Qwen3-VL MoE service + Piper / Kokoro voice engine with offline rule-based tutor and SAPI fallback.
- [x] **Phase 5** — Spectrum-dark WPF workstation, SkiaSharp CAD viewport, demo-mode fault injection.
- [x] **Phase 6** — Google-linked Colab Qwen3-VL runtime, still-image input (`LOAD IMAGE`, drag & drop, `BREADBOARD_IMAGE_PATH`), dock tabs.
- [x] **Phase 7** — one-paste Colab pairing (`BBAOI1|url|key`), prebuilt CUDA `llama-server`, persisted endpoint, optional Drive auto-connect.
- [x] **Phase 8** — interactive X-ray circuit builder, expanded DRC rule set (104/105/202), resolved-netlist panel, LM Studio / Ollama discovery, three-sentence tutor with turn latency.
- [x] **Review remediation (v0.8.0)** — ring-buffer lifetime, thread-safe DRC + canonical hash + power-short naming, DPAPI token encryption, HTTPS-only pairing, worker-loop fault reporting, YOLO serialisation, netlist diagnostics.
- [x] **Realistic symbols (v0.8.0)** — physical 2D part glyphs for builder parts and live detections.
- [x] **Phase 9 (v0.9.0)** — bundled KiCad symbols with values and pin numbers, DIP 8–40 / 3-pin packages, quick value / IC-type presets, Adobe-style VIEW workspace with a persisted dock layout, and the fluent BMO-style tutor voice (Kokoro pitch/formant lift + SAPI SSML fallback).
- [x] **Selectable voices (v0.9.x)** — `BMO PIPER` (Rhasspy Piper CLI + `bmo.onnx`), `KOKORO`, `SAPI` and `AUTO` buttons in the VIEW dock, a live `TEST VOICE` audition, persisted engine choice (`voice-settings.json`), automatic fallback with the missing path shown in the dock, and a `piper` / `kokoro` / `sapi` / `auto` boot override.
- [x] **Review remediation (v0.9.0)** — multi-pin + test-point target netlist entries, private/link-local pairing refusal with known-tunnel labelling, optional camera intrinsics / lens undistortion.
- [x] **2D-only workstation (v0.9.x)** — removed the schematic sheet, `SCHEMATIC` / `SPLIT` view modes and the `.kicad_sch` compiler; the toolbar is grouped into INSPECT / IMAGE / DIAGNOSE, the left dock is SCENE / BUILDER / AI ROUTING / VIEW, and the viewport is the simulated 2D board only. The photo→shapes importer and its dialog are gone; `LOAD IMAGE` is a frame-source replay.

## Coordinate space

Camera frames are warped (homography) into a fixed **1000 × 500 px** virtual board. Terminal strips are 63 columns × 5 rows per half (`A..E` upper strip IDs `101..163`, `F..J` lower strip IDs `201..263`); four power rails use strip IDs `1..4`. 630 terminal holes + 200 rail holes = **830 tie points**.
