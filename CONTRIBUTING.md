# Contributing

Small, focused improvements and reproducible bug reports are welcome.

1. Use a branch and keep changes logically grouped.
2. Run `./scripts/test.sh` and `./scripts/scan-secrets.sh` before proposing a release.
3. Describe what you tested: mocked unit/API tests, local real-Git fixtures,
   container build, actual AX execution or account-backed provider/GitHub use.
   Never present a health check or mocked adapter test as an end-to-end acceptance.
4. Do not commit `.local`, `.platform`, credentials, databases, private test output,
   screenshots of accounts, host addresses or rendered infrastructure state.
5. Keep deployment commands explicit about context, side effects and prerequisites.
   Do not introduce host-wide tuning, production restarts or destructive cleanup
   as a normal application install step.

Current baseline: Linux/amd64, .NET 10, Python 3.12+, Node.js 22+, pinned AX/Substrate
contracts and Codex runtime. Upgrade schemas, runtime companions and images
together with integration checks. Keep dependency lock files and third-party licenses.

The UI is intentionally buildless. The connected frontend is `dist/live.*`;
`dist/index.html`/`app.js`/`styles.css` are the separate fictional prototype.
Keep real-vs-simulated features clearly labelled.

Contributions to original project code are under [MIT](LICENSE); existing third-
party files retain their own licenses. See [NOTICE](NOTICE). Security reports
belong in the [private reporting process](SECURITY.md), not a public bug report.
