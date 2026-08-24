using System.Buffers.Binary;
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
