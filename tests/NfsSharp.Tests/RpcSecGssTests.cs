using System.Buffers.Binary;
using NfsSharp.Client;
using NfsSharp.Protocol;

namespace NfsSharp.Tests;

/// <summary>
/// RPCSEC_GSS credential layout, per-attempt sequence numbers, and reply-verifier fail-closed checks.
/// Uses a deterministic fake mechanism whose MIC depends on both data and QOP.
/// </summary>
public class RpcSecGssTests
{
    private const uint Xid = 0x1111_2222;
    private static readonly byte[] ContextHandle = [0x10, 0x20, 0x30, 0x40];

    [Fact]
    public void CallRecord_SeqNumNetworkOrder_IsBigEndian()
    {
        var record = NewRecord(seqNum: 0x0102_0304);

        Assert.Equal(new byte[] { 0x01, 0x02, 0x03, 0x04 }, record.SeqNumNetworkOrder());
    }

    [Theory]
    [InlineData(0u)]
    [InlineData(0x8000_0000u)]
    public void CallRecord_RejectsOutOfRangeSequenceNumbers(uint seqNum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => NewRecord(seqNum));
    }

    [Fact]
    public void WriteCredential_EncodesRfc2203VersionedLayout()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(xid: 7, program: 100003, version: 3, procedure: 1);

        var writer = new XdrWriter();
        session.WriteCredential(writer, record);

        var reader = new XdrReader(writer.ToArray());
        var body = reader.Opaque();
        Assert.Equal(0, reader.Remaining);

        var cred = new XdrReader(body);
        Assert.Equal(RpcSecGssConstants.Version, cred.UInt());
        Assert.Equal((uint)RpcSecGssProc.Data, cred.UInt());
        Assert.Equal(record.SeqNum, cred.UInt());
        Assert.Equal((uint)RpcSecGssService.Integrity, cred.UInt());
        Assert.Equal(ContextHandle, cred.Opaque());
        Assert.Equal(0, cred.Remaining);
    }

    [Fact]
    public void BeginDataCall_AllocatesStrictlyIncreasingSequenceNumbersPerAttempt()
    {
        var session = CreateEstablishedSession(new FakeGssMechanism());

        var first = session.BeginDataCall(1, 100003, 3, 1);
        var retry = session.BeginDataCall(1, 100003, 3, 1);
        var next = session.BeginDataCall(2, 100003, 3, 1);

        Assert.Equal(1u, first.SeqNum);
        Assert.Equal(2u, retry.SeqNum);
        Assert.Equal(3u, next.SeqNum);
        // A retry that reuses the RPC XID must still get a fresh sequence number.
        Assert.Equal(first.Xid, retry.Xid);
        Assert.NotEqual(first.SeqNum, retry.SeqNum);
        Assert.NotEqual(first.SeqNumNetworkOrder(), retry.SeqNumNetworkOrder());
    }

    [Fact]
    public void CreateHeaderVerifier_MicCoversHeaderThroughCredential()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 4);

        var header = new XdrWriter();
        header.UInt(Xid);
        header.UInt(0);
        header.UInt(2);
        header.UInt(100003);
        header.UInt(3);
        header.UInt(4);
        header.UInt((uint)RpcSecGssFlavor.Gss);
        session.WriteCredential(header, record);
        var headerBytes = header.ToArray();

        var verifier = session.CreateHeaderVerifier(headerBytes, record);

        Assert.Equal(FakeGssMechanism.Mac(headerBytes, record.Qop), verifier);
        // One-bit change to the sealed header must change the MIC.
        var tampered = (byte[])headerBytes.Clone();
        tampered[0] ^= 0x01;
        Assert.NotEqual(verifier, FakeGssMechanism.Mac(tampered, record.Qop));
    }

    [Fact]
    public void VerifyReply_AcceptsValidSequenceMic()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop));

        session.VerifyReply(reply, record);
    }

    [Fact]
    public void VerifyReply_RejectsBitFlippedVerifier()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var mic = FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop);
        mic[0] ^= 0x01;
        var reply = ParseAcceptedReply(record, mic);

        var ex = Assert.Throws<NfsException>(() => session.VerifyReply(reply, record));
        Assert.Contains("sequence number MIC mismatch", ex.Message);
    }

    [Fact]
    public void VerifyReply_RejectsWrongFlavorAndEmptyVerifier()
    {
        var session = CreateEstablishedSession(new FakeGssMechanism());
        var record = session.BeginDataCall(Xid, 100003, 3, 1);

        var wrongFlavor = ParseAcceptedReply(
            record,
            FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop),
            verifierFlavor: (uint)RpcSecGssFlavor.Sys);
        var flavorEx = Assert.Throws<NfsException>(() => session.VerifyReply(wrongFlavor, record));
        Assert.Contains("expected flavor RPCSEC_GSS", flavorEx.Message);

        var empty = ParseAcceptedReply(record, [], verifierFlavor: (uint)RpcSecGssFlavor.Gss);
        var emptyEx = Assert.Throws<NfsException>(() => session.VerifyReply(empty, record));
        Assert.Contains("verifier body is empty", emptyEx.Message);
    }

    [Fact]
    public void VerifyReply_RejectsMicOfDifferentSequenceOrQop()
    {
        var session = CreateEstablishedSession(new FakeGssMechanism());
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var wrongSeqBytes = new byte[] { 0, 0, 0, (byte)(record.SeqNum + 1) };

        var wrongSeq = ParseAcceptedReply(record, FakeGssMechanism.Mac(wrongSeqBytes, record.Qop));
        Assert.Throws<NfsException>(() => session.VerifyReply(wrongSeq, record));

        var wrongQop = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop + 1));
        Assert.Throws<NfsException>(() => session.VerifyReply(wrongQop, record));
    }

    [Fact]
    public void VerifyReply_RejectsReplyFromReplacedContextGeneration()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        // Replace the context so the late reply cannot validate against the new generation.
        session.InstallContext(NewContext(mechanism, RpcSecGssService.Integrity));
        var reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop));

        var ex = Assert.Throws<NfsException>(() => session.VerifyReply(reply, record));
        Assert.Contains("context generation mismatch", ex.Message);
    }

    [Fact]
    public void NoOpMechanism_IsRejectedForIntegrityAndPrivacy()
    {
        foreach (var service in new[] { RpcSecGssService.Integrity, RpcSecGssService.Privacy })
        {
            var options = new NfsClientOptions
            {
                GssMechanism = new NoOpGssMechanism(),
                GssService = service,
            };
            var ex = Assert.Throws<NfsException>(() => options.Validate());
            Assert.Contains("NoOpGssMechanism", ex.Message);
        }

        // NoOp remains legal for rpc_gss_svc_none in deterministic tests.
        var noneOptions = new NfsClientOptions
        {
            GssMechanism = new NoOpGssMechanism(),
            GssService = RpcSecGssService.None,
        };
        noneOptions.Validate();
    }

    [Fact]
    public void RpcSecGssProc_MatchesRfc2203Discriminators()
    {
        Assert.Equal(0u, (uint)RpcSecGssProc.Data);
        Assert.Equal(1u, (uint)RpcSecGssProc.Init);
        Assert.Equal(2u, (uint)RpcSecGssProc.ContinueInit);
        Assert.Equal(3u, (uint)RpcSecGssProc.Destroy);
    }

    [Fact]
    public void DecodeAccepted_PreservesVerifierOnAcceptFailures()
    {
        // RFC 2203 still supplies the reply verifier on accepted RPC errors.
        var fixture = new XdrWriter();
        fixture.UInt(0x20);
        fixture.UInt(1);
        fixture.UInt(0);
        fixture.UInt((uint)RpcSecGssFlavor.Gss);
        fixture.Opaque([0xAA, 0xBB]);
        fixture.UInt(4); // GARBAGE_ARGS

        var reply = RpcReplyParser.DecodeAccepted(fixture.ToArray(), 0x20);

        Assert.False(reply.IsSuccess);
        Assert.Equal(4u, reply.AcceptStatus);
        Assert.Equal((uint)RpcSecGssFlavor.Gss, reply.VerifierFlavor);
        Assert.Equal(new byte[] { 0xAA, 0xBB }, reply.Verifier);
        Assert.Contains("garbage arguments", reply.AcceptFailureMessage);

        Assert.Throws<NfsException>(() => RpcReplyParser.ThrowIfAcceptFailed(reply));
    }

    [Fact]
    public void VerifyReply_RejectsVerifierEvenWhenAcceptStatIsNotSuccess()
    {
        var session = CreateEstablishedSession(new FakeGssMechanism());
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var mic = FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop);
        mic[0] ^= 0x01;

        var fixture = new XdrWriter();
        fixture.UInt(Xid);
        fixture.UInt(1);
        fixture.UInt(0);
        fixture.UInt((uint)RpcSecGssFlavor.Gss);
        fixture.Opaque(mic);
        fixture.UInt(4); // GARBAGE_ARGS
        var reply = RpcReplyParser.DecodeAccepted(fixture.ToArray(), Xid);

        var ex = Assert.Throws<NfsException>(() => session.VerifyReply(reply, record));
        Assert.Contains("sequence number MIC mismatch", ex.Message);
    }

    private static RpcSecGssCallRecord NewRecord(uint seqNum = 1) =>
        new(
            xid: Xid,
            seqNum: seqNum,
            service: RpcSecGssService.Integrity,
            qop: 0,
            contextGeneration: 1,
            program: 100003,
            version: 3,
            procedure: 1,
            gssProc: RpcSecGssProc.Data,
            contextHandle: ContextHandle);

    private static RpcSecGssContext NewContext(IRpcSecGssMechanism mechanism, RpcSecGssService service) =>
        new()
        {
            ContextHandle = ContextHandle,
            SeqWindowSize = 16,
            SeqWindow = new byte[8],
            Service = service,
            Mechanism = mechanism,
        };

    private static RpcSecGssSession CreateEstablishedSession(FakeGssMechanism mechanism)
    {
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.Integrity,
        };
        var session = new RpcSecGssSession(options);
        session.InstallContext(NewContext(mechanism, RpcSecGssService.Integrity));
        return session;
    }

    private static RpcReply ParseAcceptedReply(
        RpcSecGssCallRecord record,
        byte[] verifier,
        uint verifierFlavor = (uint)RpcSecGssFlavor.Gss)
    {
        var fixture = new XdrWriter();
        fixture.UInt(record.Xid);
        fixture.UInt(1); // REPLY
        fixture.UInt(0); // MSG_ACCEPTED
        fixture.UInt(verifierFlavor);
        fixture.Opaque(verifier);
        fixture.UInt(0); // SUCCESS
        return RpcReplyParser.DecodeAccepted(fixture.ToArray(), record.Xid);
    }

    /// <summary>
    /// Deterministic fake GSS mechanism. The MIC is a function of both the data bytes and the QOP,
    /// so a no-op or QOP-blind verifier cannot accidentally pass negative fixtures.
    /// </summary>
    private sealed class FakeGssMechanism : IRpcSecGssMechanism, IRpcSecGssQopMechanism
    {
        public byte[] MechanismOid => [0x2A];
        public bool IsEstablished => true;
        public uint MaxMessageSize => 64 * 1024;
        public uint NextSeqNum { get; set; } = 1;
        public RpcSecGssService NegotiatedService { get; set; }

        public Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct) =>
            Task.FromResult(new byte[] { 0x01 });

        public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct) =>
            Task.FromResult(Array.Empty<byte>());

        public byte[] GetMic(byte[] data) => Mac(data, 0);
        public bool VerifyMic(byte[] data, byte[] mic) => VerifyMic(data, mic, 0);
        public byte[] Wrap(byte[] data) => data;
        public byte[] Unwrap(byte[] wrappedData) => wrappedData;
        public byte[] GetMic(byte[] data, uint qop) => Mac(data, qop);
        public bool VerifyMic(byte[] data, byte[] mic, uint qop) => Mac(data, qop).AsSpan().SequenceEqual(mic);
        public byte[] Wrap(byte[] data, uint qop) => data;
        public byte[] Unwrap(byte[] wrappedData, uint qop) => wrappedData;
        public void Dispose() { }

        internal static byte[] Mac(byte[] data, uint qop)
        {
            // Simple keyed checksum: depends on every data byte and on the QOP.
            var mic = new byte[8];
            uint state = 0x811C_9DC5 ^ qop;
            foreach (var b in data)
            {
                state ^= b;
                state *= 0x0100_0193;
            }

            BinaryPrimitives.WriteUInt32BigEndian(mic.AsSpan(0, 4), state);
            BinaryPrimitives.WriteUInt32BigEndian(mic.AsSpan(4, 4), qop);
            return mic;
        }
    }
}
