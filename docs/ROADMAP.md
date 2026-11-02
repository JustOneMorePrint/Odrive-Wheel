# Roadmap – Erweiterungen von Odrive-Wheel

Grundlage: Code-Analyse des Stands `a2beb5c` (eagabriel/Odrive-Wheel, V0.32 / FW v1.1.x),
Vergleich mit OpenFFBoard-Upstream, Simucube, Fanatec, Moza und Fachliteratur.
Arbeitsregeln: `CLAUDE.md`. Testvorgehen: `docs/TESTING.md`.

## Entscheidungen

- Jedes Feature hat einen Schalter, Default = altes Verhalten.
- Kalibrierwerte für alt und neu werden beide gespeichert (Slots A/B), Umschalten ohne
  Neukalibrierung.
- Alle Pakete sind einzeln umsetzbar; gemeinsame Dateien werden dafür mehrfach angefasst.
- Kompatibilität alte/neue Firmware bzw. Software ist kein Ziel.
- Der Web-Client wird durch das SimHub-Plugin ersetzt und nicht mehr gepflegt.
- Telemetrie-Effekte über SimHub (ABS, Schaltschlag, geschwindigkeitsabhängige Dämpfung)
  sind vorerst geparkt (siehe Backlog).
- Die Alt/Neu-Schalter gelten **global** und sind nicht Teil der Profile. Profile
  enthalten weiterhin nur die bestehenden FFB-Einstellungen.
- Testhardware: **MKS XDrive Mini**.

## Status

| Schritt | Inhalt | Status |
|---|---|---|
| 0 | Prozess-Setup | ☐ |
| 1 | Plugin-Basis (Web-Client-Ersatz), Teilschritte 1a–1e, siehe `docs/packages/PKG1.md` | ☐ |
| 2 | Plugin-Analyse | ☐ |
| 3 | Achs-Pipeline, Schutz und 8 kHz | ☐ |
| 4 | Signalkette vom Spiel | ☐ |
| 5 | Stromregler | ☐ |
| 6 | Encoder und Anticogging | ☐ |
| 7 | Beobachter und Hands-off | ☐ |
| S1 | Flash-Schutz in der Firmware, siehe unten | ☐ |

Reihenfolge: 0, dann 1. Danach beliebig. Ausführliche Spezifikationen der Pakete
liegen unter `docs/packages/PKGn.md` und werden vor dem Start des jeweiligen Pakets
angelegt.

## Adressblöcke und Befehls-Präfixe

| Paket | EEPROM-Block (virtuelle Adressen) | Befehls-Präfix | Weitere Speicher |
|---|---|---|---|
| 1, 2 | – (nur Plugin) | – | Plugin-Einstellungen in SimHub |
| 3 | `0x0600–0x063F` | `hap.*` | – |
| 4 | `0x0640–0x067F` | `sig.*` | – |
| 5 | `0x0680–0x06BF` | `cur.*` | – |
| 6 | `0x06C0–0x06FF` | `enc.*` | Flash-Sektor 9 (`0x080A0000`, 128 KB) |
| 7 | `0x0700–0x073F` | `obs.*` | – |
| S1 | – | `safe.*` | – |
| Reserve | `0x0740–0x07FF` | – | – |

Bereits belegt (nicht verwenden): `0x0000–0x0004`, `0x00FF`, `0x0100–0x0106`,
`0x0200–0x020C`, `0x0250–0x0274`, `0x0300–0x0304`, `0x04F0–0x04F2`.

## Schritt 0 – Prozess-Setup

Kein Feature-Paket, sondern Vorbereitung. Ändert kein Fahrverhalten.

- `CLAUDE.md`, `docs/ROADMAP.md`, `docs/TESTING.md`, `.github/pull_request_template.md` anlegen.
- Firmware: Git-Hash und Branch zur Build-Zeit einbetten (Makefile-Define) und als
  `sys.build?` ausgeben. Einzige Code-Änderung in diesem Schritt.
- `tests/host/` anlegen: minimales g++-Gerüst für HAL-freie Logik-Module, optional als
  CI-Job.
- Baseline: Firmware und Plugin unverändert bauen; Nutzer sichert Baseline-`.bin`,
  ODrive-Config und FFB-Einstellungen (siehe `docs/TESTING.md`).

## Paket 1 – Plugin-Basis (Web-Client-Ersatz, nur Plugin)

