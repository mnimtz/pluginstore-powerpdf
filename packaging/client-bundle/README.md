# Store client bundled with the server

This folder holds the newest Add-on Store client package (`com.tungsten.pluginstore-<version>.ppak`) and its source ZIP. The container image copies it to `/app/client-bundle` and sets `ClientBundle__Path`. When a server knows no client version yet, or only older ones, it releases the bundled one at start through the normal upload path (same checks, client lane, source stored, audit entry `client.bundle.imported`). A fresh installation offers the client installer as soon as the first administrator exists.

Update it at every client release with `python tools/bundle_client.py <version>` (after `make_ppak.py` and `make_sources.py`) and commit it with the `C1.x.y:` commit. Only one version is kept here.

A version that exists on the server in any status, also one withdrawn on purpose, is never taken again. Leave `ClientBundle__Path` empty to switch the import off.
