# Konzept: Master-Child-Betrieb (UAT-Server vor der Produktion)

Stand: Okt 10, 2026 · Status: Entwurf zur Besprechung (noch nichts umgesetzt)

## Ziel

Neue Add-ons und Versionen sollen erst in einer Testumgebung (UAT) laufen:
hochladen, prüfen, an Testkunden ausliefern, in echtem Power PDF ausprobieren.
Erst nach der Testphase gehen sie in die Produktion. Dafür bekommt derselbe
Server eine Einstellung **Betriebsart**:

| Betriebsart | Rolle |
|---|---|
| **Einzeln** (Standard, wie heute) | Ein Server, keine Kopplung |
| **Master** | Der produktive Store; nimmt Übermittlungen gekoppelter Childs an |
| **Child** | Ein UAT-Store; arbeitet vollständig wie heute und kann geprüfte Stände an seinen Master übermitteln |

Ein Child bleibt ein vollwertiger Server: eigene Konten, eigene Prüfung, eigene
Auslieferungen, eigener Store-Client. Neu ist nur der Weg nach oben.

## Grundsätze

1. **Der Master entscheidet.** Ein Child kann nur einreichen, nie direkt live
   schalten. Am Master laufen alle automatischen Prüfungen noch einmal
   unabhängig, und die Freigabe folgt der Vier-Augen-Regel des Masters.
2. **Geprüft heißt dieselben Bytes.** Übertragen wird exakt das Paket, das im
   UAT lief (gleicher SHA-256). Wer am Master etwas ändern will, lädt im UAT
   neu hoch und testet erneut.
3. **Signaturschlüssel bleiben, wo sie sind.** Der Paketsignaturschlüssel des
   Masters verlässt den Master nie. Was aus dem UAT kommt, signiert der Master
   bei der Freigabe neu. Produktive Clients vertrauen nur dem Master-Schlüssel,
   eine UAT-Signatur gilt dort nicht.
4. **Kundendaten bleiben in ihrer Umgebung.** Kunden, Kundencodes,
   Installationszahlen, Statistik, Meldungen und Benutzerkonten werden nicht
   übertragen. Testkunden im UAT sind keine Produktivkunden.
5. **Alles ist nachvollziehbar.** Jede Kopplung, Übermittlung und Antwort steht
   im Audit beider Seiten; die Prüfakte am Master zeigt die UAT-Herkunft.

## Kopplung

1. Ein Admin am Master legt unter *Einstellungen, Kopplung* ein Child an
   (Name, zum Beispiel „UAT“) und erhält einen **Kopplungscode** (einmalig,
   15 Minuten gültig, nur als Hash gespeichert).
2. Ein Admin am Child stellt die Betriebsart auf *Child* und trägt Master-Adresse
   (nur https) und Code ein.
3. Das Child erzeugt ein eigenes Schlüsselpaar (ECDSA P-256, wie die
   Paketsignatur) und schickt den öffentlichen Schlüssel mit dem Code an den
   Master. Der Master antwortet mit seinem öffentlichen Schlüssel und der
   Kopplungs-ID. Beide Seiten speichern den Schlüssel der Gegenseite.
4. Ab dann ist jede Anfrage vom Child an den Master **signiert**: Methode, Pfad,
   Zeitstempel, Einmalwert und SHA-256 des Inhalts. Der Master lehnt ab, was
   älter als 5 Minuten ist, einen Einmalwert wiederholt oder falsch signiert
   ist. Darüber liegt TLS wie bei jedem Aufruf.
5. Der Master-Admin kann eine Kopplung jederzeit **sperren**; danach nimmt der
   Master von diesem Child nichts mehr an. Neu koppeln geht nur mit neuem Code.

Der private Schlüssel des Childs liegt geschützt wie der Paketsignaturschlüssel
heute (Schlüsselring mit Passwort) und ist Teil von Sicherung und
Wiederherstellung.

## Was übertragen wird

| Inhalt | Übertragen | Bemerkung |
|---|---|---|
| Paket (.ppak) einer Version | ja | dieselben Bytes, SHA-256 wird verglichen |
| Quellcode-ZIP der Version | ja | die Quellcode-Pflicht gilt am Master genauso |
| Katalogeintrag (Name, Beschreibung, Kategorie, Anbieter, Autor, Kontakt) | ja | als Vorschlag; der Master-Admin sieht die Unterschiede |
| Auszug der UAT-Prüfakte (wer hat wann im UAT geprüft, Bedingungen, Kommentar) | ja | nur zur Information für den Master-Prüfer |
| Auslieferungs-Vorlagen (Add-on-Bündel) | später (Etappe 5) | ohne Kundenzuordnung |
| Kunden, Codes, Installationen, Statistik, Meldungen, Konten | nein | Datenschutz, getrennte Umgebungen |
| Signaturschlüssel, Tokens, Passwörter | nie | |

## Ablauf einer Übermittlung

1. Im UAT ist eine Version freigegeben (Beta oder Live) und wurde getestet.
2. Ein Admin des Childs klickt in der Versionsansicht auf **„An Produktion
   übermitteln“**, optional mit einer Notiz („2 Wochen bei Testkunde A, keine
   Fehler“).
3. Der Master legt die Version als **eingereicht, Herkunft: UAT „Name“** an.
   Die automatische Prüfung läuft wie bei jedem Upload. Bei Fehlern lehnt der
   Master ab und schickt die Befunde zurück.
