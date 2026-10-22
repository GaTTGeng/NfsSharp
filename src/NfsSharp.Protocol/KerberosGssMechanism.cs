using System.Runtime.InteropServices;
using System.Text;

namespace NfsSharp.Protocol;

/// <summary>Linux MIT GSSAPI mechanism using the process Kerberos credential cache.</summary>
public sealed class KerberosGssMechanism : IRpcSecGssMechanism, IRpcSecGssQopMechanism,
    IRpcSecGssMicResultMechanism, IRpcSecGssPrivacyMechanism, IRpcSecGssMechanismCapabilities
{
    private static readonly byte[] KerberosOidBytes = [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02];
    private static readonly byte[] PrincipalNameOidBytes = [0x2a, 0x86, 0x48, 0x86, 0xf7, 0x12, 0x01, 0x02, 0x02, 0x01];
    // System.Threading.Lock is unavailable on net8.0.
    // ReSharper disable once ChangeFieldTypeToSystemThreadingLock
    private readonly object _sync = new();
    private readonly IntPtr _mechanismOid;
    private readonly IntPtr _principalNameOid;
    private IntPtr _context;
    private IntPtr _targetName;
    private bool _disposed;
    private bool _established;

    /// <summary>Creates a mechanism backed by the host's MIT GSSAPI library.</summary>
    public KerberosGssMechanism()
    {
        EnsureLinux();
        _mechanismOid = NativeOid.Create(KerberosOidBytes);
        try
        {
            _principalNameOid = NativeOid.Create(PrincipalNameOidBytes);
        }
        catch
        {
            NativeOid.Free(_mechanismOid);
            throw;
        }
    }

    /// <inheritdoc />
    public byte[] MechanismOid => KerberosOidBytes.ToArray();

    /// <inheritdoc />
    public bool ProvidesCryptographicProtection => true;

    /// <inheritdoc />
    public bool CanComputeMic => true;

    /// <inheritdoc />
    public bool IsEstablished
    {
        get
        {
            lock (_sync)
                return _established;
        }
    }

    /// <inheritdoc />
    public uint MaxMessageSize => 1024 * 1024;

    /// <inheritdoc />
    public uint NextSeqNum { get; set; }

    /// <inheritdoc />
    public RpcSecGssService NegotiatedService { get; set; }

    /// <inheritdoc />
    public Task<byte[]> InitiateContextAsync(string targetName, GssCredentials? credentials, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetName);
        ct.ThrowIfCancellationRequested();
        if (credentials is not null)
            throw new NfsException("KerberosGssMechanism uses the process Kerberos credential cache; configure credentials before creating the NFS client.");

        lock (_sync)
        {
            ThrowIfDisposed();
            if (_targetName != IntPtr.Zero || _context != IntPtr.Zero)
                throw new InvalidOperationException("A Kerberos GSS context has already been initiated.");

            var nameBytes = Encoding.UTF8.GetBytes(targetName);
            var nameMemory = Marshal.AllocHGlobal(nameBytes.Length);
            try
            {
                Marshal.Copy(nameBytes, 0, nameMemory, nameBytes.Length);
                var nameBuffer = new GssBuffer { Length = (nuint)nameBytes.Length, Value = nameMemory };
                var major = NativeMethods.ImportName(out var minor, ref nameBuffer, _principalNameOid, out _targetName);
                if (major != NativeMethods.GssComplete)
                {
                    if (_targetName != IntPtr.Zero)
                        NativeMethods.ReleaseName(out _, ref _targetName);
                    EnsureSuccess(major, minor, "import target principal");
                }
            }
            finally
            {
                Marshal.FreeHGlobal(nameMemory);
            }

            return Task.FromResult(ContinueContextCore(ReadOnlySpan<byte>.Empty));
        }
    }

    /// <inheritdoc />
    public Task<byte[]> ContinueContextAsync(byte[] serverToken, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(serverToken);
        ct.ThrowIfCancellationRequested();
        lock (_sync)
        {
            ThrowIfDisposed();
            if (_targetName == IntPtr.Zero)
                throw new InvalidOperationException("Context not initiated.");
            return Task.FromResult(ContinueContextCore(serverToken));
        }
    }

    /// <inheritdoc />
    public byte[] GetMic(byte[] data) => GetMic(data, 0);

    /// <inheritdoc />
    public byte[] GetMic(byte[] data, uint qop)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            EnsureEstablished();
            var message = ToBuffer(data);
            GssBuffer token = default;
            try
            {
                var major = NativeMethods.GetMic(out var minor, _context, qop, ref message, out token);
                EnsureSuccess(major, minor, "compute MIC");
                return CopyBuffer(token);
            }
            finally
            {
                FreeInputBuffer(ref message);
                ReleaseBuffer(ref token);
            }
        }
    }

    /// <inheritdoc />
    public bool VerifyMic(byte[] data, byte[] mic) => VerifyMicWithResult(data, mic, 0).Status == RpcSecGssMicVerificationStatus.Verified;

    /// <inheritdoc />
    public bool VerifyMic(byte[] data, byte[] mic, uint qop) => VerifyMicWithResult(data, mic, qop).Status == RpcSecGssMicVerificationStatus.Verified;

    /// <inheritdoc />
    public RpcSecGssMicVerificationResult VerifyMicWithResult(byte[] data, byte[] mic, uint qop)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(mic);
        lock (_sync)
        {
            EnsureEstablished();
            var message = ToBuffer(data);
            var token = ToBuffer(mic);
            try
            {
                var major = NativeMethods.VerifyMic(out var minor, _context, ref message, ref token, out var actualQop);
                if (major == NativeMethods.GssBadSignature)
                    return new RpcSecGssMicVerificationResult(RpcSecGssMicVerificationStatus.Invalid, qop);
                EnsureTokenVerificationSuccess(major, minor, "verify MIC");
                return new RpcSecGssMicVerificationResult(
                    actualQop == qop ? RpcSecGssMicVerificationStatus.Verified : RpcSecGssMicVerificationStatus.UnsupportedQop,
                    actualQop);
            }
            finally
            {
                FreeInputBuffer(ref message);
                FreeInputBuffer(ref token);
            }
        }
    }

    /// <inheritdoc />
    public byte[] Wrap(byte[] data) => WrapPrivacy(data, 0).Data;

    /// <inheritdoc />
    public byte[] Wrap(byte[] data, uint qop) => WrapPrivacy(data, qop).Data;

    /// <inheritdoc />
    public byte[] Unwrap(byte[] wrappedData) => UnwrapPrivacy(wrappedData, 0).Data;

    /// <inheritdoc />
    public byte[] Unwrap(byte[] wrappedData, uint qop) => UnwrapPrivacy(wrappedData, qop).Data;

    /// <inheritdoc />
    public RpcSecGssPrivacyResult WrapPrivacy(byte[] data, uint qop)
    {
        ArgumentNullException.ThrowIfNull(data);
        lock (_sync)
        {
            EnsureEstablished();
            var message = ToBuffer(data);
            GssBuffer token = default;
            try
            {
                var major = NativeMethods.Wrap(out var minor, _context, 1, qop, ref message, out var confidential, out token);
                EnsureSuccess(major, minor, "wrap privacy payload");
                return new RpcSecGssPrivacyResult(CopyBuffer(token), qop, confidential != 0);
            }
            finally
            {
                FreeInputBuffer(ref message);
                ReleaseBuffer(ref token);
            }
        }
    }

    /// <inheritdoc />
    public RpcSecGssPrivacyResult UnwrapPrivacy(byte[] wrappedData, uint qop)
    {
        ArgumentNullException.ThrowIfNull(wrappedData);
        lock (_sync)
        {
            EnsureEstablished();
            var token = ToBuffer(wrappedData);
            GssBuffer message = default;
            try
            {
                var major = NativeMethods.Unwrap(out var minor, _context, ref token, out message, out var confidential, out var actualQop);
                EnsureTokenVerificationSuccess(major, minor, "unwrap privacy payload");
                return new RpcSecGssPrivacyResult(CopyBuffer(message), actualQop, confidential != 0);
            }
            finally
            {
                FreeInputBuffer(ref token);
                ReleaseBuffer(ref message);
            }
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed)
                return;
            _disposed = true;
            if (_context != IntPtr.Zero)
            {
                var major = NativeMethods.DeleteSecContext(out _, ref _context, out var outputToken);
                ReleaseBuffer(ref outputToken);
                _ = major;
            }
            if (_targetName != IntPtr.Zero)
            {
                NativeMethods.ReleaseName(out _, ref _targetName);
            }
            NativeOid.Free(_mechanismOid);
            NativeOid.Free(_principalNameOid);
        }
    }

    private byte[] ContinueContextCore(ReadOnlySpan<byte> serverToken)
    {
        var inputBytes = serverToken.ToArray();
        var inputMemory = inputBytes.Length == 0 ? IntPtr.Zero : Marshal.AllocHGlobal(inputBytes.Length);
        var input = new GssBuffer { Length = (nuint)inputBytes.Length, Value = inputMemory };
        try
        {
            if (inputMemory != IntPtr.Zero)
                Marshal.Copy(inputBytes, 0, inputMemory, inputBytes.Length);
            // RPCSEC_GSS provides replay protection and accepts reordered calls (RFC 2203 §5.2.2).
            var requestedFlags = NativeMethods.GssMutualFlag | NativeMethods.GssIntegrityFlag;
            if (NegotiatedService == RpcSecGssService.Privacy)
                requestedFlags |= NativeMethods.GssConfidentialityFlag;

            var major = NativeMethods.InitSecContext(
                out var minor,
                IntPtr.Zero,
                ref _context,
                _targetName,
                _mechanismOid,
                requestedFlags,
                0,
                IntPtr.Zero,
                ref input,
                out var actualMechanism,
                out var output,
                out var returnedFlags,
                out _);
            try
            {
                if ((major & NativeMethods.GssCallingOrRoutineErrorMask) != 0)
                    throw CreateGssException(major, minor, "establish context");
                var established = (major & NativeMethods.GssContinueNeeded) == 0;
                if (established)
                {
                    if (!NativeOid.Matches(actualMechanism, KerberosOidBytes) ||
                        (returnedFlags & (NativeMethods.GssMutualFlag | NativeMethods.GssIntegrityFlag)) !=
                        (NativeMethods.GssMutualFlag | NativeMethods.GssIntegrityFlag) ||
                        (NegotiatedService == RpcSecGssService.Privacy &&
                         (returnedFlags & NativeMethods.GssConfidentialityFlag) == 0))
                    {
                        throw new NfsException("Kerberos GSSAPI established a context without the required mechanism or protection flags.");
                    }
                }
                _established = established;
                return CopyBuffer(output);
            }
            finally
            {
                ReleaseBuffer(ref output);
            }
        }
        finally
        {
            if (inputMemory != IntPtr.Zero)
                Marshal.FreeHGlobal(inputMemory);
        }
    }

    private void EnsureEstablished()
    {
        ThrowIfDisposed();
        if (!_established || _context == IntPtr.Zero)
            throw new InvalidOperationException("The Kerberos GSS context is not established.");
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(_disposed, this);

    private static void EnsureLinux()
    {
        if (!OperatingSystem.IsLinux())
            throw new PlatformNotSupportedException("KerberosGssMechanism currently requires MIT GSSAPI on Linux.");
    }

    private static void EnsureSuccess(uint major, uint minor, string operation)
    {
        if (major != NativeMethods.GssComplete)
            throw CreateGssException(major, minor, operation);
    }

    internal static bool IsSuccessfulTokenVerification(uint major) =>
        (major & NativeMethods.GssCallingOrRoutineErrorMask) == 0 &&
        (major & ~(NativeMethods.GssUnsequencedToken | NativeMethods.GssGapToken)) == NativeMethods.GssComplete;

    private static void EnsureTokenVerificationSuccess(uint major, uint minor, string operation)
    {
        if (!IsSuccessfulTokenVerification(major))
            throw CreateGssException(major, minor, operation);
    }

    private static NfsException CreateGssException(uint major, uint minor, string operation) =>
        new($"Kerberos GSSAPI failed to {operation} (gss_major={major}, gss_minor={minor}).");

    private static GssBuffer ToBuffer(byte[] value)
    {
        if (value.Length == 0)
            return default;
        var memory = Marshal.AllocHGlobal(value.Length);
        try
        {
            Marshal.Copy(value, 0, memory, value.Length);
            return new GssBuffer { Length = (nuint)value.Length, Value = memory };
        }
        catch
        {
            Marshal.FreeHGlobal(memory);
            throw;
        }
    }

    private static byte[] CopyBuffer(GssBuffer buffer)
    {
        if (buffer.Length == 0)
            return [];
        if (buffer.Length > int.MaxValue)
            throw new NfsException("Kerberos GSSAPI returned an oversized buffer.");
        var result = new byte[(int)buffer.Length];
        Marshal.Copy(buffer.Value, result, 0, result.Length);
        return result;
    }

    private static void ReleaseBuffer(ref GssBuffer buffer)
    {
        if (buffer.Value != IntPtr.Zero)
            NativeMethods.ReleaseBuffer(out _, ref buffer);
        buffer = default;
    }

    private static void FreeInputBuffer(ref GssBuffer buffer)
    {
        if (buffer.Value != IntPtr.Zero)
            Marshal.FreeHGlobal(buffer.Value);
        buffer = default;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GssBuffer
    {
        public nuint Length;
        public IntPtr Value;
    }

    private static class NativeOid
    {
        public static IntPtr Create(byte[] elements)
        {
            var elementMemory = Marshal.AllocHGlobal(elements.Length);
            var oidMemory = IntPtr.Zero;
            try
            {
                Marshal.Copy(elements, 0, elementMemory, elements.Length);
                var oid = new GssOid { Length = (uint)elements.Length, Elements = elementMemory };
                oidMemory = Marshal.AllocHGlobal(Marshal.SizeOf<GssOid>());
                Marshal.StructureToPtr(oid, oidMemory, false);
                return oidMemory;
            }
            catch
            {
                Marshal.FreeHGlobal(oidMemory);
                Marshal.FreeHGlobal(elementMemory);
                throw;
            }
        }

        public static void Free(IntPtr oidMemory)
        {
            if (oidMemory == IntPtr.Zero)
                return;
            var oid = Marshal.PtrToStructure<GssOid>(oidMemory);
            Marshal.FreeHGlobal(oid.Elements);
            Marshal.FreeHGlobal(oidMemory);
        }

        public static bool Matches(IntPtr oidMemory, byte[] elements)
        {
            if (oidMemory == IntPtr.Zero)
                return false;
            var oid = Marshal.PtrToStructure<GssOid>(oidMemory);
            if (oid.Length != elements.Length)
                return false;
            var actual = new byte[oid.Length];
            Marshal.Copy(oid.Elements, actual, 0, actual.Length);
            return actual.AsSpan().SequenceEqual(elements);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GssOid
        {
            public uint Length;
            public IntPtr Elements;
        }
    }

    private static class NativeMethods
    {
        private const string Library = "libgssapi_krb5.so.2";
        public const uint GssComplete = 0;
        public const uint GssContinueNeeded = 1;
        public const uint GssBadSignature = 0x0006_0000;
        public const uint GssCallingOrRoutineErrorMask = 0xffff_0000;
        public const uint GssUnsequencedToken = 1 << 3;
        public const uint GssGapToken = 1 << 4;
        public const uint GssMutualFlag = 2;
        public const uint GssConfidentialityFlag = 16;
        public const uint GssIntegrityFlag = 32;

        [DllImport(Library, EntryPoint = "gss_import_name", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ImportName(out uint minor, ref GssBuffer input, IntPtr nameType, out IntPtr outputName);

        [DllImport(Library, EntryPoint = "gss_init_sec_context", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint InitSecContext(
            out uint minor,
            IntPtr credential,
            ref IntPtr context,
            IntPtr targetName,
            IntPtr mechanism,
            uint requestedFlags,
            uint requestedLifetime,
            IntPtr channelBindings,
            ref GssBuffer inputToken,
            out IntPtr actualMechanism,
            out GssBuffer outputToken,
            out uint returnedFlags,
            out uint returnedLifetime);

        [DllImport(Library, EntryPoint = "gss_get_mic", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint GetMic(out uint minor, IntPtr context, uint qop, ref GssBuffer message, out GssBuffer token);

        [DllImport(Library, EntryPoint = "gss_verify_mic", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint VerifyMic(out uint minor, IntPtr context, ref GssBuffer message, ref GssBuffer token, out uint qop);

        [DllImport(Library, EntryPoint = "gss_wrap", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Wrap(out uint minor, IntPtr context, int confidential, uint qop, ref GssBuffer input, out int confidentialityState, out GssBuffer output);

        [DllImport(Library, EntryPoint = "gss_unwrap", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint Unwrap(out uint minor, IntPtr context, ref GssBuffer input, out GssBuffer output, out int confidentialityState, out uint qop);

        [DllImport(Library, EntryPoint = "gss_release_buffer", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ReleaseBuffer(out uint minor, ref GssBuffer buffer);

        [DllImport(Library, EntryPoint = "gss_release_name", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint ReleaseName(out uint minor, ref IntPtr name);

        [DllImport(Library, EntryPoint = "gss_delete_sec_context", CallingConvention = CallingConvention.Cdecl)]
        public static extern uint DeleteSecContext(out uint minor, ref IntPtr context, out GssBuffer outputToken);
    }
}
