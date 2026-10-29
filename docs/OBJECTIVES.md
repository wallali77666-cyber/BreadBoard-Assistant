# BreadboardAOI — Engineering Objectives

**Status:** binding for v0.9.x and later.
**Owner:** Senior Systems Architect.
**Last updated:** 2026-10-29.

This file records the objectives the implementation must satisfy when a review,
a pasted prototype, or a feature request adds requirements. An objective is only
"done" when the code exists, a test locks the behaviour, and the release build
is clean.

---

## 1. Product objective

Ship a native Windows (.NET 8) AOI / DRC workstation that:

1. inspects a physical breadboard at 60 FPS on the E-Cores;
2. judges the circuit with the deterministic KiCad-style DRC engine;
3. explains faults in three spoken sentences from a local-or-remote tutor;
4. keeps the workstation on the simulated 2D board, where the student places,
   inspects and corrects real component shapes;
5. provides no schematic sheet, photo→shapes converter or netlist export.

Everything except the optional remote tutor model runs locally. No feature may
download model or asset files onto the operator's PC at runtime.

---

## 2. 2D workstation objectives

| ID | Objective | Implementation | Status |
| :-- | :--- | :--- | :--- |
| B-1 | One viewport: the simulated breadboard with realistic 2D parts | `SkiaArOverlayRenderer.RenderCadViewport` paints the board, 830 pins, X-ray clips, part glyphs and DRC markers from one `DrcStatePacket` | Done. |
| B-2 | Photo replay without part extraction | `LOAD IMAGE` / `BREADBOARD_IMAGE_PATH` swap the camera for a still frame; the optional underlay shows it behind the vector board | Done. |
| B-3 | Builder footprints for DIP 8–40, 3-pin modules and KiCad symbols | `FootprintPlacer`, `PinCountOptions` and `SymbolPickerDialog` | Done. |
| B-4 | Grouped, CAD-style command surface | Toolbar clusters **INSPECT / IMAGE / DIAGNOSE**; left dock tabs **SCENE / BUILDER / AI ROUTING / VIEW** | Done. |
| B-5 | Corrective DRC overlay | Red X-ray capsules, crosshairs and dashed green ratsnest arrows to `SuggestedTargetHole` | Done. |
| B-6 | No dead import or schematic code left behind | `SchematicSheetRenderer`, `SchematicImportPlanner`, `SchematicSymbolDetector`, the `.kicad_sch` compiler, `BoardImageImporter`, `PhotoComponentDetector`, `ImageImportDialog` and the `7_BreadboardAOI.DesktopServer` project are deleted | Done. |

### 2.1 Deliberate decisions (do not "fix" these back)

* **One engine, one canvas.** The CAD viewport is the only render target; hover,
  hover-inspector hit testing and DRC markers share the same transform.
* **No photo→shapes importer.** `LOAD IMAGE` is a frame-source replay; parts come
  from the builder, the demo catalogue or the live AOI detector only.
* **KiCad library is a footprint source, not a sheet renderer.** The bundled
  `assets/kicad/kicad-symbols-master.zip` stays because the builder's symbol
  picker reads it; it is not drawn as a schematic.
* **Measured performance, not claimed performance.** Frame-time figures are
  targets to measure, not contractual guarantees on every machine.

### 2.2 Acceptance criteria

1. `dotnet build BreadboardAOI.sln --configuration Release --no-restore`
   reports **0 warnings / 0 errors**.
2. `dotnet test` is green, including the matrix snapping, DRC rules and
   target-netlist cases.
3. The solution contains six projects; `7_BreadboardAOI.DesktopServer` is absent
   from `BreadboardAOI.sln` and from every project reference.
4. No `SCHEMATIC` / `SPLIT` control, no `EXPORT KICAD` button and no schematic
   sheet renderer remains in the app.
5. No `IMAGE → SHAPES` command, import dialog or photo detector remains; a
   loaded photo only changes the camera frame source.

---

## 3. Workspace and voice objectives

