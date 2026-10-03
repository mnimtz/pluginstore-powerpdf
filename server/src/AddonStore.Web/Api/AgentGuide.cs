namespace AddonStore.Web.Api;

/// <summary>
/// The self-describing part of the API: a markdown guide that lets a developer
/// or an AI agent work against this server with no prior knowledge, plus the
/// manifest JSON schema. Served at /api/agent-guide and /api/schema/manifest.
/// </summary>
public static class AgentGuide
{
    public static string Markdown(string baseUrl, string version) => $$"""
# PluginStore-PowerPDF, developer and agent guide

Server version {{version}}. This server distributes plugins (.ppak packages)
for Tungsten Power PDF. Everything you need to develop, package, validate and
submit a plugin is available from this API.

## Start here (AI assistants)

This URL is the only thing you need. Set yourself up once:

1. **Token.** Use the environment variable `PPAK_TOKEN`. Never print it,
   never store it in files or commits. If it is not set, ask the user to
   create a token at {{baseUrl}}/Profile and set it with
   `setx PPAK_TOKEN "ppak_..."` (new terminal afterwards).
2. **Skill (Claude Code, once per machine).** Download
   {{baseUrl}}/api/skill and save it as
   `%USERPROFILE%\.claude\skills\powerpdf-plugin-store\SKILL.md`, then tell
   the user it is installed. Future sessions recognise Power PDF plugin
   work automatically. Skip this if the file already exists.
3. **Check the connection:** `GET {{baseUrl}}/api/me` with the token.
4. Then follow the rest of this guide: package, validate until green, submit.

## Quick orientation

1. `GET {{baseUrl}}/api` lists all endpoints.
2. `GET {{baseUrl}}/api/me` with your token verifies authentication.
3. `GET {{baseUrl}}/api/devkit` lists SDK documentation, knowledge files and
   templates you can download (Plugin SDK docs, known pitfalls, project template).
4. `GET {{baseUrl}}/api/schema/manifest` is the schema for manifest.json.

## Authentication

Personal token, created by a signed-in user on the profile page of the web UI.
Send it on every authenticated call:

    Authorization: Bearer ppak_...

The token acts on behalf of its user and can only manage that user's own
packages. 401 responses carry a code: TOKEN_INVALID (wrong or unknown value),
TOKEN_REVOKED (recreate one in the profile), USER_NOT_ACTIVE (account not
approved yet).

## Package format (.ppak)

A .ppak is a ZIP container:

    manifest.json          required, see /api/schema/manifest
    x64/<Name>.zxt         required; x64 covers every machine, including
                           Windows-on-ARM (Power PDF runs there as ARM64EC
                           and loads x64 plugins)
    arm64/<Name>.zxt       optional native ARM64 build, validated when present
    assets/icon.png        recommended, square icon for the catalog
    LICENSES.md            recommended, third-party licenses (MIT/BSD/Apache-2.0 only)
    docs/...               optional documentation

## Languages (mandatory)

Every user-facing text in the manifest ships in ALL 16 European Power PDF
languages: en, de, fr, it, es, nl, pt, da, fi, nb, sv, pl, cs, hu, ru, tr.

- `description` and `changelog` MUST be objects containing all 16 codes; the
  server rejects the upload otherwise (`LANG_TEXT_INCOMPLETE`). Translate the
  texts yourself before uploading; the store, the web UI and the Power PDF
  client show them in each user's language.
- `name` may stay a single product name (e.g. {"en": "Smart Bookmarks"}) or be
  localized.
- The plugin's own UI follows the same rule: every UILayout language folder
  (ENU DEU FRA ITA ESP NLD PTB DAN FIN NOR SVE PLK CSY HUN RUS TRK) and all
  string resources.

## Ribbon governance (mandatory)

All store plugins share ONE ribbon tab so Power PDF does not grow a tab per
plugin:

- Toolbar atom: `FeaturePack`, localized title "Enhanced Features"
  (German "Erweiterte Funktionen") in every UILayout NameAndTitle.xml.
- Your plugin adds only its own GROUP on that tab: atom `FeaturePack::<YourName>`,
  buttons `FeaturePack::<YourName>::<Action>`. Set `ribbonAtomNamespace` in the
  manifest to that group atom.
- In code: `RVFrisbeeGetToolBar("FeaturePack")`, and only when it does not
  exist yet create it with exactly that atom and the localized title.
- Never use the `panel::` atom namespace (it belongs to Power PDF itself), use
  `IconMode="4"` for large ribbon buttons (1 renders a small icon), and keep
  every UILayout language folder's NameAndTitle.xml atom set identical.
- Ship all 16 European language folders: ENU DEU FRA ITA ESP NLD PTB DAN FIN
  NOR SVE PLK CSY HUN RUS TRK.

The validation pipeline checks all of this and reports findings with hints.

Rules enforced by the server:

- `version` is SemVer and must be strictly higher than the latest submitted
  version of the same package id.
- `changelog` must not be empty; write what changed, admins review it.
- Every packaged .zxt must be a native Windows DLL for the right machine type
  (x64 = 0x8664, arm64 = 0xAA64); x64 is mandatory, arm64 optional.
- `sha256.<arch>` must match each packaged file (lowercase hex).
- `ribbonAtomNamespace` must be unique across the store.

## Submitting

Recommended loop:

1. Build and pack the .ppak.
2. `POST {{baseUrl}}/api/packages/validate` (dry run, nothing stored).
3. Fix every finding with severity "error"; each finding has a `hint` that
   tells you exactly what to change. Repeat until `data.passed` is true.
4. `POST {{baseUrl}}/api/packages` to submit for real.

Upload either as raw body or as multipart:

    curl -X POST -H "Authorization: Bearer ppak_..." \
         -H "Content-Type: application/zip" \
         --data-binary @my-plugin.ppak \
         {{baseUrl}}/api/packages/validate

Responses always use the same envelope:

    { "ok": bool, "error": {"code","message","hint"} | null,
      "findings": [ {"code","severity","message","hint"}, ... ], "data": {...} }

On success (201) the version enters the **beta channel**: visible to Power PDF
clients that enabled the beta option, while an admin reviews it for the live
store. You are notified by email on approval or rejection; the rejection
comment is also visible via `GET /api/packages/{id}`.

Submitting the same version again returns 409 VERSION_EXISTS. Withdraw one of
your own beta versions with `DELETE /api/packages/{id}/{version}`.

## Reading the catalog (what the Power PDF client does)

    GET {{baseUrl}}/api/catalog              released (live) packages
    GET {{baseUrl}}/api/catalog?channel=beta ...including newer beta versions
    GET {{baseUrl}}/api/packages/{id}/{version}/download

Verify the download against the catalog's `sha256` before installing.

## Size limit

Packages up to 200 MB. Larger payloads should be fetched at install time by
the plugin itself, not bundled.

## Good citizenship

- Never put real credentials, API keys or customer data into a package.
- Use only MIT/BSD/Apache-2.0 licensed third-party code.
- Test on both x64 and Windows-on-ARM when you can; the store enforces that
  both binaries exist, not that they work.
""";

