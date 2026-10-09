# Konzept: „Senden an" (Dokumente an eigene Kontakte in Power PDF)

Stand: Okt 9, 2026 · Status: Entwurf zur Besprechung (noch nichts umgesetzt)
Grundprinzip: **Einladung und gegenseitige Bestätigung**. Jeder baut sein
eigenes Adressbuch auf; der Server braucht dafür kaum Admin-Arbeit.

## Idee in einem Satz

In Power PDF auf **Senden an** klicken, einen Kontakt aus dem eigenen
Adressbuch wählen, senden. Beim Empfänger erscheint in Power PDF „Neues
Dokument von Marcus Nimtz. Annehmen?", ein Klick öffnet es. Kontakte kommen
per Einladung ins Adressbuch: E-Mail-Adresse eingeben, der andere bestätigt,
fertig.

## Ziele

- Ein Dokument erreicht einen Kontakt, ohne Power PDF zu verlassen
- Senden nur an Personen, die **zugestimmt** haben (bestätigte Kontakte)
- Funktioniert innerhalb einer Firma und firmenübergreifend gleich
- Möglichst **keine Admin-Arbeit** auf dem Server (keine Gruppen, Codes,
  Domainlisten)
- Ist Power PDF beim Empfänger geschlossen, wartet das Dokument und kommt beim
  nächsten Start
- Tungsten sieht keinen Inhalt und keinen Dateinamen (Ende-zu-Ende-verschlüsselt)
- Firmen-IT kann die Funktion per Richtlinie sperren oder auf die eigene
  Domain begrenzen
- Verteilung über die normalen Store-Wege: gezielte Auslieferung, Beta
  oder öffentlich

Nicht in Phase 1: Empfänger ohne Add-on (Link-Zustellung), iPhone,
Arbeitsaufträge mit Rücklauf, geteilte Verteilerlisten.

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

### A. Anmeldung bei der Installation

Es gibt keinen Einrichtungsschritt.

1. Beim ersten Start liest das Add-on die E-Mail aus der Identity und leitet
   daraus den Namen ab (`marcus.nimtz@...` → „Marcus Nimtz", Regeln siehe
   Identität).
2. Es erzeugt sein Schlüsselpaar und meldet Name, E-Mail und öffentlichen
   Schlüssel beim Dienst an.
3. Der Benutzer ist damit **für niemanden sichtbar**. Es gibt kein
   Verzeichnis; nur bestätigte Kontakte sehen Name und E-Mail.
4. Im Bereich steht einmalig ein Hinweis (kein Popup): „Du erscheinst bei
   deinen Kontakten als **Marcus Nimtz** (marcus.nimtz@tungstenautomation.com).
   Bearbeiten · Ausblenden".
5. Liegen für diese Adresse bereits Einladungen vor, erscheinen sie sofort
   (siehe B).

