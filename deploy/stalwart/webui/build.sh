#!/usr/bin/env bash
# Builds Stalwart's web UI (github.com/stalwartlabs/webui, AGPL-3.0) at a pinned
# commit with this directory's patches applied, into webui.zip next to this
# script, which Stalwart serves instead of the upstream release (its
# Application resourceUrl, set by ../configure.sh, file:///opt/webui/webui.zip).
# Rebuilds only when the commit or a patch changed (webui.zip.stamp). Needs
# curl, tar, patch, python3 and Docker; npm runs in a pinned Node container.
#
# To move to a newer upstream: set WEBUI_REF to its commit, check each patch
# still applies (./build.sh fails if not), and rebuild.
set -euo pipefail
cd "$(dirname "$0")"

WEBUI_REF=6b8ad61b57fd7450bef1e061c7186d8c52c13346  # v2.0.6
NODE_IMAGE=node:22.23.3-bookworm-slim

shopt -s nullglob
patches=(patches/*.patch)
stamp=$( { echo "$WEBUI_REF $NODE_IMAGE"; cat "${patches[@]}" /dev/null; } | openssl dgst -sha256 | awk '{print $NF}')
if [[ -f webui.zip && "$(cat webui.zip.stamp 2>/dev/null)" == "$stamp" ]]; then
  echo "webui: webui.zip is current (${WEBUI_REF:0:7}, ${#patches[@]} patch(es))"
  exit 0
fi

rm -rf .build && mkdir .build
curl -fsSL "https://codeload.github.com/stalwartlabs/webui/tar.gz/${WEBUI_REF}" | tar -xz -C .build --strip-components=1
for p in "${patches[@]}"; do
  echo "webui: applying ${p}"
  patch -d .build -p1 --forward --silent < "$p"
done

# As the invoking user, so .build stays removable without root.
docker run --rm --user "$(id -u):$(id -g)" -e HOME=/tmp -e npm_config_cache=/tmp/.npm \
  -v "$PWD/.build:/src" -w /src "$NODE_IMAGE" sh -c 'npm ci --no-audit --no-fund --loglevel=error && npm run build'

python3 - <<'PY'
import os, zipfile
with zipfile.ZipFile("webui.zip.tmp", "w", zipfile.ZIP_DEFLATED) as z:
    for root, _, files in os.walk(".build/dist"):
        for name in sorted(files):
            path = os.path.join(root, name)
            z.write(path, os.path.relpath(path, ".build/dist"))
PY
mv webui.zip.tmp webui.zip
echo "$stamp" > webui.zip.stamp
rm -rf .build
echo "webui: built webui.zip (${WEBUI_REF:0:7}, ${#patches[@]} patch(es))"
