# CLAUDE.md – Arbeitsregeln für dieses Repository

Diese Datei liest Claude Code zu Beginn jeder Sitzung. Sie gilt für alle Arbeiten
in diesem Fork. Details zu den geplanten Paketen stehen in `docs/ROADMAP.md`,
das Testvorgehen in `docs/TESTING.md`.

## Kontext

- Fork von `eagabriel/Odrive-Wheel`: ODrive-Firmware 0.5.6 (Motorregelung) plus
  OpenFFBoard-FFB-Stack auf STM32F405 (MKS XDrive Mini, ODESC V4.2, ODrive-v3.6-Klone).
- Zielhardware des Nutzers: DIY-Direct-Drive-Basis (bis 25 Nm) mit KUKA-Servomotor
  an 48 V, Bremswiderstand, ESP32 schaltet die Versorgung per Relais.
- Wir erweitern Firmware und SimHub-Plugin in **sieben voneinander unabhängigen
  Paketen** (siehe `docs/ROADMAP.md`).
- Der Web-Client `Odrive-Wheel/tools/odrive-wheel.html` wird durch das SimHub-Plugin
  ersetzt. Er bleibt im Repo, wird aber **nicht mehr geändert**. GitHub Pages gehört nicht
  zum Projektbetrieb.
- Kompatibilität zwischen alter und neuer Firmware bzw. Software ist kein Ziel.
- Testhardware des Nutzers: **MKS XDrive Mini** (VBUS-Teiler 19, Default der Firmware).
- Die Alt/Neu-Schalter neuer Features gelten **global**. Sie sind nicht Teil der
  Plugin-Profile und werden beim Anwenden eines Profils nicht verändert.

## Repo-Karte

| Bereich | Pfad | Wichtig |
|---|---|---|
| FFB-Schicht | `Odrive-Wheel/src`, `Odrive-Wheel/inc` | `ffb_task.cpp` (1-kHz-Loop, `ODriveLocalAxis`), `EffectsCalculator.*`, `HidFFB.cpp`, `EqCascade.*`, `Filters.*`, `cmd_table.cpp` (Serial-Befehle), `eeprom_addresses.*` (Flash-Variablen), `gpio_inputs.*` |
| Brücke zur ODrive | `Odrive-Wheel/src/odrive_bridge.cpp` | Einzige Datei außerhalb der ODrive-Firmware, die `odrive_main.h` einbindet (Namenskollision `Axis`) |
| Motorregelung | `ODrive-fw-v0.5.6/ODrive-fw-v0.5.6/Firmware/MotorControl` | `foc.cpp` (Stromregler, 8 kHz), `motor.*`, `controller.*`, `encoder.*`, `low_level.cpp`, `main.cpp` |
| Flash-Layout | `Odrive-Wheel/linker/STM32F405RGTx_FLASH.ld` | S1+S2 FFB-EEPROM, S3–S9 Code, S10+S11 ODrive-NVM |
| SimHub-Plugin | `Odrive-Wheel/tools/simhub-plugin` | .NET Framework 4.8, WPF, HidSharp |
| Referenz | `OpenFFBoard-master/OpenFFBoard-master` | Submodule, nur lesen |

## Harte Regeln

1. **Jedes Feature hat einen Schalter, Default = altes Verhalten.** Mit allen Schaltern
   eines Pakets auf „aus“ muss das Verhalten identisch zum Stand vor dem Paket sein.
   Ausnahmen nur für reine Robustheits-Fixes ohne spürbare Änderung; im PR begründen.
2. **Pakete sind unabhängig.** Kein Paket setzt Code eines anderen Pakets voraus.
   Optionale Verzahnungen bringt das später umgesetzte Paket mit, mit eigenem Schalter.
   Dafür werden gemeinsame Dateien bewusst mehrfach angefasst.
3. **ODrive-Konfigurationsstrukturen nicht ändern.** Keine Änderungen an `Config_t`
   in `motor.hpp`, `controller.hpp`, `encoder.hpp`, `axis.hpp`, an den Board-/ODrive-Configs
   und an `odrive-interface.yaml`. Grund: ODrive speichert die Config als Binärblock mit
   CRC (`nvm_config.hpp`); jede Strukturänderung setzt beim Flashen die komplette Motor-
   und Encoder-Kalibrierung zurück. Neue ODrive-seitige Parameter sind Laufzeitvariablen
   (Nicht-Config-Member), die `odrive_bridge` beim Booten aus unserem EEPROM setzt.
   Laufzeit-Overrides **nie** in `config_` schreiben, sonst landen sie beim Speichern der
   ODrive-Config im Alt-Slot.
4. **EEPROM-Regeln (FFB-Speicher, Sektoren 1+2):**
   - `EE_LAYOUT_VERSION` nicht erhöhen (die Firmware formatiert sonst den FFB-Speicher).
   - Neue Variablen nur im Adressblock des eigenen Pakets (Tabelle in `docs/ROADMAP.md`).
   - Jede neue Adresse in `VirtAddVarTab` eintragen und `NB_OF_VAR` anpassen. Nicht
     eingetragene Adressen gehen beim Page-Transfer verloren.
   - Unbeschrieben (`0xFFFF`) bedeutet Default bzw. „aus“.
