# Konzept: Git & Veracode im Add-on Store

Stand: Nov 23, 2026.

**Umsetzung:** Schritt 1 und der Server-Teil von 1a sind mit S2.8.0 gebaut (Einstellungsseite, generische Git-Verbindung, Veracode-Vorbereitung, Verknüpfungen, Webhook-Empfang, Notbremse mit Pausiert/Außer Betrieb/Zurück auf letzte gute Version, Notfall-Nutzer). Der Client-Teil von 1a ist mit dem Store-Client 2.3.0 gebaut (Abschalten und Wiedereinschalten mit einer Administrator-Bestätigung, Hinweisleiste im Store-Fenster), S2.8.1 enthält die Korrekturen aus dem Code-Check. Offen: Freigabe und Upload von Client 2.3.0, 1b und die Schritte 2 bis 5.

Entscheidungen Marcus (Nov 23, 2026): nichts fest auf ein Git, alles wird später eingetragen; Veracode-Zugang gibt es noch nicht (vorbereitend); Ziel ist später das Firmen-GitHub; Notfall-Rolle ja, Kunden-Admins benachrichtigen ja.

## Ziel

- Plug-ins liegen in Git-Repos. Dort laufen Build und Veracode-Scan, beim Release und regelmäßig.
- Der Store ist mit dem Git verbunden, holt fertige und gescannte Versionen selbst ab und zeigt die Scan-Ergebnisse.
- Nach der ersten Freigabe durch einen Menschen kann der Store spätere Updates nach festen Regeln automatisch freigeben, z. B. wenn ein Veracode-Fund behoben wurde.
- Alles wird unter **einem** Admin-Punkt "Git & Veracode" konfiguriert. Der Rest des Servers bleibt so gut wie unberührt.
- Generisch: Heute ein eigenes GitHub zum Testen, später das Firmen-Git. Umstellen heißt nur Einstellungen ändern.

## 1. Aufbau im Server: ein abgeschlossenes Modul

Vorbild ist "Senden an": eigene Tabellen, eigene Dienste, eigene Admin-Seite.

| Teil | Datei(en) | Aufgabe |
|---|---|---|
| Daten | `Data/GitScanEntities.cs` | eigene Tabellen mit Präfix `GitScan*`, eigenes Schema wie `SendToSchema` |
| Git-Anbindung | `Services/GitScan/IGitProvider.cs`, `GitHubProvider.cs` | Schnittstelle je Git-Anbieter; zuerst GitHub (github.com und GitHub Enterprise Server) |
| Veracode | `Services/GitScan/VeracodeClient.cs` | optional: Policy-Status direkt bei Veracode nachprüfen (HMAC-Anmeldung, nur lesen) |
| Ablauf | `Services/GitScan/GitScanService.cs` | Verknüpfungen, Import, Scan-Ergebnisse, Fristen |
| Freigaberegeln | `Services/GitScan/AutoReleaseService.cs` | prüft Bedingungen, gibt frei oder lässt liegen |
| Hintergrund | `Services/GitScan/GitScanWorker.cs` | Abgleich als Rückfallebene, Fristen, Erinnerungen |
| Webhook | `Api/GitScanEndpoints.cs` | Empfang der Git-Ereignisse mit Signaturprüfung |
| Oberfläche | `Pages/Admin/GitScan.cshtml(.cs)` | die eine Einstellungsseite mit Bereichen links |

**Berührungspunkte mit dem bestehenden Server** (bewusst klein gehalten):
1. Import ruft das vorhandene `SubmissionService.SubmitAsync(..., via: "git")` auf. Alle heutigen Prüfungen laufen unverändert.
2. Automatische Freigabe ruft die vorhandene Freigabe in `VersionActionService` auf.
3. Prüfseite einer Version und Katalog zeigen ein Scan-Kästchen bzw. Badge (nur lesend aus dem Modul).
4. Schalter im Bereich "Features": Modul an/aus. Aus = keine Webhooks, kein Worker, Seite nur mit Hinweis.
5. Sicherung: neue Tabellen kommen ins Backup (Dauerregel "Backup vollumfänglich"); Geheimnisse bleiben verschlüsselt.
6. Ein Link im Einstellungs-Menü.

