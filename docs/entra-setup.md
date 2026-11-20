# Anmeldung mit Microsoft (Entra ID) einrichten

Der Add-on Store kann neben der Anmeldung mit E-Mail und Passwort eine zusätzliche
Schaltfläche **Mit Microsoft anmelden** zeigen. Externe Entwickler behalten ihre
lokalen Konten; wer ein Microsoft-Arbeitskonto aus einem freigegebenen Tenant hat,
kann sich damit anmelden.

Die Funktion ist **aus**, solange die Einstellungen unten nicht vollständig gesetzt
sind: dann gibt es keine Schaltfläche, keinen OpenID-Connect-Handler und keine
zusätzlichen Adressen (`/Account/Microsoft`, `/signin-oidc` antworten mit 404).

Diese Anleitung beschreibt zuerst den Test mit dem eigenen privaten Tenant
(Domain `power-pdf.de`) und danach, wie später der Tungsten-Tenant nur per
Konfiguration dazukommt.

## 1. Technik in Kürze

- OpenID Connect mit dem Paket `Microsoft.AspNetCore.Authentication.OpenIdConnect`
  (Microsoft, MIT).
- Autorisierungscode-Flow mit PKCE, `response_mode=query`, Rücksprungadresse
  `/signin-oidc`. Der Server tauscht den Code mit dem Client-Secret gegen das
  ID-Token; es geht nie über den Browser.
- Warum `query` und nicht `form_post`: Die Antwort von Microsoft kommt als normale
  GET-Navigation zurück. Die Korrelations- und Nonce-Cookies funktionieren damit
  mit `SameSite=Lax`, und die Content-Security-Policy des Stores bleibt
  unverändert. Die Anmeldung startet über einen Link (GET), weil ein Formular mit
  Weiterleitung zu `login.microsoftonline.com` an `form-action 'self'` scheitern
  würde.
- Es werden keine Tokens gespeichert. Im kurzlebigen externen Cookie (5 Minuten)
  liegen nur Tenant-ID:Objekt-ID, Adresse und Name, danach gilt das normale
  Store-Cookie (Secure, HttpOnly, SameSite=Lax wie bisher).
- Abmelden meldet nur im Store ab, nicht bei Microsoft.

## 2. App-Registrierung im privaten Tenant (power-pdf.de)

