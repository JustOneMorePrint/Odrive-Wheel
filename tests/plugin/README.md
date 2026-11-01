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
| `ResyncTests.cs` | Nichts Abgesagtes verlässt die Vorbereitung, gesendet und nicht gesendet bleiben unterscheidbar; verspätete Antworten erreichen nach Timeout oder Abbruch nie den nächsten Befehl, in beliebigem zeitlichem Abstand |
| `StopDisplayTests.cs` | Grün nur, solange der Stopp gilt und die Achse jetzt IDLE meldet; Widerspruch, fehlende Rückmeldung, neue Verbindung, gelöschte Sperre |
| `ParameterTests.cs` | Beide Befehlsdialekte, Ablehnungen, ungültige Eingaben, Parameter-Katalog, Statustexte |
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

`FakeBoard.Lines` enthält die Befehle, `FakeBoard.WireLines` zusätzlich die Sync-Zeilen `f <n>`.
