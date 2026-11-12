# GAPO build — handoff / resume file

Read this first when resuming. It says where the build is, what was decided, and exactly what to do next. Keep it current: update it at the end of every working step and before any long-running operation.

Last updated: 2026-11-12 (Europe/Madrid), build-order step 0 in CI.

## The job

Build the real GAPO product from `Adaptive-Performance-FINAL.md` (the handover spec, in the repo root) following `Instructions.txt` (in the project folder, untracked). Work Part III → "Build order" in order, starting with step 0 (the test rig). The spec has been read in full; re-read the relevant Part II/III sections before each step.

Standing rules from `Instructions.txt` (do not drop any of these):

- Every increment builds, passes tests and passes the Windows end-to-end run before it is pushed. PR to GitHub regularly.
- Optimiser overhead budgets (spec III.7) are a CI gate. Show own CPU/RAM in the UI.
- UI: minimal, flat, one background colour, follows Windows light/dark/high-contrast live, no gradients/glow/Mica/Acrylic. Simple view with one honest percentage + "Show more options" with five tabs. Do not copy the demo's styling.
- Product source contains no code comments; a CI check enforces it. Rationale goes in `docs/` and `docs/adr/`.
- Never inject into games, read game memory, ship vulnerable drivers, change BIOS settings, kill processes, or make unverified performance claims.
- Update `Adaptive-Performance-FINAL.md` with every new lesson (🧪 / ✏️ / ➕ markers) in the same commit as the code that taught it.
- At the end of each phase, summarise what works, what was measured on Windows (FPS before/after, overhead) and what is next.
- The end goal is a day-to-day app for an average user that optimises Steam games without breaking them.

## Environment facts

- Project folder: `C:\Users\Kevin\Desktop\Project` — it is now a git working tree of the GitHub repo `mathforreallife/adaptive-performance` (private). `gh` is logged in.
- Default branch is `claude/eloquent-mendel-mn6f48` (holds the demo). Two other branches (`claude/exciting-hopper-l21584`, `claude/friendly-pasteur-dwvktb`) contain unrelated projects — leave them alone.
- Repo-local git identity is set (`mathforreallife`, GitHub noreply address).
- Untracked on purpose so far: `Instructions.txt`, `GAPO-Demo.zip` (same zip as `demo/release/GAPO-Demo.zip`).
- .NET 10 SDK 10.0.404 is installed per-user at `%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe` (not on PATH; the `dotnet` on PATH has runtimes only). No Visual Studio, no MSVC, no Windows SDK, no PowerShell 7, no Node.
- This PC is itself a real entry-level test machine: Dell Latitude E6430, i7-3520M (2 cores / 4 threads), Intel HD 4000, 16 GB DDR3 dual-channel, SSD, 1600×900 @ 60 Hz, Windows 10 Pro 22H2 (19045). The shell session is elevated and the user is in *Performance Log Users*. Steam is not installed.
- A persistent Windows 11 VM is not realistic on this hardware (unsupported CPU, 2 cores). The test rig is: GitHub Actions `windows-latest` (virtual) + this laptop (physical).
- The auto-mode permission classifier refuses `git checkout -- .`; use non-forcing commands such as `git checkout-index -a` to materialise missing files.
- Usage limits: a 10-agent parallel workflow exhausted the 5-hour usage limit within minutes. Keep fan-out small (3–4 agents at a time) and do more work inline.

## Measured so far

- Demo end-to-end on this laptop (2026-11-12): 26.1 → 68.1 average FPS (+161 %), 1 % low 11.2 → 58.0, frame work 37.1 → 14.5 ms, stutters 20.2 → 0 per minute, optimiser 0.33 % CPU run average / 0.00 % in windows / 34 MB private, repaint 14.2 ms average. Kept: autosave 10 s → 5 min, render threads 1 → 2, physics substeps 8 → 4, SSAA → FXAA. Rolled back: 60 FPS cap. This still has to be recorded in the spec (Appendix B) with the first product commit.

## Where the work stands

Step 0 (test rig) — in progress. Nothing of the product has been committed yet.

Done:

1. Read the whole spec and the instructions.
2. Connected the folder to the repo; built and ran the demo locally.
3. Research briefs (in `docs/research/`): `demo-sandbox.md` (complete), `demo-engine.md` (written, verify it is complete).

