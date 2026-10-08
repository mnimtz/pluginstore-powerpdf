# Konzept: Power-PDF-Updates über den Add-on Store

Stand: Okt 8, 2026 · Status: Etappe 1 umgesetzt (S1.8.0, Client 1.6.0); Etappe 2 offen

## Ziel

Der Add-on Store weiß, welche Power-PDF-Version auf einem Rechner läuft. Er
soll erkennen, dass Tungsten ein neueres Update für genau diese Version
veröffentlicht hat, den Anwender darauf hinweisen und, wenn ein Admin das
Update-Paket bereitgestellt hat, das Update auf Wunsch herunterladen und
installieren: Power PDF beenden, Update starten, Power PDF neu starten.

Der Store ist nur für **Power PDF Business (SaaS-Edition)** gedacht. Es gibt
also genau eine Produktlinie, aber mehrere **Release-Linien** nebeneinander
(z. B. 2025.3 für Bestandskunden und 2026.4 als neue Hauptversion).

**Mehrwert:** In der Business-Edition sind automatische Produkt-Updates laut
Network Installation Guide 2025.3 abgeschaltet. Anwender und kleinere
Kunden ohne Softwareverteilung erfahren heute nur zufällig von neuen Updates.

## Begriffe

| Begriff | Bedeutung |
|---|---|
| Release-Linie | Eine Hauptversion mit ihren Updates, erkannt am Anfang von `VersionLong` (z. B. `2025.3`). |
| Update | Kumulatives Update einer Linie, z. B. 2025.3 Update 9 = `2025.3.9`. Enthält alle früheren Updates der Linie. |
| Hauptversion | Neue Linie, z. B. 2026.4. Wird nie automatisch installiert. |
| Update-Paket | Das offizielle ZIP von Tungsten mit dem MSI (z. B. `TungstenPowerPDFBusiness-2025.3.9.msi`). |
| Überwachte Adresse | Eine Webseite, die der Server täglich abruft (DocShield-Portal einer Linie, Doku-Übersicht). |

## Ausgangslage (geprüfte Fakten)

- Die DocShield-Portalseite einer Version verlinkt immer die neueste ReadMe,
  z. B. `ReadMe-TungstenPowerPDFBusiness-2025.3.9.htm` (Update 9, Build vom
  Sep 20, 2026).
