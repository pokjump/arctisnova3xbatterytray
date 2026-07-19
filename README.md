# ArctisBatteryTray

A Windows system tray battery indicator for the **SteelSeries Arctis Nova 3X Wireless** headset.

It reads battery state **directly from the USB dongle over HID** -- no SteelSeries GG required
(and it can run alongside GG without conflict, since it opens its own separate HID handle).

## What it shows

- **Tray icon** with the battery percent (e.g. `52`), colored by level: green at 50%+, orange at
  20-49%, red below 20%.
- A small yellow lightning bolt overlay while charging.
- A gray dash `-` when the headset is off or the dongle isn't found.
- **Tooltip** (on hover): `Arctis Nova 3X -- 52% (discharging)`, `... charging: 64%`,
  `Headset is off`, `USB dongle not found`.
- **Context menu** (right-click): current reading, "Start with Windows", "Refresh now", "Exit".
- **Left-click** shows a balloon notification with the current state.

Polling runs every **2 seconds**, plus an immediate poll on startup, on USB plug/unplug
(`WM_DEVICECHANGE`), and on wake from sleep.

## Verified hardware protocol

Determined empirically on real hardware (`--probe` mode) and cross-checked against the
[HeadsetControl](https://github.com/Sapd/HeadsetControl) parser
(`lib/devices/steelseries_arctis_nova_3p_wireless.hpp`).

| Parameter | Value |
|---|---|
| Vendor ID | `0x1038` (SteelSeries) |
| Product ID (Nova 3X Wireless, 2.4 GHz dongle) | `0x226d` |
| Product ID (twin Nova 3P Wireless model) | `0x2269` |
| Active HID interface | `mi_03` (`inLen=65`, `outLen=65`) |
| Battery request | Output Report `[0x00, 0xb0]` (report ID 0 + `0xb0`), zero-padded to 65 bytes |
| Raw response (65 B, example while discharging) | `00 b0 03 02 47 03 64 64 00 ...` |
| Raw response (65 B, example while charging over cable) | `00 b0 03 02 41 01 64 64 00 ...` |

### Response layout

HidSharp prepends a **report-ID byte (`0x00`) at index 0**, and the actual payload starts at the
`0xb0` marker:

```
buf:   [0]=0x00 (report ID)  [1]=0xb0 (marker)  [2]=connection  [3]=_  [4]=battery%  [5]=charging  [6..7]=0x64 0x64  ...
```

- **Battery** = `buf[markerIdx + 3]` -- a **direct 0-100 percent** (no 0-4 scale; confirmed by
  HeadsetControl's `map(raw,0,100,0,100)`). Example: `0x47` = 71%.
- **Connection** = `buf[markerIdx + 1]`: the only verified value is `0x02` = `HEADSET_OFFLINE`
  (exactly what HeadsetControl checks -- that's all they look at). A value of `0x03` in practice
  just meant "battery available"; it says nothing about charging.
- **Charging** = `buf[markerIdx + 4]` -- the byte immediately after the battery percent, outside
  the 4-byte window HeadsetControl reads (which is why their parser doesn't decode it). Verified
  empirically across two confirmed states:
  - `0x01` -- headset is charging over cable,
  - `0x03` -- headset is running wirelessly, discharging.

  These are the same values an earlier hardware plan for the Nova 7 family assumed (`1`=charging,
  `3`=discharging) -- the semantics were right, just the offset was off by one byte.

Interface `mi_04` has `outLen=0` and returns `IOException: Can't write to this device` -- it's
skipped. Interface `mi_05` never responds to `0xb0`. Only `mi_03` is used.

## Building

Requires the .NET SDK (project targets `net10.0-windows`, WinForms).

```bash
dotnet build -c Debug
```

### Publishing a single self-contained .exe

```bash
dotnet publish -c Release -r win-x64 --self-contained \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

Output: `bin/Release/net10.0-windows/win-x64/publish/ArctisBatteryTray.exe` -- a single file, no
.NET installation required on the target machine. The `.csproj` already sets
`EnableCompressionInSingleFile=true`, so this file is automatically compressed (~47 MB, vs.
~116 MB uncompressed) -- no extra flag needed.

### Publishing a lightweight .exe (target machine already has .NET 10)

If the target machine already has the **.NET 10 Desktop Runtime** installed, you don't need to
bundle the runtime at all:

```bash
dotnet publish -c Release -r win-x64 --self-contained false \
  -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=false
```

Output: same path, but only **~450 KB** -- it contains just the app, not the .NET runtime.
(`EnableCompressionInSingleFile` must be turned off here since compression only applies to
self-contained publishes.) The target machine needs the
[.NET 10 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/10.0) installed, otherwise
the app won't start.

## Running

- **Normal use:** double-click `ArctisBatteryTray.exe` -- a tray icon appears. Single instance
  only (named mutex `Global\ArctisBatteryTray`).
- **Autostart:** enabled by default on first run (`HKCU\...\Run` entry). Toggle it from the
  context menu.
- **Diagnostics:** `ArctisBatteryTray.exe --probe` lists all VID `0x1038` devices, sends
  `[0x00,0xb0]` to each, and prints the raw hex responses. Use this if a firmware update changes
  the PID or byte layout.
- **Debug logging:** `ArctisBatteryTray.exe --debug` writes a detailed log to
  `%LocalAppData%\ArctisBatteryTray\log.txt` (rotates at 1 MB).

## Architecture

| File | Role |
|---|---|
| `Program.cs` | Entry point: single-instance mutex, `--probe` mode, windowless `ApplicationContext`. |
| `HeadsetService.cs` | HID logic: VID 0x1038 enumeration, interface selection, request/parse, path caching. |
| `IconRenderer.cs` | 32x32 GDI+ icon with the percent, color thresholds, `DestroyIcon` (no GDI leak). |
| `TrayAppContext.cs` | `NotifyIcon`, 2 s poll timer, context menu, `WM_DEVICECHANGE`, `PowerModeChanged`. |
| `AutoStart.cs` | Autostart via the `HKCU\...\Run` registry key. |
| `Logger.cs` | Rotating file logger. |

## Known limitations

- Tested on a single Nova 3X unit (PID `0x226d`, 2026 firmware). If a firmware update changes the
  PID, run `--probe` and add the new PID to `HeadsetService.KnownNova3Pids` if needed.
- Charging state is read directly from the `markerIdx+4` byte (verified against two confirmed
  states: charging/discharging). If a future firmware returns an unrecognized value for that byte,
  the app falls back to trend inference (`TrendPending` -> direction inferred from the change in
  percent across consecutive readings), so it will never lock onto a wrong state.
- The two `0x64` (100) bytes in the response have an unknown purpose and are unused.

## Handled error states

No dongle found, headset off, dongle unplugged mid-session (full re-enumeration on next poll),
unrecognized PID (falls back to probing every VID 0x1038 device), two instances running (named
mutex), sleep/wake, battery value out of range (reading discarded; after 3 consecutive failures
the state is reported offline).

## License

MIT -- see [LICENSE](LICENSE).