| Feature | Vorteil | Änderung |
|---|---|---|
| Verbindung und ODrive-Parametereditor | Alle ODrive-Einstellungen in SimHub | Plugin: `OdriveSerialCli.cs` erweitert; neu Parameter-Service und Editor |
| Quick-Start-Assistent | Komplette Inbetriebnahme in SimHub | Plugin: neu Assistent |
| KUKA-Setup: Kt-Wizard (Nm/A_rms ↔ Nm/A_peak, Ke in V/krpm), KTY84-Preset, Haltebremse über ODrive-GPIO-Modus `MECH_BRAKE` | Echte Nm, Temperaturschutz, Bremse schließt automatisch | Plugin; Hardware: MOSFET oder Relais |
| Moment-Drehzahl-Rechner, Empfehlungen zu Strom-/PLL-Bandbreite (rad/s!) und Feedforward | Du weißt, ab welcher Lenkgeschwindigkeit das Moment einbricht | Plugin |
| FFB-Einstellungen der heutigen Firmware (`axis.*`, `fx.*`, EQ) | Alles, was der Web-Client heute einstellt | Plugin: `SettingsControl`, Profil-Klassen |
| Bereich „Erweiterungen“ | Andockpunkt für die Steuerelemente der Pakete 3–7 | Plugin |
| DFU-Flasher mit diesem Fork als Quelle | Updates ohne Browser | Plugin: neu DFU-Klasse; Bootloader-Sprung über vorhandenen ODrive-Befehl |
| Resolver-Anbindung | Motor ohne Umbau der Rückführung nutzbar | Nur Hardware: AD2S1210 an ABZ-Eingang oder Magnet mit MT6835 |

## Paket 2 – Plugin-Analyse (nur Plugin)

| Feature | Vorteil | Änderung |
|---|---|---|
| Performance-Test, Coastdown, Frequenz-Sweep | J, b und Losbrechmoment als Basis fürs Tuning | Plugin: neu Test-Runner; Testkräfte als HID-PID-Reports wie im Web-Client (nur ohne laufendes Spiel) |
| Live-Plots und FFT | Sehen, was Spiel und Motor gerade machen | Plugin: neu Chart-Fenster |
| Overlay | Ersatz für das PiP-Overlay | SimHub-Dashboard mit Plugin-Properties |
| Logger und Fehleranzeige | Fehler im Klartext | Plugin: `OdriveErrorDecoder.cs` |

## Paket 3 – Achs-Pipeline, Schutz und 8 kHz

| Feature | Vorteil | Änderung |
|---|---|---|
| Endstopp mit Vorrang (nicht mehr mit `fxratio` skaliert, nicht mit Spielkraft gemeinsam geclippt) | Anschlag hält immer | FW-FFB: neu `haptics_core.*`, Hook in `ffb_task.cpp` |
| Soft-Start (Armen, Index-Fund, Nullen) | Kein Schlag | Core |
| Speed-Limiter und Out-of-Bounds-Abschaltung | Schutz beim Drift-Snap | Core |
| Netzteil-Limiter (statt Disarm bei `dc_max_positive_current`) | FFB fällt nicht aus | Core, Bridge |
| I²t-Thermomodell | Peak kurz nutzbar, dauerhaft geschützt | Core, Bridge |
| Watchdog (ODrive-Axis-Watchdog füttern) und HID-Timeout | Sicherer Zustand bei Hängern | `ffb_task.cpp`, Bridge |
| E-Stop-Eingang und Status-Ausgang | ESP kann sauber abschalten | `gpio_inputs.*` |
| Expo auf Kraft (alter Expo auf Position bleibt als „alt“) | Lineare Lenkung | Core |
| Neue Effekt-Filter für Damper/Friction/Inertia (alte: Q 0,2 → ca. 15–40 ms Verzug) | Kein Filterverzug | Core |
| Center-Damping, Slew-Rate in Nm/ms, `pos_estimate` statt `shadow_count` | Ruhe um die Mitte, vergleichbare Werte | Core |
| Ausführungsrate 1 kHz oder 8 kHz | Bis ca. 8× härterer Anschlag (Passivität: b > KT/2 + \|B\|) | Hook in `controller.cpp`, Bridge |
| Bedienung, Status | – | `cmd_table.cpp` (`hap.*`), `eeprom_addresses.*`; Plugin: Steuerelement, Profilfelder |

## Paket 4 – Signalkette vom Spiel

| Feature | Vorteil | Änderung |
|---|---|---|
| Filter-Updates ohne Zurücksetzen des Zustands (heute: `calcBiquad()` nullt z1/z2) | Kein Kraftloch/-sprung beim Live-Tuning | `Filters.*`, `EqCascade.*` |
| Adaptiver Rekonstruktionsfilter, echte CF-Rate statt fester „1000“ | Weniger Körnigkeit | `EffectsCalculator.*`, `cmd_table.cpp` (`sig.*`) |
| Erweitertes EQ, Notch-Filter | Gezielte Klangformung | `EqCascade.*` |
| Static Force Reduction | Lange Kurven ermüden weniger | `EffectsCalculator.*` |
| Mutex zwischen USB-Callback und FFB-Thread (ohne Schalter) | Keine sporadischen Abstürze | `HidFFB.cpp`, `EffectsCalculator.*` |
| Bedienung, EQ-Kurve | – | `eeprom_addresses.*`; Plugin: Steuerelement |

