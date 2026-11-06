{name} {version} - manual installation for Tungsten Power PDF
==============================================================

For computers and companies that do not use the Add-on Store client.

Install
  1. Close Power PDF.
  2. Unzip this folder and double-click install.bat.
  3. Confirm the administrator prompt once. Start Power PDF again: the add-on
     appears on the "Enhanced Features" tab.

Remove
  Double-click uninstall.bat, or remove "{name} (Power PDF add-on)" under
  Windows Settings > Apps > Installed apps.

Software distribution (Intune, SCCM, ...)
  install.bat /quiet      installs without questions (run as administrator or SYSTEM)
  uninstall.bat /quiet    removes without questions
  Exit code 0 = done. Other users get the ribbon group at their next sign-in.

Please note
  - No automatic updates: a newer version, a fix or a security block of the
    store does not reach this installation. Check the add-on page from time to
    time: {page}
  - The package is checked against the signature of the Add-on Store and its
    checksum before anything is copied.
  - If the Add-on Store client is installed later, it recognises the add-on
    and keeps it up to date from then on.
  - Licences of the components used: LICENSES.md (when the add-on has any).

Add-on id: {id}

----------------------------------------------------------------------

{name} {version} - manuelle Installation für Tungsten Power PDF
==============================================================

Für Computer und Firmen, die den Add-on-Store-Client nicht nutzen.

Installieren
  1. Power PDF schließen.
  2. Diesen Ordner entpacken und install.bat doppelt anklicken.
  3. Die Administrator-Abfrage einmal bestätigen. Power PDF neu starten: das
     Add-on erscheint im Register "Erweiterte Funktionen".

Entfernen
  uninstall.bat doppelt anklicken, oder "{name} (Power PDF add-on)" unter
  Windows-Einstellungen > Apps > Installierte Apps deinstallieren.

Softwareverteilung (Intune, SCCM, ...)
  install.bat /quiet      installiert ohne Rückfragen (als Administrator oder SYSTEM)
  uninstall.bat /quiet    entfernt ohne Rückfragen
  Rückgabewert 0 = erledigt. Andere Benutzer erhalten die Menüband-Gruppe bei
  ihrer nächsten Anmeldung.

Bitte beachten
  - Keine automatischen Updates: eine neuere Version, eine Fehlerbehebung oder
    eine Sicherheitssperre des Store erreichen diese Installation nicht. Schauen
    Sie ab und zu auf die Seite des Add-ons: {page}
  - Das Paket wird vor dem Kopieren gegen die Signatur des Add-on Store und
    seine Prüfsumme geprüft.
  - Wird später der Add-on-Store-Client installiert, erkennt er das Add-on und
    hält es ab dann aktuell.
  - Lizenzen der verwendeten Bausteine: LICENSES.md (falls das Add-on welche hat).

Add-on-ID: {id}
