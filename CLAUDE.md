# CLAUDE.md – Arbeitsregeln für dieses Repository

Diese Datei liest Claude Code zu Beginn jeder Sitzung. Sie gilt für alle Arbeiten
in diesem Fork. Planning is in `docs/ROADMAP.md`, the shared contract in `docs/FOUNDATIONS.md`,
das Testvorgehen in `docs/TESTING.md`.

## Kontext

- Product goal: a DIY direct drive for hoverboard and industrial servo motors, operated entirely through the
  English SimHub plugin. Reliable function, reproducible setup, low measured latency, understandable diagnosis.
- Fork von `eagabriel/Odrive-Wheel`: ODrive-Firmware 0.5.6 (Motorregelung) plus
  OpenFFBoard-FFB-Stack auf STM32F405.
- Target boards: MKS XDrive Mini, MKS XDrive S, MKS XDrive. What is planned and what is checked for each: `docs/HARDWARE.md`.
- Zielhardware des Nutzers: DIY-Direct-Drive-Basis (bis 25 Nm) mit KUKA-Servomotor
  an 48 V, Bremswiderstand, ESP32 schaltet die Versorgung per Relais.
- Packages keep their numbers, the order follows the user's flows: `docs/ROADMAP.md`.
- Der Web-Client `Odrive-Wheel/tools/odrive-wheel.html` wird durch das SimHub-Plugin
  ersetzt. Er bleibt im Repo, wird aber **nicht mehr geändert**. GitHub Pages gehört nicht
  zum Projektbetrieb.
- Kompatibilität zwischen alter und neuer Firmware bzw. Software ist kein Ziel.
- Testhardware des Nutzers: **MKS XDrive Mini** (VBUS-Teiler 19, Default der Firmware).
- Die Alt/Neu-Schalter neuer Features gelten **global**. Sie sind nicht Teil der
  Plugin-Profile und werden beim Anwenden eines Profils nicht verändert.
- Hardware tests are deferred at the user's request and stay listed as open in `docs/TESTING.md`.

## Documentation scope

Use the current contracts in `docs/ROADMAP.md`, `docs/FOUNDATIONS.md`, `docs/HARDWARE.md`, `docs/MEASUREMENT.md`,
`docs/TESTING.md` and `docs/packages/PKG1.md` together with the production code. Distinguish planned behaviour from implemented behaviour.
Legacy HTML-client guides, historical release notes, alternative integration notes and vendored documentation describe their original
context; they do not establish this fork's current defaults, compatibility or validation. Explicit user instructions take precedence.

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

1. **Switches only for real variants.** An old/new switch exists where a comparison or a tuning variant makes sense, with
   the old behaviour as default. Safety and robustness corrections always apply, the PR gives the reason. A value that depends on the
   hardware is a setting, no switch. Calibrations stay traceable to hardware and version (`docs/FOUNDATIONS.md`).
2. **Shared foundations.** Connection, stop, firmware capabilities, abort, partial changes and unclear results follow
   `docs/FOUNDATIONS.md`, no function brings its own rules. Real dependencies between packages are listed in `docs/ROADMAP.md`,
   and a package does not need more than those.
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
13. **Protection is decided in the firmware.** The plugin explains and refuses early, and it is never the only barrier for
    something that can do harm. Where that is not so yet, `docs/FOUNDATIONS.md` says so. Hands-off detection is no protection.
14. **Minimum capabilities.** A write function needs what `docs/FOUNDATIONS.md` lists. There are no new heuristics to guess what an
    older firmware can do. No motor limit is invented: without the motor's data the field stays empty and what needs it stays off.
15. **Measured claims.** A benefit counts when it is measured with a metric from `docs/MEASUREMENT.md`, before and after. Otherwise
    it is called a hypothesis.

## How a task works

Standing rules are here and are not repeated in a task. A task states four things:

- **User goal:** what the user can do afterwards.
- **Scope:** what is in it, and what is left out.
- **State contract:** which states can change during it (connection, stop, capabilities, values), and what happens then.
- **Acceptance:** what shows that it is done.

Then:

1. Read the part of `docs/ROADMAP.md` and any specification the task touches. Implement directly if the task is clear, large ones in
   parts with a branch and PR each.