## Paket 5 – Stromregler

| Feature | Vorteil | Änderung |
|---|---|---|
| Deadband-Modus: alt (Fehler-basiert, 0,1 A) / sollwertbasiert / weich | Feine Kräfte bleiben erhalten | `foc.cpp`, `foc.hpp` |
| Maximale Modulation einstellbar (heute fest 0,8) | Ca. 6–12 % mehr Drehzahlreserve | `foc.cpp` |
| Tuning-Slot B für Strombandbreite, Encoder-PLL, Feedforward | Alt-/Neu-Tuning ohne Umstellen | `motor.*` (Laufzeitwerte), Bridge, EEPROM |
| Bedienung, Status | – | `cmd_table.cpp` (`cur.*`), `eeprom_addresses.*`; Plugin: Steuerelement |

## Paket 6 – Encoder und Anticogging

| Feature | Vorteil | Änderung |
|---|---|---|
| Harmonic Compensation | Weniger Geschwindigkeitswelligkeit | `encoder.*` |
| Bidirektionales Anticogging in Slot B (3600 Floats) | Kein Richtungsversatz | `controller.cpp` |
| Phase-Offset in Slot B | Umschalten ohne Neukalibrierung (nur disarmt) | `encoder.cpp` |
| Slot-Speicher | Alt- und Neu-Kalibrierung bleiben erhalten | neu `slot_store.*`, Linker-Script (FLASH 720K → 592K, neue Region Sektor 9) |
| Kalibrier-Assistent | Geführte Einmal-Kalibrierung | `cmd_table.cpp` (`enc.*`), Bridge; Plugin: Assistent |

## Paket 7 – Beobachter und Hands-off

| Feature | Vorteil | Änderung |
|---|---|---|
| Zustandsbeobachter im 8-kHz-Loop (Iq·Kt als Eingang) | Beschleunigung und Fahrermoment ohne Verzug | neu `observer.*`, Aufruf in `controller.cpp` |
| J und b als Parameter | Läuft auch ohne Paket 2 | `cmd_table.cpp` (`obs.*`), `eeprom_addresses.*` |
| Hands-off-Erkennung und -Reaktion am Drehmoment-Sollwert | Rad schlägt beim Loslassen nicht aus | `observer.*`, Bridge |
| Bedienung, Status | – | Plugin: Steuerelement |

## S1 – Flash-Schutz in der Firmware

Reiner Schutz ohne Schalter, vom Nutzer freigegebene Ausnahme von Regel 1. Setzt kein anderes
Paket voraus. Wo dieser Schritt und mögliche weitere Sicherheits- und Messschritte in der
Reihenfolge stehen, ist noch nicht entschieden.

Beim Schreiben oder Löschen steht die CPU, weil sie ihren Code aus demselben Flash holt, und mit ihr
der Regel-Interrupt. Die PWM bleibt dann auf dem letzten Wert, und niemand regelt den Strom. Deshalb:

- **Keine Flash-Operation, solange ein Motor aktiv ist.** Aktiv heißt auf irgendeiner Achse: Motor
  scharf, angeforderter Zustandswechsel noch nicht übernommen oder Achse nicht in IDLE.
- **Kein Armen während einer Flash-Operation.** `Motor::arm` verweigert. Der Schutz führt dafür keinen
  eigenen Fehlercode ein, die weitere Folge bestimmt der laufende Ablauf (siehe „Verweigertes Armen“).

| Weg | Bei aktivem Motor, laut Code |
|---|---|
| `sys.save!`, `sys.save?` | Antwort `ERR`, `sys.savestat?` bleibt unverändert |
| `sys.eetest!`, `sys.eetest?` | Antwort `ERR` |
| `sys.eedump?` | Antwort `ERR`. Der Befehl schreibt trotz `?` einen Testwert und kann über `EE_Init` löschen |
| `sys.eeformat!`, `sys.eeformat?` | Antwort `ERR` |
| `se` | Keine Wirkung und keine Antwort, wie `ss` |
| `ss` | Unverändert, der Schutz stammt aus der Original-ODrive-Firmware |

`safe.status?` liefert `now=<allowed|busy|armed|requested|active> blocked=<n> last=<none|busy|armed|requested|active> armrefused=<n>`:
was jetzt gälte, wie viele Flash-Operationen seit dem Start abgelehnt wurden, der letzte Grund und
wie viele Armen-Versuche in eine Flash-Operation fielen.

