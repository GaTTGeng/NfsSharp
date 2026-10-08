using System.Buffers.Binary;
using System.Threading.Channels;
using NfsSharp.Client;
using NfsSharp.Protocol;

namespace NfsSharp.Tests;

public sealed class NfsV3ArchitectureTests
{
    [Fact]
    public async Task ProtocolClient_EncodesLookupAndDecodesReplyWithoutFacade()
    {
        var response = new XdrWriter();
        response.UInt(NfsV3Status.Ok);
        response.Opaque([9, 8, 7]);
        response.Bool(false);
        response.Bool(false);
        var rpc = new RecordingRpcClient(response.ToArray());
        var protocol = new NfsV3ProtocolClient(
            rpc,
            NfsClientOptions.Default,
            new NfsDirectoryCache(NfsClientOptions.Default));

        var result = await protocol.LookupAsync([1, 2, 3], "item", CancellationToken.None);

        Assert.Equal(new byte[] { 9, 8, 7 }, result.Handle);
        Assert.Null(result.Attr);
        Assert.Equal(NfsRpcConstants.ProgNfs, rpc.Program);
        Assert.Equal(NfsRpcConstants.VerNfs, rpc.Version);
        Assert.Equal(NfsRpcConstants.NfsLookup, rpc.Procedure);
        var arguments = new XdrReader(rpc.Arguments);
        Assert.Equal(new byte[] { 1, 2, 3 }, arguments.Opaque());
        Assert.Equal("item", arguments.Str());
        Assert.Equal(0, arguments.Remaining);
    }

    [Fact]
    public async Task PathResolver_ResolvesParentAndRejectsTraversal()
    {
        var observed = new List<string>();
        var resolver = new NfsPathResolver(
            () => [1],
            (parent, name, _) =>
            {
                observed.Add(name);
                return Task.FromResult(new NfsLookup([checked((byte)(parent[0] + 1))],
                    new NfsFattr(NfsType.Dir, 0, null)));
            },
            (_, _) => throw new InvalidOperationException());

        var (parent, name) = await resolver.ResolveParentAsync("/one/./two/file", CancellationToken.None);

        Assert.Equal(new[] { "one", "two" }, observed);
        Assert.Equal(new byte[] { 3 }, parent);
        Assert.Equal("file", name);
        Assert.Throws<NfsException>(() => NfsPathResolver.Split("../outside").ToArray());
    }

    [Fact]
    public void DirectoryCache_ClonesResultsAndInvalidatesContainingDirectory()
    {
        var options = NfsClientOptions.Default with
        {
            EnableDirectoryCache = true,
            DirectoryCacheTtl = TimeSpan.FromMinutes(1)
        };
        var cache = new NfsDirectoryCache(options);
        var directory = new byte[] { 1 };
        var child = new byte[] { 2 };
        cache.Store(
            directory,
            [new NfsEntryPlus("child", 1, null, child)],
            cache.CaptureMutationGeneration());

        Assert.True(cache.TryGet(directory, out var first));
        first[0].Handle![0] = 99;
        Assert.True(cache.TryGet(directory, out var second));
        Assert.Equal(2, second[0].Handle![0]);

        cache.InvalidateForMutation(child);
        Assert.False(cache.TryGet(directory, out _));
    }

    [Fact]
    public void DirectoryCache_DoesNotPublishReadThatRacedWithMutation()
    {
        var options = NfsClientOptions.Default with { EnableDirectoryCache = true };
        var cache = new NfsDirectoryCache(options);
        var generation = cache.CaptureMutationGeneration();

        cache.Invalidate([1]);
        cache.Store([1], [new NfsEntryPlus("stale", 1, null, [2])], generation);

        Assert.False(cache.TryGet([1], out _));
    }

    [Fact]
    public async Task Transport_RecordSeamWritesAndReadsDeterministicFrames()
    {
        var stream = new MemoryStream();
        await RpcTransport.SendRecordAsync(stream, new byte[] { 1, 2, 3 }, CancellationToken.None);
        var frame = stream.ToArray();
        Assert.Equal(0x8000_0003u, BinaryPrimitives.ReadUInt32BigEndian(frame));

        stream.Position = 0;
        Assert.Equal(new byte[] { 1, 2, 3 },
            await RpcTransport.ReceiveRecordAsync(stream, CancellationToken.None));
    }

