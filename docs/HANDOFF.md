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

Step 1 so far (branch `step-1-telemetry-core`, 111 unit tests passing locally):

- `src/Gapo.Core/Telemetry/ProcessorLoad.cs` + `src/Gapo.Windows/Telemetry/ProcessorTimesProbe.cs` — per-core load from one `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` call.
- `src/Gapo.Core/Telemetry/ProcessSnapshot.cs`, `ProcessActivity.cs` + `src/Gapo.Windows/Telemetry/ProcessSnapshotReader.cs` — one `SystemProcessInformation` snapshot: every process (cycle counts, memory, I/O) and the threads of one chosen process; share of machine per process; busiest thread as % of one processor.

Build and test locally:

```
%LOCALAPPDATA%/Microsoft/dotnet/dotnet.exe build Gapo.slnx -c Release
%LOCALAPPDATA%/Microsoft/dotnet/dotnet.exe test --solution Gapo.slnx -c Release
artifacts/bin/Gapo.SourceRules/release/gapo-source-rules.exe check . --exclude demo --exclude docs
artifacts/bin/Gapo.EndToEnd/release/Gapo.EndToEnd.exe --output=<dir> --seconds=20
```

Research workflows failed twice on the usage limit and cost a lot; do not re-run them. Decide by direct experiment and look things up inline only when needed.

## Next actions, in order

1. Frame timing without injection (step 1 core). Build GAPO's own real-time ETW consumer:
   - `Gapo.Core/FrameTiming`: pure `PresentTracker` — fed present start/stop events (process, thread, swap chain, QPC time, flags), it emits frames with frame time (start to start), CPU busy (previous present end to this present start) and time inside the present call; ignores test presents (flag 0x1); tracks per swap chain. Unit-test it with synthetic event streams.
   - `Gapo.Windows/FrameTiming`: `EtwPresentSession` via P/Invoke (`StartTraceW`, `EnableTraceEx2`, `OpenTraceW`, `ProcessTrace` on its own thread, `ControlTraceW` stop; real-time mode, QPC timestamps, event-record callback as an unmanaged function pointer). Providers: Microsoft-Windows-DXGI `{CA11C036-0102-4A2D-A6AD-F03CFED5D3C9}` (Present start 42 / stop 43, PresentMultiplaneOverlay 55 / 56) and Microsoft-Windows-D3D9 `{783ACA0A-790E-4D7F-8451-AA850511C6B9}` (Present start 1 / stop 2). Needs admin or *Performance Log Users*; report access-denied as "frame timing unavailable", never crash. Stop a stale session with the same name before starting.
   - Validate against ground truth: run the fixture game, capture its presents by ETW, compare frame count and mean frame time with the fixture's own JSON result (expect agreement within about 2 %). Add this to the end-to-end rig (the shell on this laptop is elevated and the CI runner is admin). Measure the consumer's overhead with the existing gate.
   - Record the decision in the spec (changed: own ETW consumer of the events PresentMon uses, PresentMon as cross-check when permitted) and in an ADR.
2. Game CPU / I/O / memory on a cached handle (`GetProcessIoCounters`, `K32GetProcessMemoryInfo`), memory status (`GlobalMemoryStatusEx`), a 1 Hz monitor in the agent that ties these together, all inside the overhead budget.
3. Push `step-1-telemetry-core`, open PR #2 (base `step-0-test-rig` unless PR #1 was merged), make CI green, write the lessons into the spec.
4. Then continue the build order below (step 2 hardware inventory, step 3 measurement harness, ...).

## Task list (build order, spec III.4)

- [x] 0. Test rig — done (PR #1, waiting for the user to merge)
- [ ] 1. Telemetry core — in progress
- [ ] 2. Hardware inventory
- [ ] 3. Measurement harness
- [ ] 4. Diagnosis v1
- [ ] 5. First three game adapters
- [ ] 6. Experiment engine
- [ ] 7. UI
- [ ] 8. Knowledge base + learning
- [ ] 9. Sensors via vendor SDKs
- [ ] 10. Latency and network modules
- [ ] 11. Beta hardening
- [ ] 12. Autonomous optimisation
