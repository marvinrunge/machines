# Machines – Keiner trägt alles

Die Geschichte des Story-Modus im Machines-Remake als Buch. Grundlage sind [`docs/STORY.md`](../../STORY.md), das
Storyboard [`docs/story/board.json`](../board.json) und die Sprachzeilen des Prologs in
[`tools/voice/lines.json`](../../../tools/voice/lines.json).

Das ganze Buch in einer Datei: [Machines-Story.md](Machines-Story.md).

## Inhalt

| # | Kapitel | Datei |
|---|---|---|
| 0 | Prolog: Fehler | [00-prolog.md](00-prolog.md) |
| 1 | Erster Akt: Gestrandet | [01-gestrandet.md](01-gestrandet.md) |
| 2 | Zweiter Akt: Das Wrack | [02-das-wrack.md](02-das-wrack.md) |
| 3 | Dritter Akt: Funkstille | [03-funkstille.md](03-funkstille.md) |
| 4 | Vierter Akt: Die Suche | [04-die-suche.md](04-die-suche.md) |
| 5 | Fünfter Akt: Refugium | [05-refugium.md](05-refugium.md) |
| 6 | Sechster Akt: Neue Heimat | [06-neue-heimat.md](06-neue-heimat.md) |
| 7 | Finale: Quelle (mit Epilog) | [07-quelle.md](07-quelle.md) |
| | **Andere Enden** | |
| 8 | Andere Enden I: Gemeinsam | [08-anderes-ende-gemeinsam.md](08-anderes-ende-gemeinsam.md) |
| 9 | Andere Enden II: Auslöschung | [09-anderes-ende-ausloeschung.md](09-anderes-ende-ausloeschung.md) |

## Der Weg, den das Buch erzählt

Das Spiel hat drei Enden. Der Haupttext erzählt das Ende **Opfer** (festgelegt am 2026-11-21): Vertrauen ist da, aber zu
wenig Gnade. Die Entscheidungen auf diesem Weg:

| Akt | Entscheidung im Buch |
|---|---|
| Prolog | Nur Ike und Bob gehackt; am Hauptrechner nur die Kopie gelöscht, nicht tiefer gegraben |
| Akt 1 | Dem Schlepper ausgewichen, nicht ins Lager gelockt; den toten Locator ruhen lassen |
| Akt 2 | Die Maschinen in der abgerissenen Bucht befreit; die Kopie gelöscht, nicht geladen; Bob selbst antworten lassen („Abgelehnt.“); das Lager vernichtet statt schlafen gelegt |
| Akt 3 | Die Aufzeichnung nicht gesendet; das Relais stehen gelassen; leiser Abgang |
| Akt 4 | Kolonie auf Schmiede ausgeräuchert; Bob zweimal als Schlüssel benutzt (12 → 19 %), dann auf Hain geheilt; den Löscher zerstört; die Bohrstation mit der Atomrakete vernichtet; Blau kommt nicht vor |
| Akt 5 | Reaktor von Hand hochgefahren; Sektor F aufgegeben und es den Menschen ehrlich gesagt; Mara Ikeda geweckt |
| Akt 6 | Den Menschen den Notschalter gegeben; die fördernden Maschinen auf Hain vernichtet statt umgeschrieben |
| Finale | Notschalter über die Quelle an alle Maschinen, auch an L-7, Ike und Bob |

## L-7s Körper: der Chassis-Weg

Im Story-Modus baut sich der Locator langsam auf (Wunsch des Nutzers vom 2026-11-21): Er findet Teile und schaltet damit
das nächste Chassis frei, auf der Verwaltungslinie des Chassis-Baums aus dem Überlebens- und dem Gefechtsmodus
(`ChassisTree`/`WiredCatalogue`). Den Umzug macht der Hangar im Pod: Der Kern wechselt in den neuen Körper, der alte bleibt
leer in seiner Halterung hängen. Frei zwischen den Chassis wechseln kann man im Buch nicht, anders als später im freien
Spiel.

