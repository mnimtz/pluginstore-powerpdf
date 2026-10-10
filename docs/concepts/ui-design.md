# Konzept: Einheitliches UI-Design für unsere Plug-ins

Stand: Okt 10, 2026 · Status: Etappe E1 gebaut (Vorlage 1.0.0 in `C:\Claude\PluginUiKit`), E2 bis E6 offen

## Ziel

Alle aktiven Plug-ins von Marcus bekommen **ein gemeinsames Aussehen**. Grundlage
ist das, was sich bewährt hat: der Look von Smart Compare und dem Add-on Store.
Er bleibt im Kern erhalten und wird an das Tungsten Automation Brand Book
(Style Guide März 2025) angeglichen. Daraus entsteht eine verbindliche
**UI-Vorlage**: feste Farben, Schriften, Größen und Bausteine, die jedes
Plug-in gleich verwendet.

Kurz: gleicher Look wie heute, nur brandkonform und überall gleich.

## Geltungsbereich

Aktive Plug-ins im Store mit Marcus als Besitzer (Stand Okt 10, 2026):

| Plug-in | Quelle | Oberfläche heute | Ziel |
|---|---|---|---|
| Add-on Store (Client) | PluginStore-PowerPDF | WebView2, Store-Look | auf die Vorlage umstellen |
| Senden an | SendTo | WebView2-Seitenpanel, Store-Look | auf die Vorlage umstellen |
| KI-Dienste | AiCore | WebView2-Fenster, Store-Look | auf die Vorlage umstellen; seine Fensterhülle wird Teil der Vorlage |
| Smart Compare (vor der ersten Veröffentlichung) | SmartCompare | WebView2-Fenster, eigener Look | Referenz für die Vorlage, als Erstes umgestellt |
| OneClick Sign | Feature Pack 1 | klassische Dialoge | umziehen |
| QES Sign | Feature Pack 1 | klassische Dialoge | umziehen |
| Commerzbank Sign (privat) | Feature Pack 1 | klassische Dialoge | umziehen, mit Rücksicht auf „wenig Klicks“ |
| Smart Bookmarks | Feature Pack 1 | klassische Dialoge, Navigationspanel | umziehen |
| Serienbrief (Mail Merge) | Feature Pack 1 | klassische Dialoge | umziehen |
| E-Rechnung | Feature Pack 1 | klassische Dialoge | umziehen |
| Compliance Check | Feature Pack 1 | klassische Dialoge | umziehen |
| Barcode-Stempel | Feature Pack 1 | Stempelpalette von Power PDF, eigene Dialoge | eigene Dialoge umziehen |
| Stempel-Assistent | Feature Pack 2 | klassische Dialoge | umziehen |
| Printix SecurePrint | Feature Pack 2 | klassische Dialoge | umziehen |

Nicht dabei: XFA-Konverter (zurückgezogen) und Office Konverter (nicht im
Store). Die Quellen von Feature Pack 1 und 2 enthalten zusammen rund 32
klassische Dialoge, nicht alle davon gehören zu Store-Plug-ins; die genaue
Liste je Plug-in entsteht in der jeweiligen Etappe.

**Bleibt wie es ist:** alles, was Power PDF selbst zeichnet. Das sind der
Signatur-Dialog, die Stempelpalette, das Menüband und die Seiten unter
*Datei, Optionen*. Gestaltet wird nur, was unsere Plug-ins selbst anzeigen
(Regel: nichts nachbauen, was Power PDF schon kann).

## Was vom heutigen Look bleibt

Diese Merkmale aus Smart Compare und dem Store-Fenster sind schon nah am Brand
Book und werden zur Norm:

- Kopfzeile in Navy `#002854` mit weißem Titel, darunter die 3 px hohe
  Akzentlinie im Power-PDF-Verlauf `#00EB86 → #00A0FB`
- Karten mit 1 px Rahmen und 10 px Eckenradius auf weißem Grund
- Primäre Schaltfläche in Navy, sekundäre weiß mit Rahmen, dazu Ghost-, kleine
  und große Variante, 8 px Eckenradius
- Segment-Schalter, Kippschalter, Filter-Chips, Pillen und Badges
- Bereichsnavigation links, lange Listen seitenweise (25/50/100)
- Banner für Info, Erfolg, Warnung und Fehler; Hinweisbox; Leerzustand
- Fortschritt als Overlay mit Verlaufsbalken, Ladekreisel
- Strich-Icons im SVG-Format, runde Linienenden
- Dialoge in der Seite statt Browser-Popups
- 21 Sprachen, Arabisch von rechts nach links