Deinstallation meldet das Gerät ab. Hat der Benutzer kein Gerät mehr,
bleiben seine Kontakte bestehen, er kann aber nichts empfangen, bis er
wieder ein Gerät anmeldet (Absender sehen „derzeit nicht erreichbar").

### B. Kontakt einladen

1. Im Bereich „Senden an", Abschnitt **Kontakte**: **Kontakt einladen**,
   E-Mail-Adresse eingeben (mehrere getrennt durch Komma oder Zeilenumbruch
   möglich).
2. **Wer einlädt, hat damit schon zugestimmt** (Vorgabe Marcus, Okt 9, 2026).
   Die Einladung ist von seiner Seite aus angenommen.
3. Der Eingeladene bekommt:
   - eine **E-Mail**: „Marcus Nimtz (marcus.nimtz@tungstenautomation.com)
     möchte mit dir Dokumente über Power PDF austauschen. **Bestätigen** ·
     Ablehnen"
   - und, falls er das Add-on schon hat, die Anfrage zusätzlich direkt im
     Bereich (Abschnitt Kontakte, „Anfragen") mit **Annehmen / Ablehnen**.
4. **Annehmen** (per Link oder im Add-on): Beide stehen sofort gegenseitig
   im Adressbuch und können sich in **beide Richtungen** senden.
5. **Annahme per Link, auch vor der Installation** (Entscheidung Marcus,
   Okt 9, 2026: Variante A mit Button). Der Link öffnet eine Webseite auf dem
   Store-Server („Einladung von Marcus Nimtz"). Angenommen wird erst mit dem
   Button **Annehmen** auf dieser Seite (POST), **nie schon durch das Öffnen
   des Links**: Mail-Sicherheitsscanner (z. B. „Sichere Links") öffnen Links
   automatisch und dürfen dabei nichts annehmen. Hat der Eingeladene das
   Add-on noch nicht, zeigt die Seite danach den Weg zum Add-on Store; sobald
   er das Add-on mit dieser Adresse anmeldet, ist der Kontakt ohne weiteren
   Klick aktiv.
6. Der Einladende sieht den Stand im Abschnitt Kontakte: eingeladen,
   angenommen, **angenommen, noch nicht eingerichtet** (Senden erst möglich,
   wenn der andere ein Gerät angemeldet hat, weil ohne seinen Schlüssel
   nichts für ihn verschlüsselt werden kann), abgelehnt, abgelaufen.

Regeln:

- Einladungen verfallen nach 14 Tagen (einstellbar).
- Höchstens 20 offene Einladungen pro Person (einstellbar).
- Nach einer Ablehnung kann dieselbe Person den Eingeladenen 30 Tage lang
  nicht erneut einladen.
- Laden sich zwei Personen gegenseitig ein, ist der Kontakt sofort aktiv.
- Die Mail-Bestätigung belegt nebenbei, dass die eingeladene Adresse dem
  Empfänger gehört.

### C. Senden

Ribbon-Button **„Senden an"** öffnet den Bereich **rechts** neben dem
Dokument:

1. Abschnitt **Kontakte**: Suchfeld, Favoriten, eigene Verteilerlisten,
   zuletzt verwendet, alle bestätigten Kontakte.
2. Einen oder mehrere Kontakte bzw. eine Verteilerliste anklicken, optional
   Notiz („Bitte bis Freitag prüfen").
3. **Senden**. Das aktuelle Dokument wird lokal verschlüsselt und
   hochgeladen. Status im Abschnitt **Gesendet**.

Ein zweiter Klick auf **Senden an** schließt den Bereich. Wer in den
Optionen „kein Bereich" gewählt hat, bekommt einen kompakten Dialog mit
denselben Inhalten.

Verteilerliste als Empfänger: Die Liste wird beim Senden aufgelöst, jedes
Mitglied erhält eine eigene Übertragung mit eigenem Status. Listen enthalten
nur bestätigte Kontakte.

Ungespeicherte Änderungen: Das Add-on sendet den aktuellen Stand, nicht die
Datei auf der Platte (vorher Kopie in einen temporären Ordner speichern).

### D. Empfangen

1. Das Add-on erhält die Meldung über eine offene Verbindung (oder beim Start).
2. Hinweis ohne Unterbrechung: Badge am Ribbon-Button plus Eintrag im
   Abschnitt **Eingang** „Neues Dokument von Marcus Nimtz
   (marcus.nimtz@...): Vertrag.pdf, Notiz ...". Kein modales Fenster.
3. **Annehmen**: Download, Entschlüsseln, Absender prüfen, Datei im
   Benutzerprofil ablegen, in Power PDF öffnen, Bestätigung an den Dienst.
   **Ablehnen**: Dienst löscht, Absender erfährt es.
4. Der Absender sieht: zugestellt, angenommen bzw. abgelehnt.

### E. Kontakt entfernen, blockieren, melden

| Aktion | Wirkung |
|---|---|
| **Entfernen** (einer der beiden) | Die Erlaubnis erlischt **in beide Richtungen**. Noch nicht abgeholte Sendungen zwischen den beiden werden gelöscht. Der andere sieht den Kontakt nicht mehr; eine neue Einladung ist jederzeit möglich |
| **Blockieren** | Wie Entfernen, zusätzlich keine Einladungen dieser Person mehr |
| **Melden** | Blockieren plus Meldung an die Store-Admins (z. B. Spam, schadhafte Dokumente) |

Entfernen und Blockieren gehen auch bei offenen Anfragen.

### F. Ablauf ohne Annahme

Nach der Vorhaltezeit (Standard 7 Tage, einstellbar) löscht der Dienst die
Übertragung; der Absender sieht „nicht angenommen, gelöscht".

## Verteilung: über die normalen Store-Wege

Weil jede Verbindung von beiden Seiten bestätigt wird, hängt die Funktion
nicht davon ab, wie das Add-on verteilt wird. Die Verteilung entscheidet der
Store-Admin später im Portal mit den üblichen Möglichkeiten (Vorgabe Marcus,
Okt 9, 2026):

| Weg | Wer bekommt das Add-on |
|---|---|
| **Auslieferung (privat)** | Gezielt einzelne Kunden über Kunden → Auslieferungen, mit Beta- und Live-Stufe (Mechanismus aus `kundenauslieferung.md`); z. B. zuerst nur „Tungsten intern" als Pilot |
| **Beta** | Alle mit eingeschaltetem Beta-Kanal |
| **Öffentlich** | Alle im Katalog |

Der Code ist in allen Fällen derselbe; es ändert sich nur die Sichtbarkeit
des Pakets.

Folge bei gezielter Auslieferung: Wer bei einer Firma ohne Auslieferung
eingeladen wird, kann das Add-on nicht installieren. Die Einladungsmail
erklärt das dann neutral („Senden an ist für dein Unternehmen noch nicht
verfügbar, frag deine IT"); die Einladung bleibt bis zu ihrem Ablauf
gültig.

Vor einer **öffentlichen** Verteilung müssen Kontingente, Blockieren/Melden,
Sperren im Portal und die Rechtsprüfung stehen (siehe Datenschutz).

Steuerung unabhängig vom Verteilweg:

- **Store-Admin:** Hauptschalter, Sperren einzelner Benutzer oder Domains.
- **Firmen-IT:** Richtlinien `Enabled`, `InviteDomains`, `BlockExternal`.

## Identität und Name

| Angabe | Quelle |
|---|---|
| E-Mail | `HKCU\...\Identity\email` (Anmeldung am Cloud License Server, bestätigt Okt 8, 2026) |
| Name (Anzeigename) | 1. `HKCU\...\Identity\name` (Name des Cloud-License-Kontos, enthält Umlaute und Namenszusätze richtig); 2. aus der E-Mail abgeleitet (Regeln unten); 3. Windows-Anzeigename (`GetUserNameExW` mit `NameDisplay`); jederzeit über **Bearbeiten** korrigierbar (Reihenfolge entschieden Okt 9, 2026) |

**Name aus der E-Mail ableiten** (Rückfall, wenn `Identity\name` fehlt). Die Identity
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
jeden Teil groß beginnen, Ziffern entfernen.

**Bearbeiten (Anzeigename):**

- Oben im Bereich („Ich: Marcus Nimtz · Bearbeiten"), im Ersthinweis und in
  der Optionsseite.
- Nur der Anzeigename ist änderbar; die E-Mail kommt aus der Identity.
- Server prüft: 2 bis 64 Zeichen, Rand-Leerzeichen entfernt, keine
  Steuerzeichen, keine Adressen oder Links.
- Sofort bei allen Kontakten sichtbar; einmal korrigiert, überschreibt die
  automatische Ableitung den Namen nicht mehr. „Zurücksetzen" stellt den
  abgeleiteten Namen wieder her.
- Adressbuch, Anfragen und Eingang zeigen immer **Name und E-Mail**, damit
  sich niemand per Anzeigename als jemand anderes ausgibt.

**Kein zusätzlicher Fälschungsschutz für die eigene Identität** (Entscheidung
Marcus, Okt 9, 2026): Power PDF SaaS startet nicht, wenn `Identity\email`
falsch ist oder geändert wird, und der Wert ist in Firmen meist durch die IT
geschützt. Das Add-on läuft nur bei Lizenzart cloud. Die Adresse des
**Eingeladenen** belegt zusätzlich der Klick auf den Bestätigungslink.

Freemail-Adressen (gmail.com, outlook.com, web.de, ...) sind **erlaubt**
(Entscheidung Marcus, Okt 9, 2026): Die Identity stammt aus der
SaaS-Anmeldung; wer bei uns nicht als Kunde geprüft ist, hat keinen Zugang
zu Power PDF.

## Verschlüsselung

| Schritt | Verfahren |
|---|---|
| Schlüssel pro Gerät und Windows-Benutzer | X25519, privater Schlüssel per DPAPI an das Windows-Konto gebunden |
| Inhalt | Zufälliger Dateischlüssel, AES-256-GCM in Blöcken zu 1 MB (fortsetzbar) |
| Umschlag (Dateiname, Notiz, Dateischlüssel) | HPKE (RFC 9180), Suite DHKEM(X25519, HKDF-SHA256) / HKDF-SHA256 / AES-128-GCM, mit dem öffentlichen Schlüssel jedes Empfängergeräts |
| Absendernachweis | Signatur des Umschlags (ECDSA P-256), Empfänger prüft gegen den bekannten Absenderschlüssel |

- Hat ein Empfänger mehrere PCs, entsteht pro Gerät ein Umschlag; der Inhalt
  wird nur einmal hochgeladen.
- **Kein gemeinsamer Schlüssel** für Gruppen oder Listen: eigener Schlüssel
  pro Dokument, verpackt für jedes einzelne Empfängergerät. Wer entfernt
  wird, erhält einfach keine neuen Umschläge.
- **Vertrauen in Schlüssel:** Der Dienst verteilt die öffentlichen Schlüssel
  nur zwischen bestätigten Kontakten. Das Add-on merkt sich den Schlüssel
  eines Kontakts beim ersten Mal und warnt deutlich, wenn er sich ändert;
  Fingerabdruck-Vergleich in den Kontaktdetails.
- **Bibliothek: nur Windows CNG** (Ergebnis des Spikes, Okt 9, 2026). Die
  Store-Regel erlaubt nur Windows-DLLs als Importe; CNG (`bcrypt.dll`) ist
  FIPS-validiert und bringt X25519, AES-GCM, HMAC und ECDSA mit. HPKE ist aus
  diesen Bausteinen in einer festen Suite und nur im Basismodus aufgebaut und
  wird bei jedem Build gegen den offiziellen Testvektor RFC 9180 A.1.1 und
  RFC 7748 geprüft (`sendto_cli selftest`). OpenSSL entfällt damit (keine
  fremde DLL, kein bin/-Paket). CNG hat kein Ed25519, deshalb ECDSA P-256 für
  Signaturen. Die gewählte HPKE-Suite gibt es auch in CryptoKit (iOS).

| Schicht | Schlüssel | Wirkung |
|---|---|---|
| 1. Ende-zu-Ende | Pro Dokument, verpackt pro Empfängergerät (HPKE) | Nur die Empfänger können lesen; Tungsten nie |
| 2. Speicher (Azure) | Standard-Verschlüsselung von Azure Storage (AES-256, immer an, kostenlos) | Schutz der Datenträger im Rechenzentrum |
| 3. Transport | TLS 1.2+ | Absicherung unterwegs |

**Was der Dienst sieht:** Wer mit wem verbunden ist, Absender, Empfänger,
Größe, Zeitpunkte, Status. **Nicht:** Inhalt, Dateiname, Notiz.

## Transport: nur Relay über den Store-Server

Entscheidung Marcus (Okt 9, 2026): Der Absender lädt das verschlüsselte
Dokument über den Store-Server in den Azure Blob Storage, der Empfänger holt
es dort ab. Gründe: Kontakte sind oft firmenübergreifend (PCs verschiedener
Firmen erreichen sich direkt praktisch nie), der Server wird für Adressbuch
und Einladungen ohnehin gebraucht, Zustellung klappt auch bei
ausgeschaltetem Absender-PC, und es braucht kein WebRTC. Ein Direktweg PC zu
PC (P2P) war geprüft und ist verworfen.

### Netzwerk und Firewall (Okt 9, 2026)

- **Kein neuer Port.** Das Add-on spricht wie der Store-Client nur
  ausgehend HTTPS über 443 mit der Store-Adresse. Kunden mit Store-Client
  haben diese Freigabe schon. Die Ports 5299/5391/5392 gibt es nur in der
  lokalen Entwicklung.
- **Nichts eingehend.** Kein PC nimmt Verbindungen an; neue Dokumente holt
  das Add-on per Abfrage. Der Server verbindet sich nie zu einem PC.
- **Proxy:** WinHTTP mit automatischem Proxy (Windows-Einstellung). Ein
  TLS-prüfender Firmenproxy sieht nur verschlüsselte Blöcke (E2E).
- **Blob nur serverseitig.** Die Clients erreichen den Blob-Speicher nie;
  der Server reicht 1-MiB-Blöcke durch. Damit greifen auch keine
  Größengrenzen für einzelne Anfragen.
- **Azure Web App:** dieselbe App auf 443. Hat das Storage-Konto eine
  Netzwerk-Firewall, muss die Web App dort zugelassen sein
  (VNet-Integration oder ihre ausgehenden IPs), wie heute schon beim
  Cloud-Backup. Kommen später Server-Sent Events, braucht der Strom ein
  Lebenszeichen unter 230 Sekunden (Leerlaufgrenze von App Service).
- **Einladungslink:** öffnet die Store-Seite im Browser, ebenfalls 443.

## Server: Erweiterung des Add-on Stores

Der Store-Server ist schon da, spricht nur HTTPS über 443 und kennt jeden
Client.

### API (Entwurf)

| Methode und Pfad | Zweck |
|---|---|
| `POST /api/sendto/devices` | Anmeldung beim ersten Start: Name, E-Mail, öffentliche Schlüssel; liefert Geräte-Token |
| `DELETE /api/sendto/devices/{id}` | Gerät abmelden |
| `PATCH /api/sendto/me` | Anzeigenamen ändern oder zurücksetzen |
| `GET /api/sendto/contacts?q=&page=` | Eigene bestätigte Kontakte |
| `DELETE /api/sendto/contacts/{id}` | Kontakt entfernen (wirkt in beide Richtungen) |
| `POST /api/sendto/contacts/{id}/block`, `.../report` | Blockieren, Melden |
| `GET /api/sendto/contacts/{id}/devices` | Öffentliche Schlüssel der Geräte eines Kontakts (nur bei bestätigtem Kontakt) |
| `POST /api/sendto/invitations` | Einladen (eine oder mehrere Adressen) |
| `GET /api/sendto/invitations` | Eigene offene Einladungen (gesendet und erhalten) |
| `POST /api/sendto/invitations/{id}/accept`, `.../decline`, `DELETE .../{id}` | Annehmen, Ablehnen, Zurückziehen |
| `GET /sendto/invite/{token}` | Bestätigungsseite aus der Mail (Webseite, kein Login); zeigt nur an, ändert nichts |
| `POST /sendto/invite/{token}` | Button Annehmen oder Ablehnen auf der Seite (mit Anti-Forgery-Token) |
| `GET/POST /api/sendto/lists`, `PUT/DELETE /api/sendto/lists/{id}` | Eigene Verteilerlisten und Favoriten |
| `GET/POST /api/sendto/quick`, `PUT/DELETE /api/sendto/quick/{id}` | Schnellziele |
| `POST /api/sendto/transfers` | Übertragung anlegen, Umschläge je Empfängergerät |
| `PUT /api/sendto/transfers/{id}/chunks/{n}` | Verschlüsselten Block hochladen |
| `POST /api/sendto/transfers/{id}/cancel` | „Rückgängig" innerhalb der Wartezeit |
| `GET /api/sendto/inbox` | Wartende Übertragungen für dieses Gerät |
| `GET /api/sendto/transfers/{id}/chunks/{n}` | Block herunterladen |
| `POST /api/sendto/transfers/{id}/accept`, `.../decline` | Annahme bzw. Ablehnung, löst Löschung aus |
| `GET /api/sendto/config` | Gültige Einstellungen |
| `GET /api/sendto/events` | Server-Sent Events: neue Übertragungen, Anfragen, Statusänderungen, `config.changed` |
| `GET/PUT /api/sendto/admin/...` | Portal-Funktionen (Sperren, Meldungen, Einstellungen), nur Store-Admins |

Jeder Senden-Aufruf prüft serverseitig, dass Absender und Empfänger
bestätigte Kontakte sind und keiner den anderen blockiert hat. Fallback ohne
dauerhafte Verbindung (Proxy): Abfrage alle 60 Sekunden. Die Endpunkte
gehören wie alle anderen in Agent-Guide, Schema und API-Seite (Dauerregel).

### Datenmodell (Kern)

| Objekt | Felder |
|---|---|
| SendToUser | id, E-Mail, Domain, Anzeigename, Namensquelle (E-Mail, Windows, Benutzer), abgeleiteter Name (für Zurücksetzen), gesperrt, erstellt |
| SendToUser.customer_id | optional: zugehöriger Store-Kunde (aus dem Kundencode des Store-Clients), nur für den kundeneigenen Speicher |
| CustomerStorage | customer_id, Blob-Endpunkt, Container, Zugriff (SAS, `dp:`-verschlüsselt, mit Ablaufdatum), Status der letzten Prüfung |
| SendToDevice | id, user_id, öffentlicher Schlüssel (X25519 + ECDSA P-256), Token-Hash, zuletzt gesehen |
| Invitation | id, von_user_id, an_email, an_user_id (sobald bekannt), Token-Hash, Status (offen, angenommen, abgelehnt, abgelaufen, zurückgezogen), erstellt, läuft_ab |
| Contact | user_a_id, user_b_id (ein Eintrag pro Paar), seit |
| Block | von_user_id, blockiert_user_id oder E-Mail, erstellt |
| Report | von_user_id, betrifft_user_id, Grund, erstellt, Status (offen, erledigt) |
| DistributionList | id, owner_user_id, Name, Favorit (ja/nein), erstellt, geändert |
| DistributionListMember | list_id, user_id (nur bestätigte Kontakte) |
| QuickTarget | id, owner_user_id, Ziel (user_id oder list_id), Beschriftung, feste Notiz, Tastenkürzel, im Ribbon (ja/nein), Reihenfolge |
| Transfer | id, absender_device_id, Speicher (Tungsten oder customer_id), Größe, Blockanzahl, erstellt, zustellen_ab (Rückgängig-Fenster), läuft_ab |
| Envelope | transfer_id, empfänger_device_id, Umschlag, Signatur, Status (wartend, angenommen, abgelehnt, abgelaufen) |

Wird ein Kontakt entfernt, löscht der Server den Contact-Eintrag, alle
wartenden Envelopes zwischen den beiden und entfernt die Person aus den
Verteilerlisten und Schnellzielen des jeweils anderen (das Schnellziel wird
ausgegraut mit Hinweis).

### Eigenes Menü „Senden an" im Portal

Nur für Admins, Unterseiten mit Navigation links:

| Unterseite | Inhalt |
|---|---|
| Übersicht | Benutzer, Geräte, Kontakte, offene Einladungen, wartende Übertragungen, Speicherbelegung, versendete Mails |
| Meldungen | Gemeldete Benutzer mit Grund; Aktion: erledigt, Benutzer sperren |
| Sperren | Benutzer oder ganze Domains sperren und entsperren |
| Einstellungen | Globale Werte und Dokumentenspeicher (unten) |
| Protokoll | Audit-Einträge zu „Senden an" (ohne Inhalte, ohne Dateinamen) |

Eine laufende Pflege gibt es nicht: keine Gruppen, keine Codes, keine
Domainlisten. Admin-Arbeit entsteht nur bei Meldungen.

### Server-Einstellungen (Store-Admin)

Unter **Senden an → Einstellungen**, gespeichert über den vorhandenen
Settings-Dienst, jede Änderung im Audit (`settings.changed`).

| Einstellung | Wirkung | Standard |
|---|---|---|
| `SendTo.Enabled` | **Hauptschalter:** Funktion komplett an oder aus | aus |
| `SendTo.Retention` | **Vorhaltezeit** bis zur automatischen Löschung nicht abgeholter Dokumente; 1 Stunde bis 30 Tage | 7 Tage |
| `SendTo.MaxFileMB` / `SendTo.MaxPendingMB` | Grenzen pro Datei / pro Benutzer wartend | 100 / 1024 |
| `SendTo.QuickSend` | Schnell-Senden erlaubt | an |
| `SendTo.InviteExpiryDays` | Gültigkeit einer Einladung | 14 |
| `SendTo.MaxOpenInvites` | Offene Einladungen pro Person | 20 |
| `SendTo.MaxInvitesPerDay` | Neue Einladungen pro Person und Tag (gegen Spam, schont das Mailkontingent) | 30 |

**Hauptschalter aus = alles verschwindet** (Vorgabe Marcus, Okt 9, 2026):

| Ort | Bei `SendTo.Enabled` = aus |
|---|---|
| Portal | Menü „Senden an", Kennzahlen und Hinweise ausgeblendet. Es bleibt nur der Hauptschalter selbst unter `/Admin/Settings` → „Funktionen", damit man wieder einschalten kann |
| Öffentliche Seiten, API-Seite, Agent-Guide | Abschnitt „Senden an" wird nicht ausgegeben; die Quelltexte bleiben vollständig (Docs-Sync-Prüfung unverändert) |
| API | Alle `/api/sendto/*`-Aufrufe antworten `SENDTO_DISABLED`; Bestätigungsseite zeigt „derzeit nicht verfügbar" |
| Add-on | Ribbon-Gruppe, Bereich, Optionsseite und Badge verschwinden |

Für einen **gesperrten Benutzer** gilt im Add-on dasselbe (alles
verschwindet).

**Wirkung im Add-on:**

- Das Add-on holt die Einstellungen beim Start und erhält Änderungen über die
  offene Verbindung (`GET /api/sendto/config`, Ereignis `config.changed`).
- Bei abgeschaltet oder gesperrt verschwindet alles, nichts wird nur
  ausgegraut:
  - Ribbon-Buttons sofort unsichtbar (`RVToolButtonSetComputeVisibleProc`);
    **im Spike prüfen**, ob der Host die Gruppe ausblendet, wenn alle ihre
    Buttons unsichtbar sind. Sonst entfernt das Add-on die Gruppe beim
    nächsten Start aus dem Benutzer-Layout.
  - Bereich und Optionsseite werden beim Start nur registriert, wenn der
    zuletzt bekannte Zustand „an" ist (lokal zwischengespeichert). Eine
    Abschaltung während der Sitzung leert den Bereich sofort; ab dem
    nächsten Start fehlen beide.
  - Kein Hinweis, keine Meldung, kein Badge. Wartende Sendungen werden
    gelöscht.
  - Wird wieder eingeschaltet, erscheint alles beim nächsten Abruf bzw.
    Start wieder.
- Server-Einstellungen sind die oberste Stufe. Richtlinien der Firmen-IT
  können nur weiter einschränken.

### Mailversand

Einladungen gehen über den vorhandenen Resend-Weg des Stores, in der Sprache
des Einladenden bzw. der Browsersprache auf der Bestätigungsseite. Die Mail
enthält nur Name und Adresse des Einladenden und die Links, keine
Dokumentdaten. Ob das Resend-Kontingent für den öffentlichen Betrieb reicht,
ist vor dem Schritt „öffentlich" zu prüfen (offene Frage).

### Dokumentenspeicher anbinden

Wunsch Marcus (Okt 9, 2026): Unter **Senden an → Einstellungen** bindet der
Admin einen **eigenen Blob-Container nur für die Dokumente** an, getrennt
vom /data-Share des Stores (Datenbank, Pakete, Backups).

**Standard (kostengünstig):** ein neuer Container im **vorhandenen**
Storage-Konto des Stores. Keine neue Ressource, keine Grundgebühr; bezahlt
werden nur Speicher und Zugriffe der kurz liegenden Dokumente (erwartet im
Cent- bis niedrigen Euro-Bereich pro Monat, vor dem Betrieb mit der
Azure-Preisseite gegenrechnen). Ein anderes Storage-Konto lässt sich
genauso eintragen.

| Feld | Inhalt |
|---|---|
| Blob-Endpunkt | z. B. `https://<konto>.blob.core.windows.net` |
| Container | Standard `sendto` |
| Anmeldung | **Managed Identity** des App Service (empfohlen, kein Geheimnis gespeichert) oder Verbindungszeichenfolge / SAS als Rückfall |
| Status | Ergebnis der letzten Verbindungsprüfung, Belegung, Anzahl wartender Dokumente |

- **Geheimnisse:** Verbindungszeichenfolge bzw. SAS werden wie der
  Resend-Key per ASP.NET Data Protection verschlüsselt gespeichert
  (`dp:`-Präfix) und nie wieder angezeigt. Ein Restore auf einer anderen
  Instanz braucht dieselben Data-Protection-Schlüssel, sonst den Wert neu
  eintragen; Managed Identity vermeidet das.
- **„Verbindung testen"** prüft: Testdatei schreiben, lesen, löschen; Soft
  Delete und Versionierung für Blobs **aus**; Lifecycle-Regel vorhanden;
  kein öffentlicher Zugriff. Abweichungen mit Erklärung.
- **Ohne gültigen Speicher** lässt sich der Hauptschalter nicht einschalten.
- **Speicher wechseln:** Neue Sendungen gehen sofort in den neuen Speicher;
  der alte bleibt lesend angebunden, bis seine Dokumente abgeholt oder
  abgelaufen sind.
- Jede Änderung im Audit (ohne Geheimnis).

### Option: kundeneigener Dokumentenspeicher

Entscheidung Marcus (Okt 9, 2026): Ein Kunde kann **seinen eigenen Azure
Blob Storage** für die Dokumente nutzen. Dann liegen die Dokumente, die
seine Mitarbeiter versenden, in seinem Tenant statt bei Tungsten.

| Punkt | Umsetzung |
|---|---|
| Einrichten | Store-Admin öffnet im Portal den Kunden (Kunden → Kunde) und trägt im Bereich „Senden an: eigener Speicher" Blob-Endpunkt, Container und Zugriff ein; „Verbindung testen" wie beim Standardspeicher |
| Zugriff | Firmenübergreifend geht keine Managed Identity. Der Kunde stellt eine **SAS** nur für diesen Container aus (Lesen, Schreiben, Löschen, Auflisten; keine Kontorechte), mit Ablaufdatum; der Server warnt 30 Tage vor Ablauf per Mail und im Portal |
| Zuordnung | Der Store-Client kennt bereits den Kundencode des Kunden. Das Add-on meldet ihn bei der Anmeldung mit; so weiß der Server, zu welchem Kunden ein Absender gehört |
| Welcher Speicher | Immer der des **Absenders**: Hat seine Firma einen eigenen Speicher, liegt das Dokument dort, auch wenn der Empfänger in einer anderen Firma sitzt. Absender ohne eigenen Speicher nutzen den Tungsten-Standardspeicher |
| Speicher nicht erreichbar | Senden schlägt mit klarer Meldung fehl. **Kein stiller Rückfall** auf den Tungsten-Speicher, sonst lägen Daten doch bei Tungsten |
| Anforderungen an den Kunden-Speicher | Wie beim Standard: Soft Delete und Versionierung für Blobs aus, Lifecycle-Regel, kein öffentlicher Zugriff, Region nach Wahl des Kunden. Der Verbindungstest prüft das, soweit die SAS es erlaubt |
| Weg der Daten | Hoch- und Herunterladen laufen weiter über den Store-Server (ein Host, Proxys, Host-Bindung); er reicht die verschlüsselten Blöcke durch und speichert sie beim Kunden. Lesen kann sie dort niemand: Ende-zu-Ende-Verschlüsselung |
| Löschung | Wie beim Standard: nach Abholung, nach Frist, Lifecycle-Regel als zweite Sicherung |
| Wechsel oder Entfernen | Wie beim Standardspeicher: alter Speicher bleibt lesend angebunden, bis seine Dokumente abgeholt oder abgelaufen sind |
| Kosten | Speicher und Zugriffe zahlt der Kunde; für Tungsten keine |

Der Nutzen ist vor allem **Compliance** („liegt nie bei einem Dritten");
der Sicherheitsgewinn ist wegen der Ende-zu-Ende-Verschlüsselung gering.

### Ablage und Grenzen

Jedes Dokument liegt verschlüsselt im Blob Storage, bis der Empfänger es
abgeholt hat, und wird danach gelöscht. Metadaten bleiben in der
bestehenden Datenbank.

| Punkt | Umsetzung |
|---|---|
| Speicherort | Privater Container, kein öffentlicher Zugriff, Blob-Name = zufällige ID (kein Dateiname), Region EU |
| Zugriff | Nur der Store-Server. Clients laden über den Store-Server hoch und herunter: ein Host, ein Zertifikat, funktioniert durch Firmen-Proxys, passt zur Host-Bindung des Clients |
| Löschung nach Abholung | Sofort nach „Annehmen" bzw. „Ablehnen" (alle Empfänger durch) |
| Löschung nach Frist | Hintergrunddienst wie `UsageMaintenance` löscht abgelaufene Übertragungen und Einladungen |
| Zweite Sicherung | Lifecycle-Regel löscht jeden Blob spätestens nach der maximalen Frist plus einem Tag, auch wenn der Server ausfällt |
| Kein Wiederherstellen | Soft Delete und Versionierung **für Blobs** aus; unabhängig vom Soft Delete des /data-Shares (Azure Files), der unberührt bleibt |
| Bereitstellung | `infra/azuredeploy.json` legt im vorhandenen Konto Container, Lifecycle-Regel und Rollenzuweisung an |

**Bewusst ohne Encryption Scopes** (Entscheidung Marcus, Okt 9, 2026): Sie
kosten 1 US-Dollar pro Scope und Monat und bringen gegenüber Schicht 1 kaum
Sicherheitsgewinn.

- **Backup:** Übertragungen sind kurzlebig und für den Server unlesbar; sie
  werden bewusst **nicht** gesichert (Ausnahme von der Backup-Regel,
  entschieden Okt 9, 2026). Benutzer, Geräte, Kontakte, Einladungen,
  Sperren, Meldungen, Verteilerlisten und Schnellziele kommen ins Backup.
- **Verteilerlisten und Schnellziele liegen auf dem Server**, damit sie auf
  allen PCs eines Nutzers gleich sind; nur für ihren Besitzer sichtbar.

## Das Add-on

- Eigenes Store-Add-on „Senden an" (Arbeitsname), Gruppe
  `FeaturePack::SendTo` auf dem gemeinsamen Register, x64 Pflicht
- **Bereich „Senden an" rechts** (`RVAppRegisterNavigationPanel` mit
  `kRVPanelRight`) als zentrale Oberfläche:

  | Abschnitt | Inhalt |
  |---|---|
  | Kontakte | Suchfeld, Favoriten, Verteilerlisten, zuletzt verwendet, alle bestätigten Kontakte; **Kontakt einladen**; **Anfragen** (erhalten: Annehmen/Ablehnen; gesendet: Status, Zurückziehen); Mehrfachauswahl, Notiz, Senden |
  | Eingang | Neue und angenommene Dokumente, Annehmen, Ablehnen, Öffnen |
  | Gesendet | Status je Empfänger: zugestellt, angenommen, abgelehnt, abgelaufen |

  Rechtsklick auf einen Kontakt: Zu Liste hinzufügen, Als Favorit,
  Schnellziel anlegen, Fingerabdruck anzeigen, Entfernen, Blockieren, Melden.

  **Position** (Entscheidung Marcus, Okt 9, 2026: **rechts als Standard**):

  | Optionen: „Position" | Wirkung |
  |---|---|
  | **rechts** (Standard) | „Senden an" im Ribbon öffnet/schließt den Bereich rechts |
  | links | Gleicher Bereich in der linken Navigationsleiste |
  | kein Bereich | Kompakter Dialog mit denselben Abschnitten |
  | Richtlinie `PanelPosition` | Admin legt die Position fest (right, left, none) |

  Technik nach dem erprobten Rezept (FP2, PanelDemo-Sample, Power PDF
  2025.3 FP8): Handler nur `procCreate` + `procGetIcon`, Registrierung in
  `PluginInit`, Anzeige über Ribbon-Toggle mit `RVDocShowNavigationPanel`,
  Layout in `WM_SIZE` und `WM_WINDOWPOSCHANGED`. Ein Positionswechsel wirkt
  ab dem nächsten Start. **Im Spike zuerst prüfen:** Wir haben bisher nur
  links getestet; das PanelDemo-Sample registriert auch rechts. Läuft
  rechts nicht stabil, wird links Standard.

  **Bekannte Host-Einschränkung:** Ein eigenes Icon in der Panel-Leiste
  (`procGetIcon`) beschädigt in 2025.3 FP8 native Panel-Icons. Bis Kofax das
  behebt, wird mit `procGetIcon = NULL` ausgeliefert; der Bereich öffnet sich
  über den Ribbon-Button **Senden an**. Ob der Fehler rechts ebenso
  auftritt, ist offen; im Spike mit der aktuellen Version erneut prüfen.
- Netzwerk in einem eigenen Worker-Thread; Host-Aufrufe nur im UI-Thread
- Abholung nur, solange Power PDF läuft; kein Hintergrunddienst

## Design: angelehnt an den Add-on Store

Vorgabe Marcus (Okt 9, 2026): Das Add-on sieht aus wie das Store-Fenster,
damit beides als eine Produktfamilie wirkt.

| Element | Umsetzung |
|---|---|
| Technik | Wie das Store-Fenster: WebView2-Seite als RCDATA-Ressource (Vorbild `client/ui/store.html` + `client/store/webui.cpp`), Texte aus der rc per JSON, Daten nur per `textContent`, Navigation gesperrt |
| Farben | Gleiche Tokens wie `store.html`: `--navy #002854`, `--navy2`, `--blue #00A0FB`, `--green #00EB86`, `--ink`, `--slate`, `--line`, `--soft`, `--sel` (Brand Book) |
| Schrift | Wie Store-Fenster: „Segoe UI Variable Text", Segoe UI, Arial; RTL-Regel für Arabisch übernehmen |
| Bausteine | Listen, Suchfeld, Chips, Statuszeile, Buttons und In-Page-Dialoge aus dem Store-Fenster (keine Browser-Popups) |
| Wo | Bereich rechts (WebView2 als Kindfenster im Panel), Dialog bei „kein Bereich", Hinweis „Gesendet · Rückgängig", Bestätigungsseite der Einladung im Store-Web |
| Optionsseite | Ebenfalls WebView2 im Store-Stil; **im Spike prüfen**, ob WebView2 in der Power-PDF-Optionsseite stabil läuft, sonst native Steuerelemente in Store-Farben |
| Ohne WebView2-Runtime | Klassischer Fallback wie beim Store-Client (`ClassicUI`) |
| Sprachen | 21 Power-PDF-Sprachen wie der Store |

Gemeinsames CSS und gemeinsame Bausteine gehören in eine Datei, die
Store-Client und Add-on beide einbinden.

## Ribbon-Gruppe und Schnell-Senden

### Buttons

Gruppe „Senden an" auf dem Register „Erweiterte Funktionen":

| Button | Wirkung |
|---|---|
| **Senden an** (groß, Umschalter) | Öffnet/schließt den Bereich rechts. Markiert, solange offen; Badge bei neuen Dokumenten und Anfragen |
| **Schnell-Buttons** (klein, z. B. „An Frank", „An Team Vertrieb") | Senden das aktuelle Dokument **sofort** an das festgelegte Ziel. **Öffnen nichts** |
| **Schnell senden ▾** (klein, mit Menü) | Listet alle Schnellziele; ein Klick sendet sofort, ebenfalls ohne etwas zu öffnen |
| **Optionen** (klein) | Öffnet die Optionsseite „Senden an" |

### Schnell-Senden

| Feld | Inhalt |
|---|---|
| Ziel | Ein Kontakt oder eine eigene Verteilerliste |
| Beschriftung | Text auf dem Button, Vorschlag „An <Vorname>" |
| Feste Notiz | Optional, z. B. „Bitte gegenzeichnen" |
| Tastenkürzel | Optional, z. B. Strg+Alt+1 (`RVToolButtonSetShortKey`) |
| Als Button im Ribbon | ja/nein; sonst nur im Menü „Schnell senden" |
| Reihenfolge | Verschieben nach oben/unten |

Ablauf beim Klick auf „An Frank":

1. Das Add-on sendet sofort. Es öffnet sich **nichts** (Vorgabe Marcus,
   Okt 9, 2026).
2. Kurz eingeblendeter Hinweis unten am Fenster (Toast im Store-Stil) „An
   Frank Giessler gesendet · **Rückgängig**". Der Dienst stellt erst nach 10
   Sekunden zu; bis dahin zieht „Rückgängig" die Sendung zurück.
3. Fehler (Kontakt entfernt, Datei zu groß, offline) zeigt das Add-on als
   Meldung, nur dann mit Fenster.

Ein Schnellziel, dessen Kontakt entfernt wurde, wird ausgegraut; der Tooltip
nennt den Grund.

### Technische Umsetzung im Ribbon

Die Ribbon-Struktur wird vom Host per Atom-Namen zwischengespeichert;
dynamisch neue Buttons gehen nicht zuverlässig. Deshalb:

- **Feste Plätze** `FeaturePack::SendTo::Quick1` bis `Quick5`, von Anfang an
  registriert; unbelegte blendet `RVToolButtonSetComputeVisibleProc` aus.
- Beschriftung und Hilfetext zur Laufzeit über `RVToolButtonSetLabelText` /
  `RVToolButtonSetHelpText`. **Im Spike prüfen**, ob der Host eine geänderte
  Beschriftung nach der Init sofort zeigt (bei `RVToolButtonSetIcon` belegt).
- Rückfallweg: Menü „Schnell senden" (`RVToolButtonSetMenu`) mit allen Zielen.
- Die Gruppe trägt der Store-Client selbst ins Layout ein (Host-Merge-Falle,
  seit C1.9.4 gelöst).

### Optionsseite

| Bereich | Inhalt |
|---|---|
| Mein Eintrag | Anzeigename mit Bearbeiten und Zurücksetzen, E-Mail (nur Anzeige) |
| Kontakte | Adressbuch, Anfragen, Blockierte (Blockierung aufheben) |
| Schnell-Senden | Schnellziele: Hinzufügen, Bearbeiten, Entfernen, Reihenfolge |
| Verteilerlisten | Eigene Listen verwalten |
| Ansicht | Position des Bereichs: rechts (Standard), links, kein Bereich |
| Empfang | Ablageordner, angenommene Dokumente automatisch öffnen ja/nein |
| Geräte | Eigene angemeldete PCs, Gerät entfernen |

`RVAppRegisterPrefsType` immer zusammen mit `RVAppRegisterPrefsPage`
registrieren (sonst Absturz beim Öffnen der Kategorie).

## Richtlinien für die Firmen-IT

Unter `HKLM\...\FeaturePack\Policies\SendTo` (gleiches Muster wie im
Feature Pack, schlägt HKCU):

| Wert | Wirkung |
|---|---|
| `Enabled` | Funktion an oder aus (aus = alles verschwindet) |
| `InviteDomains` | Einladen und Annehmen nur für diese Domains, z. B. `firma.de;tochter.de` |
| `BlockExternal` | Nur Kontakte mit derselben Domain wie die eigene Identity |
| `MaxSizeMB` | Kleinere Dateigrenze als der Server |
| `RetentionHours` | Kürzere Vorhaltezeit als der Server (nur verkürzen) |
| `QuickSend` | Schnell-Senden erlauben oder verbieten |
| `UndoSeconds` | Wartezeit für „Rückgängig" (0 bis 60 Sekunden) |
| `PanelPosition` | Position festlegen: right, left, none |
| `LockPage` | Optionsseite sperren |

`InviteDomains` und `BlockExternal` prüft das Add-on beim Einladen und
Annehmen; bestehende Kontakte außerhalb der erlaubten Domains werden
ausgeblendet und können weder senden noch empfangen. Da Richtlinien nur auf
dem Gerät wirken, meldet das Add-on die gültigen Einschränkungen beim Start
an den Server, der sie für dieses Gerät zusätzlich durchsetzt.

## Sicherheit

| Bedrohung | Gegenmaßnahme |
|---|---|
| Angriff auf den Server | Nur verschlüsselte Blöcke und Umschläge, kein privater Schlüssel auf dem Server |
| Unerwünschte Dokumente | Senden nur an bestätigte Kontakte; Entfernen und Blockieren jederzeit |
| Einladungs-Spam | Höchstens 20 offene und 30 neue Einladungen pro Tag, Ablauf nach 14 Tagen, 30 Tage Sperre nach Ablehnung, Blockieren und Melden, Sperren durch Admins |
| Missbrauch als Gratis-Dateitransfer | Kontingente (100 MB pro Datei, 1 GB wartend), kurze Vorhaltezeit, Löschen nach Abholung, Ratenbegrenzung |
| Jemand gibt sich per Registry als jemand anderes aus | Power PDF (SaaS) startet mit falscher Identity nicht; Registry meist IT-geschützt; Eingeladene bestätigen ihre Adresse per Link; Adressbuch zeigt immer die E-Mail |
| Server schiebt einen fremden Schlüssel unter | Schlüssel-Merken plus Warnung bei Änderung, Fingerabdruck in den Kontaktdetails |
| Gestohlener PC | Schlüssel per DPAPI an das Windows-Konto gebunden, Gerät von einem anderen PC aus entfernbar |
| Manipulierte Datei | AES-GCM erkennt Änderungen, Signatur bestätigt den Absender |
| Schadhaftes PDF (JavaScript, eingebettete Dateien) | Nur von bestätigten Kontakten, bewusstes „Annehmen", Absender mit E-Mail sichtbar, Defender prüft die abgelegte Datei, Melden; Abstimmung mit dem Malware-Konzept |
| Dokumente verlassen die Firma ohne Zustimmung der IT | Richtlinien `Enabled`, `InviteDomains`, `BlockExternal` |

## Datenschutz

- **Neue Datenarten auf dem Server:** Name und E-Mail pro Nutzer,
  Geräteschlüssel, Kontaktbeziehungen (wer mit wem), Einladungen
  einschließlich E-Mail-Adressen eingeladener Personen, die (noch) keine
  Nutzer sind, Sperren und Meldungen, Verteilerlisten, Übertragungs-Metadaten,
  verschlüsselte Dokumente bis zur Abholung. Dem Datenschutz zu melden.
- **Kein Verzeichnis:** Niemand kann nach Personen suchen; Name und E-Mail
  sieht nur, wer bestätigter Kontakt ist.
- Die Erfassung ist **sichtbar**: Store-Beschreibung und Ersthinweis nennen
  sie. Kein Rechnername, keine Windows-Kontodaten außer dem Anzeigenamen.
- Adressen eingeladener Nicht-Nutzer werden nur für die Einladung gespeichert
  und mit ihrem Ablauf gelöscht.
- Keine Dateinamen, keine Inhalte in Protokollen.
- **Öffentlicher Betrieb ist eine neue Lage:** Tungsten verarbeitet dann
  Daten beliebiger Nutzer, nicht nur von Kunden mit Vertrag. Vor dem Schritt
  „öffentlich" Nutzungsbedingungen und Datenschutzhinweis für „Senden an"
  durch Legal und Datenschutz bewerten lassen.

## Phasen

| Phase | Umfang |
|---|---|
| 1a Spike | Bereich rechts (kRVPanelRight) mit WebView2, Ribbon-Beschriftung zur Laufzeit, lokaler Mock-Server mit Blob-Emulator (Azurite), zwei Windows-Benutzer: einladen, annehmen, senden, empfangen, entfernen, mit Ende-zu-Ende-Verschlüsselung |
| 1b Pilot | Server-Endpunkte im Store, Einladungs-Mails, Portal-Menü, Richtlinien; Verteilung per Auslieferung, zuerst an „Tungsten intern" |
| 1c Ausweitung | Weitere Kunden per Auslieferung oder Beta/öffentlich (Entscheidung im Portal); Option kundeneigener Speicher; vor „öffentlich": Kontingente final, Melden/Sperren, Rechtsprüfung, Mailkontingent |
| 2 Ohne Add-on | Empfänger ohne Add-on erhalten einen Link, dessen Schlüssel nur im Link-Fragment steht (eigenes Konzept) |
| 3 Ausbau | iPhone als Absender, Arbeitsaufträge (gegenzeichnen, prüfen mit Rücklauf), geteilte Verteilerlisten |

## Entscheidungen

| Datum | Entscheidung |
|---|---|
| Okt 9, 2026 | Grundprinzip Einladung: Kontakt per E-Mail einladen, der Einladende hat damit schon zugestimmt, der Eingeladene bestätigt; danach Senden in beide Richtungen |
| Okt 9, 2026 | Entfernen durch einen der beiden beendet die Erlaubnis in beide Richtungen |
| Okt 9, 2026 | Kein Firmenverzeichnis, keine Kreise, keine Kunden-Freischaltung (ersetzt die früheren Entwürfe mit Kreisen und Kundencode) |
| Okt 9, 2026 | Verteilung über die normalen Store-Wege (Auslieferung an Kunden, Beta, öffentlich), Entscheidung später im Portal; Pilot per Auslieferung an „Tungsten intern" |
| Okt 9, 2026 | Keine Einrichtung: Anmeldung automatisch beim ersten Start; Name aus `vorname.nachname@` abgeleitet, per Bearbeiten korrigierbar; Anzeige immer mit E-Mail |
| Okt 9, 2026 | Kein zusätzlicher Fälschungsschutz für die eigene Identität (SaaS-Identity, IT-geschützt) |
| Okt 9, 2026 | Nur Relay über den Store-Server (P2P und Warten verworfen) |
| Okt 9, 2026 | Verschlüsselung: Ende-zu-Ende pro Dokument und Empfänger plus Standard-Verschlüsselung von Azure; keine Encryption Scopes |
| Okt 9, 2026 | Krypto nur über Windows CNG (Store-Regel: nur Windows-DLLs); HPKE gegen RFC-Testvektoren geprüft; Signaturen ECDSA P-256 statt Ed25519 (Spike-Ergebnis) |
| Okt 9, 2026 | Dokumente in eigenem Container im vorhandenen Storage-Konto, angebunden in den Server-Einstellungen (Managed Identity bevorzugt, Verbindungstest) |
| Okt 9, 2026 | Vorhaltezeit bis zur automatischen Löschung einstellbar (1 Stunde bis 30 Tage, Standard 7 Tage) |
| Okt 9, 2026 | Übertragungen sind vom Backup ausgenommen |
| Okt 9, 2026 | Eigenes Portal-Menü „Senden an"; Hauptschalter aus = alles verschwindet (Portal und Add-on) |
| Okt 9, 2026 | Bereich „Senden an" rechts als Standard, öffnet über den Ribbon-Button; links oder nur Dialog wählbar |
| Okt 9, 2026 | Schnell-Senden öffnet nichts, sendet sofort (Hinweis mit Rückgängig) |
| Okt 9, 2026 | Design wie das Store-Fenster (WebView2, gleiche Brand-Tokens und Bausteine) |
| Okt 9, 2026 | Arbeitsaufträge erst nach Phase 1; Verteilerlisten zunächst privat |
| Okt 9, 2026 | Annahme per Link auch vor der Installation, aber nur über den Button auf der Seite (nie durch bloßes Öffnen des Links) |
| Okt 9, 2026 | Option kundeneigener Blob Storage: Dokumente liegen im Speicher der Absender-Firma; kein stiller Rückfall auf Tungsten |
| Okt 9, 2026 | Freemail-Adressen erlaubt (SaaS-Identity ist durch unsere Kundenprüfung gedeckt) |
| Okt 9, 2026 | Grenzen bestätigt: 100 MB pro Datei, 1 GB wartend, 20 offene / 30 neue Einladungen pro Tag; fünf feste Schnell-Plätze |

## Offene Fragen

- [ ] Reicht das Resend-Kontingent, oder braucht es einen bezahlten Tarif? (im Pilot messen)
