# Konzept: Kundenauslieferungen mit Code

Stand: Okt 4, 2026 · Status: Entwurf zur Abstimmung (Marcus)

## Ziel

Add-ons, die nur für einen bestimmten Kunden gedacht sind (Projekt-Plug-ins,
Vorabversionen, angepasste Varianten), sollen über denselben Store
ausgeliefert werden wie die öffentlichen Add-ons, ohne öffentlich sichtbar zu
sein. Entwickler und Admins legen Kunden an, erzeugen einen Kundencode und
liefern einzelne Add-ons gezielt an diese Kunden aus. Beim Kunden genügt der
Add-on-Store-Client mit hinterlegtem Code; Installation, Updates, Neustart und
Richtlinien funktionieren wie gewohnt.

## Begriffe

| Begriff | Bedeutung |
|---|---|
| Kunde | Eine Organisation oder ein Projekt beim Kunden (Name, Ansprechpartner, Notiz, Sprache). |
| Kundencode | Geheimer Zugangscode des Kunden, z. B. `K7QM-4XRT-9WPL-2HDN-6CVB`. Wird im Client hinterlegt. |
| Privates Add-on | Paket mit Sichtbarkeit „privat": erscheint nie im öffentlichen Katalog, auf der Website oder im öffentlichen Client. |
| Auslieferung | Zuordnung „Add-on an Kunde", wahlweise „immer neueste freigegebene Version" oder „fest Version x.y.z", mit optionalem Start- und Enddatum. |

## Ablauf aus Sicht der Beteiligten

**Entwickler oder Admin (Portal, Register „Kunden"):**

1. Kunde anlegen. Der Store erzeugt den Kundencode und zeigt ihn an.
2. Eigenes Add-on hochladen und als „privat" markieren (Manifest-Feld
   `visibility: "private"` oder Schalter im Portal). Öffentliche Add-ons
   können zusätzlich an Kunden ausgeliefert werden, z. B. um eine feste
   Version zu garantieren.
3. Auslieferung anlegen: Kunde wählen, Add-on wählen, „neueste" oder feste
   Version, optional Zeitraum.
4. Optional „Kunde informieren": E-Mail an den Ansprechpartner in seiner
   Sprache mit Kundencode, Link zum Client-Download und Kurzanleitung.

**Kunde (IT oder Anwender):**

- IT hinterlegt den Code per Richtlinie
  `HKLM\SOFTWARE\Kofax\PDF\Tungsten Power PDF\PluginStore\Policies\Store\CustomerCode`
  (gilt für alle Arbeitsplätze, Teil der Softwareverteilung), oder
