#!/usr/bin/env bash
# Prepare a fresh build context from source + the official pinned runtime only.
# No host Codex home, credentials, account settings or sessions are copied.
set -euo pipefail
umask 077
ROOT=$(cd "$(dirname "$0")/.." && pwd)
cd "$ROOT"
[[ -z $(git status --porcelain) ]] || { echo 'Prepare images from clean, committed source.' >&2; exit 1; }
OUT="$ROOT/.local/build"
[[ ! -e "$OUT" ]] || { echo 'Move/remove .local/build after reviewing it; refusing to reuse a stale context.' >&2; exit 1; }
mkdir -p "$OUT/download" "$OUT/publish"
git rev-parse HEAD > "$OUT/revision"
export DOTNET_PROCESSOR_COUNT=${DOTNET_PROCESSOR_COUNT:-2}
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet publish backend/AgentDesk.Portal -c Release -m:1 \
  -p:UseSharedCompilation=false -p:RestoreLockedMode=true -o "$OUT/publish" --verbosity minimal
# Download the platform package directly; do not install or run npm lifecycle scripts.
(cd "$OUT/download" && npm pack --ignore-scripts @openai/codex@0.165.0-linux-x64 --silent > package-name)
PACKAGE=$(cat "$OUT/download/package-name")
[[ "$PACKAGE" =~ ^[A-Za-z0-9._-]+\.tgz$ ]]
tar -xzf "$OUT/download/$PACKAGE" -C "$OUT/download" --no-same-owner
VENDOR="$OUT/download/package/vendor/x86_64-unknown-linux-musl"
mkdir -p "$OUT/codex-bundle"
while read -r hash relative; do
  [[ "$(sha256sum "$VENDOR/$relative" | cut -d' ' -f1)" == "$hash" ]] || { echo "Runtime checksum mismatch: $relative" >&2; exit 1; }
  install -D -m 755 "$VENDOR/$relative" "$OUT/codex-bundle/$relative"
done < third_party/codex/bundle.sha256
[[ "$("$OUT/codex-bundle/bin/codex" --version)" == 'codex-cli 0.165.0' ]]
cp "$OUT/codex-bundle/bin/codex" "$OUT/codex"
cp third_party/codex/LICENSE "$OUT/publish/CODEX-LICENSE"
cp -a dist guest "$OUT/"
# Delete only this invocation’s temporary package extraction.
rm -rf -- "$OUT/download"
[[ -z $(git status --porcelain) && $(git rev-parse HEAD) == "$(cat "$OUT/revision")" ]] || { echo 'Source changed during preparation; discard this context.' >&2; exit 1; }
touch "$OUT/complete"
printf 'Prepared %s. Nothing deployed or pushed.\n' "$OUT"