Interrupted by the usage limit — still to produce (one brief each in `docs/research/`):

- `demo-telemetry.md` — demo hardware scanner, monitor, GameLink: P/Invokes, WMI queries and AOT-safe replacements.
- `demo-ui.md` — information architecture and strings of the demo UI (structure only).
- `presentmon.md` — current PresentMon versions, metrics, privileges, behaviour on WARP/no-GPU runners, integration path.
- `ui-stack.md` — WinUI 3 / Windows App SDK today: CLI-only build, unpackaged, footprint, theming, CI screenshots.
- `dotnet-runtime.md` — NativeAOT prerequisites, service, pipes, SQLite, EcoQoS, test framework, no-comments checker.
- `ci-runners.md` — windows-latest image, hardware, minutes budget for a private repo, CI design.
- `game-detection.md` — Steam/Epic/GOG library formats, running-game detection, anti-cheat signatures.
- `adapter-games.md` — first three Steam adapters and one free game that runs unattended on a CI runner.

The research workflow script is saved at
`C:\Users\Kevin\.claude\projects\C--Users-Kevin-Desktop-Project\03b2d8a3-1b4f-4985-b608-67e477eab4b4\workflows\scripts\gapo-understand-wf_e4c9822a-3a0.js`
(run id `wf_e4c9822a-3a0`, resumable only inside the same session; in a new session just re-create the missing briefs).

## In-progress state (updated 2026-11-12, step 0 done, step 1 started)

**Step 0 is complete and green on Windows CI twice** (runs 43218799941 and 43219536423). PR #1 (`step-0-test-rig` into the default branch) is open and waiting for the user: the permission system does not let the agent merge pull requests ("merge without review"). Do not try to work around that. Later steps are stacked on top: branch `step-1-telemetry-core` was created from the step-0 tip; open its PR with base `step-0-test-rig` (or the default branch once PR #1 is merged).

Two things need the user's decision (asked in chat on 2026-11-12; check whether they answered):

1. Merging PR #1 (and later PRs) — the user has to merge, or add a permission rule that allows it.
2. PresentMon — downloading and running the PresentMon console executable (GameTechDev/PresentMon release v2.6.0, `PresentMon-2.6.0-x64.exe`) was also blocked by the permission system. Without it the frame-timing path is being built as GAPO's own small ETW consumer of the same present events (see next actions). If the user allows PresentMon, use it to cross-check the own consumer.

Step 0 results: laptop 23.1 / 23.3 / 23.3 FPS (alone / with agent / alone), agent 0.019–0.054 % CPU, 6.0 MB; CI runner 39.4 / 39.5 / 39.3 FPS, agent 0.064 % CPU, 6.1 MB. Budget: 0.3 % / 40 MB.

