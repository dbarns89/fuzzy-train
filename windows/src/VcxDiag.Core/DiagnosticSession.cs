using VcxDiag.Core.Gm;
using VcxDiag.Core.J2534;
using VcxDiag.Core.Obd;
using VcxDiag.Core.Protocols;
using VcxDiag.Core.Transport;

namespace VcxDiag.Core;

public sealed record ObdInfo(string Source, IReadOnlyList<string> CalibrationIds, IReadOnlyList<string> Cvns, string? EcuName);

public sealed record FreezeFrame(string? TriggerCode, IReadOnlyList<(ObdParameter Param, double Value)> Values);

/// <summary>
/// A user-defined data parameter read with service $22 (2-byte identifier). The app ships
/// none, because GM's parameter lists are proprietary, but users can add their own from
/// documentation they're entitled to.
/// </summary>
public sealed record CustomParameter
{
    public string Name { get; init; } = "";
    [System.Text.Json.Serialization.JsonIgnore] public uint Module { get; init; } = 0x7E0;
    [System.Text.Json.Serialization.JsonIgnore] public ushort Identifier { get; init; }

    /// <summary>Module address in hex for the settings file, e.g. "7E0" (CAN) or "10" (Class 2).</summary>
    public string ModuleHex
    {
        get => Module.ToString("X");
        init => Module = Convert.ToUInt32(value.Replace("0x", "").Replace("$", ""), 16);
    }

    /// <summary>Two-byte data identifier for service $22, in hex, e.g. "1234".</summary>
    public string IdentifierHex
    {
        get => Identifier.ToString("X4");
        init => Identifier = Convert.ToUInt16(value.Replace("0x", "").Replace("$", ""), 16);
    }
    public int Offset { get; init; }
    public int Length { get; init; } = 1;
    public bool Signed { get; init; }
    public double Scale { get; init; } = 1;
    public double Add { get; init; }
    public string Unit { get; init; } = "";

    public double? Decode(byte[] data)
    {
        if (Offset + Length > data.Length || Length is < 1 or > 4) return null;
        long raw = 0;
        for (int i = 0; i < Length; i++) raw = raw << 8 | data[Offset + i];
        if (Signed && (raw & (1L << (Length * 8 - 1))) != 0) raw -= 1L << (Length * 8);
        return raw * Scale + Add;
    }
}

/// <summary>
/// The diagnostic functions of the app. Everything here is synchronous and talks to the
/// bus directly; call it through <see cref="DiagWorker"/> so it runs off the UI thread.
/// </summary>
public sealed class DiagnosticSession : IDisposable
{
    public IPassThruDevice Device { get; }
    public VehicleBus? Bus { get; private set; }
    public Action<string>? Log { get; set; }

    public DiagnosticSession(IPassThruDevice device) => Device = device;

    // ---------------------------------------------------------------- connection

