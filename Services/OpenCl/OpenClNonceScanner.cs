using System.Buffers.Binary;
using OpenCL.Net;

namespace Qadopoolminer.Services.OpenCl;

public sealed class OpenClNonceScanner : IDisposable
{
    private const int ResultCapacity = 8_192;

    private readonly OpenClMiningDevice _device;
    private readonly ILogSink _log;
    private readonly string _tuningProfileName;
    private readonly int[] _preferredLocalWorkSizes;
    private readonly string _buildOptions;
    private readonly int _batchSize;

    private readonly int[] _foundCountHost = new int[1];
    private readonly ulong[] _foundNoncesHost = new ulong[ResultCapacity];
    private readonly byte[] _foundHashesHost = new byte[ResultCapacity * 32];
    private readonly uint[] _block0Words = new uint[16];
    private readonly uint[] _block1Words = new uint[16];
    private readonly uint[] _block2Words = new uint[16];
    private readonly uint[] _precomputedCvWords = new uint[8];
    private readonly uint[] _targetWords = new uint[8];
    private readonly IntPtr[] _globalWorkSize = new IntPtr[1];
    private readonly Event[] _singleEventWaitList = new Event[1];

    private Context _context;
    private CommandQueue _queue;
    private Program _program;
    private Kernel _kernel;
    private IMem? _precomputedCvBuffer;
    private IMem? _block1Buffer;
    private IMem? _block2Buffer;
    private IMem? _targetBuffer;
    private IMem? _foundCountBuffer;
    private IMem? _foundNoncesBuffer;
    private IMem? _foundHashesBuffer;
    private IntPtr[]? _localWorkSize;
    private int _configuredLocalWorkSize;
    private bool _initialized;
    private bool _disposed;
    private DateTimeOffset _lastOverflowLogUtc;

    public OpenClNonceScanner(OpenClMiningDevice device, ILogSink log)
    {
        _device = device;
        _log = log;

        var profile = ResolveTuningProfile(device);
        _tuningProfileName = profile.ProfileName;
        _preferredLocalWorkSizes = profile.PreferredLocalWorkSizes;
        _buildOptions = profile.BuildOptions;
        _batchSize = ResolveBatchSize(device);
        _globalWorkSize[0] = (IntPtr)_batchSize;
    }

    public int BatchSize => _batchSize;

    public void UploadTemplate(byte[] headerBytesZeroNonce, byte[] shareTarget)
    {
        EnsureOpenClReady();

        if (headerBytesZeroNonce is not { Length: 145 })
        {
            throw new ArgumentException("headerBytesZeroNonce must be exactly 145 bytes.", nameof(headerBytesZeroNonce));
        }

        if (shareTarget is not { Length: 32 })
        {
            throw new ArgumentException("shareTarget must be exactly 32 bytes.", nameof(shareTarget));
        }

        WriteWordBlock(headerBytesZeroNonce, 0, _block0Words);
        WriteWordBlock(headerBytesZeroNonce, 64, _block1Words);
        WriteWordBlock(headerBytesZeroNonce, 128, _block2Words);
        PrecomputeChunk0Cv(_block0Words, _precomputedCvWords);
        WriteTargetWords(shareTarget, _targetWords);

        ErrorCode error;
        Event uploadEvent;

        error = Cl.EnqueueWriteBuffer(_queue, _precomputedCvBuffer, Bool.True, IntPtr.Zero, (IntPtr)(8 * sizeof(uint)), _precomputedCvWords, 0, null, out uploadEvent);
        ThrowIfError(error, "upload precomputed cv");
        ReleaseEventNoThrow(uploadEvent);

        error = Cl.EnqueueWriteBuffer(_queue, _block1Buffer, Bool.True, IntPtr.Zero, (IntPtr)(16 * sizeof(uint)), _block1Words, 0, null, out uploadEvent);
        ThrowIfError(error, "upload block1");
        ReleaseEventNoThrow(uploadEvent);

        error = Cl.EnqueueWriteBuffer(_queue, _block2Buffer, Bool.True, IntPtr.Zero, (IntPtr)(16 * sizeof(uint)), _block2Words, 0, null, out uploadEvent);
        ThrowIfError(error, "upload block2");
        ReleaseEventNoThrow(uploadEvent);

        error = Cl.EnqueueWriteBuffer(_queue, _targetBuffer, Bool.True, IntPtr.Zero, (IntPtr)(8 * sizeof(uint)), _targetWords, 0, null, out uploadEvent);
        ThrowIfError(error, "upload target");
        ReleaseEventNoThrow(uploadEvent);
    }

