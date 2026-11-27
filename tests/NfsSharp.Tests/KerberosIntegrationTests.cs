using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Logging;
using NfsSharp.Client;
using NfsSharp.Protocol;

namespace NfsSharp.Tests;

/// <summary>Exercises RPCSEC_GSS against an isolated MIT Kerberos and NFSv3 server fixture.</summary>
public sealed class KerberosIntegrationTests
{
    private const uint AuthTooWeak = 5;

    private static string ServerHost => GetEnv("NFSSHARP_KERBEROS_SERVER", "127.0.0.1");
    private static string ServicePrincipal => GetEnv("NFSSHARP_KERBEROS_SPN", "nfs/nfs.test.local@TEST.LOCAL");
    private static string Realm => GetEnv("NFSSHARP_KERBEROS_REALM", "TEST.LOCAL");
    private static string RwExport => GetEnv("NFSSHARP_KERBEROS_EXPORT", "/export");
    private static string RoExport => GetEnv("NFSSHARP_KERBEROS_READONLY_EXPORT", "/readonly");
    private static string PrivateExport => GetEnv("NFSSHARP_KERBEROS_PRIVATE_EXPORT", "/private");
    private static uint AliceUid => uint.TryParse(Environment.GetEnvironmentVariable("NFSSHARP_KERBEROS_ALICE_UID"), out var uid) ? uid : 2001u;
    private static string ClientKeytabPath => GetEnv("NFSSHARP_KERBEROS_CLIENT_KEYTAB", "/tmp/client.keytab");
    private static string AliceCcachePath => GetEnv("NFSSHARP_KERBEROS_ALICE_CCACHE", "/tmp/krb5cc_alice");
    private static string BobCcachePath => GetEnv("NFSSHARP_KERBEROS_BOB_CCACHE", "/tmp/krb5cc_bob");
    private static string ActiveCcachePath => GetEnv("NFSSHARP_KERBEROS_ACTIVE_CCACHE", "/tmp/krb5cc_active");
    private static string ServerKeytabPath => GetEnv("NFSSHARP_KERBEROS_SERVER_KEYTAB", "/etc/krb5.keytab");
    private static string RestartCommand => GetEnv("NFSSHARP_KERBEROS_RESTART_CMD", "/usr/local/bin/restart-ganesha");
    private static string? RekeyCommand => Environment.GetEnvironmentVariable("NFSSHARP_KERBEROS_REKEY_CMD");
    private static string ExpectedWeakServiceOutcome => GetEnv("NFSSHARP_KERBEROS_EXPECTED_WEAK_SERVICE_OUTCOME", "auth-too-weak");