5. **Neue Logik in eigene Dateien.** In gemeinsamen Dateien (`cmd_table.cpp`,
   `eeprom_addresses.*`, `ffb_task.cpp`, `controller.cpp`, `odrive_bridge.*`,
   `SettingsControl.xaml(.cs)`, `OdriveProfileFields.cs`) nur kurze, abgegrenzte Blöcke,
   jeweils markiert mit `// [pkgN] <Zweck>`.
6. **Logik-Module ohne HAL-Abhängigkeit schreiben** (keine STM32-/FreeRTOS-Includes im
   Rechenkern), damit sie unter `tests/host/` mit g++ auf dem PC testbar sind.
7. **Befehle:** eigener Präfix pro Paket (siehe ROADMAP). Bestehende Befehle behalten
   Namen und Bedeutung. Jedes Paket bietet `<präfix>.status?` für den Zustand.
   Keine Änderung am HID-Report-Deskriptor ohne Rücksprache mit dem Nutzer.
8. **Echtzeit:**
   - 8-kHz-Pfad (Regel-Interrupt): kein Heap, kein `printf`, keine RTOS-Aufrufe,
     keine Mutexe, kein `double`; Float-Literale mit `f`. Laufzeit messen und im PR nennen.
   - 1-kHz-FFB-Thread: im Normalbetrieb kein Heap.
9. **Speicherbudget:** Haupt-RAM ca. 49 KB frei, CCM-RAM voll (FreeRTOS-Heap).
   Firmware-Code aktuell ca. 280 KB. Flash-Sektor 9 (`0x080A0000`, 128 KB) ist für den
   Slot-Speicher aus Paket 6 reserviert. Größenänderung (`arm-none-eabi-size`) im PR nennen.
10. **Hardware-Sicherheit (25 Nm!):** Claude sendet keine Befehle ans Board, die den Motor
    scharf schalten, kalibrieren, bewegen, Testkräfte erzeugen, die Config löschen oder
    flashen. Lesende Befehle nur nach Freigabe durch den Nutzer. Hardware-Tests führt der
    Nutzer selbst aus; Claude liefert die Testanleitung.
11. Submodule und Fremdcode (`OpenFFBoard-master`, `ThirdParty`, HAL) nicht ändern.
12. **Flashen per Software:** Jeder DFU-Code (Web-Client, Plugin) darf die Sektoren
    S1+S2 (FFB-EEPROM), S9 (Slot-Speicher, reserviert) und S10+S11 (ODrive-NVM) weder
    löschen noch beschreiben.

## Workflow pro Paket

1. Vor dem Start: Paketbeschreibung in `docs/ROADMAP.md` und, falls vorhanden, die
   Spezifikation `docs/packages/PKGn.md` lesen. Plan kurz mit dem Nutzer abstimmen
   (Dateien, neue Befehle, EEPROM-Adressen, offene Fragen). Große Pakete werden in
   Teilschritte mit je eigenem Branch und PR zerlegt (`pkg/1a-...`).
2. Branch `pkg/<n>-<kurzname>` vom aktuellen `main` dieses Forks.
3. Kleine Commits, Conventional Commits auf Englisch, z. B. `feat(pkg3): add endstop priority switch`.
4. Firmware und Plugin bauen; keine neuen Warnungen.
5. Testanleitung nach `docs/TESTING.md` in die PR-Beschreibung schreiben.
6. PR **gegen `main` dieses Forks**, nicht gegen eagabriel:
   `gh pr create --repo JustOneMorePrint/Odrive-Wheel --base main`
7. Nach dem Merge: Status in `docs/ROADMAP.md` aktualisieren.

## Build

- **Firmware, Windows:** `.\build-local.ps1` im Repo-Root (MSYS2 + ARM GCC).
- **Firmware, Linux/WSL:** Autogen-Schritte aus `docs/BUILDING.md`, dann `make -C Odrive-Wheel -j`.
- **CI:** `.github/workflows/build.yml` baut bei jedem PR gegen `main` und lädt
  `odrive-wheel.bin` als Artifact hoch.
- **Ausgabe:** `Odrive-Wheel/build/odrive-wheel.bin` / `.hex`.
- **Plugin:** `cd Odrive-Wheel/tools/simhub-plugin && dotnet build -c Release`
  (optional `-p:SimHubPath="D:\SimHub"`). Kopiert die DLL nach SimHub; SimHub vorher schließen.

## Konventionen

- Code-Kommentare, Commit-Messages und Bezeichner: Englisch.
- Doku für den Nutzer (`docs/`, PR-Beschreibungen, Testanleitungen): Deutsch.
- Nicht ändern: `sys.swver` liefert absichtlich fest „1.17.0“ (Kompatibilität mit dem
  OpenFFBoard-Configurator). Zur Identifikation der Build-Version dient `sys.build?`
  (Git-Hash und Branch, angelegt im Setup-Schritt 0 der ROADMAP).