    public int MineBatch(ulong nonceBase, List<OpenClFoundShare> foundShares)
    {
        EnsureOpenClReady();
        ArgumentNullException.ThrowIfNull(foundShares);

        foundShares.Clear();
        _foundCountHost[0] = 0;

        ErrorCode error;
        Event resetEvent = default;
        Event kernelEvent = default;
        Event countReadEvent = default;
        Event noncesReadEvent = default;
        Event hashesReadEvent = default;

        try
        {
            error = Cl.EnqueueWriteBuffer(_queue, _foundCountBuffer, Bool.True, IntPtr.Zero, (IntPtr)sizeof(int), _foundCountHost, 0, null, out resetEvent);
            ThrowIfError(error, "reset found count");

            error = Cl.SetKernelArg(_kernel, 4, nonceBase);
            ThrowIfError(error, "set nonce base");

            error = Cl.EnqueueNDRangeKernel(_queue, _kernel, 1, null, _globalWorkSize, _localWorkSize, 0, null, out kernelEvent);
            ThrowIfError(error, "launch kernel");

            _singleEventWaitList[0] = kernelEvent;
            error = Cl.EnqueueReadBuffer(_queue, _foundCountBuffer, Bool.True, IntPtr.Zero, (IntPtr)sizeof(int), _foundCountHost, 1, _singleEventWaitList, out countReadEvent);
            ThrowIfError(error, "read found count");

            var totalFound = Math.Max(0, _foundCountHost[0]);
            if (totalFound == 0)
            {
                return 0;
            }

            var storedFound = Math.Min(totalFound, ResultCapacity);

            error = Cl.EnqueueReadBuffer(_queue, _foundNoncesBuffer, Bool.True, IntPtr.Zero, (IntPtr)(storedFound * sizeof(ulong)), _foundNoncesHost, 0, null, out noncesReadEvent);
            ThrowIfError(error, "read found nonces");

            error = Cl.EnqueueReadBuffer(_queue, _foundHashesBuffer, Bool.True, IntPtr.Zero, (IntPtr)(storedFound * 32), _foundHashesHost, 0, null, out hashesReadEvent);
            ThrowIfError(error, "read found hashes");

            for (var i = 0; i < storedFound; i++)
            {
                var hashBytes = new byte[32];
                Buffer.BlockCopy(_foundHashesHost, i * 32, hashBytes, 0, 32);
                foundShares.Add(new OpenClFoundShare(_foundNoncesHost[i], hashBytes));
            }

            if (totalFound > ResultCapacity && DateTimeOffset.UtcNow - _lastOverflowLogUtc >= TimeSpan.FromSeconds(10))
            {
                _lastOverflowLogUtc = DateTimeOffset.UtcNow;
                _log.Warn("Mining", $"OpenCL result buffer overflow on {_device.DisplayName}: {totalFound} shares found, only {ResultCapacity} queued.");
            }

            return storedFound;
        }
        finally
        {
            ReleaseEventNoThrow(hashesReadEvent);
            ReleaseEventNoThrow(noncesReadEvent);
            ReleaseEventNoThrow(countReadEvent);
            ReleaseEventNoThrow(kernelEvent);
            ReleaseEventNoThrow(resetEvent);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        ReleaseAllNoThrow();
    }

    private void EnsureOpenClReady()
    {
        if (_initialized)
        {
            return;
        }

        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(OpenClNonceScanner));
        }

        ErrorCode error;

        _context = Cl.CreateContext(null, 1, new[] { _device.DeviceHandle }, null, IntPtr.Zero, out error);
        ThrowIfError(error, "create context");

        _queue = Cl.CreateCommandQueue(_context, _device.DeviceHandle, CommandQueueProperties.None, out error);
        ThrowIfError(error, "create command queue");

        _program = Cl.CreateProgramWithSource(_context, 1, new[] { OpenClKernelSource.Source }, null, out error);
        ThrowIfError(error, "create program");

        error = Cl.BuildProgram(_program, 1, new[] { _device.DeviceHandle }, _buildOptions, null, IntPtr.Zero);
        if (error != ErrorCode.Success)
        {
            var buildLog = SafeBuildLog();
            throw new InvalidOperationException($"OpenCL build failed on {_device.DisplayName}: {error}. {buildLog}".Trim());
        }

