# Deploy the connected portal

Prerequisite: a **dedicated, trusted demo cluster** that passed the
[AX/Substrate checks](K3S.md). This is not a replacement platform installer.
Nothing below requires another nested cluster, host watcher modifications or a
production service restart. All Kubernetes commands use your explicit context.

## 1. Build images from clean source

Use Linux/amd64 with .NET 10 SDK, npm/Node.js, Git, Python 3, Docker and sufficient
build/storage headroom. Check [the version pins](../deploy/platform/versions.env).
Do not mix a newer AX runner with the vendored contracts without integration tests.

```bash
./scripts/test.sh
./scripts/prepare-images.sh
export IMAGE_REPOSITORY=registry.example.com/your-project  # replace with your registry
export AX_RUNNER_IMAGE="$IMAGE_REPOSITORY/ax-task-runner@$(cat .platform/ax/bin/ax-task-runner.digest)"
./scripts/build-images.sh --push
```

`prepare-images.sh` publishes .NET and downloads the official pinned Codex platform
package with npm lifecycle scripts disabled. It verifies every runtime file
against `third_party/codex/bundle.sha256`, including the code-mode host companion.
It never copies host login/config/session files. Outputs are ignored under `.local/`.

The build helper pushes only the portal and Codex runner images and records their
digests in `.local/images.json`; it **does not deploy**. Use a fresh prepared context
after source changes; old contexts are refused. A registry must be readable by
containerd and atelet; configure private-image authentication separately if needed.
No prebuilt public application image is promised by this repository.

## 2. Create your own login and encryption key

```bash
./scripts/init-login.py --username operator
```

Enter a unique password at the private terminal prompt. The tool writes only a
password hash, random user ID and new 32-byte vault key under `.local/secrets/`
with owner-only permissions. It refuses to replace an existing directory. It does
not contact GitHub/OpenAI or import an existing account. No default password exists.

**First installation only:** verify the target namespace contains no retained
portal data/Secrets. For upgrades or migrations, recover the original users/key
instead of provisioning new ones. Changing a user ID breaks run ownership;
changing the vault key makes existing encrypted connections unreadable.

```bash
export KUBECONFIG=/absolute/path/to/your/dedicated-kubeconfig
export CONTEXT=your-dedicated-k3s-context
kubectl --context "$CONTEXT" -n ax-system get deployment ax-server

# `create`, not blind `apply`: existing Secrets produce an error, preventing silent rotation.
kubectl --context "$CONTEXT" -n ax-system create secret generic agent-desk-portal-login \
  --from-file=users.json=.local/secrets/users.json
kubectl --context "$CONTEXT" -n ax-system create secret generic agent-desk-codex-vault \
  --from-file=key=.local/secrets/vault.key
```

The deployment runs one replica with Recreate strategy, a persistent data volume,
read-only root filesystem, no Linux capabilities and no Kubernetes bearer token
mounted in the portal. For this trusted demo it reuses AX's authorized service
account **certificate identity**, not per-user cluster identities.

## 3. Render and inspect

Choose one mode. Start with loopback if you do not yet have TLS/ingress configured.

### A. SSH/loopback only

```bash
mkdir -p .local/rendered
./scripts/render-portal.py --loopback-http > .local/rendered/portal.yaml
```

This explicitly permits a non-Secure session cookie **only for your private
loopback tunnel**. Do not add public ingress, NodePort, `0.0.0.0` port-forward or
shared reverse proxy in this mode. GitHub App callbacks need the HTTPS mode below.

### B. HTTPS through an ingress controller

```bash
# Set your real host and the actual source network used by your ingress controller.
# This example CIDR is documentation-only; do NOT copy it as your cluster network.
./scripts/render-portal.py --origin https://agent-desk.example.com \
  --trusted-proxy-network 192.0.2.0/24 > .local/rendered/portal.yaml
```

The backend accepts forwarded HTTPS scheme only from loopback or your configured
CIDRs; `/0` trust-all networks are rejected. Determine the source address as seen
by the backend—pod networking/host networking varies. Keep the allowed range as
narrow as practical. NetworkPolicy must independently allow only the intended
ingress pods, and the controller must overwrite untrusted `X-Forwarded-Proto`.
The backend validates the configured host/origin and uses secure cookies.

