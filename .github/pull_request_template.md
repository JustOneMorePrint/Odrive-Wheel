## Paket

Paket-Nr. und Name (siehe `docs/ROADMAP.md`):

## Was ändert sich

- Neue Features und ihre Schalter (Befehl, Default = aus):
- Geänderte Dateien (Firmware FFB / Firmware ODrive / Plugin):
- Neue EEPROM-Adressen (im Block des Pakets, in `VirtAddVarTab` eingetragen):
- Neue Befehle (`<präfix>.*`), inkl. `<präfix>.status?`:

## Checkliste

- [ ] Alle neuen Features standardmäßig aus; mit Schaltern aus Verhalten unverändert
- [ ] Keine Änderung an ODrive-`Config_t` oder `odrive-interface.yaml`
- [ ] `EE_LAYOUT_VERSION` unverändert
- [ ] Neue Logik in eigenen Dateien, gemeinsame Dateien nur mit `// [pkgN]`-Blöcken
- [ ] 8-kHz-Pfad ohne Heap/RTOS/printf/double, Laufzeit gemessen: ___ µs
- [ ] Firmware baut, keine neuen Warnungen
- [ ] Plugin baut
- [ ] Größe vorher/nachher (`arm-none-eabi-size`): text ___ → ___, bss ___ → ___
- [ ] Host-Tests (falls zutreffend) laufen
- [ ] `docs/ROADMAP.md` Status aktualisiert

## Testanleitung für den Nutzer

### T2 – Schalter aus = identisch

### T3 – Feature-Tests

### Bekannte Grenzen / offene Punkte
