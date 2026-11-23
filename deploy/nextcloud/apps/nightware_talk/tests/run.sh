#!/usr/bin/env bash
# Runs the app's plain-PHP tests with the PHP of the nextcloud image (no Nextcloud bootstrap):
#   deploy/nextcloud/apps/nightware_talk/tests/run.sh
# With php on the PATH (NIGHTWARE_TALK_PHP=local), on the host instead.
set -euo pipefail
here="$(cd "$(dirname "$0")/.." && pwd)"
image="${NIGHTWARE_TALK_PHP_IMAGE:-nextcloud:35.0.3-apache}"

if [[ "${NIGHTWARE_TALK_PHP:-docker}" == local ]]; then
  exec php "$here/tests/RecordingPathTest.php"
fi
exec docker run --rm -v "$here:/app:ro" --entrypoint php "$image" /app/tests/RecordingPathTest.php
