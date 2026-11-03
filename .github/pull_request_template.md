## Task

Package and stage (see `docs/ROADMAP.md`):

- **User goal:**
- **Scope, and what is left out:**
- **State contract:** which states can change during this, and what happens then (`docs/FOUNDATIONS.md`):
- **Acceptance:**

## What changes

- Changed files (firmware FFB / firmware ODrive / plugin / docs):
- Switches, only for a comparison or tuning variant (command, default):
- New EEPROM addresses (in the block of the package, entered in `VirtAddVarTab`):
- New commands (`<prefix>.*`), including `<prefix>.status?`:

## Checklist

- [ ] No change to ODrive `Config_t` or `odrive-interface.yaml`
- [ ] `EE_LAYOUT_VERSION` unchanged
- [ ] New logic in its own files, shared files only with `// [pkgN]` blocks
- [ ] 8 kHz path without heap/RTOS/printf/double, measured runtime: ___ µs
- [ ] Benefits claimed are measured (`docs/MEASUREMENT.md`), or marked as not measured
- [ ] Firmware builds, no new warnings
- [ ] Plugin builds
- [ ] Size before/after (`arm-none-eabi-size`): text ___ → ___, bss ___ → ___
- [ ] Host and plugin tests pass, chosen by failure cases and state transitions
- [ ] `docs/ROADMAP.md` status and open hardware tests in `docs/TESTING.md` updated

Leave out what does not apply, for example to a pure documentation change.

## Test instructions for the user

### T2 – Compared with before

### T3 – Feature tests

### Known limits / open points
