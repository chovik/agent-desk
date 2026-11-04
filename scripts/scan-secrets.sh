#!/usr/bin/env bash
# Scan committed history and the committed source archive (not local credentials).
# Downloads a pinned scanner release from its official repository; verifies SHA256.
set -euo pipefail
cd "$(dirname "$0")/.."
[[ $(uname -sm) == 'Linux x86_64' ]] || { echo 'Use your platform’s gitleaks build for this check.' >&2; exit 2; }
mkdir -p .local/tools
VERSION=8.30.1
ARCHIVE="gitleaks_${VERSION}_linux_x64.tar.gz"
HASH=551f6fc83ea457d62a0d98237cbad105af8d557003051f41f3e7ca7b3f2470eb
curl --fail --silent --show-error --location \
  "https://github.com/gitleaks/gitleaks/releases/download/v$VERSION/$ARCHIVE" -o ".local/tools/$ARCHIVE"
(cd .local/tools && printf '%s  %s\n' "$HASH" "$ARCHIVE" | sha256sum -c - && tar -xzf "$ARCHIVE" gitleaks)
.local/tools/gitleaks git --redact --no-banner .
TMP=$(mktemp -d)
trap 'rm -rf -- "$TMP"' EXIT
git archive HEAD | tar -x -C "$TMP"
.local/tools/gitleaks dir --redact --no-banner "$TMP"
