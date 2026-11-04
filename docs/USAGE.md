# Use the portal

The connected portal is the backend's `/` page, not the simulated `/demo`.
Sign in with the local login provisioned by your deployment operator. Never put
provider passwords or tokens into a task prompt.

## Start without an AI account

Choose **New run → Environment check** and enter a short note. It runs a fixed
diagnostic inside AX, streams real output, writes its small session state and
pauses for review. Resume with another note and confirm the turn increases.
**Failure check** deliberately exits with code 7: a failed result is expected.
Neither task sends the note to a model or treats it as a shell command.

## Connect your personal ChatGPT/Codex account

1. Open **Connections → Continue with ChatGPT**.
2. Open the official verification link displayed there and enter the one-time code.
3. Complete authorization in your own browser. If necessary, enable device-code
   login in your ChatGPT security settings or ask your account administrator.
4. Wait until the portal shows **Connected**. This connection is separate from
   your local portal login.

The demo uses the pinned Codex runtime's managed `chatgptDeviceCode` flow; it does
not collect your ChatGPT password or provide a custom password-login page.
See [official Codex authentication guidance](https://developers.openai.com/codex/auth/).
Feature/model availability and limits depend on your account. There is no promise
of free usage or an automatic API-key/billing fallback.

Do not share device codes, callback URLs, `auth.json`, App keys or access/refresh
tokens. Disconnect removes the portal's stored connection and cancels its active
lease; it is not a provider-wide session-revocation guarantee. Use provider account
security controls when needed. Managed credential refresh is implemented, but
live expiry/rotation stress testing is not claimed.

## Run a small Codex task

Choose **New run → Codex task**, for example:

> Create `artifacts/hello.txt` containing `Hello from Agent Desk` and a newline.
> Use Python to verify its exact contents. Briefly explain what you did.
> Do not use the internet or modify `.agent-desk`.

Watch **Conversation** for actual agent messages and expandable command results.
Raw internal reasoning is not displayed. Tool commands execute automatically
inside the isolated sandbox; there is no per-command approval UI. Start with
non-sensitive inputs and review the result yourself.

The demo bounds a turn to five minutes/eight model requests, one CPU and 1 GiB for
Codex (512 MiB for checks). Repository dependency installation requiring network
access will not work; use preinstalled tools or build a reviewed runner image.

## Review, browse and continue

- **Ready for review** means the turn ended and AX confirmed suspension. It does
  not prove the requested goal is correct or all tests passed.
- **Files → Open workspace · no AI turn** resumes the filesystem for browsing,
  without making a model call or incrementing the conversation turn.
- Close the workspace, then **Resume with feedback** to continue the same files
  and Codex thread. Opening files is not the same as an AI resume.
- **Save export** retains optional copies on the portal volume. Ordinary live
  browsing does not copy file contents into SQLite. See [Files](FILES.md).
- **Suspend** stops the active work and saves the durable workspace when confirmed.
- **Finish** or **Cancel** releases/deletes the AX environment. Neither silently
  publishes code. Save needed deliverables before finishing; explicit exports and
  conversation history remain, subject to storage limits.

If status is **Needs attention / recovery-required**, release was not confirmed.
Inspect the error and retry Suspend/Cancel rather than launching more work blindly.
If a request's response is lost, check Runs before resubmitting after reloading;
browser-memory retry keys do not survive a full page reload.

## Work in a GitHub repository

See [GitHub setup](GITHUB.md). The optional form section selects repo, base branch,
required check and publication mode. Codex works at `/workspace/repo`, can commit
locally, and cannot directly push using a GitHub token.

Default: inspect **Changes & publishing**, then **Push & create draft PR**.
Optional: explicitly enable automatic draft PR publication after successful turns
and checks. Both flows use the same verification gates, reuse the run branch/PR,
and **never merge**. Choose a meaningful check, not an always-successful placeholder.

## What is only simulated?

The `/demo` prototype includes multiple repositories per workspace, agent/resource
selectors and event-rule examples. These illustrate UX ideas; the real connected
portal supports the features listed in the README, not every simulated option.
No webhooks, multi-agent scheduler, arbitrary model/account sharing, terminal or
in-browser file editor is implemented by the connected demo.