    private static string GetEnv(string name, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ConnectsAndTransfersData(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, CreateOptions(service), timeout.Token);
        Assert.Equal(NfsType.Dir, (await client.GetAttributesAsync(client.RootHandle, timeout.Token)).Type);

        var path = $"kerberos-{Guid.NewGuid():N}";
        var payload = new byte[] { 0, 1, 2, 3, 255 };
        await using var input = new MemoryStream(payload);
        await client.WriteFileAsync(path, input, timeout.Token);
        await using var output = new MemoryStream();
        await client.ReadFileAsync(path, output, timeout.Token);
        Assert.Equal(payload, output.ToArray());
        await client.DeleteFileAsync(path, timeout.Token);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ReadOnlyExportRejectsCreate(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RoExport, CreateOptions(service), timeout.Token);
        Assert.Equal(NfsType.Dir, (await client.GetAttributesAsync(client.RootHandle, timeout.Token)).Type);

        await using var input = new MemoryStream([1, 2, 3]);
        var error = await Assert.ThrowsAsync<NfsException>(() =>
            client.WriteFileAsync($"denied-{Guid.NewGuid():N}", input, timeout.Token));
        Assert.Equal(NfsV3Status.RoFs, error.Status);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task RejectsUnknownServicePrincipal(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var options = new NfsClientBuilder()
            .WithKerberos($"nfs/missing.test.local@{Realm}", service)
            .WithPrivilegedSourcePort(false)
            .BuildOptions();
        await Assert.ThrowsAsync<NfsException>(() =>
            NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token));
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task PrincipalSpecificAuthorizationEnforcesOwnerModeBits(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var path = $"alice-private-{Guid.NewGuid():N}";
        var payload = "alice-secret-data"u8.ToArray();

        using var ccacheScope = CredentialCacheScope.Use(AliceCcachePath);
        await using var alice = await NfsV3Client.ConnectAsync(ServerHost, RwExport, CreateOptions(service), timeout.Token);
        try
        {
            await using var input = new MemoryStream(payload);
            await alice.WriteFileAsync(path, input, timeout.Token);
            await alice.ChmodAsync(path, 0x180, timeout.Token); // 0600 (owner read/write only)

            var attributes = await alice.GetAttributesAsync(path, timeout.Token);
            Assert.Equal(AliceUid, attributes.Uid);
            Assert.Equal(0x180u, attributes.Mode & 0x1FFu);

            await using var aliceRead = new MemoryStream();
            await alice.ReadFileAsync(path, aliceRead, timeout.Token);
            Assert.Equal(payload, aliceRead.ToArray());

            using (CredentialCacheScope.Use(BobCcachePath))
            {
                await using var bob = await NfsV3Client.ConnectAsync(ServerHost, RwExport, CreateOptions(service), timeout.Token);
                var lookup = await bob.LookupPathAsync(path, timeout.Token);

                var readError = await Assert.ThrowsAsync<NfsException>(() =>
                    bob.ReadAtAsync(lookup.Handle, 0, new byte[payload.Length], 0, payload.Length, timeout.Token));
                Assert.Equal(NfsV3Status.Access, readError.Status);

                var writeError = await Assert.ThrowsAsync<NfsException>(() =>
                    bob.WriteAtAsync(lookup.Handle, 0, new byte[] { 0x42 }, timeout.Token));
                Assert.Equal(NfsV3Status.Access, writeError.Status);
            }
        }
        finally
        {
            try { await alice.DeleteFileAsync(path, timeout.Token); }
            catch (NfsException) { }
        }
    }

    [KerberosIntegrationFact]
    [Trait("Category", "KerberosIntegration")]
    public async Task PrivacyOnlyExportRejectsWeakerServices()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await using (var privacyClient = await NfsV3Client.ConnectAsync(
            ServerHost, PrivateExport, CreateOptions(RpcSecGssService.Privacy), timeout.Token))
        {
            var path = $"private-{Guid.NewGuid():N}";
            var payload = new byte[] { 10, 20, 30, 40 };
            await using var input = new MemoryStream(payload);
            await privacyClient.WriteFileAsync(path, input, timeout.Token);
            await using var output = new MemoryStream();
            await privacyClient.ReadFileAsync(path, output, timeout.Token);
            Assert.Equal(payload, output.ToArray());
            await privacyClient.DeleteFileAsync(path, timeout.Token);
        }

        foreach (var weaker in new[] { RpcSecGssService.None, RpcSecGssService.Integrity })
        {
            await using var client = await NfsV3Client.ConnectAsync(
                ServerHost, PrivateExport, CreateOptions(weaker, maxRetries: 0), timeout.Token);
            var error = await Assert.ThrowsAsync<NfsException>(() =>
                client.GetAttributesAsync(client.RootHandle, timeout.Token));
            switch (ExpectedWeakServiceOutcome)
            {
                case "auth-too-weak":
                    Assert.Equal(AuthTooWeak, error.RpcAuthStatus);
                    break;
                case "nfs-access":
                    Assert.Equal(NfsV3Status.Access, error.Status);
                    break;
                default:
                    throw new InvalidOperationException(
                        $"Unsupported NFSSHARP_KERBEROS_EXPECTED_WEAK_SERVICE_OUTCOME '{ExpectedWeakServiceOutcome}'.");
            }
        }
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ConcurrentCallsAndAuthenticatedDestroyCleanup(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start();
        var options = CreateOptions(service, portmapPort: proxy.PortmapPort, maxOutstanding: 8);
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);

        var tasks = Enumerable.Range(0, 8)
            .Select(_ => client.GetAttributesAsync(client.RootHandle, timeout.Token))
            .ToArray();
        var results = await Task.WhenAll(tasks);
        Assert.All(results, attrs => Assert.Equal(NfsType.Dir, attrs.Type));

        await client.UnmountAsync(timeout.Token);
        Assert.Null(proxy.MutatorException);
        Assert.Contains(RpcSecGssProc.Init, proxy.ObservedGssProcs);
        Assert.Contains(RpcSecGssProc.Data, proxy.ObservedGssProcs);
        Assert.Contains(RpcSecGssProc.Destroy, proxy.ObservedGssProcs);

        var requestsAfterUnmount = proxy.ObservedNfsRequestCount;
        await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Equal(requestsAfterUnmount, proxy.ObservedNfsRequestCount);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task TamperedContextCompletionVerifierFailsClosed(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start((replyIndex, reply) =>
            replyIndex == 1 ? FlipReplyVerifierBit(reply) : reply);
        var options = CreateOptions(service, portmapPort: proxy.PortmapPort, maxRetries: 0);

        var error = await Assert.ThrowsAsync<NfsException>(() =>
            NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token));
        Assert.Null(proxy.MutatorException);
        Assert.Contains("completed context verifier", error.Message);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task TamperedDataReplyVerifierFailsClosedAndRetiresContext(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start((replyIndex, reply) =>
            replyIndex == 2 ? FlipReplyVerifierBit(reply) : reply);
        var options = CreateOptions(service, portmapPort: proxy.PortmapPort, maxRetries: 0);
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);

        var error = await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Null(proxy.MutatorException);
        Assert.Contains("reply verifier", error.Message);

        var requestsAfterFailure = proxy.ObservedNfsRequestCount;
        await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Equal(requestsAfterFailure, proxy.ObservedNfsRequestCount);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TamperedIntegrityReplyBodyFailsClosedAndRetiresContext(bool flipChecksum)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start((replyIndex, reply) =>
            replyIndex == 2
                ? (flipChecksum ? FlipIntegrityChecksumBit(reply) : FlipFirstBodyOpaqueBit(reply))
                : reply);
        var options = CreateOptions(RpcSecGssService.Integrity, portmapPort: proxy.PortmapPort, maxRetries: 0);
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);

        var error = await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Null(proxy.MutatorException);
        Assert.Contains("integrity result", error.Message);

