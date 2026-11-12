using VcxDiag.Core.Gm;
using VcxDiag.Core.Obd;

namespace VcxDiag.Core.Simulation;

/// <summary>
/// A made-up GM vehicle used by demo mode and the tests. It answers the same service
/// requests the app sends to real modules, with plausible values. Nothing here comes from
/// GM data; codes and numbers are invented for demonstration.
/// </summary>
public sealed class SimVehicle
{
    public sealed class SimModule
    {
        public required uint Address { get; init; }   // Class 2 node or CAN request ID
        public required byte DiagAddress { get; init; }
        public List<(string Code, byte Ftb, byte Status)> Dtcs { get; } = new();
        public Dictionary<byte, byte[]> Identifiers { get; } = new();
    }

    public string Vin { get; }
    public bool Can { get; }
    public List<SimModule> Modules { get; } = new();
    private readonly DateTime _start = DateTime.UtcNow;
    private bool _obdCleared;

    public SimVehicle(bool can, bool singleWire = false)
    {
        Can = can;
        Vin = MakeVin(can ? "1GCVKREC#EZ123456" : "1GCEK19T#4Z000001");
        if (!can)
        {
            Add(0x10, 0x10, ("P0171", 0, 0x81), ("P0420", 0, 0x00));
            Add(0x28, 0x28, ("C1214", 0, 0x80));
            Add(0x40, 0x40, ("B2725", 0, 0x00));
            Add(0x58, 0x58);
            Add(0x60, 0x60);
            Add(0x80, 0x80);
            Add(0x99, 0x99, ("B0408", 0, 0x01));
        }
        else if (singleWire)
        {
            Add(0x241, 0x40, ("B1325", 0x03, 0x09));
            Add(0x244, 0x99);
            Add(0x24A, 0x80, ("U0184", 0x00, 0x08));
            Add(0x24C, 0x60);
        }
        else
        {
            Add(0x7E0, 0x11, ("P0171", 0x00, 0xAF), ("P0420", 0x00, 0x28));
            Add(0x7E2, 0x18);
            Add(0x243, 0x28, ("C0035", 0x5A, 0x09));
            Add(0x241, 0x40, ("B1325", 0x03, 0x28));
            Add(0x247, 0x58);
            Add(0x24C, 0x60, ("U0140", 0x00, 0x08));
        }
        foreach (var m in Modules)
        {
            m.Identifiers[0x90] = System.Text.Encoding.ASCII.GetBytes(Vin);
            m.Identifiers[0xB0] = new[] { m.DiagAddress };
            m.Identifiers[0xC1] = Hex.U32((uint)(12600000 + m.DiagAddress * 101));
            m.Identifiers[0xCB] = Hex.U32((uint)(12650000 + m.DiagAddress * 7));
            m.Identifiers[0x99] = new byte[] { 0x20, 0x14, 0x03, 0x17 };
            m.Identifiers[0x98] = System.Text.Encoding.ASCII.GetBytes("DEMO000001");
        }
    }

    private void Add(uint addr, byte diag, params (string Code, byte Ftb, byte Status)[] dtcs)
    {
        var m = new SimModule { Address = addr, DiagAddress = diag };
        m.Dtcs.AddRange(dtcs);
        Modules.Add(m);
    }

    private static string MakeVin(string template)
    {
        var tmp = template.Replace('#', '0');
        return template.Replace('#', VcxDiag.Core.Gm.Vin.CheckDigit(tmp) ?? '0');
    }

    public SimModule? Find(uint addr) => Modules.FirstOrDefault(m => m.Address == addr);
    public SimModule Engine => Modules[0];

    // ------------------------------------------------------------------ live values

    private double T => (DateTime.UtcNow - _start).TotalSeconds;

