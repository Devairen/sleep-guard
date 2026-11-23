# Sleep Guard

A small floating widget that decides whether this PC is allowed to sleep, so a
Claude Code session (or a download, or a build) is not cut off while you are
away from the desk.

Windows only. Roughly 54 KB, no installer, no runtime to fetch: it uses the
WPF and .NET Framework that already ship inside Windows 10 and 11.

## Install

Download `bin/SleepGuard.exe` and run it. That is all. The tray menu has
**Start with Windows** if you want it back after a reboot.

## What the three states mean

One pill, three positions. They escalate: each one includes the one before it.

| Position | What Windows is allowed to do |
|---|---|
| **Auto** | Normal behaviour. The screen blanks after 10 minutes, the PC sleeps after 1 hour. |
| **Awake** | The PC never sleeps. The screen is still allowed to blank, which is harmless to a running session. |
| **Screen** | Neither the screen nor the PC sleeps. Use it when you want to glance at progress from across the room. |

The sleep and screen timings in the widget's caption are read out of the live
Windows power plan, not hardcoded, so they stay correct if you change the plan.

## The time limit

Away from **Auto**, a second row opens: `1h / 2h / 4h / 8h / ∞`, and a footer
saying what happens at the end (hibernate by default). The widget counts down,
warns two minutes before with a tray balloon (clicking it adds 30 minutes), then
drops the hold and hibernates. This is the "don't stay awake all night because a
game is open" guard.

When the timer ends, the card switches back to Auto and shows it for a second
and a half before the PC goes down, so it wakes up showing the right state. If
the PC was already asleep or hibernated when the deadline passed (more than 90
seconds late), the card only goes back to Auto: it does not hibernate the PC
again the moment you wake it. On every wake it settles the clock and redraws.

## Why it cannot leave the PC stuck awake

The hold is a `SetThreadExecutionState` call, which lives only as long as this
process. It never edits the power plan. Quit the app, kill it, crash it, log
out — the normal timeouts come back immediately. The widget also starts in
**Auto** on every launch, on purpose, so a reboot can never leave the machine
silently pinned awake.

## Using it

- **Taskbar mode (default since 23/11/2026).** A chip inside the taskbar, next
  to the tray icons, shows the mode with the same icons as the pill (moon =
  Auto, bolt = Awake, monitor = Screen), a ring that empties as the time limit
  runs out, and the time left as h:mm. Click it and the card springs up above
  the taskbar; click anywhere else and it closes. Right-click gives the tray
  menu, where "Show in the taskbar" switches back to the desktop card below
  (live, no restart). The chip never takes focus, re-pins itself above the
  taskbar, and hides under full-screen apps. The card grows upwards when the
  timer row opens. Code: `src/TaskbarChip.cs` and the flyout part of
  `src/MainWindow.cs`. The Today card's chip finds this one by its window title
  "Sleep Guard chip" and sits to its left. The self-tests force the desktop card.
- Click a pill position to switch. Drag the card anywhere; it remembers (desktop mode).
- The `✕` hides it to the tray. Only the tray menu quits.
- Tray icon: a ring that empties as the time runs out, so you can read the state
  without the card on screen.
- It behaves like a desktop widget: other windows go over it, and Show desktop
  (Win+D, or the corner of the taskbar) reveals it instead of hiding it. The
  next window you pick goes back over it. A click on the tray icon brings it to
  the front without taking focus. Logic and the Windows quirks it works around:
  `src/DesktopLayer.cs`.
- Tray menu: show/hide, keep above other windows (pins it on top for good),
  start with Windows, quit.
- Starts with Windows via `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`,
  value `SleepGuard`.
- Settings live in `%APPDATA%\SleepGuard\settings.ini`.

## Building

```powershell
.\build\build.ps1
```

No SDK, no NuGet, no installs. It compiles with the C# compiler that ships
inside Windows (`csc.exe` from .NET Framework 4.8), which means C# 5 syntax
only: no string interpolation, no `?.`, no expression-bodied members. WPF built
in code, no XAML files. Result is a single ~54 KB exe in `bin\`.

Stop the running copy before rebuilding, or the compiler cannot overwrite the
exe.

## Checking the wake lock yourself

```powershell
.\bin\SleepGuard.exe --selftest
```

Writes `%TEMP%\sleepguard-selftest.txt`. It sets each state and reads back the
flags Windows stored, which is how you prove the hold registered rather than
trusting a message.

To see it from the system's side, with the widget on **Awake** or **Screen**,
run this in an **administrator** PowerShell — it needs elevation:

```powershell
powercfg /requests
```

`SleepGuard.exe` should appear under SYSTEM (and under DISPLAY in the Screen
state).

## Checking the card does not jump

```powershell
.\bin\SleepGuard.exe --geotest
```

Writes `%TEMP%\sleepguard-geotest.txt`. It opens the card at its saved spot,
arms it from code and logs the window position before and after. The two `top`
values must be identical: the top edge is the anchor, so the timer row grows
downwards instead of shifting the card under the cursor. The one exception is a
card parked so low that the taller version would fall off the bottom of that
monitor, which pulls it up by the minimum needed.

```powershell
.\bin\SleepGuard.exe --statetest
```

Writes `%TEMP%\sleepguard-statetest.txt`: four lines comparing the real state,
the position the slider is drawn at, and whether the timer row is open. It arms
Awake, lets the timer run out, lets it run out "while asleep", and goes Screen
then Auto. Every line after the first must read `state=Auto drawn=Auto
timerRow=closed`. The test ends with "then nothing", so it never powers the PC
down, and it puts `settings.ini` back exactly as it found it. Before the fix of
19/11/2026 line 2 read `state=Auto drawn=Awake`: a finished WPF animation kept
the slider on Awake, and a click on Auto was ignored.
