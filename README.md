# ADDD

**Another DIY Direct Drive (Wheel)**

Firmware and SimHub tools for DIY direct-drive racing wheels built from hoverboard or industrial servo motors.
ADDD brings setup, calibration, configuration, Force Feedback tuning, backups, firmware updates and analysis into
one English SimHub interface.

[User manual](Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md) · [Hardware](docs/HARDWARE.md) ·
[Build instructions](docs/BUILDING.md) · [Roadmap](docs/ROADMAP.md) · [Testing](docs/TESTING.md)

This README describes the complete ADDD application. Implementation progress and hardware verification are recorded
in the [roadmap](docs/ROADMAP.md) and [test record](docs/TESTING.md).

## One workflow for a DIY wheel

### Setup and calibration

- Select the matching MKS XDrive board and configure the motor, encoder, supply and brake resistor.
- Work through guided motor and encoder calibration, force direction, mechanical center and operating limits.
- Configure temperature sensing and an optional holding brake for motors that need one.
- Keep calibration results traceable to the hardware, firmware and measurement conditions.
- Enter motor data that calibration cannot determine, with explicit units and current conventions.

### Force Feedback and tuning

- USB HID PID / DirectInput Force Feedback: constant, spring, damper, friction, inertia, ramp and periodic effects.
- Local axis effects, endstop priority, controlled startup, center damping and torque slew settings.
- Filters, equalization and notch controls, with profiles for different games and vehicles.
- Input and GPIO configuration, telemetry properties and wheel-mappable SimHub actions.
- Motor-specific current-loop and encoder tuning, optional anticogging and observer-based comfort features.

### Configuration, backup and recovery

- Typed parameters, reviewed changes and readback, with RAM changes distinguished from permanent storage.
- Configuration snapshots, file comparison, versioned backups and traceable calibration records.
- Reviewed restore and firmware updates using images for the selected board.
- Preservation of FFB settings, reserved calibration storage and ODrive configuration during protected updates.
- A documented route back to a known working firmware and configuration.

A catalog snapshot and a complete restore backup have different scopes. ADDD identifies what a file contains, its
source, any failed reads and uncertain values; it does not infer flash persistence from a RAM read or a save request.

### Diagnosis and analysis

- Clear-text errors, live status, operating-limit information and an exportable diagnostic report.
- Logging, plots, FFT and dashboard/overlay data.
- Guided test forces, coastdown and frequency sweeps to characterize the motor and mechanics.
- Measurements of current response, processing delay, filter delay, runtime, jitter and missed deadlines.

The goal is reproducible force with low measured latency. A higher execution rate, more filtering or a more complex
motor model is evaluated against the actual wheel; it is not an automatic improvement. Hands-off estimation is a
comfort feature, not an emergency-stop mechanism.

## How ADDD works

The game sends Force Feedback directly to the board over USB HID. Motor control and real-time force processing run
on the board. SimHub provides configuration, diagnosis, profiles and analysis; it is not in the normal game-to-motor
real-time torque path.

Board-side protection handles operating limits and fault reactions. The plugin explains those states and refuses
invalid actions early. The software stop requests IDLE over USB and lets the motor coast; it does not replace an
independent hardware power cut-off.

All configuration belongs in the SimHub plugin. The inherited HTML client is retained as a reference rather than a
second product interface. GitHub Pages is not used.

## Hardware

The board family is **MKS XDrive Mini, MKS XDrive S and MKS XDrive**. The selected board, firmware target, wiring and
measurement configuration must match. A firmware target label is not physical board detection.

| Item | Configuration |
|---|---|
| Motor | Hoverboard or industrial servo motor with supported feedback and suitable drive characteristics |
| Encoder | The appropriate incremental or absolute feedback path for the board and motor; resolver systems need compatible interface hardware |
| Power supply | 24–48 V nominal, continuous output current, current headroom, optional short-time current and maximum duration |
| Brake resistor | Decimal resistance above 0 up to and including 20 ohms, or a separate disabled state |
| Temperature sensing | Supported board and motor sensor circuits with verified wiring and plausible readings |
| Optional hardware | Holding brake, inputs/GPIO and independent power cut-off as required by the build |

These input ranges are not electrical ratings of every board or motor. Supply current, phase current and regeneration
are separate quantities. Repeating peak loads also need suitable recovery or duty-cycle data. Hardware-specific limits
come from the actual components, not a universal preset.

See [Hardware, sensors and calibration](docs/HARDWARE.md) for the detailed requirements and verification records.

## Installation and builds

- [User manual](Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md): SimHub installation and operation on Windows.
- [Getting started](docs/GETTING_STARTED.md): setup requirements and bring-up guidance.
- [Building from source](docs/BUILDING.md): ARM firmware and .NET plugin builds.
- [Build Firmware](https://github.com/JustOneMorePrint/Odrive-Wheel/actions/workflows/build.yml): development firmware
  artifacts for an exact commit, retained for 14 days.

Use images and plugin builds from this repository. Keep the working firmware and configuration records together.
For source builds, the repository and technical paths retain their existing names:

```sh
git clone --recurse-submodules https://github.com/JustOneMorePrint/Odrive-Wheel.git
cd Odrive-Wheel
```

The plugin targets Windows, WPF and .NET Framework 4.8. See the build guide for the .NET SDK and SimHub reference
assemblies. Build and installation identifiers are not changed merely by the product name.

## Codebase and acknowledgements

ADDD is an independently developed fork of [eagabriel/Odrive-Wheel](https://github.com/eagabriel/Odrive-Wheel).
Its foundations are:

- **eagabriel/Odrive-Wheel:** the original integration, firmware adaptations, HTML tools and initial SimHub plugin.
- **[ODrive Firmware v0.5.6](https://github.com/odriverobotics/ODrive):** motor control, current regulation,
  calibration and the ODrive interface.
- **[OpenFFBoard](https://github.com/Ultrawipf/OpenFFBoard):** USB HID Force Feedback and effect processing.
  The local FFB sources derive from v1.17.0; the repository also keeps a pinned upstream reference.

Existing community contributions remain credited:

- [@aksc857-stack](https://github.com/aksc857-stack): MT6835 integration and related SPI, encoder and force-direction work.
- [@TelksBr](https://github.com/TelksBr) / [ODrive-Wheel-Forge](https://github.com/TelksBr/ODrive-Wheel-Forge):
  HID compatibility fixes and thermal telemetry.
- [@simachines](https://github.com/simachines): autogen tooling support.

FFBeast is a reference for setup and calibration design, not an included codebase.

## Development and support

Read [CLAUDE.md](CLAUDE.md), the [shared contracts](docs/FOUNDATIONS.md) and the relevant roadmap step before
changing code. [Testing](docs/TESTING.md) records the evidence and open hardware checks. See also
[Measurement](docs/MEASUREMENT.md), [Torque control](docs/TORQUE_CONTROL.md) and the
[branding notes](assets/branding/README.md).

Report problems in [this repository](https://github.com/JustOneMorePrint/Odrive-Wheel/issues), with the firmware
build, board and encoder, plugin version, reproduction steps and a diagnostic report where available.
Distinguish simulated results from observations on hardware.

## License

The combined firmware is distributed under **GPLv3**, as stated in the repository's [LICENSE](LICENSE).
ODrive retains its [MIT license](ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/LICENSE.md). The plugin and bundled components
retain their applicable existing notices; the ADDD name does not change their licenses.
