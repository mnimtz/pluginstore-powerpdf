<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/img/tungsten-logo-white.svg">
  <img src="docs/img/tungsten-logo-navy.svg" alt="Tungsten Automation" width="320">
</picture>

# Add-on Store for Tungsten Power PDF

**A private plugin store for Tungsten Power PDF.**
Publish `.ppak` plugin packages through a reviewed pipeline; end users browse
and install them with one click from an *Add-on Store* ribbon inside Power PDF.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fmnimtz%2Fpluginstore-powerpdf%2Fmain%2Finfra%2Fazuredeploy.json)

![.NET 10](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square)
![C++ /MFC client](https://img.shields.io/badge/client-C%2B%2B%20%2F%20MFC%20.zxt-00A0FB?style=flat-square)
![i18n](https://img.shields.io/badge/i18n-16%20European%20languages-00EB86?style=flat-square)
![Brand](https://img.shields.io/badge/design-Tungsten%20Brand%20Book-002854?style=flat-square)
![License: MIT](https://img.shields.io/badge/license-MIT-brightgreen?style=flat-square)

<img src="docs/img/web-catalog.svg" alt="The Add-on Store web catalog" width="760">

</div>

---

## Why

The Add-on Store consolidates Power PDF extensions in one place. Anyone who
builds a plugin with the Plugin SDK can offer it here centrally: it passes
automatic quality checks and a review, and users find, install and update it
directly inside Power PDF.

## How it works

```
 Developer (+ AI assistant, API token)       Admin                 End user
        │                                      │                       │
        │ POST /api/packages/validate          │ review queue          │ Add-on Store ribbon
        │ POST /api/packages                   │ approve / reject      │ browse · install · update
        ▼                                      ▼                       ▼
  ┌──────────────────────────────────────────────────────────────────────────┐
  │   Add-on Store server · Azure Web App · SQLite + packages on /data      │
  │                                                                          │
  │   submitted ──(automatic checks)──▶ BETA channel ──(approval)──▶ LIVE    │
  └──────────────────────────────────────────────────────────────────────────┘
```

| | |
|---|---|
| 🏪 **End users never see this server** | The catalog is read anonymously by the Add-on Store ribbon add-on. Accounts exist only for plugin developers and admins; registration is an access *request* that an admin approves. Roles: **Developer** (uploads and maintains own add-ons; called "User" before S0.9.0), **Reviewer**, **Admin**. |
| 🔍 **Every upload is validated** | Manifest schema, SemVer monotonicity, x64 + ARM64 PE checks, debug-runtime detection, import-table scan, SHA-256 verification, ribbon governance, reserved names, layout and localization pitfalls. Every finding carries a stable `code` and a concrete `hint`. |
| 🗄️ **Source code escrow** | After every upload the agent also sends the source code of that version. It is checked (credentials, GPL/AGPL, build output), stored next to the version, visible to admins only and part of every backup; by default a version cannot be approved without it. |
| 🔁 **Source round trip for admins** | An admin (or their AI assistant) fetches an add-on's stored source (`GET /api/packages/{id}/source/latest`), changes it, publishes a higher version for the original owner and uploads the changed source for that new version, so the store holds matching source for every version. Steps in the agent guide and SKILL.md. |
| 🔗 **Share links for sales** | Every add-on has its own public page `/a/<short name>` (e.g. `/a/smartbookmarks`; the full id works too) with icon, description, what's new, the "Install in Power PDF" button, the client download and a share box: copy the link or open a prepared email in the page's language. Link previews in Teams/Outlook show name, description and icon (Open Graph). An optional `?ref=<short name>` attributes page views, install clicks and client downloads to the person who shared the link (reports, "Shared links"); link-preview bots are not counted. Catalog cards have a "Share link" button; the JSON catalog carries `pageUrl`. |
| ⭐ **Ratings, problem reports, screenshots** | Users rate add-ons (1 to 5 stars, one rating per installation, anonymous install id) and send problem reports or comments from the store window inside Power PDF, optionally with a reply address and a log excerpt. The average shows on catalog cards, add-on pages and in the client; reports reach the owner by email and are listed on the plug-in page and via `GET /api/packages/{id}/feedback` (owner/admin, so an AI assistant can work through them). Packages may carry up to 6 screenshots with captions (manifest `screenshots`), shown as a gallery on the website and in the client. |
| 🤖 **AI assistant (optional)** | Off by default; an admin picks Claude (official Anthropic SDK) or Gemini, enters the key (encrypted with the server's data protection keys), clicks "Connect and load models" and then chooses the model from the list the provider offers for that key (loaded live), switches each part on and runs a test request: problem reports are sorted in the background (category, urgency, summary in English and German, reply draft, duplicate hint); reviewers get a review aid per version (what changed against the previous version, does the changelog match, concerns such as new hosts or third-party code, a recommendation), on request or automatically, in any of the 16 store languages (automatic ones in the language set by the admin; a viewer whose UI language differs can recreate it in theirs); visitors and the store window search by need ("split scanned invoices by barcode") with a reason per hit. Never sent: email or IP addresses, log excerpts, accounts; changed source lines only with an extra option. Daily request limit, connection test, every change audited. Without AI the plain word search answers. |
| 🔏 **Signed catalog** | Every catalog entry is signed by the server (ECDSA P-256 over id, version, SHA-256 and binary name; TSV column 21, JSON `signature`, key at `/api/signing-key`). Clients 0.7.1+ install only packages signed with a key they trust (built in, or HKLM policy `TrustedSigningKeys`), so a changed server address in the user profile cannot deliver foreign packages. Key: App Setting `Signing__PrivateKeyPem`, else created once and stored encrypted in the database. |
| 🛡️ **Admin safeguards** | Backups download with the data protection key ring encrypted by a password (AES-256-GCM, PBKDF2) or without it; optional four-eyes rule (nobody approves a version they uploaded or own); first-run setup needs a one-time token from the server log or `data/setup-token.txt` (24 h). |
| 🏢 **Customer deliveries** | Register "Customers": developers and admins create customers, deliver add-ons (also private ones that never appear in the catalog) with a beta and a live stage each ("newest", fixed version or off; "Make live" moves the tested beta version to all workstations), an optional period, pause or end. Codes per customer (all its deliveries) and per delivery, shown any time to the people who manage the customer, replaced with a transition period or revoked. Clients send codes only in the `X-Customer-Code` header; private add-ons answer 404 to everyone else; brute-force protection per address. The customer page offers private add-ons only and can delete the customer with its codes and deliveries (also `DELETE /api/customers/{cid}`); new customers are created behind a "New customer" button; the plug-in list shows each add-on's visibility with a switch (public / private, with confirmation), and `GET /api/packages/{id}` reports it; `make_ppak.py` takes `"visibility": "private"` from the spec so a customer add-on is private from its first upload. Developers manage their own customers and may deliver every add-on, also other developers' private ones (the owner is informed by email); admins are informed by email. |
| 🔖 **Bookmarks and shortcuts** | Power PDF's own application icon (taken from PowerPDF.exe) as favicon, Apple touch icon and web-app icon (`/site.webmanifest`, localized name, brand colours); page titles in the visitor's language ("Add-on Store für Tungsten Power PDF", sub pages "<page> · Add-on Store für Power PDF"); meta description and an Open Graph preview card (`/img/social-card.png`) for links in Teams, Outlook and messengers. |
| 📊 **Reports** | Admin tab with downloads per add-on (ranking, trend against the previous period, share, sparkline), downloads and store-window openings per day, week or month, client versions in use, Power PDF and Windows versions, architecture (x64/ARM64), client languages, download sources, countries, cities and network operators (IP geolocation by DB-IP, CC BY 4.0), submissions and review times, developers and versions without source. A navigation on the left splits the report into sections (overview, add-ons, shared links, clients, locations, submissions, developers, source code, IP address logging); long lists are paged with 25, 50 or 100 rows per page, and the IP requests are paged in the database. The same paging applies to the audit log (paged in the database), users, plug-ins, customers, versions, problem reports and the public catalog (in the browser, on top of search and category filter). Filters for period, add-on, country and source; report language selectable (16 languages); CSV export and a print view for "Save as PDF" with all sections. **Settings → Reset statistics** deletes collected test data (downloads and usage, shared links, stored IP addresses, optionally ratings and problem reports) after an automatic safety backup, so the reports count from a chosen point on; add-ons, accounts, customers and the audit log stay. |
| 🕵️ **IP address logging (optional, GDPR)** | Off by default. Daily counters never store the IP address itself. An admin can switch on per-request logging (IP, host name, location, network, user agent) in the settings after a one-time GDPR confirmation (who and when is kept and audited), with a retention period (1 to 730 days, deleted automatically) or, after a second explicit choice, permanent storage; one button deletes all stored IP addresses. Every export is audited. |
| 🏷️ **Growing categories** | Every upload names a category. If none fits, the upload may propose a new high-level one (16 languages); the server rejects names that are too close to an existing category, too specific or beyond the limit, and creates it on submission. Admins merge or delete categories. |
| ✏️ **Editable catalog entry** | Owners and admins correct name, description (16 languages), author and contact on the server or via `PATCH /api/packages/{id}`, without a new version; the Power PDF client shows the change immediately. |
| 🧪 **Beta channel** | Versions that pass all automatic checks become instantly installable for users who enabled the beta option in Power PDF, while an admin reviews them for the live store. |
| 🤖 **Agent-friendly API, any assistant** | `GET /api/agent-guide` teaches any AI assistant (Claude, ChatGPT, Gemini, Copilot or your own agent) the full workflow with zero prior knowledge. Vendor-neutral entry points: `/llms.txt`, an OpenAPI 3.1 description at `/api/openapi.json` (ChatGPT custom GPT actions, Gemini function calling, Postman, code generators) and an `AGENTS.md` at `/api/agents-md` for coding assistants (Codex, Copilot, Cursor, Gemini CLI); the Claude Code skill is one more option. Plain HTTPS, bearer token, JSON. Personal tokens let the assistant validate, fix and submit packages in a loop until the report is green. CI fails when a route is missing from the OpenAPI document. |
| 📜 **Audit trail** | Registrations, approvals, tokens, submissions (version + changelog are mandatory), reviews and downloads are recorded and searchable. |
| 💾 **Backup and restore** | One click downloads a full backup (database including usage counters and stored IP events, packages and source code, avatars, developer kit); restore checks the archive, refuses to lock out the acting admin, keeps an automatic safety backup of the previous state, unpacks everything next to its target before anything is replaced (a damaged archive or a full disk puts the previous state back automatically), asks before a backup without key ring is restored on a server that cannot read its signing key, and upgrades an older backup to the current schema right away. The geolocation databases (data/geo) are not backed up; the server downloads them again. **Automatic backup** (S0.18.0) to Azure Blob Storage: one access key (connection string or SAS URL), a passphrase that encrypts the whole backup before it leaves the server (AES-256-GCM in chunks, PBKDF2-SHA256), time and weekdays in the server time zone, how many backups to keep; "Back up now", connection test, restore straight from the container or from a downloaded .psbak file, email to the admins when a run fails. |
| ✉️ **Notifications** | Email via Resend with a test button (free recipient); admins choose which events send mail: access requests, submissions, client releases, and for authors upload receipts, approval or rejection, withdrawn or restored versions and catalog changes by an admin. Authors can opt out in their profile. Every sent or failed mail is in the audit log. |
| 🗂️ **Plug-in management** | One compact overview (admins: all plug-ins, authors: their own) with search and filters (awaiting approval, live, beta, private, not in the store) and a visibility switch per plug-in; a details page per plug-in to approve, reject, withdraw or restore versions, set a live version back to beta (admins; it needs approval again), take a plug-in out of the store and edit its catalog entry. |
| 📱 **Phone-ready** | Menu button and card layout on small screens; the whole portal works on a smartphone. |
| 🛡️ **Licenses, legal and privacy** | Every upload carries a mandatory, truthful compliance statement (`thirdParty` components with SPDX licenses, `complianceAudit` with the external services a plugin contacts). The server verifies independently: copyleft and known-library signatures in binaries, credentials and key files, hosts compiled into the code, third-party brand names. Only MIT/BSD/Apache-2.0 code passes without review; GPL/AGPL/LGPL fails. |
| ⚖️ **Disclaimer** | Landing page, a dedicated disclaimer page and the Power PDF client state that plugins come from independent authors, without warranty or official support, and that Tungsten Automation accepts no liability. |
| 🌍 **16 European languages** | Auto-detected from the browser (including `no`/`nn` for Norwegian), manually switchable, with localized catalog texts straight from the package manifests. |

## Deploy in one click

Press **Deploy to Azure** above and fill in one required parameter:

| Parameter | Default | Meaning |
|---|---|---|
| `siteName` | *(required)* | Globally unique name → `https://<siteName>.azurewebsites.net` |
| `sku` | `B1` | ~13 USD/month, always-on. `F1` is free but sleeps when idle. |
| `containerImage` | `ghcr.io/mnimtz/pluginstore-powerpdf:latest` | Pin a version tag for reproducible deployments |
| `tz` | `Europe/Berlin` | IANA timezone |

The template provisions a Linux App Service running the GHCR container plus a
storage account with a file share mounted at `/data` (SQLite database, plugin
packages, developer kit). No connection strings, no secrets to manage.

**First run:** open the site, the setup wizard creates the first administrator
account. Colleagues use *Request access* on the login page; admins approve on
the Users page.

**Email notifications** via [Resend](https://resend.com): enter the API key
and sender on the admin **Settings** page (or as app settings
`Email__ResendApiKey` / `Email__From`), use **Send test email** to check the
setup, and choose which events send mail. Without a key the app runs
normally; skipped mails are recorded in the audit log.

**Backup and restore:** admin **Settings → Backup and restore**. Store backup
files like passwords: they contain password hashes and settings. For automatic
backups, paste the connection string of an Azure storage account (Azure portal:
storage account → Access keys → Connection string) or a SAS URL under
**Automatic backup**, set a passphrase (keep it outside the server), time and
weekdays; the page has a short guide. Restore works straight from the container
or from a downloaded `.psbak` file with the passphrase. Disaster recovery on a new server: run the
first-run setup, enter the same access key and passphrase, restore from the
container, then sign in with an admin account from the backup.

## The Power PDF client

The `client/` folder holds the **Add-on Store ribbon add-on** (C++/MFC `.zxt`,
built with the Power PDF Plugin SDK):

- Adds its own **Store** ribbon tab (client 1.1.0+; before: a group on the shared tab).
- **Modern store window** (client 0.4+): cards with icon, status and one-click
  action, search, category chips and a detail panel, rendered by Microsoft
  WebView2 from a page compiled into the plug-in (`client/ui/store.html`). Data
  is inserted as text only; the page cannot navigate or load anything external.
  Without the WebView2 runtime, or with `ClassicUI = 1` (HKCU or the HKLM
  policy key), the classic list dialog opens instead.
- **Restart and self-update** (client 0.4.1+/0.4.2+): after an install or
  removal Power PDF closes and a helper starts it again. Updating the store
  client asks once, closes Power PDF, installs the new MSI with a progress
  bar (one UAC prompt) and starts Power PDF again, also when the installation
  was cancelled. Helper steps are logged to `%TEMP%\PluginStore.log`, the MSI
  log to `%TEMP%\AddonStoreUpdate.log`.
- **Ratings, problem reports, screenshots** (client 0.5.0+): stars on every
  card, the user's own rating (installed add-ons, one per installation; a
  random install id in HKCU, only a per-package hash reaches the server),
  "Report a problem" with an optional reply address and log excerpt, and a
  screenshot strip with enlarged view in the detail panel.
- **Update badge, search by need, customer code** (client 0.6.0+): about
  15 s after Power PDF starts (and after the store window closes) the client
  reads the catalog in the background and puts an amber dot on the ribbon
  button when the store client or an installed add-on has a NEWER version
  (an older catalog version is never offered). Enter in the search box asks
  the server's search by need. A customer code (Options page, or policy
  `CustomerCode`) is sent as `X-Customer-Code` and unlocks add-ons delivered
  to that customer; they show a "For <customer>" label.
- **Hardened installation** (client 0.6.0+): catalog fields are validated
  (id, version, SHA-256, binary name, URLs); WinHTTP follows no redirects and
  uses TLS 1.2 or newer; downloads stop at the announced size. The elevated
  step runs `%SystemRoot%\System32\...\powershell.exe` with the script in
  memory (`-EncodedCommand`, no script file), copies the package into an
  admin-only staging folder under `Plug-Ins`, checks the hash there again and
  only then unpacks it. The self-update helper checks the hash again before
  it starts `msiexec.exe` by full path.
- **Signed packages, responsive window** (client 0.7.1+): the client installs
  only catalog entries signed by a trusted store key (built in: the store
  instance and an offline recovery key; HKLM policy `TrustedSigningKeys` adds
  keys of a company's own instance, `AllowUnsigned = 1` switches the check off
  for test servers; nothing in HKCU can add a key). Network work of the store
  window (catalog, icons, screenshots, ratings, reports, search, install and
  removal) runs on worker threads. The self-update copies the package into an
  admin-only staging folder under `Plug-Ins`, checks the hash there and runs
  the MSI from there (one UAC prompt).
- **Usage statistics** (client 0.4.2+): the client's user agent carries its
  version, the Power PDF and Windows version and the native architecture
  (x64/arm64), e.g. `AddonStore-PowerPDF/0.4.2 (PowerPDF 15.1.0.555; Windows
  10.0.26200; arm64)`. Technical data only, used for the admin reports.
- Lists the catalog with localized names, changelogs, installed versions and
  update status; installs with SHA-256 verification and a single UAC prompt.
- Drops each package's `manifest.json` next to the plugin, so updates are
  detected for every plugin, including MSI-deployed ones.
- Options page under *File → Options → Add-on Store*: server URL (defaults to
  your instance, enforceable via HKLM policy) and the beta-channel switch.
- **One firewall rule is enough:** catalog, downloads and self-updates use
  HTTPS (port 443) to the configured store host only, through the system proxy.
  The client refuses plain HTTP (except localhost for development) and any
  other host, whatever the catalog says.
- **"Install in Power PDF" buttons** on the catalog website: the MSI registers
  the `addonstore://` URL scheme with a small helper (`AddonStoreLink.exe`) that
  passes a validated package id to the client; Power PDF opens the store dialog
  with that add-on selected and asks before installing. A website can only open
  the dialog, never install anything by itself.
- The first browser download of the unsigned MSI may trigger Windows SmartScreen
  ("More info" → "Run anyway"); the landing page explains this. Updates and
  plug-ins installed from the client are not affected.
- A fresh MSI installation (not an update) clears leftovers of removed plugins
  from the shared *Enhanced Features* tab once per user; installed plugins add
  their groups again on their next start.

End users install it with the MSI from the store's landing page
(`/download/pluginstore.msi`); afterwards the client offers its own updates.

### Deploying the client in companies

The MSI installs per machine and runs silently, best as its own step right
after Power PDF (Intune, SCCM, GPO, scripts):

```bat
msiexec /i PluginStore-<version>.msi /qn /norestart /l*v "%TEMP%\AddonStore.log"
```

- Requires Power PDF to be installed first (the MSI finds it via App Paths or
  `HKLM\SOFTWARE\Kofax\PDF`). Installing before Power PDF was ever started is
  fine: the layout is merged on the first start.
- Preconfigure for all users under
  `HKLM\SOFTWARE\Kofax\PDF\Tungsten Power PDF\PluginStore\Policies\Store`:
  `ServerUrl` (REG_SZ, locks the URL), `BetaChannel` (DWORD), `LockPage`
  (DWORD 1 locks the options page), `ClassicUI` (DWORD 1),
  `DisableInstall` (DWORD 1: browse only, no install or removal; client
  0.4.3+), `DisableSelfUpdate` (DWORD 1: no store-client update from the
  store, for IT-managed MSI rollouts; client 0.4.3+), `CustomerCode` (REG_SZ,
  sets and locks the customer code; client 0.6.0+), `UpdateBadge` (DWORD 0:
  no background update check; client 0.6.0+). DWORD values may also be
  deployed as a decimal REG_SZ such as "1" (client 0.6.0+),
  `TrustedSigningKeys` (REG_SZ or REG_MULTI_SZ, `keyId:base64` of the public
  key from `/api/signing-key`, for an own store instance; client 0.7.1+),
  `AllowUnsigned` (DWORD 1, test servers only; client 0.7.1+). From 32-bit
  deployment agents use `reg add ... /reg:64`.
- Intune detection rule: the file `<bin>\Plug-Ins\PluginStore.zxt` with a
  minimum version (the ProductCode changes with every version).
- **Customer code in the store window, MSI in 16 languages** (client
  0.8.0+): a "Customer code" button in the store header opens a dialog that
  checks the code at `GET /api/customer-code` before saving it (unknown codes
  count toward the brute-force limit) and shows the customer and the number
  of unlocked add-ons; customer add-ons come first in the list and get their
  own filter chip; "Remove code" clears it (both locked when the policy
  `CustomerCode` is set). The client MSI carries all 16 languages as embedded
  language transforms (`python client\installer\build_msi.py`, texts in
  `client\installer\make_l10n.py`); Windows picks the user's language, and
  the self-update passes the store window's language as `TRANSFORMS=:<LCID>`.
- **Own ribbon tab "Store"** (client 1.1.0+): the store client sits on its own
  tab "Store" (toolbar atom `AddonStore`, Alt then A); add-ons stay on the
  shared "Enhanced Features" tab. The client moves itself there in existing
  profiles (removes its old group from the shared tab) at the first start.
  Tab order (client 1.1.2+): ... *Enhanced Features*, own tabs of private
  add-ons, *Store*, and Power PDF's *Help* always last.
- The Power PDF Customization Kit can add the files of a plug-in ("Additional
  Files"), but no registry values (URL scheme, policies); the separate silent
  MSI step is the recommended way.

Build with `python tools\fetch_webview2.py` (once), `client\build.cmd` and `client\installer\build_msi.cmd`
(Visual Studio 2022, WiX v3 in C:\Claude\Tools\wix314, Python 3.12 or older for msilib), deploy for testing with an elevated
`client\deploy.cmd`. Publishing a new client version (own lane: admin-only,
live immediately) is described step by step in
[docs/RELEASING.md](docs/RELEASING.md).

## API in 30 seconds

**One URL is enough for an AI assistant** (Claude, ChatGPT, Gemini, Copilot
or any other): tell it
*"Read https://<host>/agent-guide and publish the plugin in this folder."* (`/agent-guide` is the guide as a web page, which every assistant's web reader accepts; `/api/agent-guide` is the same text as text/plain, or text/markdown when the Accept header asks for it; HEAD works on all documentation addresses, and `/robots.txt` allows them)
The guide explains the token (environment variable `PPAK_TOKEN`), lets the
assistant remember the store in the way its tool supports (`AGENTS.md`, the
OpenAPI description or the Claude Code skill) and walks through packaging,
validation and submission. Signed-in users find the same instructions on the
**API** page of the web UI.

```bash
curl https://<host>/api                  # discover all endpoints
curl https://<host>/api/agent-guide      # the full guide (markdown text)
curl https://<host>/api/schema/manifest  # manifest.json schema

curl https://<host>/llms.txt             # short index for language models
curl https://<host>/api/openapi.json     # OpenAPI 3.1 (GPT actions, function calling, tools)
curl https://<host>/api/agents-md        # AGENTS.md for coding assistants
curl https://<host>/api/skill            # Claude Code skill (SKILL.md)

# with a personal token from your profile page:
curl -H "Authorization: Bearer ppak_..." https://<host>/api/me

# dry-run validation, then submit:
curl -X POST -H "Authorization: Bearer ppak_..." -H "Content-Type: application/zip" \
     --data-binary @my-plugin.ppak https://<host>/api/packages/validate
curl -X POST -H "Authorization: Bearer ppak_..." -H "Content-Type: application/zip" \
     --data-binary @my-plugin.ppak https://<host>/api/packages

# find add-ons by need (AI ranking when the store's AI assistant is on, else word search):
curl "https://<host>/api/search?q=split+invoices+by+barcode&lang=en"
curl https://<host>/api/features         # which optional AI features are on

# correct the catalog entry without a new version (null resets a field):
curl -X PATCH -H "Authorization: Bearer ppak_..." -H "Content-Type: application/json" \
     -d '{"author": "Team Signing", "contactEmail": "team@example.com"}' \
     https://<host>/api/packages/com.example.myplugin
```

Every response uses one envelope:
`{ ok, error{code,message,hint}, findings[{code,severity,message,hint}], data }`.
Hard errors return the exact fix in `hint`; repeat until `passed` is true.

## Package format (.ppak)

A ZIP container, authoritative schema at `/api/schema/manifest`:

```
manifest.json          id · version (SemVer, must increase) · names per language ·
                       changelog (mandatory) · sha256 · ribbonAtomNamespace · uninstall info
x64/MyPlugin.zxt       required
arm64/MyPlugin.zxt     required (both architectures, always)
assets/icon.png        square icon for the catalog
LICENSES.md            third-party licenses (MIT/BSD/Apache-2.0 only)
```

**Ribbon governance:** all store plugins share ONE ribbon tab (toolbar atom
`FeaturePack`, title *Enhanced Features* / *Erweiterte Funktionen*) and add
only their own group `FeaturePack::<YourName>`. The validator checks this.
Exception: a **private** customer add-on may bring its own tab
(`ribbonAtomNamespace` = tab atom, e.g. `CustomerSign`); it then stays private
(switching it to public is refused). Every manifest names `author` and
`contactEmail` (warning when missing).

**Manual upload package (S1.0.7):** when an AI assistant cannot reach the store
(for example a cloud sandbox whose network policy blocks it) or has no token,
it builds `<id>-<version>-upload.zip` with exactly `<id>-<version>.ppak` and
`<id>-<version>-source.zip` at its root and hands it over. The user uploads it
under *Plug-ins*, *Submit a package*: the store submits the .ppak and stores the
source code at the new version in one step (`POST /api/packages` accepts it too).
One line for the assistant:

```text
Read https://addon.power-pdf.de/agent-guide and build a manual upload package for the plugin in this folder.
```

The offline packer `GET /api/tools/make-ppak.ps1` (Windows PowerShell 5.1, no
network, no token) fills `architectures`/`files`/`sha256` and writes the .ppak,
the source ZIP and the upload package:

```powershell
powershell -ExecutionPolicy Bypass -File make-ppak.ps1 -Package .\ppak -Source . -Out .\dist
```

## Local development

```bash
dotnet run --project server/src/AddonStore.Web --urls http://localhost:5190
```

SQLite and package storage land in `server/src/AddonStore.Web/data/`.
The client's default server URL is `https://addon.power-pdf.de` (the Azure address `https://ppdf-store.azurewebsites.net` keeps working); point it at `http://localhost:5190` for local development via *File → Options → Add-on Store*.

## Versions and releases

Server and client are versioned independently and are easy to tell apart on GitHub:

| Part | Prefix | Source of the number | Example |
|---|---|---|---|
| Server (web app, API) | `S` | `VERSION` | `S0.5.0` |
| Client (ribbon add-on, MSI) | `C` | `client/common/version.h` | `C0.3.4` |

- Every release commit starts with its version, e.g. `S0.5.0: …` or `S0.5.0 + C0.3.4: …`,
  so the Actions run list shows which version a build carries.
- After a successful build on `main`, CI creates the Git tag and a GitHub Release
  for every server or client version that does not have one yet (*Releases* in
  the sidebar). Server builds are also pushed as container tag `ghcr.io/mnimtz/pluginstore-powerpdf:S<version>`.
- The running server shows its version in the page footer (`Add-on Store S0.5.0`)
  and in `GET /api/ping`.

## Repository layout

```
server/src/AddonStore.Web/   ASP.NET Core 10 app (Razor Pages + minimal API)
  Api/                       endpoints, agent guide, manifest schema
  Validation/                the package validation pipeline
  Services/                  submission, tokens, audit, notifications, catalog
  Pages/                     publisher + admin web UI, Tungsten-branded
  Resources/                 SharedResource.<lang>.resx, 16 languages
client/                      Add-on Store ribbon add-on (C++/MFC .zxt)
infra/azuredeploy.json       1-click ARM template
Dockerfile                   container build, published to GHCR by Actions
docs/                        project plan, brand guide, images
VERSION                      single source of truth, shown in the footer
```

## License

MIT, see [LICENSE](LICENSE).

**Trademark note:** *Tungsten Automation*, *Power PDF* and the Tungsten logo
are property of Tungsten Automation Corporation. This project uses the brand
for internal look-and-feel consistency.
