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
| 🌍 **21 Power PDF languages** | The 16 European ones plus Simplified and Traditional Chinese, Japanese, Korean and Arabic (right to left); auto-detected from the browser (including `no`/`nn` for Norwegian, `zh-CN`/`zh-TW`), manually switchable, with localized catalog texts straight from the package manifests. |

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

**Deployment fails with "SubscriptionIsOverQuotaForSku"** (B1 VMs: limit 0):
the Azure subscription has no App Service quota in that region yet. Request a
quota of 1, pick another region or size, or use a company subscription; step by
step in [docs/azure-quota.md](docs/azure-quota.md).

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
  the server's search by need. A customer code (store window, button *Customer code*, or policy
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
  `AllowUnsigned` (DWORD 1, test servers only; client 0.7.1+),
  `PowerPdfUpdates` and `UpdateNotice` (DWORD 0/1; clients 1.6.0+ and
  1.7.0+), `Inventory` (DWORD 0: no daily report for the store's
  existing-customer evaluation; client 1.8.0+). From 32-bit
  deployment agents use `reg add ... /reg:64`.
- Intune detection rule: the file `<bin>\Plug-Ins\PluginStore.zxt` with a
  minimum version (the ProductCode changes with every version).
- **Customer code in the store window, MSI in 21 languages** (client
  0.8.0+): a "Customer code" button in the store header opens a dialog that
  checks the code at `GET /api/customer-code` before saving it (unknown codes
  count toward the brute-force limit) and shows the customer and the number
  of unlocked add-ons; customer add-ons come first in the list and get their
  own filter chip; "Remove code" clears it (both locked when the policy
  `CustomerCode` is set). The client MSI carries all 21 languages as embedded
  language transforms (`python client\installer\build_msi.py`, texts in
  `client\installer\make_l10n.py`); Windows picks the user's language, and
  the self-update passes the store window's language as `TRANSFORMS=:<LCID>`.
- **Own ribbon tab "Store"** (client 1.1.0+): the store client sits on its own
  tab "Store" (toolbar atom `AddonStore`, Alt then A); add-ons stay on the
  shared "Enhanced Features" tab. The client moves itself there in existing
  profiles (removes its old group from the shared tab) at the first start.
  Tab order (client 1.1.2+): ... *Enhanced Features*, own tabs of private
  add-ons, *Store*, and Power PDF's *Help* always last.
- **Installation folder, version, edition** (client 1.1.3+): the MSI and the
  client take the folder from `HKLM\SOFTWARE\Kofax\PDF\V1` `InstallPath`
  (the product's record of the current installation; App Paths can still
  name the folder of an older release after an update, "Power PDF 2025" ->
  "2026"), App Paths only as fallback. Version (`VersionLong`, third number =
  hotfix) and edition (`ProductName`) are read for the running Power PDF.
  Power PDF 2025 loads plug-ins in **Business** from 2025.3.7; from **2026.4**
  every edition (Standard, Advanced, Business) does. The MSI refuses other
  editions before 2026.4, the store window explains it.
- **Add-ons left in an older Power PDF folder** (client 1.1.4+): after an update
  to a new release the store window finds add-ons the store had installed in
  another "Power PDF ..." folder (next to the running one, under Program Files
  \Tungsten and \Kofax; by their `Plug-Ins\<Name>\manifest.json`) that this
  installation lacks, and offers "Install here" (one after the other, one
  restart question). "Later" hides the hint for these add-ons
  (HKCU `...\PluginStore\MigrateDismissed`).
- **Problem reports and "What's new"** (client 1.2.0+): the report form takes up
  to 3 attachments (PNG, JPEG, PDF, txt/log; 5 MB each, 10 MB together), and the
  footer of the store window reports a problem with the store itself. After an
  update (by the store, IT or the self-update) the store window shows once what
  is new in the installed add-ons and the store (HKCU `...\PluginStore\Seen`
  keeps the versions seen; installs from the store window count as seen).
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
`contactEmail` (mandatory since S1.4.1).

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

**Manual (PDF, English):** `docs/manual/AddonStore-Manual-<version>.pdf`,
generated by `python tools/make_manual.py --base https://addon.power-pdf.de`
from the live guide, `/api/rules`, the OpenAPI description and the manifest
schema: introduction and principles, the full developer and agent guide, every
rule by area, the API reference and the manifest reference.

**Audit dossier (S1.3.0):** each version on the plug-in page has an *Audit
dossier* button (`/Dossier/{id}/{version}`, print or save as PDF; JSON at
`GET /api/packages/{id}/{version}/dossier`) for owners, admins and reviewers:
package and binary hashes with the catalog signature, the compliance
declaration, the automatic checks together with the rules in force at that
time (every upload now stores a rules snapshot: server version, rules hash,
house rules, warnings made mandatory, condition wording), the source code
check, the AI review aid, the review decision with the approval conditions in
the wording confirmed, blocks, problem reports and the audit trail. Admins also
get *Audit log of this add-on* on the plug-in page. JSON exports are audited.
The AI review aid in the dossier is shown to reviewers and admins only.

