# Agent Desk

A small, self-hosted portal demonstrating **[Google AX](https://github.com/google/ax)**
and **[Agent Substrate](https://github.com/agent-substrate/substrate)**: start a coding
task, watch it work, inspect its files, pause it, and continue later.

Independent community demo. **Not a Google or OpenAI product, endorsed integration,
production platform, or public multi-tenant service.** Public source does not mean
anonymous access: run your own authenticated deployment with your own connections.

```text
Browser → authenticated Agent Desk backend → AX → Substrate → gVisor sandbox
                    │                              └─ saved workspace / resume
                    ├─ SQLite: runs, events, optional exports
                    ├─ encrypted connection store + separate key
                    └─ trusted model broker / GitHub publisher
```

## What works

| Feature | Connected portal |
|---|---|
| Tasks | Codex task, credential-free environment check, intentional failure check |
| Conversation | Streamed agent messages and expandable command output; durable event replay |
| Lifecycle | Queue, suspend, same-workspace / same-Codex-thread resume, cancel and finish |
| Files | Live folder browser, text previews and downloads; optional saved exports |
| AI connection | Personal ChatGPT/Codex device-code login; provider tokens stay outside the sandbox |
| GitHub | Private App setup, selected repositories or explicitly consented all-repository access |
| Code publishing | Private checkout, local commits, checked diff review, manual draft PR by default |
| Optional automation | Publish a draft PR after each successful turn **and** required check; never merge |
| UI | Light/dark themes, compact conversation, responsive layout |
| Access | Local provisioned login, owner checks, CSRF protection, persistent queue |

One worker, small repositories, approved images, bounded turns. No guest dependency
downloads or arbitrary internet access. See [limits and security](SECURITY.md).

## Start here

**Just explore the UX — no accounts, Kubernetes, or AI usage:**

```bash
git clone https://github.com/chovik/agent-desk.git
cd agent-desk
python3 -m http.server 8080 --bind 127.0.0.1 --directory dist
```

Open **http://127.0.0.1:8080**. This is the **separate simulated prototype**;
its sample repositories, messages, resource choices, automations and PRs are fake.
It is also available at `/demo` on a connected deployment. Never enter credentials
into that prototype. The real portal is served by the .NET backend at `/`.

**Run real tasks:**

1. [Install and validate AX/Substrate on k3s](docs/K3S.md).
2. [Build and deploy the portal](docs/DEPLOYMENT.md).
3. [Connect Codex and try a task](docs/USAGE.md).
4. [Connect GitHub and publish a draft PR](docs/GITHUB.md).

**k3s status:** the pinned stack passed execution and workspace restore tests on
an isolated **k3s 1.36.5** configuration. This is observed compatibility, **not an
upstream support certification**. A clean native/bare-metal k3s install using this
public packaging has **not** been repeated. The guide explains the adaptations
instead of promising a turnkey installer. Do not upgrade a working production
cluster just to try this demo.

## For developers

Prerequisites: .NET 10 SDK, Node.js 22+, Python 3.12+, Git and Linux `prlimit`.
No application build step is needed for the browser assets.

```bash
./scripts/test.sh             # offline application/packaging tests after package restore
./scripts/scan-secrets.sh     # downloads a pinned Gitleaks, scans committed history/source
```

Tests do not need an AI account, GitHub App, or running cluster. They do not prove
your cluster is working: follow the [acceptance checklist](docs/VERIFICATION.md).

| Directory | Purpose |
|---|---|
| `backend/` | ASP.NET Core API, native AX adapter, persistence, brokers and tests |
| `dist/` | Buildless connected UI and clearly separate mock prototype |
| `guest/` | Codex driver and bounded file/repository helpers |
| `deploy/` | Image definitions, private portal manifests, example ingress and source pins |
| `examples/` | AX Task/Workspace and Substrate WorkerPool examples |
| `scripts/` | Read-only preflight, build/render utilities and publication checks |
| `third_party/` | Pinned protocol schemas, runtime hashes, original licenses |

Further reading: [architecture](docs/ARCHITECTURE.md) ·
[file storage](docs/FILES.md) · [troubleshooting](docs/TROUBLESHOOTING.md) ·
[contributing](CONTRIBUTING.md) · [public release scope](docs/PUBLIC-RELEASE.md).


## License

[MIT](LICENSE) for original project code: reuse, modify and distribute, including
commercially, with the notice retained and no warranty. Third-party files keep
their original licenses; see [NOTICE](NOTICE).
