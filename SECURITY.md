# Security and limitations

**A trusted, single-operator demo—not a hardened public multi-tenant service.**
Running arbitrary agent commands is inherently risky. Use a dedicated disposable
environment, non-sensitive test repositories and accounts you are authorized to use.
The software is provided without warranty; see [LICENSE](LICENSE).

## Boundaries that matter

- The pinned [AX security policy](https://github.com/google/ax/blob/a496fc1ff1cf9bea4c34924348e4119172cfea5b/SECURITY.md)
  states that its API lacks authentication/authorization and atespaces are not
  tenant isolation. Keep AX reachable only by trusted operators and the backend.
- Portal login/owner checks do not turn AX into a secure hostile multi-tenant control
  plane. The demo reuses AX's service-account certificate identity and upstream
  authorization grants. Review upstream broad Secret-reading/bootstrap permissions.
- Substrate node components need powerful host access. Do not equate a privileged
  container or a separate namespace with an independent VM security boundary.
- NetworkPolicy enforcement, certificate projection, correct storage and registry
  permissions are deployment responsibilities. Never expose platform debug APIs.
- Codex tools run without a second nested OS sandbox inside gVisor. No per-tool
  human approval UI exists. Guest code and check output remain untrusted.

## Credentials

- Portal accounts are provisioned hashed passwords; no signup/default login.
- Provider/App credentials are owner-bound AES-GCM encrypted caches with an external
  32-byte vault key. Browser/guest/event APIs do not return provider or App secrets.
- Root, cluster administrators and the backend remain trusted. This is not an
  administrator-isolation boundary, external HSM or production secrets service.
- GitHub operation tokens are scoped to the selected repo; read-only for checkout,
  write only for publishing. They never enter the sandbox Git config/workspace.
- The model broker uses fixed endpoints/model and bounded requests. No arbitrary
  guest provider URL, direct guest internet access or API-key fallback.
- Disconnect is local deletion/cancellation, not provider-wide revocation or GitHub
  App uninstallation. Live credential-expiry/rotation stress tests remain incomplete.
- Retain user IDs, encryption and data-protection keys during recovery. Password
  rotation does not revoke all existing sessions; removing a provisioned user and
  restarting rejects that user ID. Complete session revocation/versioning is future work.

## Resource/data limits

One portal replica/worker; up to 20 queued jobs. Codex turns: 1 CPU / 1 GiB,
five minutes / eight model requests. Check tasks: 512 MiB. These are application
limits, not a total cluster/build budget or per-user billing guarantee.

Small Git repositories only: 8 MiB bundles, 128 MiB backend spool, bounded Git/diff
operations, no LFS/submodule initialization and no guest dependency downloads.
Required checks are guest-executed and must not mutate HEAD/tree. They are not
independent code review or proof of correctness. No force-push, branch-protection
bypass, workflow-file publication or merge is provided.

Files/exports have [explicit bounds](docs/FILES.md). History/credential databases
and platform snapshots require monitoring, backups and an operator retention
policy. No general retention UI, secure erasure guarantee or completed disaster-
recovery drill. A PVC capacity request does not reserve free space on a local-path host.

## Report a vulnerability privately

Use this repository's **Security → Report a vulnerability** when available.
Do not post live credentials, private repository contents, database dumps, task
prompts or callback URLs in a public issue. If private reporting is unavailable,
open an issue asking for a private contact channel **without exploit details or
sensitive data**. This community demo has no guaranteed response SLA.

Report upstream defects to the relevant upstream project; do not assume this
project's maintainer can provide Google/OpenAI/Substrate security support.
