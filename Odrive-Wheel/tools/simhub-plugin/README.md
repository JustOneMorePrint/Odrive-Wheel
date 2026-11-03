# Odrive-Wheel SimHub plugin

A SimHub plugin for Odrive-Wheel telemetry and configuration. Live telemetry
comes from the firmware's 1 kHz HID input reports; the serial service provides
profiles, the software stop, the configuration window and catalog snapshots.
HID telemetry uses the fields actually present in the report. Configuration
features use this fork's firmware capabilities, not a version-number guess.
See the [repository README](../../../README.md) for implemented and planned scope.

## What you get

Properties published to SimHub under **Odrive-Wheel Telemetry**:

| Property                       | Type   | Source            |
|-------------------------------|--------|--------------------|
| `Connected`                    | bool   | HID stream alive within 500 ms |
| `ReportRateHz`                 | double | Measured, should read ~1000 |
| `LastReportMillisAgo`          | long   | Age of most recent report |
| `WheelPositionDeg`             | double | Raw X × HalfRange / 32767 |
| `WheelPositionNormalized`      | double | −1..+1 |
| `WheelVelocityTurnsPerSec`     | double | firmware Y / 1000 |
| `WheelVelocityDegPerSec`       | double | × 360 |
| `IqCurrentA`                   | double | firmware Z / 1000 |
| `TorqueOutputNm`               | double | firmware Dial / 1000 |
| `VBusVoltage`                  | double | firmware VBus / 100 |
| `IBusCurrentA`                 | double | firmware IBus / 100 |
| `BrakeResistorCurrentA`        | double | firmware IBrake / 100 |
| `ButtonsBitmask`               | ulong  | 64-bit button state |
| `Button01`..`Button32`         | bool   | individual bits |
| `FetTempC`                     | double | Reported FET/inverter °C; NaN if the HID field is unavailable or has the invalid sentinel |
| `MotorTempC`                   | double | Reported motor °C; NaN if the HID field is unavailable or has the invalid sentinel |
| `FetTempValid`                 | bool   | HID temperature field is available and not `-128`; does not prove a physical sensor is present |
| `MotorTempValid`               | bool   | HID temperature field is available and not `-128`; does not prove a physical sensor is present |
| `MotorMechPowerW`              | double | torque · ω · 2π |
| `MotorCopperLossW`             | double | 1.5 · R · Iq² (needs R_phase) |
| `BrakePowerAvgW`               | double | R · ⟨I²⟩ over 60 s (needs R_brake) |
| `ClipOutPercent`               | double | Fraction where \|Iq\| ≥ 0.95 · lim over 30 s (needs current_lim) |

Telemetry comes from the 1 kHz HID stream. The serial CLI channel is for
configuration: profile fields, profiles, mappable actions and the software stop,
plus the parameter editor and read-only snapshot, comparison and diagnostic report.
See [the user manual](USER_MANUAL.md) and [hardware limits](../../../docs/HARDWARE.md).

## Building

Requirements:

- `dotnet` CLI (tested with .NET SDK 10) and access to nuget.org.
  The .NET Framework 4.8 Developer Pack is optional, the SDK downloads the
  reference assemblies otherwise.
- SimHub installed at `C:\Program Files (x86)\SimHub` (or set the `SimHubPath`
  MSBuild property to your install location)

Close SimHub first, it locks the DLL.

```bash
dotnet restore
dotnet build -c Release
```

The `AfterBuild` target copies `OdriveWheel.SimHubPlugin.dll` straight
into your SimHub install directory (SimHub already ships `HidSharp.dll`,
so we don't overwrite it). Restart SimHub and enable the plugin under
**Settings → Plugins**. The next start opens the COM port of a connected board.

Tests run without SimHub and without a board, see `tests/plugin/README.md`.

If SimHub is installed elsewhere:

```bash
dotnet build -c Release -p:SimHubPath="D:\SimHub"
```

## Manual install (if the copy step fails)

Copy `OdriveWheel.SimHubPlugin.dll` from `bin/Release/net48/` into your
SimHub install directory (next to `SimHubWPF.exe`). Restart SimHub.

`HidSharp.dll` is already bundled with SimHub — don't overwrite it.

## Configuration

Open SimHub → left menu → **Odrive-Wheel**. Range, phase resistance, brake
resistance and current limit are read from the board over serial. Settings like
the profiles folder persist to SimHub's `PluginsData` folder.

## Coexistence

- **The game reading the wheel via DirectInput/WGI**: works. Windows allows
  multiple HID readers on the same device for Input reports, so this plugin
  and the game read the same stream in parallel with no contention.
- **The Odrive HTML configurator open at the same time**: works, as long as
  it isn't holding the HID device with `exclusive: true`. Overlay uses
  `inputreport` listeners like this plugin does, so both can coexist.
- **The serial port**: only one application can hold it, and the plugin holds it
  while it runs, so that the software stop can go out at any moment. Press
  **Release port** before another serial tool takes over. The local HTML file is a
  legacy reference; follow [the testing and update instructions](../../../docs/TESTING.md)
  for firmware updates.

The software stop sets the axis to IDLE. It needs a working USB link and is
**not a hardware E-stop**: keep the cut-off of the power supply within reach.

## Troubleshooting

- **Plugin loads but `Connected` stays false**: unplug/replug the board,
  check Windows Device Manager for `Odrive-Wheel` under HID devices, confirm
  VID 0x1209 / PID 0x0D40. SimHub log (`Documents\SimHub\Logs`) shows the
  reader thread's device-open messages.
- **`ReportRateHz` reads much lower than 1000**: the game or another app may
  be blocking the USB endpoint. Native HID Input Reports should always
  arrive at the polling interval (1 ms), but heavy USB bus contention on
  the same hub can slow it. Move the wheel to its own USB root port.
- **`WheelPositionDeg` reads wrong**: the plugin has not read `axis.range` yet and
  uses 900°. Check that the Serial LED is green and use **Refresh from device now**.

## License

The plugin retains its existing MIT notice. Firmware and bundled components have
their own licenses; this is not a license statement for the whole combined firmware.
See the [repository README](../../../README.md) for codebase attribution and licensing.