**Send to storage test (S1.17.1):** "Test connection" no longer stops when a
SAS for the container alone may not create the container (HTTP 403); the
container must then exist, which the write test proves.

**Send to (S1.17.0, off by default):** server side of the "Send to" add-on:
Power PDF users exchange documents with contacts they confirmed by e-mail,
end-to-end encrypted, relayed through the store's own Blob container. Nothing
changes until an admin switches it on under *Settings, Features*; see the
section *Send to* below.

**SaaS edition on the start page, Send to invitations (S1.17.2):** the start
page title reads "Add-on Store for Tungsten Power PDF SaaS", with a line that
the store is for the Business SaaS edition only (21 languages). Send to: an
invitation accepted on the page by someone who has no device yet
(`pending_setup` in the inviter's contacts, Power PDF without sign-in) carries
its `invitationId` and can be withdrawn with `DELETE
/api/sendto/invitations/{id}`; it no longer stays on the inviter's list for good.

**Start page highlights (S1.16.0):** admins mark public add-ons as highlights
with the star button on a catalog tile (or `PATCH /api/packages/{id}
{"featured": true}`). Highlights come first, in the order they were marked, so
on the first page, with a frame in the PDF gradient of the brand book, a soft
glow, a slow light sweep (off with reduced motion) and a "Highlight" badge. At
most 9 (one page); private add-ons and the store client cannot be highlights;
the JSON catalog reports `featured`; every change is in the audit log.

**Store client comes with the server (S1.15.0):** every client release puts its
package and source ZIP into `packaging/client-bundle/` (`python
tools/bundle_client.py <version>`, committed with the `C1.x.y:` commit), and the
container image carries the folder (`ClientBundle__Path=/app/client-bundle`). A
server that knows no client yet, or only older ones, releases the bundled one
about 20 seconds after its start through the normal upload path (same checks,
client lane, source stored, audit `client.bundle.imported`, staff email), and
looks again every 10 minutes (every minute while no administrator exists yet).
A version that exists in any status, also one withdrawn on purpose, is never
taken again; a bundle that fails the checks is logged (`client.bundle.failed`)
and not tried again until the next start. An empty `ClientBundle__Path` switches
the import off (local development and tests).

**Catalog pages (S1.14.1):** the start page shows 9 tiles a page (3 x 3 on a
wide screen) with numbered pages under the grid (1 2 3, on long lists
1 ... 4 5 6 ... 12) instead of 25/50/100 rows; search and category start again
on page 1, and the page is kept in the address (`?page=3`), so going back from
an add-on returns to it.

**Add-on choice (S1.14.0):** deliveries and delivery templates no longer list
every add-on in one drop-down. "Choose add-ons" opens an in-page dialog: search
by name, ID or author, filters public / private / only mine and category, 25,
50 or 100 rows per page, several add-ons at a time with a version each (empty =
newest approved). Add-ons delivered to the customer already are shown but cannot
be chosen; in a template choosing one again changes its version. On the customer
page the stages, the period and the own code apply to every chosen add-on
(a "fixed" stage takes the version chosen per add-on, else the newest approved
one; an add-on waiting for its first approval follows "newest"); the page
reports which add-ons could not be delivered. A template takes all chosen
add-ons in one change (one update of its customers, one email). The forms still
accept the single `packageId` field of before. The page builds the list with
three queries instead of one per add-on.

**Faster store window (S1.13.2, client 1.9.3):** the public catalog is kept in
server memory (on the production share every query reads the database over the
network: about a second per catalog); a write to packages, versions,
categories, ratings or accounts, a restore or a statistics reset starts a new
generation, and an entry is at most a minute old. The client keeps the last
catalog fetched with its stored codes in memory and in
`%LOCALAPPDATA%\Tungsten\AddonStore\cache\catalog.tsv` (the server's TSV behind a
header with the SHA-256 of server, language, channel and codes; the codes
themselves are not written). The window opens with it, loads a fresh one
behind it and keeps it when the server cannot be reached; until a first
catalog is there it shows a loading indicator instead of "no add-ons". The file
is parsed with the same checks as a server answer and installing still needs
the server's signature.

**Code check of the day (S1.13.1, client 1.9.2):** four independent reviews
(security, logic, client, validator) and a hardening run.
- Customer catalog limit: decided from the customers of the valid codes, so
  it also holds for a paused customer or one without a current delivery
  (before, such a code saw the full catalog).
- Templates: a manual delivery ended by hand is never revived by someone
  else's template (only an admin or the add-on's owner may take over an ended
  or expired one); template deliveries notify the add-on's owner and staff
  like manual ones; changes and deletion notify the owners of affected
  customers; a seat limit alone no longer detaches; adding an add-on is not
  blocked by a withdrawn fixed version elsewhere; items replaced in one
  transaction; all template writes serialized; names without control or
  text-direction characters.
