# Security review, Oct 3, 2026 (v0.2.0)

Adversarial code review of the server (auth, uploads, path handling, XSS,
email injection, redirects, DoS). Findings and their status:

| # | Severity | Finding | Status |
|---|---|---|---|
| H1 | High | ZIP decompression bomb: entries inflated into memory without a cap | **Fixed**: hard caps per entry type (manifest 256 KB, text 1 MB, icon 4 MB, .zxt 120 MB) and 400 MB total inflated per package |
| H2 | High | Open redirect via /set-lang returnUrl (`//host`); 500 on invalid culture | **Fixed**: culture validated against the supported list, returnUrl must be a single-slash relative path |
| M1 | Medium | Devkit path prefix check without separator (sibling-dir leak) | **Fixed**: separator-aware IsUnder() |
| M2 | Medium | HTML injection into admin mail via display name / token name | **Fixed**: HtmlEncode on both |
| M3 | Medium | Beta channel = anonymous distribution of unreviewed native code | **Accepted for the internal phase** (documented); the customer phase gates beta behind an access code (see project plan 3.2a). Decision Oct 3, 2026: no server-side malware scan; protection comes from reviewer approval, the audit log, SHA-256 verification in the client and Windows Defender on download/install. Microsoft Defender for Storage is the planned addition before any customer-facing launch. |
| M4 | Medium | Cookie-authenticated state-changing API endpoints without antiforgery | **Fixed**: POST /api/packages and DELETE are bearer-token only; the web UI uses its own antiforgery-protected page handlers |
| L1 | Low | Login user enumeration via distinct status messages | **Fixed**: password verified first |
| L2 | Low | Invite tokens without expiry | **Fixed**: 7-day validity |
| L3 | Low | Unbounded catalog JSON parse | **Mitigated** via the 256 KB manifest cap (H1); pagination/caching deferred |
| L4 | Low | DB write per anonymous download (counter) | Accepted for now (team-size traffic) |
| L5 | Low | Host header reflected into generated URLs | Mitigated: App.PublicBaseUrl setting is used for emails/invites; pin AllowedHosts when a custom domain lands |
| L6 | Low | Missing security response headers | **Fixed**: nosniff, X-Frame-Options DENY, Referrer-Policy, CSP (frame-ancestors 'none') |
| L7 | Low | First-run setup check-then-act race | Accepted (window of seconds on first boot of a fresh instance) |
| L8 | Low | Auth failure codes never surfaced; null-deref in token revoke | **Fixed** |

Confirmed sound by the review: token scheme (192-bit random, hash-only storage),
zip-slip rejection, Razor encoding (no Html.Raw), authorization matrix including
the Reviewer role, avatar handling, antiforgery on all forms, Resend JSON
construction, no secrets in the repo.

Dependency audit: `dotnet list package --vulnerable --include-transitive` is
clean after pinning SQLitePCLRaw.bundle_e_sqlite3 3.0.5 (2.x carried
GHSA-2m69-gcr7-jv3q, High). License inventory: docs/LICENSES-THIRD-PARTY.md.

# Hardening audit, Oct 4, 2026 (S0.14.1)

Full audit of server and client after the customer deliveries (S0.14.0):
security, correctness, concurrency, resource use. Three independent review
passes, every finding checked against the code, regression suite of 12 test
scripts (customers, authorization matrix with 44 checks, AI, provider
neutrality, overview, pages, usage, sharing, source, categories, feedback,
backup/restore) green after the fixes.

## Fixed in S0.14.1

