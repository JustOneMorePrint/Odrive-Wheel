# Paket 1 – Plugin-Basis (Web-Client-Ersatz, Teil 1)

Ziel: Das SimHub-Plugin übernimmt alle Funktionen des Web-Clients außer den
Analyse-Werkzeugen (Performance-Test, Sweep, Live-Plots, FFT, Overlay, Force-Test),
die in Paket 2 folgen. Keine Firmware-Änderungen in diesem Paket.

Die Konfiguration erfolgt ausschließlich über das SimHub-Plugin. Der Web-Client bleibt nur
als lesbare Referenz im Repository und wird nicht weiterentwickelt, GitHub Pages gehört
nicht zum Projektbetrieb. Wo die Testspalte unten den Web-Client nennt, ist die lokale Datei
`Odrive-Wheel/tools/odrive-wheel.html` gemeint.

**Stand:** 1a ist in `main`, die Hardwaretests dazu sind offen. 1b–1e sind geplant und nicht
umgesetzt, die Funktionen fehlen im Plugin also noch.

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
- **Oberflächensprache:** Englisch wie das bestehende Plugin; Doku auf Deutsch.

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