- Validator: language-neutral resources no longer pass the 21-language rule;
  elevation also via runas with any process start, CreateProcessWithLogon or
  Token and the COM elevation moniker; reason at most 500 characters; network
  through COM (WinHttpRequest, MSXML2) and processes through WMI count;
  `IMPORT_BY_ORDINAL` (warning) for numbered imports from system DLLs; name
  entries at most 80 characters on upload; a caption must be an object.
- A private add-on switched to public is reviewed again; AI review and
  translation buttons only for reviewers and admins; a ReadMe link must pass
  the host check; release notes go to the AI as data.
- Existing customers: retention also applies while switched off; 2000
  reports per address and hour, then 429 (the client tries again later); at
  most 20 new domains per address and day; an admin correction made while the
  AI answers is kept; unknown domains are matched again against portal
  customers; the audit log no longer lists domains.
- API tokens: the last-used stamp is written at most once a minute and a
  locked database no longer fails the request (20 parallel calls answered 500
  before); audit entries retry briefly when SQLite is locked; the S1.6.0
  status migration runs once.
- Client 1.9.2: the ReadMe host check ends at `?` and `#`; a waiting notice is
  dropped when a newer check result arrives; updates that need a newer Power
  PDF are not counted or offered in the updates view; old Help groups removed
  safely (also self-closing); no dot from a kept count when the background
  check is off; the classic dialog opens normally from "Updates"; the access
  answer tolerates whitespace; thread-safe host version and install id; more
  free-mail providers and no daily report where the license mode may not use
  the store; updates view keeps its scroll position and waits for the catalog.

**Delivery templates (S1.13.0):**
- New page "Delivery templates" (next to Deliveries): a template is a named
  set of add-ons, public and private mixed, each with "newest approved
  version" or a fixed approved version, optionally "Limit the catalog of its
  customers to their deliveries".
- Templates are linked: on the customer page one or more are assigned, their
  add-ons are delivered at once, and every later change of a template
  reaches all customers that have it. Manual deliveries stay possible next to
  them; for the same add-on the manual one wins. Changing a template delivery
  by hand (stages, pause, make live) or "Detach from the template" makes it
  manual. Add-ons that leave every template end; deleting a template ends its
  deliveries.
- Tables DeliveryTemplates, DeliveryTemplateItems, CustomerTemplates and
  Deliveries.TemplateId (all in the database, so in every backup). API:
  `/api/templates` (GET, POST, PATCH, DELETE) and
  `/api/customers/{cid}/templates` (POST, DELETE).

**Deliveries of any add-on, catalog limited to them (S1.12.0):**
- The customer page offers every add-on for a delivery, public and private
  (marked), not only private ones. A delivery of a public add-on fixes its
  version for the customer or, with the option below, is what it may use.
- New customer option "Limit the catalog to its deliveries" (off by
  default; on the new-customer form, under customer data and as
  `restrictCatalog` in `POST`/`PATCH /api/customers`). With one of its codes
  the store client lists only the delivered add-ons and the store client
  itself; other downloads answer 403 `NOT_DELIVERED`. With several codes the
  limit applies as soon as one belongs to such a customer; everything the
  codes deliver together is allowed. `GET /api/customer-code` reports
  `restricted`. Together with the policy `Store\CustomerCode` users cannot
  leave the limit; it is a usage rule, not copy protection. No client change.

**One "Updates" button in the Help tab (client 1.9.1):** the Help tab group
is `AddonStore::HelpTab` with a single button `AddonStore::HelpTab::Updates`,
always there; it opens the "Available updates" view, and while updates are
pending its icon gets the amber dot and its tooltip the number. The groups of
1.7.0 and 1.9.0 (`AddonStore::Updates`, `AddonStore::Help` with its extra
"Add-on Store" button) are removed from the user layout.

**Available updates (client 1.9.0):**
- The Help tab group is now `AddonStore::Help` (new atoms; the C1.7.0 group
  `AddonStore::Updates` is removed from the user layout) and always there
  with an "Add-on Store" button, so "Updates available" appears in it as soon
  as updates are found. Before, Power PDF left the group out when it had no
  visible button at start, and the button came only with the next start.
- "Updates available" and the one-time notice open the store window in its
  "Available updates" view: the Power PDF update (title, installed and new
  version, build date, the end-user text, "What's new" and "Hide"), the store
  client and every add-on update with its changelog, "Update all" for several
  add-ons, "All add-ons" switches to the full store.

**Stricter checks (S1.11.0):**
- All 21 Power PDF languages are required now. Name, description, changelog
  and screenshot captions need `zh-Hans`, `zh-Hant`, `ja`, `ko` and `ar` as
  well, the layout needs the UILayout folders `CHS`, `CHT`, `JPN`, `KOR` and
  `ARA`, and the .zxt needs string tables in these five languages.
  `NAME_NOT_LOCALIZED`, `LANG_TEXT_EXTENDED`, `LANGS_EXTENDED_MISSING` and
  `UI_LANGS_EXTENDED` used to be warnings; they are errors now. The offline
  packer `make-ppak.ps1` checks all 21 languages too.
