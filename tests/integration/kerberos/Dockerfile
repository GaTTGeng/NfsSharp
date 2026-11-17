FROM mcr.microsoft.com/dotnet/sdk:9.0-noble@sha256:eeaa543fc683baaae7389e83786f749add545f68cd2fecff19212f23a3bec58f

ENV DEBIAN_FRONTEND=noninteractive
RUN apt-get update && apt-get install --yes --no-install-recommends \
    nfs-ganesha=4.3-8ubuntu1 nfs-ganesha-mem=4.3-8ubuntu1 rpcbind=1.2.6-7ubuntu2 \
    krb5-kdc krb5-admin-server krb5-user \
    && rm -rf /var/lib/apt/lists/*
COPY start.sh /usr/local/bin/kerberos-integration
ENTRYPOINT ["bash", "/usr/local/bin/kerberos-integration"]
