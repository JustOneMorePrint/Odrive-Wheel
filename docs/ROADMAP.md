# Roadmap

**Product goal:** a DIY direct drive for hoverboard and industrial servo motors, operated entirely through the
English SimHub plugin. What counts is reliable function, a reproducible setup, low measured latency and a diagnosis
that can be understood.

| Topic | Where |
|---|---|
| Standing rules, and how a task is worked on | `CLAUDE.md` |
| Connection, stop, capabilities, abort, partial changes, value states, minimum capabilities | `docs/FOUNDATIONS.md` |
| Target boards, power data, temperature sensors, calibration | `docs/HARDWARE.md` |
| Real rates, metrics, goals, the experiment with higher rates | `docs/MEASUREMENT.md` |
| Test levels, and the open hardware tests | `docs/TESTING.md` |
| Specification of package 1, including the capability contract | `docs/packages/PKG1.md` |

The basis of this plan was an analysis of `a2beb5c` (eagabriel/Odrive-Wheel, V0.32 / FW v1.1.x), compared with OpenFFBoard
upstream, Simucube, Fanatec, Moza and the literature. Benefits named there are now goals to be measured.

## Decisions

- The order follows what a user does: setup, backup and restore, FFB operation, analysis, optimization. Foundations and a
  first backup come before anything that changes the setup.
- Package numbers stay as names. A package is no longer independent of the others by rule: it builds on the foundations, and
  its real dependencies are listed below.
- Old/new switches exist where a comparison or a tuning variant makes sense. Safety and robustness corrections always apply.
  Switches are global and no part of the profiles, and profiles keep holding the existing FFB settings.
- Final protection decisions belong in the firmware.
- Compatibility between old and new firmware or software is no goal. Write functions state what they need, and nothing guesses
  what an older firmware can do.
- Configuration happens only in the SimHub plugin. The web client stays as a readable reference and is not developed further.
  GitHub Pages is no part of this project.
- Calibrations for old and new variants can both be kept (slots A/B of package 6), each traceable to hardware and version.
- Telemetry effects through SimHub stay parked, see the backlog.
- Hardware tests are deferred at the user's request, and stay visibly open in `docs/TESTING.md`.

## Order of development

| Stage | The user wants to | Contains | State |
|---|---|---|---|
| 0 Foundations | Connect, stop, trust what is shown | Step 0, 1a, S1, complete ASCII replies, capability check, Power / Brake to RAM | In `main`, not checked on the board |
| 1 First backup | Have a copy before changing anything | Read-only snapshot, comparison with a file, compact diagnostic report | **Next** |
| 2 Setup | Bring a board and a motor into operation | Board identity and flasher, editor with clear value states, save, power data, wizard, calibration, sensors, protection | Planned |
| 3 Backup and restore | Go back reliably | Restore with a review, calibration records, firmware rollback | Planned |
| 4 FFB operation | Set up and use the wheel | FFB tabs, inputs, torque processing, signal chain | Planned |
| 5 Analysis | See what the system does | Metrics, logger, plots, FFT, overlay, performance test, sweep | Planned |
| 6 Optimization | Improve what was measured | Higher rate experiment, current controller, encoder, anticogging, observer | Planned, each step decided on its measurements |

## All planned functions

Nothing was dropped. "Setting" means a value the function needs from the user's hardware, with no old/new behind it.

### Stage 0: Foundations

| Function | Package | State | Switch |
|---|---|---|---|
| Process setup, `sys.build?`, host tests | 0 | Done | None |
| Configuration window, connection service, parameter service, catalog | 1a | Done, hardware tests open | None |
| Software stop, Extensions area | 1a | Done, hardware tests open | None |
| Flash guard | S1 | Done, hardware tests open | None |
| Complete ASCII replies, `sys.capabilities?` | 1b | Done, hardware tests open | None |
| Read-only tabs, power supply data, Power / Brake to RAM | 1b | Done, hardware tests open | None |

### Stage 1: First backup

No write of any kind to the board.

