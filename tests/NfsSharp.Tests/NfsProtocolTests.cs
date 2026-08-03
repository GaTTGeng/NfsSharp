using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NfsSharp.Client;
using NfsSharp.Protocol;
using Xunit.Abstractions;

namespace NfsSharp.Tests;

public class XdrTests
{
    private readonly ITestOutputHelper _output;
    public XdrTests(ITestOutputHelper output) { _output = output; }

    [Fact]
    public void XdrWriterReader_UInt_Roundtrip()
    {
        var writer = new XdrWriter();
        writer.UInt(42);
        writer.UInt(0);
        writer.UInt(uint.MaxValue);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        Assert.Equal(42u, reader.UInt());
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(uint.MaxValue, reader.UInt());
    }

    [Fact]
    public void XdrWriterReader_ULong_Roundtrip()
    {
        var writer = new XdrWriter();
        writer.ULong(1234567890123456789UL);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        Assert.Equal(1234567890123456789UL, reader.ULong());
    }

    [Fact]
    public void XdrWriterReader_Bool_Roundtrip()
    {
        var writer = new XdrWriter();
        writer.Bool(true);
        writer.Bool(false);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        Assert.True(reader.Bool());
        Assert.False(reader.Bool());
    }

    [Fact]
    public void XdrReader_Bool_RejectsInvalidWireValues()
    {
        var writer = new XdrWriter();
        writer.UInt(2);

        var ex = Assert.Throws<NfsException>(() => new XdrReader(writer.ToArray()).Bool());
        Assert.Contains("Malformed XDR boolean", ex.Message);
    }

    [Fact]
    public void XdrWriterReader_Opaque_Roundtrip()
    {
        var data = new byte[] { 1, 2, 3, 4, 5 };
        var writer = new XdrWriter();
        writer.Opaque(data);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        var result = reader.Opaque();
        Assert.Equal(data, result);
    }

    [Fact]
    public void XdrWriterReader_Str_Roundtrip()
    {
        var writer = new XdrWriter();
        writer.Str("hello world");
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        Assert.Equal("hello world", reader.Str());
    }

    [Fact]
    public void XdrWriterReader_MultipleFields()
    {
        var writer = new XdrWriter();
        writer.UInt(1);
        writer.Str("name");
        writer.Bool(true);
        writer.Opaque(new byte[] { 0xFF });
        writer.ULong(999);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        Assert.Equal(1u, reader.UInt());
        Assert.Equal("name", reader.Str());
        Assert.True(reader.Bool());
        Assert.Equal(new byte[] { 0xFF }, reader.Opaque());
        Assert.Equal(999UL, reader.ULong());
    }

    [Fact]
    public void XdrReader_ThrowsOnInsufficientData()
    {
        var writer = new XdrWriter();
        writer.UInt(1);
        writer.UInt(2);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        reader.UInt(); // ok
        reader.UInt(); // ok
        Assert.Throws<NfsException>(() => reader.UInt()); // should fail
    }

    [Fact]
    public void XdrReader_Remaining()
    {
        var writer = new XdrWriter();
        writer.UInt(1);
        writer.UInt(2);
        var bytes = writer.ToArray();

        var reader = new XdrReader(bytes);
        Assert.Equal(8, reader.Remaining);
        reader.UInt();
        Assert.Equal(4, reader.Remaining);
    }

    [Fact]
    public void XdrReader_RejectsNonZeroPadding()
    {
        var writer = new XdrWriter();
        writer.FixedBytes([0x01]);
        var bytes = writer.ToArray();
        bytes[1] = 0xFF;

        var ex = Assert.Throws<NfsException>(() => new XdrReader(bytes).FixedBytes(1));
        Assert.Contains("padding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void XdrReader_RejectsOpaqueLengthsAboveLimitBeforeAllocation()
    {
        var writer = new XdrWriter();
        writer.UInt((64u * 1024 * 1024) + 1);

        var ex = Assert.Throws<NfsException>(() => new XdrReader(writer.ToArray()).Opaque());
        Assert.Contains("too large", ex.Message);
    }
}

public class RpcReplyParserTests
{
    private const uint Xid = 0x10203040;

    [Fact]
    public void Decode_AcceptedSuccess_ExposesOnlyProcedurePayload()
    {
        var fixture = AcceptedReply(0, verifierFlavor: 0, verifier: []);
        fixture.UInt(0xCAFE_BABE);

        var reply = RpcReplyParser.Decode(fixture.ToArray(), Xid);

        Assert.Equal(0u, reply.VerifierFlavor);
        Assert.Empty(reply.Verifier);
        Assert.Equal(0xCAFE_BABEu, reply.Body.UInt());
    }

    [Theory]
    [InlineData(1u, "program unavailable")]
    [InlineData(3u, "procedure unavailable")]
    [InlineData(4u, "garbage arguments")]
    [InlineData(5u, "system error")]
    public void Decode_AcceptedFailures_RejectProcedureResult(uint acceptStatus, string expectedMessage)
    {
        var exception = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(AcceptedReply(acceptStatus).ToArray(), Xid));

        Assert.Contains(expectedMessage, exception.Message);
    }

    [Fact]
    public void Decode_AcceptedProgramMismatch_IncludesSupportedRange()
    {
        var fixture = AcceptedReply(2);
        fixture.UInt(2);
        fixture.UInt(4);

        var exception = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(fixture.ToArray(), Xid));

        Assert.Contains("2..4", exception.Message);
    }

    [Fact]
    public void Decode_RejectsInvalidAcceptedAndDeniedDiscriminators()
    {
        var invalidAccepted = Assert.Throws<NfsException>(
            () => RpcReplyParser.Decode(AcceptedReply(6).ToArray(), Xid));
        Assert.Contains("Invalid RPC accept_stat", invalidAccepted.Message);

        var invalidDenied = new XdrWriter();
        invalidDenied.UInt(Xid);
        invalidDenied.UInt(1);
        invalidDenied.UInt(1);
        invalidDenied.UInt(2);
        var deniedException = Assert.Throws<NfsException>(
            () => RpcReplyParser.Decode(invalidDenied.ToArray(), Xid));
        Assert.Contains("Invalid RPC reject_stat", deniedException.Message);
    }

