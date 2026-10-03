# Security review, Oct 3, 2026 (v0.2.0)

Adversarial code review of the server (auth, uploads, path handling, XSS,
email injection, redirects, DoS). Findings and their status:

| # | Severity | Finding | Status |
|---|---|---|---|
| H1 | High | ZIP decompression bomb: entries inflated into memory without a cap | **Fixed**: hard caps per entry type (manifest 256 KB, text 1 MB, icon 4 MB, .zxt 120 MB) and 400 MB total inflated per package |
| H2 | High | Open redirect via /set-lang returnUrl (`//host`); 500 on invalid culture | **Fixed**: culture validated against the supported list, returnUrl must be a single-slash relative path |
| M1 | Medium | Devkit path prefix check without separator (sibling-dir leak) | **Fixed**: separator-aware IsUnder() |
| M2 | Medium | HTML injection into admin mail via display name / token name | **Fixed**: HtmlEncode on both |
| M3 | Medium | Beta channel = anonymous distribution of unreviewed native code | **Accepted for the internal phase** (documented); the customer phase gates beta behind an access code (see project plan 3.2a) |
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