Mit allen Achsen in IDLE verhalten sich die Befehle laut Code wie bisher. Beim Booten läuft
`ffb_init_storage_early` weiter ohne Schutz, die Achsen-Threads starten erst danach. Neue
Flash-Aufrufe fallen in `tests/host/test_flash_paths.sh` auf.

### Verweigertes Armen

`Motor::arm` gibt `false` zurück, die Aufrufer ignorieren das und sehen nur, dass `is_armed_` falsch
bleibt. Danach greift die vorhandene Logik des jeweiligen Ablaufs, und die kann vorhandene Fehler
melden. `armrefused` zählt jeden Fall. Laut Code:

| Ablauf | Folge |
|---|---|
| Closed Loop, Lock-in-Spin, Index-Suche, Richtungssuche, Encoder-Offset, Homing | Bricht ab, Achse geht auf IDLE, kein neuer Fehler |
| Hall-Polarität, Hall-Phase | Ebenso. `hall_polarity_calibrated` beziehungsweise `hall_edge_phcnt` sind dann aber schon zurückgesetzt |
| Motorkalibrierung, Widerstand | Bricht ab und überschreibt `phase_resistance` im RAM ohne Messung: mit 0 Ω, bei `calibration_current` 0 mit NaN. Nur der NaN-Fall meldet `ERROR_PHASE_RESISTANCE_OUT_OF_RANGE` |
| Motorkalibrierung, Induktivität | Würde `phase_inductance` auf NaN setzen und `ERROR_PHASE_INDUCTANCE_OUT_OF_RANGE` melden. Laut Logik nicht erreichbar, weil während einer Kalibrierung keine Flash-Operation startet |

Das setzt voraus, dass die Zustandsanforderung eintrifft, während eine Flash-Operation läuft. Befehle
über USB laufen nacheinander im selben Thread, dort lehnt zuerst die Flash-Sperre ab oder das
Speichern ist schon fertig. Es bleiben andere Wege wie CAN oder UART. Ein danach gesendetes `ss` würde
den überschriebenen Widerstand speichern, ein Neustart ohne `ss` stellt den gespeicherten Wert wieder
her. Das Muster ist nicht neu, auch eine abgebrochene Messung überschreibt den Wert. S1 ergänzt einen
weiteren Auslöser, die Abläufe selbst sind unverändert.

### Nachweisstand

Die Host-Tests belegen die Entscheidung und den Aufbau des Quelltexts, führen die Firmware aber nicht
aus. Am Board ist S1 noch nicht geprüft, offen sind T2 und T3 aus
[PR #3](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/3).

Bei aktivem Motor werden am angetriebenen Lenkrad zum Testen keine Flash-Befehle gesendet, nur
Statusabfragen. Bei versagender Sperre wären sie echte Flash-Zugriffe, und auch ein EEPROM-Seitenwechsel
oder `EE_Init` können löschen. Softwaregrenzen wie `axis.maxtorque` oder `current_lim` sind dafür keine
verlässliche Absicherung. Schreibtests laufen nur bei nachweislich inaktiven Achsen.

Statusabfragen weisen die Wirkung der Sperre nicht nach. Auch nach T2 und T3 bleibt ungeprüft, ob die
gebaute Firmware bei aktivem Motor wirklich ablehnt und ob `Motor::arm` während einer Flash-Operation
verweigert. Ein möglicher zusätzlicher Versuch ist im PR beschrieben und nicht ausgeführt.

## Optionale Verzahnungen

Werden vom später umgesetzten Paket mitgebracht, jeweils mit eigenem Schalter.

- 2 → 7: gemessene J- und b-Werte in den Beobachter übernehmen.
- 7 → 3: Core nutzt Beschleunigung des Beobachters statt eigener Schätzung.
- 2 → 4: Bode-Messung über die neuen Filterkurven legen.

## Backlog

- **Telemetrie-Effekte über SimHub** (geparkt): Vendor-OUT-Report, ABS/Schaltschlag,
  geschwindigkeitsabhängige Dämpfung. Freie SimHub-Version liefert Telemetrie nur mit
  ca. 10 Hz, Lizenz 60 Hz; direkte Spiel-Leser im Plugin wären schneller.
- **Altlast:** `fx.master` wird auf `0x04F0` gespeichert, die Adresse fehlt aber in
  `VirtAddVarTab` → geht beim Page-Transfer verloren. Behebung als eigener kleiner PR,
  nach Rücksprache.
- **Altlast:** `docs/TORQUE_CONTROL.md` nennt Strombandbreiten in „Hz“, der Parameter ist rad/s.
