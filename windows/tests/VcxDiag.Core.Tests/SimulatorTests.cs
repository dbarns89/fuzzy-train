using VcxDiag.Core;
using VcxDiag.Core.Gm;
using VcxDiag.Core.Obd;
using VcxDiag.Core.Protocols;
using VcxDiag.Core.Simulation;

namespace VcxDiag.Core.Tests;

/// <summary>End-to-end runs against the simulated vehicle, through the real bus code (headers, ISO-TP, UUDT).</summary>
public class SimulatorTests
{
    private static DiagnosticSession Open(BusKind kind)
    {
        var s = new DiagnosticSession(new SimulatedDevice());
        s.OpenBus(kind);
        return s;
    }

    [Fact]
    public void AutoDetectFindsCan()
    {
        using var s = new DiagnosticSession(new SimulatedDevice());
        Assert.Equal(BusKind.GmlanHighSpeed, s.AutoDetect());
    }

    [Theory]
    [InlineData(BusKind.GmlanHighSpeed)]
    [InlineData(BusKind.Class2)]
    public void ReadsValidVin(BusKind kind)
    {
        using var s = Open(kind);
        var vin = s.ReadVin();
        Assert.NotNull(vin);
        Assert.True(Vin.IsValid(vin!), vin);
    }

    [Fact]
    public void ScansGmlanModulesWithDiagAddresses()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var mods = s.ScanModules();
        Assert.Equal(6, mods.Count);
        var bcm = mods.Single(m => m.Address == 0x241);
        Assert.Equal((byte)0x40, bcm.DiagAddress);
        Assert.Equal("Body control (BCM)", bcm.Name);
    }

    [Fact]
    public void ScansClass2Modules()
    {
        using var s = Open(BusKind.Class2);
        var mods = s.ScanModules();
        Assert.Contains(mods, m => m.Address == 0x10);
        Assert.Contains(mods, m => m.Address == 0x99);
        Assert.Equal(7, mods.Count);
    }

    [Fact]
    public void ReadsGmlanDtcsFromUudt()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var bcm = s.ScanModules().Single(m => m.Address == 0x241);
        var dtcs = s.ReadModuleDtcs(bcm);
        var d = Assert.Single(dtcs);
        Assert.Equal("B1325-03", d.FullCode);
    }

    [Fact]
    public void ReadsClass2DtcsAndClears()
    {
        using var s = Open(BusKind.Class2);
        var ecm = s.ScanModules().Single(m => m.Address == 0x10);
        Assert.Equal(new[] { "P0171", "P0420" }, s.ReadModuleDtcs(ecm).Select(d => d.Code));
        Assert.True(s.ClearModuleDtcs(ecm));
        Assert.Empty(s.ReadModuleDtcs(ecm));
    }

    [Theory]
    [InlineData(BusKind.GmlanHighSpeed)]
    [InlineData(BusKind.Class2)]
    public void ReadsObdCodesAndClearAll(BusKind kind)
    {
        using var s = Open(kind);
        Assert.Equal(new[] { "P0171", "P0420" }, s.ReadObdDtcs(DtcSource.Stored).Select(d => d.Code));
        Assert.Single(s.ReadObdDtcs(DtcSource.Pending));
        Assert.True(s.ClearAllDtcs() > 0);
        Assert.Empty(s.ReadObdDtcs(DtcSource.Stored));
    }

    [Fact]
    public void ReadsLiveDataInOneCanRequest()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var supported = s.SupportedPids();
        Assert.Contains((byte)0x0C, supported);
        Assert.Contains((byte)0x42, supported);
        var values = s.ReadPids(new byte[] { 0x0C, 0x0D, 0x05, 0x11, 0x0B, 0x42 });
        Assert.Equal(6, values.Count);
    }

    [Fact]
    public void ReadsLiveDataOnClass2()
    {
        using var s = Open(BusKind.Class2);
        var values = s.ReadPids(new byte[] { 0x0C, 0x05 });
        Assert.Equal(2, values.Count);
    }

    [Fact]
    public void ReadsIdentificationOverIsoTp()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var ecm = s.ScanModules().Single(m => m.Address == 0x7E0);
        var items = s.ReadIdentification(ecm);
        Assert.Contains(items, i => i.Name == "VIN" && i.Value.Length == 17); // multi-frame reply
        Assert.Contains(items, i => i.Name == "End model part number");
    }

    [Fact]
    public void ReadsFreezeFrameReadinessAndTests()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var ff = s.ReadFreezeFrame();
        Assert.Equal("P0171", ff!.TriggerCode);
        Assert.NotEmpty(ff.Values);
        var r = s.ReadReadiness();
        Assert.True(r!.MilOn);
        var tests = s.ReadMode06();
        Assert.Contains(tests, t => !t.Passed);
        Assert.Contains(tests, t => t.MonitorName == "Catalyst monitor bank 1");
    }

    [Fact]
    public void ReadsObdInfo()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var info = Assert.Single(s.ReadObdInfo());
        Assert.Equal(new[] { "12654321", "12654987" }, info.CalibrationIds);
        Assert.Equal(2, info.Cvns.Count);
        Assert.Equal("ECM-EngineControl", info.EcuName);
    }

    [Fact]
    public void ReadsCustomParameter()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        var v = s.ReadCustom(new CustomParameter { Name = "demo", Module = 0x7E0, Identifier = 0x1234, Length = 2 });
        Assert.NotNull(v);
    }

    [Fact]
    public void SendsMultiFrameRequest()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        // 10-byte request forces a first frame + flow control + consecutive frame.
        var r = s.Raw(0x241, false, new byte[] { 0x1A, 0x90, 1, 2, 3, 4, 5, 6, 7, 8 });
        Assert.Contains(r, x => x.Payload[0] == 0x5A);
    }

    [Fact]
    public void MonitorSeesTraffic()
    {
        using var s = Open(BusKind.GmlanHighSpeed);
        s.Bus!.BeginMonitor();
        var frames = new List<VcxDiag.Core.Transport.RawFrame>();
        for (int i = 0; i < 20 && frames.Count == 0; i++) frames.AddRange(s.Bus.ReadRaw(30));
        s.Bus.EndMonitor();
        Assert.NotEmpty(frames);
    }

    [Fact]
    public void SingleWireBusHasBodyModules()
    {
        using var s = Open(BusKind.GmlanSingleWire);
        var mods = s.ScanModules();
        Assert.Contains(mods, m => m.Name == "Radio / audio");
    }
}
