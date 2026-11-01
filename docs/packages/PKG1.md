# Paket 1 – Plugin-Basis (Web-Client-Ersatz, Teil 1)

Ziel: Das SimHub-Plugin übernimmt alle Funktionen des Web-Clients außer den
Analyse-Werkzeugen (Performance-Test, Sweep, Live-Plots, FFT, Overlay, Force-Test),
die in Paket 2 folgen. Keine Firmware-Änderungen in diesem Paket.

Der Web-Client bleibt während Paket 1 und 2 parallel nutzbar, damit du jederzeit
vergleichen kannst. Danach wird er nicht mehr verwendet.

## Architektur

- **Eigenes Konfigurationsfenster** (WPF), geöffnet über einen Button im bestehenden
  SimHub-Panel. Das Panel bleibt für Status, Profile und Aktionen zuständig.
- **Verbindungsdienst:** ein Hintergrund-Thread hält den COM-Port, arbeitet eine
  Befehlswarteschlange ab, erkennt Trennen/Wiederverbinden und meldet Fehler.
  Button „Port freigeben“, damit Web-Client oder `dfu-util` übernehmen können.
  Der bestehende Profil-Service wird auf diesen Dienst umgestellt.
- **Parameter-Service:** ODrive-ASCII (`r`/`w`, `ss`, Fehler löschen, Zustand setzen)
  und OpenFFBoard-Befehle (`?`, `=`, `!`). Ein **Parameter-Katalog** als Datendatei
  (Pfad, Typ, Einheit, Min/Max, Gruppe/Tab, Beschreibung) wird aus den Tab-Definitionen
  und Tooltips des Web-Clients übernommen. Die Oberfläche wird daraus erzeugt.
- **Bereich „Erweiterungen“:** Host für die Steuerelemente der Pakete 3–7
  (Interface, z. B. `IFeaturePanel`). Ein Paket registriert sein Steuerelement mit
  einer Zeile. Die Schalter dort sind global und nicht Teil der Profile.
- **Not-Aus-Button** im Fenster, immer sichtbar: setzt die Achse sofort auf IDLE.
- **Oberflächensprache:** Englisch wie das bestehende Plugin; Doku auf Deutsch.

## Sicherheitsregeln für alle Aktionen, die den Motor bewegen

- Bestätigungsdialog mit Checkliste (Hände weg, Lenkrad frei, Not-Aus in Reichweite).
- Kalibrierungen laufen mit reduziertem Strom gemäß Quick-Start-Vorgaben des Web-Clients.
- Während einer Bewegung ist der Not-Aus-Button hervorgehoben.
- Claude testet keine dieser Aktionen selbst an der Hardware.

## Teilschritte

Jeder Teilschritt ist ein eigener Branch (`pkg/1a-...` usw.) und PR. 1a ist die Basis,
1b–1e bauen darauf auf und sind untereinander unabhängig.

| Teilschritt | Inhalt | Test durch den Nutzer |
|---|---|---|
| **1a – Gerüst** | Konfigurationsfenster, Verbindungsdienst, Parameter-Service, Parameter-Katalog (Datenstruktur und erste Einträge), Not-Aus-Button, leerer Bereich „Erweiterungen“, Umstellung des Profil-Service | Fenster öffnet, verbindet, liest `vbus_voltage` und Fehler, Not-Aus setzt IDLE, Port freigeben/übernehmen mit dem Web-Client klappt, Profile funktionieren wie vorher |
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

- Geschützt (weder löschen noch beschreiben): S1+S2 (FFB-EEPROM), S9 (reserviert
  für Paket 6), S10+S11 (ODrive-NVM). Reicht eine `.bin` in einen geschützten Sektor,
  bricht der Flasher mit Fehlermeldung ab.
- Bootloader-Sprung über den vorhandenen ODrive-ASCII-Befehl wie im Web-Client.
- Umsetzung bevorzugt mit mitgeliefertem `dfu-util` und getrennten Schreibvorgängen
  für S0 und ab S3 (keine eigene USB-Implementierung). Lizenzhinweis (GPL-2.0) und
  Quellenlink beilegen.
- Einmalig weiterhin nötig: WinUSB-Treiber für `0483:df11` (Zadig), wie beim Web-Client.
- CI: Workflow, der bei einem Tag `v*` ein GitHub-Release mit `.bin` und `.hex` erstellt.