2. Branch from the current `main` of this fork: `pkg/<n>-<kurzname>`, `fix/…`, `docs/…`.
3. Kleine Commits, Conventional Commits auf Englisch, z. B. `feat(pkg3): add endstop priority switch`.
4. Build what the change touches, no new warnings. A pure documentation change needs no build.
5. Choose tests by failure cases and state transitions (`docs/TESTING.md`). A correction shows its regression red first.
6. PR **gegen `main` dieses Forks**, nicht gegen eagabriel:
   `gh pr create --repo JustOneMorePrint/Odrive-Wheel --base main`. Test instructions for the user go into the description.
7. The full final report is an English comment on the PR: head and base, what was done and left out, decisions, tests, builds, CI
   links, limits, open hardware tests. Update that comment after corrections. The chat message says PR link, head and CI status, and
   anything that is wrong or risky.
8. Auto-fix applies to the PR it was turned on for, and ends with it. Claude does not merge.
9. After the merge: update the status in `docs/ROADMAP.md` and the open tests in `docs/TESTING.md`.

### Before reporting a PR as ready

This review is part of the task and needs no additional approval round.

1. State the applicable invariants and a small failure/transition matrix before implementation. For operations, include the relevant events
   (Cancel, stop, reconnect, capability change, closing) at the boundaries before sending, after a reply and before final completion. Keep
   related work in small, usable steps rather than combining several independent workflows in one PR.
2. Trace the full production path against those invariants. Check the result object, visible state and exported output together. In particular,
   check late cancellation, blocking work on the UI thread, unsupported certainty claims, invalid input types and numeric ranges, and cleanup
   that must only remove files the operation created. Command syntax alone is no proof that an operation has no side effects.
3. Use an independent reviewer with fresh context when available. Give it the requirements, relevant contracts, final diff and source. It must
   look for concrete functional errors and missing failure cases, not treat the implementation narrative or green CI as proof. If no separate
   reviewer is available, perform a distinct review pass and record that limitation.
4. Reproduce confirmed findings with targeted, deterministic regressions, show them failing on the affected code, and fix them within the PR.
   Use controlled hooks or barriers for timing cases. Test counts and successful mutations do not establish completeness. Avoid broad mutation
   campaigns and repeated full-suite runs without a specific unresolved timing concern.
5. Run the relevant builds and tests on the final code, and verify CI for the reported head. Review the corrections against the affected
   invariants; repeat a full review only if new changes or findings justify it. Documentation-only changes need source/diff checks, not builds.
6. Include the review findings, their resolution, evidence and remaining limits in the English final PR comment. Keep deferred hardware tests
   explicitly open. This process does not authorize hardware commands or an automatic merge.

**Handover after a finished PR.** A few lines are enough for a new session:

```text
main at <hash>, merged PR #<n>: <one line>.
Open hardware tests: docs/TESTING.md.
Next task: <goal, scope, state contract, acceptance>.
```

## Build

- **Firmware, Windows:** `.\build-local.ps1` im Repo-Root (MSYS2 + ARM GCC).
- **Firmware, Linux/WSL:** Autogen-Schritte aus `docs/BUILDING.md`, dann `make -C Odrive-Wheel -j`.
- **CI:** `.github/workflows/build.yml` baut bei jedem PR gegen `main` und lädt
  `odrive-wheel.bin` als Artifact hoch.
- **Ausgabe:** `Odrive-Wheel/build/odrive-wheel.bin` / `.hex`.
- **Plugin:** `cd Odrive-Wheel/tools/simhub-plugin && dotnet build -c Release`
  (optional `-p:SimHubPath="D:\SimHub"`). Kopiert die DLL nach SimHub; SimHub vorher schließen.

## Konventionen

- Talking to the user: German.
- User interface, identifiers, code comments, new or changed documentation, commit messages, PR
  descriptions, test instructions and PR comments: English. Existing German documents are not
  translated wholesale, only the parts that change.
- Nicht ändern: `sys.swver` liefert absichtlich fest „1.17.0“ (Kompatibilität mit dem
  OpenFFBoard-Configurator). Zur Identifikation der Build-Version dient `sys.build?`
  (Git-Hash und Branch, angelegt im Setup-Schritt 0 der ROADMAP).
