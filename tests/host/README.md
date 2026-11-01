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
in der Shell „MSYS2 MINGW64“):

```bash
mingw32-make -C tests/host check
```

Aufräumen: `make -C tests/host clean`.

Erfolgreicher Lauf:

```text
test_example_clamp.cpp: 5 checks, 0 failed
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
