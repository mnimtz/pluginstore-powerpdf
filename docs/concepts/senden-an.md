# Konzept: „Senden an" (Dokumente an Kollegen in Power PDF)

Stand: Okt 9, 2026 · Status: Entwurf zur Besprechung (noch nichts umgesetzt)
Umfang: Phase 1, nur **internes** Senden. Externe Empfänger folgen später
(siehe Ausblick).

## Idee in einem Satz

In Power PDF auf **Senden an** klicken, einen Kollegen aus der Liste wählen,
senden. Beim Kollegen erscheint in Power PDF „Neues Dokument von Marcus
Nimtz. Annehmen?", ein Klick öffnet es.

## Ziele

- Ein Dokument erreicht einen Kollegen, ohne Power PDF zu verlassen
- Die Kollegenliste zeigt nur Personen aus dem eigenen Unternehmen, die das
  Add-on nutzen
- Ist Power PDF beim Empfänger geschlossen, wartet das Dokument und kommt beim
  nächsten Start
- Tungsten sieht keinen Inhalt und keinen Dateinamen (Ende-zu-Ende-verschlüsselt)
- Keine IT-Beteiligung pro Nutzer, aber volle Steuerung per Richtlinie

Nicht in Phase 1: externe Empfänger, iPhone, von Admins gepflegte
Firmenverteiler, Arbeitsaufträge mit Rücklauf (siehe offene Fragen).
Eigene Verteilerlisten der Nutzer sind Teil von Phase 1.

## Abgrenzung

Was Power PDF heute schon kann (E-Mail-Versand, Cloud-Connectoren), wird
nicht nachgebaut. Der Mehrwert liegt hier:

| Heute (Outlook, Teams) | Mit „Senden an" |
|---|---|
| Datei speichern, Mail öffnen, anhängen, Empfänger tippen | Ein Klick aus dem offenen Dokument |
| Empfänger sucht den Anhang, speichert, öffnet | „Annehmen" öffnet direkt in Power PDF |
| Inhalt liegt im Postfach, in Kopien, im Archiv | Inhalt liegt verschlüsselt und nur bis zur Abholung beim Dienst |
| Keine Rückmeldung | Absender sieht „zugestellt" und „angenommen" |

## Nutzerabläufe

### A. Automatische Anmeldung bei der Installation

Wer den Add-on Store **und** das Add-on „Senden an" installiert hat, ist
automatisch als Empfänger wählbar. Es gibt keinen Einrichtungsschritt.

