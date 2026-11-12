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

## In-progress state (updated 2026-11-12, step 0 pushed for its first Windows CI run)

Branch `step-0-test-rig`, pushed, PR open against the default branch. What exists and passes locally (89 unit tests, no-comments check, end-to-end rig):

- `tools/Gapo.SourceRules` — no-comments checker (`check . --exclude demo --exclude docs`) and `strip`.
- `fixture/Gapo.Sandbox.Contract` — telemetry contract (120-byte record, pinned by tests), settings INI, paths.
- `fixture/Gapo.Sandbox` — the fixture game (WinForms window, software renderer, D3D11 flip-model present with WARP and GDI fallbacks, scenario switches, JSON result log). Run: `artifacts/bin/Gapo.Sandbox/release/GAPO-Sandbox.exe --data-dir=<dir> --reset --autotest=20 --log=<file.json> [--scenario=...] [--workload=W,E,D]`.
- `src/Gapo.Core` (overhead meter + budgets), `src/Gapo.Windows` (own-process probe using cycle counts, background mode, process liveness), `src/Gapo.Agent` (skeleton: attaches to the fixture's engine hook, reads frames at 10 Hz, measures itself, exits 4 when over budget).
- `tests/Gapo.EndToEnd` — the rig: fixture alone, fixture + agent, fixture alone; asserts overhead budget and A/B within noise. Run: `artifacts/bin/Gapo.EndToEnd/release/Gapo.EndToEnd.exe --output=<dir> --seconds=20`.
- `.github/workflows/windows.yml` — build, source rules, unit tests, end-to-end, artifacts. The demo workflow is manual-only now.

Build and test locally:

```
%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe build Gapo.slnx -c Release
%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe test --solution Gapo.slnx -c Release
```

Research workflows failed twice on the usage limit and cost a lot; do not re-run them. Decide by direct experiment instead (for example: run PresentMon against the fixture here to see what it reports), and look things up inline only when needed.

## Next actions, in order

1. Check the Windows CI run of the PR (`gh run list -R mathforreallife/adaptive-performance --branch step-0-test-rig`), fix whatever fails on the runner (likely candidates: D3D device creation on the GPU-less VM, timing of the A/B check on 2-4 vCPUs, action versions), add the CI numbers to spec Appendix B, merge the PR when green.
2. Remaining step-0 items: a background-load generator executable (`fixture/Gapo.LoadGenerator`), ADRs in `docs/adr/` (solution layout, fixture present path, cycle-counted overhead, test rig = hosted runner + this laptop, branch/PR convention), README refresh, end-of-phase summary for the user.
3. Step 1, telemetry core: get PresentMon working against the fixture on this laptop first (download the console build from the GameTechDev/PresentMon GitHub releases into a scratch folder, run it elevated with CSV to stdout for the fixture's process, confirm it reports the D3D11 presents and CPUBusy/GPUBusy), then decide the integration (console child process vs own ETW consumer), then build: frame-timing source abstraction, per-core load and process snapshot via `NtQuerySystemInformation`, game CPU/IO/memory on cached handles, all at <= 1 Hz except frame events, with the overhead gate still green.
4. Then continue the build order below.

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
