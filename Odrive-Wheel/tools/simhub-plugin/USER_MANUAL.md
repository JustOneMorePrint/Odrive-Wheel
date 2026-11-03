# Odrive-Wheel SimHub Plugin — User Manual

This plugin brings your Odrive-Wheel board into SimHub. It reads live
telemetry over USB HID at the firmware's native 1 kHz rate, syncs FFB
tuning parameters over serial, and exposes everything as SimHub properties
and mappable actions for dashboards and button-box control.

> **What is Odrive-Wheel?** An open direct-drive sim racing wheel firmware
> that combines the ODrive motor controller with OpenFFBoard's Force
> Feedback stack, running on the MKS XDrive Mini or ODESC V4.2 boards.
> Repo: <https://github.com/eagabriel/Odrive-Wheel>

---

## Table of contents

1. [Features at a glance](#features-at-a-glance)
2. [System requirements](#system-requirements)
3. [Install](#install)
4. [First launch](#first-launch)
5. [Panel walkthrough](#panel-walkthrough)
6. [Configuration window](#configuration-window)
7. [Property reference](#property-reference)
8. [Mappable actions](#mappable-actions)
9. [Profile workflow](#profile-workflow)
10. [Save to flash](#save-to-flash)
11. [Auto-per-game](#auto-per-game)
12. [Update the plugin](#update-the-plugin)
13. [Troubleshooting](#troubleshooting)
14. [Coexistence with the HTML tool](#coexistence-with-the-html-tool)
15. [Uninstall](#uninstall)

---

## Features at a glance

- **1 kHz HID telemetry** — wheel position, velocity, Iq, torque, VBus,
  IBus, brake resistor current, FET/motor temperatures, ODrive errors.
- **Live serial sync** — every FFB parameter that ends up in a profile
  (max torque, EQ gains, filter Q/frequency, current limit, etc.) is
  read once on connect and cached; edits made in the panel commit
  through the serial CLI.
- **Profile support** — pick the same folder your HTML tool saves
  profiles to; the plugin lists the `.json` files and can apply any of
  them with a click.
- **Auto-per-game** — optionally applies `<GameName>.json` when SimHub
  detects a game change.
- **21 mappable actions** — increment/decrement of key live-tuning
  properties (FX master, FX ratio, 3-band EQ) plus a Zero-wheel-position
  action. Bind these to your wheel's buttons in **Controls and events**.
- **Rich in-panel visuals** — wheel-position gauge with rotating marker,
  Iq/Torque scope, colour-coded temperature bars, current-vs-limit meter,
  sparklines for VBus and torque, error decoder that translates ODrive
  bitmasks into readable names.

---

## System requirements

- **Windows 10/11**
- **SimHub 9.x** installed at `C:\Program Files (x86)\SimHub` (or point
  the build to a custom install path)
- **.NET Framework 4.8 runtime** (included with Windows 10 1903+ and Windows 11)
- An **Odrive-Wheel board** running firmware **v1.3 or newer** —
  identifiable in Windows Device Manager as HID `VID_1209&PID_0D40`.
  Older firmware (v1.1/1.2) works too, but temperature and error
  fields stay empty.

---

## Install

### Option A — Prebuilt DLL

1. Grab `OdriveWheel.SimHubPlugin.dll` from the
   [Odrive-Wheel Releases page](https://github.com/eagabriel/Odrive-Wheel/releases).
   (SimHub already ships `HidSharp.dll` — the plugin reuses it.)
2. Close SimHub (right-click the tray icon → *Exit* to be sure).
3. Copy the DLL into `C:\Program Files (x86)\SimHub`.
4. Start SimHub. On first launch it detects the new DLL and asks
   *"A new plugin was found — enable it?"* — click **Yes**.
5. SimHub restarts. **Odrive-Wheel** appears in the left sidebar.

### Option B — Build from source

Requires the `dotnet` CLI (tested with .NET SDK 10) and access to nuget.org. The .NET Framework 4.8 Developer Pack is optional, the SDK downloads
the reference assemblies otherwise.

Close SimHub first, it locks the DLL.

```bash
cd tools/simhub-plugin
dotnet build -c Release
```

The `AfterBuild` MSBuild target copies the DLLs into your SimHub
install directory automatically. For a non-default location:

```bash
dotnet build -c Release -p:SimHubPath="D:\SimHub"
```

Restart SimHub. Same first-launch flow as Option A.

### Verify install

- Left sidebar shows an entry labeled **Odrive-Wheel** with a small
  wheel-and-shield outline icon.
- Clicking it opens the plugin panel with the ODRIVE-WHEEL header
  banner at the top.

![SimHub with the plugin selected — header banner and connection card](Assets/Capture1.png)

---

## First launch

Plug the board via USB. Within 5 seconds of opening the plugin panel:

1. **HID LED** turns **green** — telemetry stream is alive.
2. **Serial LED** turns **green** — the plugin discovered the COM port
   (via WMI) and did its initial bulk read of 33 fields (~1 second).
3. **Live readings** start populating — position, velocity, current,
   temperatures.
4. **DEVICE CONFIG · SERIAL** section fills in with the current values
   read from the board.

If any LED stays gray or amber, see [Troubleshooting](#troubleshooting).

---

## Panel walkthrough

### Header banner

The dark strip at the top of the panel. Shows the project badge, plugin
name, current version and firmware-feature detection status, plus quick
links to **GitHub**, **PIX** (Brazilian donation), **Buy Me a Coffee**,
and **GitHub Sponsors**. Purely informational — click links open in
your default browser.

### Warning banner

Only visible when the ODrive reports at least one error (any of
`axis.error`, `motor.error`, `encoder.error`, `controller.error` non-zero).
Shows a bright red band above the Connection card with the decoded error
names, e.g.:

> ⚠ ODrive error detected
> Motor: 0x00000008 — DRV_FAULT
> Axis: 0x00000040 — MOTOR_FAILED

Disappears automatically when the errors clear (usually after fixing the
underlying issue and calling `w axis0.requested_state 8` or hitting a
reset button in the HTML tool).

### CONNECTION

Two rows: **HID** (the 1 kHz stream) and **Serial** (the config
channel). Each has a coloured LED with a subtle glow:

- **Green** — live and healthy
- **Amber** (Serial) — searching for the board, the port is held by another
  application, or you released it. The text says which.
- **Gray** (HID) — no device / stale

The Serial row also shows the COM port name (e.g., `COM7`) and how long
ago the last bulk read completed.

Below the rows:

- **Open configuration window** — the separate window with the status
  values, the ODrive tabs, the power supply data, the software stop and the
  Extensions tab. It runs on its own UI thread, so a busy SimHub or a SimHub
  dialog does not block it. See [Configuration window](#configuration-window).
- **Release port / Take over port** — the plugin holds the COM port while
  it runs. Release it before you connect the HTML tool or start a flash from
  there. While released, profiles and actions report that the port is released.
- **SOFTWARE STOP (IDLE)** — see below.

### Software stop

Sets the axis to IDLE, ahead of everything that is waiting: queued
commands are dropped and a running profile ends. You can also map it to a
button (`SoftwareStop` under [Mappable actions](#mappable-actions)). The
status line tells you what happened:

- **IDLE confirmed** (green) — the stop still stands and the axis reports IDLE
  right now. Green is never just a memory of an earlier success.
- **Stop was confirmed, no current axis state** (amber) — it worked, but the HID
  stream reports nothing at the moment, so the current state is unknown.
- **The board reconnected since the stop** (amber) — it may have rebooted, and a
  reboot can arm the motor again.
- **AXIS IS NOT IDLE NOW** (red) — the HID stream contradicts the earlier stop.
- **NOT CONFIRMED** (red) — the command went out, but nothing reported IDLE.
- **NOT DELIVERED** (red) — the command never reached the board, for example
  because USB is gone or another application holds the port.
- **Last software stop …** (gray) — you pressed **Clear stop**. This is history
  and says nothing about the axis now.

In the red cases use the power cut-off.

**This is not a hardware E-stop.** It needs a working USB link and a
responsive firmware. If SimHub is not running, it does nothing. IDLE only switches
the drive off: the motor coasts and does not brake. While the board writes to
flash, the stop waits until that is done, about 1–2 s at worst. Always keep the
cut-off of the power supply within reach.

After a stop the plugin refuses everything that could start the motor again
until you press **Clear stop**: `ss` and reboots (a reboot arms the motor again
if `startup_closed_loop_control` is set), motion commands and other axis states.
This covers the plugin only. The HTML tool or CAN can still arm the motor.

### LIVE READINGS · HID

![Wheel gauge with rotating marker, big readouts and value grid with bars/sparklines](Assets/Capture2.png)

The dashboard section. Data flow:

- **Wheel gauge** — a stylised wheel that rotates 1:1 with the physical
  wheel angle (the red dot at 12 o'clock is the visual rotation marker).
  At full lock on a 900° wheel, the icon has rotated 1.25 turns.
- **Big readouts on the right** — Position, Velocity and Torque in large
  monospace typography.
- **Numeric grid** — every 1 kHz-derived value with visual assists:
  - **Iq (A)** row shows a bar meter with the accent-coloured usage vs
    a dark red "clip zone" (last 5% of `current_lim`). When Iq
    approaches the limit, the bar visually enters the red zone.
  - **Torque and VBus** rows have inline sparklines showing the last
    ~60 s of history at 4 Hz.
  - **FET/Motor temp** rows have colour-coded 0..100 °C bars: green
    below 60, yellow 60–75, orange 75–85, red above 85.
- **Axis state / errors** — the decoded ODrive state (e.g.
  `8 — CLOSED_LOOP_CONTROL`) and each of the four error bitmasks in
  hex + human-readable names.

### SCOPE · IQ / TORQUE

A live 2-trace oscilloscope showing Iq (blue) and Torque (orange)
overlaid on a shared Y axis. Window: last ~2.4 s at 100 Hz. Includes:

- Subtle grid + a dashed zero-line highlight when zero is in view
- Auto-scaled Y axis with min/max labels in the top-right
- "waiting for data…" placeholder before the first samples arrive

Useful for spotting FFB spikes, cornering ramps and Iq vs Torque
alignment (they should track closely with a slight lag).

### DEVICE CONFIG · SERIAL

![Editable FFB Axis fields — text boxes for numerics, checkbox for booleans, read-only extras marked as such](Assets/Capture3.png)

Everything the plugin can read AND write over serial. 5 sub-groups:

1. **Motor & Encoder (ODrive)** — `current_lim`, `current_control_bandwidth`,
   `encoder.bandwidth`, plus read-only `phase_resistance` and
   `brake_resistance`.
2. **FFB Axis** — the 12 `axis.*` parameters (range, maxtorque, invert,
   idle spring, damper, inertia, friction, endstop gain, expo, etc).
3. **FX Master gains** — master, spring, damper, friction, inertia.
4. **FX Filters (LPF)** — 4 effects × freq + Q.
5. **3-band EQ** — weight, chassis, road gain (dB).

Every writable field has an inline editor:

- **Numeric fields**: a text input. **Enter** commits the new value via
  serial; **Esc** reverts to the cached value. The border turns yellow
  when the shown value differs from what the device has ("dirty" state).
- **Boolean (`axis.invert`)**: a checkbox that commits on click.
- **Read-only fields** (phase/brake resistance): shown as plain text.

The right-hand hint (`→ AxisMaxTorqueNm`) is the property name to use
in dashboard expressions.

Writes go to RAM only — see [Save to flash](#save-to-flash) to persist.

The status line below the buttons shows the last write result:
- `✓ axis.maxtorque = 15.00 (RAM only — use Save to flash to persist)`
- `✗ axis.foo → rejected by firmware (invalid value?)`

### Refresh from device now

Only useful if you edited values in the HTML tool while the plugin was
running. Re-reads every field over serial, which takes about a second.
Not needed for normal operation — the cache stays authoritative on its
own because the plugin is the sole writer.

### PROFILES

![Profiles and Automatic per-game sections with browse/apply/save buttons and toggle switches](Assets/Capture4.png)

Point at the folder your HTML tool saves `.json` profiles to. The
dropdown lists every file whose top level has
`"kind": "odrive-wheel-profile"`. Buttons:

- **▶ Apply selected profile** — writes every field of the profile to
  the board via serial. RAM-only.
- **💾 Save to flash (sys.save! + ss)** — persists via `sys.save!`
  (OpenFFBoard params to emulated EEPROM) and `ss` (ODrive config to
  NVM). The board **reboots**; FFB drops for ~1–2 s. Confirmation dialog.
- **↻** next to the dropdown re-scans the folder.

### AUTOMATIC PER-GAME

Two toggle switches:

- **Auto-apply `<GameName>.json` when the game changes** — the plugin
  watches SimHub's detected game name and, on a change, looks for
  `<safe-file-name>.json` in the profiles folder. If found, applies it.
- **Also save to flash after auto-apply** — dangerous default-off.
  Enabling makes each auto-apply reboot the ODrive; only useful if you
  want the setting to persist across power cycles.

The plugin is idempotent per game — won't re-apply the same file
back-to-back if the game name is stable.

### Footer

Version, license, GitHub repo path.

---

## Configuration window

Opened with **Open configuration window** in the panel. The connection state,
**Release port** and **SOFTWARE STOP (IDLE)** stay at the top on every tab, and
both keep working while values are being read: they end the read, and whatever
it did not reach stays unread.

Tabs: **Status**, **PSU / Brake**, **Axis**, **Motor**, **Encoder**,
**Controller**, **Snapshot** and **Extensions**.

**The parameter lists only read.** They send read commands and change nothing
on the board. The only way to write is the Power / Brake editor described below.
Editing anything else, clearing errors, erasing the configuration, calibrations,
the console and import are planned (Package 1b). Export is the
[catalog snapshot](#catalog-snapshot) below.

Implemented and tested against a simulated board. **Not tested on a real board
yet.**

The **Status** tab reads the errors, the axis state, the bus voltage and the
build id once a second. Everything else is read only when you press **Refresh
tab** or the **Refresh** of a group.

Each row shows the value, what the firmware allows (`read-only`, `writable`, or
`calibration` for a value set by a calibration) and the time of the read. A row
can also say:

| Text | Meaning |
|---|---|
| `—` | Not read on this connection. |
| `outdated (was …)` | Read before the connection changed. The board may have rebooted, press **Refresh**. |
| `not supported by this firmware (…)` | The running firmware does not have this parameter. |
| `not readable (…)` | The firmware refused the read. |
| `no reply` | No answer in time. Two in a row end the read. |
| `unexpected reply (…)` | The answer is not a value of the expected type. |
| `… (may be cut off)` | See below. |

Older firmware returns **at most 9 characters** from an ODrive read and cuts
longer numbers without saying so. A number that fills all 9 may therefore be
wrong, and a reply alone does not show which firmware sent it. So current firmware
says it: when a connection comes up, the plugin asks once with `sys.capabilities?`,
and the line **Firmware replies** at the top of the window shows the result.

| Firmware replies | Meaning |
|---|---|
| `declared complete and exact on this connection` | Numbers are shown as they come, and error bits are decoded. |
| `nothing declared (…)` | Older firmware, no reply or an unusable one. A number with 9 or more characters is marked, and error bits are not decoded in that case. With a decimal point in the reply only decimals can be lost, so `100.00000` means 100. |
| `checking…` | The answer is not here yet. Values already shown are redrawn when it arrives. |

**Check firmware again** asks once more, it appears only after a check that found
nothing. It ends a prepared review and a running **Apply to RAM**, as they rest on
the state before. The plugin does not conclude anything from the version or the build
id, and a new connection is always asked again, as another firmware may be on the board.

Newer firmware also writes floats with up to nine significant digits, so `25`,
`0.100000001` or `1.17549435e-38` instead of `25.000000`, `0.100000` or `0.000000`.
The units and the meaning of the values are the same.

### Power supply data

The **PSU / Brake** tab starts with what you know about your power supply: DC
output voltage, continuous output current, current headroom and, if your supply
has it, a short-time current with its maximum duration. Leave what you do not know
empty, nothing is assumed. Decimals take a point, not a comma.

**Usable continuous current** = continuous output current × (1 − current headroom
/ 100), for example 20 A with 10 % headroom gives 18 A.

**Save to plugin settings** keeps this in the SimHub plugin settings on this PC. It
is not part of any profile and **nothing goes to the board**: saving here is not
saving the board configuration. This is DC supply current, not motor phase current,
and no board value is taken from it. A short-time current is not a continuous limit,
and the plugin has no time-dependent current limit: this data is the basis for later
features.

### Editing Power / Brake (RAM only)

Below the power supply data you can change the ten `config.*` values of the group
Power / Brake. `sys.vbusdiv` and the communication values stay read-only.

1. Press **Refresh these values**. Without a current and exact value for all ten, there
   is no review.
2. Type the new values and tick their fields. Typing sends nothing. A boolean is 0 or 1.
3. Press **Review selected changes**. The summary shows old and new values in the order
   they will be written, and whatever speaks against them. The new value is the float32
   the firmware will store, which can differ slightly from what you typed. A review
   sends nothing, and any further edit needs a new one.
4. Press **Apply to RAM**. The stop button and **Release port** keep working.

Before each write the plugin checks that both axes are IDLE, not armed and without
a pending state request. If an axis is active or an answer is missing, it stops. It
does not disarm anything by itself. **These checks are no lock:** another command
source can still change the state between the check and the write.

| Result | Meaning |
|---|---|
| `verified by readback` | The board reports exactly the float32 or boolean that was written. |
| `sent, not verified (…)` | The line went out, but the value is not confirmed. |
| `sent, readback differs (…)` | The line went out and the board reports another value. |
| `refused by the firmware (…)` | The firmware answered with an error. |
| `not sent` | The line never went out. |

A write has no answer of its own, so only the readback can confirm it. Each next
write rests on the one before, so **anything but `verified by readback` ends the run**,
and the result names what already went out. It is not repeated and not undone. A
software stop, **Release port**, a new connection or a change of **Firmware replies**
ends it as well, also if it comes while a command is on its way: nothing more goes
out, and a reply that comes in after it confirms nothing.

**Exact values:** the relations between the values need what the board really holds.
With **Firmware replies** at `declared complete and exact`, that is what a reply says,
also for a value like `32.012001` or `0.015625`, and the readback compares the float32
values. There is no need to limit the number of decimals.

While the line says `checking…` there is no review, its answer decides what counts as
exact. Without a declaration, a reply like `56.000000` or `0.015625` could be rounded or cut
by older firmware. Such a value cannot be judged, so the review refuses it and names
the field, and a readback in that form does not count as verified. On older firmware
that is nearly every float, so editing takes a current firmware.

**Applied values are in RAM only.** A reboot restores the saved values. The editor
never saves to the board. If you want to keep them, that is **Save to flash** in the
panel, which saves everything currently in RAM. `enable_brake_resistor` takes effect only
after a reboot, and enabling it without a connected brake resistor can break the power
supply.

### Catalog snapshot

The **Snapshot** tab reads every value of the plugin's catalog once and keeps the
result as it was, for export, comparison and a diagnostic report. Nothing is
written to the board, and nothing is read without a button press.

**What it is and is not.** A catalog snapshot: every value of the plugin's catalog,
read one after the other from the **RAM** of the board. It is not a complete NVM
image, not a calibration record and not a restore backup. The values were read at
different moments between the start and the end time shown, and none of them says
what is stored in flash. Not included: most FFB settings (only `axis.maxtorque` and
`fx.master` are in the catalog yet), FFB filters, inputs and GPIO, profiles, the
plugin settings, the anticogging map and `axis1`.

1. Press **Capture snapshot**. The line next to it shows the progress, **Cancel**
   ends it. The stop button and **Release port** keep working and end it too.
2. The summary says how far it got and on which state:
   - `Catalog snapshot complete`: every path has a usable reply.
   - `ended with failed values`: it ran to the end, the failed paths are named.
   - `PARTIAL`: a software stop, **Release port**, a lost or new connection, a new
     firmware check or **Cancel** ended it, also when that came after the last
     reply. The values before it are kept, the rest is not read, and nothing is
     filled in from earlier reads.
   With a finished software stop and its latch set, a capture still works: it only
   reads. It never clears the latch, and what the latch blocks stays blocked.
3. **Precision** follows **Firmware replies**: with `declared complete and exact`
   every ODrive reply is the value on the board. Without it, floats may be rounded
   and numbers of 9 or more characters may be cut off, and the snapshot says so.

**Export snapshot…** writes two files next to each other and never replaces one:

| File | Content |
|---|---|
| `odrive_config_<time>.json` | The settings as `"path": "value"`, exactly as the board replied, in the format of the web client's export. Status values are not in it. |
| `odrive_config_<time>.meta.json` | What the values are: catalog version and scope, firmware build, the firmware's declaration, start and end time, port and connection, stop latch, failed and uncertain paths, completeness, status values. It carries the SHA-256 of the values file. |

A partial snapshot gets `_partial` in both names. After writing, the plugin reads
both files back and loads them like any other file. Only then it says **Exported
and read back**, otherwise **Export FAILED** and nothing of it stays on disk. The
web client's import loads the values file as one of its own exports.

**Compare.** Choose a file with **Browse…**:

- **Compare board with file** first captures a new snapshot, through the catalog
  paths only. The keys of a file are data: they are shown, never sent.
- **Compare the two files** works offline. It does not touch the connection, also
  not with the port released.

Settings and status values are listed apart: status values change on their own.

| Result | Meaning |
|---|---|
| `same` | The same value by its type, and both sides are certain of it. With `declared complete and exact` on both sides `32.012` and `32.012001` are the same float32. |
| `same text` | The same text, but one side's precision is not proven. Equal as text, nothing more. |
| `DIFFERS` | A difference no rounding or cutting can explain. |
| `unclear` | Different text that may still be the same value on the board. |
| `only left`, `only right` | One side has no such path. |
| `not compared` | A side tried to read it and failed. |

Fixed rules: floats compare as float32, +0 and -0 are the same value, two NaN are
the same, `inf` and `-inf` differ from each other and from every number. Integers
and error bits compare as whole numbers of any length. Booleans take `0`, `1` and the
`True`/`False` of older web client files. Without matching metadata, for example a
file of the web client or a values file that was changed, a file's floats count as
not proven. A file that is no JSON object, has a key twice, is not UTF-8 or is too
large is refused with the reason.

**Write diagnostic report…** writes a short Markdown file for an issue: build,
declaration, the scope of the snapshot, errors in clear text, axis state, bus voltage,
temperatures with their limits, failed reads and the differences from the last
compared file. It names no board type, since the firmware reports none (`hw_version`
reads the same on all supported boards), and it never says that a temperature sensor
is connected: without a sensor a plausible number can appear, and 0 is also what the
firmware reports after an invalid reading.

A finished snapshot stays as it is after a disconnect. The tab then says that its
connection has ended, and it can still be exported.

---

## Property reference

All properties live under `OdriveWheel.SimHubPlugin.*` in Dash Studio
expressions (e.g., `[OdriveWheel.SimHubPlugin.WheelPositionDeg]`).

### Connection & telemetry status

| Property | Type | Description |
|---|---|---|
| `Connected` | bool | HID stream alive within last 500 ms |
| `ReportRateHz` | double | Measured HID rate (should read ~1000) |
| `LastReportMillisAgo` | long | Age of most recent report |
| `SerialPortName` | string | Discovered COM port, empty if none |
| `SerialLastRefreshSecondsAgo` | double | Time since last bulk read (-1 if never) |

### Wheel motion (from HID)

| Property | Type | Description |
|---|---|---|
| `WheelPositionDeg` | double | Wheel angle in degrees, scaled by half-range |
| `WheelPositionNormalized` | double | −1..+1 |
| `WheelVelocityTurnsPerSec` | double | Signed |
| `WheelVelocityDegPerSec` | double | Signed |

### Motor electrical (from HID)

| Property | Type | Description |
|---|---|---|
| `IqCurrentA` | double | Instant Iq (motor quadrature current) |
| `TorqueOutputNm` | double | Instant torque command |
| `VBusVoltage` | double | DC bus voltage |
| `IBusCurrentA` | double | DC bus current |
| `BrakeResistorCurrentA` | double | Regen current through the brake resistor |

### Temperatures (from HID, firmware ≥ 1.2)

| Property | Type | Description |
|---|---|---|
| `FetTempC` | double | FET/inverter °C (NaN if no sensor) |
| `MotorTempC` | double | Motor °C (NaN if no sensor) |
| `FetTempValid` | bool | true iff FET thermistor present |
| `MotorTempValid` | bool | true iff motor thermistor present |

### ODrive state & errors (from HID, firmware ≥ 1.3)

| Property | Type | Description |
|---|---|---|
| `AxisState` | long | ODrive `AxisState` enum (8 = CLOSED_LOOP_CONTROL) |
| `AxisError` | long | Bitmask, 0 = ERROR_NONE |
| `MotorError` | long | Bitmask |
| `EncoderError` | long | Bitmask |
| `ControllerError` | long | Bitmask |
| `AnyError` | bool | `true` if any of the four is non-zero |

### Buttons (from HID)

| Property | Type | Description |
|---|---|---|
| `ButtonsBitmask` | ulong | 64-bit button state, packed |
| `Button01` .. `Button32` | bool | Individual bits (32 of the 64 exposed) |

### Derived quantities

| Property | Type | Description |
|---|---|---|
| `MotorMechPowerW` | double | τ · ω · 2π (signed) |
| `MotorCopperLossW` | double | 1.5 · R · Iq² (uses cached `phase_resistance`) |
| `BrakePowerAvgW` | double | R · ⟨I²⟩ over 60 s (uses cached `brake_resistance`) |
| `ClipOutPercent` | double | % of samples with \|Iq\| ≥ 0.95 · `current_lim` over 30 s |

### Profile fields (serial read/write)

All 31 profile fields plus 2 read-only extras. Full list, in the order
they appear on the panel:

**Motor & Encoder (ODrive)**

| Path | Property | Type |
|---|---|---|
| `axis0.motor.config.current_lim` | `MotorCurrentLimA` | double |
| `axis0.motor.config.current_control_bandwidth` | `CurrentControlBandwidthHz` | double |
| `axis0.encoder.config.bandwidth` | `EncoderBandwidthHz` | double |
| `axis0.motor.config.phase_resistance` | `MotorPhaseResistanceOhm` | double (read-only) |
| `config.brake_resistance` | `BrakeResistanceOhm` | double (read-only) |

**FFB Axis**

| Path | Property | Type |
|---|---|---|
| `axis.range` | `AxisRangeDeg` | long |
| `axis.maxtorque` | `AxisMaxTorqueNm` | double |
| `axis.fxratio` | `AxisFxRatio` | double |
| `axis.invert` | `AxisInvert` | bool |
| `axis.idlespring` | `AxisIdleSpring` | long |
| `axis.axisdamper` | `AxisDamper` | long |
| `axis.axisinertia` | `AxisInertia` | long |
| `axis.axisfriction` | `AxisFriction` | long |
| `axis.esgain` | `AxisEndstopGain` | long |
| `axis.maxtorquerate` | `AxisMaxTorqueRate` | long |
| `axis.expo` | `AxisExpo` | long |
| `axis.exposcale` | `AxisExpoScale` | long |

**FX Master**

| Path | Property | Type |
|---|---|---|
| `fx.master` | `FxMaster` | long |
| `fx.spring` | `FxSpring` | long |
| `fx.damper` | `FxDamper` | long |
| `fx.friction` | `FxFriction` | long |
| `fx.inertia` | `FxInertia` | long |

**FX Filters (LPF)**

| Path | Property | Type |
|---|---|---|
| `fx.filterCfFreq` | `FxConstantForceFreqHz` | long |
| `fx.filterCfQ` | `FxConstantForceQ` | long |
| `fx.filterFrFreq` | `FxFrictionFreqHz` | long |
| `fx.filterFrQ` | `FxFrictionQ` | long |
| `fx.filterDaFreq` | `FxDamperFreqHz` | long |
| `fx.filterDaQ` | `FxDamperQ` | long |
| `fx.filterInFreq` | `FxInertiaFreqHz` | long |
| `fx.filterInQ` | `FxInertiaQ` | long |

**3-band EQ**

| Path | Property | Type |
|---|---|---|
| `axis.eqweight` | `EqWeightGainDb` | double |
| `axis.eqchassis` | `EqChassisGainDb` | double |
| `axis.eqroad` | `EqRoadGainDb` | double |

---

## Mappable actions

Bind these in SimHub → **Controls and events** → **Additional plugins** →
**Odrive-Wheel Telemetry**.

### Zero wheel position

| Action | Description |
|---|---|
| `ZeroWheelPosition` | Captures the current wheel position and applies it as the new center. Uses the firmware-native `axis.zeroenc!` command. Instant. Essential for incremental encoders without a Z index. |

### Software stop

| Action | Description |
|---|---|
| `SoftwareStop` | Sets the axis to IDLE ahead of everything else, see [Software stop](#software-stop). Not a hardware E-stop. |

### Increment / decrement (5 properties × 4 variants each)

Each of the 5 live-tunable properties has a **fine** step (single-tap
adjust) and a **coarse** step (bigger jump). Bind them to whichever
buttons you want.

| Base | Range | Fine | Coarse | Action names |
|---|---|---|---|---|
| `fx.master` | 0..255 | ±10 | ±25 | `FxMasterUpFine`, `FxMasterDownFine`, `FxMasterUpCoarse`, `FxMasterDownCoarse` |
| `axis.fxratio` | 0..1 | ±0.05 | ±0.20 | `FxRatioUpFine`, `FxRatioDownFine`, `FxRatioUpCoarse`, `FxRatioDownCoarse` |
| `axis.eqweight` | −12..+12 dB | ±0.5 dB | ±2 dB | `EqWeightUpFine`, `EqWeightDownFine`, `EqWeightUpCoarse`, `EqWeightDownCoarse` |
| `axis.eqchassis` | −12..+12 dB | ±0.5 dB | ±2 dB | `EqChassisUpFine`, `EqChassisDownFine`, `EqChassisUpCoarse`, `EqChassisDownCoarse` |
| `axis.eqroad` | −12..+12 dB | ±0.5 dB | ±2 dB | `EqRoadUpFine`, `EqRoadDownFine`, `EqRoadUpCoarse`, `EqRoadDownCoarse` |

Behaviour:

- **Atomic** — a lock serialises actions so back-to-back presses can't
  interleave.
- **Cache-first** — the plugin reads the current value from its own
  cache (instant), applies the delta, clamps to the range, writes via
  serial, and updates the cache with the new value. Result: ~1 serial
  round-trip per action (~30 ms).
- **Clamps at limits** — pressing `FxMasterUpFine` when already at 255
  logs "already at limit" and no-ops.
- **RAM-only** — same as manual editing. Use **Save to flash** to
  persist a tuning session.

### Best-practice button mapping

Suggested layout for a wheel with left/right D-pad or dedicated buttons:

```
              FxMasterUpFine
                    ↑
FxMasterDownFine ← MASTER → FxMasterUpCoarse (Shift+Right, e.g.)
                    ↓
              FxMasterDownFine
```

Or dedicate a knob to master and use rotary encoder pulses as
inc/dec triggers.

---

## Profile workflow

1. In the **PROFILES** section, click **Browse…** and pick the folder
   where the HTML tool saves your `.json` profiles. Path is remembered
   across restarts.
2. Wait for the dropdown to populate. Only files starting with
   `"kind": "odrive-wheel-profile"` show up — legacy odrive config
   dumps are filtered.
3. Select a profile → **▶ Apply selected profile**. Watch the status
   line — you'll see something like `'MyRallyProfile' — 31 ok
   (not persisted; use Save to flash)`.
4. Try it in-game. If you like it, click **💾 Save to flash**. If you
   don't, just reboot the board and it reverts to the previous
   flashed setting.

---

## Save to flash

The **💾 Save to flash** button runs two commands in sequence:

1. `sys.save!` — persists FFB parameters (all `axis.*`, `fx.*`) into
   the OpenFFBoard's emulated EEPROM. Non-destructive.
2. `ss` — persists ODrive config (`config.*`, `axis0.motor.*`,
   `axis0.encoder.*`) into flash NVM and **reboots the board**.
   Sent only after `sys.save!` has answered `OK`. On an error, a timeout,
   a lost connection or a software stop the plugin shows the error and
   sends no `ss`, without retrying.

FFB drops for ~1–2 seconds during the reboot. Don't do this mid-race.
A confirmation dialog protects against accidental clicks.

If you only edited FFB/EQ parameters (not motor calibration), the
reboot is technically unnecessary — but the plugin runs both for
simplicity and matches what the HTML tool's unified Save button does.

**Known limit:** `ss` has no reply, and the firmware ignores it while the motor
is armed. The status line can therefore only say that `ss` was sent, not that
the ODrive config was saved; a reboot of the board is the only sign that it
took effect. The ODrive editor (planned, Package 1b) will handle this properly;
until then set the axis to IDLE first if you changed ODrive values. After a
[software stop](#software-stop), `ss` is refused until you press **Clear stop**.

---

## Auto-per-game

The plugin subscribes to SimHub's game-change events. When SimHub
detects the active sim change (game name goes from A → B), the plugin:

1. Sanitises the game name (invalid filename chars → `_`).
2. Looks for `<GameName>.json` in your profiles folder.
3. If found → applies it via the same serial-write path as manual
   apply.
4. If **Also save to flash** is on and every field was accepted → runs
   `sys.save!`, and `ss` only after `sys.save!` has answered `OK`.

Naming examples:
- `iRacing.json`
- `Assetto Corsa Competizione.json`
- `Automobilista 2.json`
- `Le Mans Ultimate.json`

The plugin logs auto-loads to `Documents\SimHub\Logs\` — check there
if you're not sure a file was found.

---

## Update the plugin

1. Close SimHub completely (including the tray icon).
2. Overwrite `OdriveWheel.SimHubPlugin.dll` in
   `C:\Program Files (x86)\SimHub`. Don't touch `HidSharp.dll` — it's
   part of SimHub itself.
3. Start SimHub. Your settings (profiles folder, auto-load toggles)
   persist automatically — they live in
   `Documents\SimHub\PluginsData\OdriveWheel.SimHubPlugin`.

---

## Troubleshooting

### HID LED stays gray

- Board not detected as HID. Check Windows Device Manager for
  `VID_1209 PID_0D40`. Unplug/replug. If Windows still shows it as
  "Unknown device", replug into a different USB port (some hubs are
  flaky for HID devices).
- Older firmware without the extended report (< v1.2). Basic telemetry
  still works.
- Another app has the device in exclusive mode (rare — DirectInput
  games don't exclusive-lock the FFB device).

### Serial LED stays amber

Read the text next to the LED:

- **searching for the board** = WMI didn't discover a COM port matching
  VID 1209 / PID 0D40. Windows sometimes takes 5–30 s to enumerate a new
  device. **Wrong driver**: the device should appear as a USB Serial Device
  (COMx) under Ports in Device Manager. If it's under HID or "Other devices",
  reinstall the ODrive USB driver.
- **held by another application** = usually the HTML tool: disconnect there.
  The plugin retries every second.
- **released for other applications** = press **Take over port**.

### A command fails with "the reply stream is out of sync"

The firmware answers `r`/`w` commands and `class.property` commands on two separate
paths. After a timeout the plugin asks the board with a harmless line where the reply
stream of that path stands (`f <n>` or `odwsync.m<n>?`), and sends nothing on that path
until the answer is back, so that a late reply can't pass for the current one.
The message means no answer came within a second, and the command was **not** sent.
The other path may well keep working. If it stays that way, restart the board: that
reply channel is stuck.

### Serial LED is green but Device config fields all show "—"

- The port is open but the firmware isn't responding. Symptom of the
  old plugin bug where DTR/RTS weren't asserted. Update to the latest
  plugin — should be fixed.
- Firmware might be stuck in an ODrive fault. Check the warning banner
  for `MotorError` or `AxisError` bits and clear them via the HTML tool.

### ReportRateHz much lower than 1000

- Native HID Input Reports should arrive at the 1 ms polling interval.
  If the rate drops below 500 Hz there's USB bus contention:
  - Move the wheel to a **root** USB port (not through a hub).
  - Avoid sharing the port with a headset or webcam.
  - Windows Game Bar / Fast Startup can occasionally throttle HID —
    disable them if the rate is consistently low.

### WheelPositionDeg looks half or double what it should

- The plugin uses the firmware's `axis.range` value (read via serial)
  to scale the raw HID position. If serial isn't connecting, the
  fallback is 450° (half of 900°). Force a **Refresh from device now**
  once serial is working, and the correct scale takes effect.

### Auto-per-game doesn't fire

- SimHub's game detection only works for sims it recognises (most
  major sims are supported). Check what SimHub reports as the current
  game via **Home → Data corner**.
- The profile file name must match exactly, minus invalid chars. If
  the game name has emoji or unusual characters, rename the file to
  what the plugin would sanitise it to.

### Live readings show but AxisError never has a value

- The board is on firmware v1.2 or older, which doesn't include the
  error fields in the HID report. Errors default to 0 / `AnyError=false`.
  Everything else works.

---

## Coexistence with the HTML tool

Both tools can be open at the same time, but only one can use the serial
port:

### HID (1 kHz telemetry)

- Windows allows **multiple HID readers on Input Reports**. The game
  (via DirectInput/WGI), the HTML tool's overlay, and this plugin all
  read the same stream in parallel with no contention.

### Serial (config CLI)

- The COM port is **single-owner**. Only one app can hold it at a time.
- The plugin **holds the port while it runs**, so that the software stop can
  go out at any moment. Press **Release port** in the panel or the configuration
  window before you connect the HTML tool, and **Take over port** afterwards.
- While released, the plugin leaves the port alone. Only a software stop still
  tries to open it for a moment; if the HTML tool holds it, the stop reports
  NOT DELIVERED.
- If the HTML tool already holds the port when SimHub starts, the plugin shows
  that and connects as soon as the port is free.
- SimHub's data thread never waits for the serial link. Per-game profiles
  are applied in the background.

### Recommended workflow

Use the HTML tool for **deep tuning**: motor calibration, encoder
setup, filter frequency exploration, EQ analysis. Use the SimHub plugin
for **live use**: dashboards during racing, quick parameter tweaks via
button-mapped inc/dec, per-game profile switching.

---

## Uninstall

1. Close SimHub completely.
2. Delete `OdriveWheel.SimHubPlugin.dll` from
   `C:\Program Files (x86)\SimHub`. Don't touch `HidSharp.dll` — it
   belongs to SimHub.
3. Optionally delete
   `Documents\SimHub\PluginsData\OdriveWheel.SimHubPlugin\` to remove
   saved settings.
4. Restart SimHub.

---

## License

MIT. Part of the [Odrive-Wheel](https://github.com/eagabriel/Odrive-Wheel)
project. Firmware, HTML configurator, and this plugin share the same
license.
