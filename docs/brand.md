# Brand-Vorgaben für den Add-on Store

Quelle: **Tungsten Automation Brand Book, November 2026** (40 Seiten), dazu die
offizielle Vorlage `Tungsten_2027_Template.potx` (Theme „Tungsten Automation 2026“).
Es ersetzt den Style Guide vom März 2025. Gilt verbindlich für alles Sichtbare in
diesem Projekt: Server-Web-UI, E-Mails, Store-Client (Fenster, Dialoge, Icons),
Developer-Kit-Doku und Paket-Icons.

**Nur Originale:** Logos, Lockups, Icons, Schriften und Flow lines kommen aus dem
Brand-Ordner (Logos & Lockups, Icons, Fonts, PowerPoint-Vorlage). Nichts wird
nachgezeichnet, aus PDFs ausgeschnitten oder umgefärbt.

## Farben

Die Palette 2026 ist gegenüber 2025 unverändert. Werte aus dem Theme der Vorlage:

| Rolle im Theme | Hex | Token | Verwendung |
|---|---|---|---|
| dk2 | `#002854` | `--tun-navy` | Logo auf hell, Buttons, Navy-Flächen, Unterüberschriften |
| accent1 | `#00A0FB` | `--tun-blue` | Überschriften, Akzente, Callout-Linie |
| dk1 | `#231F20` | `--tun-ink` | Fließtext |
| lt2 | `#FAFAFA` | `--tun-gray-100` | Kacheln, Karten |
| accent3-6 | `#D030E8`, `#FF6D69`, `#FFC600`, `#00EB86` | `--tun-magenta` usw. | sparsam, Statusfarben |

Abstufungen werden nur aus diesen Farben gemischt (in `site.css` als Hexwert, mit
Mischverhältnis im Kommentar). Wichtig sind vor allem die Kontrastwerte:

| Token | Hex | Mischung | Zweck |
|---|---|---|---|
| `--tun-blue-ink` | `#0072BC` | Blau 62 % + Navy | Links und blauer Text auf Weiß, Kontrast 5:1 |
| `--tun-slate` | `#476484` | Navy 72 % + Weiß | Nebentext, Kontrast 6:1 (statt `#8094AA`, 3,2:1) |
| `--tun-tint` | `#E8F6FF` | Blau 9 % + Weiß | Callouts |
| `--tun-gray-200` | `#E0E5EA` | Navy 12 % + Weiß | Linien, Rahmen |
| `--tun-on-navy` | `#BFE7FE` | Weiß 75 % + Blau | Nebentext auf Navy |

**Statustexte (S2.0.0):** nie die hellen Akzente als Schrift auf Weiß. Erfolg
`--tun-ok` `#016839` (6,9:1), Fehler `--tun-err` `#B3261E` (6,5:1), Nebentext
`#476484`. Auf Navy bleiben die hellen Akzente lesbar.

**Kontrast:** Weiße Schrift auf `#00A0FB` erreicht nur 2,8:1. Der helle Hauptknopf
(`.btn.cta`) trägt deshalb navyfarbene Schrift. Überschriften in `#00A0FB` sind
groß und dünn, wie das Brand Book es verlangt (S. 22). Ihr Kontrast auf Weiß
(2,8:1) liegt unter der WCAG-Schwelle für großen Text (3:1). Bewusste
Brand-Entscheidung; die Inhalte darunter tragen die Information.

**Produktfamilie „PDF & eSignature“** (Power PDF): Verlauf `#00EB86 → #00A0FB`, im
Store nur noch für Highlights und Fortschrittsbalken.

## Typografie (S. 20, 22)

- **Red Hat Display**, vom Store selbst ausgeliefert (`wwwroot/fonts`, Light,
  Regular, Medium, Bold; SIL OFL 1.1, `OFL.txt` daneben und im Notices-File).
  Fallback Arial.
- Überschriften **Light** in `#00A0FB`. Ist das auf dem Grund nicht lesbar, dann
  Navy; auf Navy weiß.
- Unterüberschriften **Regular** Navy, Fließtext **Regular**, Callouts **Medium**.
  **Bold** nur zum Hervorheben einzelner Wörter, Zahlen oder Kennwerte.
