# Demo sandbox — porting brief for the .NET 10 test fixture

Date: 2026-11-12. Scope: everything needed to rebuild the prototype "GAPO Sandbox" game as a permanent, controllable test fixture (spec III.4 step 0, III.5 "Fixture game tests").

Sources read in full: `demo/src/Sandbox/{Program,Game,GameForm,Renderer,Simulation,World}.cs`, `demo/src/Sandbox/GAPO.Sandbox.csproj`, `demo/src/Shared/{Telemetry,SandboxSettings,Native}.cs`. Read for the client side of the contract: `demo/src/Optimiser/Game/GameLink.cs`, `Optimiser/Engine/{KnowledgeBase,FrameStats}.cs`, parts of `OptimisationEngine.cs` and `Diagnosis.cs`, `.github/workflows/windows-demo.yml`, `demo/build.sh`, spec Parts I, III, Appendices A–C.

Verification status is stated per item. "Verified" means read directly from source or measured; section 20 lists everything not verified.

---

## 1. What the sandbox is

A single-process C# WinForms program (`GAPO-Sandbox.exe`, .NET Framework 4.6.2, `WinExe`, AnyCPU non-32-bit-preferred, unsafe code, no NuGet runtime dependencies). It renders a 2048×2048 wrap-around height-field island in software on the CPU ("voxel space" ray-march per screen column, then deferred-style full-screen passes), simulates flocking creatures with all-pairs AI and physics on the main thread, and presents with plain GDI. It deliberately ships wasteful defaults, calibrates its workload to the PC so the defaults run at roughly 33–38 FPS, hot-reloads an INI settings file, and publishes one telemetry record per frame through a named shared-memory map.

| File | Role |
|---|---|
| `Shared/Telemetry.cs` | `FrameFlags`, `FrameRecord`, `SandboxState`, `SandboxCommand`, `TelemetryChannel` (map, header, ring, seqlock, commands, toasts) |
| `Shared/SandboxSettings.cs` | Settings model, INI read/write, atomic save, tolerant load, `DemoPaths` |
| `Shared/Native.cs` | P/Invoke: DPI, `timeBeginPeriod`, `DwmFlush`, DWM title bar, `PeekMessage`, GDI |
| `Sandbox/Program.cs` | Command line, windowed vs headless entry |
| `Sandbox/Game.cs` | Init, calibration, settings reload, the frame function, camera, autosave |
| `Sandbox/GameForm.cs` | Window, idle loop, GDI present, HUD, input, auto-test finish |
| `Sandbox/Renderer.cs` | Software renderer, 7 timed passes, thread dispatch |
| `Sandbox/Simulation.cs` | Creatures (O(n²) AI + physics), particles, `CameraPath` |
| `Sandbox/World.cs` | Height field, colour chunks (streamed warm-up), baked shadows, texture pack |

Project facts (`GAPO.Sandbox.csproj`): `TargetFramework net462`, `OutputType WinExe`, `AllowUnsafeBlocks true`, `Optimize true`, `DebugType none`, `AssemblyName GAPO-Sandbox`, `RootNamespace Gapo.Sandbox`, version `1.0.0`, references `System.Windows.Forms` and `System.Drawing`, compiles `..\Shared\*.cs` by link, icon `..\..\assets\sandbox.ico`. Shared namespace is `Gapo.Shared`.

---

## 2. Files and paths

`DemoPaths.DataDir` = `%LOCALAPPDATA%\GAPO-Demo` (falls back to `Path.GetTempPath()` when LocalApplicationData is empty).