- der Anwender trägt ihn in den Optionen des Add-on Stores ein (Feld
  „Kundencode", gespeichert per DPAPI verschlüsselt im Benutzerprofil).
- Der Store zeigt dann zusätzlich die für diesen Kunden ausgelieferten
  Add-ons, gekennzeichnet mit „Für <Kundenname>". Alles andere bleibt gleich.

**Claude per API:** Alles, was das Portal kann, gibt es auch als API, z. B.
„liefere Version 1.3.0 von com.kunde.xyz an Kunde Muster AG aus". Der
Agent-Guide und SKILL.md bekommen dafür einen eigenen Abschnitt.

## Register „Kunden" im Portal

- **Liste:** Name, Anzahl Auslieferungen, letzte Aktivität (letzter
  Katalogabruf mit diesem Code), Status (aktiv, pausiert). Suche und Filter.
- **Detailseite eines Kunden:**
  - Stammdaten (Name, Ansprechpartner, E-Mail, Sprache, Notiz)
  - Kundencode: anzeigen, kopieren, neu erzeugen (der alte Code gilt noch
    eine einstellbare Übergangszeit, Standard 14 Tage), sperren
  - Auslieferungen: Tabelle mit Add-on, Version (neueste oder fest),
    Zeitraum, Status; Aktionen pausieren, beenden, Version ändern
  - Nutzung: Katalogabrufe, Downloads und installierte Versionen je
    Auslieferung (aus den vorhandenen Zählern, ergänzt um die Kunden-ID)
  - Verlauf: alle Änderungen aus dem Audit-Log
- **Berechtigungen:**
  - Admins sehen und bearbeiten alle Kunden.
  - Entwickler sehen die Kunden, die sie angelegt haben oder für die sie als
    Mitbearbeiter eingetragen sind, und liefern nur eigene Add-ons aus.
  - Reviewer sehen Kunden nur lesend.

## Freigabe

Private Add-ons durchlaufen dieselben automatischen Prüfungen wie öffentliche
(Manifest, Lizenzen, Compliance-Erklärung, Quellcode-Pflicht). Für die
menschliche Freigabe schlage ich eine Instanz-Einstellung vor:

- **Admin-Freigabe erforderlich** (Standard): Eine Auslieferung wird erst
  aktiv, wenn ein Admin die Version freigegeben hat.
- **Automatisch nach bestandener Prüfung:** Entwickler liefern direkt aus;
  Admins werden per E-Mail informiert und können jederzeit stoppen.

## Technik

**Server:**

- Neue Tabellen `Customers`, `CustomerCodes` (nur SHA-256-Hash plus Präfix
  zur Anzeige), `CustomerMembers` (Mitbearbeiter), `Deliveries`.
- `Package.Visibility` (`public` oder `private`).
- Der Client sendet den Code im Header `X-Customer-Code`, niemals in der URL
  (Proxy-Logs). Katalog und Download prüfen den Code und liefern öffentliche
  plus ausgelieferte Pakete. Ein Download eines privaten Pakets ohne
  passenden Code gibt 404 (nicht 403, damit die Existenz nicht verraten wird).
- Schutz vor Durchprobieren: Codes mit rund 100 Bit Zufall, Rate-Limit je IP
  für Code-Prüfungen, Audit-Eintrag bei Fehlversuchsserien.
- Zähler (`UsageStats`, `UsageEvents`) bekommen eine Kunden-ID; die Berichte
  erhalten den Filter „Kunde".
- Alles liegt in der Datenbank und ist damit im Backup.
- Data-Protection-Schlüssel des Servers werden im Datenordner gespeichert
  (heute nicht der Fall, deshalb gehen Anmeldungen bei jedem Neustart
  verloren; wird unabhängig davon jetzt behoben).

**Client:**

- Optionsfeld „Kundencode" (DPAPI), Richtlinie `CustomerCode`, Header bei
  jedem Katalog- und Downloadabruf.
- Kennzeichnung „Für <Kunde>" an Karten und in der Detailansicht.
- Unterstützt mehrere Codes (z. B. ein Dienstleister mit mehreren Kunden),
  getrennt durch Semikolon.

## Code anzeigen oder nur einmal zeigen?

| Variante | Vorteil | Nachteil |
|---|---|---|
| A: Code nur bei Erzeugung anzeigen, gespeichert als Hash | Ein Datenbank- oder Backup-Leck verrät keine Codes | Wer den Code verlegt, muss neu erzeugen (mit Übergangszeit kein Problem) |
| B: Code jederzeit anzeigbar, verschlüsselt gespeichert | Bequemer für den Vertrieb | Wer Datenbank und Schlüssel hat, sieht alle Codes |

Empfehlung: A, wie bei den API-Tokens.

## Umsetzung in Etappen

1. **Server-Grundlage:** Datenmodell, Register „Kunden", Auslieferungen,
   Katalog und Download mit Code, API, Agent-Guide, Backup, 16 Sprachen.
2. **Client:** Kundencode in Optionen und Richtlinie, Kennzeichnung im
   Store-Fenster.
3. **Komfort:** E-Mail „Kunde informieren", Kundenfilter in den Berichten,
   Übergangszeit beim Code-Wechsel, Mitbearbeiter.

## Offene Entscheidungen

1. Freigabe: Admin-Freigabe als Standard, oder dürfen Entwickler direkt
   ausliefern?
2. Code: Variante A (einmal anzeigen) oder B (jederzeit anzeigbar)?
3. Ein Code pro Kunde, oder mehrere Codes je Kunde (z. B. je Standort oder
   Testumgebung)?
4. Sollen Entwickler Kunden untereinander teilen können (Mitbearbeiter), oder
   reicht „eigene Kunden plus Admins"?
5. Datenschutz: Für Kunden speichern wir nur Firmenname, einen Ansprechpartner
   und dessen E-Mail. Passt das, oder sollen Kontaktdaten ganz entfallen?
