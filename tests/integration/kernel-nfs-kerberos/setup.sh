#!/usr/bin/env bash

set -euo pipefail

readonly export_root="/srv/nfssharp-kernel-kerberos"
readonly rw_export="${export_root}/export"
readonly ro_export="${export_root}/readonly"
readonly private_export="${export_root}/private"
readonly fixture_marker="${export_root}/.nfssharp-kerberos-integration-fixture"
readonly fixture_marker_content="NfsSharp Linux kernel NFS Kerberos integration fixture"
readonly created_users_file="${export_root}/.nfssharp-created-users"
readonly runtime_dir="/run/nfssharp-kernel-kerberos"
readonly kdc_dir="${runtime_dir}/kdc"
readonly client_dir="${runtime_dir}/client"
readonly gssproxy_dir="${runtime_dir}/gssproxy.d"
readonly exports_file="/etc/exports.d/nfssharp-kerberos.exports"
readonly nfs_conf_file="/etc/nfs.conf.d/nfssharp-kerberos.conf"
readonly gssproxy_dropin="/etc/systemd/system/gssproxy.service.d/nfssharp-kerberos.conf"
readonly config_marker="# Managed by the NfsSharp Linux kernel NFS Kerberos integration fixture."
readonly realm="TEST.LOCAL"
readonly service_principal="nfs/nfs.test.local@${realm}"
readonly kdc_port="18888"
readonly alice_user="nfssharp-alice"
readonly bob_user="nfssharp-bob"
readonly alice_uid="2001"
readonly bob_uid="2002"

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
require_fixture_owned_or_absent "${gssproxy_dropin}"

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

if [[ -f "${runtime_dir}/kdc.pid" ]]; then
  old_pid="$(cat -- "${runtime_dir}/kdc.pid" 2>/dev/null || true)"
  if [[ -n "${old_pid}" ]] &&
     [[ "$(ps -p "${old_pid}" -o comm= 2>/dev/null || true)" == "krb5kdc" ]]; then
    kill "${old_pid}" || true
  fi
fi

export DEBIAN_FRONTEND=noninteractive
apt-get update
apt-get install --yes --no-install-recommends \
  nfs-kernel-server \
  rpcbind \
  gssproxy \
  krb5-kdc \
  krb5-admin-server \
  krb5-user

modprobe -q auth_rpcgss || true
modprobe -q rpcsec_gss_krb5 || true
if [[ ! -e /proc/net/rpc/use-gss-proxy ]]; then
  echo "Kernel RPCSEC_GSS support (/proc/net/rpc/use-gss-proxy) is not available." >&2
  exit 1
fi

install -d -m 0755 "${export_root}"
printf '%s\n' "${fixture_marker_content}" > "${fixture_marker}"
install -d -m 0777 "${rw_export}" "${private_export}"
install -d -m 0755 "${ro_export}"

rm -rf -- "${runtime_dir}"
install -d -m 0755 "${runtime_dir}" "${gssproxy_dir}"
install -d -m 0700 "${kdc_dir}"
install -d -m 0777 "${client_dir}"

touch "${created_users_file}"
ensure_test_user() {
  local username="$1"
  local uid="$2"
  if id -u "${username}" >/dev/null 2>&1; then
    return
  fi
  if getent passwd "${uid}" >/dev/null 2>&1; then
    echo "UID ${uid} is already in use by another user." >&2
    exit 1
  fi
  useradd --uid "${uid}" --no-create-home --shell /usr/sbin/nologin "${username}"
  printf '%s\n' "${username}" >> "${created_users_file}"
}

ensure_test_user "${alice_user}" "${alice_uid}"
ensure_test_user "${bob_user}" "${bob_uid}"

cat > "${runtime_dir}/krb5.conf" <<EOF
[libdefaults]
    default_realm = ${realm}
    dns_lookup_kdc = false
    dns_lookup_realm = false
    rdns = false
    clockskew = 1
    default_tgs_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    default_tkt_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96
    permitted_enctypes = aes256-cts-hmac-sha1-96 aes128-cts-hmac-sha1-96

[realms]
    ${realm} = {
        kdc = 127.0.0.1:${kdc_port}
    }

[domain_realm]
    .test.local = ${realm}
    test.local = ${realm}
EOF
chmod 0644 "${runtime_dir}/krb5.conf"

cat > "${runtime_dir}/kdc.conf" <<EOF
[kdcdefaults]
    kdc_listen = 127.0.0.1:${kdc_port}
    kdc_tcp_listen = 127.0.0.1:${kdc_port}

[realms]
    ${realm} = {
        database_name = ${kdc_dir}/principal
        admin_keytab = ${kdc_dir}/kadm5.keytab
        acl_file = ${kdc_dir}/kadm5.acl
        key_stash_file = ${kdc_dir}/.k5.${realm}
        max_life = 10h 0m 0s
        max_renewable_life = 7d 0h 0m 0s
        supported_enctypes = aes256-cts-hmac-sha1-96:normal aes128-cts-hmac-sha1-96:normal
    }
