# ADR 0004 — User interface: WinUI 3, unpackaged, built with the .NET SDK only

Date: 2026-11-13 · Status: accepted

## Context

The spec asks for a GPU-composited, native-looking Windows UI that follows the light, dark and high-contrast themes live, has one flat background colour and no gradients, glows or Mica/Acrylic backdrops (III.3, III.8). The demo's WinForms/GDI+ UI was the optimiser's largest CPU cost. The developer PC has no Visual Studio.

## Decision

- `src/Gapo.App` is a **WinUI 3** application on Windows App SDK 1.8.260921001 (first built on 2.6.5; see the amendment below for why it was moved back), **unpackaged** (`WindowsPackageType=None`) and **self-contained** (`WindowsAppSDKSelfContained=true`), targeting `net10.0-windows10.0.19041.0`, x64.
- It builds with `dotnet build` as part of `Gapo.slnx`; no Visual Studio or MSBuild from Visual Studio is needed. Verified on the developer laptop (Windows 10 22H2).
- Theme: the root element's `RequestedTheme` is left at its default so the app follows Windows; colours come from one resource dictionary with Light, Dark and HighContrast theme dictionaries (`Themes/Tokens.xaml`). High contrast uses only system colours. No system backdrop is set, so the window background is a single solid colour.
- The primary button uses GAPO's own accent token rather than the Windows accent colour, by overriding the accent-button brushes per theme. The Windows accent on the test PC was a mustard yellow with poor contrast against white text; the token pair is chosen to meet WCAG AA in both themes.
- The spec's `text-tertiary` values (#8A909B on light, #7D828D on dark) do not reach the 4.5 : 1 contrast the spec itself requires for small text, so the app uses #6F7582 and #9096A1.
- The app has a screenshot mode (`--report=<agent report> --screenshots=<folder> --theme=light|dark`) that renders the result view and each detail tab to PNG with `RenderTargetBitmap` and exits. The end-to-end rig runs it after the optimisation run, so every push produces pictures of every screen in both themes.
- First increment: the UI renders a finished optimisation from the agent's report file. The live link to the agent (named pipe) and the home view with live numbers come next; until then the app shows no controls that do nothing.

## Consequences

- The self-contained output folder is about 150 MB; packaging and trimming belong to beta hardening.
- A minimal WinUI 3 window used 30 MB of private memory and no measurable CPU when idle on the laptop, inside the 80 MB / 0.5 % budget for the UI process. This has to be re-measured once the live views exist.
- Whether WinUI 3 starts and renders on the GPU-less CI runner is decided by the first CI run of this step; if it cannot, screenshots stay a laptop-only check and the rig must skip them on machines without a graphics adapter.

## Amendment, 2026-11-13 — Windows App SDK pinned to 1.8, tiered PGO off, overhead gated

Measured on the developer laptop (Windows 10 22H2, Intel HD 4000, two cores / four threads) with the fixture game running in its default 1280 × 720 window:

- On **Windows App SDK 2.6.5** the compositor's render thread (`dwmcorei.dll` 12.6.5) never blocked: it looped through `NtGdiDdDDIWaitForVerticalBlankEvent`, `NtDCompositionGetBatchId` and timer calls and used **one whole logical processor (25 % of the PC)** for as long as the game was presenting. It did so with a completely static page, with the window in front of or behind the game, and with the game presenting through either DXGI or GDI. It stopped only when the window was minimised (0.02 %) or hidden (0 %), or when the game exited. Plain CPU load, a 1 ms timer resolution or an ordinary animating window did not trigger it.
- The same app built on **1.7.260224002** and on **1.8.260921001** does not do this (0.2 – 0.4 % with live data showing). The app is therefore pinned to **1.8.260921001**. Building that line without Visual Studio needs `EnableMsixTooling=true`.
- With the spin gone, the largest remaining cost was the .NET runtime re-compiling methods for profile-guided optimisation in the background (about 0.2 % of the PC for minutes after start). `TieredPGO` is off for the app, as it already was for the agent.
- `NamedPipeClientStream.ConnectAsync` with a timeout spin-waits while the pipe does not exist. The interface connects with a zero timeout and retries once a second instead.

The end-to-end rig now runs the interface with live data next to the fixture game and fails the build when the interface process exceeds its 0.5 % CPU / 80 MB budget (median of three 15-second slices after a 20-second settle; the CPU budget is enforced only when the game renders on a hardware adapter, and a ceiling of a quarter of one logical processor applies everywhere to catch a spinning thread). Moving to a newer Windows App SDK is allowed only if that check still passes on Windows 10.