## Was sich durch das Brand Book ändert

Alle vier WebView2-Oberflächen nutzen heute dieselben neun Grundfarben, die
vom Brand Book abweichen; die Statusfarben unterscheiden sich sogar je
Plug-in. Das wird so vereinheitlicht:

| Element | Heute | Brand Book | Künftig |
|---|---|---|---|
| Schrift | Segoe UI | Red Hat Display, Ersatz Arial (S. 23) | Red Hat Display, Ersatz Arial (siehe Schrift) |
| Fließtext | `#1f2a37` | `#231F20` (S. 18) | `#231F20` |
| Überschriften | `#002854` | `#002854` (S. 25) | unverändert |
| Sekundärtext | `#6b7a8c` | Slate `#8094AA` | `#5B6B80` (aus Slate abgedunkelt, siehe Lesbarkeit) |
| Linien, Rahmen | `#e1e7ee` | `#E4E4E4` | `#E4E4E4` |
| Flächen | `#f2f6fa` | `#F0F0F0`, Seite `#FAFAFA`, blaustichig `#D9DFE6` | diese drei |
| Hover der Navy-Schaltfläche | `#00366f` / `#0a3a6b` | Navy tief `#004766` | `#004766` |
| Links | `#00A0FB` | Akzentblau `#00A0FB` | Linktext `#006FB5`; `#00A0FB` für Fokusrahmen, aktive Marker, Icon-Akzente, Fortschritt |
| Erfolg | je Plug-in verschieden | Grün `#00EB86`, dunkel `#016839`, hell `#B6FFDE` (S. 20) | Text `#016839` auf `#B6FFDE` |
| Warnung | je Plug-in verschieden | Gelb `#FFC600` | Text `#7A5A00` auf `#FFF4CC`, Marker `#FFC600` |
| Fehler | je Plug-in verschieden | Koralle `#FF6D69` | Text `#B3261E` auf `#FFE1E0`, Marker `#FF6D69` |
| Info | `#E8F6FE` | (keine Vorgabe) | Text Navy auf `#E8F6FE` |
| Schaltflächen-Text | normal | Bold, Satzschreibung (S. 24, 27) | Primär Bold, Satzschreibung |
| Icons | einfarbig | Linien-Icons zweifarbig `#231F20` + `#00A0FB`, auf dunkel `#FFFFFF` + `#00A0FB` (S. 57/58) | Grundfarbe plus Akzentfarbe je Icon |

Akzentfarben (Gelb, Koralle, Magenta, Grün) bleiben nach Brand Book sparsam,
höchstens etwa 20 % der Fläche. Der Verlauf `#00EB86 → #00A0FB` ist das
Erkennungszeichen der Produktfamilie PDF & eSignature und erscheint genau an
zwei Stellen: in der Akzentlinie unter der Kopfzeile und im Fortschrittsbalken.

### Lesbarkeit

Zwei Brand-Farben sind als Textfarbe auf Weiß zu schwach (gemessen nach WCAG,
gut lesbar ab 4,5:1):

| Farbe | Kontrast auf Weiß | Verwendung |
|---|---|---|
| Akzentblau `#00A0FB` | 2,8:1 | nur Flächen, Linien, Icons, Fokus |
| Slate `#8094AA` | 3,1:1 | nur Dekor, deaktivierte Elemente |
| Linkblau `#006FB5` (neu, aus dem Akzentblau abgedunkelt) | 5,3:1 | Linktext |
| Sekundärtext `#5B6B80` (neu, aus Slate abgedunkelt) | 5,4:1 | Untertitel, Metadaten |
| Fehlertext `#B3261E` | 6,5:1 | Fehlermeldungen |
| Erfolgstext `#016839` (Brand, dunkles Grün) | 6,9:1 | Erfolgsmeldungen |
| Warntext `#7A5A00` (neu, aus Gelb abgedunkelt) | 6,4:1 | Warnungen |

Die neuen Töne sind Abstufungen der Brand-Farben, keine fremden Farben.

### Fachfarben von Smart Compare

Smart Compare braucht eigene Farben für die Änderungsarten (eingefügt,
gelöscht, ersetzt, verschoben, Format, Grafik, Seite). Sie bleiben als
**Fachfarben** erlaubt, werden aber an die Palette angelehnt: eingefügt
Grün `#016839`, gelöscht `#B3261E`, ersetzt `#006FB5`, verschoben aus Magenta
`#D030E8` abgedunkelt, Format `#7A5A00`, Seite Navy. Andere Plug-ins mit
Fachfarben (zum Beispiel Status in Compliance Check) folgen derselben Regel.

