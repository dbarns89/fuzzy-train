using VcxDiag.Core.J2534;
using VcxDiag.Core.Transport;

namespace VcxDiag.Core.Simulation;

/// <summary>
/// A pretend J2534 interface wired to a <see cref="SimVehicle"/>. It works at the frame
/// level (Class 2 headers, CAN IDs, ISO-TP segmentation), so demo mode exercises the same
/// code paths as a real VCX Nano.
/// </summary>
public sealed class SimulatedDevice : IPassThruDevice
{
    public string Name => "Demo vehicle (simulator)";
    public DeviceVersion ReadVersion() => new("SIM 1.0", "VCX Diag simulator", "04.04");
    public double? ReadBatteryVolts() => 12.4 + 0.3 * Math.Sin(DateTime.UtcNow.Second / 9.0);

    public IPassThruChannel Connect(ProtocolId protocol, uint baud, uint flags = 0, uint? j1962Pins = null) => protocol switch
    {
        ProtocolId.J1850VPW => new SimChannel(protocol, new SimVehicle(can: false)),
        ProtocolId.CAN => new SimChannel(protocol, new SimVehicle(can: true)),
        ProtocolId.SW_CAN_PS => new SimChannel(protocol, new SimVehicle(can: true, singleWire: true)),
        _ => throw new PassThruException("PassThruConnect", J2534Status.InvalidProtocolId, "the demo vehicle has no " + protocol + " bus"),
    };

    public void Dispose() { }
}

internal sealed class SimChannel : IPassThruChannel
{
    private readonly SimVehicle _car;
    private readonly Queue<RawFrame> _rx = new();
    private readonly Dictionary<uint, (byte[] Mask, byte[] Pattern)> _filters = new();
    private readonly Dictionary<uint, Queue<byte[]>> _pendingCf = new();
    private readonly Dictionary<uint, (byte[] Buf, int Have)> _inbound = new();
    private uint _nextFilter = 1;
    private DateTime _lastTraffic = DateTime.UtcNow;
    private readonly Random _rand = new(7);

    public ProtocolId Protocol { get; }
    private bool IsCan => Protocol != ProtocolId.J1850VPW;

    public SimChannel(ProtocolId protocol, SimVehicle car)
    {
        Protocol = protocol;
        _car = car;
    }

    public uint StartPassFilter(byte[] mask, byte[] pattern)
    {
        _filters[_nextFilter] = (mask, pattern);
        return _nextFilter++;
    }

    public void StopFilter(uint filterId) => _filters.Remove(filterId);
    public void ClearRx() => _rx.Clear();

    private void Deliver(byte[] frame)
    {
        foreach (var (mask, pattern) in _filters.Values)
        {
            bool ok = true;
            for (int i = 0; i < mask.Length && ok; i++)
                ok = i < frame.Length && (frame[i] & mask[i]) == (pattern[i] & mask[i]);
            if (ok) { _rx.Enqueue(new RawFrame(frame, (uint)Environment.TickCount, 0)); return; }
        }
    }

    public IReadOnlyList<RawFrame> Read(int max, uint timeoutMs)
    {
        GenerateBackgroundTraffic();
        if (_rx.Count == 0 && timeoutMs > 0) Thread.Sleep((int)Math.Min(timeoutMs, 5));
        var list = new List<RawFrame>();
        while (_rx.Count > 0 && list.Count < max) list.Add(_rx.Dequeue());
        return list;
    }

    /// <summary>Normal module chatter, so the bus monitor and passive scan have something to see.</summary>
    private void GenerateBackgroundTraffic()
    {
        if ((DateTime.UtcNow - _lastTraffic).TotalMilliseconds < 25) return;
        _lastTraffic = DateTime.UtcNow;
        var m = _car.Modules[_rand.Next(_car.Modules.Count)];
        if (IsCan)
        {
            uint[] ids = { 0x0C9, 0x0F1, 0x1E9, 0x3C9, 0x4C1, 0x4D1 };
            var id = ids[_rand.Next(ids.Length)];
            var data = new byte[8];
            _rand.NextBytes(data);
            Deliver(Hex.U32(id).Concat(data).ToArray());
        }
        else
        {
            var data = new byte[_rand.Next(1, 5)];
            _rand.NextBytes(data);
            Deliver(new byte[] { 0x88, (byte)(0x0B + _rand.Next(4)), (byte)m.Address }.Concat(data).ToArray());
        }
    }

    public void Write(byte[] data, uint txFlags = 0)
    {
        if (IsCan) WriteCan(data);
        else WriteVpw(data);
    }

    // ----------------------------------------------------------------- Class 2

