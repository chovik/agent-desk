#!/usr/bin/env bash
# No cluster, account, model call, registry write or deployment required.
set -euo pipefail
cd "$(dirname "$0")/.."
export DOTNET_PROCESSOR_COUNT=${DOTNET_PROCESSOR_COUNT:-2}
export MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_TELEMETRY_OPTOUT=1
dotnet test backend/AgentDesk.Portal.Tests -c Release -m:1 \
  -p:UseSharedCompilation=false -p:RestoreLockedMode=true --verbosity minimal
node --check dist/app.js
node --check dist/live.js
node --test tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_*.py'
for script in scripts/*.sh; do bash -n "$script"; done
python3 scripts/check-public-tree.py