## Schrift

Das Brand Book verlangt Red Hat Display (statische Schnitte) mit Arial als
Ersatz. Die Web-Typografie (S. 25) legt Rollen und Schnitte fest, nennt aber
**keine Größen**. Für unsere dichten Desktop-Fenster gilt deshalb diese
Skala (aus Smart Compare übernommen und gerundet):

| Rolle (Brand Book) | Bei uns | Größe | Schnitt | Farbe |
|---|---|---|---|---|
| Header 1 | Fenstertitel in der Kopfzeile | 17 px | Bold | Weiß |
| Header 2 | Seitenüberschrift | 16 px | Bold | Navy |
| Header 3 | Kartenüberschrift, Formularkopf | 14 px | Bold | Navy |
| Header 4 | Unterüberschrift, große Schaltfläche | 13 px | Bold | Navy |
| Header 5 | Feldbeschriftung, Navigation, Standard-Schaltfläche | 13 px | Regular (Schaltflächen: Bold, siehe offene Fragen) | Navy |
| Paragraph | Fließtext, Formularfehler | 13 px | Regular | `#231F20` |
| Header 6 | Kleintext, Bildunterschrift, Fußnote | 11,5 px | Regular | `#5B6B80` |

Sprachen ohne lateinische Schrift: Red Hat Display hat keine chinesischen,
japanischen, koreanischen oder arabischen Zeichen. Dort greift je Sprache die
Systemschrift (Microsoft YaHei UI, Microsoft JhengHei UI, Yu Gothic UI,
Malgun Gothic, für Arabisch Segoe UI), die Farben und Größen bleiben gleich.

**Mitliefern oder nicht:** Das Store-Portal fordert Red Hat Display heute nur
über den Namen an und liefert keine Schriftdatei mit; ohne installierte
Schrift erscheint Arial. Die Plug-ins würden es zunächst genauso machen.
Die Schrift einzubetten (sie steht unter der SIL OFL 1.1) wäre neu und
braucht eine Freigabe, siehe offene Fragen.

## Bausteine der Vorlage

| Baustein | Inhalt |
|---|---|
| Kopfzeile | Navy, Titel, optionale Unterzeile, Aktionen rechts, Akzentlinie |
| Bereichsnavigation | links, aktive Seite Navy hinterlegt |
| Karte | Rahmen, Radius 10 px, Überschrift Header 3 |
| Schaltflächen | primär, sekundär, Ghost, klein, groß, deaktiviert; Satzschreibung |
| Formular | Textfeld, Auswahl, Textbereich, Kippschalter, Segment, Fokus im Akzentblau |
| Listen | Filter-Chips, Suchfeld, Einträge mit Statusleiste links, Seitenumschaltung 25/50/100 |
| Tabelle | Kopf in Navy-Schrift auf `#F0F0F0`, Zeilen mit Linie `#E4E4E4` |
| Meldungen | Banner (Info, Erfolg, Warnung, Fehler), Hinweisbox, Toast, Leerzustand |
| Dialoge | Bestätigung und Eingabe in der Seite, Abbrechen ist vorausgewählt |
| Fortschritt | Overlay mit Schritten, Verlaufsbalken, Ladekreisel |
| Menüs | Aufklappmenü, Kontextmenü |
| Icons | Satz von Strich-Icons, zweifarbig nach Brand Book |

Fensterform als Regel: **Seitenpanel** für Begleitfunktionen, die neben dem
Dokument offen bleiben (wie Senden an); **eigenes Fenster** für
Arbeitsabläufe (wie Smart Compare und der Store); kleine Rückfragen als
**einheitlicher kleiner Dialog** der Vorlage.

## Technik

Die Vorlage wird ein eigener Baustein („Plug-in UI-Kit“), den jedes Plug-in in
einer festen Version einbindet:

- `tpui.css`: alle Farben und Größen als Variablen, dazu die Bausteine. Ein
  Plug-in schreibt keine eigenen Farbwerte, nur Variablen der Vorlage.
- `tpui.js`: Dialoge, Listen mit Seitenumschaltung, Sprachumschaltung (21
  Sprachen, RTL), Nachrichtenbrücke zur C++-Seite.
- C++-Fensterhülle für WebView2 (aus `AiCore/plugin/src/webwindow.*`
  herausgelöst), als Fenster oder Seitenpanel, Inhalte aus den Ressourcen des
  Plug-ins.
- Klassischer Ersatzdialog in denselben Farben (Navy-Kopf, Akzentlinie,
  Systemschrift), falls WebView2 fehlt.