## 2. Die Einstellungsseite "Git & Veracode"

Bereiche links, eine Ansicht zur Zeit, lange Listen seitenweise (25/50/100).

### 2.1 Übersicht
- Verbindung Git: verbunden ja/nein, Organisation, letzter Webhook, letzter Abgleich.
- Verbindung Veracode: eingerichtet ja/nein, letzter Test.
- Zahlen: verknüpfte Add-ons, offene Funde, überfällige Fristen, automatisch freigegebene Versionen (30 Tage).
- Rote Hinweise oben: Frist läuft ab, Webhook seit X Tagen still, Zugang läuft ab.

### 2.2 Git-Verbindung
- **Anbieter:** GitHub.com, GitHub Enterprise Server (eigene Adresse). Später weitere über dieselbe Schnittstelle (z. B. GitLab, Azure DevOps).
- **API-Adresse:** vorbelegt für GitHub.com, frei für das Firmen-Git. Nur HTTPS.
- **Verbinden als GitHub App** (empfohlen, statt persönlichem Token):
  - "Mit GitHub verbinden": Der Store legt die App über den Manifest-Ablauf von GitHub selbst an. Man bestätigt bei GitHub, wählt Organisation und Repos, fertig.
  - Alternativ von Hand: App-ID, privater Schlüssel (.pem), Installations-ID.
  - Der Schlüssel wird verschlüsselt gespeichert (wie heute der KI-Schlüssel) und nie wieder angezeigt.
- **Webhook:** Adresse und Geheimnis zeigt der Store an (Geheimnis nur einmal beim Erzeugen). Bei der App-Variante richtet GitHub das selbst ein.
- **Test:** prüft Anmeldung, Rechte und Webhook-Empfang.
- **Trennen:** löscht Schlüssel und Geheimnis; Verknüpfungen bleiben als "getrennt" stehen.

Die App braucht nur: Inhalte lesen, Actions lesen und starten, Pull Requests anlegen (für die Workflow-Einrichtung), Metadaten lesen.

### 2.3 Veracode
- **Woher kommt das Ergebnis:**
  - "aus dem Workflow": Der Store liest das Scan-Ergebnis, das der Workflow als Artefakt ablegt. Der Store fragt GitHub selbst, aus welchem Repo, Tag und Lauf es stammt.
  - "zusätzlich bei Veracode nachprüfen": Mit API-ID und Schlüssel (nur Leserechte) fragt der Store den Policy-Status selbst ab.
- **Zugangsdaten:** API-ID und Schlüssel, verschlüsselt, Test-Knopf.
- **Erwartete Policy:** Name der Veracode-Policy, die bestanden sein muss.
- **Regel-Scan:** Standard-Rhythmus für Nach-Scans der live stehenden Versionen (z. B. wöchentlich, Tag und Uhrzeit).
- **Was als bestanden gilt:** Policy bestanden, oder Funde nur mit von AppSec akzeptierter Mitigation.

### 2.4 Plug-in-Verknüpfungen
Liste aller Add-ons mit Verknüpfung, seitenweise. Je Eintrag:
- **Add-on** ↔ **Repo** (Auswahl aus den Repos der Verbindung).
- **Unterordner** und **Tag-Muster** für Sammel-Repos mit mehreren Plug-ins (z. B. FP1: Ordner `measuretools/`, Tag `measuretools-v*`). Einzel-Repo: Ordner leer, Tag `v*`.
- **Workflow-Datei** (Standard `.github/workflows/addon-release.yml`).
- **Status:** letzter Build, letzter Scan, Ergebnis, nächste Nach-Prüfung, Frist.
- **Knöpfe:** "Workflow einrichten" (Store legt einen Pull Request mit der Vorlage an), "Jetzt scannen" (startet den Workflow), "Jetzt abgleichen", "Trennen".
- **Automatische Freigabe für dieses Add-on:** aus / wie global / eigene Stufe (siehe 2.6).