1. Beim ersten Start nach der Installation liest das Add-on die E-Mail aus
   der Identity und leitet daraus den Namen ab (`marcus.nimtz@...` →
   „Marcus Nimtz", Regeln siehe Identität).
2. Es erzeugt sein Schlüsselpaar und meldet Name, E-Mail, Domain und
   öffentlichen Schlüssel beim Dienst an.
3. Ab sofort erscheint der Benutzer in der Kollegenliste aller anderen
   Mitglieder seiner Kreise.
4. Im Panel steht einmalig ein Hinweis (kein Popup): „Kollegen sehen dich
   als **Marcus Nimtz** (marcus.nimtz@tungstenautomation.com). Bearbeiten ·
   Ausblenden". Über **Bearbeiten** (Panel oder Optionen) korrigiert jeder
   seinen Anzeigenamen jederzeit selbst.

Deinstallation des Add-ons meldet das Gerät ab; hat der Benutzer kein
Gerät mehr, verschwindet er aus der Liste.

### B. Senden

Hauptweg ist das Panel **„Senden an"** in der linken Leiste von Power PDF
(Aufbau siehe Abschnitt „Das Add-on"):

1. Dokument offen, linke Leiste, Panel „Senden an", Bereich **Kollegen**.
2. Suchfeld oben; darunter Favoriten, eigene Verteilerlisten, zuletzt
   verwendet, dann alle Kollegen desselben Kunden (seitenweise).
3. Einen oder mehrere Kollegen bzw. eine Verteilerliste anklicken,
   optional Notiz („Bitte bis Freitag prüfen").
4. **Senden**. Das aktuelle Dokument wird lokal verschlüsselt und
   hochgeladen. Fortschritt und Status erscheinen im Bereich **Gesendet**.

Zweitweg für Nutzer, die das Panel geschlossen haben: Ribbon-Button
**Senden an** öffnet das Panel bzw. einen kompakten Auswahldialog mit
derselben Suche.

Verteilerliste als Empfänger: Die Liste wird beim Senden aufgelöst, jedes
Mitglied erhält eine eigene Übertragung mit eigenem Status. Mitglieder ohne
eingerichtetes Add-on werden vor dem Senden angezeigt und übersprungen.

Ungespeicherte Änderungen: Das Add-on sendet den aktuellen Stand, nicht die
Datei auf der Platte (vorher Kopie in einen temporären Ordner speichern).

### C. Empfangen

1. Das Add-on erhält die Meldung über eine offene Verbindung (oder beim Start).
2. Hinweis ohne Unterbrechung: Badge am Ribbon-Button plus Eintrag im
   Bereich **Eingang** des Panels „Neues Dokument von Marcus Nimtz:
   Vertrag.pdf, Notiz ...".
   Kein modales Fenster mitten in der Arbeit.
3. **Annehmen**: Download, Entschlüsseln, Absender prüfen, Datei im
   Benutzerprofil ablegen, in Power PDF öffnen, Bestätigung an den Dienst.
   **Ablehnen**: Dienst löscht, Absender erfährt es.
4. Der Absender sieht im Panel: zugestellt, angenommen bzw. abgelehnt.

### D. Ablauf ohne Annahme

Nach der Vorhaltezeit (Standard 7 Tage, einstellbar) löscht der Dienst die Übertragung, der
Absender sieht „nicht angenommen, gelöscht".

## Freischaltung: privates Add-on über Auslieferungen

„Senden an" ist bewusst ein **privates Add-on** (`visibility: "private"`).
Es erscheint nie im öffentlichen Katalog. Ein Kunde bekommt es erst, wenn
ein Store-Admin es ihm im Portal unter **Kunden → Auslieferungen**
freischaltet (Mechanismus aus `kundenauslieferung.md`).

**Ablauf beim Store-Admin:**

1. Kunde anlegen bzw. öffnen (Kundencode vorhanden).
2. Auslieferung „Senden an" anlegen, Stufen Beta/Live wie bei jedem
   privaten Add-on. Auch über Auslieferungs-Vorlagen möglich.
3. Auf der Auslieferung erscheint der Bereich **„Senden an: Optionen"**
   (nur bei diesem Add-on): Funktion für diesen Kunden aktiv ja/nein,
   erlaubte Wege, Fristen, Grenzen, Schnell-Senden. Standard: aktiv aus.
   Erst wenn der Admin hier aktiviert, nimmt der Server Anmeldungen dieses
   Kunden an.

**Zwei Schlüssel nötig, damit es beim Kunden läuft:**

| Stufe | Wer | Wirkung |
|---|---|---|
| Auslieferung vorhanden | Store-Admin | Add-on erscheint im Store-Client des Kunden und lässt sich installieren |
| Option „aktiv" auf der Auslieferung | Store-Admin | Server nimmt Geräte dieses Kunden an, Kollegenliste und Senden funktionieren |

Zusätzlich gilt der globale Hauptschalter `SendTo.Enabled`.

**Prüfung auf dem Server:** Das Add-on meldet sich mit dem Kundencode an,
den der Store-Client bereits hat (Richtlinie `Policies\Store\CustomerCode`
oder DPAPI-gespeichert in den Optionen; das Add-on nutzt dieselbe
Lesefunktion `PSCustomerCodes()`). Der Server prüft bei jeder Anmeldung und
bei jedem Abruf:

- Kundencode gültig und Kunde nicht gesperrt
- Auslieferung „Senden an" für diesen Kunden aktiv (Zeitraum, nicht beendet)
- Option „aktiv" auf der Auslieferung gesetzt

Sonst Antwort `SENDTO_NOT_ENABLED`; das Add-on blendet sich dann wie beim
abgeschalteten Hauptschalter vollständig aus (siehe Server-Einstellungen). Endet die Auslieferung oder wird die Option
abgeschaltet, sind die Geräte dieses Kunden sofort gesperrt; wartende
Relay-Sendungen werden gelöscht.

Für den Pilot wird Tungsten selbst als Kunde „Tungsten intern" angelegt.

## Identität und Unternehmensgrenze

Das ist der wichtigste Punkt des Konzepts: Wer erscheint in wessen Liste, und
woran erkennt der Dienst, dass jemand wirklich zu diesem Unternehmen gehört?

**Wer ist wählbar: Kreise.** Die Kollegenliste ergibt sich aus Kreisen,
nicht direkt aus dem Kunden. Der Kundencode regelt nur, wer das Add-on
nutzen darf (Freischaltung); der Kreis regelt, wer wen sieht und wem
senden darf.

| Kreis | Entsteht | Mitglieder |
|---|---|---|
| **Standard-Kreis** | Automatisch für jeden freigeschalteten Kunden | Alle Benutzer dieses Kunden (Kundencode), deren E-Mail zu den Domains des Kunden passt; ohne Domain-Eintrag gilt die Domain des ersten Benutzers |
| **Weitere Kreise** | Store-Admin legt sie mit eigenem **Kreis-Code** an | Alle, die den Kreis-Code haben, auch **aus verschiedenen Kunden** (z. B. Tungsten und ein Partner, Konzernmutter und Töchter, eine Abteilung für sich) |

- Ein Benutzer kann in **mehreren Kreisen** sein (Entscheidung Okt 9, 2026).
  Das Panel gruppiert die Kollegenliste nach Kreis.
- Kreis-Code verteilen: Richtlinie `Policies\SendTo\CircleCodes` (Liste)
  oder Eintrag in den Optionen des Add-ons.
- Pro Kreis optional **erlaubte Domains**: ein weitergegebener Code allein
  reicht dann nicht.
- Codes lassen sich erneuern (Übergangszeit wie beim Kundencode) und sperren;
  der Admin sieht die Mitglieder je Kreis und kann einzelne entfernen.

**Kundenübergreifende Kreise** schicken Dokumente in ein anderes
Unternehmen. Deshalb:

1. Option „Kreise mit anderen Kunden erlaubt" auf der Auslieferung jedes
   beteiligten Kunden, Standard **aus**. Ohne sie wird ein Benutzer dieses
   Kunden einem gemischten Kreis nicht zugeordnet.
2. Es gilt der **strengste** Wert der beteiligten Kunden (Vorhaltezeit,
   Grenzen, erlaubte Wege).
3. Mitglieder anderer Kunden zeigt das Panel mit Firmenname
   („Frank Giessler · Partner GmbH"), damit niemand unbemerkt nach außen sendet.
4. Vor dem Einsatz bei Kunden durch Datenschutz und Legal bewerten (Namen,
   Adressen und Dokumente gehen an Dritte).

Externe Empfänger **ohne** Add-on bleiben Thema des Ausblicks (Handler).

**Was bei der Installation erfasst wird:**

| Angabe | Quelle (in dieser Reihenfolge) |
|---|---|
| E-Mail | `HKCU\...\Identity\email` (Anmeldung am Cloud License Server, bestätigt Okt 8, 2026) |
| Domain | Teil der E-Mail nach dem @ |
| Name (Anzeigename) | Aus der E-Mail abgeleitet (Regeln unten); passt die Adresse nicht ins Muster, Windows-Anzeigename (`GetUserNameExW` mit `NameDisplay`); jederzeit vom Benutzer über **Bearbeiten** korrigierbar |

**Name aus der E-Mail ableiten** (Vorgabe Marcus, Okt 9, 2026). Die Identity
lautet meist `vorname.nachname@domain.de`:

| Adresse (Teil vor dem @) | Abgeleiteter Name |
|---|---|
| `marcus.nimtz` | Marcus Nimtz |
| `anna-lena.schmidt` | Anna-Lena Schmidt |
| `max_mustermann` | Max Mustermann |
| `peter.mueller2` | Peter Mueller (Ziffern entfallen; „Müller" nur per Bearbeiten) |
| `jan.van.der.berg` | Jan Van Der Berg (per Bearbeiten zu „Jan van der Berg") |
| `mnimtz`, `info` (nur ein Teil) | kein Muster: Windows-Anzeigename, sonst der Teil groß geschrieben |

Regeln: an `.`, `_` und `-` trennen (Bindestrich bleibt im Namen erhalten),
jeden Teil groß beginnen, Ziffern entfernen. Umlaute und Namenszusätze
lassen sich aus der Adresse nicht sicher erkennen; dafür gibt es Bearbeiten.

**Bearbeiten (Anzeigename):**

- Erreichbar oben im Panel („Ich: Marcus Nimtz · Bearbeiten"), im
  Ersthinweis und in der Optionsseite.
- Nur der Anzeigename ist änderbar; die E-Mail kommt aus der Identity und
  bleibt fest.
- Prüfung auf dem Server: 2 bis 64 Zeichen, Leerzeichen am Rand entfernt,
  keine Steuerzeichen, keine Adressen oder Links im Namen.
- Die Änderung ist sofort bei allen Kollegen sichtbar; einmal korrigiert,
  überschreibt die automatische Ableitung den Namen nicht mehr.
- „Zurücksetzen" stellt den abgeleiteten Namen wieder her.
- Damit niemand sich per Anzeigename als jemand anderes ausgibt, zeigt die
  Kollegenliste immer **Name und E-Mail** (zweite Zeile bzw. Tooltip), und
  eingehende Dokumente nennen den Absender mit E-Mail.

**Kein zusätzlicher Fälschungsschutz in Phase 1** (Entscheidung Marcus,
Okt 9, 2026). Begründung:

- Das Add-on richtet sich an Power PDF als SaaS. Dort ist `Identity\email`
  die Anmeldung, mit der Power PDF startet. Stimmt der Wert nicht oder wird
  er geändert, startet Power PDF nicht mehr; eine gefälschte Adresse lässt
  sich also nicht unbemerkt nutzen.
- Der Registry-Wert ist in Unternehmen in der Regel durch die IT geschützt.
- Folge: Neue Benutzer können sofort senden und empfangen, ohne
  Bestätigungsmail.
- Das Add-on läuft nur bei Lizenzart cloud (SaaS). Ohne gültige
  Identity-Anmeldung zeigt es einen Hinweis statt der Kollegenliste.
- Freemail-Domains (gmail.com, outlook.com, web.de, ...) werden abgelehnt.

Später nachrüstbar, falls nötig: Bestätigungslink per Mail oder Anmeldung
über Microsoft Entra ID (Tenant-ID als Grenze). Entra verlangt bei vielen
Kunden eine Admin-Freigabe der App im Tenant.

- Mehrere Domains eines Unternehmens (z. B. Tochterfirmen) trägt der
  Store-Admin auf der Kundenseite ein.

## Verschlüsselung

| Schritt | Verfahren |
|---|---|
| Schlüssel pro Gerät und Windows-Benutzer | X25519, privater Schlüssel per DPAPI an das Windows-Konto gebunden |
| Inhalt | Zufälliger Dateischlüssel, AES-256-GCM in Blöcken zu 1 MB (fortsetzbar) |
| Umschlag (Dateiname, Notiz, Dateischlüssel) | HPKE (RFC 9180) mit dem öffentlichen Schlüssel jedes Empfängergeräts |
| Absendernachweis | Signatur des Umschlags (Ed25519), Empfänger prüft gegen den bekannten Absenderschlüssel |

- Hat ein Empfänger mehrere PCs, entsteht pro Gerät ein Umschlag; der Inhalt
  wird nur einmal hochgeladen.
- **Vertrauen in Schlüssel:** Der Dienst verteilt die öffentlichen Schlüssel.
  Phase 1 nutzt „Vertrauen beim ersten Kontakt": Das Add-on merkt sich den
  Schlüssel eines Kollegen und warnt deutlich, wenn er sich ändert. Ein
  Fingerabdruck-Vergleich ist in den Kontaktdetails möglich.
- **Bibliothek:** Windows CNG kann AES-GCM und X25519, aber kein HPKE. HPKE
  nicht selbst zusammensetzen. Vorschlag: OpenSSL ab 3.2 (Apache-2.0, eigene
  HPKE-API). Das ist auch das Format, das CryptoKit auf iOS spricht, falls das
  iPhone später Absender wird. Auslieferung als eigene DLL über den
  bin/-Mechanismus des Stores (S1.4.0).

**Was der Dienst sieht:** Absender, Empfänger, Größe, Zeitpunkte, Status.
**Nicht:** Inhalt, Dateiname, Notiz.

## Transportwege (Hybrid)

Das Dokument soll das Unternehmen möglichst nicht verlassen. Deshalb gibt es
drei Wege, die der Store-Admin auf dem Server einzeln freischaltet (siehe
Server-Einstellungen):

| Weg | Ablauf | Dokument verlässt das Unternehmen |
|---|---|---|
| **Direkt (P2P)** | Beide online: Verbindung PC zu PC über WebRTC (nur ausgehende Verbindungen, keine Firewall-Freigabe). Der Store-Server vermittelt nur (wer ist online, Verbindungsaufbau), er sieht kein Dokument | Nein, solange beide im Firmennetz oder VPN sind |
| **Warten** | Empfänger offline: Das Dokument bleibt verschlüsselt beim Absender; Zustellung direkt, sobald beide gleichzeitig online sind | Nein |
| **Relay** | Standardweg: verschlüsselte Ablage im Azure Blob Storage bis zur Abholung | Ja, verschlüsselt (Tungsten sieht nur Metadaten) |

Reihenfolge im Add-on: zuerst Direkt (falls erlaubt und beide online);
klappt das nicht, Relay; ist Relay für den Kunden abgeschaltet, Warten. Der Absender sieht im Bereich Gesendet, auf
welchem Weg zugestellt wurde bzw. dass die Sendung wartet.

Nachteil von „Warten": Absender und Empfänger müssen gleichzeitig online
sein. Fährt der Absender seinen PC herunter, verzögert sich die Zustellung.

**WebRTC:** WebView2 (bereits im Store-Client im Einsatz) bringt WebRTC mit;
das Add-on würde eine unsichtbare WebView2-Instanz als Datenkanal nutzen.
**Im Spike zu prüfen**, ob das im Plug-in-Prozess zuverlässig läuft und in
welchen Netzen der Direktweg klappt (Firmennetz, VPN, Homeoffice). Eigene
WebRTC-Bibliotheken scheiden vorerst aus: die gängigen schlanken stehen
unter MPL-2.0 und sind nach unserer Lizenzregel nicht frei gegeben.
Die Ende-zu-Ende-Verschlüsselung (oben) gilt auf allen drei Wegen gleich.

## Server: Erweiterung des Add-on Stores

Der Store-Server ist schon da, spricht nur HTTPS über 443 und kennt jeden
Client. Phase 1 erweitert ihn, statt einen neuen Dienst zu betreiben.

### API (Entwurf)

| Methode und Pfad | Zweck |
|---|---|
| `POST /api/sendto/devices` | Automatische Anmeldung beim ersten Start: Kundencode, Name, E-Mail, öffentliche Schlüssel; Prüfung der Freischaltung; liefert Geräte-Token |
| `PATCH /api/sendto/me` | Anzeigenamen ändern oder zurücksetzen, Sichtbarkeit an/aus |
| `DELETE /api/sendto/devices/{id}` | Gerät abmelden (Deinstallation oder Entfernen aus den Optionen) |
| `GET /api/sendto/contacts?q=&circle=&page=` | Kollegen aus den eigenen Kreisen suchen (seitenweise, nach Kreis gruppiert) |
| `POST /api/sendto/circles/join` | Kreis-Code einlösen |
| `GET/POST /api/sendto/admin/circles`, `PUT/DELETE .../{id}` | Kreise verwalten (Store-Admin) |
| `GET /api/sendto/contacts/{id}/devices` | Öffentliche Schlüssel der Geräte eines Kollegen |
| `GET/POST /api/sendto/lists`, `PUT/DELETE /api/sendto/lists/{id}` | Eigene Verteilerlisten und Favoriten verwalten |
| `GET/POST /api/sendto/quick`, `PUT/DELETE /api/sendto/quick/{id}` | Schnellziele verwalten |
| `POST /api/sendto/transfers/{id}/cancel` | „Rückgängig" innerhalb der Wartezeit |
| `POST /api/sendto/transfers` | Übertragung anlegen, Umschläge je Empfängergerät |
| `PUT /api/sendto/transfers/{id}/chunks/{n}` | Verschlüsselten Block hochladen |
| `GET /api/sendto/inbox` | Wartende Übertragungen für dieses Gerät |
| `GET /api/sendto/transfers/{id}/chunks/{n}` | Block herunterladen |
| `POST /api/sendto/transfers/{id}/accept` / `decline` | Annahme bzw. Ablehnung, löst Löschung aus |
| `GET /api/sendto/config` | Gültige Einstellungen für den Kunden des Geräts (global plus Kunden-Optionen) |
| `GET/PUT /api/customers/{cid}/deliveries/{did}/sendto` | Kunden-Optionen lesen/setzen (Admin, gleiche Rechte wie Auslieferungen) |
| `POST /api/sendto/signal` | Vermittlung für den Direktweg (Verbindungsangebote zwischen zwei Geräten, nur Metadaten) |
| `GET /api/sendto/events` | Server-Sent Events für neue Übertragungen und Statusänderungen |

Fallback ohne dauerhafte Verbindung (Proxy): Abfrage alle 60 Sekunden.
Die neuen Endpunkte gehören wie alle anderen in Agent-Guide, Schema und
API-Seite (Dauerregel).

### Datenmodell (Kern)

| Objekt | Felder |
|---|---|
| (Customer, vorhanden) | Store-Kunde mit Kundencode; neu: Domains für „Senden an" |
| SendToCustomerOptions | delivery_id, aktiv (ja/nein), Kreise mit anderen Kunden erlaubt (ja/nein), erlaubte Wege, Frist, Grenzen, Schnell-Senden (leer = global) |
| Circle | id, Name, Art (Standard eines Kunden oder eigener Kreis), customer_id (nur Standard), Code-Hash, alter Code-Hash + Ablauf, erlaubte Domains, gesperrt |
| CircleMember | circle_id, user_id, beigetreten |
| SendToUser | id, customer_id, E-Mail, Domain, Anzeigename, Namensquelle (E-Mail, Windows, Benutzer), abgeleiteter Name (für Zurücksetzen), sichtbar (ja/nein), erstellt |
| DistributionList | id, owner_user_id, Name, Favorit (ja/nein), erstellt, geändert |
| DistributionListMember | list_id, user_id |
| QuickTarget | id, owner_user_id, Ziel (user_id oder list_id), Beschriftung, feste Notiz, Tastenkürzel, im Ribbon (ja/nein), Reihenfolge |
| SendToDevice | id, user_id, öffentlicher Schlüssel (X25519 + Ed25519), Token-Hash, zuletzt gesehen |
| Transfer | id, absender_device_id, Größe, Blockanzahl, erstellt, zustellen_ab (Rückgängig-Fenster), läuft_ab |
| Envelope | transfer_id, empfänger_device_id, Umschlag, Signatur, Status (wartend, angenommen, abgelehnt, abgelaufen) |

### Eigenes Menü „Senden an" im Portal

Alles zu „Senden an" bündelt ein eigener Menüpunkt im Store-Portal
(nur Admins), Unterseiten mit Navigation links:

| Unterseite | Inhalt |
|---|---|
| Übersicht | Aktive Kunden, Kreise, Benutzer, Geräte, wartende Übertragungen, Speicherbelegung im Blob |
| Kreise | Kreise anlegen, Code anzeigen/erneuern/sperren, erlaubte Domains, Mitglieder entfernen |
| Kunden | Alle Kunden mit Auslieferung „Senden an", Status aktiv, Absprung zu den Kunden-Optionen |
| Einstellungen | Globale Werte (Tabelle unten) |
| Protokoll | Audit-Einträge zu „Senden an" (ohne Inhalte, ohne Dateinamen) |

### Server-Einstellungen (Store-Admin)

Unter **Senden an → Einstellungen**. Gespeichert über den vorhandenen
Settings-Dienst, jede Änderung im Audit (`settings.changed`).

**Hauptschalter aus = alles verschwindet** (Vorgabe Marcus, Okt 9, 2026):

| Ort | Bei `SendTo.Enabled` = aus |
|---|---|
| Portal | Menü „Senden an" ausgeblendet, ebenso der Bereich „Senden an: Optionen" auf Auslieferungen, Kennzahlen, Hinweise. Einzige Stelle, die bleibt: der Hauptschalter selbst unter `/Admin/Settings` → „Funktionen", damit man wieder einschalten kann |
| Öffentliche Seiten, API-Seite, Agent-Guide | Abschnitt „Senden an" wird nicht ausgegeben; die Quelltexte bleiben vollständig (Docs-Sync-Prüfung unverändert) |
| API | Alle `/api/sendto/*`-Aufrufe antworten `SENDTO_DISABLED` |
| Add-on | Ribbon-Gruppe, Panel, Optionsseite und Badge verschwinden (siehe „Wirkung im Add-on") |

| Einstellung | Wirkung | Standard |
|---|---|---|
| `SendTo.Enabled` | **Hauptschalter:** Funktion für alle Kunden komplett an oder aus | aus |
| `SendTo.Direct` | Direktweg (P2P) erlaubt | an |
| `SendTo.Wait` | Warten beim Absender erlaubt | an |
| `SendTo.Relay` | Zwischenablage im Blob Storage erlaubt (Standardweg) | an |
| `SendTo.Retention` | **Vorhaltezeit** bis zur automatischen Löschung nicht abgeholter Dokumente; wählbar 1 Stunde bis 30 Tage (Stunden oder Tage) | 7 Tage |
| `SendTo.MaxFileMB` / `SendTo.MaxPendingMB` | Grenzen pro Datei / pro Benutzer wartend (Relay) | 100 / 1024 |
| `SendTo.QuickSend` | Schnell-Senden erlaubt | an |

Die globalen Werte sind Standard und Obergrenze. Pro Kunde überschreibt der
Admin sie im Bereich „Senden an: Optionen" der Auslieferung (siehe
Freischaltung), z. B. Relay für eine Bank aus. Der Kunden-Schalter „aktiv"
ist zusätzlich zum Hauptschalter nötig.

**Wirkung im Add-on:**

- Das Add-on holt die gültigen Einstellungen beim Start und erhält
  Änderungen über die offene Verbindung (`GET /api/sendto/config`, Ereignis
  `config.changed`).
- **Hauptschalter aus oder Kunde nicht (mehr) freigeschaltet:** alles zu
  „Senden an" verschwindet, nichts wird nur ausgegraut:
  - Ribbon-Buttons sofort unsichtbar (`RVToolButtonSetComputeVisibleProc`);
    **im Spike prüfen**, ob der Host die Gruppe ausblendet, wenn alle ihre
    Buttons unsichtbar sind. Sonst entfernt das Add-on die Gruppe beim
    nächsten Start aus dem Benutzer-Layout (gleicher Weg wie der Store-Client
    beim Eintragen).
  - Panel und Optionsseite werden beim Start nur registriert, wenn der
    zuletzt bekannte Zustand „an" ist (lokal zwischengespeichert). Eine
    Abschaltung während der Sitzung leert das Panel sofort; Panel und
    Optionsseite fehlen ab dem nächsten Start.
  - Kein Hinweis, keine Meldung, kein Badge.
  - Der Server lehnt alle `/api/sendto/*`-Aufrufe mit `SENDTO_DISABLED` ab,
    Vermittlung für P2P inklusive. Wartende Sendungen werden gelöscht.
  - Wird wieder eingeschaltet, erscheint alles beim nächsten Abruf bzw.
    Start wieder.
- **Einzelner Weg aus:** Das Add-on nutzt nur die erlaubten Wege; ist gar
  kein Weg mehr erlaubt, gilt das wie Hauptschalter aus.
- Server-Einstellungen sind die oberste Stufe. Die Richtlinien beim Kunden
  (unten) können nur weiter einschränken, nie etwas freischalten, was der
  Server verbietet.

### Ablage und Grenzen

**Ablage im Azure Blob Storage** (Entscheidung Marcus, Okt 9, 2026): Jedes
Dokument liegt verschlüsselt im Blob Storage, bis der Empfänger es abgeholt
hat, und wird danach gelöscht. Metadaten bleiben in der bestehenden
Datenbank.

| Punkt | Umsetzung |
|---|---|
| Verschlüsselung, Schicht 1 | Ende-zu-Ende auf dem Absender-PC (AES-256-GCM, Umschlag per HPKE). Im Blob liegen nur Chiffretext-Blöcke; Tungsten hat keinen Schlüssel |
| Verschlüsselung, Schicht 2 | Azure Storage Service Encryption (AES-256, immer an). Optional später mit eigenem Schlüssel im Key Vault (customer-managed key) |
| Speicherort | Eigener privater Container `sendto`, kein öffentlicher Zugriff, Blob-Name = zufällige ID (kein Dateiname), Region EU |
| Zugriff | Nur der Store-Server über Managed Identity (Rolle „Storage Blob Data Contributor" nur auf diesen Container). Clients laden über den Store-Server hoch und herunter: ein Host, ein Zertifikat, funktioniert durch Firmen-Proxys, passt zur Host-Bindung des Clients |
| Löschung nach Abholung | Sofort nach „Annehmen" bzw. „Ablehnen" (alle Empfänger durch) |
| Löschung nach Frist | Hintergrunddienst wie `UsageMaintenance` löscht abgelaufene Übertragungen und meldet „nicht zugestellt" |
| Zweite Sicherung | Lifecycle-Regel auf dem Container löscht jeden Blob spätestens nach der maximal einstellbaren Frist plus einem Tag, auch wenn der Server ausfällt |
| Kein Wiederherstellen | Soft Delete und Versionierung für diesen Speicher **aus**, sonst bliebe ein „gelöschtes" Dokument noch Tage abrufbar. Deshalb eigenes Storage-Konto `sendto` statt des Kontos mit dem /data-Share |
| Bereitstellung | `infra/azuredeploy.json` um Storage-Konto, Container, Lifecycle-Regel und Rollenzuweisung erweitern |

Damit ist **Relay der Standardweg**: Dokumente warten verschlüsselt im Blob,
auch wenn der Absender seinen PC ausschaltet.
- Startwerte: 100 MB pro Datei, 1 GB wartend pro Benutzer, Ratenbegrenzung
- **Backup:** Übertragungen sind kurzlebig und für den Server unlesbar. Sie
  werden bewusst **nicht** gesichert (Ausnahme von der Backup-Regel,
  entschieden Okt 9, 2026). Kunden-Optionen, Benutzer, Geräte und Verteilerlisten kommen
  ins Backup.
- **Verteilerlisten liegen auf dem Server**, damit sie auf allen PCs eines
  Nutzers gleich sind. Sie enthalten nur Verweise auf Kollegen desselben
  Kunden und sind nur für ihren Besitzer sichtbar (Phase 1; geteilte
  Listen später).

## Das Add-on

- Eigenes Store-Add-on „Senden an" (Arbeitsname), Gruppe
  `FeaturePack::SendTo` auf dem gemeinsamen Register, x64 Pflicht
- **Panel „Senden an" in der linken Leiste** (`RVAppRegisterNavigationPanel`,
  Vorlage `EnhancedFeaturePack/bookmarks/panel.cpp`) als zentrale Oberfläche
  mit drei Bereichen:

  | Bereich | Inhalt |
  |---|---|
  | Kollegen | Suchfeld, Favoriten, eigene Verteilerlisten, zuletzt verwendet, alle Kollegen (seitenweise); Mehrfachauswahl, Notiz, Senden |
  | Eingang | Neue und angenommene Dokumente, Annehmen, Ablehnen, Öffnen |
  | Gesendet | Status je Empfänger: zugestellt, angenommen, abgelehnt, abgelaufen |

  Verteilerlisten im Bereich Kollegen anlegen, umbenennen, löschen;
  Mitglieder per Suche hinzufügen oder per Rechtsklick auf einen Kollegen
  („Zu Liste hinzufügen", „Als Favorit").

  **Seitenleiste als Möglichkeit** (Wunsch Marcus, Okt 9, 2026): Das Panel
  ist wählbar, nicht Pflicht.

  | Einstellung | Wirkung |
  |---|---|
  | Optionen: „In der Seitenleiste anzeigen" (Standard an) | Panel wird registriert; Ribbon-Button **Seitenleiste** blendet es je Dokument ein und aus |
  | aus | Kein Panel; alles läuft über den Dialog „Senden an" und den Eingang im Dialog (gleiche Bereiche Kollegen, Eingang, Gesendet) |
  | Richtlinie `Policies\SendTo\NavigationPanel` | Admin erzwingt an oder aus |

  Technik nach dem erprobten Rezept (FP2, PanelDemo-Sample, Power PDF
  2025.3 FP8): Handler nur `procCreate` + `procGetIcon`, Registrierung in
  `PluginInit`, Anzeige über Ribbon-Toggle mit `RVDocShowNavigationPanel`,
  Layout in `WM_SIZE` und `WM_WINDOWPOSCHANGED`. Da das Panel beim Start
  registriert wird, wirkt das Umschalten ab dem nächsten Start.

  **Bekannte Host-Einschränkung:** Ein eigenes Icon in der Panel-Leiste
  (`procGetIcon`) beschädigt in 2025.3 FP8 native Panel-Icons. Bis Kofax das
  behebt, wird mit `procGetIcon = NULL` ausgeliefert: Das Panel hat dann
  kein eigenes Symbol in der Leiste und öffnet sich über den Ribbon-Button
  **Seitenleiste**. Im Spike mit der aktuellen Power-PDF-Version erneut
  prüfen (16x16 statt 24x24 war ein offener Testpunkt).
- Ribbon-Gruppe, Schnell-Senden und Optionen: siehe nächster Abschnitt
- Netzwerk in einem eigenen Worker-Thread; Host-Aufrufe nur im UI-Thread
- Abholung nur, solange Power PDF läuft. Ein Hintergrunddienst ist in
  Phase 1 nicht vorgesehen.

## Design: angelehnt an den Add-on Store

Vorgabe Marcus (Okt 9, 2026): Das Add-on sieht aus wie das Store-Fenster
des Add-on Stores, damit beides als eine Produktfamilie wirkt.

| Element | Umsetzung |
|---|---|
| Technik | Wie das Store-Fenster: WebView2-Seite als RCDATA-Ressource (Vorbild `client/ui/store.html` + `client/store/webui.cpp`), Texte aus der rc per JSON, Daten nur per `textContent`, Navigation gesperrt |
| Farben | Gleiche Tokens wie `store.html`: `--navy #002854`, `--navy2`, `--blue #00A0FB`, `--green #00EB86`, `--ink`, `--slate`, `--line`, `--soft`, `--sel` (Brand Book) |
| Schrift | Wie Store-Fenster: „Segoe UI Variable Text", Segoe UI, Arial; RTL-Regel für Arabisch übernehmen |
| Bausteine | Listen, Suchfeld, Chips, Statuszeile, Buttons und In-Page-Dialoge aus dem Store-Fenster wiederverwenden (keine Browser-Popups) |
| Wo | Panel in der linken Leiste (WebView2 als Kindfenster im Panel), Dialog „Senden an", Hinweis „Gesendet · Rückgängig" |
| Optionsseite | Ziel ebenfalls WebView2 im Store-Stil; **im Spike prüfen**, ob WebView2 in der Power-PDF-Optionsseite stabil läuft, sonst native Steuerelemente in Store-Farben |
| Ohne WebView2-Runtime | Klassischer Fallback wie beim Store-Client (`ClassicUI`) |
| Sprachen | 21 Power-PDF-Sprachen wie der Store |

Synergie: Dieselbe WebView2-Instanz kann den Direktweg (WebRTC) tragen.
Gemeinsames CSS und gemeinsame Bausteine gehören in eine gemeinsame Datei,
die Store-Client und Add-on beide einbinden, damit das Design nicht
auseinanderläuft.

## Ribbon-Gruppe und Schnell-Senden

### Buttons

Gruppe „Senden an" auf dem Register „Erweiterte Funktionen":

| Button | Wirkung |
|---|---|
| **Senden an** (groß) | Öffnet den Auswahldialog bzw. das Panel: Suche, Kollegen, Favoriten, Verteilerlisten, Notiz, Senden. Badge bei neuen Eingängen |
| **Seitenleiste** (klein, Umschalter) | Blendet das Panel „Senden an" in der linken Leiste ein/aus; nur sichtbar, wenn die Seitenleiste in den Optionen aktiv ist |
| **Schnell-Buttons** (klein, z. B. „An Frank", „An Team Vertrieb") | Senden das aktuelle Dokument **ohne weitere Interaktion** an ein vorher festgelegtes Ziel |
| **Schnell senden ▾** (klein, mit Menü) | Listet alle Schnellziele, auch die, die nicht als eigener Button angezeigt werden |
| **Optionen** (klein) | Öffnet die Optionsseite „Senden an" |

### Schnell-Senden

Ein Schnellziel wird in den Optionen angelegt:

| Feld | Inhalt |
|---|---|
| Ziel | Ein Kollege oder eine eigene Verteilerliste |
| Beschriftung | Text auf dem Button, Vorschlag „An <Vorname>" |
| Feste Notiz | Optional, z. B. „Bitte gegenzeichnen" |
| Tastenkürzel | Optional, z. B. Strg+Alt+1 (`RVToolButtonSetShortKey`) |
| Als Button im Ribbon | ja/nein; sonst nur im Menü „Schnell senden" |
| Reihenfolge | Verschieben nach oben/unten |

Ablauf beim Klick auf „An Frank":

1. Das Add-on sendet sofort, kein Dialog.
2. Unten im Panel bzw. in der Statuszeile erscheint ohne Popup „An Frank
   Giessler gesendet · **Rückgängig**". Der Dienst stellt erst nach 10
   Sekunden zu; bis dahin zieht „Rückgängig" die Sendung zurück. Das
   schützt vor dem Fehlklick, ohne eine Bestätigungsfrage einzubauen.
3. Fehler (Empfänger abgemeldet, Datei zu groß, offline) zeigt das Add-on
   als Meldung, nur dann mit Fenster.

Ein Schnellziel, dessen Empfänger nicht mehr existiert, wird ausgegraut; der Tooltip nennt den Grund.

Schnellziele liegen wie Verteilerlisten auf dem Server und sind damit auf
allen PCs des Nutzers gleich.

### Technische Umsetzung im Ribbon

Die Ribbon-Struktur steht in `Publish.xml` und wird vom Host per Atom-Namen
zwischengespeichert. Dynamisch neue Buttons zur Laufzeit gehen damit nicht
zuverlässig. Deshalb:

- **Feste Plätze:** Das Add-on registriert fünf Schnell-Plätze
  `FeaturePack::SendTo::Quick1` bis `Quick5` von Anfang an.
- Unbelegte Plätze blendet `RVToolButtonSetComputeVisibleProc` aus.
- Beschriftung und Hilfetext setzt das Add-on beim Start und nach Änderungen
  über `RVToolButtonSetLabelText` / `RVToolButtonSetHelpText`. **Im Spike
  prüfen**, ob der Host eine geänderte Beschriftung nach der Init sofort
  zeigt (bei `RVToolButtonSetIcon` ist das belegt).
- Mehr als fünf Ziele oder falls die Laufzeit-Beschriftung nicht greift:
  Button „Schnell senden" mit Menü (`RVToolButtonSetMenu`), das alle Ziele
  listet. Das ist der sichere Rückfallweg.
- Die Gruppe trägt der Store-Client selbst ins Layout ein (Host-Merge-Falle,
  seit C1.9.4 gelöst).

### Optionsseite

| Bereich | Inhalt |
|---|---|
| Mein Eintrag | Anzeigename mit Bearbeiten und Zurücksetzen, E-Mail (aus der Power-PDF-Anmeldung, nur Anzeige), „In der Kollegenliste sichtbar" |
| Schnell-Senden | Liste der Schnellziele: Hinzufügen, Bearbeiten, Entfernen, Reihenfolge (Felder siehe oben) |
| Verteilerlisten | Eigene Listen verwalten (gleich wie im Panel) |
| Ansicht | „In der Seitenleiste anzeigen" ja/nein |
| Empfang | Ablageordner, angenommene Dokumente automatisch öffnen ja/nein |
| Geräte | Eigene angemeldete PCs, Gerät entfernen |

`RVAppRegisterPrefsType` immer zusammen mit `RVAppRegisterPrefsPage`
registrieren (sonst Absturz beim Öffnen der Kategorie).

## Richtlinien für Admins

Unter `HKLM\...\FeaturePack\Policies\SendTo` (gleiches Muster wie im
Feature Pack, schlägt HKCU):

| Wert | Wirkung |
|---|---|
| `Enabled` | Funktion an oder aus |
| `AllowedDomains` | Nur diese Empfänger-Domains (Einschränkung innerhalb des Kunden) |
| `Visible` | Sichtbarkeit in der Kollegenliste erzwingen oder verbieten |
| `MaxSizeMB` | Kleinere Dateigrenze als der Server |
| `RetentionHours` | Kürzere Vorhaltezeit als der Server (nur verkürzen) |
| `QuickSend` | Schnell-Senden erlauben oder verbieten |
| `UndoSeconds` | Wartezeit für „Rückgängig" (0 bis 60 Sekunden) |
| `NavigationPanel` | Seitenleiste erzwingen an oder aus |
| `LockPage` | Optionsseite sperren |

## Sicherheit

| Bedrohung | Gegenmaßnahme |
|---|---|
| Angriff auf den Server | Nur verschlüsselte Blöcke und Umschläge, kein privater Schlüssel auf dem Server |
| Jemand gibt sich per Registry als Kollege aus | Power PDF (SaaS) startet mit falscher Identity nicht; Registry in der Regel durch die IT geschützt; Freemail abgelehnt. Kein eigener Schutz in Phase 1 (bewusste Entscheidung) |
| Server schiebt einen fremden Schlüssel unter | Schlüssel-Merken plus Warnung bei Änderung, Fingerabdruck in den Kontaktdetails |
| Gestohlener PC | Schlüssel per DPAPI an das Windows-Konto gebunden, Gerät aus der Optionsseite eines anderen PCs entfernbar |
| Manipulierte Datei | AES-GCM erkennt Änderungen, Signatur bestätigt den Absender |
| Schadhaftes PDF (JavaScript, eingebettete Dateien) | Nur nach bewusstem „Annehmen", Absender sichtbar und geprüft, Defender prüft die abgelegte Datei; Abstimmung mit dem Malware-Konzept |
| Missbrauch als Speicher oder Spam | Nur innerhalb desselben Kunden, nur nach Freischaltung, Kontingente, Ratenbegrenzung, kurze Fristen, Kontakt blockieren |

## Datenschutz

- **Neue Datenarten auf dem Server:** Name, E-Mail-Adresse und Domain pro
  Nutzer (automatisch bei der Installation erfasst), Geräteschlüssel,
  Verteilerlisten, Übertragungs-Metadaten, verschlüsselte Dokumente bis zur
  Abholung. Das ist dem Datenschutz zu melden; der Store ist grundsätzlich
  geklärt, diese Datenarten sind neu.
- Die Erfassung ist **sichtbar**, nicht heimlich: Die Store-Beschreibung des
  Add-ons nennt sie, und das Panel zeigt beim ersten Start, unter welchem
  Namen der Benutzer sichtbar ist. Erfasst wird nur, was das Add-on für die
  Funktion braucht (kein Rechnername, keine Windows-Kontodaten außer dem
  Anzeigenamen).
- Die Kollegenliste zeigt Namen und Adressen nur innerhalb der eigenen
  Domain. Sichtbarkeit ist abschaltbar (Nutzer und Richtlinie); Abmelden
  bzw. Deinstallieren entfernt den Eintrag.
- Keine Dateinamen, keine Inhalte in Protokollen.

## Phasen

| Phase | Umfang |
|---|---|
| 1a Spike | WebRTC über WebView2 im Plug-in (Firmennetz, VPN, Homeoffice), lokaler Server, zwei Windows-Benutzer, Senden und Annehmen mit Verschlüsselung, Panel |
| 1b MVP | Server-Endpunkte im Store, Freischaltung über Auslieferungen, automatische Anmeldung, Kollegenliste, Richtlinien, Store-Paket, Pilot intern |
| 2 Extern | Handler-Schnittstelle für externe Empfänger (eigener Abschnitt unten) |
| 3 Ausbau | iPhone als Absender, Arbeitsaufträge (gegenzeichnen, prüfen mit Rücklauf), geteilte Verteilerlisten, von Admins gepflegte Firmenverteiler (oder Übernahme aus Entra-ID-Gruppen) |

## Ausblick: externe Empfänger (nicht Phase 1)

Externe Empfänger laufen über eine **Handler-Schnittstelle** mit denselben
Operationen wie der interne Transport. Ausprägungen: Tungsten-gehostet,
kundeneigen (self-hosted) oder Adapter auf vorhandene Systeme des Kunden.
Empfänger ohne Add-on erhalten einen Link, dessen Schlüssel nur im
Link-Fragment steht. Dazu gehören zwingend: Richtlinie „extern aus / nur
erlaubte Domains / Freigabe durch zweite Person", sichtbare Markierung
externer Empfänger, Audit-Log, Ablauf und Widerruf, Bewertung durch Legal
und Compliance (DSGVO, Exportkontrolle). Wird in einem eigenen Konzept
ausgearbeitet.

## Entscheidungen

| Datum | Entscheidung |
|---|---|
| Okt 9, 2026 | Phase 1 nur internes Senden; extern später über Handler-Schnittstelle |
| Okt 9, 2026 | Keine Einrichtung: wer Store und Add-on installiert hat, ist automatisch wählbar; Name und Domain werden bei der Installation erfasst |
| Okt 9, 2026 | Kein zusätzlicher Fälschungsschutz (SaaS-Identity, IT-geschützt) |
| Okt 9, 2026 | Arbeitsaufträge (gegenzeichnen, prüfen) erst nach Phase 1 |
| Okt 9, 2026 | Übertragungen sind vom Backup ausgenommen |
| Okt 9, 2026 | Verteilerlisten in Phase 1 nur privat |
| Okt 9, 2026 | Hybrid-Transport: Direkt (P2P), Warten, Relay |
| Okt 9, 2026 | Dokumente liegen verschlüsselt im Azure Blob Storage bis zur Abholung; Vorhaltezeit bis zur automatischen Löschung global und je Kunde einstellbar |
| Okt 9, 2026 | Store-Admin schaltet die Funktion auf dem Server komplett an/aus (auch je Weg und je Kunde) |
| Okt 9, 2026 | Privates Add-on; Kunde bekommt es nur über eine Auslieferung, und die Funktion muss dort zusätzlich als Option aktiviert werden |
| Okt 9, 2026 | Gruppen = Kreise: Standard-Kreis je Kunde, weitere Kreise mit eigenem Code, auch kundenübergreifend (Opt-in je Kunde); mehrere Kreise pro Benutzer |
| Okt 9, 2026 | Anzeigename aus `vorname.nachname@` abgeleitet, vom Benutzer per Bearbeiten korrigierbar; Liste zeigt immer Name und E-Mail |
| Okt 9, 2026 | Panel in der linken Navigationsleiste als wählbare Möglichkeit (Optionen + Richtlinie), Dialog bleibt immer verfügbar |
| Okt 9, 2026 | Design des Add-ons wie das Store-Fenster (WebView2, gleiche Brand-Tokens und Bausteine) |
| Okt 9, 2026 | Eigenes Portal-Menü „Senden an"; Hauptschalter aus = alle Menüs, Hinweise, Funktionen und Register verschwinden (Portal und Add-on) |

## Offene Fragen

- [ ] Grenzen (100 MB pro Datei, 1 GB wartend pro Benutzer) passend?
- [ ] Direkt (P2P) und Warten zusätzlich behalten, oder nur Relay über Blob?
- [ ] Sollen Kunden-Admins später selbst Mitglieder sperren oder Domains
      pflegen können (heute nur Store-Admin)?
- [ ] Fünf feste Schnell-Plätze im Ribbon ausreichend?
- [ ] Teil des Add-on Stores oder später eigener Dienst, falls die Last steigt?