| Function | Package | Switch |
|---|---|---|
| Export of all readable ODrive and FFB values, in the web client's JSON format, with build, board and capability state | 1b | None |
| Comparison of the board against a file, and of two files | 1b, new | None |
| Compact diagnostic report: build, board, capabilities, errors in clear text, temperatures, what differs from the last backup | 2, brought forward | None |

### Stage 2: Setup

| Function | Package | Depends on | Switch |
|---|---|---|---|
| Board selection and identity in the firmware | 1e, new | Foundations | None |
| DFU flasher with this fork as source, backup first, protected sectors, release workflow | 1e | Board identity, first backup, S1 | None |
| Builds for XDrive S and XDrive, as the comparison in `docs/HARDWARE.md` allows | new | Board identity | None |
| Editing all other parameters, value states, save, clear errors, erase with confirmation, state changes | 1b | First backup, shared operation | None |
| Console | 1b | Connection | None |
| Power input range 24–48 V, brake resistor (0, 20] Ω or disabled | new | Editor | Setting |
| Setup wizard, in the order of `docs/HARDWARE.md` | 1d | Editor, state changes, board identity | None |
| Kt wizard, KTY84 preset, NTC calculator, holding brake on `MECH_BRAKE` | 1d | Wizard | Setting |
| Torque over speed calculator, bandwidth and feedforward advice | 1d | Motor data | None |
| Guided checks: force direction, regeneration | new, from the FFBeast comparison | Wizard | None |
| Resolver connection (hardware only: AD2S1210 on ABZ, or magnet with MT6835) | 1 | – | – |
| Protection: endstop priority, soft start | 3 | – | None |
| Protection: speed limit, out of bounds | 3 | – | Setting |
| Protection: watchdog, HID timeout | 3 | – | None |
| Protection: E-stop input, status output | 3 | Wiring | Setting |
| Protection: supply limiter | 3 | Power supply data | Setting |
| Protection: I²t model | 3 | Thermal data of the motor, off without it | Setting |
| Mutex between USB callback and FFB thread | 4, brought forward | – | None |
| Controls and status for these, `hap.*` | 3 | – | – |

### Stage 3: Backup and restore

| Function | Package | Depends on | Switch |
|---|---|---|---|
| Import and restore in the web client's format, with a review and the operation contract | 1b | Editor, value states | None |
| Calibration records, traceable to hardware and version | new | Wizard | None |
| Way back to an earlier firmware, checked (T4) | 1e | Flasher, restore | None |

### Stage 4: FFB operation

| Function | Package | Depends on | Switch |
|---|---|---|---|
| Tabs FFB Wheel, Effects, Filters with 3-band EQ and curve, Inputs/GPIO, link to profiles | 1c | Shared operation | None |
| Filter updates without resetting their state | 4 | – | None, a correction |
| Expo on force, the old one on position stays | 3 | – | Yes |
| New effect filters for damper, friction, inertia | 3 | M4 on the host | Yes |
| Center damping, slew in Nm/ms, `pos_estimate` instead of `shadow_count` | 3 | – | Yes |
| Adaptive reconstruction filter, real CF rate instead of a fixed 1000 | 4 | M4 | Yes |
| Extended EQ, notch | 4 | – | Off until set |
| Static force reduction | 4 | – | Yes |
| Controls, EQ curve, `sig.*` | 4 | – | – |

### Stage 5: Analysis

| Function | Package | Depends on | Switch |
|---|---|---|---|
| Measuring points and counters of `docs/MEASUREMENT.md` | new | – | None, no effect on behaviour |
| Logger, error display | 2 | Diagnostic report | None |
| Live plots, FFT | 2 | HID stream | None |
| Overlay as a SimHub dashboard | 2 | Properties | None |
| Performance test, coastdown, frequency sweep, J, b and breakaway. Test forces as HID PID reports, without a running game | 2 | Protection, confirmation in the window | None |
| Optional: Bode measurement over the filter curves | 2 → 4 | Sweep, new filters | None |

### Stage 6: Optimization

Each step starts with a baseline and ends with a comparison, `docs/MEASUREMENT.md`. A step that shows no benefit is dropped or stays
a variant.