4. Ein Prüfer am Master gibt frei. Die UAT-Prüfung ist ein Hinweis, keine
   Freigabe. Wer die Version im UAT hochgeladen hat, darf sie auch am Master
   nicht freigeben (Vier-Augen-Regel über beide Umgebungen hinweg, verknüpft
   über die E-Mail-Adresse des Kontos).
5. Das Child fragt den Status regelmäßig ab und zeigt ihn an: *übermittelt,
   in Prüfung, abgelehnt (mit Befunden), live seit …*.

Die Add-on-ID ist in beiden Umgebungen dieselbe. Gibt es die ID am Master noch
nicht, legt die erste Übermittlung das Add-on dort an; Besitzer wird das Konto
mit derselben E-Mail-Adresse, sonst der übermittelnde Admin.

## Rückrichtung: Produktivstand ins UAT holen (optional)

Damit das UAT nicht veraltet, kann ein Child den **aktuellen Live-Katalog des
Masters spiegeln**: Pakete und Katalogeinträge, die im UAT fehlen oder älter
sind, werden übernommen (als bereits freigegeben, Herkunft: Master). So testet
man neue Versionen gegen den echten Produktivstand. Kundendaten bleiben auch
hier außen vor.

## Kennzeichnung, damit nichts verwechselt wird

- Ein Child zeigt im Portal ein festes Band **„UAT-Umgebung, nicht produktiv“**
  und im Seitentitel den Zusatz „UAT“.
- Der Katalog eines Childs meldet `"environment": "uat"`; der Store-Client
  zeigt im Fenster einen Hinweis „Testsystem“.
- UAT-PCs zeigen per IT-Richtlinie `ServerUrl` auf das Child, wie heute schon
  möglich. Ein Produktiv-PC bekommt UAT-Pakete nie zu sehen, weil die
  Signatur nicht passt.

## Datenmodell (grob)

- `AppSettings`: `Federation.Mode` (standalone, master, child),
  `Federation.MasterUrl`, eigene Kopplungs-ID.
- Master: Tabelle `FederationChildren` (Id, Name, öffentlicher Schlüssel,
  angelegt, gesperrt, zuletzt gesehen); an `PackageVersions` die Herkunft
  (Child-ID, UAT-Versionsreferenz, UAT-Prüfauszug).
- Child: Tabelle `Promotions` (Version, Zustand, Master-Referenz, Befunde,
  Zeitpunkte).
- Einmalwerte der letzten 10 Minuten im Speicher (Schutz gegen Wiederholung).

Alle neuen Tabellen liegen in der Datenbank und sind damit in Sicherung und
Wiederherstellung enthalten.

## API (grob)

Am Master, nur für gekoppelte Childs (signierte Anfragen):

- `POST /api/federation/pair`: Code und Child-Schlüssel, Antwort mit
  Master-Schlüssel und Kopplungs-ID
- `POST /api/federation/promotions`: Paket, Quellcode, Katalogvorschlag,
  Prüfauszug, Notiz
- `GET /api/federation/promotions/{id}`: Status und Befunde
- `GET /api/federation/catalog`: Live-Stand zum Spiegeln

Agent-Guide, OpenAPI und README werden wie bei jeder Neuerung mitgeführt.

## Sicherheit im Überblick

| Gefahr | Gegenmittel |
|---|---|
| Fremder gibt sich als Child aus | Kopplung nur mit einmaligem Code, danach jede Anfrage signiert |
| Mitgeschnittene Anfrage wird wiederholt | Zeitstempel plus Einmalwert, TLS |
| Übernommenes UAT schleust Schadcode ein | Master prüft alles erneut, Freigabe nur durch Master-Prüfer, Sperre der Kopplung |
| Verwechslung UAT und Produktion | Band im Portal, Hinweis im Client, getrennte Signaturschlüssel |
| Kundendaten gelangen ins falsche System | werden grundsätzlich nicht übertragen |
| Überlastung | Größenlimit wie beim Upload, Mengenbegrenzung pro Kopplung |

## Etappen

| Etappe | Inhalt |
|---|---|
| E1 | Betriebsart, UAT-Band, Kopplung mit Code und Schlüsseltausch, Sperren |
| E2 | Übermitteln einer Version (Paket und Quellcode), erneute Prüfung am Master, Herkunft in der Prüfakte |
| E3 | Status-Rückmeldung ans Child, Katalogvorschlag mit Unterschieden, Vier-Augen über beide Umgebungen |
| E4 | Spiegeln des Live-Katalogs vom Master ins UAT |
| E5 | Auslieferungs-Vorlagen übertragen |

E1 bis E3 ergeben den Kern deiner Idee; E4 und E5 sind Ergänzungen.

## Offene Fragen zur Entscheidung

1. **Mehrere Childs?** Ein Master mit mehreren UATs (zum Beispiel „UAT“ und
   „Entwicklung“) ist im Modell vorgesehen. Brauchen wir das, oder reicht eins?
2. **Wer darf übermitteln?** Vorschlag: nur Admins des Childs. Sollen auch
   Besitzer eines Add-ons ihre eigenen Versionen übermitteln dürfen?
3. **Vertrauensstufe:** Soll der Master eine UAT-Freigabe durch einen anderen
   Prüfer als Freigabe anerkennen dürfen (schneller), oder bleibt die
   Master-Prüfung immer Pflicht (Vorschlag: immer Pflicht)?
4. **Spiegeln:** Soll das UAT den Produktivstand automatisch (zum Beispiel
   täglich) oder nur auf Knopfdruck übernehmen?
5. **Hosting des UAT:** eigener Azure App Service mit eigener Datenbank (wie
   Produktion, Vorschlag) oder ein zweiter Slot derselben App?
