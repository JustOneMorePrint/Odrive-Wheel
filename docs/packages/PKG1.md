# Paket 1 – Plugin-Basis (Web-Client-Ersatz, Teil 1)

Ziel: Das SimHub-Plugin übernimmt alle Funktionen des Web-Clients außer den
Analyse-Werkzeugen (Performance-Test, Sweep, Live-Plots, FFT, Overlay, Force-Test),
die in Paket 2 folgen. No firmware changes in this package, except the read-only
`sys.capabilities?`.

Die Konfiguration erfolgt ausschließlich über das SimHub-Plugin. Der Web-Client bleibt nur
als lesbare Referenz im Repository und wird nicht weiterentwickelt, GitHub Pages gehört
nicht zum Projektbetrieb. Wo die Testspalte unten den Web-Client nennt, ist die lokale Datei
`Odrive-Wheel/tools/odrive-wheel.html` gemeint.

**Status:** 1a is in `main`, its hardware tests are open. Of 1b, the read-only tabs, the power supply
data, the editing of Power / Brake to RAM and the firmware capability check are implemented and not
checked on the board ([Status of 1b](#status-of-1b)). The rest of 1b and 1c–1e are planned, so the plugin
does not have those functions yet. The capability check is the one firmware change of this package, see
[Firmware capabilities](#firmware-capabilities).

## Architektur

- **Eigenes Konfigurationsfenster** (WPF), geöffnet über einen Button im bestehenden
  SimHub-Panel. Das Panel bleibt für Status, Profile und Aktionen zuständig.
- **Verbindungsdienst:** ein Hintergrund-Thread hält den COM-Port, arbeitet eine
  Befehlswarteschlange ab, erkennt Trennen/Wiederverbinden und meldet Fehler.
  Button „Port freigeben“, damit Web-Client oder `dfu-util` übernehmen können.
  Der bestehende Profil-Service wird auf diesen Dienst umgestellt.
  ODrive-Antworten tragen kein Echo, und ein OpenFFBoard-Echo nennt nur den Pfad, nicht die
  Anfrage. Beide Antwortwege laufen in der Firmware getrennt und können sich überholen. Nach
  einem Timeout oder einem Abbruch synchronisiert der Dienst deshalb den Weg des nächsten
  Befehls, jeweils mit einer eigenen Marke: `f <n>` mit einer Zahl oberhalb von `AXIS_COUNT`
  liest und ändert nichts und wird mit `invalid motor <n>` beantwortet; `odwsync.m<n>?` ruft
  keinen Handler auf und wird mit `[odwsync.m<n>?|NOT_FOUND]` beantwortet. Die Klasse `odwsync`
  darf es in `cmd_table.cpp` deshalb nicht geben (`tests/host/test_cmdparser_sync.cpp`).
  Ohne das Echo des eigenen Wegs geht kein Befehl auf diesem Weg hinaus.
- **Parameter-Service:** ODrive-ASCII (`r`/`w`, `ss`, Fehler löschen, Zustand setzen)
  und OpenFFBoard-Befehle (`?`, `=`, `!`). Ein **Parameter-Katalog** als Datendatei
  (Pfad, Typ, Einheit, Min/Max, Gruppe/Tab, Beschreibung) wird aus den Tab-Definitionen
  und Tooltips des Web-Clients übernommen. Die Oberfläche wird daraus erzeugt.
  Min/Max gibt es im Web-Client nicht, sie werden von Hand gepflegt.
- **Bereich „Extensions“:** Host für die Steuerelemente der Pakete 3–7
  (`IFeaturePanel`). Ein Paket registriert sein Steuerelement mit
  einer Zeile in `FeaturePanels`. Die Schalter dort sind global und nicht Teil der Profile.
- **Software-Stopp „SOFTWARE STOP (IDLE)“** im Fenster, immer sichtbar, dazu im Panel und
  als mappbare Aktion: setzt die Achse sofort auf IDLE und hat Vorrang vor allen wartenden
  Befehlen. **Er ersetzt keinen Hardware-Not-Aus:** Er braucht eine funktionierende USB-
  Verbindung, und der Motor läuft danach frei aus, statt zu bremsen.
- **Language:** The interface is English, like the existing plugin. So is new or changed documentation, see `CLAUDE.md`.

## Sicherheitsregeln für alle Aktionen, die den Motor bewegen

- Bestätigungsdialog mit Checkliste (Hände weg, Lenkrad frei, Not-Aus in Reichweite).
  Der Dialog gehört **nicht** als modales `ShowDialog` auf den Thread des Fensters: Das sperrt
  alle Fenster dieses Threads und damit den Stop-Button (siehe `tests/plugin/WindowTests.cs`).
  Die Bestätigung läuft deshalb im Fenster selbst.
- Kalibrierungen laufen mit reduziertem Strom gemäß Quick-Start-Vorgaben des Web-Clients.
- Während einer Bewegung ist der Stop-Button hervorgehoben.
- Claude testet keine dieser Aktionen selbst an der Hardware.

## Teilschritte

Jeder Teilschritt ist ein eigener Branch (`pkg/1a-...` usw.) und PR. 1a ist die Basis,
1b–1e bauen darauf auf und sind untereinander unabhängig.

| Teilschritt | Inhalt | Test durch den Nutzer |
|---|---|---|
| **1a – Gerüst** | Konfigurationsfenster, Verbindungsdienst, Parameter-Service, Parameter-Katalog (Datenstruktur und erste Einträge), Software-Stopp, leerer Bereich „Extensions“, Umstellung des Profil-Service | Fenster öffnet, verbindet, liest `vbus_voltage` und Fehler, Software-Stopp setzt IDLE, Port freigeben/übernehmen mit dem Web-Client klappt, Profile funktionieren wie vorher |
| **1b – ODrive-Editor** | Tabs PSU/Bremswiderstand, Achse, Motor, Encoder, Controller, Status/Fehler (mit Klartext-Decoder), Konsole; Speichern (`ss`), Fehler löschen, Config löschen mit Rückfrage; **Export/Import im gleichen JSON-Format wie der Web-Client** (flach, `"pfad": "wert"`), damit vorhandene Backups nutzbar bleiben | Gleiche Werte wie im Web-Client; Export aus dem Plugin lässt sich im Web-Client importieren und umgekehrt |
| **1c – FFB-Einstellungen** | Tabs FFB Wheel, FFB Effects, FFB Filters inkl. 3-Band-EQ mit Kurve, Inputs/GPIO; Anbindung an das Profilsystem | Werte stimmen mit dem Web-Client überein; Profil anwenden/speichern wie bisher |
| **1d – Quick-Start und Motor-Setup** | Assistent mit den 13 Schritten des Web-Clients; **Kt-Wizard**; **KTY84-Preset**; **Haltebremse**; **Moment-Drehzahl-Rechner**; Empfehlungen zu Strom-/PLL-Bandbreite und Feedforward | Kompletter Durchlauf mit dem XDrive Mini; Kt-Wizard-Ergebnis plausibel gegen Datenblatt |
| **1e – DFU-Flasher** | Flashen aus dem Plugin, Quelle: lokale Datei oder neuestes Release dieses Forks; automatische Sicherung vor dem Flashen; geschützte Sektoren; Release-Workflow in der CI | Flashen einer PR-`.bin` klappt, FFB-Einstellungen und ODrive-Config bleiben erhalten, `sys.build?` zeigt den neuen Stand |

## Status of 1b

| Scope | Implemented | Checked on the board |
|---|---|---|
| Parameter catalog for PSU/brake, axis, motor, encoder, controller and status/errors | yes, checked against the firmware interface | no |
| Read-only lists with targeted refresh (tab or group), decoded errors, visible read failures, outdated values | yes, tested against the simulated board | no |
| Power supply data in the plugin settings | yes, tested without a board | does not touch the board |
| Editing the ten `config.*` fields of Power / Brake, to RAM only | yes, tested against the simulated board | no |
| `sys.capabilities?` in the firmware, and its use in the plugin | yes, on the PC: real parser and formatting, simulated board | no |
| Editing anything else, saving (`ss`) from the editor, clearing errors, erasing the config, state changes and calibrations | planned | – |
| Console, export/import | planned | – |

The lists only read: their logic only gets a read delegate, and `tests/plugin/EditorWindowTests.cs`
checks that on the wire. Without a request the window reads nothing but the eight values with
`"poll": true`. The only way to write is the editor described below.

## Firmware capabilities

A reply alone does not say whether it is complete: `0.015625` is what current firmware sends for that
float, and what older firmware sends for anything that rounds to it. So the firmware says it.

**The contract.** `sys.capabilities?` answers `[sys.capabilities?|v=1 ascii_full=1 float32_rt=1]`, with the
value `v=1 ascii_full=1 float32_rt=1`:

| Field | Meaning |
|---|---|
| `v` | Version of this schema. Another field or another meaning needs another version. |
| `ascii_full` | `1`: an `r <path>` reply holds the whole number, or an error. It is never cut. |
| `float32_rt` | `1`: a float in an `r <path>` reply parses back to the same float32 (`%.9g`, the fix of PR #6). |

- Exactly these three fields, each once, separated by one space. Anything else is no declaration: a missing
  or repeated field, a value other than `0` or `1`, an extra field, another `v`.
- Get only. `sys.capabilities=…` and `sys.capabilities!` answer `ERR` and change nothing. It has no state and
  touches no flash. It is a read-only command for any client, also with the software stop set.
- Firmware without the command answers `NOT_FOUND`.
- `sys.swver` and `sys.build` keep their meaning, and nothing is concluded from them.

`Odrive-Wheel/src/firmware_capabilities.c` is the handler. `tests/host/test_firmware_capabilities.cpp` runs it
in the real parser and checks the registration and the frame. `tests/host/test_ascii_read.cpp` holds both flags
against the real formatting, so a change there fails until the declaration follows.

**The check in the plugin** (`Core/OdriveFirmwareCapabilities.cs`):

- `OdriveCapabilityDetector` asks once per connection, when `OdriveSerialService.Connected` reports it. That event
  comes on a thread pool thread, so nothing waits on the serial worker. There is no polling.
- A declaration is bound to the `ConnectGeneration` it was asked on, and that is checked on the way out and on the
  way back: the request cannot reach a newer connection, and a late reply of an earlier connection is ignored. After a
  disconnect, a released port or a reconnect the old one counts for nothing, and the new connection is asked again.
- Timeout, `ERR`, `NOT_FOUND` and every unusable reply leave the plugin as careful as it was without a check.
  There is no trust from a Git hash, a branch, `sys.swver`, the length of a number or a switch. **Check firmware
  again** in the window asks once more on request, only after a check that found nothing.
- The read uses `SerialOperationContext.ForConnection`: it is bound to the connection only, as it has to work
  with the stop latch set, and a connection can start with a latch that nobody clears. A running stop still
  ends it and goes first. It then runs again when the stop has finished, at most `MaxAttempts` times in all. What the
  latch blocks stays blocked, and the commands of an apply stay bound to the stop state as well.

**What a declaration changes**, all from one object for one connection:

| | Without | With `ascii_full=1 float32_rt=1` |
|---|---|---|
| Display (`OdriveParameterFormat`, `OdriveParameterReader`) | A number of 9 or more characters is marked `may be cut off`, error bits in it are not decoded | The number as it is, and error bits decoded |
| Basis of a review and the fresh check (`TryParseExactReply`) | A reply older firmware could have produced is refused | Every float reply is the float32 on the board |
| Readback (`CompareReadback`) | Such a reply never verifies | The float32 values decide, what the reply looks like does not matter |

- The display uses `ascii_full`. The basis and the readback need both flags: a float may be neither cut nor rounded.
  A valid declaration without them rules out every float, however harmless a reply looks.
- Error bits above bit 31 have no names yet. They show as the whole number with `above 32 bits, not decoded`.
- The text of a value is worked out when it is shown, with what was declared on the connection it was read on. So a value
  read before the check ended is redrawn without another read. The same firmware answered both. A value of an earlier
  connection stays `outdated` and is never upgraded.
- A review keeps the declaration it was made with, and the fresh check and the readback of that apply use the same
  object. If the declaration changes while a review is waiting, the window drops the review and asks for a new one.
- The firmware is not trusted with anything else: all guards of an apply stay, see below.

**What this does not prove:** the tests run on the PC. `printf` is the PC's there, so `%.9g` in newlib-nano and the
`%f` parser behind `w` have not been seen on a board. The declaration says what the firmware is built to do.

## Power supply data and editing Power / Brake

**Power supply data** is hardware data in the global plugin settings, `PowerSupplyData`, and not part
of any profile. Entering, saving or loading it sends nothing to the board. It is DC supply current, not
motor phase current, and nothing takes a board value from it. A short-time current is not a continuous
limit, and no time-dependent current limit exists: the data is a basis for later roadmap features.
Usable continuous current = continuous output current × (1 − current headroom / 100).

**The editor** covers the ten `config.*` fields of the group Power / Brake, marked `"editable": true`
in the catalog. `sys.vbusdiv` stays read-only, it calibrates the voltage measurement, and so do the
communication parameters and all other tabs.

| Step | What happens |
|---|---|
| Typing, selecting | Nothing is sent. |
| Review | Sends nothing. All ten values must have been read on this connection and be exact. Shows old and new values. No modal dialog, it would block the stop button. |
| Apply to RAM | Reads all ten values and the bus voltage again, and stops if one differs from the review or is not exact. Then, before each write: stop, connection and both axes (`current_state` is IDLE, `motor.is_armed` is 0, `requested_state` is 0). |
| Write | One `w` line. The firmware has no positive answer, so this only means sent. |
| Read back | The reply is compared as a float32 or as a boolean, if the connection and the stop state still hold when it is here. |

The outcomes of a write are `verified by readback`, `sent, not verified`, `sent, readback differs`,
`refused by the firmware` and `not sent`. Anything but `verified by readback` ends the run and names what
already went out, because each next step rests on the value before it. There is no retry and no writing
back.

**Bound to the review:** every command of an apply carries the connection generation and the last stop of
the review (`SerialOperationContext`). The serial worker checks them right before the line, so a stop or a
new connection holds it back however late it comes. Requests without a context, such as profiles and the
panel, keep their existing rules.

**Without a declaration of the firmware** ([Firmware capabilities](#firmware-capabilities)), replies older
firmware could have produced (`OdriveConfigValue.MayBeOlderFloatFormat`) are in doubt, as they may be rounded or
cut. Such a reply:

- never counts as verified,
- is no basis for relations or the write order. The review and the fresh check refuse it alike, and reading
  the same text again does not help. The example behind this: `dc_max_negative_current` holds
  `-0.09999974`, older firmware replies `-0.100000`, and a `max_regen_current` of `0.0999999` would seem to
  fit,
- is accepted only for the measured bus voltage, which moves by far more than its last digit between two
  readings.

With a declaration none of this applies, and the current float32 values are used as they are.

**Relations checked**, all taken from what the firmware does (`ODrive::do_fast_checks` in `main.cpp`,
`update_brake_current` in `low_level.cpp`, the remarks in `odrive-interface.yaml`). No value is too high or
too low on its own:

- With the brake resistor enabled, `brake_resistance` must be above 0, the firmware stops otherwise.
- `dc_bus_undervoltage_trip_level` below `dc_bus_overvoltage_trip_level`, and the measured bus voltage
  between them, otherwise the firmware stops at once.
- With the ramp enabled: brake resistor enabled and above 0 ohm, `ramp_start` below `ramp_end`, otherwise
  the firmware disables the ramp. `ramp_start` above the measured bus voltage, because the ramp also runs
  with the motors disarmed.
- `dc_max_positive_current` not negative and `dc_max_negative_current` not positive, the firmware stops
  at idle otherwise.
- With the brake resistor enabled: `max_regen_current` not negative, and `dc_max_negative_current` greater
  in magnitude.

Each write is a state of its own, so the values go out in an order in which no step adds a problem.

**Limits:**

- The state checks run in the plugin, right before each write. They are no atomic safeguard: another
  command source can change the state in between.
- A stop requested in the instant between the worker's last check and the line cannot call that line back.
  The stop lines follow right behind it, and nothing else of the apply goes out.
- Without a declaration the review refuses while one of the ten values has a reply in doubt, which on older
  firmware is nearly every float.
- Only finite values can be entered, so `dc_max_positive_current` cannot go back to `inf` here. A reboot
  restores the saved value, as nothing is saved.
- `enable_brake_resistor` takes effect only after a reboot, says the firmware interface. The review points
  this out.
- A value written to RAM stays there without any further sign until the board reboots. The existing Save to
  flash in the panel would persist it, the editor never does.
- None of this has run on a board, in particular the `%f` parser of newlib-nano behind `w`.

Befunde aus dem Abgleich, wichtig für die weiteren Teile von 1b. Maßgeblich ist das Firmware-Interface,
nicht der Web-Client:

- **Ältere Firmware kürzt Antworten auf 9 Zeichen.** `cmd_read_property` schrieb in einen Puffer von 10 Byte,
  `to_string` kürzte ohne Meldung. Aus dem Fehlercode `2147483648` wurde so `214748364`. The firmware is fixed
  (`docs/ROADMAP.md`, „Vollständige ASCII-Leseantworten“) and says so through `sys.capabilities?`. Without that
  declaration the display still marks every number of 9 or more characters. **For the export this means:** values read
  through `r` may go into a backup only with a declaration on that connection, or marked as uncertain.
- **Falsche Namen im Web-Client:** Er nennt die Stream-Protokolle `1 FIBRE`, `2 ASCII`, `3 ASCII_AND_STDOUT`,
  `4 STDOUT`. Die Firmware zählt `0 Fibre`, `1 Ascii`, `2 Stdout`, `3 AsciiAndStdout`. Der Katalog folgt der Firmware.
- **Schreibbarkeit weicht ab:** `phase_resistance`, `phase_inductance`, `direction`, `phase_offset` und
  `phase_offset_float` kann die Firmware schreiben, der Web-Client sperrt sie als Kalibrierergebnisse. Der
  Katalog führt sie als `writable` mit `"calibrated": true`. Umgekehrt sind `anticogging.cogging_ratio` und
  `anticogging.index` in der Firmware nur lesbar, obwohl der Web-Client sie zum Schreiben anbietet.
- **Einheiten** stammen aus dem Interface. Nur wo es keine nennt, gilt die ausdrückliche Angabe des Web-Clients.
  Parameter ohne Text in beiden Quellen tragen den Hinweis „No description in the firmware interface.“

## Details zu 1d

**Kt-Wizard** – Eingabe wahlweise als:
- Kt in Nm/A_rms → ODrive-Wert = Kt_rms / √2
- Kt in Nm/A_peak → unverändert
- Ke in V_rms (Leiter-Leiter) pro 1000 U/min → ODrive-Kt ≈ 0,0117 × Ke
- Kv in U/min/V → ODrive-Kt = 8,27 / Kv

Der Wizard zeigt das Ergebnis, die Umrechnung und eine Plausibilitätsprüfung
(Nennmoment / Nennstrom aus dem Datenblatt).

**KTY84-Preset:** Eingabe des Vorwiderstands des Spannungsteilers am Thermistor-GPIO.
Das Plugin berechnet aus der KTY84-130-Kennlinie (Datenblatt-Tabelle) die
Polynomkoeffizienten für den ODrive-Motor-Thermistor und setzt Warn-/Abschalt-
temperaturen. Hinweis im UI: PTC-Kaltleiter (Schwellwertfühler) lassen sich so nicht
sinnvoll abbilden.

**Haltebremse:** ODrive 0.5.6 kennt den GPIO-Modus „Mechanical Brake“ (Bremse
schließt bei Idle/Disarm). Das Plugin stellt GPIO und Polarität ein und zeigt einen
Hinweis zur Hardware (3,3-V-GPIO → MOSFET oder Relais für die 24-V-Spule,
Freilaufdiode). Die Bremse ist eine Haltebremse und nicht zum dynamischen Bremsen gedacht.

**Moment-Drehzahl-Rechner:** Eingaben Vbus, Modulationsgrenze (aktuell 0,8), Kt,
Phasenwiderstand, Induktivität, Polpaare, Stromgrenze. Ausgabe: Kurve verfügbares
Moment über Lenkgeschwindigkeit (°/s und U/min) mit markiertem Knickpunkt.

**Bandbreiten-Empfehlung:** Anzeige von `current_control_bandwidth` und
`encoder.config.bandwidth` in rad/s **und** Hz; Hinweis, wenn die Strombandbreite
deutlich unter ca. 100 Hz liegt; Empfehlung, R/L-Feedforward zu aktivieren.

## Details zu 1e

**Geschützte Bereiche** (weder löschen noch beschreiben):

| Sektoren | Adressbereich | Inhalt |
|---|---|---|
| S1+S2 | `0x08004000–0x0800BFFF` | FFB-EEPROM |
| S9 | `0x080A0000–0x080BFFFF` | reserviert für Paket 6 |
| S10+S11 | `0x080C0000–0x080FFFFF` | ODrive-NVM |

**Reguläre `.bin` überdecken S1+S2:** Eine `.bin` ist ein zusammenhängendes Abbild ab
`0x08000000`. Die Vektoren liegen in S0, der Code beginnt in S3, und der Build füllt die
Lücke dazwischen mit `0xFF` (`objcopy --gap-fill=0xFF`). Jede reguläre Firmware-`.bin`
reicht deshalb rechnerisch über S1+S2. Ihre Gesamtausdehnung ist **kein** Ablehnungsgrund.
Entscheidend ist der Inhalt.

**Prüfung vor dem ersten Lösch- oder Schreibzugriff:** Der Flasher prüft das ganze Abbild,
bevor er das Board anfasst. Schlägt eine Prüfung fehl, bleibt das Board unverändert.

| Fall | Reaktion |
|---|---|
| Alle Bytes des Abbilds in einem geschützten Bereich sind `0xFF` | Padding: Bereich beim Löschen und Schreiben überspringen |
| Mindestens ein Byte in einem geschützten Bereich ist nicht `0xFF` | Echte Nutzdaten: abbrechen, Fehlermeldung nennt Sektor und Adresse des ersten solchen Bytes |
| Abbild reicht über das Flash-Ende `0x08100000` hinaus | Abbrechen |
| Abbild ist leer oder S0 enthält keine Nutzdaten | Abbrechen |

**Lösch- und Schreibplan:**

- Der Flasher zerlegt das Abbild in Segmente außerhalb der geschützten Bereiche. Bei
  einer regulären `.bin` sind das S0 und der Abschnitt ab S3 bis zum letzten Sektor mit
  Nutzdaten. Rein aus `0xFF` bestehendes Ende des Abbilds wird nicht geschrieben.
- Gelöscht werden nur Sektoren, die ein Segment berührt. Ein Segment endet spätestens an
  der Grenze zum nächsten geschützten Bereich, da Sektoren nur als Ganzes löschbar sind.
- Die Segmente werden in getrennten Schreibvorgängen geflasht. Das gesamte Abbild am
  Stück zu schreiben (`dfu-util -s 0x08000000:leave -D ...`) ist für den Flasher
  ausgeschlossen, weil es dabei auch S1+S2 löscht.
- Das Springen in die Firmware (`:leave`) erfolgt erst nach dem letzten Segment.

**Pflicht-Testfälle (ohne Board prüfbar, gegen die Planungslogik):**

- Reguläre CI-`.bin`: wird angenommen, Plan enthält S0 und ab S3, nichts aus den
  geschützten Bereichen.
- Reguläre `.bin` mit einem einzelnen Byte ungleich `0xFF` in S1, S2, S9, S10 oder S11:
  wird abgelehnt, Plan bleibt leer.
- `.bin` mit reinem `0xFF`-Padding über S9 hinaus und ohne Nutzdaten dort: wird
  angenommen, S9–S11 bleiben unberührt.
- Abbild über `0x08100000` hinaus: wird abgelehnt.

**Weitere Punkte:**

- Bootloader-Sprung über den vorhandenen ODrive-ASCII-Befehl wie im Web-Client.
- Umsetzung bevorzugt mit mitgeliefertem `dfu-util` (keine eigene USB-Implementierung).
  Lizenzhinweis (GPL-2.0) und Quellenlink beilegen.
- Einmalig weiterhin nötig: WinUSB-Treiber für `0483:df11` (Zadig), wie beim Web-Client.
- CI: Workflow, der bei einem Tag `v*` ein GitHub-Release mit `.bin` und `.hex` erstellt.
