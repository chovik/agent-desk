#!/usr/bin/env bash
# Fetch sources only. Does not install software, change K3s, or deploy a cluster.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
source "$ROOT/deploy/platform/versions.env"
mkdir -p "$ROOT/.platform"
fetch() {
  local name="$1" url="$2" ref="$3" dir="$ROOT/.platform/$1"
  if [[ -e "$dir" ]]; then
    [[ -d "$dir/.git" ]] || { printf '%s exists but is not a Git checkout.\n' "$dir" >&2; exit 1; }
    [[ "$(git -C "$dir" rev-parse HEAD)" == "$ref" ]] || {
      printf '%s is at another revision. Refusing to modify it.\n' "$dir" >&2
      exit 1
    }
    printf 'Keeping existing %s at %s. Local changes, if any, are preserved.\n' "$name" "$ref"
    return
  fi
  git clone --filter=blob:none --no-checkout "$url" "$dir"
  git -C "$dir" fetch origin "$ref"
  git -C "$dir" checkout --detach "$ref"
  [[ "$(git -C "$dir" rev-parse HEAD)" == "$ref" ]]
}
fetch ax https://github.com/google/ax.git "$AX_REF"
fetch substrate https://github.com/agent-substrate/substrate.git "$SUBSTRATE_REF"
printf '\nSources are ready under %s/.platform. Nothing was deployed.\n' "$ROOT"