| Function | Package | Depends on | Switch |
|---|---|---|---|
| Execution rate 1 kHz or 8 kHz, experimental | 3 | M5, M6, protection | Yes |
| Deadband: old, setpoint based, soft | 5 | M3 | Yes |
| Maximum modulation adjustable (fixed at 0.8 today) | 5 | M3, torque over speed | Setting |
| Tuning slot B: current bandwidth, encoder PLL, feedforward | 5 | Calibration records | Yes |
| Controls and status, `cur.*` | 5 | – | – |
| Harmonic compensation | 6 | Ripple measurement | Yes |
| Bidirectional anticogging in slot B (3600 floats) | 6 | Slot store, ripple measurement | Yes |
| Phase offset in slot B, switching only while disarmed | 6 | Slot store | Yes |
| Slot store in sector 9, linker change | 6 | Calibration records | None |
| Calibration assistant, `enc.*` | 6 | Wizard | None |
| State observer in the control loop, with Iq·Kt as input | 7 | Measured J and b or entered ones, M4, M5 | Yes |
| J and b as parameters, `obs.*` | 7 | – | Setting |
| Optional: measured J and b into the observer | 2 → 7 | Performance test | None |
| Optional: torque processing uses the observer's acceleration | 7 → 3 | Observer | Yes |
| Hands-off detection and reaction at the torque setpoint. A comfort function, never a protection | 7 | Observer | Yes |
| Controls and status | 7 | – | – |

## Real dependencies

Only what cannot work otherwise. Everything also builds on `docs/FOUNDATIONS.md`.

- **Anything that changes the setup** needs the first backup.
- **The wizard** needs the editor, state changes and the board identity. Calibration that moves the motor also needs a backup and a
  confirmation.
- **The flasher** needs the board identity, and S1 for what it must not touch.
- **Restore** needs the editor and value states, and refuses a backup of another board.
- **The supply limiter** needs power supply data, **I²t** needs thermal data of the motor. Without the data they stay off.
- **Test forces** need the protection step.
- **Everything in stage 6** needs the metrics and a baseline from stage 5.
- **Hands-off** needs the observer, **slots** need the calibration records.

Open for later: FFB tabs (stage 4) can go ahead of a finished wizard if the user wants to use a board that was set up with the web
client. Analysis can also start before FFB operation is complete, nothing in it needs stage 4.

## Address blocks and command prefixes

| Package | EEPROM block (virtual addresses) | Command prefix | Other storage |
|---|---|---|---|
| 1, 2 | – (plugin only) | – | Plugin settings in SimHub |
| 3 | `0x0600–0x063F` | `hap.*` | – |
| 4 | `0x0640–0x067F` | `sig.*` | – |
| 5 | `0x0680–0x06BF` | `cur.*` | – |
| 6 | `0x06C0–0x06FF` | `enc.*` | Flash sector 9 (`0x080A0000`, 128 KB) |
| 7 | `0x0700–0x073F` | `obs.*` | – |
| S1 | – | `safe.*` | – |
| Reserve | `0x0740–0x07FF` | – | – |

In use, do not use: `0x0000–0x0004`, `0x00FF`, `0x0100–0x0106`, `0x0200–0x020C`, `0x0250–0x0274`, `0x0300–0x0304`, `0x04F0–0x04F2`.

Where a package's firmware part changes: `haptics_core.*` (3), `Filters.*`, `EqCascade.*`, `EffectsCalculator.*`, `HidFFB.cpp` (4), `foc.*`,
`motor.*` as runtime values (5), `encoder.*`, `controller.cpp`, `slot_store.*`, linker script (6), `observer.*` (7). Each with rule 3 of `CLAUDE.md`.

## S1 – Flash-Schutz in der Firmware

A protection without a switch, part of stage 0.

Beim Schreiben oder Löschen steht die CPU, weil sie ihren Code aus demselben Flash holt, und mit ihr
der Regel-Interrupt. Die PWM bleibt dann auf dem letzten Wert, und niemand regelt den Strom. Deshalb:

- **Keine Flash-Operation, solange ein Motor aktiv ist.** Aktiv heißt auf irgendeiner Achse: Motor
  scharf, angeforderter Zustandswechsel noch nicht übernommen oder Achse nicht in IDLE.