- Every version of a private add-on needs an approval before a customer
  delivery hands it out. It waits in the review queue like a public one
  (status `submitted`), and a reviewer approves it in one step. Until then
  only the developer's personal test code shows it.
- New security checks on the .zxt. `ELEVATION_UNDECLARED` (error): the
  binary can start programs with administrator rights (ShellExecute with
  `runas`), but the manifest has no `"elevation": {"reason": "..."}` (20 to
  500 characters). `ELEVATION_DECLARED` (warning): the reason is given, and
  the reviewer checks what runs elevated and why. `COMMAND_SHELL` (warning):
  the binary can start cmd.exe, PowerShell or another script host.
  Short words such as `runas` or `cmd.exe` are now searched directly in the
  binary (ASCII and UTF-16, any position); the string scan before only saw
  texts of 8 characters or more.
- Versions already in the store are not checked again. Uploads of the store
  client need all 21 languages and the elevation reason too (its packaging
  spec declares the elevated install step; `tools/make_ppak.py` passes
  `elevation` through).
- A customer delivery of a private add-on can be set up while its first
  version still waits for review; it follows "newest" and hands out the
  version once it is approved. Switching an approved private add-on to public
  offers its approved versions.
- AI review aid: written in English by default (setting `Ai.ReviewLanguage`,
  Settings, AI assistant). Next to it a language list and "Show in this
  language" translate the stored aid once and keep the translation
  (`PackageVersion.AiReviewTranslationsJson`); "Show the original" goes back.
  Creating it again drops the translations.
- Power PDF updates: "Check now" also reads suggested release lines and shows
  what it found as a preview in "Detected as current"; clients get no hint
  and no mail goes out until the line is set to "Maintained". Saving a
  suggested line as maintained checks it right away (detection mail, client
  hint). The release line fields use the full width.

**Existing customers (S1.10.0, client 1.8.0):**
- The navigation entry "Customers" is now "Deliveries" (customer codes and
  private add-ons, unchanged).
- New admin area "Existing customers": which companies use the store, with
  installations (active in the last 30 days), Power PDF and store versions,
  installed add-ons and how many installations run an older add-on version.
  Search over company, domain and add-on, pages of 25/50/100, a detail view
  per company with the installation list (hash only).
- Off by default. Settings, data protection, card "Existing customers
  (evaluation)": switched on only after the GDPR confirmation (legal basis,
  users informed, retention period 7 to 730 days, default 180), recorded with
  name and time in the audit log. While it is off, the area and its
  navigation entry do not exist and reports are ignored. Switching off can
  also delete all data.
- Client 1.8.0 reports once a day `POST /api/client/inventory` (store client
  User-Agent only, otherwise 403 `CLIENT_ONLY`): install id (stored as a
  hash), the **domain** of the Cloud License Server sign-in (HKCU
  `Identity\email`; never the address), license mode, Power PDF and client
  version, installed add-ons with versions. Free-mail domains are refused.
  Only the last state per installation is kept; not seen within the retention
  period: deleted. Policy `Store\Inventory` 0 switches the report off.
- The company behind a domain is found every 15 minutes: first the contact
  e-mails of the portal's customers, then the AI assistant (the domain is
  passed as data; unknown gives an empty answer, at most 3 tries). Admins
  correct the name, look it up again or delete a domain's data; a corrected
  name is never overwritten.

**Store notification instead of own Power PDF updates (S1.9.0, client 1.7.0):**
- Power PDF Business brings its own Update Manager from 2026.4; the store is
  not part of it. So the store installs no Power PDF updates (stages 2 and 3
  of the concept are dropped), and release lines have "Power PDF updates
  itself" (preset from 2026.4): their clients get no store hint.
- Client 1.7.0: a button "Updates available" in Power PDF's Help tab
  (group `AddonStore::Updates`, added to the user layout at start), visible
  only while updates for the store, installed add-ons or Power PDF (lines
  without an Update Manager) are pending; the last count is kept in HKCU
  `PendingUpdates`, so the button shows right at the next start.
- One notice per new update ("Open the Add-on Store now?"), on by default
  under Options and switchable off (policy `Store\UpdateNotice` 0/1); the
  updates already shown are kept in HKCU `NotifiedUpdates`.

**Power PDF update hints, stage 1 (S1.8.0, client 1.6.0; concept
docs/concepts/powerpdf-updates.md):**
- Settings, "Power PDF updates": off by default. Once switched on, the server
  reads the documentation overview and the watched page of every release line
  once a day (also "Check now"). A new major version on the overview becomes a
  proposed release line (status "Suggested") that an admin confirms; lines run
  in parallel (e.g. 2025.3 and 2026.4).
