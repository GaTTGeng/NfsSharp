#!/usr/bin/env bash
set -euo pipefail

# All keys and the KDC database are generated inside this disposable container.
useradd -u 2001 -m alice
useradd -u 2002 -m bob
cat >/etc/idmapd.conf <<'EOF'
[General]
Verbosity = 0
Domain = test.local
Local-Realms = TEST.LOCAL
[Mapping]
Nobody-User = nobody
Nobody-Group = nogroup
[Translation]
Method = nsswitch
EOF
cat >/etc/krb5.conf <<'EOF'
[libdefaults]
 default_realm = TEST.LOCAL
 dns_lookup_kdc = false
 dns_lookup_realm = false
 rdns = false
 clockskew = 1
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
kadmin.local -q 'addprinc -randkey alice@TEST.LOCAL'
kadmin.local -q 'ktadd -k /tmp/alice.keytab alice@TEST.LOCAL'
kadmin.local -q 'addprinc -randkey bob@TEST.LOCAL'
kadmin.local -q 'ktadd -k /tmp/bob.keytab bob@TEST.LOCAL'
krb5kdc -n &
dbus-daemon --system --nofork &
rpcbind -w -f &
cat >/etc/ganesha/ganesha.conf <<'EOF'
NFS_CORE_PARAM { Protocols = 3; NFS_Port = 2049; MNT_Port = 20048; Enable_NLM = false; Enable_UDP = false; }
NFSV4 { DomainName = "test.local"; }
EXPORT { Export_Id = 1; Path = "/export"; Pseudo = "/export"; Access_Type = RW; Protocols = 3; Transports = TCP; SecType = krb5, krb5i, krb5p; Squash = No_Root_Squash; PrivilegedPort = false; FSAL { Name = MEM; } }
EXPORT { Export_Id = 2; Path = "/readonly"; Pseudo = "/readonly"; Access_Type = RO; Protocols = 3; Transports = TCP; SecType = krb5, krb5i, krb5p; Squash = No_Root_Squash; PrivilegedPort = false; FSAL { Name = MEM; } }
EXPORT { Export_Id = 3; Path = "/private"; Pseudo = "/private"; Access_Type = RW; Protocols = 3; Transports = TCP; SecType = krb5p; Squash = No_Root_Squash; PrivilegedPort = false; FSAL { Name = MEM; } }
MEM { Inode_Size = 1114112; }
NFS_KRB5 { Active_krb5 = true; PrincipalName = "nfs"; KeytabPath = "/etc/krb5.keytab"; }
EOF
ganesha.nfsd -F -L /tmp/ganesha.log -f /etc/ganesha/ganesha.conf &
cleanup() {
    status=$?
    if [ -d /results ]; then
        cp /tmp/ganesha.log /results/ganesha.log 2>/dev/null || true
        chmod -R a+rX /results 2>/dev/null || true
    fi
    if [ "$status" -ne 0 ]; then cat /tmp/ganesha.log || true; fi
    jobs -pr | xargs -r kill || true
}
trap cleanup EXIT
for attempt in $(seq 1 30); do
    if rpcinfo -t 127.0.0.1 100003 3 >/dev/null 2>&1 && rpcinfo -t 127.0.0.1 100005 3 >/dev/null 2>&1; then break; fi
    if [ "$attempt" -eq 30 ]; then echo 'NFS server did not become ready' >&2; exit 1; fi
    sleep 1
done
kinit -k -t /tmp/client.keytab -c /tmp/krb5cc_client client@TEST.LOCAL
kinit -k -t /tmp/alice.keytab -c /tmp/krb5cc_alice alice@TEST.LOCAL
kinit -k -t /tmp/bob.keytab -c /tmp/krb5cc_bob bob@TEST.LOCAL
cp /tmp/krb5cc_client /tmp/krb5cc_active
export KRB5CCNAME=FILE:/tmp/krb5cc_active
export NFSSHARP_RUN_KERBEROS_INTEGRATION=1
if [ -d /results ]; then
    dpkg-query --show --showformat='${Package} ${Version}\n' \
        nfs-ganesha nfs-ganesha-mem rpcbind krb5-kdc krb5-user libgssapi-krb5-2 \
        > /results/server-version.txt 2>&1 || true
    showmount -e 127.0.0.1 > /results/exports.txt 2>&1 || true
    dotnet vstest /tests/NfsSharp.Tests.dll \
        --TestCaseFilter:Category=KerberosIntegration \
        --Logger:trx \
        --ResultsDirectory:/results
else
    dotnet vstest /tests/NfsSharp.Tests.dll --TestCaseFilter:Category=KerberosIntegration
fi
