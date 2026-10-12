# Third-party inventory (license compliance)

Standing team policy: only MIT, BSD, Apache-2.0 or CC0-1.0 licensed components ship
in deliverables; no GPL/AGPL/LGPL. Every component comes with its copyright notice and
its full license text, including code a library contains from other authors.
Status: **compliant**. Last audit: Oct 12, 2026 (S1.25.0, C1.9.13).

## Server (AddonStore.Web, shipped as container image)

The image carries `/app/THIRD-PARTY-NOTICES.md` with the copyright and full license
text of every NuGet package (generated from the restored packages by
`tools/gen_third_party_notices.py`, file `server/THIRD-PARTY-NOTICES.md`; the CI step
`tools/check_docs.py` fails when a package reference is missing there) and `/app/LICENSE`.

| Component | Version | License | Notes |
|---|---|---|---|
| ASP.NET Core 10 / .NET 10 runtime | 10.0 | MIT | part of the base image, notices in `/usr/share/dotnet/ThirdPartyNotices.txt` |
| Microsoft.AspNetCore.Identity.EntityFrameworkCore | 10.0.12 | MIT | |
| Microsoft.EntityFrameworkCore.Sqlite (+ Core, Relational, Abstractions, Analyzers, Microsoft.Data.Sqlite.Core) | 10.0.12 | MIT | |
| SQLitePCLRaw.bundle_e_sqlite3 (+ config, core, provider) | 3.0.5 | Apache-2.0 | pinned directly; 2.x carried GHSA-2m69-gcr7-jv3q (High), 3.x is clean |
| SQLite (native, via bundle) | 3.53.4 | Public domain | |
| MaxMind.Db (reader for .mmdb files) | 5.2.0 | Apache-2.0 | since S0.9.0, reports geolocation |
| Azure.Storage.Blobs (+ Azure.Storage.Common 12.29.0, Azure.Core 1.60.0, System.ClientModel 1.14.0, System.IO.Hashing, System.Memory.Data, Microsoft.Bcl.AsyncInterfaces) | 12.30.0 | MIT | since S0.18.0, automatic backup to Azure Blob Storage (off until configured) |
| Microsoft.Identity.Client (+ Extensions.Msal, IdentityModel.Abstractions) | 4.84.2 | MIT | pulled in by Azure.Core |
| System.Security.Cryptography.ProtectedData | 4.5.0 | MIT | |
| Anthropic (official C# SDK for the Claude API) | 12.53.0 | MIT | since S0.12.0, optional AI assistant (off by default); Gemini is called via plain HTTPS, no extra package |
| Microsoft.Extensions.AI.Abstractions | 10.5.1 | MIT | pulled in by Anthropic |
| Apache.Arrow | 22.1.0 | Apache-2.0 | pulled in by Anthropic; its LICENSE (with the FlatBuffers part) and the NOTICE of Apache Arrow .NET are in the notices file |
| DB-IP "IP to City Lite" + "IP to ASN Lite" (data) | monthly | CC BY 4.0 | since S0.9.0; data, not code; downloaded at runtime into data/geo, not in the image; attribution "IP Geolocation by DB-IP" with link on the reports page |
| Red Hat Display (font) | n/a | SIL OFL 1.1 | referenced by name only (`font-family`), no font file bundled |
| Tungsten logos / brand assets | n/a | Tungsten Automation property | per Brand Book |

**Base image.** `mcr.microsoft.com/dotnet/aspnet:10.0` is Ubuntu 24.04 "noble". Its
Ubuntu packages (some under GPL or LGPL, for example the C library) are passed on
unchanged under their own licenses; texts in `/usr/share/doc/<package>/copyright`,
sources via https://launchpad.net/ubuntu and https://github.com/dotnet/dotnet-docker.
The notices file and the image label `com.tungsten.addonstore.base-image-licenses` say so.

`dotnet list package --vulnerable --include-transitive`: **no vulnerable
packages** (after the SQLitePCLRaw pin).

## Add-on Store ribbon client (PluginStore.zxt and its MSI)

| Component | License | Notes |
|---|---|---|
| Power PDF Plugin SDK headers and `client/common/pimain.cpp` | Tungsten Automation | own product SDK |
| MFC / Win32 / WinHTTP / BCrypt | Microsoft platform | OS-provided, dynamic MFC |
| Microsoft WebView2 SDK (static loader) | BSD-3-Clause | since C0.4.0; header + LICENSE.txt in client/third_party/webview2 |
| Tungsten Plug-in UI (tpui) | MIT, Tungsten Automation | own kit, client/third_party/tpui with LICENSE |
| Installer (MSI) | own | built with the WiX Toolset 3.14 as a compiler only; since C1.9.13 no WiX runtime parts (no WixUI dialogs, bitmaps, texts, no wixca.dll; MS-RL) |

## Store packages built from our plug-ins (current versions)

| Package | Third-party inside | License |
|---|---|---|
| com.tungsten.pluginstore 1.9.13 | WebView2 SDK static loader | BSD-3-Clause |
| com.tungsten.compliancecheck 1.2.3, einvoice 1.2.3, mailmerge 1.2.3, qessign 1.2.3, stampassistant 1.2.4 | nlohmann/json 3.12.0 with the code it contains (Hoehrmann and Loitsch MIT, Hedley CC0-1.0, Google Abseil excerpt Apache-2.0); WebView2 SDK | MIT, CC0-1.0, Apache-2.0, BSD-3-Clause |
| com.tungsten.printixsecureprint 1.1.2 | nlohmann/json 3.12.0 with the code it contains | MIT, CC0-1.0, Apache-2.0 |
| com.tungsten.smartbookmarks 1.5.2 | nlohmann/json 3.12.0 with the code it contains; C++/WinRT projection headers (Windows SDK); WebView2 SDK. OCR through Windows.Media.Ocr, no Tesseract | MIT, CC0-1.0, Apache-2.0, BSD-3-Clause |
| com.tungsten.barcodestamps 1.2.5 | ZXing-C++ 2.3.0 with its libzueci (BSD-3-Clause, gitlost); nlohmann/json 3.12.0 with the code it contains; WebView2 SDK | Apache-2.0, BSD-3-Clause, MIT, CC0-1.0 |
| com.tungsten.xfaconverter 0.2.0 (withdrawn) | pugixml | MIT |

Each package carries the full texts in its LICENSES.md. Plug-ins of other teams and
sessions (Smart Compare, AI module, Send to, Spool Viewer and others) go through the
same checks at upload.

## Enforcement in the pipeline

The upload validator refuses copyleft licenses and markers (`LICENSE_NOT_ALLOWED`,
`LICENSE_COPYLEFT_BINARY`, `LICENSE_COPYLEFT_SOURCE`), and since S1.25.0 also:

- `LICENSE_TEXT_MISSING`: a declared license without its full text in LICENSES.md;
- `NOTICE_INCOMPLETE`: nlohmann/json or ZXing-C++ compiled in, but the code they
  contain (Abseil, Hoehrmann, Loitsch, Hedley; libzueci) is missing in LICENSES.md;
- `LICENSE_NOT_ALLOWED` for WiX runtime parts (MS-RL) in an MSI or EXE.

It warns on `THIRDPARTY_NOT_IN_LICENSES`, `FONT_LICENSE_REVIEW`,
`SOURCE_FOREIGN_DOCUMENT` and unexpected native imports (`FOREIGN_DEPENDENCY`), so
reviewers check bundled DLLs, fonts and documents for license and origin.