    public byte[]? Pid(byte pid)
    {
        double t = T;
        double rpm = 750 + 1400 * Math.Max(0, Math.Sin(t / 4));
        double speed = Math.Max(0, 60 * Math.Sin(t / 12));
        double coolant = Math.Min(92, 40 + t * 2);
        static byte B(double v) => (byte)Math.Clamp(Math.Round(v), 0, 255);
        static byte[] W(double v) { var x = (ushort)Math.Clamp(Math.Round(v), 0, 65535); return new[] { (byte)(x >> 8), (byte)x }; }

        return pid switch
        {
            0x00 => new byte[] { 0xBE, 0x3F, 0xB8, 0x13 },
            0x20 => new byte[] { 0x80, 0x07, 0xF0, 0x19 },
            0x40 => new byte[] { 0xFE, 0xD0, 0x80, 0x00 },
            0x01 => new byte[] { (byte)((_obdCleared ? 0 : 0x80) | (_obdCleared ? 0 : 2)), 0x07, 0x65, (byte)(_obdCleared ? 0x65 : 0x21) },
            0x03 => new byte[] { 0x02, 0x00 },
            0x04 => new[] { B(20 + rpm / 60) },
            0x05 => new[] { B(coolant + 40) },
            0x06 => new[] { B(128 + 6 * Math.Sin(t)) },
            0x07 => new[] { B(128 + 12) },
            0x08 => new[] { B(128 + 5 * Math.Cos(t)) },
            0x09 => new[] { B(128 + 9) },
            0x0B => new[] { B(30 + rpm / 40) },
            0x0C => W(rpm * 4),
            0x0D => new[] { B(speed) },
            0x0E => new[] { B((12 + 10 * Math.Sin(t / 3) + 64) * 2) },
            0x0F => new[] { B(25 + 40) },
            0x10 => W((3 + rpm / 200) * 100),
            0x11 => new[] { B(15 + 30 * Math.Max(0, Math.Sin(t / 4))) },
            0x13 => new byte[] { 0x33 },
            0x14 => new[] { B(90 + 80 * Math.Sin(t * 3)), B(128) },
            0x15 => new[] { B(130), (byte)0xFF },
            0x1C => new byte[] { 0x01 },
            0x1F => W(t),
            0x21 => W(_obdCleared ? 0 : 42),
            0x2F => new[] { B(255 * 0.62) },
            0x30 => new[] { B(_obdCleared ? 0 : 18) },
            0x31 => W(_obdCleared ? 0 : 412),
            0x33 => new byte[] { 99 },
            0x3C => W((480 + 40) * 10),
            0x42 => W(14100 + 200 * Math.Sin(t)),
            0x45 => new[] { B(40) },
            0x46 => new[] { B(22 + 40) },
            0x47 => new[] { B(50) },
            0x49 => new[] { B(38) },
            0x4A => new[] { B(19) },
            0x4C => new[] { B(42) },
            _ => null,
        };
    }

    // ------------------------------------------------------------------ OBD requests