    public VehicleBus OpenBus(BusKind kind)
    {
        CloseBus();
        IPassThruChannel ch = kind switch
        {
            BusKind.Class2 => Device.Connect(ProtocolId.J1850VPW, 10400),
            BusKind.GmlanHighSpeed => Device.Connect(ProtocolId.CAN, 500000),
            BusKind.GmlanSingleWire => Device.Connect(ProtocolId.SW_CAN_PS, 33333, 0, 0x0100),
            BusKind.GmlanMediumSpeed => Device.Connect(ProtocolId.CAN_PS, 95238, 0, 0x030B),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        VehicleBus bus = kind == BusKind.Class2 ? new Class2Bus(ch) : new GmlanBus(ch, kind);
        try { bus.Start(); }
        catch { bus.Dispose(); throw; }
        bus.Trace = s => Log?.Invoke(s);
        Bus = bus;
        Log?.Invoke($"Opened {kind.Title()}");
        return bus;
    }

    public void CloseBus()
    {
        Bus?.Dispose();
        Bus = null;
    }

    /// <summary>Tries high speed CAN, then Class 2, using OBD mode $01 PID $00 as the probe.</summary>
    public BusKind? AutoDetect()
    {
        foreach (var kind in new[] { BusKind.GmlanHighSpeed, BusKind.Class2 })
        {
            try
            {
                var bus = OpenBus(kind);
                var r = bus.ObdRequest(new byte[] { 0x01, 0x00 }, new RequestOptions { TimeoutMs = 500 });
                if (r.Any(x => x.Payload.Length >= 2 && x.Payload[0] == 0x41)) return kind;
                // Some GMLAN vehicles only answer GM requests before the engine module wakes up.
                if (kind != BusKind.Class2 && bus.FunctionalRequest(new byte[] { 0x1A, 0xB0 }, new RequestOptions { TimeoutMs = 400 }).Count > 0)
                    return kind;
            }
            catch (PassThruException e)
            {
                Log?.Invoke($"{kind.Title()}: {e.Message}");
            }
        }
        CloseBus();
        return null;
    }

    private VehicleBus B => Bus ?? throw new InvalidOperationException("Not connected to a vehicle bus.");

    private bool IsCan => B.Kind.IsCan();

    // ---------------------------------------------------------------- identification

    public string? ReadVin()
    {
        // 1) OBD mode $09 PID $02
        var r = B.ObdRequest(new byte[] { 0x09, 0x02 }, new RequestOptions { TimeoutMs = 600, IdleMs = 120 });
        var vin = Vin.FromBytes(AssembleMode9(r, 0x02).FirstOrDefault().Data ?? Array.Empty<byte>());
        if (vin is not null) return vin;

        // 2) GM enhanced
        if (IsCan)
        {
            foreach (var target in new uint[] { 0x7E0, 0x241, 0x244 })
            {
                var g = B.Request(target, new byte[] { 0x1A, 0x90 }, new RequestOptions { TimeoutMs = 300, FirstOnly = true });
                var p = g.FirstOrDefault(x => x.Payload.Length > 2 && x.Payload[0] == 0x5A);
                if (p is not null && Vin.FromBytes(p.Payload[2..]) is string v) return v;
            }
        }
        else
        {
            var bytes = new List<byte>();
            for (byte block = 1; block <= 3; block++)
            {
                var g = B.Request(0x10, new byte[] { 0x3C, block }, new RequestOptions { TimeoutMs = 300, FirstOnly = true });
                var p = g.FirstOrDefault(x => x.Payload.Length > 2 && x.Payload[0] == 0x7C);
                if (p is null) break;
                bytes.AddRange(p.Payload[2..]);
            }
            if (Vin.FromBytes(bytes) is string v) return v;
        }
        return null;
    }

    /// <summary>Groups mode $49 replies per module and joins multi-frame (Class 2) answers.</summary>
    private List<(uint Source, byte[] Data)> AssembleMode9(IEnumerable<Reply> replies, byte pid)
    {
        var result = new List<(uint, byte[])>();
        foreach (var g in replies.Where(x => x.Payload.Length >= 3 && x.Payload[0] == 0x49 && x.Payload[1] == pid).GroupBy(x => x.Source))
        {
            if (IsCan)
            {
                // 49 pid count data...
                result.Add((g.Key, g.First().Payload[3..]));
            }
            else
            {
                // 49 pid seq d1 d2 d3 d4, one frame per seq
                var data = g.OrderBy(x => x.Payload[2]).SelectMany(x => x.Payload[3..]).ToArray();
                result.Add((g.Key, data));
            }
        }
        return result;
    }

    public IReadOnlyList<ObdInfo> ReadObdInfo()
    {
        var o = new RequestOptions { TimeoutMs = 700, IdleMs = 150 };
        var cal = AssembleMode9(B.ObdRequest(new byte[] { 0x09, 0x04 }, o), 0x04);
        var cvn = AssembleMode9(B.ObdRequest(new byte[] { 0x09, 0x06 }, o), 0x06);
        var name = AssembleMode9(B.ObdRequest(new byte[] { 0x09, 0x0A }, o), 0x0A);
        var sources = cal.Select(x => x.Source).Concat(cvn.Select(x => x.Source)).Concat(name.Select(x => x.Source)).Distinct();
        var list = new List<ObdInfo>();
        foreach (var s in sources)
        {
            var calIds = Chunk(cal.FirstOrDefault(x => x.Source == s).Data, 16)
                .Select(c => Hex.AsText(c) ?? Hex.Format(c)).Where(x => x.Length > 0).ToList();
            var cvns = Chunk(cvn.FirstOrDefault(x => x.Source == s).Data, 4).Select(c => Hex.Format(c).Replace(" ", "")).ToList();
            var ecu = name.FirstOrDefault(x => x.Source == s).Data is byte[] n ? Hex.AsText(n) : null;
            list.Add(new ObdInfo(NameFor(s), calIds, cvns, ecu));
        }
        return list;
    }

    private static IEnumerable<byte[]> Chunk(byte[]? data, int size)
    {
        if (data is null) yield break;
        for (int i = 0; i + size <= data.Length; i += size) yield return data[i..(i + size)];
    }

    private string NameFor(uint source) => IsCan
        ? GmModuleNames.ForCanRequestId(source)
        : GmModuleNames.ForNode(source);

    // ---------------------------------------------------------------- module scan

    public List<EcuModule> ScanModules(IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var found = new Dictionary<uint, EcuModule>();
        var kind = B.Kind;

        void Add(uint addr, string how, byte? diag = null)
        {
            if (!found.TryGetValue(addr, out var m))
                found[addr] = m = new EcuModule { Address = addr, Bus = kind, FoundBy = how };
            if (diag is not null) m.DiagAddress = diag;
        }

        if (IsCan)
        {
            progress?.Report("Asking all modules to identify themselves…");
            foreach (var r in B.FunctionalRequest(new byte[] { 0x1A, 0xB0 }, new RequestOptions { TimeoutMs = 600, IdleMs = 200 }))
                if (!r.Unacknowledged) Add(r.Source, "Answered roll call", r.Payload is [0x5A, 0xB0, var d, ..] ? d : null);
        }
        else
        {
            progress?.Report("Listening to the bus…");
            B.BeginMonitor();
            try
            {
                var until = DateTime.UtcNow.AddMilliseconds(1500);
                while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
                    foreach (var f in B.ReadRaw(50))
                        if (B.SourceOf(f.Data) is uint s && s != Class2Bus.Tester && s < 0xF0) Add(s, "Heard on bus");
            }
            finally { B.EndMonitor(); }
        }

        foreach (var addr in B.ScanAddresses)
        {
            ct.ThrowIfCancellationRequested();
            if (found.ContainsKey(addr)) continue;
            progress?.Report($"Checking {B.FormatAddress(addr)}…");
            var probe = IsCan ? new byte[] { 0x1A, 0xB0 } : new byte[] { 0x3C, 0x01 };
            var r = B.Request(addr, probe, new RequestOptions { TimeoutMs = 120, FirstOnly = true });
            if (r.Count > 0) Add(addr, "Answered probe", IsCan && r[0].Payload is [0x5A, 0xB0, var d, ..] ? d : null);
        }

        foreach (var m in found.Values) m.Name = GmModuleNames.Resolve(m);
        progress?.Report($"Found {found.Count} module(s).");
        return found.Values.OrderBy(m => m.Address).ToList();
    }

    public List<IdentItem> ReadIdentification(EcuModule m)
    {
        var items = new List<IdentItem>();
        if (IsCan)
        {
            foreach (var (did, label) in GmIdentifiers.Gmlan)
            {
                var r = B.Request(m.Address, new byte[] { 0x1A, did }, new RequestOptions { TimeoutMs = 200, FirstOnly = true });
                var p = r.FirstOrDefault(x => x.Payload.Length > 2 && x.Payload[0] == 0x5A && x.Payload[1] == did);
                if (p is not null) items.Add(new IdentItem(label, $"$1A {did:X2}", p.Payload[2..]));
            }
        }
        else
        {
            for (byte block = 0x01; block <= 0x10; block++)
            {
                var r = B.Request(m.Address, new byte[] { 0x3C, block }, new RequestOptions { TimeoutMs = 150, FirstOnly = true });
                var p = r.FirstOrDefault(x => x.Payload.Length > 2 && x.Payload[0] == 0x7C && x.Payload[1] == block);
                if (p is not null)
                    items.Add(new IdentItem(block <= 3 ? $"VIN part {block}" : $"Data block ${block:X2}", $"$3C {block:X2}", p.Payload[2..]));
            }
        }
        return items;
    }

    // ---------------------------------------------------------------- trouble codes

    public List<Dtc> ReadModuleDtcs(EcuModule m)
    {
        var list = new List<Dtc>();
        if (!IsCan)
        {
            var r = B.Request(m.Address, new byte[] { 0x19, 0xD3, 0xFF, 0x00 }, new RequestOptions
            {
                TimeoutMs = 1200, IdleMs = 250,
                Done = x => x.Payload is [0x59, 0x00, 0x00, ..],
            });
            foreach (var x in r)
            {
                if (x.Payload is not [0x59, var hi, var lo, var st, ..]) continue;
                if (Dtc.DecodeCode(hi, lo) is not string code) continue;
                list.Add(new Dtc
                {
                    Code = code, Module = m.Name, ModuleAddress = m.Address, Source = DtcSource.Module,
                    StatusByte = st, StatusText = Class2Status(st),
                });
            }
        }
        else
        {
            var r = B.Request(m.Address, new byte[] { 0xA9, 0x81, 0x12 }, new RequestOptions
            {
                TimeoutMs = 1500, IdleMs = 300,
                Done = x => x.Unacknowledged && x.Payload is [0x81, 0x00, 0x00, 0x00, ..],
            });
            foreach (var x in r)
            {
                if (!x.Unacknowledged || x.Payload is not [0x81, var hi, var lo, var ftb, var st, ..]) continue;
                if (Dtc.DecodeCode(hi, lo) is not string code) continue;
                list.Add(new Dtc
                {
                    Code = code, Module = m.Name, ModuleAddress = m.Address, Source = DtcSource.Module,
                    FailureType = ftb, StatusByte = st, StatusText = GmlanStatus(st),
                });
            }
        }
        m.DtcCount = list.Count;
        return list;
    }

    /// <summary>Class 2 $19 status byte (bit meanings as commonly documented; confirm on your vehicle).</summary>
    public static string Class2Status(byte s)
    {
        var parts = new List<string> { (s & 0x01) != 0 ? "Current" : "History" };
        if ((s & 0x80) != 0) parts.Add("warning lamp requested");
        if ((s & 0x20) != 0) parts.Add("failed since clear");
        return string.Join(", ", parts);
    }

    /// <summary>GMLAN $A9 status byte, using the ISO 14229 style bit layout GMLAN follows.</summary>
    public static string GmlanStatus(byte s)
    {
        var parts = new List<string>();
        if ((s & 0x01) != 0) parts.Add("Current");
        else if ((s & 0x08) != 0 || (s & 0x20) != 0) parts.Add("History");
        if ((s & 0x04) != 0) parts.Add("pending");
        if ((s & 0x80) != 0) parts.Add("warning lamp requested");
        if ((s & 0x10) != 0) parts.Add("test not run since clear");
        return parts.Count == 0 ? $"Status ${s:X2}" : string.Join(", ", parts);
    }

    public List<Dtc> ReadObdDtcs(DtcSource which)
    {
        byte mode = which switch { DtcSource.Pending => 0x07, DtcSource.Permanent => 0x0A, _ => 0x03 };
        byte resp = (byte)(mode + 0x40);
        var r = B.ObdRequest(new[] { mode }, new RequestOptions { TimeoutMs = 1000, IdleMs = 200 });
        var list = new List<Dtc>();
        foreach (var x in r.Where(x => x.Payload.Length >= 1 && x.Payload[0] == resp))
        {
            var body = IsCan ? x.Payload.Skip(2).ToArray() : x.Payload.Skip(1).ToArray();
            for (int i = 0; i + 1 < body.Length; i += 2)
            {
                if (Dtc.DecodeCode(body[i], body[i + 1]) is string code && !list.Any(d => d.Code == code && d.ModuleAddress == x.Source))
                    list.Add(new Dtc
                    {
                        Code = code, Module = NameFor(x.Source) + " (OBD)", ModuleAddress = x.Source, Source = which,
                        StatusText = which switch { DtcSource.Pending => "Pending", DtcSource.Permanent => "Permanent", _ => "Stored (emissions)" },
                    });
            }
        }
        return list;
    }

    public bool ClearModuleDtcs(EcuModule m)
    {
        byte svc = IsCan ? (byte)0x04 : (byte)0x14;
        var r = B.Request(m.Address, new[] { svc }, new RequestOptions { TimeoutMs = 1500, FirstOnly = true });
        m.DtcCount = null;
        return r.Any(x => x.Payload.Length > 0 && x.Payload[0] == svc + 0x40);
    }

    /// <summary>Clears codes in every module (GM functional clear) and emissions codes (OBD mode $04).</summary>
    public int ClearAllDtcs()
    {
        byte svc = IsCan ? (byte)0x04 : (byte)0x14;
        var gm = B.FunctionalRequest(new[] { svc }, new RequestOptions { TimeoutMs = 1500, IdleMs = 300 });
        var obd = B.ObdRequest(new byte[] { 0x04 }, new RequestOptions { TimeoutMs = 1500, IdleMs = 300 });
        return gm.Count(x => x.Payload.Length > 0 && x.Payload[0] == svc + 0x40) +
               obd.Count(x => x.Payload.Length > 0 && x.Payload[0] == 0x44);
    }

    // ---------------------------------------------------------------- OBD data

    /// <summary>Mode $01 (or $02) PIDs the engine module supports.</summary>
    public SortedSet<byte> SupportedPids(byte mode = 0x01)
    {
        var set = new SortedSet<byte>();
        byte basePid = 0x00;
        while (true)
        {
            var req = mode == 0x02 ? new byte[] { 0x02, basePid, 0x00 } : new byte[] { 0x01, basePid };
            var r = B.ObdRequest(req, new RequestOptions { TimeoutMs = 400, IdleMs = 100 });
            bool any = false;
            foreach (var x in r)
            {
                int skip = mode == 0x02 ? 3 : 2;
                if (x.Payload.Length < skip + 4 || x.Payload[0] != mode + 0x40 || x.Payload[1] != basePid) continue;
                any = true;
                foreach (var p in ObdPids.DecodeSupportBitmap(basePid, x.Payload.AsSpan(skip, 4))) set.Add(p);
            }
            byte next = (byte)(basePid + 0x20);
            if (!any || !set.Contains(next) || basePid >= 0xC0) break;
            basePid = next;
        }
        return set;
    }

    /// <summary>Reads mode $01 PIDs. On CAN up to 6 PIDs go in one request.</summary>
    public Dictionary<byte, byte[]> ReadPids(IReadOnlyCollection<byte> pids)
    {
        var result = new Dictionary<byte, byte[]>();
        var groups = IsCan ? pids.Chunk(6) : pids.Select(p => new[] { p });
        foreach (var g in groups)
        {
            var req = new byte[1 + g.Length];
            req[0] = 0x01;
            g.CopyTo(req, 1);
            var r = B.ObdRequest(req, new RequestOptions { TimeoutMs = 250, IdleMs = 40, FirstOnly = !IsCan || g.Length == 1 });
            // Prefer the engine module when several answer.
            foreach (var x in r.OrderBy(x => x.Source))
            {
                if (x.Payload.Length < 2 || x.Payload[0] != 0x41) continue;
                foreach (var (pid, data) in ObdPids.SplitReply(x.Payload.AsSpan(1)))
                    result.TryAdd(pid, data);
            }
        }
        return result;
    }

    public double? ReadCustom(CustomParameter p)
    {
        var r = B.Request(p.Module, new byte[] { 0x22, (byte)(p.Identifier >> 8), (byte)p.Identifier },
            new RequestOptions { TimeoutMs = 250, FirstOnly = true });
        var x = r.FirstOrDefault(x => x.Payload.Length > 3 && x.Payload[0] == 0x62);
        return x is null ? null : p.Decode(x.Payload[3..]);
    }

    public ReadinessStatus? ReadReadiness()
    {
        var r = B.ObdRequest(new byte[] { 0x01, 0x01 }, new RequestOptions { TimeoutMs = 400, IdleMs = 100 });
        var x = r.OrderBy(x => x.Source).FirstOrDefault(x => x.Payload.Length >= 6 && x.Payload[0] == 0x41 && x.Payload[1] == 0x01);
        return x is null ? null : ReadinessStatus.Decode(x.Payload.AsSpan(2, 4));
    }

    public FreezeFrame? ReadFreezeFrame()
    {
        var o = new RequestOptions { TimeoutMs = 400, FirstOnly = true };
        var trig = B.ObdRequest(new byte[] { 0x02, 0x02, 0x00 }, o)
            .FirstOrDefault(x => x.Payload.Length >= 5 && x.Payload[0] == 0x42 && x.Payload[1] == 0x02);
        string? code = trig is null ? null : Dtc.DecodeCode(trig.Payload[3], trig.Payload[4]);
        if (code is null) return null;

        var supported = SupportedPids(0x02);
        if (supported.Count == 0) supported = new SortedSet<byte>(new byte[] { 0x04, 0x05, 0x06, 0x07, 0x0B, 0x0C, 0x0D, 0x11 });
        var values = new List<(ObdParameter, double)>();
        foreach (var pid in supported.Where(p => !ObdPids.IsSupportBitmap(p) && p != 0x02 && ObdPids.Length(p) > 0))
        {
            var x = B.ObdRequest(new byte[] { 0x02, pid, 0x00 }, o)
                .FirstOrDefault(x => x.Payload.Length >= 3 && x.Payload[0] == 0x42 && x.Payload[1] == pid);
            if (x is null) continue;
            var data = x.Payload[3..];
            if (data.Length < ObdPids.Length(pid)) continue;
            foreach (var p in ObdPids.ForPid(pid)) values.Add((p, p.Decode(data)));
        }
        return new FreezeFrame(code, values);
    }

    public List<TestResult> ReadMode06()
    {
        if (!IsCan) throw new NotSupportedException("Test results (mode $06) are shown for CAN vehicles only. Class 2 vehicles use a different, non-standard layout.");
        var mids = new SortedSet<byte>();
        byte basePid = 0x00;
        while (true)
        {
            var x = B.ObdRequest(new byte[] { 0x06, basePid }, new RequestOptions { TimeoutMs = 400, FirstOnly = true })
                .FirstOrDefault(x => x.Payload.Length >= 6 && x.Payload[0] == 0x46 && x.Payload[1] == basePid);
            if (x is null) break;
            foreach (var p in ObdPids.DecodeSupportBitmap(basePid, x.Payload.AsSpan(2, 4))) mids.Add(p);
            byte next = (byte)(basePid + 0x20);
            if (!mids.Contains(next) || basePid >= 0xE0) break;
            basePid = next;
        }
        var results = new List<TestResult>();
        foreach (var mid in mids.Where(m => m % 0x20 != 0))
        {
            var x = B.ObdRequest(new byte[] { 0x06, mid }, new RequestOptions { TimeoutMs = 400, FirstOnly = true })
                .FirstOrDefault(x => x.Payload.Length >= 10 && x.Payload[0] == 0x46);
            if (x is not null) results.AddRange(Mode06.ParseCan(x.Payload.AsSpan(1)));
        }
        return results;
    }

    // ---------------------------------------------------------------- raw + functions

    public IReadOnlyList<Reply> Raw(uint? target, bool obd, byte[] payload, int timeoutMs = 1000)
    {
        var o = new RequestOptions { TimeoutMs = timeoutMs, IdleMs = 200 };
        if (obd) return B.ObdRequest(payload, o);
        if (target is null) return B.FunctionalRequest(payload, o);
        return B.Request(target.Value, payload, o);
    }

    /// <summary>Ends any device control or special mode (GM "return to normal", service $20).</summary>
    public int ReturnToNormal() =>
        B.FunctionalRequest(new byte[] { 0x20 }, new RequestOptions { TimeoutMs = 400, IdleMs = 150 }).Count;

    /// <summary>Keeps modules in diagnostic mode (tester present, service $3E). No reply expected.</summary>
    public void TesterPresent() =>
        B.FunctionalRequest(new byte[] { IsCan ? (byte)0x3E : (byte)0x3F }, new RequestOptions { TimeoutMs = 30 });

    public void Dispose()
    {
        CloseBus();
        Device.Dispose();
    }
}