        _kernel = Cl.CreateKernel(_program, "search_nonce", out error);
        ThrowIfError(error, "create kernel");

        _precomputedCvBuffer = Cl.CreateBuffer(_context, MemFlags.ReadOnly, (IntPtr)(8 * sizeof(uint)), out error);
        ThrowIfError(error, "create precomputed-cv buffer");
        _block1Buffer = Cl.CreateBuffer(_context, MemFlags.ReadOnly, (IntPtr)(16 * sizeof(uint)), out error);
        ThrowIfError(error, "create block1 buffer");
        _block2Buffer = Cl.CreateBuffer(_context, MemFlags.ReadOnly, (IntPtr)(16 * sizeof(uint)), out error);
        ThrowIfError(error, "create block2 buffer");
        _targetBuffer = Cl.CreateBuffer(_context, MemFlags.ReadOnly, (IntPtr)(8 * sizeof(uint)), out error);
        ThrowIfError(error, "create target buffer");
        _foundCountBuffer = Cl.CreateBuffer(_context, MemFlags.ReadWrite | MemFlags.CopyHostPtr, (IntPtr)sizeof(int), _foundCountHost, out error);
        ThrowIfError(error, "create found-count buffer");
        _foundNoncesBuffer = Cl.CreateBuffer(_context, MemFlags.ReadWrite | MemFlags.CopyHostPtr, (IntPtr)(ResultCapacity * sizeof(ulong)), _foundNoncesHost, out error);
        ThrowIfError(error, "create found-nonces buffer");
        _foundHashesBuffer = Cl.CreateBuffer(_context, MemFlags.ReadWrite | MemFlags.CopyHostPtr, (IntPtr)_foundHashesHost.Length, _foundHashesHost, out error);
        ThrowIfError(error, "create found-hashes buffer");

        error = Cl.SetKernelArg(_kernel, 0, _precomputedCvBuffer);
        ThrowIfError(error, "bind precomputed-cv buffer");
        error = Cl.SetKernelArg(_kernel, 1, _block1Buffer);
        ThrowIfError(error, "bind block1 buffer");
        error = Cl.SetKernelArg(_kernel, 2, _block2Buffer);
        ThrowIfError(error, "bind block2 buffer");
        error = Cl.SetKernelArg(_kernel, 3, _targetBuffer);
        ThrowIfError(error, "bind target buffer");
        error = Cl.SetKernelArg(_kernel, 5, _foundCountBuffer);
        ThrowIfError(error, "bind found-count buffer");
        error = Cl.SetKernelArg(_kernel, 6, _foundNoncesBuffer);
        ThrowIfError(error, "bind found-nonces buffer");
        error = Cl.SetKernelArg(_kernel, 7, _foundHashesBuffer);
        ThrowIfError(error, "bind found-hashes buffer");
        error = Cl.SetKernelArg(_kernel, 8, (uint)ResultCapacity);
        ThrowIfError(error, "bind max-results");

