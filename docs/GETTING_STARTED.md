# Getting started

This guide describes the current development version of this fork. The finished product is intended to provide setup,
configuration, backups, firmware updates and analysis entirely through the SimHub plugin. That complete workflow is
still being built; see the [roadmap](ROADMAP.md) and [plugin manual](../Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md).

Hardware tests remain deferred at the user's request. A successful build or simulated-board test is not a hardware
compatibility claim. Nothing in this guide asks for those tests to be performed now.

## 1. Check the board and motor

- MKS XDrive Mini has a firmware build in this fork. Board tests are open.
- MKS XDrive S and MKS XDrive are planned targets; do not assume the Mini image fits them.
- Check the motor, encoder, temperature sensing, supply and brake resistor against their actual specifications.
  The intended supply range is 24–48 V nominal. This is not an electrical rating of every board.
- Motor phase current and DC supply current are different quantities. A generic current, resistor power or torque
  value is not a safe default for an unknown motor or board.

The source of each setting and calibration, the available sensor paths and the support levels are in
[Hardware, sensors and calibration](HARDWARE.md).

## 2. Get this fork's software

Use [this repository](https://github.com/JustOneMorePrint/Odrive-Wheel), not an upstream release as a substitute for
its current firmware. The plugin's new configuration features use the capability declaration of this fork.

- **Firmware:** download `odrive-wheel.bin` from a successful
  [Build Firmware run](https://github.com/JustOneMorePrint/Odrive-Wheel/actions/workflows/build.yml) for the commit
  you intend to use, or [build it locally](BUILDING.md). CI artifacts are retained for 14 days.
- **Plugin:** follow the [installation and build instructions](../Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md).
  The plugin runs in SimHub on Windows and targets .NET Framework 4.8/WPF. There is no automated plugin release
  download promised by this guide.

Keep the firmware and plugin revision together in your records. `sys.build?` identifies the running firmware;
`sys.swver` is a compatibility value and does not prove which features are available.

## 3. If a working board is already configured

Use the plugin to connect, inspect its firmware capability status and read the existing settings. The Snapshot tab
can capture the current catalog, export values and metadata, compare files and write a diagnostic report.

A **catalog snapshot is not a complete restore backup**. It reads current RAM values, not flash contents, and
does not capture every FFB setting, profile, input configuration or anticogging map. Its scope, failed reads and
uncertain values are recorded explicitly. Keep older configuration exports, profiles and the last working firmware
as well; follow the [backup checklist](TESTING.md#vor-jedem-flashen) before any later firmware change.

File comparison can run offline. Treat values from another board or an older file as reference data, not settings
to apply automatically.

## 4. First flashing and later updates

The plugin's board-aware, protected-sector DFU flasher is planned and is not implemented yet. The historical HTML
flasher is not the approved protected-update path for this fork.

The current flash layout is:

| Area | Purpose |
|---|---|
| S0 and S3–S9 | Firmware image; S9 is reserved for the planned slot store and must be protected by the future updater |
| S1+S2 | FFB emulated EEPROM |
| S10+S11 | ODrive nonvolatile configuration |

A contiguous `.bin` can span the FFB sectors even when those bytes contain only `0xFF`. A whole-image flashing tool
can erase them. Do not infer preservation from the file name, the image length or an old browser message. S1, the
running firmware's flash guard, does not run in the DFU bootloader.

For a **blank board**, the manual `dfu-util` route is described in [Building from source](BUILDING.md#manual-flashing).
Follow the board maker's BOOT0/reset procedure to enter its STM32 DFU bootloader. Windows may require the WinUSB
driver for the `STM32 BOOTLOADER` device via [Zadig](https://zadig.akeo.ie/); this is separate from the normal USB CDC
serial device. A manual whole-image flash is not a guarantee that existing settings survive.

For an **existing working setup**, first preserve the data and working firmware listed in [Testing](TESTING.md).
The future updater's exact protected-sector rules are in [Package 1e](packages/PKG1.md#details-zu-1e).

## 5. Motor setup and calibration

The full editor, state changes and guided setup are planned in the plugin. The current Power / Brake editor writes
ten settings to RAM; it is not a complete first-time motor setup wizard.

The legacy local file `Odrive-Wheel/tools/odrive-wheel.html` remains available as a reference and temporary manual
fallback for functions the plugin does not yet provide. It is not developed further. Its automatic GitHub firmware
download selects upstream firmware, and its example motor values and flash-preservation claims are not this fork's
specification. GitHub Pages is not used.

When a manual setup is eventually performed, use the order in [Hardware](HARDWARE.md#calibration-what-exists-and-what-to-take-over):

1. Identify the board, motor and encoder, and obtain their electrical and thermal data.
2. Establish supply and brake limits from that hardware, with no automatic conversion of DC current to phase current.
3. Enter the supported encoder mode, CPR, pole pairs and motor calibration inputs.
4. Perform the required motor and encoder calibrations deliberately. R/L measurement energizes the motor and can
   move it; offset and index searches can rotate it. Review existing ODrive startup actions before powering the setup;
   they can request motion. The planned wizard does not require calibration movement at every power-up.
5. Check errors, force direction, mechanical center and the chosen operating limits before game use.
6. Save only with the axes in the required inactive state. ODrive and FFB have separate stores. A reboot or `OK`
   alone does not confirm persistence; see [Shared foundations](FOUNDATIONS.md#states-of-a-value).

The plugin's software stop requests IDLE over USB and lets the motor coast. It is not a hardware emergency stop.
Keep an independent means to remove drive power and follow the [hardware test precautions](TESTING.md#sicherheits-checkliste-für-hardware-tests).

## Documentation and support

- [Plugin manual](../Odrive-Wheel/tools/simhub-plugin/USER_MANUAL.md): current controls, files and known limits.
- [Roadmap](ROADMAP.md): what exists and what is planned.
- [Testing](TESTING.md): open board tests and validation procedures.
- [Torque and current control](TORQUE_CONTROL.md): the actual control path and units.
- [Issues for this fork](https://github.com/JustOneMorePrint/Odrive-Wheel/issues): include firmware build, board and
  motor details, reproduction steps and a diagnostic report where available. State which steps were simulated
  and which were actually run on hardware.
