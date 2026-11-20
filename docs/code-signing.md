# Code-Signatur (Authenticode) für den Add-on-Store-Client

Stand: Nov 20, 2026. Gilt für die Client-Builds nach C2.0.1 (Build-Schritt `client\signing\sign.py`).

Ohne Code-Signatur zeigt Windows beim Start der heruntergeladenen MSI "Unbekannter Herausgeber",
und SmartScreen kann den Start mit "Der Computer wurde durch Windows geschützt" aufhalten. Mit einer
Authenticode-Signatur zeigen UAC-Dialog, SmartScreen und die Dateieigenschaften den Herausgeber aus dem
Zertifikat.

Der Signaturschritt im Build ist **ausgeschaltet, solange nichts eingerichtet ist**: Ohne
Konfiguration schreibt er eine Infozeile und lässt alle Dateien unverändert.

## 1. Was signiert wird, und in welcher Reihenfolge

| Schritt | Datei | Wer signiert | Warum an dieser Stelle |
|---|---|---|---|
| 1 | `client\Release\PluginStore.zxt` | `client\build.cmd` | Plug-in-DLL (PE-Datei, die Endung spielt für Authenticode keine Rolle); muss signiert sein, bevor die MSI sie einpackt |
| 2 | `client\link\Release\AddonStoreLink.exe` | `client\build.cmd` | Link-Helfer für `addonstore://`; ebenfalls vor der MSI |
| 3 | `client\installer\Release\PluginStore-<version>.msi` | `client\installer\build_msi.py`, ganz am Ende | erst nach dem Einbetten der 20 Sprach-Transforms und dem Schreiben der Summary-Information, denn jede spätere Änderung an der MSI zerstört die Signatur; danach entsteht die Kopie `PluginStore.msi` (gleiche Bytes, also ebenfalls signiert) |

Weitere EXE- oder DLL-Dateien gibt es nicht: Der WebView2-Loader ist statisch in die `.zxt` gelinkt,
die MFC- und C++-Laufzeit-DLLs bringt Power PDF selbst mit. Die Zwischendateien
`installer\Release\lang\*.msi` und `*.mst` werden nicht ausgeliefert; die eingebetteten Transforms
sind von der MSI-Signatur mit abgedeckt.

`build_msi.py` prüft bei eingeschalteter Signatur vor dem Packen, dass `.zxt` und `.exe` signiert
sind, und bricht sonst ab. So kann keine MSI mit unsignierten Binärdateien entstehen.

**Die Store-Signatur bleibt davon unabhängig.** Der Add-on Store signiert jedes Paket selbst
(ECDSA P-256 über ID, Version, SHA-256 und Plug-in-Namen, geprüft in `client\store\signature.cpp`).
Diese Katalogsignatur entsteht beim Upload auf dem Server und hat mit Authenticode nichts zu tun.
Wichtig ist nur die Reihenfolge: zuerst Authenticode (Build), dann `make_ppak.py`, dann Upload. Wer
nach dem Packen noch signiert, ändert den Paket-Hash. Der Store-Server prüft heute keine
Authenticode-Signaturen; er verlässt sich auf seine eigene Signatur.

**Was sich durch die Signatur nicht ändert:** Installation, Update und Entfernen von Add-ons sowie
das Selbst-Update des Clients laufen über einen erhöhten Schritt mit Windows PowerShell. Diese
UAC-Abfrage zeigt weiterhin "Windows PowerShell, Microsoft Windows" als Herausgeber. Den eigenen
Herausgebernamen sehen Anwender bei der direkt gestarteten MSI (Download von der Store-Startseite)
und in den Dateieigenschaften.

## 2. Der Tungsten-Weg: Azure Trusted Signing

Tungsten signiert intern mit **Azure Trusted Signing** (Microsoft hat den Dienst nach unserem
Kenntnisstand inzwischen in "Artifact Signing" umbenannt; bitte beim Anbieter bzw. bei IT prüfen,
welcher Name im Portal steht). Der Ablauf:

