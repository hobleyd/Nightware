# Stalwart's web admin, patched

Stalwart serves its web admin (`/admin`, `/account`) from a zip it downloads,
by default upstream's rolling `v2` release of
[stalwartlabs/webui](https://github.com/stalwartlabs/webui) (AGPL-3.0). This
directory builds that app at a pinned commit with the patches below, and
`../configure.sh` points Stalwart at the result
(`file:///opt/webui/webui.zip`; `docker-compose.yml` mounts this directory
there). `STALWART_WEBUI_PATCHED=false` goes back to upstream's release.

| Patch | Why |
|---|---|
| `patches/0001-login-hint.patch` | The sign-in page takes `?login_hint=<address>`, fills it in and goes straight on. Stalwart decides where an account signs in from the typed name (a bare username stays on its own password page), so the admin console's Stalwart link sends the admin's address and the Keycloak session signs them in without a prompt. Which server signs them in still comes from Stalwart's `/api/discover`. |

`build.sh` downloads the source at `WEBUI_REF`, applies the patches, builds
with `npm ci && npm run build` in a pinned Node container and zips `dist`. It
rebuilds only when the commit or a patch changes. The pin is the commit whose
unpatched build reproduces the bundle production served before (v2.0.6,
`index-DzuFj-KX.js`).

**Moving to a newer upstream:** set `WEBUI_REF` to its commit, run
`./build.sh` (it fails if a patch no longer applies; refresh the patch with
`diff -u` against the new source), then `../configure.sh`. Pinning means
Stalwart no longer updates the web admin by itself.
