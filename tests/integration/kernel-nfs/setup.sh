#!/usr/bin/env bash

set -euo pipefail

readonly export_root="/srv/nfssharp-kernel-export"
readonly fixture_marker="${export_root}/.nfssharp-integration-fixture"
readonly fixture_marker_content="NfsSharp Linux kernel NFS integration fixture"
readonly exports_file="/etc/exports.d/nfssharp.exports"
readonly nfs_conf_file="/etc/nfs.conf.d/nfssharp.conf"
readonly config_marker="# Managed by the NfsSharp Linux kernel NFS integration fixture."

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this script as root (for example, sudo bash $0)." >&2
  exit 1
fi

require_fixture_owned_or_absent() {
  local path="$1"

  if [[ -L "${path}" ]]; then
    echo "Refusing to replace symbolic link ${path}." >&2
    exit 1
  fi

  if [[ -e "${path}" ]] &&
     { [[ ! -f "${path}" ]] || [[ "$(head -n 1 -- "${path}")" != "${config_marker}" ]]; }; then
    echo "Refusing to replace host-owned configuration ${path}." >&2
    exit 1
  fi
}

require_fixture_owned_or_absent "${exports_file}"
require_fixture_owned_or_absent "${nfs_conf_file}"

if [[ -L "${export_root}" ]]; then
  echo "Refusing to reuse symbolic link ${export_root}." >&2
  exit 1
fi

if [[ -e "${export_root}" ]] &&
   { [[ ! -d "${export_root}" ]] ||
     [[ ! -f "${fixture_marker}" ]] ||
     [[ -L "${fixture_marker}" ]] ||
     [[ "$(cat -- "${fixture_marker}")" != "${fixture_marker_content}" ]]; }; then
  echo "Refusing to reuse ${export_root} without a valid fixture marker." >&2
  exit 1
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install --yes --no-install-recommends nfs-kernel-server rpcbind

install -d -m 0777 "${export_root}"
printf '%s\n' "${fixture_marker_content}" > "${fixture_marker}"
install -d -m 0755 /etc/exports.d /etc/nfs.conf.d

printf '%s\n' \
  "${config_marker}" \
  "${export_root} *(rw,sync,no_subtree_check,no_root_squash,insecure,fsid=55)" \
  > "${exports_file}"

printf '%s\n' \
  "${config_marker}" \
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
