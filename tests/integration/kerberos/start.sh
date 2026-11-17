#!/usr/bin/env bash
set -euo pipefail

# All keys and the KDC database are generated inside this disposable container.
cat >/etc/krb5.conf <<'EOF'
[libdefaults]
 default_realm = TEST.LOCAL
 dns_lookup_kdc = false
 dns_lookup_realm = false
 rdns = false
[realms]
 TEST.LOCAL = {
  kdc = 127.0.0.1
 }
[domain_realm]
 .test.local = TEST.LOCAL
EOF
mkdir -p /run/krb5kdc /run/ganesha /run/rpcbind /run/dbus
kdb5_util create -s -P "$(head -c 32 /dev/urandom | base64)" -r TEST.LOCAL
kadmin.local -q 'addprinc -randkey nfs/nfs.test.local@TEST.LOCAL'
kadmin.local -q 'ktadd -k /etc/krb5.keytab nfs/nfs.test.local@TEST.LOCAL'
kadmin.local -q 'addprinc -randkey client@TEST.LOCAL'
kadmin.local -q 'ktadd -k /tmp/client.keytab client@TEST.LOCAL'
krb5kdc -n &
dbus-daemon --system --nofork &
rpcbind -w -f &
cat >/etc/ganesha/ganesha.conf <<'EOF'
NFS_CORE_PARAM { Protocols = 3; NFS_Port = 2049; MNT_Port = 20048; Enable_NLM = false; Enable_UDP = false; }
EXPORT { Export_Id = 1; Path = "/export"; Pseudo = "/export"; Access_Type = RW; Protocols = 3; Transports = TCP; SecType = krb5, krb5i, krb5p; Squash = No_Root_Squash; PrivilegedPort = false; FSAL { Name = MEM; } }
EXPORT { Export_Id = 2; Path = "/readonly"; Pseudo = "/readonly"; Access_Type = RO; Protocols = 3; Transports = TCP; SecType = krb5, krb5i, krb5p; Squash = No_Root_Squash; PrivilegedPort = false; FSAL { Name = MEM; } }
MEM { Inode_Size = 1114112; }
NFS_KRB5 { Active_krb5 = true; PrincipalName = "nfs"; KeytabPath = "/etc/krb5.keytab"; }
EOF
ganesha.nfsd -F -L /tmp/ganesha.log -f /etc/ganesha/ganesha.conf &
cleanup() {
    status=$?
    if [ "$status" -ne 0 ]; then cat /tmp/ganesha.log || true; fi
    jobs -pr | xargs -r kill || true
}
trap cleanup EXIT
for attempt in $(seq 1 30); do
    if rpcinfo -t 127.0.0.1 100003 3 >/dev/null 2>&1 && rpcinfo -t 127.0.0.1 100005 3 >/dev/null 2>&1; then break; fi
    if [ "$attempt" -eq 30 ]; then echo 'NFS server did not become ready' >&2; exit 1; fi
    sleep 1
done
kinit -k -t /tmp/client.keytab client@TEST.LOCAL
export NFSSHARP_RUN_KERBEROS_INTEGRATION=1
dotnet vstest /tests/NfsSharp.Tests.dll --TestCaseFilter:Category=KerberosIntegration
