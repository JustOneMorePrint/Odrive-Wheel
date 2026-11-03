# Testvorgehen

Claude schreibt die Testanleitung in die PR-Beschreibung; ausgeführt werden
Hardware-Tests nur vom Nutzer.

## Choosing tests

Tests follow what can go wrong, not a fixed list and not line coverage. For each task:

1. Name the failure cases and state transitions of its state contract: what if the stop, the connection or a capability changes at each step,
   what if a step fails halfway, what is shown afterwards.
2. Show the regression red on the old code where it is a correction. Use real services, and a stand-in only for hardware.
3. Remove a layer of protection once to see that a test notices it where layers overlap. No campaigns.
4. Name what the tests cannot show, in particular anything about the real board.

## Open hardware tests

Deferred at the user's request. A point stays here until the user has run it, and nothing counts as checked on a board before that.

| From | What is open |
|---|---|
| [PR #2](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/2), 1a | Window, connection, stop, port release, profiles |
| [PR #3](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/3), S1 | T2 and T3, also the calibrations with the changed order |
| [PR #4](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/4) | Save order |
| [PR #5](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/5) | Read-only tabs against the real firmware |
| [PR #6](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/6) | `%.9g` of newlib-nano, values read back on the board |
| [PR #7](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/7) | Power supply data, reading and writing the ten fields, the `%f` parser behind `w` |
| [PR #8](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/8) | `sys.capabilities?`, the T3 steps of the PR |
| [PR #10](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/10), 1b | Catalog snapshot on the board, with and without the stop latch, the export in the web client's import in a real browser, both comparisons, the report's temperatures against a known temperature |
| `docs/HARDWARE.md` | Support level of each board |

## Vor jedem Flashen

1. **ODrive-Konfiguration sichern.** In the plugin: tab Snapshot, **Capture snapshot**, **Export snapshot…**. It is a catalog snapshot
   of the RAM and no complete backup: FFB settings beyond `axis.maxtorque` and `fx.master`, the profiles and the anticogging map are not in
   it (`docs/packages/PKG1.md`). Otherwise the export of the local file `Odrive-Wheel/tools/odrive-wheel.html`.
2. **FFB-Einstellungen und Profile sichern** (Profil-Export).
3. **Aktuelle Firmware-Datei aufheben** (`odrive-wheel.bin` des zuletzt funktionierenden
   Stands, inklusive Baseline aus Schritt 0).

## Flashen

- Release the serial port in SimHub before another tool takes it over.
- The protected DFU updater in the plugin is planned (1e), not implemented. The historical HTML flasher is not the approved
  protected-update path; its automatic GitHub download selects upstream firmware, not this fork. GitHub Pages is not used.
- For a blank, matching board, see the [manual flashing route](BUILDING.md#manual-flashing). A raw whole-image flash can erase
  existing settings. FFB EEPROM is in S1+S2, ODrive NVM in S10+S11, and S9 is reserved for the planned calibration store.
- Preserve the records listed above before an update. Neither the current catalog snapshot nor the legacy JSON export is a full
  NVM or calibration-map backup. The future updater must enforce the sector rules in [Package 1e](packages/PKG1.md#details-zu-1e).
- Use the `.bin` from this fork's successful CI run for the intended commit, or the matching local build. Keep the file.
- After a user-performed flash, check `sys.build?`. S1 protects flash access in the running application, not the DFU bootloader.

## Testebenen

| Ebene | Was | Wer |
|---|---|---|
| T0 – Build | CI grün, keine neuen Warnungen, Größenänderung (`arm-none-eabi-size`) im PR | Claude |
| T1 – Host-Tests | HAL-freie Logik-Module unter `tests/host/` mit g++ getestet, Plugin-Logik unter `tests/plugin/` gegen eine Attrappe des Boards | Claude |
| T2 – Compared with before | Where a switch exists: off is identical. Where a change claims no visible effect: identical | Nutzer |
| T3 – Feature an | Paketspezifische Tests aus der PR-Beschreibung | Nutzer |
| T4 – Rückweg | Alte `.bin` flashen, Config wieder einspielen, alles läuft wie vorher | Nutzer (bei größeren Paketen) |

Die Host-Tests führen auch Produktionsfunktionen aus, mit Stubs für Hardware und umgebende Klassen
(`tests/host/README.md`). Sie zeigen die Logik der Abläufe und ersetzen keinen Test am Board.

### T2 – Compare with the state before

1. Mit der vorherigen Firmware: festen Ablauf im Force-Test fahren (z. B. Konstantkraft-
   Stufen und ein Sinus) und das HID-Log (1 kHz) als CSV speichern.
2. Neue Firmware flashen, vorhandene Schalter aus, denselben Ablauf wiederholen.
3. `torque_output`, `Iq_measured` und Position vergleichen. Abweichungen im Rahmen des
   Messrauschens sind ok; systematische Unterschiede sind ein Fehler.
4. Kurz frei fahren (ein Spiel), subjektiver Gegencheck.

### T3 – Feature-Tests

Stehen pro Paket in der PR-Beschreibung: welcher Schalter, welche Werte, was erwartet
wird, woran man einen Fehler erkennt (z. B. Statusabfrage `<präfix>.status?`).

## Sicherheits-Checkliste für Hardware-Tests

- Choose reduced initial torque and current from the actual motor, board, supply and mechanics. There is no universal safe Nm/A
  value for every build; increase only after the corresponding checks succeed.
- Beim Armen und bei den ersten Sekunden mit neuem Feature: Hände weg vom Lenkrad,
  Lenkrad frei drehbar, nichts im Schwenkbereich.
- Not-Aus (Netzteil- bzw. Relais-Abschaltung) in Reichweite. Der Software-Stopp des Plugins
  ersetzt ihn nicht.
- Bei Paketen, die den Stromregler oder den 8-kHz-Regelkreis betreffen (Pakete 3, 5, 6, 7):
  zuerst ohne Spiel, nur mit Force-Test bzw. Statusabfragen.
- Nach jedem Test Fehlerzustand prüfen (Axis-, Motor-, Encoder-, Controller-Fehler).
- Am angetriebenen Lenkrad bei aktivem Motor keine Flash-Befehle zum Testen senden (`sys.save`, `sys.eetest`,
  `sys.eedump`, `sys.eeformat`, `ss`, `se`), auch nicht zum Prüfen einer Sperre. Bei versagender Sperre wären es echte
  Flash-Zugriffe, Softwaregrenzen für Moment und Strom schützen dann nicht verlässlich.
- On unexpected behaviour, stop and remove drive power as needed, record the error and report it in the PR. If the function has a
  comparison switch, return to its baseline only after the setup is inactive. Protection fixes have no old/unsafe mode to select.