1. **Helpdesk-Ticket** für Azure Trusted Signing stellen. IT richtet den Zugang ein und nennt
   - den Endpunkt (z. B. `https://weu.codesigning.azure.net`, je nach Region),
   - den Namen des Code-Signing-Kontos und des Zertifikatprofils,
   - und, falls vorhanden, das Signierskript von IT.
   Das eigene Azure-Konto braucht auf dem Zertifikatprofil die Rolle zum Signieren
   ("Trusted Signing Certificate Profile Signer"; die genaue Rollenbezeichnung bitte bei IT prüfen).
2. **Client-SDK installieren**:
   - Windows SDK mit "Signing Tools" (signtool.exe; für Trusted Signing mindestens Version
     10.0.22621.755, bitte beim Anbieter prüfen; auf dem Entwicklungsrechner liegt 10.0.26100),
   - das Trusted-Signing-Client-Paket von Microsoft (NuGet `Microsoft.Trusted.Signing.Client`
     bzw. der Installer "Trusted Signing Client Tools"); daraus wird
     `bin\x64\Azure.CodeSigning.Dlib.dll` gebraucht. Ob zusätzlich eine .NET-Laufzeit nötig ist,
     hängt von der Paketversion ab (bitte beim Anbieter prüfen).
3. **Anmelden**: Trusted Signing meldet sich mit der Azure-Identität des Benutzers oder Rechners an
   (DefaultAzureCredential: `az login`, Visual Studio, Umgebungsvariablen oder Managed Identity).
   Zugangsdaten gehören weder ins Repository noch in die Konfigurationsdatei; der Build weist eine
   Konfiguration mit Kennwort-, Schlüssel- oder Token-Feldern ab.
4. **Konfiguration anlegen** (Abschnitt 4), entweder mit der Dlib oder mit dem Skript von IT.
5. **Bauen** wie gewohnt (`docs\RELEASING.md`): `client\build.cmd`, dann
   `client\installer\build_msi.cmd`. Die `[sign]`-Zeilen zeigen, was signiert wurde.

Die Zertifikate von Trusted Signing sind nach unserem Kenntnisstand nur wenige Tage gültig (bitte
beim Anbieter prüfen). Deshalb ist der RFC-3161-Zeitstempel Pflicht: Er hält die Signatur auch
nach Ablauf des Zertifikats gültig. Der Build verlangt ihn standardmäßig.

## 3. Alternativen

| Weg | Was man braucht | Hinweise |
|---|---|---|
| OV-Zertifikat einer Zertifizierungsstelle | Firmenvalidierung durch die CA, Schlüssel auf Hardware-Token oder HSM | Seit Jun 1, 2023 verlangen die Regeln des CA/Browser Forum für alle öffentlich vertrauenswürdigen Code-Signing-Zertifikate einen Schlüssel in zertifizierter Hardware (Token, HSM oder Cloud-HSM); eine PFX-Datei gibt es dafür nicht mehr. Preise, Laufzeiten und ob Einzelpersonen ein Zertifikat bekommen: bitte beim Anbieter prüfen. |
| EV-Zertifikat | strengere Validierung, ebenfalls Hardware | Früher brachte EV sofort SmartScreen-Reputation; nach unserem Kenntnisstand hat Microsoft das 2024 geändert, sodass auch EV-signierte Dateien Reputation erst aufbauen. Bitte beim Anbieter prüfen. |
| Azure Trusted Signing für eigene Konten | Azure-Abonnement, Identitätsprüfung durch Microsoft | Für Tungsten läuft das über IT (Abschnitt 2). Für wen der Dienst außerhalb davon offensteht (Organisationen, Einzelpersonen, Länder) und was er kostet: bitte beim Anbieter prüfen. |

