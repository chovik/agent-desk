#!/usr/bin/env bash
# Explicitly builds and pushes TWO application images; never changes Kubernetes.
set -euo pipefail
[[ ${1:-} == --push && $# == 1 ]] || { echo "Usage: IMAGE_REPOSITORY=... AX_RUNNER_IMAGE=... $0 --push" >&2; exit 2; }
cd "$(dirname "$0")/.."
: "${IMAGE_REPOSITORY:?Set a registry/project you control}"
: "${AX_RUNNER_IMAGE:?Set the matching AX task-runner image digest}"
[[ "$AX_RUNNER_IMAGE" =~ ^[a-zA-Z0-9._:/-]+@sha256:[a-f0-9]{64}$ ]] || { echo 'AX_RUNNER_IMAGE must be digest-pinned.' >&2; exit 2; }
[[ "$IMAGE_REPOSITORY" =~ ^[a-zA-Z0-9._:/-]+$ && "$IMAGE_REPOSITORY" != *example* ]] || { echo 'Set a real image repository.' >&2; exit 2; }
[[ -z $(git status --porcelain) ]] || { echo 'Commit/review source before building a labelled image.' >&2; exit 1; }
[[ -d .local/build/publish && -f .local/build/codex ]] || { echo 'Run scripts/prepare-images.sh first.' >&2; exit 1; }
REVISION=$(git rev-parse HEAD)
[[ "$(cat .local/build/revision)" == "$REVISION" ]] || { echo "Build context is stale; prepare it again from this commit." >&2; exit 1; }
PORTAL_TAG="$IMAGE_REPOSITORY/agent-desk-portal:${REVISION:0:12}"
CODEX_TAG="$IMAGE_REPOSITORY/agent-desk-codex:${REVISION:0:12}"
docker build --platform linux/amd64 -f deploy/portal/CodexRunner.Dockerfile \
  --build-arg "AX_RUNNER_IMAGE=$AX_RUNNER_IMAGE" --build-arg "REVISION=$REVISION" -t "$CODEX_TAG" .local/build
docker build --platform linux/amd64 -f deploy/portal/Dockerfile \
  --build-arg "REVISION=$REVISION" -t "$PORTAL_TAG" .local/build
docker push "$CODEX_TAG"
docker push "$PORTAL_TAG"
python3 - "$PORTAL_TAG" "$CODEX_TAG" "$AX_RUNNER_IMAGE" <<'PY'
import json, pathlib, subprocess, sys
def digest(tag):
    refs=json.loads(subprocess.check_output(['docker','inspect',tag]))[0]['RepoDigests']
    prefix=tag.rsplit(':',1)[0]+'@'
    return next(r for r in refs if r.startswith(prefix))
values={'PORTAL_IMAGE':digest(sys.argv[1]),'CODEX_RUNNER_IMAGE':digest(sys.argv[2]),'RUNNER_IMAGE':sys.argv[3]}
p=pathlib.Path('.local/images.json');p.write_text(json.dumps(values,indent=2)+'\n')
print('Image references written to .local/images.json; no deployment performed.')
PY

