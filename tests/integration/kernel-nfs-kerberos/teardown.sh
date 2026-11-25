#!/usr/bin/env bash

set -euo pipefail

readonly export_root="/srv/nfssharp-kernel-kerberos"
readonly fixture_marker="${export_root}/.nfssharp-kerberos-integration-fixture"
readonly fixture_marker_content="NfsSharp Linux kernel NFS Kerberos integration fixture"
readonly created_users_file="${export_root}/.nfssharp-created-users"
readonly runtime_dir="/run/nfssharp-kernel-kerberos"
readonly exports_file="/etc/exports.d/nfssharp-kerberos.exports"
readonly nfs_conf_file="/etc/nfs.conf.d/nfssharp-kerberos.conf"
readonly gssproxy_dropin="/etc/systemd/system/gssproxy.service.d/nfssharp-kerberos.conf"
readonly config_marker="# Managed by the NfsSharp Linux kernel NFS Kerberos integration fixture."

if [[ "${EUID}" -ne 0 ]]; then
  echo "Run this script as root (for example, sudo bash $0)." >&2
  exit 1
fi

if [[ ! -d "${export_root}" ]] ||
   [[ -L "${export_root}" ]] ||
   [[ ! -f "${fixture_marker}" ]] ||
   [[ -L "${fixture_marker}" ]] ||
   [[ "$(cat -- "${fixture_marker}")" != "${fixture_marker_content}" ]]; then
  echo "No NfsSharp Kerberos fixture marker found; leaving host configuration unchanged."
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
remove_fixture_owned_config "${gssproxy_dropin}"
rmdir /etc/systemd/system/gssproxy.service.d 2>/dev/null || true

exportfs -ra || true
systemctl stop nfs-server.service || true
systemctl daemon-reload || true
systemctl stop gssproxy.service || true

if [[ -f "${runtime_dir}/kdc.pid" ]]; then
  kdc_pid="$(cat -- "${runtime_dir}/kdc.pid" 2>/dev/null || true)"
  if [[ -n "${kdc_pid}" ]] &&
     [[ "$(ps -p "${kdc_pid}" -o comm= 2>/dev/null || true)" == "krb5kdc" ]]; then
    kill "${kdc_pid}" || true
  fi
fi

if [[ -f "${created_users_file}" ]]; then
  while IFS= read -r username; do
    if [[ -n "${username}" ]] && id -u "${username}" >/dev/null 2>&1; then
      userdel "${username}" || true
    fi
  done < "${created_users_file}"
fi

rm -rf -- "${runtime_dir}"

if [[ -f "${fixture_marker}" ]] &&
   [[ "$(realpath "${export_root}")" == "/srv/nfssharp-kernel-kerberos" ]]; then
  find "${export_root}" -mindepth 1 -delete
  rmdir "${export_root}"
fi
