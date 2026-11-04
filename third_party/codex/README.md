# Codex runtime pin

Native Linux x64 binary from the official npm package `@openai/codex@0.165.0-linux-x64` (parent CLI 0.165.0).

SHA256: `c06a67fd8ac76508dc78bd99ce20c9f317b8d568beda59485f3f4134df9962d5`.

The image-preparation helper verifies this hash/version before copying the binary into ignored build context. No host auth file, config, environment credential, CLI home or session is copied. Runtime remains Apache-2.0; the binary is not checked into this repository.

The protocol was inspected from that exact binary in an empty CODEX_HOME. Managed `chatgptDeviceCode` login is used. The `chatgptAuthTokens` schema explicitly marks that mode internal-only at this pin, so this implementation does not use it despite broader current web documentation describing it as experimental.

The AX guest image preserves the native distribution layout under `/opt/codex`:
`bin/codex`, its `bin/codex-code-mode-host` companion, `codex-path/rg`,
`codex-resources/bwrap`, and `codex-package.json`. Every copied file is hash-pinned
in `bundle.sha256`. Voice resources are not included; voice is not a portal feature.
The first live AX test exposed why copying only the main executable is insufficient:
model replies worked, but command execution could not start the code-mode host.