SmartScreen bewertet Datei und Herausgeber nach Verbreitung. Auch mit gültiger Signatur kann eine
neue Datei anfangs eine Warnung zeigen, bis genug Reputation besteht. Wie schnell das geht, ist
nicht vorhersagbar (bitte beim Anbieter prüfen).

## 4. Konfiguration des Builds

Die Konfiguration ist eine JSON-Datei **außerhalb des Repositorys**:

- Standardort: `%LOCALAPPDATA%\AddonStoreBuild\signing.json`
- oder ein beliebiger Pfad in der Umgebungsvariable `ADDONSTORE_SIGN_CONFIG` (dann muss die Datei
  existieren, sonst bricht der Build ab).

Eine Konfigurationsdatei im Repository wird abgewiesen. PFX-, PEM- oder Schlüsseldateien und Felder
wie `password`, `secret` oder `token` werden ebenfalls abgewiesen.

**A. Trusted Signing mit signtool und Dlib** (empfohlen):

```json
{
  "method": "trusted-signing",
  "dlib": "C:\\Tools\\TrustedSigning\\bin\\x64\\Azure.CodeSigning.Dlib.dll",
  "metadata": "C:\\Tools\\TrustedSigning\\metadata.json",
  "timestampUrl": "http://timestamp.acs.microsoft.com",
  "description": "Add-on Store for Tungsten Power PDF",
  "descriptionUrl": "https://addon.power-pdf.de",
  "expectedSubject": "Tungsten Automation"
}
```

`metadata.json` (Werte von IT, keine Geheimnisse):

```json
{
  "Endpoint": "https://weu.codesigning.azure.net",
  "CodeSigningAccountName": "<Konto>",
  "CertificateProfileName": "<Profil>"
}
```

Der Build ruft dafür auf:
`signtool sign /fd SHA256 /dlib <dlib> /dmdf <metadata> /tr <timestampUrl> /td SHA256 [/d ...] [/du ...] <Datei>`.
signtool wird im neuesten Windows SDK gesucht; ein anderer Pfad geht über `"signtool"`.

`expectedSubject` ist bei Trusted Signing Pflicht. Grund (im Test nachgestellt): Kann signtool die
Dlib nicht laden, meldet es das nicht, sondern sucht sich ein Zertifikat aus dem eigenen
Zertifikatspeicher. Liegt dort genau eines, würde damit signiert. Die Prüfung des Herausgebers
fängt das ab. Zusätzlich prüft der Build vorab, dass die Dlib wirklich eine DLL ist. Optional
lässt sich mit `expectedIssuer` auch der Aussteller festlegen (bei Trusted Signing nach unserem
Kenntnisstand eine Microsoft-CA mit "Microsoft ID Verified" im Namen; bitte am ersten echt
signierten Build ablesen, bevor der Wert eingetragen wird).

**B. Signierskript von IT** (ohne Codeänderung einsetzbar):

```json
{
  "method": "script",
  "script": "C:\\Tools\\Signing\\Sign-File.ps1",
  "expectedSubject": "Tungsten Automation"
}
```

Das Skript wird je Datei einmal aufgerufen:
`powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File <script> <Datei>`.
Exit-Code 0 heißt "signiert". Danach prüft der Build die Datei selbst (Abschnitt 5); ein Skript,
das 0 meldet, aber nicht signiert hat, lässt den Build scheitern. Braucht das Skript PowerShell 7,
den Pfad in `"powershell"` angeben (z. B. `C:\\Program Files\\PowerShell\\7\\pwsh.exe`).

**C. Zertifikat im Windows-Zertifikatspeicher** (z. B. OV-Zertifikat auf Token):

```json
{
  "method": "certstore",
  "thumbprint": "<40 Hex-Zeichen>",
  "store": "My",
  "machineStore": false,
  "timestampUrl": "<RFC-3161-Adresse der CA>"
}
```

Aufruf: `signtool sign /fd SHA256 /sha1 <thumbprint> /s My [/sm] /tr <timestampUrl> /td SHA256 <Datei>`.

