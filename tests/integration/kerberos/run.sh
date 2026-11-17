#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/../../.." && pwd)
docker build -t nfssharp/kerberos-integration "$root/tests/integration/kerberos"
docker run --rm --hostname nfs.test.local \
    --mount "type=bind,source=$root/tests/NfsSharp.Tests/bin/Release/net9.0,target=/tests,readonly" \
    nfssharp/kerberos-integration