- Überschriften und Buttons in **Satzschreibung**, nie alles groß oder jedes Wort groß.

## Logo (S. 13, 14)

- Logo 2026 (`tungsten-automation-logo-2026-blue.svg` / `-white.svg`): auf hell
  `#002854`, auf dunkel weiß. Mindesthöhe 50 px; Schutzraum = Höhe des „N“.
- **Don'ts:** das alte Logo mit Kasten hinter „AUTOMATION“, linksbündig, andere
  Schrift oder Farbe, verzerrt oder gedreht, mit Kontur oder Effekten, ohne
  „Automation“, mit „Formerly Kofax“.
- Power-PDF-Lockup (`tungsten-power-pdf-lockup-reverse.svg`) nur für das Produkt
  Power PDF. **Add-ons unabhängiger Autoren bekommen nie ein Tungsten-Lockup**,
  sonst wirken sie wie offizielle Produkte.
- **App-Symbol (S2.0.0):** `tungsten-power-pdf-icon.svg` ist die Original-Datei
  `Tungsten-Power-PDF.svg` mit nur einem geänderten `viewBox` (Ausschnitt auf das
  Symbol). Daraus entstehen `favicon.ico` (16/32/48), `favicon-32.png`,
  `apple-touch-icon.png` und `icon-192/256/512.png` (weißer Grund, 8 % Rand).
  Kein Symbol aus `PowerPDF.exe`, nichts nachgezeichnet. Der Store-Client zeigt
  dasselbe Symbol bei einem Power-PDF-Update (`client/ui/img`).

## Flächen, Callouts, Flow lines (S. 3, 22, 26)

- Viel Weißraum. Flache Kacheln in `#FAFAFA`, Navy-Blöcke nur zur Betonung (Bühne,
  Fußzeile).
- Callout: hellblau hinterlegt, Strich auf der Startseite in `#00A0FB`, Medium.
  Hinweise (`.alert`) folgen derselben Form in den Statusfarben.
- Flow lines (`flowlines-title.svg` aus dem Titel-Layout der Vorlage, Original
  `ppt/media/image1.svg`): nur als Akzent im freien Raum, **nie hinter Text**; auf
  schmalen Bildschirmen weggelassen.

## Icons (S. 23)

Icon-Bibliothek, 230 Stück, Light Colorway (`#00A0FB` + `#002854`) auf hell, Dark
Colorway auf Navy. Im Katalog je bekannter Kategorie ein Icon (`wwwroot/img/icons`).

## Namensregeln (S. 13, 15)

- Erste Nennung „Tungsten Automation“, danach „Tungsten“.
- Produkte: erste Nennung „Tungsten Power PDF“, danach „Power PDF“. Keine Akronyme.

## Übertragung auf Store-Client und Plug-ins (Phasen 2 und 3)

Phase 2 erledigt mit C1.9.18 (Nov 19, 2026): Store-Fenster auf Plug-in UI 1.1.0, Logo 2026 und
Red Hat Display eingebettet (`font-src data:`, `img-src data:` in der CSP der Seite). C2.0.0:
Setup-Banner weiß mit dem Original-Logo 2026 (PNG aus dem Brand-Paket, nur skaliert, rechts),
Titel navy; Update-Ansicht mit dem Power-PDF-Symbol. Phase 3: jedes
Plug-in übernimmt Kit 1.1.0 mit seinem nächsten Release.

- Store-Fenster und Plug-in UI-Kit übernehmen dieselben Tokens, Schriftschnitte
  und Komponenten (Kopf weiß mit Logo 2026, Light-Überschriften, Callouts, flache
  Kacheln).
- Native MFC-Dialoge: Systemschrift, nur Farbakzente. Icons nach den technischen
  Regeln: Panel 24x24 32-bit mit Alpha, Ribbon 24-bit deckend.

## Prüfschritt

„Brand-Check“ gehört zur Definition of Done jedes UI-Inkrements (Server wie Client)
und zur Admin-Review-Checkliste für eingereichte Plug-ins: Paket-Icon,
Katalogtexte nach den Namensregeln, kein Tungsten-Lockup bei fremden Add-ons.
