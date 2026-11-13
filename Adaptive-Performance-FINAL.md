# GAPO — Adaptive Gaming Performance Engine
## Final handover specification for building the commercial product

*Version 2 · 30 September 2026 · written after building and testing the GAPO demo (sandbox game + optimiser) on real Windows.*

---

## How to use this document

This is the **complete handover** for the next build. It contains everything needed to build the commercial product:

- **Part I – Conclusions from the demo.** What was built, what was measured, what worked, what didn't, the problems hit, the Windows/API gotchas, and which decisions to keep or change.
- **Part II – The product specification.** The original specification, **complete and word for word** (every section, table, list, example and principle), with the demo's findings inserted as clearly marked call-outs. The only edit to the original text is the §11 score formula, whose characters had been corrupted; it was repaired and is marked ✏️.
- **Part III – Practical build guide.** Architecture, tech stack, build order, testing (including the mandatory virtual Windows test machine), pitfalls, how to keep the optimiser's own CPU/RAM overhead low, the design system and UI rules, engineering rules, and distribution.
- **Part IV – Appendices.** The demo's measured results, how the demo maps to the product, a checklist, and a glossary.

**Reading order for the next build:** read the whole file once before writing any code. Then work Part III → *Build order* step by step, checking each step against the matching Part II sections.

### Legend for marked changes

| Mark | Meaning |
|---|---|
| 🧪 **Demo lesson** | Something learned while building or testing the demo that confirms, refines or warns about the original text. |
| ✏️ **Changed** | The original requirement was changed or tightened because of a demo finding (the original text is still there). |
| ➕ **Added** | A new requirement or piece of knowledge that was not in the original. |

Part II is the original text plus these call-outs. Parts I, III and IV are entirely new (➕).

### Contents

- Part I — Conclusions from the demo
  - I.1 What was built
  - I.2 What was measured
  - I.3 What worked
  - I.4 What didn't work, and the limitations
  - I.5 Problems hit, and how they were solved
  - I.6 Windows and API gotchas
  - I.7 Decisions to keep, and decisions to change
- Part II — The product specification (original §1–§19, with marked lessons)
- Part III — Practical build guide
- Part IV — Appendices

---

# Part I — Conclusions from the demo

## I.1 What was built

