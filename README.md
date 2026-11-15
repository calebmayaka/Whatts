# Whatts

A small Windows app that shows your laptop's live battery power (watts), voltage, current, charge, battery health and temperatures. It shows a 10-minute power graph and tracks each plugged-in or on-battery stretch. The current wattage also appears as a number in the system tray.

- Needs no admin rights and no drivers. The app is a single ~260 KB exe, and the installer is about 2 MB.
- It reads the battery through the Windows battery driver, the same source as the Windows battery icon. Temperatures come from the ACPI thermal-zone performance counters.

## Install

Run `Whatts-Setup-<version>.exe`. The wizard installs for your user account only, so it doesn't ask for admin rights. It:

- adds a Start Menu shortcut
- can start Whatts when you sign in (on by default)
- can add a desktop shortcut

If the .NET 10 Desktop Runtime is missing, setup offers to download it from Microsoft. That step is the only one that asks for admin rights. Uninstall from **Settings → Apps**; this also removes the sign-in entry and saved settings.

## Using it

| | |
|---|---|
| **Win+Shift+W** | Show or hide the window from anywhere. If another app already uses that key, Whatts uses **Ctrl+Alt+W** instead. The right-click menu shows which one is active. |
| Tray icon | Live watts, rounded. The bar under the number is blue while charging and orange on battery. Left-click to open the window. |
| Minimize | Hides the window to the tray. Closing the window exits the app. |
| Right-click | **Always on top**, **Start with Windows**, **Exit**. |
| Section headers | Click to collapse or expand. **Details** starts collapsed. |
| Graph | Hover to read the exact value at any moment. |

Opening Whatts again while it's running brings up the existing window. Window position, always-on-top and collapsed sections are remembered in `%LOCALAPPDATA%\Whatts\settings.json`.

## What it shows

| Field | Meaning |
|---|---|
| **Power** | Power going into the battery (+) or coming out of it (−). |
| **Voltage / Current** | Battery terminal voltage. Current is power ÷ voltage. |
| **Charge** | Remaining energy and its share of the current full-charge capacity. |
| **Health** | Full-charge capacity as a percentage of the design capacity. |
| **Session** | Since you last plugged in or unplugged (or since launch): duration, energy added or used, average power, peak power, and start → current charge. Energy and average come from the fuel gauge, so they stay steady even when the live rate jumps around. Average appears after 2 minutes. |
| **Thermal zones** | ACPI sensors defined by the firmware (CPU/SoC, chassis, skin and so on). |
| **Details** | Cycle count, battery temperature (most batteries don't report it), chemistry and model. |

## Limits

These come from Windows and the laptop firmware, not from this app:

- **Charger (adapter) wattage isn't exposed.** Windows has no standard API for it. While plugged in, **Power** is the power going into the battery, not the total coming from the wall. On battery, it is roughly what the whole laptop is drawing. To compare chargers, use the session's **Peak charging** and **Average**.
- **The rate updates on the firmware's schedule.** Many batteries refresh it only every 5–30 s and may briefly report 0 W.
- **Thermal zones aren't per-core CPU temperatures.** Those need a kernel driver, which this app avoids.
- **x64 Windows only.**

## Build

Requires the .NET 10 SDK. Building the installer also requires Inno Setup 6 (`winget install JRSoftware.InnoSetup`).

```powershell
dotnet run -c Release          # run from source
.\build.ps1                    # -> installer\Output\Whatts-Setup-<version>.exe
.\build.ps1 -NoInstaller       # -> publish\Whatts.exe only
```

The version comes from `<Version>` in [Whatts.csproj](Whatts.csproj).

## Code layout

- [Battery.cs](Battery.cs): enumerates batteries with SetupAPI and queries `IOCTL_BATTERY_QUERY_{TAG,INFORMATION,STATUS}`.
- [Thermal.cs](Thermal.cs): reads `\Thermal Zone Information(*)\High Precision Temperature` through PDH.
- [Session.cs](Session.cs): 10-minute power history and per-session stats.
- [MainForm.cs](MainForm.cs): custom-drawn window, graph, tray icon and global hotkey.
- [Settings.cs](Settings.cs): saved preferences and the "Start with Windows" Run key.
- [Program.cs](Program.cs): single-instance handling and `--tray` hidden start.
- [installer/Whatts.iss](installer/Whatts.iss): Inno Setup wizard script.
