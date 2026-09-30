# Adaptive Performance — GAPO

**GAPO (Game Adaptive Performance Optimizer)** is an adaptive performance engine for PC games. It finds what is stopping the next frame from being produced, tests one fix at a time, keeps what measurably helps and rolls back what doesn't.

This repository contains:

| What | Where |
|---|---|
| **Ready-to-run Windows demo** (game + optimiser, no install) | [`demo/release/GAPO-Demo.zip`](demo/release/GAPO-Demo.zip), or the two `.exe` files in [`demo/release/GAPO-Demo/`](demo/release/GAPO-Demo) |
| **Handover spec for the commercial product**, including the lessons from building this demo | [`Adaptive-Performance-FINAL.md`](Adaptive-Performance-FINAL.md) |
| Demo source code (C#, .NET Framework 4.6.2, WinForms, no dependencies) | [`demo/src`](demo/src) |
| Windows CI that builds and runs the demo end to end | [`.github/workflows/windows-demo.yml`](.github/workflows/windows-demo.yml) |

## Run the demo (Windows 10 / 11)

1. Download [`demo/release/GAPO-Demo.zip`](demo/release/GAPO-Demo.zip) and extract it. It's two small programs, about 190 KB zipped.
2. Run **`GAPO-Sandbox.exe`**. This is a simple island sandbox game with deliberately wasteful "Ultra" defaults. It measures your PC for a second at start-up and scales its world so it runs a little laggy on any machine: roughly 30–40 FPS, with a short freeze every 10 s. The FPS counter and frame-time graph are in the top left.
3. Run **`GAPO-Optimiser.exe`** while the game is running. It scans your hardware, connects to the game and shows live FPS and the current bottleneck. Pick a goal (**Balanced** is a good start) and click **Optimise now**.
4. Wait about a minute. The optimiser measures a baseline twice (to learn how noisy measurements are on your PC), diagnoses the bottleneck, then tests one change at a time on a repeatable benchmark flight. It keeps what beats the noise and rolls back the rest.
5. The results screen shows the **percentage increase in average FPS**. **Show more options** reveals everything it did, in five tabs: every change with its measurements and reasoning, the before/after diagnosis, your PC, tips it deliberately didn't apply, and a live analyser.

**Revert all** in the optimiser, or **F5** in the game, restores the unoptimised settings. Nothing outside `%LOCALAPPDATA%\GAPO-Demo` is changed, apart from the game's process priority. The optimiser only tests that when background apps compete for the CPU, and Windows resets it when the game closes.

The demo is not code-signed, so Windows SmartScreen may say *"Windows protected your PC"*. Click **More info → Run anyway**.

The optimiser shows its own CPU and memory use in the top bar at all times. Keeping that overhead tiny is a core requirement: an optimiser that causes lag has failed.

## Build from source

Any OS with the .NET SDK (8+):

```bash
./demo/build.sh          # builds both programs and writes demo/release/GAPO-Demo.zip
```

The output targets .NET Framework 4.6.2, which ships with Windows 10 and 11, so the `.exe` files run without installing anything. Every push runs [`windows-demo.yml`](.github/workflows/windows-demo.yml) on a Windows runner. It builds both programs, runs the sandbox, runs the optimiser's full auto-test against it, prints the optimisation report and the optimiser's measured overhead, and uploads the binaries and screenshots as artifacts.

Useful switches:

| Program | Switch | Effect |
|---|---|---|
| `GAPO-Sandbox.exe` | `--reset` | Start from the unoptimised defaults |
| | `--recalibrate` | Re-measure this PC and rescale the demo workload |
| | `--autotest=SECONDS --screenshot=FILE --log=FILE` | Run, capture, write stats and exit (CI) |
| | `--headless=SECONDS` | Run the game loop without a window (testing off Windows) |
| `GAPO-Optimiser.exe` | `--autotest=DIR [--goal=Balanced\|MaxFps\|Quality\|Smooth]` | Optimise automatically, save screenshots and the report to `DIR`, then exit |
| | `--selftest` | Console-only run of the full engine against a running sandbox |

## How the demo is put together

```text
GAPO-Sandbox.exe                                   GAPO-Optimiser.exe
┌──────────────────────────────┐   shared memory   ┌──────────────────────────────────────┐
│ game loop (main thread)      │ ── frame times ─▶ │ GameLink      reads telemetry @10 Hz │
│  simulation: O(n²) AI+physics│    & stage costs  │ SystemMonitor per-core, game CPU/IO, │
│  software renderer (passes)  │                   │               own overhead @1 Hz     │
│  blocking autosave (hitch)   │ ◀─ benchmark cmd ─│ HardwareScanner once at start-up     │
│ hot-reloads settings INI     │ ◀─ settings INI ──│ Engine: observe → diagnose → predict │
│ reports applied revision     │   (atomic write)  │   → change one → measure → keep/revert│
└──────────────────────────────┘                   │ UI: simple result + Show more options│
                                                   └──────────────────────────────────────┘
```

The optimiser never injects into the game or reads its memory. It changes the game the same way a player would, by editing the game's settings file, and it proves every change with a measurement.
