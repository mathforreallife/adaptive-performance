# ADR 0003 — The optimiser measures its own CPU from cycle counts

Date: 2026-11-12 · Status: accepted

## Context

The optimiser must show its own CPU and memory use and fail the build when it exceeds the budgets of spec III.7. The first version of the gate read `GetProcessTimes` and reported 0.000 % CPU for a 20-second agent run.

Windows charges kernel and user time by sampling at the timer tick. A process that sleeps on the timer and wakes for a few microseconds is almost never on a CPU when the tick fires, so it is charged nothing. A 10 Hz agent is exactly that process. A gate that always reads zero protects nothing, and showing "0.00 %" to users would be an unverified claim.

## Decision

- Own CPU time comes from `QueryProcessCycleTime`, converted to seconds with the processor's nominal clock (`~MHz` in the registry). Cycle counts are exact per context switch.
- If either value is unavailable the probe falls back to `GetProcessTimes`, and the report states which source was used (`cpuMeasuredFromCycleCounts`).
- CPU is always expressed as a share of the whole PC (processor time ÷ wall time ÷ logical processors), as the spec's budget table does.
- Private bytes come from `K32GetProcessMemoryInfo` (`PrivateUsage`).
- Budgets are code (`OverheadBudget.For(state)`); the agent exits with a distinct code when over budget, and the end-to-end rig also checks that the game is not slower with the agent attached than the A/A noise floor.

## Consequences

- Converting cycles with the nominal clock states CPU time "at nominal frequency". When the CPU runs below nominal (power saving) or above it (turbo) the figure differs slightly from wall-clock CPU time; that is acceptable for a budget and far better than a constant zero.
- The same technique must be used when the product reports the CPU use of a game's threads over short intervals.