    /// <summary>Answers a generic OBD request. Returns (module address, payload) pairs.</summary>
    public IEnumerable<(uint, byte[])> Obd(byte[] req)
    {
        if (req.Length == 0) yield break;
        var ecm = Engine.Address;
        byte mode = req[0];
        switch (mode)
        {
            case 0x01:
            {
                var outp = new List<byte> { 0x41 };
                foreach (var pid in req.Skip(1))
                    if (Pid(pid) is byte[] d) { outp.Add(pid); outp.AddRange(d); }
                if (outp.Count > 1) yield return (ecm, outp.ToArray());
                break;
            }
            case 0x02:
            {
                if (req.Length < 3 || _obdCleared) break;
                byte pid = req[1];
                if (pid == 0x02) { var (h, l) = Dtc.EncodeCode("P0171"); yield return (ecm, new byte[] { 0x42, 0x02, 0x00, h, l }); }
                else if (pid == 0x00) yield return (ecm, new byte[] { 0x42, 0x00, 0x00, 0x7E, 0x3F, 0x80, 0x00 });
                else if (Pid(pid) is byte[] d) yield return (ecm, new byte[] { 0x42, pid, 0x00 }.Concat(d).ToArray());
                break;
            }
            case 0x03 or 0x07 or 0x0A:
            {
                var codes = _obdCleared ? new List<string>() : mode switch
                {
                    0x03 => new List<string> { "P0171", "P0420" },
                    0x07 => new List<string> { "P0171" },
                    _ => new List<string> { "P0420" },
                };
                var body = codes.SelectMany(c => { var (h, l) = Dtc.EncodeCode(c); return new[] { h, l }; }).ToList();
                if (Can) yield return (ecm, new byte[] { (byte)(mode + 0x40), (byte)codes.Count }.Concat(body).ToArray());
                else
                {
                    if (body.Count == 0) body.AddRange(new byte[] { 0, 0 });
                    yield return (ecm, new byte[] { (byte)(mode + 0x40) }.Concat(body).ToArray());
                }
                if (Can && mode == 0x03) yield return (Modules[1].Address, new byte[] { 0x43, 0x00 });
                break;
            }
            case 0x04:
                _obdCleared = true;
                Engine.Dtcs.Clear();
                yield return (ecm, new byte[] { 0x44 });
                break;
            case 0x06 when Can && req.Length >= 2:
            {
                byte mid = req[1];
                if (mid == 0x00) yield return (ecm, new byte[] { 0x46, 0x00, 0x80, 0x00, 0x00, 0x01 });
                else if (mid == 0x20) yield return (ecm, new byte[] { 0x46, 0x20, 0x80, 0x00, 0x00, 0x01 });
                else if (mid == 0x40) yield return (ecm, new byte[] { 0x46, 0x40, 0x00, 0x00, 0x00, 0x01 });
                else if (mid == 0x60) yield return (ecm, new byte[] { 0x46, 0x60, 0x00, 0x00, 0x00, 0x01 });
                else if (mid == 0x80) yield return (ecm, new byte[] { 0x46, 0x80, 0x00, 0x00, 0x00, 0x01 });
                else if (mid == 0xA0) yield return (ecm, new byte[] { 0x46, 0xA0, 0x78, 0x00, 0x00, 0x00 });
                else if (mid == 0x01) yield return (ecm, new byte[] { 0x46, 0x01, 0x01, 0x0A, 0x0B, 0x54, 0x0B, 0x54, 0xFF, 0xFF,
                                                                     0x01, 0x05, 0x10, 0x00, 0x48, 0x00, 0x00, 0x00, 0x96 });
                else if (mid == 0x21) yield return (ecm, new byte[] { 0x46, 0x21, 0x80, 0x01, 0x00, 0x4C, 0x00, 0x00, 0x00, 0x60 });
                else if (mid is >= 0xA2 and <= 0xA9) yield return (ecm, new byte[] { 0x46, mid, 0x0B, 0x24, 0x00, (byte)(mid == 0xA4 ? 0x40 : 0x00), 0x00, 0x00, 0x00, 0x32 });
                break;
            }
            case 0x09 when req.Length >= 2:
                foreach (var r in Mode9(req[1], ecm)) yield return r;
                break;
        }
    }

    private IEnumerable<(uint, byte[])> Mode9(byte pid, uint ecm)
    {
        byte[]? data = pid switch
        {
            0x02 => System.Text.Encoding.ASCII.GetBytes(Vin),
            0x04 => System.Text.Encoding.ASCII.GetBytes("12654321\0\0\0\0\0\0\0\0" + "12654987\0\0\0\0\0\0\0\0"),
            0x06 => new byte[] { 0x1A, 0x2B, 0x3C, 0x4D, 0x5E, 0x6F, 0x70, 0x81 },
            0x0A => System.Text.Encoding.ASCII.GetBytes("ECM\0-EngineControl\0"),
            _ => null,
        };
        if (data is null) yield break;
        byte count = pid switch { 0x04 => 2, 0x06 => 2, _ => 1 };
        if (Can)
        {
            yield return (ecm, new byte[] { 0x49, pid, count }.Concat(data).ToArray());
            yield break;
        }
        // Class 2: 4 data bytes per frame with a sequence number; VIN is padded to 20 bytes.
        var padded = pid == 0x02 ? new byte[3].Concat(data).ToArray() : data;
        for (int i = 0, seq = 1; i < padded.Length; i += 4, seq++)
            yield return (ecm, new byte[] { 0x49, pid, (byte)seq }.Concat(padded.Skip(i).Take(4)).ToArray());
    }