### 2.5 Scan-Ergebnisse
- Liste der Läufe, seitenweise, filterbar nach Add-on, Ergebnis, Zeitraum.
- Je Lauf: Version, Commit, Auslöser (Release, Regel-Scan, von Hand), Policy-Ergebnis, Funde nach Schweregrad, Links zum Git-Lauf und zu Veracode.
- Je Fund: CWE, Schweregrad, Datei/Zeile, Mitigation-Status, seit wann offen, Frist.
- Der Entwickler sieht die Ergebnisse seiner eigenen Add-ons auf seiner Entwicklerseite (nur lesend).

### 2.6 Automatische Freigabe
- **Globaler Schalter**, Standard aus.
- **Stufe:** automatisch nach Beta, oder automatisch live.
- **Voraussetzungen, alle müssen erfüllt sein:**
  - Das Add-on hat schon eine von einem Menschen freigegebene Version.
  - Die neue Version kommt aus dem verknüpften Repo, mit passendem Tag, aus dem hinterlegten Workflow.
  - Veracode-Policy bestanden.
  - Alle Store-Prüfungen ohne Fehler; Warnungen nur, wenn sie schon in der letzten freigegebenen Version standen (wählbar).
  - Keine neuen Online-Dienste, Fremdkomponenten, Lizenzen oder Rechte gegenüber der letzten freigegebenen Version.
  - Wählbar: KI-Prüfhilfe sagt "approve"; nur Patch- oder Minor-Versionen; nur wenn der Lauf von einem Regel-Scan-Fund ausgelöst wurde.
- **Danach:** Eintrag im Audit-Protokoll ("automatisch freigegeben nach Regel …"), Mail an die Admins, Knopf "Zurücknehmen" (vorherige Version wieder live).
- **Nie automatisch:** der Store-Client selbst und Add-ons ohne Verknüpfung.

### 2.7 Fristen & Sperren
- Frist nach einem neuen Fund: aus der Veracode-Policy übernommen oder fest (z. B. 90 Tage für Medium).
- Erinnerungen an Entwickler und Admins: z. B. 30, 7 und 1 Tag vorher.
- Bei Fristablauf, wählbar: nur warnen / Badge "Funde offen" im Katalog / Version sperren (vorhandene Sperrfunktion).

### 2.8 Außer Betrieb nehmen

Für den Fall, dass ein Problem zu groß ist oder nicht schnell behoben werden kann. Drei Stufen, jede mit einem Klick und Pflicht-Begründung:

| Stufe | Wirkung im Store | Wirkung bei installierten Kopien |
|---|---|---|
| **Pausiert** | keine neuen Installationen und Updates; Katalog zeigt "vorübergehend nicht verfügbar" mit Begründung | laufen weiter |
| **Außer Betrieb** | wie Pausiert | der Store-Client schaltet das Plug-in beim nächsten Power-PDF-Start ab, ohne es zu deinstallieren; Einstellungen bleiben; Hinweis an den Benutzer mit Begründung |
| **Sicherheitssperre** | wie heute (S1.0.11) | der Client fordert zum Entfernen auf |