| # | Severity | Finding | Fix |
|---|---|---|---|
| A1 | High | Inline scripts forced `'unsafe-inline'` in the CSP | Per-request nonce on all scripts, no inline handlers; CSP `script-src 'self' 'nonce-…'; object-src 'none'; base-uri 'self'; form-action 'self'` |
| A2 | High | API writes authenticated by the sign-in cookie had no CSRF guard | Such calls need `X-Requested-With` (error CSRF_CHECK); bearer tokens unaffected |
| A3 | High | Password guessing: no lockout, no per-address throttle | Identity lockout on failure, 20 attempts per address per 15 minutes, one neutral error message |
| A4 | High | Disabled users and role changes stayed signed in until the cookie expired | Security stamp renewed on status/role change, validated every minute |
| A5 | High | Client IP taken from spoofable `X-Client-IP` / leftmost `X-Forwarded-For` (rate limits, geo, usage) | Only the rightmost `X-Forwarded-For` hop (the one App Service appends) |
| A6 | Medium | Ratings and feedback accepted for private add-ons without access | Same access check as downloads |
| A7 | Medium | Private add-on details (owner email) visible to customers with a code | Details only for owner, admin, reviewer; owner shown by public name |
| A8 | Medium | Two packages could ship the same `.zxt` file name and overwrite each other on install | ZXT_NAME_INVALID (strict pattern) and ZXT_NAME_TAKEN |
| A9 | Medium | Malformed ZIPs and huge version numbers raised 500 instead of a finding | ZIP_UNREADABLE, strict SemVer, MIN_HOST_VERSION_INVALID, INFLATE_LIMIT |
| A10 | Medium | MSI download buffered up to 100 MB in memory per request | Extracted once to a disk cache keyed by the package hash, streamed |
| A11 | Medium | Lost download counts under concurrency | Atomic `UPDATE … SET Downloads = Downloads + 1` |
| A12 | Medium | Anonymous AI search could use up the whole daily AI budget | Search capped at a third of the daily limit |
| A13 | Medium | Resend API key stored in plain text in the settings table | Encrypted with Data Protection (`dp:` prefix), old values still read |
| A14 | Medium | Restore left old connections, caches and key ring in memory | App restarts after a restore (production); ten newest safety backups kept |
| A15 | Medium | Registration revealed existing email addresses; no throttle | Same answer for existing addresses, 5 registrations per address per hour |
| A16 | Low | Cookies without explicit Secure/SameSite; `Server` header; no HSTS / Permissions-Policy | Secure + HttpOnly + SameSite=Lax, header removed, HSTS and Permissions-Policy set |
| A17 | Low | Reviewers could create customers; delivery edit without owner check | Owner or admin only |
| A18 | Low | Withdraw over the API also hit draft/review versions | Only beta and live (VERSION_NOT_WITHDRAWABLE) |
| A19 | Low | Delivery "until" date ended at midnight before the chosen day | Inclusive end date |
| A20 | Low | Pinned version silently replaced in the stage form once unavailable | Kept and marked "no longer available" |
| A21 | Low | Duplicate language keys in PATCH raised 500 | Last value wins |
| A22 | Low | `set-lang` returnUrl accepted backslash/control characters | Strict relative path check |
| A23 | Low | One failing background job could stop the host; AI worker retried a broken item forever | Background errors logged, not fatal; worker gives up after three attempts |
| A24 | Low | Private add-ons leaked through category counts and slug lookups | Excluded |
| A25 | Low | Temp files of rejected uploads left behind | Cleaned up |
| A26 | Low | HTML of customer names unencoded in notification mails | HtmlEncode |
| A27 | Low | Duplicate resource keys (one showed "an" instead of "until" in reports) | Removed, label fixed |
| A28 | Low | Container build context could include local data and credentials | `.dockerignore` |
| A29 | Info | EF Core / Identity 8.0.11 | Updated to the latest 8.0 patch (8.0.31); `dotnet list package --vulnerable` clean |

## Open, with recommendation

| # | Severity | Item | Recommendation |
|---|---|---|---|
| O1 | High | .NET 8 support ends Nov 10, 2026 | **Fixed in S0.15.0**: .NET 10 LTS, EF Core/Identity 10.0.12, container images 10.0 |
| O2 | Medium | Backup archives contain the Data Protection key ring unencrypted | **Fixed in S0.15.0**: downloads carry it only encrypted with a password (AES-256-GCM, PBKDF2-SHA256 600k) or not at all; the password is checked before a restore changes anything |
| O3 | Medium | Admins can approve their own uploads | **Fixed in S0.15.0**: setting four-eyes rule (uploader and owner cannot approve), off by default while only one admin exists |
| O4 | Medium | First-run setup had no token at all (first visitor became admin) | **Fixed in S0.15.0**: one-time token in the server log and data/setup-token.txt, 24 hours, deleted after use; setup serialized |
| O5 | Low | Some list pages run one query per row; concurrent writes on the same row can still answer 500 | Batch queries and catch concurrency exceptions when traffic grows |
| O6 | Low | Geo database reload not synchronized with readers | Swap the reader atomically |
| O7 | Low | Dates are stored in UTC and shown without time zone | Show the user's time zone in reports |
| O8 | Low | Category usage counts include versions the catalog no longer shows | Count only deliverable versions |
| O9 | Privacy | IP addresses in rate limits/usage, AI data flow, log excerpts with user names in problem reports | Review with Legal / data protection before the customer phase |

