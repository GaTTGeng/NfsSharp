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
    public void NfsException_IsNotFound()
    {
        var ex = new NfsException("not found", NfsV3Status.NoEnt);
        Assert.True(ex.IsNotFound);
        Assert.Equal(NfsV3Status.NoEnt, ex.Status);
    }

    [Fact]
    public void NfsV3Status_Describe()
    {
        Assert.Equal("OK", NfsV3Status.Describe(NfsV3Status.Ok));
        Assert.Equal("NOENT", NfsV3Status.Describe(NfsV3Status.NoEnt));
        Assert.Equal("STALE", NfsV3Status.Describe(NfsV3Status.Stale));
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