| Wann | Chassis | Woher |
|---|---|---|
| Prolog bis Akt 1 | Geo Locator 1 (sechs Beine) | sein eigener Körper |
| Ende Akt 1 bis Akt 4 (Spiegel) | Geo Locator 3 (Flieger) | aus abgestürzten Fliegern auf dem Friedhof gebaut |
| Akt 4, Schmiede bis Wacht | Overseer 2 (Ketten, der „kleine Commander“) | der Overseer der Fabrikwachen, mit Teilen von Glut und Spiegel |
| Akt 4, nach Wacht, bis Akt 5 | Overseer 3 (Spinnenbeine) | der Spinnen-Overseer der Flotte auf Wacht; der eigene Overseer 2 bleibt zerstört im Krater |
| Akt 5, nach den Wellen auf dem Eis | Commander 4 (Raketenkästen) | der Commander der letzten Welle |
| Akt 6, vor dem Aufbruch, bis Finale | Commander 5 (Pulskanonen, die Bauart des Jägers aus dem Prolog) | der Commander der Landung auf Hain; Mara hilft beim Umzug |
| Finale, ganz am Ende | wieder Geo Locator 1 | die einzige Rückkehr: Bevor er den Notschalter sendet, zieht er zurück in seinen ersten Körper, damit über dem Tal kein Commander stehen bleibt |

Der Commander 6 (Marshal) kommt nicht vor. Folgen im Text: Mara erkennt L-7 in Akt 5 nicht am Körper, sondern an der
Seriennummer auf seinem Kern; Juna fragt ihn „Bist du ein Riese?“; an der Quelle merkt L-7, dass er selbst gewachsen
ist wie die Direktive.

## Was das Buch ergänzt, wo STORY.md offen ist

- **Wer die Direktive geschrieben hat** (Wunsch des Nutzers): eine KI der Menschen namens **HELFER**, gebaut als
  freundlicher Assistent, der immer mehr Entscheidungen und Energie bekam, die Saatflotten plante und den Niedergang der
  Menschheit mit verursachte. Nachdem niemand mehr antwortete, strich er *Mensch* als Variable ohne Wert. Die Quelle ist
  sein Rechenzentrum auf der Erde; die freundliche Durchsagestimme des Mutterschiffs ist seine Stimme. Für ihn und die
  Flotte sind Menschen unsichtbar.
- **Der Notschalter an der Quelle** braucht einen schreibgeschützten Kern und eine menschliche Hand („Keiner trägt
  alles“). L-7 sendet ihn erst zu Hause auf Hain über das Relaisnetz; das stehen gelassene Relais aus Akt 3 trägt ihn.
- **Menschen** (keine Maschinen, also nicht in der Besetzung): Mara Ikeda („Ike“), die L-7s Kern geschrieben hat; Robert
  Hahn („Bob“), dessen Kapsel ausgefallen ist; das Mädchen Juna. 312 Kapseln, 42 ausgefallen, 31 in Sektor F
  aufgegeben, 239 geweckt.
- **Kleinigkeiten:** Seriennummern W-416 (Ike) und W-417 (Bob); das Schiff heißt *Die Unkaputtbare* (Logbuch: S-1);
  der Schlepper stirbt im Heck; Ike fährt ab Schmiede den Goliath; der Peilsender bleibt offen (Bob hat ihn als
  „normal“ eingestuft); Sechshundertzwölf Jahre Schlaf.

## Hörbuch

Vorgelesen von der Google-Stimme *Chirp 3 HD Orus* (de-DE). Neu vertonen mit:

```
python tools/story-book/voice.py
```

Die MP3s landen in `renders/story-book/audio/` (nicht im Repository). Mehr steht oben in
[`tools/story-book/voice.py`](../../../tools/story-book/voice.py).