        ConfigureLaunchDimensions();
        _initialized = true;
    }

    private void ConfigureLaunchDimensions()
    {
        _configuredLocalWorkSize = 0;
        _localWorkSize = null;

        try
        {
            ErrorCode error;
            var raw = Cl.GetKernelWorkGroupInfo(_kernel, _device.DeviceHandle, KernelWorkGroupInfo.WorkGroupSize, out error);
            var maxWorkGroupSize = error == ErrorCode.Success ? raw.CastTo<IntPtr>().ToInt64() : 0L;

            foreach (var candidate in _preferredLocalWorkSizes)
            {
                if (candidate > maxWorkGroupSize || candidate <= 0)
                {
                    continue;
                }

                if ((_batchSize % candidate) != 0)
                {
                    continue;
                }

                _configuredLocalWorkSize = candidate;
                _localWorkSize = new[] { (IntPtr)candidate };
                break;
            }
        }
        catch
        {
            _configuredLocalWorkSize = 0;
            _localWorkSize = null;
        }

        _log.Info(
            "Mining",
            _configuredLocalWorkSize > 0
                ? $"OpenCL launch config for {_device.DisplayName}: profile={_tuningProfileName}, global={_batchSize}, local={_configuredLocalWorkSize}."
                : $"OpenCL launch config for {_device.DisplayName}: profile={_tuningProfileName}, global={_batchSize}, local=auto.");
    }

    private static (string ProfileName, int[] PreferredLocalWorkSizes, string BuildOptions) ResolveTuningProfile(OpenClMiningDevice device)
    {
        var vendor = (device.Vendor ?? string.Empty).Trim().ToLowerInvariant();
        var platform = (device.PlatformName ?? string.Empty).Trim().ToLowerInvariant();
        var combined = vendor + "|" + platform;

        if (combined.Contains("nvidia", StringComparison.Ordinal))
        {
            return ("nvidia-default", new[] { 256, 128, 512, 64, 32 }, string.Empty);
        }

        if (combined.Contains("advanced micro devices", StringComparison.Ordinal) ||
            combined.Contains("amd", StringComparison.Ordinal) ||
            combined.Contains("ati", StringComparison.Ordinal))
        {
            return ("amd-default", new[] { 256, 512, 128, 64, 32 }, string.Empty);
        }

        if (combined.Contains("intel", StringComparison.Ordinal))
        {
            return ("intel-default", new[] { 128, 64, 256, 32 }, string.Empty);
        }

        return ("generic", new[] { 256, 128, 64, 32 }, string.Empty);
    }

    private static int ResolveBatchSize(OpenClMiningDevice device)
    {
        if ((device.DeviceType & DeviceType.Cpu) == DeviceType.Cpu)
        {
            return 262_144;
        }

        if ((device.Vendor ?? string.Empty).Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return 8_388_608;
        }

        if ((device.Vendor ?? string.Empty).Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            (device.Vendor ?? string.Empty).Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            (device.Vendor ?? string.Empty).Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
        {
            return 67_108_864;
        }

        return 16_777_216;
    }

    private string SafeBuildLog()
    {
        try
        {
            ErrorCode error;
            return Cl.GetProgramBuildInfo(_program, _device.DeviceHandle, ProgramBuildInfo.Log, out error).ToString().Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private void ReleaseAllNoThrow()
    {
        try
        {
            if (_foundHashesBuffer != null) Cl.ReleaseMemObject(_foundHashesBuffer);
        }
        catch
        {
        }
        _foundHashesBuffer = null;

        try
        {
            if (_foundNoncesBuffer != null) Cl.ReleaseMemObject(_foundNoncesBuffer);
        }
        catch
        {
        }
        _foundNoncesBuffer = null;

        try
        {
            if (_foundCountBuffer != null) Cl.ReleaseMemObject(_foundCountBuffer);
        }
        catch
        {
        }
        _foundCountBuffer = null;

        try
        {
            if (_targetBuffer != null) Cl.ReleaseMemObject(_targetBuffer);
        }
        catch
        {
        }
        _targetBuffer = null;

        try
        {
            if (_block2Buffer != null) Cl.ReleaseMemObject(_block2Buffer);
        }
        catch
        {
        }
        _block2Buffer = null;

        try
        {
            if (_block1Buffer != null) Cl.ReleaseMemObject(_block1Buffer);
        }
        catch
        {
        }
        _block1Buffer = null;

        try
        {
            if (_precomputedCvBuffer != null) Cl.ReleaseMemObject(_precomputedCvBuffer);
        }
        catch
        {
        }
        _precomputedCvBuffer = null;

        try
        {
            Cl.ReleaseKernel(_kernel);
        }
        catch
        {
        }
        _kernel = default;

        try
        {
            Cl.ReleaseProgram(_program);
        }
        catch
        {
        }
        _program = default;

        try
        {
            Cl.ReleaseCommandQueue(_queue);
        }
        catch
        {
        }
        _queue = default;

        try
        {
            Cl.ReleaseContext(_context);
        }
        catch
        {
        }
        _context = default;

        _configuredLocalWorkSize = 0;
        _localWorkSize = null;
        _initialized = false;
    }

    private static void ReleaseEventNoThrow(Event openClEvent)
    {
        try
        {
            Cl.ReleaseEvent(openClEvent);
        }
        catch
        {
        }
    }

    private static void ThrowIfError(ErrorCode error, string operation)
    {
        if (error != ErrorCode.Success)
        {
            throw new InvalidOperationException($"OpenCL {operation} failed: {error}");
        }
    }

    private static void WriteWordBlock(byte[] src, int offset, uint[] dst)
    {
        var block = new byte[64];
        var available = Math.Max(0, Math.Min(64, src.Length - offset));
        if (available > 0)
        {
            Buffer.BlockCopy(src, offset, block, 0, available);
        }

        for (var i = 0; i < 16; i++)
        {
            dst[i] = BinaryPrimitives.ReadUInt32LittleEndian(block.AsSpan(i * 4, 4));
        }
    }

    private static void WriteTargetWords(byte[] target, uint[] dst)
    {
        for (var i = 0; i < 8; i++)
        {
            dst[i] = BinaryPrimitives.ReadUInt32BigEndian(target.AsSpan(i * 4, 4));
        }
    }

    private static void PrecomputeChunk0Cv(uint[] block0Words, uint[] dstCv)
    {
        Span<uint> cv =
        [
            0x6A09E667u, 0xBB67AE85u, 0x3C6EF372u, 0xA54FF53Au,
            0x510E527Fu, 0x9B05688Cu, 0x1F83D9ABu, 0x5BE0CD19u
        ];

        var blockWords = new uint[16];
        Array.Copy(block0Words, blockWords, 16);

        Span<uint> outWords = stackalloc uint[16];
        CompressWordsCpu(cv, blockWords, 0u, 0u, 64u, 1u, outWords);

        for (var i = 0; i < 8; i++)
        {
            dstCv[i] = outWords[i];
        }
    }

    private static void CompressWordsCpu(
        ReadOnlySpan<uint> cv,
        uint[] blockWords,
        uint counterLow,
        uint counterHigh,
        uint blockLen,
        uint flags,
        Span<uint> outWords)
    {
        var v = new uint[16];
        for (var i = 0; i < 8; i++) v[i] = cv[i];
        v[8] = 0x6A09E667u;
        v[9] = 0xBB67AE85u;
        v[10] = 0x3C6EF372u;
        v[11] = 0xA54FF53Au;
        v[12] = counterLow;
        v[13] = counterHigh;
        v[14] = blockLen;
        v[15] = flags;

        RoundFnCpu(v, blockWords);
        PermuteCpu(blockWords);
        RoundFnCpu(v, blockWords);
        PermuteCpu(blockWords);
        RoundFnCpu(v, blockWords);
        PermuteCpu(blockWords);
        RoundFnCpu(v, blockWords);
        PermuteCpu(blockWords);
        RoundFnCpu(v, blockWords);
        PermuteCpu(blockWords);
        RoundFnCpu(v, blockWords);
        PermuteCpu(blockWords);
        RoundFnCpu(v, blockWords);

        for (var i = 0; i < 8; i++)
        {
            outWords[i] = v[i] ^ v[i + 8];
            outWords[i + 8] = v[i + 8] ^ cv[i];
        }
    }

    private static void RoundFnCpu(uint[] v, uint[] m)
    {
        GCpu(ref v[0], ref v[4], ref v[8], ref v[12], m[0], m[1]);
        GCpu(ref v[1], ref v[5], ref v[9], ref v[13], m[2], m[3]);
        GCpu(ref v[2], ref v[6], ref v[10], ref v[14], m[4], m[5]);
        GCpu(ref v[3], ref v[7], ref v[11], ref v[15], m[6], m[7]);

        GCpu(ref v[0], ref v[5], ref v[10], ref v[15], m[8], m[9]);
        GCpu(ref v[1], ref v[6], ref v[11], ref v[12], m[10], m[11]);
        GCpu(ref v[2], ref v[7], ref v[8], ref v[13], m[12], m[13]);
        GCpu(ref v[3], ref v[4], ref v[9], ref v[14], m[14], m[15]);
    }

    private static void PermuteCpu(uint[] m)
    {
        var t = new uint[16];
        t[0] = m[2]; t[1] = m[6]; t[2] = m[3]; t[3] = m[10];
        t[4] = m[7]; t[5] = m[0]; t[6] = m[4]; t[7] = m[13];
        t[8] = m[1]; t[9] = m[11]; t[10] = m[12]; t[11] = m[5];
        t[12] = m[9]; t[13] = m[14]; t[14] = m[15]; t[15] = m[8];
        Array.Copy(t, m, 16);
    }

    private static void GCpu(ref uint a, ref uint b, ref uint c, ref uint d, uint mx, uint my)
    {
        a = a + b + mx;
        d = RotateRight(d ^ a, 16);
        c = c + d;
        b = RotateRight(b ^ c, 12);
        a = a + b + my;
        d = RotateRight(d ^ a, 8);
        c = c + d;
        b = RotateRight(b ^ c, 7);
    }

    private static uint RotateRight(uint value, int shift)
        => (value >> shift) | (value << (32 - shift));
}