EOF
chmod 0600 "${runtime_dir}/kdc.conf"
touch "${kdc_dir}/kadm5.acl"

export KRB5_CONFIG="${runtime_dir}/krb5.conf"
export KRB5_KDC_PROFILE="${runtime_dir}/kdc.conf"

kdb5_util create -s -P "$(head -c 32 /dev/urandom | base64)" -r "${realm}"
kadmin.local -q "addprinc -randkey ${service_principal}"
kadmin.local -q "ktadd -k ${runtime_dir}/krb5.keytab ${service_principal}"
kadmin.local -q "addprinc -randkey client@${realm}"
kadmin.local -q "ktadd -k ${client_dir}/client.keytab client@${realm}"
kadmin.local -q "addprinc -randkey ${alice_user}@${realm}"
kadmin.local -q "ktadd -k ${client_dir}/alice.keytab ${alice_user}@${realm}"
kadmin.local -q "addprinc -randkey ${bob_user}@${realm}"
kadmin.local -q "ktadd -k ${client_dir}/bob.keytab ${bob_user}@${realm}"

chmod 0600 "${runtime_dir}/krb5.keytab"
chmod 0644 "${client_dir}/client.keytab" "${client_dir}/alice.keytab" "${client_dir}/bob.keytab"

krb5kdc -P "${runtime_dir}/kdc.pid" </dev/null >/dev/null 2>&1

for attempt in {1..30}; do
  if kinit -k -t "${client_dir}/client.keytab" -c "${client_dir}/krb5cc_client" "client@${realm}" >/dev/null 2>&1; then
    break
  fi
  if [[ "${attempt}" -eq 30 ]]; then
    echo "Disposable MIT KDC did not become ready." >&2
    exit 1
  fi
  sleep 1
done

kinit -k -t "${client_dir}/alice.keytab" -c "${client_dir}/krb5cc_alice" "${alice_user}@${realm}"
kinit -k -t "${client_dir}/bob.keytab" -c "${client_dir}/krb5cc_bob" "${bob_user}@${realm}"
cp "${client_dir}/krb5cc_client" "${client_dir}/krb5cc_active"
chmod 0666 "${client_dir}/krb5cc_client" "${client_dir}/krb5cc_alice" "${client_dir}/krb5cc_bob" "${client_dir}/krb5cc_active"
if [[ -n "${SUDO_USER:-}" ]]; then
  chown -R "${SUDO_USER}" "${client_dir}" 2>/dev/null || true
fi

cat > "${runtime_dir}/gssproxy.conf" <<EOF
[gssproxy]
EOF

cat > "${gssproxy_dir}/24-nfs-server.conf" <<EOF
[service/nfs-server]
  mechs = krb5
  socket = /run/gssproxy.sock
  cred_store = keytab:${runtime_dir}/krb5.keytab
  trusted = yes
  kernel_nfsd = yes
  euid = 0
EOF

install -d -m 0755 /etc/systemd/system/gssproxy.service.d /etc/exports.d /etc/nfs.conf.d

printf '%s\n' \
  "${config_marker}" \
  '[Service]' \
  "Environment=KRB5_CONFIG=${runtime_dir}/krb5.conf" \
  'ExecStart=' \
  "ExecStart=/usr/sbin/gssproxy -D -c ${runtime_dir}/gssproxy.conf -C ${gssproxy_dir}" \
  > "${gssproxy_dropin}"

printf '%s\n' \
  "${config_marker}" \
  "${rw_export} *(rw,sync,no_subtree_check,no_root_squash,insecure,sec=krb5:krb5i:krb5p,fsid=56)" \
  "${ro_export} *(ro,sync,no_subtree_check,no_root_squash,insecure,sec=krb5:krb5i:krb5p,fsid=57)" \
  "${private_export} *(rw,sync,no_subtree_check,no_root_squash,insecure,sec=krb5p,fsid=58)" \
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

systemctl daemon-reload
systemctl restart gssproxy.service
systemctl restart rpcbind.service
exportfs -ra
systemctl restart nfs-server.service

for attempt in {1..30}; do
  if rpcinfo -t 127.0.0.1 100000 2 >/dev/null 2>&1 &&
     rpcinfo -t 127.0.0.1 100005 3 >/dev/null 2>&1 &&
     rpcinfo -t 127.0.0.1 100003 3 >/dev/null 2>&1 &&
     [[ "$(cat /proc/net/rpc/use-gss-proxy 2>/dev/null || echo 0)" == "1" ]]; then
    rpcinfo -p 127.0.0.1
    showmount -e 127.0.0.1
    exit 0
  fi

  sleep 1
done

echo "Linux kernel NFSv3 Kerberos fixture did not become ready." >&2
systemctl --no-pager --full status gssproxy.service rpcbind.service nfs-server.service >&2 || true
cat /proc/net/rpc/use-gss-proxy >&2 || true
rpcinfo -p 127.0.0.1 >&2 || true
exit 1