## Client C0.6.0 (same audit)

Live-tested in Power PDF on an ARM64 machine: update badge on and off, update
through the hardened elevated step, install and removal of a private add-on
delivered by customer code, search by need, policy as REG_SZ, downgrade fix.
The generated PowerShell helper scripts pass the PowerShell parser.

| # | Severity | Finding | Fix |
|---|---|---|---|
| C1 | High | The elevated install script was a file in the user's TEMP folder that another user-level process could change before PowerShell read it | Script passed in memory (`-EncodedCommand`); no script files |
| C2 | High | The package hash was checked in user context only; the file in TEMP could be swapped before the elevated step unpacked it | Elevated step copies it into an admin-only staging folder under `Plug-Ins` and checks the hash again there |
| C3 | High | WinHTTP followed redirects, which bypassed the store-host pinning | Redirect policy NEVER; TLS 1.2 or newer |
| C4 | Medium | `powershell.exe` / `msiexec.exe` resolved through the search path | Full paths from the system folder |
| C5 | Medium | Catalog values (binary name, id, version) went unquoted into the elevated script | Strict field validation; every value quoted (including U+201A/U+201B) |
| C6 | Medium | Any different catalog version counted as an update, so an older one was offered (downgrade) | Update only when the catalog version is newer; same rule for page, classic dialog and badge |
| C7 | Medium | Policies deployed as REG_SZ "1" were ignored (store stayed open) | Decimal REG_SZ accepted |
| C8 | Medium | Localized messages with catalog names in fixed buffers could end Power PDF on overlong names | Truncating formatting |
| C9 | Low | Unlimited download size | Capped at the announced size (icons 4 MB, screenshots 3 MB) |
| C10 | Low | Page messages accepted without checking their origin | Only from the page the host loaded |
| C11 | Low | Updating an add-on nested `assets`, `docs`, `UILayout` inside the old folders | Folders replaced as a whole |
| C12 | Low | Self-update helper used `msiexec` from the path and trusted the downloaded file | Hash checked again, full path |
| C13 | Low | A foreign catalog entry named `PluginStore` could replace the store client | Skipped |

Open: `ServerUrl` in HKCU can be changed by any process of the user (an
install still needs the UAC prompt); package and MSI signing would close
this (planned H4). The self-update MSI is unpacked in the user's TEMP before
`msiexec` elevates. Network calls of the store window still run on the UI
thread (short freezes on slow networks). The options page with the new
customer-code field was built but not clicked through live.

## S0.15.0 additions

- Catalog signatures (ECDSA P-256 over id, version, SHA-256; TSV column 21,
  JSON `signature`, key at `/api/signing-key`). Clients from 0.7.1 install
  only packages signed with a pinned key; closes the open client item about
  a changed `ServerUrl` in HKCU. Key: App Setting `Signing__PrivateKeyPem`,
  else created once and stored encrypted in the database (backed up with the
  encrypted key ring). A second, offline key is pinned in the client for
  recovery.
- UI-language rule: every add-on's own UI in the 16 Power PDF languages
  (`UI_LANGS_MISSING`, `LANGS_INCOMPLETE` now errors); the agent guide makes
  the submitting AI add missing translations before it uploads.

## Code check S0.16.0 (API and validation pipeline)

A separate review of the API and the package checks (26 confirmed findings)
led to these changes:

- Signing key unreadable after a key-less restore no longer takes the catalog
  down (served unsigned, admins see a warning); a key-less restore of a backup
  with key ring needs explicit confirmation. Signature format v2 also covers
  the binary name.
- Resource parser hardened against crafted .zxt (visited set, entry caps);
  import walk bounded.
