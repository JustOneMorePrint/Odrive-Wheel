# Plugin-Tests

Testet die Logik des SimHub-Plugins gegen eine Attrappe des Boards (`FakeBoard.cs`). Kein Board,
keine SimHub-Installation und keine ARM-Toolchain nötig. Die CI führt sie bei jedem PR gegen `main`
aus (`.github/workflows/plugin-tests.yml`, Windows).

`SerialPortTransport.cs` und `WmiPortLocator.cs` gehören absichtlich nicht zu diesem Projekt. Eine
Verbindung zu einem echten COM-Port ist hier also ausgeschlossen, auch nicht aus Versehen.

## Ausführen

Voraussetzung: .NET SDK und Zugang zu nuget.org. Läuft als x86-Prozess, wie `SimHubWPF.exe`.

```bash
dotnet run --project tests/plugin -c Release
```

Nur ein Teil, gefiltert nach dem Testnamen:

```bash
dotnet run --project tests/plugin -c Release -- stop
```

Erfolgreicher Lauf:

```text
plugin tests: <n> tests, <n> checks, 0 failed, <n> s
```

Schlägt ein Check fehl, steht `FAILED:` mit Test und Grund in der Ausgabe, und der Exit-Code ist 1.
Ein hängender Test scheitert nach 30 s an seinem Zeitlimit.

## Was geprüft wird

| Datei | Prüft |
|---|---|
| `SerialServiceTests.cs` | Reihenfolge und Prioritäten, nur ein Befehl auf der Leitung, Zuordnung der Antworten, Timeouts, Abbruch, Verbindungsverlust, Wiederverbinden ohne Nachholen, Port freigeben und übernehmen |
| `StopTests.cs` | Vorrang des Stopps, Verzögerung bei laufendem Warten und blockiertem Schreiben, Bestätigung, Stop-Sperre, Beenden des Dienstes |
| `ResyncTests.cs` | Nichts Abgesagtes verlässt die Vorbereitung, gesendet und nicht gesendet bleiben unterscheidbar, auch bei einem Abbruch zu jedem Zeitpunkt; verspätete Antworten erreichen nach Timeout oder Abbruch nie den nächsten Befehl, in beliebigem zeitlichem Abstand und auf beiden Antwortwegen, auch bei wiederholten Lese- und Schreibbefehlen desselben Pfads |
| `StopDisplayTests.cs` | Grün nur, solange der Stopp gilt und die Achse jetzt IDLE meldet; Widerspruch, fehlende Rückmeldung, neue Verbindung, gelöschte Sperre |
| `ParameterTests.cs` | Beide Befehlsdialekte, Ablehnungen, ungültige Eingaben, Parameter-Katalog, Statustexte |
| `CatalogInterfaceTests.cs` | Katalog gegen die Firmware: Pfade, Typen, Schreibbarkeit, Einheiten und Enum-Namen gegen `odrive-interface.yaml` (gelesen mit `YamlSubset.cs`), OpenFFBoard-Befehle gegen `cmd_table.cpp`, Fehlertabellen des Decoders, keine erfundenen Grenzwerte |
| `ParameterReaderTests.cs` | Lese-Logik der Tabs: Anzeige je Typ, gekürzte Zahlen, Fehlerdecoder, nicht unterstützte Parameter, Timeouts, veraltete Werte, Stopp und Portfreigabe während des Lesens |
| `EditorWindowTests.cs` | Lesende Tabs im echten Fenster: gezieltes Aktualisieren von Tab und Gruppe, begrenztes Polling, der Nachweis, dass jeder Button nur Lesebefehle sendet, Stop- und Port-Button während des Lesens |
| `PowerSupplyTests.cs` | Power supply data: validation, headroom calculation, optional short-time values, settings round trip, not part of profiles, no board command from the window |
| `PowerBrakeTests.cs` | Power / Brake editor: relations from the firmware, float32 values and readback, ambiguous replies of older firmware as readback and as a basis, the review, every reason not to write, aborts between two writes, a stop or a new connection right before a line and around a readback, commands bound to a connection and stop state, and in the real window that only selected values go out and the stop button works during an apply |
| `ProfileAndActionTests.cs` | Profil anwenden in exakt der bisherigen Zeilenfolge, Spielwechsel, Save to flash, Profildatei, Inc/Dec-Aktionen |
| `WindowTests.cs` | Echtes WPF-Fenster auf eigenem Thread, Stop-Button per UI-Automation, modaler Dialog auf einem anderen Thread, Schließen während des Starts, Bereich „Extensions“ |

## Was hier nicht geprüft wird

Echte Port-Zugriffe (`SerialPortTransport`), USB ziehen, WMI, die SimHub-Einbindung und das Verhalten
am Board. Das sind Hardware-Tests durch den Nutzer (`docs/TESTING.md`), die Schritte stehen im jeweiligen
PR. Die Zeitgrenzen der Stop-Tests gelten für die PC-Seite. Eine Firmware, die gerade den Flash
beschreibt, kann die Attrappe nicht nachbilden.

## Neuen Test anlegen

Eine `public static void Test_...`-Methode in einer `public static class` genügt, der Runner findet sie
selbst. `Rig` liefert einen gestarteten Dienst auf einer Attrappe, `ProfileRig` zusätzlich den Profil-Dienst
nach dem ersten Lesen. Auf Ereignisse wartet `Wait.Until`, nicht `Thread.Sleep`, damit die Tests auch auf
einer ausgelasteten CI-Maschine stabil bleiben. Für einen exakten Zeitpunkt hält `WorkerHold` den Worker bei
seinem nächsten Lese- oder Schreibzugriff an.

`FakeBoard.Lines` enthält die Befehle, `FakeBoard.WireLines` zusätzlich die Sync-Zeilen `f <n>` und
`odwsync.m<n>?`. Die Attrappe beantwortet ODrive- und OpenFFBoard-Befehle wie die Firmware getrennt: Jeder
Weg hält seine Reihenfolge, einer kann den anderen überholen.