    // ------------------------------------------------------------------ GM requests

    public sealed record GmAnswer(List<byte[]> Usdt, List<byte[]> Uudt);

    /// <summary>Answers a physical (or functional) GM request to one module.</summary>
    public GmAnswer? Gm(SimModule m, byte[] req, bool functional = false)
    {
        var usdt = new List<byte[]>();
        var uudt = new List<byte[]>();
        if (req.Length == 0) return null;
        switch (req[0])
        {
            case 0x1A when req.Length >= 2:
                if (m.Identifiers.TryGetValue(req[1], out var v)) usdt.Add(new byte[] { 0x5A, req[1] }.Concat(v).ToArray());
                else if (!functional) usdt.Add(new byte[] { 0x7F, 0x1A, 0x31 });
                break;
            case 0x3C when req.Length >= 2 && !Can:
                if (req[1] is >= 1 and <= 3 && m.Address == 0x10)
                {
                    var vin = System.Text.Encoding.ASCII.GetBytes(Vin);
                    var part = req[1] == 1 ? vin[..5] : req[1] == 2 ? vin[5..11] : vin[11..];
                    usdt.Add(new byte[] { 0x7C, req[1] }.Concat(part).ToArray());
                }
                else if (req[1] == 0x04) usdt.Add(new byte[] { 0x7C, 0x04 }.Concat(Hex.U32(9380000u + m.Address)).ToArray());
                else usdt.Add(new byte[] { 0x7F, 0x3C, req[1], 0x12 });
                break;
            case 0x19 when !Can:
                foreach (var (code, _, st) in m.Dtcs)
                {
                    var (h, l) = Dtc.EncodeCode(code);
                    usdt.Add(new byte[] { 0x59, h, l, st });
                }
                usdt.Add(new byte[] { 0x59, 0x00, 0x00, 0xFF });
                break;
            case 0xA9 when Can:
                // GMLAN reports each code as an unsegmented (UUDT) frame, then an all-zero end marker.
                foreach (var (code, ftb, st) in m.Dtcs)
                {
                    var (h, l) = Dtc.EncodeCode(code);
                    uudt.Add(new byte[] { 0x81, h, l, ftb, st, 0x00, 0x00, 0x00 });
                }
                uudt.Add(new byte[] { 0x81, 0x00, 0x00, 0x00, 0xFF, 0x00, 0x00, 0x00 });
                break;
            case 0x04 when Can:
            case 0x14 when !Can:
                m.Dtcs.Clear();
                if (m == Engine) _obdCleared = true;
                usdt.Add(new[] { (byte)(req[0] + 0x40) });
                break;
            case 0x20:
                usdt.Add(new byte[] { 0x60 });
                break;
            case 0x3E or 0x3F:
                if (!functional) usdt.Add(new[] { (byte)(req[0] + 0x40) });
                break;
            case 0x22 when req.Length >= 3:
                // Demo identifier $1234 on the engine module: a 2-byte counter.
                if (m == Engine && req[1] == 0x12 && req[2] == 0x34)
                {
                    var t = (ushort)(T * 10);
                    usdt.Add(new byte[] { 0x62, 0x12, 0x34, (byte)(t >> 8), (byte)t });
                }
                else if (!functional) usdt.Add(new byte[] { 0x7F, 0x22, 0x31 });
                break;
            default:
                if (!functional) usdt.Add(new byte[] { 0x7F, req[0], 0x11 });
                break;
        }
        return new GmAnswer(usdt, uudt);
    }
}