- ZIP entries: duplicates and collisions on Windows, reserved device names,
  control characters, entry count, unexpected top-level folders, nested
  archives (by content), oversized entries reported instead of skipped,
  executables found by content.
- VERSION_EXISTS reaches clients as the documented 409 (and in dry runs);
  concurrent uploads of a package are serialized.
- FOREIGN_DEPENDENCY is an error (extra DLLs are never installed next to the
  plug-in), hint corrected.
- UILayout checks are root-anchored and case-insensitive, read every layout
  file and toolbar, require the base NameAndTitle.xml; atoms must stay inside
  the declared namespace; the bare tab atom is refused; rejected/withdrawn
  packages no longer block a namespace.
- New checks: PlugInMain export, .NET assemblies refused, VERSIONINFO vs
  manifest version (warning), ASLR/DEP (warning), reserved id prefixes for
  admins, scripts in help pages (warning), arm64 declared consistently.
- Private packages are not revealed by validator messages; share-link click
  counting uses the public set; TSV and JSON pick the same delivery; JSON
  carries zxtName and iconUrl; "no" accepted for Norwegian.
- Deliveries: no unreviewed beta as the default live stage of a public add-on;
  a version without a mode means "fixed"; dates without offset are UTC.
- Source code of a reviewed version can only be replaced by admins.
- API withdraw uses the portal service (owner notified, warning for pinned
  deliveries).
- 2 MB request limit on all API endpoints except uploads; 60 checks and
  submissions per account and hour; X-Forwarded-For trust configurable
  (Network:TrustForwardedFor).

Decided earlier and still open: no server-side malware scan (decision Oct 3,
2026), no Authenticode signing (no certificate).

## S0.17.1 to S0.18.0 (Oct 4, 2026)

- **Start-up crash on App Service (exit code 139), fixed in S0.17.1.** The SQLite database
  lives on the Azure Files share (`/data`, SMB). Entity Framework creates it in WAL mode,
  whose index is a memory-mapped `-shm` file; SQLite does not support that on network file
  systems. While App Service started a new container and the old one was still running,
  the new one crashed, several times in a row, until App Service blocked the site. The
  server now switches the database to the rollback journal at start-up (stored in the file)
  and reads the geolocation databases into memory instead of mapping them from the share.
  Nothing slow runs before the web server listens; start-up steps are logged with durations.
- **Paged lists (S0.17.2/S0.17.3):** audit log paged in the database (before: fixed to the
  newest 300), reports, users, plug-ins, customers, versions, problem reports and the public
  catalog paged (25/50/100).
- **Stylesheet cache busting (S0.17.3):** `asp-append-version`, so a security or layout fix
  in CSS reaches browsers at once.
- **Automatic backup to Azure Blob Storage (S0.18.0).** The access key (connection string or
  SAS URL) and the passphrase are stored with the data protection key ring (never in plain
  text, never in logs or the audit; error texts mask `sig=` and `AccountKey=`). The whole
  backup is encrypted before upload: format PSBAK1, PBKDF2-SHA256 (600,000 iterations),
  AES-256-GCM in 1 MiB chunks with a random nonce each; every chunk authenticates the header,
  its index and a last-chunk flag, so reordered, swapped, damaged or cut-off files are refused
  before anything is restored. The key ring inside is additionally encrypted (keys.enc). A
  SAS limited to one container works (the container is only created when it is missing).
  Recommended on the storage account: soft delete for blobs. A failed run is audited and
  mailed to the admins (event "BackupFailed").
- **Faster start on the share (S0.18.1):** the schema upgrade reads tables, indexes and
  columns in one query and runs only what is missing (before: some 60 statements and two
  writes on every start, 7 to 18 seconds on Azure Files); category seeding in one query and
  without a write when nothing changed. The production log showed the cause of the earlier
  crashes as "SQLite Error 14: unable to open database file" while the old container still
  held the WAL database; every start since S0.17.1 succeeded with both containers running.
