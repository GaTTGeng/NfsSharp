#!/usr/bin/env bash

set -euo pipefail

readonly export_root="/srv/nfssharp-kernel-export"
readonly fixture_marker="${export_root}/.nfssharp-integration-fixture"
readonly exports_file="/etc/exports.d/nfssharp.exports"
readonly nfs_conf_file="/etc/nfs.conf.d/nfssharp.conf"

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this script as root (for example, sudo bash $0)." >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install --yes --no-install-recommends nfs-kernel-server rpcbind

if [[ -d "${export_root}" && ! -f "${fixture_marker}" ]]; then
  echo "Refusing to reuse ${export_root} without the fixture marker." >&2
  exit 1
fi

install -d -m 0777 "${export_root}"
touch "${fixture_marker}"
install -d -m 0755 /etc/exports.d /etc/nfs.conf.d

printf '%s\n' \
  "${export_root} *(rw,sync,no_subtree_check,no_root_squash,insecure,fsid=55)" \
  > "${exports_file}"

printf '%s\n' \
  '[nfsd]' \
  'udp = n' \
  'tcp = y' \
  'vers2 = n' \
  'vers3 = y' \
  'vers4 = n' \
  > "${nfs_conf_file}"

systemctl restart rpcbind.service
exportfs -ra
systemctl restart nfs-server.service

for attempt in {1..30}; do
  if rpcinfo -t 127.0.0.1 100000 2 >/dev/null 2>&1 &&
     rpcinfo -t 127.0.0.1 100005 3 >/dev/null 2>&1 &&
     rpcinfo -t 127.0.0.1 100003 3 >/dev/null 2>&1; then
    rpcinfo -p 127.0.0.1
    showmount -e 127.0.0.1
    exit 0
  fi

  sleep 2
done

echo "Linux kernel NFSv3 fixture did not become ready." >&2
systemctl --no-pager --full status rpcbind.service nfs-server.service >&2 || true
rpcinfo -p 127.0.0.1 >&2 || true
exit 1
