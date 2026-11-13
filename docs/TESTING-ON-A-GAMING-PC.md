# Testing GAPO on a PC with Steam

This build has only been tested with GAPO's own test game. This test is the first time it meets real games, so expect rough edges, and please report what you see.

## What it does and does not do

- It **measures** any running Steam game: frames per second, frame time, stutters, the processor and graphics-card load, and after about half a minute of play it says **what limits the game**.
- It **changes settings only for Unreal Engine games without anti-cheat**. It changes one setting at a time in the game's `GameUserSettings.ini`, asks you to restart the game, and keeps the change only if the game measurably needs less work per frame. Every change can be undone with **Revert all**.
- It **never** injects into games, reads game memory, installs drivers or closes programs. Games with Easy Anti-Cheat or BattlEye are measured only; their files are never touched.

## How to run it

1. Unzip the folder anywhere (for example `C:\GAPO-test`).
2. Right-click **GAPO.exe** and choose **Run as administrator**. Frame timing uses Windows event tracing, which needs administrator rights; without them GAPO shows processor use only and says why.
3. Start a Steam game and play normally. Within about 5 seconds GAPO shows the game's name and live numbers. After the game has settled and about 20 seconds of play, **What limits this game** appears.
4. For an Unreal Engine game without anti-cheat, **Optimise** is offered. Choose a goal, press Optimise and keep playing. When GAPO wants the next test it **flashes its taskbar button** and shows **Restart the game to test: …**; close the game and start it again. It needs up to six restarts and shows the result at the end. (Windows does not show notifications for programs running as administrator, so in this test build the flashing taskbar button is the signal. Alt-Tab to GAPO when it flashes.)
5. To undo everything GAPO changed, press **Revert all**. A copy of the original settings file is also kept next to it as `GameUserSettings.ini.gapo-original`.

## What to send back

- A screenshot of the GAPO window while the game runs, and of the result if you optimised.
- The game's name, and whether the frames per second GAPO shows roughly match the game's own counter or Steam's FPS overlay.
- The files in `%LOCALAPPDATA%\GAPO\` (reports and the change journal), if any were written.
- Anything that looked wrong, was confusing, or made the game behave differently.

## Known gaps

- Only Steam libraries are searched; Epic, GOG and others are not yet.
- Graphics-card busy time is not available on GPUs with hardware-accelerated GPU scheduling turned on, or with software rendering; the diagnosis then relies on the processor figures.
- Without a built-in benchmark GAPO measures the scene you are playing, so keep to a similar activity while it measures; very different scenes make results noisy (GAPO then rolls changes back rather than guess).