The renderer is offline: it validates digest-pinned images and HTTPS settings,
substitutes the model (default `gpt-6.1-sol`; account availability is not guaranteed)
and embeds a hash of the CONNECT proxy script to trigger rollouts on changes.
It emits **no Secrets** and does not contact any cluster.

## 4. Apply the portal

Review `.local/rendered/portal.yaml`, storage class/PVC capacity and resource limits.
The fixed namespace is `ax-system`; using another namespace requires reviewing
service DNS names, certificate identities and NetworkPolicies together.

```bash
kubectl --context "$CONTEXT" -n ax-system create configmap agent-desk-codex-egress \
  --from-file=connect.py=deploy/portal/codex-connect-proxy.py --dry-run=client -o yaml | \
  kubectl --context "$CONTEXT" apply -f -
kubectl --context "$CONTEXT" apply -f .local/rendered/portal.yaml
kubectl --context "$CONTEXT" -n ax-system rollout status deployment/agent-desk-codex-egress --timeout=120s
kubectl --context "$CONTEXT" -n ax-system rollout status deployment/agent-desk-portal --timeout=180s
```

Default ingress is denied. Portal egress is restricted to DNS, AX, the mTLS router
and the internal CONNECT relay. The relay allows only specific provider/GitHub
TLS hosts and rejects private/metadata addresses. Guest command internet access
remains denied. Ensure your CNI actually enforces these rules.

For **loopback mode**, keep this process running:

```bash
kubectl --context "$CONTEXT" -n ax-system port-forward --address 127.0.0.1 \
  svc/agent-desk-portal 8080:8080
```

Open http://127.0.0.1:8080 on that machine. For a remote host, use your own SSH
tunnel rather than exposing the forwarding listener:

```bash
ssh -N -L 8080:127.0.0.1:8080 your-user@your-demo-host
```

## 5. Optional public HTTPS

Public **source** does not require a publicly reachable deployment. If you choose
one, first configure DNS, a valid TLS certificate and controller-side HTTP→HTTPS
redirect. Use [ingress.example.yaml](../deploy/portal/ingress.example.yaml) as an
unapplied template: replace host, TLS Secret, class and controller selectors.
It routes directly to the portal in the **same cluster**; no host bridge service
or production routing namespace is needed.

Before exposing it, verify:

- HTTPS-mode manifest applied; `AllowInsecureLabCookie=false`; real PublicOrigin.
- Correct trusted proxy source CIDR and narrow NetworkPolicy controller selectors.
- TLS chain valid, HTTP redirected, cookies Secure/HttpOnly/SameSite=Strict.
- Ingress streams SSE without response buffering, timeout longer than 60 seconds.
- Ingress access/error logs redact query strings on `/github/setup/callback`:
  that URL contains a one-use code. Never publish raw callback logs.
- Rate/connection limits appropriate for a small demo; no anonymous registration.
- AX, Substrate APIs, broker, database and object storage remain private.

Do not apply the example before reviewing it. It intentionally does not install
cert-manager, obtain a certificate, assume an issuer or change cluster-wide Traefik.

## 6. Verify and maintain

Follow [usage](USAGE.md) and the [acceptance checklist](VERIFICATION.md).
`/healthz` checks the portal process, **not** AX/Substrate execution or model access.

Before upgrades, stop accepting new work, wait for the worker/publication queue to
be idle, and close browse sessions. Back up SQLite consistently, its data-protection
keys, encrypted connection databases **and the separate vault key**. Retain user IDs.
Never copy a live SQLite file without its consistency/WAL requirements.

Interrupted turns/publications are not blindly replayed; inspect state and explicitly
retry. Pending jobs remain durable. Do not scale the SQLite-backed portal beyond
one replica or run a second worker against its database.

Uninstall deliberately: finish/cancel desired runs through the portal first,
remove only this app's ingress/deployments/services/policies, then decide separately
whether to retain/delete its PVC and Secrets. **Do not delete `ax-system` merely
to remove the portal**: that namespace also contains AX and its database/workers.
Do not assume deleting Kubernetes resources immediately erases snapshots or backups.