Step 1 so far (branch `step-1-telemetry-core`, PR #2 with base `step-0-test-rig`, 140 unit tests passing, CI green on the runner for the frame-timing commit):

- Per-core load: `Gapo.Core/Telemetry/ProcessorLoad.cs`, `Gapo.Windows/Telemetry/ProcessorTimesProbe.cs`.
- Process/thread snapshot: `Gapo.Core/Telemetry/ProcessSnapshot.cs`, `ProcessActivity.cs`, `Gapo.Windows/Telemetry/ProcessSnapshotReader.cs`.
- Frame timing by GAPO's own ETW consumer: `Gapo.Core/FrameTiming/PresentTracker.cs`, `PresentedFrameQueue.cs`, `Gapo.Windows/FrameTiming/EtwNative.cs`, `EtwPresentSession.cs`; agent side `Gapo.Agent/FrameTiming/`. Validated in the rig against the fixture's telemetry (0.0-0.3 % apart on the laptop, 0.1 % on the runner, no events lost; tracing does not slow the game).
- Game counters and memory: `Gapo.Windows/Processes/GameProcessProbe.cs`, `Gapo.Windows/Telemetry/MemoryStatusProbe.cs`.
- 1 Hz monitor: `Gapo.Core/Telemetry/SystemMonitor.cs`, agent side `Gapo.Agent/Monitoring/`. The rig asserts the single-threaded fixture uses about one processor, its busiest thread is near 100 % of one, and its autosave writes are seen.
- Agent with everything on: about 0.14 % CPU, 9.6 MB private (budget 0.3 % / 40 MB).

Build and test locally:

```
%LOCALAPPDATA%/Microsoft/dotnet/dotnet.exe build Gapo.slnx -c Release
%LOCALAPPDATA%/Microsoft/dotnet/dotnet.exe test --solution Gapo.slnx -c Release
artifacts/bin/Gapo.SourceRules/release/gapo-source-rules.exe check . --exclude demo --exclude docs
artifacts/bin/Gapo.EndToEnd/release/Gapo.EndToEnd.exe --output=<dir> --seconds=20
```

Research workflows failed twice on the usage limit and cost a lot; do not re-run them. Decide by direct experiment and look things up inline only when needed.

## Step 2 state (branch `step-2-hardware-inventory`, stacked on step 1)

- `src/Gapo.Core/Hardware/` — `HardwareInventory.cs` (facts model), `HardwareRules.cs` (channel judgement, adapter classification, Windows naming, PC class), `SmbiosMemoryReader.cs` (pure SMBIOS type 17 parser).
- `src/Gapo.Windows/Hardware/` — `HardwareNative.cs`, `ProcessorProbe.cs`, `GraphicsProbe.cs` (DXGI through the COM vtable), `SystemProbes.cs` (memory modules, display, storage, power, Windows), `HardwareScanner.cs` (isolated steps, "could not be read" list).
- The agent scans once at start-up and writes the inventory into its report; the rig checks it (logical processors, memory, display, Windows build, the adapter the game rendered on is listed, scan under 1.5 s). On the laptop: 55 ms, nothing unread.
- Not yet done for step 2: rescan on hardware or driver change, VRAM budget/usage (`IDXGIAdapter3::QueryVideoMemoryInfo`), PCIe link width, monitor VRR.

## Step 3 state (branch `step-3-measurement-harness`, stacked on step 2)

- `src/Gapo.Core/Measurement/` — `FrameStatistics.cs`, `MeasurementWindow.cs`, `NoiseFloor.cs` (with `ChangeComparison`), `WarmupDetector.cs`; tests in `tests/Gapo.Core.Tests/MeasurementTests.cs`.
- `src/Gapo.Agent/Measurement/BaselineMeasurement.cs` — state machine driven from the agent loop: wait for warm-up, switch the fixture to its benchmark flight, restart the scene before each window, two windows, noise floor, retry up to three times when the pair cannot be trusted. `--measure-baseline` turns it on; the report gets a `baseline` object. The rig runs it as a fourth run (`baseline-measurement`, 45 s) and checks it, including CPU inside windows.
- Rig output directories: the old session scratchpad is gone; use `artifacts/rig/<name>` inside the repo (ignored by git).
- Timing-sensitive unit tests failed twice on loaded machines; keep lower bounds loose (15-20 % of a processor) whenever a test spins a thread.

## Step 4 state (branch `step-4-diagnosis`, stacked on step 3)

- `src/Gapo.Core/Diagnosis/` — `DiagnosisInput.cs`, `BottleneckDiagnosis.cs`, `Diagnostician.cs`; tests in `tests/Gapo.Core.Tests/DiagnosticianTests.cs`.
- Agent: after a successful baseline it diagnoses from the combined windows, stage medians (`Measurement/StageAccumulator.cs`), the monitor summary, present timing and hardware; the report gets a `diagnosis` object. The rig runs the default fixture (expects `RenderSingleThread` plus the storage-hitch note) and `--scenario=simulation-bound` (expects `CpuMainThread` and the engine-limitation text).
- CI budget: the repository is private and Windows minutes are limited (each run is 3-5 minutes, billed double). Push once per step, not per commit.
- The rig now takes about 4 minutes locally (five fixture runs).

## Steps 5-6 state (branch `step-5-6-adapters-and-engine`, stacked on step 4)

- `src/Gapo.Core/Optimisation/` - `GameSettings.cs`, `OptimisationModel.cs` (candidates, goals, records, the `IGameSession` / `IGameKnowledge` / `IChangeJournal` interfaces), `OptimisationEngine.cs`. Tests: `tests/Gapo.Core.Tests/OptimisationEngineTests.cs` with `SimulatedGame.cs`.
- `src/Gapo.Adapters.Sandbox/` - `SandboxSettingsMap.cs`, `SandboxKnowledge.cs` (all ladders), `StageAccumulator.cs`. Tests in `tests/Gapo.Adapters.Sandbox.Tests`.
- Agent: `Optimisation/SandboxSession.cs` (the session runs on the agent's own loop: every wait pumps the loop, no extra thread), `Optimisation/FileChangeJournal.cs`, `Optimisation/OptimisationReportJson.cs`. Options: `--optimise=balanced|maximum-fps|quality|smoothness`, `--journal=<path>`, `--ask-game-to-close-when-done` (fixture command `Quit`), `--revert-all`.
- Rig runs: baseline-1, with-agent, baseline-2, optimise (+ revert all), simulation-bound. About 5 minutes locally.
- Result on this laptop: 25.2 -> 94.0 FPS (+274 %), or +143 % when the 60 FPS cap is kept; agent 0.29 % CPU while optimising, 0.11 % in windows, 16 MB.
- Step 5 is only done for the fixture. Not done: the Steam library index, running-game detection without the hook, real-game adapters.
- Scripts that patch several files at once must be written to `artifacts/rig/*.py` and run once; a script that failed half-way was re-run and applied edits twice. Check `git diff` after any failure before re-running.

## Next actions, in order

1. Push `step-5-6-adapters-and-engine`, open PR #6 (base `step-4-diagnosis`), make CI green. Check PR #5's run too.
2. Write the end-of-phase summary for the user (what works, FPS before/after, overhead, what is next) if not already given.
3. Step 7, UI: WinUI 3 app `src/Gapo.App`. **Verified on this laptop on 2026-11-12:** a minimal WinUI 3 app (Microsoft.WindowsAppSDK 2.6.5, `net10.0-windows10.0.19041.0`, `UseWinUI`, `WindowsPackageType=None`, `WindowsAppSDKSelfContained=true`, `RuntimeIdentifier=win-x64`, `Platform=x64`) builds with `dotnet build` and no Visual Studio in 1.5 minutes, runs on Windows 10 22H2, uses 30 MB private memory and 0 % CPU when idle; its output folder is 156 MB (self-contained runtime). The probe project is in `%LOCALAPPDATA%\Temp\gapo-winui-probe` (App.xaml with `XamlControlsResources`, a Window with a Grid). Not yet checked: whether it starts on the GPU-less CI runner, and screenshot capture there. Then: flat design per spec III.8 (tokens, typography, layout, states, copy rules); home view, results view with one honest percentage, "Show more options" with five tabs; own CPU/RAM in the header; follows light/dark/high contrast live; a named pipe between UI and agent (the agent keeps doing the work; the UI only renders when visible); screenshots of every screen in CI. If WinUI 3 cannot be built or run headless on the runner, record the reason in an ADR and fall back to WPF with the same design.
4. Real games: Steam library index (registry `SteamPath`, `steamapps/libraryfolders.vdf`, `appmanifest_<id>.acf`, a pure KeyValues parser with tests), running-game detection, a settings adapter for one real game with a text config, frame timing by ETW (already built), restart-required settings. Steam is not installed on this laptop.
5. Leftovers: GPU busy time (DxgKrnl), hitch-period observation, ADR 0004 (frame timing), ADR 0005 (engine and adapter interfaces), README refresh.

## Task list (build order, spec III.4)

- [x] 0. Test rig — done (PR #1, waiting for the user to merge)
- [x] 1. Telemetry core — done except GPU busy time (PR #2)
- [x] 2. Hardware inventory — done (PR #3)
- [x] 3. Measurement harness — done for the fixture (PR #4)
- [x] 4. Diagnosis v1 — done for the classes the fixture can show (PR #5)
- [ ] 5. First three game adapters - fixture adapter done (PR #6); real games not started
- [x] 6. Experiment engine - done on the fixture (PR #6)
- [ ] 7. UI
- [ ] 8. Knowledge base + learning
- [ ] 9. Sensors via vendor SDKs
- [ ] 10. Latency and network modules
- [ ] 11. Beta hardening
- [ ] 12. Autonomous optimisation