| File | Path | Written by |
|---|---|---|
| Settings | `<DataDir>\sandbox-settings.ini` | game (creates defaults, F5 reset), optimiser |
| Calibration | `<DataDir>\calibration.ini` | game |
| Autosave | `<DataDir>\world.sav` | game |
| Reports | `<DataDir>\Reports\` | optimiser |

The paths are hard-coded; there is no override. The fixture needs a `--data-dir` switch so tests are hermetic (new).

---

## 3. Shared-memory telemetry contract

### 3.1 Map

| Item | Value |
|---|---|
| Map name (Windows) | `GAPO_Sandbox_Telemetry_v1` — no `Global\`/`Local\` prefix, so it lives in the creating session's namespace; pagefile-backed |
| Created with | `MemoryMappedFile.CreateOrOpen(name, TotalSize, ReadWrite)` by the game; reader uses `MemoryMappedFile.OpenExisting(name, MemoryMappedFileRights.ReadWrite)` (read-write because the reader writes commands and toasts into the header) |
| Non-Windows fallback | file `<temp>/gapo-telemetry-v1.bin`, `FileShare.ReadWrite | Delete`, mapped whole (development only) |
| `Magic` | `0x4F504147` (bytes `47 41 50 4F` = "GAPO", little-endian int32) |
| `Version` | `1` |
| `HeaderSize` | 4096 bytes |
| `Capacity` | 4096 records |
| `RecordSize` | **120 bytes** (`Marshal.SizeOf(FrameRecord)`) |
| `TotalSize` | 4096 + 4096 × 120 = **495,616 bytes** |

Verified: I compiled a copy of the struct under the .NET Framework CLR and `Marshal.SizeOf` returned 120 with the offsets in 3.3. **The spec (Appendix A) says "4,096 × 128 B records"; that is wrong — the shipped layout is 120 B.**

All integers little-endian. Strings are UTF-16LE code units, NUL-terminated, truncated to `maxChars − 1`.

Writer start-up (`CreateForWriter` → `Clear`): zero the 4096 header bytes (the ring is **not** cleared), write `Version`, then `Magic` last. Readers reject the map unless both match. On writer `Dispose`: `State = Exiting`, then release the view and handle; the map disappears when the last handle closes.

### 3.2 Header layout (4096 bytes)

| Offset | Size | Type | Field | Written by | Meaning |
|---:|---:|---|---|---|---|
| 0 | 4 | int32 | Magic | game | `0x4F504147` |
| 4 | 4 | int32 | Version | game | 1 |
| 8 | 4 | int32 | Pid | game | game process id |
| 12 | 4 | int32 | State | game | `SandboxState` |
| 16 | 8 | int64 | FramesWritten | game | last frame index + 1 |
| 24 | 8 | int64 | HeartbeatTicks | game | `DateTime.UtcNow.Ticks` at each frame publish |
| 32 | 4 | int32 | AppliedRevision | game | settings revision now in effect |
| 36 | 4 | int32 | InternalWidth | — | declared, **never written** (0) |
| 40 | 4 | int32 | InternalHeight | — | never written |
| 44 | 4 | int32 | ClientWidth | — | never written |
| 48 | 4 | int32 | ClientHeight | — | never written |
| 52 | 4 | int32 | EntityCount | game | creatures in the world (`Sim.Total`; updated when a herd is spawned) |
| 56 | 4 | int32 | CameraMode | game | 0 Manual, 1 Autopilot, 2 Benchmark (written after every frame) |
| 60 | 4 | int32 | ContentLevel | game | calibrated base width `BaseW` |
| 64 | 4 | float32 | CalibratedUltraMs | game | calibration estimate of Ultra frame cost |
| 68 | 4 | int32 | CmdSeq | optimiser | incremented per command |
| 72 | 4 | int32 | CmdId | optimiser | `SandboxCommand` |
| 76 | 4 | int32 | CmdArg | optimiser | argument |
| 80 | 4 | int32 | CmdAck | game | last `CmdSeq` taken |
| 84 | 4 | int32 | ToastSeq | optimiser | incremented per toast |
| 88 | 8 | — | unused | | |
| 96 | 512 | char[256] | SettingsPath | game | full path of the INI |
| 608 | 256 | char[128] | Toast | optimiser | text to show in the game |
| 864 | 128 | char[64] | GameTitle | game | `"GAPO Sandbox"` |
| 992 | 3104 | — | unused (zero) | | |

All scalar accesses use `Volatile.Read/Write` on raw pointers.

### 3.3 Frame record (120 bytes, `[StructLayout(LayoutKind.Sequential, Pack = 4)]`)

| Offset | Type | Field | Meaning |
|---:|---|---|---|
| 0 | int64 | FrameIndex | 0-based, monotonic since process start; doubles as the seqlock word |
| 8 | float64 | TimeMs | game clock (Stopwatch since `Game` construction) at frame end |
| 16 | float32 | FrameMs | frame end − previous frame end (what the player feels) |
| 20 | float32 | WorkMs | busy time: frame start → after present; excludes cap/VSync wait |
| 24 | float32 | SimMs | SimEntitiesMs + SimPhysicsMs + SimParticlesMs |
| 28 | float32 | SimEntitiesMs | active-set selection + flocking AI + integration |
| 32 | float32 | SimPhysicsMs | pairwise collision × substeps |
| 36 | float32 | SimParticlesMs | particle update |
| 40 | float32 | RenderMs | sum of the seven pass times below |
| 44 | float32 | TerrainMs | terrain column ray-march |
| 48 | float32 | ShadowMs | shadow pass |
| 52 | float32 | WaterMs | water pass |
| 56 | float32 | SkyMs | sky + clouds |
| 60 | float32 | FogMs | fog pass |
| 64 | float32 | SpritesMs | sprite/particle projection (single thread) + rasterising |
| 68 | float32 | PostMs | anti-aliasing resolve **plus** per-frame set-up before the terrain pass |
| 72 | float32 | PresentMs | time inside the present callback (0 headless or minimised) |
| 76 | float32 | IoMs | chunk-streaming bake time + autosave time in this frame |
| 80 | float32 | WaitMs | frame-cap / VSync wait |
| 84 | int32 | Revision | settings revision this frame ran with |
| 88 | int32 | Flags | `FrameFlags` bit set |
| 92 | int32 | EntitiesSimulated | creatures inside the simulation radius this frame |
| 96 | int32 | EntitiesTotal | creatures considered = round(Total × EntityDensity %) |
| 100 | int32 | ParticlesActive | particle count for the current quality |
| 104 | int32 | RenderThreads | renderer thread setting in effect |
| 108 | int32 | RenderWidth | output width (after AA resolve, not the 2× SSAA target) |
| 112 | int32 | RenderHeight | output height |
| 116 | int32 | Reserved | 0 |

`WorkMs − SimMs − RenderMs − PresentMs − IoMs` is untimed "other": settings-file stat, command poll, camera, `CollectSprites`, and the main-thread baked-shadow refresh when `Shadows=High` (24 rows per frame).

### 3.4 Enums

`FrameFlags`: `Warmup=1`, `Autosave=2`, `GarbageCollect=4`, `SettingsReload=8`, `ManualCamera=16`, `BenchmarkCamera=32`, `TerrainEdit=64`, `Calibrating=128` (declared, **never set**).

`SandboxState`: `Starting=0`, `Calibrating=1`, `Warmup=2`, `Running=3`, `Exiting=4`. The channel is created only after calibration, so 0 and 1 are never observable in practice; the game sets `Warmup` at init, `Running` when the last chunk is baked, `Exiting` on dispose.

`SandboxCommand`: `None=0`, `BenchmarkCamera=1`, `RestartBenchmarkPath=2`.

Game-side `CameraMode`: `Manual=0`, `Autopilot=1`, `Benchmark=2`.

### 3.5 Seqlock protocol

Slot address = `base + 4096 + (index mod 4096) × 120`.

Writer (`WriteFrame`), single writer only:
1. Volatile-write `-1` into the slot's first 8 bytes.
2. Copy the whole 120-byte record with `FrameIndex = -1`.
3. `Thread.MemoryBarrier()`.
4. Volatile-write the real index into the first 8 bytes.
5. `FramesWritten = index + 1`.
6. `HeartbeatTicks = DateTime.UtcNow.Ticks`.

Reader (`ReadNewFrames(ref nextIndex, buffer)`):
1. `written = FramesWritten`.
2. If `nextIndex > written` → `nextIndex = written` (the game restarted).
3. If `written − nextIndex > 4080` (`Capacity − 16`) → `nextIndex = written − 4080` (lapped; skip).
4. While `nextIndex < written` and the buffer has room: read the slot's first 8 bytes (`before`), copy 120 bytes, memory barrier, read the first 8 bytes again (`after`). Accept only if `before == nextIndex && after == nextIndex`, then set `FrameIndex = nextIndex` in the copy. Advance `nextIndex` either way — a torn or overwritten record is dropped, not retried.

Reader cadence in the demo (`GameLink`): 100 ms while connected, 1000 ms while looking for the map; first read starts at `max(0, FramesWritten − 600)`; client buffer 4096 records; client store capped at 24,000 frames (trimmed to 22,000). Connect only if `Pid` is alive and `State != Exiting`. Disconnect when no new frame for > 4 s **and** (process gone or `State == Exiting`).

### 3.6 Commands (optimiser → game)

Send: write `CmdId`, write `CmdArg`, then `CmdSeq = CmdSeq + 1` (plain read-increment-write, single sender assumed). Game: at the start of every frame, if `CmdSeq != lastSeenSeq`, read id and arg, remember the seq, write `CmdAck = seq`. One pending command at a time: two sends inside one game frame lose the first. The demo client waits for `CmdAck == CmdSeq`, polling every 15 ms, timeout 2000 ms (800 ms on the cancel path).

| Command | Arg | Effect in the game |
|---|---|---|
| `BenchmarkCamera` (1) | ≠ 0 | `Mode = Benchmark`, benchmark clock `_benchT = 0` |
| `BenchmarkCamera` (1) | 0 | `Mode = Autopilot`, autopilot clock `_autoT = 18 + _benchT` |
| `RestartBenchmarkPath` (2) | ignored | `_benchT = 0` (does not change mode) |

### 3.7 Toasts (optimiser → game)

Write the text at offset 608 (≤ 127 chars), then `ToastSeq + 1`. The game checks once per frame and shows the text for 3200 ms. Toasts used by the demo optimiser: "GAPO is measuring — benchmark flight", "GAPO testing: <change>", the final result line, "GAPO reverted every change". The game's own toasts: start-up toast (6500 ms), "Settings reloaded (<ModifiedBy>)" (suppressed when `ModifiedBy` contains "GAPO", case-insensitive), "Settings reset to the unoptimised defaults", "Autopilot resumed", herd spawned, calibration cleared.

---

## 4. Settings INI

### 4.1 Format

UTF-8 without BOM, `Environment.NewLine` line ends. Default file as written by the game (padding before `;` is cosmetic):

```ini
; GAPO Sandbox settings. The game reloads this file automatically while it runs.
; The GAPO Optimiser edits it one setting at a time and reverts anything that does not help.

