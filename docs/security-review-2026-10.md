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

