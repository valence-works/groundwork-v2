using System.Security.Cryptography;
using System.Text;

namespace Groundwork.SqlServer;

internal static class SqlServerPhysicalName
{
    public static string Normalize(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        const int maximumLength = 128;
        if (value.Length <= maximumLength)
            return value;

        var suffix = "_" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))[..12].ToLowerInvariant();
        var take = maximumLength - suffix.Length;
        while (take > 0 && char.IsHighSurrogate(value[take - 1]))
            take--;
        return value[..take] + suffix;
    }
}
