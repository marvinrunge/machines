# Neue Karten für den Story-Modus – Vorgehen (Vorschlag)

> Status: Entwurf, noch nichts gebaut. Er beruht auf einer Bestandsaufnahme des Codes vom 2026-11-20 (Stellen unten
> verlinkt). Ziel ist eine **Kartenschmiede**: ein Ablauf, der aus einer Beschreibung eine spielbare, schöne Karte macht.
> Die KI plant, entwirft und prüft. Feste Werkzeuge rechnen, damit jede Karte reproduzierbar ist und online bei allen Spielern
> gleich aussieht.

## Was eine Karte heute ist

Ein Planet besteht aus den Textdateien des Originals in `data/original/models/planet/<p>/` und aus dem, was der Exporter aus der
eigenen Spielkopie zieht (`classic/planets/<p>/`, nicht im Repo):

| Datei | Inhalt | Wer liest sie |
|---|---|---|
| `<p>.psf` | Kachelgitter: Größe, Kachelmaß (30–300 m), pro Kachel ein Original-Mesh | `SurfaceParser`, Größe der Welt |
| `<p>.csp` | **Hindernis-Polygone** (Fels, Wasser). Daraus allein entsteht die Begehbarkeit | `NavGrid` (4-m-Zellen) |
| `<p>.env` | Sonne, Nebel, Farben, Himmel | `EnvironmentParser` |
| `heights.bin` | Höhengitter, Kopf mit Spalten, Zeilen, Abstand (bisher immer 4 m) | `HeightField`, `TerrainHeights` (Sicht und Schuss) |
| `terrain.json` | die Original-Kacheln als ein Mesh mit BMP-Texturen | `TerrainBuilder` |
| `artefacts/*.json` | Requisiten des Planeten | `ArtefactMesh` |
| `.scn` | Szenario: Einheiten, Gebäude, `ARTEFACTS` (sichtbar oder mit Kollision) | `ScenarioParser` |

Der Überholt-Look malt den Boden selbst. Dafür liest er aber die Farben der Original-BMPs (`TerrainMaterials.Classify`), und er
kennt nur Planeten-Familien, die im Code stehen. Auch Himmel, Wetter und Steine hängen an Planetennamen in festen Tabellen.

## Wo das Original schwach ist, und was wir besser machen

| Original | Neu |
|---|---|
| Kleine Karten (oft 1,6 km), grobe 80-m-Kacheln, Hügel aus wenigen Dreiecken | Größere Karten, feinere Höhen (2 m statt 4 m prüfen), Geländeformen mit **Erosion**: Rinnen, Schuttkegel, Terrassen, ausgewaschene Canyons |
| Boden aus wiederholten BMP-Kacheln | **Materialien aus der Form berechnet**: Neigung, Höhe, Krümmung, Wasserfluss, Feuchte → Sand, Kies, Fels, Schnee, Gras, Asche |
| Begehbarkeit von Hand als Polygone gezeichnet, passt nicht immer zum Bild | Hindernisse **automatisch aus dem Gelände** (Steilheit, Wasser), immer deckungsgleich mit dem, was man sieht |
| Wenige, kleine Requisiten | **Meshy-Requisiten** für jeden besonderen Ort, dazu verstreute Natur (Steine, Pflanzen) aus dem Bestand |
| Karten fürs RTS, ohne Geschichte | Karten **nach dem Story-Board**: Wahrzeichen, Sichtachsen, Engstellen, Verstecke, Wege für die Ego-Sicht |
| Feste Tabellen pro Planet im Code | Eine Datei `look.json` pro neuem Planeten (Himmel, Wetter, Materialfamilie, Licht) |

## Die Kartenschmiede in sieben Schritten

```
Story-Board ─► 1 Karten-Brief ─► 2 Skizze ─► 3 Höhen ─► 4 Materialien ─► 5 Hindernisse ─► 6 Requisiten ─► 7 Szenario
                 (Claude)          (Gemini)    (Werkzeug)   (Werkzeug)       (Werkzeug)        (Gemini → Meshy)   (Claude)
                                                    ▲                                                                  │
                                                    └──────────────── 8 Prüfen: Bilder, Wege, Review ◄─────────────────┘
```

