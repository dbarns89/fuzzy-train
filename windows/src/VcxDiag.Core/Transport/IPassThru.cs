using VcxDiag.Core.J2534;

namespace VcxDiag.Core.Transport;

/// <summary>A frame as delivered by the interface. For CAN, the first 4 bytes are the CAN ID.</summary>
public sealed record RawFrame(byte[] Data, uint TimestampUs, uint RxStatus)
{
    public bool IsTxEcho => (RxStatus & J2534Flags.TxMsgType) != 0;
}

public sealed record DeviceVersion(string Firmware, string Dll, string Api);

/// <summary>
/// A vehicle interface (J2534 device or the built-in simulator).
/// All calls are synchronous and must come from one thread (see <see cref="DiagWorker"/>).
/// </summary>
public interface IPassThruDevice : IDisposable
{
    string Name { get; }
    DeviceVersion ReadVersion();
    /// <summary>Voltage on OBD pin 16, or null if the device can't report it.</summary>
    double? ReadBatteryVolts();
    IPassThruChannel Connect(ProtocolId protocol, uint baud, uint flags = 0, uint? j1962Pins = null);
}

public interface IPassThruChannel : IDisposable
{
    ProtocolId Protocol { get; }
    void Write(byte[] data, uint txFlags = 0);
    /// <summary>Reads up to <paramref name="max"/> frames, waiting at most <paramref name="timeoutMs"/>.</summary>
    IReadOnlyList<RawFrame> Read(int max, uint timeoutMs);
    uint StartPassFilter(byte[] mask, byte[] pattern);
    void StopFilter(uint filterId);
    void ClearRx();
}

public sealed class PassThruException : Exception
{
    public uint Code { get; }
    public PassThruException(string call, uint code, string detail)
        : base($"{call} failed: {J2534Status.Describe(code)}{(string.IsNullOrWhiteSpace(detail) ? "" : " (" + detail.Trim() + ")")}")
        => Code = code;
}