- Vorschau im Browser mit Testdaten, wie heute schon für Store und Senden an.
- Prüfskript: keine Farbwerte außerhalb der Vorlage, Kontrast mindestens
  4,5:1 für Text, alle 21 Sprachen vorhanden, keine Browser-Popups.
- x64 und ARM64, eigener Code unter MIT, WebView2-SDK unter BSD-3-Clause.

Die Vorlage bekommt eine Musterseite mit allen Bausteinen und eine kurze
Gestaltungsregel; beides wird später auch im Devkit für fremde Entwickler
angeboten (Idee „Store-UI-Regel“).

## Etappen

Jede Umstellung ist ein eigenes Release mit neuer Version, Änderungstext für
Anwender und Test in Power PDF.

| Etappe | Inhalt |
|---|---|
| E1 | Vorlage v1: Farben, Schrift, Größen, Bausteine, Fensterhülle, Ersatzdialog, Vorschau, Prüfskript, Musterseite. **Gebaut Okt 10, 2026 (1.0.0)** |
| E2 | Smart Compare auf die Vorlage (vor seiner ersten Veröffentlichung), danach der Store-Client |
| E3 | Senden an und KI-Dienste |
| E4 | Feature Pack 1: OneClick Sign, QES Sign, Commerzbank Sign, Smart Bookmarks, Serienbrief, E-Rechnung, Compliance Check, Barcode-Stempel (Bestandsaufnahme der Dialoge, dann Umzug je Plug-in) |
| E5 | Feature Pack 2: Stempel-Assistent, Printix SecurePrint |
| E6 | optional: Server-Portal auf dieselben Variablen (nutzt die Brand-Farben schon weitgehend) |

## Prüfschritt

Der Brand-Check gehört zur Abnahme jeder Etappe: Musterseite und echte
Fenster nebeneinander, Prüfskript grün, Bildschirmfotos in Deutsch, Englisch
und Arabisch.

## Offene Fragen zur Entscheidung

1. **Schrift mitliefern?** Red Hat Display nur über den Namen anfordern wie
   im Portal (kein Lizenzthema, ohne installierte Schrift Arial), oder die
   Schriftdatei einbetten (SIL OFL 1.1, liegt außerhalb der Dauerregel
   „nur MIT, BSD, Apache“ und bräuchte deine Freigabe)?
2. **Ablage der Vorlage:** eigenes Repo `C:\Claude\PluginUiKit` (Vorschlag,
   weil die Plug-ins in getrennten Repos liegen) oder ein Ordner im Store-Repo?
3. **Schaltflächen-Schnitt:** Das Brand Book nennt für Schaltflächen einmal
   Bold (S. 24) und für Standard-Schaltflächen Regular (S. 25). Vorschlag:
   alle Schaltflächen Bold.
4. **Englische Überschriften:** Das Brand Book will Überschriften „initial
   capped“ (S. 27), also jedes Wort groß. Für alle englischen Texte
   übernehmen oder bei Satzschreibung bleiben?
5. **Dunkles Design:** jetzt mit vorsehen oder später?
6. ~~Spool View~~ **entschieden:** gehört nicht dazu (nur für einen Kollegen
   hochgeladen).

## Stand der Umsetzung

**E1 gebaut (Okt 10, 2026), Vorlage 1.0.0** in `C:\Claude\PluginUiKit` (eigenes
Repo, lokal; Sicherung `C:\Claude\Backup\pluginuikit-2026-10-10.bundle`).
Für die noch offenen Fragen 1 bis 5 gelten bis zur Entscheidung die Vorschläge:
Schrift nur über den Namen, eigenes Repo, alle Schaltflächen fett, englische
Texte unverändert, dunkles Design nur vorbereitet.

- `web/tpui.css`, `web/tpui.js`, `web/tpui-icons.js` (41 Icons im Brand-Stil),
  Kompatibilitäts-Variablen, damit bestehende Seiten sofort die Brand-Farben
  bekommen
- `native/`: WebView2-Hülle für Fenster und Panel, modales Fenster (aus AiCore
  gelöst), klassische Zeichenhilfen
- `tools/inline.py`, `tools/check_ui.py`; Musterseite `demo/muster.html`
- Tests: nativer Test mit echtem WebView2 (Nachrichten in beide Richtungen,
  Unicode, Hintergrund-Thread) grün; 13 Werkzeugtests grün; kompiliert mit
  Warnstufe 4 ohne Warnung
- Ausgangslage für E2/E3 laut Prüfskript (eigene Farbwerte): Smart Compare 55,
  Store-Fenster 37, Senden an 19, KI-Dienste 19
