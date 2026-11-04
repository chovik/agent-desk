# AX and Substrate on k3s

## Compatibility, not a blanket support claim

K3s is a Kubernetes distribution. What matters here is the Kubernetes API version,
certificate projections, Linux kernel/storage, networking and permissions—not
just the distribution name.

| Baseline | Version / status |
|---|---|
| AX CLI, server, runner and schema | `a496fc1ff1cf9bea4c34924348e4119172cfea5b` |
| Substrate | `d3f2363590a11b2f50489971985ba033b4c9eff7` (`v0.6.0`) |
| Tested cluster | Isolated k3s `v1.36.5+k3s1`, Linux amd64, Ubuntu 24.04 / kernel 6.8 |
| Tested runtime | gVisor; execution, Substrate snapshot continuity and AX workspace restore |
| Build toolchain used | Go 1.27.3; .NET 10 for the portal |
| Not established | Official upstream k3s certification; clean native/bare-metal installation; multi-node HA, disaster recovery, microVM/KVM or hostile multi-tenancy |

Use the [source pins](../deploy/platform/versions.env), not unrelated `latest`
CLIs/images. Upstream APIs are evolving. This public distribution has not been
reinstalled from scratch on a fresh machine; [verification scope](VERIFICATION.md)
separates original stack tests from public-packaging tests.

**Use a dedicated disposable VM/node for the first experiment.** This guide does
not create a second cluster on a shared production host, change sysctls or restart
an existing service. Never run upstream E2E reset/cleanup scripts against data you
want to retain. Terraform is optional for VM/disk/network provisioning; it is not
required to install applications on an existing cluster.

## 1. Prerequisites

- Linux/amd64; OverlayFS `lowerdir+` support (upstream kernel 6.8+ or a verified backport).
- Compatible Kubernetes certificate APIs: ClusterTrustBundle and PodCertificateRequest,
  plus kubelet ClusterTrustBundleProjection support on every worker.
- StorageClass for AX PostgreSQL, Substrate PostgreSQL, snapshot storage and portal PVC.
  Local-path storage is acceptable for a disposable single-node demo, **not HA**.
- NetworkPolicy-enforcing CNI; working DNS and node/pod access to your registry.
- Host-level permissions and correct kubelet/plugin paths for Substrate node services.
- Git, Make, Docker, Go compatible with the pins, OpenSSL, Python 3 and `kubectl`.
  Portal builds additionally need .NET 10 SDK and npm/Node.js.

**Planning estimate, not a minimum:** start around 8 vCPU / 16 GiB RAM and 100 GiB
free SSD space, allowing extra room for source builds, images and snapshots. A
small running sandbox does not imply a small full build/install footprint. Measure
node resources and both root/data filesystems; PVC requests do not necessarily
enforce a host disk quota. Do not copy a tightly constrained host's emergency limits.

The initial runtime is gVisor; KVM/nested virtualization is not required for it.

## 2. Select the cluster explicitly

```bash
export KUBECONFIG=/absolute/path/to/your/dedicated-kubeconfig
export CONTEXT=your-dedicated-k3s-context
kubectl --context "$CONTEXT" config view --minify
./scripts/preflight-k3s.sh "$CONTEXT"
```

The preflight is read-only: version, nodes, certificate API discovery, storage.
It does not read Secrets, test mount propagation, guarantee registry access or
prove that a sandbox can start.

For the tested Kubernetes 1.36 baseline, review
[`feature-gates.example.yaml`](../deploy/k3s/feature-gates.example.yaml) and merge
the applicable arguments into your dedicated node's k3s configuration. Do not
overwrite existing arguments. Agent-only nodes need kubelet settings too.
Changing these settings requires a planned k3s/k3s-agent restart and affects
workloads. An older binary cannot gain missing APIs merely by adding flags.