    [Theory]
    [InlineData(1u, "bad credentials")]
    [InlineData(2u, "rejected credentials")]
    [InlineData(3u, "bad verifier")]
    [InlineData(4u, "rejected verifier")]
    [InlineData(5u, "credentials too weak")]
    [InlineData(6u, "invalid response verifier")]
    [InlineData(7u, "authentication failed")]
    [InlineData(8u, "Kerberos error")]
    [InlineData(9u, "ticket expired")]
    [InlineData(10u, "ticket file error")]
    [InlineData(11u, "credential decode error")]
    [InlineData(12u, "network address mismatch")]
    [InlineData(13u, "RPCSEC_GSS credential problem")]
    [InlineData(14u, "RPCSEC_GSS context problem")]
    public void Decode_DeniedAuthenticationFailures_RejectProcedureResult(uint authStatus, string expectedMessage)
    {
        var exception = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(DeniedAuthReply(authStatus).ToArray(), Xid));

        Assert.Contains(expectedMessage, exception.Message);
        Assert.Contains($"auth_stat={authStatus}", exception.Message);
    }

    [Fact]
    public void Decode_DeniedRpcMismatch_IncludesSupportedRange()
    {
        var fixture = new XdrWriter();
        fixture.UInt(Xid);
        fixture.UInt(1);
        fixture.UInt(1);
        fixture.UInt(0);
        fixture.UInt(2);
        fixture.UInt(3);

        var exception = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(fixture.ToArray(), Xid));

        Assert.Contains("2..3", exception.Message);
    }

    [Theory]
    [InlineData(0u, "xid mismatch")]
    [InlineData(1u, "Unexpected RPC message type")]
    [InlineData(2u, "Invalid RPC reply_stat")]
    public void Decode_RejectsInvalidEnvelopeOrderAndDiscriminators(uint malformedField, string expectedMessage)
    {
        var fixture = AcceptedReply(0);
        var bytes = fixture.ToArray();
        switch (malformedField)
        {
            case 0:
                bytes[3]++;
                break;
            case 1:
                bytes[7] = 0;
                break;
            case 2:
                bytes[11] = 2;
                break;
        }

        var exception = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(bytes, Xid));

        Assert.Contains(expectedMessage, exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Decode_RejectsMalformedOrOversizedVerifier()
    {
        var truncated = new XdrWriter();
        truncated.UInt(Xid);
        truncated.UInt(1);
        truncated.UInt(0);
        truncated.UInt(0);
        truncated.UInt(1);
        var truncatedException = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(truncated.ToArray(), Xid));
        Assert.Contains("Malformed XDR payload", truncatedException.Message);

        var oversized = AcceptedReply(0, verifierFlavor: 1, verifier: new byte[401]);
        var oversizedException = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(oversized.ToArray(), Xid));
        Assert.Contains("opaque length is too large", oversizedException.Message);

        var nonEmptyNone = AcceptedReply(0, verifierFlavor: 0, verifier: [1]);
        var noneException = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(nonEmptyNone.ToArray(), Xid));
        Assert.Contains("AUTH_NONE must be empty", noneException.Message);
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(15u)]
    public void Decode_RejectsInvalidDeniedAuthenticationStatus(uint authStatus)
    {
        var exception = Assert.Throws<NfsException>(() => RpcReplyParser.Decode(DeniedAuthReply(authStatus).ToArray(), Xid));

        Assert.Contains("Invalid RPC auth_stat", exception.Message);
    }

    private static XdrWriter AcceptedReply(uint acceptStatus, uint verifierFlavor = 0, byte[]? verifier = null)
    {
        var fixture = new XdrWriter();
        fixture.UInt(Xid);
        fixture.UInt(1);
        fixture.UInt(0);
        fixture.UInt(verifierFlavor);
        fixture.Opaque(verifier ?? []);
        fixture.UInt(acceptStatus);
        return fixture;
    }

    private static XdrWriter DeniedAuthReply(uint authStatus)
    {
        var fixture = new XdrWriter();
        fixture.UInt(Xid);
        fixture.UInt(1);
        fixture.UInt(1);
        fixture.UInt(1);
        fixture.UInt(authStatus);
        return fixture;
    }
}