### 1. Karten-Brief (Claude)

Aus den Szenen eines Akts schreibt Claude eine `map.json`: Größe, Biom, Tageszeit und Wetter, dazu eine Liste von **Orten**
mit Art und Zweck. Beispiel Akt 1: Absturzkrater mit Wrack, Landeplatz des Pods, Ebene mit dem Friedhof, Canyon, Tagebau,
Grat mit Funkmast. Für jeden Ort steht fest, wie groß er ist, wie er erreichbar ist, wer ihn sieht, und welche Szene dort spielt.
Dazu kommen Regeln: Vom Pod aus muss die Rauchsäule sichtbar sein. Zwischen Pod und Krater liegen mindestens zwei Wege. Der
Canyon hat eine Engstelle, durch die nur kleine Maschinen passen.

### 2. Skizze (Gemini)

Gemini zeichnet aus dem Brief eine **Draufsicht** (Kartenskizze mit Höhen als Schattierung) und Konzeptbilder der wichtigsten
Orte. Die Draufsicht ist ein Entwurf zum Abnicken, nicht die Karte selbst. Aus ihr liest Claude die grobe Lage der Orte und
gleicht sie mit dem Brief ab. Ein zweiter Versuch, der getestet werden sollte: Gemini malt direkt eine **Graustufen-Höhenkarte**,
die nur als grobe Vorform dient. Die Feinform kommt in jedem Fall aus Schritt 3.

### 3. Höhen (Werkzeug, von der KI gesteuert)

