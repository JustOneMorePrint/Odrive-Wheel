## Package

Package number and name (see `docs/ROADMAP.md`):

## What changes

- New features and their switches (command, default = off):
- Changed files (firmware FFB / firmware ODrive / plugin):
- New EEPROM addresses (in the block of the package, entered in `VirtAddVarTab`):
- New commands (`<prefix>.*`), including `<prefix>.status?`:

## Checklist

- [ ] All new features off by default; with the switches off the behavior is unchanged
- [ ] No change to ODrive `Config_t` or `odrive-interface.yaml`
- [ ] `EE_LAYOUT_VERSION` unchanged
- [ ] New logic in its own files, shared files only with `// [pkgN]` blocks
- [ ] 8 kHz path without heap/RTOS/printf/double, measured runtime: ___ µs
- [ ] Firmware builds, no new warnings
- [ ] Plugin builds
- [ ] Size before/after (`arm-none-eabi-size`): text ___ → ___, bss ___ → ___
- [ ] Host tests (if applicable) pass
- [ ] `docs/ROADMAP.md` status updated

## Test instructions for the user

### T2 – Switches off = identical

### T3 – Feature tests

### Known limits / open points