- **Kein Armen während einer Flash-Operation.** `Motor::arm` verweigert. Der Schutz führt dafür keinen
  eigenen Fehlercode ein, und der Start schlägt ohne Änderung an Kalibrierwerten fehl (siehe „Verweigertes Armen“).

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

`Motor::arm` gibt `false` zurück und merkt sich das in `arm_refused_`, einer Laufzeitvariable außerhalb der
Config. Der Schutz führt keinen eigenen Fehlercode ein. Jeder der fünf Aufrufer behandelt das ausdrücklich: Ein
verweigerter Start schlägt fehl und lässt Kalibrierwerte und Kennzeichen unverändert.

| Weg | Bleibt bei verweigertem Start erhalten | Umsetzung |
|---|---|---|
| Motorkalibrierung (`measure_phase_resistance`, `measure_phase_inductance`) | `phase_resistance`, `phase_inductance`, `is_calibrated_`, Reglerverstärkungen | Rückkehr direkt nach dem Armversuch |
| Index-Suche | `use_index`, `index_found_` | Änderung läuft erst nach dem Armen |
| Hall-Polarität | `hall_polarity`, `hall_polarity_calibrated`, `states_seen_count_` | Änderung läuft erst nach dem Armen |
| Hall-Phase | `hall_edge_phcnt`, `hall_phase_calib_seen_count_`, `direction` | Änderung läuft erst nach dem Armen, der Fehlerpfad setzt die Standardwerte nicht mehr |
| Richtungssuche, Lock-in | `direction` | War schon so |
| Encoder-Offset | `shadow_count_`, `phase_offset`, `phase_offset_float`, `direction`, `is_ready_` | Der Abgleich von `shadow_count_` läuft erst nach dem Armen |
| Closed Loop | Kein neuer Fehler, Achse geht auf IDLE | `start_closed_loop_control` meldet `false` |
| Homing, erste Phase | `is_homed`, Endstopp-Fehler | Wird wiederhergestellt, die Achse hat sich nicht bewegt |
| Homing, zweite Phase | Die Position wird nicht genullt | Abbruch. `is_homed` bleibt `false`, die Achse hat sich bewegt |

Für die Werte, die erst nach dem Armen geändert werden, bekam `Axis::run_lockin_spin` den Parameter `on_armed`.
Ein Vorabcheck des Schutzes würde das Zeitfenster bis zum Armen nicht schließen, hier entscheidet das Armen selbst.

**Unverändert bleibt:**

- Im Erfolgsweg ändert sich nur die Reihenfolge: Die genannten Werte werden direkt nach statt kurz vor dem Armen gesetzt.
- Bereits gestartete, später abgebrochene Kalibrierungen verhalten sich wie bisher. Eine abgebrochene
  Widerstandsmessung schreibt zum Beispiel weiter ihren Zwischenwert, eine abgebrochene Hall-Phase setzt die Standardwerte.
- `Motor::arm` gibt bei entschärftem Bremswiderstand weiter `true` zurück, ohne zu armen. Das ist kein verweigerter
  Start durch den Schutz und unverändertes Verhalten: Die Messungen schreiben dann weiter ohne Messung.

Ein verweigerter Start setzt voraus, dass die Anforderung eintrifft, während eine Flash-Operation läuft. Über USB
laufen Befehle nacheinander im selben Thread, über CAN oder UART ist es möglich. Die USB-Reihenfolge ist also
keine firmwareweite Absicherung, deshalb die Behandlung in den Aufrufern. Nach dem verweigerten Start geht die Achse
auf IDLE, dort schließt die Bremse wieder, falls sie im Zeitfenster schon geöffnet wurde.

### Nachweisstand

**Automatisiert, ohne Board:** `tests/host/test_arm_refusal.cpp` führt die echten Produktionsfunktionen auf dem PC
aus, mit Stubs für Hardware und umgebende Klassen, und prüft verweigerte Starts, den normalen Erfolgsweg und
abgebrochene Abläufe. `test_flash_guard.cpp` prüft die Entscheidung, `test_flash_paths.sh` die Flash-Aufrufe im
Quelltext. Das Motorverhalten ist dabei simuliert, die Firmware läuft nicht auf dem Zielprozessor.

