using VcxDiag.Core.Transport;

namespace VcxDiag.Core.Protocols;

/// <summary>
/// GM Class 2 / SAE J1850 VPW. Frames are [priority/type, target, source, data...];
/// the interface adds and strips the CRC. The scan tool's address is 0xF1.
/// </summary>
public sealed class Class2Bus : VehicleBus
{
    public const byte Tester = 0xF1;
    public const byte HdrPhysical = 0x6C;   // node-to-node, priority 3
    public const byte HdrFunctional = 0x68; // functional, priority 3
    public const byte ObdTarget = 0x6A;     // SAE J1979 request target
    public const byte ObdResponse = 0x6B;   // SAE J1979 response target
    public const byte AllNodes = 0xFE;

    public override BusKind Kind => BusKind.Class2;

    public Class2Bus(IPassThruChannel channel) : base(channel) { }

    protected override void InstallNormalFilters()
    {
        // Anything addressed to the tester, plus OBD responses.
        AddFilter(new byte[] { 0x00, 0xFF, 0x00 }, new byte[] { 0x00, Tester, 0x00 });
        AddFilter(new byte[] { 0x00, 0xFF, 0x00 }, new byte[] { 0x00, ObdResponse, 0x00 });
    }

    // Common Class 2 node addresses (SAE J2178-1). Not every vehicle has all of them.
    private static readonly uint[] Candidates =
    {
        0x10, 0x11, 0x12, 0x18, 0x19, 0x1A, 0x28, 0x29, 0x2A, 0x2C, 0x30, 0x32, 0x38, 0x3A,
        0x40, 0x41, 0x42, 0x43, 0x45, 0x58, 0x59, 0x60, 0x61, 0x62, 0x64, 0x68, 0x70, 0x72,
        0x80, 0x81, 0x82, 0x88, 0x8A, 0x90, 0x91, 0x93, 0x95, 0x97, 0x98, 0x99, 0x9A, 0x9B,
        0xA0, 0xA1, 0xA2, 0xA3, 0xA4, 0xA6, 0xB0, 0xC0, 0xC2, 0xC5,
    };

    public override IReadOnlyList<uint> ScanAddresses => Candidates;

    public override string FormatAddress(uint address) => $"${address:X2}";

    public override IReadOnlyList<Reply> Request(uint target, byte[] payload, RequestOptions? options = null)
    {
        Channel.ClearRx();
        Send(Frame(HdrPhysical, (byte)target, payload));
        return Collect(options ?? RequestOptions.Default, f =>
            f.Length >= 4 && f[1] == Tester && f[2] == target ? new[] { new Reply(f[2], f[3..]) } : Array.Empty<Reply>());
    }

    public override IReadOnlyList<Reply> ObdRequest(byte[] payload, RequestOptions? options = null)
    {
        Channel.ClearRx();
        Send(Frame(HdrFunctional, ObdTarget, payload));
        return Collect(options ?? RequestOptions.Default, f =>
            f.Length >= 4 && (f[1] == ObdResponse || f[1] == Tester) ? new[] { new Reply(f[2], f[3..]) } : Array.Empty<Reply>());
    }

    public override IReadOnlyList<Reply> FunctionalRequest(byte[] payload, RequestOptions? options = null)
    {
        Channel.ClearRx();
        Send(Frame(HdrFunctional, AllNodes, payload));
        return Collect(options ?? RequestOptions.Default, f =>
            f.Length >= 4 && f[1] == Tester ? new[] { new Reply(f[2], f[3..]) } : Array.Empty<Reply>());
    }

    public static byte[] Frame(byte header, byte target, byte[] payload)
    {
        var f = new byte[3 + payload.Length];
        f[0] = header; f[1] = target; f[2] = Tester;
        payload.CopyTo(f, 3);
        return f;
    }

    public override (string Header, byte[] Data) SplitFrame(byte[] frame) =>
        frame.Length >= 3 ? (Hex.Format(frame.AsSpan(0, 3)), frame[3..]) : (Hex.Format(frame), Array.Empty<byte>());

    public override uint? SourceOf(byte[] frame) => frame.Length >= 3 && frame[2] != Tester ? frame[2] : null;
}