Two small Windows programs (C#, .NET Framework 4.6.2, no installer, about 200 KB together), plus a Windows CI pipeline that builds and runs them end to end.

**GAPO Sandbox (the "game").** A voxel-terrain island sandbox with flocking creatures, water, clouds, fog, blossom particles and terraforming. Its rendering is done in software on the CPU and presented with plain GDI, so it runs on any Windows PC and any graphics driver. It ships with deliberately wasteful "Ultra" defaults:

- 4× supersampling.
- Per-pixel ray-marched shadows that look the same as baked ones.
- Procedural clouds with 5 noise octaves per pixel.
- Volumetric fog.
- Ray-marched water reflections.
- No terrain LOD.
- Rendering on 1 thread.
- All-pairs (O(n²)) creature AI with 8 physics substeps on the main thread.
- A blocking autosave every 10 s.

At start-up it **calibrates its world size and ray-march detail to the PC**, so the unoptimised game runs at roughly 30–40 FPS with a visible hitch every 10 s on any machine. It publishes per-frame engine telemetry (frame time, busy time, time per render pass and per simulation stage, flags for warm-up, autosave and GC) through shared memory. It hot-reloads a plain INI settings file and reports the settings revision it is running with.

**GAPO Optimiser.** It scans the hardware once at start-up and monitors the system cheaply while running. It reads the game's telemetry and diagnoses the bottleneck, then runs the closed loop from the spec: *observe → diagnose → predict → change one setting → measure → compare → keep or roll back → re-diagnose*. It explains every decision. The UI shows the result as one big percentage, and **Show more options** opens five tabs: *What I changed*, *Diagnosis*, *Your PC*, *Tips* and *Live analyser*. The optimiser measures and displays its own CPU and memory use at all times.

## I.2 What was measured

The key test platform was a **GitHub Actions Windows runner** (Windows Server 2025, AMD EPYC 7763 with 1 core / 2 threads, 8 GB RAM, no GPU, Hyper-V display adapter, 64 Hz). As a gaming PC it counts as a very weak machine. Results of the final build (CI run #6, **Balanced** goal; all runs are in Part IV, Appendix B):

| Metric | Unoptimised | After GAPO | Change |
|---|---:|---:|---:|
| Average FPS | 39.1 | 87.9 | **+125 %** |
| 1 % low FPS | 18.0 | 62.0 | +244 % |
| 0.1 % low FPS | 9.7 | 46.1 | +375 % |
| Frame work (busy time, median) | 25.2 ms | 11.3 ms | 2.2× less work |
| Stutters per minute | 34.8 | 0 | −100 % |
| Time taken | — | 31 s · 8 measurement windows · 1,518 frames | noise floor ±2.5 % |
| GAPO's own CPU (share of the whole PC) | — | 0.55 % run average · **0.06 % inside measurement windows** | budget ≤ 1 % / ≈ 0 |
| GAPO's own memory | — | 36–38 MB private | budget ≤ 100 MB |

The engine chose these changes, in this order:

1. Autosave interval 10 s → 5 min: hitch fix, done first, then verified (no save freezes afterwards).
2. Anti-aliasing 4× SSAA → FXAA: +41 %.
3. Physics substeps 8 → 4: +17 %.
4. Render threads 1 → 2: +8 %, tested as an SMT question on this 1-core/2-thread VM.
5. A 64 FPS cap. **Tested and rolled back**, because frame pacing was not smoother with it on this machine.

It then **stopped**, because the Balanced target was met with headroom. It had considered shadows, terrain LOD, simulation distance and particles, and left them alone on purpose. Its diagnosis also recorded the key evidence: per-core load was only 52 % / 53 %, yet the game's busiest thread used **97 % of one core**. That is a one-thread limit that utilisation hides.

Earlier builds on the same machine made different but consistent decisions:

- Run #4: +80 %, 35 → 63 FPS, locked to 64 Hz by a kept frame cap. Frame-time σ went 1.3 → 0.2 ms.
- Run #5: +36 %, 46 → 63 FPS, also capped.

With a cap the headline number is limited by the display. *Frame work per frame* fell 2.2–2.3× in every run.

On a 4-thread Linux test machine (Mono runtime, much slower), the same engine chose a different path:

1. Render threads 1 → 4: +40–49 %.
2. Anti-aliasing.
3. Simulation distance.
4. A 60 FPS cap.

It **rolled back** a physics change that fell inside the noise, and it tried to give anti-aliasing quality back, then rolled that back when it broke the frame budget. The same engine on different hardware made different, correct decisions. That hardware-adaptiveness is the core product idea (§19).

**Overhead:** the first Windows run measured the optimiser at **2.2 % of total CPU averaged over the run (1.7 % inside measurement windows)**. That was over budget, so it was treated as a bug. The cause was software UI drawing (~12 ms per full repaint on a machine without GPU acceleration). The fix was:

- no repaints inside measurement windows;
- the live dashboard at 1 Hz, and 2 Hz between experiments;
- allocation-free text formatting;
- slower polling.

After the fix it measured **0.39–0.55 % run average and 0.00–0.06 % inside measurement windows**. The lesson (§I.5): on a PC without a hardware-accelerated 2D path, **UI drawing is the optimiser's biggest cost by far**. Everything else (telemetry, monitoring, engine) was a fraction of a percent.

## I.3 What worked

1. **Causal diagnosis from stage times, not utilisation.** Splitting each frame into simulation, rendering, present and streaming/saving, and comparing that against the frame budget, found the real limiter every time. That included a case the original spec didn't list: *rendering bound because it runs on one thread*. It was fixed with a parallelism setting at zero visual cost, not by lowering quality.
2. **The A/A baseline noise floor.** Measuring the unchanged game twice and requiring every change to beat 2.5× that difference stopped false "improvements". It also produced honest reversals, such as *"Reverted: +10 % is within the ±10.0 % measurement noise — no proven benefit"*.
3. **Identical scenes for every measurement.** The game's deterministic benchmark flight restarts at the start of each window. Without it, window-to-window variation would swamp most setting changes.
4. **Acknowledged, atomic, transactional changes.** Each change was written atomically (temp file + swap), the game acknowledged the revision, settling frames were discarded, the change was measured, then committed or rolled back. Nothing ever got stuck half-applied, and "Revert all" always worked.
5. **Fixing sporadic hitches first.** The autosave freeze would have polluted every later measurement. Removing it first (zero visual cost) made all the following decisions cleaner.
6. **Value ordering (predicted ms ÷ visual cost) plus stopping at the target.** The engine reached the goal with the cheapest changes and did not spend visual quality it didn't need to. The optional *quality give-back* pass tries to restore the most visible reduction when there is lots of headroom.
7. **Waiting out warm-up.** Refusing to measure while the game streams its world kept streaming spikes out of the baseline.
8. **Explaining everything.** Each experiment card shows the hypothesis, the measured before/after, the visual cost, and the decision with its reason. The *Considered but not changed* list (for example textures, left alone because memory wasn't under pressure) builds trust as much as the kept changes do.
9. **Hardware-aware decisions.** Core count → render-thread count to test. SMT → test it rather than assume it. Refresh rate → frame budget and cap. Unknown refresh → assume 60 Hz and say so. Memory headroom → leave textures alone. Battery, power mode, single-channel RAM, drive type and virtual GPU → evidence-based tips.
10. **Native, driver-free telemetry.** Per-core load, the game's CPU/IO/memory, per-thread CPU (from one kernel snapshot) and the optimiser's own overhead all came from cheap Windows calls, with no admin rights and no kernel driver.
11. **Testing on real Windows from day one.** CI on a Windows runner caught real problems that the Linux/Mono development loop could not: the true overhead, the runner's below-normal process priority, a monitor/game connection race, a WMI "1 Hz" vs real 64 Hz refresh report, inconsistent VM memory-module data, and fonts.
12. **The simple view plus "Show more options".** One honest number with 3–4 before → after metrics for everyone, and every measurement and reason one click away. (The *structure* worked; the demo's gradient/glow *styling* is demo-only — see Part III.8.)

## I.4 What didn't work, and the limitations

- **Controlling the game.** The demo controls its own game: it has a benchmark path, a telemetry hook, a settings acknowledgement and hot reload. Real games offer none of these by default. The commercial product has to rebuild each of them per game (settings adapters, restart handling, PresentMon for frame timing, scene matching or built-in benchmarks). This is the biggest gap between demo and product.
- **1 % and 0.1 % lows from short windows are statistically weak** (150 frames means the "1 % low" is 2 frames). The demo judges experiments on median busy time and p95, and reports lows over longer combined windows. A real product must collect lows across play sessions.
- **Rendering is CPU-only**, so GPU-bound behaviour (VRAM, GPU clocks, ray tracing) is only *modelled* by the "pixel work" stages. Real GPU telemetry needs vendor SDKs and PresentMon.
- **No temperatures, power, fan or voltage** (these need drivers or vendor SDKs; see §5 call-outs). Battery, Quiet and Competitive modes were not implemented.
- **No input-latency or network measurement.**
- **Predictions are rough.** The cost model got the order of magnitude and the ranking right, but individual predictions were off by up to ~2×. They need learning from measured outcomes (Phase 6).
- **One change at a time is slow** when settings need a game restart. The demo's ~3.5 s per test becomes minutes per test in real games. Prioritisation and early stopping matter even more there.
- **WinForms and GDI+ are not the right UI stack for the product.** It worked, looks good and has zero dependencies, but CPU-side 2D drawing was the largest overhead, and accessibility and per-monitor DPI are weak.

## I.5 Problems hit, and how they were solved

| # | Problem | Symptom | Solution / lesson |
|---|---|---|---|
| 1 | **Measurement noise** | Window-to-window differences of 3–10 % on VMs and busy PCs | A/A baseline → noise floor (2.5× the difference, clamped 2.5–10 %). Changes must beat it. |
| 2 | **Different scenes per window** | Throughput "changes" caused by the camera, not the setting | Deterministic benchmark flight restarted per window, plus a discard period after each restart. |
| 3 | **Measuring before a change took effect** | Mixed old/new frames | Settings revision acknowledgement from the game, then discard 650 ms, then measure. |
| 4 | **Warm-up contamination** | Streaming spikes would enter the baseline | Detect warm-up (flags or spike pattern), wait up to 45 s, tell the user why. |
| 5 | **Periodic hitches longer than a window** | Autosave every 10 s appears in some windows and not others | Observe ≥ 14 s before judging hitches. Fix hitch sources first. Verify afterwards over a long span. |
| 6 | **Stutter statistics mixed across settings** | "0 → 356 stutters/min" (a stutter threshold relative to a median built from frames of different configurations) | Compute stutter statistics only over frames with the same settings revision. |
| 7 | **Frame cap hides gains** | FPS flat while work shrinks | Judge experiments on busy time (work excluding the cap wait). Apply the cap last. |
| 8 | **Short-window 1 % lows** | Random rejections | Reject on 1 % low only when the drop exceeds max(12 %, 3× noise). Judge throughput on medians. |
| 9 | **Optimiser overhead over budget** | 2.2 % CPU on a 2-vCPU machine | No repaints inside measurement windows. Dashboard at 1 Hz, 2 Hz between experiments. Cached text formats. Slower polling. Rolling overhead display. |
| 10 | **Misleading overhead number** | Pill averaged in the one-off WMI hardware scan | Show a rolling 10-second average. Report run and in-window overhead separately. |
| 11 | **False evidence text** | "The rest of the CPU is mostly idle" while background load was 52 % | Every evidence sentence is conditional on the measurement that supports it. |
| 12 | **Process priority contradictions** | CI runner starts processes at *below normal* priority | Read the original priority. Test "restore normal" when it is below normal. Never raise beyond *above normal*. |
| 13 | **Unreliable refresh rate** | WMI reported 1 Hz on the CI VM while `EnumDisplaySettings` reported 64 Hz | Use `EnumDisplaySettings`; treat ≤ 1 Hz as unknown, assume 60 Hz, tell the user. |
| 14 | **Inconsistent VM memory data** | "2 × 1 GB modules" on an 8 GB machine | Sanity-check the module sum against total RAM. Don't give channel advice on inconsistent data. |
| 15 | **"Tiny bit of lag" on unknown hardware** | Unoptimised demo too slow on weak PCs | Start-up calibration of world size, then ray-march detail, verified by re-measuring. |
| 16 | **No Windows machine in the dev container** | Could not run the `.exe` locally | GitHub Actions Windows runner as the virtual test machine. Auto-test modes in both programs. Screenshots rendered to file and printed (compressed) into the CI log. |
| 17 | **Mono vs .NET Framework differences** | Different speed, missing Win32 APIs off Windows | Portable fallbacks only for development. Always confirm on real Windows. |
| 18 | **Progress is unknowable in advance** | The progress bar jumped | Phase-based progress (baseline 6 %, diagnosis 16 %, tests 20–82 %, pacing 85 %, validation 90 %). |
| 19 | **Source spec encoding** | The original MD was UTF-16 | Normalise input documents to UTF-8 first. |
| 20 | **Connection race** | When the game was already running at start-up, the link connected before the UI subscribed to its event; the monitor never got the game's PID, so game CPU read 0 % and "background" 55 %, which triggered a pointless priority test | Reconcile state on every tick instead of relying on one-shot events (monitor PID = link PID). Prefer level-triggered state over edge-triggered events. |
| 21 | **Animation cost showed up in the overhead meter** | 25 Hz result count-up pushed the 10 s average "over budget" | 12 Hz count-up for ~1.2 s. Judge the budget over 30 s. No animation in measurement windows. |

## I.6 Windows and API gotchas

| Area | Gotcha | Do this instead |
|---|---|---|
| WMI | Slow (100–500 ms per query at first use), its work runs in `WmiPrvSE.exe` (outside your process, so your own overhead meter doesn't see it), and the repository can be corrupt on user PCs | Use native APIs first. Use WMI once at start-up, with timeouts. Never use it during measurement. |
| WMI `Win32_VideoController.AdapterRAM` | 32-bit, caps at 4 GB | Registry `HardwareInformation.qwMemorySize` or DXGI `DedicatedVideoMemory`. |
| `Win32_PhysicalMemory` | Garbage on VMs, odd on soldered LPDDR | Sanity-check against `GlobalMemoryStatusEx`. |
| OS name | `ProductName` says "Windows 10" on Windows 11 | Build ≥ 22000 → Windows 11. |
| Refresh rate | 0/1 = "hardware default" on VMs and basic drivers; WMI and `EnumDisplaySettings` can disagree (1 Hz vs 64 Hz on the CI VM) | Use `EnumDisplaySettings`; treat ≤ 1 as unknown. |
| CPU clock | `CallNtPowerInformation` `CurrentMhz` is often static | Use it for base/max/limit only. Effective clock via PDH `% Processor Performance` (slow to create). |
| Per-core load | PDH counters are slow to create and can be corrupt | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)`. |
| Per-process / per-thread CPU | `Process.GetProcesses()` + per-process handles is expensive and hits access-denied | One `NtQuerySystemInformation(SystemProcessInformation)` snapshot (the struct layout differs between 32- and 64-bit; fall back safely). |
| Process I/O | — | `GetProcessIoCounters` on a cached `PROCESS_QUERY_LIMITED_INFORMATION` handle. |
| Memory of a process | `Process.PrivateMemorySize64` refreshes a system-wide snapshot | `K32GetProcessMemoryInfo` on a cached handle. |
| Hybrid CPUs | Core type isn't in the CPU name | `GetLogicalProcessorInformationEx` → `EfficiencyClass`. |
| Power | The classic plan says "Balanced" while the Windows 11 slider limits the CPU | `PowerGetEffectiveOverlayScheme` (Best power efficiency / Balanced / Best performance). |
| Temperatures | No reliable user-mode API. WinRing0 is vulnerable (CVE-2020-14979) and flagged by Defender. | Vendor SDKs (NVML/NVAPI, ADLX, IGCL). An optional signed driver only if essential. |
| Timers | `Sleep(1)` sleeps ~15.6 ms by default | `timeBeginPeriod(1)` (per process since Windows 10 2004), plus a short spin for frame caps. |
| Frame timing | Needs ETW (admin or *Performance Log Users*) | PresentMon SDK/service in a privileged helper. The UI stays unprivileged. |
| Process priority | Inherited from the launcher, which may be below normal | Always read it first. Restore on exit. Resets when the game closes. |
| DPI | WinForms on .NET Framework has weak per-monitor DPI | Declare DPI awareness in the manifest. The product UI stack must support Per-Monitor v2. |
| Title bar | Default light title bar clashes with a dark UI | `DwmSetWindowAttribute` (dark mode 20/19, rounded corners 33) on Windows 10 20H1+ / 11. |
| SmartScreen | Unsigned downloads show "Windows protected your PC" | Code-sign (EV or Azure Trusted Signing). Build reputation. |
| Settings files | Games overwrite config on exit. Cloud sync (Steam Cloud) restores old files. Some files are checksummed or binary. | Per-game adapters: write when safe, verify after launch, keep backups, respect cloud sync. |
| Anti-cheat | Injection or memory reads can get users banned | Only files, public APIs and ETW. Get a per-game anti-cheat policy flag from the knowledge base. |
| ✏️ Frame timing source (product build, step 1) | The plan was to run PresentMon. The build environment's permission rules did not allow downloading and running that executable, and the console build is a separate ~1 MB process (the full service is a 170 MB installer). | GAPO now has its **own small ETW consumer** of the same present events PresentMon uses (`src/Gapo.Windows/FrameTiming`, real-time session, QPC timestamps, providers Microsoft-Windows-DXGI — Present start 42 / stop 43, multiplane overlay 55 / 56 — and Microsoft-Windows-D3D9 start 1 / stop 2), feeding a pure, unit-tested `PresentTracker` that produces frame time (present start to present start), CPU busy (previous present end to this present start) and time inside the present call. It needs admin or *Performance Log Users*; access denied is reported as "frame timing unavailable". Validated against the fixture game's own telemetry on the entry-level laptop: median frame time 44.15 ms by event tracing vs 44.02 ms from the game (0.3 % apart), 0 events lost. Tracing does not slow the game measurably: three alternating pairs of runs gave median frame work 42.90 ms without and 43.01 ms with tracing (+0.3 %, inside run-to-run noise). The agent with tracing on used 0.07–0.11 % CPU and 8 MB. GPU busy time (DxgKrnl events) is still to do. 🧪 **Cross-checked against PresentMon 2.6.0 (13 November 2026, once the owner allowed running it):** both tools captured the same 40-second run of the fixture game on the laptop. Median frame time 39.733 ms (PresentMon) vs 39.772 ms (GAPO), **0.10 % apart**; mean 39.754 vs 39.747 ms (0.02 %); median CPU busy 39.589 vs 39.624 ms (0.09 %); median time inside the present call 0.147 vs 0.146 ms; the game's own telemetry said 39.746 ms. PresentMon also reported about 1.0 ms of GPU busy time per frame, the number GAPO cannot produce yet. PresentMon stays a development cross-check; it is not shipped or required. |
| ✏️ Hardware inventory without WMI (product build, step 2) | The demo's scan used WMI for memory modules, graphics adapters and drive types: seconds of work, done in another process, and unavailable to NativeAOT code. | The product's scan (`src/Gapo.Windows/Hardware`) uses no WMI at all and took **55 ms** on the entry-level laptop with nothing left unread, giving the same facts the demo's WMI scan gave: memory modules from the raw SMBIOS table (`GetSystemFirmwareTable('RSMB')`, structure type 17 — size, type, speed, configured speed, locator, manufacturer; parsed by pure, unit-tested code); graphics adapters from DXGI (`CreateDXGIFactory1` → `EnumAdapters1` → `GetDesc1`, called through the COM vtable) with the 64-bit dedicated-memory value, the software-adapter flag and vendor/device ids, plus driver version and date from the display-class registry key; drive type from `IOCTL_STORAGE_QUERY_PROPERTY` on `\.\C:` opened with zero access rights (seek-penalty property → hard disk or solid-state; device property → bus and model); topology, clocks, display, power and Windows facts from the same native calls as the demo. Every probe is isolated and adds a plain sentence to the "could not be read" list when it fails. |
| 🧪 Battery charge "100 %" (product build, step 2) | `SystemInformation.PowerStatus.BatteryLifePercent` (Windows Forms) returns 1.0 when Windows reports the charge as *unknown* (255). The demo therefore showed "Laptop on AC (100%)" for a battery whose charge Windows does not know. | Read `GetSystemPowerStatus` directly and treat 255 as unknown; never print a percentage that was not reported. |
| ➕ Own CPU time (product build, step 0) | `GetProcessTimes` is charged by timer tick. A process that sleeps on the timer and wakes for microseconds (exactly what a 10 Hz agent does) is almost never running when the tick fires, so its CPU reads **0.00 %** however long it runs. The first product overhead gate "passed" with 0.000 % this way. | `QueryProcessCycleTime` ÷ the nominal clock (`~MHz` under `HKLM\HARDWARE\DESCRIPTION\System\CentralProcessor\0`). The same agent then measured 0.019 % average, 0.35 % peak. Fall back to `GetProcessTimes` only when cycle counts are unavailable, and say which source was used in the report. |
| ➕ `dotnet test` on .NET 10 (product build, step 0) | With xunit v3 / Microsoft.Testing.Platform projects the SDK refuses the old VSTest path ("Testing with VSTest target is no longer supported"). | Opt in once in `global.json`: `"test": { "runner": "Microsoft.Testing.Platform" }`, then `dotnet test --solution <file>`. No `Microsoft.NET.Test.Sdk` or `xunit.runner.visualstudio` packages. |
| ➕ "Hardware" Direct3D device on a PC without a GPU (product build, step 0) | `D3D11CreateDevice(DriverType.Hardware)` **succeeds** on the GPU-less CI runner: Windows hands back the *Microsoft Basic Render Driver* (software). "A hardware device was created" therefore does not mean "there is a GPU". | Identify the software adapter by its DXGI description: vendor `0x1414`, device `0x8C` (or the `DXGI_ADAPTER_FLAG_SOFTWARE` flag) and treat it as "no GPU" in diagnosis, exactly like the Hyper-V video adapter. |
| ➕ `File.Delete` (product build, step 0) | Deleting a file in a directory that does not exist throws `DirectoryNotFoundException` (a missing *file* is silent). The fixture's `--reset` crashed on a fresh data directory; the end-to-end rig caught it on its first run. | Create the data directory before any reset or cleanup. |

## I.7 Decisions to keep, and decisions to change

**Keep**

- The closed loop exactly as the spec describes it, with the demo's additions: A/A noise floor, identical scenes, acknowledged changes, fix hitches first, re-diagnose after each commit, stop at target, quality give-back.
- Stage-time diagnosis with a frame budget, and conditional, evidence-based wording.
- The value ordering: predicted saving ÷ (visual cost + 0.6).
- Transactional changes with atomic writes, snapshots and "Revert all". Show rolled-back changes as prominently as kept ones.
- Native, driver-free telemetry on the hot path. WMI once. Nothing heavy inside measurement windows.
- A visible self-overhead meter with a hard budget, where over budget = bug.
- The UI *structure*: one honest number, then "Show more options" with tabs; experiment cards with hypothesis → measurement → decision; the frame-budget bar; the copy rules. **Not** the demo's visual style (next list).
- CI on a real Windows machine running full end-to-end auto-tests, with screenshots, on every push.
- The sandbox game **as a permanent test fixture**. A controllable game with configurable bottlenecks is invaluable for regression-testing the engine.

**Change**

- **UI stack:** move from WinForms/GDI+ to a GPU-composited UI (WinUI 3 recommended, see Part III). Suspend all UI rendering while a game is in the foreground.
- **UI look:** drop the demo's gradients, glows and neon cyan/violet palette. The product must be **minimalistic, follow the Windows light/dark theme automatically, and use one flat background colour with no gradients or glow effects**, so it doesn't look "made with AI" (Part III.8).
- **Architecture:** split into an unprivileged UI process and a small privileged service (ETW/PresentMon, sensors). Everything talks over a local named pipe.
- **Frame timing:** PresentMon (ETW) for any game, with engine hooks only for partners.
- **Game control:** per-game adapters, a knowledge base, and built-in benchmark or scene-matching support. Handle restart-required settings.
- **Statistics:** confidence intervals or rank tests instead of a single noise-floor multiplier, lows accumulated over play time, and ABAB interleaving to cancel thermal drift.
- **Predictions:** store predicted vs measured for every experiment and learn corrections per game × hardware (Phase 5/6).
- **Runtime:** a modern .NET (8+) with NativeAOT for the service, or Rust/C++ for the service core. .NET Framework 4.6.2 was right for a zero-install demo, not for the product.
- **Code comments:** the product source must contain **no code comments** (Part III, *Engineering rules*). The demo's comments exist only to explain the prototype.

---

# Part II — The product specification (original, complete, with demo lessons marked)

*Everything below the line is the original specification, kept word for word — every section, table, list and example — with the demo's lessons inserted as marked call-outs. Nothing was removed; the only edit is the repaired §11 formula (marked ✏️).*

---

# Adaptive Gaming Performance Engine

## 1. Product concept

The proposed product should not be a conventional “FPS booster.” It should be an adaptive performance engine that:

1. Monitors the game, operating system, and hardware in real time.
2. Identifies the resource currently preventing the next frame from being produced.
3. Determines the likely cause of the bottleneck.
4. Recommends the least visually damaging intervention.
5. Tests one change at a time.
6. Measures the result.
7. Keeps successful changes and automatically reverts unsuccessful ones.
8. Doesn’t use up a drastic amount of CPU or memory usage that will backfire on the performance increase.

> ✏️ **Changed** — point 8 now has hard numbers. The optimiser's own footprint is a product requirement with a budget, not a nice-to-have:
> - **Idle / dashboard:** ≤ 0.5 % of total CPU, ≤ 80 MB private memory.
> - **While measuring:** ≤ 1 % of total CPU averaged over the run, and effectively zero inside a measurement window (no scans, no WMI, 1 Hz UI).
> - **Never** load a kernel driver, inject into the game, or run a heavy query while a game is being measured.
> - The UI must **show its own CPU and RAM use at all times**, and exceeding the budget is a **bug** (the demo turns its overhead pill amber and writes "over budget").
> In the demo, the whole optimiser (UI + monitor + engine) measured on a 2-vCPU Windows machine is reported in Appendix B.


A useful guiding question is:

> What is preventing this game from reaching its target frame time, and what is the least destructive way to fix it?

> ➕ **Added** — a second guiding question from the demo: **"…and how will we prove it?"** Every claim the product makes ("+80 % FPS", "anti-aliasing was the problem") must be backed by a measurement taken on the same scene, with a known noise floor. If it can't be measured, it is shown as a recommendation, never as a result.


Possible product names:

- Game Adaptive Performance Optimizer (GAPO).
- Adaptive Gaming Runtime.

The system sits conceptually between:

```text
Game → Operating system → Hardware
```

Its main advantage is causal diagnosis rather than simply displaying hardware utilisation.

> 🧪 **Demo lesson** — the demo implemented this whole concept end to end (Game → OS → Hardware): a sandbox game exposes frame telemetry, the optimiser reads it plus Windows counters, diagnoses the limiting stage, predicts the effect of one change, applies it through the game's settings file, measures on a repeatable benchmark flight, and keeps or rolls back. On a 2-thread Windows CI machine with no GPU it took the unoptimised sandbox from 35 FPS to a steady 63 FPS (locked to the 64 Hz display), cut stutters from ~48 per minute to 0, and needed 31–38 seconds (Appendix B). The causal-diagnosis angle is what made the result explainable — every kept change has a "why" and a measured "how much".


## 2. Core principles

### Utilisation is not the same as a bottleneck

High utilisation does not automatically mean that a component is the cause of poor performance.

For example:

```text
Overall CPU usage: 62%

Core 0: 99%
Core 1: 94%
Core 2: 17%
Core 3: 12%
```

The game may still be CPU-limited if its main thread is overloaded, even though overall CPU usage appears moderate.

Similarly, low GPU utilisation does not prove that the GPU is healthy. The game may instead be limited by:

- A CPU main thread.
- Memory pressure.
- Storage or asset streaming.
- Shader compilation.
- Thread synchronisation.
- A frame-rate cap.
- VSync or display configuration.
- Network latency.

The program should therefore analyse frame times and dependencies rather than relying on raw utilisation percentages.

> 🧪 **Demo lesson** — the demo reproduced this. On the 2-thread Windows CI machine the per-core load was **58 % and 53 %** — no core looked "maxed out" — yet the frame was limited by a single render thread (rendering took 15 ms of a 20 ms frame, all on one thread). Windows moves a busy thread between cores, so **per-core load can hide a one-thread limit too**; the reliable signal is the game's **per-thread CPU time** (one `SystemProcessInformation` snapshot gives it without opening handles) combined with the engine's stage times. GAPO words the evidence conditionally (e.g. *"Total CPU use is X %, but the busiest core runs at Y %"* only when Y − X ≥ 25 points) so it never states a contradiction the data doesn't show.

> ➕ **Added (product build, step 1)** — the product's monitor reproduces this from outside the game with two cheap calls. On the entry-level laptop, with the fixture game on one render thread: **machine load 42 %, busiest processor 55 %, yet the game used 0.96 processors and its busiest thread 96 % of one**. The pieces: per-processor load from `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` every second; the game's own CPU from cycle counts on a cached handle; and the busiest thread from two `SystemProcessInformation` snapshots two seconds apart, taken once every ten seconds (thread times are tick-based, which is accurate for a thread that is actually busy). Background load is what remains after subtracting the game and GAPO itself (17.9 % in that run — the build tools were running). The agent doing all of this plus ETW frame timing measured 0.14 % CPU and 9.6 MB.

> ➕ **Added** — **Frame time vs busy time.** Frame time (present-to-present) is what the player feels; *busy time* (the work the frame needed, excluding waiting for a cap/VSync) is what the optimiser must judge experiments on. With a frame cap active, FPS stops moving while the work shrinks — the demo evaluates every experiment on the **median busy time** and only uses FPS for reporting. In a commercial product, PresentMon's *CPUBusy / GPUBusy* metrics are the equivalent.


### Minecraft example

If Minecraft has a workload that is fundamentally limited by a single main thread, Windows cannot automatically redistribute that work across several other CPU cores. The game engine itself must be designed to parallelise the workload.

The optimiser can still help by reducing settings that affect the main thread, such as:

- Simulation distance.
- Entity distance.
- Chunk-generation workload.
- Render distance.
- Physics.
- Particles.
- Background applications.
- Process priority, where appropriate.

It should explain the limitation clearly:

> This is a main-thread limitation. External settings cannot make the game simulation multithreaded. Reducing simulation distance is likely to provide the largest available improvement.

> 🧪 **Demo lesson** — the sandbox has a deliberately naive all-pairs creature simulation on the main thread. The optimiser correctly labelled it *"C · engine limitation, mitigated by B · game settings"*, printed this exact explanation, and found **simulation distance** to be the largest simulation lever: on the 4-thread test machine *Unlimited → Far* was kept with **+35–40 % less frame work** for a visual cost of 1/10 (the AI cost is O(n²), so simulating only nearby creatures cuts it disproportionately). This wording is now used verbatim in the product's diagnosis card.


## 3. Bottleneck categories

The system should separate problems into three groups.

### A. Problems that can often be fixed externally

Examples include:

- Process priority.
- CPU affinity, used cautiously.
- Windows power mode.
- GPU selection.
- Windows graphics settings.
- Background applications.
- Overlay conflicts.
- Shader-cache conditions.
- Resolution and graphics settings.
- FPS caps.
- VRR configuration.
- Windows Game Mode.
- Hardware-accelerated GPU scheduling, where appropriate.
- RAM and pagefile pressure.

> 🧪 **Demo lesson** — external (category A) changes were the *least* valuable in the demo. Process priority only helps when something else competes for the CPU, so the demo only tests it when background apps use ≥ 15 % CPU — otherwise it records *"not tested: nothing to win"*. Power plan, Game Mode and background recording are **detected and recommended with a button to the right Windows Settings page**, never silently changed. Changing system-wide state from a consumer app needs a transaction log and guaranteed restore (see Part III).


### B. Problems that can be addressed through game settings

Examples include:

- Render distance.
- Simulation distance.
- Shadows.
- Reflections.
- Crowd density.
- Physics.
- Foliage.
- View distance.
- Texture quality.
- Ray tracing.
- Volumetrics.
- Effects.
- Resolution.
- Upscaling.
- Frame generation.

### C. Fundamental game-engine limitations

Examples include:

- Single-threaded simulation.
- Poor thread synchronisation.
- Inefficient engine architecture.
- Inefficient asset streaming.
- Shader compilation.
- Excessive draw calls.
- Inefficient AI.
- Poor memory management.

The product should detect these issues but must not pretend to solve them externally. It should identify the limitation and recommend the best available mitigation.

> ➕ **Added** — **D. Transient conditions** (new category). World streaming right after start-up, shader compilation, a Windows Update burst, an antivirus scan, the first minute after a driver install. They look like bottlenecks but disappear on their own. The product must **detect them and wait**, not "fix" them — the demo refuses to measure while the game reports warm-up frames and tells the user why (spec §13's "avoid judging performance during initial compilation" generalised). Also: *"random hitches would pollute every later measurement"* — fix or wait out sporadic hitch sources **before** running experiments.


## 4. Telemetry and hardware analysis

### CPU

| Factor | Impact | What the optimiser can do |
|---|---|---|
| CPU architecture and IPC | 🔴 Very high | Detect CPU limitation |
| CPU single-core performance | 🔴 Very high | Identify main-thread bottlenecks |
| CPU multi-core performance | 🟠 High | Identify parallel-workload limitations |
| Core count | 🟠 High | Detect whether the game scales across cores |
| Thread count | 🟡 Medium | Detect utilisation and scaling |
| CPU clock frequency | 🔴 High | Detect frequency reductions |
| CPU boost behaviour | 🔴 High | Detect inadequate boosting |
| CPU temperature | 🔴 High | Detect thermal throttling |
| CPU power limit | 🔴 High | Detect power throttling |
| CPU voltage | 🟡 Medium | Provide diagnostic information |
| CPU cache | 🟠 High | Record hardware characteristics |
| L1 cache | 🟡/🟠 | Provide diagnostic information |
| L2 cache | 🟠 | Provide diagnostic information |
| L3 cache | 🟠/🔴 | Important for some games |
| CPU memory latency | 🟠 High | Detect memory bottlenecks |
| CPU memory bandwidth | 🟠 High | Detect bandwidth-sensitive workloads |
| SMT/Hyper-Threading | 🟡 | Assess game-specific behaviour |
| P/E-core architecture | 🔴 High on hybrid CPUs | Detect scheduling problems |
| Thread migration | 🟠 | Detect poor scheduling |
| Core parking | 🟡/🟠 | Diagnose possible performance limits |
| CPU affinity | 🟡/🟠 | Offer optional intervention |
| Scheduler behaviour | 🟠 | Diagnose scheduling issues |
| Background CPU processes | 🔴 High when competing | Suspend, close, or recommend action |
| Interrupt/DPC load | 🟠 | Detect driver-related issues |
| CPU thermal throttling | 🔴 | Recommend cooling or power changes |

The system should record per-core behaviour, main-thread time, worker-thread time, CPU wait time, clock speed, temperature, and power limits.

> ➕ **Added** — **What Windows actually exposes (learned while building the demo).** Plan the telemetry layer around this, not around the wish list above:
>
> | Signal | Without admin / driver | How (fast path) | Notes |
> |---|---|---|---|
> | Per-core load | ✅ | `NtQuerySystemInformation(SystemProcessorPerformanceInformation)` | One call, microseconds. Avoid PDH counters for this. |
> | Game's per-thread CPU time | ✅ | One `NtQuerySystemInformation(SystemProcessInformation)` snapshot | Gives every process *and* every thread without opening handles. ~1–3 ms; never inside a measurement window. |
> | Cores, SMT, hybrid P/E cores, L2/L3 | ✅ | `GetLogicalProcessorInformationEx` (`EfficiencyClass`) | Reliable on Windows 10+. |
> | Base / max clock, clock limit | ✅ | `CallNtPowerInformation(ProcessorInformation)` | `CurrentMhz` is often frozen at base on modern Windows; use it only for base/max/limit. |
> | Effective clock | ✅ (slow) | PDH `% Processor Performance` × base | Creating PDH counters is slow and sometimes corrupt on user PCs — create once, off the hot path. |
> | CPU temperature, package power, voltages | ❌ | Needs a kernel driver or vendor SDK | `MSAcpi_ThermalZoneTemperature` needs admin and is usually wrong or absent. |
> | Interrupt/DPC load | ⚠️ | ETW kernel providers | Requires admin or the *Performance Log Users* group. |
> | Game frame times | ⚠️ | ETW via PresentMon (DXGI/D3D/Vulkan present events) | Admin or *Performance Log Users*; no injection needed. The demo got them from the game itself (engine hook). |


### GPU

The GPU subsystem should monitor:

- GPU architecture.
- Shader performance.
- Compute units.
- Ray-tracing hardware.
- AI or tensor hardware.
- GPU clock and effective clock.
- GPU utilisation.
- GPU power and voltage.
- GPU temperature and hotspot temperature.
- Thermal and power throttling.
- Fan speed.
- Driver version.
- Driver overhead.
- GPU scheduling.
- Workload distribution.
- VRAM capacity, allocation, and resident usage.
- VRAM bandwidth and frequency.
- Texture streaming.
- PCIe transfers.
- Shared system memory.

> ➕ **Added** — **GPU telemetry reality:**
> - **VRAM size:** WMI `Win32_VideoController.AdapterRAM` is a 32-bit field and **caps at 4 GB** — the demo reads `HardwareInformation.qwMemorySize` from the display-adapter registry key instead (DXGI `DedicatedVideoMemory` is the other correct source).
> - **VRAM usage per process / budget:** `IDXGIAdapter3::QueryVideoMemoryInfo` (budget + current usage). Needed before any "textures are the problem" claim.
> - **GPU utilisation:** PDH `GPU Engine` counters work without admin but enumerate hundreds of instances — expensive; sample at ≤ 1 Hz and only for the game's PID. D3DKMT statistics are the low-overhead alternative.
> - **Clocks, temperature, hotspot, power, fan, throttle reasons:** vendor SDKs only — **NVML/NVAPI** (NVIDIA), **ADLX** (AMD), **Intel IGCL**. Plan a per-vendor adapter from day one.
> - **Virtual / basic adapters** (Hyper-V, Basic Display, remote sessions) must be recognised and never treated as "the GPU". The CI test machine reports *Microsoft Hyper-V Video*, which the demo labels as a virtual adapter.


Relevant game settings include:

- Resolution.
- Resolution scale.
- Texture quality.
- Texture filtering.
- Shadow quality and resolution.
- Contact shadows.
- Ambient occlusion.
- Screen-space reflections.
- Ray tracing and path tracing.
- Global illumination.
- Volumetric lighting and fog.
- Clouds.
- Water quality.
- Reflections.
- Particles.
- Foliage.
- Post-processing.
- Anti-aliasing.
- Anisotropic filtering.
- Depth of field.
- Motion blur.
- Chromatic aberration.
- Bloom.
- Tessellation.
- Geometry, mesh, and terrain quality.

### GPU bottleneck detection

The product should compare CPU and GPU frame times rather than relying only on utilisation.

Example:

```text
GPU frame time: 14.8 ms
CPU frame time:  6.1 ms
GPU busy time:  14.2 ms
CPU busy time:   5.7 ms
```

Diagnosis:

```text
GPU-bound
```

Likely interventions:

- Reduce resolution.
- Reduce shadows.
- Reduce volumetrics.
- Disable or reduce ray tracing.
- Adjust effects and reflections.

By contrast:

```text
CPU frame time: 19 ms
GPU frame time:  7 ms
```

Diagnosis:

```text
CPU-bound
```

Likely interventions:

- Reduce simulation distance.
- Reduce crowd or entity density.
- Reduce view distance.
- Close competing background applications.

Reducing texture quality or resolution would probably have little effect in this case.

> 🧪 **Demo lesson** — the demo's diagnosis compares **stage times**, not utilisation: simulation (main thread) vs rendering vs present vs streaming/saving, against the frame budget. The same structure maps to CPU frame time vs GPU frame time in a real game (PresentMon: `CPUBusy`, `GPUBusy`, `GPUWait`). It also caught a case the spec's two examples don't cover: **rendering bound *because* it runs on one thread** — the fix was a parallelism setting (render threads = physical cores), not lower quality. On a 4-thread machine that single change gave **+40–49 %** with zero visual cost; on the 1-core/2-thread CI VM, where SMT was the only extra parallelism, it gave **+4–12 %** — the same rule, different hardware, correctly different payoff.


### RAM

The system should monitor:

- Total RAM.
- Available RAM.
- Committed memory.
- Memory pressure.
- RAM frequency and bandwidth.
- Timings and latency.
- Single- or dual-channel operation.
- DIMM and rank configuration.
- DDR generation.
- XMP/EXPO status.
- Memory-controller behaviour.
- Page faults.
- Working-set size.
- Standby memory.
- Memory compression.
- Pagefile activity.
- Fragmentation.
- Game memory consumption.
- Background application memory.

> ➕ **Added** — **RAM, practically:** `GlobalMemoryStatusEx` (load, available, commit) is free to call every second. Module count, speed and type come from WMI `Win32_PhysicalMemory` — slow, so read once at start-up. **Single-channel detection is valuable on low-end PCs**: one DIMM + integrated graphics halves the iGPU's memory bandwidth. The demo reports it as *"Memory runs in single-channel mode — a second matching module can raise graphics performance a lot (hardware upgrade — GAPO can't change this)"*. Soldered LPDDR reports odd module counts — don't flag it.


Example of a healthy configuration:

```text
RAM:          32 GB
Usage:        19 GB
Bandwidth:    High
Page faults:  Low
Swap:         None

Diagnosis:
RAM is not limiting performance.
```

Example of memory pressure:

```text
RAM:          16 GB
Usage:        15.6 GB
Commit:       98%
Pagefile:     High activity
Game:         Frequent asset stalls

Diagnosis:
System memory pressure is probably causing stutter.
```

The appropriate response may be to close Chrome, Discord, or other applications rather than reduce graphics quality.

> 🧪 **Demo lesson** — the demo groups background processes by application (Chrome's 30 processes become one "Google Chrome" row), measures CPU over two snapshots taken *between* measurement windows, and keeps a **never-touch list** (Defender, system processes, audio, DWM). Closing apps is always the user's decision; the product only names the apps with evidence (CPU %, RAM).


### VRAM

VRAM should be treated as a separate subsystem. Monitor:

- VRAM capacity.
- Allocated and resident usage.
- Texture memory.
- Render-target memory.
- Streaming activity.
- Usage spikes.
- PCIe transfers.
- Shared GPU memory.
- Memory bandwidth and clock.
- Memory throttling.

Example:

```text
VRAM:                 11.8 / 12 GB allocated
GPU utilisation:      93%
Texture streaming:    Frequent
Symptom:              Stutter when turning the camera
```

Potential interventions:

```text
Texture quality:      Ultra → High
Texture streaming:    Unlimited → High
Ray tracing:           On → Off
```

If the GPU has 4 GB of VRAM but is using only 2.7 GB, texture quality is unlikely to be the current problem.

> 🧪 **Demo lesson** — the demo includes a texture-quality setting that only changes memory use. Because memory was not under pressure, GAPO **deliberately left it alone** and said so under "Considered but not changed". Users trusted the result more when they could see what was *not* touched and why.


### Storage

Storage can affect stutter and asset streaming without significantly changing average FPS.

Monitor:

- SSD or HDD.
- NVMe or SATA.
- PCIe generation.
- Sequential read speed.
- Random-read performance.
- IOPS.
- Latency.
- Drive temperature.
- Thermal throttling.
- Free space.
- Filesystem.
- Fragmentation.
- Queue depth.
- Disk utilisation.
- Asset streaming.
- Shader-cache reads.
- Game installation location.
- Pagefile location.
- Simultaneous disk activity.

Important use cases include:

- Open-world games.
- Texture streaming.
- Shader caches.
- Unreal Engine games.
- DirectStorage workloads.
- Games with frequent asset loading.

The system should be able to classify a problem as:

```text
Storage-bound / asset-streaming-bound
```

rather than incorrectly labelling it as CPU- or GPU-bound.

> 🧪 **Demo lesson** — the sandbox saves the world every 10 s on its main thread with write-through I/O, causing a periodic ~60–150 ms freeze (longer on HDDs). GAPO classified it as a **storage hitch**, not a CPU/GPU problem, changed the autosave interval (zero visual cost) **before** any other experiment, and verified afterwards that no save freezes occurred. Practical notes:
> - Periodic hitches need an observation window **longer than their period** (the demo watches ≥ 14 s before judging).
> - Correlate spikes with the game's disk writes (`GetProcessIoCounters` on one cached handle — cheap) rather than guessing.
> - Drive type/bus: `MSFT_PhysicalDisk` (`MediaType` 3=HDD, 4=SSD; `BusType` 17=NVMe) joined to `MSFT_Partition` by disk number. WMI again — once at start-up.


### PCIe and motherboard

Monitor PCIe information such as:

- PCIe generation.
- Lane width.
- GPU link width.
- Actual negotiated link.
- PCIe bandwidth.
- GPU bus saturation.
- NVMe bus usage.
- Lane-sharing conflicts.
- Whether the GPU is operating at x16, x8, or x4.
- PCIe-generation throttling.

Example:

```text
Expected: PCIe 4.0 x16
Actual:   PCIe 4.0 x4
```

This should generate an investigation warning.

Motherboard-related checks may include:

- Chipset.
- BIOS version.
- PCIe configuration.
- Resizable BAR.
- Above 4G decoding.
- Memory configuration.
- CPU power limits.
- VRM behaviour.
- BIOS power management.
- Firmware problems.
- PCIe bifurcation.
- Lane sharing.
- Smart Access Memory.
- USB-controller load.

The program should not automatically modify BIOS settings. It should:

```text
Detect → Recommend → Optionally guide
```

> ➕ **Added** — **PCIe link and motherboard facts without a driver:** current/max PCIe link speed and width are exposed per device through SetupAPI device properties (`DEVPKEY_PciDevice_CurrentLinkSpeed/Width`, `…MaxLinkSpeed/Width`) — enough for the "expected x16, running x4" warning. BIOS version/vendor: `Win32_BIOS`; board: `Win32_BaseBoard`. Resizable BAR state is vendor-SDK territory (NVAPI/ADLX). Keep it **Detect → Recommend → Optionally guide**, as written.


## 5. Cooling and power

### Cooling

Monitor:

- CPU temperature.
- CPU package power.
- CPU clock and effective clock.
- Thermal limits.
- Power limits.
- GPU temperature.
- GPU hotspot and memory temperature.
- GPU power and clock.
- Fan speed.
- Ambient temperature, where available.
- Case-fan speed and curves.
- Laptop thermal mode.

Example:

```text
At game start:
CPU: 5.0 GHz
Temperature: 75°C

After ten minutes:
CPU: 4.2 GHz
Temperature: 95°C
FPS: 110 → 83
```

Diagnosis:

```text
Performance degradation strongly correlates with thermal throttling.
```

The recommended solution should be improved cooling, a different fan profile, lower CPU power, or a laptop performance mode—not lower texture quality.

> ➕ **Added** — **Getting temperatures safely is a product decision, not a detail.** The WinRing0 kernel driver used by many hardware-monitoring tools has a known privilege-escalation vulnerability (CVE-2020-14979) and is flagged by Microsoft Defender — shipping it would get the product quarantined and would open a security hole on users' PCs. Use, in order of preference: GPU vendor SDKs (NVML/NVAPI, ADLX, IGCL), Windows-provided data, and — only if CPU sensors are essential — a properly **signed, maintained** driver from a vendor with a security track record, installed as an optional component. The demo reads no temperatures at all and says so; it only reports the clock limit Windows publishes (`MhzLimit` below max → "power or thermal limit").


### Power

Detect:

- PSU capability, where discoverable.
- GPU power limit.
- CPU power limit.
- AC or battery operation.
- Windows power plan.
- GPU power mode.
- CPU boost policy.
- Thermal power limits.
- GPU or CPU undervolting.
- Power throttling.

Example:

```text
GPU clock:   Normally 2,800 MHz
During game: 2,050 MHz
Power:       99% of power limit
Temperature: 63°C
```

This is more likely to indicate a power limit than a thermal problem.

> 🧪 **Demo lesson** — power-state detection that worked without admin: battery/AC and charge (`GetSystemPowerStatus`), the classic power plan (`PowerGetActiveScheme` + `PowerReadFriendlyName`) and — important on Windows 11 — the **power-mode slider overlay** (`PowerGetEffectiveOverlayScheme`: Best power efficiency / Balanced / Best performance). The classic plan usually reads "Balanced" while the slider is what actually limits the CPU. The demo recommends (with an *Open power settings* button) instead of changing either.


## 6. Display, frame pacing, and latency

### Display configuration

Monitor:

- Refresh rate.
- Resolution.
- Variable refresh rate.
- FreeSync, G-SYNC, and Adaptive-Sync.
- HDR.
- Response time.
- VSync.
- FPS cap.
- Display latency.
- Multi-monitor configuration.
- Primary monitor.
- HDMI or DisplayPort mode.
- Monitor bandwidth.

A 144 Hz display does not necessarily require unstable performance above 110 FPS. A stable 90 FPS with VRR may provide a better experience than fluctuating performance between 110 and 145 FPS.

> 🧪 **Demo lesson** — refresh rate is unreliable on virtual machines, remote sessions and basic display drivers: the value 0 or 1 means "hardware default". On the Windows CI machine WMI (`Win32_VideoController.CurrentRefreshRate`) reported **1 Hz** while `EnumDisplaySettings` reported **64 Hz** for the same display. The demo uses `EnumDisplaySettings`, treats ≤ 1 as unknown, assumes 60 Hz and says so in the UI — and it correctly used the 64 Hz value as the frame budget and cap on that machine. On fixed-refresh displays a cap must be the refresh rate or an **even divisor** of it (60 → 60 or 30); a 45 FPS cap on a 60 Hz panel judders. VRR detection needs DXGI/DisplayConfig queries plus the Windows "Variable refresh rate" setting — plan for it in the display adapter.


### Frame pacing

Average FPS can hide severe stutter.

Example:

```text
Average FPS: 120

Frame times:
8 ms
8 ms
9 ms
8 ms
42 ms  ← severe hitch
8 ms
8 ms
```

The optimiser should therefore measure:

- Average FPS.
- 1% low FPS.
- 0.1% low FPS.
- Frame-time variance.
- Frame-time spikes.
- Frame-time consistency.
- Dropped frames.
- CPU wait time.
- GPU wait time.
- Present latency.
- Display timing.

A configuration that increases average FPS but worsens 1% lows should be rejected.

> ✏️ **Changed** — this rule is kept, with a statistical caveat learned in the demo: a 1 % low needs **≥ 1,000 frames** and a 0.1 % low **≥ 10,000 frames** to be stable. A 3-second window at 50 FPS has 150 frames — its "1 % low" is the average of two frames. The demo therefore (a) judges experiments on median busy time and the 95th-percentile frame time, (b) rejects a change only when the 1 % low drops by more than max(12 %, 3× the noise floor), and (c) reports 1 %/0.1 % lows over longer combined windows. A commercial product should accumulate frames across play time for lows, and use short windows only for the throughput decision.

> 🧪 **Demo lesson** — the frame cap was the demo's best smoothness win. Once work per frame fitted comfortably in the 16.7 ms budget, capping to the display refresh cut frame-time variation from ~2 ms to ~0.3 ms and freed CPU time (less heat and fan noise). The cap is applied **last**, after all throughput experiments, because a cap hides throughput gains from FPS.


### Input latency

The product should analyse the full path:

```text
Input
  ↓
CPU
  ↓
Game simulation
  ↓
Rendering
  ↓
GPU
  ↓
Present
  ↓
Display
```

It should distinguish:

- High FPS with poor latency.
- Lower FPS with excellent latency.
- Rendering delay.
- Queueing delay.
- Display delay.
- Network delay.

Potential recommendations include:

- FPS caps.
- Latency-reduction modes.
- VSync changes.
- VRR configuration.
- Render-queue changes.
- Graphics settings.
- Display configuration.

> ➕ **Added** — input latency was **not** measured in the demo — it needs PresentMon's latency metrics, NVIDIA Reflex / AMD Anti-Lag markers, or Windows' PC-latency ETW events. Treat it as its own phase; don't fake it from FPS.


## 7. Network and background software

### Network

Network conditions usually do not determine rendered FPS, but they can strongly affect perceived responsiveness.

Monitor:

- Ping.
- Jitter.
- Packet loss.
- Upload and download activity.
- Wi-Fi signal strength.
- Wi-Fi channel congestion.
- Ethernet or Wi-Fi connection.
- Bufferbloat.
- Server distance.
- Network-adapter utilisation.

The product should distinguish between:

```text
FPS problem
```

and:

```text
Network problem
```

For example:

> FPS is stable at 144, but the game feels delayed because ping increased from 25 ms to 180 ms.

> ➕ **Added** — network measurement belongs in its own lightweight module (per-game server endpoints, ICMP/UDP probes at a low rate, Wi-Fi signal via WLAN API). The demo does not include it; keep the FPS-problem vs network-problem split exactly as written.


### Background applications

Potential sources of resource contention include:

- Browsers.
- Discord.
- Steam.
- OBS and recording software.
- Antivirus.
- Windows Update.
- OneDrive.
- RGB utilities.
- Motherboard utilities.
- Game launchers.
- Hardware-monitoring software.
- Cloud synchronisation.
- Virtual machines.
- Docker or WSL.
- Indexing.
- Video playback.
- Other games.

The system should calculate a resource-contention score for each process.

Example:

```text
Game:              72%
Chrome:            11%
OBS:                8%
Discord:            3%
Windows services:   2%
Other:              4%
```

It should only recommend closing or suspending a process when telemetry shows that it is actually affecting performance.

> 🧪 **Demo lesson** — the per-process contention score is computed from **one kernel snapshot** (`SystemProcessInformation`) taken twice, 2 s apart, *between* measurement windows — no per-process handles, no WMI, no PDH. That keeps the scan itself from becoming background load. The rule "only recommend when telemetry shows impact" is implemented as: background CPU ≥ 10 % **and** the closable apps account for ≥ 4 %.


## 8. Drivers and game-engine knowledge

### Drivers

Monitor:

- GPU-driver version.
- Audio driver.
- Chipset driver.
- Network driver.
- Storage driver.
- Driver crashes.
- Driver-reset events.
- DPC latency.
- Device errors.
- Driver conflicts.

The system should not assume that the newest driver is always the best driver. A useful database could record:

```text
Game × GPU × Driver × Settings × Performance
```

Relevant metrics include:

- Average FPS.
- 1% lows.
- Stutter.
- Crashes.
- Frame-time variance.
- Latency.

### Game-engine database

For every game, store:

- Game ID.
- Engine and version.
- Graphics API.
- CPU scaling.
- GPU scaling.
- RAM sensitivity.
- VRAM sensitivity.
- Storage sensitivity.
- Shader-compilation behaviour.
- Ray-tracing sensitivity.
- Simulation sensitivity.
- Known setting behaviour.
- Known engine limitations.

Example:

```text
Game A:
CPU-heavy
GPU-light
Main-thread limited
Simulation distance highly sensitive
Resolution has relatively low impact

Game B:
GPU-heavy
CPU-light
Resolution highly sensitive
Ray tracing extremely expensive
Textures mainly affect VRAM

Game C:
Shader-compilation stutter
High storage activity
VRAM-sensitive
```

The database allows the optimiser to use known behaviour instead of rediscovering it every time.

> 🧪 **Demo lesson** — the demo's `KnowledgeBase` is a working miniature of this database for one game: each setting has a ladder of levels, a visual-loss score, which stage it costs (main thread / render / memory / storage), and a formula that turns *measured stage times* into a *predicted saving*. Predictions ranked the candidates correctly and were usually within ±30 %, but not always: on Windows, anti-aliasing SSAA→FXAA was predicted −10.4 ms and measured −9.6 ms, physics substeps −1.2 ms vs −1.3 ms, SMT render threads −0.6 ms vs −0.4 ms; on the 4-thread test machine render threads 1→4 were predicted −43.7 ms and measured −35.3 ms, and simulation distance −4.8 ms vs −6.6 ms. **Store predicted vs measured for every experiment** — that error is the training data for Phase 6.


## 9. Settings cost model

Each game setting should have an estimated effect on:

- CPU cost.
- GPU cost.
- VRAM cost.
- RAM cost.
- Latency.
- Visual quality.

| Setting | CPU | GPU | VRAM | Visual loss |
|---|---|---|---|---|
| Resolution | 🟢 | 🔴🔴🔴 | 🟡 | 🔴 |
| Textures | 🟢 | 🟡 | 🔴🔴🔴 | 🟠 |
| Shadows | 🟡 | 🔴🔴 | 🟡 | 🟠 |
| Draw distance | 🔴🔴 | 🟠 | 🟡 | 🟠 |
| Ray tracing | 🟢 | 🔴🔴🔴 | 🟠 | 🟠 |
| Crowd density | 🔴🔴🔴 | 🟡 | 🟢 | 🟠 |
| Physics | 🔴🔴🔴 | 🟢 | 🟢 | 🟠 |
| Ambient occlusion | 🟢 | 🟠 | 🟢 | 🟡 |
| Texture filtering | 🟢 | 🟡 | 🟡 | 🟢 |
| Volumetrics | 🟢 | 🔴🔴 | 🟡 | 🟠 |

These values should not be treated as universal. They should be learned for the specific:

- Game.
- Engine.
- GPU.
- CPU.
- Scene.
- Resolution.
- Settings combination.


> 🧪 **Demo lesson** — measured costs in the demo sandbox (unoptimised Ultra, one render thread) confirm the table's shape: anti-aliasing (4× SSAA) multiplied every pixel pass (−46 to −47 % frame work when switched to FXAA on Windows), "Ultra" shadows were ray-marched per pixel yet looked the same as the baked "High" shadows, volumetric fog and procedural clouds were mid-cost, and particles were cheap (< 1 ms) — so the optimiser correctly never touched particles. Costs changed after each kept change (e.g. once rendering was multithreaded, simulation became the bottleneck) — **re-diagnose after every commit**.

## 10. Closed-loop optimisation

The optimiser should run controlled experiments instead of changing many settings at once.

Example:

```text
Baseline:
87 FPS
11.5 ms GPU time

Test:
Shadows: Ultra → High

Result:
94 FPS
10.6 ms GPU time
Performance improvement: 8%
```

If the visual-quality loss is acceptable, keep the change. If not, revert it.

> ➕ **Added** — **How the demo made single-change experiments trustworthy** (all of these are now requirements):
> 1. **A/A baseline** — measure the unchanged configuration twice; the difference × 2.5 (clamped 2.5–10 %) is the *noise floor*. A change must beat it to be kept.
> 2. **Identical scene** — the game flies a deterministic benchmark path restarted at the start of every window, so every measurement sees the same frames. Real games: use built-in benchmarks, replay files, or scene fingerprinting.
> 3. **Acknowledged apply** — the game reports the settings revision it is running with; the optimiser waits for that acknowledgement, then discards 650 ms of settling frames before measuring.
> 4. **Right metric for the hypothesis** — throughput changes on median busy time; pacing changes on frame-time spread and 95th/99th percentiles; hitch fixes on hitch count over a window longer than the hitch period.
> 5. **Fix sporadic hitches first**, then test throughput changes.
> 6. **Re-diagnose after each commit** — the bottleneck moves.
> 7. **Stop when the goal is met** (Balanced/Quality) instead of spending visual quality for nothing, then try a **quality give-back** pass if there is lots of headroom.
> One experiment took ~3.5–4 s; a full Windows run with 5–7 experiments took 31–38 s. On real games with restart-required settings this becomes minutes — prioritise by predicted value per visual cost and stop early.

> ➕ **Added (product build, step 3)** — the measurement harness is now pure, unit-tested code in `src/Gapo.Core/Measurement`, fed with frames from any source (the engine hook today, ETW presents for games without one):
> - `FrameStatistics` — median busy time, median / 95th / 99th percentile frame time, spread, stutters (same thresholds as the demo). **1 % and 0.1 % lows are simply not reported below 1,000 and 10,000 frames**, so a short window can never produce a low that is really one frame.
> - `MeasurementWindow` — discards 650 ms of settling frames, keeps only frames of the expected settings revision that are not warm-up frames, completes after 2.6 s and 45 frames, and after 7 s either completes flagged as *cut short* (8 or more usable frames) or **fails** with "Too few frames were measured" — never "no gain".
> - `NoiseFloor` — 2.5 × the A/A difference, clamped to 2.5–10 %, plus the rule learned in step 1: when the A/A pair differs by more than 4 % (the point where the clamp would be reached) the floor *cannot be trusted* and the pair is measured again, up to three attempts.
> - `ChangeComparison` — a gain inside the floor proves nothing; a gain under 3× the floor is *borderline* and asks for confirmation by interleaved A/B pairs, which must agree in direction and beat the floor on average (the answer to Appendix B's note that the pacing decision flipped between runs).
> - `WarmupDetector` — warm when 30–90 recent frames carry no warm-up mark and at most two spikes; works from frame times alone when the game gives no marks.
>
> First run against the fixture on the entry-level laptop: waited 7.4 s for warm-up, two windows of 62 and 65 frames on the restarted benchmark flight at 40.58 and 40.22 ms frame work (0.9 % apart → noise floor ±2.5 %, first attempt), and the agent used **0.09 % CPU inside the windows** (budget 0.3 %); process snapshots are suspended while a window is open.


The full process should be:

```text
Observe
   ↓
Diagnose
   ↓
Predict
   ↓
Change one setting
   ↓
Measure
   ↓
Compare
   ↓
Keep or revert
   ↓
Learn
```

### Automatic rollback

Every change should be transactional:

```text
Snapshot
   ↓
Apply change
   ↓
Play or benchmark
   ↓
Measure result
   ↓
Improved? ── Yes → Commit
        └── No  → Roll back
```

> 🧪 **Demo lesson** — transactions were implemented as: snapshot the settings object → write the new settings atomically (temp file + swap, never a half-written file) → wait for the game's acknowledgement → measure → either keep, or write the snapshot back and wait for acknowledgement again. "Revert all" restores the original snapshot and the original process priority. Changes to system state (priority) are reverted automatically when the game exits.

> ➕ **Added (product build, steps 5–6)** — the product's closed loop now runs end to end on real Windows against the fixture game, and every push is gated on it:
> - **Engine** (`src/Gapo.Core/Optimisation`, pure code, 17 unit tests against a simulated game): baseline A/A with re-measurement when the pair cannot be trusted → diagnosis → hitch fix first → candidates ordered by predicted saving ÷ (visual loss + 0.6) → one change at a time, kept or rolled back (noise floor; a gain under 3× the floor is confirmed by a second window; rolled back when the slowest frames get more than 12 % slower; goal weights) → stop at target → quality give-back with the predictive check → pacing last → validation → "Revert all".
> - **Adapter** (`src/Gapo.Adapters.Sandbox`): the game-specific part behind two interfaces — a *session* (read and apply settings with acknowledgement, wait for warm-up, benchmark scene, measure a window, observe hitches) and *knowledge* (the setting ladders with visual cost, predictions from measured stage times and hypotheses in plain words). The engine never sees game-specific types; settings are a generic key → value set, and each candidate is just the keys it changes, which is also what makes give-back and the journal generic.
> - **Crash-safe journal** (`FileChangeJournal`): a small JSON file rewritten atomically (temp file, flush to disk, rename) before each trial and after each decision, holding the original settings, the last committed settings and whether a trial is in flight. When the agent starts and finds a trial in flight it writes the last committed settings back; `--revert-all` writes the original settings back even when the game is not running.
> - **Results on the entry-level laptop (Balanced goal, 60 Hz), two runs:** 24.7 → 59.9 FPS (+143 %, the 60 FPS cap was kept) and 25.2 → 94.0 FPS (+274 %, the cap was tested and rolled back because it did not make pacing smoother); frame work 38.7 → 10.3 ms (3.8× less); stutters 11.3 → 0 per minute; 11 windows in 51 s; noise floor ±2.5 %. Kept: autosave 10 s → 5 min (first), render threads 1 → 2 (+25 %), physics substeps 8 → 4 (+10 %), 4× SSAA → FXAA (+41 %), simulation distance Unlimited → Far (+28 %), render threads 2 → 4 (+8 %, the SMT question, measured). It then stopped: *"the 60 FPS target is met with headroom (10.3 ms of 16.7 ms)"* and named the four options it left alone. As in the demo's Appendix B, the pacing decision is borderline on this machine and flipped between the two runs.
> - **Predicted vs measured (ms saved):** render threads 11.1 vs 9.8; physics substeps 3.0 vs 2.9; anti-aliasing 11.3 vs 10.7; simulation distance 3.0 vs 4.2; SMT threads 0.8 vs 0.8. These pairs are in every report and are the training data for step 8.
> - **Overhead while optimising:** 0.26–0.29 % CPU averaged over the run (budget 1 %), 0.11 % inside measurement windows (budget 0.3 %), 15–16 MB private (budget 100 MB).
> - 🧪 **Time blocked inside the present call is waiting, not work.** On the GPU-less CI runner the first full optimisation went 28.2 → 62.5 FPS (+121 %) and then tested **ten more changes that all measured exactly 0.0 ms**. Once the game reached the 64 Hz display rate, the flip-model `Present` call itself blocked until the next composition, and the fixture counted that wait in its busy time, pinning "frame work" at 16.0 ms whatever the settings were. The engine rolled every one of them back as unproven, which was honest but wasted 40 seconds. Two fixes: the fixture now reports time inside the present call as wait (PresentMon's CPUWait), so busy time keeps falling and the engine stops at the target; and the engine has a new stop rule for real games, where it cannot see the cause — **after three changes in a row that make no measurable difference (under half the noise floor) although each was predicted to help, it stops and says that something outside these settings limits the frame rate (a cap, VSync or the display).** The real GPU on the laptop never showed this, which is why the virtual machine and the physical machine are both needed.
> - 🧪 **…and the wait is not always inside the present call.** Subtracting the present call changed nothing on the runner: busy time stayed pinned at 15.7–16.0 ms and eleven changes were rolled back. Timing each part of the present path showed why: at the display rate the software adapter blocks in the **`Map` call of the next frame's texture upload** (5.1 ms instead of 0.09 ms) while the present call itself returns in 0.13 ms. A real game can wait in many such places, and from outside (ETW) only the present call is visible. So the engine must not rely on busy time alone: it now also treats **"typical frame time equals the refresh period while busy time fills it"** as *already at the display rate*, stops lowering visuals there, and does not try a cap the display already enforces. With that rule the runner went 33.1 → 62.0 FPS (+87 %) with four changes kept, none rolled back, in 9 windows and 42 s, and reported *"Stopped lowering visuals: the game already runs at your display's 64 Hz (63 FPS), so further reductions could not show more frames."* The fixture now counts the map wait as wait as well.
> - 🧪 **`dotnet test` builds only test projects.** A rig run after `dotnet test` used stale agent and fixture executables and "passed" without exercising the change. Always `dotnet build` the solution before running the end-to-end executable (CI does).
> - 🧪 **Attribute CPU to windows at the window boundary.** The overhead meter sampled once a second and credited the whole second to "inside a window" if a window happened to be open at the sample. Work done just before a window (writing settings, the journal's flush to disk) was then counted as in-window CPU (0.23 % reported). Sampling at the moment a window opens or closes gave the true 0.11 %.


Rollback should occur if:

- The game crashes.
- Performance becomes worse.
- Frame pacing deteriorates.
- Visual quality becomes unacceptable.
- The user rejects the change.

> ➕ **Added** — - The measurement cannot be completed (too few frames, game stops responding) — roll back and report it as *failed*, not as *no gain*.
> - The game closes mid-experiment — keep the snapshot on disk so the next launch can restore it.


### Explanations

The product should always explain its decision.

Example:

```text
GPU bottleneck detected

GPU frame time: 11.8 ms
CPU frame time:  5.4 ms
VRAM usage:      7.2 / 12 GB

Shadow quality was reduced because it was predicted to lower
GPU time by approximately 1.2 ms with relatively little visual loss.

Result:
Average FPS: 82 → 94
1% low:       57 → 68
```

This creates user trust and makes the system easier to troubleshoot.

> 🧪 **Demo lesson** — every experiment card in the demo shows: the **hypothesis** ("Ultra shadows ray-march every pixel every frame (6.1 ms)…"), the **measured result** (frame work before → after, FPS, 1 % low, predicted saving), the **visual cost** (0–10 as five dots) and the **decision with its reason** (real examples: *"Reverted: +10 % is within the ±10.0 % measurement noise — no proven benefit"*, *"Reverted: no measurable benefit (−8 %)"* for a process-priority test, *"Reverted: restoring it would break the frame budget (17 ms)"* for a quality give-back). Rolled-back changes are shown as prominently as kept ones — that is what makes the result credible.


## 11. Optimisation targets

Users should be able to choose the goal.

| Mode | Priority |
|---|---|
| Maximum FPS | Highest possible FPS, regardless of visual quality |
| Competitive | High FPS and very low input latency |
| Quality | Maximum visual quality while meeting a minimum FPS |
| Smoothness | Strong 1% lows and consistent frame times |
| Balanced | Best performance-to-visual-quality ratio |
| Battery | Maximum FPS per watt for laptops |
| Quiet | Maintain performance below a chosen temperature or noise level |

The system could use an objective function such as:

$$
\text{Score} = w_f(\text{FPS}) + w_l(\text{1\% low}) - w_v(\text{visual loss}) - w_t(\text{temperature}) - w_p(\text{power}) - w_i(\text{input latency}) - w_s(\text{stutter})
$$

```text
Score = w_f·FPS + w_l·(1% low) − w_v·(visual loss) − w_t·(temperature) − w_p·(power) − w_i·(input latency) − w_s·(stutter)
```

> ✏️ **Changed** — the formula is unchanged in meaning; it was repaired because the original file had lost its `\t` characters (`\text` had become a tab + "ext"). The demo's implementation of it per experiment is: `score = w_perf·(relative busy-time gain) + w_pace·(relative p95 frame-time gain) − w_vis·(visual loss 0–10)`, keep only if the gain also beats the noise floor and 1 % lows did not collapse.

The user-selected mode determines the weighting values.

> 🧪 **Demo lesson** — the demo implements four of the seven modes — **Balanced, Maximum FPS, Quality, Smoothness** — as weight sets (performance gain, pacing gain, visual-loss penalty per point). Balanced needs ≈ 2.2 % measured gain per visual-loss point; Quality needs ≈ 5 %, stops once the target is met and never tries changes with visual cost ≥ 4 unless the target is missed; Maximum FPS almost ignores visual loss and never caps; Smoothness weights frame-time consistency above averages and may cap to half refresh. Competitive, Battery and Quiet need latency, power and temperature telemetry (vendor SDKs) and remain Phase 6+.


### Performance per visual quality

Example:

```text
Shadow quality:
Ultra → High

Performance:
100 FPS → 112 FPS

Visual-quality loss:
2/10
```

Compared with:

```text
Resolution:
1440p → 1080p

Performance:
100 FPS → 145 FPS

Visual-quality loss:
8/10
```

The optimiser should prefer the shadow change because it provides a better performance-to-quality trade-off.

> 🧪 **Demo lesson** — this is exactly how the demo orders candidates: **predicted ms saved ÷ (visual loss + 0.6)**. In practice it chose the free or near-free changes first — render threads where cores were available (4-thread machine) or anti-aliasing SSAA→FXAA (large saving, small loss) where they weren't (2-thread VM) — then physics substeps / simulation distance, and never reached visible reductions such as resolution scale or render distance because the target was met.


## 12. Performance budgets

The system should work with frame-time budgets.

| Target | Frame budget |
|---|---:|
| 60 FPS | 16.67 ms |
| 120 FPS | 8.33 ms |
| 144 FPS | 6.94 ms |
| 165 FPS | 6.06 ms |
| 240 FPS | 4.17 ms |
| 360 FPS | 2.78 ms |

Example:

```text
Target: 144 FPS
Frame budget: 6.94 ms

CPU: 8.2 ms  ← bottleneck
GPU: 4.1 ms
```

The system can then determine that the CPU must save approximately 1.3 ms. This is more useful than simply reporting high CPU utilisation.

The product should distinguish between:

- Hardware capability.
- Current utilisation.
- Current bottleneck.
- Sensitivity to setting changes.

Hardware impact should not be expressed as a fixed percentage, such as “the CPU accounts for 30% of gaming performance,” because the real impact depends on the game and current workload.

> 🧪 **Demo lesson** — the demo derives the target from the display (refresh rate → budget; 60 Hz → 16.7 ms; unknown refresh → assume 60 Hz and say so) and shows a frame-budget panel while it works (target FPS with ms per frame, frame work now, ms still to save). Users understood "ms still to save" far better than utilisation bars.


## 13. Synchronisation and shader compilation

### Thread synchronisation

A CPU may appear underutilised while still being limited by thread dependencies.

Example:

```text
Core 1: 90%
Core 2: 30%
Core 3: 30%
Core 4: 30%
```

If Thread B must wait for Thread A, and Thread C must wait for Thread B, adding more CPU cores will not solve the problem.

The optimiser should distinguish between:

```text
CPU capacity problem
```

and:

```text
CPU synchronisation problem
```

### Shader compilation

Shader compilation can cause stutter without indicating a permanent GPU bottleneck.

Possible indicators:

- Large frame-time spikes.
- CPU activity spikes.
- Disk activity spikes.
- Repeated behaviour associated with new areas or effects.
- Shader-related game or driver activity.

Possible recommendations:

- Allow shader precompilation to finish.
- Avoid unnecessarily deleting shader caches.
- Avoid judging performance during initial compilation.
- Use appropriate driver and game settings.
- Avoid reducing graphics quality to solve a temporary compilation problem.

> 🧪 **Demo lesson** — the sandbox streams its world for ~3 s after start-up (visible pop-in and spikes, like shader compilation). The optimiser waits until the game reports no warm-up frames for ~1.5 s (up to 45 s), telling the user *"Measuring now would blame the wrong thing — like judging a game while it compiles shaders."* Without this rule the streaming spikes would enter the baseline and make every later change look better than it really was.


## 14. User interface

### Main dashboard

```text
┌─────────────────────────────────────────┐
│             GAME OPTIMIZER              │
├─────────────────────────────────────────┤
│ Minecraft                               │
│                                         │
│ FPS             117                     │
│ 1% LOW           82                     │
│ FRAME TIME       8.5 ms                 │
│ LATENCY          24 ms                  │
│                                         │
│ CURRENT BOTTLENECK                      │
│ ████████████████████ CPU MAIN THREAD    │
│                                         │
│ CPU Main Thread       94%               │
│ GPU                   51%               │
│ VRAM                  42%               │
│ RAM                   58%               │
│ Storage               13%               │
│                                         │
│ RECOMMENDATION                          │
│ Simulation Distance: 12 → 8             │
│ Expected: +17 FPS                       │
│ Visual impact: Low                      │
│                                         │
│ [ Apply ] [ Test ] [ Ignore ]           │
└─────────────────────────────────────────┘
```

The displayed percentages should represent estimated contribution to the current bottleneck, not raw hardware utilisation.

> 🧪 **Demo lesson** — implemented as a **stacked stage bar with a frame-budget marker**: simulation (main thread), rendering, present, streaming/saving, other — each as a share of the frame's busy time, plus the budget line. It answers "what is the frame spending its time on, and how far over budget is it" at a glance, and it is honest (shares add up to the frame).


### Advanced view

```text
CPU
────────────────────────────────────
Main thread       8.9 ms
Render thread     3.2 ms
Worker threads    2.1 ms
CPU wait          0.8 ms

GPU
────────────────────────────────────
GPU time          5.7 ms
GPU busy          81%
GPU clock         2,745 MHz
GPU temperature   67°C
VRAM              7.4 / 12 GB

MEMORY
────────────────────────────────────
RAM                18.2 / 32 GB
Bandwidth           61 GB/s
Page faults         Low

FRAME
────────────────────────────────────
Average FPS         117
1% low               82
0.1% low             64
Frame variance      2.1 ms

LATENCY
────────────────────────────────────
Input → display     24 ms
CPU → GPU            4 ms
GPU → display        8 ms
```

> 🧪 **Demo lesson** — the demo's "Live analyser" tab (under *Show more options*) is this view: per-logical-processor load bars, game CPU in cores, background CPU, memory, game disk activity, average / 1 % / 0.1 % lows, frame-time variation, main-thread (simulation) ms, render ms, present ms — and GAPO's own overhead with its budget.


### “Why am I not reaching my target?”

Example response:

```text
Target: 144 FPS
Frame budget: 6.94 ms

Current:
CPU: 9.2 ms   ← bottleneck
GPU: 5.1 ms

Required improvement:
Approximately 2.3 ms of CPU time
```

Ranked interventions:

```text
1. Simulation Distance
   Expected improvement: -1.4 ms
   Visual impact: Low

2. Entity Distance
   Expected improvement: -0.6 ms
   Visual impact: Low

3. Background process: Chrome
   Expected improvement: -0.3 to -0.7 ms
   Visual impact: None

4. Render Distance
   Expected improvement: -0.4 ms
   Visual impact: Medium
```

> 🧪 **Demo lesson** — implemented in the demo's Diagnosis tab: target, budget, current frame work, required improvement, then the top four remaining interventions ranked by predicted value with "expected −X ms · visual impact low/medium/high". When the target is met it says so and explains what the headroom is for.


## 15. Laptop mode

Laptop users could choose between:

- Battery.
- Quiet.
- Balanced.
- Performance.
- Turbo.

Possible targets:

| Mode | Optimisation target |
|---|---|
| Battery | Maximum FPS per watt |
| Quiet | Maximum FPS below a temperature or noise limit |
| Performance | Maximum stable FPS |
| Turbo | Maximum available performance |

Example:

```text
Current:
92 FPS
95 W
83°C

Optimised:
88 FPS
71 W
72°C
```

For many laptop users, the second configuration may be preferable.

> ➕ **Added** — laptop detection (battery present, AC/battery, charge, power-mode overlay) works without admin and is shown on the "Your PC" card. When on battery the demo raises an *important* tip ("Plug in your laptop — Windows limits CPU and GPU speed on battery"). Battery/Quiet optimisation targets need package power and temperature (vendor SDKs) — Phase 6.


## 16. What the product should avoid

The product should not behave like an unreliable “PC cleaner” or “game booster.”

Avoid:

- Random registry hacks.
- Generic “clear RAM” buttons.
- Indiscriminate Windows-service disabling.
- Forced CPU affinity without evidence.
- Blindly killing processes.
- Unnecessary cache deletion.
- Random process-priority changes.
- Unsupported claims such as “200% more FPS.”
- Automatic BIOS changes.
- Disabling security software.
- Changing dozens of settings simultaneously.
- Treating 100% utilisation as inherently bad.


> ➕ **Added** — additions from the demo:
> - Measuring during warm-up, shader compilation or right after a settings change.
> - Claiming a gain that is smaller than the measured noise floor.
> - Running WMI/PDH queries or process scans inside a measurement window (the optimiser becomes the bottleneck it is measuring).
> - Heavy UI animation while measuring (the demo repaints at 1 Hz inside a window).
> - Shipping vulnerable kernel drivers (e.g. WinRing0) for sensors.
> - Injecting into games or reading game memory (anti-cheat bans).
> - Writing settings files non-atomically, or while the game is about to overwrite them on exit.
> - Hiding rolled-back experiments — show them.

The correct approach is:

```text
Measure → Hypothesise → Test → Verify → Keep or revert
```

## 17. Product structure

The project could eventually become three products.

### 1. Optimiser

For everyday users:

> Make my game run better.

### 2. Analyser

For enthusiasts:

> Tell me exactly what is limiting my PC.

### 3. Developer profiler

For game developers:

> Explain why my game is stuttering.

The developer version could provide detailed CPU, GPU, memory, storage, synchronisation, and frame-pacing analysis.

> 🧪 **Demo lesson** — the demo UI already splits along these lines: the **simple view** is the Optimiser (one number such as "+80 %", before → after), **Show more options** is the Analyser (diagnosis, evidence, per-core load, stage times), and the engine's per-stage telemetry contract with the sandbox is a small preview of what a Developer profiler would need from game engines (a published frame-timing/markers API).


## 18. Recommended MVP

The project should be built in stages.

> ✏️ **Changed** — the phases are unchanged in content; the demo proved Phases 1–4 in miniature and changed the **order of risk**. Build the measurement harness (A/A noise floor, repeatable scenes, acknowledged applies, rollback) *before* the prediction logic, and build per-game settings adapters and a real-Windows test rig early — they took the most engineering in the demo. See Part III → *Build order* for the revised, practical sequence.


### Phase 1: Telemetry

Implement:

- Game detection.
- CPU monitoring.
- GPU monitoring.
- RAM and VRAM monitoring.
- Temperature, clock, and power monitoring.
- FPS and frame-time measurement.
- 1% lows.
- CPU and GPU frame time.
- Per-core utilisation.

### Phase 2: Bottleneck detection

Classify:

- CPU main-thread bound.
- CPU parallel-workload bound.
- GPU-bound.
- VRAM-bound.
- RAM-bound.
- Thermal-bound.
- Power-bound.
- Storage-bound.
- Background-process-bound.
- Frame-pacing-bound.
- Shader-compilation-bound.
- Synchronisation-bound.
- Display-bound.
- Network-bound.

> ➕ **Added** — - Transient (warm-up / shader compilation / background update) — detect and wait.
> - Single-threaded render/submit (parallelism setting available) — found in the demo; distinct from pixel-bound rendering.

> ➕ **Added (product build, step 4)** — diagnosis v1 is pure, unit-tested code (`src/Gapo.Core/Diagnosis`), fed by the baseline measurement, the monitor and the hardware inventory. It has two paths:
> - **With stage times from the game** (the fixture's hook, later partner engines): the demo's rules — transient when more than 20 % of frames are warm-up; rendering on one thread; rendering workload with its three costliest passes; main-thread simulation with the engine-limitation text — and the target-reached verdict when frame work is under 92 % of the budget with no save freezes left.
> - **From outside only** (any game, ETW presents plus the monitor): *waiting to present* when 40 % or more of the frame is spent inside the present call (graphics card, VSync or a cap — and never "target reached" while the frame itself is longer than the budget); *limited by one CPU thread* when the busiest thread is at 85 % or more of a processor while the whole game uses fewer than 1.6; *CPU fully used* at 80 % or more of all processors; otherwise it says that no single limit stands out rather than guessing.
> - Evidence and secondary problems are each conditional on their own measurement (idle other processors only under 35 %, background apps only from 15 %, the hidden one-thread sentence only with a 25-point gap, memory pressure from 88 % load or under 700 MB free, uneven pacing only when no save freeze explains it, a clock limit under 80 % of maximum).
>
> Checked on real Windows by the end-to-end rig with two fixture scenarios on the entry-level laptop: the default fixture → *"Rendering is stuck on one CPU thread — rendering takes 27.9 ms of each 40.5 ms frame, all on 1 of your 4 CPU threads, while the other processors are mostly idle (29 % on average)"*; `--scenario=simulation-bound` → *"CPU main thread (simulation) — the simulation takes 32.3 ms of each 47.7 ms frame (1464 creatures simulated, physics 25.9 ms)"* with the main-thread limitation text; both also reported the autosave freezes (*"froze for up to 230 ms while saving, 4 time(s) in the last 38 s"*).


### Phase 3: Game settings

Begin with:

- Resolution.
- Textures.
- Shadows.
- View distance.
- Effects.
- Ray tracing.
- Volumetrics.
- Crowd density.
- VSync.
- FPS cap.
- Upscaling.

### Phase 4: Controlled experiments

Use:

```text
Change → Benchmark → Compare → Keep or revert
```

### Phase 5: Game database

Collect:

```text
Game × Hardware × Settings × Performance
```

### Phase 6: Prediction model

Predict:

- Expected FPS.
- Expected 1% low.
- Expected latency.
- Expected frame-time variance.
- Expected temperature and power changes.

### Phase 7: Autonomous optimisation

The long-term goal is:

> Optimise this game for 120 FPS with the highest possible visual quality.

The system would then determine the configuration automatically.

> 🧪 **Demo lesson** — the demo's Quality mode is a first step towards this: it reaches the target with the least visual loss, stops, and then tries to **give back** the most visible reduction when there is ≥ 40 % headroom, keeping it only if the target still holds.


## 19. Long-term vision

The final product is not simply an FPS booster. It is a PC and game performance reasoning engine.

It should eventually answer questions such as:

- Why does this game run at only 83 FPS?
- Why does it stutter despite showing 120 FPS?
- Why is GPU utilisation only 55%?
- Which setting provides the most FPS for the least visual degradation?
- Why does performance decline after 20 minutes?
- Is the CPU, GPU, RAM, VRAM, SSD, cooling, power delivery, driver, game engine, or Windows configuration responsible?
- What is the optimal configuration for this exact PC, game, scene, and target FPS?

The strongest product principle is:

> Do not ask which hardware component is being used the most. Ask which resource is preventing the game from reaching the target frame time, why that is happening, and what intervention fixes it with the least visual cost.

The key innovation is the reasoning layer that converts telemetry into causal diagnosis, controlled experiments, and verified optimisation.

> 🧪 **Demo lesson** — this was the part the demo validated most convincingly. The same telemetry that a monitoring overlay shows became, through the reasoning layer, a sequence of explained, measured, reversible decisions — and a single honest headline number.


Additional brief example: Every PS5 game runs smoothly at a high frame rate, because the game is specifically optimised for the PS5’s hardware. If you boot up the exact same game on a PC with around the same specs, the game will play noticeably worse in terms of framerate. This is due to the fact that developers are able to optimise every game for a PS5, because every PS5 has the same specs, but for PC users, the components differ, and developers must optimise their game in a “general” way, so all PC users can have an “OK” experience with their game. This program fixes it.

> ➕ **Added** — the demo is a small-scale version of this idea: it adapts one game to the PC it finds (core count → render threads, display refresh → frame budget and cap, memory headroom → leave textures alone, storage type → hitch advice, PC class → which wins to go after first) instead of shipping one "general" configuration for everyone.

---

# Part III — Practical build guide

## III.1 Engineering rules (non-negotiable)

1. **No code comments in the product's source code.** The commercial codebase must contain no `//`, `/* */`, `///` or `#` comments, doc-comment blocks, or commented-out code. That keeps it clean, commercial and professional. The code has to explain itself:
   - Intention-revealing names for types, methods, variables and constants (for example `MeasurementWindow.DiscardSettlingFrames()`, not `Step3()`).
   - Small single-purpose functions and types.
   - Named constants instead of magic numbers (`NoiseFloorMultiplier = 2.5`).
   - Tests that document behaviour (`RejectsChangeWithinNoiseFloor`).
   - Design rationale lives **outside** the code: this document, `docs/` pages and short Architecture Decision Records (`docs/adr/NNNN-title.md`).
   - **Enforce it in CI.** A check fails the build when product source files contain comments. Generated code and third-party code are excluded. Licence text lives in `LICENSE` and `THIRD-PARTY-NOTICES`, not in file headers.
   - The demo's comments were for the prototype only. Do not carry them over.
2. **A virtual Windows test machine from day one, used continuously.** The coding agent must set up a virtual Windows test machine and keep building, running and testing the program on it throughout development, fixing bugs as they appear. Nothing counts as "done" until it has run on Windows. See III.6.
3. **The overhead budget is a test gate** (III.7). A change that makes the optimiser exceed its CPU/RAM budget fails the build, like a failing unit test.
4. **Commit and push working increments.** Each increment must build, pass tests and pass the Windows end-to-end run. Use small, descriptive commits.
5. **Keep this document alive.** Every new lesson, gotcha or changed decision goes into this file with the 🧪 / ✏️ / ➕ markers, in the same commit as the code that taught it.
6. **Never** inject into games, read game memory, ship vulnerable drivers, change BIOS settings, disable security software, kill processes on the user's behalf, or make unverifiable performance claims (§16).

## III.2 Recommended architecture

```text
┌───────────────────────────── User session ─────────────────────────────┐
│                                                                        │
│  GAPO UI (unprivileged, only runs when opened)                         │
│   • Simple view, "Show more options", tray, notifications              │
│   • Renders nothing while a game is in the foreground                  │
│            ▲  named pipe (ACL: this user)                              │
│            ▼                                                           │
│  GAPO Agent (per-user process, low priority, always tiny)              │
│   • Game detection (process-start events, launcher libraries)         │
│   • Game adapters: read/write settings, restart handling, benchmarks   │
│   • Experiment engine: windows, A/A noise, transactions, rollback      │
│   • Diagnosis + prediction + knowledge base (SQLite)                   │
│            ▲  named pipe (ACL: SYSTEM ↔ user)                          │
└────────────┼───────────────────────────────────────────────────────────┘
             ▼
┌──────────── GAPO Telemetry Service (Windows service, minimal) ─────────┐
│   • PresentMon/ETW frame timing (CPUBusy, GPUBusy, latency)            │
│   • Vendor sensor SDKs: NVML/NVAPI, ADLX, IGCL (clocks, temps, power)  │
│   • Nothing else: no settings writes, no UI, no network                │
└────────────────────────────────────────────────────────────────────────┘
```

- **Why split?** ETW frame timing needs admin rights or the *Performance Log Users* group. Keep the privileged part small, auditable and read-only. Everything that changes things runs as the user.
- **Game adapters are plugins** with one interface: `Detect`, `ReadSettings`, `WriteSettings` (atomic + backup), `RequiresRestart(setting)`, `Benchmark` (built-in benchmark, replay, or scene fingerprint), `AntiCheatPolicy`, and `SettingLadders` (levels, visual-loss scores, stage mapping). Start with games that have INI/JSON configs and a built-in benchmark.
- **Knowledge base:** per game × engine × setting, it stores cost ladders, known limitations and adapters. It also records every experiment (hardware, driver, settings before/after, predicted vs measured). The same records later feed the *Game × Hardware × Driver × Settings × Performance* database (§8).
- **Transaction log:** every change the product makes (game settings, priority, anything else) is journaled with how to undo it. Uninstall and "Revert all" replay the journal backwards. A crash-safe journal matters more than any single optimisation.

## III.3 Tech stack

| Layer | Recommendation | Why | Demo lesson |
|---|---|---|---|
| Telemetry service | C# on .NET 8+ with **NativeAOT**, or Rust | Small memory footprint (~15–30 MB), fast start, easy P/Invoke / FFI to PresentMon and vendor SDKs | The demo's P/Invoke layer (NtQuerySystemInformation, PowerGetEffectiveOverlayScheme, GetLogicalProcessorInformationEx) is directly reusable |
| Agent + engine | C# .NET 8+ (NativeAOT where possible) | Productive, fast enough, the engine logic from the demo ports directly | Engine logic was ~1,000 lines; the hard parts were measurement discipline, not CPU speed |
| UI | **WinUI 3 (Windows App SDK)** with Fluent controls, a flat solid background (no Mica/Acrylic), theme following Windows light/dark, composition-based charts | GPU-composited (cheap to draw), native Windows 11 look, automatic theming, accessibility, Per-Monitor v2 DPI | GDI+ custom drawing worked but was the largest CPU cost on a machine without GPU acceleration; the demo's gradient look is demo-only (III.8) |
| Charts | Win2D or Composition shapes; decimate data before drawing (≤ 200 points) | Cheap to redraw | The demo decimates to 150 buckets (keeping the worst frame per bucket so spikes stay visible) |
| Storage | SQLite (local), JSON for adapter definitions | Simple, robust | — |
| Frame timing | PresentMon SDK / service (ETW) | Works with DX9–DX12, Vulkan and OpenGL without injection | The demo's engine hook proved which metrics matter: frame time, busy time, per-stage times, flags |
| Sensors | NVML / NVAPI, ADLX, Intel IGCL; no WinRing0 | Safe, supported | — |
| Installer / updates | MSIX (or WiX) plus a signed auto-updater | Clean install/uninstall, trust | SmartScreen blocks unsigned downloads |
| CI | GitHub Actions `windows-latest` + self-hosted Windows VMs (III.6) | Real Windows every push | Worked well; results read from logs |

If the team prefers one language for everything, a Rust service + Rust/C# agent + WinUI 3 UI is equally valid. The rules in III.7 matter more than the language.

## III.4 Build order

Each step ends with a Windows end-to-end run (III.6) and a commit.

0. **Test rig first.** Set up the virtual Windows test machine(s) and CI, port the demo sandbox as a test fixture (with configurable bottlenecks), write auto-test modes and screenshot capture, and add the overhead meter and budget gate.
1. **Telemetry core (Phase 1).** PresentMon integration in the service. Per-core load, the process snapshot, game CPU/IO/memory, own overhead. Everything at ≤ 1 Hz except frame events.
2. **Hardware inventory.** A one-shot, cached scan with a "could not be read" list (demo `HardwareScanner` → product). Rescan only on hardware or driver change events.
3. **Measurement harness.** Windows, warm-up detection, A/A noise floor, statistics (median busy time, p95/p99, lows over long spans, hitch detection per revision), ABAB interleaving.
4. **Diagnosis v1 (Phase 2).** CPU main-thread vs CPU parallel vs GPU vs VRAM vs RAM vs storage hitch vs transient vs background vs pacing, with conditional evidence sentences.
5. **First three game adapters (Phase 3).** Pick games with text configs, built-in benchmarks and no kernel anti-cheat problems. Implement setting ladders, visual-loss scores and restart handling.
6. **Experiment engine (Phase 4).** Transactions, journal, rollback, value ordering, stop-at-target, quality give-back, pacing last, explanations.
7. **UI** (III.8). Simple view, "Show more options" tabs, tray, notifications. Accessibility and localisation hooks from the start.
8. **Knowledge base + learning (Phases 5–6).** Store predicted vs measured for every experiment and learn per-game corrections. Opt-in shared database.
9. **Sensors via vendor SDKs.** Temperatures, power, throttle reasons → Thermal/Power diagnosis, Laptop/Battery/Quiet modes (§5, §15).
10. **Latency and network modules** (§6, §7).
11. **Beta hardening.** Signing, installer, updater, crash reporting, privacy review, anti-cheat vendor conversations, support-log export.
12. **Autonomous optimisation (Phase 7).** "Optimise this game for 120 FPS with the highest possible visual quality".

## III.5 Testing approach

- **Unit tests:** statistics (percentiles, lows, stutter detection, noise floor), settings parsing/writing (atomicity, backups), diagnosis rules on recorded traces (golden files), value ordering, stop rules, rollback logic.
- **Recorded-trace tests:** save real telemetry traces (frames + monitor samples) from test machines, and replay them through diagnosis and decision logic to check that the conclusions don't regress.
- **Fixture game tests:** the sandbox (ported) with switches that create each bottleneck class on demand (single-thread render, O(n²) simulation, storage hitch, warm-up, memory pressure, background load, GPU-like pixel load). Every bottleneck class must be diagnosed and fixed or explained.
- **Real-game tests:** automated runs of adapter games using their built-in benchmarks on the VM (III.6) and on physical hardware.
- **Hardware matrix (physical, before every beta):** an entry-level laptop (dual/quad-core Celeron/Pentium/Athlon, iGPU, **single-channel 4–8 GB**, HDD or eMMC), a mainstream desktop (6–8 cores, mid GPU), an Intel hybrid (P/E cores), an AMD APU, NVIDIA/AMD/Intel Arc GPUs, a 144 Hz+ VRR display, a laptop on battery, and a multi-monitor setup.
- **Overhead regression (gate):** every CI run records the optimiser's CPU average (whole run and inside measurement windows), peak, and private memory, and fails above budget.
- **Soak tests:** 8-hour idle and 8-hour gaming sessions to catch leaks, handle growth and timer drift.
- **UI tests:** render every screen to images in CI (as the demo does) at 100/150/200 % scale, in dark and light themes. Check contrast and keyboard navigation.
- **Safety tests:** kill the agent mid-experiment, cut power (VM reset) mid-write, uninstall mid-run. The journal must always restore the original settings.

## III.6 The virtual Windows test machine (mandatory)

The coding agent must **set up a virtual Windows test machine and keep building, running and testing the program on it throughout development, fixing bugs as they appear.**

- **Minimum (works from any cloud dev container):** GitHub Actions `windows-latest` runners, exactly as the demo used them. Every push builds, installs, starts the fixture game, runs the optimiser's auto-test end to end, and prints the report, the overhead numbers and compressed screenshots into the log. It also uploads the artifacts. The demo's workflow (`.github/workflows/windows-demo.yml`) and screenshot printer (`demo/tools/Print-Screenshot.ps1`) are the starting point.
- **Better (persistent VM):** a Windows 11 VM the agent can drive (Hyper-V, VirtualBox, QEMU/KVM, or a cloud VM such as Azure or AWS) with WinRM/SSH/PowerShell remoting, snapshots and restore-to-clean. Use a GPU-enabled cloud VM or GPU passthrough to test vendor SDKs and real games.
- **Loop:** build → copy → install → start the game/fixture → auto-test → collect report, logs, screenshots and overhead → assert → fix → repeat. Restore the VM snapshot before each full run so results don't depend on leftover state.
- **Known VM quirks** (from the demo): refresh rate reported as 1 Hz by WMI but 64 Hz by `EnumDisplaySettings`, Hyper-V display adapter, odd memory-module data, 1 core with SMT, below-normal inherited process priority, unspecified disk media type. Code must handle them, and tests must not mistake them for bugs in the product.
- Physical hardware (III.5 matrix) confirms results before each beta. The VM does not replace it.

> ➕ **Added (product build, step 0)** — the rig as built:
> - `tests/Gapo.EndToEnd` is one executable that starts the fixture game alone, then with the agent attached, then alone again, reads their JSON result files, and fails on: a non-zero exit, too few frames, the agent not connecting or missing frames, the agent over its overhead budget, or the game running slower with the agent than the A/A noise floor allows. `.github/workflows/windows.yml` builds, runs the no-comments check, the unit tests and this executable on `windows-latest` for every push that touches code, and uploads results and screenshots as artifacts (fetch them with `gh run download`; no base64 in the log needed).
> - The fixture game (`fixture/Gapo.Sandbox`) presents through a **D3D11 flip-model swap chain**, falling back to WARP and then to GDI, so that it produces real present events for PresentMon and can load a real GPU (`--gpu-load=<iterations>` runs a per-pixel shader loop that scales with resolution scale and anti-aliasing). Scenario switches create each bottleneck class on demand: `--scenario=single-thread-render | simulation-bound | storage-hitch | warm-up | memory-pressure | gpu-load`, plus `--entities`, `--warmup-seconds`, `--save-size-mb`, `--memory-ballast-mb`, `--memory-leave-free-mb`, `--priority=belownormal`, `--no-telemetry`, `--data-dir`, `--map-name`, and the command `TriggerWarmup` to restart streaming mid-run. Background load needs a separate process and is still to be added.
> - The developer PC has only the .NET SDK (no Visual Studio, no MSVC linker). Everything so far builds and runs with `dotnet build`; NativeAOT publishing needs the MSVC linker, so AOT builds will come from CI. A persistent Windows 11 VM is not realistic on this hardware (unsupported CPU, 2 cores); the physical laptop plus the hosted runner is the rig.
> - The old demo workflow is now manual-only (`workflow_dispatch`) so it does not spend private-repo Windows minutes on every push.

## III.7 Keeping the optimiser's own overhead low

The product fails if the optimiser causes lag. Treat overhead like a performance feature with budgets, measurements and regression gates.

**Budgets (on an entry-level 2-core/4-thread PC):**

| State | CPU (share of the whole PC) | Private memory | Disk | Network |
|---|---|---|---|---|
| No game running (agent only) | ≤ 0.1 % | ≤ 30 MB | none | none |
| Game running, dashboard closed | ≤ 0.3 % | ≤ 40 MB | none | none |
| UI open, live dashboard | ≤ 0.5 % | ≤ 80 MB (UI process) | none | none |
| During optimisation (average) | ≤ 1 % | ≤ 100 MB total | settings writes only | none |
| Inside a measurement window | ≤ 0.3 % (target ≈ 0) | unchanged | none | none |

**Techniques that worked in the demo:**

- **Nothing heavy inside measurement windows.** No WMI, no PDH creation, no process scans, no UI repaint (the demo freezes its UI during windows and says *"Measuring — GAPO is keeping quiet"*).
- **Heavy queries once, at start-up, before anything is measured.** Then force a GC so the garbage from them is released.
- **Cheap native calls on cached handles** (`GetProcessTimes`, `GetProcessIoCounters`, `K32GetProcessMemoryInfo`, `NtQuerySystemInformation`) with preallocated buffers.
- **Low sampling rates.** Monitor at 1 Hz, telemetry reads at 10 Hz from shared memory (a 4,096-frame ring means nothing is lost), live dashboard at 1 Hz, 2 Hz between experiments, and animation only for short user-visible moments (a ~1.2 s result count-up at 12 Hz). A software-drawn repaint cost ~11.6 ms on the no-GPU CI VM, so every repaint per second costs ≈ 0.6 % of a 2-thread PC. That is why the product UI must be GPU-composited and must not render while a game has focus.
- **Low priority.** The optimiser process runs *below normal* while optimising, and its worker threads run below normal.
- **Bounded buffers.** Frame store ≤ 24,000 records (~3 MB); monitor history 180 samples.
- **Allocation-free hot paths.** Shared text formats, no LINQ in per-frame code, reused arrays.
- **Measure yourself and show it.** GAPO's CPU (rolling 10 s average, peak) and private memory in the header. It turns amber with "over budget" when exceeded.

> ➕ **Added (product build, step 0)** — the product's overhead meter (`src/Gapo.Core/Overhead`, fed by `src/Gapo.Windows/Processes/OwnProcessProbe`) reports CPU as a share of the whole PC from **cycle counts**, not tick-based process times (see the I.6 row "Own CPU time"), with a rolling 10 s average, a run average, a separate in-window average, the peak, and private bytes. The budgets of the table above are code (`OverheadBudget.For(state)`) and the agent exits with a non-zero code when it exceeds them, so CI fails like on a failing test. First measurement of the agent skeleton attached to the fixture game on the entry-level laptop (JIT build, below-normal priority, EcoQoS requested, 10 Hz telemetry reads, 1 Hz self-sample): **0.019 % CPU average, 0.35 % peak, 6.0 MB private** against the 0.3 % / 40 MB "game running, dashboard closed" budget. The A/B check ran the fixture alone, with the agent attached, and alone again: 23.1 / 23.3 / 23.3 FPS, so the attached run was inside the ±2.5 % noise floor.

**Additional techniques for the product:**

- A GPU-composited UI (WinUI 3/Composition). No rendering while the game has focus or the window is occluded/minimised. Honour the *reduced motion* setting.
- **EcoQoS** for background work (`SetProcessInformation(ProcessPowerThrottling)`), so Windows schedules the agent on efficiency cores and at low clocks.
- Event-driven game detection (ETW process-start or a cheap process-list poll every 5 s) instead of WMI event subscriptions, which are expensive.

> ➕ **Added (product build, step 5 — finding real games)** — GAPO can now find installed Steam games and tell which one is running, without the engine hook and without touching the game:
> - **Where Steam is:** `HKCU\Software\Valve\Steam\SteamPath`, then `HKLM\SOFTWARE\Valve\Steam\InstallPath` in the 32-bit and 64-bit views (`src/Gapo.Windows/Games/SteamLocator`). No Steam on the PC is a normal answer, not an error.
> - **What is installed:** `steamapps\libraryfolders.vdf` lists every library folder (both the current format with a `path` and an `apps` section per folder and the older `"1" "D:\\SteamLibrary"` form are read); each `appmanifest_<id>.acf` gives the app id, name, install folder, size and whether the install is complete (`StateFlags` bit 4). These are Valve's text KeyValues files, read by a small pure parser (`src/Gapo.Core/Games`) that **rejects damaged text instead of guessing** (unbalanced braces, a key without a value, nesting deeper than 64) — Steam rewrites these files while it runs, so half-written files do occur.
> - **Files are untrusted input.** An `installdir` that is not a plain folder name (`..\..\Windows`, an absolute path, a path with separators) is refused, so a manifest can never point GAPO at a folder outside its library. Files larger than 4 MB are not read.
> - **Nothing is hidden:** an unreachable library (an unplugged drive), an unreadable manifest and a missing library list are returned as "could not be read" notes, the same way the hardware inventory does it. The Steamworks redistributables entry (app 228980) is left out because it is not a game.
> - **Which game is running:** the process list the monitor already captures is matched against the install folders. A process whose executable lives inside a game's install folder is that game; when several do (launcher, crash handler, the game), the one with the largest working set is the game. Executable paths come from `QueryFullProcessImageName` on a query-only handle and are resolved **once per process** (cached by process id and start time), so the steady-state cost is the dictionary lookups — no memory is read and nothing is injected.
> - Run it: `Gapo.Agent.exe --list-games [--steam-root=<folder>]`. Verified on Windows with a Steam folder tree on disk (two libraries, one unreachable drive, one incomplete install) and a stand-in process started from inside a game folder. Steam itself is not installed on the test laptop, so this has **not yet been run against a real Steam installation** — that is the next thing to do on a PC that has one.
> - ➕ **Measuring a game GAPO does not know yet.** In serve mode the agent polls for a running Steam game every 5 s while no hooked game is connected (one process-list snapshot; executable paths are cached, the library is re-read every 5 minutes). When it finds one, it starts event-tracing frame timing for **that process only**, created when the game appears and stopped when it exits, so nothing is traced while no game runs. The interface shows the game's name, FPS, frame time, CPU busy time ("frame work"), stutters and the chart, says plainly that GAPO "does not know this game's settings yet, so it cannot change anything", and **does not show Optimise or the goal selector**; an Optimise command from an older interface is answered with the same reason. The protocol carries this as `canOptimise` on each live sample (missing means true, so an older agent still works). If frame timing is not permitted (no administrator rights and not in *Performance Log Users*), the interface says so instead of showing empty tiles without a reason.
> - 🧪 **Tested end to end without Steam:** the rig builds a Steam library folder in its output directory, copies the fixture game into it as "Fixture Without Its Hook" and runs it with `--no-telemetry`, so the agent has nothing but the library files, the process list and event tracing. On the laptop: found within one poll, **24.6 FPS live vs 25.2 FPS measured by the game itself (2.6 % apart — the live figure covers the last 10 s, the game's figure the whole run)**, frame work 39.5 ms vs the game's 39.6 ms frame time, Optimise refused with the reason, the exit noticed, agent 0.16 % CPU and 15 MB. The rig gate allows 10 % between the live and the game's own FPS.
> - ➕ **Diagnosing a game GAPO does not know.** Once the unknown game's frame times have settled (the same warm-up detector, working from frame times alone, at most 45 s), the agent resets the monitor's summaries, observes **20 seconds** of play and runs the diagnostician's outside-in path (`FromOutside`: waiting inside present, one busy thread, all processors busy, or "no single limit stands out") with the event-tracing frame statistics and the monitor's thread and processor figures. Nothing is changed and no scene is controlled, and the interface says so under the result: "Observed for 20 s while you played … GAPO could not repeat the same scene, so this describes those 20 s." The result appears in the home view under **What limits this game** and is re-sent to an interface that connects later. On the laptop the hook-less fixture was diagnosed as **"The game is limited by one CPU thread — the busiest thread uses 100 % of one processor while the whole game uses 1.0 of your 4 CPU threads"**, which matches what the hook-based diagnosis finds with full stage times. The rig fails if it does not.
> - 🧪 **"Background apps" includes GAPO's own window.** Background CPU is the whole-PC average minus the game and the agent. When the interface was started during the 20 s observation, its start-up showed up as "Background apps use 33 % of the CPU". The figure is right for those 20 s; the observation simply happened to include the window starting. The interface's start-up cost is now visible in the diagnosis rather than hidden.
- Sensor polling through vendor SDKs at ≤ 1 Hz, and only while a game runs.
- NativeAOT or trimmed runtimes to cut memory. Workstation non-concurrent GC for the service.
- An **A/B overhead check** in CI: run the fixture game with and without the agent attached and require the FPS difference to stay within the noise floor.

> ➕ **Added (product build, step 1)** — two lessons from running that A/B check on a PC that is also being used:
> - **A noise floor that grows with the noise can hide anything.** The floor is 2.5× the difference between the two runs without the agent. When the machine was busy during one of them (16.9 vs 25.4 FPS — the development tools woke up mid-run), the floor hit its 10 % cap and the check "passed" while proving nothing. The rig now refuses to judge when the two baseline runs differ by more than 8 %, says so, measures the whole trio again (up to three attempts) and fails with "too busy to judge" if the machine never settles. The product's measurement harness needs the same rule: a noisy A/A pair is a reason to wait and re-measure, not a wider tolerance.
> - **Ask ETW for only the events you use.** Enabling the DXGI provider without a filter delivered about 14,000 events in 20 s, of which under 1,000 were the game's presents. An event-id filter on `EnableTraceEx2` (`EVENT_FILTER_TYPE_EVENT_ID`, present start/stop ids only) cut that to about 3,000 (the game's presents plus those of other programs), with the same frame-time agreement (0.1 %) and no lost events.

## III.8 Making it look good

> ✏️ **Changed** — the commercial product's interface must be **minimalistic**, must **follow the Windows light/dark theme setting automatically**, and must have **one flat background colour, with no gradients and no glow effects**, so it looks designed rather than "made with AI". The demo's gradient/glow look (gradient buttons, gradient result number, radial background glows, gradient chart fills) was fine for a test and **must not carry over** to the product. What carries over from the demo is the *structure*: the information hierarchy, the simple view with "Show more options", the frame-budget bar, the experiment cards and the copy rules.

> ➕ **Added (product build, step 7, first increment)** — the product UI exists as a WinUI 3 app (`src/Gapo.App`, Windows App SDK — first 2.6.5, now pinned to 1.8, see below — unpackaged, self-contained) that builds with the .NET SDK alone. It currently renders a finished optimisation from the agent's report: the one honest number with "faster", the "× less work per frame" line, four before → after rows with flat good/bad tags, the one-sentence summary, **Show more options**, and the five tabs (*What I changed* with hypothesis → measurement → decision cards and *Considered but not changed*, *Diagnosis*, *Your PC* with what could not be read, *Tips*, *Live analyser* with GAPO's own use against its budget). GAPO's own CPU and memory are in the header. A screenshot mode renders every screen to PNG, and the end-to-end rig runs it in the light and the dark theme after each optimisation run. Measured on the laptop: a minimal WinUI 3 window uses 30 MB and no measurable CPU when idle.
> - ✏️ **`text-tertiary` was too faint for its own rule.** #8A909B on the light background and #7D828D on the dark one are about 3.2 : 1 and 4.0 : 1, below the 4.5 : 1 this section requires for 12 px labels. The app uses **#6F7582** (light) and **#9096A1** (dark).
> - ✏️ **Primary button colour.** The Windows accent colour on the test PC was a mustard yellow that made white button text hard to read, so the primary button uses the `accent` token pair from the table below in both themes instead of the Windows accent.
>
> ➕ **Added (product build, step 7, second increment — the live link)** — the interface and the agent are now two processes joined by a named pipe (`\\.\pipe\GAPO.<session>`, newline-delimited messages defined in `src/Gapo.Core/Protocol`). The agent runs in **serve mode**: it watches for the game, sends a status line on every state change, a live sample once a second (FPS, frame time, frame work, stutters per minute, processors used, machine load, its own CPU and memory, and the last 10 seconds of frame times decimated to 150 points that keep the spikes) and the path of each finished report; it accepts Optimise (with a goal), Stop and Revert all. The interface starts the agent when none is running and the agent exits when the interface's process ends. The home view shows the four live tiles, the frame-time chart with 60 and 30 FPS guide lines, the goal selector (Balanced / Maximum FPS / Quality / Smoothness) and the Optimise, Stop and Revert all buttons; the result view has Revert all and Optimise again. The header shows **the agent and the window added together**, with the split in a tooltip. The interface does not redraw live data while its window is not the active one.
> - 🧪 **A UI framework can cost more than the whole optimiser, and only next to a game.** On Windows App SDK 2.6.5 the window measured 0.1 % CPU when alone and **25 % of the PC — one whole logical processor — whenever the fixture game was presenting in its default 1280 × 720 window.** The time was in the framework's own compositor thread (`dwmcorei.dll`), which looped on vertical-blank waits without ever blocking; the page was static, so nothing GAPO drew caused it. Minimising (0.02 %) or hiding (0 %) the window stopped it; plain CPU load, a 1 ms timer resolution or an ordinary animating window did not trigger it. Versions 1.7 and 1.8 of the same SDK do not do this, so the app is pinned to **1.8.260921001** (ADR 0004). *Lesson: measure the interface next to a running game on the entry-level PC, not in isolation — an idle measurement said "30 MB and no CPU" for a build that took a core from the game.*
> - 🧪 **The rig now gates the interface, not just the agent.** A new end-to-end phase runs the window with live data next to the fixture game and fails the build above 0.5 % CPU / 80 MB. Start-up compilation arrives in bursts, so a single 30-second window read anywhere from 0.35 % to 1.06 % for the same build; the gate waits 20 s, measures three 15-second slices and judges the **median slice** (the overall figure is still printed). The CPU budget is enforced only when the game renders on a hardware adapter; a ceiling of a quarter of one logical processor applies everywhere, because a spinning thread takes a whole one. Laptop result after the fixes: **0.15 – 0.41 % CPU in the median slice, 37 MB private.**
> - 🧪 **Profile-guided re-compilation is a background cost.** After the spin was gone, the largest consumer in the window process was the .NET runtime re-compiling methods for tiered PGO: about 0.2 % of the PC for the first minutes. `TieredPGO=false` (already set for the agent) took the window from 0.46 % to 0.29 % in the same test.
> - 🧪 **`NamedPipeClientStream.ConnectAsync` with a timeout spin-waits** while the pipe does not exist; with a game holding the timer at 1 ms that was 0.6 % of the PC just to wait for the agent. The interface connects with a zero timeout and retries once a second (0.18 %, including everything else the idle window does).
> - 🧪 **Start-up is not steady state.** The agent's first seconds (loading, first hardware scan) read 0.8 % and coloured the header as "over budget". The over-budget flag is now judged on a 30-second average, for the agent and for the window, and only once 30 seconds have passed.
> - 🧪 **Test scripts must clean up after themselves.** Ten test copies of the window were left running because the probe's output was piped through a command that stops the pipeline early (`Select-Object -First`), which skipped the clean-up and skewed later measurements. Capture the output first, then trim it.
> - Still to do: the frame-budget bar, running/warm-up/error states with phase progress, Save report, tray icon and notifications, high-contrast and 150 % / 200 % screenshots, Tips content.
>
> 🧪 **A frame cap must not be judged on raw frame-time spread from one window.** Two rig runs of the same build on the same laptop decided differently: in one the 60 FPS cap trial measured 0.04 ms of frame-time variation and was kept (60 FPS, 0 stutters per minute); in the next a single hiccup put the capped window at 0.9 ms against an unusually smooth 0.4 ms uncapped window, the cap was rolled back, and the validated result ran uncapped at 85 FPS with 3.4 ms of variation and **twice the stutters of the unoptimised game** (11.3 → 22.9 per minute). A 0.9 ms variation on a 16.7 ms frame cannot be seen. ✏️ The cap is now kept when it holds and its variation is either no worse than before (+5 %) **or below 10 % of the capped frame period** (1.7 ms at 60 FPS, 3.3 ms at 30 FPS); it is still rolled back when it makes pacing visibly worse. A unit test covers the case and fails under the old rule.

**Principles**

- **Minimal.** Every element must earn its place. Use whitespace and typography for hierarchy, not decoration. Have one primary action per screen.
- **Flat.**
  - The window background is one solid colour.
  - Cards are solid surfaces with a 1 px hairline border.
  - No gradients anywhere: background, buttons, text, charts, progress.
  - No glows, neon, blur/Acrylic/Mica backdrops, or decorative shadows. At most the system's default elevation for flyouts and dialogs.
- **Native.** It should look like a well-made Windows 11 app: Fluent controls, Segoe UI Variable, standard spacing, the system title bar in the current theme, and standard focus visuals.
- **Colour means something.** One accent colour for the primary action and selection. Green, amber and red only for *kept*, *rolled back* and *worse/error*. Stage colours only inside the frame-budget bar and its legend. Everything else is neutral.

**Theme: follow Windows automatically**

- Read the setting at start-up and follow changes live, without a restart:
  - `AppsUseLightTheme` under `HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`: 1 = light, 0 = dark.
  - In WinUI 3, leave `RequestedTheme` unset and listen to `UISettings.ColorValuesChanged`.
- The title bar follows the theme (`DWMWA_USE_IMMERSIVE_DARK_MODE` for any custom window chrome).
- **High contrast:** when Windows high-contrast mode is on, switch to the system high-contrast colours. No custom colours.
- A setting (*Theme: System · Light · Dark*) defaults to **System**.
- Test every screen in light, dark and high contrast, at 100 %, 150 % and 200 % scaling (III.5 UI tests).

**Colour tokens** (the same token names in both themes; all flat)

| Token | Light | Dark | Use |
|---|---|---|---|
| `background` | `#F5F6F8` | `#1C1D21` | The window. **One flat colour.** |
| `surface` | `#FFFFFF` | `#25262B` | Cards, list rows, the chart area |
| `border` | `#E2E4E9` | `#34363C` | 1 px hairlines around cards, dividers |
| `text` | `#16181D` | `#F2F3F5` | Primary text and big numbers |
| `text-secondary` | `#5B6170` | `#A8ACB5` | Descriptions, labels |
| `text-tertiary` | `#8A909B` | `#7D828D` | Captions, units, section labels |
| `accent` | `#2563EB` | `#6EA8FE` | Primary button, selected tab/segment, focus ring, chart line (use the Windows accent colour instead if the brand allows) |
| `good` | `#15803D` | `#4ADE80` | Kept / improved |
| `warn` | `#B45309` | `#FBBF24` | Rolled back / caution |
| `bad` | `#B91C1C` | `#F87171` | Worse / error |
| Stage colours | muted: simulation `#C2417A`/`#E27AA8`, rendering `#1D7FB5`/`#5BB3E0`, present `#6D5BD0`/`#A499F0`, streaming `#B7791F`/`#E6B450`, other `#8A909B`/`#7D828D` | | Frame-budget bar and its legend only |

All text/background pairs must meet WCAG AA (4.5 : 1 for body text, 3 : 1 for large numbers and UI parts).

**Typography**

Segoe UI Variable (*Display* for the result number and titles, *Text* for everything else), falling back to Segoe UI. Tabular figures for all numbers. Solid colours only.

| Role | Size / weight |
|---|---|
| Result number | 56–64 px, semibold, `text` colour (not gradient) |
| Page / card title | 20 px semibold |
| Body | 14 px regular |
| Secondary | 12–13 px regular, `text-secondary` |
| Section labels | 12 px semibold, `text-tertiary`, sentence case (no ALL CAPS) |

**Layout**

- 4/8 px spacing grid. 24 px page padding. 16 px gaps between cards.
- 8 px corner radius (the Windows 11 standard) on cards, buttons and inputs.
- Plenty of whitespace. No more than 3–4 numbers in one visual group.
- **Home:** the main card (≈ 64 %) holds, top to bottom:
  - the game name and status;
  - four stat tiles (FPS, 1 % low, frame time, stutters/min);
  - the frame-time chart;
  - the current bottleneck (one sentence + the frame-budget bar);
  - the goal selector;
  - one primary **Optimise** button.

  A narrower "Your PC" card (≈ 36 %) lists seven hardware rows, each with an icon, a title and one line of detail.
- **Header:** the product name, the connection status, and GAPO's own CPU/RAM use as plain text (it turns `warn` when over budget).
- Minimum window 900 × 620. It must work at 1366 × 768 at 100 % and at 150 % scaling.

**Components (flat versions)**

- **Buttons:** one filled `accent` button for the primary action. Secondary actions are outlined or text buttons.
- **Stat tiles:** label, value, unit. No background tint.
- **Frame-time chart:** a single 1.5 px `accent` line on the `surface` background. Dashed 60/30 FPS guide lines in `border` colour. No area fill, or at most a flat 8 %-opacity fill. Decimate to ≤ 150 points, keeping spikes.
- **Frame-budget bar:** flat stage segments on a `border`-coloured track, with a thin `text`-coloured budget marker.
- **Segmented goal control:** Fluent style. The selected segment is `accent` with white text.
- **Visual-cost indicator:** five small dots.
- **Status tags** ("Kept", "Rolled back", "+46 %"): text in `good`/`warn`/`bad` on a 10 %-opacity flat tint of the same colour.
- **Info boxes:** a `surface` card with a coloured 3 px left edge. No tinted gradient.
- **Loading:** a flat skeleton, or a standard progress bar or ring.
- **Icons:** Fluent System Icons (regular weight, 16/20 px), monochrome in `text-secondary`, and `accent` only when interactive.

**Motion** (subtle, functional only)

- 150–200 ms ease-out for state changes.
- The result number counts up once (≤ 1 s).
- Progress fills by phase.
- **No motion during measurement windows.**
- No looping, pulsing or glowing animations.
- Honour *Settings → Accessibility → Visual effects → Animation effects*.

**Simple view vs "Show more options" (progressive disclosure)**

- **Simple view:**
  - One big number: the % change in average FPS, in the `text` colour, with the word "faster" beside it.
  - One line: "2.3× less work per frame".
  - 3–4 before → after rows: average FPS, 1 % low, stutters per minute, frame work (ms), each with a small `good`/`bad` delta tag.
  - One sentence: *"Kept 5 changes, rolled back 1 that didn't prove itself. Locked to 60 FPS to match your 60 Hz display."*
  - Actions: **Show more options** (primary), **Revert all**, **Optimise again**, **Save report**.
  - Below: a before/after frame-time chart (before in `text-tertiary`, after in `accent`) and a short list of what changed.
- **Show more options** — five tabs (Fluent `SelectorBar` or `Pivot`):
  1. **What I changed.** One card per experiment: hypothesis, measured before → after, visual cost, decision and reason. Followed by "Considered but not changed".
  2. **Diagnosis.** Before/after bottleneck with frame-budget bars on a shared scale. "Why am I not reaching my target?" with ranked remaining interventions. Engine limitations. Evidence. Measurement quality (noise floor, frames, windows, overhead).
  3. **Your PC.** Hardware cards, "How GAPO used your hardware", PC class, and what could not be read.
  4. **Tips.** Evidence-based recommendations with buttons that open the exact Windows Settings page, and background activity (protected processes are marked "never touched").
  5. **Live analyser.** Per-core bars, game CPU, background CPU, memory, disk, frame metrics, and GAPO's own overhead against its budget.

**States to design**

- No game (explain, with a "Launch" button).
- Scanning (skeleton).
- Waiting for warm-up (explain why).
- Running (progress + phase + hypothesis + live chart + a list of kept/rolled-back rows).
- Results.
- Error (plain words and what was restored).
- Over budget (the overhead text turns `warn`).

**Copy rules**

- Plain language, numbers with units, always the "why".
- Never "boost", "turbo" or "up to 200 %".
- Say what was **not** changed and why.
- Use "rolled back" rather than "failed" for changes that simply didn't help.
- No emoji in the product UI.

**Windows integration:** the system title bar in the current theme, Windows 11 rounded corners (system default), a simple flat app icon, a tray icon with status, and toast notifications ("GAPO: +80 % in <game> — details").

**What not to do** (the "made with AI" look): gradient backgrounds, gradient text or buttons, radial glows, neon cyan/violet palettes, glassmorphism, heavy drop shadows, ALL-CAPS micro-labels everywhere, decorative sparkle icons, pulsing effects, and dense dashboards that show every number at once.

## III.9 Pitfalls to avoid (summary)

- Measuring too early (warm-up), on different scenes, or before a change was acknowledged.
- Judging throughput on FPS while a cap is active. Judge on busy time.
- Trusting 1 % lows from short windows.
- Letting periodic hitches leak into experiments. Fix them first, and observe longer than their period.
- Mixing frames from different settings in one statistic.
- Heavy queries or UI animation during measurement. The optimiser becomes the bottleneck.
- Evidence sentences that aren't conditional on data ("mostly idle" when it isn't).
- Assuming defaults: process priority, refresh rate, module counts, drive types, "Windows 10" names.
- Writing config files non-atomically, while the game runs if it rewrites on exit, or against cloud sync.
- Vulnerable drivers, injection, memory reads (anti-cheat), unsigned binaries (SmartScreen).
- Hiding rolled-back changes, or claiming gains within the noise.

## III.10 Distribution, trust and legal

- **Code signing** (EV certificate or Azure Trusted Signing) for every binary and the installer.
- **Installer:** MSIX or WiX. Uninstall runs "Revert all" from the journal.
- **Updates:** signed delta updates. Never update during a game session.
- **Privacy:**
  - Local-first.
  - Sharing results to the knowledge base is **opt-in**, anonymised (no user names, paths or serial numbers), with retention limits and GDPR/CCPA documentation.
  - A "copy support bundle" button for user-initiated sharing.
- **Anti-cheat:** keep to files, public APIs and ETW. Build a per-game policy list, and talk to the vendors (EAC, BattlEye, Vanguard, etc.) before shipping adapters for protected games.
- **Claims:** marketing claims must match measured results with methodology ("measured on the same scene, noise floor ±x %").

---

# Part IV — Appendices

## Appendix A — How the demo maps to the product

| Demo file (repo `demo/src/…`) | What it does | Product equivalent |
|---|---|---|
| `Shared/Telemetry.cs` | Shared-memory frame ring (4,096 × 120 B records, seqlock), commands, toasts ✏️ *(this row said 128 B; the shipped record is 120 bytes, measured while porting — the product pins the layout with tests)* | PresentMon/ETW stream in the telemetry service, plus an optional partner engine-hook SDK |
| `Shared/SandboxSettings.cs` | INI settings model, atomic save, tolerant load | One *game adapter* per game |
| `Sandbox/*` | Fixture game with controllable bottlenecks, calibration, benchmark flight, telemetry | Keep as a **test fixture** and extend it to cover every bottleneck class |
| `Optimiser/Hardware/HardwareScanner.cs` | One-shot inventory (native APIs + registry + WMI with timeouts, "could not be read" notes, PC class) | Inventory module (cached, rescanned on device/driver change) |
| `Optimiser/Monitoring/SystemMonitor.cs` | 1 Hz per-core, memory, game CPU/IO/memory, own overhead; on-demand kernel process snapshot | Agent monitor + telemetry service |
| `Optimiser/Game/GameLink.cs` | Telemetry reader, settings writer with revision acknowledgement, benchmark commands | Adapter host + PresentMon client |
| `Optimiser/Engine/FrameStats.cs` | Median/p95/p99, 1 %/0.1 % lows, stutters, stage medians | Statistics library (add confidence intervals) |
| `Optimiser/Engine/Diagnosis.cs` | Stage-time bottleneck classification, conditional evidence, engine-limitation text | Diagnosis engine (add GPU/VRAM/thermal/power/latency/network) |
| `Optimiser/Engine/KnowledgeBase.cs` | Setting ladders, visual loss, predicted savings, "not worth touching" | Knowledge base + learned predictions |
| `Optimiser/Engine/OptimisationEngine.cs` | The closed loop (A/A, hitch-first, value ordering, stop-at-target, give-back, pacing, validation, revert) | Experiment engine + transaction journal |
| `Optimiser/Engine/Report.cs` | Report model, text export, background-app contention, recommendations | Reports, tips, support bundle |
| `Optimiser/UI/*` | Immediate-mode custom-drawn UI (demo-only gradient styling), charts, icons, auto-test screenshots | WinUI 3 app with the same layouts and information hierarchy but the minimal, flat, theme-following design of III.8 |
| `.github/workflows/windows-demo.yml` | Windows build + end-to-end auto-test + report/overhead/screenshots in logs | The start of the Windows test pipeline (III.6) |

## Appendix B — Measured results (Windows CI, GitHub Actions `windows-latest`)

Machine: Windows Server 2025 Datacenter 24H2 (build 26100), AMD EPYC 7763 (1 core / 2 threads visible), 8 GB RAM, Microsoft Hyper-V Video (no GPU), 1024 × 768 at 64 Hz, virtual disks. As a gaming PC it is entry-level.

### B.1 End-to-end runs (goal: Balanced, target = display refresh)

| CI run | What changed in the build | Before: avg / 1 % low FPS | After: avg / 1 % low FPS | Headline | Frame work (median) | Stutters / min | Kept / rolled back | Time | Noise floor | GAPO CPU (run avg / inside windows) | GAPO RAM |
|---|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| #4 | First end-to-end build | 35.2 / 13.7 | 63.2 / 48.2 | **+80 %** | 27.6 → 12.1 ms | 47.6 → 0 | 5 / 0 | 31 s | ±7.7 % | 2.22 % / 1.67 % ✗ over budget | 42 MB |
| #5 | UI quiet during windows, cheaper drawing | 46.4 / 19.2 | 63.3 / 53.1 | **+36 %** | 20.2 → 8.9 ms | 21.3 → 0 | 5 / 2 | 38 s | ±2.5 % | **0.39 % / 0.00 %** ✓ | 37 MB |
| #6 | Connection-race fix, predictive give-back, heavier calibration | 39.1 / 18.0 | 87.9 / 62.0 | **+125 %** | 25.2 → 11.3 ms | 34.8 → 0 | 4 / 1 | 31 s | ±2.5 % | **0.55 % / 0.06 %** ✓ | 36 MB |
| #7 | Final build (1 Hz dashboard, specific pacing reasons) — same code as the committed release | 41.8 / 18.6 | 63.5 / 56.9 | **+52 %** (cap kept, 64 Hz) | 23.5 → 10.6 ms | 36.1 → 0 | 5 / 0 | 31 s | ±4.1 % | **0.60 % / 0.03 %** ✓ | 36 MB |

Notes:

- The headline is the change in **average FPS**. In runs #4, #5 and #7 a frame cap at the display refresh (64 Hz on this VM) was kept, which caps the headline; in run #6 the cap was measured, found not to make pacing smoother on this VM, and rolled back, so the headline shows the uncapped gain. The pacing decision is borderline on this VM and flips with measurement noise. In the product, borderline decisions should be re-tested over a longer window before being reported. "Frame work" shows the real reduction in work per frame (2.2–2.3× in every run).
- Run #5's baseline was lighter (46 FPS) because the sandbox's calibration had just gained its *detail* knob. For run #6 the calibration was made slightly heavier (≈ 30 ms Ultra budget); the unoptimised game then ran at 38–39 FPS with a freeze every 10 s — "a tiny bit of lag".
- "Inside windows" is the optimiser's CPU while a measurement window was open. After the fixes it was 0.00–0.06 % of the PC.
- Session averages (including the one-off start-up hardware scan and the CI screenshot capture) were 1.1–1.2 %. The peaks (≈ 9–10 %) come from the WMI hardware scan at start-up, which is why that scan runs before anything is measured.

### B.2 Run #6 (final build) in detail

Diagnosis before: *"Rendering is stuck on one CPU thread — rendering takes 18 ms of each 25 ms frame, all on 1 of your 2 CPU threads."* Evidence: per-core load 52 % / 53 %; the game's busiest thread at **97 % of one core**; frame work 25 ms against a 16 ms budget; a 77 ms autosave freeze every 10 s.

| # | Change | Hypothesis (short) | Predicted | Measured | Decision |
|---:|---|---|---:|---|---|
| 1 | Autosave interval 10 s → 5 min | A blocking save freezes the game for ~77 ms every 10 s | — | 1 freeze in 14 s before → 0 in the 21 s after | **Kept** (done first; verified) |
| 2 | Anti-aliasing 4× SSAA → FXAA | 4× the pixels for every pass | −11.8 ms | 25.2 → 14.8 ms (+41 %), 39 → 67 FPS | **Kept** (visual cost 2/10) |
| 3 | Physics substeps 8 → 4 | Collision solved 8× per frame | −2.0 ms | 14.8 → 12.3 ms (+17 %) | **Kept** (visual cost 0) |
| 4 | Render threads 1 → 2 | Test whether SMT helps this game | −0.8 ms | 12.3 → 11.3 ms (+8 %) | **Kept** (visual cost 0) |
| 5 | Frame-rate cap → 64 FPS | Even pacing at the display refresh | — | pacing not smoother on this VM | **Rolled back** |

Not changed, with reasons: texture quality (memory not under pressure), present mode (0.8 ms), process priority (background CPU low), and further visual reductions — simulation distance, terrain LOD, physics 4 → 2, particles — because the target was already met with headroom.

Optimiser overhead during the session: 51 repaints averaging 11.9 ms; 0.55 % CPU averaged over the optimisation run and 0.06 % inside measurement windows; 1.1 % over the whole session including the one-off start-up hardware scan (the 8.7 % peak) and the CI screenshot capture; 36–38 MB private memory.

The committed release binaries (`demo/release/GAPO-Demo/*.exe`, the files users download) were also launched on the same Windows runner: the sandbox ran, and the optimiser scanned the hardware and connected to the game.

### B.3 Development machine run (Linux, Mono runtime, 4 threads — not Windows)

Same engine, different hardware: render threads 1 → 4 was kept (+40–49 %), then anti-aliasing SSAA → FXAA, simulation distance Unlimited → Far (+35–40 %), physics substeps rolled back (within ±10 % noise), a quality give-back of anti-aliasing tried and rolled back (it would have broken the budget), and a 60 FPS cap. The average FPS went from 13.5–20 to 60 (Mono is several times slower than .NET on Windows, so these absolute numbers are not representative). This shows the hardware-adaptive behaviour: with spare cores the engine parallelises first; without them it removes wasted pixel work first.

### B.4 ➕ Physical entry-level laptop (product build, 12 November 2026)

Machine: Dell Latitude E6430, Intel Core i7-3520M (2 cores / 4 threads, 2.9 GHz), Intel HD Graphics 4000, 16 GB DDR3-1600 dual-channel, SATA SSD, 1600 × 900 at 60 Hz, Windows 10 Pro 22H2 (build 19045). This is the "entry-level 2-core/4-thread PC" the III.7 budgets are written for, and it is the product build's physical test machine next to the GitHub Actions runner.

**The demo, unchanged, on this laptop (goal Balanced, target 60 FPS):** 26.1 → 68.1 average FPS (**+161 %**), 1 % low 11.2 → 58.0, 0.1 % low 9.6 → 56.2, frame work 37.1 → 14.5 ms (2.6× less), stutters 20.2 → 0 per minute, 32 s, 8 windows, noise floor ±2.5 %. Optimiser: 0.33 % CPU run average, 0.00 % inside windows (tick-based figure — see the I.6 row "Own CPU time"), 9.2 % peak, 34 MB private, 45 repaints averaging 14.2 ms. Kept: autosave 10 s → 5 min, render threads 1 → 2 (+22 %), physics substeps 8 → 4 (+13 %), 4× SSAA → FXAA (+42 %). Rolled back: 60 FPS cap (frame-time variation 0.7 → 1.4 ms). Not changed: textures, present mode, process priority (background 6 %). With two real cores the engine parallelised first (render threads), where the 1-core CI VM removed pixel work first — the same hardware-adaptive behaviour as B.3.

**The ported fixture game (.NET 10, D3D11 flip-model present on the HD 4000), unoptimised defaults:** 23–25 FPS, 1 % low 12–13, frame work 39–42 ms (rendering ≈ 31 ms, simulation ≈ 9 ms), one ≈ 85 ms autosave freeze per 10 s. Calibration reached its floor on this machine (base width 224, ray-march detail 0.45), so "Ultra" is heavier here than the 30–40 FPS the calibration aims for. A fixed workload (`--workload=<width>,<entities>,<detail>`) is available for runs that must be comparable.

**Product test rig, step 0 (fixture alone → fixture + agent → fixture alone):** 23.1 / 23.3 / 23.3 FPS; agent 0.019 % CPU average (0.35 % peak, cycle-counted), 6.0 MB private, 456 frames read through the engine hook; gate passed.

**The same rig on the GitHub Actions `windows-latest` runner (first product CI run, 12 November 2026, green on the first attempt):** fixture alone 39.4 and 39.3 FPS, with the agent attached 39.5 FPS (inside the ±2.5 % noise floor); frame work 25.0–25.4 ms (rendering ≈ 17 ms, simulation ≈ 7 ms); 1 % low 20–25 FPS; autosave freeze ≈ 50–55 ms. Agent: **0.064 % CPU average, 0.40 % peak, 6.1 MB private**, 781 frames read, EcoQoS and below-normal priority accepted. The fixture's D3D11 swap chain worked there on the *Microsoft Basic Render Driver* (no GPU). Build, the no-comments check and 89 unit tests ran on the runner before the end-to-end step.

## Appendix C — Definition of done for the MVP

- [ ] Detects the game, its settings and its anti-cheat policy. Refuses gracefully when unsupported.
- [ ] Frame timing via PresentMon for DX11/DX12/Vulkan, including busy times.
- [ ] Hardware inventory with a "could not be read" list, no admin, no driver.
- [ ] Waits out warm-up. A/A noise floor. Identical-scene windows. Acknowledged changes.
- [ ] Diagnosis covers at least CPU main thread, CPU parallel, GPU, VRAM, RAM pressure, storage hitch, background contention, pacing and transient.
- [ ] One change at a time, value-ordered, stop-at-target, pacing last, quality give-back, validation.
- [ ] Every decision explained. Rolled-back changes shown. "Considered but not changed" shown.
- [ ] Revert all + crash-safe journal + uninstall restore.
- [ ] Simple view (one number + before → after) and "Show more options" (5 tabs).
- [ ] Own overhead shown live. CI overhead gate green on the entry-level profile.
- [ ] Runs clean on the virtual Windows test machine and on the physical hardware matrix.
- [ ] No code comments in product source (CI check green).
- [ ] Signed binaries, installer, updater, privacy statement.

## Appendix D — Glossary

| Term | Meaning |
|---|---|
| Frame time | Time between two presented frames; what the player feels. |
| Busy time / frame work | Time the game actually worked on a frame, excluding waiting for a cap or VSync; what experiments are judged on. |
| Frame budget | 1000 ÷ target FPS (e.g. 16.67 ms for 60 FPS). |
| 1 % / 0.1 % low | FPS equivalent of the average of the slowest 1 % / 0.1 % of frames. |
| Stutter | A frame much slower than the typical frame (demo: > max(1.8 × median, median + 20 ms), or a marked hitch visibly longer than usual). |
| Noise floor | Smallest change that can be told apart from measurement noise; from the A/A baseline. |
| A/A test | Measuring the same configuration twice to learn the measurement noise. |
| Hitch fix | A change that removes sporadic freezes (autosave, streaming) rather than raising throughput. |
| Quality give-back | Restoring a previously lowered setting when there is ample headroom, keeping it only if the target still holds. |
| Visual loss | 0–10 estimate of how visible a change is; divides the predicted saving when ranking candidates. |
| Category A / B / C / D | External fix / game setting / engine limitation / transient condition (§3, with D added). |