    [Fact]
    public async Task RpcClient_EncodesEnvelopeAndExposesOnlyProcedurePayload()
    {
        var reply = new XdrWriter();
        reply.UInt(1);
        reply.UInt(1);
        reply.UInt(0);
        reply.UInt(0);
        reply.Opaque([]);
        reply.UInt(0);
        reply.UInt(0xCAFE_BABE);
        var stream = new ScriptedDuplexStream(Frame(reply.ToArray()), maxReadSize: 3);
        await using var rpc = new RpcClient(stream, NfsClientOptions.Default with { MaxRetries = 0 });

        var body = await rpc.CallAsync(100003, 3, 1, [0x01, 0x02, 0x03, 0x04], CancellationToken.None);

        Assert.Equal(0xCAFE_BABEu, body.UInt());
        var written = stream.Written;
        Assert.Equal(0x8000_0000u | (uint)(written.Length - 4), BinaryPrimitives.ReadUInt32BigEndian(written));
        var call = new XdrReader(written[4..]);
        Assert.Equal(1u, call.UInt());
        Assert.Equal(0u, call.UInt());
        Assert.Equal(2u, call.UInt());
        Assert.Equal(100003u, call.UInt());
        Assert.Equal(3u, call.UInt());
        Assert.Equal(1u, call.UInt());
        Assert.Equal(1u, call.UInt());
        _ = call.Opaque();
        Assert.Equal(0u, call.UInt());
        Assert.Empty(call.Opaque());
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, call.FixedBytes(4));
    }

    [Fact]
    public async Task RpcClient_DistinguishesCommandTimeoutFromRequestedCancellation()
    {
        var options = NfsClientOptions.Default with
        {
            CommandTimeout = TimeSpan.FromMilliseconds(20),
            MaxRetries = 0
        };
        await using var timedRpc = new RpcClient(new ScriptedDuplexStream([], stallReads: true), options);
        var timeout = await Assert.ThrowsAsync<NfsException>(
            () => timedRpc.CallAsync(100003, 3, 1, [], CancellationToken.None));
        Assert.Contains("timed out", timeout.Message, StringComparison.OrdinalIgnoreCase);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await using var cancelledRpc = new RpcClient(new ScriptedDuplexStream([], stallReads: true), options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cancelledRpc.CallAsync(100003, 3, 1, [], cancellation.Token));
    }

    [Fact]
    public async Task RpcClient_DispatchesConcurrentRepliesByXidWhenTheyArriveOutOfOrder()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: true);
        var options = NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 2
        };
        await using var rpc = new RpcClient(stream, options);

        var first = rpc.CallAsync(100003, 3, 1, UIntArgument(101), CancellationToken.None);
        var second = rpc.CallAsync(100003, 3, 1, UIntArgument(202), CancellationToken.None);
        await stream.WaitForRequestsAsync(2);

        Assert.Equal(101u, (await first).UInt());
        Assert.Equal(202u, (await second).UInt());
        Assert.Equal(0, rpc.PendingCallCountForTesting);
        Assert.Equal(2, rpc.PendingCallHighWaterMarkForTesting);
        Assert.True(stream.RepliesWereSentInReverseOrder);
    }

    [Fact]
    public async Task RpcClient_CancellingOnePendingCallDoesNotAffectOtherCallsOrLateReplies()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: false);
        var options = NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 2
        };
        await using var rpc = new RpcClient(stream, options);
        using var cancellation = new CancellationTokenSource();

        var cancelled = rpc.CallAsync(100003, 3, 1, UIntArgument(1), cancellation.Token);
        var survivor = rpc.CallAsync(100003, 3, 1, UIntArgument(2), CancellationToken.None);
        await stream.WaitForRequestsAsync(2);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);

        await stream.ReplyForXidAsync(0xDEAD_BEEFu, 99); // Unknown XID is discarded.
        await stream.ReplyToAsync(2);
        await stream.ReplyToAsync(2); // Duplicate reply is discarded.
        Assert.Equal(2u, (await survivor).UInt());
        await stream.ReplyToAsync(1); // Late XID is discarded after its caller has left.

        var later = rpc.CallAsync(100003, 3, 1, UIntArgument(3), CancellationToken.None);
        await stream.WaitForRequestsAsync(3);
        await stream.ReplyToAsync(3);
        Assert.Equal(3u, (await later).UInt());
        Assert.Equal(0, rpc.PendingCallCountForTesting);
    }

    [Fact]
    public async Task RpcClient_AppliesConfiguredOutstandingCallBound()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: false);
        var options = NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 1
        };
        await using var rpc = new RpcClient(stream, options);

        var first = rpc.CallAsync(100003, 3, 1, UIntArgument(1), CancellationToken.None);
        await stream.WaitForRequestsAsync(1);
        var second = rpc.CallAsync(100003, 3, 1, UIntArgument(2), CancellationToken.None);
        await Task.Delay(20);
        Assert.Equal(1, stream.RequestCount);

        await stream.ReplyToAsync(1);
        Assert.Equal(1u, (await first).UInt());
        await stream.WaitForRequestsAsync(2);
        await stream.ReplyToAsync(2);
        Assert.Equal(2u, (await second).UInt());
    }

    [Fact]
    public async Task RpcClient_XidWraparoundSkipsAnIdStillPendingOnTheConnection()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: false);
        await using var rpc = new RpcClient(stream, NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 2
        });
        typeof(RpcClient).GetField("_xid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(rpc, -2);

        var first = rpc.CallAsync(100003, 3, 1, UIntArgument(1), CancellationToken.None);
        await stream.WaitForRequestsAsync(1);
        typeof(RpcClient).GetField("_xid", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(rpc, -2);
        var second = rpc.CallAsync(100003, 3, 1, UIntArgument(2), CancellationToken.None);
        await stream.WaitForRequestsAsync(2);

        Assert.Equal(new[] { uint.MaxValue, 0u }, stream.RequestXids);
        await stream.ReplyToAsync(1);
        await stream.ReplyToAsync(2);
        Assert.Equal(1u, (await first).UInt());
        Assert.Equal(2u, (await second).UInt());
    }

    [Fact]
    public async Task RpcClient_MalformedReplyFailsEveryCallOnThatConnection()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: false);
        await using var rpc = new RpcClient(stream, NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 2
        });
        var first = rpc.CallAsync(100003, 3, 1, UIntArgument(1), CancellationToken.None);
        var second = rpc.CallAsync(100003, 3, 1, UIntArgument(2), CancellationToken.None);
        await stream.WaitForRequestsAsync(2);

        await stream.SendRawReplyAsync([0x01, 0x02]);

        await Assert.ThrowsAsync<NfsException>(() => first);
        await Assert.ThrowsAsync<NfsException>(() => second);
        Assert.Equal(0, rpc.PendingCallCountForTesting);
    }

    [Fact]
    public async Task RpcClient_DisposalCompletesAllPendingCallsAndStopsTheReceiveLoop()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: false);
        var rpc = new RpcClient(stream, NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 2
        });
        var first = rpc.CallAsync(100003, 3, 1, UIntArgument(1), CancellationToken.None);
        var second = rpc.CallAsync(100003, 3, 1, UIntArgument(2), CancellationToken.None);
        await stream.WaitForRequestsAsync(2);

        await rpc.DisposeAsync();

        await Assert.ThrowsAsync<NfsException>(() => first);
        await Assert.ThrowsAsync<NfsException>(() => second);
        Assert.Equal(0, rpc.PendingCallCountForTesting);
        await rpc.DisposeAsync();
    }

    [Fact]
    public async Task RpcClient_OneCommandTimeoutDoesNotInterruptAnotherOutstandingCall()
    {
        var stream = new MultiplexingTestStream(autoReplyAfterTwoRequests: false);
        await using var rpc = new RpcClient(stream, NfsClientOptions.Default with
        {
            MaxRetries = 0,
            MaxOutstandingRpcCallsPerConnection = 2,
            CommandTimeout = TimeSpan.FromMilliseconds(250)
        });
        var timedOut = rpc.CallAsync(100003, 3, 1, UIntArgument(1), CancellationToken.None);
        var survivor = rpc.CallAsync(100003, 3, 1, UIntArgument(2), CancellationToken.None);
        await stream.WaitForRequestsAsync(2);
        await stream.ReplyToAsync(2);

        Assert.Equal(2u, (await survivor).UInt());
        var timeout = await Assert.ThrowsAsync<NfsException>(() => timedOut);
        Assert.Contains("timed out", timeout.Message, StringComparison.OrdinalIgnoreCase);

        var later = rpc.CallAsync(100003, 3, 1, UIntArgument(3), CancellationToken.None);
        await stream.WaitForRequestsAsync(3);
        await stream.ReplyToAsync(3);
        Assert.Equal(3u, (await later).UInt());
    }

    [Fact]
    public async Task RpcClient_CommandTimeoutInterruptsAStalledRecordWriteAndClosesConnection()
    {
        var stream = new FaultingRpcStream(blockedWriteNumber: 2);
        await using var rpc = new RpcClient(stream, NfsClientOptions.Default with
        {
            MaxRetries = 0,
            CommandTimeout = TimeSpan.FromMilliseconds(100)
        });

        var call = rpc.CallAsync(100003, 3, 1, UIntArgument(1), CancellationToken.None);
        await stream.WaitForBlockedWriteAsync();

        var timeout = await Assert.ThrowsAsync<NfsException>(() => call);
        Assert.Contains("timed out", timeout.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(stream.IsDisposed);
        Assert.Equal(0, rpc.PendingCallCountForTesting);
    }

    [Fact]
    public async Task RpcConnection_ReceiveFailureDuringWritePropagatesConnectionFailure()
    {
        var stream = new FaultingRpcStream(blockedWriteNumber: 4);
        await using var connection = new RpcConnection(stream, maxOutstandingCalls: 2);
        Assert.True(connection.TryRegister(1, out var firstPending));
        Assert.True(connection.TryRegister(2, out var secondPending));

        var first = connection.SendAndReceiveAsync(1, firstPending, [1], CancellationToken.None);
        await stream.WaitForReadAsync();
        var second = connection.SendAndReceiveAsync(2, secondPending, [2], CancellationToken.None);
        await stream.WaitForBlockedWriteAsync();

        stream.FailRead();

        var firstFailure = await Assert.ThrowsAsync<NfsException>(() => first);
        var secondFailure = await Assert.ThrowsAsync<NfsException>(() => second);
        Assert.Contains("receive failed", firstFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("receive failed", secondFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("timed out", secondFailure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, connection.PendingCallCount);
    }

    [Fact]
    public async Task RpcClient_ExplicitStopClosesTheConnectionAndPreventsFurtherCalls()
    {
        await using var rpc = new RpcClient(new ScriptedDuplexStream([], stallReads: true), NfsClientOptions.Default);

        await rpc.StopAndCloseActiveConnectionAsync();

        await Assert.ThrowsAsync<NfsException>(
            () => rpc.CallAsync(100003, 3, 1, [], CancellationToken.None));
    }

    [Theory]
    [InlineData(100003u, 3u, 1u, true)]
    [InlineData(100003u, 3u, 7u, false)]
    [InlineData(100005u, 3u, 3u, false)]
    public void RetryPolicy_OwnsSafeProcedureClassification(
        uint program, uint version, uint procedure, bool expected) =>
        Assert.Equal(expected, NfsRetryPolicy.CanRetry(program, version, procedure));

    private sealed class RecordingRpcClient(byte[] response) : IRpcCallClient
    {
        internal uint Program { get; private set; }
        internal uint Version { get; private set; }
        internal uint Procedure { get; private set; }
        internal byte[] Arguments { get; private set; } = [];

        public Task<XdrReader> CallAsync(
            uint program,
            uint version,
            uint procedure,
            byte[] arguments,
            CancellationToken ct)
        {
            Program = program;
            Version = version;
            Procedure = procedure;
            Arguments = arguments;
            return Task.FromResult(new XdrReader(response));
        }
    }

    private static byte[] Frame(byte[] message)
    {
        var result = new byte[message.Length + 4];
        BinaryPrimitives.WriteUInt32BigEndian(result, 0x8000_0000u | (uint)message.Length);
        message.CopyTo(result, 4);
        return result;
    }

    private static byte[] UIntArgument(uint value)
    {
        var writer = new XdrWriter();
        writer.UInt(value);
        return writer.ToArray();
    }

    private sealed class FaultingRpcStream(int blockedWriteNumber) : Stream
    {
        private readonly TaskCompletionSource<bool> _blockedWrite = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource<bool> _failRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _writeCount;
        private int _disposed;

        internal bool IsDisposed => Volatile.Read(ref _disposed) != 0;

        internal Task WaitForBlockedWriteAsync() => _blockedWrite.Task.WaitAsync(TimeSpan.FromSeconds(2));
        internal Task WaitForReadAsync() => _readStarted.Task.WaitAsync(TimeSpan.FromSeconds(2));
        internal void FailRead() => _failRead.TrySetResult(true);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            _readStarted.TrySetResult(true);
            await _failRead.Task.WaitAsync(cancellationToken);
            throw new IOException("Injected receive failure.");
        }

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Interlocked.Increment(ref _writeCount) == blockedWriteNumber)
            {
                _blockedWrite.TrySetResult(true);
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
        }

        protected override void Dispose(bool disposing)
        {
            Interlocked.Exchange(ref _disposed, 1);
            base.Dispose(disposing);
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class MultiplexingTestStream(bool autoReplyAfterTwoRequests) : Stream
    {
        private readonly object _writeSync = new();
        private readonly MemoryStream _writeBuffer = new();
        private readonly Channel<byte[]> _replies = Channel.CreateUnbounded<byte[]>();
        private readonly List<(uint Xid, uint Tag)> _requests = [];
        private readonly List<uint> _replyTags = [];
        private readonly SemaphoreSlim _requestChanged = new(0);
        private byte[]? _currentReply;
        private int _replyOffset;

        internal int RequestCount { get { lock (_writeSync) return _requests.Count; } }
        internal uint[] RequestXids { get { lock (_writeSync) return _requests.Select(item => item.Xid).ToArray(); } }
        internal bool RepliesWereSentInReverseOrder { get { lock (_writeSync) return _replyTags.SequenceEqual([202u, 101u]); } }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        internal async Task WaitForRequestsAsync(int count)
        {
            while (RequestCount < count)
                await _requestChanged.WaitAsync(TimeSpan.FromSeconds(2));
        }

        internal Task ReplyToAsync(uint tag)
        {
            (uint Xid, uint Tag) request;
            lock (_writeSync)
                request = _requests.Single(item => item.Tag == tag);
            return QueueReplyAsync(request.Xid, request.Tag);
        }

        internal Task ReplyForXidAsync(uint xid, uint tag) => QueueReplyAsync(xid, tag);

        internal Task SendRawReplyAsync(byte[] reply) => _replies.Writer.WriteAsync(Frame(reply)).AsTask();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            while (_currentReply is null || _replyOffset == _currentReply.Length)
            {
                _currentReply = await _replies.Reader.ReadAsync(cancellationToken);
                _replyOffset = 0;
            }

            var count = Math.Min(buffer.Length, _currentReply.Length - _replyOffset);
            _currentReply.AsMemory(_replyOffset, count).CopyTo(buffer);
            _replyOffset += count;
            return count;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            lock (_writeSync)
            {
                _writeBuffer.Position = _writeBuffer.Length;
                _writeBuffer.Write(buffer.Span);
                ProcessCompleteRequests();
            }

            return ValueTask.CompletedTask;
        }

        private void ProcessCompleteRequests()
        {
            var bytes = _writeBuffer.ToArray();
            var offset = 0;
            while (bytes.Length - offset >= 4)
            {
                var marker = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));
                var length = (int)(marker & 0x7fff_ffff);
                if ((marker & 0x8000_0000u) == 0 || bytes.Length - offset - 4 < length)
                    break;

                var message = bytes.AsSpan(offset + 4, length);
                var reader = new XdrReader(message.ToArray());
                var xid = reader.UInt();
                _ = reader.UInt();
                _ = reader.UInt();
                _ = reader.UInt();
                _ = reader.UInt();
                _ = reader.UInt();
                _ = reader.UInt();
                _ = reader.Opaque();
                _ = reader.UInt();
                _ = reader.Opaque();
                var tag = reader.UInt();
                _requests.Add((xid, tag));
                _requestChanged.Release();
                offset += 4 + length;

                if (autoReplyAfterTwoRequests && _requests.Count == 2)
                {
                    foreach (var request in _requests.AsEnumerable().Reverse())
                        _ = QueueReplyAsync(request.Xid, request.Tag);
                }
            }

            var remaining = bytes.AsSpan(offset).ToArray();
            _writeBuffer.SetLength(0);
            _writeBuffer.Write(remaining);
        }

        private Task QueueReplyAsync(uint xid, uint tag)
        {
            var writer = new XdrWriter();
            writer.UInt(xid);
            writer.UInt(1);
            writer.UInt(0);
            writer.UInt(0);
            writer.Opaque([]);
            writer.UInt(0);
            writer.UInt(tag);
            lock (_writeSync)
                _replyTags.Add(tag);
            return _replies.Writer.WriteAsync(Frame(writer.ToArray())).AsTask();
        }

        protected override void Dispose(bool disposing)
        {
            _replies.Writer.TryComplete();
            base.Dispose(disposing);
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).GetAwaiter().GetResult();
    }

    private sealed class ScriptedDuplexStream(
        byte[] input,
        int maxReadSize = int.MaxValue,
        bool stallReads = false) : Stream
    {
        private readonly MemoryStream _writes = new();
        private int _readOffset;

        internal byte[] Written => _writes.ToArray();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() { }
        public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public override async ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            if (stallReads)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
                return 0;
            }

            var count = Math.Min(Math.Min(buffer.Length, maxReadSize), input.Length - _readOffset);
            if (count <= 0)
                return 0;
            input.AsMemory(_readOffset, count).CopyTo(buffer);
            _readOffset += count;
            return count;
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer,
            CancellationToken cancellationToken = default) =>
            _writes.WriteAsync(buffer, cancellationToken);

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _writes.Write(buffer, offset, count);
    }
}
