# ADR 0001 — Solution layout and the test rig

Date: 2026-11-12 · Status: accepted

## Context

The handover spec (Part III.4, step 0) asks for the test rig before any product logic: a virtual Windows test machine, the demo sandbox ported as a fixture, auto-test modes, the overhead meter and a budget gate. The developer PC has only the .NET SDK (no Visual Studio, no MSVC linker) and is itself an entry-level Windows 10 laptop (2 cores / 4 threads, Intel HD 4000).

## Decision

- One solution, `Gapo.slnx`, built with the .NET 10 SDK CLI only.
  - `src/` — product code. `Gapo.Core` is pure logic with no Windows dependency (so it is unit-testable anywhere); `Gapo.Windows` holds all P/Invoke; `Gapo.Agent` is the per-user process.
  - `fixture/` — the test fixture game, its telemetry/settings contract and helper executables. Not shipped.
  - `tests/` — unit tests per project, plus `Gapo.EndToEnd`, an executable that runs real processes on Windows and asserts on their results.
  - `tools/` — build-time tools (`Gapo.SourceRules`).
  - `demo/` — the prototype, kept as reference only. Excluded from the product build settings and from the no-comments rule.
- The test rig is **GitHub Actions `windows-latest`** (virtual, every push that touches code) plus **the developer laptop** (physical, entry-level, before every push). A persistent Windows 11 VM is not used: the only available host cannot run one.
- The end-to-end run is an executable rather than a script, so its logic is typed, testable and identical locally and in CI. It exits non-zero on any failed expectation.
- Test framework: xunit v3 on Microsoft.Testing.Platform (`global.json` opts `dotnet test` into it).
- Branches: one branch per build-order step (`step-N-…`), one pull request per step into the default branch, merged when the Windows run is green.

## Consequences

- NativeAOT cannot be published on the developer PC; AOT builds, when introduced, come from CI. Product projects are marked `IsAotCompatible` from the start so the analysers keep them publishable.
- The hosted runner has no GPU. GPU-bound behaviour can be exercised there only through WARP; real GPU behaviour is checked on the laptop's Intel HD 4000 until better hardware is available.
- Private-repository Windows minutes are limited, so the workflow ignores documentation-only pushes and cancels superseded runs.