        var requestsAfterFailure = proxy.ObservedNfsRequestCount;
        await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Equal(requestsAfterFailure, proxy.ObservedNfsRequestCount);
    }

    [KerberosIntegrationFact]
    [Trait("Category", "KerberosIntegration")]
    public async Task TamperedPrivacyReplyBodyFailsClosedAndRetiresContext()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start((replyIndex, reply) =>
            replyIndex == 2 ? FlipFirstBodyOpaqueBit(reply) : reply);
        var options = CreateOptions(RpcSecGssService.Privacy, portmapPort: proxy.PortmapPort, maxRetries: 0);
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);

        var error = await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Null(proxy.MutatorException);
        Assert.Contains("privacy result", error.Message);

        var requestsAfterFailure = proxy.ObservedNfsRequestCount;
        await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Equal(requestsAfterFailure, proxy.ObservedNfsRequestCount);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ExpiredContextFailsClosedAndRenewedTicketRollsOver(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var ccachePath = $"/tmp/krb5cc_expiry_{Guid.NewGuid():N}";
        try
        {
            var issuedAt = DateTimeOffset.UtcNow;
            await RunCommandAsync("kinit", ["-l", "2s", "-k", "-t", ClientKeytabPath, "-c", ccachePath, $"client@{Realm}"], timeout.Token);
            using var ccacheScope = CredentialCacheScope.Use(ccachePath);

            await using var client = await NfsV3Client.ConnectAsync(
                ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token);
            Assert.Equal(NfsType.Dir, (await client.GetAttributesAsync(client.RootHandle, timeout.Token)).Type);

            NfsException? expiryError = null;
            var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
            while (DateTimeOffset.UtcNow < deadline)
            {
                try
                {
                    await client.GetAttributesAsync(client.RootHandle, timeout.Token);
                    await Task.Delay(250, timeout.Token);
                }
                catch (NfsException ex)
                {
                    expiryError = ex;
                    break;
                }
            }

            Assert.NotNull(expiryError);
            Assert.True(
                expiryError.RpcAuthStatus is 13 or 14,
                $"Expected RPCSEC_GSS_CREDPROBLEM (13) or CTXPROBLEM (14), got auth_stat={expiryError.RpcAuthStatus}, message={expiryError.Message}");
            // Once retired by expiry, the existing client remains unusable with the same server auth_stat.
            var retiredError = await Assert.ThrowsAsync<NfsException>(() =>
                client.GetAttributesAsync(client.RootHandle, timeout.Token));
            Assert.Equal(expiryError.RpcAuthStatus, retiredError.RpcAuthStatus);

            // Wait past ticket lifetime + configured 1s clockskew before verifying a new handshake fails.
            var pastClockSkew = issuedAt.AddSeconds(4.5) - DateTimeOffset.UtcNow;
            if (pastClockSkew > TimeSpan.Zero)
                await Task.Delay(pastClockSkew, timeout.Token);
            await Assert.ThrowsAsync<NfsException>(() =>
                NfsV3Client.ConnectAsync(ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token));

            // Renewing the ticket and opening a new client rolls over cleanly.
            await RunCommandAsync("kinit", ["-k", "-t", ClientKeytabPath, "-c", ActiveCcachePath, $"client@{Realm}"], timeout.Token);
            await using var rolledOver = await NfsV3Client.ConnectAsync(
                ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token);
            Assert.Equal(NfsType.Dir, (await rolledOver.GetAttributesAsync(rolledOver.RootHandle, timeout.Token)).Type);
        }
        finally
        {
            try { File.Delete(ccachePath); }
            catch (IOException) { }
        }
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task DestroyedCredentialCachePreventsNewSessionButExistingSessionRemainsUsable(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var cache = $"/tmp/krb5cc_destroy_{Guid.NewGuid():N}";
        try
        {
            await RunCommandAsync("kinit", ["-k", "-t", ClientKeytabPath, "-c", cache, $"client@{Realm}"], timeout.Token);
            using var ccacheScope = CredentialCacheScope.Use(cache);
            await using var existing = await NfsV3Client.ConnectAsync(
                ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token);
            Assert.Equal(NfsType.Dir, (await existing.GetAttributesAsync(existing.RootHandle, timeout.Token)).Type);

            await RunCommandAsync("kdestroy", ["-c", ActiveCcachePath], timeout.Token);
            Assert.Equal(NfsType.Dir, (await existing.GetAttributesAsync(existing.RootHandle, timeout.Token)).Type);
            await Assert.ThrowsAsync<NfsException>(() =>
                NfsV3Client.ConnectAsync(ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token));
        }
        finally
        {
            try { File.Delete(cache); }
            catch (IOException) { }
        }
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ServiceKeytabRekeyPreservesExistingSessionAndAuthenticatesNewSession(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var freshCcache = $"/tmp/krb5cc_rekey_{Guid.NewGuid():N}";
        try
        {
            await using var existing = await NfsV3Client.ConnectAsync(
                ServerHost, RwExport, CreateOptions(service), timeout.Token);
            Assert.Equal(NfsType.Dir, (await existing.GetAttributesAsync(existing.RootHandle, timeout.Token)).Type);

            if (!string.IsNullOrWhiteSpace(RekeyCommand))
                await RunCommandAsync("bash", [RekeyCommand], timeout.Token);
            else
                await RunCommandAsync(
                    "kadmin.local",
                    ["-q", $"ktadd -k {ServerKeytabPath} {ServicePrincipal}"],
                    timeout.Token);

            // An already-established context continues operating with its negotiated session key.
            Assert.Equal(NfsType.Dir, (await existing.GetAttributesAsync(existing.RootHandle, timeout.Token)).Type);

            // A fresh credential cache forces a new service ticket issued under the rotated KVNO.
            await RunCommandAsync("kinit", ["-k", "-t", ClientKeytabPath, "-c", freshCcache, $"client@{Realm}"], timeout.Token);
            using var ccacheScope = CredentialCacheScope.Use(freshCcache);
            await using var rekeyed = await NfsV3Client.ConnectAsync(
                ServerHost, RwExport, CreateOptions(service), timeout.Token);
            Assert.Equal(NfsType.Dir, (await rekeyed.GetAttributesAsync(rekeyed.RootHandle, timeout.Token)).Type);

            var path = $"rekey-{Guid.NewGuid():N}";
            var payload = new byte[] { 7, 8, 9, 10 };
            await using var input = new MemoryStream(payload);
            await rekeyed.WriteFileAsync(path, input, timeout.Token);
            await using var output = new MemoryStream();
            await rekeyed.ReadFileAsync(path, output, timeout.Token);
            Assert.Equal(payload, output.ToArray());
            await rekeyed.DeleteFileAsync(path, timeout.Token);
        }
        finally
        {
            try { File.Delete(freshCcache); }
            catch (IOException) { }
        }
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task ServerRestartFailsExistingClientAndNewClientConnects(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var existing = await NfsV3Client.ConnectAsync(
            ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token);
        Assert.Equal(NfsType.Dir, (await existing.GetAttributesAsync(existing.RootHandle, timeout.Token)).Type);

        await RunCommandAsync("bash", [RestartCommand], timeout.Token);

        // The old client cannot be used after the server restarts; establish a new session.
        await Assert.ThrowsAsync<NfsException>(() => existing.GetAttributesAsync(existing.RootHandle, timeout.Token));
        await using var replacement = await NfsV3Client.ConnectAsync(
            ServerHost, RwExport, CreateOptions(service, maxRetries: 0), timeout.Token);
        Assert.Equal(NfsType.Dir, (await replacement.GetAttributesAsync(replacement.RootHandle, timeout.Token)).Type);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task RetrySafeCallDropsLateFirstAttemptReplyAndCompletesWithFreshSequence(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start(retryMode: RetryProxyMode.DropFirstDataReplyAndDeliverLateOnRetry);
        var options = CreateOptions(
            service,
            portmapPort: proxy.PortmapPort,
            maxRetries: 1,
            retryDelay: TimeSpan.Zero);
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);

        // First GETATTR disconnects after the server replies; retry receives the late first reply before its own.
        var attrs = await client.GetAttributesAsync(client.RootHandle, timeout.Token);
        Assert.Equal(NfsType.Dir, attrs.Type);
        Assert.Null(proxy.MutatorException);
        Assert.True(proxy.LateFirstAttemptReplyDelivered);

        var dataRequests = proxy.ObservedGssRequests.Where(r => r.GssProc == RpcSecGssProc.Data).ToArray();
        Assert.Equal(2, dataRequests.Length);
        Assert.Equal(dataRequests[0].ContextHandle, dataRequests[1].ContextHandle);
        Assert.NotEqual(dataRequests[0].Xid, dataRequests[1].Xid);
        Assert.Equal(dataRequests[0].SeqNum + 1, dataRequests[1].SeqNum);

        // The established context stays usable after discarding the late reply.
        Assert.Equal(NfsType.Dir, (await client.GetAttributesAsync(client.RootHandle, timeout.Token)).Type);
    }

    [KerberosIntegrationTheory]
    [Trait("Category", "KerberosIntegration")]
    [InlineData(RpcSecGssService.None)]
    [InlineData(RpcSecGssService.Integrity)]
    [InlineData(RpcSecGssService.Privacy)]
    public async Task RetryReplayedFirstAttemptVerifierFailsClosedAndRetiresContext(RpcSecGssService service)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var proxy = KerberosRpcProxy.Start(retryMode: RetryProxyMode.DropFirstDataReplyAndReplayVerifierOnRetry);
        var options = CreateOptions(
            service,
            portmapPort: proxy.PortmapPort,
            maxRetries: 1,
            retryDelay: TimeSpan.Zero);
        await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);

        // Replaying the real first-attempt reply under the retry's XID must fail sequence MIC verification.
        var error = await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Null(proxy.MutatorException);
        Assert.True(proxy.ReplayedFirstAttemptReplyDelivered);
        Assert.Contains("sequence number MIC mismatch", error.Message);

        var dataRequests = proxy.ObservedGssRequests.Where(r => r.GssProc == RpcSecGssProc.Data).ToArray();
        Assert.Equal(2, dataRequests.Length);
        Assert.Equal(dataRequests[0].ContextHandle, dataRequests[1].ContextHandle);
        Assert.NotEqual(dataRequests[0].Xid, dataRequests[1].Xid);
        Assert.Equal(dataRequests[0].SeqNum + 1, dataRequests[1].SeqNum);

        var requestsAfterFailure = proxy.ObservedNfsRequestCount;
        await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
        Assert.Equal(requestsAfterFailure, proxy.ObservedNfsRequestCount);
    }

    [KerberosIntegrationFact]
    [Trait("Category", "KerberosIntegration")]
    public async Task RealServerLogsAndExceptionsRedactTokensVerifiersAndProtectedPayloads()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var logger = new CapturingLogger();
        var diagnostics = new List<string>();
        var capturedSecrets = new List<byte[]>();
        var secretText = $"nfssharp-privacy-secret-{Guid.NewGuid():N}";
        var secretBytes = Encoding.UTF8.GetBytes(secretText);
        capturedSecrets.Add(secretBytes);

        // Exercise normal privacy I/O, verifier tampering, privacy-body tampering, and handshake rejection.
        await using (var proxy = KerberosRpcProxy.Start())
        {
            var options = CreateOptions(RpcSecGssService.Privacy, portmapPort: proxy.PortmapPort, logger: logger);
            await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);
            var path = $"redact-{Guid.NewGuid():N}";
            await using (var input = new MemoryStream(secretBytes))
                await client.WriteFileAsync(path, input, timeout.Token);
            await using (var output = new MemoryStream())
            {
                await client.ReadFileAsync(path, output, timeout.Token);
                Assert.Equal(secretBytes, output.ToArray());
            }
            await client.DeleteFileAsync(path, timeout.Token);
            await client.UnmountAsync(timeout.Token);

            capturedSecrets.AddRange(proxy.ObservedSensitiveWireBlobs);
        }

        await using (var proxy = KerberosRpcProxy.Start((replyIndex, reply) =>
            replyIndex == 2 ? FlipReplyVerifierBit(reply) : reply))
        {
            var options = CreateOptions(
                RpcSecGssService.Privacy,
                portmapPort: proxy.PortmapPort,
                maxRetries: 0,
                logger: logger);
            await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);
            var ex = await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
            diagnostics.Add(ex.ToString());
            capturedSecrets.AddRange(proxy.ObservedSensitiveWireBlobs);
        }

        await using (var proxy = KerberosRpcProxy.Start((replyIndex, reply) =>
            replyIndex == 2 ? FlipFirstBodyOpaqueBit(reply) : reply))
        {
            var options = CreateOptions(
                RpcSecGssService.Privacy,
                portmapPort: proxy.PortmapPort,
                maxRetries: 0,
                logger: logger);
            await using var client = await NfsV3Client.ConnectAsync(ServerHost, RwExport, options, timeout.Token);
            var ex = await Assert.ThrowsAsync<NfsException>(() => client.GetAttributesAsync(client.RootHandle, timeout.Token));
            diagnostics.Add(ex.ToString());
            capturedSecrets.AddRange(proxy.ObservedSensitiveWireBlobs);
        }

        var unknownPrincipalError = await Assert.ThrowsAsync<NfsException>(() =>
            NfsV3Client.ConnectAsync(
                ServerHost,
                RwExport,
                CreateOptions(
                    RpcSecGssService.Privacy,
                    maxRetries: 0,
                    logger: logger,
                    servicePrincipal: $"nfs/unknown.test.local@{Realm}"),
                timeout.Token));
        diagnostics.Add(unknownPrincipalError.ToString());

        var logMessages = logger.Snapshot();
        Assert.NotEmpty(logMessages);
        Assert.Contains(logMessages, m => m.Contains("RPCSEC_GSS context established", StringComparison.Ordinal));
        Assert.Contains(logMessages, m => m.Contains("RPCSEC_GSS reply verifier accepted", StringComparison.Ordinal));
        // Require real wire tokens/verifiers/protected bodies (>= 8 bytes) so redaction checks are non-vacuous.
        var wireSecrets = capturedSecrets.Where(blob => blob.Length >= 8).ToArray();
        Assert.True(wireSecrets.Length >= 4, $"Expected at least 4 sensitive wire blobs, captured {wireSecrets.Length}.");

        diagnostics.AddRange(logMessages);
        foreach (var entry in diagnostics)
        {
            Assert.DoesNotContain(secretText, entry, StringComparison.OrdinalIgnoreCase);
            foreach (var blob in wireSecrets)
            {
                Assert.DoesNotContain(Convert.ToBase64String(blob), entry, StringComparison.Ordinal);
                Assert.DoesNotContain(Convert.ToHexString(blob), entry, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    private static NfsClientOptions CreateOptions(
        RpcSecGssService service,
        int portmapPort = 111,
        int maxRetries = 2,
        int maxOutstanding = 32,
        TimeSpan? retryDelay = null,
        ILogger? logger = null,
        string? servicePrincipal = null)
    {
        var builder = new NfsClientBuilder()
            .WithKerberos(servicePrincipal ?? ServicePrincipal, service)
            .WithPrivilegedSourcePort(false)
            .WithPortmapPort(portmapPort)
            .WithMaxRetries(maxRetries)
            .WithMaxOutstandingRpcCallsPerConnection(maxOutstanding);
        if (retryDelay.HasValue)
            builder.WithRetryDelay(retryDelay.Value);
        if (logger is not null)
            builder.WithLogger(logger);
        return builder.BuildOptions();
    }

    private static async Task RunCommandAsync(string fileName, string[] arguments, CancellationToken ct)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in arguments)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start {fileName}.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);
        await process.WaitForExitAsync(ct);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException(
                $"{fileName} exited with code {process.ExitCode}: {stderr}{stdout}");
    }

    private static byte[] FlipReplyVerifierBit(byte[] reply)
    {
        var mutated = reply.ToArray();
        var verifierFlavor = BinaryPrimitives.ReadUInt32BigEndian(mutated.AsSpan(12, 4));
        var verifierLength = (int)BinaryPrimitives.ReadUInt32BigEndian(mutated.AsSpan(16, 4));
        Assert.Equal((uint)RpcSecGssFlavor.Gss, verifierFlavor);
        Assert.True(verifierLength > 0);
        mutated[20 + verifierLength - 1] ^= 0x01;
        return mutated;
    }

    private static byte[] FlipFirstBodyOpaqueBit(byte[] reply)
    {
        var mutated = reply.ToArray();
        var bodyOffset = GetAcceptedReplyBodyOffset(mutated);
        var opaqueLength = (int)BinaryPrimitives.ReadUInt32BigEndian(mutated.AsSpan(bodyOffset, 4));
        Assert.True(opaqueLength > 0);
        mutated[bodyOffset + 4 + opaqueLength - 1] ^= 0x01;
        return mutated;
    }

    private static byte[] FlipIntegrityChecksumBit(byte[] reply)
    {
        var mutated = reply.ToArray();
        var bodyOffset = GetAcceptedReplyBodyOffset(mutated);
        var databodyLength = (int)BinaryPrimitives.ReadUInt32BigEndian(mutated.AsSpan(bodyOffset, 4));
        var checksumOffset = bodyOffset + 4 + Pad4(databodyLength);
        var checksumLength = (int)BinaryPrimitives.ReadUInt32BigEndian(mutated.AsSpan(checksumOffset, 4));
        Assert.True(checksumLength > 0);
        mutated[checksumOffset + 4 + checksumLength - 1] ^= 0x01;
        return mutated;
    }

    private static int GetAcceptedReplyBodyOffset(ReadOnlySpan<byte> reply)
    {
        var verifierLength = (int)BinaryPrimitives.ReadUInt32BigEndian(reply.Slice(16, 4));
        return 20 + Pad4(verifierLength) + 4;
    }

    private static int Pad4(int length) => (length + 3) & ~3;

    private sealed class CredentialCacheScope : IDisposable
    {
        private readonly string _activePath;
        private readonly byte[] _previousContent;

        private CredentialCacheScope(string activePath, byte[] previousContent)
        {
            _activePath = activePath;
            _previousContent = previousContent;
        }

        public static CredentialCacheScope Use(string sourceCcachePath)
        {
            var activePath = ActiveCcachePath;
            var previousContent = File.ReadAllBytes(activePath);
            ReplaceActive(activePath, File.ReadAllBytes(sourceCcachePath));
            return new CredentialCacheScope(activePath, previousContent);
        }

        public void Dispose() => ReplaceActive(_activePath, _previousContent);

        private static void ReplaceActive(string activePath, byte[] content)
        {
            var temp = $"{activePath}.{Guid.NewGuid():N}";
            File.WriteAllBytes(temp, content);
            File.Move(temp, activePath, overwrite: true);
        }
    }

    private enum RetryProxyMode
    {
        None,
        DropFirstDataReplyAndDeliverLateOnRetry,
        DropFirstDataReplyAndReplayVerifierOnRetry
    }

    private sealed record ObservedGssRequest(uint Xid, RpcSecGssProc GssProc, uint SeqNum, byte[] ContextHandle);

    private sealed class CapturingLogger : ILogger
    {
        private readonly ConcurrentQueue<string> _messages = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var entry = exception is null
                ? formatter(state, exception)
                : $"{formatter(state, exception)} {exception}";
            _messages.Enqueue(entry);
        }

        public string[] Snapshot() => _messages.ToArray();
    }

    private sealed class KerberosRpcProxy : IAsyncDisposable
    {
        private readonly TcpListener _portmapListener;
        private readonly TcpListener _nfsListener;
        private readonly Func<int, byte[], byte[]>? _replyMutator;
        private readonly RetryProxyMode _retryMode;
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _portmapLoop;
        private readonly Task _nfsLoop;
        private readonly ConcurrentQueue<RpcSecGssProc> _observedGssProcs = new();
        private readonly ConcurrentQueue<ObservedGssRequest> _observedGssRequests = new();
        private readonly ConcurrentQueue<byte[]> _observedSensitiveWireBlobs = new();
        private readonly TaskCompletionSource<byte[]> _delayedFirstDataReply =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _observedNfsRequestCount;
        private int _dataRequestCount;
        private long _firstDataXid = -1;
        private long _retryDataXid = -1;
        private int _lateFirstAttemptReplyDelivered;
        private int _replayedFirstAttemptReplyDelivered;
        private int _replyIndex;
        private Exception? _mutatorException;

        private KerberosRpcProxy(Func<int, byte[], byte[]>? replyMutator, RetryProxyMode retryMode)
        {
            _replyMutator = replyMutator;
            _retryMode = retryMode;
            _portmapListener = new TcpListener(IPAddress.Loopback, 0);
            _nfsListener = new TcpListener(IPAddress.Loopback, 0);
            _portmapListener.Start();
            _nfsListener.Start();
            PortmapPort = ((IPEndPoint)_portmapListener.LocalEndpoint).Port;
            NfsPort = ((IPEndPoint)_nfsListener.LocalEndpoint).Port;
            _portmapLoop = RunPortmapLoopAsync(_cts.Token);
            _nfsLoop = RunNfsLoopAsync(_cts.Token);
        }

        public int PortmapPort { get; }
        public int NfsPort { get; }
        public int ObservedNfsRequestCount => Volatile.Read(ref _observedNfsRequestCount);
        public IReadOnlyCollection<RpcSecGssProc> ObservedGssProcs => _observedGssProcs.ToArray();
        public IReadOnlyList<ObservedGssRequest> ObservedGssRequests => _observedGssRequests.ToArray();
        public IReadOnlyCollection<byte[]> ObservedSensitiveWireBlobs => _observedSensitiveWireBlobs.ToArray();
        public bool LateFirstAttemptReplyDelivered => Volatile.Read(ref _lateFirstAttemptReplyDelivered) != 0;
        public bool ReplayedFirstAttemptReplyDelivered => Volatile.Read(ref _replayedFirstAttemptReplyDelivered) != 0;
        public Exception? MutatorException => Volatile.Read(ref _mutatorException);

        public static KerberosRpcProxy Start(
            Func<int, byte[], byte[]>? replyMutator = null,
            RetryProxyMode retryMode = RetryProxyMode.None) =>
            new(replyMutator, retryMode);

        private async Task RunPortmapLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await _portmapListener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => ForwardPortmapConnectionAsync(client, ct), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task ForwardPortmapConnectionAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                try
                {
                    await upstream.ConnectAsync(IPAddress.Loopback, 111, ct);
                    await using var clientStream = client.GetStream();
                    await using var upstreamStream = upstream.GetStream();

                    var request = await RpcRecordStream.ReceiveAsync(clientStream, ct);
                    var shouldRewrite = IsGetPortForNfs(request);
                    await RpcTransport.SendRecordAsync(upstreamStream, request, ct);
                    var reply = await RpcRecordStream.ReceiveAsync(upstreamStream, ct);
                    if (shouldRewrite && reply.Length >= 4)
                        BinaryPrimitives.WriteUInt32BigEndian(reply.AsSpan(reply.Length - 4, 4), (uint)NfsPort);
                    await RpcTransport.SendRecordAsync(clientStream, reply, ct);
                }
                catch { }
            }
        }

        private static bool IsGetPortForNfs(byte[] request)
        {
            try
            {
                var reader = new XdrReader(request);
                _ = reader.UInt(); // xid
                _ = reader.UInt(); // msg_type
                _ = reader.UInt(); // rpcvers
                var prog = reader.UInt();
                _ = reader.UInt(); // vers
                var proc = reader.UInt();
                _ = reader.UInt(); // cred flavor
                _ = reader.Opaque(); // cred body
                _ = reader.UInt(); // verf flavor
                _ = reader.Opaque(); // verf body
                if (prog != NfsRpcConstants.ProgPortmap || proc != NfsRpcConstants.PmapGetPort)
                    return false;
                var targetProgram = reader.UInt();
                return targetProgram == NfsRpcConstants.ProgNfs;
            }
            catch
            {
                return false;
            }
        }

        private async Task RunNfsLoopAsync(CancellationToken ct)
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var client = await _nfsListener.AcceptTcpClientAsync(ct);
                    _ = Task.Run(() => ForwardNfsConnectionAsync(client, ct), ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (SocketException) { }
        }

        private async Task ForwardNfsConnectionAsync(TcpClient client, CancellationToken ct)
        {
            using (client)
            using (var upstream = new TcpClient())
            {
                try
                {
                    await upstream.ConnectAsync(IPAddress.Loopback, 2049, ct);
                    await using var clientStream = client.GetStream();
                    await using var upstreamStream = upstream.GetStream();
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);

                    var c2s = PumpRequestsAsync(clientStream, upstreamStream, linked);
                    var s2c = PumpRepliesAsync(upstreamStream, clientStream, linked);
                    await Task.WhenAny(c2s, s2c);
                    await linked.CancelAsync();
                    client.Close();
                    upstream.Close();
                    await Task.WhenAll(c2s, s2c);
                }
                catch { }
            }
        }

        private async Task PumpRequestsAsync(Stream source, Stream destination, CancellationTokenSource linked)
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    var request = await RpcRecordStream.ReceiveAsync(source, linked.Token);
                    Interlocked.Increment(ref _observedNfsRequestCount);
                    RecordGssRequest(request);
                    await RpcTransport.SendRecordAsync(destination, request, linked.Token);
                }
            }
            catch
            {
                await linked.CancelAsync();
            }
        }

        private async Task PumpRepliesAsync(Stream source, Stream destination, CancellationTokenSource linked)
        {
            try
            {
                while (!linked.Token.IsCancellationRequested)
                {
                    var reply = await RpcRecordStream.ReceiveAsync(source, linked.Token);
                    RecordReplyWireBlobs(reply);
                    if (_retryMode != RetryProxyMode.None && reply.Length >= 4)
                    {
                        var replyXid = (long)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(0, 4));
                        if (replyXid == Volatile.Read(ref _firstDataXid))
                        {
                            // Hold the real first-attempt reply and sever the client socket to trigger a retry.
                            _delayedFirstDataReply.TrySetResult(reply);
                            await linked.CancelAsync();
                            return;
                        }

                        if (replyXid == Volatile.Read(ref _retryDataXid))
                        {
                            var delayed = await _delayedFirstDataReply.Task.WaitAsync(linked.Token);
                            if (_retryMode == RetryProxyMode.DropFirstDataReplyAndDeliverLateOnRetry)
                            {
                                await RpcTransport.SendRecordAsync(destination, delayed, linked.Token);
                                Volatile.Write(ref _lateFirstAttemptReplyDelivered, 1);
                                await RpcTransport.SendRecordAsync(destination, reply, linked.Token);
                                continue;
                            }

                            var replayed = delayed.ToArray();
                            BinaryPrimitives.WriteUInt32BigEndian(replayed.AsSpan(0, 4), (uint)replyXid);
                            await RpcTransport.SendRecordAsync(destination, replayed, linked.Token);
                            Volatile.Write(ref _replayedFirstAttemptReplyDelivered, 1);
                            continue;
                        }
                    }

                    var index = Interlocked.Increment(ref _replyIndex);
                    if (_replyMutator is not null)
                    {
                        try
                        {
                            reply = _replyMutator(index, reply);
                        }
                        catch (Exception ex)
                        {
                            Interlocked.CompareExchange(ref _mutatorException, ex, null);
                            throw;
                        }
                    }
                    await RpcTransport.SendRecordAsync(destination, reply, linked.Token);
                }
            }
            catch
            {
                await linked.CancelAsync();
            }
        }

        private void RecordGssRequest(byte[] request)
        {
            try
            {
                var reader = new XdrReader(request);
                var xid = reader.UInt(); // xid
                _ = reader.UInt(); // msg_type
                _ = reader.UInt(); // rpcvers
                _ = reader.UInt(); // prog
                _ = reader.UInt(); // vers
                _ = reader.UInt(); // proc
                var credFlavor = reader.UInt();
                var credBody = reader.Opaque();
                if (credFlavor != (uint)RpcSecGssFlavor.Gss)
                    return;

                var gssCred = new XdrReader(credBody);
                _ = gssCred.UInt(); // version
                var gssProc = (RpcSecGssProc)gssCred.UInt();
                var seqNum = gssCred.UInt();
                var service = (RpcSecGssService)gssCred.UInt();
                var contextHandle = gssCred.Opaque();
                _observedGssProcs.Enqueue(gssProc);
                _observedGssRequests.Enqueue(new ObservedGssRequest(xid, gssProc, seqNum, contextHandle));

                if (contextHandle.Length >= 8)
                    _observedSensitiveWireBlobs.Enqueue(contextHandle);

                _ = reader.UInt(); // verf flavor
                var verfBody = reader.Opaque();
                if (verfBody.Length >= 8)
                    _observedSensitiveWireBlobs.Enqueue(verfBody);

                if (gssProc is RpcSecGssProc.Init or RpcSecGssProc.ContinueInit)
                {
                    var initToken = reader.Opaque();
                    if (initToken.Length >= 8)
                        _observedSensitiveWireBlobs.Enqueue(initToken);
                }
                else if (gssProc == RpcSecGssProc.Data)
                {
                    if (service == RpcSecGssService.Privacy)
                    {
                        var wrappedBody = reader.Opaque();
                        if (wrappedBody.Length >= 8)
                            _observedSensitiveWireBlobs.Enqueue(wrappedBody);
                    }

                    if (_retryMode != RetryProxyMode.None)
                    {
                        var dataIndex = Interlocked.Increment(ref _dataRequestCount);
                        if (dataIndex == 1)
                            Volatile.Write(ref _firstDataXid, xid);
                        else if (dataIndex == 2)
                            Volatile.Write(ref _retryDataXid, xid);
                    }
                }
            }
            catch { }
        }

        private void RecordReplyWireBlobs(byte[] reply)
        {
            if (reply.Length < 24)
                return;

            var replyStat = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(8, 4));
            var verifierFlavor = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(12, 4));
            var verifierLength = (int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(16, 4));
            if (replyStat != 0 ||
                verifierFlavor != (uint)RpcSecGssFlavor.Gss ||
                verifierLength < 0 ||
                verifierLength > reply.Length - 20)
                return;

            if (verifierLength >= 8)
                _observedSensitiveWireBlobs.Enqueue(reply.AsSpan(20, verifierLength).ToArray());

            var bodyOffset = GetAcceptedReplyBodyOffset(reply);
            if (bodyOffset < 24 || bodyOffset > reply.Length - 4)
                return;

            var acceptStat = BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(bodyOffset - 4, 4));
            if (acceptStat != 0)
                return;

            var opaqueLength = (int)BinaryPrimitives.ReadUInt32BigEndian(reply.AsSpan(bodyOffset, 4));
            if (opaqueLength >= 16 && opaqueLength <= reply.Length - bodyOffset - 4)
                _observedSensitiveWireBlobs.Enqueue(reply.AsSpan(bodyOffset + 4, opaqueLength).ToArray());
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            _portmapListener.Stop();
            _nfsListener.Stop();
            try { await Task.WhenAll(_portmapLoop, _nfsLoop).WaitAsync(TimeSpan.FromSeconds(5)); }
            catch { }
            _cts.Dispose();
        }
    }
}

/// <summary>Enables the disposable Kerberos integration fixture explicitly.</summary>
public sealed class KerberosIntegrationFactAttribute : FactAttribute
{
    public KerberosIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("NFSSHARP_RUN_KERBEROS_INTEGRATION") != "1")
            Skip = "Set NFSSHARP_RUN_KERBEROS_INTEGRATION=1 to run Kerberos integration tests.";
    }
}

/// <summary>Enables the disposable Kerberos integration fixture explicitly.</summary>
public sealed class KerberosIntegrationTheoryAttribute : TheoryAttribute
{
    public KerberosIntegrationTheoryAttribute()
    {
        if (Environment.GetEnvironmentVariable("NFSSHARP_RUN_KERBEROS_INTEGRATION") != "1")
            Skip = "Set NFSSHARP_RUN_KERBEROS_INTEGRATION=1 to run Kerberos integration tests.";
    }
}