    /// <summary>
    /// A Claude Code skill (SKILL.md) that points the agent at this instance.
    /// Install: save as ~/.claude/skills/powerpdf-plugin-store/SKILL.md.
    /// Deliberately thin: the authoritative rules live in /api/agent-guide, so
    /// the skill never goes stale when the rules change.
    /// </summary>
    public static string SkillMarkdown(string baseUrl) => $$"""
---
name: powerpdf-plugin-store
description: Build, package, validate and publish Tungsten Power PDF plugins (.zxt, Plugin SDK) to the team Plugin-Store at {{baseUrl}}. Use whenever the user develops a Power PDF plugin, asks to package it as .ppak, upload or update it in the Plugin-Store, or fix store validation findings.
---

# Power PDF Plugin-Store

Store: {{baseUrl}}

1. Before packaging or uploading, fetch and follow the current rules:
   `GET {{baseUrl}}/api/agent-guide` (markdown) and
   `GET {{baseUrl}}/api/schema/manifest` (manifest.json schema).
   They are authoritative and may change; never rely on memory.
2. The personal API token is in the environment variable `PPAK_TOKEN`.
   Never print it, never write it into files or commits. If it is missing,
   ask the user to create one on {{baseUrl}}/Profile and set the variable.
3. Verify the connection with `GET {{baseUrl}}/api/me`.
4. Build the plugin (Release, x64), create the .ppak, then loop:
   `POST {{baseUrl}}/api/packages/validate` until `data.passed` is true,
   fixing every finding with severity "error" using its `hint`.
5. Submit with `POST {{baseUrl}}/api/packages` and report the resulting
   status (beta, awaiting admin review) to the user.

Key rules (details in the guide): every upload carries a new, higher SemVer
version; description and changelog in all 16 European languages; plugins
live on the shared "Enhanced Features" ribbon tab (toolbar atom
`FeaturePack`, own group `FeaturePack::<Name>`); only MIT/BSD/Apache-2.0
third-party code.
""";

