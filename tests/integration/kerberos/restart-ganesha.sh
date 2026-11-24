#!/usr/bin/env bash
set -euo pipefail

pid=$(pidof ganesha.nfsd)
kill "$pid"
for attempt in $(seq 1 30); do
    if ! kill -0 "$pid" 2>/dev/null; then break; fi
    if [ "$attempt" -eq 30 ]; then echo 'NFS server did not stop' >&2; exit 1; fi
    sleep 1
done
ganesha.nfsd -F -L /tmp/ganesha.log -f /etc/ganesha/ganesha.conf </dev/null >/dev/null 2>&1 &
for attempt in $(seq 1 30); do
    if rpcinfo -t 127.0.0.1 100003 3 >/dev/null 2>&1 && rpcinfo -t 127.0.0.1 100005 3 >/dev/null 2>&1; then exit 0; fi
    if [ "$attempt" -eq 30 ]; then echo 'NFS server did not restart' >&2; exit 1; fi
    sleep 1
done
