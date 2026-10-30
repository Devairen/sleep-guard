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

## Why it cannot leave the PC stuck awake

The hold is a `SetThreadExecutionState` call, which lives only as long as this
process. It never edits the power plan. Quit the app, kill it, crash it, log
out — the normal timeouts come back immediately. The widget also starts in
**Auto** on every launch, on purpose, so a reboot can never leave the machine
silently pinned awake.

## Using it

- Click a pill position to switch. Drag the card anywhere; it remembers.
- The `✕` hides it to the tray. Only the tray menu quits.
- Tray icon: a ring that empties as the time runs out, so you can read the state
  without the card on screen.
- Tray menu: show/hide, keep above other windows, start with Windows, quit.
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
