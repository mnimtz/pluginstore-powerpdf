<div align="center">

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="docs/img/tungsten-logo-white.svg">
  <img src="docs/img/tungsten-logo-navy.svg" alt="Tungsten Automation" width="320">
</picture>

# PluginStore-PowerPDF

**A private plugin store for Tungsten Power PDF.**
Publish `.ppak` plugin packages through a reviewed pipeline; end users browse
and install them with one click from a *Plugin-Store* ribbon inside Power PDF.

[![Deploy to Azure](https://aka.ms/deploytoazurebutton)](https://portal.azure.com/#create/Microsoft.Template/uri/https%3A%2F%2Fraw.githubusercontent.com%2Fmnimtz%2Fpluginstore-powerpdf%2Fmain%2Finfra%2Fazuredeploy.json)

![.NET 8](https://img.shields.io/badge/.NET-8.0-512BD4?style=flat-square)
![C++ /MFC client](https://img.shields.io/badge/client-C%2B%2B%20%2F%20MFC%20.zxt-00A0FB?style=flat-square)
![i18n](https://img.shields.io/badge/i18n-16%20European%20languages-00EB86?style=flat-square)
![Brand](https://img.shields.io/badge/design-Tungsten%20Brand%20Book-002854?style=flat-square)
![License: MIT](https://img.shields.io/badge/license-MIT-brightgreen?style=flat-square)

<img src="docs/img/web-catalog.svg" alt="The Plugin-Store web catalog" width="760">

</div>

---

## Why

Colleagues around the world build Power PDF plugins with the Plugin SDK, each
on their own, shared by mail and memory. PluginStore-PowerPDF turns that into
one channel: a single reviewed catalog, a beta channel for instant testing,
automatic quality gates built from hard-won SDK knowledge, and one-click
installs and updates right inside Power PDF.

## How it works

```
 Developer (+ Claude, via API token)         Admin                 End user
        │                                      │                       │
        │ POST /api/packages/validate          │ review queue          │ Plugin-Store ribbon
        │ POST /api/packages                   │ approve / reject      │ browse · install · update
        ▼                                      ▼                       ▼
  ┌──────────────────────────────────────────────────────────────────────────┐
  │   PluginStore-PowerPDF · Azure Web App · SQLite + packages on /data      │
  │                                                                          │
  │   submitted ──(automatic checks)──▶ BETA channel ──(approval)──▶ LIVE    │
  └──────────────────────────────────────────────────────────────────────────┘
```

| | |
|---|---|
| 🏪 **End users never see this server** | The catalog is read anonymously by the Plugin-Store ribbon add-on. Accounts exist only for plugin publishers and admins; registration is an access *request* that an admin approves. |
| 🔍 **Every upload is validated** | Manifest schema, SemVer monotonicity, x64 + ARM64 PE checks, debug-runtime detection, import-table scan, SHA-256 verification, ribbon governance, reserved names, layout and localization pitfalls. Every finding carries a stable `code` and a concrete `hint`. |
| 🧪 **Beta channel** | Versions that pass all automatic checks become instantly installable for users who enabled the beta option in Power PDF, while an admin reviews them for the live store. |
| 🤖 **Agent-friendly API** | `GET /api/agent-guide` teaches any AI assistant the full workflow with zero prior knowledge. Personal tokens let Claude validate, fix and submit packages in a loop until the report is green. |
| 📜 **Audit trail** | Registrations, approvals, tokens, submissions (version + changelog are mandatory), reviews and downloads are recorded and searchable. |
| 🌍 **16 European languages** | Auto-detected from the browser, manually switchable, with localized catalog texts straight from the package manifests. |

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

**Optional email notifications** via [Resend](https://resend.com), two app
settings: `Email__ResendApiKey` and `Email__From`. Without them the app runs
normally, just silently.

## The Power PDF client

The `client/` folder holds the **Plugin-Store ribbon add-on** (C++/MFC `.zxt`,
built with the Power PDF Plugin SDK):

- Adds a *Plugin-Store* group to the shared **Enhanced Features** ribbon tab.
- Lists the catalog with localized names, changelogs, installed versions and
  update status; installs with SHA-256 verification and a single UAC prompt.
- Drops each package's `manifest.json` next to the plugin, so updates are
  detected for every plugin, including MSI-deployed ones.
- Options page under *File → Options → Plugin-Store*: server URL (defaults to
  your instance, enforceable via HKLM policy) and the beta-channel switch.

End users install it with the MSI from the store's landing page
(`/download/pluginstore.msi`); afterwards the client offers its own updates.

Build with `client\build.cmd` and `client\installer\build_msi.cmd`
(Visual Studio 2022, WiX v3), deploy for testing with an elevated
`client\deploy.cmd`. Publishing a new client version (own lane: admin-only,
live immediately) is described step by step in
[docs/RELEASING.md](docs/RELEASING.md).

## API in 30 seconds

**One URL is enough for an AI assistant:** tell Claude
*"Read https://<host>/api/agent-guide and publish the plugin in this folder."*
The guide explains the token (environment variable `PPAK_TOKEN`), installs the
Claude Code skill from `/api/skill` on first use and walks through packaging,
validation and submission. Signed-in users find the same instructions on the
**API** page of the web UI.

```bash
curl https://<host>/api                  # discover all endpoints
curl https://<host>/api/agent-guide      # the full guide (markdown)
curl https://<host>/api/schema/manifest  # manifest.json schema

curl https://<host>/api/skill            # Claude Code skill (SKILL.md)

# with a personal token from your profile page:
curl -H "Authorization: Bearer ppak_..." https://<host>/api/me

# dry-run validation, then submit:
curl -X POST -H "Authorization: Bearer ppak_..." -H "Content-Type: application/zip" \
     --data-binary @my-plugin.ppak https://<host>/api/packages/validate
curl -X POST -H "Authorization: Bearer ppak_..." -H "Content-Type: application/zip" \
     --data-binary @my-plugin.ppak https://<host>/api/packages
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

## Local development

```bash
dotnet run --project server/src/AddonStore.Web --urls http://localhost:5190
```

SQLite and package storage land in `server/src/AddonStore.Web/data/`.
The client's default server URL is `https://ppdf-store.azurewebsites.net`; point it at `http://localhost:5190` for local development via *File → Options → Plugin-Store*.

## Repository layout

```
server/src/AddonStore.Web/   ASP.NET Core 8 app (Razor Pages + minimal API)
  Api/                       endpoints, agent guide, manifest schema
  Validation/                the package validation pipeline
  Services/                  submission, tokens, audit, notifications, catalog
  Pages/                     publisher + admin web UI, Tungsten-branded
  Resources/                 SharedResource.<lang>.resx, 16 languages
client/                      Plugin-Store ribbon add-on (C++/MFC .zxt)
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