    private void WriteVpw(byte[] f)
    {
        if (f.Length < 4) return;
        byte target = f[1];
        var req = f[3..];
        if (target == 0x6A)
        {
            foreach (var (src, p) in _car.Obd(req))
                Deliver(new byte[] { 0x48, 0x6B, (byte)src }.Concat(p).ToArray());
            return;
        }
        var modules = target == 0xFE ? _car.Modules : _car.Modules.Where(m => m.Address == target).ToList();
        foreach (var m in modules)
        {
            var a = _car.Gm(m, req, functional: target == 0xFE);
            if (a is null) continue;
            foreach (var p in a.Usdt) Deliver(new byte[] { 0x6C, 0xF1, (byte)m.Address }.Concat(p).ToArray());
        }
    }

    // ----------------------------------------------------------------- CAN / ISO-TP

    private static uint ResponseIdFor(uint req) => req is >= 0x7E0 and <= 0x7E7 ? req + 8 : req + 0x400;
    private static uint UudtIdFor(uint req) => req is >= 0x7E0 and <= 0x7E7 ? req - 0x1F8 : req + 0x300;

    private void SendUsdt(uint respId, byte[] payload)
    {
        if (payload.Length <= 7)
        {
            Deliver(Frame(respId, new[] { (byte)payload.Length }.Concat(payload).ToArray()));
            return;
        }
        Deliver(Frame(respId, new[] { (byte)(0x10 | payload.Length >> 8), (byte)payload.Length }.Concat(payload.Take(6)).ToArray()));
        var q = new Queue<byte[]>();
        byte seq = 1;
        for (int i = 6; i < payload.Length; i += 7, seq = (byte)((seq + 1) & 0xF))
            q.Enqueue(Frame(respId, new[] { (byte)(0x20 | seq) }.Concat(payload.Skip(i).Take(7)).ToArray()));
        _pendingCf[respId] = q;
    }

    private static byte[] Frame(uint id, byte[] data)
    {
        var f = new byte[12];
        Hex.U32(id).CopyTo(f, 0);
        data.Take(8).ToArray().CopyTo(f, 4);
        return f;
    }

    private void WriteCan(byte[] f)
    {
        if (f.Length < 5) return;
        uint id = Hex.BigEndian(f.AsSpan(0, 4));
        var d = f[4..];

        if (id == 0x101 && d[0] == 0xFE)
        {
            int len = d[1] & 0x0F;
            var req = d.Skip(2).Take(len).ToArray();
            foreach (var m in _car.Modules)
            {
                var a = _car.Gm(m, req, functional: true);
                if (a is null) continue;
                foreach (var p in a.Usdt) SendUsdt(ResponseIdFor(m.Address), p);
            }
            return;
        }

        int pci = d[0] >> 4;
        if (pci == 3)
        {
            // Flow control from the tester: release the rest of a segmented reply.
            var resp = ResponseIdFor(id);
            if (_pendingCf.Remove(resp, out var q)) while (q.Count > 0) Deliver(q.Dequeue());
            return;
        }

        byte[]? payload = null;
        if (pci == 0) payload = d.Skip(1).Take(d[0] & 0x0F).ToArray();
        else if (pci == 1)
        {
            int len = (d[0] & 0x0F) << 8 | d[1];
            var buf = new byte[len];
            Array.Copy(d, 2, buf, 0, 6);
            _inbound[id] = (buf, 6);
            Deliver(Frame(ResponseIdFor(id), new byte[] { 0x30, 0x00, 0x00 }));
            return;
        }
        else if (pci == 2 && _inbound.TryGetValue(id, out var st))
        {
            int n = Math.Min(7, st.Buf.Length - st.Have);
            Array.Copy(d, 1, st.Buf, st.Have, n);
            st.Have += n;
            if (st.Have < st.Buf.Length) { _inbound[id] = st; return; }
            _inbound.Remove(id);
            payload = st.Buf;
        }
        if (payload is null || payload.Length == 0) return;

        if (id == 0x7DF)
        {
            foreach (var (src, p) in _car.Obd(payload)) SendUsdt(ResponseIdFor(src), p);
            return;
        }
        var mod = _car.Find(id);
        if (mod is null) return;
        if (mod == _car.Engine && payload[0] is >= 0x01 and <= 0x0A)
        {
            foreach (var (_, p) in _car.Obd(payload)) SendUsdt(ResponseIdFor(id), p);
            return;
        }
        var ans = _car.Gm(mod, payload);
        if (ans is null) return;
        foreach (var p in ans.Usdt) SendUsdt(ResponseIdFor(id), p);
        foreach (var u in ans.Uudt) Deliver(Frame(UudtIdFor(id), u));
    }

    public void Dispose() { }
}
