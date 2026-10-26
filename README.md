# Bahn-Plugin für Brim

Zeigt die nächsten Züge an deinen Bahnhöfen in der Notch und auf „Jetzt“, mit Verspätung, Ausfällen und Gleiswechseln.

- **In der Notch:** Der nächste Zug, den du noch schaffst, steht kompakt neben der Notch, z. B. „RE 7 · 12 min“. Rot heißt Ausfall oder ab 5 Minuten Verspätung, gelb ein anderes Gleis. Aufgeklappt siehst du die ganze Tafel.
- **Meldungen:** Fällt der nächste Zug aus, wechselt er das Gleis oder kommen 5 Minuten Verspätung dazu, meldet sich die Notch kurz, z. B. „RE 7 16:42 nach Offenburg: heute von Gleis 5 statt 3“.
- **Widget „Abfahrten“** (klein, mittel, breit, groß, ganz): Plan, Echtzeit, Linie, Ziel und Gleis. Bei mehreren Bahnhöfen blätterst du mit einem Klick auf die Karte.
- **Widget „Pendelstrecke“** (mittel, breit, groß): die nächsten Verbindungen von A nach B mit Ankunft und Umstiegen.
- **Weg zum Bahnhof:** Züge, die du zu Fuß nicht mehr erreichst, blendet das Plugin aus.

## Woher die Daten kommen

Ohne Einrichtung kommen die Daten von [transport.rest](https://v6.db.transport.rest/) (Fahrplan der Deutschen Bahn, ohne API-Key). Der Dienst ist ein Freiwilligenprojekt und fällt ab und zu aus; dann steht auf den Widgets „offline“ und die letzten Abfahrten bleiben stehen.

Zuverlässiger ist die offizielle **Timetables-API der Deutschen Bahn**. Sie ist kostenlos, braucht aber ein Konto:

1. Auf [developers.deutschebahn.com](https://developers.deutschebahn.com/) anmelden und eine Anwendung anlegen.
2. Die API **Timetables** für diese Anwendung abonnieren (Plan „Free“).
3. Client-ID und API-Key in Brim eintragen: Einstellungen → Plugins → Bahn.

Mit der DB-API zeigt die Pendelstrecke nur direkte Züge, die am Ziel halten, und keine Ankunftszeit. Umstiege kennt nur transport.rest.

## Rechte

Brim fragt beim Einschalten nach dem Recht **„Netzwerk“**. Das Plugin spricht nur mit transport.rest oder der DB-API.

## Installieren

**Aus der Bibliothek:** Brim → Einstellungen → Plugins → Bibliothek → Bahn → Installieren.

**Fertig gebaut:** `brim-db.zip` aus dem neuesten Release nach `%LocalAppData%\Brim\Plugins\bahn` entpacken, Brim neu starten und im Tab **Plugins** „Bahn“ einschalten.

**Selbst bauen:** Das Plugin nutzt `Brim.Sdk` aus dem Brim-Repository. Beide Repositories nebeneinander klonen:

```powershell
git clone https://github.com/wheatley2056/brim.git
git clone https://github.com/wheatley2056/brim-db.git
cd brim-db
dotnet publish src/Bahn -c Release -r win-x64 --self-contained false -o "$env:LOCALAPPDATA\Brim\Plugins\bahn"
```

Liegt Brim woanders: `-p:BrimRoot=C:\Pfad\zu\brim`.

## Einstellungen

Brim → Einstellungen → Plugins → Bahn:

- **Lieblingsbahnhöfe:** Namen oder EVA-Nummern, mit Komma getrennt, z. B. `Freiburg Hbf, Offenburg`. Der erste steht in der Notch.
- **Pendelstrecke von / nach:** z. B. `Freiburg Hbf` und `Offenburg`. Ist sie gesetzt, zeigt die Notch ihre nächste Verbindung statt des ersten Bahnhofs.
- **Weg zum Bahnhof in Minuten** (0)
- **Nächsten Zug in der Notch zeigen** (an), **sobald er in höchstens so vielen Minuten fährt** (30). Davor bleibt er im Hintergrund und steht nur aufgeklappt auf „Jetzt“.
- **Melden, wenn der nächste Zug ausfällt, sich verspätet oder das Gleis wechselt** (an)
- **Nur Züge** (an): keine Busse, Straßen- und U-Bahnen.
- **Client-ID / API-Key** für die DB-API (leer: transport.rest)
- **Server für transport.rest:** für einen eigenen Server mit [db-rest](https://github.com/derhuerst/db-rest).

## Wenn etwas nicht klappt

Das Protokoll liegt in `%LocalAppData%\Brim\logs\plugins\de.brim.bahn.log`. Dort steht, welche Quelle das Plugin nutzt, welchen Bahnhof es zu deinem Namen gefunden hat (z. B. „Bahnhof Freiburg Hbf: Freiburg(Breisgau) Hbf (8000107)“) und jeder Fehler beim Abruf.

- **„Bahnhof … nicht gefunden“:** den Namen genauer schreiben oder die EVA-Nummer eintragen.
- **„transport.rest antwortet gerade nicht“:** später noch mal, oder die DB-API einrichten.
- **„Client-ID oder API-Key stimmt nicht“:** Schlüssel prüfen und ob die Anwendung die Timetables-API abonniert hat.
