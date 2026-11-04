# Public demo scope

This repository starts with **new Git history**, not a fork containing deployment
history. It contains application source and reusable examples, not a live service
or a copy of anyone's tasks, accounts or workspaces.

## Kept

- Complete connected portal and separate mock UX.
- Local login, event streaming, lifecycle/resume, files/exports, Codex connection
  and broker, GitHub checkout/checks/manual and optional automatic publishing.
- Application tests, pinned upstream protocols/licenses and runtime hashes.
- Generalized image preparation, offline manifest rendering, preflight, CI and
  source publication checks.
- Useful operational lessons rewritten as troubleshooting guidance.

## Deliberately excluded

- Prior Git history, chronological lab journals, acceptance run/PR IDs and account metadata.
- Machine addresses, live URLs, hostnames, private disk paths and infrastructure topology.
- Databases, snapshots, credentials, login files, keys, kubeconfigs, logs, screenshots,
  rendered deployment state, image archives and local source/build caches.
- Host-specific filesystem-watcher patches, service restarts, resource-guard units,
  disk migration/recovery instructions and nested-cluster bootstrap orchestration.
- One-off account probes and deployment-bound smoke harnesses. Their useful checks
  are retained as automated tests or reproducible user-facing acceptance steps.
- Obsolete integration plans and intermediate status claims.

## Reusable does not mean automatically portable

No existing service needs to be restarted to publish this source. The application
remains feature-complete relative to the demo baseline; deployment assumptions are
now explicit instead of silently inheriting an operator's machine state.

The new build helpers use a downloaded, checksum-verified runtime, not a host's
installed account. The guest base image is a required parameter, not a local
registry reference. HTTPS ingress can trust explicitly configured proxy CIDRs;
default trust is loopback only, and `/0` is rejected.

Publication review includes an allowlisted source export, neutral fixture identities,
history/source secret scanning and a machine-data/link check. No scanner proves
that every possible sensitive datum or security defect is absent. Review changes
before publishing; never add runtime state with `git add -f`.
