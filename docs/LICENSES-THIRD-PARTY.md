# Third-party inventory (license compliance)

Standing team policy: only MIT, BSD or Apache-2.0 licensed components ship in
deliverables; no GPL/AGPL/LGPL. Status: **compliant**. Last audit: Oct 3, 2026.

## Server (AddonStore.Web, shipped as container image)

| Component | Version | License | Notes |
|---|---|---|---|
| ASP.NET Core 8 / .NET 8 runtime | 8.0 | MIT | Microsoft |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 8.0.11 | MIT | |
| Microsoft.EntityFrameworkCore.Sqlite (+ Microsoft.Data.Sqlite) | 8.0.11 | MIT | |
| SQLitePCLRaw.bundle_e_sqlite3 | 3.0.5 | Apache-2.0 | pinned directly; 2.x carried GHSA-2m69-gcr7-jv3q (High), 3.x is clean |
| SQLite (native, via bundle) | 3.x | Public domain | |
| MaxMind.Db (reader for .mmdb files) | 5.2.0 | Apache-2.0 | since S0.9.0, reports geolocation |
| DB-IP "IP to City Lite" + "IP to ASN Lite" (data) | monthly | CC BY 4.0 | since S0.9.0; data, not code; downloaded at runtime into data/geo, not in the image; attribution "IP Geolocation by DB-IP" with link on the reports page |
| Red Hat Display (font) | n/a | SIL OFL 1.1 | referenced by name only (`font-family`), no font file bundled |
| Tungsten logos / brand assets | n/a | Tungsten Automation property | internal use per Brand Book |

`dotnet list package --vulnerable --include-transitive`: **no vulnerable
packages** (after the SQLitePCLRaw pin).

## Add-on Store ribbon client (PluginStore.zxt)

| Component | License | Notes |
|---|---|---|
| Power PDF Plugin SDK headers | Tungsten Automation | internal |
| MFC / Win32 / WinHTTP / BCrypt | Microsoft platform | OS-provided, dynamic MFC |
| Microsoft WebView2 SDK (static loader) | BSD-3-Clause | since C0.4.0, modern store window; header + LICENSE in client/third_party/webview2 |
| (no other third-party code) | | pugixml was removed before the first release |

## Store packages built from our plugins

| Package | Third-party inside | License |
|---|---|---|
| com.tungsten.pluginstore | none | |
| com.tungsten.smartbookmarks | nlohmann/json (json.hpp) | MIT |
| | Tesseract OCR: **not bundled**; optional, detected at runtime | Apache-2.0 |
| com.tungsten.xfaconverter | pugixml | MIT |

## Enforcement in the pipeline

The upload validator warns on GPL/AGPL/LGPL markers in LICENSES.md
(`LICENSE_GPL_MARKER`) and flags unexpected native imports
(`FOREIGN_DEPENDENCY`) so reviewers check bundled DLLs for license and origin.