References: [K3s configuration](https://docs.k3s.io/installation/configuration),
[K3s server arguments](https://docs.k3s.io/cli/server),
[Kubernetes 1.36 projections](https://v1-36.docs.kubernetes.io/docs/concepts/storage/projected-volumes/).

## 3. Fetch and build pinned sources

```bash
./scripts/fetch-platform-sources.sh   # source downloads only; no cluster changes
export ROOT="$PWD"
mkdir -p .platform/bin .local/rendered
(
  cd .platform/substrate
  go build -p 2 -o ../bin/ate-setup ./cmd/ate-setup
  go build -p 2 -o ../bin/kubectl-ate ./cmd/kubectl-ate
)
(
  cd .platform/ax
  make build
)
```

Use a registry name reachable from the build host, Kubernetes containerd **and
Substrate's own image cache**. `localhost` and Docker network aliases are not
portable registry names. Authentication/trust must be configured for each image
consumer, not just `docker login` on your laptop. Use a TLS registry; do not expose
an unauthenticated writable registry on the internet.

## 4. Install Substrate — distribution-specific adaptation

Read the pinned [operator guide](https://github.com/agent-substrate/substrate/blob/d3f2363590a11b2f50489971985ba033b4c9eff7/docs/operator-install.md)
and [installer reference](https://github.com/agent-substrate/substrate/blob/d3f2363590a11b2f50489971985ba033b4c9eff7/cmd/ate-setup/commands.md).
At these pins, the installer has a Kind/local profile and a GCP-oriented profile;
it is **not a turnkey native-k3s installer**.

The working experimental setup adapted the **local/S3 manifest profile** selected
by `--kind` to an already-created k3s cluster. This flag chooses manifests; it
does not make k3s into Kind. Do not use it merely to bypass storage validation.
Before using that profile on a dedicated demo cluster, review and adapt:

1. **Storage:** the local profile deploys RustFS and a bucket-init Job. Its
   development credentials must be replaced with your own Secret references in
   RustFS, the init Job and both API/node object-store plugins. Keep endpoint,
   path-style mode, bucket and credentials consistent. Review PVC capacities.
2. **Node paths:** verify kubelet plugin/device-plugin directories against your
   k3s `data-dir`. `/var/lib/ate` must be backed by a suitable disk filesystem with
   correct shared/slave mount propagation. Do not use a container OverlayFS upperdir.
3. **Registry:** the local atelet overlay has a Kind-specific localhost-registry
   replacement. Replace/remove that assumption for your real registry. Containerd
   pull success alone does not prove atelet can resolve/pull the same image.
4. **Identity:** enable the signers/trust bundles, authorize the AX service account
   and allow its SPIFFE identity at the router. Bootstrap-owner grants below are
   for a trusted disposable demo, not least-privilege production authorization.
5. **Network/security:** keep all control planes, database, object store and debug
   ports private. Check default deny policies and projected certificate issuance.

The following records the **installer shape used by the experiment**, with
machine-specific values removed. It assumes those adaptations are complete:

```bash
export IMAGE_REPOSITORY=registry.example.com/your-project  # replace
(
  cd "$ROOT/.platform/substrate"
  ../bin/ate-setup deploy ate-system --no-dev-env --kind \
    --context "$CONTEXT" --kubeconfig "$KUBECONFIG" \
    --ko-docker-repo "$IMAGE_REPOSITORY" --ko-default-platforms linux/amd64 \
    --rollout-timeout 5m --record-dir "$ROOT/.local/substrate-install" \
    --credential-provider '{"enabled":false}' \
    --ateapi-authz-bootstrap-owners 'system:serviceaccount:ate-system:ate-client,system:serviceaccount:ax-system:ax-server' \
    --ingress-allowed-spiffe-ids 'spiffe://cluster.local/ns/ate-system/sa/ate-client,spiffe://cluster.local/ns/ax-system/sa/ax-server'
)
```

Disabling the image credential-provider assumes images are otherwise readable
from that isolated environment. A private registry needs a reviewed node-side
credential configuration; portal GitHub credentials are unrelated. Keep install
records private. Re-running the installer can overwrite manual manifest patches.

Verify `ate-api-server`, controller, certificate controller, atelet, router,
database and object store are healthy. Check issued trust bundles and
`gvisor-default` SandboxConfig. Then run the upstream authenticated
[counter demo](https://github.com/agent-substrate/substrate/tree/d3f2363590a11b2f50489971985ba033b4c9eff7/demos)
and verify application state across **synchronous suspend/resume**. Use the
configured TLS service/CA/identity; never disable certificate verification to
make a smoke test pass.

## 5. Build and install AX

Build matching server and runner images. This pushes only to your chosen registry:

```bash
(
  cd "$ROOT/.platform/ax"
  make images AX_IMAGE_REPO="$IMAGE_REPOSITORY" IMAGE_TAG=demo IMAGE_ARCH=amd64
)
export AX_SERVER_IMAGE="$IMAGE_REPOSITORY/ax-server@$(cat .platform/ax/bin/ax-server.digest)"
export AX_RUNNER_IMAGE="$IMAGE_REPOSITORY/ax-task-runner@$(cat .platform/ax/bin/ax-task-runner.digest)"
./scripts/render-ax.py --server-image "$AX_SERVER_IMAGE" \
  --runner-image "$AX_RUNNER_IMAGE" > .local/rendered/ax.yaml
```

Review the generated file. It uses upstream PostgreSQL and the pinned E2E server
template with explicit identity settings; it does not create a password. The
upstream AX role can read cluster Secrets: this is a significant trust boundary,
not a reason to put it in a production cluster.

```bash
kubectl --context "$CONTEXT" create namespace ax-system --dry-run=client -o yaml | \
  kubectl --context "$CONTEXT" apply -f -
kubectl --context "$CONTEXT" label namespace ax-system ate.dev/api-access=true --overwrite

# FIRST INSTALL ONLY. If the Secret/PVC already exists, recover it, never rotate blindly.
umask 077
openssl rand -hex 24 | tr -d '\n' > .local/ax-postgres-password
kubectl --context "$CONTEXT" -n ax-system create secret generic ax-postgres \
  --from-file=password=.local/ax-postgres-password
kubectl --context "$CONTEXT" apply -f .local/rendered/ax.yaml
```

Protect AX immediately with an ingress-deny policy; the portal later adds a narrow
allow rule. This API has no application authentication at the pinned version.

```bash
kubectl --context "$CONTEXT" apply -f deploy/platform/ax-private.yaml
```

Add a gVisor WorkerPool using [the example](../examples/workerpool.example.yaml).
Set `workerImage` to the digest from the **same Substrate build**, not a `ko://`
reference. If you need to resolve/build it using Substrate's pinned tooling:

```bash
sed 's|REPLACE_WITH_PINNED_ATEWORKER_GVISOR_IMAGE|ko://go.ate.dev/substrate/cmd/ateworker-gvisor|' \
  examples/workerpool.example.yaml > .local/rendered/workerpool-source.yaml
(
  cd .platform/substrate
  KO_DOCKER_REPO="$IMAGE_REPOSITORY" ./hack/run-tool.sh ko resolve --platform=linux/amd64 \
    -f "$ROOT/.local/rendered/workerpool-source.yaml"
) > .local/rendered/workerpool.yaml
kubectl --context "$CONTEXT" apply -f .local/rendered/workerpool.yaml
kubectl --context "$CONTEXT" -n ax-system rollout status statefulset/ax-postgres --timeout=180s
kubectl --context "$CONTEXT" -n ax-system rollout status deployment/ax-server --timeout=180s
kubectl --context "$CONTEXT" -n ax-system get workerpools,pods
```

A ready server without a ready worker is not execution capacity. Size the worker
for sandbox limits **plus platform/golden-template overhead**, not just the one
visible agent. `AX_SNAPSHOTS_BUCKET` is not a substitute for Substrate's actual
snapshot-location/object-store configuration. The `gs://ate-snapshots/ax/` value
in the rendered demo reflects the legacy AX setting, not a new cloud account.

## 6. Prove workspace restoration before adding AI

Using the CLI from the same source checkout:

```bash
AX_BIN="$ROOT/.platform/ax/bin/ax"
"$AX_BIN" --context "$CONTEXT" -n ax-system apply -f examples/ax-smoke.yaml
"$AX_BIN" --context "$CONTEXT" -n ax-system resume task agent-desk-smoke
"$AX_BIN" --context "$CONTEXT" -n ax-system describe task agent-desk-smoke
# Wait for Running / Ready=True before continuing.
"$AX_BIN" --context "$CONTEXT" -n ax-system ssh agent-desk-smoke -- \
  sh -c 'printf checkpoint-ok > /workspace/proof.txt'
"$AX_BIN" --context "$CONTEXT" -n ax-system suspend task agent-desk-smoke
"$AX_BIN" --context "$CONTEXT" -n ax-system resume task agent-desk-smoke
"$AX_BIN" --context "$CONTEXT" -n ax-system ssh agent-desk-smoke -- cat /workspace/proof.txt
```

Expected: `checkpoint-ok`. This tests workspace persistence, not process-memory
checkpointing or an AI conversation. After inspection, delete **only this fixture**:

```bash
"$AX_BIN" --context "$CONTEXT" -n ax-system delete task agent-desk-smoke
"$AX_BIN" --context "$CONTEXT" -n ax-system delete workspace agent-desk-smoke-workspace
```

Only then continue to [portal deployment](DEPLOYMENT.md). If anything fails, use
the [troubleshooting guide](TROUBLESHOOTING.md); do not weaken TLS, credential
isolation or network policy to hide a failure.