Dazu:
- **Zurück auf die letzte gute Version** als eigene Schnellaktion: Ist nur die neueste Version betroffen, wird sie zurückgezogen und die vorherige wieder live gestellt. Clients bekommen sie als Update.
- **Wieder in Betrieb nehmen:** hebt die Stufe auf. Bei "Außer Betrieb" schaltet der Client das Plug-in beim nächsten Start wieder ein.
- **Wiedervorlage:** jede Stufe hat ein Prüfdatum. Der Store erinnert daran, damit nichts vergessen in "pausiert" stehen bleibt.
- **Wer:** Admins. Optional eine Notfall-Rolle, die nur stilllegen, aber nichts freigeben darf.
- **Benachrichtigung:** Mail an Entwickler und Admins. Wählbar auch an die Kunden-Admins, deren Benutzer das Plug-in installiert haben (aus dem Bestand).
- **Audit:** wer, wann, warum, welche Stufe; erscheint im Dossier der Version.
- **Automatisch:** wählbar als Folge eines Fristablaufs (2.7), z. B. "Pausiert" bei Ablauf, "Außer Betrieb" 14 Tage danach.
- **Gilt für jedes Add-on**, auch ohne Git-Verknüpfung. Die Liste der stillgelegten Add-ons steht in diesem Bereich, die Aktion zusätzlich auf der Seite des Add-ons.

**Neu im Store-Client nötig** (eigenes Client-Release): "Außer Betrieb" lesen (die heutige Sperrliste bekommt eine zweite Art), Plug-in-Datei beim nächsten Start beiseitelegen statt löschen, beim Aufheben zurücklegen, Hinweis in allen 21 Sprachen. Ein Plug-in, das Power PDF schon geladen hat, lässt sich nicht im laufenden Betrieb entladen; abgeschaltet wird beim nächsten Start.

### 2.9 Workflow-Vorlage
- Zeigt den zentralen wiederverwendbaren Workflow mit Versionsangabe und eine Anleitung für Entwickler.
- Download der Aufrufdatei für ein Plug-in-Repo.

### 2.10 Protokoll
- Empfangene Webhooks, Importe, Freigaben, Fehler. Werte gehen durch `LogSafe.Of`.

## 3. Ablauf

**Neues Release**
1. Entwickler setzt einen Tag im Plug-in-Repo.
2. Der Workflow baut x64 und ARM64, baut den Veracode-Scan-Build (x64, Debug, PDB) und lässt den Policy Scan laufen.
3. Er legt Paket, Quell-ZIP und Scan-Ergebnis als Artefakte ab.
4. GitHub meldet "Lauf fertig" per Webhook an den Store. Zur Sicherheit gleicht der Store zusätzlich stündlich ab.
5. Der Store prüft bei GitHub selbst: richtiges Repo, richtiger Workflow, Tag passt zum Muster, Version im Paket passt zum Tag.
6. Er lädt die Artefakte und reicht sie über `SubmitAsync` ein. Alle heutigen Prüfungen laufen.
7. Status "eingereicht" mit Scan-Kästchen. Freigabe von Hand oder per Regel (2.6).

**Regel-Scan**
1. Der Workflow läuft zeitgesteuert, oder der Store startet ihn.
2. Er scannt den Stand der live stehenden Version neu.
3. Neue Funde: Frist startet, Mail, Status am Add-on.
4. Behebung = normales neues Release, mit höherer Version (Dauerregel). Bei passenden Regeln geht es automatisch live.

## 3a. Store selbst: Server und Client

Server und Store-Client liegen schon gemeinsam auf GitHub (`mnimtz/pluginstore-powerpdf`, heute mit CodeQL und Trivy). Sie bekommen denselben Veracode-Weg:

- **Eigener Workflow im Store-Repo** `veracode.yml`:
  - Server: `dotnet publish -c Release` mit PDBs, ohne `appsettings.Development.json`, ohne Python-Werkzeuge.
  - Client: Scan-Build der `PluginStore.zxt` und der `AddonStoreLink.exe` (x64, Debug, PDB), nie in den Store.
  - Auslöser: Tags `S*` (Server) und `C*` (Client), wöchentlicher Regel-Scan, von Hand.
- **Im Modul als zwei feste Einträge** "Store-Server" und "Store-Client" unter den Verknüpfungen, mit Scan-Ergebnissen, Funden und Fristen wie bei den Plug-ins. Gleiches Repo, getrennt über die Tag-Muster.
- **Keine automatische Freigabe** für beide: Der Server geht weiter über die eigene CI auf UAT und Prod, der Client weiter nur mit Marcus' OK.
- Die vorbereiteten Mitigation-Texte aus `docs/security-review-2026-10.md` gehen beim ersten echten Scan in Veracode ein.
- Beim Umzug aufs Firmen-Git zieht das Store-Repo mit um; die Sichtbarkeit bleibt privat bzw. wird nur mit Marcus geändert.

