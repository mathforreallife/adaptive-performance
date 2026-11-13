# ADR 0005 — Frame and GPU timing from GAPO's own event-tracing consumer

Date: 2026-11-13 · Status: accepted

## Context

The spec plans frame timing "via PresentMon". At step 1 running PresentMon was not permitted, and shipping it means a separate process (the console build) or a 170 MB service. GAPO needs, per tracked game: frame time, CPU-side busy time, time inside the present call, and — to tell a graphics-card limit from a processor limit — how long the graphics card works on each frame. All of it must fit the agent's overhead budget (0.3 % CPU while a game runs) on a two-core laptop and work without the engine hook for games GAPO does not know.

## Decision

- **Presents:** a real-time ETW session (`GAPO-FrameTiming`) consumes Microsoft-Windows-DXGI (present start/stop 42/43, multiplane 55/56) and Microsoft-Windows-D3D9 (1/2) with an event-id filter. A pure `PresentTracker` turns them into frame time (present start to present start), CPU busy (previous present end to this present start) and time inside present.
- **Only the game's events:** when a game is tracked, both providers are re-enabled with a process-id filter, so other programs never emit present events into the session. If Windows refuses the filter the event-id filter alone stays in effect and the report says so.
- **GPU busy:** a second session (`GAPO-GpuTiming`) consumes Microsoft-Windows-DxgKrnl device and context start/stop, a capture-state rundown of existing devices and contexts, `QueuePacket_Start` (to learn the process of contexts that existed before the session) and `DmaPacket_Start`/`DmaPacket_Info`, with keywords Base | Resource | LongHaul and never the Performance keyword. Fields are found **by name through the event's own metadata** (`TdhGetEventInformation`, cached per event id and version) rather than hard-coded offsets. A pure `GpuBusyTracker`, ported from PresentMon's `GpuTrace` (DMA-packet path), keeps one in-order packet queue per adapter node and counts a process as busy while at least one of its packets runs on any node; missed events are skipped as PresentMon does. The counter is cumulative; each measurement takes the difference over its own window.
- **Diagnosis:** GPU busy per frame enters `PresentTiming.GpuBusyMs`. A graphics card busy for at least 95 % of each frame is the limit whatever the processor shows (drivers often spin a thread while waiting for the card); between 80 % and 95 % it is the limit only if no processor thread is pegged.
- **PresentMon** stays a development cross-check, downloaded on demand into `artifacts/tools` and never shipped.

## Consequences

- Measured on the entry-level laptop (Intel HD Graphics 4000): frame time within 0.0–0.3 % of the game's own telemetry and 0.10 % of PresentMon; GPU busy 0.99 vs 0.96 ms per frame (light load) and 31.48 vs 30.61 ms (heavy load) against PresentMon; 2.1–2.2 events per presented frame with the process filter (6.3 without); agent 0.19–0.21 % CPU while watching a game with both sessions running.
- Both sessions need administrator rights or membership of *Performance Log Users*; without them frame timing is reported as unavailable, not guessed.
- Software rendering (Microsoft Basic Render Driver, e.g. the CI runner) produces no DMA packets, so GPU busy is not measured there; the rig checks it only on hardware adapters.
- **Not covered yet:** GPUs with hardware-accelerated GPU scheduling (HAGS) report work through `HwQueue`/`QueuePacket` events instead of DMA packets. Neither the test laptop nor the CI runner has one, so that path is not implemented; on such a PC GPU busy will read zero and the diagnosis falls back to the processor rules. It must be added and tested on a HAGS-capable PC.
