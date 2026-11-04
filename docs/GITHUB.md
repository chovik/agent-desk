# GitHub checkout and publishing

## Connect your account

1. Portal **Connections → Connect GitHub** opens GitHub's private App creation
   form. Create your own App (this demo uses the personal-account flow).
2. Install it with **Only select repositories** (recommended), or **All repositories**
   if you want access to every current and future repository in the account.
3. Return to Connections, **Refresh installations**, then **Use this installation**.
   All-repository installations additionally require checking the explicit access
   warning and clicking **Confirm all-repository access**. The backend will not use
   an all-repository installation without this stored owner consent.
   A successful setup may require signing back into the local portal because its
   session cookie is deliberately SameSite=Strict.
4. In **New run → Codex task → GitHub repository**, choose a repository, base
   branch and a required local check command. Leave publishing on **Wait for my
   review**, unless you deliberately want automatic draft-PR publishing.

Do not paste a PAT, App private key, callback URL or ChatGPT credentials into a
prompt. The manifest callback is single-use. If it fails, return to Connections
and start again; reloading that URL is not a recovery method. An unsuccessful
manifest exchange can leave an unused App in your GitHub settings; inspect/remove
that unused App there if desired, rather than sharing its private key.

## What gets checked out

The backend resolves the selected GitHub repository ID and base branch to an
immutable commit when the run is created. It fetches that commit using ordinary
Git over HTTPS, with an installation token restricted to **that repository** and
read-only Contents permission. It creates a Git bundle and sends it through the
existing verified Substrate guest RPC stream—never a Kubernetes Secret mounted
into the sandbox, and not AX's upstream repository credential helper.

The guest receives a normal checkout at **`/workspace/repo`** on
**`agent-desk/<run-id>`**. Codex's working directory is that checkout. Git author
identity is `Agent Desk <agent-desk@users.noreply.local>`; this is not a signature or
an assertion that the human personally wrote the commit. Codex can edit, test and
commit. No authenticated remote or GitHub token is installed in the guest.
Resuming restores this checkout and its commits, not a fresh clone. The original
base commit does not silently move when GitHub's base branch advances.

## Default: review, then publish

1. Codex finishes its turn and commits its changes.
2. The portal requires the assigned branch, a clean worktree (including untracked
   files), the original base in its history, and a successful **required check**.
   A check that changes HEAD or leaves file changes fails this gate.
3. The backend imports and verifies the bounded bundle into its own bare repo.
   The displayed patch is computed there, not accepted as an agent's claim.
4. The sandbox is suspended. **Changes & publishing** shows the committed diff,
   check output and exact commit. Click **Push & create draft PR** and confirm.
5. Only then does the backend request a short-lived repository-scoped token with
   Contents and Pull requests write permissions. It pushes the assigned branch
   without force, verifies the remote SHA, and creates a **draft** PR.

A successful local check is not proof of all requirements, independent code
review, or passing GitHub CI. Select a meaningful check; do not use `true` as a
substitute for tests in real work. Repository code and checks run only inside the
gVisor task, never in the trusted portal process.

## Optional: automatic draft PR

Choose **Automatically push & create draft PR** before starting the run and accept
the confirmation. After **each successful Codex turn**, the same clean-tree,
commit-history and required-check gates apply. Passing results queue publication
atomically; otherwise there is no automatic push. Publishing never merges.

Later turns update the **same branch and PR**. An externally changed branch or
closed/merged PR stops publication rather than replacing work or opening another
PR. A removed previously published branch is not silently recreated. Existing PRs
that a human has marked ready remain in that state; the portal does not change PR
review status.

## Failure and retry behavior

- Failed checks/uncommitted files: fix them with another turn, then review again.
- A new turn invalidates the prior publish candidate immediately.
- Duplicate button submissions use a durable idempotency key.
- A failure after pushing retains the pushed SHA. Retry looks for the same PR
  before creating one, avoiding duplicates after a lost response.
- Restart during an active publication is **not blindly replayed**. Inspect
  GitHub, then explicitly retry the same checked commit. Queued, not-yet-started
  work remains durable.
- Publishing does not wake the sandbox or call an AI model. Resume/cancel/finish
  are unavailable while publication is in progress.
- Finish/cancel delete the retained sandbox and backend repository spool, **without
  publishing**. Conversation, reviewed patch/check output and PR link remain.
  GitHub branches/PRs are not deleted. AX snapshot GC is separate, not secure erasure.

## Credentials and operational limits

- The owner-bound App key is AES-GCM encrypted in `github-vault.db` on the portal
  PVC using the retained external vault key. Back up both as described in [deployment](DEPLOYMENT.md). The App key is never sent to the browser or guest.
- Installation tokens are generated per operation, never stored in Git config,
  SQLite or command arguments. Trusted Git receives them through its askpass
  process environment; this is not a defense against a compromised backend/root.
- The App itself has Contents + Pull requests write access on selected
  repositories by default, or every current/future repository when explicitly
  authorized. Checkout tokens are read-only and scoped to one repo; publish tokens
  are also narrowed to one repo. Listing uses a read-only installation-wide token.
- Disconnect deletes this portal's saved App connection; it does **not** uninstall
  the GitHub App. Existing runs will not silently bind to a newly created App.
- GitHub.com only; personal private-App manifest setup; no Enterprise host,
  organization-owned App setup, shared service connection or OAuth PAT import.
- Repository picker lists up to 500 accessible repositories.
- Small repos: full-history fetch; **8 MiB seed/change bundle**, **128 MiB aggregate
  backend repo spool**, 32 MiB per Git-produced backend file, bounded Git output and
  100-second backend Git operations. Close old runs to free spool space. A large
  diff (>256K characters) fails the review import rather than truncating approval.
- Required checks: 180 seconds, 16 KB displayed output; background process group
  stopped after checks. Guest helper limits produced files to 16 MiB. No guest
  internet/dependency downloads, submodule initialization or Git LFS downloads.
- `.github/workflows` changes cannot be published by this integration. App workflow
  permission is not requested. No branch protection bypass, force push or merge.
- GitHub plan/organization policy can prevent private-repository draft PRs. Such an
  error is surfaced; it never silently falls back to a non-draft PR.
- The demo serializes execution/publication. It is not a multi-tenant Git hosting service.

## Deployment and verification

GitHub setup requires your public HTTPS origin and a correctly configured proxy.
Updating the CONNECT allowlist must roll out its process; the renderer embeds the
script checksum for this purpose. See [troubleshooting](TROUBLESHOOTING.md).

The core implementation passed a real private-repository checkout, Codex commit,
required check, manual draft PR and same-PR reuse after resume. Automatic publication
is covered by automated tests, not a separate live GitHub PR acceptance. The new
public repository has not repeated account-backed acceptance on a new deployment.
Follow [the verification checklist](VERIFICATION.md) with a small disposable repo
you own. A portal run is not authorization to modify repositories outside its binding.