public class RpcAuthSysTests
{
    [Fact]
    public void Encode_UsesUnsignedIdentifiersAndPermitsSixteenGroups()
    {
        uint[] groups = [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, uint.MaxValue];

        var encoded = RpcAuthSys.Encode(uint.MaxValue, "nfs-host", uint.MaxValue, 0, groups);
        var reader = new XdrReader(encoded);

        Assert.Equal(uint.MaxValue, reader.UInt());
        Assert.Equal("nfs-host", reader.Str());
        Assert.Equal(uint.MaxValue, reader.UInt());
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(16u, reader.UInt());
        foreach (var group in groups)
            Assert.Equal(group, reader.UInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void Encode_TruncatesMachineNameAtUtf8CharacterBoundary()
    {
        var encoded = RpcAuthSys.Encode(0, new string('\u00E9', 128), 0, 0, []);
        var reader = new XdrReader(encoded);

        reader.UInt();
        Assert.Equal(254, reader.Opaque().Length);
    }

    [Fact]
    public void Encode_RejectsMoreThanSixteenGroups()
    {
        var exception = Assert.Throws<NfsException>(() => RpcAuthSys.Encode(0, "host", 0, 0, Enumerable.Repeat(0u, 17).ToArray()));

        Assert.Contains("at most 16", exception.Message);
    }
}

public class NfsModelsTests
{
    [Fact]
    public async Task NfsV3Client_PortmapUnavailableMountServiceIsExplicit()
    {
        await using var portmap = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.UInt(0)));

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(portmap.Port), CancellationToken.None));

        Assert.Contains("mountd service is not registered in portmap", exception.Message);
        await portmap.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_PortmapUnavailableNfsServiceDoesNotFallBackTo2049()
    {
        await using var portmap = new RpcFixtureServer(2, (call, index) =>
            RpcFixtureServer.AcceptedReply(
                call.Xid,
                RpcFixtureServer.Success,
                writer => writer.UInt(index == 0 ? 2048u : 0u)));

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ConnectAsync("127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None));

        Assert.Contains("NFS service is not registered in portmap", exception.Message);
        await portmap.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_PortmapRejectsOutOfRangePort()
    {
        await using var portmap = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.UInt(65536)));

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(portmap.Port), CancellationToken.None));

        Assert.Contains("invalid TCP port 65536", exception.Message);
        await portmap.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_PreservesRpcProgramVersionAndProcedureRejections()
    {
        await using var programUnavailable = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.ProgramUnavailable));

        var programException = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(programUnavailable.Port), CancellationToken.None));

        Assert.Contains("prog=100000, vers=2, proc=3", programException.Message);
        Assert.Contains("program unavailable", programException.Message);
        await programUnavailable.WaitForRequestsAsync();

        await using var versionMismatch = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(
                call.Xid,
                RpcFixtureServer.ProgramMismatch,
                writer =>
                {
                    writer.UInt(3);
                    writer.UInt(4);
                }));

        var versionException = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(versionMismatch.Port), CancellationToken.None));

        Assert.Contains("program version mismatch", versionException.Message);
        Assert.Contains("supported range 3..4", versionException.Message);
        await versionMismatch.WaitForRequestsAsync();

        await using var procedureUnavailable = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.ProcedureUnavailable));

        var procedureException = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(procedureUnavailable.Port), CancellationToken.None));

        Assert.Contains("procedure unavailable", procedureException.Message);
        await procedureUnavailable.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_PreservesDeniedRpcContext()
    {
        await using var portmap = new RpcFixtureServer(1, call => RpcFixtureServer.DeniedVersionReply(call.Xid, 1, 2));

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(portmap.Port), CancellationToken.None));

        Assert.Contains("RPC message denied", exception.Message);
        Assert.Contains("prog=100000, vers=2, proc=3", exception.Message);
        Assert.Contains("supported range 1..2", exception.Message);
        await portmap.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_RejectsUnexpectedRpcReplyStatusWithoutDecodingDeniedBody()
    {
        await using var portmap = new RpcFixtureServer(1, call => RpcFixtureServer.ReplyWithStatus(call.Xid, 2));

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(portmap.Port), CancellationToken.None));

        Assert.Contains("Invalid RPC reply_stat discriminator: 2", exception.Message);
        Assert.Contains("prog=100000, vers=2, proc=3", exception.Message);
        await portmap.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_ListsEmptyAndGroupVariantExportReplies()
    {
        await using var emptyMount = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.Bool(false)));
        await using var emptyPortmap = CreateMountPortmap(emptyMount.Port);

        var empty = await NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(emptyPortmap.Port), CancellationToken.None);

        Assert.Empty(empty);
        await emptyPortmap.WaitForRequestsAsync();
        await emptyMount.WaitForRequestsAsync();

        await using var groupsMount = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer =>
            {
                writer.Bool(true);
                writer.Str("/data");
                writer.Bool(true);
                writer.Str("*");
                writer.Bool(true);
                writer.Str("admins");
                writer.Bool(false);
                writer.Bool(false);
            }));
        await using var groupsPortmap = CreateMountPortmap(groupsMount.Port);

        var exports = await NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(groupsPortmap.Port), CancellationToken.None);

        var export = Assert.Single(exports);
        Assert.Equal("/data", export.Path);
        Assert.Equal(["*", "admins"], export.Groups);
        await groupsPortmap.WaitForRequestsAsync();
        await groupsMount.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_PreservesMountStatusAndUnmountTransportFailure()
    {
        await using var deniedMount = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.UInt(MountV3Status.Access)));
        await using var deniedPortmap = new RpcFixtureServer(2, (call, index) =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.UInt(index == 0 ? (uint)deniedMount.Port : 2048)));

        var mountException = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ConnectAsync("127.0.0.1", "/denied", CreateFixtureOptions(deniedPortmap.Port), CancellationToken.None));

        Assert.Equal(MountV3Status.Access, mountException.Status);
        Assert.Contains("mountstat3=ACCESS (13)", mountException.Message);
        await deniedPortmap.WaitForRequestsAsync();
        await deniedMount.WaitForRequestsAsync();

        await using var nfs = new RpcFixtureServer(
            1,
            _ => throw new InvalidOperationException("NFS should not receive an RPC call."),
            readRequests: false);
        await using var mount = new RpcFixtureServer(2, (call, index) => index == 0
            ? RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer =>
            {
                writer.UInt(MountV3Status.Ok);
                writer.Opaque([0x01]);
                writer.UInt(0);
            })
            : RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.ProcedureUnavailable));
        await using var portmap = new RpcFixtureServer(2, (call, index) =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.UInt(index == 0 ? (uint)mount.Port : (uint)nfs.Port)));

        await using var client = await NfsV3Client.ConnectAsync("127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        var unmountException = await Assert.ThrowsAsync<NfsException>(() => client.UnmountAsync(CancellationToken.None));

        Assert.Contains("procedure unavailable", unmountException.Message);
        await client.UnmountAsync(CancellationToken.None);
        await portmap.WaitForRequestsAsync();
        await mount.WaitForRequestsAsync();
        await nfs.WaitForRequestsAsync();
    }

    [Fact]
    public void MountV3Status_DescribesKnownValues()
    {
        Assert.Equal("ACCESS", MountV3Status.Describe(MountV3Status.Access));
        Assert.Equal("10007", MountV3Status.Describe(10007));
    }

    [Fact]
    public void NfsFattr_Creation()
    {
        var attr = new NfsFattr(NfsType.Reg, 1024, DateTime.UtcNow)
        {
            Mode = 0x1A4,
            Uid = 1000,
            Gid = 1000
        };
        Assert.Equal(NfsType.Reg, attr.Type);
        Assert.Equal(1024, attr.Size);
        Assert.Equal(0x1A4u, attr.Mode);
    }

    [Fact]
    public void NfsLookup_Creation()
    {
        var handle = new byte[] { 1, 2, 3 };
        var lookup = new NfsLookup(handle, null);
        Assert.Equal(handle, lookup.Handle);
        Assert.Null(lookup.Attr);
    }

    [Fact]
    public void NfsClientOptions_Default()
    {
        var opts = NfsClientOptions.Default;
        Assert.Equal(30u, (uint)opts.CommandTimeout.TotalSeconds);
        Assert.True(opts.TcpKeepAlive);
        Assert.True(opts.TcpNoDelay);
    }

    [Fact]
    public void NfsClientOptions_RejectsInvalidRetryAndCacheOptions()
    {
        Assert.Throws<NfsException>(
            () => new NfsClientOptions { CommandTimeout = TimeSpan.FromMilliseconds(-1) }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions { StableHow = (NfsWriteStableHow)99 }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions { MaxRetries = -1 }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions { RetryDelay = TimeSpan.FromMilliseconds(-1) }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions
            {
                EnableDirectoryCache = true,
                DirectoryCacheTtl = TimeSpan.Zero
            }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions { KeepAliveInterval = TimeSpan.FromMilliseconds(-1) }.Validate());
    }

    [Fact]
    public void NfsV3Client_CanRetryTransient_AllowsOnlyRetrySafeProcedures()
    {
        Assert.True(NfsV3Client.CanRetryTransient(100000, 2, 3)); // PMAP GETPORT
        Assert.True(NfsV3Client.CanRetryTransient(100005, 3, 1)); // MOUNT MNT
        Assert.True(NfsV3Client.CanRetryTransient(100005, 3, 5)); // MOUNT EXPORT

        uint[] retrySafeNfsProcedures = [1, 3, 4, 5, 6, 16, 17, 18, 19, 20, 21];
        foreach (var proc in retrySafeNfsProcedures)
            Assert.True(NfsV3Client.CanRetryTransient(100003, 3, proc));

        uint[] mutationProcedures = [2, 7, 8, 9, 10, 11, 12, 13, 14, 15];
        foreach (var proc in mutationProcedures)
            Assert.False(NfsV3Client.CanRetryTransient(100003, 3, proc));

        Assert.False(NfsV3Client.CanRetryTransient(100005, 3, 3)); // MOUNT UMNT
        Assert.False(NfsV3Client.CanRetryTransient(100003, 4, 1));
        Assert.False(NfsV3Client.CanRetryTransient(42, 1, 1));
    }

    [Fact]
    public async Task NfsV3Client_WriteOperations_PrioritizeRequestedCancellation()
    {
        await using var client = CreateNfsV3Client();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteAtWithResultAsync([0x01], 0, new byte[] { 0x02 }, cancellation.Token));

        await using var input = new MemoryStream([0x03], writable: false);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteFileAsync([0x01], input, cancellation.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteFileAsync("cancelled.bin", input, cancellation.Token));
    }

    [Fact]
    public async Task NfsClient_WriteOperations_PrioritizeRequestedCancellationBeforeMount()
    {
        await using var client = new NfsClient(NfsVersion.V3, NfsClientOptions.Default);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteAtAsync([0x01], 0, new byte[] { 0x02 }, cancellation.Token));
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteAtWithResultAsync([0x01], 0, new byte[] { 0x02 }, cancellation.Token));

        await using var input = new MemoryStream([0x03], writable: false);
        await Assert.ThrowsAsync<OperationCanceledException>(
            () => client.WriteAsync("cancelled.bin", input, cancellation.Token));
    }

    [Fact]
    public void NfsV3Client_DirectoryPaging_RejectsNonterminalPagesWithoutProgress()
    {
        var method = typeof(NfsV3Client).GetMethod(
            "EnsureDirectoryReadProgress",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var emptyPage = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [0UL, 0UL, 0, false, "READDIR"]));
        Assert.Contains("without advancing its cookie", Assert.IsType<NfsException>(emptyPage.InnerException).Message);

        var repeatedCookie = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [4UL, 4UL, 1, false, "READDIRPLUS"]));
        Assert.Contains("READDIRPLUS", Assert.IsType<NfsException>(repeatedCookie.InnerException).Message);

        method.Invoke(null, [0UL, 4UL, 1, false, "READDIR"]);
        method.Invoke(null, [0UL, 0UL, 0, true, "READDIRPLUS"]);
    }

    [Fact]
    public void NfsV3Client_RpcRecordLength_RejectsAggregateOverflow()
    {
        RpcRecordStream.ValidateLength(1024, 1024L);
        RpcRecordStream.ValidateLength(RpcRecordStream.MaxRecordLength, 0);

        var ex = Assert.Throws<NfsException>(
            () => RpcRecordStream.ValidateLength(1, RpcRecordStream.MaxRecordLength));
        Assert.Contains("Invalid RPC record length", ex.Message);
    }

    [Fact]
    public void NfsV3Client_IsTransient_RecognizesWrappedTruncatedRecord()
    {
        var truncated = new NfsException("Truncated RPC record.", new EndOfStreamException());

        Assert.True(NfsV3Client.IsTransient(truncated));
    }

    [Fact]
    public async Task RpcRecordStream_ReassemblesFragmentsAndRejectsTruncation()
    {
        var record = Concat(
            RecordFragment(last: false, [0x01, 0x02]),
            RecordFragment(last: true, [0x03, 0x04, 0x05]));
        await using var stream = new MemoryStream(record, writable: false);

        Assert.Equal([0x01, 0x02, 0x03, 0x04, 0x05], await RpcRecordStream.ReceiveAsync(stream, CancellationToken.None));

        await using var truncated = new MemoryStream(
            Concat(RecordFragment(last: true, [0x10, 0x11])[..5]),
            writable: false);
        var ex = await Assert.ThrowsAsync<NfsException>(
            () => RpcRecordStream.ReceiveAsync(truncated, CancellationToken.None));
        Assert.Contains("Truncated RPC record", ex.Message);
    }

    private static byte[] RecordFragment(bool last, byte[] payload)
    {
        var record = new byte[sizeof(uint) + payload.Length];
        var marker = (uint)payload.Length | (last ? 0x8000_0000u : 0);
        BinaryPrimitives.WriteUInt32BigEndian(record, marker);
        payload.CopyTo(record, sizeof(uint));
        return record;
    }

    private static byte[] Concat(params byte[][] parts) => parts.SelectMany(part => part).ToArray();

    [Fact]
    public void NfsException_IsNotFound()
    {
        var ex = new NfsException("not found", NfsV3Status.NoEnt);
        Assert.True(ex.IsNotFound);
        Assert.Equal(NfsV3Status.NoEnt, ex.Status);
    }

    private static NfsClientOptions CreateFixtureOptions(int portmapPort) => new()
    {
        PortmapPort = portmapPort,
        CommandTimeout = TimeSpan.FromSeconds(5)
    };

    private static RpcFixtureServer CreateMountPortmap(int mountPort) => new(
        1,
        call => RpcFixtureServer.AcceptedReply(
            call.Xid,
            RpcFixtureServer.Success,
            writer => writer.UInt((uint)mountPort)));

    private static NfsV3Client CreateNfsV3Client()
    {
        var ctor = typeof(NfsV3Client).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IPAddress), typeof(NfsClientOptions)],
            modifiers: null);

        Assert.NotNull(ctor);
        return (NfsV3Client)ctor.Invoke([IPAddress.Loopback, NfsClientOptions.Default]);
    }

    [Fact]
    public void NfsV3Status_Describe()
    {
        Assert.Equal("OK", NfsV3Status.Describe(NfsV3Status.Ok));
        Assert.Equal("NOENT", NfsV3Status.Describe(NfsV3Status.NoEnt));
        Assert.Equal("STALE", NfsV3Status.Describe(NfsV3Status.Stale));
    }

    [Fact]
    public void NfsV4Status_UsesProtocolErrorCodesAndNames()
    {
        Assert.Equal(10008u, NfsV4Status.Delay);
        Assert.Equal("DELAY", NfsV4Status.Describe(NfsV4Status.Delay));

        Assert.Equal(10022u, NfsV4Status.StaleClientId);
        Assert.Equal("STALE_CLIENTID", NfsV4Status.Describe(NfsV4Status.StaleClientId));

        Assert.Equal(10023u, NfsV4Status.StaleStateId);
        Assert.Equal("STALE_STATEID", NfsV4Status.Describe(NfsV4Status.StaleStateId));

        Assert.Equal(10025u, NfsV4Status.BadStateId);
        Assert.Equal("BADSTATEID", NfsV4Status.Describe(NfsV4Status.BadStateId));

        Assert.Equal(10028u, NfsV4Status.LockRange);
        Assert.Equal("LOCK_RANGE", NfsV4Status.Describe(NfsV4Status.LockRange));

        Assert.Equal(10029u, NfsV4Status.SymLink);
        Assert.Equal("SYMLINK", NfsV4Status.Describe(NfsV4Status.SymLink));

        Assert.Equal(10044u, NfsV4Status.OpIllegal);
        Assert.Equal("OP_ILLEGAL", NfsV4Status.Describe(NfsV4Status.OpIllegal));
    }

    [Fact]
    public void NfsSetAttributes_Defaults()
    {
        Assert.Equal(0x1A4u, NfsSetAttributes.FileDefault.Mode);
        Assert.Equal(0x1EDu, NfsSetAttributes.DirectoryDefault.Mode);
    }

    [Fact]
    public void NfsTimestamp_PreservesRawNanosecondsAndConvertsToUtcDateTime()
    {
        var timestamp = new NfsTimestamp(1_704_158_645, 123_456_789);

        Assert.Equal(1_704_158_645u, timestamp.Seconds);
        Assert.Equal(123_456_789u, timestamp.Nanoseconds);
        Assert.Equal(
            DateTimeOffset.FromUnixTimeSeconds(timestamp.Seconds)
                .AddTicks(timestamp.Nanoseconds / 100)
                .UtcDateTime,
            timestamp.ToDateTimeUtc());

        var roundtrip = NfsTimestamp.FromDateTime(timestamp.ToDateTimeUtc());
        Assert.Equal(timestamp.Seconds, roundtrip.Seconds);
        Assert.Equal(123_456_700u, roundtrip.Nanoseconds);
    }

    [Fact]
    public void NfsAccessMode_Flags()
    {
        var mode = NfsAccessMode.Read | NfsAccessMode.Modify;
        Assert.True(mode.HasFlag(NfsAccessMode.Read));
        Assert.True(mode.HasFlag(NfsAccessMode.Modify));
        Assert.False(mode.HasFlag(NfsAccessMode.Execute));
    }

    [Fact]
    public void NfsV4Bitmap_Of_EncodesAttributeNumbersIntoMaskWords()
    {
        var bitmap = NfsV4Bitmap.Of(
            NfsV4Attr.Type,
            NfsV4Attr.Mode,
            NfsV4Attr.OwnerGroup);

        Assert.True(bitmap.HasAttr(NfsV4Attr.Type));
        Assert.True(bitmap.HasAttr(NfsV4Attr.Mode));
        Assert.True(bitmap.HasAttr(NfsV4Attr.OwnerGroup));
        Assert.False(bitmap.HasAttr(NfsV4Attr.Size));
        Assert.Equal([1u << 1, (1u << 1) | (1u << 5)], bitmap.Masks);

        var masks = bitmap.Masks;
        masks[0] = 0;
        Assert.True(bitmap.HasAttr(NfsV4Attr.Type));

        var writer = new XdrWriter();
        bitmap.Encode(writer);
        var reader = new XdrReader(writer.ToArray());

        Assert.Equal(2u, reader.UInt());
        Assert.Equal(1u << 1, reader.UInt());
        Assert.Equal((1u << 1) | (1u << 5), reader.UInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void NfsV4StateId_EncodesAndDecodesFixedStateIdFields()
    {
        var data = new byte[]
        {
            0x01, 0x02, 0x03, 0x04,
            0x10, 0x11, 0x12, 0x13,
            0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B
        };
        var expected = data.ToArray();
        var stateId = new NfsV4StateId(data);
        data[0] = 0xFF;

        var writer = new XdrWriter();
        stateId.Encode(writer);
        var reader = new XdrReader(writer.ToArray());

        Assert.Equal(0x01020304u, reader.UInt());
        Assert.Equal(expected[4..], reader.FixedBytes(12));
        Assert.Equal(0, reader.Remaining);

        writer = new XdrWriter();
        writer.UInt(0x01020304u);
        writer.FixedBytes(expected[4..]);

        var decoded = NfsV4StateId.Decode(new XdrReader(writer.ToArray()));
        Assert.Equal(expected, decoded.Data);

        var returned = decoded.Data;
        returned[4] = 0xFF;
        Assert.Equal(expected, decoded.Data);
    }

    [Fact]
    public void NfsV4StateId_StaticSpecialValues_UseProtocolDefinedWireValues()
    {
        var anonymousReader = EncodeStateId(NfsV4StateId.Anonymous);
        Assert.Equal(0u, anonymousReader.UInt());
        Assert.Equal(new byte[12], anonymousReader.FixedBytes(12));
        Assert.Equal(0, anonymousReader.Remaining);

        var specialReader = EncodeStateId(NfsV4StateId.Special);
        Assert.Equal(uint.MaxValue, specialReader.UInt());
        Assert.Equal(Enumerable.Repeat((byte)0xFF, 12).ToArray(), specialReader.FixedBytes(12));
        Assert.Equal(0, specialReader.Remaining);

        static XdrReader EncodeStateId(NfsV4StateId stateId)
        {
            var writer = new XdrWriter();
            stateId.Encode(writer);
            return new XdrReader(writer.ToArray());
        }
    }

    [Fact]
    public void NfsV4CompoundResponse_DecodesStatusFirstAndConsumesOperationPayloads()
    {
        var stateIdData = new byte[]
        {
            0x01, 0x02, 0x03, 0x04,
            0x10, 0x11, 0x12, 0x13,
            0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B
        };
        var fileHandle = new byte[] { 0xAA, 0xBB, 0xCC };

        var writer = new XdrWriter();
        writer.UInt(NfsV4Status.Ok);
        writer.Str("open-getfh");
        writer.UInt(3);
        writer.UInt((uint)NfsV4Op.PutRootFh);
        writer.UInt(NfsV4Status.Ok);
        writer.UInt((uint)NfsV4Op.Open);
        writer.UInt(NfsV4Status.Ok);
        new NfsV4StateId(stateIdData).Encode(writer);
        writer.Bool(true); // cinfo.atomic
        writer.ULong(10); // cinfo.before
        writer.ULong(11); // cinfo.after
        writer.UInt(0); // rflags
        NfsV4Bitmap.Of(NfsV4Attr.Size).Encode(writer);
        writer.UInt(0); // OPEN_DELEGATE_NONE
        writer.UInt((uint)NfsV4Op.GetFh);
        writer.UInt(NfsV4Status.Ok);
        writer.Opaque(fileHandle);

        var response = NfsV4CompoundResponse.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(NfsV4Status.Ok, response.Status);
        Assert.Equal("open-getfh", response.Tag);
        Assert.Equal(3, response.Results.Count);
        Assert.Equal(NfsV4Op.PutRootFh, response.Results[0].Op);
        Assert.Equal(NfsV4Op.Open, response.Results[1].Op);
        Assert.Equal(NfsV4Op.GetFh, response.Results[2].Op);
        Assert.Equal(stateIdData, NfsV4StateId.Decode(response.Results[1].Data!).Data);
        Assert.Equal(fileHandle, response.Results[2].Data!.Opaque());
        Assert.Equal(0, response.Results[2].Data!.Remaining);
    }

    [Fact]
    public void NfsV4CompoundResponse_CapturesRemoveAndRenameChangeInfoPayloads()
    {
        var fileHandle = new byte[] { 0xAA, 0xBB, 0xCC };

        var writer = new XdrWriter();
        writer.UInt(NfsV4Status.Ok);
        writer.Str("remove-rename-getfh");
        writer.UInt(3);
        writer.UInt((uint)NfsV4Op.Remove);
        writer.UInt(NfsV4Status.Ok);
        writer.Bool(true); // remove cinfo.atomic
        writer.ULong(10); // remove cinfo.before
        writer.ULong(11); // remove cinfo.after
        writer.UInt((uint)NfsV4Op.Rename);
        writer.UInt(NfsV4Status.Ok);
        writer.Bool(false); // rename source cinfo.atomic
        writer.ULong(20); // rename source cinfo.before
        writer.ULong(21); // rename source cinfo.after
        writer.Bool(true); // rename target cinfo.atomic
        writer.ULong(30); // rename target cinfo.before
        writer.ULong(31); // rename target cinfo.after
        writer.UInt((uint)NfsV4Op.GetFh);
        writer.UInt(NfsV4Status.Ok);
        writer.Opaque(fileHandle);

        var response = NfsV4CompoundResponse.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(NfsV4Op.Remove, response.Results[0].Op);
        Assert.Equal(NfsV4Op.Rename, response.Results[1].Op);
        Assert.Equal(NfsV4Op.GetFh, response.Results[2].Op);

        var removeReader = response.Results[0].Data!;
        Assert.True(removeReader.Bool());
        Assert.Equal(10UL, removeReader.ULong());
        Assert.Equal(11UL, removeReader.ULong());
        Assert.Equal(0, removeReader.Remaining);

        var renameReader = response.Results[1].Data!;
        Assert.False(renameReader.Bool());
        Assert.Equal(20UL, renameReader.ULong());
        Assert.Equal(21UL, renameReader.ULong());
        Assert.True(renameReader.Bool());
        Assert.Equal(30UL, renameReader.ULong());
        Assert.Equal(31UL, renameReader.ULong());
        Assert.Equal(0, renameReader.Remaining);

        Assert.Equal(fileHandle, response.Results[2].Data!.Opaque());
    }

    [Fact]
    public void NfsV4CompoundResponse_CapturesOpenNoneExtendedDelegation()
    {
        var stateIdData = new byte[]
        {
            0x01, 0x02, 0x03, 0x04,
            0x10, 0x11, 0x12, 0x13,
            0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B
        };
        var fileHandle = new byte[] { 0xAA, 0xBB, 0xCC };

        var writer = new XdrWriter();
        writer.UInt(NfsV4Status.Ok);
        writer.Str("open-none-ext-getfh");
        writer.UInt(2);
        writer.UInt((uint)NfsV4Op.Open);
        writer.UInt(NfsV4Status.Ok);
        new NfsV4StateId(stateIdData).Encode(writer);
        writer.Bool(false); // cinfo.atomic
        writer.ULong(20); // cinfo.before
        writer.ULong(21); // cinfo.after
        writer.UInt(0); // rflags
        NfsV4Bitmap.Of().Encode(writer);
        writer.UInt(3); // OPEN_DELEGATE_NONE_EXT
        writer.UInt(1); // WND4_CONTENTION
        writer.Bool(true); // ond_server_will_push_deleg
        writer.UInt((uint)NfsV4Op.GetFh);
        writer.UInt(NfsV4Status.Ok);
        writer.Opaque(fileHandle);

        var response = NfsV4CompoundResponse.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(NfsV4Op.Open, response.Results[0].Op);
        Assert.Equal(NfsV4Op.GetFh, response.Results[1].Op);

        var openReader = response.Results[0].Data!;
        Assert.Equal(stateIdData, NfsV4StateId.Decode(openReader).Data);
        Assert.False(openReader.Bool());
        Assert.Equal(20UL, openReader.ULong());
        Assert.Equal(21UL, openReader.ULong());
        Assert.Equal(0u, openReader.UInt());
        Assert.Empty(NfsV4Bitmap.Decode(openReader).Masks);
        Assert.Equal(3u, openReader.UInt());
        Assert.Equal(1u, openReader.UInt());
        Assert.True(openReader.Bool());
        Assert.Equal(0, openReader.Remaining);

        Assert.Equal(fileHandle, response.Results[1].Data!.Opaque());
    }

    [Fact]
    public void NfsV4CompoundResponse_CapturesOpenWriteDelegationBlockLimit()
    {
        var stateIdData = new byte[]
        {
            0x01, 0x02, 0x03, 0x04,
            0x10, 0x11, 0x12, 0x13,
            0x14, 0x15, 0x16, 0x17,
            0x18, 0x19, 0x1A, 0x1B
        };
        var delegationStateIdData = new byte[]
        {
            0x05, 0x06, 0x07, 0x08,
            0x20, 0x21, 0x22, 0x23,
            0x24, 0x25, 0x26, 0x27,
            0x28, 0x29, 0x2A, 0x2B
        };
        var fileHandle = new byte[] { 0xAA, 0xBB, 0xCC };

        var writer = new XdrWriter();
        writer.UInt(NfsV4Status.Ok);
        writer.Str("open-write-delegation-getfh");
        writer.UInt(2);
        writer.UInt((uint)NfsV4Op.Open);
        writer.UInt(NfsV4Status.Ok);
        new NfsV4StateId(stateIdData).Encode(writer);
        writer.Bool(true); // cinfo.atomic
        writer.ULong(30); // cinfo.before
        writer.ULong(31); // cinfo.after
        writer.UInt(0); // rflags
        NfsV4Bitmap.Of(NfsV4Attr.Size).Encode(writer);
        writer.UInt(2); // OPEN_DELEGATE_WRITE
        new NfsV4StateId(delegationStateIdData).Encode(writer);
        writer.Bool(false); // recall
        writer.UInt(2); // NFS_LIMIT_BLOCKS
        writer.UInt(4096); // num_blocks
        writer.UInt(512); // bytes_per_block
        writer.UInt(0); // ace.type
        writer.UInt(0); // ace.flag
        writer.UInt(0x001F01FF); // ace.access_mask
        writer.Str("OWNER@");
        writer.UInt((uint)NfsV4Op.GetFh);
        writer.UInt(NfsV4Status.Ok);
        writer.Opaque(fileHandle);

        var response = NfsV4CompoundResponse.Decode(new XdrReader(writer.ToArray()));

        Assert.Equal(NfsV4Op.Open, response.Results[0].Op);
        Assert.Equal(NfsV4Op.GetFh, response.Results[1].Op);

        var openReader = response.Results[0].Data!;
        Assert.Equal(stateIdData, NfsV4StateId.Decode(openReader).Data);
        Assert.True(openReader.Bool());
        Assert.Equal(30UL, openReader.ULong());
        Assert.Equal(31UL, openReader.ULong());
        Assert.Equal(0u, openReader.UInt());
        Assert.Equal([1u << 4], NfsV4Bitmap.Decode(openReader).Masks);
        Assert.Equal(2u, openReader.UInt());
        Assert.Equal(delegationStateIdData, NfsV4StateId.Decode(openReader).Data);
        Assert.False(openReader.Bool());
        Assert.Equal(2u, openReader.UInt());
        Assert.Equal(4096u, openReader.UInt());
        Assert.Equal(512u, openReader.UInt());
        Assert.Equal(0u, openReader.UInt());
        Assert.Equal(0u, openReader.UInt());
        Assert.Equal(0x001F01FFu, openReader.UInt());
        Assert.Equal("OWNER@", openReader.Str());
        Assert.Equal(0, openReader.Remaining);

        Assert.Equal(fileHandle, response.Results[1].Data!.Opaque());
    }

    [Fact]
    public void NfsV4Client_OpenNoCreate_EncodesClaimImmediatelyAfterOpenType()
    {
        var client = CreateNfsV4Client();
        var method = typeof(NfsV4Client).GetMethod("MakeOpenOp", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var op = (NfsV4Operation)method.Invoke(
            client,
            ["file.txt", NfsV4OpenShareAccess.Write, NfsV4OpenShareDeny.None])!;

        Assert.Equal(NfsV4Op.Open, op.Op);
        var reader = new XdrReader(op.Args!);
        Assert.Equal(0u, reader.UInt()); // seqid
        Assert.Equal((uint)NfsV4OpenShareAccess.Write, reader.UInt());
        Assert.Equal((uint)NfsV4OpenShareDeny.None, reader.UInt());
        Assert.Equal(0UL, reader.ULong()); // owner.clientid
        Assert.Equal("owner-0-0", reader.Str());
        Assert.Equal(0u, reader.UInt()); // OPEN4_NOCREATE
        Assert.Equal((uint)NfsV4OpenClaimType.Null, reader.UInt());
        Assert.Equal("file.txt", reader.Str());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void NfsV4Client_Copy_EncodesNfsV42CopyArgumentsInWireOrder()
    {
        var client = CreateNfsV4Client(minorVersion: 2);
        var method = typeof(NfsV4Client).GetMethod("MakeCopyOp", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var op = (NfsV4Operation)method.Invoke(client, [3UL, 5UL, 7UL])!;

        Assert.Equal(NfsV4Op.Copy, op.Op);
        var reader = new XdrReader(op.Args!);
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(new byte[12], reader.FixedBytes(12));
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(new byte[12], reader.FixedBytes(12));
        Assert.Equal(3UL, reader.ULong());
        Assert.Equal(5UL, reader.ULong());
        Assert.Equal(7UL, reader.ULong());
        Assert.False(reader.Bool());
        Assert.True(reader.Bool());
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void NfsV4Client_Clone_UsesCloneOpcodeAndArgumentLayout()
    {
        var client = CreateNfsV4Client(minorVersion: 2);
        var method = typeof(NfsV4Client).GetMethod("MakeCloneOp", BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var op = (NfsV4Operation)method.Invoke(client, [11UL, 13UL, 17UL])!;

        Assert.Equal(NfsV4Op.Clone, op.Op);
        Assert.Equal(71u, (uint)op.Op);
        var reader = new XdrReader(op.Args!);
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(new byte[12], reader.FixedBytes(12));
        Assert.Equal(0u, reader.UInt());
        Assert.Equal(new byte[12], reader.FixedBytes(12));
        Assert.Equal(11UL, reader.ULong());
        Assert.Equal(13UL, reader.ULong());
        Assert.Equal(17UL, reader.ULong());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    public void NfsV4Client_SecInfo_ResolvesParentDirectoryBeforeName()
    {
        var method = typeof(NfsV4Client).GetMethod("MakeParentLookupOps", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        object?[] args = ["/exports/project/file.txt", null];
        var ops = (List<NfsV4Operation>)method.Invoke(null, args)!;

        Assert.Equal("file.txt", args[1]);
        Assert.Equal([NfsV4Op.PutRootFh, NfsV4Op.Lookup, NfsV4Op.Lookup], ops.Select(op => op.Op));
        Assert.Null(ops[0].Args);
        Assert.Equal("exports", new XdrReader(ops[1].Args!).Str());
        Assert.Equal("project", new XdrReader(ops[2].Args!).Str());
    }

    [Fact]
    public void NfsV4Client_SecInfo_DecodesRpcSecGssOpaqueOid()
    {
        var writer = new XdrWriter();
        writer.UInt(2);
        writer.UInt(1); // AUTH_SYS
        writer.UInt(6); // RPCSEC_GSS
        writer.Opaque([0x2A, 0x86, 0x48, 0x86, 0xF7, 0x12, 0x01, 0x02, 0x02]); // Kerberos V5 OID
        writer.UInt(0); // qop
        writer.UInt(1); // rpc_gss_svc_none

        var method = typeof(NfsV4Client).GetMethod("DecodeSecInfoFlavors", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var flavors = (List<uint>)method.Invoke(null, [new XdrReader(writer.ToArray())])!;

        Assert.Equal([1u, 6u], flavors);
    }

    private static NfsV4Client CreateNfsV4Client(uint minorVersion = 0)
    {
        var ctor = typeof(NfsV4Client).GetConstructor(
            BindingFlags.Instance | BindingFlags.NonPublic,
            binder: null,
            [typeof(IPAddress), typeof(NfsClientOptions), typeof(uint)],
            modifiers: null);
        Assert.NotNull(ctor);

        return (NfsV4Client)ctor.Invoke([IPAddress.Loopback, NfsClientOptions.Default, minorVersion]);
    }

    [Fact]
    public void NfsWriteAndCommitResults_CarryVerifierData()
    {
        var verifier = new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 };

        var write = new NfsWriteResult(4, NfsWriteStableHow.FileSync, verifier);
        verifier[0] = 9;
        Assert.Equal(4, write.Count);
        Assert.Equal(NfsWriteStableHow.FileSync, write.Committed);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, write.WriteVerifier);
        var returnedWriteVerifier = write.WriteVerifier;
        returnedWriteVerifier[1] = 9;
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, write.WriteVerifier);

        var commitVerifier = new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 };
        var commit = new NfsCommitResult(commitVerifier);
        commitVerifier[0] = 9;
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, commit.WriteVerifier);
        var returnedCommitVerifier = commit.WriteVerifier;
        returnedCommitVerifier[1] = 9;
        Assert.Equal(new byte[] { 8, 7, 6, 5, 4, 3, 2, 1 }, commit.WriteVerifier);
    }

    [Fact]
    public void NfsWriteAndCommitResults_RejectInvalidValues()
    {
        Assert.Throws<NfsException>(
            () => new NfsWriteResult(-1, NfsWriteStableHow.FileSync, Array.Empty<byte>()));

        Assert.Throws<NfsException>(
            () => new NfsWriteResult(1, (NfsWriteStableHow)99, new byte[8]));

        Assert.Throws<ArgumentNullException>(
            () => new NfsWriteResult(1, NfsWriteStableHow.FileSync, null!));

        Assert.Throws<NfsException>(
            () => new NfsWriteResult(1, NfsWriteStableHow.FileSync, new byte[7]));

        Assert.Throws<ArgumentNullException>(
            () => new NfsCommitResult(null!));

        Assert.Throws<NfsException>(
            () => new NfsCommitResult(new byte[7]));
    }
}

internal sealed class RpcFixtureServer : IAsyncDisposable
{
    public const uint Success = 0;
    public const uint ProgramUnavailable = 1;
    public const uint ProgramMismatch = 2;
    public const uint ProcedureUnavailable = 3;

    private readonly TcpListener _listener;
    private readonly Task _serveTask;

    public RpcFixtureServer(
        int expectedRequests,
        Func<RpcFixtureCall, byte[]> reply,
        bool readRequests = true)
        : this(expectedRequests, (call, _) => reply(call), readRequests)
    {
    }

    public RpcFixtureServer(
        int expectedRequests,
        Func<RpcFixtureCall, int, byte[]> reply,
        bool readRequests = true)
    {
        if (expectedRequests <= 0)
            throw new ArgumentOutOfRangeException(nameof(expectedRequests));

        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _serveTask = ServeAsync(expectedRequests, reply, readRequests);
    }

    public int Port { get; }

    public Task WaitForRequestsAsync() => _serveTask.WaitAsync(TimeSpan.FromSeconds(5));

    public static byte[] AcceptedReply(uint xid, uint acceptStat, Action<XdrWriter>? result = null)
    {
        var writer = new XdrWriter();
        writer.UInt(xid);
        writer.UInt(1); // REPLY
        writer.UInt(0); // MSG_ACCEPTED
        writer.UInt(0); // AUTH_NONE verifier
        writer.Opaque(Array.Empty<byte>());
        writer.UInt(acceptStat);
        result?.Invoke(writer);
        return writer.ToArray();
    }

    public static byte[] DeniedVersionReply(uint xid, uint low, uint high)
    {
        var writer = new XdrWriter();
        writer.UInt(xid);
        writer.UInt(1); // REPLY
        writer.UInt(1); // MSG_DENIED
        writer.UInt(0); // RPC_MISMATCH
        writer.UInt(low);
        writer.UInt(high);
        return writer.ToArray();
    }

    public static byte[] ReplyWithStatus(uint xid, uint replyStatus)
    {
        var writer = new XdrWriter();
        writer.UInt(xid);
        writer.UInt(1); // REPLY
        writer.UInt(replyStatus);
        return writer.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        _listener.Stop();
        try
        {
            await _serveTask;
        }
        catch (OperationCanceledException)
        {
            // Listener shutdown cancels pending accepts.
        }
        catch (ObjectDisposedException)
        {
            // Listener shutdown races its accept loop.
        }
    }

    private async Task ServeAsync(int expectedRequests, Func<RpcFixtureCall, int, byte[]> reply, bool readRequests)
    {
        for (var index = 0; index < expectedRequests; index++)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            if (!readRequests)
                continue;

            var call = await ReadCallAsync(client.GetStream());
            var response = reply(call, index);
            await SendRecordAsync(client.GetStream(), response);
        }
    }

    private static async Task<RpcFixtureCall> ReadCallAsync(Stream stream)
    {
        var record = await ReadRecordAsync(stream);
        var reader = new XdrReader(record);
        var xid = reader.UInt();
        Assert.Equal(0u, reader.UInt()); // CALL
        Assert.Equal(2u, reader.UInt()); // RPC version
        var program = reader.UInt();
        var version = reader.UInt();
        var procedure = reader.UInt();
        reader.UInt(); // credential flavor
        reader.SkipOpaque();
        reader.UInt(); // verifier flavor
        reader.SkipOpaque();
        return new RpcFixtureCall(xid, program, version, procedure, reader.ReadRemainingBytes());
    }

    private static async Task<byte[]> ReadRecordAsync(Stream stream)
    {
        using var result = new MemoryStream();
        var last = false;
        var header = new byte[4];

        while (!last)
        {
            await stream.ReadExactlyAsync(header);
            var marker = BinaryPrimitives.ReadUInt32BigEndian(header);
            last = (marker & 0x8000_0000u) != 0;
            var length = checked((int)(marker & 0x7FFF_FFFF));
            var fragment = new byte[length];
            await stream.ReadExactlyAsync(fragment);
            result.Write(fragment);
        }

        return result.ToArray();
    }

    private static async Task SendRecordAsync(Stream stream, byte[] message)
    {
        var header = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(header, 0x8000_0000u | (uint)message.Length);
        await stream.WriteAsync(header);
        await stream.WriteAsync(message);
        await stream.FlushAsync();
    }
}

internal sealed record RpcFixtureCall(uint Xid, uint Program, uint Version, uint Procedure, byte[] Arguments);
