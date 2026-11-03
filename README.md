# Odrive-Wheel — DIY direct-drive firmware and SimHub configuration

An independently developed fork of [eagabriel/Odrive-Wheel](https://github.com/eagabriel/Odrive-Wheel)
for direct-drive sim racing wheels built from hoverboard or industrial servo motors.
This project combines motor-control firmware, USB HID Force Feedback and a Windows SimHub plugin.

**The goal is one English SimHub interface for setup, configuration, backup, firmware updates,
FFB tuning and diagnosis, with reproducible behaviour and low measured latency.**
That is the finished-product goal; the implementation is still in development.

[Plugin manual](Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md) ·
[Roadmap](docs/ROADMAP.md) · [Hardware](docs/HARDWARE.md) ·
[Build instructions](docs/BUILDING.md) · [Testing](docs/TESTING.md)

## How it works

- The game sends Force Feedback directly to the wheel through USB HID PID / DirectInput.
- The board runs the motor control and FFB processing. SimHub is not in the real-time torque path.
- The SimHub plugin configures the board over USB serial and reads its HID telemetry for status,
  profiles, actions and diagnostics.
- The current motor-control loop runs at 8 kHz; the FFB layer and HID telemetry run at 1 kHz.
  A higher FFB execution rate is a planned experiment, not a demonstrated latency improvement.

Configuration is moving entirely into SimHub. The existing HTML client stays in the repository
as an unchanged upstream reference and temporary fallback for functions the plugin does not yet have.
GitHub Pages is not part of this fork's operation.

## What is available now

The current development code includes:

- USB HID Force Feedback, inherited from the original Odrive-Wheel integration, including constant,
  spring, damper, friction, inertia, ramp and periodic effects.
- A SimHub configuration window, serial connection service, software stop and port release.
- Read-only ODrive parameter tabs, decoded errors, telemetry, FFB profiles and mappable actions.
- Local power-supply data: nominal voltage, continuous current, headroom and optional short-time
  current and duration. These entries do not automatically set motor phase-current limits.
- Reviewed edits of the ten Power / Brake settings to RAM, with fresh checks and typed readback.
- Read-only catalog snapshots, flat JSON export with associated metadata, file and board comparisons,
  and an exportable diagnostic report.
- Firmware build identification, complete ODrive ASCII numeric replies, a firmware capability check
  and a guard against flash operations while a motor is active.

**A catalog snapshot is not a complete NVM image or a complete restore backup.** It includes the
catalog's ODrive values and selected FFB fields. Other FFB settings, profiles, plugin settings,
anticogging maps and flash contents are outside its scope. Files identify missing or uncertain values;
a successful RAM read does not prove that a value is saved in flash.

Automated host and plugin tests exercise production paths with simulated hardware.
Hardware tests of the changes in this fork are deferred and remain explicitly open in
[the test record](docs/TESTING.md). A passing build is not a claim of verified board compatibility.

## What the finished project is intended to provide

The [roadmap](docs/ROADMAP.md) follows the user workflow: setup, backup and restore,
FFB operation, analysis and optimization. Planned work includes:

- **Guided setup:** board selection, motor and encoder calibration, temperature sensors,
  optional holding-brake setup, power data and traceable calibration records.
- **Backup and updates:** reviewed restore, a DFU flasher for this fork's firmware,
  protected configuration sectors and a documented way back to an earlier firmware.
- **FFB operation:** complete tuning, filter/EQ and input/GPIO controls in SimHub,
  with profiles kept separate from hardware data.
- **Protection and diagnosis:** controlled startup, endstop priority, communication monitoring,
  supply and thermal limits, understandable fault reports and operating-limit information.
- **Analysis:** test forces, coastdown and sweep measurements, logging, plots, FFT and an overlay.
- **Measured optimization:** filter and torque-path improvements, motor-specific compensation,
  optional anticogging, observers and experiments with execution rates.

These are goals, not a list of features already available in the plugin. Each improvement needs
appropriate evidence; more filters, a higher rate or a motor model do not automatically improve every wheel.

## Hardware targets

| Board | Current status in this fork |
|---|---|
| MKS XDrive Mini | Firmware build exists; this is the user's test hardware. Hardware validation of the fork's changes is open. |
| MKS XDrive S | Planned; board-specific comparison and validation are still required. |
| MKS XDrive | Planned; the existing FFB integration drives axis 0 only. |

The planned setup range is a **24–48 V nominal power supply** and a brake-resistor input of
**more than 0 up to 20 ohms**, with a separate disabled state. These input ranges are not electrical
ratings for the boards, motors or resistors. Board identity, wiring, sensing and limits must match.

The code retains upstream encoder support, including incremental ABZ, AS5047 and MT6835 paths.
A code path is not a promise that every encoder works on every target board.
ODESC V4.2 belongs to the upstream project's hardware history and is not a current target of this fork.
See [hardware, sensors and calibration](docs/HARDWARE.md) for the support levels and open questions.

## Getting started

1. Read the [hardware notes](docs/HARDWARE.md) and [open test status](docs/TESTING.md).
2. Use firmware built from **this repository**, and keep the exact firmware file and configuration
   records for the setup you are using.
3. Install the [SimHub plugin](Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md) on Windows.
4. Use the configuration window for the implemented functions. The complete setup wizard,
   restore workflow and plugin DFU flasher are still planned.

[Getting started](docs/GETTING_STARTED.md) documents the current bring-up and interim flash routes.
Where it uses the local HTML reference, do not confuse an upstream download with this fork's firmware.

### Builds and downloads

Development firmware is available as the `odrive-wheel-firmware` artifact of a successful
[Build Firmware run in this fork](https://github.com/JustOneMorePrint/Odrive-Wheel/actions/workflows/build.yml).
Artifacts expire after 14 days. Match the artifact to its commit; an upstream release is not a release of this fork.

For a local build, follow [Building from source](docs/BUILDING.md). Clone this repository with submodules:

```sh
git clone --recurse-submodules https://github.com/JustOneMorePrint/Odrive-Wheel.git
cd Odrive-Wheel
```

Firmware builds use ARM GCC, make and the documented generation steps. The SimHub plugin is a Windows
WPF application component targeting .NET Framework 4.8; building it requires a .NET SDK and the SimHub
reference assemblies. Its normal build can copy the DLL into SimHub, so follow the installation/build guide.
A published plugin-DLL release workflow is not yet provided by this fork.

## Working with powered hardware

A direct-drive motor can produce substantial force. Keep the wheel clear for calibration and initial
checks, use appropriate current limits and provide a hardware power cut-off.
The plugin's **SOFTWARE STOP (IDLE)** needs a working USB connection and allows the motor to coast;
it does not replace a hardware E-stop.

Back up what can be backed up before changing configuration or firmware. The planned plugin flasher
must preserve FFB EEPROM, reserved calibration storage and ODrive NVM (sectors S1+S2, S9 and S10+S11).
The runtime flash guard does not protect these sectors during DFU. Current manual flashing can erase
settings; read [Testing](docs/TESTING.md) and [Getting started](docs/GETTING_STARTED.md) first.

## Codebase and acknowledgements

This fork builds on the work of the original projects and contributors:

- **[eagabriel/Odrive-Wheel](https://github.com/eagabriel/Odrive-Wheel):** the original integration,
  local firmware adaptations, HTML tools and initial SimHub plugin.
- **[ODrive Firmware v0.5.6](https://github.com/odriverobotics/ODrive):** motor control, calibration,
  current regulation and the ODrive interface.
- **[OpenFFBoard](https://github.com/Ultrawipf/OpenFFBoard):** HID Force Feedback and effect processing.
  Local FFB sources derive from v1.17.0; the upstream reference is a pinned submodule.
- **[@aksc857-stack](https://github.com/aksc857-stack):** MT6835 encoder integration and related
  threaded SPI, encoder and force-direction work credited by upstream.
- **[@TelksBr](https://github.com/TelksBr) / [ODrive-Wheel-Forge](https://github.com/TelksBr/ODrive-Wheel-Forge):**
  HID compatibility fixes and thermal telemetry credited by upstream.
- **[@simachines](https://github.com/simachines):** autogen tooling support credited by upstream.

FFBeast is a reference for setup and calibration design; it is not identified here as an included codebase.

## Contributing and reporting problems

Read [CLAUDE.md](CLAUDE.md), the [shared contracts](docs/FOUNDATIONS.md) and the relevant roadmap step
before changing code. Tests and hardware-validation limits are documented in [Testing](docs/TESTING.md).

Report issues in [this repository](https://github.com/JustOneMorePrint/Odrive-Wheel/issues).
Include the firmware build, board and encoder, plugin version, reproduction steps and available diagnostic
report. Clearly distinguish simulated results from observations on hardware.

## License

The combined firmware is distributed under **GPLv3**, as stated in the repository's [LICENSE](LICENSE).
ODrive retains its [MIT license](ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/LICENSE.md), and OpenFFBoard is GPLv3.
Retain the applicable upstream copyright and license notices when using or distributing the code.
