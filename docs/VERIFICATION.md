# What was tested, and how to verify your installation

## Scope

The application baseline previously passed real execution on a pinned isolated
k3s 1.36.5 stack: Substrate state continuity, AX workspace restoration, Codex output
and same-thread resume, live files/exports, private GitHub checkout and a manually
published draft PR reused after resume. No live identifiers or account data are
included in this repository.

Automatic PR publication is covered by automated tests, **not a separate live
GitHub automatic-publication test**. Multi-node/HA, malicious-tenant security,
disaster recovery, sustained capacity and microVM/KVM are not established.

The public copy retains those implementations, adds portable configuration/build
helpers and removes deployment-bound state. It is **not** evidence that a fresh
native-k3s installation has been performed from this new repository. Do not claim
turnkey support based on the earlier isolated-container experiment.

## Automated checks

```bash
./scripts/test.sh
./scripts/scan-secrets.sh
```

Coverage includes authentication/ownership/CSRF, queue/recovery, bounded file reads,
symlink/path protection, credential encryption/leases, Git bundle/check gates,
manual/auto publication queueing, retry/stale commits/remote branch conflicts,
frontend escaping/state, themes and public manifest/proxy configuration.

Backend lifecycle tests use a fake platform; Git fixture tests use real local Git;
provider/GitHub contract tests do **not** call live accounts. The publication scan
checks committed history and its source archive, while the hygiene check also
checks candidate files and local Markdown links. They complement manual review,
not replace it.

## Cluster acceptance order

Use a small dedicated test environment. Do not run these during someone else's
active task. Never manually alter database states to make tests appear successful.

1. **Substrate:** certificate projections and trust bundles, ready worker, real
   counter traffic, synchronous suspend/resume with state continuity. All TLS verified.
2. **AX:** use `examples/ax-smoke.yaml` and the [k3s guide](K3S.md). Write a marker in
   `/workspace`, suspend, resume and verify the exact marker, then clean up that fixture.
3. **Portal without AI:** anonymous run endpoints denied; login works; environment
   check streams actual output; resume preserves state; failure check reports exit 7.
4. **Files without AI:** open a paused environment's workspace, list/preview/download
   a created file; turn number must not change. Close or wait for browse-idle suspension.
5. **Codex (consumes your subscription usage):** authorize your own account, create
   a tiny file with an exact-byte assertion, inspect streamed commands and download it.
   Resume to append a second line and verify both; confirm the same thread/workspace.
6. **Saved export:** capture intentionally, Finish the fixture, verify the export
   remains available while live workspace access is no longer possible.
7. **GitHub (writes to a repo you own):** authorize a small disposable repo, create
   one text-file commit with a required check, choose **manual** publication. Verify
   the branch does not exist remotely before clicking Publish. Confirm exactly one
   draft PR, correct base/head/contents and no merge. Resume and confirm the same PR.
8. **Optional auto mode (also writes):** explicitly opt in on a separate fixture.
   Prove failed checks do not publish; then a successful clean commit/check publishes
   one draft PR. Treat this as new acceptance, not something already proven above.
9. **Recovery:** only while idle, restart your demo backend and verify login/runs/events
   persist. Separately exercise interrupted operations in a disposable environment;
   they must require explicit reconciliation/retry, not blind replay of side effects.

Review and remove **only your own test branches/PRs/workspaces** afterward. The portal
does not delete remote PRs/branches when finishing a run. No step requires merging.

## Before sharing a URL

Verify TLS, secure cookies, allowed origin/proxy, anonymous denial, callback log
redaction and NetworkPolicy enforcement. Health endpoints alone do not prove
these controls. Refer to [deployment](DEPLOYMENT.md) and [security](../SECURITY.md).
