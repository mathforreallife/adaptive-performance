# ADR 0002 — The fixture game and how it presents

Date: 2026-11-12 · Status: accepted

## Context

The spec keeps the demo sandbox "as a permanent test fixture" with configurable bottlenecks (I.7, III.5). The demo presented with GDI `BitBlt`, used no GPU and calibrated its workload per machine. The product measures frames with PresentMon (ETW present events) and must diagnose GPU-bound games.

## Decision

- The fixture is a **mechanical port** of the demo sandbox to .NET 10 (`fixture/Gapo.Sandbox`): same world, renderer, simulation and frame loop, comments removed. It is test equipment, so its internal style follows the prototype; the product's naming rules apply to `src/`.
- The **telemetry contract is unchanged** (map layout, 120-byte record, flags, commands) and lives in `fixture/Gapo.Sandbox.Contract`, rewritten cleanly and pinned by layout tests. It plays the role of the "optional partner engine hook" from the spec's architecture: the product can read it, but must not depend on it for real games.
- The fixture presents through a **D3D11 flip-model swap chain**. The software-rendered frame and HUD are uploaded to a texture and drawn with a full-screen pass; a loop in the pixel shader (`--gpu-load`) creates real GPU work that scales with resolution scale and anti-aliasing. If no hardware device can be created it uses WARP; if the swap chain cannot be created at all it falls back to GDI and records why in its result file.
- Workload is calibrated per machine by default (as in the demo, with wider limits) and can be fixed with `--workload` for comparable runs.
- Each bottleneck class has a switch or scenario; background CPU load comes from a separate process (`fixture/Gapo.LoadGenerator`), because it must not be part of the game.
- The fixture is strict: unknown arguments are an error, failures produce a non-zero exit code and an error file next to the result log.

## Consequences

- PresentMon sees the fixture like any Direct3D game, so the product's real frame-timing path can be tested on the fixture, with the engine hook as a second, richer source to compare against.
- The fixture depends on Vortice.Windows (MIT) for Direct3D bindings. It is not shipped with the product.
- Frame-for-frame determinism is still limited to the camera path (creature and particle state follow real time); measurement code must keep tolerating that.
