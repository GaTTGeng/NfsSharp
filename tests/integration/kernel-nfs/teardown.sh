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

if [[ ! -d "${export_root}" ]] ||
   [[ -L "${export_root}" ]] ||
   [[ ! -f "${fixture_marker}" ]] ||
   [[ -L "${fixture_marker}" ]] ||
   [[ "$(cat -- "${fixture_marker}")" != "${fixture_marker_content}" ]]; then
  echo "No NfsSharp fixture marker found; leaving host configuration unchanged."
  exit 0
fi

remove_fixture_owned_config() {
  local path="$1"

  if [[ -f "${path}" ]] &&
     [[ ! -L "${path}" ]] &&
     [[ "$(head -n 1 -- "${path}")" == "${config_marker}" ]]; then
    rm -f -- "${path}"
  elif [[ -e "${path}" || -L "${path}" ]]; then
    echo "Leaving non-fixture configuration in place: ${path}" >&2
  fi
}

remove_fixture_owned_config "${exports_file}"
remove_fixture_owned_config "${nfs_conf_file}"
exportfs -ra || true
systemctl stop nfs-server.service || true

if [[ -f "${fixture_marker}" ]] &&
   [[ "$(realpath "${export_root}")" == "/srv/nfssharp-kernel-export" ]]; then
  find "${export_root}" -mindepth 1 -delete
  rmdir "${export_root}"
fi
