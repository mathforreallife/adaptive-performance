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
- Always run `dotnet build Gapo.slnx -c Release` before the end-to-end executable: `dotnet test` does not rebuild the agent, the fixture or the rig.
- CI runner result for the first engine run: 28.2 -> 62.5 FPS (+121 %), then ten experiments measured 0.0 because the present call blocks at the display rate on the software adapter; fixed by counting time inside present as wait and by the engine stop rule (three changes in a row without effect).
- Scripts that patch several files at once must be written to `artifacts/rig/*.py` and run once; a script that failed half-way was re-run and applied edits twice. Check `git diff` after any failure before re-running.

## State on 2026-11-13 (read this first; older sections below are history)

- **PRs #1-#13 are merged** into the default branch `claude/eloquent-mendel-mn6f48` (the user authorised merging on 2026-11-13: "make operational decisions on the dev including presentmon, merge PRs"). New work goes on a fresh branch off the default branch with one PR per step; merge it yourself once CI is green. Doc-only pushes (`docs/**`, `*.md`) do not start CI. Since 2026-11-13 the session runs on Opus 5.5 (the user asked to continue with Opus); commit trailers name Claude Opus 5.5.
- Rig phases and how to run one alone after a calibration run: `--only=interface` (window overhead next to the game), `--only=unknown-game` (hook-less fixture in a fake Steam library with Easy Anti-Cheat: measured only), `--only=restart` (Unreal-style restart loop, about 4 minutes). The full rig takes about 14 minutes locally and 15 on CI (timeout 30).
- **PresentMon is allowed** as a development cross-check. `artifacts/tools/presentmon/PresentMon-2.6.0-x64.exe` (not in git; `gh release download v2.6.0 -R GameTechDev/PresentMon -p "PresentMon-2.6.0-x64.exe"`, Intel-signed). GAPO's own frame timing agrees with it to 0.1 %.
- **Windows App SDK is pinned to 1.8.260921001.** 2.6.5 makes the window's compositor thread burn a whole logical processor while a game presents (ADR 0004 amendment). Do not upgrade unless the rig's live-interface phase passes on this Windows 10 laptop.
- Branch `step-7-live-link`: agent serve mode (`--serve`, `--pipe-name`, `--reports-dir`, `--exit-with-process`), `src/Gapo.App/AgentLink.cs`, live `HomeView`, own-use meter in `MainWindow`, rig phase `RunLiveInterface` (`Gapo.EndToEnd.exe --only=interface` runs just that phase after a calibration run).
- Thread-level CPU probe for the window: `artifacts/rig/threadprobe.ps1` (not in git). Never pipe its output through `Select-Object -First`; that skips its clean-up and leaves windows open.
- Latest laptop rig run (e2e26): fixture 23.5 -> 59.8 FPS (+154 %, capped, stutters 11.3 -> 0/min); agent 0.155 % CPU watching, 0.133 % inside windows; window 0.18 % / 37 MB; unknown Steam game 24.4 FPS live vs 24.1 from the game; graphics-bound unknown game diagnosed as "graphics card is the limit"; restart loop +37 % (range over six runs +37 % to +300 %, see the spec). CI (PR #13): A/B -0.1 %, 0.261 % inside windows, restart loop +109 %.
- The user asked (2026-11-13) how far the product is from working on practically any Steam game. Answer given: measuring/diagnosing works for any Steam game but has never run against a real one; automatic changes only for Unreal Engine games without anti-cheat; next decisive step is a test on a PC with Steam (offered to prepare a build and instructions; no reply yet).
- PR #13 was merged on GitHub (not by this session) at commit 010cc3b, before the fixes that made its CI green were pushed; they reached the default branch only through PR #16 (merge of `origin/step-1-gpu-busy`). Before pushing more commits to a PR branch, check `gh pr view <n> --json state`; once a PR is merged, put further work on a new branch.
- Per-setting control (user request 2026-11-13): `GameSettingsMessage` + `LockSettingCommand`/`SetSettingCommand`/`RevertSettingCommand` on the pipe; `src/Gapo.Agent/Games/GameSettingsControl.cs` (fixture and Unreal controls), `src/Gapo.Windows/Games/SettingLockStore.cs` (`setting-locks.json` next to the journal), `src/Gapo.App/Views/GameSettingsView.cs`; engines take locked keys. Rig phase `--only=settings`. Still to do from the same request: ask-before-change mode, the FPS overlay, the Settings page with saved preferences.
- Test kit: `powershell -File tools/package-test-kit.ps1` builds `artifacts/test-kit/GAPO-test-<date>.zip` (self-contained window + single-file agent + `docs/TESTING-ON-A-GAMING-PC.md`); checked on the laptop. The window notifies (app notification + taskbar flash) when GAPO asks for a restart or finishes, but app notifications never work while elevated, and this laptop has notifications switched off; the privileged telemetry service of the architecture section is the real fix (not built yet).

## Step 5, real games (branch `step-5-steam-library`, on top of `step-7-live-link`)

- `src/Gapo.Core/Games`: `KeyValues.cs` (Valve KeyValues parser, `TryParse`), `SteamLibrary.cs` (`SteamLibraryFiles.ReadLibraryFolders` / `ReadAppManifest`, `SteamLibraryIndex.Scan` over an `IGameFileSource`), `RunningGames.cs` (`RunningGameMatcher`).
- `src/Gapo.Windows/Games`: `SteamLocator` (registry), `DiskGameFileSource`, `RunningGameFinder` (process snapshot + cached image paths).
- Agent: `--list-games [--steam-root=<folder>]`. Sample tree builder: `artifacts/rig/make_steamtree.py <folder>`; stand-in running game test: `artifacts/rig/steam_running_test.ps1` (neither is in git).
- Serve mode watches for a running Steam game (`src/Gapo.Agent/Games/SteamGameWatcher.cs`, 5 s poll) and measures it through event tracing only; `canOptimise=false` on live samples hides Optimise in the interface. Rig phase `RunUnknownSteamGame` (`Gapo.EndToEnd.exe --only=unknown-game`) runs the fixture with `--no-telemetry` from a fake Steam library.
- Unknown games are diagnosed after warm-up from 20 s of event-tracing frames plus the monitor (`src/Gapo.Agent/Measurement/ObservedGameDiagnosis.cs`, `DiagnosisMessage` on the pipe, "What limits this game" in the home view). The rig expects `CpuOneThreadLimit` for the hook-less fixture.
- Unreal Engine adapter, first part (branch `step-5-first-adapter`): `src/Gapo.Core/Games/Unreal` (`IniDocument` line-preserving INI, `UnrealSettings` known keys and plain-word descriptions, `UnrealGameDetector` install layout, settings file candidates, anti-cheat markers), `src/Gapo.Agent/Games/GameRecognition.cs` (status text). Agent options `--local-app-data` and `--documents` exist for tests. The rig's unknown game has an Unreal layout.
- Restart-based optimisation for Unreal games: `src/Gapo.Core/Games/Unreal/UnrealKnowledge.cs` (ladder by diagnosis), `src/Gapo.Core/Optimisation/RestartJudgement.cs` (noise floor ≥ 5 %, keep rule, target), `src/Gapo.Windows/Games/IniChangeJournal.cs` (key-level journal, `IniSettingsFile` keeps encoding), `src/Gapo.Agent/Optimisation/RestartOptimiser.cs` (state machine driven by the 1 Hz tick). The journal for INI games is `<journal name>-ini.json` next to the sandbox journal. Fixture: `--unreal-settings=<file>`. Rig phase `RunRestartOptimisation` (`--only=restart`), about 4 minutes; CI timeout raised to 30 minutes.
- Not done: Epic/GOG libraries, the first real-game adapter (settings file read/write with restart-required settings), a run against a real Steam installation, an ETW process-id filter so other programs' presents are not delivered to the agent at all.

## Step 7 state (branch `step-7-ui`, stacked on steps 5-6)

- `src/Gapo.App` - WinUI 3 app: `Themes/Tokens.xaml` (design tokens for light, dark, high contrast), `MainWindow`, `Views/ResultsView`, `Views/DetailsView` (five tabs), `Views/HomeView` (no-game state), `Reports/ReportView.cs` (reads the agent's report JSON), `ScreenshotRun.cs`, `AppOptions.cs`. Run: `artifacts/bin/Gapo.App/release_win-x64/GAPO.exe --report=<agent report> [--screenshots=<folder>] [--theme=light|dark]`.
- The rig runs the app after the optimise run in both themes and expects six PNG files each (`ui-light`, `ui-dark` under the rig output folder).
- Engine/fixture state: the engine stops when the game already runs at the display rate; on the CI runner the result is 33.1 -> 62.0 FPS (+87 %), four kept, none rolled back. The fixture counts time blocked in `Map` and `Present` as wait.
- Not done in the UI: live link to the agent (named pipe), home view with live stat tiles and frame-time chart, frame-budget bar, running / warm-up / error states, Revert all / Optimise again / Save report buttons, tray icon, notifications, high-contrast and 150 % / 200 % screenshots, measuring the UI process against its 0.5 % / 80 MB budget.

## Next actions, in order

1. Make sure the `step-7-live-link` PR is merged and its CI run is green (on the GPU-less runner the interface CPU budget is reported, not enforced; the quarter-of-a-processor ceiling and the 80 MB limit are).
2. Remaining UI: frame-budget bar, running / warm-up / error states with phase progress, Save report, tray icon, notifications, high-contrast and 150 % / 200 % screenshots, Tips content.
3. Real games, continued: run the Unreal adapter against a real Unreal Engine game on a PC with Steam; frame-rate cap/VSync tuning for restart-based games; second engine family (Unity's `prefs` in the registry, or a JSON-config game); a tray notification when GAPO waits for a restart.
4. Leftovers: GPU busy for hardware-scheduled GPUs (HAGS: `HwQueue`/`QueuePacket` path, needs a HAGS-capable PC), hitch-period observation, ADR for the engine/adapter interfaces, README refresh. (GPU busy time for DMA-packet GPUs is done: ADR 0005.)
5. Then steps 8-12 of the build order.

## Task list (build order, spec III.4)

- [x] 0. Test rig — done (PR #1, merged)
- [x] 1. Telemetry core — done except GPU busy time (PR #2)
- [x] 2. Hardware inventory — done (PR #3)
- [x] 3. Measurement harness — done for the fixture (PR #4)
- [x] 4. Diagnosis v1 — done for the classes the fixture can show (PR #5)
- [ ] 5. First three game adapters - fixture adapter done (PR #6); real games not started
- [x] 6. Experiment engine - done on the fixture (PR #6)
- [ ] 7. UI - results and details (PR #7, merged); live link, home view and interface overhead gate (branch `step-7-live-link`); states, tray, notifications and scaling screenshots still to do
- [ ] 8. Knowledge base + learning
- [ ] 9. Sensors via vendor SDKs
- [ ] 10. Latency and network modules
- [ ] 11. Beta hardening
- [ ] 12. Autonomous optimisation
