# Power PDF Add-on Store, Projektplan v0.2 (Diskussionsgrundlage)

Stand: Okt 3, 2026. Status: Entwurf, erste Entscheidungen getroffen (siehe Abschnitt 7).

## 1. Vision

Eine zentrale Verteilstelle für Power-PDF-Plug-ins. Weltweit erstellen Kollegen heute
Plug-ins jeder für sich, Weitergabe per MSI und Zuruf. Der Store macht daraus einen
geordneten Kanal:

- **Server** (Azure Web App): nimmt Pakete entgegen, Admins prüfen und geben frei,
  freigegebene Pakete stehen allen zur Verfügung.
- **Store-Client** (selbst ein Power-PDF-Plug-in): eigener Ribbon-Bereich, listet live
  den Katalog, Benutzer wählt aus und installiert mit einem Klick. Updates werden
  erkannt und angeboten.
- **Paketformat**: ein einheitlicher Container, damit jedes Plug-in gleich eingereicht,
  geprüft und installiert werden kann.

Zielgruppen in zwei Stufen: zuerst das interne Team (Phase 1), später optional
Kunden/Endanwender (eigene Entscheidung mit Legal/Compliance, Phase 3).

## 2. Was der Store konkret löst

1. **Verteilung**: kein MSI-Versand per Mail/Teams mehr; eine Quelle, immer aktuell.
2. **Updates**: Client vergleicht installierte Version gegen Katalog ("Bump the version
   every release" wird damit erst richtig nutzbar).
3. **Qualität**: Freigabe-Workflow erzwingt Review (Lizenz-Policy, Viren-Scan,
   Smoke-Test, ARM64+x64-Pflicht) bevor etwas das Team erreicht.
4. **Saubere Deinstallation**: Heute verteilen sich Plug-in-Spuren auf 5 Orte und MSIs
   räumen nur 2 davon. Wenn der Store-Client Installation UND Deinstallation übernimmt,
   ist das Aufräumen endlich einheitlich gelöst.
5. **Sichtbarkeit**: Kollegen sehen überhaupt erst, was es schon gibt (Regel "Nichts
   nachbauen": der Katalog verhindert Doppelarbeit auch im Team selbst).

## 3. Architektur (Vorschlag)

```
+-------------------+        HTTPS        +---------------------------+
| Power PDF          |  ----------------> | Azure Web App              |
|  Store-Client .zxt |   Katalog (JSON)   |  ASP.NET Core 8            |
|  Ribbon "Store"    |   Paket-Download   |  - API: catalog/packages   |
|  Panel/Dialog      |                    |  - Admin-Web-UI (Review)   |
+-------------------+                     |  - Entra-ID-Auth           |
        |                                 +------------+--------------+
        | installiert/entfernt                         |
        v                                              v
  %APPDATA%\...\UILayout\Publish.xml          Azure Blob Storage
  Plug-in-Verzeichnis (.zxt)                  (Pakete, Icons)
                                              Azure SQL/Table (Metadaten)
```

### 3.1 Server

- **Hosting**: eine Azure Web App (App Service, Linux oder Windows, B1 reicht anfangs).
  Alternativ Azure Static Web Apps + Functions (günstiger, aber zwei Bausteine).
  Empfehlung: eine einzelne ASP.NET-Core-App, weil Katalog, Upload, Review-UI und
  Download dann ein Deployment sind.
- **"Statisch" präzisiert**: Der *Auslieferungspfad* ist quasi statisch (signierte
  catalog.json + unveränderliche Paket-Blobs, cachebar). Upload und Freigabe brauchen
  aber Logik und Benutzer, deshalb ist es keine rein statische Site.
- **Speicher**: Pakete in Blob Storage (ein Blob pro Paketversion, unveränderlich),
  Metadaten + Benutzerkonten in SQLite auf einem Azure-Files-Share (NimShare-Muster,
  siehe 3.4).
- **Auth (ENTSCHIEDEN, eigene Benutzerverwaltung)**: Der Server hat eine eigene,
  kleine Benutzerverwaltung (ASP.NET Core Identity, Passwörter nur als Hash
  gespeichert, niemals im Klartext). Genau zwei Rollen:
  - `Benutzer` (Publisher): meldet sich am Web-UI an, lädt eigene Plug-ins hoch,
    verwaltet seine Einreichungen, sieht deren Status.
  - `Admin`: prüft Einreichungen, gibt sie für den Live-Store frei oder lehnt ab,
    verwaltet Benutzerkonten.
  **Registrierung (ENTSCHIEDEN)**: Auf der Login-Seite kann man sich registrieren
  und damit einen Zugang BEANTRAGEN. Das Konto entsteht im Status "beantragt" und
  kann sich noch nicht anmelden; Admins bekommen eine E-Mail, prüfen und schalten
  frei oder lehnen ab, der Antragsteller wird per E-Mail informiert.
  Wichtig: **End-Anwender haben KEIN Konto und sehen den Server nie.** Sie nutzen
  ausschließlich das Store-Plug-in in Power PDF. Der Katalog und die Downloads der
  freigegebenen Pakete sind deshalb lesend offen (anonym, nur HTTPS-GET).
  Entra ID bleibt als mögliche spätere Option notiert, ist aber bewusst NICHT Teil
  des MVP.
- **API-Skizze**:
  - `GET  /api/catalog` (freigegebene Pakete, Versionen, Hashes)
  - `GET  /api/packages/{id}/{version}` (Download, Redirect auf Blob-SAS)
  - `POST /api/packages` (Upload, Status "eingereicht"; Auth per API-Token)
  - `POST /api/packages/{id}/{version}/approve|reject` (Admin)
  - `GET  /devkit` (Developer Kit, öffentlich)
  - Admin-Web-UI: Review-Queue mit Prüfbericht, Changelog, Diff zur Vorversion,
    Download-Zahlen
- **Upload-Wege (beides)**: simples Upload-Formular im Web-UI (eingeloggt als
  Benutzer) UND dieselbe Funktion als REST-API mit persönlichem API-Token, damit
  `ppak submit` und Claude-gesteuerte Abläufe direkt einreichen können. Beide
  Wege münden in dieselbe Validierungs-Pipeline (3.2a).
- **Persönliches API-Token (ENTSCHIEDEN)**: jeder Benutzer erzeugt in seinem
  Profil ein persönliches Token (anzeigen nur einmal bei Erzeugung, Speicherung
  nur als Hash, jederzeit widerrufbar). Das Token gibt er seinem Claude; damit
  kann Claude Pakete automatisch hochladen und aktualisieren. Ein Token wirkt
  immer nur im Namen seines Benutzers und kann nur dessen eigene Pakete anfassen.
- **Versions- und Changelog-Pflicht (ENTSCHIEDEN)**: jede Einreichung MUSS eine
  neue, höhere SemVer-Version tragen und eine kurze Änderungsbeschreibung
  mitliefern (Manifest-Feld `changelog`, API lehnt leere Changelogs ab).
- **Audit (ENTSCHIEDEN)**: der Server protokolliert alle relevanten Aktionen
  (Registrierung, Konto-Freigabe, Token erzeugt/widerrufen, Upload mit Version +
  Changelog + Weg (Web/API/Token-Name), Prüfergebnis, Beta-Eintritt, Freigabe/
  Ablehnung mit Begründung, Downloads je Version aggregiert). Admins sehen das
  als durchsuchbare Audit-Ansicht im Web-UI; je Paket gibt es eine
  Versions-Historie mit Changelogs.
- **Sicherheit**: HTTPS only, SHA-256-Hash je Paket im Katalog, Katalog signiert
  (Client prüft Signatur, damit ein kompromittierter Blob-Store nicht reicht),
  Upload-Größenlimit, kein serverseitiger Malware-Scan (Entscheidung Okt 3, 2026, s. R3), keine Secrets im
  Client, Least Privilege auf Blob (Client bekommt nur zeitlich begrenzte SAS-Links).

### 3.2 Paketformat ("PPAK", Arbeitsname)

ZIP-Container `<id>-<version>.ppak`:

```
manifest.json          Pflicht, siehe unten
x64/MyPlugin.zxt       Pflicht
arm64/MyPlugin.zxt     Pflicht (Dauerregel ARM64+x64)
assets/icon.png        Pflicht (Katalog-Anzeige)
docs/README.de.md      optional, mehrsprachig
LICENSES.md            Pflicht (nur MIT/BSD/Apache-2.0-Abhängigkeiten)
```

manifest.json (Kernfelder):

```json
{
  "id": "com.tungsten.officekonverter",
  "version": "0.3.0",
  "name": { "de": "Office Konverter", "en": "Office Converter" },
  "description": { "de": "...", "en": "..." },
  "publisher": "marcus.nimtz@tungstenautomation.com",
  "minPowerPdfVersion": "5.0",
  "architectures": ["x64", "arm64"],
  "ribbonAtomNamespace": "OfficePro.v3",
  "files": { "x64": "x64/MyPlugin.zxt", "arm64": "arm64/MyPlugin.zxt" },
  "sha256": { "x64": "...", "arm64": "..." },
  "changelog": { "de": "...", "en": "..." },
  "uninstall": { "registryKeys": [], "extraPaths": [] }
}
```

Bewusst im Manifest: `ribbonAtomNamespace` (wegen Ribbon-Atom-Cache des Hosts) und
`uninstall`-Angaben (damit der Client restlos entfernen kann, alle 5 Orte).

Ein CLI-Tool `ppak.exe pack|validate|submit|install` baut Pakete aus den
bestehenden Projektstrukturen und prüft das Manifest lokal (passt zu
tools/deliver.py-Denke).

### 3.2a Automatische Prüfung beim Upload (Validierungs-Pipeline)

Jeder Upload durchläuft serverseitig eine automatische Prüfstrecke. Harte Fehler
weisen den Upload sofort ab (der Einreicher sieht den Prüfbericht), der Rest
landet als Report in der Admin-Review-Queue. Geplante Prüfungen:

| Stufe | Prüfung | hart/weich |
|---|---|---|
| 1 | ZIP lesbar, Größenlimit, keine Pfad-Tricks (ZipSlip) | hart |
| 2 | manifest.json gegen JSON-Schema (Pflichtfelder, SemVer, ID-Format) | hart |
| 3 | Version > letzte eingereichte Version derselben ID; ID gehört dem Einreicher | hart |
| 4 | x64- UND arm64-.zxt vorhanden; PE-Header: Machine-Type stimmt (0x8664/0xAA64), ist DLL, exportiert den Plug-in-Einstiegspunkt | hart |
| 5 | SHA-256 im Manifest == tatsächliche Dateien | hart |
| 6 | ~~Malware-Scan~~ entfällt (Entscheidung Okt 3, 2026; s. R3) | – |
| 7 | LICENSES.md vorhanden; Textscan auf GPL/AGPL-Marker in Manifest/Begleitdateien | weich (Flag für Admin) |
| 8 | Import-Tabelle der .zxt: fremde DLL-Abhängigkeiten außerhalb Power-PDF-bin + Windows-Systemliste | weich (Flag) |
| 9 | Ribbon-Atom-Namespace kollidiert nicht mit anderem Katalog-Paket | hart |
| 10 | Icon vorhanden und dekodierbar; Katalogtexte mind. de+en | weich |
| 11 | Authenticode-Signatur der .zxt | weich im MVP, hart ab Phase 2 |

### Paket-Lebenszyklus mit Beta-Kanal (ENTSCHIEDEN)

Jede eingereichte Paketversion durchläuft diese Zustände:

1. **Eingereicht**: Upload angenommen, automatische Prüfung läuft. Sichtbar nur
   für Einreicher und Admins.
2. **Beta**: alle harten automatischen Prüfungen bestanden. Ab jetzt im Katalog
   sichtbar für Clients, bei denen in den Power-PDF-Optionen der Punkt
   **"Beta: nicht freigegebene Plug-ins anzeigen"** aktiviert ist. So können
   Kollegen und Tester sofort installieren und testen, ohne auf den Admin zu
   warten. Beta-Pakete tragen im Store-Client ein deutliches Beta-Badge und
   einen Hinweis ("ungeprüft, auf eigene Verantwortung"). Der Admin kann eine
   Version jederzeit aus dem Beta-Kanal ziehen.
3. **Live**: vom Admin freigegeben, für alle sichtbar.
4. **Abgelehnt / Zurückgezogen**: nicht mehr ladbar, bleibt in der Historie.

API: `GET /api/catalog` liefert Live, `GET /api/catalog?channel=beta`
zusätzlich Beta-Versionen. Updates: ein Client mit Beta-Option bekommt auch
Beta-Updates eines Pakets angeboten, ohne Beta nur Live-Versionen.

Hinweis für die Kundenphase (Phase 3): dort darf Beta nicht frei zugänglich
sein; dann wird der Kanal zusätzlich über einen Beta-Zugangscode aus den
Optionen geschützt. Intern (Phase 1) reicht der Schalter.

Was automatisch NICHT geht: ein echter Funktionstest (dafür bräuchte der Server
Windows + installiertes Power PDF). Der bleibt Teil des Admin-Reviews, als
dokumentierte manuelle Checkliste; später evtl. eine Windows-Test-VM als
nächtlicher Job. Der Prüfbericht je Einreichung wird gespeichert und ist Teil
der Freigabe-Historie.

Dieselbe Prüfstrecke läuft auch lokal: `ppak validate` nutzt denselben Code
(gemeinsame .NET-Library), damit Einreicher Fehler VOR dem Upload sehen.

**KI-freundliche Prüfschleife (ENTSCHIEDEN)**: Die API ist so gebaut, dass ein
per Claude gesteuerter Upload die Prüfung selbst iterieren kann:

- `POST /api/packages/validate`: nimmt das Paket entgegen, führt die komplette
  Prüfstrecke aus und antwortet NUR mit dem Prüfbericht; es wird nichts
  gespeichert. Beliebig oft wiederholbar.
- Der Prüfbericht ist maschinenlesbar: je Befund `{ code, severity, message,
  hint }`, wobei `hint` eine konkrete Korrekturanweisung ist (z. B.
  "arm64/<name>.zxt fehlt im ZIP; Paket muss beide Architekturen enthalten").
- `POST /api/packages` (der echte Upload) liefert denselben Bericht mit: bei
  harten Fehlern HTTP 422 + Bericht (nichts gespeichert), bei Erfolg HTTP 201 +
  Bericht + neuer Status (Beta).
- Ablauf für Claude (steht so auch in der Developer-Kit-Skill): packen →
  validate → Fehler anhand der hints beheben → wiederholen bis 0 Fehler →
  submit.

### 3.2b-2 Ribbon-Governance (ENTSCHIEDEN)

Damit kein Register-Wildwuchs entsteht, gilt für alle Store-Plug-ins:

- **EIN gemeinsames Register** mit Toolbar-Atom `FeaturePack` und lokalisiertem
  Titel "Erweiterte Funktionen" (en: "Enhanced Features", alle 16 Sprachen in
  der NameAndTitle.xml des Plug-ins). Jedes Plug-in hängt dort nur eine eigene
  GRUPPE an (`FeaturePack::<EigenerName>`); wer das Register nicht vorfindet,
  legt es mit exakt diesem Atom und Titel an (bewährtes Muster aus dem
  Feature Pack / XFA-Konverter).
- Die Claude-Skill im Developer Kit schreibt dieses Muster vor (inkl.
  Code-Schnipsel), und die Prüf-Pipeline kontrolliert es: Warnung, wenn der
  `ribbonAtomNamespace` nicht mit `FeaturePack::` beginnt.
- **Der Plugin-Store-Button selbst** liegt als Gruppe `FeaturePack::PluginStore`
  auf demselben Register und soll immer der LETZTE Eintrag (ganz rechts) sein;
  der Layout-Reparaturlauf des Clients sortiert die eigene Gruppe bei jedem
  Start ans Ende des Registers.

### 3.2c API-Design aus Agentensicht (ENTSCHIEDEN)

Gestaltungsfrage: "Wie würde Claude sich die API wünschen?" Antwort, so gebaut:

1. **Selbsterklärend ohne Vorwissen**: `GET /api` listet alle Endpunkte;
   `GET /api/agent-guide` liefert die komplette Anleitung als Markdown (Auth,
   Workflow, Beispiele, Grenzen). Ein Agent mit nur URL + Token findet allein
   den Weg, auch ohne Developer Kit.
2. **`GET /api/me` zuerst**: bestätigt Token, nennt Benutzer, Rolle und eigene
   Pakete. Der natürliche erste Call zum Verifizieren der Verbindung.
3. **Ein Antwort-Umschlag überall**: `{ ok, error{code,message,hint},
   findings[], data }`. Fehlercodes stabil und dokumentiert, `hint` ist immer
   eine konkrete Korrekturanweisung.
4. **Dry-Run**: `POST /api/packages/validate` prüft ohne zu speichern.
5. **Upload simpel**: `POST /api/packages` akzeptiert das ZIP wahlweise als
   roher Body (`Content-Type: application/zip`, curl-freundlich) oder als
   Multipart-Feld `package`. Alle Metadaten stehen im Manifest, keine
   Doppel-Eingabe per Formularfeldern.
6. **Idempotenz statt Ratespiel**: dieselbe Version mit identischem Hash noch
   einmal → eindeutige Antwort `VERSION_EXISTS` mit Hinweis, kein stiller
   Duplikat-Zustand.
7. **Status nachsehbar**: `GET /api/packages/{id}` zeigt dem Besitzer alle
   Versionen, Status, Prüfberichte, Review-Kommentare ("warum abgelehnt").
8. **Eigene Fehler zurücknehmbar**: `DELETE /api/packages/{id}/{version}`
   zieht eine EIGENE Beta-Version zurück (Live bleibt Admin-Sache).
9. **Schema abrufbar**: `GET /api/schema/manifest` liefert das
   Manifest-JSON-Schema zum lokalen Prüfen.
10. **Präzise Auth-Fehler**: 401 unterscheidet TOKEN_INVALID, TOKEN_REVOKED,
    USER_NOT_ACTIVE; 413 nennt das Größenlimit.

### 3.2b Developer Kit: Vorgaben direkt für Claude

Alle Kollegen entwickeln mit Claude. Der Server bietet deshalb ein
versioniertes **Developer Kit** zum Download an (`GET /devkit`, öffentlich):

- eine **Claude-Skill** (`powerpdf-store-publish`): beschreibt Paketstruktur,
  Manifest-Schema, die Prüfregeln, typische SDK-Fallen (Atom-Cache, IconMode,
  Laufzeit, ODR) und den Upload-Weg. Kollegen legen sie in ihr `.claude`-Verzeichnis,
  danach weiß ihr Claude beim Entwickeln automatisch, wie ein Store-Paket
  auszusehen hat und wie es eingereicht wird.
- `ppak.exe` + manifest-schema.json + ein Beispielpaket.
- eine Projektvorlage (leeres Plug-in-Gerüst mit x64/arm64-Konfiguration).

Das Kit wird aus dem Server-Repo gebaut und trägt eine eigene Version; die
Skill sagt Claude auch, wie es Updates des Kits erkennt.

**Full-Service-Prinzip (ENTSCHIEDEN)**: Der Server hostet zusätzlich die
KOMPLETTE Plugin-SDK-Dokumentation und unser gesammeltes Praxiswissen, sodass
ein leerer Claude-Chat mit nur der Store-URL (+ Token) alles Nötige findet:

- `GET /api/agent-guide`: Einstieg als Markdown; verweist auf alles Weitere.
- `GET /api/devkit`: listet die verfügbaren Dateien.
- `GET /api/devkit/{pfad}`: liefert sie aus, u. a. `sdk.zip` (Plugin SDK:
  Header, Doku, Samples), `knowledge.md` (unsere SDK-Fallen: Atom-Cache,
  IconMode, Laufzeit, Panel-Rezept, Prefs-Crash, Icon-Formate, ODR, Cleanup),
  `skill/` (die Claude-Skill), Manifest-Schema, Projektvorlage.
- Befüllung: Admins laden die Inhalte über das Web-UI in den DevKit-Bereich
  (Dateiablage auf dem /data-Share), Aktualisierung jederzeit ohne Deployment.
- Hinweis Phase 3: SDK-Weitergabe an Externe braucht vorher eine
  Produktmanagement-Entscheidung; intern (Tungsten-eigenes SDK) unkritisch.

### 3.3 Store-Client (Plug-in)

- Eigenes .zxt, Ribbon-Tab oder -Gruppe "Add-on Store", öffnet Dialog oder
  Navigationspanel (Panel-Rezept aus EnhancedFeaturePack v1 vorhanden).
- Funktionen MVP: Katalog anzeigen (Name, Beschreibung, Version, Icon, Changelog),
  installieren, aktualisieren, deinstallieren, "Neustart erforderlich"-Hinweis
  (Plug-ins laden beim Start).
- **Optionen-Seite (ENTSCHIEDEN)**: eigene Seite unter Power-PDF-Optionen mit der
  Server-URL. Voreingestellt auf unsere Store-URL, aber frei änderbar (falls die
  URL sich ändert oder jemand einen eigenen Store betreibt). Dazu der Schalter
  **"Beta: nicht freigegebene Plug-ins anzeigen"** (Standard: aus), siehe
  Paket-Lebenszyklus in 3.2a. Technisch über das
  bekannte Prefs-Rezept (RVAppRegisterPrefsType IMMER zusammen mit
  RVAppRegisterPrefsPage, sonst Crash). Optional später: Policies-Muster aus dem
  Feature Pack (HKLM erzwingt die URL in Firmenumgebungen).
- Technik: WinHTTP für API-Zugriff, JSON-Parsing, Download in Temp, Hash-Prüfung,
  dann zweistufige Installation (Ergebnis Spike S1): elevierter Helfer kopiert
  den Program-Files-Anteil (ein UAC-Dialog), der Publish.xml-Patch läuft
  uneleviert im Benutzerkontext. Im Client ein Info-Hinweis: "Installation
  erfordert lokale Administratorrechte."
- Selbst-Update: der Store-Client ist selbst ein Katalog-Paket.
- Auslieferung des Clients selbst: einmalig als MSI (bestehender Weg), danach
  aktualisiert er sich über den Store.

### 3.3a Update-Konzept (ENTSCHIEDEN, Empfehlung umgesetzt)

Grundsatzentscheidung: **EIN zentraler Update-Agent (der Store-Client), nicht
Update-Code in jedem Plug-in.** Gründe: ein einziger leiser HTTP-Check pro
Power-PDF-Start statt N Plug-ins, die einzeln nach Hause telefonieren
(Firewall-Rauschen, Datenschutz, doppelter Code); alte Plug-ins müssen nicht
nachgerüstet werden; der Client kennt ohnehin Katalog und Installationsweg.

Die Idee "das Plug-in definiert verpflichtend, wo es nachschaut" fließt so ein:

- **Pflicht im Paketformat**: Bei jeder Installation legt der Installer (Store-
  Client wie MSI) die `manifest.json` des Pakets in den Datenordner des
  Plug-ins (`Plug-Ins\<Name>\manifest.json`). Darin stehen Paket-ID und
  Version; optional `updateServer` (Standard: unser Store).
- **Update-Erkennung**: Der Store-Client scannt beim Start alle
  `Plug-Ins\*\manifest.json`, vergleicht mit dem Katalog (live bzw. beta je
  Option) und erkennt so AUCH Plug-ins, die per MSI/Intune OHNE Store
  installiert wurden. Damit ist "Deployment ohne Store, Updates trotzdem
  sichtbar" abgedeckt, ohne dass ein Plug-in je selbst HTTP spricht.
- **Anzeige**: dezente Benachrichtigung beim Power-PDF-Start ("2 Updates im
  Plugin-Store verfügbar", einmal je Sitzung, abschaltbar in den Optionen) und
  ein Update-Bereich oben im Store-Dialog mit 1-Klick-Update je Plug-in
  (gleicher Mechanismus wie Installation, ein UAC-Dialog). Ein dynamisches
  Badge IM Ribbon ist wegen des Atom-/Titel-Cachings des Hosts unzuverlässig;
  bewusst nicht Teil des Konzepts.
- **Check-Frequenz konfigurierbar**: bei jedem Start / täglich / aus
  (Optionen-Seite; per HKLM-Policy erzwingbar). Der Check ist ein einzelner
  GET /api/catalog, anonym, ohne Telemetrie.
- **Enterprise ohne Store-Client**: `ppak.exe update --all --silent` prüft
  dieselben manifest.json-Dateien und aktualisiert headless (für Intune/SCCM-
  Wartungsfenster).

### 3.4 Server-Deployment (Muster: NimShare)

Wir übernehmen das erprobte Schema aus github.com/mnimtz/nimshare:

- **ASP.NET Core 8 im Docker-Container**, Image via GitHub Actions nach GHCR
  (Tags: `latest` + SemVer + SHA), Push auf main baut automatisch.
- **1-Click-Deployment**: ein ARM-Template (`infra/azuredeploy.json`) mit
  "Deploy to Azure"-Button im README. Provisioniert: Linux App Service (B1)
  mit dem Container, Storage Account (Blob-Container für Pakete, Azure-Files-Share
  für die SQLite-Metadaten-DB, gemountet unter /data), Managed Identity mit
  "Storage Blob Data Contributor" (keine Connection Strings, keine Keys in den
  App Settings).
- **SQLite statt Azure SQL**: eine Datei auf dem Files-Share, reicht für
  Katalog-Metadaten und Benutzerkonten locker, hält die Kosten bei ~12 EUR/Monat.
- **First-Run-Wizard**: erster Aufruf der frischen Instanz legt das erste
  Admin-Konto an (E-Mail, Name, Passwort), genau wie bei NimShare. Weitere
  Konten kommen über den Registrierungs-Antrag auf der Login-Seite.
- **E-Mail-Gateway (Resend, NimShare-Muster; SMTP als Alternative konfigurierbar)**
  für Benachrichtigungen:
  - an Admins: neuer Registrierungs-Antrag, neue Plug-in-Einreichung
    (inkl. Link auf die Review-Queue und Kurzfassung des Prüfberichts)
  - an Benutzer: Konto freigegeben/abgelehnt, Einreichung freigegeben/abgelehnt
    (mit Begründung), neue Version eines eigenen Pakets live
  - Betreiber-Konfig: `Email__ResendApiKey` als App Setting, Absenderdomäne
    verifiziert; ohne Konfiguration läuft der Server normal, nur ohne Mails.
- **Server-Updates**: Push auf main → neues Image → App-Service-Neustart zieht
  `latest`. Releases zusätzlich als Versions-Tag, damit man pinnen kann.
- **VERSION-Datei** als Single Source of Truth, sichtbar im Footer (Regel:
  jede Auslieferung bumpt die Version).

### 3.5 Enterprise-Deployment beim Endkunden (Konzept, Phase 2/3)

Für Kunden, die Plug-ins zusammen mit Power PDF ausrollen wollen (Intune,
SCCM & Co.), gibt es einen Silent-Pfad ohne Store-UI:

- `ppak.exe install <paket.ppak | id> --silent [--server <url>]`: läuft
  eleviert im Deployment-Kontext, holt das Paket (oder nutzt eine lokal
  mitgelieferte Datei, auch offline), verifiziert Hash/Signatur und installiert
  maschinenweit. Exit-Codes für die Deployment-Tools.
- Einsatz direkt nach dem Power-PDF-Setup im selben Task-Sequence-Schritt,
  damit ist "Power PDF + Add-ons in einem Rutsch" abgedeckt.
- Die Store-URL lässt sich per Policy (HKLM, bekanntes Policies-Muster aus dem
  Feature Pack) fest vorgeben, inkl. Option, die benutzerdefinierte URL im
  Client zu sperren.

## 3.9 Stand der Umsetzung (Okt 3, 2026)

v0.1.0 ist gebaut und lokal verifiziert:

- **Server**: ASP.NET Core 8, läuft lokal (http://localhost:5190). Fertig:
  First-Run-Wizard, Login, Registrierungs-Antrag + Admin-Freigabe,
  persönliche API-Tokens, Upload (Web + API, roh/multipart), komplette
  Validierungs-Pipeline inkl. Wissens-Checks (PE-Maschinentyp, Debug-Runtime,
  Import-Tabelle, Reserved-Names, Atom-/Tab-Governance, panel::-Falle,
  IconMode-1-Falle, NameAndTitle-Sprachkonsistenz, GPL-Marker, Icon-Format),
  Beta→Live-Workflow, Audit-Log, Download-Zähler, E-Mail-Hooks (Resend),
  16 Sprachen (Auto-Erkennung + Umschalter), Brand-Styling, /api/agent-guide,
  /api/schema/manifest, TSV-Katalog für den nativen Client.
  Durchstich getestet: Setup → Token → validate (Fehlerpfad) → submit →
  409-Idempotenz → Beta-Katalog → Admin-Freigabe → Live-Katalog → Download →
  Audit. Alles grün.
- **Client**: PluginStore.zxt (x64) baut; Gruppe "Plugin-Store" auf dem
  gemeinsamen Register, Dialog mit Katalogliste (TSV), installiert mit
  Hash-Prüfung + einem UAC-Prompt, erkennt installierte Versionen über
  manifest.json im Plug-in-Datenordner, Optionen-Seite (URL, Beta, Logging,
  Policy-Lock). Test in Power PDF selbst steht aus (deploy.cmd liegt bei).
- **v0.2.0 (gleicher Tag)**: Produktiv-Instanz https://ppdf-store.azurewebsites.net
  (1-Click-Deployment, SQLite+Pakete auf /data). Benutzerverwaltung komplett:
  Rolle "Prüfer" (nur Review-Queue), Benutzer direkt anlegen, Einladen per
  E-Mail (7-Tage-Token, Link-Fallback ohne Mail-Konfiguration), Rollenwechsel
  mit Letzter-Admin-Schutz, Profil mit Namen + Avatar, Admin-Bereich
  "Einstellungen" (Resend-Key, Absender, Public-Base-URL, zur Laufzeit in der
  DB). Katalog-Startseite: Client-Download-Box, Suchfeld, Kategorien-Filter
  (Manifest-Feld `category`, lokalisierte Chips). Deinstallation im Client.
  ARM64-Regel korrigiert: x64 Pflicht (deckt ARM64EC-Hosts ab), natives arm64
  optional. Sicherheits-Review durchgeführt und gefixt
  (docs/security-review-2026-10.md), Lizenz-Inventur
  (docs/LICENSES-THIRD-PARTY.md), SQLite-CVE-Pin. Pakete gebaut und lokal
  eingereicht: Plugin-Store-Client 0.2.0, Smart Bookmarks 1.0.5,
  XFA Converter 0.2.0 (Office Konverter zurückgestellt: 478-MB-Engine braucht
  den Nachlade-Mechanismus).
- **Offen für die erste Team-Auslieferung**: Client-Dialog-Strings in den
  übrigen 14 Sprachen (.rc) + 14 weitere NameAndTitle-Ordner; Live-Test des
  Clients in Power PDF; Resend-Key in den Einstellungen; MSI für den Client;
  ppak-CLI; Devkit-Inhalte (sdk.zip, knowledge.md, Skill); Office Konverter
  mit Engine-Download bei Installation.

## 4. Phasenplan

### Phase 0, Spikes (1-2 Wochen, entscheidet die Machbarkeit)

- **S1 Installationspfad ohne Admin, ERLEDIGT (Okt 3, 2026), Ergebnis: Admin nötig.**
  Befund auf dieser Maschine (Power PDF 2025):
  - Plug-ins liegen ausschließlich in `<Install>\bin\Plug-Ins\` (flaches .zxt +
    Datenordner je Plug-in). ACL: normale Benutzer haben nur Lese-/Ausführrechte.
  - Kein Benutzer-Plug-in-Ordner unter %APPDATA% vorhanden.
  - String-Scan in PowerPDF.exe: genau EINE Fundstelle "Plug-Ins", als
    Verzeichnis-Scan (`\*.*`) relativ zur Installation. Kein alternativer oder
    konfigurierbarer Ladepfad erkennbar.
  Konsequenz (vom Team akzeptiert): Installation erfordert lokale Admin-Rechte.
  - Der Store-Client zeigt das offen an: "Info"-Hinweis je Plug-in bzw. global
    ("Installation erfordert lokale Administratorrechte").
  - Technisch: ein kleiner elevierter Helfer (EIN UAC-Dialog) kopiert nur den
    Program-Files-Anteil. Der Publish.xml-Patch läuft bewusst UNELEVIERT im
    Benutzerkontext (bekannte Falle: eleviert löst %APPDATA% ins falsche Profil auf).
- **S2 entfällt** (war: Login im Client). Entschieden: der Client braucht keinen
  Login, der Katalog ist lesend offen. Uploads laufen über Web-UI oder API.
- **S3 Laufzeit-Abhängigkeiten**: Pakete deklarieren benötigte Runtime; Prüfung,
  dass die Ziel-Power-PDF-Version die Laufzeit mitbringt (Regel: .zxt braucht
  dieselbe Laufzeit wie PowerPDF.exe).

### Phase 1, MVP intern (Team weltweit)

- Server: Katalog, Upload, Review-Queue, Freigabe, Entra-Login im Web-UI
- Paketformat + `ppak`-CLI, 2-3 Bestandsplug-ins als Pilotpakete (z. B.
  Office Konverter, XFA-Konverter, Feature Pack)
- Store-Client: Katalog anzeigen, installieren, aktualisieren, deinstallieren
- Server-Prüfpipeline (3.2a) und Developer Kit mit Claude-Skill (3.2b)
- Betrieb: eine Azure-Subscription, Kostenrahmen klein (App Service B1 + Storage,
  grob 12 EUR/Monat, NimShare-Erfahrungswert)

### Phase 2, Komfort

- Auto-Update-Benachrichtigung beim Power-PDF-Start (leiser Check)
- Download-Statistiken, Kommentarfeld/Feedback je Plug-in
- Signierung der .zxt mit Firmenzertifikat (Authenticode) statt nur Hash
  (der Beta-Kanal selbst ist in Phase 1 vorgezogen, siehe 3.2a)

### Phase 3, Kundenphase (separate Entscheidung)

- Native Plug-ins = voller Code im Prozess des Kunden. Vorher nötig:
  Code-Signing-Pflicht, härterer Review, Haftungs-/Supportmodell, Freigabe durch
  Produktmanagement und Legal/Compliance (GDPR-Betrachtung für Benutzerkonten
  und Telemetrie). Erst planen, wenn Phase 1 intern läuft.

## 5. Risiken und offene Punkte

| # | Risiko/Frage | Einschätzung |
|---|---|---|
| R1 | .zxt-Ablage erfordert Adminrechte | GEKLÄRT (S1): ja, Admin nötig. Akzeptiert; Lösung: elevierter Helfer + Info-Hinweis im Client, Silent-Pfad für Enterprise-Deployment (3.5). |
| R2 | Plug-in-Konflikte (Atom-Cache, doppelte MSI+Store-Installation) | Manifest kennt Atom-Namespace; Client erkennt MSI-installierte Duplikate und warnt. |
| R3 | Native Code im Store = Malware-Oberfläche | ENTSCHIEDEN (Okt 3, 2026): KEIN serverseitiger Scan. Schutz: Freigabe durch Admin/Prüfer, Audit-Log, SHA-256-Prüfung im Client, Windows Defender beim Herunterladen (Prüfer) und bei der Installation (Anwender). VirusTotal ausgeschlossen (Dateien würden an Dritte gehen). Für die Kundenphase: Microsoft Defender for Storage nachrüsten (Pakete dafür in Blob Storage), mit Security abstimmen; Code-Signing Pflicht. |
| R4 | Azure-Subscription und Eigentümerschaft | Wessen Subscription, wer betreibt, wer ist Admin? Früh mit IT klären. |
| R5 | Power PDF lädt Plug-ins nur beim Start | UX: sauberer "Jetzt neu starten"-Flow im Client. |
| R6 | Mehrsprachigkeit | ENTSCHIEDEN (Dauerregel): immer ALLE europäischen Power-PDF-Sprachen (en de fr it es nl pt da fi no sv pl cs hu ru tr). Store-Client folgt der Power-PDF-Sprache; Server-UI erkennt die Sprache automatisch (Accept-Language, schon beim Login) und ist manuell umschaltbar. Jede Textänderung zieht alle Sprachen mit; Release erst bei Vollständigkeit. |
| R7 | Kompatibilität Power-PDF-Versionen | `minPowerPdfVersion` im Manifest + Client meldet eigene Hostversion an den Katalogfilter. |

## 5a. Brand-Vorgabe (verbindlich)

Alles Sichtbare (Server-Web-UI, Store-Client, Icons, Doku, E-Mails) folgt dem
Tungsten Automation Brand Book (März 2025). Die konkreten Farb-, Schrift-,
Logo- und Namensregeln samt CSS-Tokens stehen in `docs/brand.md`. Der
Brand-Check ist Teil der Definition of Done jedes UI-Inkrements und der
Admin-Review-Checkliste.

## 6. Bewusste Nicht-Ziele (vorerst)

- Kein Bezahlmodell, keine Lizenzierung je Plug-in
- Kein automatisches Sandboxing nativer Plug-ins (nicht realistisch bei .zxt)
- Kein sofort aktiver Self-Service-Account: Registrierung ist nur ein ANTRAG,
  aktiv wird ein Konto erst nach Admin-Freigabe
- Keine Kundenfreigabe in Phase 1

## 7. Entscheidungen

Getroffen (Okt 3, 2026):

1. **Rollenmodell**: End-Anwender nutzen nur das Store-Plug-in, nie den Server.
   Server-Web-UI nur für Benutzer (= Plug-in-Einreicher) und Admins (= Freigabe).
2. **Benutzerverwaltung**: eigene Verwaltung auf dem Server (kein Entra ID im MVP),
   zwei Rollen: Benutzer und Admin.
3. **Katalog**: lesend offen, kein Login im Client.
4. **Client-Konfiguration**: Server-URL als Einstellung in den Power-PDF-Optionen,
   mit unserer URL vorbelegt, benutzerdefiniert änderbar.

Noch offen:

5. Name des Projekts/Stores (Arbeitstitel "Add-on Store" / "Plugin-Store").
6. Installationsmechanik: Client platziert Dateien direkt (Empfehlung) vs. MSI pro
   Plug-in. Hängt am Ergebnis von Spike S1.
7. Pilot-Plug-ins für Phase 1.
8. Azure-Subscription und Betrieb.