- Each line has its watched page (DocShield portal page), a pattern (group 1 =
  update version, default `ReadMe-TungstenPowerPDFBusiness-(2025\.3\.\d+)\.htm`),
  a status (maintained, security updates only, ended), an optional end of
  support and an optional hint about a newer major version.
- The portal page builds its ReadMe link in JavaScript, so the pattern is
  searched in the whole page and the ReadMe address follows the DocShield
  scheme `/PowerPDF/<lang>/<folder>/print/<file>`; title and build date come
  from the ReadMe. A page that no longer matches is reported, not ignored.
- New update or new major version: email to the admins (event
  "PowerPdfUpdate") and a tile on their start page. Optional AI text for end
  users in 21 languages, accepted or discarded by an admin; the ReadMe is
  passed to the AI as data.
- Fetching: HTTPS on the allowed hosts only (default
  `docshield.tungstenautomation.com`), no redirects to other hosts, 2 MB and
  20 s limits; http://localhost only on a development server (tests).
- S1.8.1: the section shows "Detected as current" (newest update, build
  date, detection and last check per line, ReadMe link) and "How a client
  sees it": enter a version and see whether a client gets the hint, and why
  not.
- `GET /api/powerpdf/update?version=<VersionLong>&lang=` answers the store
  client; nothing for a license mode the store does not allow.
- Client 1.6.0: Options, "Show a hint when an update for Power PDF is
  available" (on by default since client 1.6.1, as the server answers
  nothing until its admins switch the hints on; policy
  `Store\PowerPdfUpdates` 0/1 locks it).
  The store window shows a banner with the update, the text, "What's new"
  (opens the official ReadMe, only https on tungstenautomation.com) and "Hide"
  (until a newer update). Nothing is downloaded or installed.

**Allowed license modes (S1.7.0, client 1.5.0):**
- Settings has a new category "Add-on Store": the admins choose which Power
  PDF installations may use the store, by license mode: Cloud License Server
  (SaaS), License Server on premises, serial number, unknown (also store
  clients before 1.5.0). All are allowed by default.
- Client 1.5.0 reads the mode from `HKLM\SOFTWARE\Kofax\PDF\V1` (subkey
  `CLS` with `LicenseURL` = cloud, `SerialNumber` filled = serial, otherwise
  unknown; on-premise License Server is not told apart yet) and sends it as
  `X-License-Mode` with every request.
- At start and every 4 hours it asks `GET /api/client/access`. For a mode
  that is off it hides its ribbon button (the decision is kept in HKCU
  `StoreAccess`, so the button stays hidden offline); installed add-ons keep
  working.
- For a mode that is off, the catalog lists only the store client itself
  (so every client can still update) and add-on downloads answer 403
  `LICENSE_MODE_NOT_ALLOWED`. The website and API callers are not affected.
  The mode is reported by the client: a usage rule, not copy protection.

**Approval for beta or live, personal test code (S1.6.0):**
- A new upload waits for review (status `submitted`). No store client gets
  it yet, also not in the beta channel.
- Reviewers approve it **for beta** (clients with the beta option) or **for
  the live store**, or reject it. A version approved for beta can go live
  later; the review queue lists those under "In beta, ready for the live
  store".
- Setting a live version back to beta keeps it approved for beta. A withdrawn
  version is restored to the stage it was approved for.
- Every developer can create a **personal test code** (profile page or
  `GET`/`POST`/`DELETE /api/me/test-code`). Entered in the store window like a
  customer code, it shows the newest version of each of the developer's
  add-ons, also one that waits for review ("For Test: <name>", catalog
  channel `test`). It shares the guessing limit of customer codes.
- Private add-ons still need no approval: customer deliveries hand out a
  version that waits for review. (Changed in S1.11.0: private versions need
  an approval too.)
- Existing data: beta versions nobody reviewed become `submitted`; reviewed
  ones stay approved for beta.

**S1.5.1:** the customer page tells where the code goes now: the store
window's *Customer code* button (up to 10 codes per computer) or the policy
`CustomerCode`.

**Client 1.4.3:** the store window lists the add-ons installed on this
computer first, under *Installed (n)* with the number of updates; updates come
first, then the rest by name, add-ons no longer offered last. *More add-ons*
follows below. A search by need keeps its own ranking. Client 1.4.4 orders the
installed ones by origin: from a customer code first, then installed by hand,
then no longer offered (updates first in each group). Client 1.4.5 fixes the
code dialog after adding or removing a code (it read the host's code object as
the list and stopped half-drawn); a faulty page message now closes the dialog
instead of freezing the window. Client 1.4.6 manages customer codes in the
store window only; the Options page just shows how many codes are stored
(policy-set codes count too). Without the WebView2 runtime or with ClassicUI
the classic dialog opens, which has no code management, so the Options page
keeps its input field there.