| ID | Objective | Implementation | Status |
| :-- | :--- | :--- | :--- |
| W-1 | An Adobe-style workspace editor inside the app, not a fixed three-column shell | Left dock **VIEW** tab: four presets, independent left/right dock visibility, two width sliders, reset | Done. |
| W-2 | The layout survives a restart and can be recovered if it hides its own panel | Debounced JSON at `%LOCALAPPDATA%\BreadboardAOI\workspace-layout.json`, startup creation, window-close flush, toolbar **PANELS** button | Done. |
| W-3 | Tuning the layout never blocks the 60 FPS loop | Layout changes only touch WPF bindings and a 700 ms dispatcher timer | Done. |
| V-1 | The tutor sounds fluent, not like separate clips stitched together | One playback stream, punctuation-aware 150 ms / 55 ms pauses, click-free fades, loudness normalisation | Done. |
| V-2 | A recognisable bright, youthful BMO-style voice | Kokoro `af_sky` auto-selection, +1.6 semitone pitch/formant lift, 5.1 Hz warble, presence shelf, soft saturation; SAPI Zira with SSML `+16%` pitch / `+6%` rate | Done. |
| V-3 | Speech degrades gracefully and never blocks vision/DRC | Piper → Kokoro → SAPI → silent transcript; voice work stays on the cognitive loop | Done. |
| V-4 | Technical text is spoken as words, not symbols | `SpeechTextNormalizer` expands VCC/GND/units, keeps `V C C` stable across normalization passes, and avoids the `hello,.` terminator bug | Done. |
| V-5 | The operator chooses the speech engine inside the app | VIEW → VOICE ENGINE (`AUTO`, `BMO PIPER`, `KOKORO`, `SAPI`) and **TEST VOICE**, persisted to `voice-settings.json`, overridable with `BREADBOARD_VOICE_BACKEND` | Done. |
| V-6 | The BMO Piper voice from the reference project is supported without conversion | `PiperTtsEngine` runs `piper.exe --model bmo.onnx` with the voice's own `.onnx.json`; a missing executable/model/JSON falls back and reports the exact path | Done; audition requires the user-supplied model. |

### 3.1 Acceptance criteria

1. `dotnet build BreadboardAOI.sln --configuration Release --no-restore`
   reports **0 warnings / 0 errors**.
2. `dotnet test BreadboardAOI.sln --configuration Release --no-build` reports
   **236 passed / 0 failed**, including `BmoVoiceTests` and `PiperTtsTests`.
3. `BREADBOARD_START_VIEW=1 BREADBOARD_START_LAYOUT=builder` opens the VIEW
   panel with the BUILDER preset, the right dock hidden and the left dock at
   320 px.
4. A preset or width change is present in
   `%LOCALAPPDATA%\BreadboardAOI\workspace-layout.json` after the next launch.
5. With Kokoro absent, the SAPI path still speaks the three tutor sentences
   through Zira (or the brightest installed voice) without throwing.
6. With Piper absent, **BMO PIPER** falls back to Kokoro or SAPI, the dock keeps
   the active engine name and the missing `bmo.onnx` / `piper.exe` path visible,
   and **TEST VOICE** still speaks.
7. `BREADBOARD_VOICE_BACKEND=piper` at boot selects the Piper order first; the
   saved `voice-settings.json` is used only when that variable is unset.

---

## 4. Non-negotiable project constraints

1. **Dual loop.** The 60 FPS vision/DRC loop never awaits the tutor, the TTS or
   the network. Cross-loop traffic uses `System.Threading.Channels`.
2. **Zero-allocation hot path.** No per-frame `new List<>`, LINQ or boxing inside
   `Camera -> Homography -> YOLO -> PinMatrix -> DRC -> SkiaSharp`.
3. **Locked packages.** Only the NuGet versions in `AGENTS.md` §3.
4. **Local by default.** YOLO, homography, DRC, rendering and voice stay local;
   only the optional tutor endpoint may be remote, and private/link-local hosts
   are refused.
5. **No silent downloads.** Assets are bundled or supplied by the operator.
6. **Release discipline.** Build and test locally, report the results, and wait
   for explicit approval before `git push` or a release.

---

## 5. Backlog (not yet scheduled)

| Item | Why it is not done now |
| :--- | :--- |
| Netlist export to KiCad | Removed with the schematic subsystem; the DRC netlist stays in-app. |
| Full A* routing for a future exporter | Needs a new consumer before it is re-added; the router went with the schematic engine. |
| Additional embedded footprints (transistor, regulator) | The bundled 22,910-symbol library already covers them through the symbol picker. |
| Interactive drag of 2D parts on the canvas | The builder currently places by two clicks plus a typed editor; direct drag is future work. |
