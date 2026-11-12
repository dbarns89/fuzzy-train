using System.Runtime.InteropServices;
using System.Text;
using VcxDiag.Core.Transport;

namespace VcxDiag.Core.J2534;

/// <summary>PASSTHRU_MSG from SAE J2534-1. "unsigned long" is 32-bit on Windows in both x86 and x64.</summary>
[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal unsafe struct PassThruMsg
{
    public uint ProtocolID;
    public uint RxStatus;
    public uint TxFlags;
    public uint Timestamp;
    public uint DataSize;
    public uint ExtraDataIndex;
    public fixed byte Data[4128];

    public static PassThruMsg Create(ProtocolId protocol, ReadOnlySpan<byte> data, uint txFlags)
    {
        if (data.Length > 4128) throw new ArgumentException("frame too long");
        var m = new PassThruMsg { ProtocolID = (uint)protocol, TxFlags = txFlags, DataSize = (uint)data.Length };
        data.CopyTo(new Span<byte>(m.Data, 4128));
        return m;
    }

    public byte[] ToArray()
    {
        var n = (int)Math.Min(DataSize, 4128u);
        fixed (byte* p = Data) return new ReadOnlySpan<byte>(p, n).ToArray();
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct SConfig { public uint Parameter; public uint Value; }

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SConfigList { public uint NumOfParams; public SConfig* ConfigPtr; }

/// <summary>
/// Loads a vendor J2534 DLL (for example the VXDIAG VCX Nano driver installed by VX Manager)
/// and calls it directly. J2534 functions use __stdcall.
/// </summary>
public sealed unsafe class J2534Device : IPassThruDevice
{
    private readonly IntPtr _lib;
    private readonly uint _deviceId;
    private bool _open;
    private readonly List<J2534Channel> _channels = new();

    private readonly delegate* unmanaged[Stdcall]<IntPtr, uint*, int> _open_;
    private readonly delegate* unmanaged[Stdcall]<uint, int> _close;
    internal readonly delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint*, int> PtConnect;
    internal readonly delegate* unmanaged[Stdcall]<uint, int> PtDisconnect;
    internal readonly delegate* unmanaged[Stdcall]<uint, PassThruMsg*, uint*, uint, int> PtRead;
    internal readonly delegate* unmanaged[Stdcall]<uint, PassThruMsg*, uint*, uint, int> PtWrite;
    internal readonly delegate* unmanaged[Stdcall]<uint, uint, PassThruMsg*, PassThruMsg*, PassThruMsg*, uint*, int> PtStartFilter;
    internal readonly delegate* unmanaged[Stdcall]<uint, uint, int> PtStopFilter;
    internal readonly delegate* unmanaged[Stdcall]<uint, uint, void*, void*, int> PtIoctl;
    private readonly delegate* unmanaged[Stdcall]<uint, byte*, byte*, byte*, int> _readVersion;
    private readonly delegate* unmanaged[Stdcall]<byte*, int> _getLastError;

    public string Name { get; }
    public string DllPath { get; }

    public J2534Device(string dllPath, string name)
    {
        DllPath = dllPath;
        Name = name;
        if (!File.Exists(dllPath))
            throw new FileNotFoundException("J2534 driver DLL not found. Reinstall VX Manager / the VCX GM driver.", dllPath);
        try
        {
            _lib = NativeLibrary.Load(dllPath);
        }
        catch (BadImageFormatException)
        {
            throw new InvalidOperationException(
                $"The driver DLL is built for a different CPU type than this app ({(Environment.Is64BitProcess ? "64" : "32")}-bit). " +
                "Use the matching VCX Diag build (x86 for 32-bit drivers, x64 for 64-bit drivers).");
        }

        IntPtr F(string n) => NativeLibrary.TryGetExport(_lib, n, out var p)
            ? p : throw new InvalidOperationException($"{Path.GetFileName(dllPath)} is not a J2534 driver (missing {n}).");

        _open_ = (delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)F("PassThruOpen");
        _close = (delegate* unmanaged[Stdcall]<uint, int>)F("PassThruClose");
        PtConnect = (delegate* unmanaged[Stdcall]<uint, uint, uint, uint, uint*, int>)F("PassThruConnect");
        PtDisconnect = (delegate* unmanaged[Stdcall]<uint, int>)F("PassThruDisconnect");
        PtRead = (delegate* unmanaged[Stdcall]<uint, PassThruMsg*, uint*, uint, int>)F("PassThruReadMsgs");
        PtWrite = (delegate* unmanaged[Stdcall]<uint, PassThruMsg*, uint*, uint, int>)F("PassThruWriteMsgs");
        PtStartFilter = (delegate* unmanaged[Stdcall]<uint, uint, PassThruMsg*, PassThruMsg*, PassThruMsg*, uint*, int>)F("PassThruStartMsgFilter");
        PtStopFilter = (delegate* unmanaged[Stdcall]<uint, uint, int>)F("PassThruStopMsgFilter");
        PtIoctl = (delegate* unmanaged[Stdcall]<uint, uint, void*, void*, int>)F("PassThruIoctl");
        _readVersion = (delegate* unmanaged[Stdcall]<uint, byte*, byte*, byte*, int>)F("PassThruReadVersion");
        _getLastError = (delegate* unmanaged[Stdcall]<byte*, int>)F("PassThruGetLastError");

        uint id;
        Check(_open_(IntPtr.Zero, &id), "PassThruOpen");
        _deviceId = id;
        _open = true;
    }

    internal void Check(int rc, string call)
    {
        if (rc == 0) return;
        throw new PassThruException(call, (uint)rc, LastError());
    }

    internal string LastError()
    {
        var buf = stackalloc byte[256];
        try { _getLastError(buf); } catch { return ""; }
        return ReadCString(buf, 256);
    }

    private static string ReadCString(byte* p, int max)
    {
        int n = 0;
        while (n < max && p[n] != 0) n++;
        return Encoding.ASCII.GetString(p, n);
    }

    public DeviceVersion ReadVersion()
    {
        var fw = stackalloc byte[256];
        var dll = stackalloc byte[256];
        var api = stackalloc byte[256];
        Check(_readVersion(_deviceId, fw, dll, api), "PassThruReadVersion");
        return new DeviceVersion(ReadCString(fw, 256), ReadCString(dll, 256), ReadCString(api, 256));
    }

    public double? ReadBatteryVolts()
    {
        uint mv = 0;
        int rc = PtIoctl(_deviceId, J2534Ioctl.ReadVbatt, null, &mv);
        return rc == 0 ? mv / 1000.0 : null;
    }

    public IPassThruChannel Connect(ProtocolId protocol, uint baud, uint flags = 0, uint? j1962Pins = null)
    {
        uint ch;
        Check(PtConnect(_deviceId, (uint)protocol, flags, baud, &ch), $"PassThruConnect({protocol} @ {baud})");
        var channel = new J2534Channel(this, ch, protocol);
        try
        {
            if (j1962Pins is uint pins) channel.SetConfig(J2534Config.J1962Pins, pins);
            channel.SetConfig(J2534Config.Loopback, 0, ignoreErrors: true);
        }
        catch
        {
            channel.Dispose();
            throw;
        }
        _channels.Add(channel);
        return channel;
    }

    internal void Forget(J2534Channel c) => _channels.Remove(c);

    public void Dispose()
    {
        if (!_open) return;
        foreach (var c in _channels.ToArray()) c.Dispose();
        try { _close(_deviceId); } catch { /* driver may already be gone */ }
        _open = false;
        NativeLibrary.Free(_lib);
    }
}

internal sealed unsafe class J2534Channel : IPassThruChannel
{
    private readonly J2534Device _dev;
    private readonly uint _id;
    private bool _open = true;
    private readonly PassThruMsg* _rx;
    private const int RxSlots = 32;

    public ProtocolId Protocol { get; }

    public J2534Channel(J2534Device dev, uint id, ProtocolId protocol)
    {
        _dev = dev;
        _id = id;
        Protocol = protocol;
        _rx = (PassThruMsg*)NativeMemory.AllocZeroed((nuint)(sizeof(PassThruMsg) * RxSlots));
    }

    public void SetConfig(uint parameter, uint value, bool ignoreErrors = false)
    {
        var cfg = new SConfig { Parameter = parameter, Value = value };
        var list = new SConfigList { NumOfParams = 1, ConfigPtr = &cfg };
        int rc = _dev.PtIoctl(_id, J2534Ioctl.SetConfig, &list, null);
        if (!ignoreErrors) _dev.Check(rc, $"SET_CONFIG(0x{parameter:X})");
    }

    public void Write(byte[] data, uint txFlags = 0)
    {
        var msg = PassThruMsg.Create(Protocol, data, txFlags);
        uint n = 1;
        _dev.Check(_dev.PtWrite(_id, &msg, &n, 100), "PassThruWriteMsgs");
    }

    public IReadOnlyList<RawFrame> Read(int max, uint timeoutMs)
    {
        uint n = (uint)Math.Clamp(max, 1, RxSlots);
        int rc = _dev.PtRead(_id, _rx, &n, timeoutMs);
        if (rc != 0 && rc != (int)J2534Status.BufferEmpty && rc != (int)J2534Status.Timeout)
            _dev.Check(rc, "PassThruReadMsgs");
        if (n == 0) return Array.Empty<RawFrame>();
        var list = new List<RawFrame>((int)n);
        for (int i = 0; i < n; i++)
            list.Add(new RawFrame(_rx[i].ToArray(), _rx[i].Timestamp, _rx[i].RxStatus));
        return list;
    }

    public uint StartPassFilter(byte[] mask, byte[] pattern)
    {
        var m = PassThruMsg.Create(Protocol, mask, 0);
        var p = PassThruMsg.Create(Protocol, pattern, 0);
        uint fid;
        _dev.Check(_dev.PtStartFilter(_id, J2534Flags.PassFilter, &m, &p, null, &fid), "PassThruStartMsgFilter");
        return fid;
    }

    public void StopFilter(uint filterId) => _dev.PtStopFilter(_id, filterId);

    public void ClearRx() => _dev.PtIoctl(_id, J2534Ioctl.ClearRxBuffer, null, null);

    public void Dispose()
    {
        if (!_open) return;
        _open = false;
        try { _dev.PtDisconnect(_id); } catch { }
        NativeMemory.Free(_rx);
        _dev.Forget(this);
    }
}
