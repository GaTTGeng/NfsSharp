#!/usr/bin/env bash

set -euo pipefail

if [[ "${EUID}" -ne 0 ]]; then
  exec sudo bash "$0" "$@"
fi

readonly runtime_dir="/run/nfssharp-kernel-kerberos"
readonly service_principal="nfs/nfs.test.local@TEST.LOCAL"

export KRB5_CONFIG="${runtime_dir}/krb5.conf"
export KRB5_KDC_PROFILE="${runtime_dir}/kdc.conf"

kadmin.local -q "ktadd -k ${runtime_dir}/krb5.keytab ${service_principal}"