    public const string ManifestSchema = """
{
  "$schema": "https://json-schema.org/draft/2020-12/schema",
  "title": "PluginStore-PowerPDF package manifest (manifest.json)",
  "type": "object",
  "required": ["id", "version", "name", "changelog", "architectures", "files", "sha256"],
  "properties": {
    "id": {
      "type": "string",
      "pattern": "^[a-z0-9][a-z0-9-]*(\\.[a-z0-9][a-z0-9-]*)+$",
      "description": "Reverse-DNS package id, lowercase, e.g. com.tungsten.myplugin. Permanent; owned by the first submitting user."
    },
    "version": {
      "type": "string",
      "pattern": "^\\d+\\.\\d+\\.\\d+$",
      "description": "SemVer. Every upload must be strictly higher than the latest submitted version."
    },
    "name": {
      "type": "object",
      "minProperties": 1,
      "additionalProperties": { "type": "string" },
      "description": "Display name per language code, e.g. {\"en\": \"...\", \"de\": \"...\"}. Provide all European Power PDF languages where possible (en de fr it es nl pt da fi no sv pl cs hu ru tr)."
    },
    "description": {
      "type": "object",
      "additionalProperties": { "type": "string" },
      "description": "Short catalog description per language code."
    },
    "changelog": {
      "type": ["object", "string"],
      "description": "What changed in THIS version; required and reviewed by admins. Object keyed by language code, or a plain string."
    },
    "minPowerPdfVersion": {
      "type": "string",
      "description": "Lowest Power PDF version the plugin supports, e.g. \"5.0\"."
    },
    "author": {
      "type": "string",
      "description": "Optional author shown in the catalog (person or team). Defaults to the publishing account's display name."
    },
    "contactEmail": {
      "type": "string",
      "description": "Optional contact address shown in the catalog. Defaults to the publishing account's email."
    },
    "category": {
      "enum": ["conversion", "forms", "signing", "navigation", "printing", "productivity", "system", "other"],
      "description": "Catalog filter category."
    },
    "architectures": {
      "type": "array",
      "items": { "enum": ["x64", "arm64"] },
      "minItems": 1,
      "description": "x64 is mandatory and covers every machine (Power PDF on Windows-on-ARM runs as ARM64EC and loads x64 plugins); a native arm64 build is optional."
    },
    "files": {
      "type": "object",
      "required": ["x64"],
      "properties": {
        "x64": { "type": "string", "description": "ZIP path of the x64 .zxt, e.g. x64/MyPlugin.zxt" },
        "arm64": { "type": "string", "description": "ZIP path of the optional native arm64 .zxt (same base name as x64)" }
      }
    },
    "sha256": {
      "type": "object",
      "required": ["x64"],
      "properties": {
        "x64": { "type": "string", "pattern": "^[a-f0-9]{64}$" },
        "arm64": { "type": "string", "pattern": "^[a-f0-9]{64}$" }
      },
      "description": "Lowercase hex SHA-256 of each packaged .zxt."
    },
    "ribbonAtomNamespace": {
      "type": "string",
      "description": "The ribbon atom namespace the plugin registers; must be unique across the store (the host caches ribbon layouts by atom name)."
    },
    "uninstall": {
      "type": "object",
      "properties": {
        "registryKeys": { "type": "array", "items": { "type": "string" }, "description": "HKCU/HKLM keys the plugin creates, for complete removal." },
        "extraPaths": { "type": "array", "items": { "type": "string" }, "description": "Additional files/folders the plugin creates at runtime." }
      },
      "description": "Everything the store client must remove for a clean uninstall."
    }
  }
}
""";
}
