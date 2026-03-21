using System.Globalization;
using System.Numerics;

namespace Qadopoolminer.Infrastructure;

public static class HexUtility
{
    public static bool IsHex(string? value, int byteLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        value = value.Trim();
        if (value.Length != byteLength * 2)
        {
            return false;
        }

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            var valid =
                (c >= '0' && c <= '9') ||
                (c >= 'a' && c <= 'f') ||
                (c >= 'A' && c <= 'F');

            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    public static string NormalizeLower(string value, int byteLength)
    {
        if (!IsHex(value, byteLength))
        {
            throw new FormatException($"Expected {byteLength} bytes of hex.");
        }

        return value.Trim().ToLowerInvariant();
    }

    public static byte[] Parse(string value, int byteLength)
        => Convert.FromHexString(NormalizeLower(value, byteLength));

    public static string ToLowerHex(byte[] value)
        => Convert.ToHexString(value).ToLowerInvariant();
}

public static class UInt256Utility
{
    public static bool IsHashAtOrBelowTarget(byte[] hash, string targetHex)
        => Compare(hash, ParseHex(targetHex)) <= 0;

    public static int Compare(byte[] hash, BigInteger target)
    {
        var hashValue = new BigInteger(hash, isUnsigned: true, isBigEndian: true);
        return hashValue.CompareTo(target);
    }

    public static BigInteger ParseHex(string value)
    {
        var normalized = HexUtility.NormalizeLower(value, 32);
        return new BigInteger(Convert.FromHexString(normalized), isUnsigned: true, isBigEndian: true);
    }
}

public static class HashrateUtility
{
    public static string Format(double hashesPerSecond)
    {
        if (hashesPerSecond < 0)
        {
            hashesPerSecond = 0;
        }

        var units = new[] { "H/s", "KH/s", "MH/s", "GH/s", "TH/s" };
        var value = hashesPerSecond;
        var unitIndex = 0;

        while (value >= 1000d && unitIndex < units.Length - 1)
        {
            value /= 1000d;
            unitIndex++;
        }

        return value.ToString("0.00", CultureInfo.InvariantCulture) + " " + units[unitIndex];
    }
}