Im [Microsoft Entra Admin Center](https://entra.microsoft.com) mit einem
Administrator des eigenen Tenants:

1. **Identität > Anwendungen > App-Registrierungen > Neue Registrierung**
   - Name: `Add-on Store`
   - Unterstützte Kontotypen: **Nur Konten in diesem Organisationsverzeichnis
     (Einzelner Mandant)**. Für den Test reicht das; für den Tungsten-Tenant
     später siehe Abschnitt 9.
   - Umleitungs-URI: Plattform **Web**, `https://addon.power-pdf.de/signin-oidc`
2. **Authentifizierung**
   - Unter *Web* eine zweite Umleitungs-URI für den lokalen Test eintragen:
     `http://localhost:5297/signin-oidc` (Entra erlaubt `http` nur für
     `localhost`; der Port muss zu dem passen, mit dem der Server lokal läuft).
   - *Front-Channel-Abmelde-URL*: leer lassen.
   - *Implizite Genehmigung und Hybridflows*: **ID-Token** und **Zugriffstoken**
     **nicht** anhaken. Der Store nutzt den Code-Flow mit PKCE und holt das
     ID-Token über den Token-Endpunkt; der Haken ist nur für den älteren
     impliziten Flow nötig und wäre eine unnötige Angriffsfläche.
3. **Zertifikate & Geheimnisse > Neuer geheimer Clientschlüssel**
   - Beschreibung z. B. `addon-store`, Ablauf höchstens 24 Monate.
   - Sofort den **Wert** kopieren (nicht die "Geheime ID"); er wird nur einmal
     angezeigt. Den Ablauf im Kalender vormerken (Abschnitt 10).
4. **Tokenkonfiguration > Optionalen Anspruch hinzufügen**
   - Tokentyp **ID**, Ansprüche **email**, **upn** und **xms_edov** auswählen.
   - Fragt das Portal, ob die Microsoft-Graph-Berechtigung `email` aktiviert
     werden soll: zustimmen.
5. **API-Berechtigungen**
   - Microsoft Graph, delegiert: **openid**, **profile**, **email**
     (`User.Read` ist voreingestellt und darf bleiben).
   - **Administratorzustimmung für <Tenant> erteilen** anklicken.
6. **Übersicht**: **Anwendungs-ID (Client)** und **Verzeichnis-ID (Mandant)**
   notieren.

## 3. Testbenutzer mit Adresse @power-pdf.de

Der Store übernimmt als Adresse nur, was der Tenant selbst bestätigt hat
(Abschnitt 8). Deshalb:

1. **Einstellungen > Domänennamen**: `power-pdf.de` als benutzerdefinierte Domäne
   hinzufügen und per DNS-TXT-Eintrag bestätigen (falls noch nicht geschehen).
2. **Benutzer > Neuer Benutzer**: z. B. `marcus.test@power-pdf.de` anlegen.
3. Mit diesem Benutzer testen.

Wichtig: Das private Microsoft-Konto, mit dem der Tenant angelegt wurde (z. B.
eine Outlook- oder Hotmail-Adresse), ist im Tenant ein externes Konto. Der Store
lehnt es mit der Meldung "Ihr Microsoft-Konto hat keine von seiner Organisation
bestätigte E-Mail-Adresse übermittelt" ab. Das gilt ebenso für Gastkonten (B2B).
Diese Personen melden sich weiter mit E-Mail und Passwort an.

## 4. Einstellungen im App Service

Azure Portal > App Service des Stores > **Einstellungen > Umgebungsvariablen >
App-Einstellungen**. Die Namen haben zwei Unterstriche:

| Name | Wert | Pflicht |
|---|---|---|
| `Entra__ClientId` | Anwendungs-ID (Client) aus Abschnitt 2 | ja |
| `Entra__ClientSecret` | Wert des geheimen Clientschlüssels | ja |
| `Entra__TenantId` | Verzeichnis-ID (Mandant) des Tenants power-pdf.de | ja (oder `Entra__Authority`) |
| `Entra__AllowedTenants` | Tenant-IDs, die sich anmelden dürfen, durch Kommas getrennt | bei einem einzelnen Tenant optional: dann gilt `Entra__TenantId` |
| `Entra__Authority` | nur in Sonderfällen statt `Entra__TenantId`, z. B. `https://login.microsoftonline.com/organizations/v2.0` | nein |

Hinweise:

- Die Werte gehören nur in die App-Einstellungen (oder Umgebungsvariablen), nie
  ins Repository und nie in die Datenbank. Wer das Secret nicht im Klartext in
  den App-Einstellungen haben möchte, kann eine Key-Vault-Referenz verwenden
  (`@Microsoft.KeyVault(SecretUri=...)`).
- Nach dem Speichern startet der App Service neu. Im Protokoll steht dann
  `Startup: Sign in with Microsoft on, 1 allowed tenant(s)`. Ist die
  Konfiguration unvollständig, steht dort `Startup: Sign in with Microsoft stays
  off: ...` mit dem Grund, und die Funktion bleibt aus.
- Ein Zertifikat statt Secret wird derzeit nicht unterstützt.

## 5. Lokal testen

Mit einer **Kopie** des Datenordners (nie dem Original) und dem Port aus
Abschnitt 2 (PowerShell):

```powershell
$env:ASPNETCORE_ENVIRONMENT = "Development"
$env:Storage__Data = "C:\Temp\addonstore-test\data"
$env:Entra__ClientId = "<Anwendungs-ID>"
$env:Entra__ClientSecret = "<Secret-Wert>"
$env:Entra__TenantId = "<Verzeichnis-ID>"
dotnet run --project server/src/AddonStore.Web --no-launch-profile --urls http://localhost:5297
```

Dann `http://localhost:5297/Account/Login` öffnen und **Mit Microsoft anmelden**
wählen. Das Secret nicht in Skripte oder Dateien im Repository schreiben.

## 6. Was beim Anmelden passiert

1. Microsoft meldet den Benutzer an und schickt ihn zu `/signin-oidc` zurück.
2. Der Store prüft das ID-Token: Signatur, Aussteller eines freigegebenen
   Tenants, Zielgruppe (Client-ID), Gültigkeit, Nonce.
3. Er bestimmt Tenant (`tid`), Objekt-ID (`oid`) und die Adresse (Abschnitt 8).
4. Er sucht das Konto, verknüpft es oder legt es an (Abschnitt 7).
5. Kontostatus wie bei der Passwort-Anmeldung:
   - **Aktiv**: angemeldet, weiter zu Dashboard bzw. Startseite.
   - **Wartet auf Freigabe**: nicht angemeldet, Meldung "Ihre Zugriffsanfrage
     wartet noch auf die Genehmigung durch einen Administrator."
   - **Abgelehnt / deaktiviert**: nicht angemeldet, Meldung "Dieses Konto ist
     nicht aktiv. Wenden Sie sich an einen Administrator."
   - **Gesperrt** (zu viele falsche Passwörter): nicht angemeldet, bis die Sperre
     abläuft.

Rollen (Admin, Reviewer, Developer) kommen **nie** aus Entra; sie bleiben im
Store und werden auf der Benutzerseite vergeben.

## 7. Verknüpfung ohne doppelte Konten

Die Microsoft-Anmeldung wird in der Tabelle `AspNetUserLogins` gespeichert:
Anbieter `Entra`, Schlüssel `{Tenant-ID}:{Objekt-ID}`. Die Objekt-ID ändert sich
nie, auch nicht bei einer Namens- oder Adressänderung.

Reihenfolge beim Anmelden:

1. **Schon verknüpft**: Es gibt einen Eintrag mit diesem Schlüssel. Der Store
   meldet genau dieses Konto an.
2. **Gleiche Adresse**: Es gibt ein Konto mit derselben Adresse, verglichen in
   normalisierter Form, also ohne Rücksicht auf Groß- und Kleinschreibung
   (`Alice@Example.com` = `alice@example.com`). Die Microsoft-Anmeldung wird mit
   diesem Konto verknüpft; es entsteht **kein** zweites Konto. Das Audit-Protokoll
   erhält den Eintrag `user.entra-linked`.
3. **Neue Adresse**: Der Store legt ein Konto im selben Zustand wie bei
   "Zugriff anfordern" an: wartet auf Freigabe, Rolle Developer, ohne Passwort.
   Die Admins werden wie bei einer Zugriffsanfrage benachrichtigt (Ereignis
   "AccessRequest"), das Audit-Protokoll erhält `user.access-requested`. Nach der
   Freigabe kann sich die Person mit Microsoft anmelden.

Umgekehrt legt auch eine spätere Registrierung von Hand mit einer Adresse, die
schon ein Konto hat, kein zweites Konto an: die Seite antwortet wie bei einer
neuen Anfrage und speichert nichts (Identity-Regel "eindeutige E-Mail", die für
alle Wege gilt: Registrierung, Einladung, Admin, Einrichtung, Microsoft).

Im **Profil** steht im Abschnitt **Microsoft-Konto**, ob eine Microsoft-Anmeldung
verknüpft ist. Die Verknüpfung lässt sich dort aufheben, aber nur, wenn das Konto
ein Passwort hat; sonst wäre der Zugang verloren. Konten, die über Microsoft
entstanden sind, haben zunächst kein Passwort und legen bei Bedarf eines über
"Passwort vergessen?" an.

## 8. Welche Adresse übernommen wird

- `tid` muss in `Entra__AllowedTenants` stehen, sonst Meldung "Ihr Microsoft-Konto
  gehört zu einer Organisation, die sich hier nicht anmelden kann".
- Gäste und andere externe Konten (der Anspruch `idp` nennt einen anderen Tenant
  oder `live.com`) werden abgelehnt: ihre Adresse hat der Tenant nicht bestätigt.
- Die Adresse ist `upn`, sonst `preferred_username`; beide liegen bei Mitgliedern
  auf einer Domäne, die der Tenant bestätigt hat.
- Der Anspruch `email` wird nur verwendet, wenn er dieselbe Adresse ist oder
  `xms_edov=true` meldet, dass die Domäne der E-Mail-Adresse bestätigt ist. Eine
  abweichende, unbestätigte `email` wird ignoriert. So kann niemand über ein
  frei eintragbares E-Mail-Feld ein fremdes Store-Konto übernehmen.

Restrisiko: Ein Konto, das jemand von Hand mit einer fremden Adresse registriert
hat, würde bei der ersten Microsoft-Anmeldung des echten Adressinhabers mit ihm
verknüpft. Dagegen steht die Freigabe jedes neuen Kontos durch einen Admin; bei
der Freigabe auf passende Adressen achten.

## 9. Tungsten-Tenant später dazunehmen

Nur Konfiguration, kein neuer Code. Zwei Wege:

**A) Die bestehende App-Registrierung wird mehrinstanzenfähig (empfohlen, wenn
Tungsten-IT zustimmt)**

