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
| `docs/HARDWARE.md` | Support level of each board |

## Vor jedem Flashen

1. **ODrive-Konfiguration sichern** (Export als JSON. In the plugin planned next, stage 1 of `docs/ROADMAP.md`; bis dahin
   nur über die lokale Datei `Odrive-Wheel/tools/odrive-wheel.html`).
2. **FFB-Einstellungen und Profile sichern** (Profil-Export).
3. **Aktuelle Firmware-Datei aufheben** (`odrive-wheel.bin` des zuletzt funktionierenden
   Stands, inklusive Baseline aus Schritt 0).

## Flashen

- Läuft SimHub mit dem Plugin, zuerst dort **Release port** drücken. Das Plugin hält den
  seriellen Port, solange es läuft.
- Der DFU-Flasher im Plugin ist geplant (Teilschritt 1e) und noch nicht vorhanden.
- Bis dahin: `dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D odrive-wheel.bin`.
  Achtung: Die `.bin` deckt auch den Bereich des FFB-Speichers ab; beim Flashen per
  `dfu-util` können die FFB-Einstellungen verloren gehen. Deshalb vorher sichern.
- Übergangsweise bleibt die lokale Datei `Odrive-Wheel/tools/odrive-wheel.html` → Tab
  „DFU Flash“ → **Datei auswählen** mit der `.bin` aus dem PR-Build. Nicht „Fetch latest
  from GitHub“ verwenden, das lädt die Original-Firmware. Eine gehostete Fassung über
  GitHub Pages gehört nicht zum Projektbetrieb.
- Die `.bin` gibt es als Artifact im CI-Lauf des PRs oder aus dem lokalen Build.
- Nach dem Flashen mit `sys.build?` prüfen, dass der richtige Stand läuft.

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

- Erste Tests mit reduzierter Leistung: `axis.maxtorque` klein (z. B. 3 Nm) und
  `motor.config.current_lim` reduziert; erst nach erfolgreichem Test schrittweise erhöhen.
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
- Bei unerwartetem Verhalten: Schalter aus, Fehler notieren, im PR melden.
