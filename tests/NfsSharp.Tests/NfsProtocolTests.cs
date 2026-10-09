using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using NfsSharp.Client;
using NfsSharp.Protocol;
using Xunit.Abstractions;

namespace NfsSharp.Tests;

/// <summary>XDR primitive encode/decode round-trips and rejection of malformed wire values.</summary>
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
    // The declared length is checked against the size limit before any buffer is allocated.
    public void XdrReader_RejectsOpaqueLengthsAboveLimitBeforeAllocation()
    {
        var writer = new XdrWriter();
        writer.UInt((64u * 1024 * 1024) + 1);

        var ex = Assert.Throws<NfsException>(() => new XdrReader(writer.ToArray()).Opaque());
        Assert.Contains("too large", ex.Message);
    }
}

/// <summary>RPC reply envelope decoding: accept/reject arms, status mapping, and verifier validation.</summary>
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
        // Minimal MSG_ACCEPTED reply carrying only the accept_stat discriminator (plus optional verifier).
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

/// <summary>AUTH_SYS credential encoding: unsigned ids, the 16-group limit, and machine-name truncation.</summary>
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
    // The AUTH_SYS machine name is capped at 255 UTF-8 bytes and must be cut on a character boundary (128 two-byte chars fit in 254 bytes).
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

/// <summary>
/// NFSv3 client behavior driven by scripted RPC fixtures (portmap/mount discovery, status mapping,
/// wire-field validation), plus public model construction and NFSv4 compound wire-format checks.
/// </summary>
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
    // A missing NFS registration must fail explicitly; the client must not silently fall back to the well-known port 2049.
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
        // Three independent MSG_ACCEPTED rejections: PROGRAM_UNAVAILABLE, PROGRAM_MISMATCH, PROCEDURE_UNAVAILABLE.
        // Each must surface the full prog/vers/proc call context so failures are diagnosable from the message alone.
        await using var programUnavailable = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.ProgramUnavailable));

        var programException = await Assert.ThrowsAsync<NfsException>(
            () => NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(programUnavailable.Port), CancellationToken.None));

        // PMAP GETPORT context is embedded in the failure text even though the call never reaches the mount program.
        Assert.Contains("prog=100000, vers=2, proc=3", programException.Message);
        Assert.Contains("program unavailable", programException.Message);
        await programUnavailable.WaitForRequestsAsync();

        // PROGRAM_MISMATCH carries the server's supported low/high version range as the result arm.
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
    // Unknown reply_stat values are rejected before any MSG_DENIED body is interpreted.
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
    // Invokes the private DecodeRpcReplyWithContext via reflection to assert the prog/vers/proc context is embedded in failures.
    public void NfsV3Client_RpcReplyFailuresIncludeRawCallContext()
    {
        var method = typeof(NfsV3Client).GetMethod(
            "DecodeRpcReplyWithContext",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var reply = RpcFixtureServer.AcceptedReply(42, RpcFixtureServer.ProcedureUnavailable);
        var exception = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [reply, 42u, 100003u, 3u, 0u]));

        var inner = Assert.IsType<NfsException>(exception.InnerException);
        Assert.Contains("prog=100003, vers=3, proc=0", inner.Message);
        Assert.Contains("procedure unavailable", inner.Message);
    }

    [Fact]
    public async Task NfsV3Client_ListsEmptyAndGroupVariantExportReplies()
    {
        // MOUNT EXPORT reply as a linked list of (value_follows, value) pairs; a leading false means an empty list.
        await using var emptyMount = new RpcFixtureServer(1, call =>
            RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, writer => writer.Bool(false)));
        await using var emptyPortmap = CreateMountPortmap(emptyMount.Port);

        var empty = await NfsV3Client.ListExportsAsync("127.0.0.1", CreateFixtureOptions(emptyPortmap.Port), CancellationToken.None);

        Assert.Empty(empty);
        await emptyPortmap.WaitForRequestsAsync();
        await emptyMount.WaitForRequestsAsync();

        // One export "/data" with a two-element group list ("*" wildcard plus a named group),
        // then two terminators: one ends the group list, the next ends the export list.
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
    // Two scenarios: a failed MOUNT preserves mountstat3 on the exception, and an unmount RPC failure is surfaced even though the connection stays usable.
    public async Task NfsV3Client_PreservesMountStatusAndUnmountTransportFailure()
    {
        // Scenario 1: MOUNT MNT succeeds at the RPC layer but returns mountstat3=ACCESS; the status must survive on NfsException.
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

        // Scenario 2: mount succeeds (MNT returns the fixture root handle), then the UMNT RPC fails.
        // The NFS endpoint is scripted to throw if any call arrives; only mount/unmount traffic is expected here.
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
        // A failed unmount must not poison the connection: a second unmount attempt is still served.
        await client.UnmountAsync(CancellationToken.None);
        await portmap.WaitForRequestsAsync();
        await mount.WaitForRequestsAsync();
        await nfs.WaitForRequestsAsync();
    }

    [Fact]
    public async Task NfsV3Client_ReadSideProceduresDecodeOptionalMetadataAndExpectedStatuses()
    {
        // RFC 1813 §§3.3.1 and 3.3.3-3.3.6 define these status-discriminated result arms.
        // Each procedure is scripted twice: once as an OK reply with its full payload, once as its failure status.
        uint[] procedures = [1, 1, 3, 3, 4, 4, 5, 5, 6, 6];
        await using var nfs = new RpcFixtureServer(procedures.Length, (call, index) =>
        {
            AssertNfsProcedure(call, procedures[index]);
            return index switch
            {
                // GETATTR OK: fattr3 with type/size/fileid asserted below.
                0 => NfsReply(call, writer =>
                {
                    writer.UInt(NfsV3Status.Ok);
                    WriteFattr3(writer, NfsType.Reg, 7, fileId: 42);
                }),
                1 => NfsReply(call, writer => writer.UInt(NfsV3Status.NoEnt)),
                // LOOKUP OK: file handle plus two absent post_op_attr (optional metadata stays null).
                2 => NfsReply(call, writer =>
                {
                    writer.UInt(NfsV3Status.Ok);
                    writer.Opaque([0xA1]);
                    WritePostOpAttr(writer, present: false);
                    WritePostOpAttr(writer, present: false);
                }),
                3 => NfsReply(call, writer => writer.UInt(NfsV3Status.NoEnt)),
                // ACCESS OK: absent post_op_attr then the granted access bitmap.
                4 => NfsReply(call, writer =>
                {
                    writer.UInt(NfsV3Status.Ok);
                    WritePostOpAttr(writer, present: false);
                    writer.UInt((uint)NfsAccessMode.Read);
                }),
                5 => NfsReply(call, writer => writer.UInt(NfsV3Status.Access)),
                // READLINK OK: absent post_op_attr then the symlink target path.
                6 => NfsReply(call, writer =>
                {
                    writer.UInt(NfsV3Status.Ok);
                    WritePostOpAttr(writer, present: false);
                    writer.Str("target/file");
                }),
                7 => NfsReply(call, writer => writer.UInt(NfsV3Status.Inval)),
                // READ OK: absent post_op_attr, count=3, eof=true, then a 3-byte data opaque.
                8 => NfsReply(call, writer =>
                {
                    writer.UInt(NfsV3Status.Ok);
                    WritePostOpAttr(writer, present: false);
                    writer.UInt(3);
                    writer.Bool(true);
                    writer.Opaque([0x10, 0x11, 0x12]);
                }),
                9 => NfsReply(call, writer => writer.UInt(NfsV3Status.IsDir)),
                _ => throw new InvalidOperationException($"Unexpected NFS fixture request {index}.")
            };
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            // Success and failure arms are exercised back to back so each procedure keeps its paired fixture order.
            var attr = await client.GetAttributesAsync(FixtureHandle, CancellationToken.None);
            Assert.Equal(NfsType.Reg, attr.Type);
            Assert.Equal(7, attr.Size);
            Assert.Equal(42ul, attr.FileId);
            // WriteFattr3 emits (0, 0) nfstime3 pairs; those are Unix epoch values, not absent timestamps.
            Assert.Equal(DateTime.UnixEpoch, attr.Mtime);
            Assert.Equal(DateTime.UnixEpoch, attr.Atime);
            Assert.Equal(DateTime.UnixEpoch, attr.Ctime);
            Assert.Equal(new NfsTimestamp(0, 0), attr.MtimeTimestamp);
            Assert.Equal(new NfsTimestamp(0, 0), attr.AtimeTimestamp);
            Assert.Equal(new NfsTimestamp(0, 0), attr.CtimeTimestamp);

            var getattrFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.GetAttributesAsync(FixtureHandle, CancellationToken.None));
            Assert.Equal(NfsV3Status.NoEnt, getattrFailure.Status);

            // LOOKUP preserves the handle and treats absent post_op_attr as a null attribute.
            var lookup = await client.LookupAsync(FixtureHandle, "entry", CancellationToken.None);
            Assert.Equal([0xA1], lookup.Handle);
            Assert.Null(lookup.Attr);

            var lookupFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.LookupAsync(FixtureHandle, "missing", CancellationToken.None));
            Assert.Equal(NfsV3Status.NoEnt, lookupFailure.Status);

            // The granted mask is intersected with the request: only Read remains even though Lookup was also asked for.
            var granted = await client.AccessAsync(
                FixtureHandle, NfsAccessMode.Read | NfsAccessMode.Lookup, CancellationToken.None);
            Assert.Equal(NfsAccessMode.Read, granted);

            var accessFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.AccessAsync(FixtureHandle, NfsAccessMode.Read, CancellationToken.None));
            Assert.Equal(NfsV3Status.Access, accessFailure.Status);

            Assert.Equal("target/file", await client.ReadLinkAsync(FixtureHandle, CancellationToken.None));

            var readLinkFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadLinkAsync(FixtureHandle, CancellationToken.None));
            Assert.Equal(NfsV3Status.Inval, readLinkFailure.Status);

            // A short READ body is fine: the count field, not the caller buffer size, bounds the copied bytes.
            var buffer = new byte[4];
            var read = await client.ReadAtAsync(FixtureHandle, 0, buffer, 0, buffer.Length, CancellationToken.None);
            Assert.Equal(3, read.BytesRead);
            Assert.True(read.Eof);
            Assert.Equal([0x10, 0x11, 0x12], buffer[..3]);

            var readFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadAtAsync(FixtureHandle, 0, new byte[1], 0, 1, CancellationToken.None));
            Assert.Equal(NfsV3Status.IsDir, readFailure.Status);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    public async Task NfsV3Client_PreservesEpochZeroTimestampsAndUsesPresenceFlagsForAbsentAttributes()
    {
        // RFC 1813 nfstime3 (0, 0) is the Unix epoch, a valid timestamp. Optional attributes are
        // signaled only by post_op_attr / name_attributes presence bits, never by zero timestamps.
        var nonZeroAtime = new NfsTimestamp(1_704_158_645, 123_456_789);
        var nonZeroCtime = new NfsTimestamp(1_704_158_700, 0);
        byte[] readDirVerifier = [0x30, 0x31, 0x32, 0x33, 0x34, 0x35, 0x36, 0x37];
        byte[] readDirPlusVerifier = [0x40, 0x41, 0x42, 0x43, 0x44, 0x45, 0x46, 0x47];
        uint[] procedures = [1, 1, 16, 17, 17];
        await using var nfs = new RpcFixtureServer(procedures.Length, (call, index) =>
        {
            AssertNfsProcedure(call, procedures[index]);
            switch (index)
            {
                // GETATTR OK: all three nfstime3 pairs are (0, 0) — Unix epoch, not absent values.
                case 0:
                    return NfsReply(call, writer =>
                    {
                        writer.UInt(NfsV3Status.Ok);
                        WriteFattr3(writer, NfsType.Reg, 0);
                    });
                // GETATTR OK: mixed timestamps so epoch zero is not confused with a decoding default.
                case 1:
                    return NfsReply(call, writer =>
                    {
                        writer.UInt(NfsV3Status.Ok);
                        WriteFattr3(
                            writer,
                            NfsType.Reg,
                            4,
                            fileId: 7,
                            atime: nonZeroAtime,
                            mtime: new NfsTimestamp(0, 0),
                            ctime: nonZeroCtime);
                    });
                // READDIR: names only — no attribute payload, so timestamp preservation is not applicable.
                case 2:
                    return NfsReply(call, writer => WriteReadDirResult(
                        writer, readDirVerifier, [(11ul, "epoch-name", 11ul)], eof: true));
                // READDIRPLUS with name_attributes present and zero timestamps on the entry.
                case 3:
                    return NfsReply(call, writer => WriteReadDirPlusResult(
                        writer,
                        readDirPlusVerifier,
                        [(12ul, "epoch-entry", 12ul)],
                        eof: true,
                        includeAttributes: true));
                // READDIRPLUS with mixed entry timestamps and an absent name_attributes arm nearby.
                case 4:
                    return NfsReply(call, writer =>
                    {
                        writer.UInt(NfsV3Status.Ok);
                        WritePostOpAttr(writer, present: false);
                        writer.FixedBytes(readDirPlusVerifier);
                        writer.Bool(true);
                        writer.ULong(13);
                        writer.Str("mixed-entry");
                        writer.ULong(13);
                        writer.Bool(true);
                        WriteFattr3(
                            writer,
                            NfsType.Reg,
                            0,
                            13,
                            nonZeroAtime,
                            new NfsTimestamp(0, 0),
                            nonZeroCtime);
                        writer.Bool(false);
                        writer.Bool(true);
                        writer.ULong(14);
                        writer.Str("absent-entry");
                        writer.ULong(14);
                        // name_attributes false: the protocol absence path, independent of timestamps.
                        WritePostOpAttr(writer, present: false);
                        writer.Bool(false);
                        writer.Bool(false);
                        writer.Bool(true);
                    });
                default:
                    throw new InvalidOperationException($"Unexpected timestamp fixture request {index}.");
            }
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            var epoch = await client.GetAttributesAsync(FixtureHandle, CancellationToken.None);
            Assert.Equal(DateTime.UnixEpoch, epoch.Mtime);
            Assert.Equal(DateTime.UnixEpoch, epoch.Atime);
            Assert.Equal(DateTime.UnixEpoch, epoch.Ctime);
            Assert.Equal(new NfsTimestamp(0, 0), epoch.MtimeTimestamp);
            Assert.Equal(new NfsTimestamp(0, 0), epoch.AtimeTimestamp);
            Assert.Equal(new NfsTimestamp(0, 0), epoch.CtimeTimestamp);

            var mixed = await client.GetAttributesAsync(FixtureHandle, CancellationToken.None);
            Assert.Equal(DateTime.UnixEpoch, mixed.Mtime);
            Assert.Equal(new NfsTimestamp(0, 0), mixed.MtimeTimestamp);
            Assert.Equal(nonZeroAtime, mixed.AtimeTimestamp);
            Assert.Equal(nonZeroCtime, mixed.CtimeTimestamp);
            Assert.Equal(nonZeroAtime.ToDateTimeUtc(), mixed.Atime);
            Assert.Equal(nonZeroCtime.ToDateTimeUtc(), mixed.Ctime);

            var entries = await client.ReadDirAsync(FixtureHandle, CancellationToken.None);
            Assert.Collection(entries, entry => Assert.Equal(new NfsEntry("epoch-name", 11), entry));

            var epochPlus = await client.ReadDirPlusAsync(FixtureHandle, CancellationToken.None);
            Assert.Collection(
                epochPlus,
                entry =>
                {
                    Assert.Equal("epoch-entry", entry.Name);
                    Assert.NotNull(entry.Attr);
                    Assert.Equal(DateTime.UnixEpoch, entry.Attr.Mtime);
                    Assert.Equal(DateTime.UnixEpoch, entry.Attr.Atime);
                    Assert.Equal(DateTime.UnixEpoch, entry.Attr.Ctime);
                    Assert.Equal(new NfsTimestamp(0, 0), entry.Attr.MtimeTimestamp);
                });

            var mixedPlus = await client.ReadDirPlusAsync(FixtureHandle, CancellationToken.None);
            Assert.Collection(
                mixedPlus,
                entry =>
                {
                    Assert.Equal("mixed-entry", entry.Name);
                    Assert.NotNull(entry.Attr);
                    Assert.Equal(DateTime.UnixEpoch, entry.Attr.Mtime);
                    Assert.Equal(nonZeroAtime, entry.Attr.AtimeTimestamp);
                    Assert.Equal(nonZeroCtime, entry.Attr.CtimeTimestamp);
                },
                entry =>
                {
                    // Absent attributes remain null via the name_attributes presence bit.
                    Assert.Equal("absent-entry", entry.Name);
                    Assert.Null(entry.Attr);
                    Assert.Null(entry.Handle);
                });
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    public async Task NfsV3Client_AccessRejectsResponseGrantsOutsideRequestedMask()
    {
        // Server grants MODIFY even though only Read was requested; the client must reject the inconsistency.
        await using var nfs = new RpcFixtureServer(1, call => NfsReply(call, writer =>
        {
            AssertNfsProcedure(call, 4);
            writer.UInt(NfsV3Status.Ok);
            WritePostOpAttr(writer, present: false);
            writer.UInt((uint)NfsAccessMode.Modify);
        }));
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            var exception = await Assert.ThrowsAsync<NfsException>(
                () => client.AccessAsync(FixtureHandle, NfsAccessMode.Read, CancellationToken.None));
            Assert.Contains("outside requested mask", exception.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    public async Task NfsV3Client_ReadRejectsInconsistentCountsAndNonTerminalEmptyResponses()
    {
        // RFC 1813 §3.3.6 requires count and data to describe the same READ result.
        // Three malformed replies: count above the request size, opaque longer than count, empty non-terminal read.
        await using var nfs = new RpcFixtureServer(3, (call, index) =>
        {
            AssertNfsProcedure(call, 6);
            return NfsReply(call, writer =>
            {
                writer.UInt(NfsV3Status.Ok);
                WritePostOpAttr(writer, present: false);
                switch (index)
                {
                    case 0: // count=3 for a 2-byte request
                        writer.UInt(3);
                        writer.Bool(true);
                        writer.Opaque([0x01, 0x02, 0x03]);
                        break;
                    case 1: // count=2 but the opaque carries 3 bytes
                        writer.UInt(2);
                        writer.Bool(true);
                        writer.Opaque([0x04, 0x05, 0x06]);
                        break;
                    case 2: // eof=false with zero bytes: a non-terminal page that cannot advance the file offset
                        writer.UInt(0);
                        writer.Bool(false);
                        writer.Opaque([]);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected READ fixture request {index}.");
                }
            });
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            // Each failure maps to a distinct validation message so the exact wire defect is identifiable.
            var countFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadAtAsync(FixtureHandle, 0, new byte[2], 0, 2, CancellationToken.None));
            Assert.Contains("count 3 for 2 byte request", countFailure.Message);

            var dataFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadAtAsync(FixtureHandle, 0, new byte[2], 0, 2, CancellationToken.None));
            Assert.Contains("XDR opaque length is too large", dataFailure.Message);

            // ReadFileAsync streams until eof; a zero-byte non-terminal reply must fail instead of spinning forever.
            await using var output = new MemoryStream();
            var progressFailure = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadFileAsync(FixtureHandle, output, CancellationToken.None));
            Assert.Contains("non-terminal response without data", progressFailure.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    // A short READ body is valid when MaxReadSize is large; only the returned count limits the data.
    public async Task NfsV3Client_ReadFileAllowsShortResponsesWithLargeConfiguredReadLimit()
    {
        // MaxReadSize is deliberately above 64 MiB so the reply of 1 byte is a short body relative to the request.
        const int largeReadLimit = 64 * 1024 * 1024 + 1;
        await using var nfs = new RpcFixtureServer(1, call => NfsReply(call, writer =>
        {
            AssertNfsProcedure(call, 6);
            // The READ request must carry handle, offset 0, and the full configured count.
            var request = new XdrReader(call.Arguments);
            Assert.Equal(FixtureHandle, request.Opaque());
            Assert.Equal(0ul, request.ULong());
            Assert.Equal((uint)largeReadLimit, request.UInt());

            writer.UInt(NfsV3Status.Ok);
            WritePostOpAttr(writer, present: false);
            writer.UInt(1);
            writer.Bool(true);
            writer.Opaque([0x5A]);
        }));
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var options = CreateFixtureOptions(portmap.Port) with { MaxReadSize = largeReadLimit };
        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", options, CancellationToken.None);
        try
        {
            // Only the returned count (1 byte) is written; eof=true ends the streaming loop.
            await using var output = new MemoryStream();
            await client.ReadFileAsync(FixtureHandle, output, CancellationToken.None);
            Assert.Equal([0x5A], output.ToArray());
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    // fattr3 size is unsigned 64-bit on the wire, but the public model exposes Int64; values above that range must be rejected.
    public async Task NfsV3Client_GetAttrRejectsFileSizesOutsideThePublicModelRange()
    {
        // Wire size of 2^64-1 cannot be represented by the Int64 public model and must be rejected at decode time.
        await using var nfs = new RpcFixtureServer(1, call => NfsReply(call, writer =>
        {
            AssertNfsProcedure(call, 1);
            writer.UInt(NfsV3Status.Ok);
            WriteFattr3(writer, NfsType.Reg, ulong.MaxValue);
        }));
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            var exception = await Assert.ThrowsAsync<NfsException>(
                () => client.GetAttributesAsync(FixtureHandle, CancellationToken.None));
            Assert.Contains("exceeds the supported Int64 range", exception.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    public async Task NfsV3Client_DirectoryResultsContinueCookiesAndRejectMalformedPages()
    {
        // RFC 1813 §§3.3.16-3.3.17 require cookie/verifier continuation for non-terminal pages.
        // Two multi-page traversals (READDIR and READDIRPLUS) followed by status and paging-defect cases.
        byte[] readDirVerifier = [0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x16, 0x17];
        byte[] readDirPlusVerifier = [0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27];
        uint[] procedures = [16, 16, 17, 17, 16, 17, 16, 17];
        await using var nfs = new RpcFixtureServer(procedures.Length, (call, index) =>
        {
            AssertNfsProcedure(call, procedures[index]);
            switch (index)
            {
                // Page 1 of READDIR: starts at cookie 0 with a zeroed verifier, ends at cookie 11.
                case 0:
                    AssertReadDirRequest(call, expectedCookie: 0, new byte[8], plus: false);
                    return NfsReply(call, writer => WriteReadDirResult(
                        writer, readDirVerifier, [(1ul, "first", 11ul)], eof: false));
                // Page 2 continues with the verifier echoed from page 1 and the prior entry cookie.
                case 1:
                    AssertReadDirRequest(call, expectedCookie: 11, readDirVerifier, plus: false);
                    return NfsReply(call, writer => WriteReadDirResult(
                        writer, readDirVerifier, [(2ul, "second", 22ul)], eof: true));
                case 2:
                    AssertReadDirRequest(call, expectedCookie: 0, new byte[8], plus: true);
                    return NfsReply(call, writer => WriteReadDirPlusResult(
                        writer, readDirPlusVerifier, [(3ul, "third", 33ul)], eof: false));
                case 3:
                    AssertReadDirRequest(call, expectedCookie: 33, readDirPlusVerifier, plus: true);
                    return NfsReply(call, writer => WriteReadDirPlusResult(
                        writer, readDirPlusVerifier, [(4ul, "fourth", 44ul)], eof: true));
                // Status arms surface as NfsException without any client-side cookie bookkeeping.
                case 4:
                    return NfsReply(call, writer => writer.UInt(NfsV3Status.BadCookie));
                case 5:
                    return NfsReply(call, writer => writer.UInt(NfsV3Status.NotDir));
                // Empty non-terminal page: no entries and eof=false cannot advance the cookie.
                case 6:
                    return NfsReply(call, writer => WriteReadDirResult(
                        writer, new byte[8], [], eof: false));
                // Entry whose cookie repeats the page start (0), so the page is stalled.
                case 7:
                    return NfsReply(call, writer => WriteReadDirPlusResult(
                        writer, new byte[8], [(5ul, "stalled", 0ul)], eof: false));
                default:
                    throw new InvalidOperationException($"Unexpected directory fixture request {index}.");
            }
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            // Both paginated traversals must stitch pages via cookie/verifier and yield entry fileids in order.
            var entries = await client.ReadDirAsync(FixtureHandle, CancellationToken.None);
            Assert.Collection(
                entries,
                entry => Assert.Equal(new NfsEntry("first", 1), entry),
                entry => Assert.Equal(new NfsEntry("second", 2), entry));

            // READDIRPLUS entries without attributes/handles decode as nulls rather than default values.
            var plusEntries = await client.ReadDirPlusAsync(FixtureHandle, CancellationToken.None);
            Assert.Collection(
                plusEntries,
                entry =>
                {
                    Assert.Equal("third", entry.Name);
                    Assert.Null(entry.Attr);
                    Assert.Null(entry.Handle);
                },
                entry =>
                {
                    Assert.Equal("fourth", entry.Name);
                    Assert.Null(entry.Attr);
                    Assert.Null(entry.Handle);
                });

            var readDirStatus = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadDirAsync(FixtureHandle, CancellationToken.None));
            Assert.Equal(NfsV3Status.BadCookie, readDirStatus.Status);

            var readDirPlusStatus = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadDirPlusAsync(FixtureHandle, CancellationToken.None));
            Assert.Equal(NfsV3Status.NotDir, readDirPlusStatus.Status);

            var emptyPage = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadDirAsync(FixtureHandle, CancellationToken.None));
            Assert.Contains("without advancing its cookie", emptyPage.Message);

            var stalledPage = await Assert.ThrowsAsync<NfsException>(
                () => client.ReadDirPlusAsync(FixtureHandle, CancellationToken.None));
            Assert.Contains("READDIRPLUS", stalledPage.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    public async Task NfsV3Client_MutationResultsPreserveStatusesAndOptionalMetadata()
    {
        // RFC 1813 §§3.3.2 and 3.3.8-3.3.15 use WCC data even when optional
        // pre- and post-operation attributes are unavailable.
        // Eight mutation procedures, each scripted as an OK reply then a typical failure status.
        uint[] procedures = [2, 2, 8, 8, 9, 9, 12, 12, 13, 13, 14, 14, 15, 15, 10, 10];
        await using var nfs = new RpcFixtureServer(procedures.Length, (call, index) =>
        {
            AssertNfsProcedure(call, procedures[index]);
            return NfsReply(call, writer =>
            {
                switch (index)
                {
                    // SETATTR: bare WCC data on both arms (no handle/attr payload).
                    case 0:
                        writer.UInt(NfsV3Status.Ok);
                        WriteWccData(writer);
                        break;
                    case 1:
                        writer.UInt(NfsV3Status.Access);
                        WriteWccData(writer);
                        break;
                    // CREATE / SYMLINK: diropres3 carries handle + post_op_attr on success, WCC on failure.
                    case 2:
                        writer.UInt(NfsV3Status.Ok);
                        WriteDiropResult(writer, [0xC8]);
                        break;
                    case 3:
                        writer.UInt(NfsV3Status.Exist);
                        WriteWccData(writer);
                        break;
                    case 4:
                        writer.UInt(NfsV3Status.Ok);
                        WriteDiropResult(writer, [0xC9]);
                        break;
                    case 5:
                        writer.UInt(NfsV3Status.Exist);
                        WriteWccData(writer);
                        break;
                    // REMOVE / RMDIR / RENAME: WCC-only results; RENAME carries two cinfo structures.
                    case 6:
                        writer.UInt(NfsV3Status.Ok);
                        WriteWccData(writer);
                        break;
                    case 7:
                        writer.UInt(NfsV3Status.NoEnt);
                        WriteWccData(writer);
                        break;
                    case 8:
                        writer.UInt(NfsV3Status.Ok);
                        WriteWccData(writer);
                        break;
                    case 9:
                        writer.UInt(NfsV3Status.NotEmpty);
                        WriteWccData(writer);
                        break;
                    case 10:
                        writer.UInt(NfsV3Status.Ok);
                        WriteWccData(writer);
                        WriteWccData(writer);
                        break;
                    case 11:
                        writer.UInt(NfsV3Status.NoEnt);
                        WriteWccData(writer);
                        WriteWccData(writer);
                        break;
                    // LINK: absent post_op_attr for the source plus WCC for the new name's directory.
                    case 12:
                        writer.UInt(NfsV3Status.Ok);
                        WritePostOpAttr(writer, present: false);
                        WriteWccData(writer);
                        break;
                    case 13:
                        writer.UInt(NfsV3Status.Stale);
                        WritePostOpAttr(writer, present: false);
                        WriteWccData(writer);
                        break;
                    case 14:
                        writer.UInt(NfsV3Status.Ok);
                        WriteDiropResult(writer, [0xCA]);
                        break;
                    case 15:
                        writer.UInt(NfsV3Status.Exist);
                        WriteWccData(writer);
                        break;
                    default:
                        throw new InvalidOperationException($"Unexpected mutation fixture request {index}.");
                }
            });
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            // Each mutation is called twice: once succeeding, once hitting its scripted status.
            await client.SetAttributesAsync(FixtureHandle, new NfsSetAttributes { Mode = 0x1A4 }, CancellationToken.None);
            await AssertStatusAsync(NfsV3Status.Access, () => client.SetAttributesAsync(FixtureHandle, new NfsSetAttributes(), CancellationToken.None));

            // CREATE and MKDIR return the new object's handle on success.
            Assert.Equal([0xC8], (await client.CreateFileAsync(FixtureHandle, "created", null, CancellationToken.None)).Handle);
            await AssertStatusAsync(NfsV3Status.Exist, () => client.CreateFileAsync(FixtureHandle, "created", null, CancellationToken.None));

            Assert.Equal([0xC9], (await client.CreateDirectoryAsync(FixtureHandle, "directory", null, CancellationToken.None)).Handle);
            await AssertStatusAsync(NfsV3Status.Exist, () => client.CreateDirectoryAsync(FixtureHandle, "directory", null, CancellationToken.None));

            await client.DeleteFileAsync("removed", CancellationToken.None);
            await AssertStatusAsync(NfsV3Status.NoEnt, () => client.DeleteFileAsync("missing", CancellationToken.None));

            await client.DeleteDirectoryAsync("removed-directory", recursive: false, CancellationToken.None);
            await AssertStatusAsync(NfsV3Status.NotEmpty, () => client.DeleteDirectoryAsync("nonempty-directory", recursive: false, CancellationToken.None));

            await client.MoveAsync("from", "to", CancellationToken.None);
            await AssertStatusAsync(NfsV3Status.NoEnt, () => client.MoveAsync("missing", "to", CancellationToken.None));

            // LINK failure uses STALE to prove the source-handle status is preserved.
            await client.CreateHardLinkAsync([0xD1], FixtureHandle, "hard-link", CancellationToken.None);
            await AssertStatusAsync(NfsV3Status.Stale, () => client.CreateHardLinkAsync([0xD1], FixtureHandle, "stale-link", CancellationToken.None));

            Assert.Equal([0xCA], (await client.CreateSymLinkAsync(FixtureHandle, "symbolic-link", "target", null, CancellationToken.None)).Handle);
            await AssertStatusAsync(NfsV3Status.Exist, () => client.CreateSymLinkAsync(FixtureHandle, "symbolic-link", "target", null, CancellationToken.None));
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    // Rejects write replies whose committed field is not a valid stable_how, and verifier fields shorter than 8 bytes.
    public async Task NfsV3Client_WriteAndCommitResultsValidateWireFieldsAndBoundaries()
    {
        byte[] writeVerifier = [0, 1, 2, 3, 4, 5, 6, 7];
        byte[] commitVerifier = [7, 6, 5, 4, 3, 2, 1, 0];
        // Four WRITE replies (valid, status, bad stability enum, short verifier) and three COMMIT replies.
        uint[] procedures = [7, 7, 7, 7, 21, 21, 21];
        await using var nfs = new RpcFixtureServer(procedures.Length, (call, index) =>
        {
            AssertNfsProcedure(call, procedures[index]);
            // COMMIT request layout is checked on the wire: handle, offset, and count, with nothing trailing.
            if (index == 4)
            {
                var request = new XdrReader(call.Arguments);
                Assert.Equal(FixtureHandle, request.Opaque());
                Assert.Equal(12ul, request.ULong());
                Assert.Equal(34u, request.UInt());
                Assert.Equal(0, request.Remaining);
            }

            return NfsReply(call, writer =>
            {
                writer.UInt(index is 1 or 5 ? NfsV3Status.Stale : NfsV3Status.Ok);
                WriteWccData(writer);
                switch (index)
                {
                    case 0: // Valid writeres3: count, committed, and a full 8-byte writeverf.
                        writer.UInt(3);
                        writer.UInt((uint)NfsWriteStableHow.DataSync);
                        writer.FixedBytes(writeVerifier);
                        break;
                    case 2: // committed=99 is outside the stable_how enum.
                        writer.UInt(1);
                        writer.UInt(99);
                        writer.FixedBytes(writeVerifier);
                        break;
                    case 3: // writeverf truncated to 4 bytes.
                        writer.UInt(1);
                        writer.UInt((uint)NfsWriteStableHow.FileSync);
                        writer.FixedBytes([0x01, 0x02, 0x03, 0x04]);
                        break;
                    case 4: // Valid commit: a full 8-byte writeverf after the shared WCC data.
                        writer.FixedBytes(commitVerifier);
                        break;
                    case 6: // commit writeverf truncated to 4 bytes.
                        writer.FixedBytes([0x01, 0x02, 0x03, 0x04]);
                        break;
                }
            });
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        // MaxRetries=0 so malformed replies surface immediately instead of being retried.
        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port) with { MaxRetries = 0 }, CancellationToken.None);
        try
        {
            var write = await client.WriteAtWithResultAsync(FixtureHandle, 4, new byte[] { 0x10, 0x11, 0x12 }, CancellationToken.None);
            Assert.Equal(3, write.Count);
            Assert.Equal(NfsWriteStableHow.DataSync, write.Committed);
            Assert.Equal(writeVerifier, write.WriteVerifier);

            await AssertStatusAsync(NfsV3Status.Stale, () => client.WriteAtWithResultAsync(FixtureHandle, 0, new byte[] { 0x10 }, CancellationToken.None));

            var invalidStability = await Assert.ThrowsAsync<NfsException>(
                () => client.WriteAtWithResultAsync(FixtureHandle, 0, new byte[] { 0x10 }, CancellationToken.None));
            Assert.Contains("Invalid committed write stability mode", invalidStability.Message);

            var truncatedWrite = await Assert.ThrowsAsync<NfsException>(
                () => client.WriteAtWithResultAsync(FixtureHandle, 0, new byte[] { 0x10 }, CancellationToken.None));
            Assert.Contains("Need 8 bytes", truncatedWrite.Message);

            var commit = await client.CommitWithResultAsync(FixtureHandle, 12, 34, CancellationToken.None);
            Assert.Equal(commitVerifier, commit.WriteVerifier);

            await AssertStatusAsync(NfsV3Status.Stale, () => client.CommitWithResultAsync(FixtureHandle, 0, 0, CancellationToken.None));

            var truncatedCommit = await Assert.ThrowsAsync<NfsException>(
                () => client.CommitWithResultAsync(FixtureHandle, 0, 0, CancellationToken.None));
            Assert.Contains("Need 8 bytes", truncatedCommit.Message);
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
    }

    [Fact]
    // Zero-valued FSSTAT/FSINFO/PATHCONF fields must round-trip as zero; time_delta nanoseconds above 999,999,999 are rejected.
    public async Task NfsV3Client_CapabilityResultsHandleZeroValuesStatusesAndBoundaries()
    {
        // FSSTAT, FSINFO (twice), and PATHCONF, each paired with a STALE failure except the invalid time_delta case.
        uint[] procedures = [18, 18, 19, 19, 19, 20, 20];
        await using var nfs = new RpcFixtureServer(procedures.Length, (call, index) =>
        {
            AssertNfsProcedure(call, procedures[index]);
            return NfsReply(call, writer =>
            {
                writer.UInt(index is 1 or 3 or 6 ? NfsV3Status.Stale : NfsV3Status.Ok);
                WritePostOpAttr(writer, present: false);
                switch (index)
                {
                    case 0: // FSSTAT: six unsigned hyper fields, then tbytes-encoded time_delta seconds as zero.
                        for (var field = 0; field < 6; field++)
                            writer.ULong(0);
                        writer.UInt(0);
                        break;
                    case 2: // FSINFO: all-zero sizes/limits, zero time_delta, and zero properties.
                        for (var field = 0; field < 7; field++)
                            writer.UInt(0);
                        writer.ULong(0);
                        writer.UInt(0);
                        writer.UInt(0);
                        writer.UInt(0);
                        break;
                    case 4: // time_delta nanoseconds = 1e9 is one past the legal maximum (999,999,999).
                        for (var field = 0; field < 7; field++)
                            writer.UInt(0);
                        writer.ULong(0);
                        writer.UInt(0);
                        writer.UInt(1_000_000_000);
                        writer.UInt(0);
                        break;
                    case 5: // PATHCONF: zero linkmax/namelength plus four false boolean flags.
                        writer.UInt(0);
                        writer.UInt(0);
                        for (var field = 0; field < 4; field++)
                            writer.Bool(false);
                        break;
                }
            });
        });
        await using var mount = CreateMountedExportServer();
        await using var portmap = CreateNfsPortmap(mount.Port, nfs.Port);

        var client = await NfsV3Client.ConnectAsync(
            "127.0.0.1", "/export", CreateFixtureOptions(portmap.Port), CancellationToken.None);
        try
        {
            // Zero on the wire must stay zero in the public model (no "unset" sentinel substitution).
            var stat = await client.GetFileSystemStatAsync(FixtureHandle, CancellationToken.None);
            Assert.Equal(0ul, stat.TotalBytes);
            Assert.Equal(TimeSpan.Zero, stat.InvariantUntil);
            await AssertStatusAsync(NfsV3Status.Stale, () => client.GetFileSystemStatAsync(FixtureHandle, CancellationToken.None));

            var info = await client.GetFileSystemInfoAsync(FixtureHandle, CancellationToken.None);
            Assert.Equal(0u, info.MaxReadSize);
            Assert.Equal(TimeSpan.Zero, info.TimeDelta);
            await AssertStatusAsync(NfsV3Status.Stale, () => client.GetFileSystemInfoAsync(FixtureHandle, CancellationToken.None));

            // time_delta is a nfstime3; nanoseconds outside [0, 999999999] are not a valid duration.
            var invalidDelta = await Assert.ThrowsAsync<NfsException>(
                () => client.GetFileSystemInfoAsync(FixtureHandle, CancellationToken.None));
            Assert.Contains("time_delta nanoseconds", invalidDelta.Message);

            var pathConf = await client.GetPathConfAsync(FixtureHandle, CancellationToken.None);
            Assert.Equal(0u, pathConf.LinkMax);
            Assert.False(pathConf.NoTrunc);
            await AssertStatusAsync(NfsV3Status.Stale, () => client.GetPathConfAsync(FixtureHandle, CancellationToken.None));
        }
        finally
        {
            await client.DisposeAsync();
        }

        await WaitForRequestsAsync(portmap, mount, nfs);
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
        Assert.Equal(32, opts.MaxOutstandingRpcCallsPerConnection);
    }

    [Fact]
    public void NfsClientOptions_RejectsInvalidRetryAndCacheOptions()
    {
        // Each case feeds one invalid field into Validate(); every one must throw before any connection is attempted.
        Assert.Throws<NfsException>(
            () => new NfsClientOptions { CommandTimeout = TimeSpan.FromMilliseconds(-1) }.Validate());

        // stable_how outside the enum range is rejected at option-validation time, not on the wire.
        Assert.Throws<NfsException>(
            () => new NfsClientOptions { StableHow = (NfsWriteStableHow)99 }.Validate());

        // Retry and concurrency limits must be non-negative and at least one, respectively.
        Assert.Throws<NfsException>(
            () => new NfsClientOptions { MaxRetries = -1 }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions { MaxOutstandingRpcCallsPerConnection = 0 }.Validate());

        Assert.Throws<NfsException>(
            () => new NfsClientOptions { RetryDelay = TimeSpan.FromMilliseconds(-1) }.Validate());

        // A directory cache without a positive TTL cannot serve a coherent entry.
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
    // Only idempotent NFS procedures (reads and metadata queries) are retry-safe; mutations and UMNT are not.
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
    // Reflection reaches the private EnsureDirectoryReadProgress guard that rejects non-terminal pages which never advance the cookie.
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
    // A truncated RPC record wrapped in NfsException must still classify as transient so the client can reconnect and retry.
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

    private static readonly byte[] FixtureHandle = [0xF0, 0x0D];

    private static RpcFixtureServer CreateNfsPortmap(int mountPort, int nfsPort) => new(
        2,
        // PMAP GETPORT: first call resolves mountd, second resolves nfsd.
        (call, index) =>
        {
            Assert.Equal(100000u, call.Program);
            Assert.Equal(2u, call.Version);
            Assert.Equal(3u, call.Procedure);
            return RpcFixtureServer.AcceptedReply(
                call.Xid,
                RpcFixtureServer.Success,
                writer => writer.UInt((uint)(index == 0 ? mountPort : nfsPort)));
        });

    private static RpcFixtureServer CreateMountedExportServer() => new(
        2,
        // MOUNT MNT then MOUNT UMNT; the fixture handle stands in for the root filehandle.
        (call, index) =>
        {
            Assert.Equal(100005u, call.Program);
            Assert.Equal(3u, call.Version);
            Assert.Equal(index == 0 ? 1u : 3u, call.Procedure);
            return RpcFixtureServer.AcceptedReply(
                call.Xid,
                RpcFixtureServer.Success,
                index == 0
                    ? writer =>
                    {
                        writer.UInt(MountV3Status.Ok);
                        writer.Opaque(FixtureHandle);
                        writer.UInt(0); // auth_flavors
                    }
                    : null);
        });

    private static byte[] NfsReply(RpcFixtureCall call, Action<XdrWriter> write) =>
        RpcFixtureServer.AcceptedReply(call.Xid, RpcFixtureServer.Success, write);

    private static void AssertNfsProcedure(RpcFixtureCall call, uint procedure)
    {
        // Every fixture reply first verifies the NFSv3 program/version/procedure triple on the wire.
        Assert.Equal(100003u, call.Program);
        Assert.Equal(3u, call.Version);
        Assert.Equal(procedure, call.Procedure);
    }

    private static void AssertReadDirRequest(
        RpcFixtureCall call,
        ulong expectedCookie,
        byte[] expectedVerifier,
        bool plus)
    {
        // Request payload: handle, start cookie, cookieverf, dircount; READDIRPLUS also carries maxcount.
        var reader = new XdrReader(call.Arguments);
        Assert.Equal(FixtureHandle, reader.Opaque());
        Assert.Equal(expectedCookie, reader.ULong());
        Assert.Equal(expectedVerifier, reader.FixedBytes(8));
        Assert.Equal(32 * 1024u, reader.UInt());
        if (plus)
            Assert.Equal(32 * 1024u, reader.UInt());
        Assert.Equal(0, reader.Remaining);
    }

    // Builds a READDIR/READDIRPLUS result: cookieverf, a linked list of entries, then a false marker and eof flag.
    private static void WriteReadDirResult(
        XdrWriter writer,
        byte[] cookieVerifier,
        IReadOnlyList<(ulong FileId, string Name, ulong Cookie)> entries,
        bool eof)
    {
        writer.UInt(NfsV3Status.Ok);
        WritePostOpAttr(writer, present: false);
        writer.FixedBytes(cookieVerifier);
        foreach (var entry in entries)
        {
            writer.Bool(true);
            writer.ULong(entry.FileId);
            writer.Str(entry.Name);
            writer.ULong(entry.Cookie);
        }

        writer.Bool(false);
        writer.Bool(eof);
    }

    private static void WriteReadDirPlusResult(
        XdrWriter writer,
        byte[] cookieVerifier,
        IReadOnlyList<(ulong FileId, string Name, ulong Cookie)> entries,
        bool eof,
        bool includeAttributes = false,
        NfsTimestamp? entryAtime = null,
        NfsTimestamp? entryMtime = null,
        NfsTimestamp? entryCtime = null)
    {
        writer.UInt(NfsV3Status.Ok);
        WritePostOpAttr(writer, present: false);
        writer.FixedBytes(cookieVerifier);
        foreach (var entry in entries)
        {
            writer.Bool(true);
            writer.ULong(entry.FileId);
            writer.Str(entry.Name);
            writer.ULong(entry.Cookie);
            if (includeAttributes)
            {
                // name_attributes follows as post_op_attr; present fattr3 carries the requested timestamps.
                writer.Bool(true);
                WriteFattr3(
                    writer,
                    NfsType.Reg,
                    0,
                    entry.FileId,
                    entryAtime,
                    entryMtime,
                    entryCtime);
                writer.Bool(false); // name_handle follows
            }
            else
            {
                WritePostOpAttr(writer, present: false);
                writer.Bool(false); // name_handle follows
            }
        }

        writer.Bool(false);
        writer.Bool(eof);
    }

    private static void WritePostOpAttr(XdrWriter writer, bool present)
    {
        // post_op_attr is a presence flag; false means the optional attributes are simply absent.
        writer.Bool(present);
        if (present)
            WriteFattr3(writer, NfsType.Reg, 0);
    }

    // WRITE and COMMIT replies always start with WCC data; the method-specific result fields follow.
    private static void WriteWccData(XdrWriter writer)
    {
        writer.Bool(false); // pre-operation attributes unavailable
        WritePostOpAttr(writer, present: false);
    }

    private static void WriteDiropResult(XdrWriter writer, byte[] fileHandle)
    {
        // diropres3: handle presence + handle, post_op_attr for the new object, then WCC for its parent directory.
        writer.Bool(true);
        writer.Opaque(fileHandle);
        WritePostOpAttr(writer, present: false);
        WriteWccData(writer);
    }

    private static async Task AssertStatusAsync(uint expectedStatus, Func<Task> operation)
    {
        var exception = await Assert.ThrowsAsync<NfsException>(operation);
        Assert.Equal(expectedStatus, exception.Status);
    }

    // fattr3 wire layout: type, mode, nlink, uid, gid, size, used, rdev, fsid, fileid, then three nfstime3 pairs.
    // Default timestamps are (0, 0) so fixtures exercise the Unix epoch path unless they override a field.
    private static void WriteFattr3(
        XdrWriter writer,
        NfsType type,
        ulong size,
        ulong fileId = 1,
        NfsTimestamp? atime = null,
        NfsTimestamp? mtime = null,
        NfsTimestamp? ctime = null)
    {
        writer.UInt((uint)type);
        writer.UInt(0x1A4);
        writer.UInt(1);
        writer.UInt(1000);
        writer.UInt(1000);
        writer.ULong(size);
        writer.ULong(size);
        writer.UInt(0);
        writer.UInt(0);
        writer.ULong(1);
        writer.ULong(fileId);
        WriteNfsTime3(writer, atime ?? new NfsTimestamp(0, 0));
        WriteNfsTime3(writer, mtime ?? new NfsTimestamp(0, 0));
        WriteNfsTime3(writer, ctime ?? new NfsTimestamp(0, 0));
    }

    private static void WriteNfsTime3(XdrWriter writer, NfsTimestamp value)
    {
        writer.UInt(value.Seconds);
        writer.UInt(value.Nanoseconds);
    }

    private static Task WaitForRequestsAsync(params RpcFixtureServer[] servers) =>
        Task.WhenAll(servers.Select(server => server.WaitForRequestsAsync()));

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

    // Creates an NfsV3Client without connecting: reflection reaches the non-public constructor used for unit-level API tests.
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
    // The nanosecond field is preserved verbatim; round-tripping through DateTime truncates to 100ns ticks.
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
        // Attribute numbers 1 (type), 2 (mode), and 33 (owner_group) pack into two 32-bit mask words.
        var bitmap = NfsV4Bitmap.Of(
            NfsV4Attr.Type,
            NfsV4Attr.Mode,
            NfsV4Attr.OwnerGroup);

        Assert.True(bitmap.HasAttr(NfsV4Attr.Type));
        Assert.True(bitmap.HasAttr(NfsV4Attr.Mode));
        Assert.True(bitmap.HasAttr(NfsV4Attr.OwnerGroup));
        Assert.False(bitmap.HasAttr(NfsV4Attr.Size));
        // Word 0 covers attrs 0-31 (bits 1 and 2); word 1 covers attrs 32-63 (bit 1 => attr 33).
        Assert.Equal([1u << 1, (1u << 1) | (1u << 5)], bitmap.Masks);

        // Masks is a defensive copy: mutating the returned array must not affect the bitmap.
        var masks = bitmap.Masks;
        masks[0] = 0;
        Assert.True(bitmap.HasAttr(NfsV4Attr.Type));

        // Wire form is count-prefixed words followed by the big-endian mask words themselves.
        var writer = new XdrWriter();
        bitmap.Encode(writer);
        var reader = new XdrReader(writer.ToArray());

        Assert.Equal(2u, reader.UInt());
        Assert.Equal(1u << 1, reader.UInt());
        Assert.Equal((1u << 1) | (1u << 5), reader.UInt());
        Assert.Equal(0, reader.Remaining);
    }

    [Fact]
    // NfsV4StateId must snapshot its input and expose a defensive copy so callers cannot mutate encoded state.
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
        // Mutating the constructor input after construction must not change the encoded state.
        data[0] = 0xFF;

        // Wire layout is a 4-byte sequence id (big-endian) followed by the 12-byte "other" field.
        var writer = new XdrWriter();
        stateId.Encode(writer);
        var reader = new XdrReader(writer.ToArray());

        Assert.Equal(0x01020304u, reader.UInt());
        Assert.Equal(expected[4..], reader.FixedBytes(12));
        Assert.Equal(0, reader.Remaining);

        // Decode the same layout back and confirm the round trip preserves every byte.
        writer = new XdrWriter();
        writer.UInt(0x01020304u);
        writer.FixedBytes(expected[4..]);

        var decoded = NfsV4StateId.Decode(new XdrReader(writer.ToArray()));
        Assert.Equal(expected, decoded.Data);

        // The Data property returns a fresh copy each time.
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
    // RENAME carries two cinfo structures (source and target directories) in a single result payload.
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
    // OPEN_DELEGATE_NONE_EXT adds a why-no-delegation reason and optional push/pull flags after the delegation type.
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
    // Write delegations carry a second stateid, recall flag, and space limit (blocks or bytes) before the delegated ACE.
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
    // OPEN4_NOCREATE claims encode the claim type immediately after the opentype, before the claim name.
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
    // NFSv4.2 COPY uses consecutive stateids for source and destination; both are sent as "current" (all-zero) placeholders here.
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
    // SECINFO's RPCSEC_GSS flavor carries a variable-length opaque OID that must be skipped when collecting flavor ids.
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
    // Defensive copies: mutating the source or the returned verifier array must not change the stored result.
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

/// <summary>
/// Loopback TCP RPC fixture that serves a scripted number of calls and records decoded requests.
/// Replies are XDR-encoded MSG_ACCEPTED or MSG_DENIED envelopes, optionally with procedure payloads.
/// </summary>
internal sealed class RpcFixtureServer : IAsyncDisposable
{
    // accept_stat values from RFC 5531 used when building accepted replies.
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
        // Accepts connections until the scripted call count is exhausted; one connection may carry several calls.
        var index = 0;
        while (index < expectedRequests)
        {
            using var client = await _listener.AcceptTcpClientAsync();
            // readRequests=false mode only counts connections (used when the client is not expected to send anything).
            if (!readRequests)
            {
                index++;
                continue;
            }

            while (index < expectedRequests)
            {
                RpcFixtureCall call;
                try
                {
                    call = await ReadCallAsync(client.GetStream());
                }
                catch (EndOfStreamException)
                {
                    // Client closed the connection early; accept a replacement for the remaining calls.
                    break;
                }

                var response = reply(call, index++);
                await SendRecordAsync(client.GetStream(), response);
            }
        }
    }

    private static async Task<RpcFixtureCall> ReadCallAsync(Stream stream)
    {
        // Decodes an RPC CALL envelope: xid, msg_type, rpcvers, program/version/procedure, then credential and verifier.
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
        // Everything after the auth fields is the procedure-specific argument payload.
        return new RpcFixtureCall(xid, program, version, procedure, reader.ReadRemainingBytes());
    }

    private static async Task<byte[]> ReadRecordAsync(Stream stream)
    {
        // RFC 5531 record marking: fragments carry a high-bit "last" flag plus a 31-bit length.
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

/// <summary>Decoded RPC call header plus the remaining procedure arguments, as captured by RpcFixtureServer.</summary>
internal sealed record RpcFixtureCall(uint Xid, uint Program, uint Version, uint Procedure, byte[] Arguments);