- Jede Installation zeigt in der Registry selbst auf diese Portalseite:
  `HKLM\SOFTWARE\Kofax\PDF\V1\DocURL`. Dort stehen auch `ProductName`
  („Tungsten Power PDF Business"), `VersionLong` (z. B. `2025.3.8.0.26414`)
  und `ProcessorArchitecture`.
- Die Portaladresse enthält je Version eine eigene Kennung
  (`2025.3-jlrwz2ja2j`); sie lässt sich nicht berechnen.
- Laut ReadMe: ZIP entpacken, MSI starten; Power PDF darf nicht laufen; ggf.
  Windows neu starten; ein Update lässt sich nicht deinstallieren oder
  zurücknehmen. Das MSI nimmt Parameter auf der Kommandozeile an (Beispiel
  in der ReadMe: `TRANSFORMS=…`).
- Jedes Update bringt ein Zertifikat für die Office-Add-ins mit, das bei der
  Office-Einstellung „nur vertrauenswürdige Herausgeber" in den
  Zertifikatsspeicher gehört.
- Add-ons können schon heute eine Mindestversion verlangen
  (`minPowerPdfVersion`, z. B. 2026.4); der Client zeigt dann „braucht
  Power PDF 2026.4".

## Grundsatz: erst einschalten

Die ganze Funktion ist **standardmäßig aus**, an zwei Stellen:

- **Server:** Einstellungen, Bereich „Power-PDF-Updates", Hauptschalter
  „Power-PDF-Updates verwalten" (aus/an). Solange er aus ist, ruft der Server
  nichts ab, zeigt keine Aufgaben und meldet den Clients nichts.
- **Client:** Optionen, Add-on Store, „Auf Power-PDF-Updates hinweisen"
  (aus/an). Solange er aus ist, fragt der Client nicht nach und zeigt nichts.

Erst wenn beide an sind, erscheint ein Hinweis. Die Installation über den
Store (Etappe 2) ist ein eigener, zusätzlicher Schalter an beiden Stellen.

## Release-Linien (Server)

Admins pflegen die Linien im Portal unter Einstellungen, „Power-PDF-Updates"
(sichtbar, sobald der Hauptschalter an ist):

| Feld | Beispiel | Hinweis |
|---|---|---|
| Linie | `2025.3` | Präfix von `VersionLong` |
| Bezeichnung | „Power PDF 2025.3" | für Anzeigen |
| Überwachte Adresse | DocShield-Portal 2025.3 | je Linie eingetragen |
| Erkennungsmuster | `ReadMe-TungstenPowerPDFBusiness-(2025\.3\.\d+)\.htm` | vorbelegt aus der Linie, änderbar |
| Zuletzt erkannt | 2025.3.9, ReadMe-Link, Build-Datum | vom Server gesetzt |
| Pakete | 2025.3.9 (Beta oder Live) | hochgeladene Update-ZIPs |
| Status | gepflegt · nur Sicherheitsupdates · eingestellt | eingestellt: keine Hinweise mehr |
| Supportende (optional) | Datum | Hinweis an Kunden vorher |
| Hinweis auf neuere Hauptversion | aus (Standard) / an | nur Text und Link |

Linien laufen völlig unabhängig: 2025.3.10 und 2026.4.3 können gleichzeitig
gültig sein. Ein Rechner bekommt nur Updates seiner eigenen Linie.

## Ablauf

### 1. Erkennen (täglich, ohne KI)

- Der Server ruft je Linie die überwachte Adresse ab und sucht das
  Erkennungsmuster. Neue Nummer gefunden: „Update erkannt".
- Zusätzlich beobachtet er die Doku-Übersicht. Taucht dort eine neue
  Hauptversion auf (z. B. „Power PDF 2026.4"), schlägt er eine neue Linie mit
  Adresse und Muster vor; ein Admin bestätigt.
- Findet der Server das Muster nicht mehr (Seite umgebaut), meldet er das,
  statt still nichts zu tun.
- Optional: dieselbe Überwachung für neue Plug-in-SDK-Versionen, als
  Hinweis an Entwickler.

### 2. Aufgabe für Admins

- Kachel auf der Startseite und im Dashboard: „Neues Power-PDF-Update
  erkannt: 2025.3.9 (Build Sep 20, 2026). Update-Paket hochladen".
- E-Mail an Admins (abschaltbar wie die übrigen Benachrichtigungen).
- Der Hinweis an die Clients (Stufe 1) ist ab hier schon aktiv, nur mit
  Link zur offiziellen ReadMe.

### 3. Paket bereitstellen (Stufe 2)

- Speicher: der schon eingerichtete Azure Blob Storage der Cloud-Sicherung,
  eigener Container (z. B. `powerpdf-updates`), damit die Aufräumregel der
  Sicherungen nie ein Paket trifft. Je Linie bleiben die letzten zwei bis
  drei Pakete, ältere löscht der Server.
- Ein Admin lädt das Update-ZIP hoch, direkt in den Blob über eine
  kurzlebige, nur für diese Datei gültige Schreib-SAS (umgeht das
  Upload-Limit von 220 MB und Zeitüberschreitungen).
- Der Server prüft danach SHA-256, Dateiname, Version (passt zur Linie und
  zur erkannten ReadMe) und Größe. Die Authenticode-Signatur von Tungsten
  Automation prüft verbindlich der Client unter Windows vor dem Start (der
  Server läuft als Linux-Container).
- Clients laden über den Store-Server (durchgereicht), damit sie weiter nur
  mit einem Host über HTTPS 443 sprechen.
- Sicherung: Linien, Pakete und Prüfsummen sind in der Sicherung, die
  Paketdateien nicht (sie liegen im Blob und sind bei Tungsten erneut
  erhältlich). Nach einer Wiederherstellung zeigt der Server fehlende Pakete
  an.
- Texte: Ohne KI erscheint der ReadMe-Link. Mit KI (optional, wie der
  Prüfassistent) schlägt der Server eine kurze Zusammenfassung für
  Endanwender in allen 21 Sprachen vor und hebt Sicherheitskorrekturen
  hervor („Sicherheitsupdate"). Der Admin passt an und bestätigt.
- Freigabe wie bei Add-ons: erst **für Beta** (Rechner mit Beta-Option),
  dann **für Live**.

### 4. Hinweis im Client

- Der Client liest `VersionLong`, ordnet sich seiner Linie zu und fragt den
  Server, ob es dort ein neueres Update gibt.
- Store-Fenster: Banner „Für Power PDF gibt es Update 9" mit „Was ist neu"
  und, wenn ein freigegebenes Paket vorliegt, „Jetzt installieren".
  Optional ein Punkt am Store-Knopf wie bei Add-on-Updates.
- Hauptversion: nur ein Hinweis mit Link, und nur wenn die Linie das
  erlaubt. Ein Versionssprung kann Lizenz, IT-Freigabe und Schulung
  betreffen; das entscheidet der Kunde.

### 5. Installation (Stufe 2, auf Wunsch)

1. Download des Pakets, Prüfung von SHA-256, Katalog-Signatur und
   Tungsten-Signatur des MSI. Ein anderer Signierer wird nie ausgeführt.
2. Ein Helfer außerhalb von Power PDF bittet einmal um Admin-Rechte (wie
   heute bei Add-on-Installationen).
3. Rückfrage, dann Power PDF beenden. Prüfen, ob Word, Outlook oder andere
   Programme mit Power-PDF-Add-ins laufen; den Anwender bitten, sie zu
   schließen.
4. `msiexec /i <msi> /passive /norestart /l*v <Protokoll>` (Fortschritt
   sichtbar). Später, nach Tests, optional `/qn` (still).
5. Ergebnis auswerten: Erfolg, Neustart von Windows nötig, oder Fehler mit
   Protokoll.
6. Power PDF neu starten. Der Store prüft seine Ribbon-Einträge und
   repariert sie, falls das Update die Vorlage in Program Files überschrieben
   hat.

## Einstellungen und Richtlinien

| Ort | Einstellung | Standard |
|---|---|---|
| Server, Einstellungen, Power-PDF-Updates | „Power-PDF-Updates verwalten" (Hauptschalter) | aus |
| Server, Einstellungen, Power-PDF-Updates | „Installation über den Store anbieten" (Etappe 2) | aus |
| Optionen, Add-on Store | „Auf Power-PDF-Updates hinweisen" | aus |
| Optionen, Add-on Store | „Updates über den Store installieren" | aus |
| HKLM-Richtlinie `PowerPdfUpdates` | 0 = aus, 1 = nur Hinweis, 2 = Hinweis und Installation | nicht gesetzt |
| HKLM-Richtlinie `PowerPdfUpdateSilent` | 1 = still installieren | nicht gesetzt |

Wo die IT per SCCM oder Intune verteilt, setzt sie `PowerPdfUpdates = 0`
oder `1`. Die Richtlinie schlägt die Benutzereinstellung.

## Sicherheit

- Abruf nur über HTTPS, nur von freigegebenen Hosts (zunächst
  `docshield.tungstenautomation.com`), mit Größen- und Zeitlimit und ohne
  Weiterleitung auf andere Hosts. Der Server ist so kein Sprungbrett ins
  interne Netz.
- Inhalte der überwachten Seiten sind Daten: Die KI liest sie, ihr Ergebnis
  löst nichts aus und wird erst nach Bestätigung durch einen Admin gezeigt.
- Update-Pakete laden nur Admins hoch; jeder Schritt steht im Audit-Protokoll.
- Der Client führt nur MSIs mit gültiger Tungsten-Signatur aus, nie aus
  einer überwachten Webseite direkt.
- Das Office-Zertifikat wird nicht automatisch in die vertrauenswürdigen
  Herausgeber eingetragen (Sicherheitseinstellung der IT); der Store weist
  nur darauf hin.

## Etappen

1. **Erkennen und Hinweisen:** Release-Linien, überwachte Adressen, täglicher
   Abruf, Aufgaben-Kachel und E-Mail, Hinweis im Store-Fenster mit ReadMe-Link,
   optional KI-Text, Schalter und Richtlinie. Keine Verteilung von Paketen.
2. **Geführte Installation:** Paket-Upload mit Signaturprüfung, Freigabe Beta
   und Live, Download, Power PDF beenden, Installation mit Fortschritt,
   Ribbon-Reparatur, Ergebnis.
3. **Stille Installation:** erst nach erfolgreichen Tests auf x64 und ARM64,
   nur mit eingeschalteter Option oder Richtlinie.

## Offene Entscheidungen

1. **Verteilung der Pakete über den Store-Server:** Legal oder Product
   Management sollte das für interne und externe Kunden freigeben
   (Lizenzbindung, wer Updates erhalten darf). Etappe 1 braucht das nicht.
2. **Offizieller Download-Link:** Gibt es einen, den der Hinweis zeigen
   darf, und braucht er eine Anmeldung?
3. **Ein MSI für x64 und ARM64?** Klären wir mit dem ersten Paket.
4. **Standardwerte:** entschieden, alles aus; Server und Client werden
   bewusst eingeschaltet.
5. **Supportende je Linie:** gewünscht?
6. **Rückmeldung an den Server:** Soll der Client anonym melden, welche
   Linie und welches Update installiert ist (Bericht „Power-PDF-Versionen im
   Einsatz")? Der Server sieht die Host-Version heute schon im User-Agent.
7. **Test-Paket:** 2025.3 Update 9 Business als ZIP für den Prototyp von
   Etappe 2.
