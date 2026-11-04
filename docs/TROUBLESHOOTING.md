# Troubleshooting without weakening isolation

Collect only redacted diagnostics. Never publish Secrets, kubeconfig contents,
provider auth files, full GitHub callback URLs, user prompts or private source.

| Symptom | Check / remedy |
|---|---|
| Certificate APIs missing | Compare the exact Kubernetes version and configured gates. Flags cannot create an API absent from the binary. Check workers' projection support too. |
| Pods ready but router TLS fails | Verify issued pod certificates/trust bundles, allowed SPIFFE identity, CA/server authority and router logs. A Ready pod is not an end-to-end TLS test. Never use `-k` as the fix. |
| atelet says mount is not shared/slave | Inspect the filesystem and mount propagation for its state directory in the **node's** mount namespace. Use dedicated disk-backed storage; review a narrow mount fix, not a host-wide propagation change. |
| OverlayFS upperdir rejected | Do not use a container overlay layer as backing storage. Check filesystem and kernel support on the actual worker node. |
| Containerd pulls, sandbox image fetch fails | Atelet has a separate image path. Verify its DNS/registry reachability and credentials; Docker aliases and localhost mirrors are not automatically shared with pods. |
| Worker ready, sandbox does not start | Inspect WorkerPool/SandboxConfig compatibility, golden-template initialization, available capacity, certificates and snapshot plugins. Budget platform overhead as well as actor limits. |
| ENOSPC / no space left | Distinguish disk bytes, inodes, inotify instances/watches and filesystem quotas. Measure the actual root/data mounts. Do not blindly raise limits or delete images used by other workloads. |
| Node DiskPressure / OOM | Stop accepting new demo work; inspect node events, volume usage and container limits. Retain headroom for image extraction/builds/snapshots. Local-path PVC size is not a host-disk safety guard. |
| AX connection denied | Keep AX private, but allow only the portal pod selector on port 8080. Policy labels must match the installed manifests. |
| Router reachable by Service but policy blocks guest RPC | Policies evaluate the router **pod target port 8443**, not just Service port 443. Check selectors and verified TLS, not unrestricted egress. |
| GitHub redirect/CONNECT 403 | The backend proxy must allow GitHub API/Git hosts. Changing a ConfigMap does not reload a running Python process; render/apply again so the checksum rolls the proxy. Restart setup to get a new callback, not replay a used code. |
| HTTPS origin rejected / login loop | Check PublicOrigin host, secure cookies, ingress overwriting forwarded scheme, configured trusted proxy CIDR and narrow ingress policy. Do not trust all forwarded headers or disable secure cookies on a public URL. |
| Device code not authorized | Complete authorization on the provider page; enable permitted device-code login if required. Check backend egress. Do not paste credentials into prompts. |
| Model replies but tools cannot run | Preserve the full pinned native Codex bundle, especially its code-mode host, ripgrep and bwrap companions. Copying only the main executable is insufficient. |
| Connected but inference fails | Successful login is not proof of model availability/quota. Run one tiny task; inspect sanitized failure state. No silent API-billing fallback exists. |
| Browser displays old source | Refresh the page and verify deployed image revision/digest. Do not assume building a local image deployed it. |
| Files say paused/closed | Explicitly Open workspace; GET does not wake it. Finish/Cancel removes the workspace—use retained exports if saved. |
| PR publication blocked | Inspect clean tree, assigned branch, required check, bundle size, unchanged remote branch and PR state. Fix via another turn; do not force-push to bypass a conflict. |
| Run stuck after restart | Interrupted side effects are deliberately not blindly replayed. Inspect AX/GitHub state and use explicit resume/retry/release; do not edit SQLite status fields to fabricate success. |

## Safe diagnostic sequence

```bash
# Read-only. Use your dedicated kubeconfig/context.
./scripts/preflight-k3s.sh "$CONTEXT"
kubectl --context "$CONTEXT" -n ax-system get pods,pvc,workerpools
kubectl --context "$CONTEXT" -n ate-system get pods,pvc
kubectl --context "$CONTEXT" get clustertrustbundles
kubectl --context "$CONTEXT" -n ax-system logs deployment/agent-desk-portal --tail=100
```

Review logs privately before sharing: application errors can still include
repository names or user data even when credentials are not intentionally logged.
To reproduce functionality, prefer the small [acceptance checks](VERIFICATION.md).

No watcher-pause patches, machine-specific sysctl values, host restarts, disk
migrations or broad cleanup commands are part of this public demo. Diagnose your
own environment and obtain operator approval before host-wide changes.
