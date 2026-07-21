using System.Net;
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
}

public class NfsModelsTests
{
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
        var method = typeof(NfsV3Client).GetMethod(
            "ValidateRpcRecordLength",
            BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(method);

        method.Invoke(null, [1024, 1024L]);

        var ex = Assert.Throws<TargetInvocationException>(
            () => method.Invoke(null, [1, 64L * 1024 * 1024]));
        Assert.Contains("Invalid RPC record length", Assert.IsType<NfsException>(ex.InnerException).Message);
    }

    [Fact]
    public void NfsException_IsNotFound()
    {
        var ex = new NfsException("not found", NfsV3Status.NoEnt);
        Assert.True(ex.IsNotFound);
        Assert.Equal(NfsV3Status.NoEnt, ex.Status);
    }

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