**New navigation, preview (S1.5.0):**
- After signing in, everyone can switch to a new navigation: the footer link
  *Try the new navigation (preview)* turns it on, and *Classic navigation* in
  the account menu turns it off again. The choice is stored per browser
  (cookie `pp_shell`); the classic top bar stays the default.
- The new navigation has a sidebar with groups (My work, Review,
  Administration, Help). Reports and Settings show their sections as
  sub-items in the sidebar. On small screens the sidebar opens from the menu
  button.
- The account menu (avatar) holds the profile, the language, the switch back
  and *Sign out*.
- The new start page `/Start` shows tiles: downloads of the last 30 days,
  add-ons, new problem reports, the review queue, customers, the rating and
  access requests, each role with its own tiles. Quick actions and the newest
  problem reports follow. With the new navigation, signing in opens this page.
- The catalog, the add-on pages and the disclaimer keep their layout in both
  modes.
- Long values in table cards wrap on phones (one page overflowed at 390 px).

**Code check of the day (S1.4.3, client 1.4.2):** three independent reviews of
all changes since S1.3.0, with these fixes:
- Installation seats are claimed one at a time (6 parallel downloads for 2
  seats: exactly 2), with a unique index and retries on a busy database.
- Links in mails to admins only follow trusted hosts.
- Display names are compared culture-independently, and the reply limit is
  counted per account.
- The plug-in page pages through all reports again.
- The packers require author and contact.
- `pluginstore_bin.h` gets `PLUGINSTORE_BIN_DLLS` and
  `PluginStoreBinAvailable()`: a missing DLL never falls back to the Windows
  search.
- Client 1.4.2 removes several add-ons one by one (a locked one does not stop
  the others) and fetches the whole catalog for the code preview.
- The client keeps the last security block list across restarts, and its
  staging folder gives its owner no implicit rights.
- Installed add-ons that are no longer offered are named from the manifest's
  name and never offer an update.

**Customer codes and installations (S1.4.2, client 1.4.1):**
- The store window keeps up to 10 customer codes. Each code shows its customer
  and the add-ons it unlocks.
- Removing a code first names the add-ons that came with that code only, then
  removes them together with the code (one administrator confirmation).
- Add-ons the catalog no longer offers stay listed as "No longer offered", so
  they can still be removed.
- A delivery of a private add-on may allow a number of installations
  (`maxInstalls`, default unlimited). The client sends its random installation
  id with the codes, and the server stores only a hash per delivery.
- A download takes a seat and an update keeps it. Removing the add-on frees
  the seat (`POST /api/deliveries/release`).
- When every seat is in use, a new installation is refused with a clear
  message (`SEATS_EXHAUSTED`).
- The limit is a fair-use count, not copy protection (S1.4.3). Claims are
  serialized, and a unique index keeps one seat per installation. New seats
  are limited to 200 per address and hour.
- Releases also work for paused or ended deliveries. Freed entries are
  deleted after 180 days, and the portal can free all installations not seen
  for 90 days at once.
- `maxInstalls` is checked before anything is saved and applies to private
  add-ons only.
- The customer page shows "Installations: 3 / 10" with every installation and
  a "Free" button. The store window shows the same count per add-on.
- `GET /api/customer-code` also returns the add-on ids and installations.
- Changelogs are read by end users as "What's new"; the guide asks for plain
  wording.

**S1.4.1:**
- `author` and `contactEmail` are mandatory (`AUTHOR_MISSING`,
  `CONTACT_MISSING`); `make_ppak.py` stops early without them.
- The plug-in page of a private add-on shows its author, contact and
  category.
- The portal is 1280 px wide, every table scrolls in its own frame, and long
  ids, mail addresses and codes wrap. Checked at 390 to 1440 px: no page
  overflows any more.

**Four-part versions (S1.4.1):** a version has three numbers and an optional
fourth, e.g. `1.2.0` or Power PDF's Year.Quarter.Update(.Fix) `2026.4.0.3`.
Every part is compared as a number, and a missing fourth part counts as 0.
With three parts the binary's fourth FILEVERSION field stays free for a build
number; a four-part version must match the whole FILEVERSION. The current
store clients already compare four parts, so no client update is needed.