**Weitere Felder** (alle optional): `expectedIssuer`, `requireTimestamp` (Standard `true`), `timeoutSeconds`
(Standard 300), `allowUntrustedRoot` (Standard `false`, nur für einen lokalen Test mit
selbstsigniertem Zertifikat, nie für Releases).

Was eingerichtet ist, zeigt: `python client\signing\sign.py --status`.

Verhalten:

- nicht eingerichtet: eine Infozeile, Dateien unverändert, Build läuft weiter;
- eingerichtet: jede Datei wird signiert und geprüft; scheitert Signatur oder Prüfung, endet
  `build.cmd` mit Fehler (`SIGN_EXIT=1`, `MSB_EXIT=1`) bzw. `build_msi.py` mit "Code signing failed".

## 5. Prüfen

Der Build prüft jede signierte Datei mit `Get-AuthenticodeSignature`: Signatur vorhanden und
gültig, Zeitstempel vorhanden, bei `certstore` der richtige Fingerabdruck, bei `expectedSubject`
der erwartete Herausgeber.

Von Hand:

```bat
signtool verify /pa /v client\Release\PluginStore.zxt
signtool verify /pa /v client\link\Release\AddonStoreLink.exe
signtool verify /pa /v client\installer\Release\PluginStore.msi
```

`/pa` prüft nach der Authenticode-Richtlinie; die Ausgabe nennt Aussteller, Zertifikatskette und
Zeitstempel. Im Explorer: Rechtsklick auf die Datei, Eigenschaften, Registerkarte
"Digitale Signaturen". Beim Start der MSI zeigt der UAC-Dialog "Verifizierter Herausgeber".

**Test mit selbstsigniertem Zertifikat** (nur lokal, ohne Vertrauensänderung):

```powershell
$c = New-SelfSignedCertificate -Type CodeSigningCert -Subject "CN=AddonStore Signing Test (delete me)" `
     -CertStoreLocation Cert:\CurrentUser\My -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
     -NotAfter (Get-Date).AddDays(2)
$c.Thumbprint
```

Dann eine Konfiguration `certstore` mit diesem Fingerabdruck, einer öffentlichen
Zeitstempeladresse und `"allowUntrustedRoot": true` anlegen, bauen, prüfen. `signtool verify /pa`
meldet dabei erwartungsgemäß, dass die Kette in einem nicht vertrauenswürdigen Stammzertifikat endet.
Das Zertifikat danach löschen und die Konfiguration entfernen. `New-SelfSignedCertificate` legt
zusätzlich eine Kopie unter "Zwischenzertifizierungsstellen" (`Cert:\CurrentUser\CA`) ab, daher
alle Kopien entfernen:
`Get-ChildItem Cert:\CurrentUser -Recurse | Where-Object Thumbprint -eq '<Thumbprint>' | Remove-Item`. Das Testzertifikat nie in den Stammzertifikatspeicher aufnehmen, und
damit signierte Dateien nie ausliefern: nach dem Test ohne Konfiguration neu bauen.

## 6. Herausgebername

Windows zeigt als Herausgeber den Namen aus dem Zertifikat (Subject, bei Organisationen der von der
CA bzw. Microsoft geprüfte Firmenname). Er lässt sich im Build nicht frei wählen.

- Mit dem Tungsten-Konto von Trusted Signing steht dort der von Microsoft validierte Name der
  Tungsten-Gesellschaft. Das passt zu `Manufacturer="Tungsten Automation"` in der MSI und
  `CompanyName` in den Versionsressourcen.
- Ein Zertifikat auf eine Einzelperson würde deren Namen zeigen, obwohl Produkt und MSI
  "Tungsten Automation" nennen.

**Offene Frage an Legal/Brand:** Darf der Add-on-Store-Client mit dem Tungsten-Zertifikat signiert
werden, und welcher genaue Firmenname (welche Gesellschaft) soll als Herausgeber erscheinen? Erst
danach `expectedSubject` auf diesen Namen setzen.