- **Restore fixes and disaster recovery test (S0.18.2).** (1) A restore on a new server was
  always refused: the lockout check compared the account id, and an admin created on a new
  server has a new one. It now accepts the same account or the same email address, and on a
  fresh server (only the account just created, no packages) it restores without the check and
  signs the temporary account out. (2) The restore deleted the old -wal/-shm files but not a
  rollback journal (-journal, since S0.17.1); a leftover journal would have been applied to the
  restored database and damaged it. Verified end to end: backup to (emulated) Azure Blob,
  brand-new server, first-run setup, restore from the container, restart, original admin
  signs in; users, packages, versions, settings, audit, all files, signing key and verifiable
  catalog signatures identical. Everything the server stores is in the backup (database,
  packages with source code, avatars, devkit, key ring encrypted); not included on purpose:
  geolocation data (downloaded again), safety backups, setup token, caches.
- **Access requests (S0.18.2):** "Request a developer account" in the footer for visitors;
  bot traps on the form (hidden field, minimum time of 3 s via a signed timestamp), answered
  like a normal request so bots learn nothing; dropped attempts are audited. Still 5 requests
  per address and hour, no account enumeration.

# Veracode pre-check, Oct 13, 2026 (S1.26.0, C1.9.15)

Tungsten releases developments such as the Add-on Store only after a Veracode
policy scan (static analysis of the compiled binaries, taint analysis from
untrusted sources to sinks, findings by CWE, "Fix Required by Policy" with a
deadline). Before that scan, server and client were reviewed with the CWE
classes Veracode reports. Two independent read-only passes (server C#/Razor,
client C++/WebView2), every finding checked in the code. No serious defect was
found; the fixes below remove the real weaknesses and most findings Veracode
would raise without a recognised cleanser. What remains is listed with a
prepared mitigation text.

Dependencies (SCA): `dotnet list package --vulnerable --include-transitive`
clean; the client links only the WebView2 SDK 1.0.4258.31 (static loader) and
the Plug-in UI kit; nlohmann/json is not in the client.

## Fixed in S1.26.0 (server)

| CWE | Where | Fix |
|---|---|---|
| 614 / 1004 | Culture cookie of `/set-lang` without Secure, HttpOnly, SameSite | HttpOnly, SameSite=Lax, Secure outside Development; the shell cookie is Secure outside Development too |
| 601 | `/set-lang`, `/set-shell` redirected with `Results.Redirect` after an own check | Shared `Shell.SafeTarget` plus `Results.LocalRedirect` |
| 22 / 73 | Restore: key file names from the backup ZIP were only checked for `/` (a backslash would leave the folder on Windows) | Exact pattern `key-<guid>.xml`, `Path.GetFileName` |
| 73 | `/api/devkit/{path}`, `/api/sdk/{name}`, avatars, safety-backup download built paths from request values | The request only selects one of the files that exist; the path comes from the directory listing or a constant |
| 73 | Upload bundle temp file took the extension of the ZIP entry name | Fixed `.ppak` / `.zip` |
| 117 | Package ids, versions, kinds, mail addresses, provider names, answers of other servers went into the log unchanged | `LogSafe.Of`: control characters (also U+2028/2029) become `_`, values are cut |
| 79 / 80 | 12 × `Html.Raw(JsonSerializer.Serialize(...))` in Index, layout, add-on picker, issue detail; `HtmlString` from a regex in Admin/Rules | JSON in Razor-encoded `data-*` attributes, read with `document.currentScript.dataset` / `JSON.parse`; rule texts built from encoded parts with `HtmlContentBuilder` |
| 209 | Validation answers carried `ex.Message` (ZIP, JSON); SendTo storage test showed the unmasked SDK message | ZIP: exception type only; JSON: line and position; SendTo uses the masking `CloudBackupService.Short` |
| 1333 / 400 | Regular expressions on package content without a timeout | Process-wide default timeout of 2 s (`REGEX_DEFAULT_MATCH_TIMEOUT`) |
| 918 | Cloud backup accepted `http://127.0.0.1` (storage emulator) in production | Only on Development servers |
| 20 / 93 | `App.PublicBaseUrl` stored unchecked; it goes into mail links and the UAT `.reg` file | Absolute https (http only for loopback), no query, fragment or user info; `.reg` values drop control characters |

Note for local tests: the cloud backup against the storage emulator now needs
the Development environment (launch configuration `pluginstore-dev`). The plain
`pluginstore` configuration runs in Production, where antiforgery requires
HTTPS and sign-in pages answer 500 on http://localhost.

