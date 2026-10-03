# Releasing

## Version naming on GitHub

Server and client have separate version numbers with a visible prefix:
`S<version>` for the server (`VERSION`), `C<version>` for the client
(`client/common/version.h`).

- A release commit's title starts with the version(s) it ships, e.g.
  `S0.5.0: …`, `C0.3.4: …` or `S0.5.0 + C0.3.4: …`. The Actions run list shows
  the commit title, so every build is identifiable.
- The `tag-releases` job in `container.yml` runs after a successful build on
  `main` and creates the Git tag plus a GitHub Release for every S/C version
  that has none yet (`tools/release_notes.py` writes the notes). The container
  image is additionally tagged `S<version>`.

## Server

1. Bump `VERSION` (every release, never ship an unchanged number).
2. Commit and push to `main`. GitHub Actions builds the container and pushes
   `ghcr.io/mnimtz/pluginstore-powerpdf:latest` (plus `sha-<commit>`).
3. The App Service picks up `latest`; verify with
   `GET https://addon.power-pdf.de/api/ping` (reports `VERSION`).
4. Run `python tools/check_docs.py` (also enforced in CI: the build fails if a
   finding code is not documented in the agent guide's rule reference).
5. Update the agent/API documentation in the same release for EVERY change a
   plugin developer or their Claude session needs to know (rules, manifest
   fields, limits, lifecycle, install behaviour), not only API changes: `Api/AgentGuide.cs`
   (guide, manifest schema, SKILL.md), the `/api` endpoint list, the API page
   (`Pages/Developer.cshtml`, all 16 languages) and `README.md`.

## Add-on Store client (ribbon add-on)

The client is itself a store package (`com.tungsten.pluginstore`) with its
own lane: only admins may upload it, a new version is **live immediately**
(no review queue), older client betas are superseded. The landing page
serves the MSI inside the newest live client package at
`/download/pluginstore.msi`; installed clients show an update hint in the
store dialog and run that MSI.

Steps (Windows, Visual Studio 2022, WiX v3 in `C:\Claude\Tools\wix314`):

1. Bump the version in `client/common/version.h` and the `VERSIONINFO` block
   in `client/res/PluginStore.rc`.
2. Write the changelog for **all 16 languages** into
   `packaging/pluginstore.ppakspec.json` (`changelog`); the server rejects
   uploads with missing languages.
3. Build:
   ```bat
   client\build.cmd
   client\installer\build_msi.cmd
   ```
   `build_msi.cmd` produces `PluginStore-<version>.msi` and the stable copy
   `PluginStore.msi` that the package spec references.
4. Package:
   ```bat
   python tools\make_ppak.py packaging\pluginstore.ppakspec.json dist
   ```
   `make_ppak.py` aborts when the bundled MSI's ProductVersion differs from
   the package version (guard against shipping a stale installer).
5. Upload with an **admin** token:
   ```bash
   curl -X POST -H "Authorization: Bearer $PPAK_TOKEN" -H "Content-Type: application/zip" \
        --data-binary @dist/com.tungsten.pluginstore-<version>.ppak \
        https://addon.power-pdf.de/api/packages
   ```
   The response status is `live`.
6. Verify: the landing page button shows the new version, and the downloaded
   MSI reports the new ProductVersion.

If a broken client version went out, publish a fixed higher version and
withdraw the broken one (`DELETE /api/packages/com.tungsten.pluginstore/<version>`
with an admin token).

## Plugins

Plugins are submitted by their authors (or their Claude session) through the
API; see `/api/agent-guide`. Packaging helpers for our own plugins live in
`packaging/*.ppakspec.json` and `tools/make_ppak.py`.
