# GAPO 0.9 beta: user guide

GAPO measures your PC games while you play, finds out what limits them, and, only when you ask, tests one game setting at a time and keeps only the changes that measurably help. Every change can be undone.

## Install

1. Unzip `GAPO-0.9.0-win-x64.zip` anywhere (for example your Downloads folder).
2. Double-click **Install.cmd**. It copies GAPO to `%LOCALAPPDATA%\Programs\GAPO`, adds GAPO to the Start menu and the desktop, and registers it under **Settings → Apps** so you can uninstall it normally.
3. The installer offers to add your account to the Windows group **Performance Log Users**. Say **Y**. Windows asks for permission once. After that, sign out of Windows and back in. GAPO then reads frame times without running as administrator, and its Windows notifications work.
   If you say **N**, GAPO still works but shows only processor use. To see frame times, start it with **Run as administrator**.
4. Start **GAPO** from the Start menu.

GAPO is not code-signed yet, so Windows SmartScreen may say *"Windows protected your PC"*. Choose **More info → Run anyway**.

## Use

1. Start a Steam game and play normally. Within about 5 seconds GAPO shows the game's name, live FPS, frame time, stutters and processor load.
2. After about 20 seconds of play, **What limits this game** appears: for example the graphics card, one processor thread, or the whole processor. GAPO changes nothing for this. You can switch it off in Settings.
3. If GAPO knows the game's settings, **Optimise…** appears. Pick a goal (**Balanced** is a good start) and press it. GAPO shows a **plan**: which settings it may test, how many game restarts it needs, and an estimated time. Nothing changes until you press **Start**.
4. While it runs, a progress bar shows the step and the time left. For games that apply settings only at start-up, GAPO asks you to **close and restart the game** between tests. It flashes its taskbar button and sends a notification when it needs you.
5. At the end GAPO shows the frame rate before and after and every change with its reason. **Show more options** has the details.

## Stay in control

- **Revert all** puts every setting GAPO changed back to its original value. The original settings file is also kept next to the game's file as `*.gapo-original`.
- **Game settings** lists every setting GAPO knows for the game. You can lock settings GAPO must never touch, choose a value yourself, or put back a single setting.
- **Settings → Optimisation**:
  - **Optimiser** off: GAPO only measures.
  - **Ask me before each change**.
  - Allow or forbid frame-rate caps.
  - Frame-rate target.
  - Most restarts per optimisation.
- **FPS overlay**: a small box over the game with live FPS and, after an optimisation, the frame rate before and after. It is a separate GAPO window, never anything injected into the game. You can switch it off or move it in Settings. It cannot appear over a game in *exclusive* fullscreen; borderless and windowed games work.
- GAPO shows its own CPU and memory use in the top bar. It is designed to stay under 0.5 % CPU.

## Which games

| Game | What GAPO does |
|---|---|
| Any Steam game | Measures FPS, frame time, stutters and load; says what limits it |
| Unreal Engine games without anti-cheat, once the game has written its `GameUserSettings.ini` | Also optimises: lowers one graphics setting per game restart and keeps it only if each frame takes measurably less work |
| Games with Easy Anti-Cheat or BattlEye | Measured only; their files are never touched |
| Other engines | Measured only, for now |

GAPO **never** injects into games, reads game memory, installs drivers, changes BIOS settings or closes programs.

## Uninstall

**Settings → Apps → Installed apps → GAPO → Uninstall.** The uninstaller asks whether to put every game setting back first. Your reports and preferences stay in `%LOCALAPPDATA%\GAPO` unless you delete that folder.

## Known limits of this beta

- GAPO has been tested on its own test games on Windows 10 and on Windows Server CI. This beta is its first contact with real Steam games: please report what you see.
- Measuring without administrator rights through Performance Log Users is new in this beta. If GAPO still says frame times need administrator rights after you signed out and back in, run it as administrator and please report it.
- Only Steam libraries are searched. Epic, GOG and other launchers are not yet.
- Graphics-card busy time is not available with hardware-accelerated GPU scheduling turned on; the diagnosis then relies on processor figures.
- GAPO measures the scene you are playing. Stay in a similar activity while it measures. When the numbers are too noisy, it rolls a change back rather than guess.

## Report a problem

Send the game's name, a screenshot of GAPO, and the files in `%LOCALAPPDATA%\GAPO\reports`.