## Fixed in C1.9.15 (client)

| CWE | Where | Fix |
|---|---|---|
| 252 / 295 | Redirect block and TLS 1.2 fallback set without checking the result (a failed call would have left the WinHTTP defaults) | Download stops when either cannot be set |
| 73 / 252 | Temp package name contained catalog id and version; `GetTempPathW` unchecked | `addonstore-<guid>.ppak`, stop when TEMP is not available |
| 134 | 18 messages formatted with `_snwprintf_s` and a format string from the string resources | `FPFormat` / `FPLocFormat`: placeholders filled by plain replacement; a wrong placeholder in a translation can no longer read past the arguments |
| 117 | Log lines with catalog and server values | `WriteLine` replaces control characters |
| 113 | `X-Install-Id` from HKCU only checked for length | Exactly 8-4-4-4-12 hex digits |
| 476 | `get_CoreWebView2` unchecked before use | Checked, store window falls back like any WebView2 failure |
| 362 | Beta and URL-lock flags read by worker threads | `std::atomic<bool>` |
| 125 | Empty layout file read through `&raw[0]` | Returns early |
| 427 (hardening) | Elevated PowerShell resolved modules through the user's `PSModulePath`; MSI log path from `$env:windir` | `$env:PSModulePath=$PSHOME+'\Modules'` before any cmdlet; `[Environment]::GetFolderPath('Windows')` |
| 20 | `AddonStoreLink.exe` started whatever HKLM App Paths named | Only an absolute path ending in `\PowerPDF.exe` that exists |
| Build | No CFG/CET; `AddonStoreLink.exe` without PDB | `/guard:cf`, `/CETCOMPAT`, `/DEPENDENTLOADFLAG:0x800`, full PDB for both binaries (verified with dumpbin) |

## Expected findings with prepared mitigation texts

Veracode does not recognise own validation routines as cleansers, so these
places will probably still be reported. The texts are written for the
"Propose Mitigation" dialog (English, as the reviewers read them).

**CWE-78 OS Command Injection** (client `install.cpp` RunElevated and helper
start, `webui.cpp` ShellExecute of links, `AddonStoreLink.cpp`). Mitigated by
Design: "The started program is fixed: powershell.exe from GetSystemDirectoryW,
msiexec.exe from the system folder, PowerPDF.exe from HKLM App Paths (admin-only,
checked to be an existing absolute path to PowerPDF.exe). Every value from the
network or the registry is checked against strict character whitelists (package
id, SemVer, SHA-256 hex, binary name) after the catalog signature (ECDSA P-256,
pinned key) was verified, quoted as a PowerShell literal and passed only as
Base64 via -EncodedCommand, so no value can leave its string literal or add
arguments. Links are opened only as https URLs on an allowlist or as mailto with
one checked address, never as a file or program."

**CWE-494 Download of Code Without Integrity Check** (client download and
install chain). Mitigated by Design: "Every package is signed in the catalog
(ECDSA P-256 over id, version, SHA-256 and binary name; public key pinned in the
client). The SHA-256 is checked in the user context and again in the elevated
step after the file was copied into a staging folder that only administrators
can write, before anything is extracted or run. Redirects are disabled and the
download is limited to the announced size."

**CWE-321 / 798 Hard-coded Cryptographic Key** (client `signature.cpp`).
Potential False Positive: "Pinned public verification keys (key pinning). They
are not secret; the private keys exist only on the server (Data Protection
encrypted) and offline."

**CWE-73 External Control of File Name or Path** (server storage paths from
the database: package files, source ZIPs, screenshots, mirrored packages;
client cache file names). Mitigated by Design: "Path components are restricted
by strict whitelists at upload (package id
`^[a-z0-9][a-z0-9-]*(\.[a-z0-9][a-z0-9-]*)+$`, SemVer); the resolved full path is
verified to lie inside the storage root (separator-aware prefix check). Values
read from the database were validated when they were written."

**CWE-117 Improper Output Neutralization for Logs** (remaining log calls, if
Veracode does not accept `LogSafe.Of`). Mitigated by Design: "All values that
do not come from constants pass a central neutralizer (LogSafe.Of on the server,
WriteLine on the client) that replaces CR, LF, all other control characters and
the Unicode line separators before the entry is written, so a value cannot start
a new log line."

