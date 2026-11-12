using System.Diagnostics;
using VcxDiag.Core.Transport;

namespace VcxDiag.Core.Protocols;

/// <summary>
/// GMLAN / ISO 15765-4 over a raw CAN channel (11-bit IDs). ISO-TP segmentation is done
/// here rather than in the interface so the app can also see GMLAN UUDT frames ($A9 DTC
/// reports and $AA data come back as single unsegmented frames on 0x5xx IDs).
/// </summary>
public sealed class GmlanBus : VehicleBus
{
    public const uint ObdFunctionalId = 0x7DF;
    public const uint GmFunctionalId = 0x101;
    public const byte AllNodesExtAddr = 0xFE;

    /// <summary>Filler for unused bytes in a CAN frame.</summary>
    public byte PadByte { get; set; } = 0x00;

    public override BusKind Kind { get; }

    public GmlanBus(IPassThruChannel channel, BusKind kind) : base(channel) => Kind = kind;

    // ---- ID scheme --------------------------------------------------------------------
    // Powertrain: request 7E0-7E7, response +8, UUDT 5E8-5EF.
    // Other GMLAN nodes: request 240-25F, response +0x400 (640-65F), UUDT +0x300 (540-55F).

    public static uint ResponseIdFor(uint requestId) =>
        requestId is >= 0x7E0 and <= 0x7E7 ? requestId + 8 : requestId + 0x400;

    public static uint UudtIdFor(uint requestId) =>
        requestId is >= 0x7E0 and <= 0x7E7 ? requestId - 0x1F8 : requestId + 0x300;

    public static uint RequestIdFor(uint responseId) => responseId switch
    {
        >= 0x7E8 and <= 0x7EF => responseId - 8,
        >= 0x5E8 and <= 0x5EF => responseId + 0x1F8,
        >= 0x600 and <= 0x6FF => responseId - 0x400,
        >= 0x500 and <= 0x5FF => responseId - 0x300,
        _ => responseId,
    };

    private static bool IsUsdtResponse(uint id) => id is >= 0x600 and <= 0x6FF or >= 0x7E8 and <= 0x7EF;
    private static bool IsUudt(uint id) => id is >= 0x500 and <= 0x5FF;

    protected override void InstallNormalFilters()
    {
        AddFilter(Hex.U32(0x7F0), Hex.U32(0x7E0)); // 7E8-7EF OBD / powertrain responses
        AddFilter(Hex.U32(0x700), Hex.U32(0x600)); // GMLAN USDT responses
        AddFilter(Hex.U32(0x700), Hex.U32(0x500)); // GMLAN UUDT
    }

    private static readonly uint[] Candidates =
        Enumerable.Range(0x7E0, 8).Concat(Enumerable.Range(0x240, 0x20)).Select(i => (uint)i).ToArray();

    public override IReadOnlyList<uint> ScanAddresses => Candidates;
    public override string FormatAddress(uint address) => $"{address:X3}";

    // ---- Requests ---------------------------------------------------------------------

    public override IReadOnlyList<Reply> Request(uint target, byte[] payload, RequestOptions? options = null)
    {
        Channel.ClearRx();
        var rx = ResponseIdFor(target);
        var uudt = UudtIdFor(target);
        var reasm = new Reassembler(this);
        SendIsoTp(target, rx, payload, null);
        return Collect(options ?? RequestOptions.Default, f =>
        {
            var id = Hex.BigEndian(f.AsSpan(0, 4));
            if (id == uudt) return new[] { new Reply(target, f[4..], Unacknowledged: true) };
            if (id != rx) return Array.Empty<Reply>();
            return reasm.Feed(id, f[4..], target);
        });
    }

    public override IReadOnlyList<Reply> ObdRequest(byte[] payload, RequestOptions? options = null)
    {
        Channel.ClearRx();
        var reasm = new Reassembler(this);
        SendSingle(ObdFunctionalId, payload, null);
        return Collect(options ?? RequestOptions.Default, f =>
        {
            var id = Hex.BigEndian(f.AsSpan(0, 4));
            return id is >= 0x7E8 and <= 0x7EF ? reasm.Feed(id, f[4..], id - 8) : Array.Empty<Reply>();
        });
    }

    public override IReadOnlyList<Reply> FunctionalRequest(byte[] payload, RequestOptions? options = null)
    {
        Channel.ClearRx();
        var reasm = new Reassembler(this);
        SendSingle(GmFunctionalId, payload, AllNodesExtAddr);
        return Collect(options ?? RequestOptions.Default, f =>
        {
            var id = Hex.BigEndian(f.AsSpan(0, 4));
            if (IsUudt(id)) return new[] { new Reply(RequestIdFor(id), f[4..], Unacknowledged: true) };
            return IsUsdtResponse(id) ? reasm.Feed(id, f[4..], RequestIdFor(id)) : Array.Empty<Reply>();
        });
    }

    // ---- ISO-TP ----------------------------------------------------------------------

    private byte[] CanFrame(uint id, ReadOnlySpan<byte> data)
    {
        var f = new byte[12];
        Hex.U32(id).CopyTo(f, 0);
        f.AsSpan(4).Fill(PadByte);
        data.CopyTo(f.AsSpan(4));
        return f;
    }

