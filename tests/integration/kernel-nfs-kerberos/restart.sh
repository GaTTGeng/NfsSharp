#!/usr/bin/env bash

set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  exec sudo bash "$0" "$@"
fi

systemctl restart nfs-server.service
systemctl is-active --quiet nfs-server.service

# Kernel sunrpc keeps auth.rpcsec.context entries across nfs-server unit restarts;
# flush the kernel GSS context cache to model full server/context-table reset.
flush_time="$(date +%s)"
printf '%s\n' "${flush_time}" > /proc/net/rpc/auth.rpcsec.context/flush
while [[ "$(date +%s)" -le "${flush_time}" ]]; do
  sleep 0.2
done

for attempt in {1..30}; do
  if rpcinfo -t 127.0.0.1 100000 2 >/dev/null 2>&1 &&
     rpcinfo -t 127.0.0.1 100005 3 >/dev/null 2>&1 &&
     rpcinfo -t 127.0.0.1 100003 3 >/dev/null 2>&1; then
    exit 0
  fi

  sleep 1
done

echo "Linux kernel NFSv3 Kerberos server did not become ready after restart." >&2
rpcinfo -p 127.0.0.1 >&2 || true
exit 1
