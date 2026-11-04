# Workspace files and saved exports

## Three different places

| Data | Location | How to access |
|---|---|---|
| Current workspace | `/workspace` inside the AX/Substrate sandbox | Files: open workspace, browse, preview/download |
| Durable workspace/session | Substrate snapshot/object storage managed through AX | Suspend/resume; not a normal host folder to browse |
| Optional retained copies | `artifacts.db` on the portal PVC | Saved exports, even after the sandbox is released |

The portal's normal Files tab reads directly from the sandbox. It does **not** save
file contents in SQLite just to browse them. SQLite still stores run ownership,
events, lifecycle state and browse deadlines. A GitHub checkout lives under
`/workspace/repo`; Codex session state is hidden under `/workspace/.agent-desk`.

## User flow

1. Open a run's **Files** tab.
2. If suspended, select **Open workspace · no AI turn**. This allocates sandbox
   resources without a model call or conversation turn.
3. Browse folders, preview text or download files. Reads are read-only, not a
   terminal or editor. When execution is already active, no second wake is needed.
4. **Close workspace** when finished. A browse-only session automatically closes
   after five minutes without file access or when queued work needs capacity.
5. Use **Save export (optional)** for deliverables you want after Finish/Cancel.

Only one execution/browse session is supported at a time. Status polling does not
extend the idle deadline. The timer belongs to the portal worker: a down backend
cannot immediately release resources; recovery must reconcile with AX.

## Safety and limits

Every request checks owner authorization. GET requests never wake a sandbox.
Opening/closing requires authenticated POST, CSRF and idempotency protection.

The backend calls a fixed Python helper through Substrate's verified private
process API. Paths are JSON data, not interpolated shell commands. Descriptor-
relative, no-follow reads protect against changing symlinks; no Kubernetes token
or host directory is mounted into the guest for this feature.

- Relative paths inside `/workspace` only, max 500 characters / 32 components.
- Hidden/internal paths, traversal, symlinks, hard-linked and special files blocked.
- Up to 500 listed entries (4096 inspected); no unrestricted recursive traversal.
- Live file limit **8 MiB**; text preview **200,000 characters**. Binary downloads allowed.
- Concurrent changes produce a retryable conflict, not a claimed atomic snapshot.
- HTML is displayed as text; downloads are attachments, not executable previews.
- Guest operation timeout 15 seconds, backend budget 35 seconds; two concurrent
  file HTTP requests globally and 120 requests/user/minute.

## Exports are copies, not a backup system

Exports are explicit. The latest successful capture replaces the previous export
for that run, even within the same turn. Check the capture time; copies may be
older than the workspace. A failed capture preserves the previous successful one.

Limits: 256 files, 1 MiB/file, 8 MiB/capture, 64 MiB/owner and 256 MiB total.
Hidden paths, session state, common build folders, symlinks and special files are
excluded. The backend independently validates the returned payload before storage.

Finish/Cancel deletes the working AX environment; only saved exports remain
accessible through the portal afterward. Platform snapshot garbage collection and
backup retention are separate. No automatic retention/deletion UI is implemented.
Do not put secrets in ordinary exported deliverables: filename restrictions are
not a secret scanner or secure-erasure guarantee.

For operator debugging, use the pinned `ax ssh` against an authorized task and
explicit cluster context. Sandboxes are not necessarily one ordinary Kubernetes
pod per task; `kubectl exec` into an arbitrary worker is not the portal's file API.
