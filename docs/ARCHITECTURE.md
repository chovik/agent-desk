# How the pieces fit

## Responsibilities

- **Portal:** login/ownership, task form, queue, conversation, files, connections,
  required checks and publishing policy. C#/.NET 10 with SQLite; vanilla browser JS.
- **AX:** Task and Workspace definitions and lifecycle. The portal uses native
  gRPC contracts pinned under `third_party/ax`, not shelling out to `kubectl` per run.
- **Substrate:** worker capacity, gVisor sandboxes, snapshots, private guest process
  access and certificate identity. Guest RPC uses verified mTLS through its router.
- **Codex:** executes inside the sandbox; the backend mediates model traffic.
- **GitHub:** external source of repository content and destination for approved
  branches/draft PRs. The backend alone holds the App credentials.

AX Task/Workspace definitions are **AX resources**, not Kubernetes CRDs. Submit
them with `ax apply`, not `kubectl apply`. Substrate WorkerPool/SandboxConfig are
Kubernetes resources. A WorkerPool is capacity, not a portal task template.

## From form to execution

1. The authenticated user selects an approved task type and enters title/instructions.
2. The backend validates the request, assigns run identity/owner and queues work in SQLite.
3. It generates AX Task/Workspace names and fixed, approved images/resources. Browser
   input cannot choose an arbitrary image, owner, server command or AX resource name.
4. AX starts/resumes the workspace. For a GitHub task, the trusted backend fetches a
   pinned commit and sends a credential-free Git bundle to `/workspace/repo`.
5. For Codex, user instructions become the agent prompt. For an environment/failure
   check, the form note is **data** to a fixed diagnostic program, not executable code.
6. Agent messages and tool output become bounded, persisted events streamed with SSE.
7. After a completed turn, required repository checks run and the backend verifies
   the committed bundle. AX suspension must be confirmed before normal review.

This portal supplies the prompt to its guest driver. Upstream AX examples can also
put agent configuration into `Task.spec.config` or files in `Workspace.spec.files`;
those examples are not an exact description of this portal's adapter.

## Resume means durable state, not a frozen process

AX restores `/workspace`; a new process starts. Codex session data is saved in
`/workspace/.agent-desk/codex` and the portal retains the thread ID, then uses
`thread/resume`. Failure to restore is shown as an error, not silently replaced
with a new conversation. Temporary process state is not assumed durable.

Suspended workspaces still consume persistent storage. **Finish** and **Cancel**
delete their AX Task/Workspace definitions. Snapshot garbage collection is a
separate platform policy, not immediate secure erasure. Conversation and explicit
saved exports remain in the portal.

## Credential boundaries

```text
ChatGPT login → backend encrypted cache → lease/refresh → fixed provider HTTPS
                                            ↑
guest Codex → loopback relay → private stdio RPC bridge

GitHub App → backend encrypted key → one-repository installation token
                                         ├─ read-only fetch → Git bundle → guest
                                         └─ checked commit → branch push → draft PR
```

The model broker fixes the upstream/model and limits requests, body size and turn
duration. Provider access/refresh tokens are not put into AX task config, workspace,
snapshots, browser responses or event logs. The sandbox's disposable relay key is
not a provider credential. No API-key billing fallback is implemented.

Normal Git tools inside the sandbox can edit/test/commit but have **no authenticated
remote**. Publication is a trusted backend operation. See [GitHub](GITHUB.md).

## Read the code

| Area | Starting point |
|---|---|
| API/authentication | `Program.cs`, `LabIdentity.cs` |
| Run lifecycle | `RunStore.cs`, `RunWorker.cs`, `ExecutionPlatform.cs` |
| Guest streams | `GuestFrames.cs`, `WorkspaceAccess.cs` |
| Codex auth/model broker | `CodexConnections.cs`, `CodexCredentialLease.cs`, `ModelBridge.cs` |
| Git checkout/publishing | `GithubConnections.cs`, `RepositoryWorkflow.cs`, `TrustedGit.cs` |
| Browser | `dist/live.js`, `dist/live.css`, `dist/live-theme.js` |

Backend filenames are relative to `backend/AgentDesk.Portal/`. The fixed guest
helpers are in `guest/`; protocol code generation happens during the .NET build.

