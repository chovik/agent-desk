#!/usr/bin/env bash
# Read-only checks. Never reads Secret objects or changes cluster configuration.
set -euo pipefail
if [[ $# != 1 || -z "$1" || "$1" == --* ]]; then
  printf 'Usage: %s KUBE_CONTEXT\n' "$0" >&2
  exit 2
fi
command -v kubectl >/dev/null
command -v python3 >/dev/null
CONTEXT="$1"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT
k() { kubectl --context "$CONTEXT" --request-timeout=20s "$@"; }
printf 'Read-only report for context: %s\n' "$CONTEXT"
printf 'Local shell host (may differ from cluster nodes):\n'
uname -srm
printf '\nServer version:\n'
k version -o json > "$TMP/version.json"
python3 -c 'import json,sys; print(json.load(open(sys.argv[1]))["serverVersion"]["gitVersion"])' "$TMP/version.json"
k get nodes -o json > "$TMP/nodes.json"
k get storageclasses -o json > "$TMP/storage.json"
if ! k get --raw /apis/certificates.k8s.io/v1beta1 > "$TMP/certificates.json"; then
  printf '{"resources":[]}' > "$TMP/certificates.json"
fi
python3 - "$TMP" <<'PY'
import json, pathlib, re, sys
p = pathlib.Path(sys.argv[1])
nodes = json.loads((p/'nodes.json').read_text())['items']
issues = []
print('\nNodes (checks apply to intended worker nodes):')
for n in nodes:
    info = n['status']['nodeInfo']
    alloc = n['status']['allocatable']
    ready = any(c['type']=='Ready' and c['status']=='True' for c in n['status'].get('conditions', []))
    print(f"  {n['metadata']['name']}: {info['operatingSystem']}/{info['architecture']}, kernel={info['kernelVersion']}, ready={ready}, cpu={alloc['cpu']}, memory={alloc['memory']}")
    if info['operatingSystem'] != 'linux' or info['architecture'] != 'amd64':
        issues.append(f"{n['metadata']['name']}: pinned AX guide targets Linux/amd64; exclude this node or validate another build.")
    v = re.match(r'(\d+)\.(\d+)', info['kernelVersion'])
    if v and tuple(map(int, v.groups())) < (6, 8):
        issues.append(f"{n['metadata']['name']}: kernel below 6.8; verify an OverlayFS lowerdir+ backport or use a suitable kernel.")
    if not ready:
        issues.append(f"{n['metadata']['name']}: node is not Ready.")
resources = {r['name'] for r in json.loads((p/'certificates.json').read_text()).get('resources', [])}
print('\nRequired certificate APIs for the pinned platform:')
for name in ['clustertrustbundles', 'podcertificaterequests']:
    present = name in resources
    print(f"  certificates.k8s.io/v1beta1/{name}: {'present' if present else 'MISSING or unreadable'}")
    if not present:
        issues.append(f"Enable/verify the v1beta1 {name} API on a compatible K3s version.")
classes = json.loads((p/'storage.json').read_text()).get('items', [])
defaults = [c['metadata']['name'] for c in classes if c['metadata'].get('annotations', {}).get('storageclass.kubernetes.io/is-default-class') == 'true']
print('\nDefault StorageClasses:', ', '.join(defaults) or '(none)')
if not defaults:
    issues.append('Configure a default persistent StorageClass or explicit storage for PostgreSQL and snapshot objects.')
print('\nManual checks still required:')
print('  - Kubelet certificate projection feature gates on every worker.')
print('  - Correct kubelet plugin/device-plugin host paths for this K3s setup.')
print('  - NetworkPolicy enforcement, registry access, storage permissions, and allowed host-level capabilities.')
print('  - Native-K3s Substrate storage/installer adaptation; this script does not install it.')
if issues:
    print('\nBLOCKERS / ITEMS TO RESOLVE:')
    print('\n'.join('  - '+x for x in issues))
    sys.exit(1)
print('\nBasic checks passed. This is NOT a certification or an end-to-end sandbox test.')
PY