**CWE-918 Server-Side Request Forgery** (federation master URL, update watch,
AI providers, Azure Blob). Mitigated by Design: "Target hosts are configured by
store administrators only (admin-only pages, audited). Federation and update-watch
requests use https only, no automatic redirects and a timeout; federation
requests are ECDSA-signed both ways; AI providers and Azure Blob Storage are
reached on their fixed vendor hosts; path segments from remote answers are
validated (alphanumeric) or Uri.EscapeDataString-encoded."

**CWE-352 Cross-Site Request Forgery** (cookie-authenticated minimal API
writes). Mitigated by Design: "Razor Pages use the antiforgery token on every
form. Cookie-authenticated state-changing API calls need a custom request header
that browsers cannot send cross-site without CORS (CORS is off); the sign-in
cookie is SameSite=Lax, HttpOnly, Secure. Bearer-token calls carry no ambient
credentials."

**CWE-113 HTTP Response Splitting** (Content-Disposition file names,
`X-Source-Version`). Potential False Positive: "Kestrel refuses CR, LF and
other control characters in response header values; the values are validated
(SemVer, file-name whitelist) before."

**CWE-80 / 79 in text responses** (TSV catalog, markdown guide, OpenAPI).
Potential False Positive: "Served as text/plain, text/markdown or
text/tab-separated-values with X-Content-Type-Options: nosniff and a CSP
without inline scripts; browsers do not render them as HTML."

**CWE-319 Cleartext Transmission** (client accepts http://localhost for the
development server). Mitigated by Network Environment: "Plain http is accepted
only for loopback addresses (local development server); every other address
must use https with TLS 1.2 or newer."

## Not changed, on purpose

- No JSON console logger: it would neutralize log injection for Veracode
  reviewers beyond doubt, but changes the App Service log stream that
  operations read. Revisit if Veracode refuses the CWE-117 mitigation.
- The client keeps `_CRT_SECURE_NO_WARNINGS` (removing it is not yet tested
  against the Power PDF SDK headers) and the `http://localhost` exception
  (development against a local server with release builds).
- Icons and screenshots have no catalog hash: they are no code, are checked by
  their magic bytes and shown only as `data:` images in the store window.

## For the upload to Veracode

- Server: `dotnet publish -c Release` with portable PDBs; upload the publish
  folder without `appsettings.Development.json`. Never include
  `server/dev-credentials.local.txt` or `server/signing-recovery-key.local.pem`
  (both are git-ignored, but they sit next to the sources).
