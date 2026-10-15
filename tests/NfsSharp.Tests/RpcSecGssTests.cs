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

    [Fact]
    public void CallRecord_CopiesContextHandleOnInputAndOutput()
    {
        var source = (byte[])ContextHandle.Clone();
        var record = new RpcSecGssCallRecord(
            xid: Xid,
            seqNum: 1,
            service: RpcSecGssService.Integrity,
            qop: 0,
            contextGeneration: 1,
            program: 100003,
            version: 3,
            procedure: 1,
            gssProc: RpcSecGssProc.Data,
            contextHandle: source);

        // Mutating the construction buffer must not change the record identity.
        source[0] ^= 0xFF;
        Assert.Equal(ContextHandle, record.ContextHandle);

        // Mutating the returned array must not change the stored identity either.
        var exposed = record.ContextHandle;
        exposed[1] ^= 0xFF;
        Assert.Equal(ContextHandle, record.ContextHandle);
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
    public void VerifyReply_RejectsMechanismReportedQopMismatchWithoutLeakingMechanismErrors()
    {
        var mechanism = new FakeGssMechanism { ReportedMicQopOffset = 1 };
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop));

        var qopException = Assert.Throws<NfsException>(() => RpcSecGssMechanism.VerifyMic(
            mechanism,
            record.SeqNumNetworkOrder(),
            reply.Verifier,
            record.Qop));
        Assert.Contains("requested QOP", qopException.Message);

        mechanism.ReportedMicQopOffset = 0;
        mechanism.VerificationException = new InvalidOperationException("secret-token-and-mic");
        var failure = Assert.Throws<NfsException>(() => session.VerifyReply(reply, record));
        Assert.Null(failure.InnerException);
        Assert.DoesNotContain("secret-token-and-mic", failure.ToString());
    }

    [Fact]
    public void MicVerificationResult_DefaultValueFailsClosed()
    {
        var mechanism = new FakeGssMechanism { ReturnDefaultMicResult = true };

        var exception = Assert.Throws<NfsException>(() => RpcSecGssMechanism.VerifyMic(
            mechanism,
            [1, 2, 3],
            [0xAA],
            qop: 0));

        Assert.Equal(RpcSecGssMicVerificationStatus.Failed, default(RpcSecGssMicVerificationResult).Status);
        Assert.Contains("failed to verify the MIC", exception.Message);
    }

    [Fact]
    public void ProtectArguments_IntegrityEncodesSequenceAndMicOverDatabody()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var arguments = new XdrWriter();
        arguments.UInt(0x0102_0304);
        arguments.UInt(7);

        var protectedArguments = session.ProtectArguments(arguments.ToArray(), record);
        var envelope = new XdrReader(protectedArguments);
        var databody = envelope.Opaque();
        var checksum = envelope.Opaque();
        Assert.Equal(0, envelope.Remaining);

        var expected = new XdrWriter();
        expected.UInt(record.SeqNum);
        expected.Raw(arguments.ToArray());
        Assert.Equal(expected.ToArray(), databody);
        Assert.Equal(FakeGssMechanism.Mac(databody, record.Qop), checksum);
    }

    [Fact]
    public void UnprotectReplyBody_IntegrityVerifiesBeforeReturningResult()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var result = new XdrWriter();
        result.UInt(0x1234);
        result.UInt(0x5678);
        var databody = new XdrWriter();
        databody.UInt(record.SeqNum);
        databody.Raw(result.ToArray());
        var envelope = new XdrWriter();
        envelope.Opaque(databody.ToArray());
        envelope.Opaque(FakeGssMechanism.Mac(databody.ToArray(), record.Qop));

        var reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: envelope.ToArray());
        var unprotected = session.UnprotectReplyBody(reply, record);
        Assert.Equal(result.ToArray(), unprotected.ReadRemainingBytes());

        var tampered = envelope.ToArray();
        tampered[8] ^= 1; // Change protected result bytes while retaining the original MIC.
        var badReply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: tampered);
        var exception = Assert.Throws<NfsException>(() => session.UnprotectReplyBody(badReply, record));
        Assert.Contains("MIC verification failed", exception.Message);
    }

    [Fact]
    public void ProtectAndUnprotectArguments_PrivacyRequiresConfidentialityAndMatchingQop()
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism, RpcSecGssService.Privacy);
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var arguments = new XdrWriter();
        arguments.UInt(0x0102_0304);
        var expected = new XdrWriter();
        expected.UInt(record.SeqNum);
        expected.Raw(arguments.ToArray());

        var protectedArguments = session.ProtectArguments(arguments.ToArray(), record);
        var envelope = new XdrReader(protectedArguments);
        var wrapped = envelope.Opaque();
        Assert.Equal(0, envelope.Remaining);
        Assert.Equal(expected.ToArray(), mechanism.UnwrapPrivacy(wrapped, record.Qop).Data);

        var result = new XdrWriter();
        result.UInt(42);
        var replyDatabody = new XdrWriter();
        replyDatabody.UInt(record.SeqNum);
        replyDatabody.Raw(result.ToArray());
        var replyBody = new XdrWriter();
        replyBody.Opaque(mechanism.WrapPrivacy(replyDatabody.ToArray(), record.Qop).Data);
        var reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: replyBody.ToArray());
        Assert.Equal(result.ToArray(), session.UnprotectReplyBody(reply, record).ReadRemainingBytes());

        var tamperedBody = replyBody.ToArray();
        tamperedBody[^1] ^= 0x01;
        reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: tamperedBody);
        Assert.Contains("failed to unwrap", Assert.Throws<NfsException>(() => session.UnprotectReplyBody(reply, record)).Message);

        mechanism.ReportedQopOffset = 1;
        Assert.Throws<NfsException>(() => session.ProtectArguments(arguments.ToArray(), record));
        Assert.Throws<NfsException>(() => session.UnprotectReplyBody(
            ParseAcceptedReply(
                record,
                FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop),
                body: replyBody.ToArray()),
            record));
        mechanism.ReportedQopOffset = 0;

        mechanism.ReportConfidentiality = false;
        Assert.Throws<NfsException>(() => session.ProtectArguments(arguments.ToArray(), record));
        Assert.Throws<NfsException>(() => session.UnprotectReplyBody(reply, record));
    }

    [Fact]
    public void UnprotectReplyBody_RejectsSequenceMismatchAndTrailingEnvelopeData()
    {
        var session = CreateEstablishedSession(new FakeGssMechanism());
        var record = session.BeginDataCall(Xid, 100003, 3, 1);
        var wrongSequence = new XdrWriter();
        wrongSequence.UInt(record.SeqNum + 1);
        var integrity = new XdrWriter();
        integrity.Opaque(wrongSequence.ToArray());
        integrity.Opaque(FakeGssMechanism.Mac(wrongSequence.ToArray(), record.Qop));
        var reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: integrity.ToArray());
        var sequenceException = Assert.Throws<NfsException>(() => session.UnprotectReplyBody(reply, record));
        Assert.True(sequenceException.Message.Contains("sequence mismatch", StringComparison.Ordinal), sequenceException.Message);

        var trailing = new XdrWriter();
        trailing.Opaque(wrongSequence.ToArray());
        trailing.Opaque(FakeGssMechanism.Mac(wrongSequence.ToArray(), record.Qop));
        trailing.UInt(0xDEAD_BEEF);
        reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: trailing.ToArray());
        Assert.Contains("trailing data", Assert.Throws<NfsException>(() => session.UnprotectReplyBody(reply, record)).Message);

        reply = ParseAcceptedReply(record, FakeGssMechanism.Mac(record.SeqNumNetworkOrder(), record.Qop), body: "\0\0\0"u8.ToArray());
        Assert.Contains("protected result rejected", Assert.Throws<NfsException>(() => session.UnprotectReplyBody(reply, record)).Message);
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
    public void NoOpMechanism_IsRejectedForEveryClientService()
    {
        foreach (var service in new[] { RpcSecGssService.None, RpcSecGssService.Integrity, RpcSecGssService.Privacy })
        {
            var options = new NfsClientOptions
            {
                GssMechanism = new NoOpGssMechanism(),
                GssService = service,
            };
            var ex = Assert.Throws<NfsException>(options.Validate);
            Assert.Contains("restricted to deterministic protocol tests", ex.Message);
        }
    }

    [Fact]
    public void NegotiateMechanism_IsNotReportedCryptographicallyCapable()
    {
        using var negotiate = new NegotiateGssMechanism();

        Assert.False(RpcSecGssMechanism.ProvidesCryptographicProtection(negotiate));
        Assert.False(negotiate.ProvidesCryptographicProtection);
        Assert.False(RpcSecGssMechanism.CanComputeMic(negotiate));
        Assert.False(negotiate.CanComputeMic);
    }

    [Fact]
    public void NegotiateMechanism_IsRejectedForEveryServiceIncludingNone()
    {
        // Header MICs are mandatory for all data calls; Negotiate cannot produce them at all.
        foreach (var service in new[]
                 {
                     RpcSecGssService.None,
                     RpcSecGssService.Integrity,
                     RpcSecGssService.Privacy
                 })
        {
            var options = new NfsClientOptions
            {
                GssMechanism = new NegotiateGssMechanism(),
                GssService = service,
            };
            var ex = Assert.Throws<NfsException>(options.Validate);
            Assert.Contains("cannot compute the RPCSEC_GSS data-call header verifier", ex.Message);
        }
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
    public void RpcSecGssProc_PreservesObsoleteCompatibilityAliases()
    {
#pragma warning disable CS0618 // Compatibility aliases are intentional.
        Assert.Equal((uint)RpcSecGssProc.Data, (uint)RpcSecGssProc.Create);
        Assert.Equal(4u, (uint)RpcSecGssProc.GetMic);
        Assert.Equal(5u, (uint)RpcSecGssProc.Wrap);
#pragma warning restore CS0618
    }

    [Fact]
    public void NegotiateGssMechanism_MicOperationsFailClosed()
    {
        // NegotiateAuthentication cannot bind a verifier to data; accepting one would be dishonest.
        using var mechanism = new NegotiateGssMechanism();

        Assert.Throws<NfsException>(() => mechanism.GetMic([1, 2, 3]));
        Assert.Throws<NfsException>(() => mechanism.VerifyMic([1, 2, 3], [4, 5, 6]));
        Assert.Throws<NfsException>(() => mechanism.GetMic([1, 2, 3], 0));
        Assert.Throws<NfsException>(() => mechanism.VerifyMic([1, 2, 3], [4, 5, 6], 0));
        Assert.Throws<NfsException>(() => mechanism.Wrap([1, 2, 3]));
        Assert.Throws<NfsException>(() => mechanism.Unwrap([4, 5, 6]));
    }

    [Fact]
    public void InstallContext_AcceptsServerSelectedSequenceWindowAboveLocalDefault()
    {
        // RFC 2203 does not cap seq_window at 64; a peer may negotiate 128 or more.
        var session = new RpcSecGssSession(new NfsClientOptions
        {
            GssMechanism = new FakeGssMechanism(),
            GssService = RpcSecGssService.Integrity,
        });

        session.InstallContext(new RpcSecGssContext
        {
            ContextHandle = ContextHandle,
            SeqWindowSize = 128,
            SeqWindow = new byte[8],
            Service = RpcSecGssService.Integrity,
            Mechanism = new FakeGssMechanism(),
        });

        Assert.True(session.IsEstablished);
        Assert.Equal(1u, session.BeginDataCall(Xid, 100003, 3, 1).SeqNum);
    }

    [Theory]
    [InlineData(2u, 8, 2)]
    [InlineData(8u, 2, 2)]
    public async Task CallSlots_RespectNegotiatedAndLocalOutstandingLimits(
        uint sequenceWindow,
        int localLimit,
        int expectedLimit)
    {
        var mechanism = new FakeGssMechanism();
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.Integrity,
            MaxOutstandingRpcCallsPerConnection = localLimit,
        };
        var session = new RpcSecGssSession(options);
        session.InstallContext(new RpcSecGssContext
        {
            ContextHandle = ContextHandle,
            SeqWindowSize = sequenceWindow,
            SeqWindow = new byte[8],
            Service = RpcSecGssService.Integrity,
            Mechanism = mechanism,
        });

        for (var i = 0; i < expectedLimit; i++)
            await session.AcquireCallSlotAsync(CancellationToken.None);

        var waiting = session.AcquireCallSlotAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        session.ReleaseCallSlot();
        await waiting;
        for (var i = 0; i < expectedLimit; i++)
            session.ReleaseCallSlot();
    }

    [Theory]
    [InlineData(13u)]
    [InlineData(14u)]
    public async Task RejectedContext_FailsClosedAndRequiresANewClient(uint authStatus)
    {
        var mechanism = new FakeGssMechanism();
        var session = CreateEstablishedSession(mechanism);
        var oldGeneration = session.ContextGeneration;

        session.RejectContext(authStatus);

        Assert.False(session.IsEstablished);
        Assert.True(session.ContextGeneration > oldGeneration);
        var exception = await Assert.ThrowsAsync<NfsException>(
            () => session.AcquireCallSlotAsync(CancellationToken.None));
        Assert.Equal(authStatus, exception.RpcAuthStatus);
        Assert.Contains("establish a new NFS client", exception.Message);
        Assert.Throws<NfsException>(() => session.BeginDataCall(Xid, 100003, 3, 1));
    }

    [Fact]
    public async Task RpcClient_InvalidatesRejectedGssContextWithoutReplaying()
    {
        var mechanism = new FakeGssMechanism();
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
            MaxOutstandingRpcCallsPerConnection = 2,
            MaxRetries = 0,
        };
        var session = new RpcSecGssSession(options);
        var stream = new GssMultiplexingStream(sequenceWindow: 2) { RejectAuthStatus = 14 };
        await using var rpc = new RpcClient(stream, options, session);
        await session.EstablishAsync("server.example", rpc, CancellationToken.None);

        var rejectedCall = rpc.CallAsync(100003, 3, 1, UIntBytes(303), CancellationToken.None);
        await stream.WaitForFirstDataCallAsync();
        var rejection = await Assert.ThrowsAsync<NfsException>(() => rejectedCall);

        Assert.Equal(14u, rejection.RpcAuthStatus);
        Assert.Contains("auth_stat=14", rejection.Message);
        Assert.False(session.IsEstablished);
        var sendsBeforeRetry = stream.DataCallCount;
        var nextCall = await Assert.ThrowsAsync<NfsException>(
            () => rpc.CallAsync(100003, 3, 1, UIntBytes(404), CancellationToken.None));
        Assert.Equal(14u, nextCall.RpcAuthStatus);
        Assert.Equal(sendsBeforeRetry, stream.DataCallCount);
    }

    [Fact]
    public async Task RpcClient_UsesAuthSysBeforeGssContextEstablishment()
    {
        var options = new NfsClientOptions
        {
            GssMechanism = new FakeGssMechanism(),
            GssService = RpcSecGssService.None,
            MaxRetries = 0,
        };
        var session = new RpcSecGssSession(options);
        var reply = new XdrWriter();
        reply.UInt(1); // XID
        reply.UInt(1); // REPLY
        reply.UInt(0); // MSG_ACCEPTED
        reply.UInt((uint)RpcSecGssFlavor.None);
        reply.Opaque([]);
        reply.UInt(0); // SUCCESS
        reply.UInt(123); // procedure result
        var stream = new ContextReplyStream(reply.ToArray());
        await using var rpc = new RpcClient(stream, options, session);

        var result = await rpc.CallAsync(100000, 2, 3, [], CancellationToken.None);

        Assert.Equal(123u, result.UInt());
        Assert.False(session.RequiresGss);
        var call = ReadCallFrames(stream.Written).Single();
        for (var i = 0; i < 6; i++)
            call.UInt();
        Assert.Equal((uint)RpcSecGssFlavor.Sys, call.UInt());
    }

    [Fact]
    public async Task RpcClient_MatchesOutOfOrderGssRepliesToTheirAttemptRecords()
    {
        var mechanism = new FakeGssMechanism();
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
            MaxOutstandingRpcCallsPerConnection = 2,
            MaxRetries = 0,
        };
        var session = new RpcSecGssSession(options);
        var stream = new GssMultiplexingStream(sequenceWindow: 2);
        await using var rpc = new RpcClient(stream, options, session);
        await session.EstablishAsync("server.example", rpc, CancellationToken.None);

        var first = rpc.CallAsync(100003, 3, 1, UIntBytes(101), CancellationToken.None);
        var second = rpc.CallAsync(100003, 3, 1, UIntBytes(202), CancellationToken.None);
        await stream.WaitForDataCallsAsync(2);
        await stream.ReplyInReverseOrderAsync();

        Assert.Equal(101u, (await first).UInt());
        Assert.Equal(202u, (await second).UInt());
        Assert.Equal(new uint[] { 1, 2 }, stream.SequenceNumbers);
        Assert.Equal(2, rpc.PendingCallHighWaterMarkForTesting);
    }

    [Fact]
    public void NoOpMechanism_HasDeterministicFixtureMicButNoSecurityCapability()
    {
        var mechanism = new NoOpGssMechanism();
        Assert.NotEmpty(mechanism.GetMic([1, 2, 3]));
        Assert.False(RpcSecGssMechanism.ProvidesCryptographicProtection(mechanism));
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

    [Fact]
    public async Task EstablishAsync_SendsRfc2203InitAndVerifiesSeqWindowMic()
    {
        var mechanism = new FakeGssMechanism();
        var seqWindow = 16u;
        var stream = new ContextReplyStream(ContextReply(
            xid: 1,
            verifierFlavor: (uint)RpcSecGssFlavor.Gss,
            verifier: FakeGssMechanism.Mac(UIntBytes(seqWindow), 0),
            handle: ContextHandle,
            majorStatus: 0,
            minorStatus: 0,
            seqWindow,
            token: []));
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
        };
        await using var rpc = new RpcClient(stream, options);
        var session = new RpcSecGssSession(options);

        await session.EstablishAsync("server.example", rpc, CancellationToken.None);

        Assert.True(session.IsEstablished);
        Assert.Equal(1u, session.BeginDataCall(2, 100003, 3, 0).SeqNum);
        var call = ReadCallFrames(stream.Written).Single();
        Assert.Equal(1u, call.UInt()); // XID
        Assert.Equal(0u, call.UInt()); // CALL
        Assert.Equal(2u, call.UInt()); // RPC version
        Assert.Equal(100003u, call.UInt());
        Assert.Equal(3u, call.UInt());
        Assert.Equal(0u, call.UInt());
        Assert.Equal((uint)RpcSecGssFlavor.Gss, call.UInt());
        var credential = new XdrReader(call.Opaque());
        Assert.Equal(RpcSecGssConstants.Version, credential.UInt());
        Assert.Equal((uint)RpcSecGssProc.Init, credential.UInt());
        Assert.Equal(0u, credential.UInt());
        Assert.Equal((uint)RpcSecGssService.None, credential.UInt());
        Assert.Empty(credential.Opaque());
        Assert.Equal(0, credential.Remaining);
        Assert.Equal((uint)RpcSecGssFlavor.None, call.UInt());
        Assert.Empty(call.Opaque());
        Assert.Equal(new byte[] { 0x01 }, call.Opaque());
        Assert.Equal(0, call.Remaining);
    }

    [Fact]
    public async Task EstablishAsync_ContinuesWithReturnedHandleAndToken()
    {
        var mechanism = new FakeGssMechanism
        {
            IsEstablished = false,
            EstablishOnContinueCall = 2,
            RequireEstablishedForVerification = true,
        };
        var seqWindow = 32u;
        var stream = new ContextReplyStream(
            ContextReply(
                xid: 1,
                verifierFlavor: (uint)RpcSecGssFlavor.None,
                verifier: [],
                handle: ContextHandle,
                majorStatus: 1,
                minorStatus: 0,
                seqWindow,
                token: [0xA1]),
            ContextReply(
                xid: 2,
                verifierFlavor: (uint)RpcSecGssFlavor.Gss,
                verifier: FakeGssMechanism.Mac(UIntBytes(seqWindow), 0),
                handle: ContextHandle,
                majorStatus: 0,
                minorStatus: 0,
                seqWindow,
                token: []));
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
            CommandTimeout = TimeSpan.FromSeconds(2),
        };
        await using var rpc = new RpcClient(stream, options);
        var session = new RpcSecGssSession(options);

        await session.EstablishAsync("server.example", rpc, CancellationToken.None);

        Assert.Equal(new byte[] { 0xA1 }, mechanism.ServerTokens[0]);
        Assert.Empty(mechanism.ServerTokens[1]);
        var calls = ReadCallFrames(stream.Written);
        Assert.Equal(2, calls.Count);
        var continuation = calls[1];
        Assert.Equal(2u, continuation.UInt()); // XID
        Assert.Equal(0u, continuation.UInt()); // CALL
        Assert.Equal(2u, continuation.UInt()); // RPC version
        Assert.Equal(100003u, continuation.UInt());
        Assert.Equal(3u, continuation.UInt());
        Assert.Equal(0u, continuation.UInt()); // NULLPROC
        Assert.Equal((uint)RpcSecGssFlavor.Gss, continuation.UInt());
        var credential = new XdrReader(continuation.Opaque());
        Assert.Equal(RpcSecGssConstants.Version, credential.UInt());
        Assert.Equal((uint)RpcSecGssProc.ContinueInit, credential.UInt());
        Assert.Equal(0u, credential.UInt());
        Assert.Equal((uint)RpcSecGssService.None, credential.UInt());
        Assert.Equal(ContextHandle, credential.Opaque());
        Assert.Equal(0, credential.Remaining);
        Assert.Equal((uint)RpcSecGssFlavor.None, continuation.UInt());
        Assert.Empty(continuation.Opaque());
        Assert.Equal(new byte[] { 0x02 }, continuation.Opaque());
    }

    [Fact]
    public async Task EstablishAsync_DisposesMechanismWhenFinalVerifierIsInvalid()
    {
        var mechanism = new FakeGssMechanism();
        var stream = new ContextReplyStream(ContextReply(
            xid: 1,
            verifierFlavor: (uint)RpcSecGssFlavor.Gss,
            verifier: [0xFF],
            handle: ContextHandle,
            majorStatus: 0,
            minorStatus: 0,
            seqWindow: 16,
            token: []));
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
        };
        await using var rpc = new RpcClient(stream, options);
        var session = new RpcSecGssSession(options);

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => session.EstablishAsync("server.example", rpc, CancellationToken.None));

        Assert.Contains("MIC mismatch", exception.Message);
        Assert.True(mechanism.Disposed);
        Assert.False(session.IsEstablished);
    }

    [Fact]
    public async Task EstablishAsync_RedactsMechanismFailureAndDisposesContext()
    {
        var mechanism = new FakeGssMechanism
        {
            VerificationException = new InvalidOperationException("secret-token-and-mic"),
        };
        Span<byte> seqWindowBytes = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(seqWindowBytes, 16);
        var stream = new ContextReplyStream(ContextReply(
            xid: 1,
            verifierFlavor: (uint)RpcSecGssFlavor.Gss,
            verifier: FakeGssMechanism.Mac(seqWindowBytes.ToArray(), RpcSecGssConstants.DefaultQop),
            handle: ContextHandle,
            majorStatus: 0,
            minorStatus: 0,
            seqWindow: 16,
            token: []));
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
        };
        await using var rpc = new RpcClient(stream, options);
        var session = new RpcSecGssSession(options);

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => session.EstablishAsync("server.example", rpc, CancellationToken.None));

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("secret-token-and-mic", exception.ToString());
        Assert.True(mechanism.Disposed);
        Assert.False(session.IsEstablished);
    }

    [Fact]
    public async Task EstablishAsync_RejectsMissingFinalVerifierAndEmptyContextHandle()
    {
        var missingVerifierMechanism = new FakeGssMechanism();
        var missingVerifierStream = new ContextReplyStream(ContextReply(
            xid: 1,
            verifierFlavor: (uint)RpcSecGssFlavor.None,
            verifier: [],
            handle: ContextHandle,
            majorStatus: 0,
            minorStatus: 0,
            seqWindow: 16,
            token: []));
        var missingVerifierOptions = new NfsClientOptions
        {
            GssMechanism = missingVerifierMechanism,
            GssService = RpcSecGssService.None,
        };
        await using (var rpc = new RpcClient(missingVerifierStream, missingVerifierOptions))
        {
            var session = new RpcSecGssSession(missingVerifierOptions);
            var exception = await Assert.ThrowsAsync<NfsException>(
                () => session.EstablishAsync("server.example", rpc, CancellationToken.None));
            Assert.Contains("expected flavor RPCSEC_GSS", exception.Message);
            Assert.False(session.IsEstablished);
        }
        Assert.True(missingVerifierMechanism.Disposed);

        var emptyHandleMechanism = new FakeGssMechanism();
        var emptyHandleStream = new ContextReplyStream(ContextReply(
            xid: 1,
            verifierFlavor: (uint)RpcSecGssFlavor.Gss,
            verifier: FakeGssMechanism.Mac(UIntBytes(16), 0),
            handle: [],
            majorStatus: 0,
            minorStatus: 0,
            seqWindow: 16,
            token: []));
        var emptyHandleOptions = new NfsClientOptions
        {
            GssMechanism = emptyHandleMechanism,
            GssService = RpcSecGssService.None,
        };
        await using (var rpc = new RpcClient(emptyHandleStream, emptyHandleOptions))
        {
            var session = new RpcSecGssSession(emptyHandleOptions);
            var exception = await Assert.ThrowsAsync<NfsException>(
                () => session.EstablishAsync("server.example", rpc, CancellationToken.None));
            Assert.Contains("empty context handle", exception.Message);
            Assert.False(session.IsEstablished);
        }
        Assert.True(emptyHandleMechanism.Disposed);
    }

    [Fact]
    public async Task EstablishAsync_RejectsNonNullContinuationVerifier()
    {
        var mechanism = new FakeGssMechanism { IsEstablished = false };
        var stream = new ContextReplyStream(ContextReply(
            xid: 1,
            verifierFlavor: (uint)RpcSecGssFlavor.Gss,
            verifier: [0xAA],
            handle: ContextHandle,
            majorStatus: 1,
            minorStatus: 0,
            seqWindow: 16,
            token: [0xA1]));
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = RpcSecGssService.None,
        };
        await using var rpc = new RpcClient(stream, options);
        var session = new RpcSecGssSession(options);

        var exception = await Assert.ThrowsAsync<NfsException>(
            () => session.EstablishAsync("server.example", rpc, CancellationToken.None));

        Assert.Contains("empty AUTH_NONE verifier", exception.Message);
        Assert.True(mechanism.Disposed);
        Assert.False(session.IsEstablished);
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

    private static RpcSecGssSession CreateEstablishedSession(
        FakeGssMechanism mechanism,
        RpcSecGssService service = RpcSecGssService.Integrity)
    {
        var options = new NfsClientOptions
        {
            GssMechanism = mechanism,
            GssService = service,
        };
        var session = new RpcSecGssSession(options);
        session.InstallContext(NewContext(mechanism, service));
        return session;
    }

    private static RpcReply ParseAcceptedReply(
        RpcSecGssCallRecord record,
        byte[] verifier,
        uint verifierFlavor = (uint)RpcSecGssFlavor.Gss,
        byte[]? body = null)
    {
        var fixture = new XdrWriter();
        fixture.UInt(record.Xid);
        fixture.UInt(1); // REPLY
        fixture.UInt(0); // MSG_ACCEPTED
        fixture.UInt(verifierFlavor);
        fixture.Opaque(verifier);
        fixture.UInt(0); // SUCCESS
        if (body is not null)
            fixture.Raw(body);
        return RpcReplyParser.DecodeAccepted(fixture.ToArray(), record.Xid);
    }

    private static byte[] ContextReply(
        uint xid,
        uint verifierFlavor,
        byte[] verifier,
        byte[] handle,
        uint majorStatus,
        uint minorStatus,
        uint seqWindow,
        byte[] token)
    {
        var reply = new XdrWriter();
        reply.UInt(xid);
        reply.UInt(1); // REPLY
        reply.UInt(0); // MSG_ACCEPTED
        reply.UInt(verifierFlavor);
        reply.Opaque(verifier);
        reply.UInt(0); // SUCCESS
        reply.Opaque(handle);
        reply.UInt(majorStatus);
        reply.UInt(minorStatus);
        reply.UInt(seqWindow);
        reply.Opaque(token);
        return reply.ToArray();
    }

    private static byte[] UIntBytes(uint value)
    {
        var bytes = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return bytes;
    }

    private static byte[] Frame(byte[] message)
    {
        var framed = new byte[sizeof(uint) + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(framed.AsSpan(0, sizeof(uint)), 0x8000_0000u | (uint)message.Length);
        message.CopyTo(framed, sizeof(uint));
        return framed;
    }

    private static List<XdrReader> ReadCallFrames(byte[] framed)
    {
        var calls = new List<XdrReader>();
        var offset = 0;
        while (offset < framed.Length)
        {
            var marker = BinaryPrimitives.ReadUInt32BigEndian(framed.AsSpan(offset, sizeof(uint)));
            var length = (int)(marker & 0x7FFF_FFFF);
            calls.Add(new XdrReader(framed.AsSpan(offset + sizeof(uint), length).ToArray()));
            offset += sizeof(uint) + length;
        }

        return calls;
    }

    private sealed class ContextReplyStream(params byte[][] replies) : Stream
    {
        private readonly MemoryStream _writes = new();
        private readonly System.Threading.Channels.Channel<byte[]> _replies =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private byte[]? _currentReply;
        private int _currentOffset;
        private int _writeCount;

        internal byte[] Written => _writes.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref _writeCount) - 1;
            if (index < replies.Length)
                await _replies.Writer.WriteAsync(Frame(replies[index]), cancellationToken);
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentReply is null || _currentOffset == _currentReply.Length)
            {
                _currentReply = await _replies.Reader.ReadAsync(cancellationToken);
                _currentOffset = 0;
            }

            var count = Math.Min(buffer.Length, _currentReply.Length - _currentOffset);
            _currentReply.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await _writes.WriteAsync(buffer, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _replies.Writer.TryComplete();
                _writes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _writes.Write(buffer, offset, count);
    }

    private sealed class GssMultiplexingStream(uint sequenceWindow) : Stream
    {
        private readonly MemoryStream _writes = new();
        private readonly System.Threading.Channels.Channel<byte[]> _replies =
            System.Threading.Channels.Channel.CreateUnbounded<byte[]>();
        private readonly List<(uint Xid, uint SeqNum, uint Value)> _dataCalls = [];
        private readonly TaskCompletionSource _oneCall = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _twoCalls = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private byte[]? _currentReply;
        private int _currentOffset;
        private int _writtenOffset;

        internal uint[] SequenceNumbers
        {
            get
            {
                lock (_dataCalls)
                    return _dataCalls.Select(call => call.SeqNum).ToArray();
            }
        }

        internal uint? RejectAuthStatus { get; init; }
        internal int DataCallCount
        {
            get
            {
                lock (_dataCalls)
                    return _dataCalls.Count;
            }
        }

        internal Task WaitForFirstDataCallAsync() => _oneCall.Task.WaitAsync(TimeSpan.FromSeconds(5));

        internal Task WaitForDataCallsAsync(int count) =>
            count <= 2 ? _twoCalls.Task.WaitAsync(TimeSpan.FromSeconds(5)) : Task.FromException(new ArgumentOutOfRangeException(nameof(count)));

        internal async Task ReplyInReverseOrderAsync()
        {
            (uint Xid, uint SeqNum, uint Value)[] calls;
            lock (_dataCalls)
                calls = _dataCalls.AsEnumerable().Reverse().ToArray();
            foreach (var call in calls)
            {
                var reply = new XdrWriter();
                reply.UInt(call.Xid);
                reply.UInt(1);
                reply.UInt(0);
                reply.UInt((uint)RpcSecGssFlavor.Gss);
                reply.Opaque(FakeGssMechanism.Mac(UIntBytes(call.SeqNum), RpcSecGssConstants.DefaultQop));
                reply.UInt(0);
                reply.UInt(call.Value);
                await _replies.Writer.WriteAsync(Frame(reply.ToArray()));
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }

        public override async Task FlushAsync(CancellationToken cancellationToken)
        {
            var bytes = _writes.ToArray();
            var marker = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(_writtenOffset, sizeof(uint)));
            var length = (int)(marker & 0x7FFF_FFFF);
            var message = bytes.AsSpan(_writtenOffset + sizeof(uint), length).ToArray();
            _writtenOffset += sizeof(uint) + length;
            var call = new XdrReader(message);
            var xid = call.UInt();
            call.UInt(); // CALL
            call.UInt(); // RPC version
            call.UInt(); // program
            call.UInt(); // version
            call.UInt(); // procedure
            call.UInt(); // credential flavor
            var credential = new XdrReader(call.Opaque());
            credential.UInt();
            var gssProc = credential.UInt();

            if (gssProc == (uint)RpcSecGssProc.Init)
            {
                var response = new XdrWriter();
                response.UInt(xid);
                response.UInt(1);
                response.UInt(0);
                response.UInt((uint)RpcSecGssFlavor.Gss);
                response.Opaque(FakeGssMechanism.Mac(UIntBytes(sequenceWindow), RpcSecGssConstants.DefaultQop));
                response.UInt(0);
                response.Opaque(ContextHandle);
                response.UInt(0);
                response.UInt(0);
                response.UInt(sequenceWindow);
                response.Opaque([]);
                await _replies.Writer.WriteAsync(Frame(response.ToArray()), cancellationToken);
                return;
            }

            var seqNum = credential.UInt();
            credential.UInt(); // service
            credential.Opaque(); // context handle
            call.UInt(); // RPCSEC_GSS verifier flavor
            call.Opaque(); // RPCSEC_GSS header MIC
            var value = call.UInt();
            lock (_dataCalls)
            {
                _dataCalls.Add((xid, seqNum, value));
                _oneCall.TrySetResult();
                if (_dataCalls.Count >= 2)
                    _twoCalls.TrySetResult();
            }
            if (RejectAuthStatus is { } authStatus)
            {
                var denied = new XdrWriter();
                denied.UInt(xid);
                denied.UInt(1);
                denied.UInt(1); // MSG_DENIED
                denied.UInt(1); // AUTH_ERROR
                denied.UInt(authStatus);
                await _replies.Writer.WriteAsync(Frame(denied.ToArray()), cancellationToken);
            }
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentReply is null || _currentOffset == _currentReply.Length)
            {
                _currentReply = await _replies.Reader.ReadAsync(cancellationToken);
                _currentOffset = 0;
            }

            var count = Math.Min(buffer.Length, _currentReply.Length - _currentOffset);
            _currentReply.AsMemory(_currentOffset, count).CopyTo(buffer);
            _currentOffset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) =>
            _writes.WriteAsync(buffer, cancellationToken);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _replies.Writer.TryComplete();
                _writes.Dispose();
            }
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _writes.Write(buffer, offset, count);
    }

    /// <summary>
    /// Deterministic fake GSS mechanism. The MIC is a function of both the data bytes and the QOP,
    /// so a no-op or QOP-blind verifier cannot accidentally pass negative fixtures.
    /// </summary>
    private sealed class FakeGssMechanism : IRpcSecGssMechanism, IRpcSecGssQopMechanism,
        IRpcSecGssMicResultMechanism,
        IRpcSecGssMechanismCapabilities, IRpcSecGssPrivacyMechanism
    {
        public byte[] MechanismOid => [0x2A];
        public bool IsEstablished { get; set; } = true;
        public int EstablishOnContinueCall { get; init; } = 1;
        public bool RequireEstablishedForVerification { get; init; }
        public bool Disposed { get; private set; }
        public List<byte[]> ServerTokens { get; } = [];
        private int _continueCalls;
        public uint MaxMessageSize => 64 * 1024;
        public uint NextSeqNum { get; set; } = 1;
        public RpcSecGssService NegotiatedService { get; set; }
        public bool ProvidesCryptographicProtection => true;
        public bool CanComputeMic => true;
        public bool ReportConfidentiality { get; set; } = true;
        public uint ReportedQopOffset { get; set; }
        public uint ReportedMicQopOffset { get; set; }
        public Exception? VerificationException { get; set; }
        public bool ReturnDefaultMicResult { get; init; }

        public Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct) =>
            Task.FromResult(new byte[] { 0x01 });

        public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct)
        {
            ServerTokens.Add((byte[])serverToken.Clone());
            if (Interlocked.Increment(ref _continueCalls) >= EstablishOnContinueCall)
                IsEstablished = true;
            return Task.FromResult(new byte[] { 0x02 });
        }

        public byte[] GetMic(byte[] data) => Mac(data, 0);
        public bool VerifyMic(byte[] data, byte[] mic) => VerifyMic(data, mic, 0);
        public byte[] Wrap(byte[] data) => data;
        public byte[] Unwrap(byte[] wrappedData) => wrappedData;
        public byte[] GetMic(byte[] data, uint qop) => Mac(data, qop);
        public bool VerifyMic(byte[] data, byte[] mic, uint qop)
        {
            if (RequireEstablishedForVerification && !IsEstablished)
                throw new InvalidOperationException("The GSS context is not established.");
            return Mac(data, qop).AsSpan().SequenceEqual(mic);
        }
        public RpcSecGssMicVerificationResult VerifyMicWithResult(byte[] data, byte[] mic, uint qop)
        {
            if (ReturnDefaultMicResult)
                return default;
            if (VerificationException is not null)
                throw VerificationException;
            var valid = VerifyMic(data, mic, qop);
            return new RpcSecGssMicVerificationResult(
                valid ? RpcSecGssMicVerificationStatus.Verified : RpcSecGssMicVerificationStatus.Invalid,
                qop + ReportedMicQopOffset);
        }
        public byte[] Wrap(byte[] data, uint qop) => data;
        public byte[] Unwrap(byte[] wrappedData, uint qop) => wrappedData;
        public RpcSecGssPrivacyResult WrapPrivacy(byte[] data, uint qop)
        {
            var checksum = Mac(data, qop);
            var wrapped = new byte[data.Length + checksum.Length];
            data.CopyTo(wrapped, 0);
            checksum.CopyTo(wrapped, data.Length);
            return new RpcSecGssPrivacyResult(wrapped, qop + ReportedQopOffset, ReportConfidentiality);
        }

        public RpcSecGssPrivacyResult UnwrapPrivacy(byte[] wrappedData, uint qop)
        {
            if (wrappedData.Length < 8)
                throw new NfsException("Invalid fake privacy token.");
            var data = wrappedData.AsSpan(0, wrappedData.Length - 8).ToArray();
            var checksum = wrappedData.AsSpan(wrappedData.Length - 8).ToArray();
            if (!VerifyMic(data, checksum, qop))
                throw new NfsException("Invalid fake privacy MIC.");
            return new RpcSecGssPrivacyResult(data, qop + ReportedQopOffset, ReportConfidentiality);
        }
        public void Dispose() => Disposed = true;

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