## 4. Sicherheit

- Webhook nur mit gültiger Signatur (`X-Hub-Signature-256`, zeitkonstanter Vergleich), jede Zustell-ID nur einmal.
- Der Store glaubt keiner Angabe im Webhook, sondern fragt GitHub mit seinem eigenen Zugang nach.
- Nur Tags, keine Branches; empfohlen: geschützte Tags im Repo.
- Artefakte sind fremde Daten: Größenlimit, gleiche Prüfungen wie ein Upload, nie ausgeführt.
- Geheimnisse (App-Schlüssel, Webhook-Geheimnis, Veracode-Schlüssel) verschlüsselt mit den vorhandenen Data-Protection-Schlüsseln, nie angezeigt, nie im Log, im Backup nur verschlüsselt.
- API-Adressen nur HTTPS, vom Admin eingetragen.
- Keine neue Fremdbibliothek: GitHub-API, JWT für die App-Anmeldung und Veracode-HMAC mit .NET-Bordmitteln.

## 5. Schritte

| Schritt | Inhalt | Ergebnis |
|---|---|---|
| 1 | Modul-Gerüst, Schalter, Einstellungsseite mit Git-Verbindung, Veracode, Verknüpfungen; Verbinden und Testen | man kann verbinden und verknüpfen, es passiert noch nichts automatisch |
| 1a | "Außer Betrieb nehmen": Server-Teil (Pausiert, Zurück auf letzte gute Version, Wiedervorlage) und Client-Release (Abschalten ohne Deinstallation) | Notbremse steht, unabhängig von Git und Veracode |
| 1b | Veracode-Workflow im Store-Repo für Server und Client | Store selbst wird regelmäßig gescannt |
| 2 | Zentraler Workflow (`addon-workflows`), Pilot Measure Tools im privaten Test-GitHub; Webhook, Import, Scan-Ergebnisse | Tag setzen ergibt eine eingereichte Version mit Scan-Kästchen |
| 3 | Regel-Scans, Fristen, Erinnerungen, Badge, Entwickleransicht | laufende Überwachung |
| 4 | Automatische Freigabe | Updates nach der Erstfreigabe ohne Handarbeit |
| 5 | Umzug aufs Firmen-Git: Verbindung umstellen, Repos neu verknüpfen, Veracode-Zugang der Firma | produktiver Betrieb |
| später | weitere Git-Anbieter, externe Entwickler mit eigenem Repo | |

Jeder Schritt ist ein eigenes Server-Release, erst auf UAT, mit xUnit-Tests, Doku und Agent-Guide.

## 6. Offene Punkte

1. **Test-GitHub für den Pilot:** privates Repo unter `mnimtz`?
2. **Veracode-Zugang im Pilot:** Gibt es schon API-Zugangsdaten? Ohne sie läuft im Pilot an Stelle von Veracode die Store-eigene CWE-Prüfung, der Veracode-Schritt wird später eingehängt.
3. **Power PDF SDK im Build:** Die GitHub-Runner brauchen das Plugin SDK. Darf es in ein privates Repo oder Paket, oder braucht es einen eigenen Runner?
4. **Standardstufe der automatischen Freigabe:** Beta oder live?
5. **Fristablauf:** warnen, kennzeichnen oder sperren?
6. **Firmen-Git:** GitHub (Enterprise Cloud oder Server) oder etwas anderes? Davon hängt ab, ob für Schritt 5 ein zweiter Anbieter nötig ist.
7. **Stilllegen:** Notfall-Rolle ja/nein? Kunden-Admins bei "Außer Betrieb" automatisch benachrichtigen?
