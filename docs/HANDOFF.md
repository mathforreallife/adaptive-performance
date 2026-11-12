# GAPO build — handoff / resume file

Read this first when resuming. It says where the build is, what was decided, and exactly what to do next. Keep it current: update it at the end of every working step and before any long-running operation.

Last updated: 2026-11-12 08:15 (Europe/Madrid), during build-order step 0.

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

## In-progress state (updated 2026-11-12, after the second usage-limit stop)

Branch `step-0-test-rig` (local only, not pushed yet). Commit `8ec17cd` holds the solution skeleton (`Gapo.slnx`, `Directory.Build.props`, central package versions, `global.json` with the Microsoft.Testing.Platform runner) and the no-comments checker `tools/Gapo.SourceRules` with 35 passing tests. Run it with `artifacts/bin/Gapo.SourceRules/release/gapo-source-rules.exe check . --exclude demo --exclude docs`; `strip <dir>` removes C# comments.

Uncommitted, mid-port — the fixture game under `fixture/`:

- `fixture/Gapo.Sandbox.Contract/` — done: `Telemetry.cs` (same 120-byte record and header offsets as the demo, explicit layout, map name parameter, new command `TriggerWarmup = 3`), `SandboxSettings.cs` (properties, atomic save), `SandboxPaths.cs` (data dir `%LOCALAPPDATA%\GAPO-Sandbox`, overridable).
- `fixture/Gapo.Sandbox/` — demo sources copied, comments stripped, namespace renamed. `Game.cs` and `World.cs` are already patched for: `FixtureOptions`, fixed workload, scenario scaling, streaming (warm-up) control, memory ballast, save size, GPU-load iterations, run statistics. New files written: `FixtureOptions.cs`, `RunStatistics.cs`, `MemoryBallast.cs`, the csproj (net10.0-windows, WinForms, Vortice.Direct3D11 + D3DCompiler 3.8.3), `app.manifest`.
- Still to write before it compiles: `DxgiPresenter.cs` (D3D11 flip-model swap chain, upload of the composed frame, full-screen pass with a loop of `GpuLoadIterations`, WARP fallback); rewrite `Program.cs` (parse `FixtureOptions`, drop headless mode, exit codes); `Native.cs` (add `AvailablePhysicalMb()` via `GlobalMemoryStatusEx`, drop `IsWindows`); `GameForm.cs` (take options, window size, DXGI or GDI present, JSON result log from `RunStatistics`, `_game.DeleteCalibration()` is now an instance method).
- Then: add projects to `Gapo.slnx`, contract tests (record size/offsets, settings round trip), a background-load generator exe, run the fixture on this laptop.

Research workflows failed twice on the usage limit and cost a lot; do not re-run them. Decide by direct experiment instead (for example: run PresentMon against the fixture here to see what it reports), and look things up inline only when needed.

## Next actions, in order

1. Finish the fixture port listed above; build; run it locally with `--workload=...  --autotest=...`.
2. Record decisions in ADRs: solution layout, UI stack, frame-timing path, fixture present path, branch/PR convention.
3. Continue step 0 on branch `step-0-test-rig`:
   - solution skeleton under `src/`, `fixture/`, `tests/`, `tools/`;
   - the no-comments CI checker (`tools/`), run locally and in CI;
   - port of the sandbox as the fixture game (`fixture/`), keeping the telemetry contract, adding scenario switches for every bottleneck class and a fixed-workload mode for CI;
   - auto-test modes and screenshot capture;
   - the overhead meter and the budget gate;
   - the product Windows workflow (build, tests, end-to-end, overhead gate, screenshots, artifacts); switch the old demo workflow to manual-only to save CI minutes.
4. Run everything on this laptop and on the Windows runner, fix what breaks, add the lessons to the spec, open the PR, merge when green.
5. Write the end-of-phase summary, then continue with step 1 (telemetry core).

## Task list (build order, spec III.4)

- [ ] 0. Test rig — in progress
- [ ] 1. Telemetry core
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