**Am Board noch offen:** T2 und T3 aus [PR #3](https://github.com/JustOneMorePrint/Odrive-Wheel/pull/3), auch die
Kalibrierungen mit der geänderten Reihenfolge.

Bei aktivem Motor werden am angetriebenen Lenkrad zum Testen keine Flash-Befehle gesendet, nur Statusabfragen. Bei
versagender Sperre wären sie echte Flash-Zugriffe, und auch ein EEPROM-Seitenwechsel oder `EE_Init` können löschen.
Softwaregrenzen wie `axis.maxtorque` oder `current_lim` sind dafür keine verlässliche Absicherung. Schreibtests laufen
nur bei nachweislich inaktiven Achsen.

Statusabfragen weisen die Wirkung der Sperre nicht nach. Auch nach T2 und T3 bleibt am Board ungeprüft, ob die gebaute
Firmware bei aktivem Motor wirklich ablehnt und ob `Motor::arm` während einer Flash-Operation verweigert. Ein möglicher
zusätzlicher Versuch ist im PR beschrieben und nicht ausgeführt.

## Vollständige ASCII-Leseantworten

Eng begrenzte Korrektur in `fibre-cpp/protocol.hpp` und `communication/ascii_protocol.cpp`, ohne Schalter. Sie betrifft
nur die Antwort auf `r <pfad>`. Eine Zahl, die zu lang für den Puffer ist, war vorher eine erfolgreiche, gekürzte Antwort.

| Wert | Vorher | Jetzt |
|---|---|---|
| Ganzzahl, 64 Bit, Bool | höchstens 9 Zeichen, längere gekürzt: `2147483648` wurde zu `214748364` | vollständig, bis `-9223372036854775808` |
| `float32` | `%f` mit höchstens 9 Zeichen: `25.000000`, `1.17549435e-38` wurde zu `0.000000` | `%.9g`, rücklesbar: `25`, `0.100000001`, `1.17549435e-38` |
| Null | `0.000000`, `-0.000000` | `0`, `-0` |
| Unendlich, NaN | `inf`, `-inf`, `nan` | `inf`, `-inf`, `nan` |

Einheiten und Bedeutung der Werte bleiben gleich. Wer eine Antwort auswertet, muss auch die wissenschaftliche
Schreibweise verstehen. Fehlerantworten sind unverändert. Das OpenFFBoard-Protokoll (`?`, `=`, `!`) ist nicht betroffen.

Die Formatierer melden Erfolg nur noch, wenn der ganze Wert passt, sonst steht im Puffer eine leere Zeichenkette.
`TO_STRING_BUFFER_SIZE` ist auf den längsten Wert ausgelegt. Die 64-Bit-Umwandlung bleibt von Hand geschrieben, weil
newlib-nano weder `%llu` noch `%lld` kennt.

The firmware declares this through `sys.capabilities?`, and the plugin only stops doubting a reply with that declaration
on the same connection. Without it, a number of 9 or more characters stays marked as possibly cut. The contract is in
`docs/packages/PKG1.md`, „Firmware capabilities“. Export/import follows later.

**Nachweisstand:** `tests/host/test_ascii_read.cpp` führt die echten Formatierer, den echten Antwortweg und den echten
Sende-Puffer auf dem PC aus. Die Arbeit von `printf` macht dort die C-Bibliothek des PCs. Am Board ungeprüft ist deshalb,
ob `%.9g` in newlib-nano dieselben Texte liefert und ob die Werte dort wirklich rücklesbar sind.

## Backlog

- **Telemetry effects through SimHub** (parked): vendor OUT report, ABS and gear shift effects, speed dependent damping. The free SimHub
  version gives telemetry at about 10 Hz, a license 60 Hz. Direct game readers in the plugin would be faster.
- **Left over:** `fx.master` is stored at `0x04F0`, but the address is missing from `VirtAddVarTab`, so it is lost at a page transfer. A small PR
  of its own, after asking.
- **Left over:** `docs/TORQUE_CONTROL.md` gives current bandwidths in Hz, the parameter is rad/s.
- **Left over:** `sys.temp?` and `sys.motortemp?` answer `0.0` for NaN, which looks like a reading.