Ein neues Werkzeug `tools/mapgen` (Python oder C#, mit festem Startwert, also immer gleich) baut das Gelände aus **Bausteinen**,
die Claude mit Parametern setzt:

- Grundform: Ebene, Hügelland, Hochplateau, Dünen, Eisfeld (Rauschen in mehreren Größen).
- Stempel: Krater (Wall, Auswurf, Zentralberg), Canyon entlang einer Kurve, Tafelberg, Grat, Senke, Tagebau mit Terrassen,
  Flussbett, Klippenkante.
- Danach **Erosion** (Wasser und Hangrutsch), damit es nicht nach Rechenmuster aussieht. Sie liefert auch Fluss- und Schuttkarten
  für Schritt 4.
- Orte, die eben sein müssen (Pod, Basisplatz, Bauflächen fürs RTS), werden am Ende geglättet und geschützt.

Ausgabe: `heights.bin` im gewohnten Format. Den Abstand liest der Code schon aus der Datei. Ob 2 m durch Verfeinerung, Himmelssicht
und Bodenbewuchs sauber laufen, wird geprüft (`TerrainGeometry.Refine` rastet heute auf 4 m ein).

### 4. Materialien (Werkzeug)

Aus Neigung, Höhe, Krümmung, Erosion und Feuchte entsteht eine **Materialkarte** (Gewichte für bis zu vier Materialien plus Fels).
Der Biom-Eintrag aus dem Brief wählt die Familie, zum Beispiel Wüste: Sand, Kies, roter Fels, Salzkruste. Neu im Spiel: ein Weg,
der eine solche Materialkarte direkt liest, statt die Farben der Original-BMPs zu raten, und der den Boden aus `heights.bin` baut,
ohne `terrain.json`. Wasser und Lava bekommen ihre Flächen aus derselben Karte. Gras und Steine streut `GroundScatter` wie heute.

### 5. Hindernisse und Planetendateien (Werkzeug)

- `.csp`: Hindernis-Polygone werden **aus dem Gelände berechnet** (zu steil, Wasser, tiefe Senken), die Umrisse geglättet und
  vereinfacht. So passen Bild und Begehbarkeit immer zusammen.
- `.psf` mit Kacheln ohne Original-Mesh, `.env` aus einer Vorlage pro Biom, `look.json` für Himmel, Wetter und Licht.
- Neue Planeten liegen im Repo (zum Beispiel `data/planets/<p>/`), nicht in `classic/`, weil online alle denselben Boden brauchen.
  Dafür muss `GameData.LoadPlanet` auch dort suchen.

### 6. Requisiten (Gemini → Meshy)

- Für jeden besonderen Ort eine Liste eigener Modelle: das Wrack des Mutterschiffs, alte Pods, der Funkmast, Ruinen der Erbauer.
- Gemini zeichnet ein Blatt pro Modell, Meshy macht daraus ein Modell. Das ist der bekannte Weg der Gebäude und Waffen. Die
  Grenzen der Meshy-Läufe (eine Bitte pro Blatt) gelten weiter.
- Ins Spiel kommen sie als Artefakte: Kollision und Zerstörbarkeit gibt es schon (`ACTIVE` mit `BOUNDARY`). Neu ist nur, GLB-Modelle
  als Artefakt zu laden (`Scenery` kann GLB schon lesen, nur über eine feste Liste).
- Kleinzeug (Steine, Büsche, Trümmer) kommt aus dem vorhandenen Bestand und wird nach der Materialkarte verstreut.

### 7. Szenario (Claude)

Claude setzt nach dem Brief Pod, Gegner, Patrouillen, Beute und Auslöser in eine `.scn` und trägt die Karte in `data/maps/maps.txt`
ein. `tools/showcase-map.py` ist dafür der Anfang.

### 8. Prüfen

Jede Karte durchläuft automatisch:

- **Bilder:** Draufsicht, Minimap und fünf bis zehn Blicke aus Bodenhöhe von den Orten des Briefs, im Spiel aufgenommen
  (`MACHINES_SCREENSHOT`).
- **Wege:** Ein Test fragt die Wegsuche, ob alle Orte erreichbar sind, ob die Engstelle wirklich eng ist und ob es die zwei Wege
  gibt.
- **Regeln des Briefs:** Sichtlinien (sieht man die Rauchsäule?), ebene Bauplätze, Größen.
- **Review:** Claude vergleicht die Bilder mit der Skizze und dem Brief und schlägt Änderungen an den Parametern vor. Am Ende
  kommt eine Seite für dich, wie bei den Gebäuden, mit deinem Urteil pro Karte.

Danach geht es zurück zu Schritt 3, bis die Karte steht. Weil alles aus Brief und Parametern entsteht, ist jede Änderung ein
kleiner Eingriff und kein Neuanfang.

## Innenlevel

Das Mutterschiff im Prolog, das Wrack, das Relais und das Refugium sind keine Geländekarten. Für sie gilt dasselbe Prinzip mit
anderen Bausteinen: Claude entwirft einen **Raumgraph** (Räume, Gänge, Schächte, Türen, Engstellen), Meshy liefert einen
**Baukasten** von Modulen, ein Werkzeug setzt die Module zusammen. Das Spiel kann heute noch keine Innenlevel (siehe STORY.md,
„Dungeons“, Stufe 3). Das ist ein eigener, späterer Schritt.

## Vorschlag für den Anfang

Ein Prototyp für **eine** Karte, den Absturzplaneten aus Akt 1:

1. Brief und Skizze. Das ist schnell, du kannst Lage und Stimmung abnicken.
2. `tools/mapgen` mit Grundform, Krater, Canyon, Tagebau, Grat und Erosion. Dazu die Materialkarte, die `.csp` und die Planetendateien.
3. Im Spiel: neue Planeten aus `data/planets` laden, Boden aus Höhen und Materialkarte bauen, `look.json` statt Tabellen.
4. Das Szenario ohne eigene Requisiten (Wrack und Pods zuerst aus dem Bestand), dann das Prüfen.
5. Erst wenn die Karte trägt: Meshy-Requisiten für Wrack, Friedhof und Funkmast.

## Offen

- **Größe:** Wie groß sollen Story-Karten werden? Über 1,5 km wird die Materialkarte heute gröber, und die Minimap hat 1024 Pixel.
  Beides lässt sich anheben.
- **Feinere Höhen:** 2 m statt 4 m sehen besser aus, kosten aber Speicher und Ladezeit. Das klärt der Prototyp.
- **Gemini-Höhenkarte:** ob ein gemaltes Graustufenbild als Vorform taugt oder nur Chaos bringt. Das klärt ein kurzer Test.
- **Meshy-Budget:** wie viele eigene Modelle pro Karte.