[Graphics]
ResolutionScale=100        ; 50-100 (% of base resolution)
AntiAliasing=SSAA4x           ; Off, FXAA, SSAA4x
RenderDistance=1400        ; 400-1600
TerrainLOD=Off                ; On, Off
Shadows=Ultra                ; Off, High, Ultra
WaterReflections=Ultra       ; Off, Simple, Ultra
Clouds=Ultra                 ; Off, Low, Ultra
Fog=Volumetric               ; Off, Simple, Volumetric
Particles=Ultra              ; Off, Low, Medium, High, Ultra
Textures=Ultra               ; Low, High, Ultra

[Simulation]
SimulationDistance=Unlimited  ; Near, Far, Unlimited
EntityDensity=100          ; 25-100 (%)
PhysicsSubsteps=8          ; 1-8

[Performance]
RenderThreads=1            ; 1-64
FrameRateCap=0             ; 0 = unlimited
VSync=Off

[System]
AutosaveSeconds=10         ; 0 = off

[Meta]
Revision=0
ModifiedBy=Game defaults (unoptimised)
```

Parser (`FromIni`): split on `\n`, trim; skip empty lines and lines starting with `;` or `[` (sections are ignored — one flat key space); key = text before the first `=`, trimmed; value = text after it, cut at the first `;` (except for `ModifiedBy`), trimmed. Keys are case-insensitive; later duplicates win; unknown keys are ignored. A missing or unparsable value becomes the **Ultra default**, not the previous value. Integers: invariant culture. Booleans: `on/true/1/yes`, `off/false/0/no`. Enums: case-insensitive `Enum.TryParse` (which also accepts numeric strings, including out-of-range numbers).

Clamps applied on load differ from the comments in the file: `RenderDistance` 300–1600 (comment says 400), `EntityDensity` 10–100 (comment says 25).

### 4.2 Every setting

Stage names are the `FrameRecord` fields. "Ladder" is what the demo optimiser's knowledge base tests (one step at a time) with its visual-loss score 0–10 (`Optimiser/Engine/KnowledgeBase.cs`).

| Key | Type / range (load clamp) | Levels | Ultra default | Cost it creates | Stage | Demo optimiser ladder (visual loss) |
|---|---|---|---|---|---|---|
| `ResolutionScale` | int 50–100 | — | 100 | Output size = `max(64,(BaseW×s/100)&~1)` × `max(36,(BaseH×s/100)&~1)`; all pixel passes ∝ s² | all render passes, Present | >85→85 (3), else→70 (5); only while >70 |
| `AntiAliasing` | enum | Off, FXAA, SSAA4x | SSAA4x | SSAA4x: internal target 2×W × 2×H, so **every** pass runs on 4× pixels, plus a 2×2 box resolve. FXAA: one 5-tap luma-edge pass at output size (edge threshold: luma range ≥ 24). Off: no pass, output buffer aliases the colour buffer | all render passes; resolve in PostMs | SSAA4x→FXAA (2), FXAA→Off (2) |
| `RenderDistance` | int 300–1600 | — | 1400 | Terrain march length per column (steps ≈ distance ÷ step). Also fog density `k = 3.0/dist`, sprite cull at `0.8×dist` | TerrainMs | >1150→1100 (2), >900→850 (3), else→650 (4); only while >700 |
| `TerrainLOD` | bool | On, Off | Off | Off: constant step `1/Detail`. On: step starts `0.55/Detail` and grows by `0.0105` per step (≈ 467 steps instead of ≈ 1399 at distance 1400, Detail 1) | TerrainMs | Off→On (1) |
| `Shadows` | enum | Off, High, Ultra | Ultra | Ultra: per land pixel, ray-march toward the sun (+x): `steps = max(6, (int)(22×Detail))`, step length `66/steps` cells, early exit when fully shadowed; recomputed every frame. High: one byte lookup in a baked shadow map, plus an untimed main-thread refresh of 24 map rows per frame. Off: pass skipped | ShadowMs (High refresh lands in "other") | Ultra→High (1), High→Off (5) |
| `WaterReflections` | enum | Off, Simple, Ultra | Ultra | Ultra: per water pixel, 2 trig calls + up to `max(8,(int)(24×Detail))` march steps (`kk = k×24/steps`, distance `3.5kk + 0.2kk²`). Simple: mirrored sky-row colour with a Fresnel mix. Off: skipped | WaterMs | Ultra→Simple (2), Simple→Off (3) |
| `Clouds` | enum | Off, Low, Ultra | Ultra | Ultra: per sky pixel, value-noise fBm with 5 octaves (4 when Detail < 0.8), 4 hashes per octave. Low: one bilinear sample of a pre-baked 256×256 texture. Off: gradient only | SkyMs | Ultra→Low (1), Low→Off (3) |
| `Fog` | enum | Off, Simple, Volumetric | Volumetric | Volumetric: every pixel (sky included): bilinear noise sample + 4-step height-fog march via a 1024-entry exp table. Simple: `tau = depth×k`, one table lookup. Off: skipped | FogMs | Volumetric→Simple (1), Simple→Off (4) |
| `Particles` | enum | Off, Low, Medium, High, Ultra | Ultra | Count 0 / 700 / 2000 / 4500 / 9000. Update: 2 trig calls per particle on the main thread. Draw: projection on one thread, then a plot loop | SimParticlesMs, SpritesMs | one level down (1, 1, 2, 3) |
| `Textures` | enum | Low, High, Ultra | Ultra | "Texture pack" `int[size²]`, size 256 / 1024 / 2048 → ≈ 0.26 / 4 / 16 MB. No frame-time effect by design; regenerating it on a change is a one-off hitch in the reload frame | memory only | Ultra→High only under memory pressure (2) |
| `SimulationDistance` | enum | Near, Far, Unlimited | Unlimited | Only creatures within 230 / 560 / ∞ world units of the camera run AI and physics; cost ∝ (active count)². Inactive creatures freeze but are still drawn | SimEntitiesMs, SimPhysicsMs | Unlimited→Far (1), Far→Near (2) |
| `EntityDensity` | int 10–100 (%) | — | 100 | Considered creatures = `round(Total × d/100)` (the first N in the array); the rest are neither simulated nor drawn | Sim (quadratic), SpritesMs (linear) | >75→75 (3), else→50 (4); only while >50 |
| `PhysicsSubsteps` | int 1–8 | — | 8 | Full pairwise collision pass repeated N times | SimPhysicsMs (linear) | >4→4 (0), then halve (1, 2) |
| `RenderThreads` | int 1–64 | — | 1 | Degree of parallelism of every render pass; 1 = all rendering on the main thread | all render passes (wall clock) | →physical cores (max 16); SMT test →logical (0) |
| `FrameRateCap` | int 0–1000 | — | 0 (unlimited) | Sleep/spin wait after the work | WaitMs | applied last, at the display refresh |
| `VSync` | bool | On, Off | Off | Only when `FrameRateCap == 0`: `DwmFlush()` blocks to the next composition | WaitMs | not touched |
| `AutosaveSeconds` | int 0–3600 | — | 10 (0 = off) | Blocking save on the main thread every N s (section 9) | IoMs, flag `Autosave` | 10 s → 5 min, done first (0) |
| `Revision` | int (no clamp) | — | 0 | Acknowledgement counter | — | — |
| `ModifiedBy` | string | — | `Game defaults (unoptimised)` | Drives the HUD badge and the reload toast | — | — |

`IsUnoptimisedDefaults()` compares the INI body with the defaults, ignoring `Revision` and `ModifiedBy`. The HUD badge is `UNOPTIMISED` / `OPTIMISED BY GAPO` (ModifiedBy contains "GAPO") / `CUSTOM SETTINGS`.

### 4.3 Save and load

`Save(path)`: create the directory; write `path + ".tmp"`; then `File.Delete(path)` + `File.Move(tmp, path)`, retrying on `IOException`/`UnauthorizedAccessException` up to 21 more times with 15 ms sleeps. This is **not atomic** — the file briefly does not exist. The game tolerates that (see 4.4). The port should use `File.Move(tmp, path, overwrite: true)`.

`Load(path)`: open with `FileShare.ReadWrite | Delete`, read all, parse. Returns `null` when the file or directory is missing; retries other I/O errors up to 21 times at 10 ms.

### 4.4 Hot reload and revision acknowledgement

Game side, at the start of a frame, at most every **200 ms**:
1. `stamp = File.GetLastWriteTimeUtc(path)`. Unchanged → nothing.
2. `Load`. `null` → keep the old stamp, try again in 200 ms.
3. Store the stamp and `ApplySettings`: swap the settings object; `Renderer.Threads`; regenerate the texture pack if its size changed; resize render buffers (reallocated when output size or AA mode changed); `AppliedRevision = Revision` in the header; if `AutosaveSeconds` changed, next autosave = now + `max(1, seconds)` s; toast unless written by GAPO.
4. The frame carries flag `SettingsReload` and already runs, and reports, the new `Revision`. It also contains any reallocation cost.

The trigger is the file's modification time. The game does not check that `Revision` increased.

Client side (demo optimiser), which the fixture must keep working with:
1. `rev = max(revision in file, AppliedRevision) + 1`; set `ModifiedBy = "GAPO Optimiser (<note>)"`; save.
2. Poll `AppliedRevision == rev` every 30 ms, timeout 4000 ms (3000 ms on revert). Timeout = "The game did not apply the new settings."
3. Send `RestartBenchmarkPath`; wait for the ack.
4. Discard frames until `TimeMs ≥ latest known TimeMs + 650` (`DiscardMs`).
5. Collect until the span is ≥ 2600 ms (`WindowMs`) and ≥ 45 frames (`MinFrames`); give up after 7000 + 650 ms. Keep only frames whose `Revision` equals the expected one and that lack the `Warmup` flag. Fewer than 8 frames is an error.

Other writers: F5 writes the defaults with `Revision + 1`, `ModifiedBy = "Player (F5 reset)"`. `--reset` deletes the file before start, after which the game writes defaults with `Revision = 0`.

---

## 5. Start-up sequence and calibration

`Game.Init(progress)`:
1. progress "Generating world…" → `new World(20260930)` (generate heights, bake all shadow rows at 34° sun, mark all 1024 colour chunks not ready, Ultra texture pack) → `new Renderer(world)` (bakes 256² cloud and fog-noise textures, exp table) → `new Simulation(world)`.
2. progress "Calibrating the demo to your PC…" → `LoadCalibration()` or, if that fails, `Calibrate()`.
3. `Sim.Populate(EntityBase)`; `Renderer.Detail = Detail`.
4. Load settings (create the file with defaults when missing); apply.
5. Create the telemetry channel; write `Pid`, `GameTitle`, `SettingsPath`, `ContentLevel = BaseW`, `CalibratedUltraMs`, `EntityCount`, `AppliedRevision`, `State = Warmup`.
6. Camera at path time 0; queue chunks nearest-first around the camera; first autosave at now + `max(1, AutosaveSeconds)` s.

The optimiser cannot see the game until step 5, i.e. after world generation and calibration.

### 5.1 What is calibrated

Not the world (always 2048²). Three workload numbers: base render width `BaseW` (height `BaseH = (BaseW×9/16) & ~1`), creature count `EntityBase`, and ray-march `Detail` (0.45–1.0). Targets: `RenderTargetMs = 20.0`, `SimTargetMs = 8.5`, so Ultra ≈ 28.5 ms of render + simulation ≈ 30 ms per frame with present and overhead (33–38 FPS). (A source comment says "~17 ms"; the constant is 20.0.)

### 5.2 Algorithm (`Calibrate`)

1. `BakeAllChunks()` so warm-up is not measured.
2. Renderer: Ultra defaults, 1 thread. Camera at path time `BenchmarkStart + 1 = 19 s`.
3. **Render probe**: resize to 320×180 output (640×360 internal with SSAA). Render 7 frames with no sprites or particles; drop the first 2; `renderProbe` = median of the remaining 5.
4. `w = 320 × (20.0 / max(0.5, renderProbe))^(1/1.9)` (cost assumed ∝ width^1.9). `BaseW = clamp(floor(w/32)×32, 224, 800)`. `Detail = 1`.
5. **Verify, up to 3 passes**: set Detail, resize to `BaseW×BaseH`, render 5 frames, drop the first, take element `[2]` of the sorted 4 → `m`; `renderProbe = m`. Stop if `m ≤ 24.0` (target × 1.2) or `Detail ≤ 0.45`. Otherwise `Detail = max(0.45, Detail × (20.0/m) × 1.05)` and repeat. Only slowness is corrected; a too-fast result is left alone.
6. **Simulation probe**: a separate `Simulation`, 300 creatures, 6 updates at `dt = 0.016` with Ultra settings; drop the first; sample = `EntitiesMs + PhysicsMs`; `simProbe` = median of 5.
7. `EntityBase = clamp((int)(300 × sqrt(8.5 / max(0.2, simProbe))), 80, 1400)`.
8. `CalibratedMs = renderProbe + simProbe × (EntityBase/300)²`.
9. Save the calibration; mark all chunks not ready again so the real game starts with the streamed warm-up.

### 5.3 Calibration file

```
; Delete this file (or press F6 in the game) to re-calibrate the demo's workload to this PC.
Version=5
Machine=<Environment.MachineName>/<Environment.ProcessorCount>
BaseWidth=<int>
Entities=<int>
UltraMs=<0.0>
Detail=<0.00>
```

CRLF. Reused only when `Version` is exactly `5` and `Machine` matches; otherwise, or on any parse error, the game recalibrates. On load: `BaseWidth` clamp 224–800, `Entities` clamp **120**–1400 (calibration itself allows 80), `Detail` clamp 0.45–1. Deleted by F6 or `--recalibrate`; takes effect at the next start.

Limits: the workload cannot grow past 800 px width, Detail 1.0 and 1400 creatures, so a fast PC (or a faster runtime) gets a lighter-than-intended "Ultra". Calibration also makes the workload different on every machine and runtime, which is wrong for regression tests that compare numbers across runs — the fixture needs a fixed-workload mode (section 16).

---

## 6. World, warm-up and streaming

| Constant | Value |
|---|---|
| `World.Size` | 2048 (`Shift 11`, `Mask 2047`), wraps in both axes |
| `Sea` | 62.0 |
| Chunk | 64×64 cells (`ChunkShift 6`), 32×32 = 1024 chunks |
| Height range | 18–268 (`18 + n^1.35 × 250`) |
| World seed | 20260930 (`System.Random`) |
| Arrays | `Height float[4,194,304]` 16 MB; `Color int[]` 16 MB; `ShadowBaked byte[]` 4 MB; texture pack up to 16 MB |

Generation: tileable diamond-square (amplitude ×0.62 per level while step > 64, then ×0.5), two 5-tap smoothing passes, normalise, power 1.35. Colours per cell (`ComputeColor`): Lambert light, cavity term, value noise, biome palette — about 9 height lookups, a 4-hash noise and two square roots per cell. Exact palette is cosmetic; copy it from `World.cs` if the look matters.

**Warm-up**: colours are baked chunk by chunk after start-up. While `ChunksRemaining > 0`, every frame has flag `Warmup`; whenever `frameStart ≥ nextBake`, the game bakes **40 chunks** (163,840 cells) on the main thread and sets `nextBake = frameStart + 110 ms`. That gives 26 bursts over about 2.8–3 s, one spike roughly every 110 ms, with the burst time in `IoMs`. Unbaked chunks render in a grey placeholder (`g = 70 + (int)(h×0.35)`). When the count reaches 0 the header `State` becomes `Running`. The demo optimiser treats the game as warm only when `State == Running`, it has ≥ 30 recent frames and none of the last 90 has `Warmup`; it polls every 250 ms and gives up waiting after 45 s.

Warm-up happens once per process, always, with fixed size. There is no switch to disable, lengthen or re-trigger it.

Sun: elevation `34 + 10 × sin(2π × t / 240 s)` degrees from the game clock, so Ultra shadow cost drifts slowly with wall time. Baked shadows (`Shadows=High`) refresh 24 rows per frame (a full sweep every ≈ 85 frames).

---

## 7. Camera and the deterministic benchmark flight

`CameraPath`: `LoopSeconds = 150`, `BenchmarkStart = 18`.

```
w   = 2π / 150
r   = 640 + 170 × sin(3wt)
θ   = wt
x   = 1024 + r cos θ          y = 1024 + r sin θ
r'  = 170 × 3w × cos(3wt)
dx  = r' cos θ − r w sin θ    dy = r' sin θ + r w cos θ
yaw = atan2(dy, dx) + 0.28 × sin(0.11 t)
```

Height at `t`: sample the surface (`max(height, Sea)`) at `t + 0.45k` for k = −4…10 with weights `1/(1 + 0.25|k|)`; result = `max(weighted mean + 44, max over k = 0…3 + 22)`.

Modes:
- **Autopilot** (default): `_autoT += dt`; follows `t = _autoT mod 150` with smoothing (position gain `min(1, 1.5dt)`, snap within 2 units; height gain `min(1, 2.5dt)`).
- **Benchmark**: `_benchT += dt`; `t = 18 + (_benchT mod 12.0)`; camera set exactly, no smoothing. A fixed **12 s segment (path time 18 → 30) that loops**. Player input does not leave this mode.
- **Manual**: any movement input while in autopilot. Speed 70 units/s (150 with Shift), turn 1.6 rad/s, climb 60 units/s, height clamped to ground + 10 … 420. Returns to autopilot after 12,000 ms without input.

`dt` is the real time between frame starts, clamped to 0.001–0.05 s (first frame 0.016).

**How a window restart is commanded**: the optimiser sends `BenchmarkCamera` arg 1 once at the start of a run, then `RestartBenchmarkPath` before **every** measurement window, waits for `CmdAck`, discards 650 ms and measures ≥ 2600 ms. Each window therefore covers path time ≈ 18.65 → 21.25 s or a little more. The first herd is always placed at path time 20.2, so it is in view. At the end (or on cancel) it sends `BenchmarkCamera` arg 0.

What "deterministic" covers: the camera position as a function of benchmark time. Not deterministic: creature positions (integrated with real `dt`, and the shared `Random(1234)` also feeds particle respawns), particle state, the sun angle, and path progress when a frame exceeds 50 ms (the `dt` clamp makes the flight fall behind real time).

---

## 8. Frame loop and stage timing

Windowed loop (`GameForm.OnIdle`, hooked to `Application.Idle` after init): `while (ready && !PeekMessage(out msg, 0, 0, 0, PM_NOREMOVE)) Frame(input, Present)`. Everything runs on the UI thread; input is handled between frames. A modal move/size loop (dragging the window) stops frames entirely.

`Game.Frame(input, present)` in order:

1. `frameStart = Now`. Settings-file check if 200 ms have passed.
2. Take a pending command; take a pending toast.
3. Compute `dt`; update the camera; apply terrain edit (flag `TerrainEdit`); spawn a herd on request (25 creatures, 90 units ahead).
4. `Sim.Update` → `SimEntitiesMs`, `SimPhysicsMs`, `SimParticlesMs`.
5. Streaming bake if chunks remain (flag `Warmup`, time into `IoMs`).
6. Update the sun; if `Shadows == High`, refresh 24 baked rows (untimed).
7. Autosave if due (flag `Autosave`, time into `IoMs`).
8. Collect sprites; `Renderer.Render` → 7 pass times.
9. `present()` → `PresentMs`.
10. `WorkMs = Now − frameStart`.
11. Frame cap or VSync wait → `WaitMs`.
12. Flags: `GarbageCollect` if `GC.CollectionCount(2)` changed since the last frame; `ManualCamera` / `BenchmarkCamera`.
13. `frameEnd = Now`; `FrameMs = frameEnd − lastFrameEnd`; publish the record; write header `CameraMode`; HUD history (240 frames; FPS and 1 % low recomputed every 500 ms — 1 % low = mean of the worst `max(1, n/100)` frames).

`Renderer.Render` order and timing: set-up (per-column ray directions, sky gradient) → Terrain (parallel over columns) → Sky (rows) → Shadow (rows) → Water (rows) → Fog (rows) → sprite projection (serial) + sprite rows → AA resolve (rows). `PostMs` = resolve time + set-up time. Camera: horizontal FOV 90° (`TanHalfFov = 1`), `scale = RW/2`, horizon at `0.36 × RH`.

Present (`GameForm.Present`): `GetDC`; 32-bit top-down DIB-section back buffer at client size; clear to black when letterboxed; `StretchDIBits` (COLORONCOLOR) of the renderer output into an aspect-preserving rectangle; draw the HUD into the back buffer; `BitBlt` to the window. Returns 0 without drawing when minimised or the client area is under 16 px. Initial client width = `min(1280 × dpiScale, 0.86 × work-area width)`, 16:9, limited to 0.84 × work-area height; minimum window 480×300. Present cost therefore depends on window size, DPI and occlusion.

---

## 9. Cost model of the two heavy stages

**Renderer.** Internal target `RW×RH` = output × (2×2 with SSAA4x). Buffers per internal pixel: colour int, depth float, coord int (12 bytes); at the 800-wide maximum with SSAA that is 1600×900 → ≈ 17 MB, reallocated when size or AA mode changes.
- Terrain: per column, march `z` from 1.5 to `RenderDistance`; each step is 1 height lookup and a projection; visible spans are filled downward (y-buffer occlusion, early exit when the column is full). Cost ≈ columns × steps + filled pixels.
- Sky, shadow, water and fog are per-pixel loops over rows; sky touches sky pixels, shadow touches land pixels, water touches water pixels, fog touches all.
- Sprites: creature = ellipse body + head disc with a depth test; particle = 1–3 pixels. Each parallel chunk scans the whole sprite and particle list and filters by row range, so this pass gets relatively worse as thread count rises.

**Simulation** (`Simulation.Update`, main thread, never parallel), with `a` = active creatures:
- Active set: O(considered).
- Flocking: for each active creature, loop over **all** active creatures (a² distance tests): neighbour radius² 900, separation radius² 30; weights alignment 0.02, cohesion 0.004, separation 0.6; wander 0.05; one walkability probe (5 bilinear height samples) 7 units ahead; speed clamped 0.6–3.2.
- Physics: `PhysicsSubsteps × a(a−1)/2` pair tests; overlap when distance² < 16; push `(4 − d) × 0.25 / d / steps`.
- Particles: O(count), 2 trig calls each.

Creature placement (`Populate`): herds of 14–39 beside the camera path (first herd at path time 20.2; 30–120 units ahead, ±70 sideways, members within ±26), only on walkable ground (height between Sea + 1.5 and 196, slope² < 30), `Random(1234)`.

---

## 10. The autosave hitch

Every `AutosaveSeconds` (default 10 s; next = completion time + interval), on the main thread, inside the frame:
1. Allocate a fresh `byte[4,194,304]` and fill it with `min(255, height)` for every cell.
2. Serialise **all** creatures as text: `x,y,vx,vy\n` with round-trip float formatting, UTF-8.
3. CRC-32 (polynomial `0xEDB88320`, table-driven) of both buffers, XORed together.
4. `FileStream(world.sav, FileMode.Create, FileAccess.Write, FileShare.Read, 4096, FileOptions.WriteThrough)`; write 4-byte CRC, the 4 MB buffer, the text; `Flush(true)`.

All exceptions are swallowed. The frame gets flag `Autosave` and the elapsed time in `IoMs`. The 4 MB large-object allocation also provokes gen-2 collections, which show up as `GarbageCollect` flags. Measured by the demo: ≈ 77 ms on the CI runner; the spec quotes 60–150 ms, longer on hard disks. The demo optimiser's stutter rule counts a frame flagged `Autosave` or `Warmup` as a stutter when it is > median + 15 ms; unflagged frames when > `max(1.8 × median, median + 20 ms)`.

---

## 11. Frame cap, VSync and timers

- Clock: one `Stopwatch` started when `Game` is constructed; all times in ms as `double`.
- Windowed mode calls `timeBeginPeriod(1)` at start and `timeEndPeriod(1)` at exit. **Headless mode does not.**
- Cap (`FrameRateCap > 0`): `target = lastFrameEnd + 1000/cap`; if `target < workEnd − 50` then `target = workEnd` (no catching up after a hitch). `WaitUntil`: `while (target − Now > 1.6) Thread.Sleep(1);` then `while (Now < target) Thread.SpinWait(40);`. Because the next target is taken from the measured frame end, the achieved rate sits slightly under the cap.
- VSync (only when the cap is 0, Windows only): `DwmFlush()`, errors ignored.
- Uncapped and VSync off: the loop runs flat out, also when unfocused or minimised.

---

## 12. Threading model

- One main (UI) thread: message pump, input, settings reload, commands, simulation, streaming, autosave, render orchestration, present, telemetry publish.
- Render fan-out: `Dispatch(count, body)` runs inline when `RenderThreads ≤ 1` or `count < 8`; otherwise `chunks = min(count, threads × 4)` and `Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = threads }, …)` on the .NET thread pool, with the main thread taking part and blocking until the pass finishes. Passes run one after another.
- No dedicated render, I/O or simulation threads; no affinity or priority changes; process priority is whatever the launcher gave it.
- With `RenderThreads = 1` (default) the whole game is single-threaded — this is the "one thread at 97 % while total CPU reads ~50 %" case from the spec.
- No single-instance guard: a second sandbox opens the same map (`CreateOrOpen`), clears the header and both write into it.

---

## 13. Command line, auto-test, log, screenshot

Arguments must be exactly `--name=value` or a bare flag; unknown arguments are ignored; a malformed number throws.

| Argument | Effect |
|---|---|
| `--autotest=<seconds>` | Windowed run that ends itself once the game clock has advanced `<seconds>` past the end of init (world generation and calibration are not counted). Then: screenshot, log, close, exit code 0 |
| `--screenshot=<path>` | Auto-test: saves the GDI back buffer (window-size image **with HUD**), JPEG when the path ends in `.jpg`, else PNG. Headless: saves the raw renderer output as PNG |
| `--log=<path>` | Auto-test only: writes one line (below). Ignored in headless mode |
| `--headless=<seconds>` | No window. Runs `Game.Frame(input, null)` for N seconds, prints one line per second to stdout (the exe is `WinExe`, so stdout is only visible when redirected). Meant for testing off Windows |
| `--reset` | Deletes the settings file before start (so Ultra defaults, revision 0) |
| `--recalibrate` | Deletes the calibration file before start |

Auto-test log line (invariant culture, single `\n`):

```
fps={0:0.0} low1={1:0.0} base={W}x{H} entities={n} calibratedUltraMs={0.0} work={0.0} sim={0.0} render={0.0} terrain={0.0} shadow={0.0} water={0.0} sky={0.0} fog={0.0} sprites={0.0} post={0.0} present={0.0} rev={n} threads={n}
```

`fps` and `low1` come from the HUD statistics (last ≤ 240 frames); every stage value is from the **last single frame**, not an average. On an exception the text goes to the log path, or to `autotest-error.txt` in the working directory.

Headless per-second line: `t=…s fps=… work=… sim=… (ent … phys …) render=… [terrain … sky … shadow … water … fog … sprites … post …] io=… rev=… thr=… flags=…`, preceded by `Calibrated: base WxH, N creatures, Ultra ≈ X ms`.

CI usage in the demo workflow: `--reset --autotest=20 --screenshot=<dir>\sandbox-before.jpg --log=<dir>\sandbox-before.txt` (killed as hung after 120 s); for end-to-end, `--reset --autotest=200 …` with the optimiser started 3 s later using `--autotest=<output dir>`.

Keys: F1 help, F3 HUD, F5 reset settings, F6 clear calibration, F11 fullscreen, Esc, G spawn 25 creatures, WASD/arrows/Q/E/R/F/Space/C/Shift to fly, middle-drag to look (0.005 rad per pixel), left/right mouse to raise/dig terrain (radius 16 cells, ±3.5 per frame, heights clamped 4–300).

---

## 14. Bottleneck classes of spec III.5 — what exists, what is missing

| Class (III.5) | Can the demo sandbox create it today? | How | What is missing |
|---|---|---|---|
| Single-thread render | **Yes** | Default: `RenderThreads=1` with a 20 ms render load against 8.5 ms simulation. Diagnosed when render ≥ simulation, 1 render thread and > 1 logical CPU | Nothing essential. A fixed-workload mode for repeatable numbers |
| O(n²) simulation | **Partly** | The all-pairs AI and physics exist and are controlled by `SimulationDistance`, `EntityDensity`, `PhysicsSubsteps`. By calibration they are the *smaller* stage | A switch to make simulation the **primary** limiter on demand. Today that takes a hand-made INI that strips the render load (not measured by me), and the creature count can only be raised with the G key, 25 at a time, capped by calibration at 1400 |
| Storage hitch | **Yes** | `AutosaveSeconds` (default 10) | Size and target drive are fixed (≈ 4.2 MB into LocalAppData). Switches for save size, sync mode and path would let tests scale the hitch and aim it at a slow disk |
| Warm-up | **Partly** | Automatic, once per start, ≈ 3 s, with `Warmup` flags and `State` | Cannot be disabled, lengthened or re-triggered. Needs duration/burst switches and a command to trigger streaming mid-run |
| Memory pressure | **No** | `Textures` only moves 16 → 4 → 0.26 MB; the demo's rule needs system memory load ≥ 88 % or < 700 MB available | A memory-ballast switch (commit and touch N MB, or "leave N MB free"), tied to `Textures` so that lowering textures relieves it; optionally a per-frame touch rate so paging produces stutter |
| Background load | **No** | Nothing in the sandbox; the demo relied on whatever the machine was doing | A **separate process** load generator (threads, duty cycle, priority class, optional disk and memory activity). The demo's rule triggers at ≥ 15 % background CPU. Also a switch to start the fixture at below-normal priority (spec I.5 #12) |
| GPU-like pixel load | **Modelled only** | Per-pixel CPU passes scaled by `ResolutionScale` and `AntiAliasing` (diagnosed as "rendering workload" when render threads > 1) | No GPU is ever used: GPU utilisation, VRAM and GPU-busy time stay at zero, and presentation is GDI. Needs a real GPU path if the product must diagnose GPU-bound from PresentMon data (section 16) |

---

## 15. Porting to .NET 10 — what changes

Verified from source unless marked.

1. **Keep the contract byte-for-byte**: map name, magic, version, header offsets, 120-byte record, flags, commands. Declare the record with explicit offsets and a fixed size and add a unit test asserting `sizeof == 120` and each offset. If any field changes, bump `Version` and the `_v1` suffix together.
2. **Target**: `net10.0-windows`, x64 only. Replace `Environment.OSVersion.Platform` with `OperatingSystem.IsWindows()`, `Marshal.SizeOf(typeof(T))` with `sizeof`/`Unsafe.SizeOf<T>()`, `DllImport` with `LibraryImport`. Named memory-mapped files are Windows-only in modern .NET; the temp-file fallback can go.
3. **Window and present**: the demo depends on WinForms and System.Drawing (window, loading screen text, icon, PNG/JPEG encoding). A plain Win32 window through P/Invoke removes both; a PNG writer needs only `ZLibStream` and a CRC-32.
4. **NativeAOT**: publishing needs the Visual Studio C++ build tools (documented prerequisite), which the entry-level laptop does not have. Either publish AOT in CI and copy the exe, or ship a self-contained JIT build for the laptop. WinForms + AOT support is not confirmed.
5. **Tiered JIT vs calibration**: .NET Framework compiled each method fully optimised on first call. .NET 10 starts hot loops in unoptimised tier-0 code and swaps in optimised code later, so start-up calibration can measure slower code than the game then runs. Disable tiered compilation for the fixture (`<TieredCompilation>false</TieredCompilation>`) or use AOT, and re-check the calibrated FPS. Not measured.
6. **Speed**: .NET 10 will run this code faster than .NET Framework 4.6.2 (the spec notes Mono was several times slower the other way). Expect the calibration clamps (800 px, 1400 creatures, Detail ≤ 1) to saturate on faster machines; widen them or add a load multiplier.
7. **Atomic save**: use `File.Move(tmp, path, overwrite: true)`.
8. **Comments**: the spec's no-comments rule (III.1) applies to product source. Decide explicitly whether the fixture counts as product source; the demo files are heavily commented.
9. **Seeded `Random`**: modern .NET keeps a compatibility implementation for seeded instances, so the world should match the demo's; I did not compare outputs. Float maths (`Math.Pow`, `Sin`, `Cos`) can differ in the last bit between runtimes. Do not rely on pixel-identical images across runtimes; add a golden hash of the height field if identity matters.

---

## 16. Recommended new switches (proposal, not in the demo)

Names are suggestions; the point is the capability.

| Need | Proposed switch |
|---|---|
| Repeatable workload | `--workload=<BaseW>,<entities>,<detail>` or `--calibration=off`, bypassing calibration; plus a load multiplier above the current clamps |
| Hermetic runs | `--data-dir=<path>`, `--map-name=<name>` (parallel instances), single-instance guard |
| One-line scenarios | `--scenario=single-thread-render | sim-bound | storage-hitch | warmup | memory-pressure | pixel-load`, each a fixed INI + fixture options |
| Simulation-bound | `[Fixture] EntityCount=<n>` (overrides calibration), allowing counts beyond 1400 |
| Storage hitch scaling | `[Fixture] SaveSizeMB`, `SaveWriteThrough`, `SavePath` |
| Warm-up control | `[Fixture] WarmupSeconds`, `WarmupBurstChunks`, `WarmupIntervalMs`; new command `TriggerWarmup = 3` |
| Memory pressure | `[Fixture] MemoryBallastMB` or `LeaveFreeMB`, scaled by `Textures`; `MemoryTouchMBPerFrame` |
| Background load | separate `GAPO.Fixture.Load.exe --threads=N --duty=P --priority=… --seconds=S [--disk] [--memory-mb=N]` |
| Launcher priority case | `--priority=belownormal` |
| Real GPU load | D3D11 flip-model swap chain for presentation, with a full-screen shader whose iteration count is a setting; WARP on the GPU-less CI runner |
| Deterministic simulation | `--fixed-dt=<ms>` and a separate RNG for particles |
| Product-realism cases | a restart-required setting (applies only at next start), "rewrite the INI on exit" mode, `--no-telemetry` to force the PresentMon-only path |
| Machine-readable results | `--log-format=json`, averages over the run instead of the last frame, non-zero exit code on failure |

---

## 17. Constant reference

| Constant | Value | Where |
|---|---|---|
| Map name | `GAPO_Sandbox_Telemetry_v1` | Telemetry |
| Magic / Version | `0x4F504147` / 1 | Telemetry |
| Header / capacity / record / total | 4096 B / 4096 / 120 B / 495,616 B | Telemetry |
| Reader lap margin | 16 records | Telemetry |
| String capacities | path 256, toast 128, title 64 chars | Telemetry |
| Reader poll | 100 ms connected, 1000 ms searching | GameLink |
| Reader start backlog / store cap | 600 frames / 24,000 frames | GameLink |
| Disconnect silence | 4 s | GameLink |
| Command ack timeout / poll | 2000 ms (800 on cancel) / 15 ms | GameLink |
| Revision ack timeout / poll | 4000 ms (3000 on revert) / 30 ms | GameLink, engine |
| Settle discard / window / min frames / max window | 650 ms / 2600 ms / 45 / 7000 ms | OptimisationEngine |
| Warm-up wait | ≤ 45 s, 250 ms poll, last 90 frames clean, ≥ 30 frames | OptimisationEngine |
| Hitch observation | ≥ 14 s (≤ 15 s extra wait) | OptimisationEngine |
| Settings poll | 200 ms | Game |
| Save retries | 21 × 15 ms; load 21 × 10 ms | SandboxSettings |
| Toast duration | 3200 ms (start-up toast 6500 ms) | Game, GameForm |
| Calibration targets | render 20.0 ms, simulation 8.5 ms | Game |
| Render probe | 320×180, 7 frames, drop 2, exponent 1/1.9 | Game |
| BaseW | multiple of 32, 224–800; default before calibration 480×270 | Game |
| Detail | 0.45–1.0; accept ≤ 24.0 ms; correction ×1.05; ≤ 3 passes | Game |
| Sim probe | 300 creatures, 6 updates, dt 0.016, drop 1 | Game |
| EntityBase | 80–1400 (file load 120–1400); default 500 | Game |
| Calibration file version | `5` | Game |
| World seed / sim seed / texture seed | 20260930 / 1234 / 7 | World, Simulation |
| World | 2048², sea 62, 1024 chunks of 64² | World |
| Warm-up | 40 chunks per burst, 110 ms between bursts | Game |
| Sun | 34° ± 10°, period 240 s | Game |
| Baked shadow refresh | 24 rows per frame | Game |
| Path | loop 150 s, benchmark start 18 s, benchmark segment 12 s | Simulation, Game |
| Path shape | radius 640 ± 170 (3 lobes), centre (1024, 1024), yaw wobble 0.28 × sin(0.11t) | Simulation |
| Path height | mean + 44, or ground + 22 | Simulation |
| dt clamp | 0.001–0.05 s | Game |
| Autopilot resume | 12,000 ms idle | Game |
| Manual flight | 70 / 150 units/s, 1.6 rad/s, 60 units/s vertical, ceiling 420 | Game |
| Simulation radius | Near 230, Far 560 | Simulation |
| Flocking | neighbour r² 900, separation r² 30, speed 0.6–3.2 | Simulation |
| Collision | r² 16 | Simulation |
| Particles | 0 / 700 / 2000 / 4500 / 9000; box ±170 | Simulation |
| Herd spawn | 25 creatures, 90 units ahead | Game |
| Texture pack | 256² / 1024² / 2048² ints | World |
| Terrain step | 1/Detail; LOD 0.55/Detail growing 0.0105 per step; start z 1.5 | Renderer |
| Shadow steps | max(6, 22×Detail), reach 66 cells | Renderer |
| Water steps | max(8, 24×Detail) | Renderer |
| Cloud octaves | 5 (4 when Detail < 0.8) | Renderer |
| Fog | 4 march steps; `k = 3.0 / RenderDistance` | Renderer |
| Dispatch | inline when threads ≤ 1 or count < 8; else `threads × 4` chunks | Renderer |
| Camera | FOV 90°, horizon 0.36 × height | Renderer |
| Autosave | 4,194,304-byte buffer, write-through, 4096-byte stream buffer | Game |
| Frame cap | sleep while > 1.6 ms left, then `SpinWait(40)`; no catch-up beyond 50 ms | Game |
| Timer resolution | `timeBeginPeriod(1)` (windowed only) | Program |
| HUD | 240-frame history, stats every 500 ms; FPS colour ≥ 55 green, ≥ 38 amber | Game, GameForm |
| Window | client width min(1280 × dpi, 86 % of work area), 16:9, ≤ 84 % height; min 480×300 | GameForm |
| Auto-test hang limits (CI) | 120 s (20 s run), 240 s after a 200 s run | workflow |

---

## 18. Pitfalls found in the demo code

- Spec Appendix A states 128-byte records; the real size is 120.
- `InternalWidth/Height`, `ClientWidth/Height` header fields, `FrameFlags.Calibrating`, and states `Starting`/`Calibrating` are dead.
- `EntitiesTotal` in the record is the density-scaled count, not the world total (that is header `EntityCount`).
- `PostMs` is never zero with AA off: it carries the render set-up time.
- With `Shadows=High`, the baked-shadow refresh is main-thread work outside every stage timer.
- A `Textures` change regenerates up to 4 M random values in the reload frame.
- Settings save is delete-then-move, not atomic.
- An invalid INI value silently becomes the Ultra default.
- The command channel holds one command; senders must wait for the ack.
- The ring is not cleared at start; only the header is.
- The session-local map name is invisible to a service in session 0.
- Present time depends on window size, DPI and occlusion; a minimised window presents nothing and frames run faster.
- Dragging the game window stops frames.
- Headless mode has no 1 ms timer resolution, so the frame cap is coarse there.
- The benchmark scene is only camera-deterministic (section 7).

---

## 19. Sources consulted outside the repository

- PresentMon console README (present-mode list): https://github.com/GameTechDev/PresentMon/blob/main/README-ConsoleApplication.md
- Native AOT deployment prerequisites: https://learn.microsoft.com/dotnet/core/deploying/native-aot
- `System.Random` constructor notes: https://learn.microsoft.com/dotnet/api/System.Random.-ctor
- Seeded compatibility implementation: https://source.dot.net/System.Private.CoreLib/src/libraries/System.Private.CoreLib/src/System/Random.Net5CompatImpl.cs.html

---

## 20. Not verified

- **Whether PresentMon reports frames for a window that presents only with GDI `BitBlt`.** Its README lists GDI-related composed modes but does not say a pure GDI program is tracked. This decides whether the fixture needs a DXGI swap chain; test it before choosing the present path.
- Nothing was built or run on .NET 10; I did not run the sandbox at all. All behaviour is from reading the source. Only the record size and offsets were measured (under the .NET Framework CLR).
- Performance figures (77 ms autosave, ≈ 3 s warm-up, 33–38 FPS) are the spec's or derived from constants, not re-measured.
- That an INI with the render features stripped makes simulation the primary limiter (section 14) is an inference from the calibration targets.
- Tiered-compilation impact on calibration, and seeded-`Random`/float identity between .NET Framework and .NET 10.
- WinForms under NativeAOT on .NET 10.
- `DwmFlush` behaviour on the GitHub runner's Hyper-V display, and whether Windows 11 honours `timeBeginPeriod(1)` for an occluded or minimised game window.
- Whether a .NET SDK is installed on this development machine (`dotnet --list-sdks` printed nothing).
- The current hardware of `windows-latest` runners; the spec's figures (1 core / 2 threads, 8 GB, 64 Hz, no GPU) are from the demo's runs.
