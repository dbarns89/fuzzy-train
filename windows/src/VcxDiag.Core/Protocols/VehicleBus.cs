using System.Diagnostics;
using VcxDiag.Core.J2534;
using VcxDiag.Core.Transport;

namespace VcxDiag.Core.Protocols;

public enum BusKind
{
    /// <summary>GM Class 2 (SAE J1850 VPW, 10.4 kbit/s). Most GM vehicles 1996-2007.</summary>
    Class2,
    /// <summary>GMLAN high speed CAN (500 kbit/s, OBD pins 6/14). Powertrain on 2005+, everything on 2008+.</summary>
    GmlanHighSpeed,
    /// <summary>GMLAN single wire CAN (33.3 kbit/s, OBD pin 1). Body/comfort modules 2005+.</summary>
    GmlanSingleWire,
    /// <summary>GMLAN medium speed CAN (95.2 kbit/s, OBD pins 3/11). Some Saturn/Opel/Saab models.</summary>
    GmlanMediumSpeed,
}

public static class BusKinds
{
    public static string Title(this BusKind k) => k switch
    {
        BusKind.Class2 => "GM Class 2 (J1850 VPW)",
        BusKind.GmlanHighSpeed => "GMLAN high speed CAN (500k)",
        BusKind.GmlanSingleWire => "GMLAN single wire CAN (33.3k)",
        BusKind.GmlanMediumSpeed => "GMLAN mid speed CAN (95.2k, pins 3/11)",
        _ => k.ToString(),
    };

    public static bool IsCan(this BusKind k) => k != BusKind.Class2;
}

/// <summary>A decoded reply: who sent it and the service payload with bus headers removed.</summary>
public sealed record Reply(uint Source, byte[] Payload, bool Unacknowledged = false)
{
    public bool IsNegative => Payload.Length >= 2 && Payload[0] == 0x7F;
    public bool IsPending => IsNegative && Payload[^1] == 0x78;
    public byte NegativeCode => IsNegative ? Payload[^1] : (byte)0;
}

public sealed record RequestOptions
{
    /// <summary>Total time to wait for answers.</summary>
    public int TimeoutMs { get; init; } = 300;
    /// <summary>After the first answer, stop once the bus has been quiet this long.</summary>
    public int IdleMs { get; init; } = 80;
    /// <summary>Stop at the first final answer.</summary>
    public bool FirstOnly { get; init; }
    /// <summary>Stop early when this returns true for a reply (for example an end-of-list marker).</summary>
    public Func<Reply, bool>? Done { get; init; }

    public static readonly RequestOptions Default = new();
    public static readonly RequestOptions Single = new() { FirstOnly = true };
}

/// <summary>
/// One open vehicle network. Builds headers / CAN IDs, handles segmentation, and collects
/// replies, so the diagnostic layer only deals with service bytes.
/// </summary>
public abstract class VehicleBus : IDisposable
{
    protected readonly IPassThruChannel Channel;
    private readonly List<uint> _filters = new();

    /// <summary>Called with "TX ..." / "RX ..." lines for the session log.</summary>
    public Action<string>? Trace { get; set; }

    public abstract BusKind Kind { get; }
    protected VehicleBus(IPassThruChannel channel) => Channel = channel;

    public abstract IReadOnlyList<Reply> Request(uint target, byte[] payload, RequestOptions? options = null);
    /// <summary>Generic OBD-II request to all emissions modules.</summary>
    public abstract IReadOnlyList<Reply> ObdRequest(byte[] payload, RequestOptions? options = null);
    /// <summary>GM functional ("all nodes") request.</summary>
    public abstract IReadOnlyList<Reply> FunctionalRequest(byte[] payload, RequestOptions? options = null);

    /// <summary>Addresses worth probing during a module scan.</summary>
    public abstract IReadOnlyList<uint> ScanAddresses { get; }
    public abstract string FormatAddress(uint address);
    /// <summary>Splits a raw frame into (header/ID text, data bytes) for the bus monitor.</summary>
    public abstract (string Header, byte[] Data) SplitFrame(byte[] frame);
    /// <summary>Source address of a raw frame, used for passive module discovery.</summary>
    public abstract uint? SourceOf(byte[] frame);

    protected abstract void InstallNormalFilters();

    public void Start() => InstallNormalFilters();

    protected void AddFilter(byte[] mask, byte[] pattern) => _filters.Add(Channel.StartPassFilter(mask, pattern));

    protected void ClearFilters()
    {
        foreach (var f in _filters) Channel.StopFilter(f);
        _filters.Clear();
    }

    /// <summary>Accept every frame on the bus (bus monitor). Call <see cref="EndMonitor"/> to go back.</summary>
    public void BeginMonitor()
    {
        ClearFilters();
        var w = Kind.IsCan() ? 4 : 1;
        AddFilter(new byte[w], new byte[w]);
        Channel.ClearRx();
    }

    public void EndMonitor()
    {
        ClearFilters();
        InstallNormalFilters();
        Channel.ClearRx();
    }

    public IReadOnlyList<RawFrame> ReadRaw(uint timeoutMs) =>
        Channel.Read(32, timeoutMs).Where(f => !f.IsTxEcho).ToList();

    protected void Send(byte[] frame)
    {
        Trace?.Invoke("TX " + Hex.Format(frame));
        Channel.Write(frame);
    }

    /// <summary>Reads frames until the options say to stop, passing each to <paramref name="handle"/>.</summary>
    protected List<Reply> Collect(RequestOptions o, Func<byte[], IEnumerable<Reply>> handle)
    {
        var replies = new List<Reply>();
        var sw = Stopwatch.StartNew();
        long deadline = o.TimeoutMs;
        long lastRx = -1;
        while (sw.ElapsedMilliseconds < deadline)
        {
            if (lastRx >= 0 && replies.Count > 0 && sw.ElapsedMilliseconds - lastRx > o.IdleMs) break;
            var frames = Channel.Read(32, 10);
            foreach (var f in frames)
            {
                if (f.IsTxEcho) continue;
                foreach (var r in handle(f.Data))
                {
                    Trace?.Invoke($"RX {FormatAddress(r.Source)}: {Hex.Format(r.Payload)}{(r.Unacknowledged ? " (UUDT)" : "")}");
                    lastRx = sw.ElapsedMilliseconds;
                    if (r.IsPending)
                    {
                        // Module asked for more time: wait up to 5 s more.
                        deadline = Math.Max(deadline, sw.ElapsedMilliseconds + 5000);
                        continue;
                    }
                    replies.Add(r);
                    if (o.FirstOnly || (o.Done?.Invoke(r) ?? false)) return replies;
                }
            }
        }
        return replies;
    }

    public virtual void Dispose()
    {
        try { ClearFilters(); } catch { }
        Channel.Dispose();
    }
}
