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
  JSON `signature`, key at `/api/signing-key`). Clients from 0.7.0 install
  only packages signed with a pinned key; closes the open client item about
  a changed `ServerUrl` in HKCU. Key: App Setting `Signing__PrivateKeyPem`,
  else created once and stored encrypted in the database (backed up with the
  encrypted key ring). A second, offline key is pinned in the client for
  recovery.
- UI-language rule: every add-on's own UI in the 16 Power PDF languages
  (`UI_LANGS_MISSING`, `LANGS_INCOMPLETE` now errors); the agent guide makes
  the submitting AI add missing translations before it uploads.