**Own DLLs, localized names, English dossier (S1.4.0, client 1.4.0):**
- Add-ons may bring their own x64 DLLs in `bin/` (listed in `files.bin`).
  The client installs them to `Plug-Ins\<Name>\bin\`; nothing is installed
  outside the Plug-Ins folder.
- The .zxt must delay-load them through the MIT loader header
  `GET /api/sdk/pluginstore_bin.h`.
- The server checks every DLL like the .zxt. It refuses programs, direct
  imports, system or runtime names and names another add-on already ships.
- Store clients before 1.4.0 are offered the newest version without `bin/`.
  DLLs that Power PDF has loaded are moved aside on update or removal and swept
  later, no reboot.
- `tools/make_ppak.py` takes `"bin": [...]` in the spec.
- The catalog name should exist in every language (`NAME_NOT_LOCALIZED`, a
  warning that admins can make mandatory under Settings, Rules; an error
  since S1.11.0).
- The audit dossier opens in English and offers all 21 languages.
- Private add-ons show approval as an optional review, since customer
  deliveries hand out the newest checked version anyway. (Changed in
  S1.11.0: the approval is required.)

**Security hardening (S1.3.1, client 1.3.1):** after a full code audit and
hardening test:
- Password reset links are only built on the configured public address (or
  localhost and the App Service host name). A forged `Host` header gets none.
- Behind Azure's forwarded headers the client IP can no longer be spoofed.
- Reviewers never approve their own add-ons. Admins can do so only while the
  four-eyes rule is off.
- Admins never see another admin's reset link.
- Changing or resetting a password revokes all API tokens of the account.
- Display names are unique.
- Uploads: at most 60 per account and hour. ZIP entry counts are checked
  before unpacking.
- Request bodies outside the uploads are limited to 2 MB.
- The import-table scan and the text checks are protected against crafted
  binaries and backtracking patterns.
- AI prompts keep all third-party text (names, file names, hosts) inside
  marked data blocks with control characters removed.
- Developer replies to reporters are labelled as such and limited to 50 a day.
- Catalog texts refuse text-direction overrides.
- The retention purge runs set-based in the database.
- Dates use the Gregorian calendar in every language.

Client 1.3.1 refuses downgrades, protects its staging folder, keeps the last
good blocklist when the server is unreachable and locks down the store
window's WebView2.

**21 Power PDF languages (S1.2.0, client 1.3.0):** Power PDF has 21 UI
languages. Portal, store window, MSI and catalog follow all of them: the 16
European ones plus Simplified Chinese (`zh-Hans`, UILayout `CHS`), Traditional
Chinese (`zh-Hant`, `CHT`), Japanese (`ja`, `JPN`), Korean (`ko`, `KOR`) and
Arabic (`ar`, `ARA`, right to left: the portal and the store window mirror
their layout, message boxes read right to left). For add-ons the 16 stay
mandatory; the five further ones are recommended and reported as warnings
(`LANG_TEXT_EXTENDED`, `LANGS_EXTENDED_MISSING`, `UI_LANGS_EXTENDED`), which
admins can make mandatory under *Settings, Rules*. Since S1.11.0 all 21 are
required and these codes are errors. The translations of the
five languages were machine-made; a review by native speakers is recommended
before customers in these markets use the store.

**Add-ons without ribbon buttons (S1.1.1):** an add-on that only works inside
a Power PDF feature (for example an engine for an assistant) declares
`"ui": "none"` in manifest.json. It ships no `UILayout/` and no
`ribbonAtomNamespace` (a layout is refused with `UI_NONE_HAS_LAYOUT`; since
S1.2.1 a package without UILayout and without this declaration is refused
with `UILAYOUT_MISSING`, so the declaration is the creator's confirmation), the
ribbon rules do not apply, every other check does (binaries, licenses, source,
compliance, network/injection/download checks, languages of visible texts).
The description must say where the function appears and how to switch it off
(`UI_NONE_DESCRIPTION`, approval condition R5). The catalog carries
`"ui": "none"` (TSV column 22); client 1.2.1+ shows "no ribbon buttons" in the
store window. The offline packer no longer requires UILayout for such add-ons.

**Problem report queue and dashboard (S1.1.0):** reports from the store window
(with up to 3 attachments: PNG, JPEG, PDF or text, 5 MB each, 10 MB together,
checked by content) land in the queue *Problem reports* (`/Issues`): developers
see their own add-ons, admins all. Sections by status on the left (open,
in progress, waiting, done, declined), filters, paging; each report has its
log, attachments, AI assessment, assignment, internal notes and replies that
are mailed to the reporter. The same queue is an API for AI assistants with a
token: `GET /api/feedback`, `GET /api/feedback/{id}` (+ `/attachments/{aid}`),
`PATCH /api/feedback/{id}`, `POST /api/feedback/{id}/notes`. Attachments live
in the database (so in every backup); attachments, log and reply address of
reports closed longer than the retention period (Settings, IP address logging;
default 180 days) are deleted. *Dashboard* (`/Insights`, `GET /api/insights`)
shows each developer downloads per day and version, ratings, active reports and
the Power PDF and Windows versions, architecture, language, country and channel
of the users; admins see all add-ons and can pick one developer.

**Documentation page (S1.0.12):** *Documentation* in the top menu (`/Docs`, also `/Admin/Docs`;
all signed-in users, so developers too; also under Settings and linked from the API and Rules pages)
lists every document in one place: the two PDFs, the rules and checklist, the
guides AI assistants read (agent guide, manual upload, llms.txt, AGENTS.md,
SKILL.md) and the API tools. Every link opens in a new tab; each card shows the
full address with a copy button.

**Security checks and blocks (S1.0.11):** the validator reads the import
tables of every binary. Network functions without a declared external service
(`NETWORK_UNDECLARED`), writing into other processes (`PROCESS_INJECTION`) and
downloading code at run time (`RUNTIME_DOWNLOAD`) are errors; starting
processes (`PROCESS_START`), services or Run keys (`PERSISTENCE`) and plain
http:// addresses (`INSECURE_HTTP`) are warnings the reviewer sees. A source
that switches off HTTPS certificate validation is refused
(`TLS_CHECK_DISABLED`). Admins block a version or a whole add-on on its plug-in
page, with a reason users see: a block withdraws it, a blocked add-on takes no
uploads (`PACKAGE_BLOCKED`), and `GET /api/blocked` publishes the list (SHA-256
of the lowercase id, version or `*`, reason). Client 1.1.5+ reads that list 20
seconds after start and every 4 hours (also with update notices off), asks
once per session to remove a blocked add-on, and shows a red notice with
"Remove now" in the store window. Lifting a block leaves the versions
withdrawn until they are restored.

**Mandatory conditions (S1.0.10):** the Rules page opens on the numbered
conditions a plug-in must meet, first for the **import** (A1 to E4, checked
automatically on every upload) and then for the **approval** (R1 to R4 and the
store's own, confirmed by the reviewer). Admins change the wording of any
condition (reset restores the standard) and add approval conditions. The
approval form lists every approval condition as a required box; the server
refuses an approval without all of them and records the confirmed ids in the
audit log. The agent guide shows the store's wording (checklist sections F and
G) and `GET /api/rules` lists the conditions under `data.conditions`.
The two PDFs, *Mandatory requirements for add-ons* and the *Manual*, are on the
API page (`/docs/AddonStore-Requirements.pdf`, `/docs/AddonStore-Manual.pdf`);
`tools/make_manual.py` regenerates both and copies them to `wwwroot/docs`.

**Rules page and API (S1.0.9):** every rule the store checks, grouped into
13 areas, is listed under *Settings, Rules* (`/Admin/Rules`, readable by all
signed-in users) and at `GET /api/rules`. Admins extend them there: **house
rules** (guidelines in plain words that reviewers check at approval, or
recommendations) and **mandatory warnings** (the validator then refuses the
upload). Both appear in the agent guide's pre-flight checklist (section F) and
in the manual; they are stored in AppSettings and therefore in every backup.

Web readers that summarize long pages lose detail, so the hard rules and the
manual upload also have short pages of their own (S1.0.8):
`/api/agent-guide/checklist` (HTML `/agent-guide/checklist`) and
`/api/agent-guide/manual-upload`.

The offline packer `GET /api/tools/make-ppak.ps1` (Windows PowerShell 5.1, no
network, no token) fills `architectures`/`files`/`sha256` and writes the .ppak,
the source ZIP and the upload package:

```powershell
powershell -ExecutionPolicy Bypass -File make-ppak.ps1 -Package .\ppak -Source . -Out .\dist
```

## Send to (S1.17.0)

"Send to" lets Power PDF users exchange documents with confirmed contacts
(invitation by e-mail, mutual confirmation). Documents are encrypted on the
sending PC; the server keeps only ciphertext until the recipient collects it.
Concept: `docs/concepts/senden-an.md`. Add-on and test tools: separate
repository `C:\Claude\SendTo` (spike).

- Switch: *Settings → Features → Send to* (off by default). While it is off,
  the menu, the agent guide section, the OpenAPI paths and the add-on
  disappear, and waiting documents are deleted.
- Admin menu *Send to*: overview, reports, blocks, settings (retention 1 hour
  to 30 days, limits, undo window) and the document storage (own Blob
  container; connection string or SAS URL, stored encrypted; "Test
  connection" checks write/read/delete, public access and soft delete).
- Device API `/api/sendto/*` (token `Bearer stdev_...`), invitation page
  `/sendto/invite/{token}` (the button accepts, opening the link does not).
- Data: tables `SendTo*` in the database. The backup keeps users, devices,
  contacts, blocks, reports, lists and quick targets; transfers and their
  envelopes are removed from the backup copy (VACUUM included), and the
  document blocks live only in the document storage. After a restore the
  maintenance job deletes blocks no open transfer knows (older than 1 hour).
- Network: no new port. Add-ons talk HTTPS 443 to the store address (system
  proxy honoured), outbound only; clients never reach the Blob storage, the
  server relays 1 MiB blocks. If the storage account has a network firewall,
  allow the web app (VNet integration or its outbound IPs). A later switch to
  server-sent events needs a heartbeat below the 230 s idle limit of Azure
  App Service.
- Development: `SendTo__Storage=local` uses `data/sendto-dev` instead of Blob;
  `/sendto/dev/mails` and `/sendto/dev/config` exist only in Development.

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
  Resources/                 SharedResource.<lang>.resx, 21 languages (English = the keys)
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
