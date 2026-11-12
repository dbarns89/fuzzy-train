using VcxDiag.Core;
using VcxDiag.Core.Gm;
using VcxDiag.Core.Obd;

namespace VcxDiag.Core.Tests;

public class DecodeTests
{
    [Theory]
    [InlineData(0x01, 0x71, "P0171")]
    [InlineData(0x04, 0x20, "P0420")]
    [InlineData(0x40, 0x35, "C0035")]
    [InlineData(0xD0, 0x73, "U1073")]
    [InlineData(0x93, 0x25, "B1325")]
    public void DecodesSaeDtc(byte hi, byte lo, string expected)
    {
        Assert.Equal(expected, Dtc.DecodeCode(hi, lo));
        Assert.Equal((hi, lo), Dtc.EncodeCode(expected));
    }

    [Fact]
    public void EmptyDtcIsNull() => Assert.Null(Dtc.DecodeCode(0, 0));

    [Fact]
    public void FullCodeIncludesFailureType()
    {
        var d = new Dtc { Code = "B1325", Module = "BCM", FailureType = 0x03 };
        Assert.Equal("B1325-03", d.FullCode);
        Assert.Equal("FM/PWM failure", d.FailureTypeText);
    }

    [Fact]
    public void DescribesGenericAndManufacturerCodes()
    {
        Assert.Equal("Catalyst system efficiency below threshold (bank 1)", DtcDescriptions.Describe("P0420"));
        Assert.Contains("manufacturer specific", DtcDescriptions.Describe("P1133"));
        Assert.Equal("Ignition system or misfire", DtcDescriptions.Describe("P0399"));
    }

    [Fact]
    public void DecodesRpmAndCoolant()
    {
        var rpm = ObdPids.ForPid(0x0C).Single();
        Assert.Equal(750, rpm.Decode(new byte[] { 0x0B, 0xB8 }));
        var ect = ObdPids.ForPid(0x05).Single();
        Assert.Equal(83, ect.Decode(new byte[] { 0x7B }));
    }

    [Fact]
    public void SplitsMultiPidCanReply()
    {
        // 41 0C 0B B8 0D 20 05 7B (mode byte already removed)
        var map = ObdPids.SplitReply(new byte[] { 0x0C, 0x0B, 0xB8, 0x0D, 0x20, 0x05, 0x7B });
        Assert.Equal(new byte[] { 0x0B, 0xB8 }, map[0x0C]);
        Assert.Equal(new byte[] { 0x20 }, map[0x0D]);
        Assert.Equal(new byte[] { 0x7B }, map[0x05]);
    }

    [Fact]
    public void DecodesSupportBitmap()
    {
        var pids = ObdPids.DecodeSupportBitmap(0x00, new byte[] { 0xBE, 0x3F, 0xB8, 0x13 }).ToList();
        Assert.Contains((byte)0x01, pids);
        Assert.Contains((byte)0x0C, pids);
        Assert.Contains((byte)0x20, pids);
        Assert.DoesNotContain((byte)0x02, pids);
    }

    [Fact]
    public void DecodesReadiness()
    {
        var r = ReadinessStatus.Decode(new byte[] { 0x82, 0x07, 0x65, 0x21 });
        Assert.True(r.MilOn);
        Assert.Equal(2, r.DtcCount);
        Assert.False(r.Diesel);
        var cat = r.Monitors.Single(m => m.Name == "Catalyst");
        Assert.True(cat.Supported);
        Assert.False(cat.Complete);
        Assert.True(r.Monitors.Single(m => m.Name == "Evaporative system").Complete);
    }

    [Fact]
    public void ParsesMode06Record()
    {
        var t = Mode06.ParseCan(new byte[] { 0x21, 0x80, 0x01, 0x00, 0x4C, 0x00, 0x00, 0x00, 0x60 }).Single();
        Assert.Equal("Catalyst monitor bank 1", t.MonitorName);
        Assert.Equal(76, t.Value);
        Assert.True(t.Passed);
    }

    [Theory]
    [InlineData("1GCEK19T04Z000000", false)]
    [InlineData("1M8GDM9AXKP042788", true)]
    public void ChecksVin(string vin, bool valid) => Assert.Equal(valid, Vin.IsValid(vin));

    [Fact]
    public void DecodesModelYear()
    {
        Assert.Equal(2004, Vin.ModelYear("1GCEK19T04Z000000"));
        Assert.Equal(2014, Vin.ModelYear("1GCVKREC0EZ123456"));
        Assert.Equal("Chevrolet (truck)", Vin.Maker("1GCVKREC0EZ123456"));
    }

    [Fact]
    public void ParsesHexInput()
    {
        Assert.Equal(new byte[] { 0x6C, 0x10, 0xF1, 0x19 }, Hex.Parse("6C 10 f1 19"));
        Assert.Equal(new byte[] { 0x1A, 0x90 }, Hex.Parse("0x1A,0x90"));
        Assert.Equal(new byte[] { 0x1A, 0x90 }, Hex.Parse("1a90"));
        Assert.Throws<FormatException>(() => Hex.Parse("zz"));
    }

    [Fact]
    public void CustomParameterDecodesSigned()
    {
        var p = new CustomParameter { Offset = 0, Length = 2, Signed = true, Scale = 0.1 };
        Assert.Equal(-1.0, p.Decode(new byte[] { 0xFF, 0xF6 })!.Value, 3);
    }
}