    private void SendSingle(uint id, byte[] payload, byte? extAddr)
    {
        int max = extAddr is null ? 7 : 6;
        if (payload.Length > max) throw new ArgumentException($"Functional requests are limited to {max} bytes.");
        var d = new List<byte>();
        if (extAddr is byte ea) d.Add(ea);
        d.Add((byte)payload.Length);
        d.AddRange(payload);
        Send(CanFrame(id, d.ToArray()));
    }

    internal void SendFlowControl(uint toId) => Send(CanFrame(toId, new byte[] { 0x30, 0x00, 0x00 }));

    private void SendIsoTp(uint txId, uint rxId, byte[] payload, byte? extAddr)
    {
        if (payload.Length <= 7) { SendSingle(txId, payload, extAddr); return; }
        if (payload.Length > 4095) throw new ArgumentException("Request too long for ISO-TP.");

        var first = new byte[8];
        first[0] = (byte)(0x10 | payload.Length >> 8);
        first[1] = (byte)payload.Length;
        payload.AsSpan(0, 6).CopyTo(first.AsSpan(2));
        Send(CanFrame(txId, first));

        int offset = 6;
        byte seq = 1;
        while (offset < payload.Length)
        {
            var (blockSize, stMin) = WaitFlowControl(rxId);
            int sentInBlock = 0;
            while (offset < payload.Length && (blockSize == 0 || sentInBlock < blockSize))
            {
                var n = Math.Min(7, payload.Length - offset);
                var cf = new byte[1 + n];
                cf[0] = (byte)(0x20 | seq);
                payload.AsSpan(offset, n).CopyTo(cf.AsSpan(1));
                if (stMin > 0) Thread.Sleep(stMin);
                Send(CanFrame(txId, cf));
                offset += n;
                seq = (byte)((seq + 1) & 0x0F);
                sentInBlock++;
            }
        }
    }

    private (int BlockSize, int StMinMs) WaitFlowControl(uint rxId)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 1000)
        {
            foreach (var f in Channel.Read(32, 10))
            {
                if (f.IsTxEcho || f.Data.Length < 5 || Hex.BigEndian(f.Data.AsSpan(0, 4)) != rxId) continue;
                var pci = f.Data[4];
                if (pci >> 4 != 3) continue;
                if ((pci & 0x0F) == 1) { sw.Restart(); continue; } // wait
                if ((pci & 0x0F) == 2) throw new InvalidOperationException("Module rejected the request (ISO-TP overflow).");
                int bs = f.Data.Length > 5 ? f.Data[5] : 0;
                int st = f.Data.Length > 6 ? f.Data[6] : 0;
                return (bs, st <= 0x7F ? st : 1);
            }
        }
        throw new TimeoutException("Module did not send ISO-TP flow control.");
    }

    /// <summary>Rebuilds segmented replies from several modules at once.</summary>
    private sealed class Reassembler
    {
        private readonly GmlanBus _bus;
        private readonly Dictionary<uint, (byte[] Buf, int Have, int Seq)> _open = new();

        public Reassembler(GmlanBus bus) => _bus = bus;

        public IEnumerable<Reply> Feed(uint id, byte[] d, uint source)
        {
            if (d.Length == 0) yield break;
            switch (d[0] >> 4)
            {
                case 0:
                {
                    int len = d[0] & 0x0F;
                    if (len > 0 && len < d.Length) yield return new Reply(source, d[1..(1 + len)]);
                    break;
                }
                case 1:
                {
                    int len = (d[0] & 0x0F) << 8 | (d.Length > 1 ? d[1] : 0);
                    var buf = new byte[len];
                    int n = Math.Min(len, d.Length - 2);
                    Array.Copy(d, 2, buf, 0, n);
                    _open[id] = (buf, n, 1);
                    _bus.SendFlowControl(RequestIdFor(id));
                    break;
                }
                case 2:
                {
                    if (!_open.TryGetValue(id, out var st)) break;
                    if ((d[0] & 0x0F) != st.Seq) { _open.Remove(id); break; } // lost a frame
                    int n = Math.Min(st.Buf.Length - st.Have, d.Length - 1);
                    Array.Copy(d, 1, st.Buf, st.Have, n);
                    st.Have += n;
                    st.Seq = (st.Seq + 1) & 0x0F;
                    if (st.Have >= st.Buf.Length)
                    {
                        _open.Remove(id);
                        yield return new Reply(source, st.Buf);
                    }
                    else _open[id] = st;
                    break;
                }
            }
        }
    }

    public override (string Header, byte[] Data) SplitFrame(byte[] frame) =>
        frame.Length >= 4 ? (Hex.BigEndian(frame.AsSpan(0, 4)).ToString("X3"), frame[4..]) : (Hex.Format(frame), Array.Empty<byte>());

    public override uint? SourceOf(byte[] frame)
    {
        if (frame.Length < 4) return null;
        var id = Hex.BigEndian(frame.AsSpan(0, 4));
        return IsUsdtResponse(id) ? RequestIdFor(id) : null;
    }
}
