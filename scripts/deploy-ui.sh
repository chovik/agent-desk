#!/usr/bin/env bash
# Deploy only the mocked UI. Does not install or connect AX/Substrate.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
if [[ "${1:-}" == --render && $# == 1 ]]; then
  exec kubectl kustomize "$ROOT"
fi
if [[ $# != 1 || -z "$1" || "$1" == --* ]]; then
  printf 'Usage: %s KUBE_CONTEXT\n       %s --render\n' "$0" "$0" >&2
  exit 2
fi
CONTEXT="$1"
kubectl config get-contexts "$CONTEXT" -o name | grep -Fxq -- "$CONTEXT" || {
  printf 'Unknown context: %s\n' "$CONTEXT" >&2
  exit 1
}
printf 'Deploying the MOCK UI to context %s, namespace agent-desk-demo.\n' "$CONTEXT"
kubectl --context "$CONTEXT" apply -k "$ROOT"
kubectl --context "$CONTEXT" -n agent-desk-demo rollout status deployment/agent-desk-ui --timeout=180s
printf '\nOpen a local tunnel (keep this command running):\n'
printf 'kubectl --context %q -n agent-desk-demo port-forward --address 127.0.0.1 svc/agent-desk-ui 8080:8080\n' "$CONTEXT"
printf '\nThen open http://127.0.0.1:8080 on the machine running port-forward.\n'
