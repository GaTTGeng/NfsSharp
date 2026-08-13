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

rm -f "${exports_file}" "${nfs_conf_file}"
exportfs -ra || true
systemctl stop nfs-server.service || true

if [[ -f "${fixture_marker}" ]] &&
   [[ "$(realpath "${export_root}")" == "/srv/nfssharp-kernel-export" ]]; then
  find "${export_root}" -mindepth 1 -delete
  rmdir "${export_root}"
fi
