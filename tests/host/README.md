# Host-Tests

Testet HAL-freie Logik-Module mit g++ auf dem PC, ohne Board und ohne ARM-Toolchain
(Testebene T1 in `docs/TESTING.md`). Die CI führt sie bei jedem PR gegen `main` aus
(`.github/workflows/host-tests.yml`).

## Ausführen

Linux/WSL:

```bash
make -C tests/host check
```

Windows (MSYS2 mit den Paketen `mingw-w64-x86_64-gcc` und `mingw-w64-x86_64-make`,
in der Shell „MSYS2 MINGW64“, git im `PATH`):

```bash
mingw32-make -C tests/host check CXX=g++
```

Zusätzlich nur unter Windows, in PowerShell (siehe „Tests der Build-Kennung“):

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tests\host\test_build_local.ps1
```

Aufräumen: `make -C tests/host clean`.

Erfolgreicher Lauf:

```text
test_example_clamp.cpp: 5 checks, 0 failed
test_build_info.sh: <n> checks, 0 failed, 0 skipped
test_make_clean.sh: <n> checks, 0 failed
```

Schlägt ein Check fehl, stehen Datei und Zeile in der Ausgabe, und `make` endet mit
einem Fehlercode.

## Neuen Test anlegen

1. Datei `tests/host/test_<modul>.cpp` anlegen. Jede Datei `test_*.cpp` wird automatisch
   zu einem eigenen Testprogramm gebaut; die Makefile muss nicht geändert werden.
2. `test_util.hpp` einbinden und in `main()` die Makros `CHECK` und `CHECK_NEAR`
   verwenden. Am Ende `return TEST_RESULT();`.
3. Das zu testende Modul per relativem Pfad einbinden, z. B.
   `#include "../../Odrive-Wheel/src/haptics_core.cpp"`. Es darf keine STM32- oder
   FreeRTOS-Header einbinden (`CLAUDE.md`, Regel 6).

`example_clamp.hpp` und `test_example_clamp.cpp` sind nur das Beispiel für dieses Gerüst.
Sie können entfallen, sobald es echte Tests gibt.

## Sync-Zeile des SimHub-Plugins

`test_cmdparser_sync.cpp` bindet den echten Parser `Odrive-Wheel/src/cmdparser.c` ein und prüft die
Zeile, mit der das Plugin den OpenFFBoard-Antwortweg synchronisiert: Sie ruft keinen Handler auf und
wird mit `NOT_FOUND` samt vollständiger Marke beantwortet. Die Befehlstabelle ist ein Platzhalter,
weil `cmd_table.cpp` die HAL braucht. Der Test liest deshalb zusätzlich den Quelltext dieser Datei,
die Klasse der Sync-Zeile darf dort nicht vorkommen. Die Weiche in `process_line`
(`ascii_protocol.cpp`) ist nur durch Lesen geprüft, sie hängt am ODrive-Code.

## Tests der Build-Kennung (`sys.build?`)

Die Firmware meldet `<checkout>[-dirty] [head=<PR-Head>] branch=<Branch>`.
`<checkout>` ist immer der tatsächlich gebaute Commit, bei einem PR-Build in der CI also
der Merge-Commit. `head=` gibt es nur dort.

| Datei | Prüft | Voraussetzung |
|---|---|---|
| `test_build_info.sh` (läuft mit `check`) | Heikle Branch-Namen kommen über die echte Makefile-Regel Byte für Byte im kompilierten `BUILD_ID` an und werden nicht ausgewertet; zudem Header-Aktualisierung, `-dirty`, detached HEAD, Kürzung, Ablehnung ungültiger Werte | git, make, g++ |
| `check_firmware_rebuild.sh` (eigener CI-Schritt im Firmware-Build) | Kennung in der `.bin`; unveränderter Wiederholungsbuild kompiliert nichts; geänderte Metadaten kompilieren nur `cmd_table.cpp` neu und landen in der `.bin`; danach wieder identische `.bin` | ARM-Toolchain, fertiger Build, sauberer Checkout |
| `test_build_local.ps1` (nur Windows, nicht Teil von `check`) | `build-local.ps1` in einem Pfad mit Leerzeichen, Klammern und Apostroph: vollständiger Shell-Befehl, `-NoClean`, Kennung, Fehlercodes bei Shell, Verzeichniswechsel und Make, alte Artefakte, `MSYS2_ENV_CONV_EXCL` | PowerShell, MSYS2 mit `mingw32-make`, git, Python mit den Autogen-Paketen. Die Firmware ist dabei eine Attrappe (kleines Makefile); die ARM-Toolchain wird nicht gebraucht |

Lässt das Dateisystem einen Branch-Namen nicht zu (Windows: z. B. `"` oder `|`), meldet
`test_build_info.sh` ihn als `SKIPPED` und prüft ihn nur über die Umgebungsvariable.

`test_build_local.ps1` endet mit Code 2 und `NOT RUN`, wenn ein Werkzeug fehlt. Das ist kein
bestandener Test. Es installiert nichts.

`check_firmware_rebuild.sh` unter Windows, nach einem Build in der Shell „MSYS2 MINGW64“ und
aus einem sauberen Checkout:

```bash
MAKE=mingw32-make sh tests/host/check_firmware_rebuild.sh
```

Das Skript ruft Make direkt auf, Autogen läuft dabei nicht. Deshalb gilt die Erwartung „kompiliert
nichts“ nur hier und nicht für `build-local.ps1 -NoClean`, das Autogen erneut ausführt.

## Tests von `make clean`

Die Objekte der ODrive- und OpenFFBoard-Quellen liegen nicht in `build/`, sondern daneben in
`Odrive-Wheel/ODrive-fw-v0.5.6/` und `Odrive-Wheel/OpenFFBoard-master/`.

| Datei | Prüft | Voraussetzung |
|---|---|---|
| `test_make_clean.sh` (läuft mit `check`) | Die echte Clean-Regel entfernt alle Objekte und `.d`-Dateien, auch ohne vorhandenes `build/`, und sonst nichts; ein fehlgeschlagenes Löschen beendet Make mit Fehler. Die Objekte sind leere Attrappen an den Pfaden aus dem Makefile | make |
| `check_firmware_clean.sh` (letzter CI-Schritt im Firmware-Build) | Nach einem echten Build: `make clean` lässt nichts übrig und ändert keine versionierte Datei; der nächste Build übersetzt alle Quellen und ergibt dieselbe `.bin` | ARM-Toolchain, fertiger Build, sauberer Checkout |

Unter Windows, in der Shell „MSYS2 MINGW64“ und mit denselben Voraussetzungen wie oben:

```bash
MAKE=mingw32-make sh tests/host/check_firmware_clean.sh
```
