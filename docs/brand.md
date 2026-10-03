# Brand-Vorgaben für den Add-on Store

Quelle: Tungsten Automation Brand Book, Style Guide März 2025 (60 Seiten).
Gilt verbindlich für ALLES Sichtbare in diesem Projekt: Server-Web-UI
(Katalog ist zwar API-only, aber Login, Review-Queue, Admin-Seiten, E-Mails),
Store-Client (Dialoge, Panel, Icons), Developer-Kit-Doku und Paket-Icons.

## Farben

Primärpalette (Brand Book S. 18), trägt die Mehrheit jeder Fläche:

| Token | Hex | Verwendung |
|---|---|---|
| `--tun-navy` | `#002854` | Primäres Markenblau (Pantone 295); Überschriften, Buttons, Logo auf hell |
| `--tun-navy-deep` | `#004766` | Dunkle Abstufung |
| `--tun-blue` | `#00A0FB` | Akzentblau, Links, aktive Zustände |
| `--tun-blue-light` | `#9DDDF9` | Helle Abstufung |
| `--tun-gray-100` | `#FAFAFA` | Seitenhintergrund hell |
| `--tun-gray-150` | `#F0F0F0` | Flächen |
| `--tun-gray-200` | `#E4E4E4` | Rahmen, Trenner |
| `--tun-gray-blue` | `#D9DFE6` | Flächen mit Blaustich |
| `--tun-slate` | `#8094AA` | Sekundärtext |
| `--tun-black` | `#231F20` | Fließtext auf hell |

Domains (S. 19), als Hintergrund-Verläufe:
- Blue Domain: `#00123B → #002854 → #00A0FB` (dunkle Hero-/Sidebar-Flächen)
- Light Domain: `#FAFAFA → #E4E4E4` (helle Inhaltsflächen)

Akzentfarben (S. 20) sparsam, max. ~20 % der Fläche:
- Firmenweit: Grün `#00EB86` (hell `#B6FFDE`, dunkel `#016839`)
- Gelb `#FFC600` (CTA-Tauglich), Koralle `#FF6D69`, Magenta `#D030E8`

**Unsere Produktfamilie ist "PDF & eSignature"** (S. 15/21): deren Farbcode ist
`#002854` + `#00A0FB` + `#00EB86`, zugewiesener Zwei-Farb-Verlauf
**`#00EB86 → #00A0FB`**. Diesen Verlauf nutzen wir als Signatur-Akzent des
Stores (z. B. Hero-Leiste im Web-UI, Banner im Store-Panel).

## Typografie (S. 23-25)

- **Red Hat Display** in allen gebrandeten Anwendungen, statische Schnitte
  (nicht variable), Quelle Google Fonts (Lizenz: SIL OFL 1.1, für Web-UI
  selbst gehostet ausliefern, nicht von Google-CDN laden).
- **Fallback: Arial** (wichtig für native Dialoge im Store-Client: dort wird
  die Systemschrift des Hosts respektiert; Red Hat Display nur, wo wir eigene
  Flächen rendern und die Schrift mitbringen).
- Hierarchie: Titel/Buttons **Bold**, Untertitel **Medium**, Fließtext
  **Regular**; Größenverhältnis grob 100 % Titel, 60 % Untertitel, 30 % Text.
- Textfarbe auf hell: `#002854` für Überschriften, `#231F20` für Fließtext.

## Logo (S. 5-9)

- Auf dunklem Grund weiß, auf hellem Grund `#002854`. Nie verzerren, drehen,
  umfärben, keine Effekte.
- Schutzraum: Höhe des "N" rundum; Mindesthöhe Full Logo 50 px.
- Keine Mitführung fremder Logos außer nach Partner-Lockup-Regeln.

## Namensregeln (S. 5, 12)

- Erste Nennung "Tungsten Automation", danach "Tungsten".
- Produkte: erste Nennung "Tungsten Power PDF" (mit Marke), danach "Power PDF".
- Keine Akronyme in externen Materialien.

## CSS-Startblock für das Server-Web-UI

```css
:root {
  --tun-navy: #002854;
  --tun-navy-deep: #004766;
  --tun-ink: #231F20;
  --tun-blue: #00A0FB;
  --tun-blue-light: #9DDDF9;
  --tun-green: #00EB86;
  --tun-yellow: #FFC600;
  --tun-gray-100: #FAFAFA;
  --tun-gray-150: #F0F0F0;
  --tun-gray-200: #E4E4E4;
  --tun-gray-blue: #D9DFE6;
  --tun-slate: #8094AA;
  --tun-domain-blue: linear-gradient(135deg, #00123B 0%, #002854 55%, #00A0FB 100%);
  --tun-accent-pdf: linear-gradient(90deg, #00EB86 0%, #00A0FB 100%);
  font-family: "Red Hat Display", Arial, sans-serif;
}
```

## Übersetzung auf den Store-Client (natives MFC)

Power PDFs eigene Oberfläche dominiert; wir branden gezielt, nicht flächig:

- Panel-/Dialog-Kopfzeile: Navy `#002854` mit weißem Titel, darunter eine
  dünne Akzentlinie im PDF-Verlauf (`#00EB86 → #00A0FB`).
- Primäraktion ("Installieren"): Navy-Button, weiße Bold-Schrift; Fokus/Hover
  mit `#00A0FB`.
- Statusfarben: Erfolg `#00EB86`-Familie, Warnung `#FFC600`, Fehler `#FF6D69`.
- Icons im Ribbon/Panel nach den bekannten technischen Regeln (Panel 24x24
  32-bit mit Alpha, Ribbon 24-bit deckend) und farblich aus dieser Palette.
- Schrift in nativen Dialogen: System-/Hostschrift (Fallback-Regel), KEIN
  Einbetten von Red Hat Display in den Client.

## Prüfschritt

"Brand-Check" ist Teil der Definition of Done jedes UI-Inkrements (Server wie
Client) und Teil der Admin-Review-Checkliste für eingereichte Plug-ins
(Paket-Icon und Katalogtexte folgen den Namensregeln).
