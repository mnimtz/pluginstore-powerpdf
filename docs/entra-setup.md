# Anmeldung mit Microsoft: in 5 Minuten eingerichtet

Der Add-on Store kann neben E-Mail und Passwort eine Schaltfläche **Mit Microsoft
anmelden** zeigen. Wer ein Microsoft-Arbeitskonto Ihrer Organisation hat, meldet
sich damit an; externe Entwickler behalten ihre lokalen Konten. Eingerichtet wird
alles auf der Store-Seite **Einstellungen > Mit Microsoft anmelden**. Ein Neustart
oder App-Service-Einstellungen sind nicht nötig.

## 1. Im Microsoft Entra Admin Center (drei Schritte)

[entra.microsoft.com](https://entra.microsoft.com) mit einem Administrator Ihres
Verzeichnisses öffnen, dann **Identität > Anwendungen > App-Registrierungen**.

1. **Neue Registrierung**
   - Name: beliebig, z. B. `Add-on Store`
   - Unterstützte Kontotypen: **Nur Konten in diesem Organisationsverzeichnis**
   - Umleitungs-URI: Plattform **Web**, Adresse aus dem Store kopieren (Feld
     *Umleitungs-URI* mit der Schaltfläche **Kopieren**, z. B.
     `https://addon.power-pdf.de/signin-oidc`)
2. **Zertifikate & Geheimnisse > Neuer geheimer Clientschlüssel**: sofort den
   **Wert** kopieren (nicht die *Geheime ID*; der Wert wird nur einmal angezeigt).
3. **Übersicht**: **Anwendungs-ID (Client)** und **Verzeichnis-ID (Mandant)**
   kopieren.

Mehr ist nicht nötig: keine optionalen Ansprüche, keine Änderung der
API-Berechtigungen, keine Administratorzustimmung.

## 2. Im Store

**Einstellungen > Mit Microsoft anmelden**:

1. **Anwendungs-ID (Client)** einfügen.
2. **Verzeichnis-ID (Mandant) oder Domäne** einfügen: die Verzeichnis-ID oder eine
   bestätigte Domäne wie `power-pdf.de`.
3. **Geheimer Clientschlüssel** einfügen. Er wird verschlüsselt gespeichert und nie
   wieder angezeigt; ein leeres Feld behält beim Speichern den gespeicherten
   Schlüssel.
4. **Testen**: prüft die eingegebenen Werte bei Microsoft, ohne sie zu speichern.
   Mögliche Antworten: Test bestanden, Verzeichnis nicht gefunden, Anwendung in
   diesem Verzeichnis nicht gefunden, geheimer Clientschlüssel falsch oder
   abgelaufen, Microsoft nicht erreichbar.
5. **Eingeschaltet** anhaken und **Speichern**. Die Schaltfläche erscheint sofort
   auf der Anmeldeseite.

Abschalten: Haken entfernen und speichern; die Schaltfläche verschwindet sofort.

## 3. Ausprobieren

Abmelden und **Mit Microsoft anmelden** mit einem Benutzer Ihres Verzeichnisses
wählen, dessen Adresse auf einer bestätigten Domäne liegt (z. B.
`marcus.test@power-pdf.de`; die Domäne steht im Entra Admin Center unter
**Einstellungen > Domänennamen**).

- Gibt es schon ein Store-Konto mit dieser Adresse, wird es verknüpft (kein
  zweites Konto).
- Sonst entsteht eine Zugriffsanfrage, die ein Admin auf der Benutzerseite
  freigibt.

**Private Microsoft-Konten (Outlook, Hotmail) und Gastkonten werden bewusst
abgelehnt**, auch das private Konto, mit dem ein Verzeichnis angelegt wurde. Ihre
Adresse hat Ihr Verzeichnis nicht bestätigt. Diese Personen melden sich weiter mit
E-Mail und Passwort an.

---

## Anhang: Technik und Sonderfälle

### Konfiguration und Vorrang

- Sind App-Einstellungen `Entra__*` gesetzt (`Entra__ClientId`,
  `Entra__ClientSecret`, `Entra__TenantId` oder `Entra__Authority`,
  `Entra__AllowedTenants`), gelten sie und die Store-Seite zeigt sie nur an
  ("über App-Einstellungen gesetzt"; der Schlüssel wird nie angezeigt). Diese
  Werte werden beim Start gelesen.
- Sonst gelten die Werte der Store-Seite (Tabelle `AppSettings`, Schlüssel
  `Entra.*`). Der geheime Clientschlüssel liegt mit ASP.NET Data Protection
  verschlüsselt darin (Zweck `AddonStore.EntraSecret`, wie der Resend-Schlüssel).
  Eine Domäne wird beim Speichern in die Verzeichnis-ID übersetzt, weil die Tokens
  gegen den Aussteller dieser ID geprüft werden.
- Nach dem Speichern baut der Server die OpenID-Connect-Einstellungen neu auf
  (Optionen und Metadaten), andere Server mit derselben Datenbank spätestens nach
  einer Minute. Das Audit-Protokoll erhält `entra.saved`, `entra.enabled`,
  `entra.disabled` und `entra.secret-removed`, nie den Schlüssel.
- Ist die Funktion aus oder unvollständig, gibt es keine Schaltfläche, und
  `/Account/Microsoft` sowie `/signin-oidc` antworten mit 404.
- Der Test ruft nur `login.microsoftonline.com` auf (Zeitlimit 10 Sekunden): die
  OpenID-Metadaten des Verzeichnisses und eine Client-Credentials-Anfrage mit dem
  Bereich `https://graph.microsoft.com/.default`. Dafür ist keine Berechtigung
  nötig; das Token wird sofort verworfen, nie protokolliert oder gespeichert.
- Die Umleitungs-URI kommt aus der öffentlichen Basis-URL (Einstellungen >
  Server), sonst aus der aktuellen Anfrage. Für einen lokalen Test kann in der
  App-Registrierung zusätzlich `http://localhost:<Port>/signin-oidc` eingetragen
  werden (Entra erlaubt `http` nur für `localhost`).

### Ablauf und Ansprüche

- OpenID Connect, Autorisierungscode-Flow mit PKCE, `response_mode=query`,
  Bereiche `openid profile email`. Der Server tauscht den Code mit dem geheimen
  Clientschlüssel; Tokens werden nicht gespeichert. Im externen Cookie (5 Minuten)
  liegen nur Verzeichnis-ID:Objekt-ID, Adresse und Name.
- Geprüft werden Signatur, Aussteller (nur freigegebene Verzeichnisse),
  Zielgruppe, Gültigkeit und Nonce, dann:
  - `tid` muss ein freigegebenes Verzeichnis sein und zum Aussteller passen;
  - Gäste und externe Konten (Anspruch `idp` nennt ein anderes Verzeichnis oder
    `live.com`, oder ein `#EXT#`-Name) werden abgelehnt;
  - die Adresse ist `preferred_username`, bei Mitgliedern eine Adresse auf einer
    Domäne, die das Verzeichnis bestätigt hat. Dieser Anspruch ist im
    v2.0-ID-Token ohne Zusatzeinstellung enthalten (wie `oid`, `tid`, `name`).
  - Optional und nicht nötig: `upn` (hat Vorrang, wenn vorhanden), `email` (nur
    wenn gleich der Adresse oder `xms_edov=true`).
- Rollen kommen nie aus Entra. Kontostatus wie bei der Passwort-Anmeldung:
  aktiv = angemeldet; wartet auf Freigabe, abgelehnt, deaktiviert oder gesperrt =
  nicht angemeldet, mit derselben Meldung.

### Verknüpfung ohne doppelte Konten

Die Microsoft-Anmeldung steht in `AspNetUserLogins` (Anbieter `Entra`, Schlüssel
`{Verzeichnis-ID}:{Objekt-ID}`, ändert sich nie). Reihenfolge:

1. Schon verknüpft: genau dieses Konto.
2. Konto mit derselben Adresse (ohne Rücksicht auf Groß- und Kleinschreibung):
   wird verknüpft, kein zweites Konto, Audit `user.entra-linked`. Sicherung seit
   S2.1.0: alle anderen Sitzungen dieses Kontos enden, und die Adresse erhält die
   E-Mail "Microsoft account linked". Ein Passwort, das jemand Fremdes gesetzt
   hat, gilt aber weiter; die Inhaberin oder der Inhaber setzt es über "Passwort
   vergessen?" neu.
3. Neue Adresse: Konto wie bei "Zugriff anfordern" (wartet auf Freigabe, Rolle
   Developer, ohne Passwort), Admins werden benachrichtigt, Audit
   `user.access-requested`.

Eine spätere Registrierung von Hand mit derselben Adresse legt ebenfalls kein
zweites Konto an. Im **Profil** lässt sich die Verknüpfung aufheben, wenn das
Konto ein Passwort hat.

### Tungsten-Verzeichnis später dazunehmen

- **Eigene App-Registrierung im Tungsten-Verzeichnis**: deren Anwendungs-ID,
  Verzeichnis und Schlüssel auf der Store-Seite eintragen. Fertig.
- **Bestehende Registrierung mehrinstanzenfähig machen**: in der
  App-Registrierung *Unterstützte Kontotypen* auf "Konten in einem beliebigen
  Organisationsverzeichnis" stellen, ein Tungsten-Administrator erteilt die
  Zustimmung
  (`https://login.microsoftonline.com/<Verzeichnis-ID>/adminconsent?client_id=<Anwendungs-ID>`),
  dann im Store unter **Weitere zugelassene Verzeichnisse** die Tungsten-Domäne
  oder Verzeichnis-ID eintragen. Der Store meldet dann über `organizations` an,
  lässt aber nur Tokens der eingetragenen Verzeichnisse zu.

### Betrieb

- **Schlüssel erneuern**: vor dem Ablauf einen neuen geheimen Clientschlüssel
  anlegen, im Store eintragen, **Testen**, **Speichern**, dann den alten löschen.
  Ein abgelaufener Schlüssel führt zur Meldung "Die Anmeldung mit Microsoft ist
  fehlgeschlagen"; die Passwort-Anmeldung funktioniert weiter.
- **Fehlersuche**: abgelehnte Anmeldungen stehen im Protokoll als Warnung der
  Kategorie `AddonStore.Entra` mit dem Grund (`tenant`, `email`, `failed`).
- Nach einer Wiederherstellung aus einer Sicherung wird der geheime
  Clientschlüssel nur lesbar, wenn der Schlüsselbund mit wiederhergestellt wurde;
  sonst zeigt die Store-Seite "unvollständig" und der Schlüssel wird neu
  eingetragen.