- Client: `client\Release\PluginStore.zxt` + `PluginStore.pdb` and
  `client\link\Release\AddonStoreLink.exe` + `AddonStoreLink.pdb`. Veracode may
  recognise modules by their extension; upload the plug-in as
  `PluginStore.dll` with `PluginStore.pdb` if `.zxt` is not accepted. The PDBs
  are not part of the MSI.
  Correction (S1.27.0, from the Veracode packaging guide for Windows C/C++,
  https://docs.veracode.com/r/compilation_winccpp): Veracode prefers its own
  build, `/Zi /Od /GS-`, debug runtime (`/MTd` or `/MDd`), no `/RTC`,
  `/INCREMENTAL:NO /DEBUG:FULL`, every EXE/DLL with its PDB; without the PDB of
  the main module the scan does not start. `/Z7` does not produce usable PDBs.
  Only x86/x64 are scanned (no ARM64). Optimized release builds work but give
  weaker results. Such a scan build must never be packaged for the store.
- Python under `tools/` and `client/installer/` is build tooling and never
  shipped; leave it out of the upload, otherwise Veracode scans it as an
  application module.

# Code security checks for every add-on (S1.27.0)

The CWE classes of the release scan became part of the store's own checks for
every upload, the agent guide and the AI review aid.

What Veracode does (research Oct 13, 2026, docs.veracode.com):
- "Veracode and the CWE" (https://docs.veracode.com/r/c_review_cwe) lists
  about 175 CWEs with severity 0 to 5; for example 78, 114, 134 and 190 are
  severity 5, 22, 73, 79, 80, 117, 327, 377, 427, 611, 732 and 798 severity 3.
- The recommended High and Very High policies allow no finding of Medium (3)
  or higher; grace periods are set per organisation policy, Veracode has no
  default (https://docs.veracode.com/r/Built_in_security_policies,
  https://docs.veracode.com/r/c_policy_grace_period).
- Findings need data flow from an attacker-controlled source. For C there are
  only two recognised cleansers (base64_encode, UrlEscape, CWE-113) and no
  custom cleansers (https://docs.veracode.com/r/Supported_C_Cleansing_Functions,
  https://docs.veracode.com/r/customcleansers); the rest is closed by
  mitigation proposals.

What the store checks now (all warnings; admins can make each mandatory on
the Rules page, which now also applies to the source report):

| Code | CWE | Where |
|---|---|---|
| PE_EXPLOIT_MITIGATIONS | hardening | .zxt and bin/: /guard:cf, /GS (load config security cookie), /CETCOMPAT (debug directory type 20), /HIGHENTROPYVA |
| PE_UNSAFE_FUNCTIONS | 120 | imports of strcpy, wcscat, sprintf, gets, lstrcpy, StrCpy, wsprintf ... |
| SOURCE_UNSAFE_FUNCTION | 120/676 | the Microsoft SDL banned copy/format functions, also strncpy/strncat |
| SOURCE_FORMAT_STRING | 134 | printf family and CString::Format whose format is not a literal or named constant (IDS_ ids count as resource formats) |
| SOURCE_COMMAND_EXEC | 78 | system, _popen, WinExec, CreateProcess with a NULL program path |
| SOURCE_DLL_SEARCH_PATH | 427/114 | LoadLibrary with a bare name and no LOAD_LIBRARY_SEARCH flag; SetDllDirectory, SetDefaultDllDirectories, AddDllDirectory, SetSearchPathMode |
| SOURCE_WEAK_CRYPTO | 327/328/338 | MD5, MD4, SHA-1, DES, 3DES, RC2, RC4, ECB, rand() |
| SOURCE_UNSAFE_HTML | 79 | innerHTML/outerHTML/srcdoc with data, insertAdjacentHTML, document.write, eval, new Function, jQuery .html(), string timers, postMessage to "*" |
| SOURCE_XML_EXTERNAL_ENTITIES | 611 | MSXML 3, ProhibitDTD off, resolveExternals on, XmlLite DtdProcessing_Parse, libxml2 NOENT/DTDLOAD |
| SOURCE_TEMP_FILE | 377 | tmpnam, _tempnam, _mktemp, tmpfile |
| SOURCE_WEAK_PERMISSIONS | 732 | NULL DACL, SDDL granting Everyone/Users/anonymous full access |
| SOURCE_HARDCODED_KEY | 321/329 | byte arrays named key/iv/salt/secret with fixed values |
| SOURCE_WEBVIEW_EXPOSURE | 749/346 | AddHostObjectToScript, host objects allowed, developer tools on |

Source checks read the developer's own files only: comments are blanked,
string literals masked for name searches, folders with their own license file
and `third_party/`, `vendor/`, `external/`, `deps/`, `node_modules/`,
`packages/` are skipped. The AI review aid receives the code security findings
with their places and a review brief for the CWE classes that patterns cannot
judge (integer overflow in sizes, paths and commands from outside data, log
injection, secrets in memory or storage, prompt injection); it reports every
weakness as its own concern with the CWE number, and a high one means at least
"check_more".

Trial run over the newest version of our 17 own add-ons (package + source):
- No CET anywhere and no Control Flow Guard in 13 of them; /GS and 64-bit ASLR
  everywhere. The store client 1.9.15 is the only add-on without findings.
- SOURCE_FORMAT_STRING in 11 add-ons: localized texts formatted with a format
  from the string table (the pattern C1.9.15 removed from the client).
- rand() in Barcode Stamps (GUID fallback), SHA-1 in QES Sign (LTV) and a
  Cloud Signature test tool, a constant SVG through innerHTML in the AI
  services window.
- False alarms found in the trial run and fixed before release: names inside
  string literals, the template form `sprintf_s(buf, "fmt", ...)`, named
  constant formats, and "file.html (x)" in comments.
