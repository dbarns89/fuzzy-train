using System.Globalization;
using System.Text;

namespace VcxDiag.Core;

public static class Hex
{
    public static string Format(ReadOnlySpan<byte> data) =>
        data.Length == 0 ? "" : string.Join(' ', data.ToArray().Select(b => b.ToString("X2")));

    /// <summary>Parses "6C 10 F1 19", "6c10f119" or "0x6C,0x10". Throws FormatException on bad input.</summary>
    public static byte[] Parse(string text)
    {
        var clean = new StringBuilder();
        foreach (var token in text.Split(new[] { ' ', ',', ';', '\t', '-', ':' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var t = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? token[2..] : token;
            if (t.Length % 2 == 1) t = "0" + t;
            clean.Append(t);
        }
        var s = clean.ToString();
        var bytes = new byte[s.Length / 2];
        for (int i = 0; i < bytes.Length; i++)
        {
            if (!byte.TryParse(s.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out bytes[i]))
                throw new FormatException($"'{s.Substring(i * 2, 2)}' is not a hex byte");
        }
        return bytes;
    }

    /// <summary>Printable ASCII, or null when the bytes are mostly not text.</summary>
    public static string? AsText(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return null;
        var trimmed = data.ToArray().Where(b => b != 0x00 && b != 0xFF).ToArray();
        if (trimmed.Length == 0) return null;
        int printable = trimmed.Count(b => b >= 0x20 && b < 0x7F);
        if (printable < trimmed.Length) return null;
        return Encoding.ASCII.GetString(trimmed).Trim();
    }

    public static uint BigEndian(ReadOnlySpan<byte> b)
    {
        uint v = 0;
        foreach (var x in b) v = v << 8 | x;
        return v;
    }

    public static byte[] U32(uint v) => new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
}