1. In der App-Registrierung **Authentifizierung > Unterstützte Kontotypen**:
   **Konten in einem beliebigen Organisationsverzeichnis (mehrinstanzenfähig)**.
2. Ein Administrator des Tungsten-Tenants erteilt die Zustimmung, z. B. über
   `https://login.microsoftonline.com/<Tungsten-Tenant-ID>/adminconsent?client_id=<Anwendungs-ID>`.
   Viele Firmen erlauben Benutzern keine eigene Zustimmung für Apps fremder
   Herausgeber; dann ist dieser Schritt Pflicht.
3. App-Einstellungen ändern:
   - `Entra__TenantId` = `organizations`
   - `Entra__AllowedTenants` = `<Tenant-ID power-pdf.de>,<Tenant-ID Tungsten>`
4. App Service neu starten; im Protokoll steht `2 allowed tenant(s)`.

Wichtig: Mit `organizations` könnte sich technisch jeder Arbeits-Tenant bei
Microsoft anmelden; der Store lässt aber nur Tokens aus `Entra__AllowedTenants`
zu (Aussteller und `tid` werden geprüft). Ohne diese Liste bleibt die Funktion
aus.

**B) Tungsten-IT legt eine eigene App-Registrierung im Tungsten-Tenant an**

Dann zeigt der Store nur auf diesen einen Tenant: `Entra__ClientId`,
`Entra__ClientSecret` und `Entra__TenantId` auf die Werte von Tungsten setzen,
`Entra__AllowedTenants` leer lassen oder auf die Tungsten-Tenant-ID setzen.
Verknüpfungen aus dem Test-Tenant bleiben gespeichert, werden aber nicht mehr
benutzt.

## 10. Betrieb

- **Secret erneuern**: vor dem Ablauf in der App-Registrierung ein neues Secret
  anlegen, `Entra__ClientSecret` ersetzen, danach das alte Secret löschen. Ein
  abgelaufenes Secret führt zur Meldung "Die Anmeldung mit Microsoft ist
  fehlgeschlagen"; die Passwort-Anmeldung funktioniert weiter.
- **Fehlersuche**: Abgelehnte Anmeldungen stehen im Protokoll als Warnung der
  Kategorie `AddonStore.Entra` mit dem Grund (`tenant`, `email` oder `failed`).
- **Abschalten**: `Entra__ClientId` (oder eine andere Pflichteinstellung) entfernen
  und neu starten. Bestehende Verknüpfungen bleiben gespeichert; im Profil sehen
  die Benutzer sie weiter und können sie aufheben.
