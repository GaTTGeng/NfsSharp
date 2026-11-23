#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
docker build -t nfssharp/kerberos-integration "$root/tests/integration/kerberos"
results_args=()
if [ -n "${NFSSHARP_KERBEROS_RESULTS_DIR:-}" ]; then
    mkdir -p "$NFSSHARP_KERBEROS_RESULTS_DIR"
    results_args+=(--mount "type=bind,source=$NFSSHARP_KERBEROS_RESULTS_DIR,target=/results")
fi
docker run --rm --hostname nfs.test.local \
    --mount "type=bind,source=$root/tests/NfsSharp.Tests/bin/Release/net9.0,target=/tests,readonly" \
    "${results_args[@]}" \
    nfssharp/kerberos-integration
