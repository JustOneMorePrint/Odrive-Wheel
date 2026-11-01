# Testvorgehen

Jedes Paket wird einzeln gebaut, geflasht und getestet, bevor es in `main` landet.
Claude schreibt die Testanleitung in die PR-Beschreibung; ausgeführt werden
Hardware-Tests nur vom Nutzer.

## Vor jedem Flashen

1. **ODrive-Konfiguration sichern** (Export als JSON im Web-Client bzw. später im Plugin).
2. **FFB-Einstellungen und Profile sichern** (Profil-Export).
3. **Aktuelle Firmware-Datei aufheben** (`odrive-wheel.bin` des zuletzt funktionierenden
   Stands, inklusive Baseline aus Schritt 0).

## Flashen

- Bevorzugt: Web-Client → Tab „DFU Flash“ → **Datei auswählen** mit der `.bin` aus dem
  PR-Build. Nicht „Fetch latest from GitHub“ verwenden, das lädt die Original-Firmware.
- Alternative: `dfu-util -d 0483:df11 -a 0 -s 0x08000000:leave -D odrive-wheel.bin`.
  Achtung: Die `.bin` deckt auch den Bereich des FFB-Speichers ab; beim Flashen per
  `dfu-util` können die FFB-Einstellungen verloren gehen. Deshalb vorher sichern.
- Die `.bin` gibt es als Artifact im CI-Lauf des PRs oder aus dem lokalen Build.
- Nach dem Flashen mit `sys.build?` prüfen, dass der richtige Stand läuft.

## Testebenen

| Ebene | Was | Wer |
|---|---|---|
| T0 – Build | CI grün, keine neuen Warnungen, Größenänderung (`arm-none-eabi-size`) im PR | Claude |
| T1 – Host-Tests | HAL-freie Logik-Module unter `tests/host/` mit g++ getestet | Claude |
| T2 – Schalter aus | Verhalten identisch zum Stand vor dem Paket | Nutzer |
| T3 – Feature an | Paketspezifische Tests aus der PR-Beschreibung | Nutzer |
| T4 – Rückweg | Alte `.bin` flashen, Config wieder einspielen, alles läuft wie vorher | Nutzer (bei größeren Paketen) |

### T2 – „Schalter aus = identisch“ prüfen

1. Mit der vorherigen Firmware: festen Ablauf im Force-Test fahren (z. B. Konstantkraft-
   Stufen und ein Sinus) und das HID-Log (1 kHz) als CSV speichern.
2. Neue Firmware flashen, alle Schalter des Pakets aus, denselben Ablauf wiederholen.
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
- Not-Aus (Netzteil- bzw. Relais-Abschaltung) in Reichweite.
- Bei Paketen, die den Stromregler oder den 8-kHz-Loop betreffen (Pakete 3, 5, 6, 7):
  zuerst ohne Spiel, nur mit Force-Test bzw. Statusabfragen.
- Nach jedem Test Fehlerzustand prüfen (Axis-, Motor-, Encoder-, Controller-Fehler).
- Bei unerwartetem Verhalten: Schalter aus, Fehler notieren, im PR melden.
